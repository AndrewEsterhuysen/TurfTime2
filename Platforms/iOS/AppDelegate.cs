using Foundation;
using UIKit;

namespace TurfTime2
{
    [Register("AppDelegate")]
    public class AppDelegate : MauiUIApplicationDelegate
    {
        protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

        /// <summary>
        /// Info.plist lists all orientations for App Store iPad multitasking rules,
        /// but the sideline UI is portrait-only — lock at runtime.
        /// </summary>
        [Export("application:supportedInterfaceOrientationsForWindow:")]
        public UIInterfaceOrientationMask GetSupportedInterfaceOrientations(
            UIApplication application, UIWindow? forWindow)
            => UIInterfaceOrientationMask.Portrait;

        /// <summary>
        /// Custom scheme opens (turf://v1/join?invite=…, turf://v1/import?team=…).
        /// Explicitly hand off so join/import still runs if MAUI's default path is missed.
        /// </summary>
        public override bool OpenUrl(UIApplication app, NSUrl url, NSDictionary options)
        {
            System.Diagnostics.Debug.WriteLine($"[iOS AppDelegate] OpenUrl: {url?.AbsoluteString}");
            TryHandOffDeepLink(url?.AbsoluteString);
            return base.OpenUrl(app, url, options);
        }

        public override bool ContinueUserActivity(
            UIApplication application,
            NSUserActivity userActivity,
            UIApplicationRestorationHandler completionHandler)
        {
            var url = userActivity?.WebPageUrl?.AbsoluteString;
            if (!string.IsNullOrEmpty(url))
            {
                System.Diagnostics.Debug.WriteLine($"[iOS AppDelegate] ContinueUserActivity: {url}");
                TryHandOffDeepLink(url);
            }

            return base.ContinueUserActivity(application, userActivity, completionHandler);
        }

        private static void TryHandOffDeepLink(string? absolute)
        {
            if (string.IsNullOrWhiteSpace(absolute))
                return;
            if (!Uri.TryCreate(absolute, UriKind.Absolute, out var uri))
                return;

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
                        $"[iOS AppDelegate] Deep link handoff failed: {ex.GetType().Name}: {ex.Message}");
                }
            });
        }
    }
}
