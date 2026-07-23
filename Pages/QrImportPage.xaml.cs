using TurfTime2.Helpers;
using TurfTime2.Services;
using ZXing.Net.Maui;

namespace TurfTime2;

public partial class QrImportPage : ContentPage
{
    private bool _isProcessing;
    private bool _cameraConfigured;
    private bool _cameraStarted;

    public QrImportPage()
    {
        InitializeComponent();
        CameraView.Options = new BarcodeReaderOptions
        {
            AutoRotate = true,
            Multiple = false
        };
        Loaded += OnPageLoaded;
    }

    protected override void OnDisappearing()
    {
        CameraView.IsDetecting = false;
        _cameraStarted = false;
        base.OnDisappearing();
    }

    private async void OnPageLoaded(object? sender, EventArgs e)
    {
        if (_cameraConfigured || _cameraStarted)
            return;

        _cameraStarted = true;
#if ANDROID
        if (!await EnsureCameraPermissionAsync())
        {
            _cameraStarted = false;
            return;
        }
#endif
        await ConfigureCameraAsync();
    }

#if ANDROID
    private async Task<bool> EnsureCameraPermissionAsync()
    {
        var status = await Permissions.CheckStatusAsync<Permissions.Camera>();
        if (status == PermissionStatus.Granted)
            return true;

        status = await Permissions.RequestAsync<Permissions.Camera>();
        if (status == PermissionStatus.Granted)
            return true;

        System.Diagnostics.Debug.WriteLine("[QrImportPage] Camera permission denied");
        await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            await DisplayAlert("Camera Permission Required",
                "Camera access is needed to scan QR codes. Please enable camera permission and try again.",
                "OK");
        });
        return false;
    }
