using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using TurfTime2.Services;

namespace TurfTime2;

/// <summary>
/// Firebase save bridge for legacy JavaScript roster saves.
/// Field names match <see cref="CloudRosterService"/> / <see cref="Models.RosterSnapshot"/>.
/// Prefer native <see cref="ICloudRosterService"/> for new code.
/// </summary>
public static class FirebaseSaveBridge
{
    private const string FirebaseApiKey    = "AIzaSyDAKivCFX5kYYZ6SkAQluBNdR92I320glk";
    private const string FirebaseProjectId = "turf-timer";
    private static HttpClient? _httpClient;
    private static string? _firebaseIdToken;
    private static string? _firebaseUserId;

    /// <summary>
    /// Shares the already-authenticated token from TeamDetailsPage so the bridge
    /// does not create a second anonymous user with potentially different Firestore permissions.
    /// Also forwards the token to native cloud services.
    /// </summary>
    public static void SetAuthToken(string idToken, string userId)
    {
        _firebaseIdToken = idToken;
        _firebaseUserId  = userId;
        CloudRosterService.SetAuthToken(idToken, userId);
        SessionStorageService.SetAuthToken(idToken, userId);
        System.Diagnostics.Debug.WriteLine("[FirebaseBridge] Auth token received from TeamDetailsPage (forwarded to cloud services)");
    }

