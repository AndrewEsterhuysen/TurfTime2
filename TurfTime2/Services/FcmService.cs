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

            // Check notification permission using MAUI Permissions
            var status = await Permissions.CheckStatusAsync<Permissions.PostNotifications>();

            if (status != PermissionStatus.Granted)
            {
                status = await Permissions.RequestAsync<Permissions.PostNotifications>();
            }

            if (status != PermissionStatus.Granted)
            {
                Debug.WriteLine("[FCM] ❌ Notification permission denied");
                return false;
            }

            Debug.WriteLine("[FCM] ✅ Notification permission granted");

            // Get FCM token
            _currentToken = await CrossFirebaseCloudMessaging.Current.GetTokenAsync();
            Debug.WriteLine($"[FCM] ✅ Token received: {_currentToken?.Substring(0, Math.Min(20, _currentToken.Length))}...");

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
            Debug.WriteLine($"[FCM] ❌ Initialization error: {ex.Message}");
            return false;
        }
    }

    private void OnTokenChanged(object? sender, EventArgs e)
    {
        // Token changed - get new token
        Task.Run(async () =>
        {
            try
            {
                _currentToken = await CrossFirebaseCloudMessaging.Current.GetTokenAsync();
                Debug.WriteLine($"[FCM] 🔄 Token refreshed: {_currentToken?.Substring(0, Math.Min(20, _currentToken?.Length ?? 0))}...");

                // Update token in Firestore
                await UpdateTokenInFirestoreAsync(_currentToken);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FCM] ❌ Token refresh error: {ex.Message}");
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

            var teamId = Preferences.Get("team_id", "");
            var userId = Preferences.Get("user_id", "");

            if (string.IsNullOrEmpty(teamId) || string.IsNullOrEmpty(userId))
            {
                Debug.WriteLine("[FCM] ⚠️ No team or user ID, skipping token update");
                return;
            }

            // Skip local teams
            if (teamId.StartsWith("local_"))
            {
                Debug.WriteLine("[FCM] ⚠️ Local team, skipping FCM token update");
                return;
            }

            Debug.WriteLine($"[FCM] 💾 Updating FCM token in Firestore for user: {userId}");

            // Use REST API to update token in Firestore (consistent with existing pattern)
            await UpdateTokenViaRestAsync(teamId, userId, token);

            Debug.WriteLine("[FCM] ✅ Token updated in Firestore");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FCM] ❌ Error updating token: {ex.Message}");
        }
    }

    private async Task UpdateTokenViaRestAsync(string teamId, string userId, string token)
    {
        try
        {
            var projectId = "turf-timer";

            // Get Firebase auth token
            var authToken = await GetAuthTokenAsync();
            if (string.IsNullOrEmpty(authToken))
            {
                Debug.WriteLine("[FCM] ❌ Cannot update token - auth failed");
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
                            existingTokens.Add(tokenValue.GetString() ?? "");
                        }
                    }
                }
            }

            // Add new token if not already present
            if (!existingTokens.Contains(token))
            {
                existingTokens.Add(token);
                Debug.WriteLine($"[FCM] ➕ Adding new device token. Total devices: {existingTokens.Count}");
            }
            else
            {
                Debug.WriteLine($"[FCM] ✅ Token already registered. Total devices: {existingTokens.Count}");
                return; // No update needed
            }

            // Update with the complete array
            var patchUrl = $"https://firestore.googleapis.com/v1/projects/{projectId}/databases/(default)/documents/teams/{teamId}/members/{userId}?updateMask.fieldPaths=fcmTokens&updateMask.fieldPaths=tokenUpdatedAt";

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

            var json = System.Text.Json.JsonSerializer.Serialize(payload);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            // Create new client for PATCH with auth
            using var patchClient = new HttpClient();
            patchClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", authToken);

            var response = await patchClient.PatchAsync(patchUrl, content);

            if (response.IsSuccessStatusCode)
            {
                Debug.WriteLine($"[FCM] ✅ Token saved to Firestore successfully");
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync();
                Debug.WriteLine($"[FCM] ❌ Firestore update failed: {response.StatusCode} - {error}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FCM] ❌ REST update error: {ex.Message}");
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

            Debug.WriteLine($"[FCM] 🗑️ Removing FCM token from Firestore");

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
            Debug.WriteLine($"[FCM] ❌ Error removing token: {ex.Message}");
        }
    }
}
