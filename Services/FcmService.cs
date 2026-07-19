using Plugin.Firebase.CloudMessaging;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace TurfTime2.Services;

public class FcmService
{
    private static FcmService? _instance;
    public static FcmService Instance => _instance ??= new FcmService();

    private const string FirebaseApiKey = "AIzaSyDAKivCFX5kYYZ6SkAQluBNdR92I320glk";
    private static string? _firebaseIdToken;

    private string? _currentToken;
    private bool _isInitialized;

    /// <summary>
    /// Set this to save the FCM token via the JS-authenticated chat session.
    /// Called with the raw FCM token string; should forward to ChatPage.SaveFcmTokenAsync.
    /// </summary>
    public static Func<string, Task>? SaveTokenViaJs { get; set; }

    private FcmService() { }

    private static async Task<string?> GetAuthTokenAsync()
    {
        if (!string.IsNullOrEmpty(_firebaseIdToken))
            return _firebaseIdToken;

        try
        {
            var url = $"https://identitytoolkit.googleapis.com/v1/accounts:signUp?key={FirebaseApiKey}";
            var body = JsonSerializer.Serialize(new { returnSecureToken = true });
            var content = new StringContent(body, Encoding.UTF8, "application/json");

            using var client = new HttpClient();
            var response = await client.PostAsync(url, content);

            if (!response.IsSuccessStatusCode)
            {
                Debug.WriteLine($"[FCM] ❌ Auth failed: {response.StatusCode}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);
            _firebaseIdToken = doc.RootElement.GetProperty("idToken").GetString();
            Debug.WriteLine("[FCM] ✅ Auth token acquired");
            return _firebaseIdToken;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FCM] ❌ Auth error: {ex.Message}");
            return null;
        }
    }

    public async Task<bool> InitializeAsync()
    {
        if (_isInitialized)
            return true;

        try
        {
            Debug.WriteLine("[FCM] 🔔 Initializing Firebase Cloud Messaging...");

#if ANDROID
            // Android 13+ runtime notification permission
            var status = await Permissions.CheckStatusAsync<Permissions.PostNotifications>();

            if (status != PermissionStatus.Granted)
            {
                status = await Permissions.RequestAsync<Permissions.PostNotifications>();
            }

            if (status != PermissionStatus.Granted)
            {
                Debug.WriteLine("[FCM] ❌ Notification permission denied (Android)");
                return false;
            }

            Debug.WriteLine("[FCM] ✅ Notification permission granted (Android)");

            // Must match Cloud Function android.notification.channelId ('general').
            // Without this channel on API 26+, system notifications are dropped silently.
            EnsureAndroidNotificationChannel();
#endif

            // iOS: CheckIfValidAsync requests UNUserNotificationCenter authorization, registers for
            // remote notifications (system "Allow Notifications" sheet), and validates APNs readiness.
            // Android: validates FCM registration / channel setup.
            // Do not use Permissions.PostNotifications alone on iOS — it does not register for push
            // and will not surface a Notifications entry under Settings → Turf Time.
            try
            {
                await CrossFirebaseCloudMessaging.Current.CheckIfValidAsync();
                Debug.WriteLine("[FCM] ✅ CheckIfValidAsync succeeded (permission + registration OK)");
            }
            catch (Exception validEx)
            {
                Debug.WriteLine($"[FCM] ❌ CheckIfValidAsync failed: {validEx.GetType().FullName}: {validEx.Message}");
                Debug.WriteLine($"[FCM] Stack: {validEx.StackTrace}");
                return false;
            }

            // Get FCM token (requires native Firebase + on iOS a valid APNs registration).
            _currentToken = await CrossFirebaseCloudMessaging.Current.GetTokenAsync();
            if (string.IsNullOrEmpty(_currentToken))
            {
                Debug.WriteLine("[FCM] ❌ GetTokenAsync returned empty — push cannot work until a token is issued");
                return false;
            }

            Debug.WriteLine($"[FCM] ✅ Token received: {_currentToken.Substring(0, Math.Min(20, _currentToken.Length))}...");

            // Best-effort immediate save (only works once Chat sets SaveTokenViaJs, or if REST
            // prefs exist). ChatPage also re-registers after WebView auth is ready.
            await UpdateTokenInFirestoreAsync(_currentToken);

            // Subscribe to token refresh events
            CrossFirebaseCloudMessaging.Current.TokenChanged += OnTokenChanged;

            // Subscribe to notification received events
            CrossFirebaseCloudMessaging.Current.NotificationReceived += OnNotificationReceived;
            CrossFirebaseCloudMessaging.Current.NotificationTapped += OnNotificationTapped;

            _isInitialized = true;
            Debug.WriteLine("[FCM] ✅ FCM initialized successfully");

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FCM] ❌ Initialization error: {ex.GetType().FullName}: {ex.Message}");
            Debug.WriteLine($"[FCM] Stack: {ex.StackTrace}");
            if (ex.InnerException != null)
                Debug.WriteLine($"[FCM] Inner: {ex.InnerException}");
            return false;
        }
    }

#if ANDROID
    /// <summary>
    /// Creates the high-importance channel used by sendChatNotification (channelId: general).
    /// </summary>
    private static void EnsureAndroidNotificationChannel()
    {
        try
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(26))
                return;

