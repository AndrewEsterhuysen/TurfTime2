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
        }
    }
}
