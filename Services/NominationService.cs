using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Plugin.Firebase.Firestore;
using TurfTime2.Helpers;
using TurfTime2.Models;

namespace TurfTime2.Services;

/// <summary>
/// Player-of-the-match nominations (docs/nominations-spec.md §6).
/// <para>
/// Votes are written over authenticated REST as primary (batchGet → validate →
/// single commit), matching the rest of the app where Plugin.Firebase
/// <c>SetDataAsync</c> has been seen to "succeed" with empty documents on device.
/// No REST <c>beginTransaction</c>: production refuses read-write transactions for
/// end-user tokens; the rules enforce membership and the voting window at write time.
/// If REST cannot be reached, the Plugin.Firebase <c>RunTransactionAsync</c> path
/// (optimistic, allowed for clients) runs the same checks. The snapshot listener
/// is the change signal for the tally; empty SDK data falls back to REST.
/// </para>
/// </summary>
public sealed class NominationService : INominationService
{
    public const int DefaultWindowHours = 24;
    private const string FirebaseProjectId = "turf-timer";
    private const int MaxNameLength = 80;
    private static readonly HttpClient RestHttp = new();

    private readonly IFirebaseAuthService _auth;
    private readonly IFirebaseFirestore _db;
    private readonly ICloudRosterService _roster;
    private readonly List<TallyWatch> _watches = [];

    public NominationService(IFirebaseAuthService auth, IFirebaseFirestore db, ICloudRosterService roster)
    {
        _auth = auth;
        _db = db;
        _roster = roster;
    }

    // ── Paths ─────────────────────────────────────────────────────────────

    /// <summary><c>teams/{teamId}/sessions/{sessionId}</c></summary>
    public static string SessionPath(string teamId, string sessionId)
        => $"teams/{teamId}/sessions/{sessionId}";

    /// <summary><c>teams/{teamId}/sessions/{sessionId}/nominations/{nominatorId}</c></summary>
    public static string NominationPath(string teamId, string sessionId, string nominatorId)
        => $"teams/{teamId}/sessions/{sessionId}/nominations/{nominatorId}";

    /// <summary><c>teams/{teamId}/sessions/{sessionId}/nominations</c></summary>
    public static string NominationsCollectionPath(string teamId, string sessionId)
        => $"teams/{teamId}/sessions/{sessionId}/nominations";

    /// <summary><c>teams/{teamId}/sessions/{sessionId}/nominationsMeta/status</c></summary>
    public static string NominationsMetaPath(string teamId, string sessionId)
        => $"teams/{teamId}/sessions/{sessionId}/nominationsMeta/status";

    private static string DocumentsRoot =>
        $"projects/{FirebaseProjectId}/databases/(default)/documents";

    private static string RestBase =>
        $"https://firestore.googleapis.com/v1/{DocumentsRoot}";

    private static string RestDocUrl(string path) =>
        $"{RestBase}/{string.Join('/', path.Split('/').Select(Uri.EscapeDataString))}";

    private static string FullName(string path) => $"{DocumentsRoot}/{path}";

    // ── Ballot ────────────────────────────────────────────────────────────

    public async Task<NominationBallot> LoadBallotAsync(
        string teamId, string sessionId, CancellationToken cancellationToken = default)
    {
        var ballot = new NominationBallot { TeamId = teamId ?? "", SessionId = sessionId ?? "" };
        if (IsLocalOnlyTeam(teamId) || string.IsNullOrWhiteSpace(sessionId))
            return ballot;

        try
        {
            if (await _auth.EnsureSignedInAsync().ConfigureAwait(false) is null)
                return ballot;

            var state = await ReadSessionStateAsync(teamId!, sessionId, cancellationToken).ConfigureAwait(false);
            if (state is null || !state.Exists)
                return ballot;

            var roster = await LoadRosterIfNeededAsync(teamId!, state.Session).ConfigureAwait(false);
            ballot.SessionFound = true;
            ballot.StartTime = state.Session?.StartTime;
            ballot.EndTime = state.EndTime;
            ballot.Candidates = NominationSquad.GetEligible(state.Session, roster, out var fallback);
            ballot.UsedRosterFallback = fallback;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[NominationService] LoadBallot: {ex.Message}");
        }
        return ballot;
    }

    // ── Submit ──────────────────────────────────────────────

