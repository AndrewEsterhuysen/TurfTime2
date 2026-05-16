namespace TurfTime2;

public partial class SettingsPage : ContentPage
{
    public SettingsPage()
    {
        InitializeComponent();
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
            await DisplayAlert("Error", "Could not navigate to Team Details page.", "OK");
        }
    }

    private async void OnReportsTapped(object sender, EventArgs e)
    {
        try
        {
            await Shell.Current.GoToAsync("settings/reports");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Navigation error: {ex.Message}");
            await DisplayAlert("Error", "Could not navigate to Reports page.", "OK");
        }
    }

    }

