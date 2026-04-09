namespace TurfTime2
{
    public partial class AppShell : Shell
    {
        public AppShell()
        {
            InitializeComponent();

            // Register routes for navigation from Settings page
            Routing.RegisterRoute("settings/teamdetails", typeof(TeamDetailsPage));
            Routing.RegisterRoute("settings/log", typeof(LogPage));
            Routing.RegisterRoute("settings/skins", typeof(SkinsPage));
            Routing.RegisterRoute("settings/rotationstyle", typeof(RotationStylePage));
            Routing.RegisterRoute("settings/teamview", typeof(TeamViewPage));

            // Handle navigation to clear stacks when switching to Settings tab
            this.Navigated += OnShellNavigated;
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