            var context = Android.App.Application.Context;
            var manager = (Android.App.NotificationManager?)context.GetSystemService(Android.Content.Context.NotificationService);
            if (manager == null)
                return;

            const string channelId = "general";
            if (manager.GetNotificationChannel(channelId) != null)
            {
                Debug.WriteLine("[FCM] ✅ Android notification channel 'general' already exists");
                return;
            }

            var channel = new Android.App.NotificationChannel(
                channelId,
                "Team Chat",
                Android.App.NotificationImportance.High)
            {
                Description = "Chat messages from shared teams"
            };
            channel.EnableVibration(true);
            channel.SetShowBadge(true);
            manager.CreateNotificationChannel(channel);
            Debug.WriteLine("[FCM] ✅ Created Android notification channel 'general'");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FCM] ⚠️ Could not create notification channel: {ex.Message}");
        }
    }
#endif

    private void OnTokenChanged(object? sender, EventArgs e)
    {
        // Token changed - get new token.
        // Fire-and-forget is intentional (event handler); the inner Task.Run has its own try/catch
        // so an error here cannot become an unobserved exception that the new global handlers will report.
        Task.Run(async () =>
        {
            try
            {
                _currentToken = await CrossFirebaseCloudMessaging.Current.GetTokenAsync();
                Debug.WriteLine($"[FCM] 🔄 Token refreshed: {_currentToken?.Substring(0, Math.Min(20, _currentToken?.Length ?? 0))}...");

                // Update token in Firestore (REST path, same as Android)
                await UpdateTokenInFirestoreAsync(_currentToken);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FCM] ❌ Token refresh error: {ex.GetType().FullName}: {ex.Message}");
                Debug.WriteLine($"[FCM] Stack: {ex.StackTrace}");
            }
        });
    }

    private void OnNotificationReceived(object? sender, EventArgs e)
    {
        Debug.WriteLine($"[FCM] 📩 Notification received");
        // You can show an in-app notification here if needed
    }

    private void OnNotificationTapped(object? sender, Plugin.Firebase.CloudMessaging.EventArgs.FCMNotificationTappedEventArgs e)
    {
        Debug.WriteLine($"[FCM] 👆 Notification tapped");

        // For now, just log - navigation can be enhanced later
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                // Navigate to chat page if needed
                Debug.WriteLine($"[FCM] ✅ Notification tap handled");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FCM] ❌ Navigation error: {ex.Message}");
            }
        });
    }

    public async Task<string?> GetTokenAsync()
    {
        if (_currentToken != null)
            return _currentToken;

        try
        {
            _currentToken = await CrossFirebaseCloudMessaging.Current.GetTokenAsync();
            return _currentToken;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FCM] ❌ Error getting token: {ex.Message}");
            return null;
        }
    }

    public async Task UpdateTokenInFirestoreAsync(string? token = null)
    {
        try
        {
            token ??= _currentToken;

            if (string.IsNullOrEmpty(token))
            {
                Debug.WriteLine("[FCM] ⚠️ No token to update");
                return;
            }

            // Preferred path: save via the JS chat session (authenticated as the chat member uid).
            // This is the only reliable path — chat auth lives in the WebView, not Preferences.
            if (SaveTokenViaJs != null)
            {
                Debug.WriteLine("[FCM] 💾 Saving FCM token via JS auth session");
                await SaveTokenViaJs(token);
                return;
            }

            // Fallback: REST API using anonymous sign-in (may fail if member doc doesn't exist
            // or security rules require the chat user's uid — Preferences "user_id" is usually unset).
            var teamId = Preferences.Get("team_id", "");
            var userId = Preferences.Get("user_id", "");

            if (string.IsNullOrEmpty(teamId) || string.IsNullOrEmpty(userId))
            {
                Debug.WriteLine("[FCM] ⚠️ No JS saver and no team/user_id prefs — open Chat once to register push token");
                return;
            }

            // Skip local teams
            if (teamId.StartsWith("local_"))
            {
                Debug.WriteLine("[FCM] ⚠️ Local team, skipping FCM token update");
                return;
            }

            Debug.WriteLine($"[FCM] 💾 Updating FCM token in Firestore for user: {userId}");

            // Use REST API to update token in Firestore (consistent with existing pattern that works on Android)
            await UpdateTokenViaRestAsync(teamId, userId, token);

            Debug.WriteLine("[FCM] ✅ Token updated in Firestore");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FCM] ❌ Error updating token: {ex.GetType().FullName}: {ex.Message}");
            Debug.WriteLine($"[FCM] Stack: {ex.StackTrace}");
        }
    }

    /// <summary>
    /// Saves the native FCM token using the chat WebView user's Firebase ID token.
    /// This is the reliable path: same uid as messages, no WebView Promise races.
    /// Returns true if the token is stored (or already present).
    /// </summary>
    public async Task<bool> SaveTokenWithChatAuthAsync(string teamId, string userId, string idToken, string? fcmToken = null)
    {
        fcmToken ??= _currentToken;
        if (string.IsNullOrWhiteSpace(teamId) || string.IsNullOrWhiteSpace(userId) ||
            string.IsNullOrWhiteSpace(idToken) || string.IsNullOrWhiteSpace(fcmToken))
        {
            Debug.WriteLine("[FCM] ⚠️ SaveTokenWithChatAuthAsync missing team/user/idToken/fcmToken");
            return false;
        }

        if (teamId.StartsWith("local_", StringComparison.Ordinal))
            return false;

        // Persist chat identity so later refreshes can reuse it.
        Preferences.Set("chat_user_id", userId);
        Preferences.Set("user_id", userId);

        return await UpdateTokenViaRestAsync(teamId, userId, fcmToken, idToken);
    }

    private async Task UpdateTokenViaRestAsync(string teamId, string userId, string token)
    {
        var authToken = await GetAuthTokenAsync();
        if (string.IsNullOrEmpty(authToken))
        {
            Debug.WriteLine("[FCM] ❌ Cannot update token - auth failed");
            return;
        }

        await UpdateTokenViaRestAsync(teamId, userId, token, authToken);
    }

    private async Task<bool> UpdateTokenViaRestAsync(string teamId, string userId, string token, string authToken)
    {
        try
        {
            const string projectId = "turf-timer";

            // First, get the current fcmTokens array
            var getUrl = $"https://firestore.googleapis.com/v1/projects/{projectId}/databases/(default)/documents/teams/{teamId}/members/{userId}";

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", authToken);

            var getResponse = await client.GetAsync(getUrl);

            var existingTokens = new List<string>();
            var memberExists = getResponse.IsSuccessStatusCode;

            if (memberExists)
            {
                var responseJson = await getResponse.Content.ReadAsStringAsync();
                var doc = JsonDocument.Parse(responseJson);

                if (doc.RootElement.TryGetProperty("fields", out var fields) &&
                    fields.TryGetProperty("fcmTokens", out var tokensField) &&
                    tokensField.TryGetProperty("arrayValue", out var arrayValue) &&
                    arrayValue.TryGetProperty("values", out var values))
                {
                    foreach (var item in values.EnumerateArray())
                    {
                        if (item.TryGetProperty("stringValue", out var tokenValue))
                        {
                            var existing = tokenValue.GetString() ?? "";
                            if (!string.IsNullOrEmpty(existing))
                                existingTokens.Add(existing);
                        }
                    }
                }
            }
            else
            {
                var errBody = await getResponse.Content.ReadAsStringAsync();
                Debug.WriteLine($"[FCM] Member GET {getResponse.StatusCode} — will merge-create. Body: {errBody.Substring(0, Math.Min(180, errBody.Length))}");
            }

            if (existingTokens.Contains(token))
            {
                Debug.WriteLine($"[FCM] ✅ Token already registered for {userId[..Math.Min(8, userId.Length)]}… ({existingTokens.Count} device(s))");
                return true;
            }

            existingTokens.Add(token);
            Debug.WriteLine($"[FCM] ➕ Adding device token for {userId[..Math.Min(8, userId.Length)]}… Total devices: {existingTokens.Count}");

            // PATCH with updateMask — merge-safe; creates fields if missing.
            var patchUrl =
                $"https://firestore.googleapis.com/v1/projects/{projectId}/databases/(default)/documents/teams/{teamId}/members/{userId}" +
                "?updateMask.fieldPaths=fcmTokens&updateMask.fieldPaths=tokenUpdatedAt";

            // Firestore REST: if doc doesn't exist, PATCH with updateMask can fail —
            // use PATCH with currentDocument.exists=false alternative via commit, or PATCH without mask via update.
            // Prefer: PATCH with allowMissing query param (Firestore REST v1 supports allowMissing=true).
            patchUrl += "&allowMissing=true";

            var tokenValues = existingTokens.Select(t => new { stringValue = t }).ToArray();
            var payload = new
            {
                fields = new
                {
                    fcmTokens = new
                    {
                        arrayValue = new
                        {
                            values = tokenValues
                        }
                    },
                    tokenUpdatedAt = new { timestampValue = DateTime.UtcNow.ToString("o") }
                }
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var patchClient = new HttpClient();
            patchClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", authToken);

            var response = await patchClient.PatchAsync(patchUrl, content);

            if (response.IsSuccessStatusCode)
            {
                Debug.WriteLine("[FCM] ✅ Token saved to Firestore successfully (REST + chat auth)");
                return true;
            }

            var error = await response.Content.ReadAsStringAsync();
            Debug.WriteLine($"[FCM] ❌ Firestore update failed: {response.StatusCode} - {error}");
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FCM] ❌ REST update error: {ex.Message}");
            return false;
        }
    }

    public async Task RemoveTokenFromFirestoreAsync()
    {
        try
        {
            var teamId = Preferences.Get("team_id", "");
            var userId = Preferences.Get("user_id", "");
            var token = _currentToken;

            if (string.IsNullOrEmpty(teamId) || string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(token))
                return;

            if (teamId.StartsWith("local_"))
                return;

            Debug.WriteLine($"[FCM] 🗑️ Removing FCM token from Firestore (REST, same path as Android)");

            var projectId = "turf-timer";

            // Get Firebase auth token
            var authToken = await GetAuthTokenAsync();
            if (string.IsNullOrEmpty(authToken))
            {
                Debug.WriteLine("[FCM] ❌ Cannot remove token - auth failed");
                return;
            }

            // First, get the current fcmTokens array
            var getUrl = $"https://firestore.googleapis.com/v1/projects/{projectId}/databases/(default)/documents/teams/{teamId}/members/{userId}";

            using var client = new HttpClient();

            // Add auth header for GET
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", authToken);

            var getResponse = await client.GetAsync(getUrl);

            var existingTokens = new List<string>();

            if (getResponse.IsSuccessStatusCode)
            {
                var responseJson = await getResponse.Content.ReadAsStringAsync();
                var doc = System.Text.Json.JsonDocument.Parse(responseJson);

                // Check if fcmTokens array exists
                if (doc.RootElement.TryGetProperty("fields", out var fields) &&
                    fields.TryGetProperty("fcmTokens", out var tokensField) &&
                    tokensField.TryGetProperty("arrayValue", out var arrayValue) &&
                    arrayValue.TryGetProperty("values", out var values))
                {
                    foreach (var item in values.EnumerateArray())
                    {
                        if (item.TryGetProperty("stringValue", out var tokenValue))
                        {
                            var existingToken = tokenValue.GetString() ?? "";
                            if (existingToken != token) // Keep all except current token
                            {
                                existingTokens.Add(existingToken);
                            }
                        }
                    }
                }
            }

            Debug.WriteLine($"[FCM] 🗑️ Removed this device. Remaining devices: {existingTokens.Count}");

            // Update with the remaining tokens
            var patchUrl = $"https://firestore.googleapis.com/v1/projects/{projectId}/databases/(default)/documents/teams/{teamId}/members/{userId}?updateMask.fieldPaths=fcmTokens";

            var tokenValues = existingTokens.Select(t => new { stringValue = t }).ToArray();

            var payload = new
            {
                fields = new
                {
                    fcmTokens = new 
                    { 
                        arrayValue = new 
                        { 
                            values = tokenValues 
                        } 
                    }
                }
            };

            var json = System.Text.Json.JsonSerializer.Serialize(payload);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            // Create new client for PATCH with auth
            using var patchClient = new HttpClient();
            patchClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", authToken);

            var response = await patchClient.PatchAsync(patchUrl, content);

            if (response.IsSuccessStatusCode)
            {
                Debug.WriteLine("[FCM] ✅ Token removed from Firestore");
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync();
                Debug.WriteLine($"[FCM] ❌ Token removal failed: {response.StatusCode} - {error}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FCM] ❌ Error removing token: {ex.GetType().FullName}: {ex.Message}");
            Debug.WriteLine($"[FCM] Stack: {ex.StackTrace}");
        }
    }
}
