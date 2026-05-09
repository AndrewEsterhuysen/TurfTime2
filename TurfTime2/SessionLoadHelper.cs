using TurfTime2.Models;
using System.Text.Json;
using System.Net.Http.Headers;

namespace TurfTime2;

public class SessionLoadHelper
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
            var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
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

    public static async Task<List<SessionSummary>> LoadSessionsForTeamAsync(string teamId)
    {
        try
        {
            System.Diagnostics.Debug.WriteLine($"[SessionLoadHelper] Loading sessions for team: {teamId}");

            // Get Firebase auth token
            var authToken = await GetAuthTokenAsync();
            if (string.IsNullOrEmpty(authToken))
            {
                System.Diagnostics.Debug.WriteLine("[SessionLoadHelper] ERROR: Could not get auth token");
                return new List<SessionSummary>();
            }

            // Query Firestore for sessions
            string collectionPath = $"{FIRESTORE_BASE_URL}/teams/{teamId}/sessions";
            System.Diagnostics.Debug.WriteLine($"[SessionLoadHelper] Collection Path: {collectionPath}");

            var request = new HttpRequestMessage(HttpMethod.Get, collectionPath);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authToken);

            var response = await httpClient.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                System.Diagnostics.Debug.WriteLine($"[SessionLoadHelper] âŒ Load failed: {response.StatusCode}");
                System.Diagnostics.Debug.WriteLine($"[SessionLoadHelper] Response: {responseBody}");
                return new List<SessionSummary>();
            }

            // Parse response
            var json = JsonDocument.Parse(responseBody);
            var root = json.RootElement;

            var sessions = new List<SessionSummary>();

            if (root.TryGetProperty("documents", out var documents))
            {
                foreach (var doc in documents.EnumerateArray())
                {
                    try
                    {
                        var session = ParseSessionDocument(doc);
                        if (session != null)
                        {
                            sessions.Add(session);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[SessionLoadHelper] Error parsing session: {ex.Message}");
                    }
                }
            }

            System.Diagnostics.Debug.WriteLine($"[SessionLoadHelper] âœ… Loaded {sessions.Count} sessions");
            return sessions.OrderByDescending(s => s.StartTime).ToList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SessionLoadHelper] ERROR: {ex.Message}");
            return new List<SessionSummary>();
        }
    }

    public static async Task<string> LoadSessionDataAsync(string teamId, string sessionId)
    {
        try
        {
            System.Diagnostics.Debug.WriteLine($"[SessionLoadHelper] Loading session data: {sessionId}");

            // Get Firebase auth token
            var authToken = await GetAuthTokenAsync();
            if (string.IsNullOrEmpty(authToken))
            {
                System.Diagnostics.Debug.WriteLine("[SessionLoadHelper] ERROR: Could not get auth token");
                return "";
            }

            // Get specific session document
            string documentPath = $"{FIRESTORE_BASE_URL}/teams/{teamId}/sessions/{sessionId}";
            System.Diagnostics.Debug.WriteLine($"[SessionLoadHelper] Document Path: {documentPath}");

            var request = new HttpRequestMessage(HttpMethod.Get, documentPath);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authToken);

            var response = await httpClient.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                System.Diagnostics.Debug.WriteLine($"[SessionLoadHelper] âŒ Load failed: {response.StatusCode}");
                return "";
            }

            // Parse and convert Firestore document to JSON
            var json = JsonDocument.Parse(responseBody);
            var sessionJson = ConvertFirestoreDocToJson(json.RootElement);

            System.Diagnostics.Debug.WriteLine($"[SessionLoadHelper] âœ… Session data loaded");
            return sessionJson;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SessionLoadHelper] ERROR: {ex.Message}");
            return "";
        }
    }

    private static SessionSummary? ParseSessionDocument(JsonElement doc)
    {
        if (!doc.TryGetProperty("fields", out var fields))
            return null;

        var summary = new SessionSummary();

        if (fields.TryGetProperty("sessionId", out var sessionId) &&
            sessionId.TryGetProperty("stringValue", out var sessionIdValue))
        {
            summary.SessionId = sessionIdValue.GetString() ?? "";
        }

        if (fields.TryGetProperty("startTime", out var startTime) &&
            startTime.TryGetProperty("stringValue", out var startTimeValue))
        {
            if (DateTime.TryParse(startTimeValue.GetString(), out var dt))
            {
                summary.StartTime = dt;
            }
        }

        if (fields.TryGetProperty("endTime", out var endTime) &&
            endTime.TryGetProperty("stringValue", out var endTimeValue))
        {
            if (DateTime.TryParse(endTimeValue.GetString(), out var dt))
            {
                summary.EndTime = dt;
            }
        }

        if (fields.TryGetProperty("location", out var location) &&
            location.TryGetProperty("stringValue", out var locationValue))
        {
            summary.Location = locationValue.GetString() ?? "";
        }

        if (fields.TryGetProperty("matchDuration", out var matchDuration) &&
            matchDuration.TryGetProperty("integerValue", out var matchDurationValue))
        {
            if (int.TryParse(matchDurationValue.GetString(), out var duration))
            {
                summary.MatchDuration = duration;
            }
        }

        return summary;
    }

    private static string ConvertFirestoreDocToJson(JsonElement doc)
    {
        if (!doc.TryGetProperty("fields", out var fields))
            return "{}";

        var session = new Dictionary<string, object?>();

        // Extract all fields
        if (fields.TryGetProperty("sessionId", out var sessionId) &&
            sessionId.TryGetProperty("stringValue", out var sessionIdValue))
        {
            session["sessionId"] = sessionIdValue.GetString();
        }

        if (fields.TryGetProperty("startTime", out var startTime) &&
            startTime.TryGetProperty("stringValue", out var startTimeValue))
        {
            session["startTime"] = startTimeValue.GetString();
        }

        if (fields.TryGetProperty("endTime", out var endTime) &&
            endTime.TryGetProperty("stringValue", out var endTimeValue))
        {
            session["endTime"] = endTimeValue.GetString();
        }

        if (fields.TryGetProperty("location", out var location) &&
            location.TryGetProperty("stringValue", out var locationValue))
        {
            session["location"] = locationValue.GetString();
        }

        if (fields.TryGetProperty("matchDuration", out var matchDuration) &&
            matchDuration.TryGetProperty("integerValue", out var matchDurationValue))
        {
            if (int.TryParse(matchDurationValue.GetString(), out var duration))
            {
                session["matchDuration"] = duration;
            }
        }

        if (fields.TryGetProperty("rotationInterval", out var rotationInterval) &&
            rotationInterval.TryGetProperty("integerValue", out var rotationIntervalValue))
        {
            if (int.TryParse(rotationIntervalValue.GetString(), out var interval))
            {
                session["rotationInterval"] = interval;
            }
        }

        // Parse logs array
        if (fields.TryGetProperty("logs", out var logs) &&
            logs.TryGetProperty("arrayValue", out var logsArray) &&
            logsArray.TryGetProperty("values", out var logsValues))
        {
            var logsList = new List<object>();
            foreach (var logItem in logsValues.EnumerateArray())
            {
                if (logItem.TryGetProperty("mapValue", out var mapValue) &&
                    mapValue.TryGetProperty("fields", out var logFields))
                {
                    logsList.Add(ConvertLogFields(logFields));
                }
            }
            session["logs"] = logsList;
        }

        // Parse summary
        if (fields.TryGetProperty("summary", out var summary) &&
            summary.TryGetProperty("mapValue", out var summaryMap) &&
            summaryMap.TryGetProperty("fields", out var summaryFields))
        {
            session["summary"] = ConvertSummaryFields(summaryFields);
        }

        return JsonSerializer.Serialize(session);
    }

    private static Dictionary<string, object?> ConvertLogFields(JsonElement fields)
    {
        var log = new Dictionary<string, object?>();

        if (fields.TryGetProperty("id", out var id) &&
            id.TryGetProperty("stringValue", out var idValue))
        {
            log["id"] = idValue.GetString();
        }

        if (fields.TryGetProperty("timestamp", out var timestamp) &&
            timestamp.TryGetProperty("stringValue", out var timestampValue))
        {
            log["timestamp"] = timestampValue.GetString();
        }

        if (fields.TryGetProperty("eventType", out var eventType) &&
            eventType.TryGetProperty("stringValue", out var eventTypeValue))
        {
            log["eventType"] = eventTypeValue.GetString();
        }

        if (fields.TryGetProperty("description", out var description) &&
            description.TryGetProperty("stringValue", out var descriptionValue))
        {
            log["description"] = descriptionValue.GetString();
        }

        if (fields.TryGetProperty("playerName", out var playerName) &&
            playerName.TryGetProperty("stringValue", out var playerNameValue))
        {
            log["playerName"] = playerNameValue.GetString();
        }

        return log;
    }

    private static Dictionary<string, object?> ConvertSummaryFields(JsonElement fields)
    {
        var summary = new Dictionary<string, object?>();

        if (fields.TryGetProperty("totalRotations", out var totalRotations) &&
            totalRotations.TryGetProperty("integerValue", out var totalRotationsValue))
        {
            if (int.TryParse(totalRotationsValue.GetString(), out var rotations))
            {
                summary["totalRotations"] = rotations;
            }
        }

        if (fields.TryGetProperty("duration", out var duration) &&
            duration.TryGetProperty("integerValue", out var durationValue))
        {
            if (int.TryParse(durationValue.GetString(), out var dur))
            {
                summary["duration"] = dur;
            }
        }

        // Parse playerStats array
        if (fields.TryGetProperty("playerStats", out var playerStats) &&
            playerStats.TryGetProperty("arrayValue", out var statsArray) &&
            statsArray.TryGetProperty("values", out var statsValues))
        {
            var statsList = new List<object>();
            foreach (var statItem in statsValues.EnumerateArray())
            {
                if (statItem.TryGetProperty("mapValue", out var mapValue) &&
                    mapValue.TryGetProperty("fields", out var statFields))
                {
                    statsList.Add(ConvertPlayerStatFields(statFields));
                }
            }
            summary["playerStats"] = statsList;
        }

        return summary;
    }

    private static Dictionary<string, object?> ConvertPlayerStatFields(JsonElement fields)
    {
        var stat = new Dictionary<string, object?>();

        if (fields.TryGetProperty("playerName", out var playerName) &&
            playerName.TryGetProperty("stringValue", out var playerNameValue))
        {
            stat["playerName"] = playerNameValue.GetString();
        }

        if (fields.TryGetProperty("timeOnField", out var timeOnField) &&
            timeOnField.TryGetProperty("integerValue", out var timeOnFieldValue))
        {
            if (int.TryParse(timeOnFieldValue.GetString(), out var time))
            {
                stat["timeOnField"] = time;
            }
        }

        if (fields.TryGetProperty("timeAsBench", out var timeAsBench) &&
            timeAsBench.TryGetProperty("integerValue", out var timeAsBenchValue))
        {
            if (int.TryParse(timeAsBenchValue.GetString(), out var time))
            {
                stat["timeAsBench"] = time;
            }
        }

        if (fields.TryGetProperty("timeAsGoalie", out var timeAsGoalie) &&
            timeAsGoalie.TryGetProperty("integerValue", out var timeAsGoalieValue))
        {
            if (int.TryParse(timeAsGoalieValue.GetString(), out var time))
            {
                stat["timeAsGoalie"] = time;
            }
        }

        if (fields.TryGetProperty("rotationsIn", out var rotationsIn) &&
            rotationsIn.TryGetProperty("integerValue", out var rotationsInValue))
        {
            if (int.TryParse(rotationsInValue.GetString(), out var rotations))
            {
                stat["rotationsIn"] = rotations;
            }
        }

        if (fields.TryGetProperty("rotationsOut", out var rotationsOut) &&
            rotationsOut.TryGetProperty("integerValue", out var rotationsOutValue))
        {
            if (int.TryParse(rotationsOutValue.GetString(), out var rotations))
            {
                stat["rotationsOut"] = rotations;
            }
        }

        return stat;
    }
}

