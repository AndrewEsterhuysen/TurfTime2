namespace TurfTime2;

public partial class WelcomePage : ContentPage
{
    public WelcomePage()
    {
        InitializeComponent();
        VersionLabel.Text = $"v{AppInfo.VersionString} (build {AppInfo.BuildString})";
    }

    // Tapping the label toggles the checkbox
    private void OnDontShowLabelTapped(object sender, TappedEventArgs e)
    {
        DontShowCheckBox.IsChecked = !DontShowCheckBox.IsChecked;
    }

    private async void OnCloseClicked(object sender, EventArgs e)
    {
        Preferences.Set("welcome_dont_show", DontShowCheckBox.IsChecked);
        await Navigation.PopModalAsync();
    }
}