    public async Task<NominationSubmitResult> SubmitNominationAsync(
        string teamId,
        string sessionId,
        int nomineeSlotId,
        string nomineeName,
        CancellationToken cancellationToken = default)
    {
        if (IsLocalOnlyTeam(teamId))
            return NominationSubmitResult.From(
                NominationSubmitStatus.DisabledForLocalTeam,
                "Nominations are available on shared teams only.");

        if (string.IsNullOrWhiteSpace(sessionId))
            return NominationSubmitResult.From(NominationSubmitStatus.SessionNotFound, "Missing match.");

        var name = (nomineeName ?? string.Empty).Trim();
        if (name.Length > MaxNameLength) name = name[..MaxNameLength];
        if (nomineeSlotId < 1 || name.Length == 0)
            return NominationSubmitResult.From(NominationSubmitStatus.InvalidNominee, "Pick a player from the match squad.");

        string? uid;
        try { uid = await _auth.EnsureSignedInAsync().ConfigureAwait(false); }
        catch { uid = null; }
        if (uid is null)
            return NominationSubmitResult.From(NominationSubmitStatus.NotSignedIn, "Sign in to nominate.");

        var displayName = UserDisplayName.Get();
        if (displayName.Length > MaxNameLength) displayName = displayName[..MaxNameLength];

        // Legacy sessions (no squad snapshot) need the current roster to resolve slots.
        // The roster is not part of the transaction: it only maps old names to slots.
        IReadOnlyList<PlayerSnapshot>? roster = null;
        try
        {
            var pre = await ReadSessionStateAsync(teamId, sessionId, cancellationToken).ConfigureAwait(false);
            roster = await LoadRosterIfNeededAsync(teamId, pre?.Session).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[NominationService] Submit pre-read: {ex.Message}");
        }

        var vote = new PendingVote(uid, displayName, nomineeSlotId, name);

        try
        {
            return await SubmitViaRestAsync(teamId, sessionId, vote, roster, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return NominationSubmitResult.From(NominationSubmitStatus.Failed, "Cancelled.");
        }
        catch (FirestoreRestException restEx) when (restEx.StatusCode == HttpStatusCode.Forbidden)
        {
            // The server refused a read or the write. Work out why before telling the user.
            Log($"REST submit denied: {restEx.Message}");
            return await ExplainDeniedAsync(teamId, sessionId, uid, restEx, cancellationToken).ConfigureAwait(false);
        }
        catch (FirestoreRestException restEx)
        {
            // The server answered with another error (not a rules refusal): don't retry via the SDK.
            Log($"REST submit failed: {restEx.Message}");
            return NominationSubmitResult.From(NominationSubmitStatus.Failed,
                $"Couldn't save your vote ({restEx.Code}). Please try again.");
        }
        catch (Exception restEx) when (LooksOffline(restEx))
        {
            Log($"REST submit offline: {restEx.GetType().Name}: {restEx.Message}");
            return NominationSubmitResult.From(NominationSubmitStatus.Offline,
                "You're offline. Your vote wasn't saved. Try again when you're back online.");
        }
        catch (Exception restEx)
        {
            Log($"REST submit failed, trying SDK: {restEx.GetType().Name}: {restEx.Message}");
        }

        try
        {
            return await SubmitViaSdkTransactionAsync(teamId, sessionId, vote, roster).ConfigureAwait(false);
        }
        catch (Exception sdkEx)
        {
            Log($"SDK transaction failed: {sdkEx.GetType().Name}: {sdkEx.Message}");
            if (LooksOffline(sdkEx))
                return NominationSubmitResult.From(NominationSubmitStatus.Offline,
                    "You're offline. Your vote wasn't saved. Try again when you're back online.");
            if (LooksPermissionDenied(sdkEx))
                return await ExplainDeniedAsync(teamId, sessionId, uid, sdkEx, cancellationToken).ConfigureAwait(false);
            return NominationSubmitResult.From(NominationSubmitStatus.Failed,
                "Couldn't save your vote. Please try again.");
        }
    }

    /// <summary>
    /// After a PERMISSION_DENIED: re-read what the rules check so the message says why.
    /// Not a member (no members/{uid} doc) → <see cref="NominationSubmitStatus.NotMember"/>;
    /// closed, past 24 h, or no Firestore-timestamp endTime → <see cref="NominationSubmitStatus.WindowClosed"/>;
    /// anything else → <see cref="NominationSubmitStatus.PermissionDenied"/> (logged with the server error).
    /// </summary>
    private async Task<NominationSubmitResult> ExplainDeniedAsync(
        string teamId, string sessionId, string uid, Exception denied, CancellationToken ct)
    {
        try
        {
            var (isMember, _) = await GetDocViaRestAsync($"teams/{teamId}/members/{uid}", ct).ConfigureAwait(false);
            if (!isMember)
            {
                Log($"Denied: no members/{uid} doc in team {teamId}");
                return NominationSubmitResult.From(NominationSubmitStatus.NotMember,
                    "Your vote wasn't accepted because this device isn't a member of the team any more. Rejoin the team, then vote again.");
            }

            var (found, doc) = await GetDocViaRestAsync(SessionPath(teamId, sessionId), ct).ConfigureAwait(false);
            if (!found)
                return NominationSubmitResult.From(NominationSubmitStatus.SessionNotFound, "That match could not be found.");
            var fields = doc is { } d && d.TryGetProperty("fields", out var f) ? f : default;
            var hasTimestampEnd = fields.ValueKind == JsonValueKind.Object
                                  && fields.TryGetProperty("endTime", out var et)
                                  && et.TryGetProperty("timestampValue", out _);
            if (!hasTimestampEnd)
            {
                Log($"Denied: session {sessionId} endTime is missing or not a Firestore timestamp");
                return NominationSubmitResult.From(NominationSubmitStatus.WindowClosed,
                    "Voting isn't open for this match (its end time wasn't saved).");
            }
            var state = SessionState.FromRest(doc);
            var closed = await ReadClosedAsync(teamId, sessionId, ct).ConfigureAwait(false);
            if (closed)
                return NominationSubmitResult.From(NominationSubmitStatus.WindowClosed, "An Admin has closed voting for this match.");
            if (!NominationTallyCalculator.IsOpen(state.EndTime, closedByAdmin: false, DateTimeOffset.UtcNow))
                return NominationSubmitResult.From(NominationSubmitStatus.WindowClosed, "Voting closed 24 hours after the match.");
        }
        catch (Exception ex)
        {
            Log($"Denied: follow-up checks failed: {ex.GetType().Name}: {ex.Message}");
            if (LooksOffline(ex))
                return NominationSubmitResult.From(NominationSubmitStatus.Offline,
                    "You're offline. Your vote wasn't saved. Try again when you're back online.");
        }

        Log($"Denied for an unknown reason (member, voting open): {denied.Message}");
        return NominationSubmitResult.From(NominationSubmitStatus.PermissionDenied,
            "The server refused your vote (permission denied). Please try again, and tell your team Admin if it keeps happening.");
    }

    private sealed record PendingVote(string Uid, string DisplayName, int SlotId, string NomineeName);

    /// <summary>Checks shared by the REST and SDK transactions (spec §6 step 3).</summary>
    private static NominationSubmitResult? Validate(
        SessionState session, bool closedByAdmin, PendingVote vote,
        IReadOnlyList<PlayerSnapshot>? roster, DateTimeOffset now)
    {
        if (!session.Exists)
            return NominationSubmitResult.From(NominationSubmitStatus.SessionNotFound, "That match could not be found.");
        if (session.EndTime is null)
            return NominationSubmitResult.From(NominationSubmitStatus.WindowClosed, "Voting opens when the match has ended.");
        if (closedByAdmin)
            return NominationSubmitResult.From(NominationSubmitStatus.WindowClosed, "An Admin has closed voting for this match.");
        if (!NominationTallyCalculator.IsOpen(session.EndTime, closedByAdmin: false, now))
            return NominationSubmitResult.From(NominationSubmitStatus.WindowClosed, "Voting closed 24 hours after the match.");
        if (!NominationSquad.IsEligible(session.Session, roster, vote.SlotId))
            return NominationSubmitResult.From(NominationSubmitStatus.InvalidNominee,
                "Only players who were present for this match can be nominated.");
        return null;
    }

    /// <summary>
    /// Read the session and close status, validate, then write the vote with one commit.
    /// <para>
    /// No <c>beginTransaction</c>: production Firestore refuses read-write transactions
    /// started over REST with an end-user ID token (403 PERMISSION_DENIED), although the
    /// emulator allows them. That refusal was the "no longer on this team" error in build 58.
    /// A transaction adds nothing here: the vote doc id is the voter's uid (no contention)
    /// and the security rules re-check membership, endTime + 24 h and the close flag
    /// atomically at write time.
    /// </para>
    /// </summary>
    private async Task<NominationSubmitResult> SubmitViaRestAsync(
        string teamId, string sessionId, PendingVote vote,
        IReadOnlyList<PlayerSnapshot>? roster, CancellationToken ct)
    {
        var token = await GetTokenAsync().ConfigureAwait(false)
                    ?? throw new InvalidOperationException("No Firebase id token");

        var docs = await BatchGetAsync(token,
            [SessionPath(teamId, sessionId), NominationsMetaPath(teamId, sessionId)], ct).ConfigureAwait(false);

        var session = SessionState.FromRest(docs.GetValueOrDefault(SessionPath(teamId, sessionId)));
        var closed = ReadRestBool(docs.GetValueOrDefault(NominationsMetaPath(teamId, sessionId)), "closed");
        var invalid = Validate(session, closed, vote, roster, DateTimeOffset.UtcNow);
        if (invalid is not null)
            return invalid;

        await CommitAsync(token, NominationPath(teamId, sessionId, vote.Uid), vote, ct).ConfigureAwait(false);
        Log($"✅ REST vote saved team={teamId} session={sessionId} slot={vote.SlotId}");
        return NominationSubmitResult.From(NominationSubmitStatus.Ok);
    }

    private async Task<NominationSubmitResult> SubmitViaSdkTransactionAsync(
        string teamId, string sessionId, PendingVote vote, IReadOnlyList<PlayerSnapshot>? roster)
    {
        var sessionRef = _db.GetDocument(SessionPath(teamId, sessionId));
        var metaRef = _db.GetDocument(NominationsMetaPath(teamId, sessionId));
        var voteRef = _db.GetDocument(NominationPath(teamId, sessionId, vote.Uid));

        var result = await _db.RunTransactionAsync(tx =>
        {
            var sessionSnap = tx.GetDocument<Dictionary<string, object>>(sessionRef);
            var metaSnap = tx.GetDocument<Dictionary<string, object>>(metaRef);

            var session = SessionState.FromSdk(sessionSnap?.Data);
            var closed = ReadSdkBool(metaSnap?.Data, "closed");
            var invalid = Validate(session, closed, vote, roster, DateTimeOffset.UtcNow);
            if (invalid is not null)
                return invalid;

            // Overwrite (no merge): doc id = uid, so a new vote replaces the old one.
            tx.SetData(voteRef, new (object, object)[]
            {
                ("nominatorId", vote.Uid),
                ("nominatorDisplayName", vote.DisplayName),
                ("nomineeSlotId", (long)vote.SlotId),
                ("nomineeName", vote.NomineeName),
                ("timestamp", DateTimeOffset.UtcNow)
            });
            return NominationSubmitResult.From(NominationSubmitStatus.Ok);
        }).ConfigureAwait(false);

        FirestoreUsageMeter.RecordSdk("Transaction", "Nomination");
        return result;
    }

    // ── Admin close ───────────────────────────────────────────────────────

    public async Task<NominationCloseResult> CloseVotingAsync(
        string teamId, string sessionId, CancellationToken cancellationToken = default)
    {
        if (IsLocalOnlyTeam(teamId) || string.IsNullOrWhiteSpace(sessionId))
            return NominationCloseResult.Fail("Nominations are available on shared teams only.");

        string? uid;
        try { uid = await _auth.EnsureSignedInAsync().ConfigureAwait(false); }
        catch { uid = null; }
        if (uid is null)
            return NominationCloseResult.Fail("You're not signed in. Check your connection and try again.");

        var now = DateTimeOffset.UtcNow;
        var path = NominationsMetaPath(teamId, sessionId);
        Exception? restError = null;
        try
        {
            var token = await GetTokenAsync().ConfigureAwait(false)
                        ?? throw new InvalidOperationException("No Firebase id token");
            var fields = new Dictionary<string, object>
            {
                ["closed"] = new Dictionary<string, object> { ["booleanValue"] = true },
                ["closedAt"] = new Dictionary<string, object> { ["timestampValue"] = RestTimestamp(now) },
                ["closedBy"] = new Dictionary<string, object> { ["stringValue"] = uid }
            };
            using var req = new HttpRequestMessage(HttpMethod.Patch, RestDocUrl(path))
            {
                Content = new StringContent(JsonSerializer.Serialize(new { fields }), Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await RestHttp.SendAsync(req, cancellationToken).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            FirestoreUsageMeter.RecordRestBody("PATCH", req.RequestUri?.ToString(), body);
            if (resp.IsSuccessStatusCode)
            {
                Log($"✅ Voting closed team={teamId} session={sessionId}");
                NotifyClosed(teamId, sessionId);
                return NominationCloseResult.Ok();
            }

            var error = FirestoreRestException.From($"PATCH {path}", resp.StatusCode, body);
            Log($"Close refused: {error.Message}");
            if (resp.StatusCode == HttpStatusCode.Forbidden)
            {
                var isAdmin = await IsViewerAdminAsync(teamId, uid, cancellationToken).ConfigureAwait(false);
                return NominationCloseResult.Fail(isAdmin
                    ? $"The server refused to close voting ({error.Code}). Please try again; if it keeps happening, tell the developer: {error.Message}"
                    : "Only team Admins can close voting, and this account isn't an Admin of this team.");
            }
            return NominationCloseResult.Fail($"Couldn't close voting: {error.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            restError = ex;
            Log($"Close REST: {ex.GetType().Name}: {ex.Message}");
        }

        // REST unreachable: try the SDK, then confirm the flag really is set (the SDK has
        // been seen to "succeed" without writing on device).
        try
        {
            await _db.GetDocument(path).SetDataAsync(new (object, object)[]
            {
                ("closed", true),
                ("closedAt", now),
                ("closedBy", uid)
            }).ConfigureAwait(false);
            FirestoreUsageMeter.RecordSdk("Set", "NominationsMeta");
            if (await ReadClosedAsync(teamId, sessionId, cancellationToken).ConfigureAwait(false))
            {
                NotifyClosed(teamId, sessionId);
                return NominationCloseResult.Ok();
            }
            Log("Close SDK write did not stick");
        }
        catch (Exception ex)
        {
            Log($"Close SDK: {ex.GetType().Name}: {ex.Message}");
        }

        return NominationCloseResult.Fail(restError is not null && LooksOffline(restError)
            ? "Couldn't close voting: you appear to be offline. Check your connection and try again."
            : $"Couldn't close voting: {restError?.Message ?? "unknown error"}");
    }

    /// <summary>Update open watches on this device straight away (don't wait for the listener).</summary>
    private void NotifyClosed(string teamId, string sessionId)
    {
        List<TallyWatch> watches;
        lock (_watches) watches = _watches.Where(w => w.Matches(teamId, sessionId)).ToList();
        foreach (var w in watches)
        {
            try { w.SetClosed(true); }
            catch (Exception ex) { Log($"NotifyClosed: {ex.Message}"); }
        }
    }

    // ── Tally ─────────────────────────────────────────────────────────────

    public async Task<NominationTally?> GetTallyAsync(
        string teamId, string sessionId, CancellationToken cancellationToken = default)
    {
        if (IsLocalOnlyTeam(teamId) || string.IsNullOrWhiteSpace(sessionId))
            return null;
        try
        {
            var uid = await _auth.EnsureSignedInAsync().ConfigureAwait(false);
            if (uid is null) return null;

            var session = await ReadSessionStateAsync(teamId, sessionId, cancellationToken).ConfigureAwait(false);
            if (session is null || !session.Exists) return null;
            var closed = await ReadClosedAsync(teamId, sessionId, cancellationToken).ConfigureAwait(false);
            var isAdmin = await IsViewerAdminAsync(teamId, uid, cancellationToken).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var open = NominationTallyCalculator.IsOpen(session.EndTime, closed, now);

            // Members never read other members' votes while voting is open.
            var votes = open && !isAdmin
                ? await ReadMyVoteAsync(teamId, sessionId, uid, cancellationToken).ConfigureAwait(false)
                : await ListVotesViaRestAsync(teamId, sessionId, cancellationToken).ConfigureAwait(false);
            return NominationTallyCalculator.Build(teamId, sessionId, votes, uid, session.EndTime, closed, now,
                viewerIsAdmin: isAdmin);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[NominationService] GetTally: {ex.Message}");
            return null;
        }
    }

    public async Task<IDisposable?> WatchTallyAsync(
        string teamId,
        string sessionId,
        Action<NominationTally> onTally,
        Action<Exception>? onError = null)
    {
        if (IsLocalOnlyTeam(teamId) || string.IsNullOrWhiteSpace(sessionId))
            return null;

        string? uid;
        try { uid = await _auth.EnsureSignedInAsync().ConfigureAwait(false); }
        catch { uid = null; }
        if (uid is null)
        {
            onError?.Invoke(new InvalidOperationException("Not signed in to Firebase"));
            return null;
        }

        var watch = new TallyWatch(this, teamId, sessionId, uid, onTally, onError);
        try
        {
            await watch.StartAsync().ConfigureAwait(false);
            lock (_watches) _watches.Add(watch);
            return watch;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[NominationService] Watch: {ex.Message}");
            watch.Dispose();
            onError?.Invoke(ex);
            return null;
        }
    }

    /// <summary>
    /// Live tally. Admins (and everyone once voting has closed) listen to the whole
    /// nominations collection. Members, while voting is open, listen only to their own
    /// vote doc: the live tally is Admin-only so it can't sway voters, and members don't
    /// download other votes. When voting closes (Admin close or the 24-hour timer) a
    /// member's watch reads all votes once and shows the result. The close-status doc is
    /// watched in both modes.
    /// </summary>
    private sealed class TallyWatch : IDisposable
    {
        private static readonly TimeSpan[] ResultRetryDelays =
            [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(45), TimeSpan.FromMinutes(2)];

        private readonly NominationService _svc;
        private readonly string _teamId, _sessionId, _uid;
        private readonly Action<NominationTally> _onTally;
        private readonly Action<Exception>? _onError;
        private readonly object _gate = new();
        private IDisposable? _votesReg, _metaReg;
        private Timer? _closeTimer;
        private List<NominationVote> _votes = [];
        private bool _closed;
        private DateTimeOffset? _endTime;
        private bool _disposed;
        private bool _isAdmin;
        private bool _allVotes;       // _votes holds every vote (admin, or result loaded after close)
        private bool _loadingResult;

        public TallyWatch(NominationService svc, string teamId, string sessionId, string uid,
            Action<NominationTally> onTally, Action<Exception>? onError)
        {
            _svc = svc; _teamId = teamId; _sessionId = sessionId; _uid = uid;
            _onTally = onTally; _onError = onError;
        }

        public async Task StartAsync()
        {
            var session = await _svc.ReadSessionStateAsync(_teamId, _sessionId, default).ConfigureAwait(false);
            _endTime = session?.EndTime;
            _isAdmin = await _svc.IsViewerAdminAsync(_teamId, _uid, default).ConfigureAwait(false);

            try { _closed = await _svc.ReadClosedAsync(_teamId, _sessionId, default).ConfigureAwait(false); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[NominationService] Initial close flag: {ex.Message}"); }

            var open = NominationTallyCalculator.IsOpen(_endTime, _closed, DateTimeOffset.UtcNow);
            _allVotes = _isAdmin || !open;

            // Initial REST load so the UI is right even if SDK listener data is empty.
            try
            {
                _votes = _allVotes
                    ? await _svc.ListVotesViaRestAsync(_teamId, _sessionId, default).ConfigureAwait(false)
                    : await _svc.ReadMyVoteAsync(_teamId, _sessionId, _uid, default).ConfigureAwait(false);
                Emit();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NominationService] Initial tally: {ex.Message}");
                if (_allVotes && !_isAdmin)
                {
                    // Closed for this member but the list failed — show own pick, retry the result.
                    _allVotes = false;
                    try { _votes = await _svc.ReadMyVoteAsync(_teamId, _sessionId, _uid, default).ConfigureAwait(false); }
                    catch { /* best effort */ }
                    Emit();
                }
            }

            if (_endTime is { } end)
            {
                var due = end + NominationTallyCalculator.VotingWindow - DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1);
                if (due > TimeSpan.Zero && due < TimeSpan.FromDays(2))
                    _closeTimer = new Timer(_ => Emit(), null, due, Timeout.InfiniteTimeSpan);
            }

            FirestoreUsageMeter.SetFlag("watchingNominations", true);
            if (_allVotes)
                StartAllVotesListener();
            else
                StartMyVoteListener();

            _metaReg = _svc._db.GetDocument(NominationsMetaPath(_teamId, _sessionId))
                .AddSnapshotListener<Dictionary<string, object>>(
                    snapshot => _ = Task.Run(() => OnMetaSnapshotAsync(snapshot)),
                    ex => System.Diagnostics.Debug.WriteLine($"[NominationService] Meta listener: {ex.Message}"));
        }

        /// <summary>
        /// Close-flag change. On device the SDK snapshot Data can be empty even when the
        /// doc says closed=true, so a snapshot without a readable "closed" field is only a
        /// change signal and REST decides. (Build 61 read empty Data as "open", so Close
        /// voting appeared to do nothing and a reopened page showed voting as open.)
        /// </summary>
        private async Task OnMetaSnapshotAsync(IDocumentSnapshot<Dictionary<string, object>>? snapshot)
        {
            try
            {
                FirestoreUsageMeter.RecordListener("NominationsMeta");
                bool closed;
                if (snapshot?.Data is { } data && data.ContainsKey("closed"))
                    closed = ReadSdkBool(data, "closed");
                else
                    closed = await _svc.ReadClosedAsync(_teamId, _sessionId, default).ConfigureAwait(false);
                SetClosed(closed);
            }
            catch (Exception ex)
            {
                // Keep the last known state rather than guessing "open".
                Log($"Meta snapshot: {ex.Message}");
            }
        }

        /// <summary>Called by the service after this device closed voting, and by the meta listener.</summary>
        public void SetClosed(bool closed)
        {
            lock (_gate)
            {
                if (_disposed) return;
                _closed = closed;
                // Admins hold every vote already; make sure the result is complete.
                if (closed && _isAdmin) _allVotes = true;
            }
            Emit();
        }

        public bool Matches(string teamId, string sessionId)
            => string.Equals(_teamId, teamId, StringComparison.Ordinal)
               && string.Equals(_sessionId, sessionId, StringComparison.Ordinal);

        private void StartAllVotesListener()
        {
            _votesReg = _svc._db.GetCollection(NominationsCollectionPath(_teamId, _sessionId))
                .AddSnapshotListener<Dictionary<string, object>>(
                    snapshot => _ = Task.Run(() => OnVotesSnapshotAsync(snapshot)),
                    ex =>
                    {
                        FirestoreUsageMeter.RecordListenerError("Nominations");
                        _onError?.Invoke(ex);
                    });
        }

        private void StartMyVoteListener()
        {
            _votesReg = _svc._db.GetDocument(NominationPath(_teamId, _sessionId, _uid))
                .AddSnapshotListener<Dictionary<string, object>>(
                    snapshot => _ = Task.Run(() => OnMyVoteSnapshotAsync(snapshot)),
                    ex =>
                    {
                        FirestoreUsageMeter.RecordListenerError("Nominations");
                        _onError?.Invoke(ex);
                    });
        }

        private async Task OnMyVoteSnapshotAsync(IDocumentSnapshot<Dictionary<string, object>>? snapshot)
        {
            try
            {
                FirestoreUsageMeter.RecordListener("Nominations");
                List<NominationVote> mine;
                if (snapshot?.Data is { Count: > 0 } data)
                {
                    mine = [new NominationVote(_uid, ReadSdkInt(data, "nomineeSlotId"),
                        ReadSdkString(data, "nomineeName"), ReadSdkTimestamp(data, "timestamp"))];
                    if (mine[0].NomineeSlotId < 1)
                        mine = await _svc.ReadMyVoteAsync(_teamId, _sessionId, _uid, default).ConfigureAwait(false);
                }
                else
                {
                    // Empty SDK data can mean "no vote" or an unreadable snapshot — REST decides.
                    mine = await _svc.ReadMyVoteAsync(_teamId, _sessionId, _uid, default).ConfigureAwait(false);
                }

                lock (_gate)
                {
                    if (_allVotes) return; // result already loaded
                    _votes = mine;
                }
                Emit();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NominationService] My vote snapshot: {ex.Message}");
                _onError?.Invoke(ex);
            }
        }

        private async Task OnVotesSnapshotAsync(IQuerySnapshot<Dictionary<string, object>>? snapshot)
        {
            try
            {
                FirestoreUsageMeter.RecordListener("Nominations");
                var docs = snapshot?.Documents?.ToList() ?? [];
                var parsed = new List<NominationVote>();
                foreach (var doc in docs)
                {
                    if (doc?.Data is not { Count: > 0 } data) continue;
                    var id = doc.Reference?.Id ?? ReadSdkString(data, "nominatorId");
                    parsed.Add(new NominationVote(id, ReadSdkInt(data, "nomineeSlotId"),
                        ReadSdkString(data, "nomineeName"), ReadSdkTimestamp(data, "timestamp")));
                }

                // Empty / partial SDK data → REST is the source of truth for this event.
                // (An empty snapshot can also be the device bug, so confirm it over REST.)
                if (parsed.Count == 0 || parsed.Count < docs.Count)
                    parsed = await _svc.ListVotesViaRestAsync(_teamId, _sessionId, default).ConfigureAwait(false);

                lock (_gate) _votes = parsed;
                Emit();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NominationService] Votes snapshot: {ex.Message}");
                _onError?.Invoke(ex);
            }
        }

        /// <summary>Voting has closed for a member: read every vote once, then show the result.</summary>
        private async Task LoadResultAsync()
        {
            for (var attempt = 0; ; attempt++)
            {
                lock (_gate) if (_disposed) return;
                try
                {
                    var all = await _svc.ListVotesViaRestAsync(_teamId, _sessionId, default).ConfigureAwait(false);
                    IDisposable? myReg;
                    lock (_gate)
                    {
                        if (_disposed) return;
                        _votes = all;
                        _allVotes = true;
                        _loadingResult = false;
                        myReg = _votesReg;
                        _votesReg = null;
                    }
                    try { myReg?.Dispose(); } catch { /* ignore */ }
                    Emit();
                    return;
                }
                catch (Exception ex)
                {
                    // e.g. the rules still consider voting open for a few seconds (clock skew).
                    System.Diagnostics.Debug.WriteLine($"[NominationService] Result load {attempt + 1}: {ex.Message}");
                    if (attempt >= ResultRetryDelays.Length)
                    {
                        lock (_gate) _loadingResult = false;
                        _onError?.Invoke(ex);
                        return;
                    }
                    await Task.Delay(ResultRetryDelays[attempt]).ConfigureAwait(false);
                }
            }
        }

        private void Emit()
        {
            NominationTally tally;
            var startResultLoad = false;
            lock (_gate)
            {
                if (_disposed) return;
                var now = DateTimeOffset.UtcNow;
                var open = NominationTallyCalculator.IsOpen(_endTime, _closed, now);
                if (!open && !_allVotes && !_loadingResult)
                {
                    _loadingResult = true;
                    startResultLoad = true;
                }
                tally = NominationTallyCalculator.Build(
                    _teamId, _sessionId, _votes, _uid, _endTime, _closed, now,
                    viewerIsAdmin: _isAdmin, votesComplete: _allVotes);
            }
            if (startResultLoad)
                _ = Task.Run(LoadResultAsync);
            try { _onTally(tally); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NominationService] onTally: {ex.Message}");
                _onError?.Invoke(ex);
            }
        }

        public void Dispose()
        {
            lock (_gate) _disposed = true;
            lock (_svc._watches) _svc._watches.Remove(this);
            try { _votesReg?.Dispose(); } catch { /* ignore */ }
            try { _metaReg?.Dispose(); } catch { /* ignore */ }
            try { _closeTimer?.Dispose(); } catch { /* ignore */ }
            _votesReg = _metaReg = null;
            _closeTimer = null;
            FirestoreUsageMeter.SetFlag("watchingNominations", false);
        }
    }

