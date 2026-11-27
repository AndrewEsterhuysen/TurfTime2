using Microsoft.UI.Xaml;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace TurfTime2.WinUI
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : MauiWinUIApplication
    {
        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            this.InitializeComponent();

            // Setup client: leave a trace when something goes wrong (no crash reporter on Windows yet).
            this.UnhandledException += (_, e) => LogCrash("WinUI", e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash("AppDomain", e.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (_, e) => LogCrash("Task", e.Exception);
        }

        private static void LogCrash(string source, Exception? ex)
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TurfTimer");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "windows-errors.log"),
                    $"{DateTime.Now:O} [{source}] {ex}\n\n");
            }
            catch { /* never throw from the handler */ }
        }

        protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
    }

}
