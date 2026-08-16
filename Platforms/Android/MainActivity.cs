using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using TurfTime2.Helpers;

namespace TurfTime2
{
    // AdjustResize so bottom Entry (e.g. Chat) stays above the soft keyboard instead of being covered.
    [Activity(
        Theme = "@style/Maui.SplashTheme",
        MainLauncher = true,
        LaunchMode = LaunchMode.SingleTop,
        ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density,
        WindowSoftInputMode = SoftInput.AdjustResize)]
    // Local team import deep link: turf://v1/import?team=...
    [IntentFilter(
        new[] { Intent.ActionView },
        Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
        DataScheme = "turf",
        DataHost = "v1",
        DataPathPrefix = "/import")]
    // Shared team join deep link: turf://v1/join?invite=CODE  (must be registered separately)
    [IntentFilter(
        new[] { Intent.ActionView },
        Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
        DataScheme = "turf",
        DataHost = "v1",
        DataPathPrefix = "/join")]
    [IntentFilter(
        new[] { Intent.ActionView },
        Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
        DataScheme = "https",
        DataHost = "andrewesterhuysen.github.io",
        DataPathPrefix = "/import")]
    [IntentFilter(
        new[] { Intent.ActionView },
        Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
        DataScheme = "https",
        DataHost = "andrewesterhuysen.github.io",
        DataPathPrefix = "/join")]
    public class MainActivity : MauiAppCompatActivity
    {
        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            HandleIncomingIntent(Intent);
        }

        protected override void OnNewIntent(Intent? intent)
        {
            base.OnNewIntent(intent);
            Intent = intent;
            HandleIncomingIntent(intent);
        }

        private static void HandleIncomingIntent(Intent? intent)
        {
            if (intent is null)
                return;

            HandleChatIntent(intent);
            HandleDeepLinkIntent(intent);
        }

        /// <summary>
        /// Explicitly forward VIEW intents (turf:// / https join-import) into App deep-link handling.
        /// MAUI may not always raise OnAppLinkRequestReceived for custom schemes on all OEMs.
        /// </summary>
        private static void HandleDeepLinkIntent(Intent? intent)
        {
            if (intent?.Action != Intent.ActionView)
                return;

            var data = intent.DataString;
            if (string.IsNullOrWhiteSpace(data))
                return;

            System.Diagnostics.Debug.WriteLine($"[MainActivity] Deep link intent: {data}");

            if (!Uri.TryCreate(data, UriKind.Absolute, out var uri))
                return;

            // Fire-and-forget; App waits for Shell / UI readiness.
            Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() =>
            {
                try
                {
                    if (Microsoft.Maui.Controls.Application.Current is App app)
                        app.HandleIncomingDeepLink(uri);
                    else
                        App.EnqueuePendingDeepLink(uri);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[MainActivity] Deep link handoff failed: {ex.GetType().Name}: {ex.Message}");
                }
            });
        }

        private static void HandleChatIntent(Intent? intent)
        {
            if (intent?.GetBooleanExtra("open_chat", false) != true)
                return;

            // Shell may not be ready on cold start — retry briefly.
            _ = Task.Run(async () =>
            {
                for (var i = 0; i < 20; i++)
                {
                    await Task.Delay(150).ConfigureAwait(false);
                    if (Microsoft.Maui.Controls.Application.Current?.Windows?.FirstOrDefault()?.Page is not null
                        || Shell.Current is not null)
                    {
                        ChatNavigation.OpenChat();
                        return;
                    }
                }
                ChatNavigation.OpenChat();
            });
        }
    }
}
