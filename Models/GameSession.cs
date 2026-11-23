namespace TurfTime2.Models;

/// <summary>Per-player statistics accumulated during a session.</summary>
/// <remarks>
/// Session schema v2 adds <see cref="SlotId"/> and <see cref="WasPresent"/> so the
/// session keeps a squad snapshot (used by player-of-the-match nominations).
/// Older sessions deserialize with <c>SlotId = 0</c> and <c>WasPresent = null</c>;
/// see <see cref="NominationSquad"/> for the fallback to the current roster.
/// </remarks>
public sealed record PlayerStats(
    string PlayerName,
    int FieldSeconds,
    int BenchSeconds,
    int GoalieSeconds,
    int RotationsIn,
    int RotationsOut,
    int SlotId = 0,
    bool? WasPresent = null);

/// <summary>Summary computed at the end of a game session (statistics/analytics).</summary>
public sealed record GameSessionSummary(
    int TotalRotations,
    int DurationSeconds,
    IReadOnlyList<PlayerStats> PlayerStats);

/// <summary>Full game session: events, players, and optional summary.</summary>
public sealed class GameSession
{
    public string SessionId { get; init; } = Guid.NewGuid().ToString();
    public DateTimeOffset StartTime { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndTime { get; set; }
    public string? Location { get; init; }
    public int MatchDurationSeconds { get; init; }
    public int RotationIntervalSeconds { get; init; }
    public List<GameEvent> Events { get; } = [];
    public GameSessionSummary? Summary { get; set; }
    public int ScoreUs { get; set; }
    public int ScoreThem { get; set; }
    public string? TeamName { get; set; }

    /// <summary>
    /// Team the game was played for (from 3.6.0). Lets local history be filtered by team;
    /// older sessions leave it null.
    /// </summary>
    public string? TeamId { get; set; }
}

/// <summary>Lightweight listing record used by ReportsPage and SessionLoadHelper.</summary>
public class SessionSummary
{
    public string SessionId { get; set; } = "";
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public string Location { get; set; } = "";
    public int MatchDuration { get; set; }
}