#endif

    private async Task ConfigureCameraAsync()
    {
        try
        {
            System.Diagnostics.Debug.WriteLine("[QrImportPage] ========== CAMERA SETUP STARTING ==========");

            var cameras = await CameraView.GetAvailableCameras();
            if (cameras is null || cameras.Count == 0)
            {
                System.Diagnostics.Debug.WriteLine("[QrImportPage] ❌ No cameras found!");
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[QrImportPage] Found {cameras.Count} camera(s)");
            foreach (var cam in cameras)
            {
                System.Diagnostics.Debug.WriteLine($"  - {cam.Name} ({cam.Location})");
            }

            var preferred = cameras.FirstOrDefault(camera => camera.Location == CameraLocation.Rear);
            preferred ??= cameras.First();

            CameraView.IsDetecting = false;
            CameraView.SelectedCamera = preferred;
            System.Diagnostics.Debug.WriteLine($"[QrImportPage] ✓ Selected camera: {preferred.Name}");

            System.Diagnostics.Debug.WriteLine("[QrImportPage] ========== CAMERA SETUP COMPLETE ==========");
            CameraView.IsDetecting = true;
            _cameraConfigured = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[QrImportPage] ❌ Camera setup failed: {ex.Message}");
            CameraView.IsDetecting = false;
            _cameraConfigured = false;
        }
        await Task.CompletedTask;
    }

    private void UpdateFocusStatusLabel(string status)
    {
        FocusStatusLabel.Text = status;
    }

    private void OnBarcodesDetected(object? sender, BarcodeDetectionEventArgs e)
    {
        if (_isProcessing)
            return;

        var value = e.Results?.FirstOrDefault()?.Value;
        if (string.IsNullOrWhiteSpace(value))
            return;

        System.Diagnostics.Debug.WriteLine($"\n[QrImportPage] ✅✅✅ QR CODE DETECTED! ✅✅✅");

        _isProcessing = true;
        MainThread.BeginInvokeOnMainThread(async () => await ImportFromRawContentAsync(value));
    }

    private async void OnImportFromPhotoClicked(object sender, EventArgs e)
    {
        if (_isProcessing)
            return;

        try
        {
            var photo = await MediaPicker.Default.PickPhotoAsync();
            if (photo is null)
                return;

            await using var stream = await photo.OpenReadAsync();
            var qrContent = QrCodeService.DecodeQrContentFromImage(stream);
            if (string.IsNullOrWhiteSpace(qrContent))
            {
                await DisplayAlert("No QR Found", "No readable Turf Time QR code was found in this image.", "OK");
                return;
            }

            _isProcessing = true;
            await ImportFromRawContentAsync(qrContent);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[QrImportPage] Photo import failed: {ex.Message}");
            await DisplayAlert("Import Failed", "Could not read the selected image.", "OK");
        }
    }

    private async Task ImportFromRawContentAsync(string content)
    {
        CameraView.IsDetecting = false;
        try
        {
            if (!QrCodeService.TryParseTeamShareData(content, out var teamData, out var error) || teamData is null)
            {
                await DisplayAlert("Invalid QR", error, "OK");
                return;
            }

            if (teamData.IsSharedJoin)
            {
                var joined = await JoinSharedTeamFromInviteAsync(teamData.InviteCode);
                if (joined)
                    await CloseAndOpenTeamViewAsync();
                return;
            }

            QrCodeService.ImportTeamToLocal(teamData);
            System.Diagnostics.Debug.WriteLine($"[QrImportPage] ✅ Local team '{teamData.TeamName}' imported successfully");
            await DisplayAlert("Team Imported", $"Imported '{teamData.TeamName}' successfully.", "OK");
            await CloseAndOpenTeamViewAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[QrImportPage] Import failed: {ex.GetType().FullName}: {ex.Message}");
            await DisplayAlert("Import Failed", "Could not import team from QR data.", "OK");
        }
        finally
        {
            _isProcessing = false;
            CameraView.IsDetecting = true;
        }
    }

    /// <returns>True if join completed and navigation to Team Details should follow.</returns>
    private async Task<bool> JoinSharedTeamFromInviteAsync(string inviteCode)
    {
        var code = QrCodeService.NormalizeInviteCode(inviteCode);
        if (string.IsNullOrEmpty(code))
        {
            await DisplayAlert("Invalid QR", "Shared-team QR has no invite code.", "OK");
            return false;
        }

        var displayName = await ResolveDisplayNameAsync();
        if (displayName is null)
            return false;

        var cloud = ResolveCloudTeam();
        if (cloud is null)
        {
            await DisplayAlert("Unavailable", "Cloud team service is not available. Check your connection and try again.", "OK");
            return false;
        }

        var result = await cloud.JoinByInviteCodeAsync(code, displayName);
        System.Diagnostics.Debug.WriteLine($"[QrImportPage] JoinByInviteCode result: {result}");

        if (result.StartsWith("success:", StringComparison.Ordinal))
        {
            var parts = result.Split(':', 3);
            if (parts.Length >= 3)
            {
                var teamId = parts[1];
                var teamName = parts[2];
                QrCodeService.ApplySharedJoinLocalState(teamId, teamName, displayName);
                RefreshAppShellMenu();
                _ = FcmService.Instance.EnsureRegisteredForCurrentTeamAsync();

                await DisplayAlert(
                    "Joined Team!",
                    $"Successfully joined: {teamName}\n\nRole: Member\nChat name: {displayName}\n\nTeam data will load from the cloud.",
                    "OK");
                return true;
            }
        }

        if (result.StartsWith("already_member:", StringComparison.Ordinal))
        {
            var parts = result.Split(':', 3);
            if (parts.Length >= 3)
            {
                var teamId = parts[1];
                var teamName = parts[2];
                UserDisplayName.Set(displayName);
                QrCodeService.ApplySharedJoinLocalState(teamId, teamName, displayName);
                RefreshAppShellMenu();
                await DisplayAlert(
                    "Already a Member",
                    $"You are already a member of '{teamName}'. Switched to that team and updated your display name.",
                    "OK");
                return true;
            }
        }

        var message = result.StartsWith("error:", StringComparison.Ordinal)
            ? result["error:".Length..].Trim()
            : result;
        await DisplayAlert("Join Failed", string.IsNullOrWhiteSpace(message) ? "Could not join team." : message, "OK");
        return false;
    }

    /// <returns>Validated display name, or null if cancelled / invalid.</returns>
    private async Task<string?> ResolveDisplayNameAsync()
    {
        var existing = UserDisplayName.Get();
        if (UserDisplayName.TryValidate(existing, out var valid, out _))
            return valid;

        var entered = await DisplayPromptAsync(
            "Display Name",
            "Enter the name teammates will see in Chat (required to join a shared team):",
            accept: "Join",
            cancel: "Cancel",
            maxLength: UserDisplayName.MaxLength,
            keyboard: Keyboard.Text,
            initialValue: existing);

        if (entered is null)
            return null;

        if (!UserDisplayName.TryValidate(entered, out var displayName, out var nameError))
        {
            await DisplayAlert("Display Name Required", nameError ?? "Invalid name.", "OK");
            return null;
        }

        return displayName;
    }

    private static ICloudTeamService? ResolveCloudTeam()
    {
        return Application.Current?.Handler?.MauiContext?.Services?.GetService<ICloudTeamService>();
    }

    private static void RefreshAppShellMenu()
    {
        try
        {
            if (Application.Current?.Windows.FirstOrDefault()?.Page is AppShell shell)
                shell.RefreshMenu();
            else if (Shell.Current is AppShell appShell)
                appShell.RefreshMenu();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[QrImportPage] Menu refresh: {ex.Message}");
        }
    }

    private async Task CloseAndOpenTeamViewAsync()
    {
        await OnCloseAsync();
        if (Shell.Current is not null)
        {
            await Shell.Current.GoToAsync(AppShell.TeamDetailsRoute);
        }
    }

    private async Task OnCloseAsync()
    {
        var navigation = Navigation ?? Shell.Current?.Navigation;
        if (navigation?.ModalStack.Count > 0)
        {
            await navigation.PopModalAsync();
            return;
        }

        if (Shell.Current is not null)
            await Shell.Current.GoToAsync("..");
    }

    private async void OnCloseClicked(object sender, EventArgs e)
    {
        await OnCloseAsync();
    }

    private async void OnCloseToolbarClicked(object? sender, EventArgs e)
    {
        await OnCloseAsync();
    }
}
