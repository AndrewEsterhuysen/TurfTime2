namespace TurfTime2.Models;

/// <summary>
/// Snapshot of the full roster and timer state persisted to local storage and Firestore.
/// Version-stamped for forward-compatibility.
/// </summary>
public sealed class RosterSnapshot
{
    /// <summary>Schema version. v3 adds per-player <see cref="PlayerSnapshot.FieldCell"/>.</summary>
    public int Version { get; set; } = 3;
    public DateTimeOffset LastModifiedUtc { get; set; } = DateTimeOffset.UtcNow;
    public int MatchDurationSeconds { get; set; } = 90 * 60;
    public int HalfDurationSeconds { get; set; }
    public int MatchRemainingSeconds { get; set; } = 90 * 60;
    public string CurrentHalf { get; set; } = "setup";
    public bool TimerRunning { get; set; }
    public int CountdownPresetSeconds { get; set; } = 2 * 60;
    /// <summary>Rotation countdown remaining (seconds). Shared so view-only clients tick locally after Start/Pause/Reset signals.</summary>
    public int CountdownRemainingSeconds { get; set; } = 2 * 60;
    public int ViewMode { get; set; }
    public int TeamAScore { get; set; }
    public int TeamBScore { get; set; }

    /// <summary>How many field/bench pairs rotate each cycle (admin-controlled).</summary>
    public int RotationCount { get; set; } = 1;

    /// <summary>
    /// Next field players to come OFF, as <see cref="PlayerSnapshot.SlotId"/> values (FIFO order).
    /// 0 = empty/deselected queue slot. View-only clients use this for blue “next” highlights.
    /// </summary>
    public List<int> NextFieldSlotIds { get; set; } = [];

    /// <summary>Next bench players to come ON (SlotIds). 0 = empty/deselected slot.</summary>
    public List<int> NextBenchSlotIds { get; set; } = [];

    /// <summary>Last field player rotated (SlotId); 0 = none. Used when queues are empty.</summary>
    public int LastFieldSlotId { get; set; }

    /// <summary>Last bench player rotated (SlotId); 0 = none.</summary>
    public int LastBenchSlotId { get; set; }

    // ── Single-controller lock (shared teams, multi-admin) ────────────────
    /// <summary>Firebase uid of the Admin currently allowed to control the match.</summary>
    public string ControllerUid { get; set; } = "";

    /// <summary>Chat display name of the controlling Admin (banner text).</summary>
    public string ControllerDisplayName { get; set; } = "";

    /// <summary>Firebase uid of an Admin requesting control (empty if none).</summary>
    public string ControlRequestUid { get; set; } = "";

    /// <summary>Display name of the Admin requesting control.</summary>
    public string ControlRequestDisplayName { get; set; } = "";

    /// <summary>Opaque id for the pending request (detect new requests / avoid duplicate popups).</summary>
    public string ControlRequestId { get; set; } = "";

    /// <summary>
    /// Last time the controlling Admin confirmed they are still online.
    /// Stale heartbeats trigger auto-release so another Admin can take over.
    /// </summary>
    public DateTimeOffset ControllerHeartbeatUtc { get; set; } = DateTimeOffset.MinValue;

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

    /// <summary>
    /// Outfield grid cell 1–16 when <see cref="Field"/> is true; 0/omitted = unset (older clients).
    /// </summary>
    public int FieldCell { get; set; }
}
