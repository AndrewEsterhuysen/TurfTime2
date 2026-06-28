namespace TurfTime2.Models;

/// <summary>Per-player statistics accumulated during a session.</summary>
public sealed record PlayerStats(
    string PlayerName,
    int FieldSeconds,
    int BenchSeconds,
    int GoalieSeconds,
    int RotationsIn,
    int RotationsOut);

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
