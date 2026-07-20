using Plugin.Firebase.CloudMessaging;

#if ANDROID
using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;
using AndroidApp = Android.App.Application;
#endif

#if IOS
using UserNotifications;
using Foundation;
using UIKit;
#endif

namespace TurfTime2.Services;

/// <summary>
/// Native FCM via Plugin.Firebase. Tokens are written to
/// <c>teams/{teamId}/members/{uid}.fcmTokens</c> via <see cref="IChatService"/> (REST).
/// </summary>
public class FcmService
{
    /// <summary>Must match Cloud Function android.notification.channelId and AndroidManifest meta-data.</summary>
    public const string ChatChannelId = "turftime_chat";

    private static FcmService? _instance;
    public static FcmService Instance => _instance ??= new FcmService();

    private string? _currentToken;
    private bool _isInitialized;
    private bool _handlersWired;
    private int _saveInFlight;
    private int _localNotifyId = 4000;

    private FcmService() { }

    private static T? GetService<T>() where T : class
    {
        try
        {
            return Microsoft.Maui.Controls.Application.Current?.Handler?.MauiContext?.Services.GetService<T>();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Request permission, obtain FCM token, and persist it for the current shared team.
    /// Safe to call repeatedly (e.g. after join, on Chat open, on resume).
    /// </summary>
    public async Task<bool> InitializeAsync()
    {
        try
        {
            System.Diagnostics.Debug.WriteLine("[FCM] 🔔 Initializing Firebase Cloud Messaging…");

            var auth = GetService<IFirebaseAuthService>();
            if (auth is not null)
            {
                var uid = await auth.EnsureSignedInAsync();
                System.Diagnostics.Debug.WriteLine(
                    uid is null
                        ? "[FCM] ⚠️ Auth not ready"
                        : $"[FCM] Auth uid={uid[..Math.Min(8, uid.Length)]}…");
            }

#if ANDROID
            var status = await Permissions.CheckStatusAsync<Permissions.PostNotifications>();
            if (status != PermissionStatus.Granted)
                status = await Permissions.RequestAsync<Permissions.PostNotifications>();
            System.Diagnostics.Debug.WriteLine($"[FCM] Android notification permission: {status}");
            EnsureAndroidNotificationChannel();
#endif

#if IOS
            await RequestIosNotificationPermissionAsync();
#endif

            // iOS: CheckIfValidAsync also validates APNs registration.
            try
            {
                await CrossFirebaseCloudMessaging.Current.CheckIfValidAsync();
                System.Diagnostics.Debug.WriteLine("[FCM] ✅ CheckIfValidAsync OK");
            }
            catch (Exception validEx)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[FCM] ⚠️ CheckIfValidAsync: {validEx.GetType().Name}: {validEx.Message}");
            }

#if IOS
            // Plugin.Firebase may install its own UNUserNotificationCenter.Delegate during
            // CheckIfValid/Initialize. Re-assert ours last so foreground banners always show.
            InstallIosNotificationDelegate();
#endif

            try
            {
                _currentToken = await CrossFirebaseCloudMessaging.Current.GetTokenAsync();
            }
            catch (Exception tokenEx)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[FCM] ❌ GetTokenAsync: {tokenEx.GetType().Name}: {tokenEx.Message}");
                return false;
            }

            if (string.IsNullOrEmpty(_currentToken))
            {
                System.Diagnostics.Debug.WriteLine("[FCM] ❌ Empty FCM token");
                return false;
            }

            System.Diagnostics.Debug.WriteLine(
                $"[FCM] ✅ Token: {_currentToken[..Math.Min(24, _currentToken.Length)]}… (len={_currentToken.Length})");

            WireHandlersOnce();
            await UpdateTokenInFirestoreAsync(_currentToken);

            _isInitialized = true;
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FCM] ❌ Init: {ex.GetType().FullName}: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[FCM] Stack: {ex.StackTrace}");
            return false;
        }
    }

    private void WireHandlersOnce()
    {
        if (_handlersWired) return;
        _handlersWired = true;

        CrossFirebaseCloudMessaging.Current.TokenChanged += OnTokenChanged;
        CrossFirebaseCloudMessaging.Current.NotificationReceived += OnNotificationReceived;
        CrossFirebaseCloudMessaging.Current.NotificationTapped += OnNotificationTapped;
        CrossFirebaseCloudMessaging.Current.Error += (_, e) =>
            System.Diagnostics.Debug.WriteLine($"[FCM] ❌ Plugin error: {e}");
    }

    private void OnNotificationReceived(object? sender, object e)
    {
        // Android chat pushes are data messages so we always render them ourselves
        // (with the full-color app LargeIcon). iOS still uses APNs system UI.
        string title = "Turf Time";
        string body = "New team message";
        try
        {
            // Plugin.Firebase event args expose .Notification with Title/Body,
            // and sometimes Data["title"] / Data["body"] for data-only messages.
            dynamic dyn = e;
            var n = dyn.Notification;
            if (n != null)
            {
                title = (n.Title as string) ?? title;
                body = (n.Body as string) ?? body;
            }

            try
            {
                var data = dyn.Data ?? dyn.Notification?.Data;
                if (data != null)
                {
                    string? dt = null, db = null;
                    try { dt = data["title"] as string ?? data["Title"] as string; } catch { /* ignore */ }
                    try { db = data["body"] as string ?? data["Body"] as string; } catch { /* ignore */ }
                    if (!string.IsNullOrWhiteSpace(dt)) title = dt!;
                    if (!string.IsNullOrWhiteSpace(db)) body = db!;
                }
            }
            catch { /* optional data bag */ }
        }
        catch { /* best-effort */ }

        System.Diagnostics.Debug.WriteLine($"[FCM] 📩 Notification received: {title} — {body}");
#if ANDROID
        // Always post our own notification so LargeIcon (app art) is applied.
        try { ShowLocalNotification(title, body); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FCM] Local notify failed: {ex.Message}");
        }
