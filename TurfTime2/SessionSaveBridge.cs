using System.Text;
using System.Text.Json;
using System.Net.Http.Headers;

namespace TurfTime2;

public class SessionSaveBridge
{
    private static readonly HttpClient httpClient = new HttpClient();
    private const string FIRESTORE_BASE_URL = "https://firestore.googleapis.com/v1/projects/turftime-6a97b/databases/(default)/documents";
    private const string FirebaseApiKey = "AIzaSyDAKivCFX5kYYZ6SkAQluBNdR92I320glk";
    private static string? _firebaseIdToken;

    private static async Task<string?> GetAuthTokenAsync()
    {
        if (!string.IsNullOrEmpty(_firebaseIdToken))
            return _firebaseIdToken;

        try
        {
            var url = $"https://identitytoolkit.googleapis.com/v1/accounts:signUp?key={FirebaseApiKey}";
            var body = JsonSerializer.Serialize(new { returnSecureToken = true });
            var content = new StringContent(body, Encoding.UTF8, "application/json");
            var response = await httpClient.PostAsync(url, content);
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);
            _firebaseIdToken = doc.RootElement.GetProperty("idToken").GetString();
            return _firebaseIdToken;
        }
        catch
        {
            return null;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("android")]
    [System.Runtime.Versioning.SupportedOSPlatform("ios")]
    [System.Runtime.Versioning.SupportedOSPlatform("maccatalyst")]
    public static async void SaveSessionToFirestore(string jsonData)
    {
        try
        {
            System.Diagnostics.Debug.WriteLine($"[SessionSaveBridge] ==========================================");
            System.Diagnostics.Debug.WriteLine($"[SessionSaveBridge] 🔵 Received request to save session");
            System.Diagnostics.Debug.WriteLine($"[SessionSaveBridge] 🔵 JSON data length: {jsonData?.Length ?? 0}");

            var data = JsonDocument.Parse(jsonData);
            var root = data.RootElement;

            if (!root.TryGetProperty("teamId", out var teamIdElement))
            {
                System.Diagnostics.Debug.WriteLine("[SessionSaveBridge] ❌ ERROR: No teamId in request");
                System.Diagnostics.Debug.WriteLine($"[SessionSaveBridge] ❌ Available properties: {string.Join(", ", root.EnumerateObject().Select(p => p.Name))}");
                return;
            }

            if (!root.TryGetProperty("sessionData", out var sessionDataElement))
            {
                System.Diagnostics.Debug.WriteLine("[SessionSaveBridge] ❌ ERROR: No sessionData in request");
                System.Diagnostics.Debug.WriteLine($"[SessionSaveBridge] ❌ Available properties: {string.Join(", ", root.EnumerateObject().Select(p => p.Name))}");
                return;
            }

            string teamId = teamIdElement.GetString();
            string sessionJson = sessionDataElement.GetString();

            System.Diagnostics.Debug.WriteLine($"[SessionSaveBridge] ✅ Team ID: {teamId}");
            System.Diagnostics.Debug.WriteLine($"[SessionSaveBridge] ✅ Session Data Length: {sessionJson?.Length ?? 0}");

            // Parse session data
            var sessionData = JsonDocument.Parse(sessionJson);
            var session = sessionData.RootElement;

            if (!session.TryGetProperty("sessionId", out var sessionId))
            {
                System.Diagnostics.Debug.WriteLine("[SessionSaveBridge] ❌ ERROR: No sessionId in session data");
                System.Diagnostics.Debug.WriteLine($"[SessionSaveBridge] ❌ Session properties: {string.Join(", ", session.EnumerateObject().Select(p => p.Name))}");
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[SessionSaveBridge] ✅ Session ID: {sessionId.GetString()}");

            // Get Firebase auth token
            System.Diagnostics.Debug.WriteLine($"[SessionSaveBridge] 🔑 Getting Firebase auth token...");
            var authToken = await GetAuthTokenAsync();
            if (string.IsNullOrEmpty(authToken))
            {
                System.Diagnostics.Debug.WriteLine("[SessionSaveBridge] ❌ ERROR: Could not get auth token");
                return;
            }
            System.Diagnostics.Debug.WriteLine($"[SessionSaveBridge] ✅ Auth token obtained");

            // Convert session data to Firestore format
            System.Diagnostics.Debug.WriteLine($"[SessionSaveBridge] 🔄 Converting to Firestore format...");
            var firestoreDoc = new Dictionary<string, object>
            {
                ["fields"] = ConvertSessionToFirestoreFields(session)
            };

            var firestoreJson = JsonSerializer.Serialize(firestoreDoc);
            System.Diagnostics.Debug.WriteLine($"[SessionSaveBridge] ✅ Firestore JSON Length: {firestoreJson.Length}");
            System.Diagnostics.Debug.WriteLine($"[SessionSaveBridge] 📄 Firestore JSON preview: {firestoreJson.Substring(0, Math.Min(200, firestoreJson.Length))}...");

            // Save to Firestore: teams/{teamId}/sessions/{sessionId}
            string documentPath = $"{FIRESTORE_BASE_URL}/teams/{teamId}/sessions/{sessionId.GetString()}";
            System.Diagnostics.Debug.WriteLine($"[SessionSaveBridge] 📤 Document Path: {documentPath}");

            var request = new HttpRequestMessage(HttpMethod.Patch, documentPath)
            {
                Content = new StringContent(firestoreJson, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authToken);

            var response = await httpClient.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                System.Diagnostics.Debug.WriteLine($"[SessionSaveBridge] ✅ Session saved successfully");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[SessionSaveBridge] ❌ Save failed: {response.StatusCode}");
                System.Diagnostics.Debug.WriteLine($"[SessionSaveBridge] Response: {responseBody}");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SessionSaveBridge] ERROR: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[SessionSaveBridge] Stack: {ex.StackTrace}");
        }
    }

    private static Dictionary<string, object> ConvertSessionToFirestoreFields(JsonElement session)
    {
        var fields = new Dictionary<string, object>();

        // Session metadata
        if (session.TryGetProperty("sessionId", out var sessionId))
            fields["sessionId"] = new { stringValue = sessionId.GetString() };

        if (session.TryGetProperty("startTime", out var startTime))
            fields["startTime"] = new { stringValue = startTime.GetString() };

        if (session.TryGetProperty("endTime", out var endTime) && endTime.ValueKind != JsonValueKind.Null)
            fields["endTime"] = new { stringValue = endTime.GetString() };

        if (session.TryGetProperty("location", out var location) && location.ValueKind != JsonValueKind.Null)
            fields["location"] = new { stringValue = location.GetString() };

        if (session.TryGetProperty("matchDuration", out var matchDuration))
            fields["matchDuration"] = new { integerValue = matchDuration.GetInt32().ToString() };

        if (session.TryGetProperty("rotationInterval", out var rotationInterval))
            fields["rotationInterval"] = new { integerValue = rotationInterval.GetInt32().ToString() };

        if (session.TryGetProperty("scoreUs", out var scoreUs))
            fields["scoreUs"] = new { integerValue = scoreUs.GetInt32().ToString() };

        if (session.TryGetProperty("scoreThem", out var scoreThem))
            fields["scoreThem"] = new { integerValue = scoreThem.GetInt32().ToString() };

        if (session.TryGetProperty("teamName", out var teamName) && teamName.ValueKind != JsonValueKind.Null)
            fields["teamName"] = new { stringValue = teamName.GetString() };

        // Logs array
        if (session.TryGetProperty("logs", out var logs) && logs.ValueKind == JsonValueKind.Array)
        {
            var logsArray = new List<object>();
            foreach (var log in logs.EnumerateArray())
            {
                logsArray.Add(new
                {
                    mapValue = new
                    {
                        fields = ConvertLogToFirestoreFields(log)
                    }
                });
            }
            fields["logs"] = new { arrayValue = new { values = logsArray } };
        }

        // Summary
        if (session.TryGetProperty("summary", out var summary) && summary.ValueKind != JsonValueKind.Null)
        {
            fields["summary"] = new
            {
                mapValue = new
                {
                    fields = ConvertSummaryToFirestoreFields(summary)
                }
            };
        }

        return fields;
    }

    private static Dictionary<string, object> ConvertLogToFirestoreFields(JsonElement log)
    {
        var fields = new Dictionary<string, object>();

        if (log.TryGetProperty("id", out var id))
            fields["id"] = new { stringValue = id.GetString() };

        if (log.TryGetProperty("timestamp", out var timestamp))
            fields["timestamp"] = new { stringValue = timestamp.GetString() };

        if (log.TryGetProperty("eventType", out var eventType))
            fields["eventType"] = new { stringValue = eventType.GetString() };

        if (log.TryGetProperty("description", out var description))
            fields["description"] = new { stringValue = description.GetString() };

        if (log.TryGetProperty("playerName", out var playerName) && playerName.ValueKind != JsonValueKind.Null)
            fields["playerName"] = new { stringValue = playerName.GetString() };

        return fields;
    }

    private static Dictionary<string, object> ConvertSummaryToFirestoreFields(JsonElement summary)
    {
        var fields = new Dictionary<string, object>();

        if (summary.TryGetProperty("totalRotations", out var totalRotations))
            fields["totalRotations"] = new { integerValue = totalRotations.GetInt32().ToString() };

        if (summary.TryGetProperty("duration", out var duration))
            fields["duration"] = new { integerValue = duration.GetInt32().ToString() };

        if (summary.TryGetProperty("playerStats", out var playerStats) && playerStats.ValueKind == JsonValueKind.Array)
        {
            var statsArray = new List<object>();
            foreach (var stat in playerStats.EnumerateArray())
            {
                statsArray.Add(new
                {
                    mapValue = new
                    {
                        fields = ConvertPlayerStatToFirestoreFields(stat)
                    }
                });
            }
            fields["playerStats"] = new { arrayValue = new { values = statsArray } };
        }

        return fields;
    }

    private static Dictionary<string, object> ConvertPlayerStatToFirestoreFields(JsonElement stat)
    {
        var fields = new Dictionary<string, object>();

        if (stat.TryGetProperty("playerName", out var playerName))
            fields["playerName"] = new { stringValue = playerName.GetString() };

        if (stat.TryGetProperty("timeOnField", out var timeOnField))
            fields["timeOnField"] = new { integerValue = timeOnField.GetInt32().ToString() };

        if (stat.TryGetProperty("timeAsBench", out var timeAsBench))
            fields["timeAsBench"] = new { integerValue = timeAsBench.GetInt32().ToString() };

        if (stat.TryGetProperty("timeAsGoalie", out var timeAsGoalie))
            fields["timeAsGoalie"] = new { integerValue = timeAsGoalie.GetInt32().ToString() };

        if (stat.TryGetProperty("rotationsIn", out var rotationsIn))
            fields["rotationsIn"] = new { integerValue = rotationsIn.GetInt32().ToString() };

        if (stat.TryGetProperty("rotationsOut", out var rotationsOut))
            fields["rotationsOut"] = new { integerValue = rotationsOut.GetInt32().ToString() };

        return fields;
    }
}
