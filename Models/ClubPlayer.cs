namespace TurfTime2.Models;

/// <summary>
/// A player in a club's player list: <c>clubs/{clubId}/players/{playerId}</c>.
/// See docs/club-players-spec.md §4.1. Stats maps (<see cref="Totals"/>, <see cref="ByTeam"/>,
/// <see cref="Borrowed"/>, <see cref="BorrowedByTeam"/>) are written only by the Cloud Function;
/// clients read them and never send them.
/// </summary>
public sealed class ClubPlayer
{
    public const string StatusActive = "active";
    public const string StatusArchived = "archived";

    /// <summary>Stable id; also the Firestore document id.</summary>
    public string PlayerId { get; set; } = "";

    /// <summary>Display name, e.g. "Sam Lee".</summary>
    public string Name { get; set; } = "";

    /// <summary>Optional preferred name (decision 12). No jersey number.</summary>
    public string? PreferredName { get; set; }

    /// <summary>Search / duplicate key from <see cref="Name"/> and <see cref="PreferredName"/> (<see cref="Helpers.ClubPlayerNames.BuildNameKey"/>).</summary>
    public string NameKey { get; set; } = "";

    /// <summary>Full date of birth as ISO <c>YYYY-MM-DD</c> (decision 2). Null only for migrated players until filled in.</summary>
    public string? DateOfBirth { get; set; }

    /// <summary>Assignment history (fresh assignments each season; history kept).</summary>
    public List<ClubPlayerTeamEntry> Teams { get; set; } = [];

    /// <summary>Current teams only (entries in <see cref="Teams"/> with no RemovedAt).</summary>
    public List<string> TeamIds { get; set; } = [];

    /// <summary><see cref="StatusActive"/> or <see cref="StatusArchived"/>.</summary>
    public string Status { get; set; } = StatusActive;

    /// <summary>Non-borrowed totals across the player's own assignments. Read-only for clients.</summary>
    public ClubPlayerStats Totals { get; set; } = new();

    /// <summary>Per assigned team (non-borrowed). Read-only for clients.</summary>
    public Dictionary<string, ClubPlayerStats> ByTeam { get; set; } = new();

    /// <summary>One-match fill-ins only; never added to <see cref="Totals"/> or <see cref="ByTeam"/>. Read-only for clients.</summary>
    public ClubPlayerStats Borrowed { get; set; } = new();

    /// <summary>Borrowed appearances per team. Read-only for clients.</summary>
    public Dictionary<string, ClubPlayerStats> BorrowedByTeam { get; set; } = new();

    public DateTimeOffset? CreatedAt { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }

    public bool IsArchived => string.Equals(Status, StatusArchived, StringComparison.OrdinalIgnoreCase);

    /// <summary>"Sam Lee" or "Sam Lee (Sammy)".</summary>
    public string DisplayName =>
        string.IsNullOrWhiteSpace(PreferredName) ? Name : $"{Name} ({PreferredName})";
}

/// <summary>One team assignment in a player's history.</summary>
public sealed class ClubPlayerTeamEntry
{
    public string TeamId { get; set; } = "";
    public string TeamName { get; set; } = "";
    /// <summary>Season year, e.g. 2027.</summary>
    public int Season { get; set; }
    public DateTimeOffset? AssignedAt { get; set; }
    /// <summary>Set when the player leaves the team; the entry (and stats) stay.</summary>
    public DateTimeOffset? RemovedAt { get; set; }
}

/// <summary>v1 stats (decision 7): matches, minutes, goals, assists, POTM.</summary>
public sealed class ClubPlayerStats
{
    public int Matches { get; set; }
    public int Goals { get; set; }
    public int Assists { get; set; }
    public int Potm { get; set; }
    public int Minutes { get; set; }
}