#else
        // iOS: system APNs already presents with the app icon; only post local if
        // we got a data-only payload while foregrounded (WillPresent handles banners).
        try { ShowLocalNotification(title, body); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FCM] Local notify failed: {ex.Message}");
        }
#endif
    }

    private void OnNotificationTapped(object? sender, object e)
    {
        System.Diagnostics.Debug.WriteLine("[FCM] 👆 Notification tapped");
        _ = MainThread.InvokeOnMainThreadAsync(async () =>
        {
            try
            {
                if (Shell.Current is not null)
                    await Shell.Current.GoToAsync("//ChatPage");
            }
            catch (Exception navEx)
            {
                System.Diagnostics.Debug.WriteLine($"[FCM] Navigate to Chat: {navEx.Message}");
            }
        });
    }

    private void ShowLocalNotification(string title, string body)
    {
#if ANDROID
        EnsureAndroidNotificationChannel();
        var context = AndroidApp.Context;
        var packageName = context.PackageName ?? "";
        var launchIntent = context.PackageManager?.GetLaunchIntentForPackage(packageName);
        PendingIntent? pending = null;
        if (launchIntent != null)
        {
            var flags = PendingIntentFlags.UpdateCurrent;
            if (OperatingSystem.IsAndroidVersionAtLeast(23))
                flags |= PendingIntentFlags.Immutable;
            pending = PendingIntent.GetActivity(context, 0, launchIntent, flags);
        }

        // Small icon = white silhouette (status bar). Large icon = full-color app art (like iOS).
        var smallIconId = context.Resources?.GetIdentifier("ic_stat_turftime", "drawable", packageName) ?? 0;
        if (smallIconId == 0)
            smallIconId = Android.Resource.Drawable.StatNotifyChat;

        var colorId = context.Resources?.GetIdentifier("turftime_notification", "color", packageName) ?? 0;
        var accent = colorId != 0
            ? new Android.Graphics.Color(context.Resources!.GetColor(colorId, context.Theme))
            : new Android.Graphics.Color(46, 125, 50); // #2E7D32

        var builder = new NotificationCompat.Builder(context, ChatChannelId)
            .SetContentTitle(title)
            .SetContentText(body)
            .SetStyle(new NotificationCompat.BigTextStyle().BigText(body))
            .SetSmallIcon(smallIconId)
            .SetColor(accent)
            .SetAutoCancel(true)
            .SetPriority(NotificationCompat.PriorityHigh)
            .SetCategory(NotificationCompat.CategoryMessage)
            .SetVisibility(NotificationCompat.VisibilityPublic)
            .SetDefaults((int)(NotificationDefaults.Sound | NotificationDefaults.Vibrate));

        // Full-color Turf Time app icon (right side of notification, similar to iOS).
        var large = LoadAppIconBitmap(context, packageName);
        if (large != null)
            builder.SetLargeIcon(large);

        if (pending != null)
            builder.SetContentIntent(pending);

        var manager = NotificationManagerCompat.From(context);
        var id = Interlocked.Increment(ref _localNotifyId);
        manager.Notify(id, builder.Build());
        System.Diagnostics.Debug.WriteLine(
            $"[FCM] ✅ Local Android notification posted id={id} largeIcon={(large != null)}");
#elif IOS
        var content = new UNMutableNotificationContent
        {
            Title = title ?? "Turf Time",
            Body = body ?? "",
            Sound = UNNotificationSound.Default,
            Badge = 1
        };
        var trigger = UNTimeIntervalNotificationTrigger.CreateTrigger(0.1, false);
        var requestId = $"turftime-chat-{Guid.NewGuid():N}";
        var request = UNNotificationRequest.FromIdentifier(requestId, content, trigger);
        UNUserNotificationCenter.Current.AddNotificationRequest(request, err =>
        {
            if (err != null)
                System.Diagnostics.Debug.WriteLine($"[FCM] iOS local notify error: {err.LocalizedDescription}");
            else
                System.Diagnostics.Debug.WriteLine("[FCM] ✅ Local iOS notification scheduled");
        });
#endif
    }

