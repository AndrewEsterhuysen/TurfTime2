using TurfTime2.Models;

namespace TurfTime2.Services;

/// <summary>
/// Player-of-the-match nominations for a completed shared-team session.
/// Path: <c>teams/{teamId}/sessions/{sessionId}/nominations/{nominatorId}</c>.
/// See docs/nominations-spec.md.
/// </summary>
public interface INominationService
{
    /// <summary>
    /// Submit or change the current member's nomination (one doc per member,
    /// written in a transaction when this service is fully wired).
    /// Fails soft; never throws for expected cases such as local teams or offline.
    /// </summary>
    Task<NominationSubmitResult> SubmitNominationAsync(
        string teamId,
        string sessionId,
        int nomineeSlotId,
        string nomineeName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Live tally listener for a session. Returns <c>IDisposable</c> to stop,
    /// or <c>null</c> when unavailable (local team, not signed in).
    /// The callback must not include a voter list — counts and the caller's
    /// own pick only.
    /// </summary>
    Task<IDisposable?> WatchTallyAsync(
        string teamId,
        string sessionId,
        Action<NominationTally> onTally,
        Action<Exception>? onError = null);
}
