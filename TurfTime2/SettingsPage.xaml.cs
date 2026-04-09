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

    private async void OnLogTapped(object sender, EventArgs e)
    {
        try
        {
            await Shell.Current.GoToAsync("settings/log");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Navigation error: {ex.Message}");
            await DisplayAlert("Error", "Could not navigate to Log page.", "OK");
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
            await DisplayAlert("Error", "Could not navigate to Skins page.", "OK");
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
            await DisplayAlert("Error", "Could not navigate to Rotation Style page.", "OK");
        }
    }

    private async void OnTeamViewTapped(object sender, EventArgs e)
    {
        try
        {
            await Shell.Current.GoToAsync("settings/teamview");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Navigation error: {ex.Message}");
            await DisplayAlert("Error", "Could not navigate to Team View page.", "OK");
        }
    }

    private async void OnSyncNowTapped(object sender, EventArgs e)
    {
        try
        {
            // Find the Frame parent for visual feedback
            Frame? frame = null;
            if (sender is Grid grid && grid.Parent is Frame parentFrame)
            {
                frame = parentFrame;
                var originalBackgroundColor = frame.BackgroundColor;
                frame.BackgroundColor = Color.FromRgb(66, 145, 76); // Lighter green to show activity

                // Request manual sync via helper
                CloudSyncHelper.RequestManualSync();

                // Give visual feedback
                await Task.Delay(500);
                frame.BackgroundColor = originalBackgroundColor;
            }
            else
            {
                // Fallback if Frame not found
                CloudSyncHelper.RequestManualSync();
                await Task.Delay(500);
            }

            await DisplayAlert("Cloud Sync", "Sync request sent. Check the Game tab for sync status.", "OK");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Sync error: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"Sync error stack: {ex.StackTrace}");
            await DisplayAlert("Error", $"Could not trigger cloud sync: {ex.Message}", "OK");
        }
    }
}