    // ── Reads (REST primary, SDK fallback) ────────────────────────────────

    private sealed class SessionState
    {
        public bool Exists { get; init; }
        public DateTimeOffset? EndTime { get; init; }
        public GameSession? Session { get; init; }

        public static SessionState FromRest(JsonElement? doc)
        {
            if (doc is not { } d || !d.TryGetProperty("fields", out var fields))
                return new SessionState { Exists = doc is not null };
            var json = ReadRestString(fields, "sessionJson");
            var session = string.IsNullOrEmpty(json)
                ? null
                : SessionStorageService.ParseSessionData(new Dictionary<string, object> { ["sessionJson"] = json });
            return new SessionState { Exists = true, EndTime = ReadRestTimestamp(fields, "endTime"), Session = session };
        }

        public static SessionState FromSdk(IDictionary<string, object>? data)
        {
            if (data is null || data.Count == 0) return new SessionState { Exists = false };
            return new SessionState
            {
                Exists = true,
                EndTime = ReadSdkTimestamp(data, "endTime"),
                Session = SessionStorageService.ParseSessionData(data)
            };
        }
    }

    private async Task<SessionState?> ReadSessionStateAsync(string teamId, string sessionId, CancellationToken ct)
    {
        var path = SessionPath(teamId, sessionId);
        try
        {
            var (found, doc) = await GetDocViaRestAsync(path, ct).ConfigureAwait(false);
            return found ? SessionState.FromRest(doc) : new SessionState { Exists = false };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[NominationService] Session REST: {ex.Message}");
        }

        try
        {
            var snap = await _db.GetDocument(path)
                .GetDocumentSnapshotAsync<Dictionary<string, object>>()
                .ConfigureAwait(false);
            FirestoreUsageMeter.RecordSdk("Get", "Session");
            return SessionState.FromSdk(snap?.Data);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[NominationService] Session SDK: {ex.Message}");
            return null;
        }
    }

