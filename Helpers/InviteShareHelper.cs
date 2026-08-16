namespace TurfTime2.Helpers;

/// <summary>
/// Builds and shares shared-team invite content (QR image + tappable deep link)
/// for messaging and email.
/// </summary>
public static class InviteShareHelper
{
    public static string BuildSharedJoinMessage(string teamName, string deepLink)
    {
        var name = string.IsNullOrWhiteSpace(teamName) ? "my team" : teamName.Trim();
        return
            $"Join my Turf Time team \"{name}\".\n\n" +
            $"Tap this link to open Turf Time and join (app must be installed):\n{deepLink}\n\n" +
            "If you received a QR image, press and hold it to open Turf Time and join.";
    }

    /// <summary>
    /// Share invite: deep-link text for email/SMS, plus QR image when the platform allows both.
    /// Falls back to text-only or image-only if combined share fails.
    /// </summary>
    public static async Task ShareSharedInviteAsync(string teamName, string deepLink, string qrPngPath)
    {
        var title = $"Join team - {teamName}";
        var message = BuildSharedJoinMessage(teamName, deepLink);

        try
        {
            await ShareInvitePlatformAsync(title, message, deepLink, qrPngPath).ConfigureAwait(true);
            return;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[InviteShare] Platform combined share failed: {ex.Message}");
        }

        // Fallback: text + link (works well for email / SMS)
        try
        {
            await Share.Default.RequestAsync(new ShareTextRequest
            {
                Title = title,
                Subject = title,
                Text = message,
                Uri = deepLink
            }).ConfigureAwait(true);
            return;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[InviteShare] Text share failed: {ex.Message}");
        }

        // Last resort: QR image only
        await Share.Default.RequestAsync(new ShareFileRequest
        {
            Title = title,
            File = new ShareFile(qrPngPath)
        }).ConfigureAwait(true);
    }

    /// <summary>Local-team QR image share (no deep-link body required).</summary>
    public static Task ShareLocalQrImageAsync(string title, string qrPngPath)
        => Share.Default.RequestAsync(new ShareFileRequest
        {
            Title = title,
            File = new ShareFile(qrPngPath)
        });

#if ANDROID
    private static async Task ShareInvitePlatformAsync(
        string title, string message, string deepLink, string qrPngPath)
    {
        var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity
            ?? throw new InvalidOperationException("No Android activity for share.");

        var context = activity;
        // MAUI Essentials FileProvider authority (see merged AndroidManifest)
        var authority = $"{context.PackageName}.fileProvider";
        var javaFile = new Java.IO.File(qrPngPath);

        Android.Net.Uri contentUri;
        try
        {
            contentUri = AndroidX.Core.Content.FileProvider.GetUriForFile(context, authority, javaFile);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[InviteShare] FileProvider failed: {ex.Message}");
            await Share.Default.RequestAsync(new ShareTextRequest
            {
                Title = title,
                Subject = title,
                Text = message,
                Uri = deepLink
            }).ConfigureAwait(true);
            return;
        }

        var intent = new Android.Content.Intent(Android.Content.Intent.ActionSend);
        intent.SetType("*/*");
        intent.PutExtra(Android.Content.Intent.ExtraStream, contentUri);
        intent.PutExtra(Android.Content.Intent.ExtraText, message);
        intent.PutExtra(Android.Content.Intent.ExtraSubject, title);
        intent.AddFlags(Android.Content.ActivityFlags.GrantReadUriPermission);

        var chooser = Android.Content.Intent.CreateChooser(intent, title);
        chooser!.AddFlags(Android.Content.ActivityFlags.GrantReadUriPermission);
        activity.StartActivity(chooser);
        await Task.CompletedTask;
    }
#elif IOS
    private static Task ShareInvitePlatformAsync(
        string title, string message, string deepLink, string qrPngPath)
    {
        var tcs = new TaskCompletionSource();

        UIKit.UIViewController? root = null;
        var scenes = UIKit.UIApplication.SharedApplication.ConnectedScenes;
        foreach (var scene in scenes)
        {
            if (scene is UIKit.UIWindowScene windowScene)
            {
                foreach (var w in windowScene.Windows)
                {
                    if (w.IsKeyWindow && w.RootViewController is not null)
                    {
                        root = w.RootViewController;
                        break;
                    }
                }
            }
            if (root is not null) break;
        }

        root ??= UIKit.UIApplication.SharedApplication.KeyWindow?.RootViewController
                 ?? throw new InvalidOperationException("No iOS root view controller for share.");

        while (root.PresentedViewController is not null)
            root = root.PresentedViewController;

        var items = new List<Foundation.NSObject>();
        if (File.Exists(qrPngPath))
        {
            var image = UIKit.UIImage.FromFile(qrPngPath);
            if (image is not null)
                items.Add(image);
        }

        items.Add(new Foundation.NSString(message));
        var nsUrl = Foundation.NSUrl.FromString(deepLink);
        if (nsUrl is not null)
            items.Add(nsUrl);

        var activity = new UIKit.UIActivityViewController(items.ToArray(), applicationActivities: null);
        if (activity.PopoverPresentationController is not null)
        {
            activity.PopoverPresentationController.SourceView = root.View!;
            var b = root.View!.Bounds;
            activity.PopoverPresentationController.SourceRect =
                new CoreGraphics.CGRect(b.X + b.Width / 2, b.Y + b.Height / 2, 1, 1);
            activity.PopoverPresentationController.PermittedArrowDirections = 0;
        }

        activity.CompletionWithItemsHandler = (_, _, _, _) => tcs.TrySetResult();
        root.PresentViewController(activity, animated: true, completionHandler: null);
        return tcs.Task;
    }
#else
    private static Task ShareInvitePlatformAsync(
        string title, string message, string deepLink, string qrPngPath)
        => Share.Default.RequestAsync(new ShareTextRequest
        {
            Title = title,
            Subject = title,
            Text = message,
            Uri = deepLink
        });
#endif
}
