using System.Windows.Input;
using TurfTime2.Models;
using TurfTime2.Services;

namespace TurfTime2;

public partial class QrShareModal : ContentPage
{
    public ICommand ShareQrImageCommand { get; }
    public ICommand CloseCommand { get; }

    public string TeamName { get; set; } = string.Empty;
    public int PlayerCount { get; set; }
    public int EncodedSize { get; set; }
    public ImageSource? QrImage { get; set; }

    private readonly string _shareLink;
    private readonly byte[] _qrPngBytes;

    public QrShareModal(TeamShareData teamData)
    {
        InitializeComponent();

        TeamName = teamData.TeamName;
        PlayerCount = teamData.Players.Count;
        EncodedSize = QrCodeService.GetApproximateEncodedSize(teamData);
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
            var filePath = Path.Combine(FileSystem.CacheDirectory, $"turftime-team-{DateTime.UtcNow:yyyyMMddHHmmss}.png");
            await File.WriteAllBytesAsync(filePath, _qrPngBytes);

            await Share.RequestAsync(new ShareFileRequest
            {
                Title = $"Share Team QR - {TeamName}",
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