    private static async Task<bool> EnsureAuthenticatedAsync()
    {
        _httpClient ??= new HttpClient();

        if (!string.IsNullOrEmpty(_firebaseIdToken))
            return true;

        System.Diagnostics.Debug.WriteLine("[FirebaseBridge] Authenticating...");
        var url = $"https://identitytoolkit.googleapis.com/v1/accounts:signUp?key={FirebaseApiKey}";
        var body = JsonSerializer.Serialize(new { returnSecureToken = true });
        var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync(url, content);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            System.Diagnostics.Debug.WriteLine($"[FirebaseBridge] Auth failed: {error}");
            return false;
        }
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        _firebaseIdToken = doc.RootElement.GetProperty("idToken").GetString();
        _firebaseUserId  = doc.RootElement.GetProperty("localId").GetString();
        if (!string.IsNullOrEmpty(_firebaseIdToken) && !string.IsNullOrEmpty(_firebaseUserId))
        {
            CloudRosterService.SetAuthToken(_firebaseIdToken, _firebaseUserId);
            SessionStorageService.SetAuthToken(_firebaseIdToken, _firebaseUserId);
        }
        System.Diagnostics.Debug.WriteLine("[FirebaseBridge] ✓ Authenticated");
        return true;
    }

    public static async Task<string> SaveRosterToFirestore(string teamId, string rosterDataJson)
    {
        try
        {
            System.Diagnostics.Debug.WriteLine($"[FirebaseBridge] SaveRoster called for team: {teamId}");

            _httpClient ??= new HttpClient();

            if (!await EnsureAuthenticatedAsync())
                return "error:auth_failed";

            using var rosterDoc = JsonDocument.Parse(rosterDataJson);
            var root = rosterDoc.RootElement;

            if (!root.TryGetProperty("players", out var playersArray))
                return "error:missing_players";

            var playersList = new List<object>();
            foreach (var player in playersArray.EnumerateArray())
            {
                playersList.Add(new { mapValue = new { fields = ConvertToFirestoreFields(player) } });
            }

            var nowUtc = DateTime.UtcNow.ToString("o");
            var fields = new Dictionary<string, object>
            {
                ["version"]         = new { integerValue = "2" },
                ["lastModifiedUtc"] = new { timestampValue = nowUtc },
                ["lastModified"]    = new { timestampValue = nowUtc },
                ["players"]         = new { arrayValue = new { values = playersList } }
            };

            // Canonical names (CloudRosterService / RosterSnapshot)
            CopyIntField(root, fields, "matchDurationSeconds", "matchDurationSeconds");
            CopyIntField(root, fields, "halfDurationSeconds", "halfDurationSeconds");
            CopyIntField(root, fields, "matchRemainingSeconds", "matchRemainingSeconds");
            CopyStringField(root, fields, "currentHalf", "currentHalf");
            CopyBoolField(root, fields, "timerRunning", "timerRunning");
            CopyIntField(root, fields, "viewMode", "viewMode");
            CopyIntField(root, fields, "teamAScore", "teamAScore");
            CopyIntField(root, fields, "teamBScore", "teamBScore");

            // Countdown: accept either key from JS; write both for compatibility
            var countdown = TryGetInt(root, "countdownPresetSeconds")
                         ?? TryGetInt(root, "countdownPreset");
            if (countdown.HasValue)
            {
                fields["countdownPresetSeconds"] = new { integerValue = countdown.Value.ToString() };
                fields["countdownPreset"]        = new { integerValue = countdown.Value.ToString() };
            }

            var firestoreJson = JsonSerializer.Serialize(new { fields });
            System.Diagnostics.Debug.WriteLine($"[FirebaseBridge] Firestore JSON: {firestoreJson.Substring(0, Math.Min(200, firestoreJson.Length))}...");

            var baseUrl   = $"https://firestore.googleapis.com/v1/projects/{FirebaseProjectId}/databases/(default)/documents";
            var rosterUrl = $"{baseUrl}/teams/{teamId}/roster/data";

            for (int attempt = 0; attempt < 2; attempt++)
            {
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _firebaseIdToken);
                var patchResponse = await _httpClient.PatchAsync(rosterUrl,
                    new StringContent(firestoreJson, System.Text.Encoding.UTF8, "application/json"));

                if (patchResponse.IsSuccessStatusCode)
                {
                    System.Diagnostics.Debug.WriteLine($"[FirebaseBridge] ✅ Saved roster for {teamId}");
                    return "success";
                }

                if (patchResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized && attempt == 0)
                {
                    System.Diagnostics.Debug.WriteLine("[FirebaseBridge] Token expired, refreshing...");
                    _firebaseIdToken = null;
                    if (!await EnsureAuthenticatedAsync())
                        return "error:auth_failed";
                    continue;
                }

                var error = await patchResponse.Content.ReadAsStringAsync();
                System.Diagnostics.Debug.WriteLine($"[FirebaseBridge] ❌ Save failed: {error}");
                return $"error:save_failed - {error}";
            }

            return "error:save_failed";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FirebaseBridge] ❌ Exception: {ex.Message}");
            return $"error:{ex.Message}";
        }
    }

    private static void CopyIntField(JsonElement root, Dictionary<string, object> fields, string source, string dest)
    {
        var v = TryGetInt(root, source);
        if (v.HasValue)
            fields[dest] = new { integerValue = v.Value.ToString() };
    }

    private static void CopyStringField(JsonElement root, Dictionary<string, object> fields, string source, string dest)
    {
        if (root.TryGetProperty(source, out var el) && el.ValueKind == JsonValueKind.String)
            fields[dest] = new { stringValue = el.GetString() };
    }

    private static void CopyBoolField(JsonElement root, Dictionary<string, object> fields, string source, string dest)
    {
        if (root.TryGetProperty(source, out var el) && (el.ValueKind == JsonValueKind.True || el.ValueKind == JsonValueKind.False))
            fields[dest] = new { booleanValue = el.GetBoolean() };
    }

    private static int? TryGetInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n)) return n;
        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var p)) return p;
        return null;
    }

    private static Dictionary<string, object> ConvertToFirestoreFields(JsonElement element)
    {
        var fields = new Dictionary<string, object>();

        foreach (var property in element.EnumerateObject())
        {
            // Normalize legacy player keys if needed — leave names as-is for map fields
            fields[property.Name] = ConvertToFirestoreValue(property.Value);
        }

        return fields;
    }

    private static object ConvertToFirestoreValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => new { stringValue = value.GetString() },
            JsonValueKind.Number => value.TryGetInt32(out var intVal)
                ? new { integerValue = intVal.ToString() }
                : new { doubleValue = value.GetDouble() },
            JsonValueKind.True => new { booleanValue = true },
            JsonValueKind.False => new { booleanValue = false },
            JsonValueKind.Null => new { nullValue = (object?)null },
            JsonValueKind.Array => new
            {
                arrayValue = new
                {
                    values = value.EnumerateArray()
                        .Select(item => new { mapValue = new { fields = ConvertToFirestoreFields(item) } })
                        .ToArray()
                }
            },
            JsonValueKind.Object => new
            {
                mapValue = new { fields = ConvertToFirestoreFields(value) }
            },
            _ => new { stringValue = value.ToString() }
        };
    }
}
