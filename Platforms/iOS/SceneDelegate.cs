using Foundation;
using UIKit;

namespace TurfTime2
{
    /// <summary>
    /// iOS 27 SDK requires the UIScene life cycle; apps without a scene manifest abort at launch.
    /// Under scenes, URL opens arrive here instead of AppDelegate.OpenUrl/ContinueUserActivity.
    /// </summary>
    [Register("SceneDelegate")]
    public class SceneDelegate : MauiUISceneDelegate
    {
        public override void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions connectionOptions)
        {
            base.WillConnect(scene, session, connectionOptions);
            foreach (var ctx in connectionOptions.UrlContexts ?? new NSSet<UIOpenUrlContext>())
                AppDelegate.TryHandOffDeepLink(ctx.Url?.AbsoluteString);
            foreach (var act in connectionOptions.UserActivities ?? new NSSet<NSUserActivity>())
                AppDelegate.TryHandOffDeepLink(act.WebPageUrl?.AbsoluteString);
        }

        [Export("scene:openURLContexts:")]
        public void OpenUrlContexts(UIScene scene, NSSet<UIOpenUrlContext> urlContexts)
        {
            foreach (var ctx in urlContexts)
                AppDelegate.TryHandOffDeepLink(ctx.Url?.AbsoluteString);
        }

        public override bool ContinueUserActivity(UIScene scene, NSUserActivity userActivity)
        {
            AppDelegate.TryHandOffDeepLink(userActivity?.WebPageUrl?.AbsoluteString);
            return base.ContinueUserActivity(scene, userActivity);
        }
    }
}
