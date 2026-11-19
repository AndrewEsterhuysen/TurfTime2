using Plugin.Firebase.Firestore;
using TurfTime2.Models;

namespace TurfTime2.Services;

/// <summary>
/// Scaffold stub for player-of-the-match nominations.
/// Path helpers and validation are in place; transactional submit and the
/// snapshot listener are not wired to Firestore yet (see docs/nominations-spec.md).
/// </summary>
public sealed class NominationService : INominationService
{
    public const int DefaultWindowHours = 24;

    private readonly IFirebaseAuthService _auth;
    private readonly IFirebaseFirestore _db;

    public NominationService(IFirebaseAuthService auth, IFirebaseFirestore db)
    {
        _auth = auth;
        _db = db;
    }

    /// <summary>
    /// <c>teams/{teamId}/sessions/{sessionId}/nominations/{nominatorId}</c>
    /// </summary>
    public static string NominationPath(string teamId, string sessionId, string nominatorId)
        => $"teams/{teamId}/sessions/{sessionId}/nominations/{nominatorId}";

    /// <summary>
    /// <c>teams/{teamId}/sessions/{sessionId}/nominations</c>
    /// </summary>
    public static string NominationsCollectionPath(string teamId, string sessionId)
        => $"teams/{teamId}/sessions/{sessionId}/nominations";

    /// <summary>
    /// <c>teams/{teamId}/sessions/{sessionId}/nominationsMeta/status</c>
    /// </summary>
    public static string NominationsMetaPath(string teamId, string sessionId)
        => $"teams/{teamId}/sessions/{sessionId}/nominationsMeta/status";

    public async Task<NominationSubmitResult> SubmitNominationAsync(
        string teamId,
        string sessionId,
        int nomineeSlotId,
        string nomineeName,
        CancellationToken cancellationToken = default)
    {
        // Keep the field so the fully wired implementation can use the SDK
        // without a constructor change. The stub does not touch Firestore.
        _ = _db;

        cancellationToken.ThrowIfCancellationRequested();

        if (IsLocalOnlyTeam(teamId))
            return NominationSubmitResult.From(
                NominationSubmitStatus.DisabledForLocalTeam,
                "Nominations are available on shared teams only.");

        if (string.IsNullOrWhiteSpace(teamId) || string.IsNullOrWhiteSpace(sessionId))
            return NominationSubmitResult.From(NominationSubmitStatus.Failed, "Missing team or session.");

        if (nomineeSlotId < 1 || string.IsNullOrWhiteSpace(nomineeName))
            return NominationSubmitResult.From(NominationSubmitStatus.InvalidNominee, "Pick a player from the match squad.");

        if (await _auth.EnsureSignedInAsync().ConfigureAwait(false) is null)
            return NominationSubmitResult.From(NominationSubmitStatus.NotSignedIn, "Sign in to nominate.");

        // Transactional submit (when wired):
        //  1. Read teams/{teamId}/sessions/{sessionId} (must exist, endTime set).
        //  2. Read nominationsMeta/status (must not be closed).
        //  3. Confirm now <= endTime + 24h and nominee is on the session squad.
        //  4. SetData on NominationPath(teamId, sessionId, uid) with merge
        //     so a second vote replaces the first (doc id = nominator uid).
        return NominationSubmitResult.From(
            NominationSubmitStatus.NotWired,
            "Nominations are scaffolded but not writing to Firestore yet.");
    }

    public async Task<IDisposable?> WatchTallyAsync(
        string teamId,
        string sessionId,
        Action<NominationTally> onTally,
        Action<Exception>? onError = null)
    {
        if (IsLocalOnlyTeam(teamId) || string.IsNullOrWhiteSpace(teamId) || string.IsNullOrWhiteSpace(sessionId))
            return null;

        var uid = await _auth.EnsureSignedInAsync().ConfigureAwait(false);
        if (uid is null)
        {
            onError?.Invoke(new InvalidOperationException("Not signed in to Firebase"));
            return null;
        }

        // When wired: AddSnapshotListener on NominationsCollectionPath plus
        // NominationsMetaPath, aggregate counts by nomineeSlotId, and omit voter
        // identities from the tally delivered to the UI.
        try
        {
            onTally(new NominationTally
            {
                TeamId = teamId,
                SessionId = sessionId,
                IsOpen = false,
                Entries = [],
                Leaders = []
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[NominationService] WatchTally stub: {ex.Message}");
            onError?.Invoke(ex);
        }

        return EmptyWatch.Instance;
    }

    private static bool IsLocalOnlyTeam(string teamId) =>
        string.IsNullOrWhiteSpace(teamId)
        || teamId.StartsWith("local_", StringComparison.Ordinal)
        || string.Equals(Preferences.Get("team_mode", string.Empty), "local", StringComparison.Ordinal);

    private sealed class EmptyWatch : IDisposable
    {
        public static readonly EmptyWatch Instance = new();
        public void Dispose() { }
    }
}