#if ANDROID
    /// <summary>
    /// Full-color launcher icon for notification LargeIcon (the "beautiful" app art iOS shows).
    /// Tries drawable/ic_notification_large then mipmap/appicon.
    /// </summary>
    private static Android.Graphics.Bitmap? LoadAppIconBitmap(Context context, string packageName)
    {
        try
        {
            var res = context.Resources;
            if (res is null) return null;

            var largeId = res.GetIdentifier("ic_notification_large", "drawable", packageName);
            if (largeId == 0)
                largeId = res.GetIdentifier("appicon", "mipmap", packageName);
            if (largeId == 0)
                largeId = res.GetIdentifier("appicon_round", "mipmap", packageName);
            if (largeId == 0)
                return null;

            var bmp = Android.Graphics.BitmapFactory.DecodeResource(res, largeId);
            if (bmp is null) return null;

            // Keep a reasonable size for the notification large-icon slot (~64dp).
            var targetPx = (int)(64 * res.DisplayMetrics!.Density);
            if (bmp.Width > targetPx || bmp.Height > targetPx)
            {
                var scaled = Android.Graphics.Bitmap.CreateScaledBitmap(bmp, targetPx, targetPx, true);
                if (!ReferenceEquals(scaled, bmp))
                    bmp.Recycle();
                return scaled;
            }

            return bmp;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FCM] LoadAppIconBitmap: {ex.Message}");
            return null;
        }
    }

    private static void EnsureAndroidNotificationChannel()
    {
        try
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;
            var context = AndroidApp.Context;
            var manager = (NotificationManager?)context.GetSystemService(Context.NotificationService);
            if (manager == null) return;

            // New channel id — existing "general" may have been auto-created at LOW/DEFAULT
            // importance (Android never upgrades importance after create).
            var existing = manager.GetNotificationChannel(ChatChannelId);
            if (existing != null && existing.Importance >= NotificationImportance.High)
                return;

            if (existing != null)
            {
                manager.DeleteNotificationChannel(ChatChannelId);
                System.Diagnostics.Debug.WriteLine($"[FCM] Deleted stale channel '{ChatChannelId}' (importance={existing.Importance})");
            }

            // Also remove legacy auto-channel if present (optional cleanup).
            try { manager.DeleteNotificationChannel("general"); } catch { /* ignore */ }

            var channel = new NotificationChannel(ChatChannelId, "Team Chat", NotificationImportance.High)
            {
                Description = "Chat messages from shared teams",
                LockscreenVisibility = NotificationVisibility.Public
            };
            channel.EnableVibration(true);
            channel.EnableLights(true);
            channel.SetShowBadge(true);
            channel.SetSound(
                Android.Provider.Settings.System.DefaultNotificationUri,
                new Android.Media.AudioAttributes.Builder()
                    .SetUsage(Android.Media.AudioUsageKind.Notification)
                    .SetContentType(Android.Media.AudioContentType.Sonification)
                    .Build());
            manager.CreateNotificationChannel(channel);
            System.Diagnostics.Debug.WriteLine($"[FCM] ✅ Created HIGH channel '{ChatChannelId}'");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FCM] Channel: {ex.Message}");
        }
    }
