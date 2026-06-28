namespace TurfTime2;

public partial class UpdatesPage : ContentPage
{
    private const string UpdatesSeenMarkerKey = "updates_seen_marker";
    private readonly bool _showWelcomeAfterClose;

    public UpdatesPage(bool showWelcomeAfterClose)
    {
        InitializeComponent();
        _showWelcomeAfterClose = showWelcomeAfterClose;
        VersionLabel.Text = $"v{AppInfo.VersionString} (build {AppInfo.BuildString})";
    }

    private async void OnContinueClicked(object sender, EventArgs e)
    {
        Preferences.Set(UpdatesSeenMarkerKey, $"{AppInfo.VersionString}:{AppInfo.BuildString}");

        await Navigation.PopModalAsync(animated: true);

        if (_showWelcomeAfterClose && !Preferences.Get("welcome_dont_show", false))
        {
            await Shell.Current.Navigation.PushModalAsync(new WelcomePage(), animated: true);
        }
    }
}
