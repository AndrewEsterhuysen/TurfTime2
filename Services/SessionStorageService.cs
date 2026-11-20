using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Plugin.Firebase.Firestore;
using TurfTime2.Models;

namespace TurfTime2.Services;

/// <summary>
/// Saves and loads game sessions via Plugin.Firebase Firestore and local Preferences.
/// Full <see cref="GameSession"/> is stored in sessionJson.
/// </summary>
public sealed class SessionStorageService : ISessionStorageService
{
    /// <summary>
    /// v2: <see cref="PlayerStats"/> carries <c>SlotId</c> + <c>WasPresent</c> (squad snapshot
    /// for nominations). v1 sessions still load; nominations fall back to the current roster.
    /// </summary>
    public const int SessionSchemaVersion = 2;

    private const string FirebaseProjectId = "turf-timer";
    private const int RestPageSize = 100;
    private const int RestMaxPages = 10;
    private static readonly HttpClient RestHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly IFirebaseAuthService _auth;
    private readonly IFirebaseFirestore _db;

    private static readonly JsonSerializerOptions SessionJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public SessionStorageService(IFirebaseAuthService auth, IFirebaseFirestore db)
    {
        _auth = auth;
        _db = db;
    }

    /// <summary>Legacy no-op — auth is owned by <see cref="IFirebaseAuthService"/>.</summary>
    public static void SetAuthToken(string idToken, string userId)
    {
        System.Diagnostics.Debug.WriteLine("[SessionStorage] SetAuthToken ignored (SDK auth)");
    }

    public async Task<bool> SaveSessionAsync(string teamId, GameSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(teamId)) return false;
        if (IsLocalOnlyTeam(teamId)) return false;

        if (await _auth.EnsureSignedInAsync().ConfigureAwait(false) is null)
        {
            System.Diagnostics.Debug.WriteLine("[SessionStorage] Auth failed — session not saved to cloud");
            return false;
        }

