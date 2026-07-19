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
    public const int SessionSchemaVersion = 1;

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

    public async Task SaveSessionAsync(string teamId, GameSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(teamId)) return;
        if (IsLocalOnlyTeam(teamId)) return;

        if (await _auth.EnsureSignedInAsync().ConfigureAwait(false) is null)
        {
            System.Diagnostics.Debug.WriteLine("[SessionStorage] Auth failed — session not saved to cloud");
            return;
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

            System.Diagnostics.Debug.WriteLine(
                $"[SessionStorage] Session {session.SessionId} saved (schema v{SessionSchemaVersion})");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SessionStorage] Save failed: {ex.Message}");
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

        if (await _auth.EnsureSignedInAsync().ConfigureAwait(false) is null)
            return null;

        try
        {
            var snap = await _db.GetDocument($"teams/{teamId}/sessions/{sessionId}")
                .GetDocumentSnapshotAsync<Dictionary<string, object>>()
                .ConfigureAwait(false);
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
            System.Diagnostics.Debug.WriteLine($"[SessionStorage] LoadCloudSummaries: {ex.Message}");
            return [];
        }
    }

    private static GameSession? ParseSessionData(IDictionary<string, object>? fields)
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
            System.Diagnostics.Debug.WriteLine($"[SessionStorage] ParseSessionData: {ex.Message}");
            return null;
        }
    }
}