    private async Task<bool> ReadClosedAsync(string teamId, string sessionId, CancellationToken ct)
    {
        var path = NominationsMetaPath(teamId, sessionId);
        try
        {
            var (found, doc) = await GetDocViaRestAsync(path, ct).ConfigureAwait(false);
            return found && ReadRestBool(doc, "closed");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[NominationService] Meta REST: {ex.Message}");
        }
        var snap = await _db.GetDocument(path).GetDocumentSnapshotAsync<Dictionary<string, object>>().ConfigureAwait(false);
        return ReadSdkBool(snap?.Data, "closed");
    }

    /// <summary>
    /// The caller's own vote (0 or 1 items): REST primary, SDK fallback. Members use this
    /// while voting is open instead of listing every vote.
    /// </summary>
    private async Task<List<NominationVote>> ReadMyVoteAsync(
        string teamId, string sessionId, string uid, CancellationToken ct)
    {
        var path = NominationPath(teamId, sessionId, uid);
        try
        {
            var (found, doc) = await GetDocViaRestAsync(path, ct).ConfigureAwait(false);
            if (!found || doc is not { } d || !d.TryGetProperty("fields", out var f)) return [];
            return [new NominationVote(uid, ReadRestInt(f, "nomineeSlotId"),
                ReadRestString(f, "nomineeName"), ReadRestTimestamp(f, "timestamp"))];
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[NominationService] My vote REST: {ex.Message}");
        }

        var snap = await _db.GetDocument(path).GetDocumentSnapshotAsync<Dictionary<string, object>>().ConfigureAwait(false);
        FirestoreUsageMeter.RecordSdk("Get", "Nominations");
        if (snap?.Data is not { Count: > 0 } data) return [];
        return [new NominationVote(uid, ReadSdkInt(data, "nomineeSlotId"),
            ReadSdkString(data, "nomineeName"), ReadSdkTimestamp(data, "timestamp"))];
    }

