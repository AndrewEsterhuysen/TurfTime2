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
    [IntentFilter(
        new[] { Intent.ActionView },
        Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
        DataScheme = "turf",
        DataHost = "v1",
        DataPathPrefix = "/import")]
    [IntentFilter(
        new[] { Intent.ActionView },
        Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
        DataScheme = "https",
        DataHost = "andrewesterhuysen.github.io",
        DataPathPrefix = "/import")]
    public class MainActivity : MauiAppCompatActivity
    {
        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            HandleChatIntent(Intent);
        }

        protected override void OnNewIntent(Intent? intent)
        {
            base.OnNewIntent(intent);
            Intent = intent;
            HandleChatIntent(intent);
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