#endif

#if IOS
    private static TurfTimeNotificationDelegate? _iosNotifDelegate;

    private static async Task RequestIosNotificationPermissionAsync()
    {
        try
        {
            var center = UNUserNotificationCenter.Current;
            var (granted, error) = await center.RequestAuthorizationAsync(
                UNAuthorizationOptions.Alert
                | UNAuthorizationOptions.Badge
                | UNAuthorizationOptions.Sound);
            System.Diagnostics.Debug.WriteLine(
                $"[FCM] iOS notification permission granted={granted} error={error?.LocalizedDescription ?? "none"}");

            // Log current settings so we can see Banner vs Notification Center only.
            var settings = await center.GetNotificationSettingsAsync();
            System.Diagnostics.Debug.WriteLine(
                $"[FCM] iOS settings: auth={settings.AuthorizationStatus} " +
                $"alert={settings.AlertSetting} banner={settings.AlertStyle} " +
                $"lock={settings.LockScreenSetting} notifCenter={settings.NotificationCenterSetting} " +
                $"sound={settings.SoundSetting}");

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                UIApplication.SharedApplication.RegisterForRemoteNotifications();
            });

            InstallIosNotificationDelegate();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FCM] iOS permission request: {ex.Message}");
        }
    }

    /// <summary>
    /// Keep a strong reference and install last so banners show in foreground.
    /// </summary>
    public static void InstallIosNotificationDelegate()
    {
        try
        {
            _iosNotifDelegate ??= new TurfTimeNotificationDelegate();
            UNUserNotificationCenter.Current.Delegate = _iosNotifDelegate;
            System.Diagnostics.Debug.WriteLine("[FCM] ✅ iOS UNUserNotificationCenter.Delegate installed (banners on present)");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FCM] InstallIosNotificationDelegate: {ex.Message}");
        }
    }

    private sealed class TurfTimeNotificationDelegate : UNUserNotificationCenterDelegate
    {
        public override void WillPresentNotification(
            UNUserNotificationCenter center,
            UNNotification notification,
            Action<UNNotificationPresentationOptions> completionHandler)
        {
            // Force banner + sound even when Turf Time is foregrounded.
            // Banner/List are iOS 14+; Alert covers older.
            var options = UNNotificationPresentationOptions.Sound
                          | UNNotificationPresentationOptions.Badge;
            if (UIDevice.CurrentDevice.CheckSystemVersion(14, 0))
            {
                options |= UNNotificationPresentationOptions.Banner
                           | UNNotificationPresentationOptions.List;
            }
            else
            {
                options |= UNNotificationPresentationOptions.Alert;
            }

            System.Diagnostics.Debug.WriteLine(
                $"[FCM] WillPresent foreground banner: {notification.Request.Content.Title}");
            completionHandler(options);
        }

        public override void DidReceiveNotificationResponse(
            UNUserNotificationCenter center,
            UNNotificationResponse response,
            Action completionHandler)
        {
            _ = MainThread.InvokeOnMainThreadAsync(async () =>
            {
                try
                {
                    if (Shell.Current is not null)
                        await Shell.Current.GoToAsync("//ChatPage");
                }
                catch { /* ignore */ }
            });
            completionHandler();
        }
    }
