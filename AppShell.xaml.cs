namespace TurfTime2
{
    public partial class AppShell : Shell
    {
        private const string TEAM_MODE_KEY = "team_mode";
        private const string TEAM_ID_KEY = "team_id";

        public static readonly string TeamDetailsRoute = "//SettingsPage/settings/teamdetails";

        public AppShell()
        {
            InitializeComponent();

            // Register routes for navigation from Settings page
            Routing.RegisterRoute("settings/teamdetails", typeof(TeamDetailsPage));
            Routing.RegisterRoute("settings/reports", typeof(ReportsPage));
            Routing.RegisterRoute("settings/skins", typeof(SkinsPage));
            Routing.RegisterRoute("settings/rotationstyle", typeof(RotationStylePage));
            Routing.RegisterRoute("settings/teamview", typeof(TeamViewPage));

            // Handle navigation to clear stacks when switching to Settings tab
            this.Navigated += OnShellNavigated;
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();

            // Update menu state when shell appears
            UpdateMenuItemAvailability();

            System.Diagnostics.Debug.WriteLine("[AppShell] OnAppearing - Menu state updated");
        }

        // Public method to refresh menu - called from TeamDetailsPage
        public void RefreshMenu()
        {
            UpdateMenuItemAvailability();
        }

        private void UpdateMenuItemAvailability()
        {
            var teamMode = Preferences.Get(TEAM_MODE_KEY, string.Empty);
            var teamId = Preferences.Get(TEAM_ID_KEY, string.Empty);
            bool hasTeam = !string.IsNullOrEmpty(teamMode) && !string.IsNullOrEmpty(teamId);
            bool isLocal = teamMode == "local";

            System.Diagnostics.Debug.WriteLine($"[AppShell] ========================================");
            System.Diagnostics.Debug.WriteLine($"[AppShell] UpdateMenuItemAvailability called");
            System.Diagnostics.Debug.WriteLine($"[AppShell] Team Mode: '{teamMode}'");
            System.Diagnostics.Debug.WriteLine($"[AppShell] Team ID: '{teamId}'");
            System.Diagnostics.Debug.WriteLine($"[AppShell] Has Team: {hasTeam}");
            System.Diagnostics.Debug.WriteLine($"[AppShell] Is Local: {isLocal}");
            System.Diagnostics.Debug.WriteLine($"[AppShell] Items.Count: {Items.Count}");

            // Hide Chat tab for local teams — chat requires cloud/Firestore.
            ChatTab.IsVisible = !isLocal;

            // Find Game tab in the TabBar
            // Structure: Shell -> Items[0] (TabBar) -> Items (ShellContent)
            // Note: MAUI adds IMPL_ prefix to routes automatically
            if (Items.FirstOrDefault() is TabBar tabBar)
            {
                System.Diagnostics.Debug.WriteLine($"[AppShell] ✓ TabBar found with {tabBar.Items.Count} items");

                // List all tabs for debugging
                for (int i = 0; i < tabBar.Items.Count; i++)
                {
                    var item = tabBar.Items[i];
                    System.Diagnostics.Debug.WriteLine($"[AppShell]   Tab {i}: Route='{item.Route}', Title='{item.Title}', IsEnabled={item.IsEnabled}");
                }

                // MAUI adds IMPL_ prefix to routes, so check for both
                var gameTab = tabBar.Items.FirstOrDefault(item => 
                    item.Route == "GamePage" || 
                    item.Route == "IMPL_GamePage" ||
                    item.Title == "Game");

                if (gameTab != null)
                {
                    var wasEnabled = gameTab.IsEnabled;
                    gameTab.IsEnabled = hasTeam;
                    System.Diagnostics.Debug.WriteLine($"[AppShell] ✅ Game tab FOUND");
                    System.Diagnostics.Debug.WriteLine($"[AppShell]    Route: {gameTab.Route}");
                    System.Diagnostics.Debug.WriteLine($"[AppShell]    Was Enabled: {wasEnabled}");
                    System.Diagnostics.Debug.WriteLine($"[AppShell]    Now Enabled: {gameTab.IsEnabled}");
                    System.Diagnostics.Debug.WriteLine($"[AppShell]    Setting to: {(hasTeam ? "ENABLED" : "DISABLED")}");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[AppShell] ❌ Game tab NOT FOUND");
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[AppShell] ❌ TabBar NOT FOUND in Items");
                System.Diagnostics.Debug.WriteLine($"[AppShell]    First item type: {Items.FirstOrDefault()?.GetType().Name ?? "NULL"}");
            }
            System.Diagnostics.Debug.WriteLine($"[AppShell] ========================================");
        }

        private async void OnShellNavigated(object sender, ShellNavigatedEventArgs e)
        {
            // When navigating to Settings tab from another tab, ensure we're at the root Settings page
            if (e.Current.Location.ToString().Contains("SettingsPage") && 
                e.Source == ShellNavigationSource.ShellItemChanged)
            {
                // Check if we're on a sub-page (like settings/teamview)
                var currentRoute = e.Current.Location.ToString();
                if (currentRoute.Contains("settings/"))
                {
                    // Navigate to the root SettingsPage using absolute navigation
                    await Shell.Current.GoToAsync("//SettingsPage");
                }
            }
        }
    }
}