    /// <summary>
    /// Authoritative Admin check: the caller's members/{uid} doc has role "admin", or the
    /// caller created the team (metadata/info.createdBy) — the same test the rules use. If it can't be read, treat the caller as a member so
    /// the live tally is never shown by mistake.
    /// </summary>
    private async Task<bool> IsViewerAdminAsync(string teamId, string uid, CancellationToken ct)
    {
        try
        {
            var (found, doc) = await GetDocViaRestAsync($"teams/{teamId}/members/{uid}", ct).ConfigureAwait(false);
            var fields = found && doc is { } d && d.TryGetProperty("fields", out var f) ? f : default;
            if (fields.ValueKind == JsonValueKind.Object
                && string.Equals(ReadRestString(fields, "role"), "admin", StringComparison.OrdinalIgnoreCase))
                return true;

            // The team creator (metadata/info.createdBy) counts as an Admin, as in the rules.
            var (hasMeta, meta) = await GetDocViaRestAsync($"teams/{teamId}/metadata/info", ct).ConfigureAwait(false);
            var metaFields = hasMeta && meta is { } m && m.TryGetProperty("fields", out var mf) ? mf : default;
            return metaFields.ValueKind == JsonValueKind.Object
                   && string.Equals(ReadRestString(metaFields, "createdBy"), uid, StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            Log($"Role REST failed: {ex.Message}");
        }

        try
        {
            var snap = await _db.GetDocument($"teams/{teamId}/members/{uid}")
                .GetDocumentSnapshotAsync<Dictionary<string, object>>().ConfigureAwait(false);
            FirestoreUsageMeter.RecordSdk("Get", "Member");
            return string.Equals(ReadSdkString(snap?.Data, "role"), "admin", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log($"Role SDK failed: {ex.Message}; treating as member");
            return false;
        }
    }

    private async Task<List<NominationVote>> ListVotesViaRestAsync(string teamId, string sessionId, CancellationToken ct)
    {
        var token = await GetTokenAsync().ConfigureAwait(false)
                    ?? throw new InvalidOperationException("No Firebase id token");
        var url = RestDocUrl(NominationsCollectionPath(teamId, sessionId)) + "?pageSize=300";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await RestHttp.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        FirestoreUsageMeter.RecordRestBody("GET", url, body);
        if (resp.StatusCode == HttpStatusCode.NotFound) return [];
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Nominations list {(int)resp.StatusCode}", null, resp.StatusCode);

        var list = new List<NominationVote>();
        using var json = JsonDocument.Parse(body);
        if (!json.RootElement.TryGetProperty("documents", out var documents)) return list;
        foreach (var doc in documents.EnumerateArray())
        {
            if (!doc.TryGetProperty("fields", out var f)) continue;
            var name = doc.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var id = name.Contains('/') ? name[(name.LastIndexOf('/') + 1)..] : ReadRestString(f, "nominatorId");
            list.Add(new NominationVote(id, ReadRestInt(f, "nomineeSlotId"),
                ReadRestString(f, "nomineeName"), ReadRestTimestamp(f, "timestamp")));
        }
        return list;
    }

    private async Task<IReadOnlyList<PlayerSnapshot>?> LoadRosterIfNeededAsync(string teamId, GameSession? session)
    {
        // v2 sessions carry their own squad snapshot.
        if (session?.Summary?.PlayerStats?.Any(p => p.SlotId > 0) == true)
            return null;
        try
        {
            var snap = await _roster.LoadAsync(teamId, preferCloud: true).ConfigureAwait(false);
            return snap?.Players;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[NominationService] Roster fallback: {ex.Message}");
            return null;
        }
    }

    // ── REST plumbing ─────────────────────────────────────────────────────

    private async Task<string?> GetTokenAsync()
    {
        var t = await _auth.GetIdTokenAsync(forceRefresh: false).ConfigureAwait(false);
        if (string.IsNullOrEmpty(t))
            t = await _auth.GetIdTokenAsync(forceRefresh: true).ConfigureAwait(false);
        return string.IsNullOrEmpty(t) ? null : t;
    }

    private async Task<(bool Found, JsonElement? Doc)> GetDocViaRestAsync(string path, CancellationToken ct)
    {
        var token = await GetTokenAsync().ConfigureAwait(false)
                    ?? throw new InvalidOperationException("No Firebase id token");
        var url = RestDocUrl(path);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await RestHttp.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            FirestoreUsageMeter.RecordRest("GET", url, 0);
            return (false, null);
        }
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        FirestoreUsageMeter.RecordRestBody("GET", url, body);
        if (!resp.IsSuccessStatusCode)
            throw FirestoreRestException.From($"GET {path}", resp.StatusCode, body);
        using var json = JsonDocument.Parse(body);
        return (true, json.RootElement.Clone());
    }

    /// <summary>batchGet. Missing docs map to null; found docs to the document JSON.</summary>
    private static async Task<Dictionary<string, JsonElement?>> BatchGetAsync(
        string token, IReadOnlyList<string> paths, CancellationToken ct)
    {
        var url = $"{RestBase}:batchGet";
        var payload = JsonSerializer.Serialize(new { documents = paths.Select(FullName).ToArray() });
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await RestHttp.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        FirestoreUsageMeter.RecordRestBody("POST", url, body);
        if (!resp.IsSuccessStatusCode)
            throw FirestoreRestException.From("batchGet", resp.StatusCode, body);

        var result = paths.ToDictionary(p => p, _ => (JsonElement?)null, StringComparer.Ordinal);
        using var json = JsonDocument.Parse(body);
        foreach (var item in json.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("found", out var found)) continue;
            var name = found.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var path = name.StartsWith(DocumentsRoot + "/", StringComparison.Ordinal)
                ? name[(DocumentsRoot.Length + 1)..]
                : name;
            if (result.ContainsKey(path))
                result[path] = found.Clone();
        }
        return result;
    }

