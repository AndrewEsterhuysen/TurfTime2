namespace TurfTime2.Models;

/// <summary>
/// Match-day location and schedule (Details → Location).
/// Store-agnostic; cloud wire format lives in <c>MatchScheduleService</c>.
/// </summary>
public sealed class MatchSchedule
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string TeamId { get; set; } = string.Empty;

    /// <summary>Local wall date: yyyy-MM-dd.</summary>
    public string MatchDate { get; set; } = string.Empty;

    /// <summary>Kickoff time (TimeSpan.ToString() / HH:mm:ss).</summary>
    public string MatchTime { get; set; } = string.Empty;

    /// <summary>Arrive-at-ground time (warm-up). Prefer this for “leave by” math.</summary>
    public string ArriveTime { get; set; } = string.Empty;

    public string LocationName { get; set; } = string.Empty;
    public string Latitude { get; set; } = string.Empty;
    public string Longitude { get; set; } = string.Empty;
    public string MapsLink { get; set; } = string.Empty;

    public DateTimeOffset LastModifiedUtc { get; set; }

    public string? UpdatedByUid { get; set; }
    public string? UpdatedByDisplayName { get; set; }

    /// <summary>True when we applied cloud data this session (vs local-only cache).</summary>
    public bool FromCloud { get; set; }

    /// <summary>True when last apply was offline/local cache (could not refresh).</summary>
    public bool IsOfflineCache { get; set; }
}

/// <summary>Human-facing validity of a schedule for display and future reminders.</summary>
public enum MatchScheduleStatus
{
    NotSet,
    Incomplete,
    Upcoming,
    Past
}
