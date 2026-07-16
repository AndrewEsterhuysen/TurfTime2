using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TurfTime2.Models;

namespace TurfTime2.Services;

/// <summary>
/// Saves and loads game sessions via Firestore REST and local <see cref="Preferences"/>.
/// Full <see cref="GameSession"/> (events, scorer/assist details, scores) is stored in sessionJson.
/// </summary>
public sealed class SessionStorageService : ISessionStorageService
{
    private const string FirebaseApiKey    = "AIzaSyDAKivCFX5kYYZ6SkAQluBNdR92I320glk";
    private const string FirebaseProjectId = "turf-timer";
    private const string FirestoreBase     =
        $"https://firestore.googleapis.com/v1/projects/{FirebaseProjectId}/databases/(default)/documents";

    /// <summary>Bump when sessionJson wire format changes in a breaking way.</summary>
    public const int SessionSchemaVersion = 1;

    private static readonly HttpClient _http = new();
    private static string? _idToken;
    private static string? _userId;

    private static readonly JsonSerializerOptions SessionJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    /// <summary>
    /// Shares an already-authenticated token so session writes use the same Firebase identity
    /// as team create/join and roster sync.
    /// </summary>
    public static void SetAuthToken(string idToken, string userId)
    {
        if (string.IsNullOrWhiteSpace(idToken)) return;
        _idToken = idToken;
        _userId  = userId;
        System.Diagnostics.Debug.WriteLine("[SessionStorage] Auth token received from host");
    }

    // ── ISessionStorageService ────────────────────────────────────────────

    public async Task SaveSessionAsync(string teamId, GameSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(teamId)) return;
        if (IsLocalOnlyTeam(teamId)) return;

        var token = await GetAuthTokenAsync().ConfigureAwait(false);
        if (token is null)
        {
            System.Diagnostics.Debug.WriteLine("[SessionStorage] Auth failed — session not saved to cloud");
            return;
        }

        var url  = $"{FirestoreBase}/teams/{teamId}/sessions/{session.SessionId}";
        var body = JsonSerializer.Serialize(ToFirestoreDocument(session));

        for (int attempt = 0; attempt < 2; attempt++)
        {
            var req = new HttpRequestMessage(HttpMethod.Patch, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var resp = await _http.SendAsync(req).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                System.Diagnostics.Debug.WriteLine($"[SessionStorage] Session {session.SessionId} saved (schema v{SessionSchemaVersion})");
                return;
            }

            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized && attempt == 0)
            {
                _idToken = null;
                token    = await GetAuthTokenAsync().ConfigureAwait(false);
                if (token is null) return;
                continue;
            }

            var err = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            System.Diagnostics.Debug.WriteLine($"[SessionStorage] Save failed: {err}");
            return;
        }
    }

    public async Task<IReadOnlyList<SessionSummary>> LoadSessionSummariesAsync(string teamId, bool isLocalTeam)
    {
        if (string.IsNullOrWhiteSpace(teamId)) return Array.Empty<SessionSummary>();

        if (isLocalTeam || IsLocalOnlyTeam(teamId))
            return LoadLocalSummaries(teamId);

        return await LoadCloudSummariesAsync(teamId).ConfigureAwait(false);
    }

    public async Task<GameSession?> LoadSessionAsync(string teamId, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(teamId) || string.IsNullOrWhiteSpace(sessionId))
            return null;

        if (IsLocalOnlyTeam(teamId))
            return LoadLocalSession(sessionId);

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

    private static bool IsLocalOnlyTeam(string teamId) =>
        teamId.StartsWith("local_", StringComparison.Ordinal)
        || string.Equals(Preferences.Get("team_mode", string.Empty), "local", StringComparison.Ordinal);

    private static IReadOnlyList<Models.SessionSummary> LoadLocalSummaries(string teamId)
    {
        try
        {
            var key = $"roster.sessionHistory.v1";
            var raw = Preferences.Get(key, string.Empty);
            if (string.IsNullOrEmpty(raw)) return Array.Empty<Models.SessionSummary>();

            var history = JsonSerializer.Deserialize<List<GameSession>>(raw, SessionJsonOptions) ?? [];
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

    private static GameSession? LoadLocalSession(string sessionId)
    {
        try
        {
            var raw = Preferences.Get("roster.sessionHistory.v1", string.Empty);
            if (string.IsNullOrEmpty(raw)) return null;
            var history = JsonSerializer.Deserialize<List<GameSession>>(raw, SessionJsonOptions) ?? [];
            return history.FirstOrDefault(s => s.SessionId == sessionId);
        }
        catch
        {
            return null;
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
        _userId  = doc.RootElement.TryGetProperty("localId", out var lid) ? lid.GetString() : null;
        return _idToken;
    }

    // ── Firestore document serialisation ──────────────────────────────────

    private static object ToFirestoreDocument(GameSession session)
    {
        // Full session (events with scorer/assist Details, scores, summary) in sessionJson.
        var sessionJson = JsonSerializer.Serialize(session, SessionJsonOptions);
        var endTime     = (session.EndTime ?? DateTimeOffset.UtcNow).ToString("o");

        return new
        {
            fields = new Dictionary<string, object>
            {
                ["schemaVersion"]   = new { integerValue = SessionSchemaVersion.ToString() },
                ["sessionId"]       = new { stringValue  = session.SessionId },
                ["startTime"]       = new { timestampValue = session.StartTime.ToString("o") },
                ["endTime"]         = new { timestampValue = endTime },
                ["matchDuration"]   = new { integerValue = session.MatchDurationSeconds.ToString() },
                ["totalRotations"]  = new { integerValue = (session.Summary?.TotalRotations ?? 0).ToString() },
                ["durationSeconds"] = new { integerValue = (session.Summary?.DurationSeconds ?? 0).ToString() },
                ["scoreUs"]         = new { integerValue = session.ScoreUs.ToString() },
                ["scoreThem"]       = new { integerValue = session.ScoreThem.ToString() },
                ["teamName"]        = new { stringValue  = session.TeamName ?? string.Empty },
                ["location"]        = new { stringValue  = session.Location ?? string.Empty },
                ["sessionJson"]     = new { stringValue  = sessionJson }
            }
        };
    }

    private static GameSession? ParseSessionDocument(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("fields", out var fields))
                return null;

            if (fields.TryGetProperty("sessionJson", out var sessionJsonElem)
                && sessionJsonElem.TryGetProperty("stringValue", out var rawEl))
            {
                var raw = rawEl.GetString();
                if (!string.IsNullOrEmpty(raw))
                {
                    var session = JsonSerializer.Deserialize<GameSession>(raw, SessionJsonOptions);
                    if (session is not null)
                        return session;
                }
            }

            // Fallback: rebuild a minimal session from top-level summary fields
            return null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SessionStorage] ParseDocument failed: {ex.Message}");
            return null;
        }
    }
}
