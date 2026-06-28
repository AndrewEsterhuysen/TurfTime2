namespace TurfTime2.Models;

/// <summary>
/// Snapshot of the full roster and timer state persisted to local storage and Firestore.
/// Version-stamped for forward-compatibility.
/// </summary>
public sealed class RosterSnapshot
{
    public int Version { get; set; } = 2;
    public DateTimeOffset LastModifiedUtc { get; set; } = DateTimeOffset.UtcNow;
    public int MatchDurationSeconds { get; set; } = 90 * 60;
    public int HalfDurationSeconds { get; set; }
    public int MatchRemainingSeconds { get; set; } = 90 * 60;
    public string CurrentHalf { get; set; } = "setup";
    public bool TimerRunning { get; set; }
    public int CountdownPresetSeconds { get; set; } = 2 * 60;
    public int ViewMode { get; set; }
    public int TeamAScore { get; set; }
    public int TeamBScore { get; set; }
    public List<PlayerSnapshot> Players { get; set; } = [];
}

public sealed class PlayerSnapshot
{
    public int SlotId { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool Field { get; set; }
    public bool Bench { get; set; }
    public bool Goalie { get; set; }
    public bool Inactive { get; set; }
    public int CounterSeconds { get; set; }
}
