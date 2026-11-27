namespace TurfTime2;

public partial class SettingsPage : ContentPage
{
    public SettingsPage()
    {
        InitializeComponent();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        var desktop = Helpers.DesktopMode.IsWindows;
        DetailsPage.ApplyPageTeamTitle(this, desktop ? "Setup" : "Settings");
        AiConnectCard.IsVisible = Services.AiConnectConfig.IsEnabled;
        // Windows is the setup client: game timers and match options live on the phone.
        TimersCard.IsVisible = !desktop;
        OptionsCard.IsVisible = !desktop;
        PageTitleLabel.Text = desktop ? "Club and team setup" : "Settings";
    }

    private async void OnAiConnectTapped(object sender, EventArgs e)
    {
        try
        {
            await Shell.Current.GoToAsync("settings/aiconnect");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Navigation error: {ex.Message}");
        }
    }

    private async void OnTeamDetailsTapped(object sender, EventArgs e)
    {
        try
        {
            await Shell.Current.GoToAsync("settings/teamdetails");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Navigation error: {ex.Message}");
            await DisplayAlert("Error", "Could not open Team Selection.", "OK");
        }
    }

    private async void OnClubAdminTapped(object sender, EventArgs e)
    {
        try
        {
            await Shell.Current.GoToAsync("settings/clubadmin");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Navigation error: {ex.Message}");
            await DisplayAlert("Error", "Could not open Club admin.", "OK");
        }
    }

    private async void OnTimersTapped(object sender, EventArgs e)
    {
        try
        {
            await Shell.Current.GoToAsync("settings/timers");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Navigation error: {ex.Message}");
            await DisplayAlert("Error", "Could not navigate to Timers page.", "OK");
        }
    }

    private async void OnOptionsTapped(object sender, EventArgs e)
    {
        try
        {
            await Shell.Current.GoToAsync("settings/options");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Navigation error: {ex.Message}");
            await DisplayAlert("Error", "Could not navigate to Options page.", "OK");
        }
    }
}
