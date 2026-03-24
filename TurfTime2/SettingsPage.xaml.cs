namespace TurfTime2;

public partial class SettingsPage : ContentPage
{
    public SettingsPage()
    {
        InitializeComponent();
    }

    private async void OnLogTapped(object sender, EventArgs e)
    {
        try
        {
            await Shell.Current.GoToAsync("settings/log");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Navigation error: {ex.Message}");
            await DisplayAlertAsync("Error", "Could not navigate to Log page.", "OK");
        }
    }

    private async void OnSkinsTapped(object sender, EventArgs e)
    {
        try
        {
            await Shell.Current.GoToAsync("settings/skins");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Navigation error: {ex.Message}");
            await DisplayAlertAsync("Error", "Could not navigate to Skins page.", "OK");
        }
    }

    private async void OnRotationStyleTapped(object sender, EventArgs e)
    {
        try
        {
            await Shell.Current.GoToAsync("settings/rotationstyle");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Navigation error: {ex.Message}");
            await DisplayAlertAsync("Error", "Could not navigate to Rotation Style page.", "OK");
        }
    }
}