    private static async Task CommitAsync(string token, string votePath, PendingVote vote, CancellationToken ct)
    {
        var url = $"{RestBase}:commit";
        var fields = new Dictionary<string, object>
        {
            ["nominatorId"] = new Dictionary<string, object> { ["stringValue"] = vote.Uid },
            ["nominatorDisplayName"] = new Dictionary<string, object> { ["stringValue"] = vote.DisplayName },
            ["nomineeSlotId"] = new Dictionary<string, object> { ["integerValue"] = vote.SlotId.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            ["nomineeName"] = new Dictionary<string, object> { ["stringValue"] = vote.NomineeName },
            ["timestamp"] = new Dictionary<string, object> { ["timestampValue"] = RestTimestamp(DateTimeOffset.UtcNow) }
        };
        // Full-document update (no mask): overwrites the member's previous vote.
        var payload = JsonSerializer.Serialize(new
        {
            writes = new object[] { new { update = new { name = FullName(votePath), fields } } }
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await RestHttp.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        FirestoreUsageMeter.RecordRestBody("POST", url, body);
        if (!resp.IsSuccessStatusCode)
            throw FirestoreRestException.From("commit", resp.StatusCode, body);
    }

    /// <summary>A Firestore REST error with the HTTP status and the server's status/message.</summary>
    private sealed class FirestoreRestException : HttpRequestException
    {
        public string Code { get; }

        private FirestoreRestException(string message, HttpStatusCode status, string code)
            : base(message, null, status) => Code = code;

        public static FirestoreRestException From(string op, HttpStatusCode status, string body)
        {
            var code = ((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var detail = "";
            try
            {
                using var json = JsonDocument.Parse(body);
                var err = json.RootElement.ValueKind == JsonValueKind.Array && json.RootElement.GetArrayLength() > 0
                    ? json.RootElement[0].GetProperty("error")
                    : json.RootElement.GetProperty("error");
                if (err.TryGetProperty("status", out var st)) code = st.GetString() ?? code;
                if (err.TryGetProperty("message", out var m)) detail = m.GetString() ?? "";
            }
            catch { detail = body.Length > 200 ? body[..200] : body; }
            return new FirestoreRestException($"{op} {(int)status} {code}: {detail}", status, code);
        }
    }

    /// <summary>Debug output plus the device log (Console), so TestFlight issues show in Xcode/Console.app.</summary>
    private static void Log(string message)
    {
        var line = $"[NominationService] {message}";
        System.Diagnostics.Debug.WriteLine(line);
        Console.WriteLine(line);
    }

    private static bool LooksPermissionDenied(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            var msg = e.Message ?? "";
            if (msg.Contains("PERMISSION_DENIED", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("permission", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("insufficient", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // ── Value helpers ─────────────────────────────────────────────────────

    private static string RestTimestamp(DateTimeOffset t)
        => t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private static string ReadRestString(JsonElement fields, string name)
        => fields.TryGetProperty(name, out var f) && f.TryGetProperty("stringValue", out var v) ? v.GetString() ?? "" : "";

    private static int ReadRestInt(JsonElement fields, string name)
    {
        if (!fields.TryGetProperty(name, out var f)) return 0;
        if (f.TryGetProperty("integerValue", out var iv)
            && int.TryParse(iv.ValueKind == JsonValueKind.String ? iv.GetString() : iv.GetRawText(), out var i))
            return i;
        if (f.TryGetProperty("doubleValue", out var dv) && dv.TryGetDouble(out var d))
            return (int)d;
        return 0;
    }

    private static bool ReadRestBool(JsonElement? doc, string name)
    {
        if (doc is not { } d) return false;
        var fields = d.TryGetProperty("fields", out var f) ? f : d;
        return fields.ValueKind == JsonValueKind.Object
               && fields.TryGetProperty(name, out var p)
               && p.TryGetProperty("booleanValue", out var b)
               && b.ValueKind == JsonValueKind.True;
    }

    private static DateTimeOffset? ReadRestTimestamp(JsonElement fields, string name)
    {
        if (!fields.TryGetProperty(name, out var f)) return null;
        var raw = f.TryGetProperty("timestampValue", out var tv) ? tv.GetString()
                : f.TryGetProperty("stringValue", out var sv) ? sv.GetString()
                : null;
        return raw is not null && DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var dto)
            ? dto.ToUniversalTime()
            : null;
    }

    private static string ReadSdkString(IDictionary<string, object>? d, string key)
        => d is not null && d.TryGetValue(key, out var v) && v is not null ? v.ToString() ?? "" : "";

    private static int ReadSdkInt(IDictionary<string, object>? d, string key)
    {
        if (d is null || !d.TryGetValue(key, out var v) || v is null) return 0;
        try { return Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture); }
        catch { return int.TryParse(v.ToString(), out var i) ? i : 0; }
    }

    private static bool ReadSdkBool(IDictionary<string, object>? d, string key)
    {
        if (d is null || !d.TryGetValue(key, out var v) || v is null) return false;
        return v is bool b ? b : bool.TryParse(v.ToString(), out var p) && p;
    }

    private static DateTimeOffset? ReadSdkTimestamp(IDictionary<string, object>? d, string key)
    {
        if (d is null || !d.TryGetValue(key, out var v) || v is null) return null;
        switch (v)
        {
            case DateTimeOffset dto: return dto.ToUniversalTime();
            case DateTime dt:
                return new DateTimeOffset(dt.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(dt, DateTimeKind.Utc)
                    : dt.ToUniversalTime());
            case string s when DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var p):
                return p.ToUniversalTime();
        }
        try
        {
            var t = v.GetType();
            foreach (var name in new[] { "ToDateTimeOffset", "ToDateTime", "ToDate" })
            {
                var m = t.GetMethod(name, Type.EmptyTypes);
                switch (m?.Invoke(v, null))
                {
                    case DateTimeOffset dto2: return dto2.ToUniversalTime();
                    case DateTime dt2: return new DateTimeOffset(DateTime.SpecifyKind(dt2, DateTimeKind.Utc));
                }
            }
            if ((t.GetProperty("Seconds") ?? t.GetProperty("seconds"))?.GetValue(v) is { } sec)
                return DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(sec, System.Globalization.CultureInfo.InvariantCulture));
        }
        catch { /* ignore */ }
        return null;
    }

    private static bool LooksOffline(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            // An HTTP status means the server was reached; only transport failures count as offline.
            if (e is HttpRequestException { StatusCode: null } or System.Net.Sockets.SocketException or TimeoutException)
                return true;
            var msg = e.Message ?? "";
            if (msg.Contains("offline", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("UNAVAILABLE", StringComparison.Ordinal)
                || msg.Contains("network", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool IsLocalOnlyTeam(string? teamId) =>
        string.IsNullOrWhiteSpace(teamId)
        || teamId.StartsWith("local_", StringComparison.Ordinal)
        || string.Equals(Preferences.Get("team_mode", string.Empty), "local", StringComparison.Ordinal);
}
