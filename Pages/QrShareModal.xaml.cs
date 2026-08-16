using System.Windows.Input;
using TurfTime2.Helpers;
using TurfTime2.Models;
using TurfTime2.Services;

namespace TurfTime2;

public partial class QrShareModal : ContentPage
{
    public ICommand ShareQrImageCommand { get; }
    public ICommand CopyJoinLinkCommand { get; }
    public ICommand CloseCommand { get; }

    public string TeamName { get; set; } = string.Empty;
    public string SubtitleLine { get; set; } = string.Empty;
    public string DetailLine { get; set; } = string.Empty;
    public string Instructions { get; set; } = string.Empty;
    public string JoinLink { get; set; } = string.Empty;
    public bool ShowJoinLink { get; set; }
    public string ShareButtonText { get; set; } = "Share QR Image";
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

        _shareLink = QrCodeService.GenerateQrLink(teamData);
        // Shared invites bake "press and hold…" under the QR so messaging apps show it.
        _qrPngBytes = QrCodeService.GenerateShareQrPng(teamData);

        if (_isSharedJoin)
        {
            Title = "Share Invite QR";
            SubtitleLine = "Shared team — invite only";
            DetailLine = $"Invite: {teamData.InviteCode}";
            ShowJoinLink = true;
            JoinLink = _shareLink;
            ShareButtonText = "Share invite (QR + link)";
            Instructions =
                "Share via message or email using the button below. The share includes:\n" +
                "• QR image — press and hold to open Turf Time and join\n" +
                "• Join link — tap to open Turf Time and join (works in email and SMS)\n\n" +
                "Turf Time must already be installed. No roster is in the QR — the team loads from the cloud after join.";
        }
        else
        {
            Title = "Share Team";
            SubtitleLine = $"Players: {teamData.Players.Count}";
            DetailLine = $"QR size: ~{QrCodeService.GetApproximateEncodedSize(teamData)} bytes";
            ShowJoinLink = false;
            JoinLink = string.Empty;
            ShareButtonText = "Share QR Image";
            Instructions =
                "Send this QR code image to another phone. In Turf Time, the recipient uses Team Import to scan the QR (camera or photo) and import a local copy of the roster.";
        }

        ShareQrImageCommand = new Command(OnShareQrImage);
        CopyJoinLinkCommand = new Command(async () => await OnCopyJoinLinkAsync());
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

            if (_isSharedJoin)
            {
                await InviteShareHelper.ShareSharedInviteAsync(TeamName, _shareLink, filePath);
            }
            else
            {
                await InviteShareHelper.ShareLocalQrImageAsync($"Share Team QR - {TeamName}", filePath);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[QrShareModal] Share failed: {ex.Message}");
            await DisplayAlert("Error", "Failed to share invite.", "OK");
        }
    }

    private async Task OnCopyJoinLinkAsync()
    {
        if (string.IsNullOrWhiteSpace(JoinLink))
            return;

        await Clipboard.Default.SetTextAsync(JoinLink);
        await DisplayAlert(
            "Link copied",
            "Join link copied. Paste it into an email or text message.\n\nTurf Time must be installed on the receiver’s phone.",
            "OK");
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
