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

    /// <summary>Name from the most recent vote for this player (names can change).</summary>
    public string NomineeName { get; set; } = "";

    public int Votes { get; set; }

    /// <summary>True when this player has the most votes (ties: every joint leader).</summary>
    public bool IsLeader { get; set; }
}

/// <summary>
/// Aggregated nominations for one session, including joint leaders.
/// Never carries who voted for whom: only counts plus the caller's own pick.
/// </summary>
public sealed class NominationTally
{
    public string TeamId { get; set; } = "";
    public string SessionId { get; set; } = "";

    /// <summary>Voting is open: not closed by an Admin and within 24 hours of the session end.</summary>
    public bool IsOpen { get; set; }

    /// <summary>An Admin closed voting early (nominationsMeta/status.closed).</summary>
    public bool ClosedByAdmin { get; set; }

    /// <summary>Session end time (UTC), when known.</summary>
    public DateTimeOffset? EndTime { get; set; }

    /// <summary>Automatic close time: <see cref="EndTime"/> + 24 hours.</summary>
    public DateTimeOffset? ClosesAt { get; set; }

    public int? MyNomineeSlotId { get; set; }
    public string? MyNomineeName { get; set; }
    public int TotalVotes { get; set; }

    /// <summary>All nominees with at least one vote, most votes first.</summary>
    public IReadOnlyList<NominationTallyEntry> Entries { get; set; } = [];

    /// <summary>Joint leaders (empty when there are no votes). No tie-break.</summary>
    public IReadOnlyList<NominationTallyEntry> Leaders { get; set; } = [];

    /// <summary>
    /// The viewer is an Admin of the team (from their members/{uid} role). Only Admins
    /// see the live tally while voting is open.
    /// </summary>
    public bool ViewerIsAdmin { get; set; }

    /// <summary>
    /// Counts and leaders are withheld: voting is open and the viewer is not an Admin,
    /// or the full vote list has not been read yet. Entries/Leaders are empty and
    /// TotalVotes is 0; only the viewer's own pick is set.
    /// </summary>
    public bool CountsHidden { get; set; }

    /// <summary>Final result: the leaders once voting has closed; empty while open.</summary>
    public IReadOnlyList<NominationTallyEntry> Winners => IsOpen ? [] : Leaders;
}

/// <summary>One stored vote as read from Firestore (service-internal; never sent to pages).</summary>
public readonly record struct NominationVote(
    string NominatorId,
    int NomineeSlotId,
    string NomineeName,
    DateTimeOffset? Timestamp);

/// <summary>Pure tally maths (no Firebase), shared by the listener and the match report.</summary>
public static class NominationTallyCalculator
{
    public static readonly TimeSpan VotingWindow = TimeSpan.FromHours(24);

    /// <summary>Voting is open when not closed by an Admin and <paramref name="now"/> ≤ end + 24h.</summary>
    public static bool IsOpen(DateTimeOffset? endTime, bool closedByAdmin, DateTimeOffset now)
        => !closedByAdmin && endTime is { } end && now <= end + VotingWindow;

    public static NominationTally Build(
        string teamId,
        string sessionId,
        IEnumerable<NominationVote> votes,
        string? myUid,
        DateTimeOffset? endTime,
        bool closedByAdmin,
        DateTimeOffset now,
        bool viewerIsAdmin = true,
        bool votesComplete = true)
    {
        var list = votes
            .Where(v => v.NomineeSlotId >= 1 && !string.IsNullOrEmpty(v.NominatorId))
            // One ballot per member (doc id = uid); guard against duplicates anyway.
            .GroupBy(v => v.NominatorId, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(v => v.Timestamp ?? DateTimeOffset.MinValue).First())
            .ToList();

        var entries = list
            .GroupBy(v => v.NomineeSlotId)
            .Select(g => new NominationTallyEntry
            {
                NomineeSlotId = g.Key,
                NomineeName = g.OrderByDescending(v => v.Timestamp ?? DateTimeOffset.MinValue)
                               .Select(v => v.NomineeName)
                               .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
                           ?? Player.DefaultName(g.Key),
                Votes = g.Count()
            })
            .OrderByDescending(e => e.Votes)
            .ThenBy(e => e.NomineeName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var max = entries.Count > 0 ? entries[0].Votes : 0;
        foreach (var e in entries)
            e.IsLeader = max > 0 && e.Votes == max;

        var mineIdx = string.IsNullOrEmpty(myUid)
            ? -1
            : list.FindIndex(v => string.Equals(v.NominatorId, myUid, StringComparison.Ordinal));
        NominationVote? mine = mineIdx >= 0 ? list[mineIdx] : null;

        // The live tally is Admin-only while open (it could sway voters).
        var open = IsOpen(endTime, closedByAdmin, now);
        var hidden = (open && !viewerIsAdmin) || !votesComplete;
        if (hidden) entries = [];

        return new NominationTally
        {
            TeamId = teamId,
            SessionId = sessionId,
            EndTime = endTime,
            ClosesAt = endTime + VotingWindow,
            ClosedByAdmin = closedByAdmin,
            IsOpen = open,
            ViewerIsAdmin = viewerIsAdmin,
            CountsHidden = hidden,
            MyNomineeSlotId = mine?.NomineeSlotId,
            MyNomineeName = mine?.NomineeName,
            TotalVotes = hidden ? 0 : list.Count,
            Entries = entries,
            Leaders = entries.Where(e => e.IsLeader).ToList()
        };
    }
}

/// <summary>What the nominate screen needs for one session (no Firebase types).</summary>
public sealed class NominationBallot
{
    public string TeamId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public bool SessionFound { get; set; }
    public DateTimeOffset? StartTime { get; set; }
    public DateTimeOffset? EndTime { get; set; }

    /// <summary>Present / active players in that match's squad.</summary>
    public IReadOnlyList<NominationCandidate> Candidates { get; set; } = [];

    /// <summary>True when an older session had no squad snapshot and the current roster was used.</summary>
    public bool UsedRosterFallback { get; set; }
}

public enum NominationSubmitStatus
{
    Ok,
    DisabledForLocalTeam,
    NotSignedIn,
    SessionNotFound,
    InvalidNominee,
    WindowClosed,
    /// <summary>Rules refused the vote and this account has no members/{uid} doc in the team.</summary>
    NotMember,
    /// <summary>Rules refused the vote for another reason (member, voting open); details are logged.</summary>
    PermissionDenied,
    /// <summary>Network unavailable; nothing was saved.</summary>
    Offline,
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