#endif

    private void OnTokenChanged(object? sender, EventArgs e)
    {
        Task.Run(async () =>
        {
            try
            {
                _currentToken = await CrossFirebaseCloudMessaging.Current.GetTokenAsync();
                System.Diagnostics.Debug.WriteLine("[FCM] Token refreshed — saving…");
                await UpdateTokenInFirestoreAsync(_currentToken);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FCM] Token refresh: {ex.Message}");
            }
        });
    }

    public async Task<string?> GetTokenAsync()
    {
        if (!string.IsNullOrEmpty(_currentToken)) return _currentToken;
        try
        {
            _currentToken = await CrossFirebaseCloudMessaging.Current.GetTokenAsync();
            return _currentToken;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FCM] GetToken: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Persist the current (or provided) FCM token on the active shared team's member doc.
    /// Call after create/join team, Chat open, and app resume.
    /// </summary>
    public async Task UpdateTokenInFirestoreAsync(string? token = null)
    {
        if (Interlocked.CompareExchange(ref _saveInFlight, 1, 0) != 0)
            return;

        try
        {
            token ??= _currentToken;
            if (string.IsNullOrEmpty(token))
            {
                try { token = await CrossFirebaseCloudMessaging.Current.GetTokenAsync(); }
                catch { /* ignore */ }
                _currentToken = token;
            }

            if (string.IsNullOrEmpty(token))
            {
                System.Diagnostics.Debug.WriteLine("[FCM] No token to save");
                return;
            }

            var teamId = Preferences.Get("team_id", "");
            var teamMode = Preferences.Get("team_mode", "");
            if (string.IsNullOrEmpty(teamId)
                || teamId.StartsWith("local_", StringComparison.Ordinal)
                || string.Equals(teamMode, "local", StringComparison.Ordinal))
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[FCM] Skip token save (team_id='{teamId}', mode='{teamMode}') — need shared team");
                return;
            }

            var chat = GetService<IChatService>();
            if (chat is null)
            {
                System.Diagnostics.Debug.WriteLine("[FCM] IChatService unavailable — DI not ready yet");
                return;
            }

            var ok = await chat.RegisterFcmTokenAsync(teamId, token);
            System.Diagnostics.Debug.WriteLine(
                ok
                    ? $"[FCM] ✅ Token saved for team={teamId}"
                    : $"[FCM] ❌ Token save failed for team={teamId}");
        }
        finally
        {
            Interlocked.Exchange(ref _saveInFlight, 0);
        }
    }

    /// <summary>Convenience: ensure init + re-save token (join team / resume).</summary>
    public async Task EnsureRegisteredForCurrentTeamAsync()
    {
        if (!_isInitialized || string.IsNullOrEmpty(_currentToken))
            await InitializeAsync();
        else
            await UpdateTokenInFirestoreAsync();
    }

    public Task RemoveTokenFromFirestoreAsync()
    {
        System.Diagnostics.Debug.WriteLine("[FCM] RemoveTokenFromFirestoreAsync not implemented yet");
        return Task.CompletedTask;
    }
}
