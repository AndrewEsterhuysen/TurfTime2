using System.Windows.Input;
using TurfTime2.Models;
using TurfTime2.Services;

namespace TurfTime2;

public partial class QrShareModal : ContentPage
{
    public ICommand CopyLinkCommand { get; }
    public ICommand ShareCommand { get; }
    public ICommand CloseCommand { get; }

    public string TeamName { get; set; } = string.Empty;
    public int PlayerCount { get; set; }
    public int EncodedSize { get; set; }
    public ImageSource? QrImage { get; set; }

    private readonly string _shareLink;

    public QrShareModal(TeamShareData teamData)
    {
        InitializeComponent();

        TeamName = teamData.TeamName;
        PlayerCount = teamData.Players.Count;
        EncodedSize = QrCodeService.GetApproximateEncodedSize(teamData);
        _shareLink = QrCodeService.GenerateDeepLink(teamData);

        CopyLinkCommand = new Command(OnCopyLink);
        ShareCommand = new Command(OnShare);
        CloseCommand = new Command(async () => await OnCloseAsync());

        BindingContext = this;
        LoadQr();
    }

    private void LoadQr()
    {
        var encodedLink = Uri.EscapeDataString(_shareLink);
        QrImage = ImageSource.FromUri(new Uri($"https://api.qrserver.com/v1/create-qr-code/?size=500x500&data={encodedLink}"));
        OnPropertyChanged(nameof(QrImage));
    }

    private async void OnCopyLink()
    {
        try
        {
            await Clipboard.SetTextAsync(_shareLink);
            await DisplayAlert("Copied", "Share link copied to clipboard.", "OK");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[QrShareModal] Copy failed: {ex.Message}");
            await DisplayAlert("Error", "Failed to copy link.", "OK");
        }
    }

    private async void OnShare()
    {
        try
        {
            await Share.RequestAsync(new ShareTextRequest
            {
                Title = "Share Team",
                Text = $"Join my team '{TeamName}' in Turf Time.\n{_shareLink}"
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[QrShareModal] Share failed: {ex.Message}");
            await DisplayAlert("Error", "Failed to share link.", "OK");
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
