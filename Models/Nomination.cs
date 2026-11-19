namespace TurfTime2.Models;

/// <summary>
/// One member's player-of-the-match nomination for a completed session.
/// Document id in Firestore is <see cref="NominatorId"/> (the member's uid).
/// See docs/nominations-spec.md.
/// </summary>
public sealed class Nomination
{
    /// <summary>Firebase Auth uid of the team member who voted.</summary>
    public string NominatorId { get; set; } = "";

    /// <summary>
    /// Member display name at vote time. Stored for audit; not shown on the team tally.
    /// </summary>
    public string NominatorDisplayName { get; set; } = "";

    /// <summary>Roster slot of the nominated player on that session's squad.</summary>
    public int NomineeSlotId { get; set; }

    /// <summary>Player name denormalized at vote time.</summary>
    public string NomineeName { get; set; } = "";

    public DateTimeOffset Timestamp { get; set; }
}

/// <summary>One row in the live tally (counts only — no voter list).</summary>
public sealed class NominationTallyEntry
{
    public int NomineeSlotId { get; set; }
    public string NomineeName { get; set; } = "";
    public int Votes { get; set; }
}

/// <summary>Aggregated nominations for one session, including joint leaders.</summary>
public sealed class NominationTally
{
    public string TeamId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public bool IsOpen { get; set; }
    public DateTimeOffset? ClosesAt { get; set; }
    public int? MyNomineeSlotId { get; set; }
    public string? MyNomineeName { get; set; }
    public IReadOnlyList<NominationTallyEntry> Entries { get; set; } = [];
    public IReadOnlyList<NominationTallyEntry> Leaders { get; set; } = [];
}

public enum NominationSubmitStatus
{
    Ok,
    DisabledForLocalTeam,
    NotSignedIn,
    InvalidNominee,
    WindowClosed,
    SelfNominationBlocked,
    /// <summary>Scaffold stub — Firestore writes are not wired yet.</summary>
    NotWired,
    Failed
}

public sealed class NominationSubmitResult
{
    public NominationSubmitStatus Status { get; init; }
    public string? Message { get; init; }
    public bool Succeeded => Status == NominationSubmitStatus.Ok;

    public static NominationSubmitResult From(NominationSubmitStatus status, string? message = null)
        => new() { Status = status, Message = message };
}
