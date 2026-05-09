using System.Text.Json;
using TurfTime2.Models;

namespace TurfTime2.Services;

/// <summary>
/// Records game events and manages session lifecycle.
/// Sessions are persisted to <see cref="Preferences"/> (local) and optionally
/// forwarded to Firestore via <see cref="ISessionStorageService"/>.
/// </summary>
public sealed class GameLoggerService : IGameLoggerService
{
    private const string CurrentSessionKey = "roster.currentSession.v1";
    private const string SessionHistoryKey  = "roster.sessionHistory.v1";
    private const int    MaxHistorySessions = 20;

    private readonly ISessionStorageService _storage;

    public GameSession? CurrentSession { get; private set; }

    public GameLoggerService(ISessionStorageService storage)
    {
        _storage = storage;
        TryLoadCurrentSession();
    }

    // â”€â”€ Session lifecycle â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public void StartSession(int matchDurationSeconds, int rotationIntervalSeconds, string? location = null)
    {
        // Archive any unfinished session before starting a new one
        if (CurrentSession is not null)
            ArchiveSession(players: null);

        CurrentSession = new GameSession
        {
            MatchDurationSeconds      = matchDurationSeconds,
            RotationIntervalSeconds   = rotationIntervalSeconds,
            Location                  = location
        };

        Log(GameEventType.GameStarted,
            $"Match started - {matchDurationSeconds / 60} minute game",
            details: new Dictionary<string, object?>
            {
                ["matchDuration"]      = matchDurationSeconds,
                ["rotationInterval"]   = rotationIntervalSeconds
            });

        PersistCurrentSession();
    }

    public void Log(GameEventType eventType, string description,
                    string? playerName = null,
                    IReadOnlyDictionary<string, object?>? details = null)
    {
        if (CurrentSession is null)
        {
            System.Diagnostics.Debug.WriteLine("[GameLogger] No active session â€” event dropped");
            return;
        }

        var entry = new GameEvent(
            Id:          Guid.NewGuid().ToString(),
            Timestamp:   DateTimeOffset.UtcNow,
            EventType:   eventType,
            Description: description,
            PlayerName:  playerName,
            Details:     details ?? new Dictionary<string, object?>());

        CurrentSession.Events.Add(entry);
        PersistCurrentSession();
    }

    public void EndSession(IReadOnlyList<Player> players)
    {
        if (CurrentSession is null) return;

        CurrentSession.EndTime = DateTimeOffset.UtcNow;
        CurrentSession.Summary = CalculateSummary(CurrentSession, players);

        Log(GameEventType.GameEnded,
            $"Match ended",
            details: new Dictionary<string, object?>
            {
                ["duration"] = CurrentSession.Summary.DurationSeconds
            });

        ArchiveSession(players);
    }

    // â”€â”€ History â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public IReadOnlyList<GameSession> GetSessionHistory()
    {
        try
        {
            var raw = Preferences.Get(SessionHistoryKey, string.Empty);
            if (string.IsNullOrEmpty(raw)) return [];
            return JsonSerializer.Deserialize<List<GameSession>>(raw) ?? [];
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GameLogger] Error loading history: {ex.Message}");
            return [];
        }
    }

    public void ClearHistory()
    {
        Preferences.Remove(SessionHistoryKey);
    }

    // â”€â”€ Internals â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private void ArchiveSession(IReadOnlyList<Player>? players)
    {
        if (CurrentSession is null) return;

        try
        {
            var history = LoadRawHistory();
            history.Insert(0, CurrentSession);
            if (history.Count > MaxHistorySessions)
                history = history.Take(MaxHistorySessions).ToList();

            Preferences.Set(SessionHistoryKey, JsonSerializer.Serialize(history));

            // Persist to Firestore asynchronously (fire-and-forget with exception logging)
            var session  = CurrentSession;
            var teamId   = Preferences.Get("team_id", string.Empty);
            _ = SaveToCloudAsync(teamId, session);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GameLogger] Error archiving session: {ex.Message}");
        }
        finally
        {
            Preferences.Remove(CurrentSessionKey);
            CurrentSession = null;
        }
    }

    private async Task SaveToCloudAsync(string teamId, GameSession session)
    {
        try
        {
            if (string.IsNullOrEmpty(teamId) || teamId.StartsWith("local_", StringComparison.Ordinal))
                return;

            await _storage.SaveSessionAsync(teamId, session).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GameLogger] Cloud save failed: {ex.Message}");
        }
    }

    private void PersistCurrentSession()
    {
        if (CurrentSession is null) return;
        try
        {
            Preferences.Set(CurrentSessionKey, JsonSerializer.Serialize(CurrentSession));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GameLogger] Error persisting session: {ex.Message}");
        }
    }

    private void TryLoadCurrentSession()
    {
        try
        {
            var raw = Preferences.Get(CurrentSessionKey, string.Empty);
            if (string.IsNullOrEmpty(raw)) return;
            CurrentSession = JsonSerializer.Deserialize<GameSession>(raw);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GameLogger] Error loading current session: {ex.Message}");
        }
    }

    private List<GameSession> LoadRawHistory()
    {
        try
        {
            var raw = Preferences.Get(SessionHistoryKey, string.Empty);
            if (string.IsNullOrEmpty(raw)) return [];
            return JsonSerializer.Deserialize<List<GameSession>>(raw) ?? [];
        }
        catch
        {
            return [];
        }
    }

    // â”€â”€ Summary calculation â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private static GameSessionSummary CalculateSummary(GameSession session, IReadOnlyList<Player>? players)
    {
        var totalRotations = session.Events.Count(e => e.EventType == GameEventType.RotationExecuted);
        var durationSeconds = session.EndTime.HasValue
            ? (int)(session.EndTime.Value - session.StartTime).TotalSeconds
            : 0;

        var statsMap = new Dictionary<string, (int rotIn, int rotOut)>(StringComparer.Ordinal);

        foreach (var e in session.Events.Where(e => e.EventType == GameEventType.RotationExecuted))
        {
            if (e.Details.TryGetValue("playerIn",  out var pIn)  && pIn  is string inName)
                statsMap[inName]  = statsMap.TryGetValue(inName,  out var v1) ? (v1.rotIn + 1, v1.rotOut) : (1, 0);
            if (e.Details.TryGetValue("playerOut", out var pOut) && pOut is string outName)
                statsMap[outName] = statsMap.TryGetValue(outName, out var v2) ? (v2.rotIn, v2.rotOut + 1) : (0, 1);
        }

        var playerStats = (players ?? []).Select(p =>
        {
            statsMap.TryGetValue(p.Name, out var rots);
            var benchSeconds = p.Position != PlayerPosition.Inactive && durationSeconds > 0
                ? Math.Max(0, durationSeconds - p.FieldSeconds)
                : 0;
            return new Models.PlayerStats(
                PlayerName:     p.Name,
                FieldSeconds:   p.FieldSeconds,
                BenchSeconds:   benchSeconds,
                GoalieSeconds:  p.Position == PlayerPosition.Goalie ? p.FieldSeconds : 0,
                RotationsIn:    rots.rotIn,
                RotationsOut:   rots.rotOut);
        }).ToList();

        return new GameSessionSummary(totalRotations, durationSeconds, playerStats);
    }
}

