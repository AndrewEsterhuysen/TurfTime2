using System.Net.Http;
using System.Net.Http.Headers;

namespace TurfTime2;

// Firebase save bridge for GamePage - allows JavaScript to save roster via C#
public static class FirebaseSaveBridge
{
    private const string FirebaseApiKey = "AIzaSyDAKivCFX5kYYZ6SkAQluBNdR92I320glk";
    private const string FirebaseProjectId = "turf-timer";
    private static HttpClient? _httpClient;
    private static string? _firebaseIdToken;
    private static string? _firebaseUserId;

    private static async Task<bool> EnsureAuthenticatedAsync()
    {
        _httpClient ??= new HttpClient();

        if (!string.IsNullOrEmpty(_firebaseIdToken))
            return true;

        System.Diagnostics.Debug.WriteLine("[FirebaseBridge] Authenticating...");
        var url = $"https://identitytoolkit.googleapis.com/v1/accounts:signUp?key={FirebaseApiKey}";
        var body = System.Text.Json.JsonSerializer.Serialize(new { returnSecureToken = true });
        var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync(url, content);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            System.Diagnostics.Debug.WriteLine($"[FirebaseBridge] Auth failed: {error}");
            return false;
        }
        var json = await response.Content.ReadAsStringAsync();
        var doc = System.Text.Json.JsonDocument.Parse(json);
        _firebaseIdToken = doc.RootElement.GetProperty("idToken").GetString();
        _firebaseUserId = doc.RootElement.GetProperty("localId").GetString();
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

            // Parse JavaScript roster data
            using var rosterDoc = System.Text.Json.JsonDocument.Parse(rosterDataJson);
            var root = rosterDoc.RootElement;
            var playersArray = root.GetProperty("players");

            // Convert players array to Firestore format
            var playersList = new List<object>();
            foreach (var player in playersArray.EnumerateArray())
            {
                playersList.Add(new { mapValue = new { fields = ConvertToFirestoreFields(player) } });
            }

            // Build Firestore document with ALL game state fields
            var firestoreDoc = new
            {
                fields = new Dictionary<string, object>
                {
                    ["version"] = new { integerValue = "2" },
                    ["lastModified"] = new { timestampValue = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") },
                    ["players"] = new { arrayValue = new { values = playersList } }
                }
            };

            // Add optional game state fields if present
            if (root.TryGetProperty("matchDurationSeconds", out var matchDuration))
                firestoreDoc.fields["matchDurationSeconds"] = new { integerValue = matchDuration.GetInt32().ToString() };

            if (root.TryGetProperty("halfDurationSeconds", out var halfDuration))
                firestoreDoc.fields["halfDurationSeconds"] = new { integerValue = halfDuration.GetInt32().ToString() };

            if (root.TryGetProperty("matchRemainingSeconds", out var matchRemaining))
                firestoreDoc.fields["matchRemainingSeconds"] = new { integerValue = matchRemaining.GetInt32().ToString() };

            if (root.TryGetProperty("currentHalf", out var currentHalf))
                firestoreDoc.fields["currentHalf"] = new { stringValue = currentHalf.GetString() };

            if (root.TryGetProperty("timerRunning", out var timerRunning))
                firestoreDoc.fields["timerRunning"] = new { booleanValue = timerRunning.GetBoolean() };

            if (root.TryGetProperty("countdownPreset", out var countdownPreset))
                firestoreDoc.fields["countdownPreset"] = new { integerValue = countdownPreset.GetInt32().ToString() };

            if (root.TryGetProperty("teamAScore", out var teamAScore))
                firestoreDoc.fields["teamAScore"] = new { integerValue = teamAScore.GetInt32().ToString() };

            if (root.TryGetProperty("teamBScore", out var teamBScore))
                firestoreDoc.fields["teamBScore"] = new { integerValue = teamBScore.GetInt32().ToString() };

            var firestoreJson = System.Text.Json.JsonSerializer.Serialize(firestoreDoc);
            System.Diagnostics.Debug.WriteLine($"[FirebaseBridge] Firestore JSON: {firestoreJson.Substring(0, Math.Min(200, firestoreJson.Length))}...");


            // Save to Firestore (retry once if token expired)
            var baseUrl = $"https://firestore.googleapis.com/v1/projects/{FirebaseProjectId}/databases/(default)/documents";
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

    private static Dictionary<string, object> ConvertToFirestoreFields(System.Text.Json.JsonElement element)
    {
        var fields = new Dictionary<string, object>();

        foreach (var property in element.EnumerateObject())
        {
            fields[property.Name] = ConvertToFirestoreValue(property.Value);
        }

        return fields;
    }

    private static object ConvertToFirestoreValue(System.Text.Json.JsonElement value)
    {
        return value.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => new { stringValue = value.GetString() },
            System.Text.Json.JsonValueKind.Number => value.TryGetInt32(out var intVal)
                ? new { integerValue = intVal.ToString() }
                : new { doubleValue = value.GetDouble() },
            System.Text.Json.JsonValueKind.True => new { booleanValue = true },
            System.Text.Json.JsonValueKind.False => new { booleanValue = false },
            System.Text.Json.JsonValueKind.Null => new { nullValue = (object?)null },
            System.Text.Json.JsonValueKind.Array => new
            {
                arrayValue = new
                {
                    values = value.EnumerateArray()
                        .Select(item => new { mapValue = new { fields = ConvertToFirestoreFields(item) } })
                        .ToArray()
                }
            },
            System.Text.Json.JsonValueKind.Object => new
            {
                mapValue = new { fields = ConvertToFirestoreFields(value) }
            },
            _ => new { stringValue = value.ToString() }
        };
    }
}
