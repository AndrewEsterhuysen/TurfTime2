namespace TurfTime2;

public partial class SettingsPage : ContentPage
{
    public SettingsPage()
    {
        InitializeComponent();
    }

    private async void OnLogTapped(object sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("settings/log");
    }

    private async void OnSkinsTapped(object sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("settings/skins");
    }
}
