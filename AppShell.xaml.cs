namespace TurfTime2
{
    public partial class AppShell : Shell
    {
        public AppShell()
        {
            InitializeComponent();

            // Register routes for navigation from Settings page
            Routing.RegisterRoute("settings/log", typeof(LogPage));
            Routing.RegisterRoute("settings/skins", typeof(SkinsPage));
            Routing.RegisterRoute("settings/rotationstyle", typeof(RotationStylePage));
            Routing.RegisterRoute("settings/teamview", typeof(TeamViewPage));

            // Handle navigation to clear stacks when switching to Settings tab
            this.Navigated += OnShellNavigated;
        }

        private async void OnShellNavigated(object sender, ShellNavigatedEventArgs e)
        {
            // When navigating to Settings tab from another tab, pop any navigation stack
            if (e.Current.Location.ToString().Contains("SettingsPage") && 
                e.Source == ShellNavigationSource.ShellItemChanged)
            {
                // User clicked on Settings tab, ensure we're at the root
                while (Navigation.NavigationStack.Count > 1)
                {
                    try
                    {
                        await Navigation.PopAsync(false);
                    }
                    catch
                    {
                        break;
                    }
                }
            }
        }
    }
}
