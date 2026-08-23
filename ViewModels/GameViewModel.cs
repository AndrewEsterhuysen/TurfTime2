using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using TurfTime2.Helpers;
using TurfTime2.Models;
using TurfTime2.Services;

namespace TurfTime2.ViewModels;

/// <summary>
/// Central ViewModel for the Game screen.
/// Owns all game state: roster, timers, rotation, view mode, scores, session logging.
/// Designed to be fully testable — no MAUI UI dependencies.
/// </summary>
public sealed class GameViewModel : INotifyPropertyChanged, IDisposable
{
    private const string DemoTeamId = "local_demo_team";
    private const int DemoCountdownSeconds = 20;
    private const string StartConfigurationKeyPrefix = "team_start_configuration_v1_";
    // ── Dependencies ──────────────────────────────────────────────────────
    private readonly IGameTimerService    _timer;
    private readonly IGameLoggerService   _logger;
    private readonly ICloudRosterService  _cloud;

    // ── Observable collections ────────────────────────────────────────────
    public ObservableCollection<Player>      Players      { get; } = [];
    public ObservableCollection<RotationPair> RotationPairs { get; } = [];

    /// <summary>
    /// Flat list shown in the swipeable roster.  Contains <see cref="Player"/>
    /// items for active players, then optionally an <see cref="InactiveGroupHeader"/>
    /// followed by inactive players if the group is expanded.
    /// </summary>
    public ObservableCollection<object> DisplayItems { get; } = [];

    /// <summary>Field View: outfield players on the pitch (Field only — goalie is separate).</summary>
    public ObservableCollection<Player> FieldBandPlayers { get; } = [];

    /// <summary>Field View: fixed 4×4 pitch cells (1–16) for formation placement.</summary>
    public ObservableCollection<FieldCellSlot> FieldGridCells { get; } = [];

    /// <summary>Field View: goalie token(s) fixed just above the field/bench divider.</summary>
    public ObservableCollection<Player> GoalieBandPlayers { get; } = [];

    /// <summary>Field View: players on the bench. Inactive excluded.</summary>
    public ObservableCollection<Player> BenchBandPlayers { get; } = [];

    private Player? _unpositionedStackTop;

