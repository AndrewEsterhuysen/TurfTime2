using TurfTime2.Models;

namespace TurfTime2.Services;

/// <summary>
/// Player-of-the-match nominations for a completed shared-team session.
/// Path: <c>teams/{teamId}/sessions/{sessionId}/nominations/{nominatorId}</c>.
/// Local teams are not supported (shared-team-only feature). See docs/nominations-spec.md.
/// Every method fails soft: expected failures (local team, offline, closed) are
/// reported through return values, never thrown.
/// </summary>
public interface INominationService
{
    /// <summary>
    /// Loads what the nominate screen needs: session times and the eligible
    /// (present / active) players from that match's squad. Returns
    /// <c>SessionFound = false</c> for local teams or when the session cannot be read.
    /// </summary>
    Task<NominationBallot> LoadBallotAsync(
        string teamId,
        string sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Submit or change the current member's nomination in a Firestore transaction:
    /// the session must exist with an end time, voting must not be closed by an Admin,
    /// now must be within 24 hours of the end time, and the nominee must have been
    /// present in that session's squad. The vote is stored under the member's uid,
    /// so a new vote replaces the old one.
    /// </summary>
    Task<NominationSubmitResult> SubmitNominationAsync(
        string teamId,
        string sessionId,
        int nomineeSlotId,
        string nomineeName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Admin (or team creator) only, enforced by Firestore rules: close voting early by
    /// writing <c>nominationsMeta/status</c>. On success, open tally watches on this device
    /// switch to the result straight away. On failure the result carries a user-facing message.
    /// </summary>
    Task<NominationCloseResult> CloseVotingAsync(
        string teamId,
        string sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Live tally listener for a session (nominations + close status). Returns
    /// <c>IDisposable</c> to stop, or <c>null</c> when unavailable (local team,
    /// not signed in). The tally has counts and the caller's own pick only, never
    /// a voter list. Same pattern as <see cref="IChatService.SubscribeAsync"/>.
    /// </summary>
    Task<IDisposable?> WatchTallyAsync(
        string teamId,
        string sessionId,
        Action<NominationTally> onTally,
        Action<Exception>? onError = null);

    /// <summary>
    /// One-shot tally read (e.g. for the match report). Returns null for local
    /// teams or when the data cannot be read.
    /// </summary>
    Task<NominationTally?> GetTallyAsync(
        string teamId,
        string sessionId,
        CancellationToken cancellationToken = default);
}
