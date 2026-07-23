using System.Windows.Input;
using TurfTime2.Models;
using TurfTime2.Services;

namespace TurfTime2;

public partial class QrShareModal : ContentPage
{
    public ICommand ShareQrImageCommand { get; }
    public ICommand CloseCommand { get; }

    public string TeamName { get; set; } = string.Empty;
    public string SubtitleLine { get; set; } = string.Empty;
    public string DetailLine { get; set; } = string.Empty;
    public string Instructions { get; set; } = string.Empty;
    public ImageSource? QrImage { get; set; }

    private readonly string _shareLink;
    private readonly byte[] _qrPngBytes;
    private readonly bool _isSharedJoin;

    public QrShareModal(TeamShareData teamData)
    {
        InitializeComponent();

        _isSharedJoin = teamData.IsSharedJoin;
        TeamName = string.IsNullOrWhiteSpace(teamData.DisplayTitle)
            ? (string.IsNullOrWhiteSpace(teamData.TeamName) ? "Team" : teamData.TeamName)
            : teamData.DisplayTitle;

        if (_isSharedJoin)
        {
            Title = "Share Invite QR";
            SubtitleLine = "Shared team — invite only";
            DetailLine = $"Invite: {teamData.InviteCode}";
            Instructions =
                "Send this QR to another phone. In Turf Time they use Import Team (camera or photo) to scan it and join the cloud team with this invite code. No roster is encoded — data loads from the cloud.";
        }
        else
        {
            Title = "Share Team";
            SubtitleLine = $"Players: {teamData.Players.Count}";
            DetailLine = $"QR size: ~{QrCodeService.GetApproximateEncodedSize(teamData)} bytes";
            Instructions =
                "Send this QR code image to another phone. In Turf Time, the recipient uses Team Import to scan the QR (camera or photo) and import a local copy of the roster.";
        }

        _shareLink = QrCodeService.GenerateQrLink(teamData);
        _qrPngBytes = QrCodeService.GenerateQrPng(_shareLink);

        ShareQrImageCommand = new Command(OnShareQrImage);
        CloseCommand = new Command(async () => await OnCloseAsync());

        BindingContext = this;
        LoadQr();
    }

    private void LoadQr()
    {
        QrImage = ImageSource.FromStream(() => new MemoryStream(_qrPngBytes));
        OnPropertyChanged(nameof(QrImage));
    }

    private async void OnShareQrImage()
    {
        try
        {
            var prefix = _isSharedJoin ? "turftime-invite" : "turftime-team";
            var filePath = Path.Combine(FileSystem.CacheDirectory, $"{prefix}-{DateTime.UtcNow:yyyyMMddHHmmss}.png");
            await File.WriteAllBytesAsync(filePath, _qrPngBytes);

            await Share.RequestAsync(new ShareFileRequest
            {
                Title = _isSharedJoin ? $"Join team QR - {TeamName}" : $"Share Team QR - {TeamName}",
                File = new ShareFile(filePath)
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[QrShareModal] Share image failed: {ex.Message}");
            await DisplayAlert("Error", "Failed to share QR image.", "OK");
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
        {
            await Shell.Current.GoToAsync("..");
        }
    }

    private async void OnCloseToolbarClicked(object? sender, EventArgs e)
    {
        await OnCloseAsync();
    }
}
