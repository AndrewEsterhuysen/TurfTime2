namespace TurfTime2;

public partial class DetailsPage : ContentPage
{
    public DetailsPage()
    {
        InitializeComponent();
    }

    private async void OnLocationTapped(object sender, EventArgs e)
    {
        await NavigateAsync("details/location", "Location");
    }

    private async void OnKitTapped(object sender, EventArgs e)
    {
        await NavigateAsync("details/comingsoon?title=Kit", "Kit");
    }

    private async void OnDutiesTapped(object sender, EventArgs e)
    {
        await NavigateAsync("details/comingsoon?title=Duties", "Duties");
    }

    private async void OnNominationsTapped(object sender, EventArgs e)
    {
        await NavigateAsync("details/comingsoon?title=Nominations", "Nominations");
    }

    private async Task NavigateAsync(string route, string featureName)
    {
        try
        {
            await Shell.Current.GoToAsync(route);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Details] Navigation error ({featureName}): {ex.Message}");
            await DisplayAlert("Error", $"Could not open {featureName}.", "OK");
        }
    }
}