    /// <summary>
    /// Top of the unpositioned (Position=None) stack on Field View — only this chip is shown.
    /// </summary>
    public Player? UnpositionedStackTop
    {
        get => _unpositionedStackTop;
        private set
        {
            if (ReferenceEquals(_unpositionedStackTop, value)) return;
            _unpositionedStackTop = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasUnpositionedStack));
        }
    }

    public bool HasUnpositionedStack => UnpositionedStackTop is not null;

    /// <summary>True when the roster has no items to show; drives the empty-state label.</summary>
    public bool IsRosterEmpty => DisplayItems.Count == 0;

    /// <summary>True when Field View pitch has no outfield or goalie tokens.</summary>
    public bool IsFieldBandEmpty => FieldBandPlayers.Count == 0 && GoalieBandPlayers.Count == 0;

    /// <summary>True when the 4×4 outfield grid has no Field players.</summary>
    public bool IsOutfieldBandEmpty => FieldBandPlayers.Count == 0;

    /// <summary>True when the goalie allocation zone has no goalie.</summary>
    public bool IsGoalieBandEmpty => GoalieBandPlayers.Count == 0;

    /// <summary>True when Field View bench has no bench tokens.</summary>
    public bool IsBenchBandEmpty => BenchBandPlayers.Count == 0;

    // Singleton header object so bindings survive list rebuilds.
    private readonly InactiveGroupHeader _inactiveHeader = new();

    // ── Rotation FIFO pointers (mirrors JS lastFieldIdx / lastBenchIdx) ───
    private int _lastFieldIdx = -1;
    private int _lastBenchIdx = -1;

    // ── Rotation FIFO queues ──────────────────────────────────────────────
    // Seeded by the automatic algorithm at game start and after each Rotate.
    // Manual taps modify the queues in-place: a de-selected slot is replaced
    // with null so subsequent slots retain their original queue positions.
    // Null slots are skipped during rotation execution and display.
    private readonly Queue<int?> _manualFieldQueue = new();
    private readonly Queue<int?> _manualBenchQueue = new();

    // ── Bindable state ────────────────────────────────────────────────────
    private TeamViewMode _viewMode = TeamViewMode.Swipeable;
    private int          _rotationCount = 1;
    private int          _rotationStyle = 1;
    private int          _teamAScore;
    private int          _teamBScore;
    private bool         _rotationDue;
    private bool         _rotationWarning;
    private bool         _showInactivePlayers;
    private string?      _userRole;   // "admin" | "member" | null (cloud role)
    private bool         _sessionViewOnly; // local watch-only; does not demote cloud admin
    private string       _currentTeamId = string.Empty;
    private string       _teamName      = string.Empty;
    private bool         _initialArrangementDone;

    // ── Single-controller lock (shared multi-admin) ───────────────────────
    private string _myUid = "";
    private string _controllerUid = "";
    private string _controllerDisplayName = "";
    private string _controlRequestUid = "";
    private string _controlRequestDisplayName = "";
    private string _controlRequestId = "";
    private string _lastShownControlRequestId = "";
    private bool   _forceLockedByController; // true when another admin holds control
    /// <summary>After the controller hydrates once from cloud, further snaps only update control fields.</summary>
    private bool   _controllerHydrated;
    private DateTimeOffset _controllerHeartbeatUtc = DateTimeOffset.MinValue;
    private System.Threading.Timer? _controllerHeartbeatTimer;
    private int _heartbeatInFlight;

    /// <summary>
    /// How often the controlling device pings the cloud (online signal for the server).
    /// Auto-release is performed by Cloud Function <c>releaseStaleGameControllers</c>, not peers.
    /// Keep interval well under the server stale window (90s).
    /// </summary>
    private static readonly TimeSpan ControllerHeartbeatInterval = TimeSpan.FromSeconds(45);

    /// <summary>Live cloud roster listener (members). Short recovery pulls only — no continuous poll.</summary>
    private IDisposable? _rosterWatch;
    private CancellationTokenSource? _memberPollCts;
    private DateTimeOffset _lastAppliedCloudUtc = DateTimeOffset.MinValue;
    private bool _cloudMirrorActive;
    /// <summary>
    /// Last countdown remaining observed from cloud (view-only). Used to detect a real admin
    /// Rotate (jump from mid/low cycle back to full preset) vs stale near-full cloud values
    /// that previously reset the follower countdown every ~5–6s.
    /// </summary>
    private int _lastCloudCountdownRemaining = int.MinValue;

    // ── Timer display properties (formatted strings for binding) ──────────
    private string _matchTimeDisplay    = "90 min";
    private string _countdownDisplay    = "15:00";
    private string _matchTimeLabelText  = "Match Time";
    private string _startButtonText     = "Start";
    private string _rotateButtonText    = "Rotate 1";

    // ── Timer overdue flags (true when the timer has reached/passed zero) ─
    private bool _matchTimerOverdue;
    private bool _countdownOverdue;

    // ── Constructor ───────────────────────────────────────────────────────

    public GameViewModel(
        IGameTimerService   timer,
        IGameLoggerService  logger,
        ICloudRosterService cloud)
    {
        _timer  = timer;
        _logger = logger;
        _cloud  = cloud;

        // Restore countdown preset persisted from the previous session.
        var savedCountdown = Preferences.Get("game.countdownPresetSeconds", 0);
        if (savedCountdown > 0)
        {
            _timer.CountdownPresetSeconds = savedCountdown;
            _timer.ResetCountdown(continueRunning: false);
        }

        // Build default 16-player roster (#01 Player … #16 Player for unique Field View tokens)
        for (int i = 1; i <= 16; i++)
            Players.Add(new Player { SlotId = i, Name = Player.DefaultName(i) });

        for (int cell = FieldGrid.MinCell; cell <= FieldGrid.MaxCell; cell++)
            FieldGridCells.Add(new FieldCellSlot(cell));

        // Wire timer events
        _timer.MatchTickOccurred      += OnMatchTick;
        _timer.CountdownTickOccurred  += OnCountdownTick;
        _timer.RotationDue            += OnRotationDue;
        _timer.RotationWarning        += OnRotationWarning;
        _timer.HalfTimeReached        += OnHalfTimeReached;
        _timer.RegulationTimeEnded    += OnRegulationTimeEnded;

        UpdateTimerDisplays();
        UpdateRotateButtonText();
        UpdateStartButtonState();
        RefreshDisplayItems();
    }

    // ── INotifyPropertyChanged ────────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    // ── Public properties ─────────────────────────────────────────────────

    public GamePhase Phase => _timer.Phase;

    public TeamViewMode ViewMode
    {
        get => _viewMode;
        set
        {
            if (Set(ref _viewMode, value))
                RefreshRotationPairs();
        }
    }

    public int RotationCount
    {
        get => _rotationCount;
        set
        {
            if (Set(ref _rotationCount, Math.Max(1, value)))
            {
                UpdateRotateButtonText();
                MarkNextPlayers();
                RefreshRotationPairs();
            }
        }
    }

    public int RotationStyle
    {
        get => _rotationStyle;
        set => Set(ref _rotationStyle, value);
    }

    public string TeamName
    {
        get => _teamName;
        private set => Set(ref _teamName, value);
    }

    public int TeamAScore
    {
        get => _teamAScore;
        private set => Set(ref _teamAScore, value);
    }

    public int TeamBScore
    {
        get => _teamBScore;
        private set => Set(ref _teamBScore, value);
    }

    public bool RotationDue
    {
        get => _rotationDue;
        set => Set(ref _rotationDue, value);
    }

    public bool RotationWarning
    {
        get => _rotationWarning;
        set => Set(ref _rotationWarning, value);
    }

    /// <summary>True while the match timer is at or past zero (extra time).</summary>
    public bool MatchTimerOverdue
    {
        get => _matchTimerOverdue;
        private set => Set(ref _matchTimerOverdue, value);
    }

    /// <summary>True while the rotation countdown is at or past zero.</summary>
    public bool CountdownOverdue
    {
        get => _countdownOverdue;
        private set => Set(ref _countdownOverdue, value);
    }

    public bool ShowInactivePlayers
    {
        get => _showInactivePlayers;
        set => Set(ref _showInactivePlayers, value);
    }

    /// <summary>Toggles the inactive group between expanded and collapsed.</summary>
    public void ToggleInactiveExpanded()
    {
        _inactiveHeader.IsExpanded = !_inactiveHeader.IsExpanded;
        RefreshDisplayItems();
    }

    /// <summary>Cloud role is admin (ignores temporary Watch Only / controller lock on this device).</summary>
    public bool IsCloudAdmin => !string.Equals(_userRole, "member", StringComparison.Ordinal);

    /// <summary>
    /// Effective member: true cloud members, voluntary Watch Only, or locked out by another controller.
    /// </summary>
    public bool IsMember =>
        string.Equals(_userRole, "member", StringComparison.Ordinal)
        || _sessionViewOnly
        || _forceLockedByController;

    /// <summary>Effective admin controls (false while Watch Only or locked by another controller).</summary>
    public bool IsAdmin => !IsMember;

    public bool IsSessionViewOnly => _sessionViewOnly;

    public bool IsForcedControllerLock => _forceLockedByController;

    public bool IsGameController =>
        IsCloudAdmin
        && !string.IsNullOrEmpty(_myUid)
        && !string.IsNullOrEmpty(_controllerUid)
        && string.Equals(_myUid, _controllerUid, StringComparison.Ordinal);

    public bool HasActiveController => !string.IsNullOrEmpty(_controllerUid);

    /// <summary>Live match (not setup / finished) on a shared team — needs exactly one controller.</summary>
    public bool IsSharedLiveMatch =>
        IsCloudAdmin
        && !string.IsNullOrWhiteSpace(_currentTeamId)
        && !_currentTeamId.StartsWith("local_", StringComparison.Ordinal)
        && !string.Equals(Preferences.Get("team_mode", string.Empty), "local", StringComparison.Ordinal)
        && Phase is not GamePhase.Setup and not GamePhase.Finished;

    public string ControllerDisplayName =>
        string.IsNullOrWhiteSpace(_controllerDisplayName) ? "Admin" : _controllerDisplayName;

    /// <summary>
    /// Voluntary Watch Only only when no one holds match control (setup / idle).
    /// During a controlled match, use Request control instead.
    /// </summary>
    public bool CanUseSessionViewOnly =>
        IsCloudAdmin
        && !string.IsNullOrWhiteSpace(_currentTeamId)
        && !_currentTeamId.StartsWith("local_", StringComparison.Ordinal)
        && !string.Equals(Preferences.Get("team_mode", string.Empty), "local", StringComparison.Ordinal)
        && !HasActiveController
        && Phase is GamePhase.Setup or GamePhase.Finished;

    public string ViewOnlyBannerText
    {
        get
        {
            if (_forceLockedByController && IsCloudAdmin)
            {
                // Vacant seat after Relinquish / server auto-release
                if (!HasActiveController && IsSharedLiveMatch)
                    return "📖 No controller · Tap to take control";

                var who = ControllerDisplayName;
                if (!string.IsNullOrEmpty(_controlRequestUid)
                    && string.Equals(_controlRequestUid, _myUid, StringComparison.Ordinal))
                    return $"📖 {who} started game — request sent…";
                return $"📖 {who} started game · Request control";
            }

            if (_sessionViewOnly)
                return "📖 WATCH ONLY (this device) — another Admin can run the game";

            if (HasActiveController && !IsCloudAdmin)
                return $"📖 {ControllerDisplayName} started game — view only";

            return "📖 VIEW-ONLY MODE — Team Admin controls the game";
        }
    }

    /// <summary>True when a locked co-admin can tap the banner to request control from the current controller.</summary>
    public bool CanRequestControl =>
        IsCloudAdmin
        && _forceLockedByController
        && HasActiveController
        && !string.Equals(_controlRequestUid, _myUid, StringComparison.Ordinal);

    /// <summary>True when the match is live but no controller holds the seat — tap banner to claim.</summary>
    public bool CanTakeVacantControl =>
        IsCloudAdmin
        && _forceLockedByController
        && !HasActiveController
        && IsSharedLiveMatch;

    public string SessionViewOnlyToggleText =>
        _sessionViewOnly ? "Take Control" : "Watch Only";

    /// <summary>
    /// Raised on the controller's device when another Admin requests control.
    /// Args: (requesterDisplayName, requestId).
    /// </summary>
    public event EventHandler<(string RequesterName, string RequestId)>? ControlRequestReceived;

    public bool ScoresVisible => Phase != GamePhase.Setup && Phase != GamePhase.Finished;

    public string MatchTimeDisplay
    {
        get => _matchTimeDisplay;
        private set => Set(ref _matchTimeDisplay, value);
    }

    public string CountdownDisplay
    {
        get => _countdownDisplay;
        private set => Set(ref _countdownDisplay, value);
    }

    public string MatchTimeLabelText
    {
        get => _matchTimeLabelText;
        private set => Set(ref _matchTimeLabelText, value);
    }

    public string StartButtonText
    {
        get => _startButtonText;
        private set => Set(ref _startButtonText, value);
    }

    public string RotateButtonText
    {
        get => _rotateButtonText;
        private set => Set(ref _rotateButtonText, value);
    }

    public int ActivePlayerCount =>
        Players.Count(p => p.Position != PlayerPosition.Inactive);

    public int InactivePlayerCount =>
        Players.Count(p => p.Position == PlayerPosition.Inactive);

    // ── Initialisation ────────────────────────────────────────────────────

    /// <summary>Load saved roster + timer state for the current team.</summary>
    public async Task InitialiseAsync(string teamId, string? userRole)
    {
        StopCloudMirror();

        _currentTeamId = teamId;
        _userRole      = userRole;
        TeamName       = Preferences.Get("team_name", string.Empty);
        _lastAppliedCloudUtc = DateTimeOffset.MinValue;
        _lastShownControlRequestId = "";
        _lastCloudCountdownRemaining = int.MinValue;
        _controllerHydrated = false;
        ClearLocalControlState();

        // Restore device-local Watch Only for shared cloud admins (does not change cloud role).
        _sessionViewOnly = IsCloudAdmin
            && !teamId.StartsWith("local_", StringComparison.Ordinal)
            && Preferences.Get(SessionViewOnlyKey(teamId), false);

        // Always reset to a clean default roster before loading the new team's
        // snapshot. Without this, switching to a brand-new team (which has no
        // saved snapshot) leaves the previous team's players visible.
        ResetToDefaultRoster();

        bool isLocal = teamId.StartsWith("local_", StringComparison.Ordinal);

        if (!isLocal)
        {
            // Pre-warm Firebase auth tokens in both services concurrently so the
            // first swipe never has to wait for an anonymous sign-up round trip (~800 ms).
            // Skipped for local teams — they never touch cloud/Firebase.
            _ = _cloud.WarmUpAsync();
            _ = _logger.WarmUpAsync();
            try
            {
                _myUid = await _cloud.GetSignedInUidAsync().ConfigureAwait(false) ?? "";
                if (!string.IsNullOrEmpty(_myUid))
                    Preferences.Set("chat_user_id", _myUid);
            }
            catch { /* best-effort */ }
        }

        // Shared teams: prefer cloud so controller lock + live match state are current.
        var preferCloud = IsMember || !isLocal;
        var snapshot = await _cloud.LoadAsync(teamId, preferCloud: preferCloud).ConfigureAwait(false);

        // ApplySnapshot mutates ObservableCollections bound to CollectionView / BindableLayout.
        // After ConfigureAwait(false) we are often off the UI thread — must marshal or iOS aborts
        // with UIKitThreadAccessException when Field View tokens are created.
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            if (snapshot is not null)
                ApplySnapshot(snapshot);

            // Start-configuration is admin-local only (field/bench layout remembered on this device).
            // Members must not re-apply empty local layout over the admin's cloud roster.
            if (!IsMember)
                RestoreStartConfigurationIfAvailable();
            else
                ViewMode = TeamViewMode.Field;

            NotifyRoleProperties();
            UpdateStartButtonState();
        }).ConfigureAwait(false);

        // Shared: always watch (members mirror; controllers receive control-request patches).
        if (!isLocal)
            StartCloudMirror(teamId);
    }

    private void ClearLocalControlState()
    {
        _controllerUid = "";
        _controllerDisplayName = "";
        _controlRequestUid = "";
        _controlRequestDisplayName = "";
        _controlRequestId = "";
        _forceLockedByController = false;
    }

    private void NotifyRoleProperties()
    {
        OnPropertyChanged(nameof(IsCloudAdmin));
        OnPropertyChanged(nameof(IsMember));
        OnPropertyChanged(nameof(IsAdmin));
        OnPropertyChanged(nameof(IsSessionViewOnly));
        OnPropertyChanged(nameof(IsForcedControllerLock));
        OnPropertyChanged(nameof(IsGameController));
        OnPropertyChanged(nameof(HasActiveController));
        OnPropertyChanged(nameof(ControllerDisplayName));
        OnPropertyChanged(nameof(CanUseSessionViewOnly));
        OnPropertyChanged(nameof(CanRequestControl));
        OnPropertyChanged(nameof(CanTakeVacantControl));
        OnPropertyChanged(nameof(IsSharedLiveMatch));
        OnPropertyChanged(nameof(ViewOnlyBannerText));
        OnPropertyChanged(nameof(SessionViewOnlyToggleText));
        OnPropertyChanged(nameof(CanStart));
    }

    private static string SessionViewOnlyKey(string teamId) => $"session_view_only_{teamId}";

    /// <summary>
    /// Cloud admins only: temporarily run as view-only on this device so a co-admin can control
    /// the match without risk of clashing writes. Does not change cloud role.
    /// </summary>
    public async Task SetSessionViewOnlyAsync(bool enabled)
    {
        if (!IsCloudAdmin) return;
        if (string.IsNullOrWhiteSpace(_currentTeamId)
            || _currentTeamId.StartsWith("local_", StringComparison.Ordinal))
            return;

        _sessionViewOnly = enabled;
        Preferences.Set(SessionViewOnlyKey(_currentTeamId), enabled);
        if (enabled)
            ViewMode = TeamViewMode.Field;
        NotifyRoleProperties();
        UpdateStartButtonState();

        System.Diagnostics.Debug.WriteLine(
            $"[GameViewModel] SessionViewOnly={enabled} team={_currentTeamId}");

        if (enabled)
        {
            try
            {
                var snap = await _cloud.LoadAsync(_currentTeamId, preferCloud: true).ConfigureAwait(false);
                if (snap is not null)
                    await MainThread.InvokeOnMainThreadAsync(() => ApplySnapshot(snap));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[GameViewModel] WatchOnly load: {ex.Message}");
            }

            StartCloudMirror(_currentTeamId);
        }
        else
        {
            // Leaving Watch Only → free admin. Keep the cloud mirror so another Admin's
            // Start still locks this device (do not StopCloudMirror).
            try
            {
                // Pull latest state written by the co-admin before resuming control.
                var snap = await _cloud.LoadAsync(_currentTeamId, preferCloud: true).ConfigureAwait(false);
                if (snap is not null)
                    await MainThread.InvokeOnMainThreadAsync(() => ApplySnapshot(snap));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[GameViewModel] TakeControl load: {ex.Message}");
            }

            if (!_cloudMirrorActive)
                StartCloudMirror(_currentTeamId);
        }
    }

    /// <summary>
    /// Stop Firestore listener for pure members when the page is hidden.
    /// Cloud Admins keep the listener so control requests / relinquish / server release still arrive.
    /// </summary>
    public void PauseCloudMirror()
    {
        if (IsCloudAdmin)
        {
            System.Diagnostics.Debug.WriteLine(
                "[GameViewModel] PauseCloudMirror skipped for cloud Admin (need control channel)");
            return;
        }
        StopCloudMirror();
    }

    /// <summary>
    /// Re-attach live mirror after resume / re-appear. Forces one cloud load first so a
    /// long-suspended iOS process does not stay stuck on a stale morning snapshot.
    /// </summary>
    public async Task ResumeCloudMirrorAsync()
    {
        // Members + free co-admins need mirror; live controller keeps it for control requests.
        if (string.IsNullOrWhiteSpace(_currentTeamId)) return;
        if (_currentTeamId.StartsWith("local_", StringComparison.Ordinal)) return;
        if (string.Equals(Preferences.Get("team_mode", string.Empty), "local", StringComparison.Ordinal))
            return;

        System.Diagnostics.Debug.WriteLine(
            $"[GameViewModel] ResumeCloudMirror team={_currentTeamId}");

        StopCloudMirror();
        try { await _cloud.WarmUpAsync().ConfigureAwait(false); }
        catch { /* best-effort */ }

        try
        {
            var snap = await _cloud.LoadAsync(_currentTeamId, preferCloud: true).ConfigureAwait(false);
            if (snap is not null && snap.Players.Count > 0)
            {
                await MainThread.InvokeOnMainThreadAsync(() => ApplySnapshot(snap));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[GameViewModel] ResumeCloudMirror load: {ex.Message}");
        }

        StartCloudMirror(_currentTeamId);
    }

    /// <summary>
    /// Members: Firestore snapshot listener on <c>teams/{id}/roster/data</c>.
    /// A few one-shot recovery pulls cover cold-start / empty SDK Data; no continuous poll.
    /// </summary>
    private void StartCloudMirror(string teamId)
    {
        if (_cloudMirrorActive && _rosterWatch is not null)
            return;

        StopCloudMirror();
        _cloudMirrorActive = true;

        _rosterWatch = _cloud.WatchRoster(teamId, snap =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                // Free co-admins (Setup + Watch Only button) must still receive Start/Reset/
                // controller claims. ApplySnapshot itself protects the live controller's local
                // timers after hydrate and only merges control-request fields for them.
                // Skip only strictly older snapshots. Equal timestamps re-apply after resume.
                // MinValue (unparseable timestamp) is never treated as "newer than everything".
                if (snap.LastModifiedUtc != DateTimeOffset.MinValue
                    && snap.LastModifiedUtc < _lastAppliedCloudUtc)
                    return;
                ApplySnapshot(snap);
            });
        });

        // Short recovery series only (not a forever loop): immediate + a few spaced pulls
        // while the listener attaches or until we have applied cloud data.
        _memberPollCts = new CancellationTokenSource();
        var token = _memberPollCts.Token;
        _ = Task.Run(async () =>
        {
            // Delays: 0s (immediate), then 5s, 15s, 30s — then stop. Listener owns ongoing sync.
            int[] delaysSeconds = [0, 5, 15, 30];
            foreach (var delay in delaysSeconds)
            {
                if (token.IsCancellationRequested) break;
                try
                {
                    if (delay > 0)
                        await Task.Delay(TimeSpan.FromSeconds(delay), token).ConfigureAwait(false);

                    var snap = await _cloud.LoadAsync(teamId, preferCloud: true).ConfigureAwait(false);
                    if (snap is null || snap.Players.Count == 0)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[GameViewModel] Recovery pull: no cloud players yet for {teamId}");
                        continue;
                    }

                    await MainThread.InvokeOnMainThreadAsync(() =>
                    {
                        // Same as watch: free co-admins need Start/controller after Reset.
                        System.Diagnostics.Debug.WriteLine(
                            $"[GameViewModel] Recovery pull apply players={snap.Players.Count} " +
                            $"lastMod={snap.LastModifiedUtc:o} member={IsMember} controller={IsGameController}");
                        ApplySnapshot(snap);
                    });
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[GameViewModel] Recovery pull: {ex.Message}");
                }
            }

            System.Diagnostics.Debug.WriteLine(
                $"[GameViewModel] Recovery pulls finished for {teamId} — listener-only thereafter");
        }, token);
    }

    private void StopCloudMirror()
    {
        _cloudMirrorActive = false;
        try { _rosterWatch?.Dispose(); } catch { /* ignore */ }
        _rosterWatch = null;
        try { _memberPollCts?.Cancel(); } catch { /* ignore */ }
        try { _memberPollCts?.Dispose(); } catch { /* ignore */ }
        _memberPollCts = null;
    }

    private void ResetToDefaultRoster()
    {
        Players.Clear();
        for (int i = 1; i <= 16; i++)
            Players.Add(new Player { SlotId = i, Name = Player.DefaultName(i) });

        TeamAScore = 0;
        TeamBScore = 0;
        // Reset timer first so Phase returns to Setup; the MatchDurationSeconds setter
        // only updates MatchRemainingSeconds when Phase == Setup, so if a previous game
        // was paused mid-match the displayed timer would keep the stale remaining value.
        _timer.Reset();
        _timer.MatchDurationSeconds   = 90 * 60;
        _timer.CountdownPresetSeconds = string.Equals(_currentTeamId, DemoTeamId, StringComparison.Ordinal)
            ? DemoCountdownSeconds
            : (Preferences.Get("game.countdownPresetSeconds", 0) is int s && s > 0 ? s : _timer.CountdownPresetSeconds);
        MatchTimerOverdue = false;
        CountdownOverdue  = false;
        RotationDue       = false;
        RotationWarning   = false;
        _rotationCount    = 1;
        _lastFieldIdx          = -1;
        _lastBenchIdx          = -1;
        _initialArrangementDone = false;
        _manualFieldQueue.Clear();
        _manualBenchQueue.Clear();

        UpdateTimerDisplays();
        UpdateRotateButtonText();
        UpdateStartButtonState();
        RefreshDisplayItems();
    }

    /// <summary>
    /// Immediately stops all running timers and resets all match state (scores, timers,
    /// rotation counters). Called before switching teams so no timer state bleeds across.
    /// </summary>
    public void ResetMatchState()
    {
        // Stop any running timers before resetting so tick callbacks don't fire during teardown.
        if (_timer.TimerRunning)
            _timer.PauseMatch();

        _timer.Reset();

        TeamAScore = 0;
        TeamBScore = 0;
        MatchTimerOverdue = false;
        CountdownOverdue  = false;
        RotationDue       = false;
        RotationWarning   = false;
        _rotationCount    = 1;
        _lastFieldIdx     = -1;
        _lastBenchIdx     = -1;
        _initialArrangementDone = false;
        _manualFieldQueue.Clear();
        _manualBenchQueue.Clear();

        UpdateTimerDisplays();
        UpdateRotateButtonText();
        UpdateStartButtonState();
    }

    // ── Game control ──────────────────────────────────────────────────────

    public void ToggleStartPause()
    {
        if (IsMember) return;

        switch (Phase)
        {
            case GamePhase.Ended:
                if (_timer.TimerRunning)
                {
                    _timer.PauseMatch();
                    EndGame();
                }
                return;

            case GamePhase.Finished:
                RestartGame();
                return;

            case GamePhase.HalfTime:
                if (_timer.TimerRunning)
                {
                    // User pressed "1/2 Time" — stop the timers for the break.
                    _timer.PauseMatch();
                    _logger.Log(GameEventType.HalfTime, "Half-time");
                    StartButtonText = "Resume";
                }
                else
                {
                    // User pressed "Resume" — start the second half.
                    _timer.StartSecondHalf();
                    _timer.ResumeMatch();
                    _timer.ResetCountdown(continueRunning: true);
                    _logger.Log(GameEventType.SecondHalfStarted, "Second half started");
                    StartButtonText = "Pause";
                }
                UpdateTimerLabelText();
                _ = ForceCloudSaveAsync(); // half-time pause / second-half start → members
                return;

            default:
                if (_timer.TimerRunning)
                    PauseGame();
                else
                    StartOrResumeGame();
                break;
        }
    }

    public void ExecuteRotations()
    {
        if (IsMember) return;

        var count = Math.Min(RotationCount,
            Math.Min(BenchCandidates().Count, FieldCandidates().Count));

        System.Diagnostics.Debug.WriteLine(
            $"[GameViewModel] 🔄 ExecuteRotations ─ swapping {count} player(s) | " +
            $"field-q=[{QueueString(_manualFieldQueue)}] | " +
            $"bench-q=[{QueueString(_manualBenchQueue)}]");

        for (int i = 0; i < count; i++)
            RotateOnce();

        // Re-seed rotation queues for the next rotation after each execution.
        SeedRotationQueues();

        System.Diagnostics.Debug.WriteLine(
            $"[GameViewModel] ✅ ExecuteRotations ─ done | next: " +
            $"field-q=[{QueueString(_manualFieldQueue)}] | " +
            $"bench-q=[{QueueString(_manualBenchQueue)}]");

        _timer.ResetCountdown(continueRunning: _timer.TimerRunning);
        MarkNextPlayers();
        RefreshRotationPairs();
        RefreshDisplayItems();   // rebuilds CollectionView rows → causes [DragRowHandler] + [PERF] entries
        _ = AutoSaveAsync();
    }

    /// <summary>
    /// Re-seeds the FIFO rotation buffers to match the current <see cref="RotationCount"/>.
    /// Call this whenever RotationCount changes mid-game so the queues reflect the new size.
    /// </summary>
    public void ReseedRotationQueues()
    {
        if (IsMember) return;
        System.Diagnostics.Debug.WriteLine(
            $"[GameViewModel] 🌱 ReseedRotationQueues — RotationCount={RotationCount}");
        SeedRotationQueues();
        MarkNextPlayers();
        RefreshRotationPairs();
        RefreshDisplayItems();
        System.Diagnostics.Debug.WriteLine(
            $"[GameViewModel] 🌱 ReseedRotationQueues — done | " +
            $"field-q=[{QueueString(_manualFieldQueue)}] | " +
            $"bench-q=[{QueueString(_manualBenchQueue)}]");
    }

    public void IncrementTeamAScore(string? scorer = null, string? assist = null)
    {
        if (IsMember) return;
        TeamAScore++;
        LogScoreEvent(GameEventType.ScoreUs, delta: +1, TeamAScore, TeamBScore, scorer, assist);
        _ = AutoSaveAsync();
    }

    public void IncrementTeamBScore(string? scorer = null, string? assist = null)
    {
        if (IsMember) return;
        TeamBScore++;
        LogScoreEvent(GameEventType.ScoreThem, delta: +1, TeamAScore, TeamBScore, scorer, assist);
        _ = AutoSaveAsync();
    }

    /// <summary>Decrement Us score, minimum 0.</summary>
    public void DecrementTeamAScore()
    {
        if (IsMember) return;
        if (TeamAScore > 0)
        {
            TeamAScore--;
            LogScoreEvent(GameEventType.ScoreUs, delta: -1, TeamAScore, TeamBScore);
            _ = AutoSaveAsync();
        }
    }

    /// <summary>Decrement Them score, minimum 0.</summary>
    public void DecrementTeamBScore()
    {
        if (IsMember) return;
        if (TeamBScore > 0)
        {
            TeamBScore--;
            LogScoreEvent(GameEventType.ScoreThem, delta: -1, TeamAScore, TeamBScore);
            _ = AutoSaveAsync();
        }
    }

    private void LogScoreEvent(GameEventType type, int delta, int usScore, int themScore, string? scorer = null, string? assist = null)
    {
        // Only log during an active game; ignore accidental taps in setup/finished state.
        if (Phase == GamePhase.Setup || Phase == GamePhase.Finished) return;

        var elapsedSeconds = _timer.MatchDurationSeconds - _timer.MatchRemainingSeconds;
        var half = Phase switch
        {
            GamePhase.FirstHalf  => "1st",
            GamePhase.HalfTime   => "HT",
            GamePhase.SecondHalf => "2nd",
            GamePhase.Ended      => "2nd",
            _                    => ""
        };
        var team  = type == GameEventType.ScoreUs ? "Us" : "Them";
        var elapsedMin = elapsedSeconds / 60;
        var elapsedSec = elapsedSeconds % 60;

        // Build simplified description (score shown in separate column)
        string description;
        if (delta > 0)
        {
            // Goal scored
            description = $"Goal: {team}";
            if (!string.IsNullOrEmpty(scorer))
            {
                description += $"\n{scorer}";
                if (!string.IsNullOrEmpty(assist))
                {
                    description += $", assisted-{assist}";
                }
            }
        }
        else
        {
            // Score corrected
            description = $"Corrected: {team}";
        }

        System.Diagnostics.Debug.WriteLine($"[LogScoreEvent] Phase={Phase} team={team} delta={delta} us={usScore} them={themScore} scorer={scorer} assist={assist} elapsed={elapsedSeconds}s sessionActive={_logger.CurrentSession is not null}");

        var details = new Dictionary<string, object?>
        {
            ["team"]           = team,
            ["delta"]          = delta,
            ["scoreUs"]        = usScore,
            ["scoreThem"]      = themScore,
            ["half"]           = half,
            ["elapsedSeconds"] = elapsedSeconds,
            ["elapsedDisplay"] = $"{half} {elapsedMin}:{elapsedSec:D2}"
        };

        if (!string.IsNullOrEmpty(scorer))
        {
            details["scorer"] = scorer;
        }
        if (!string.IsNullOrEmpty(assist))
        {
            details["assist"] = assist;
        }

        _logger.Log(type, description, details: details);
    }

    /// <summary>Get all players currently on field or as goalie for selection in goal details modal.</summary>
    public IReadOnlyList<Player> GetFieldPlayers()
    {
        return Players
            .Where(p => p.Position == PlayerPosition.Field || p.Position == PlayerPosition.Goalie)
            .ToList()
            .AsReadOnly();
    }

    /// <summary>Set a field player as the next to rotate out.</summary>
    public void SetNextFieldPlayer(Player player)
    {
        var idx = Players.IndexOf(player);
        if (idx < 0 || player.Position != PlayerPosition.Field) return;
        _lastFieldIdx = (idx - 1 + Players.Count) % Players.Count;
        MarkNextPlayers();
    }

    /// <summary>Set a bench player as the next to rotate in.</summary>
    public void SetNextBenchPlayer(Player player)
    {
        var idx = Players.IndexOf(player);
        if (idx < 0 || player.Position != PlayerPosition.Bench) return;
        _lastBenchIdx = (idx - 1 + Players.Count) % Players.Count;
        MarkNextPlayers();
    }

    /// <summary>
    /// Assigns a player to the manual next-rotation queue for their position group.
    /// Tapping a new player GROWS both queues by one (field + bench stay the same size).
    /// Tapping an already-queued player REMOVES them and shrinks both queues by one.
    /// <see cref="RotationCount"/> is updated to match the new queue size.
    /// </summary>
    public void TapPlayerQueue(Player player)
    {
        if (Phase == GamePhase.Setup || Phase == GamePhase.Finished) return;
        if (IsMember) return;

        var idx = Players.IndexOf(player);
        if (idx < 0) return;

        Queue<int?>? ownQueue = player.Position switch
        {
            PlayerPosition.Field => _manualFieldQueue,
            PlayerPosition.Bench => _manualBenchQueue,
            _                    => null
        };

        if (ownQueue is null) return; // goalie / inactive — no queue

        bool alreadyQueued = ownQueue.Any(s => s == idx);
        System.Diagnostics.Debug.WriteLine(
            $"[GameViewModel] 👆 TapPlayerQueue ENTER — {player.Name} ({player.Position}) " +
            $"idx={idx} alreadyQueued={alreadyQueued} " +
            $"field-q=[{QueueString(_manualFieldQueue)}] bench-q=[{QueueString(_manualBenchQueue)}] " +
            $"RotationCount={RotationCount}");

        var field = FieldCandidates();
        var bench = BenchCandidates();

        if (alreadyQueued)
        {
            // Remove the slot from the own queue (compact — no null).
            var trimmed = ownQueue.Where(s => s != idx).ToArray();
            ownQueue.Clear();
            foreach (var s in trimmed) ownQueue.Enqueue(s);

            // Shrink the opposite queue by trimming its tail by one.
            Queue<int?> otherQueue = player.Position == PlayerPosition.Field
                ? _manualBenchQueue : _manualFieldQueue;
            if (otherQueue.Count > 0)
            {
                var otherArr = otherQueue.ToArray()[..^1]; // drop last slot
                otherQueue.Clear();
                foreach (var s in otherArr) otherQueue.Enqueue(s);
            }

            // Clamp RotationCount down (min 1), skipping the property setter's side-effects.
            _rotationCount = Math.Max(1, _rotationCount - 1);
            UpdateRotateButtonText();
        }
        else
        {
            // Guard: cannot exceed bench player count (both sides cap at bench size).
            int maxSize = Math.Min(field.Count, bench.Count);
            if (ownQueue.Count >= maxSize)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[GameViewModel] 👆 TapPlayerQueue — skipped, already at max capacity ({maxSize})");
                return;
            }

            // Add the new player to their own queue.
            ownQueue.Enqueue(idx);

            // Grow the opposite queue by one auto-seeded slot.
            Queue<int?> otherQueue = player.Position == PlayerPosition.Field
                ? _manualBenchQueue : _manualFieldQueue;
            List<int> otherCandidates = player.Position == PlayerPosition.Field ? bench : field;
            int lastOtherIdx = player.Position == PlayerPosition.Field ? _lastBenchIdx : _lastFieldIdx;

            // Find the next candidate after whoever is currently at the tail of the other queue.
            int tailOffset = otherQueue.Count; // how many slots already filled
            var newOtherIdx = NextIndexFromWithOffset(otherCandidates, lastOtherIdx, tailOffset);
            if (newOtherIdx >= 0)
                otherQueue.Enqueue(newOtherIdx);

            // Grow RotationCount to match, skipping the property setter's side-effects.
            _rotationCount = Math.Min(_rotationCount + 1, maxSize);
            UpdateRotateButtonText();
        }

        System.Diagnostics.Debug.WriteLine(
            $"[GameViewModel] 👆 TapPlayerQueue EXIT — {player.Name} ({player.Position}) " +
            $"field-q=[{QueueString(_manualFieldQueue)}] bench-q=[{QueueString(_manualBenchQueue)}] " +
            $"RotationCount={RotationCount}");

        MarkNextPlayers();
    }

    public void SetPlayerPosition(Player player, PlayerPosition newPosition)
    {
        if (IsMember) return;

        var oldPosition = player.Position;
        if (oldPosition == newPosition)
        {
            // Same role: still ensure Field players have a cell when possible (Team swipe → Field).
            if (newPosition == PlayerPosition.Field && player.FieldCell is null)
            {
                player.FieldCell = FindFirstFreeFieldCell(except: player);
                AfterPositionMutation(player, oldPosition, newPosition);
            }
            return;
        }

#if DEBUG
        var sw = System.Diagnostics.Stopwatch.StartNew();
#endif

        // Enforce single-goalie rule
        if (newPosition == PlayerPosition.Goalie)
        {
            foreach (var p in Players.Where(p => p.Position == PlayerPosition.Goalie && p != player))
                p.Position = PlayerPosition.None;
        }

        player.Position = newPosition;

        if (newPosition == PlayerPosition.Field && player.FieldCell is null)
            player.FieldCell = FindFirstFreeFieldCell(except: player);
        // Leaving Field clears FieldCell via Player.Position setter.

        AfterPositionMutation(player, oldPosition, newPosition);

#if DEBUG
        sw.Stop(); System.Diagnostics.Debug.WriteLine($"[PERF] SetPlayerPosition total: {sw.ElapsedMilliseconds} ms");
#endif
    }

    /// <summary>
    /// Place <paramref name="player"/> on outfield cell 1–16 (sets Field).
    /// If the cell is occupied, swap cells/roles with the occupant.
    /// </summary>
    public void PlaceOrSwapOnFieldCell(Player player, int cell)
    {
        if (IsMember) return;
        var target = FieldGrid.Normalize(cell);
        if (target is null) return;

        var oldPosition = player.Position;
        var occupant = Players.FirstOrDefault(p =>
            p != player && p.Position == PlayerPosition.Field && p.FieldCell == target);

        if (occupant is null)
        {
            // Enforce single-goalie if coming from Goalie is N/A; just move to Field.
            if (player.Position == PlayerPosition.Goalie)
            {
                // no-op special
            }

            player.Position = PlayerPosition.Field;
            player.FieldCell = target;
            AfterPositionMutation(player, oldPosition, PlayerPosition.Field);
            return;
        }

        // Swap: exchange FieldCell when both Field; otherwise give target cell to incoming
        // and move occupant to the incoming player's previous Field cell (or first free / None→stack).
        var incomingCell = player.Position == PlayerPosition.Field ? player.FieldCell : null;

        player.Position = PlayerPosition.Field;
        player.FieldCell = target;

        if (incomingCell is int backCell)
        {
            occupant.Position = PlayerPosition.Field;
            occupant.FieldCell = backCell;
        }
        else
        {
            // Incoming came from stack/bench/goalie — send occupant back to unpositioned stack.
            occupant.Position = PlayerPosition.None;
        }

        AfterPositionMutation(player, oldPosition, PlayerPosition.Field);
    }

    private void AfterPositionMutation(Player player, PlayerPosition oldPosition, PlayerPosition newPosition)
    {
        var logEventType = newPosition switch
        {
            PlayerPosition.Field    => GameEventType.PlayerToField,
            PlayerPosition.Bench    => GameEventType.PlayerToBench,
            PlayerPosition.Goalie   => GameEventType.PlayerToGoalie,
            PlayerPosition.Inactive => GameEventType.PlayerToInactive,
            _                       => GameEventType.PlayerToInactive
        };
        var logPlayerName = player.Name;
        var logFrom       = oldPosition.ToString();
        var logTo         = newPosition.ToString();

        MarkNextPlayers();
        RefreshRotationPairs();
        RefreshDisplayItems();
        UpdateStartButtonState();
        _ = AutoSaveAsync();

        _ = Task.Run(() => _logger.Log(logEventType,
            $"{logPlayerName} moved to {logTo}",
            logPlayerName,
            new Dictionary<string, object?>
            {
                ["from"] = logFrom,
                ["to"] = logTo,
                ["fieldCell"] = player.FieldCell
            }));
    }

    private int? FindFirstFreeFieldCell(Player? except = null)
    {
        var used = Players
            .Where(p => p != except && p.Position == PlayerPosition.Field && p.FieldCell is int)
            .Select(p => p.FieldCell!.Value)
            .ToHashSet();

        for (var c = FieldGrid.MinCell; c <= FieldGrid.MaxCell; c++)
        {
            if (!used.Contains(c))
                return c;
        }

        return null;
    }

    public void ReorderPlayer(int fromIndex, int toIndex)
    {
        if (fromIndex == toIndex) return;
        if (fromIndex < 0 || fromIndex >= Players.Count) return;
        if (toIndex   < 0 || toIndex   >= Players.Count) return;

        // Remember which specific players are currently marked as "next to rotate"
        // so we can restore the pointers after the list order changes —
        // matching the JS behaviour in enableDragAndDrop > drop handler.
        var field = FieldCandidates();
        var bench = BenchCandidates();
        var nextFieldPlayer = field.Count > 0 && _lastFieldIdx >= 0
            ? Players.ElementAtOrDefault(NextIndexFrom(field, _lastFieldIdx))
            : null;
        var nextBenchPlayer = bench.Count > 0 && _lastBenchIdx >= 0
            ? Players.ElementAtOrDefault(NextIndexFrom(bench, _lastBenchIdx))
            : null;

        // Map player indices to DisplayItems indices BEFORE mutating Players,
        // while the two lists are still in sync.
        int displayFrom = PlayerIndexToDisplayIndex(fromIndex);
        int displayTo   = PlayerIndexToDisplayIndex(toIndex);

        var player = Players[fromIndex];

        // Option 2: Move() emits NotifyCollectionChangedAction.Move → Android
        // notifyItemMoved() — the native row view slides in-place with no
        // detach/reattach, eliminating the DragRowHandler recreation storm.
        Players.Move(fromIndex, toIndex);

        // Re-anchor the FIFO pointers to the same player objects at their new positions.
        if (nextFieldPlayer is not null)
        {
            var newIdx = Players.IndexOf(nextFieldPlayer);
            _lastFieldIdx = newIdx >= 0 ? (newIdx - 1 + Players.Count) % Players.Count : -1;
        }
        else
        {
            _lastFieldIdx = -1;
        }

        if (nextBenchPlayer is not null)
        {
            var newIdx = Players.IndexOf(nextBenchPlayer);
            _lastBenchIdx = newIdx >= 0 ? (newIdx - 1 + Players.Count) % Players.Count : -1;
        }
        else
        {
            _lastBenchIdx = -1;
        }

        MarkNextPlayers();

        // Mirror the move in DisplayItems with a single Move() notification.
        // Only do this when both indices mapped to valid display slots (i.e. the
        // player is active/visible and the header is not involved).
        if (displayFrom >= 0 && displayTo >= 0 && displayFrom != displayTo)
        {
            DisplayItems.Move(displayFrom, displayTo);
            RefreshFieldBands(); // keep Field View token order in sync
        }
        else
            RefreshDisplayItems(); // fallback for edge cases (inactive players, etc.)

        OnPropertyChanged(nameof(IsRosterEmpty));

        _ = AutoSaveAsync();

        // Fire-and-forget: log off the UI thread.
        var reorderedName = player.Name;
        var reorderedFrom = fromIndex;
        var reorderedTo   = toIndex;
        _ = Task.Run(() => _logger.Log(GameEventType.PlayerReordered,
            $"{reorderedName} moved from {reorderedFrom + 1} to {reorderedTo + 1}",
            reorderedName,
            new Dictionary<string, object?> { ["fromIndex"] = reorderedFrom, ["toIndex"] = reorderedTo }));
    }

    // ── Timer settings ────────────────────────────────────────────────────

    /// <summary>Current match duration in whole minutes (for the edit dialog prompt).</summary>
    public int MatchDurationMinutes => _timer.MatchDurationSeconds / 60;

    public void SetMatchDuration(int minutes)
    {
        if (Phase != GamePhase.Setup) return;
        _timer.MatchDurationSeconds = minutes * 60;
        Preferences.Set("game.matchDurationMinutes", minutes);
        UpdateTimerDisplays();
        _ = AutoSaveAsync();
    }

    public void SetCountdownPreset(int minutes, int seconds)
    {
        var totalSeconds = minutes * 60 + seconds;
        _timer.CountdownPresetSeconds = totalSeconds;
        Preferences.Set("game.countdownPresetSeconds", totalSeconds);
        _timer.ResetCountdown(continueRunning: _timer.TimerRunning);
        UpdateCountdownDisplay();
        _ = AutoSaveAsync();
    }

    /// <summary>
    /// Calculates the optimal rotation interval for equal playing time.
    /// Returns (minutes, seconds) or null if not enough players assigned.
    /// </summary>
    public (int minutes, int seconds)? CalculateOptimalRotationTime()
    {
        var fieldCount = Players.Count(p => p.Position == PlayerPosition.Field);
        var benchCount = Players.Count(p => p.Position == PlayerPosition.Bench);

        if (fieldCount == 0 || benchCount == 0) return null;

        var halfDuration = _timer.MatchDurationSeconds / 2;
        var equalTime    = (_timer.MatchDurationSeconds * RotationCount) / benchCount;
        var targetPerHalf = 5;
        var minPerHalf    = Math.Max(targetPerHalf, (int)Math.Ceiling((double)benchCount / RotationCount));
        var fastFives     = (halfDuration * RotationCount) / minPerHalf;
        var total         = Math.Min(equalTime, fastFives);
        if (total <= 0) return null;
        return (total / 60, total % 60);
    }

    // ── Rotation count modal support ──────────────────────────────────────

    public void IncrementRotationCount()
    {
        var maxCount = Players.Count(p => p.Position == PlayerPosition.Bench);
        if (RotationCount < maxCount)
            RotationCount++;
    }

    public void DecrementRotationCount()
    {
        if (RotationCount > 1)
            RotationCount--;
    }

    public int MaxRotationCount =>
        Players.Count(p => p.Position == PlayerPosition.Bench
                        && p.Position != PlayerPosition.Inactive
                        && p.Position != PlayerPosition.Goalie);

    // ── Theme / view support ──────────────────────────────────────────────

    public void UpdateRotationStyle(int style)
    {
        RotationStyle = style;
        MarkNextPlayers();
    }

    /// <summary>
    /// Updates the rotation warning threshold on the underlying timer service.
    /// Called when the page (re-)appears so Settings changes take effect without a full restart.
    /// </summary>
    public void UpdateRotationWarningSeconds(int seconds)
        => _timer.RotationWarningSeconds = seconds;

    /// <summary>
    /// Re-applies the match duration from Preferences when returning from Settings.
    /// Only effective while in Setup phase (no match running).
    /// </summary>
    public void UpdateMatchDurationFromPreferences()
    {
        if (Phase != GamePhase.Setup) return;
        var minutes = Preferences.Get("game.matchDurationMinutes", 90);
        if (minutes > 0 && minutes * 60 != _timer.MatchDurationSeconds)
        {
            _timer.MatchDurationSeconds = minutes * 60;
            UpdateTimerDisplays();
        }
    }

    /// <summary>
    /// Re-applies the countdown preset from Preferences when returning from Settings.
    /// </summary>
    public void UpdateCountdownPresetFromPreferences()
    {
        var seconds = Preferences.Get("game.countdownPresetSeconds", 120);
        if (seconds > 0 && seconds != _timer.CountdownPresetSeconds)
        {
            _timer.CountdownPresetSeconds = seconds;
            _timer.ResetCountdown(continueRunning: _timer.TimerRunning);
            UpdateCountdownDisplay();
        }
    }

    public void UpdateTeamView(TeamViewMode mode)
    {
        ViewMode = mode;
    }

    // ── Cloud sync ────────────────────────────────────────────────────────

    public async Task ApplyCloudSnapshotAsync(string teamId)
    {
        var snapshot = await _cloud.LoadAsync(teamId).ConfigureAwait(false);
        if (snapshot is not null)
            ApplySnapshot(snapshot);
    }

    // ── Snapshot serialisation ────────────────────────────────────────────

    public RosterSnapshot ToSnapshot()
    {
        return new RosterSnapshot
        {
            LastModifiedUtc        = DateTimeOffset.UtcNow,
            MatchDurationSeconds   = _timer.MatchDurationSeconds,
            HalfDurationSeconds    = _timer.HalfDurationSeconds,
            MatchRemainingSeconds  = _timer.MatchRemainingSeconds,
            CurrentHalf            = Phase.ToString().ToLowerInvariant(),
            // Admin owns Start/Pause/Reset; members apply these and tick locally between signals.
            TimerRunning           = _timer.TimerRunning,
            CountdownPresetSeconds = _timer.CountdownPresetSeconds,
            CountdownRemainingSeconds = _timer.CountdownRemainingSeconds,
            ViewMode               = (int)_viewMode,
            TeamAScore             = TeamAScore,
            TeamBScore             = TeamBScore,
            RotationCount          = Math.Max(1, RotationCount),
            NextFieldSlotIds       = QueueToSlotIds(_manualFieldQueue),
            NextBenchSlotIds       = QueueToSlotIds(_manualBenchQueue),
            LastFieldSlotId        = SlotIdAt(_lastFieldIdx),
            LastBenchSlotId        = SlotIdAt(_lastBenchIdx),
            ControllerUid          = _controllerUid,
            ControllerDisplayName  = _controllerDisplayName,
            // Request fields are not written by full roster upload (see CloudRosterService mask).
            ControlRequestUid      = _controlRequestUid,
            ControlRequestDisplayName = _controlRequestDisplayName,
            ControlRequestId       = _controlRequestId,
            ControllerHeartbeatUtc = IsGameController
                ? DateTimeOffset.UtcNow
                : _controllerHeartbeatUtc,
            Version                = 3,
            Players                = Players.Select(p => new PlayerSnapshot
            {
                SlotId         = p.SlotId,
                Name           = p.Name,
                Field          = p.Position == PlayerPosition.Field,
                Bench          = p.Position == PlayerPosition.Bench,
                Goalie         = p.Position == PlayerPosition.Goalie,
                Inactive       = p.Position == PlayerPosition.Inactive,
                CounterSeconds = p.FieldSeconds,
                FieldCell      = p.Position == PlayerPosition.Field
                    ? (p.FieldCell ?? 0)
                    : 0
            }).ToList()
        };
    }

    private List<int> QueueToSlotIds(Queue<int?> queue)
    {
        var list = new List<int>(queue.Count);
        foreach (var slot in queue)
        {
            if (slot is int idx && idx >= 0 && idx < Players.Count)
                list.Add(Players[idx].SlotId);
            else
                list.Add(0); // deselected / empty queue slot
        }
        return list;
    }

    private int SlotIdAt(int playerIndex)
        => playerIndex >= 0 && playerIndex < Players.Count ? Players[playerIndex].SlotId : 0;

    private void ApplySnapshot(RosterSnapshot s)
    {
        // Always merge controller / request fields first (even for the live controller).
        ApplyGameControlFromSnapshot(s);

        // Controlling admin: after first hydrate, keep local roster/timers; only process control requests.
        if (!IsMember && _controllerHydrated)
        {
            if (s.LastModifiedUtc > _lastAppliedCloudUtc)
                _lastAppliedCloudUtc = s.LastModifiedUtc;
            return;
        }

        if (s.Players.Count == 0 && s.LastModifiedUtc <= _lastAppliedCloudUtc)
            return;

        // View-only: heartbeat / sparse writes must not rebuild the roster UI or yank timers.
        // Members tick match + rotation countdown locally between real control signals.
        if (IsMember && IsSteadyStateMirror(s))
        {
            // Still track mid-cycle cloud countdown so a later jump to full preset = real Rotate.
            var steadyPreset = s.CountdownPresetSeconds > 0
                ? s.CountdownPresetSeconds
                : _timer.CountdownPresetSeconds;
            var steadyCd = s.CountdownRemainingSeconds;
            if (steadyCd == 0 && steadyPreset > 0 && ParsePhase(s.CurrentHalf) == GamePhase.Setup)
                steadyCd = steadyPreset;
            NoteCloudCountdown(steadyCd);

            if (s.LastModifiedUtc > _lastAppliedCloudUtc)
                _lastAppliedCloudUtc = s.LastModifiedUtc;
            return;
        }

        System.Diagnostics.Debug.WriteLine(
            $"[GameViewModel] ApplySnapshot players={s.Players.Count} " +
            $"scores={s.TeamAScore}-{s.TeamBScore} half={s.CurrentHalf} member={IsMember}");

        _timer.MatchDurationSeconds   = s.MatchDurationSeconds > 0 ? s.MatchDurationSeconds : 90 * 60;
        _timer.CountdownPresetSeconds = s.CountdownPresetSeconds > 0 ? s.CountdownPresetSeconds : _timer.CountdownPresetSeconds;
        // Keep the explicit Preferences key in sync so the constructor's
        // early-restore path always reflects the most recent saved value.
        // Do not stamp local prefs for members — admin cloud is source of truth.
        if (s.CountdownPresetSeconds > 0 && !IsMember)
            Preferences.Set("game.countdownPresetSeconds", s.CountdownPresetSeconds);
        TeamAScore = s.TeamAScore;
        TeamBScore = s.TeamBScore;
        // View-only members always use Field View (no Team/Rotation control surfaces).
        if (IsMember)
            ViewMode = TeamViewMode.Field;
        else
            ViewMode = (TeamViewMode)s.ViewMode switch
            {
                TeamViewMode.Rotation => TeamViewMode.Rotation,
                TeamViewMode.Field    => TeamViewMode.Field,
                _                     => TeamViewMode.Swipeable
            };

        var rosterChanged = false;
        if (s.Players.Count > 0)
            rosterChanged = ApplyPlayersFromSnapshot(s);

        if (s.LastModifiedUtc > _lastAppliedCloudUtc)
            _lastAppliedCloudUtc = s.LastModifiedUtc;

        // Members: apply Start/Pause/Reset + remaining times from cloud, then tick locally.
        var phaseBeforeTimer = Phase;
        ApplyTimerControlFromSnapshot(s, rosterChanged);

        // Phase may have advanced from Setup → live after timer apply; re-evaluate lock so
        // CanTakeVacantControl / CanUseSessionViewOnly stay correct (promote + first hydrate).
        if (phaseBeforeTimer != Phase)
            ApplyGameControlFromSnapshot(s);

        // Restore rotation FIFO so view-only clients highlight the same next players as admin.
        ApplyRotationQueuesFromSnapshot(s);

        UpdateTimerDisplays();
        MarkNextPlayers();
        RefreshRotationPairs();
        // Surgical Field→Goalie→Bench reorder when roles/order change (Move, not full clear).
        // Always refresh for view-only after a non-steady apply so groups stay correct.
        if (rosterChanged || IsMember)
            RefreshDisplayItems();
        UpdateStartButtonState();
        OnPropertyChanged(nameof(ScoresVisible));
        OnPropertyChanged(nameof(Phase));
        OnPropertyChanged(nameof(RotationCount));
        OnPropertyChanged(nameof(ActivePlayerCount));
        OnPropertyChanged(nameof(InactivePlayerCount));
        // Phase drives Watch Only vs vacant-control UI for co-admins.
        NotifyRoleProperties();

        if (IsGameController)
            _controllerHydrated = true;
    }

    /// <summary>
    /// True when cloud state matches local control surface — only heartbeat/lastModified noise.
    /// View-only clients keep ticking timers locally until a real signal arrives.
    /// Do NOT compare remaining times here: stale cloud remainings are often higher than local
    /// (local has ticked down), and treating that as "change" caused the rotate countdown to reset.
    /// </summary>
    private bool IsSteadyStateMirror(RosterSnapshot s)
    {
        var phase = ParsePhase(s.CurrentHalf);
        if (phase != _timer.Phase) return false;
        if (s.TimerRunning != _timer.TimerRunning) return false;
        if (s.TeamAScore != TeamAScore || s.TeamBScore != TeamBScore) return false;
        if (s.RotationCount > 0 && s.RotationCount != RotationCount) return false;
        if (RosterLayoutChanged(s)) return false;

        // Real admin Rotate: countdown is back near the full preset, not a stale mid-cycle value.
        var cdRem = s.CountdownRemainingSeconds;
        var preset = s.CountdownPresetSeconds > 0 ? s.CountdownPresetSeconds : _timer.CountdownPresetSeconds;
        if (IsAdminCountdownRestart(cdRem, _timer.CountdownRemainingSeconds, preset))
            return false;

        return true;
    }

    /// <summary>
    /// Detects admin Rotate (countdown reset to preset), not stale near-full cloud while the
    /// follower ticks locally. Old logic treated "cloud≈preset and local only ~6s behind" as
    /// Rotate — with a 30s preset that re-fired every ~5s and yanked the UI back to full.
    /// Real Rotate is a cloud jump from mid/low cycle up to (near) full preset.
    /// </summary>
    private bool IsAdminCountdownRestart(int cloudCountdown, int localCountdown, int presetSeconds)
    {
        if (presetSeconds <= 0) presetSeconds = 120;

        var cloudNearFull = cloudCountdown >= presetSeconds - 1;
        if (!cloudNearFull)
            return false;

        var prev = _lastCloudCountdownRemaining;
        if (prev == int.MinValue)
            return false; // no history yet — first live snap must not look like a mid-cycle Rotate

        // Cloud must have been meaningfully into the cycle, then jumped back to full.
        var wasIntoCycle = prev <= presetSeconds - Math.Max(8, presetSeconds / 4);
        if (!wasIntoCycle)
            return false;

        // Follower should also be behind cloud (not both already at full after Start).
        return localCountdown < cloudCountdown - 3;
    }

    private void NoteCloudCountdown(int cloudCountdown)
    {
        if (cloudCountdown < 0 && _lastCloudCountdownRemaining == int.MinValue)
            return;
        _lastCloudCountdownRemaining = cloudCountdown;
    }

    private bool RosterLayoutChanged(RosterSnapshot s)
    {
        if (s.Players.Count == 0) return false;
        if (s.Players.Count != Players.Count) return true;

        for (int i = 0; i < s.Players.Count; i++)
        {
            var ps = s.Players[i];
            var p = Players[i];
            var pos = ps.Field ? PlayerPosition.Field
                    : ps.Bench ? PlayerPosition.Bench
                    : ps.Goalie ? PlayerPosition.Goalie
                    : ps.Inactive ? PlayerPosition.Inactive
                    : PlayerPosition.None;
            if (ps.SlotId > 0 && p.SlotId != ps.SlotId) return true;
            if (p.Position != pos) return true;
            if (!string.Equals(p.Name, ps.Name ?? "", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(ps.Name)
                && !string.Equals(p.Name, ps.Name, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Returns true if DisplayItems must refresh (count/order/positions changed).
    /// Position changes always require RefreshDisplayItems so Field → Goalie → Bench grouping updates.
    /// </summary>
    private bool ApplyPlayersFromSnapshot(RosterSnapshot s)
    {
        var bySlot = s.Players
            .Where(p => p.SlotId > 0)
            .GroupBy(p => p.SlotId)
            .ToDictionary(g => g.Key, g => g.First());

        // Rebuild when count differs or cloud order (by SlotId sequence) differs from local.
        var needsRebuild = s.Players.Count != Players.Count;
        if (!needsRebuild && bySlot.Count > 0)
        {
            for (int i = 0; i < s.Players.Count && i < Players.Count; i++)
            {
                if (s.Players[i].SlotId > 0 && Players[i].SlotId != s.Players[i].SlotId)
                {
                    needsRebuild = true;
                    break;
                }
            }
        }

        if (needsRebuild)
        {
            // Cloud order is source of truth for view-only clients.
            Players.Clear();
            foreach (var ps in s.Players)
            {
                var pos = ps.Field ? PlayerPosition.Field
                        : ps.Bench ? PlayerPosition.Bench
                        : ps.Goalie ? PlayerPosition.Goalie
                        : ps.Inactive ? PlayerPosition.Inactive
                        : PlayerPosition.None;
                Players.Add(new Player
                {
                    SlotId = ps.SlotId > 0 ? ps.SlotId : Players.Count + 1,
                    Name = string.IsNullOrWhiteSpace(ps.Name) ? Player.DefaultName(Players.Count + 1) : ps.Name,
                    FieldSeconds = ps.CounterSeconds,
                    Position = pos,
                    FieldCell = pos == PlayerPosition.Field ? FieldGrid.Normalize(ps.FieldCell) : null
                });
            }
            return true;
        }

        // In-place update by SlotId — still flag display refresh when roles change
        // so Field/Goalie/Bench groups reorder (skipping this left mixed rows on view-only).
        var displayDirty = false;
        for (int i = 0; i < Players.Count; i++)
        {
            var p = Players[i];
            if (!bySlot.TryGetValue(p.SlotId, out var ps))
            {
                if (i < s.Players.Count)
                    ps = s.Players[i];
                else
                    continue;
            }

            var newPos = ps.Field ? PlayerPosition.Field
                       : ps.Bench ? PlayerPosition.Bench
                       : ps.Goalie ? PlayerPosition.Goalie
                       : ps.Inactive ? PlayerPosition.Inactive
                       : PlayerPosition.None;

            if (p.Position != newPos)
            {
                p.Position = newPos;
                displayDirty = true;
            }

            var newCell = newPos == PlayerPosition.Field ? FieldGrid.Normalize(ps.FieldCell) : null;
            if (p.FieldCell != newCell)
            {
                p.FieldCell = newCell;
                displayDirty = true;
            }

            if (!string.IsNullOrWhiteSpace(ps.Name) && p.Name != ps.Name)
                p.Name = ps.Name;

            p.FieldSeconds = ps.CounterSeconds;
        }

        return displayDirty;
    }

    /// <summary>
    /// Apply single-controller lock + pending control request from cloud.
    /// Updates effective IsMember for co-admins locked out of control.
    /// </summary>
    private void ApplyGameControlFromSnapshot(RosterSnapshot s)
    {
        var prevController = _controllerUid;
        var prevRequestId = _controlRequestId;
        var wasLocked = _forceLockedByController;
        var wasController = IsGameController;

        _controllerUid = s.ControllerUid?.Trim() ?? "";
        _controllerDisplayName = s.ControllerDisplayName?.Trim() ?? "";
        _controlRequestUid = s.ControlRequestUid?.Trim() ?? "";
        _controlRequestDisplayName = s.ControlRequestDisplayName?.Trim() ?? "";
        _controlRequestId = s.ControlRequestId?.Trim() ?? "";
        _controllerHeartbeatUtc = s.ControllerHeartbeatUtc > DateTimeOffset.UnixEpoch
            ? s.ControllerHeartbeatUtc.ToUniversalTime()
            : (s.LastModifiedUtc > DateTimeOffset.UnixEpoch
                ? s.LastModifiedUtc.ToUniversalTime()
                : DateTimeOffset.MinValue);

        // Auto-release of stale controllers is server-side (Cloud Function
        // releaseStaleGameControllers). Clients only mirror the cleared fields.

        // Prefer snapshot phase: ApplySnapshot runs control *before* timer sync, so on
        // init / promote re-init local Phase is still Setup while cloud is already live.
        // Using only local Phase left forceLock=false → no yellow banner and no Watch Only.
        var snapPhase = ParsePhase(s.CurrentHalf);
        var matchLive = snapPhase is not GamePhase.Setup and not GamePhase.Finished
            || Phase is not GamePhase.Setup and not GamePhase.Finished;
        var shared = IsCloudAdmin
            && !string.IsNullOrWhiteSpace(_currentTeamId)
            && !_currentTeamId.StartsWith("local_", StringComparison.Ordinal)
            && !string.Equals(Preferences.Get("team_mode", string.Empty), "local", StringComparison.Ordinal);

        // Lock rules for cloud Admins:
        //  1) Another Admin is controller → force view-only
        //  2) Live match with vacant controller (after Relinquish/server release) → force view-only
        //     until someone explicitly Takes control (prevents dual-writer free-for-all)
        if (shared
            && !string.IsNullOrEmpty(_controllerUid)
            && !string.IsNullOrEmpty(_myUid)
            && !string.Equals(_myUid, _controllerUid, StringComparison.Ordinal))
        {
            _forceLockedByController = true;
            _sessionViewOnly = false;
            StopControllerHeartbeat();
        }
        else if (shared && string.IsNullOrEmpty(_controllerUid) && matchLive)
        {
            _forceLockedByController = true;
            _sessionViewOnly = false;
            StopControllerHeartbeat();
        }
        else
        {
            _forceLockedByController = false;
        }

        // Newly became controller (accepted transfer / take vacant): drop forced lock.
        if (IsGameController)
        {
            _forceLockedByController = false;
            if (!wasController)
                _controllerHydrated = true;
            StartControllerHeartbeat();
        }
        else if (wasController && !IsGameController)
        {
            StopControllerHeartbeat();
        }

        // Became locked: next cloud apply should re-hydrate full match state.
        if (_forceLockedByController && !wasLocked)
        {
            _controllerHydrated = false;
            ViewMode = TeamViewMode.Field;
        }

        var lockChanged = wasLocked != _forceLockedByController || wasController != IsGameController;
        if (lockChanged || prevController != _controllerUid || prevRequestId != _controlRequestId)
            NotifyRoleProperties();

        System.Diagnostics.Debug.WriteLine(
            $"[GameViewModel] Control state myUid={(_myUid.Length == 0 ? "(empty)" : _myUid[..Math.Min(6, _myUid.Length)] + "…")} " +
            $"controller={(_controllerUid.Length == 0 ? "(none)" : _controllerUid[..Math.Min(6, _controllerUid.Length)] + "…")} " +
            $"name={_controllerDisplayName} isController={IsGameController} forceLock={_forceLockedByController} " +
            $"request={(_controlRequestId.Length == 0 ? "(none)" : _controlRequestId[..Math.Min(8, _controlRequestId.Length)])}");

        // Controller: surface new control request once.
        if (IsGameController
            && !string.IsNullOrEmpty(_controlRequestUid)
            && !string.IsNullOrEmpty(_controlRequestId)
            && !string.Equals(_controlRequestId, _lastShownControlRequestId, StringComparison.Ordinal)
            && !string.Equals(_controlRequestUid, _myUid, StringComparison.Ordinal))
        {
            _lastShownControlRequestId = _controlRequestId;
            var name = string.IsNullOrWhiteSpace(_controlRequestDisplayName)
                ? "Another Admin"
                : _controlRequestDisplayName;
            try
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[GameViewModel] Raising ControlRequestReceived from={name} id={_controlRequestId}");
                MainThread.BeginInvokeOnMainThread(() =>
                    ControlRequestReceived?.Invoke(this, (name, _controlRequestId)));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[GameViewModel] ControlRequestReceived: {ex.Message}");
            }
        }

        // If we sent a request and it's gone without us becoming controller → rejected (banner updates).
        if (prevRequestId.Length > 0
            && string.IsNullOrEmpty(_controlRequestId)
            && !IsGameController
            && string.Equals(prevRequestId, _lastShownControlRequestId, StringComparison.Ordinal) == false)
        {
            // no-op; ViewOnlyBannerText updates via NotifyRoleProperties
        }
    }

    /// <summary>
    /// Rebuild local rotation queues / last-rotated pointers from cloud SlotIds
    /// so MarkNextPlayers paints the same blue next-up highlights as the admin.
    /// </summary>
    private void ApplyRotationQueuesFromSnapshot(RosterSnapshot s)
    {
        if (s.RotationCount > 0)
            _rotationCount = Math.Max(1, s.RotationCount);

        _lastFieldIdx = IndexOfSlotId(s.LastFieldSlotId);
        _lastBenchIdx = IndexOfSlotId(s.LastBenchSlotId);

        // If cloud sent queues (including empty list after rotate), use them.
        // Missing/null lists leave existing queues (admin local path); members get empty then auto-FIFO.
        if (s.NextFieldSlotIds is not null)
        {
            _manualFieldQueue.Clear();
            foreach (var slotId in s.NextFieldSlotIds)
            {
                if (slotId <= 0)
                    _manualFieldQueue.Enqueue(null);
                else
                {
                    var idx = IndexOfSlotId(slotId);
                    _manualFieldQueue.Enqueue(idx >= 0 ? idx : null);
                }
            }
        }

        if (s.NextBenchSlotIds is not null)
        {
            _manualBenchQueue.Clear();
            foreach (var slotId in s.NextBenchSlotIds)
            {
                if (slotId <= 0)
                    _manualBenchQueue.Enqueue(null);
                else
                {
                    var idx = IndexOfSlotId(slotId);
                    _manualBenchQueue.Enqueue(idx >= 0 ? idx : null);
                }
            }
        }

        // Members with no queue data yet (older admin builds): seed from auto-FIFO so something highlights.
        if (IsMember
            && _manualFieldQueue.Count == 0
            && _manualBenchQueue.Count == 0
            && Phase is GamePhase.FirstHalf or GamePhase.SecondHalf or GamePhase.HalfTime or GamePhase.Ended)
        {
            SeedRotationQueues();
        }
    }

    private int IndexOfSlotId(int slotId)
    {
        if (slotId <= 0) return -1;
        for (int i = 0; i < Players.Count; i++)
            if (Players[i].SlotId == slotId)
                return i;
        return -1;
    }

    /// <summary>
    /// View-only path: mirror admin timer control without per-second cloud updates.
    /// Only re-applies on Start/Pause/phase changes or a real Rotate (countdown near full preset).
    /// Never re-applies mid-cycle remainings from stale admin saves (that reset the UI every ~15–45s).
    /// </summary>
    private void ApplyTimerControlFromSnapshot(RosterSnapshot s, bool rosterChanged = false)
    {
        var phase = ParsePhase(s.CurrentHalf);
        var matchRem = s.MatchRemainingSeconds;
        var cdRem = s.CountdownRemainingSeconds;
        var preset = s.CountdownPresetSeconds > 0 ? s.CountdownPresetSeconds : _timer.CountdownPresetSeconds;
        // Older cloud docs may omit countdownRemainingSeconds (0 default) — fall back to preset.
        if (cdRem == 0 && preset > 0 && phase == GamePhase.Setup)
            cdRem = preset;

        var running = s.TimerRunning;
        var halfDur = s.HalfDurationSeconds > 0
            ? s.HalfDurationSeconds
            : Math.Max(1, (s.MatchDurationSeconds > 0 ? s.MatchDurationSeconds : 90 * 60) / 2);

        var phaseChanged = phase != _timer.Phase;
        var runningChanged = running != _timer.TimerRunning;
        var localCd = _timer.CountdownRemainingSeconds;

        // Admin Rotate: cloud jumped back to full preset (not "stale full + local ticked 6s").
        var countdownRestart = !phaseChanged && !runningChanged
            && IsAdminCountdownRestart(cdRem, localCd, preset);

        // If cloud rarely stores mid-cycle countdown, Rotate still swaps players — treat a
        // roster layout change while local is near zero / overdue as a rotate reset.
        if (!countdownRestart && rosterChanged && !phaseChanged && !runningChanged
            && cdRem >= preset - 1
            && localCd <= Math.Max(8, preset / 5))
        {
            countdownRestart = true;
        }

        // Always remember what cloud last reported (even on steady path).
        NoteCloudCountdown(cdRem);

        if (!phaseChanged && !runningChanged && !countdownRestart)
        {
            // Steady match: keep local ticks. Ignore cloud remainings entirely.
            return;
        }

        // On countdown-only restart, keep the locally ticking match clock.
        if (countdownRestart && !phaseChanged && !runningChanged)
            matchRem = _timer.MatchRemainingSeconds;

        System.Diagnostics.Debug.WriteLine(
            $"[GameViewModel] Timer control apply phase={phase} running={running} " +
            $"matchRem={matchRem} cdRem={cdRem} restartCd={countdownRestart} roster={rosterChanged} " +
            $"(was phase={_timer.Phase} running={_timer.TimerRunning} cd={localCd} lastCloudCd={_lastCloudCountdownRemaining})");

        _timer.ApplySyncedState(
            matchDurationSeconds: s.MatchDurationSeconds > 0 ? s.MatchDurationSeconds : _timer.MatchDurationSeconds,
            halfDurationSeconds: halfDur,
            matchRemainingSeconds: matchRem,
            countdownPresetSeconds: preset > 0 ? preset : _timer.CountdownPresetSeconds,
            countdownRemainingSeconds: cdRem,
            phase: phase,
            timerRunning: running);

        MatchTimerOverdue = matchRem < 0;
        CountdownOverdue = cdRem < 0;
        if (countdownRestart)
        {
            RotationDue = false;
            RotationWarning = false;
        }
        UpdateStartButtonState();
    }

    private static GamePhase ParsePhase(string? half)
    {
        var h = (half ?? "setup").Trim().ToLowerInvariant().Replace("_", "");
        return h switch
        {
            "firsthalf" or "first" => GamePhase.FirstHalf,
            "halftime" or "half" => GamePhase.HalfTime,
            "secondhalf" or "second" => GamePhase.SecondHalf,
            "ended" => GamePhase.Ended,
            "finished" => GamePhase.Finished,
            _ => GamePhase.Setup
        };
    }

    private void SaveStartConfiguration()
    {
        if (string.IsNullOrWhiteSpace(_currentTeamId)) return;

        try
        {
            var config = new StartConfiguration
            {
                Rows = Players.Select(p => new StartConfigurationRow
                {
                    SlotId = p.SlotId,
                    Position = (int)p.Position,
                    FieldCell = p.Position == PlayerPosition.Field ? (p.FieldCell ?? 0) : 0
                }).ToList()
            };

            Preferences.Set(StartConfigurationKey(_currentTeamId), JsonSerializer.Serialize(config));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GameViewModel] SaveStartConfiguration failed: {ex.Message}");
        }
    }

    private void RestoreStartConfigurationIfAvailable()
    {
        if (string.IsNullOrWhiteSpace(_currentTeamId)) return;

        try
        {
            var raw = Preferences.Get(StartConfigurationKey(_currentTeamId), string.Empty);
            if (string.IsNullOrWhiteSpace(raw)) return;

            var config = JsonSerializer.Deserialize<StartConfiguration>(raw);
            if (config?.Rows is null || config.Rows.Count == 0) return;

            var bySlotId = Players.ToDictionary(p => p.SlotId, p => p);
            var restoredOrder = new List<Player>(config.Rows.Count);
            var usedSlots = new HashSet<int>();

            foreach (var row in config.Rows)
            {
                if (row.SlotId <= 0) continue;
                if (!bySlotId.TryGetValue(row.SlotId, out var player)) continue;
                if (!usedSlots.Add(row.SlotId)) continue;

                player.Position = Enum.IsDefined(typeof(PlayerPosition), row.Position)
                    ? (PlayerPosition)row.Position
                    : PlayerPosition.None;
                player.FieldCell = player.Position == PlayerPosition.Field
                    ? FieldGrid.Normalize(row.FieldCell)
                    : null;
                restoredOrder.Add(player);
            }

            if (restoredOrder.Count == 0) return;

            foreach (var player in Players)
            {
                if (!usedSlots.Contains(player.SlotId))
                    restoredOrder.Add(player);
            }

            Players.Clear();
            foreach (var player in restoredOrder)
                Players.Add(player);

            _lastFieldIdx = -1;
            _lastBenchIdx = -1;
            _manualFieldQueue.Clear();
            _manualBenchQueue.Clear();

            MarkNextPlayers();
            RefreshRotationPairs();
            RefreshDisplayItems();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GameViewModel] RestoreStartConfiguration failed: {ex.Message}");
        }
    }

    private static string StartConfigurationKey(string teamId)
        => $"{StartConfigurationKeyPrefix}{teamId}";

    // ── Private game control ──────────────────────────────────────────────

    private void StartOrResumeGame()
    {
        if (Phase == GamePhase.Setup && !_initialArrangementDone)
        {
            ApplyInitialArrangement();
            SaveStartConfiguration();
            _initialArrangementDone = true;
            SeedRotationQueues();
            _logger.StartSession(
                _timer.MatchDurationSeconds,
                _timer.CountdownPresetSeconds);

            // Claim single-controller lock for shared multi-admin games.
            ClaimControllerIfNeeded();
        }
        else
        {
            _logger.Log(GameEventType.GameResumed, "Match resumed");
            // Resume only if we already hold control (or local / no controller yet).
            if (IsCloudAdmin && HasActiveController && !IsGameController)
                return;
            if (IsCloudAdmin && !HasActiveController)
                ClaimControllerIfNeeded();
        }

        _timer.StartMatch();
        System.Diagnostics.Debug.WriteLine($"[GameViewModel] ▶️ Match started/resumed — Phase={Phase} TimerRunning={_timer.TimerRunning}");
        StartButtonText = "Pause";
        UpdateTimerLabelText();
        OnPropertyChanged(nameof(ScoresVisible));
        NotifyRoleProperties();
        // Claim + full roster + explicit controller patch (controller fields must hit cloud
        // even if full upload is masked/raced — other Admins depend on this).
        _ = PublishMatchStartToCloudAsync();
    }

    private async Task PublishMatchStartToCloudAsync()
    {
        try
        {
            if (string.IsNullOrEmpty(_myUid))
            {
                _myUid = await _cloud.GetSignedInUidAsync().ConfigureAwait(false) ?? "";
                if (!string.IsNullOrEmpty(_myUid))
                    Preferences.Set("chat_user_id", _myUid);
            }

            // Re-claim if first claim failed (uid not ready yet).
            if (IsCloudAdmin && !IsGameController && string.IsNullOrEmpty(_controllerUid))
            {
                await MainThread.InvokeOnMainThreadAsync(ClaimControllerIfNeeded);
            }

            await ForceCloudSaveAsync().ConfigureAwait(false);

            if (IsGameController
                && !string.IsNullOrWhiteSpace(_currentTeamId)
                && !_currentTeamId.StartsWith("local_", StringComparison.Ordinal))
            {
                await _cloud.PatchGameControlAsync(
                    _currentTeamId,
                    _controllerUid,
                    _controllerDisplayName,
                    "",
                    "",
                    "",
                    DateTimeOffset.UtcNow).ConfigureAwait(false);
                System.Diagnostics.Debug.WriteLine(
                    $"[GameViewModel] Published controller patch uid={_controllerUid[..Math.Min(8, _controllerUid.Length)]}… name={_controllerDisplayName}");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[GameViewModel] PublishMatchStart: not controller after claim " +
                    $"(IsCloudAdmin={IsCloudAdmin} myUidEmpty={string.IsNullOrEmpty(_myUid)} controller={_controllerUid})");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[GameViewModel] PublishMatchStart failed: {ex.Message}");
        }
    }

    private void ClaimControllerIfNeeded()
    {
        if (!IsCloudAdmin) return;
        if (string.IsNullOrWhiteSpace(_currentTeamId)
            || _currentTeamId.StartsWith("local_", StringComparison.Ordinal))
            return;
        if (string.Equals(Preferences.Get("team_mode", string.Empty), "local", StringComparison.Ordinal))
            return;

        if (string.IsNullOrEmpty(_myUid))
            _myUid = Preferences.Get("chat_user_id", string.Empty) ?? "";

        if (string.IsNullOrEmpty(_myUid))
        {
            System.Diagnostics.Debug.WriteLine(
                "[GameViewModel] ClaimController skipped — no Firebase uid yet");
            return;
        }

        // Don't steal control if another admin already holds it.
        if (!string.IsNullOrEmpty(_controllerUid)
            && !string.Equals(_controllerUid, _myUid, StringComparison.Ordinal))
            return;

        _controllerUid = _myUid;
        var name = UserDisplayName.Get();
        _controllerDisplayName = string.IsNullOrWhiteSpace(name) ? "Admin" : name;
        _controlRequestUid = "";
        _controlRequestDisplayName = "";
        _controlRequestId = "";
        _controllerHeartbeatUtc = DateTimeOffset.UtcNow;
        _forceLockedByController = false;
        _sessionViewOnly = false;
        _controllerHydrated = true;
        StartControllerHeartbeat();
        System.Diagnostics.Debug.WriteLine(
            $"[GameViewModel] Claimed controller uid={_myUid[..Math.Min(6, _myUid.Length)]}… name={_controllerDisplayName}");
    }

    private void ClearControllerLock()
    {
        StopControllerHeartbeat();
        _controllerUid = "";
        _controllerDisplayName = "";
        _controlRequestUid = "";
        _controlRequestDisplayName = "";
        _controlRequestId = "";
        _controllerHeartbeatUtc = DateTimeOffset.MinValue;
        _forceLockedByController = false;
        // After Reset/Relinquish we are a free co-admin again — next cloud Start must fully apply.
        _controllerHydrated = false;
        _lastShownControlRequestId = "";
        NotifyRoleProperties();
    }

    private void StartControllerHeartbeat()
    {
        if (!IsGameController) return;
        if (string.IsNullOrWhiteSpace(_currentTeamId)
            || _currentTeamId.StartsWith("local_", StringComparison.Ordinal))
            return;

        StopControllerHeartbeat();
        // Immediate ping, then periodic — server uses this for stale auto-release.
        _ = SendControllerHeartbeatAsync();
        _controllerHeartbeatTimer = new System.Threading.Timer(
            _ => _ = SendControllerHeartbeatAsync(),
            null,
            ControllerHeartbeatInterval,
            ControllerHeartbeatInterval);
    }

    private void StopControllerHeartbeat()
    {
        try { _controllerHeartbeatTimer?.Dispose(); }
        catch { /* ignore */ }
        _controllerHeartbeatTimer = null;
    }

    private async Task SendControllerHeartbeatAsync()
    {
        if (!IsGameController) return;
        if (Interlocked.CompareExchange(ref _heartbeatInFlight, 1, 0) != 0) return;

        try
        {
            _controllerHeartbeatUtc = DateTimeOffset.UtcNow;
            // Single-field patch — smaller than full control / roster writes.
            await _cloud.PatchControllerHeartbeatAsync(_currentTeamId, _controllerHeartbeatUtc)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[GameViewModel] Controller heartbeat: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _heartbeatInFlight, 0);
        }
    }

    /// <summary>
    /// Controlling Admin voluntarily releases match control (Team Settings → Relinquish).
    /// Does not end the match — timers continue on devices that were mirroring until someone claims control.
    /// </summary>
    public async Task<string> RelinquishControlAsync()
    {
        if (!IsCloudAdmin)
            return "error: Only Admins can relinquish match control.";

        if (string.IsNullOrEmpty(_myUid))
            _myUid = await _cloud.GetSignedInUidAsync().ConfigureAwait(false) ?? "";

        // Must be the current controller (or local has no lock to clear).
        if (!string.IsNullOrEmpty(_controllerUid)
            && !string.IsNullOrEmpty(_myUid)
            && !string.Equals(_controllerUid, _myUid, StringComparison.Ordinal))
        {
            return "error: You are not the Admin currently controlling the match. " +
                   "Use Request control on the Game banner, or wait for auto-release if they went offline.";
        }

        if (string.IsNullOrEmpty(_controllerUid) && !IsGameController)
            return "error: No active match controller to relinquish.";

        var teamId = _currentTeamId;
        if (string.IsNullOrWhiteSpace(teamId))
            teamId = Preferences.Get("team_id", string.Empty);

        ClearControllerLock();
        // Live match with vacant seat: stay view-only until someone Takes control
        // (including this device — prevents continuing as a free dual-writer).
        if (Phase is not GamePhase.Setup and not GamePhase.Finished
            && !teamId.StartsWith("local_", StringComparison.Ordinal)
            && IsCloudAdmin)
        {
            _forceLockedByController = true;
            NotifyRoleProperties();
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(teamId)
                && !teamId.StartsWith("local_", StringComparison.Ordinal))
            {
                await _cloud.PatchGameControlAsync(
                    teamId,
                    "",
                    "",
                    "",
                    "",
                    "",
                    DateTimeOffset.UnixEpoch).ConfigureAwait(false);
                await _cloud.PatchControlRequestAsync(teamId, "", "", "").ConfigureAwait(false);

                // Push roster/timer without claiming controller (ForceCloudSave blocked when vacant live).
                try
                {
                    var snapshot = ToSnapshot();
                    // Temporary allow write for relinquish handoff snapshot
                    snapshot.ControllerUid = "";
                    snapshot.ControllerDisplayName = "";
                    await _cloud.ForceSyncAsync(teamId, snapshot).ConfigureAwait(false);
                }
                catch { /* patch is enough for unlock */ }
            }

            return "success";
        }
        catch (Exception ex)
        {
            return $"error: {ex.Message}";
        }
    }

    /// <summary>
    /// Apply cloud controller state into this VM after a Relinquish from Team Settings
    /// (or external clear), without a full re-init.
    /// </summary>
    public void ApplyExternalControllerClear()
    {
        ClearControllerLock();
    }

    /// <summary>
    /// Locked co-admin: ask the controller to hand over match control.
    /// Patches only control fields so the live roster is not overwritten.
    /// </summary>
    public async Task<string> RequestControlAsync()
    {
        if (!CanRequestControl)
            return "error: You cannot request control right now.";

        if (string.IsNullOrEmpty(_myUid))
            _myUid = await _cloud.GetSignedInUidAsync().ConfigureAwait(false) ?? "";
        if (string.IsNullOrEmpty(_myUid))
            return "error: Not signed in.";

        var name = UserDisplayName.Get();
        if (string.IsNullOrWhiteSpace(name))
            name = "Admin";

        var requestId = Guid.NewGuid().ToString("N");
        _controlRequestUid = _myUid;
        _controlRequestDisplayName = name;
        _controlRequestId = requestId;
        NotifyRoleProperties();

        try
        {
            // Request-only patch — must never rewrite controllerUid (that wiped/raced handoffs).
            await _cloud.PatchControlRequestAsync(
                _currentTeamId,
                _controlRequestUid,
                _controlRequestDisplayName,
                _controlRequestId).ConfigureAwait(false);
            return "success";
        }
        catch (Exception ex)
        {
            _controlRequestUid = "";
            _controlRequestDisplayName = "";
            _controlRequestId = "";
            NotifyRoleProperties();
            return $"error: {ex.Message}";
        }
    }

    /// <summary>
    /// Claim vacant control after Relinquish or server auto-release (live match, no controller).
    /// </summary>
    public async Task<string> TakeVacantControlAsync()
    {
        if (!CanTakeVacantControl && !(IsCloudAdmin && !HasActiveController && IsSharedLiveMatch))
            return "error: Control is not vacant.";

        if (string.IsNullOrEmpty(_myUid))
            _myUid = await _cloud.GetSignedInUidAsync().ConfigureAwait(false) ?? "";
        if (string.IsNullOrEmpty(_myUid))
            return "error: Not signed in.";

        // Re-check cloud — another Admin may have claimed already.
        try
        {
            var snap = await _cloud.LoadAsync(_currentTeamId, preferCloud: true).ConfigureAwait(false);
            if (snap is not null && !string.IsNullOrWhiteSpace(snap.ControllerUid)
                && !string.Equals(snap.ControllerUid.Trim(), _myUid, StringComparison.Ordinal))
            {
                await MainThread.InvokeOnMainThreadAsync(() => ApplySnapshot(snap));
                return "error: Another Admin already holds control.";
            }
        }
        catch { /* proceed with claim */ }

        ClaimControllerIfNeeded();
        // Force claim even if ClaimControllerIfNeeded no-ops when empty
        if (!IsGameController)
        {
            _controllerUid = _myUid;
            var name = UserDisplayName.Get();
            _controllerDisplayName = string.IsNullOrWhiteSpace(name) ? "Admin" : name;
            _controlRequestUid = "";
            _controlRequestDisplayName = "";
            _controlRequestId = "";
            _controllerHeartbeatUtc = DateTimeOffset.UtcNow;
            _forceLockedByController = false;
            _sessionViewOnly = false;
            _controllerHydrated = true;
            StartControllerHeartbeat();
        }

        NotifyRoleProperties();

        try
        {
            await _cloud.PatchGameControlAsync(
                _currentTeamId,
                _controllerUid,
                _controllerDisplayName,
                "",
                "",
                "",
                DateTimeOffset.UtcNow).ConfigureAwait(false);
            await ForceCloudSaveAsync().ConfigureAwait(false);
            return "success";
        }
        catch (Exception ex)
        {
            return $"error: {ex.Message}";
        }
    }

    /// <summary>Controller accepts a pending request and transfers control.</summary>
    public async Task AcceptControlRequestAsync(string requestId)
    {
        if (!IsGameController) return;
        if (!string.Equals(_controlRequestId, requestId, StringComparison.Ordinal)) return;
        if (string.IsNullOrEmpty(_controlRequestUid)) return;

        var newUid = _controlRequestUid;
        var newName = string.IsNullOrWhiteSpace(_controlRequestDisplayName)
            ? "Admin"
            : _controlRequestDisplayName;

        // Final full state push while we still have effective IsAdmin, then lock ourselves.
        _controllerUid = newUid;
        _controllerDisplayName = newName;
        _controlRequestUid = "";
        _controlRequestDisplayName = "";
        _controlRequestId = "";
        // Temporarily keep IsAdmin for ForceCloudSave (IsGameController is false after uid change).
        // ForceCloudSave blocks non-controllers — push via ForceSync directly.
        try
        {
            var snapshot = ToSnapshot();
            await _cloud.ForceSyncAsync(_currentTeamId, snapshot).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GameViewModel] AcceptControl ForceSave: {ex.Message}");
        }

        try
        {
            await _cloud.PatchGameControlAsync(
                _currentTeamId,
                newUid,
                newName,
                "",
                "",
                "",
                DateTimeOffset.UtcNow).ConfigureAwait(false);
            // Clear request fields explicitly
            await _cloud.PatchControlRequestAsync(_currentTeamId, "", "", "").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GameViewModel] AcceptControl patch: {ex.Message}");
        }

        StopControllerHeartbeat();
        _forceLockedByController = true;
        _sessionViewOnly = false;
        NotifyRoleProperties();
    }

    /// <summary>Controller rejects a pending control request.</summary>
    public async Task RejectControlRequestAsync(string requestId)
    {
        if (!IsGameController) return;
        if (!string.Equals(_controlRequestId, requestId, StringComparison.Ordinal)) return;

        _controlRequestUid = "";
        _controlRequestDisplayName = "";
        _controlRequestId = "";
        NotifyRoleProperties();

        try
        {
            await _cloud.PatchControlRequestAsync(_currentTeamId, "", "", "").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GameViewModel] RejectControl: {ex.Message}");
        }
    }

    private async Task ForceCloudSaveAsync()
    {
        try
        {
            if (!IsAdmin || string.IsNullOrWhiteSpace(_currentTeamId)) return;

            var isLocal = _currentTeamId.StartsWith("local_", StringComparison.Ordinal)
                || string.Equals(Preferences.Get("team_mode", string.Empty), "local", StringComparison.Ordinal);

            // Shared live match: only the controller may push roster/timer state.
            // Vacant seat or non-controller must not write (dual-game bug).
            if (!isLocal && IsCloudAdmin)
            {
                var live = Phase is not GamePhase.Setup and not GamePhase.Finished;
                if (live && !IsGameController)
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[GameViewModel] ForceCloudSave blocked — not match controller");
                    return;
                }
            }

            var snapshot = ToSnapshot();
            // Full save must not wipe pending control requests (handled in REST upload mask).
            await _cloud.ForceSyncAsync(_currentTeamId, snapshot).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GameViewModel] ForceCloudSave failed: {ex.Message}");
        }
    }

    private void PauseGame()
    {
        _timer.PauseMatch();
        _logger.Log(GameEventType.GamePaused, "Match paused");
        StartButtonText = Phase switch
        {
            GamePhase.HalfTime => "1/2 Time",
            GamePhase.Ended    => "End",
            GamePhase.Finished => "Reset",
            GamePhase.Setup    => "Start",
            _                  => "Resume"
        };
        // Immediate cloud push so view-only clients pause without waiting for debounce.
        _ = ForceCloudSaveAsync();
    }

    private void EndGame()
    {
        var teamName = Preferences.Get("team_name", string.Empty);
        _logger.EndSession(Players, TeamAScore, TeamBScore, teamName);
        StartButtonText = "Reset";
        OnPropertyChanged(nameof(Phase));
        // Keep controller until Reset so only they can finalize; optional clear on End:
        // leave lock until Restart/Reset for cleaner handoff after full stop.
        _ = ForceCloudSaveAsync();
    }

    /// <summary>Public surface for the long-press restart command in the code-behind.</summary>
    public void RestartGameCommand() => RestartGame();

    private void RestartGame()
    {
        if (Phase != GamePhase.Finished && _logger.CurrentSession is not null)
        {
            _logger.Log(GameEventType.GameRestarted, "Game restarted");
            _logger.EndSession(Players, TeamAScore, TeamBScore, Preferences.Get("team_name", string.Empty));
        }

        _timer.Reset();
        _initialArrangementDone = false;
        _lastFieldIdx = -1;
        _lastBenchIdx = -1;
        _manualFieldQueue.Clear();
        _manualBenchQueue.Clear();
        TeamAScore = 0;
        TeamBScore = 0;

        foreach (var p in Players)
            p.FieldSeconds = 0;

        RestoreStartConfigurationIfAvailable();

        UpdateTimerDisplays();
        StartButtonText = "Start";
        UpdateTimerLabelText();
        // Match finished/reset → release single-controller lock so any Admin can start next.
        ClearControllerLock();
        UpdateStartButtonState();
        MarkNextPlayers();
        RefreshRotationPairs();
        RefreshDisplayItems();
        OnPropertyChanged(nameof(ScoresVisible));
        _ = ForceCloudSaveAsync(); // reset signal for view-only clients
    }

    /// <summary>
    /// Before the first start, assign any unpositioned players to Inactive
    /// and reorder: Field → Goalie → Bench → Inactive.
    /// </summary>
    private void ApplyInitialArrangement()
    {
        foreach (var p in Players.Where(p => p.Position == PlayerPosition.None))
            p.Position = PlayerPosition.Inactive;

        SortPlayersByPosition();
    }

    /// <summary>
    /// Re-orders the <see cref="Players"/> list in-place: Field → Goalie → Bench → Inactive.
    /// Players within the same position group keep their existing relative order.
    /// After reordering, the FIFO rotation pointers are updated to reflect the new indices
    /// of the players that were previously marked as "last rotated".
    /// </summary>
    private void SortPlayersByPosition()
    {
        // Remember which actual player objects the FIFO pointers referred to
        // so we can restore the pointers after the list is reordered.
        var lastFieldPlayer = (_lastFieldIdx >= 0 && _lastFieldIdx < Players.Count)
            ? Players[_lastFieldIdx] : null;
        var lastBenchPlayer = (_lastBenchIdx >= 0 && _lastBenchIdx < Players.Count)
            ? Players[_lastBenchIdx] : null;

        var ordered = Players
            .OrderBy(p => p.Position switch
            {
                PlayerPosition.Field    => 0,
                PlayerPosition.Goalie   => 1,
                PlayerPosition.Bench    => 2,
                _                       => 3
            })
            .ToList();

        Players.Clear();
        foreach (var p in ordered)
            Players.Add(p);

        // Restore FIFO pointers to the new indices of the same player objects.
        _lastFieldIdx = lastFieldPlayer is not null ? Players.IndexOf(lastFieldPlayer) : -1;
        _lastBenchIdx = lastBenchPlayer is not null ? Players.IndexOf(lastBenchPlayer) : -1;
    }

    // ── Rotation algorithm ────────────────────────────────────────────────

    /// <summary>
    /// Clears both rotation queues and re-populates them with the players the
    /// automatic FIFO algorithm would select next, up to <see cref="RotationCount"/> slots.
    /// Called once when the game starts and again after each Rotate execution so the
    /// queues always reflect who is coming up next.
    /// </summary>
    private void SeedRotationQueues()
    {
        _manualFieldQueue.Clear();
        _manualBenchQueue.Clear();

        var field = FieldCandidates();
        var bench = BenchCandidates();
        int slots = Math.Min(RotationCount, Math.Min(field.Count, bench.Count));

        for (int i = 0; i < slots; i++)
        {
            var fi = NextIndexFromWithOffset(field, _lastFieldIdx, i);
            if (fi >= 0) _manualFieldQueue.Enqueue(fi);

            var bi = NextIndexFromWithOffset(bench, _lastBenchIdx, i);
            if (bi >= 0) _manualBenchQueue.Enqueue(bi);
        }
    }

    private void RotateOnce()
    {
        var field = FieldCandidates();
        var bench = BenchCandidates();
        if (field.Count == 0 || bench.Count == 0) return;

        // Use the queue head if available; otherwise fall back to auto-FIFO.
        // Queues are always compact (no null slots) so no drain loop is needed.
        int fieldIdx = _manualFieldQueue.Count > 0
            ? _manualFieldQueue.Dequeue()!.Value
            : NextIndexFrom(field, _lastFieldIdx);

        int benchIdx = _manualBenchQueue.Count > 0
            ? _manualBenchQueue.Dequeue()!.Value
            : NextIndexFrom(bench, _lastBenchIdx);

        if (fieldIdx < 0 || benchIdx < 0 || fieldIdx == benchIdx) return;

        var fieldPlayer = Players[fieldIdx];
        var benchPlayer = Players[benchIdx];

        var rotNum = (_logger.CurrentSession?.Events.Count(e => e.EventType == GameEventType.RotationExecuted) ?? 0) + 1;
        _logger.Log(GameEventType.RotationExecuted,
            $"Rotation #{rotNum}: {fieldPlayer.Name} OFF, {benchPlayer.Name} ON",
            details: new Dictionary<string, object?>
            {
                ["playerOut"]      = fieldPlayer.Name,
                ["playerIn"]       = benchPlayer.Name,
                ["rotationNumber"] = rotNum
            });

        // Capture the pitch cell before role change — Position=Bench clears FieldCell.
        // Incoming bench player inherits that cell so empty grid slots stay empty across rotations.
        var formationCell = fieldPlayer.FieldCell;

        System.Diagnostics.Debug.WriteLine(
            $"[GameViewModel] ⇄ Swap #{rotNum}: {fieldPlayer.Name} (Field→Bench) ↔ {benchPlayer.Name} (Bench→Field)" +
            $" cell={formationCell?.ToString() ?? "none"}");

        // Swap roles only — do not reorder the list or repack the formation grid.
        fieldPlayer.Position = PlayerPosition.Bench;
        benchPlayer.Position = PlayerPosition.Field;
        benchPlayer.FieldCell = formationCell;

        _lastFieldIdx = fieldIdx;
        _lastBenchIdx = benchIdx;
    }

    private List<int> FieldCandidates() =>
        Players
            .Select((p, i) => (p, i))
            .Where(x => x.p.Position == PlayerPosition.Field)
            .Select(x => x.i)
            .ToList();

    private List<int> BenchCandidates() =>
        Players
            .Select((p, i) => (p, i))
            .Where(x => x.p.Position == PlayerPosition.Bench)
            .Select(x => x.i)
            .ToList();

    /// <summary>Formats a rotation queue as a comma-separated name list for debug logging.
    /// Null slots (de-selected) are shown as "NULL".</summary>
    private string QueueString(Queue<int?> queue) =>
        queue.Count == 0
            ? "(empty)"
            : string.Join(", ", queue.Select(s => s is int v ? Players[v].Name : "NULL"));

    /// <summary>
    /// Returns the next index in <paramref name="candidates"/> after <paramref name="lastIdx"/>,
    /// wrapping around (FIFO).
    /// </summary>
    internal static int NextIndexFrom(IReadOnlyList<int> candidates, int lastIdx)
    {
        if (candidates.Count == 0) return -1;
        for (int i = 0; i < candidates.Count; i++)
        {
            if (candidates[i] > lastIdx)
                return candidates[i];
        }
        return candidates[0]; // wrap
    }

    /// <summary>Returns the Nth next index (for multi-rotation highlighting).</summary>
    internal static int NextIndexFromWithOffset(IReadOnlyList<int> candidates, int lastIdx, int offset)
    {
        if (candidates.Count == 0) return -1;
        int start = -1;
        for (int i = 0; i < candidates.Count; i++)
        {
            if (candidates[i] > lastIdx) { start = i; break; }
        }
        if (start < 0) start = 0;
        return candidates[(start + offset) % candidates.Count];
    }

    private void MarkNextPlayers()
    {
        // Pair field[i] with bench[i] so Field View can share outline colours per replacement.
        // Manual tap-queue overrides take priority over the auto-FIFO pointers.
        var field = FieldCandidates();
        var bench = BenchCandidates();
        var count = Math.Min(RotationCount, Math.Min(field.Count, bench.Count));

        var fieldQueue = _manualFieldQueue.Count > 0 ? _manualFieldQueue.ToArray() : null;
        var benchQueue = _manualBenchQueue.Count > 0 ? _manualBenchQueue.ToArray() : null;

        var desiredNext = new Dictionary<int, int>(); // playerIndex → pairIndex

        for (int pair = 0; pair < count; pair++)
        {
            if (fieldQueue is not null && pair < fieldQueue.Length && fieldQueue[pair] is null) continue;
            if (benchQueue is not null && pair < benchQueue.Length && benchQueue[pair] is null) continue;

            var fi = fieldQueue is not null && pair < fieldQueue.Length
                ? fieldQueue[pair]!.Value
                : NextIndexFromWithOffset(field, _lastFieldIdx, pair);
            var bi = benchQueue is not null && pair < benchQueue.Length
                ? benchQueue[pair]!.Value
                : NextIndexFromWithOffset(bench, _lastBenchIdx, pair);

            if (fi < 0 || bi < 0) continue;
            desiredNext[fi] = pair;
            desiredNext[bi] = pair;
        }

        for (int i = 0; i < Players.Count; i++)
        {
            var shouldBe = desiredNext.ContainsKey(i);
            var pairIndex = shouldBe ? desiredNext[i] : -1;

            if (Players[i].IsNextToRotate != shouldBe)
                Players[i].IsNextToRotate = shouldBe;
            if (Players[i].RotationPairIndex != pairIndex)
                Players[i].RotationPairIndex = pairIndex;
        }
    }

    private void RefreshRotationPairs()
    {
        RotationPairs.Clear();
        if (ViewMode != TeamViewMode.Rotation) return;

        var field = FieldCandidates();
        var bench = BenchCandidates();
        var count = Math.Min(RotationCount, Math.Min(field.Count, bench.Count));

        var fieldQueue = _manualFieldQueue.Count > 0 ? _manualFieldQueue.ToArray() : null;
        var benchQueue = _manualBenchQueue.Count > 0 ? _manualBenchQueue.ToArray() : null;

        for (int i = 0; i < count; i++)
        {
            // A null slot means the user de-selected that position — skip the whole pair.
            if (fieldQueue is not null && i < fieldQueue.Length && fieldQueue[i] is null) continue;
            if (benchQueue is not null && i < benchQueue.Length && benchQueue[i] is null) continue;

            var fi = fieldQueue is not null && i < fieldQueue.Length
                ? fieldQueue[i]!.Value
                : NextIndexFromWithOffset(field, _lastFieldIdx, i);
            var bi = benchQueue is not null && i < benchQueue.Length
                ? benchQueue[i]!.Value
                : NextIndexFromWithOffset(bench, _lastBenchIdx, i);
            if (fi >= 0 && bi >= 0)
                RotationPairs.Add(new RotationPair(Players[bi].Name, Players[fi].Name));
        }
    }

    // ── Timer event handlers ──────────────────────────────────────────────

    private void OnMatchTick(object? sender, int remaining)
    {
        UpdateTimerDisplays();
        OnPropertyChanged(nameof(Phase));

        // Tick field timers for active field / goalie players
        foreach (var p in Players.Where(p => p.Position is PlayerPosition.Field or PlayerPosition.Goalie))
            p.FieldSeconds++;
    }

    private void OnCountdownTick(object? sender, int remaining)
        => UpdateCountdownDisplay();

    private void OnRotationDue(object? sender, EventArgs e)
    {
        System.Diagnostics.Debug.WriteLine("[GameViewModel] 🔔 Rotation countdown reached zero — alerting UI");
        RotationDue = true;
    }

    private void OnRotationWarning(object? sender, EventArgs e)
    {
        System.Diagnostics.Debug.WriteLine("[GameViewModel] ⚠️ Rotation countdown warning — 10s remaining");
        RotationWarning = true;
    }

    private void OnHalfTimeReached(object? sender, EventArgs e)
    {
        // Timers keep running; the button changes to "1/2 Time" so the user
        // can choose when to take the break.
        StartButtonText = "1/2 Time";
        UpdateTimerLabelText();
        OnPropertyChanged(nameof(Phase));
        if (IsAdmin)
            _ = ForceCloudSaveAsync(); // phase signal for view-only
    }

    private void OnRegulationTimeEnded(object? sender, EventArgs e)
    {
        StartButtonText = "End";
        OnPropertyChanged(nameof(Phase));
        if (IsAdmin)
            _ = ForceCloudSaveAsync();
    }

    // ── UI helpers ────────────────────────────────────────────────────────

    private void UpdateTimerDisplays()
    {
        var remaining = _timer.MatchRemainingSeconds;
        var abs = Math.Abs(remaining);
        var m   = abs / 60;
        var s   = abs % 60;
        var sign = remaining < 0 ? "-" : string.Empty;

        MatchTimeDisplay = (!_timer.TimerRunning && s == 0 && Phase == GamePhase.Setup)
            ? $"{m} min"
            : $"{sign}{m:D2}:{s:D2}";

        // Overdue = match is actively running and time has reached/passed zero.
        MatchTimerOverdue = _timer.TimerRunning && remaining <= 0;

        UpdateCountdownDisplay();
        UpdateTimerLabelText();
    }

    private void UpdateCountdownDisplay()
    {
        var r   = _timer.CountdownRemainingSeconds;
        var abs  = Math.Abs(r);
        var sign = r < 0 ? "-" : string.Empty;
        CountdownDisplay = $"{sign}{abs / 60}:{abs % 60:D2}";

        // Overdue = countdown is running and has reached/passed zero.
        CountdownOverdue = _timer.TimerRunning && r <= 0;
    }

    private void UpdateTimerLabelText()
    {
        MatchTimeLabelText = Phase switch
        {
            GamePhase.FirstHalf  => "1st Half",
            GamePhase.HalfTime   => "1st Half",
            GamePhase.SecondHalf => "2nd Half",
            GamePhase.Ended      => "2nd Half",
            _                    => "Match Time"
        };
    }

    private void UpdateRotateButtonText()
        => RotateButtonText = $"Rotate {RotationCount}";

    private void UpdateStartButtonState()
    {
        if (IsMember) return;

        OnPropertyChanged(nameof(Phase));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(HasPlayersReadyToStart));
    }

    /// <summary>
    /// Start is always tappable for admins so we can show assignment help when Setup is incomplete.
    /// </summary>
    public bool CanStart => !IsMember;

    /// <summary>
    /// True when at least one player is Field or Goalie (minimum to begin a match).
    /// </summary>
    public bool HasPlayersReadyToStart =>
        Players.Any(p => p.Position is PlayerPosition.Field or PlayerPosition.Goalie);

    // ── DisplayItems builder ──────────────────────────────────────────────

    /// <summary>
    /// Returns the index of Players[playerIndex] inside DisplayItems,
    /// or -1 if the player is not currently visible (e.g. inactive and collapsed).
    /// </summary>
    private int PlayerIndexToDisplayIndex(int playerIndex)
    {
        if (playerIndex < 0 || playerIndex >= Players.Count) return -1;
        var player = Players[playerIndex];
        for (int i = 0; i < DisplayItems.Count; i++)
            if (ReferenceEquals(DisplayItems[i], player)) return i;
        return -1;
    }

    /// <summary>
    /// Updates <see cref="DisplayItems"/> using a move-aware diff so RecyclerView
    /// receives <c>notifyItemMoved</c> instead of remove+insert pairs.
    /// Items that stay in place fire no notification at all; items that only
    /// change their position within the list animate cheaply without destroying
    /// the native view holder (avoiding DragLayoutViewGroup re-inflation).
    /// The desired order is: Field → Goalie → Bench, then the inactive header,
    /// then inactive players (when expanded).  This sort is applied here in the
    /// display layer so <see cref="Players"/> is never mutated during a rotation.
    /// </summary>
    private void RefreshDisplayItems()
    {
        // Bound CollectionView / Field View BindableLayouts require UI-thread mutations on iOS.
        if (!MainThread.IsMainThread)
        {
            MainThread.BeginInvokeOnMainThread(RefreshDisplayItems);
            return;
        }

#if DEBUG
        var sw = System.Diagnostics.Stopwatch.StartNew();
#endif
        // Build desired list in display order (Field → Goalie → Bench → Inactive header → inactive players).
        var active = Players
            .Where(p => p.Position != PlayerPosition.Inactive)
            .OrderBy(p => p.Position switch
            {
                PlayerPosition.Field  => 0,
                PlayerPosition.Goalie => 1,
                _                     => 2   // Bench and None
            })
            .ToList();

        var inactive = Players.Where(p => p.Position == PlayerPosition.Inactive).ToList();

        var desired = new List<object>(active.Count + 1 + inactive.Count);
        foreach (var p in active) desired.Add(p);
        if (inactive.Count > 0)
        {
            _inactiveHeader.Count = inactive.Count;
            desired.Add(_inactiveHeader);
            if (_inactiveHeader.IsExpanded)
                foreach (var p in inactive) desired.Add(p);
        }

        // ── Move-aware diff ────────────────────────────────────────────────
        // Pass 1: remove items from DisplayItems that are no longer in desired.
        var desiredSet = new HashSet<object>(desired, ReferenceEqualityComparer.Instance);
        for (int i = DisplayItems.Count - 1; i >= 0; i--)
        {
            if (!desiredSet.Contains(DisplayItems[i]))
                DisplayItems.RemoveAt(i);
        }

        // Pass 2: insert items that are missing from DisplayItems.
        var currentSet = new HashSet<object>(DisplayItems, ReferenceEqualityComparer.Instance);
        for (int i = 0; i < desired.Count; i++)
        {
            if (!currentSet.Contains(desired[i]))
                DisplayItems.Insert(i, desired[i]);
        }

        // Pass 3: move items that are in the wrong slot.
        // After passes 1 and 2 both lists have the same items; only order may differ.
        for (int i = 0; i < desired.Count; i++)
        {
            if (!ReferenceEquals(DisplayItems[i], desired[i]))
            {
                int from = -1;
                for (int j = i + 1; j < DisplayItems.Count; j++)
                {
                    if (ReferenceEquals(DisplayItems[j], desired[i]))
                    { from = j; break; }
                }
                if (from >= 0)
                    DisplayItems.Move(from, i); // emits notifyItemMoved — no view-holder destroyed
            }
        }

        OnPropertyChanged(nameof(IsRosterEmpty));
        RefreshFieldBands();

#if DEBUG
        sw.Stop();
        System.Diagnostics.Debug.WriteLine($"[PERF] RefreshDisplayItems (surgical): {sw.ElapsedMilliseconds} ms  items={DisplayItems.Count}");
#endif
    }

    /// <summary>
    /// Rebuilds Field View pitch/bench token lists from current positions.
    /// Outfield and goalie are separate so the goalie can sit just above the divider.
    /// Also refreshes 4×4 cell occupancy and the unpositioned stack top.
    /// </summary>
    private void RefreshFieldBands()
    {
        // Field View BindableLayout creates UIViews on collection change — UI thread only on iOS.
        if (!MainThread.IsMainThread)
        {
            MainThread.BeginInvokeOnMainThread(RefreshFieldBands);
            return;
        }

        var fieldDesired = Players
            .Where(p => p.Position == PlayerPosition.Field)
            .ToList();

        // Do NOT auto-fill FieldCell here — that packed gaps (e.g. empty cell 3) after rotation.
        // Empty cells stay empty unless the user places someone, or RotateOnce transfers a cell.

        var goalieDesired = Players
            .Where(p => p.Position == PlayerPosition.Goalie)
            .ToList();

        var benchDesired = Players
            .Where(p => p.Position == PlayerPosition.Bench)
            .ToList();

        SyncPlayerBand(FieldBandPlayers, fieldDesired);
        SyncPlayerBand(GoalieBandPlayers, goalieDesired);
        SyncPlayerBand(BenchBandPlayers, benchDesired);

        foreach (var slot in FieldGridCells)
        {
            slot.Player = fieldDesired.FirstOrDefault(p => p.FieldCell == slot.CellNumber);
        }

        // Roster order — same order as Team View list.
        UnpositionedStackTop = Players.FirstOrDefault(p => p.Position == PlayerPosition.None);

        OnPropertyChanged(nameof(IsFieldBandEmpty));
        OnPropertyChanged(nameof(IsOutfieldBandEmpty));
        OnPropertyChanged(nameof(IsGoalieBandEmpty));
        OnPropertyChanged(nameof(IsBenchBandEmpty));
    }

    private static void SyncPlayerBand(ObservableCollection<Player> band, List<Player> desired)
    {
        var desiredSet = new HashSet<Player>(desired);
        for (int i = band.Count - 1; i >= 0; i--)
        {
            if (!desiredSet.Contains(band[i]))
                band.RemoveAt(i);
        }

        var currentSet = new HashSet<Player>(band);
        for (int i = 0; i < desired.Count; i++)
        {
            if (!currentSet.Contains(desired[i]))
                band.Insert(i, desired[i]);
        }

        for (int i = 0; i < desired.Count; i++)
        {
            if (!ReferenceEquals(band[i], desired[i]))
            {
                int from = -1;
                for (int j = i + 1; j < band.Count; j++)
                {
                    if (ReferenceEquals(band[j], desired[i]))
                    { from = j; break; }
                }
                if (from >= 0)
                    band.Move(from, i);
            }
        }
    }

    // ── Player rename ─────────────────────────────────────────────────────

    /// <summary>
    /// Applies a new name to the given player and auto-saves the roster.
    /// Does nothing when the trimmed name is empty or unchanged.
    /// Blocked for view-only users (members, Watch Only, or controller lock).
    /// </summary>
    public void RenamePlayer(Player player, string newName)
    {
        if (IsMember) return;

        var trimmed = newName.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed == player.Name) return;

        player.Name = trimmed;
        _ = AutoSaveAsync();
    }

    // ── Auto-save ─────────────────────────────────────────────────────────

    private async Task AutoSaveAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_currentTeamId)) return;

            // Same single-controller write gate as ForceCloudSave (prevents dual games).
            if (!IsAdmin) return;
            var isLocal = _currentTeamId.StartsWith("local_", StringComparison.Ordinal)
                || string.Equals(Preferences.Get("team_mode", ""), "local", StringComparison.Ordinal);
            if (!isLocal && IsCloudAdmin)
            {
                var live = Phase is not GamePhase.Setup and not GamePhase.Finished;
                if (live && !IsGameController)
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[GameViewModel] AutoSave blocked — not match controller");
                    return;
                }
            }

            var snapshot = ToSnapshot();
            // Shared-team controller/admin: force immediate cloud write (REST) so peers mirror quickly.
            if (!isLocal)
            {
                await _cloud.ForceSyncAsync(_currentTeamId, snapshot).ConfigureAwait(false);
            }
            else
            {
                await _cloud.SaveAsync(_currentTeamId, snapshot, IsAdmin).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GameViewModel] AutoSave failed: {ex.Message}");
        }
    }

    private sealed class StartConfiguration
    {
        public List<StartConfigurationRow> Rows { get; set; } = [];
    }

    private sealed class StartConfigurationRow
    {
        public int SlotId { get; set; }
        public int Position { get; set; }
        /// <summary>1–16 when Position is Field; 0 = unset (older saved configs).</summary>
        public int FieldCell { get; set; }
    }

    // ── IDisposable ───────────────────────────────────────────────────────

    public void Dispose()
    {
        StopControllerHeartbeat();
        StopCloudMirror();
        _timer.MatchTickOccurred     -= OnMatchTick;
        _timer.CountdownTickOccurred -= OnCountdownTick;
        _timer.RotationDue           -= OnRotationDue;
        _timer.RotationWarning       -= OnRotationWarning;
        _timer.HalfTimeReached       -= OnHalfTimeReached;
        _timer.RegulationTimeEnded   -= OnRegulationTimeEnded;

        if (_timer is IDisposable d) d.Dispose();
    }
}
