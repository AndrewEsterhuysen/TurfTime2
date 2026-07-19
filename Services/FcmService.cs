using Plugin.Firebase.CloudMessaging;

namespace TurfTime2.Services;

/// <summary>
/// Native FCM via Plugin.Firebase. Token persistence uses <see cref="IChatService"/> (Firestore SDK).
/// </summary>
public class FcmService
{
    private static FcmService? _instance;
    public static FcmService Instance => _instance ??= new FcmService();

    private string? _currentToken;
    private bool _isInitialized;

    private FcmService() { }

    private static T? GetService<T>() where T : class
    {
        try
        {
            return Application.Current?.Handler?.MauiContext?.Services.GetService<T>();
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> InitializeAsync()
    {
        if (_isInitialized)
            return true;

        try
        {
            System.Diagnostics.Debug.WriteLine("[FCM] 🔔 Initializing Firebase Cloud Messaging…");

            var auth = GetService<IFirebaseAuthService>();
            if (auth is not null)
                await auth.EnsureSignedInAsync();

#if ANDROID
            var status = await Permissions.CheckStatusAsync<Permissions.PostNotifications>();
            if (status != PermissionStatus.Granted)
                status = await Permissions.RequestAsync<Permissions.PostNotifications>();
            if (status != PermissionStatus.Granted)
            {
                System.Diagnostics.Debug.WriteLine("[FCM] ❌ Notification permission denied (Android)");
                return false;
            }
            EnsureAndroidNotificationChannel();
#endif

            try
            {
                await CrossFirebaseCloudMessaging.Current.CheckIfValidAsync();
                System.Diagnostics.Debug.WriteLine("[FCM] ✅ CheckIfValidAsync OK");
            }
            catch (Exception validEx)
            {
                System.Diagnostics.Debug.WriteLine($"[FCM] ❌ CheckIfValidAsync: {validEx.Message}");
                return false;
            }

            _currentToken = await CrossFirebaseCloudMessaging.Current.GetTokenAsync();
            if (string.IsNullOrEmpty(_currentToken))
            {
                System.Diagnostics.Debug.WriteLine("[FCM] ❌ Empty FCM token");
                return false;
            }

            System.Diagnostics.Debug.WriteLine(
                $"[FCM] ✅ Token: {_currentToken.Substring(0, Math.Min(20, _currentToken.Length))}…");

            await UpdateTokenInFirestoreAsync(_currentToken);

            CrossFirebaseCloudMessaging.Current.TokenChanged += OnTokenChanged;
            CrossFirebaseCloudMessaging.Current.NotificationReceived += (_, _) =>
                System.Diagnostics.Debug.WriteLine("[FCM] 📩 Notification received");
            CrossFirebaseCloudMessaging.Current.NotificationTapped += (_, _) =>
                System.Diagnostics.Debug.WriteLine("[FCM] 👆 Notification tapped");

            _isInitialized = true;
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FCM] ❌ Init: {ex.GetType().FullName}: {ex.Message}");
            return false;
        }
    }

#if ANDROID
    private static void EnsureAndroidNotificationChannel()
    {
        try
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;
            var context = Android.App.Application.Context;
            var manager = (Android.App.NotificationManager?)context.GetSystemService(Android.Content.Context.NotificationService);
            if (manager == null) return;
            const string channelId = "general";
            if (manager.GetNotificationChannel(channelId) != null) return;
            var channel = new Android.App.NotificationChannel(channelId, "Team Chat", Android.App.NotificationImportance.High)
            {
                Description = "Chat messages from shared teams"
            };
            channel.EnableVibration(true);
            manager.CreateNotificationChannel(channel);
            System.Diagnostics.Debug.WriteLine("[FCM] ✅ Created channel 'general'");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FCM] Channel: {ex.Message}");
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
        if (_currentToken != null) return _currentToken;
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

    public async Task UpdateTokenInFirestoreAsync(string? token = null)
    {
        token ??= _currentToken;
        if (string.IsNullOrEmpty(token)) return;

        var teamId = Preferences.Get("team_id", "");
        if (string.IsNullOrEmpty(teamId) || teamId.StartsWith("local_", StringComparison.Ordinal))
        {
            System.Diagnostics.Debug.WriteLine("[FCM] No shared team — token not saved yet");
            return;
        }

        var chat = GetService<IChatService>();
        if (chat is null)
        {
            System.Diagnostics.Debug.WriteLine("[FCM] IChatService unavailable");
            return;
        }

        var ok = await chat.RegisterFcmTokenAsync(teamId, token);
        System.Diagnostics.Debug.WriteLine(ok ? "[FCM] ✅ Token saved via ChatService" : "[FCM] ❌ Token save failed");
    }

    public Task RemoveTokenFromFirestoreAsync()
    {
        // Optional: FieldValue.ArrayRemove — not critical for Option B first pass.
        System.Diagnostics.Debug.WriteLine("[FCM] RemoveTokenFromFirestoreAsync not implemented on SDK path yet");
        return Task.CompletedTask;
    }
}
