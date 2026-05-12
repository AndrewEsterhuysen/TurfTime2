using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TurfTime2.Models;

namespace TurfTime2.Services;

/// <summary>
/// Saves and loads game sessions via Firestore REST and local <see cref="Preferences"/>.
/// </summary>
public sealed class SessionStorageService : ISessionStorageService
{
    private const string FirebaseApiKey    = "AIzaSyDAKivCFX5kYYZ6SkAQluBNdR92I320glk";
    private const string FirebaseProjectId = "turf-timer";
    private const string FirestoreBase     =
        $"https://firestore.googleapis.com/v1/projects/{FirebaseProjectId}/databases/(default)/documents";

    private static readonly HttpClient _http = new();
    private static string? _idToken;

    // ── ISessionStorageService ────────────────────────────────────────────

    public async Task SaveSessionAsync(string teamId, GameSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(teamId)) return;
        if (teamId.StartsWith("local_", StringComparison.Ordinal)) return;

        var token = await GetAuthTokenAsync().ConfigureAwait(false);
        if (token is null) return;

        var url  = $"{FirestoreBase}/teams/{teamId}/sessions/{session.SessionId}";
        var body = JsonSerializer.Serialize(ToFirestoreDocument(session));

        var req = new HttpRequestMessage(HttpMethod.Patch, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await _http.SendAsync(req).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            System.Diagnostics.Debug.WriteLine($"[SessionStorage] Save failed: {err}");
        }
        else
        {
            System.Diagnostics.Debug.WriteLine($"[SessionStorage] Session {session.SessionId} saved");
        }
    }

    public async Task<IReadOnlyList<SessionSummary>> LoadSessionSummariesAsync(string teamId, bool isLocalTeam)
    {
        if (string.IsNullOrWhiteSpace(teamId)) return Array.Empty<SessionSummary>();

        if (isLocalTeam)
            return LoadLocalSummaries(teamId);

        return await LoadCloudSummariesAsync(teamId).ConfigureAwait(false);
    }

    public async Task<GameSession?> LoadSessionAsync(string teamId, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(teamId) || string.IsNullOrWhiteSpace(sessionId))
            return null;

        var token = await GetAuthTokenAsync().ConfigureAwait(false);
        if (token is null) return null;

        var url = $"{FirestoreBase}/teams/{teamId}/sessions/{sessionId}";
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await _http.SendAsync(req).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;

        var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        return ParseSessionDocument(json);
    }

    // ── Local sessions (Preferences) ──────────────────────────────────────

    private static IReadOnlyList<Models.SessionSummary> LoadLocalSummaries(string teamId)
    {
        try
        {
            var key = $"roster.sessionHistory.v1";
            var raw = Preferences.Get(key, string.Empty);
            if (string.IsNullOrEmpty(raw)) return Array.Empty<Models.SessionSummary>();

            var history = JsonSerializer.Deserialize<List<GameSession>>(raw) ?? [];
            return history
                .OrderByDescending(s => s.StartTime)
                .Select(s => new Models.SessionSummary
                {
                    SessionId     = s.SessionId,
                    StartTime     = s.StartTime.UtcDateTime,
                    EndTime       = s.EndTime?.UtcDateTime,
                    Location      = s.Location ?? string.Empty,
                    MatchDuration = s.MatchDurationSeconds / 60
                })
                .ToList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SessionStorage] Local load failed: {ex.Message}");
            return Array.Empty<Models.SessionSummary>();
        }
    }

    // ── Cloud sessions ────────────────────────────────────────────────────

    private async Task<IReadOnlyList<Models.SessionSummary>> LoadCloudSummariesAsync(string teamId)
    {
        var token = await GetAuthTokenAsync().ConfigureAwait(false);
        if (token is null) return [];

        var url = $"{FirestoreBase}/teams/{teamId}/sessions";
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await _http.SendAsync(req).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return [];

        var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        return ParseSummaries(json);
    }

    private static IReadOnlyList<Models.SessionSummary> ParseSummaries(string json)
    {
        var result = new List<Models.SessionSummary>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("documents", out var docs)) return result;

            foreach (var d in docs.EnumerateArray())
            {
                try
                {
                    var session = ParseSessionDocument(d.GetRawText());
                    if (session is null) continue;
                    result.Add(new Models.SessionSummary
                    {
                        SessionId     = session.SessionId,
                        StartTime     = session.StartTime.UtcDateTime,
                        EndTime       = session.EndTime?.UtcDateTime,
                        Location      = session.Location ?? string.Empty,
                        MatchDuration = session.MatchDurationSeconds / 60
                    });
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[SessionStorage] Parse error: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SessionStorage] ParseSummaries failed: {ex.Message}");
        }
        return result.OrderByDescending(s => s.StartTime).ToList();
    }

    // ── Warm-up ───────────────────────────────────────────────────────────

    /// <summary>Pre-warms the Firebase auth token on a background thread.</summary>
    public async Task WarmUpAsync()
    {
        try { await Task.Run(GetAuthTokenAsync).ConfigureAwait(false); }
        catch { /* warm-up is best-effort */ }
    }

    // ── Auth ──────────────────────────────────────────────────────────────

    private static async Task<string?> GetAuthTokenAsync()
    {
        if (!string.IsNullOrEmpty(_idToken)) return _idToken;

        var url  = $"https://identitytoolkit.googleapis.com/v1/accounts:signUp?key={FirebaseApiKey}";
        var body = JsonSerializer.Serialize(new { returnSecureToken = true });
        var resp = await _http.PostAsync(url,
            new StringContent(body, Encoding.UTF8, "application/json")).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
        _idToken = doc.RootElement.GetProperty("idToken").GetString();
        return _idToken;
    }

    // ── Firestore document serialisation ──────────────────────────────────

    private static object ToFirestoreDocument(GameSession session)
    {
        return new
        {
            fields = new Dictionary<string, object>
            {
                ["sessionId"]        = new { stringValue  = session.SessionId },
                ["startTime"]        = new { timestampValue = session.StartTime.ToString("o") },
                ["endTime"]          = new { timestampValue = (session.EndTime ?? DateTimeOffset.UtcNow).ToString("o") },
                ["matchDuration"]    = new { integerValue = session.MatchDurationSeconds.ToString() },
                ["totalRotations"]   = new { integerValue = (session.Summary?.TotalRotations ?? 0).ToString() },
                ["durationSeconds"]  = new { integerValue = (session.Summary?.DurationSeconds ?? 0).ToString() },
                ["sessionJson"]      = new { stringValue  = JsonSerializer.Serialize(session) }
            }
        };
    }

    private static GameSession? ParseSessionDocument(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var fields = doc.RootElement.GetProperty("fields");

            if (fields.TryGetProperty("sessionJson", out var sessionJsonElem))
            {
                var raw = sessionJsonElem.GetProperty("stringValue").GetString();
                if (!string.IsNullOrEmpty(raw))
                    return JsonSerializer.Deserialize<GameSession>(raw);
            }
            return null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SessionStorage] ParseDocument failed: {ex.Message}");
            return null;
        }
    }
}