        try
        {
            var endTime = session.EndTime ?? DateTimeOffset.UtcNow;
            var sessionJson = JsonSerializer.Serialize(session, SessionJsonOptions);
            var data = new Dictionary<object, object>
            {
                ["schemaVersion"] = SessionSchemaVersion,
                ["sessionId"] = session.SessionId,
                ["startTime"] = session.StartTime,
                ["endTime"] = endTime,
                ["matchDuration"] = session.MatchDurationSeconds,
                ["totalRotations"] = session.Summary?.TotalRotations ?? 0,
                ["durationSeconds"] = session.Summary?.DurationSeconds ?? 0,
                ["scoreUs"] = session.ScoreUs,
                ["scoreThem"] = session.ScoreThem,
                ["teamName"] = session.TeamName ?? string.Empty,
                ["location"] = session.Location ?? string.Empty,
                ["sessionJson"] = sessionJson
            };

            await _db.GetDocument($"teams/{teamId}/sessions/{session.SessionId}")
                .SetDataAsync(data, SetOptions.Merge())
                .ConfigureAwait(false);
            FirestoreUsageMeter.RecordSdk("Set", "Session");

            System.Diagnostics.Debug.WriteLine(
                $"[SessionStorage] Session {session.SessionId} saved (schema v{SessionSchemaVersion})");
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SessionStorage] Save failed: {ex.Message}");
            return false;
        }
    }

    public async Task<IReadOnlyList<SessionSummary>> LoadSessionSummariesAsync(string teamId, bool isLocalTeam)
    {
        if (string.IsNullOrWhiteSpace(teamId)) return Array.Empty<SessionSummary>();

        if (isLocalTeam || IsLocalOnlyTeam(teamId))
            return LoadLocalSummaries(teamId);

        return await LoadCloudSummariesAsync(teamId).ConfigureAwait(false);
    }

    public async Task<SessionListResult> LoadCompletedSessionsAsync(string teamId, string? teamName)
    {
        if (string.IsNullOrWhiteSpace(teamId)) return new SessionListResult();

        var local = LoadLocalTeamSessions(teamId, teamName);
        if (IsLocalOnlyTeam(teamId))
            return new SessionListResult { Sessions = SessionListMerger.MergeCompleted([], local) };

        var (cloud, cloudError) = await LoadCloudSummariesWithFallbackAsync(teamId).ConfigureAwait(false);
        var merged = SessionListMerger.MergeCompleted(cloud ?? [], local);
        Log($"Matches for {teamId}: cloud={(cloud is null ? "failed" : cloud.Count.ToString())}, " +
            $"local={local.Count}, completed={merged.Count}");
        return new SessionListResult
        {
            Sessions = merged,
            CloudFailed = cloud is null,
            CloudError = cloudError
        };
    }

    /// <summary>
    /// Cloud summaries: SDK query first; if it throws or yields nothing usable (Plugin.Firebase
    /// Dictionary reads can come back empty on device), list the collection over REST.
    /// Returns null when both fail.
    /// </summary>
    private async Task<(List<SessionSummary>? Items, string? Error)> LoadCloudSummariesWithFallbackAsync(string teamId)
    {
        string? error = null;
        List<SessionSummary>? sdkItems = null;
        var sdkDocCount = 0;

        if (await _auth.EnsureSignedInAsync().ConfigureAwait(false) is null)
            return (null, "Not signed in");

        try
        {
            var querySnap = await _db.GetCollection($"teams/{teamId}/sessions")
                .GetDocumentsAsync<Dictionary<string, object>>()
                .ConfigureAwait(false);
            FirestoreUsageMeter.RecordSdk("Query", "Session");

            sdkItems = [];
            foreach (var doc in querySnap?.Documents ?? [])
            {
                sdkDocCount++;
                var data = doc.Data;
                var parsed = ParseSessionData(data);
                var summary = SessionListMerger.FromSdkDocument(doc.Reference?.Id, data, parsed);
                if (summary is not null) sdkItems.Add(summary);
            }
            Log($"SDK sessions query: docs={sdkDocCount}, usable={sdkItems.Count}");
        }
        catch (Exception ex)
        {
            error = $"SDK: {ex.GetType().Name}: {ex.Message}";
            Log($"SDK sessions query failed: {error}");
        }

        // Good SDK result: every doc usable.
        if (sdkItems is { Count: > 0 } && sdkItems.Count == sdkDocCount)
            return (sdkItems, null);

        try
        {
            var restItems = await ListSummariesViaRestAsync(teamId).ConfigureAwait(false);
            Log($"REST sessions list: {restItems.Count}");
            // REST first so its fields win; the final merge de-duplicates by SessionId.
            return (sdkItems is { Count: > 0 } ? restItems.Concat(sdkItems).ToList() : restItems, null);
        }
        catch (Exception ex)
        {
            var restError = $"REST: {ex.GetType().Name}: {ex.Message}";
            Log($"REST sessions list failed: {restError}");
            error = error is null ? restError : $"{error}; {restError}";
            // SDK worked partly — use what it gave; otherwise the cloud list failed.
            return (sdkItems is { Count: > 0 } ? sdkItems : null, error);
        }
    }

    /// <summary>Firestore REST documents.list on teams/{teamId}/sessions (summary fields only, paged).</summary>
    private async Task<List<SessionSummary>> ListSummariesViaRestAsync(string teamId)
    {
        var token = await _auth.GetIdTokenAsync(forceRefresh: false).ConfigureAwait(false);
        if (string.IsNullOrEmpty(token))
            token = await _auth.GetIdTokenAsync(forceRefresh: true).ConfigureAwait(false);
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException("No Firebase id token");

        var baseUrl = $"https://firestore.googleapis.com/v1/projects/{FirebaseProjectId}/databases/(default)/documents/teams/"
                      + $"{Uri.EscapeDataString(teamId)}/sessions?pageSize={RestPageSize}"
                      + string.Concat(SessionListMerger.SummaryFieldPaths.Select(f => $"&mask.fieldPaths={f}"));

        var result = new List<SessionSummary>();
        string? pageToken = null;
        for (var page = 0; page < RestMaxPages; page++)
        {
            var url = pageToken is null ? baseUrl : $"{baseUrl}&pageToken={Uri.EscapeDataString(pageToken)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await RestHttp.SendAsync(req).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            FirestoreUsageMeter.RecordRestBody("GET", url, body);
            if (resp.StatusCode == HttpStatusCode.NotFound) break;
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"sessions list {(int)resp.StatusCode}: {body[..Math.Min(200, body.Length)]}", null, resp.StatusCode);

            var (items, next) = SessionListMerger.ParseRestListPage(body);
            result.AddRange(items);
            if (next is null) break;
            pageToken = next;
        }
        return result;
    }

    /// <summary>This team's games from the device's local history (roster.sessionHistory.v1).</summary>
    private static List<SessionSummary> LoadLocalTeamSessions(string teamId, string? teamName)
    {
        try
        {
            var raw = Preferences.Get("roster.sessionHistory.v1", string.Empty);
            if (string.IsNullOrEmpty(raw)) return [];
            var history = JsonSerializer.Deserialize<List<GameSession>>(raw, SessionJsonOptions) ?? [];
            return history
                .Where(s => SessionListMerger.BelongsToTeam(s, teamId, teamName))
                .Select(SessionListMerger.FromSession)
                .ToList();
        }
        catch (Exception ex)
        {
            Log($"Local history read failed: {ex.GetType().Name}: {ex.Message}");
            return [];
        }
    }

    private static void Log(string message)
    {
        var line = $"[SessionStorage] {message}";
        System.Diagnostics.Debug.WriteLine(line);
        Console.WriteLine(line);
    }

    public async Task<GameSession?> LoadSessionAsync(string teamId, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(teamId) || string.IsNullOrWhiteSpace(sessionId))
            return null;

        if (IsLocalOnlyTeam(teamId))
            return LoadLocalSession(sessionId);

        if (await _auth.EnsureSignedInAsync().ConfigureAwait(false) is null)
            return null;

        try
        {
            var snap = await _db.GetDocument($"teams/{teamId}/sessions/{sessionId}")
                .GetDocumentSnapshotAsync<Dictionary<string, object>>()
                .ConfigureAwait(false);
            FirestoreUsageMeter.RecordSdk("Get", "Session");
            return ParseSessionData(snap?.Data);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SessionStorage] LoadSession: {ex.Message}");
            return null;
        }
    }

    public async Task WarmUpAsync()
    {
        try { await _auth.EnsureSignedInAsync().ConfigureAwait(false); }
        catch { /* best-effort */ }
    }

    private static bool IsLocalOnlyTeam(string teamId) =>
        teamId.StartsWith("local_", StringComparison.Ordinal)
        || string.Equals(Preferences.Get("team_mode", string.Empty), "local", StringComparison.Ordinal);

    private static IReadOnlyList<SessionSummary> LoadLocalSummaries(string teamId)
    {
        try
        {
            var raw = Preferences.Get("roster.sessionHistory.v1", string.Empty);
            if (string.IsNullOrEmpty(raw)) return Array.Empty<SessionSummary>();

            var history = JsonSerializer.Deserialize<List<GameSession>>(raw, SessionJsonOptions) ?? [];
            return history
                .OrderByDescending(s => s.StartTime)
                .Select(s => new SessionSummary
                {
                    SessionId = s.SessionId,
                    StartTime = s.StartTime.UtcDateTime,
                    EndTime = s.EndTime?.UtcDateTime,
                    Location = s.Location ?? string.Empty,
                    MatchDuration = s.MatchDurationSeconds / 60
                })
                .ToList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SessionStorage] Local load failed: {ex.Message}");
            return Array.Empty<SessionSummary>();
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
        catch { return null; }
    }

    private async Task<IReadOnlyList<SessionSummary>> LoadCloudSummariesAsync(string teamId)
    {
        if (await _auth.EnsureSignedInAsync().ConfigureAwait(false) is null)
            return [];

        try
        {
            var querySnap = await _db.GetCollection($"teams/{teamId}/sessions")
                .GetDocumentsAsync<Dictionary<string, object>>()
                .ConfigureAwait(false);
            FirestoreUsageMeter.RecordSdk("Query", "Session");

            var result = new List<SessionSummary>();
            if (querySnap?.Documents == null)
                return result;

            foreach (var doc in querySnap.Documents)
            {
                var session = ParseSessionData(doc.Data);
                if (session is null) continue;
                result.Add(new SessionSummary
                {
                    SessionId = session.SessionId,
                    StartTime = session.StartTime.UtcDateTime,
                    EndTime = session.EndTime?.UtcDateTime,
                    Location = session.Location ?? string.Empty,
                    MatchDuration = session.MatchDurationSeconds / 60
                });
            }

            return result.OrderByDescending(s => s.StartTime).ToList();
        }
        catch (Exception ex)
        {
            Log($"LoadCloudSummaries failed: {ex.GetType().Name}: {ex.Message}");
            return [];
        }
    }

    internal static GameSession? ParseSessionData(IDictionary<string, object>? fields)
    {
        if (fields is null) return null;
        try
        {
            if (!fields.TryGetValue("sessionJson", out var rawObj) || rawObj is null)
                return null;
            var raw = rawObj.ToString();
            if (string.IsNullOrEmpty(raw)) return null;
            return JsonSerializer.Deserialize<GameSession>(raw, SessionJsonOptions);
        }
        catch (Exception ex)
        {
            Log($"ParseSessionData failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}

