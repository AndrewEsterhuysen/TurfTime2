namespace TurfTime2
{
    public partial class AppShell : Shell
    {
        private const string TEAM_MODE_KEY = "team_mode";
        private const string TEAM_ID_KEY = "team_id";
        private bool _forcingTabRoot;

        public static readonly string TeamDetailsRoute = "//SettingsPage/settings/teamdetails";

        public AppShell()
        {
            InitializeComponent();

            // Register routes for navigation from Settings page
            Routing.RegisterRoute("settings/teamdetails", typeof(TeamDetailsPage));
            Routing.RegisterRoute("settings/reports", typeof(ReportsPage));
            Routing.RegisterRoute("settings/timers", typeof(TimersSettingsPage));
            Routing.RegisterRoute("settings/options", typeof(OptionsPage));

            // Register routes for navigation from Details page
            Routing.RegisterRoute("details/location", typeof(SetupPage));
            Routing.RegisterRoute("details/comingsoon", typeof(ComingSoonPage));

            // Handle navigation to clear stacks when switching to Settings / Details tabs
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
            var teamMode = Preferences.Get(TEAM_MODE_KEY, "local");
            var teamId = Preferences.Get(TEAM_ID_KEY, string.Empty);
            bool hasTeam = !string.IsNullOrEmpty(teamId);
            bool isLocal = teamMode == "local";

            System.Diagnostics.Debug.WriteLine($"[AppShell] ========================================");
            System.Diagnostics.Debug.WriteLine($"[AppShell] UpdateMenuItemAvailability called");
            System.Diagnostics.Debug.WriteLine($"[AppShell] Team Mode: '{teamMode}'");
            System.Diagnostics.Debug.WriteLine($"[AppShell] Team ID: '{teamId}'");
            System.Diagnostics.Debug.WriteLine($"[AppShell] Has Team: {hasTeam}");
            System.Diagnostics.Debug.WriteLine($"[AppShell] Is Local: {isLocal}");
            System.Diagnostics.Debug.WriteLine($"[AppShell] Items.Count: {Items.Count}");

            // Chat and Details (Location / Kit / Duties / Nominations) require a shared (cloud) team.
            ChatTab.IsVisible = !isLocal;
            DetailsTab.IsVisible = !isLocal;

            GameTab.IsEnabled = hasTeam;

            if (Items.FirstOrDefault() is TabBar tabBar)
            {
                System.Diagnostics.Debug.WriteLine($"[AppShell] ✓ TabBar found with {tabBar.Items.Count} items");

                // List all tabs for debugging
                for (int i = 0; i < tabBar.Items.Count; i++)
                {
                    var item = tabBar.Items[i];
                    System.Diagnostics.Debug.WriteLine($"[AppShell]   Tab {i}: Route='{item.Route}', Title='{item.Title}', IsEnabled={item.IsEnabled}");
                }

                System.Diagnostics.Debug.WriteLine($"[AppShell] Game tab set to: {(hasTeam ? "ENABLED" : "DISABLED")}");
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
            if (_forcingTabRoot)
            {
                return;
            }

            var currentRoute = e.Current.Location.ToString();
            bool switchedTabs =
                e.Source == ShellNavigationSource.ShellItemChanged ||
                e.Source == ShellNavigationSource.ShellSectionChanged ||
                e.Source == ShellNavigationSource.ShellContentChanged;

            // Always land on root when switching back to a hub tab that has subpages.
            if (switchedTabs)
            {
                bool settingsSelected = currentRoute.Contains("SettingsPage", StringComparison.OrdinalIgnoreCase);
                bool isSettingsSubpage = currentRoute.Contains("settings/", StringComparison.OrdinalIgnoreCase);
                if (settingsSelected && isSettingsSubpage)
                {
                    await ForceTabRootAsync("//SettingsPage");
                    return;
                }

                bool detailsSelected = currentRoute.Contains("DetailsPage", StringComparison.OrdinalIgnoreCase);
                bool isDetailsSubpage = currentRoute.Contains("details/", StringComparison.OrdinalIgnoreCase);
                if (detailsSelected && isDetailsSubpage)
                {
                    await ForceTabRootAsync("//DetailsPage");
                }
            }
        }

        private async Task ForceTabRootAsync(string absoluteRoute)
        {
            try
            {
                _forcingTabRoot = true;
                await Shell.Current.GoToAsync(absoluteRoute);
            }
            finally
            {
                _forcingTabRoot = false;
            }
        }
    }
}
