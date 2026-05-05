using Microsoft.Extensions.DependencyInjection;
using TurfTime2.Services;

namespace TurfTime2
{
    public partial class App : Application
    {
        private const string TEAM_MODE_KEY = "team_mode";
        private const string TEAM_ID_KEY = "team_id";

        public App()
        {
            InitializeComponent();

            // Initialize FCM
            _ = InitializeFcmAsync();
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            // Check if a team was previously selected
            var teamMode = Preferences.Get(TEAM_MODE_KEY, string.Empty);
            var teamId = Preferences.Get(TEAM_ID_KEY, string.Empty);

            var appShell = new AppShell();
            var window = new Window(appShell);

            if (string.IsNullOrEmpty(teamMode) || string.IsNullOrEmpty(teamId))
            {
                // No team selected - navigate to Team Details page after shell loads
                System.Diagnostics.Debug.WriteLine("[App] No team selected - will navigate to Team Details");
                appShell.Loaded += async (s, e) =>
                {
                    await Shell.Current.GoToAsync("//SettingsPage/settings/teamdetails");
                };
            }
            else
            {
                // Team selected - load normally (Game page as default)
                System.Diagnostics.Debug.WriteLine($"[App] Team selected: {teamId} - starting at Game page");
            }

            return window;
        }

        private async Task InitializeFcmAsync()
        {
            try
            {
                // Wait a bit for app to fully initialize
                await Task.Delay(2000);

                var success = await FcmService.Instance.InitializeAsync();

                if (success)
                {
                    // Update token in Firestore
                    await FcmService.Instance.UpdateTokenInFirestoreAsync();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] ❌ FCM initialization error: {ex.Message}");
            }
        }
    }
}
