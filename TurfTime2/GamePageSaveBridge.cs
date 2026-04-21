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

    public static async Task<string> SaveRosterToFirestore(string teamId, string rosterDataJson)
    {
        try
        {
            System.Diagnostics.Debug.WriteLine($"[FirebaseBridge] SaveRoster called for team: {teamId}");

            // Initialize HttpClient if needed
            if (_httpClient == null)
            {
                _httpClient = new HttpClient();
            }

            // Ensure Firebase authentication
            if (string.IsNullOrEmpty(_firebaseIdToken))
            {
                System.Diagnostics.Debug.WriteLine("[FirebaseBridge] Authenticating...");
                var url = $"https://identitytoolkit.googleapis.com/v1/accounts:signUp?key={FirebaseApiKey}";
                var body = System.Text.Json.JsonSerializer.Serialize(new { returnSecureToken = true });
                var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync(url, content);
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    System.Diagnostics.Debug.WriteLine($"[FirebaseBridge] Auth failed: {error}");
                    return "error:auth_failed";
                }
                var json = await response.Content.ReadAsStringAsync();
                var doc = System.Text.Json.JsonDocument.Parse(json);
                _firebaseIdToken = doc.RootElement.GetProperty("idToken").GetString();
                _firebaseUserId = doc.RootElement.GetProperty("localId").GetString();
                System.Diagnostics.Debug.WriteLine($"[FirebaseBridge] ✓ Authenticated");
            }

            // Parse JavaScript roster data
            using var rosterDoc = System.Text.Json.JsonDocument.Parse(rosterDataJson);
            var playersArray = rosterDoc.RootElement.GetProperty("players");

            // Convert players array to Firestore format
            var playersList = new List<object>();
            foreach (var player in playersArray.EnumerateArray())
            {
                playersList.Add(new { mapValue = new { fields = ConvertToFirestoreFields(player) } });
            }

            // Build Firestore document
            var firestoreDoc = new
            {
                fields = new
                {
                    version = new { integerValue = "2" },
                    lastModified = new { timestampValue = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") },
                    players = new { arrayValue = new { values = playersList } }
                }
            };

            var firestoreJson = System.Text.Json.JsonSerializer.Serialize(firestoreDoc);
            System.Diagnostics.Debug.WriteLine($"[FirebaseBridge] Firestore JSON: {firestoreJson.Substring(0, Math.Min(200, firestoreJson.Length))}...");

            // Save to Firestore
            var baseUrl = $"https://firestore.googleapis.com/v1/projects/{FirebaseProjectId}/databases/(default)/documents";
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _firebaseIdToken);

            var rosterUrl = $"{baseUrl}/teams/{teamId}/roster/data";
            var patchResponse = await _httpClient.PatchAsync(rosterUrl,
                new StringContent(firestoreJson, System.Text.Encoding.UTF8, "application/json"));

            if (patchResponse.IsSuccessStatusCode)
            {
                System.Diagnostics.Debug.WriteLine($"[FirebaseBridge] ✅ Saved roster for {teamId}");
                return "success";
            }
            else
            {
                var error = await patchResponse.Content.ReadAsStringAsync();
                System.Diagnostics.Debug.WriteLine($"[FirebaseBridge] ❌ Save failed: {error}");
                return $"error:save_failed - {error}";
            }
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
