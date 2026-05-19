using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
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

    /// <summary>True when the roster has no items to show; drives the empty-state label.</summary>
    public bool IsRosterEmpty => DisplayItems.Count == 0;

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
    private string?      _userRole;   // "admin" | "member" | null
    private string       _currentTeamId = string.Empty;
    private string       _teamName      = string.Empty;
    private bool         _initialArrangementDone;

    // ── Timer display properties (formatted strings for binding) ──────────
    private string _matchTimeDisplay    = "90 min";
    private string _countdownDisplay    = "2:00";
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

        // Build default 16-player roster
        for (int i = 1; i <= 16; i++)
            Players.Add(new Player { Name = $"Player {i}" });

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

    public bool IsMember      => _userRole == "member";
    public bool IsAdmin       => _userRole != "member";
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
        _currentTeamId = teamId;
        _userRole      = userRole;
        TeamName       = Preferences.Get("team_name", string.Empty);

        bool isLocal = teamId.StartsWith("local_", StringComparison.Ordinal);

        if (!isLocal)
        {
            // Pre-warm Firebase auth tokens in both services concurrently so the
            // first swipe never has to wait for an anonymous sign-up round trip (~800 ms).
            // Skipped for local teams — they never touch cloud/Firebase.
            _ = _cloud.WarmUpAsync();
            _ = _logger.WarmUpAsync();
        }

        var snapshot = await _cloud.LoadAsync(teamId).ConfigureAwait(false);
        if (snapshot is not null)
            ApplySnapshot(snapshot);

        OnPropertyChanged(nameof(IsMember));
        OnPropertyChanged(nameof(IsAdmin));
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

    public void IncrementTeamAScore()
    {
        TeamAScore++;
        _ = AutoSaveAsync();
    }

    public void IncrementTeamBScore()
    {
        TeamBScore++;
        _ = AutoSaveAsync();
    }

    /// <summary>Decrement Us score, minimum 0.</summary>
    public void DecrementTeamAScore()
    {
        if (TeamAScore > 0)
        {
            TeamAScore--;
            _ = AutoSaveAsync();
        }
    }

    /// <summary>Decrement Them score, minimum 0.</summary>
    public void DecrementTeamBScore()
    {
        if (TeamBScore > 0)
        {
            TeamBScore--;
            _ = AutoSaveAsync();
        }
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
        if (oldPosition == newPosition) return;

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

        // Capture logger args now (immutable) — the actual write happens off the UI thread at the end.
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

#if DEBUG
        sw.Stop(); System.Diagnostics.Debug.WriteLine($"[PERF] MarkNextPlayers: {sw.ElapsedMilliseconds} ms"); sw.Restart();
#endif

        RefreshRotationPairs();

#if DEBUG
        sw.Stop(); System.Diagnostics.Debug.WriteLine($"[PERF] RefreshRotationPairs: {sw.ElapsedMilliseconds} ms"); sw.Restart();
#endif

        // Single RefreshDisplayItems call after all state mutations are complete.
        RefreshDisplayItems();

#if DEBUG
        sw.Stop(); System.Diagnostics.Debug.WriteLine($"[PERF] RefreshDisplayItems: {sw.ElapsedMilliseconds} ms"); sw.Restart();
#endif

        UpdateStartButtonState();
        _ = AutoSaveAsync();

        // Fire-and-forget: log off the UI thread so Preferences.Set never blocks rendering.
        _ = Task.Run(() => _logger.Log(logEventType,
            $"{logPlayerName} moved to {logTo}",
            logPlayerName,
            new Dictionary<string, object?> { ["from"] = logFrom, ["to"] = logTo }));

#if DEBUG
        sw.Stop(); System.Diagnostics.Debug.WriteLine($"[PERF] UpdateStartButtonState+AutoSave fire: {sw.ElapsedMilliseconds} ms");
#endif
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
            DisplayItems.Move(displayFrom, displayTo);
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
            TimerRunning           = false, // never persist running state
            CountdownPresetSeconds = _timer.CountdownPresetSeconds,
            ViewMode               = (int)_viewMode,
            TeamAScore             = TeamAScore,
            TeamBScore             = TeamBScore,
            Players                = Players.Select(p => new PlayerSnapshot
            {
                Name           = p.Name,
                Field          = p.Position == PlayerPosition.Field,
                Bench          = p.Position == PlayerPosition.Bench,
                Goalie         = p.Position == PlayerPosition.Goalie,
                Inactive       = p.Position == PlayerPosition.Inactive,
                CounterSeconds = p.FieldSeconds
            }).ToList()
        };
    }

    private void ApplySnapshot(RosterSnapshot s)
    {
        _timer.MatchDurationSeconds   = s.MatchDurationSeconds > 0 ? s.MatchDurationSeconds : 90 * 60;
        _timer.CountdownPresetSeconds = s.CountdownPresetSeconds;
        // Keep the explicit Preferences key in sync so the constructor's
        // early-restore path always reflects the most recent saved value.
        if (s.CountdownPresetSeconds > 0)
            Preferences.Set("game.countdownPresetSeconds", s.CountdownPresetSeconds);
        TeamAScore = s.TeamAScore;
        TeamBScore = s.TeamBScore;
        ViewMode   = (TeamViewMode)s.ViewMode == TeamViewMode.Rotation
                         ? TeamViewMode.Rotation
                         : TeamViewMode.Swipeable;

        for (int i = 0; i < Math.Min(s.Players.Count, Players.Count); i++)
        {
            var ps = s.Players[i];
            var p  = Players[i];
            p.Name         = ps.Name;
            p.FieldSeconds = ps.CounterSeconds;
            p.Position = ps.Field    ? PlayerPosition.Field
                       : ps.Bench   ? PlayerPosition.Bench
                       : ps.Goalie  ? PlayerPosition.Goalie
                       : ps.Inactive ? PlayerPosition.Inactive
                       : PlayerPosition.None;
        }

        UpdateTimerDisplays();
        MarkNextPlayers();
        RefreshRotationPairs();
        RefreshDisplayItems();
        UpdateStartButtonState();
    }

    // ── Private game control ──────────────────────────────────────────────

    private void StartOrResumeGame()
    {
        if (Phase == GamePhase.Setup && !_initialArrangementDone)
        {
            ApplyInitialArrangement();
            _initialArrangementDone = true;
            SeedRotationQueues();
            _logger.StartSession(
                _timer.MatchDurationSeconds,
                _timer.CountdownPresetSeconds);
        }
        else
        {
            _logger.Log(GameEventType.GameResumed, "Match resumed");
        }

        _timer.StartMatch();
        System.Diagnostics.Debug.WriteLine($"[GameViewModel] ▶️ Match started/resumed — Phase={Phase} TimerRunning={_timer.TimerRunning}");
        StartButtonText = "Pause";
        UpdateTimerLabelText();
        OnPropertyChanged(nameof(ScoresVisible));
        _ = AutoSaveAsync();
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
        _ = AutoSaveAsync();
    }

    private void EndGame()
    {
        var teamName = Preferences.Get("team_name", string.Empty);
        _logger.EndSession(Players, TeamAScore, TeamBScore, teamName);
        StartButtonText = "Reset";
        OnPropertyChanged(nameof(Phase));
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

        UpdateTimerDisplays();
        StartButtonText = "Start";
        UpdateTimerLabelText();
        UpdateStartButtonState();
        MarkNextPlayers();
        RefreshRotationPairs();
        RefreshDisplayItems();
        OnPropertyChanged(nameof(ScoresVisible));
        _ = AutoSaveAsync();
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

        System.Diagnostics.Debug.WriteLine(
            $"[GameViewModel] ⇄ Swap #{rotNum}: {fieldPlayer.Name} (Field→Bench) ↔ {benchPlayer.Name} (Bench→Field)");

        // Swap positions only — do not reorder the list.
        fieldPlayer.Position = PlayerPosition.Bench;
        benchPlayer.Position = PlayerPosition.Field;

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
        // Build the desired "next to rotate" set FIRST, then only fire
        // PropertyChanged for players whose flag actually changes.
        // Manual tap-queue overrides take priority over the auto-FIFO pointers.
        var field = FieldCandidates();
        var bench = BenchCandidates();
        var count = Math.Min(RotationCount, Math.Min(field.Count, bench.Count));

        var desiredNext = new HashSet<int>();

        // ── Field side ──
        if (_manualFieldQueue.Count > 0)
        {
            // Null slots are de-selected by the user — skip them.
            foreach (var slot in _manualFieldQueue.Take(count))
                if (slot is int idx && idx >= 0 && idx < Players.Count) desiredNext.Add(idx);
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                var fi = NextIndexFromWithOffset(field, _lastFieldIdx, i);
                if (fi >= 0) desiredNext.Add(fi);
            }
        }

        // ── Bench side ──
        if (_manualBenchQueue.Count > 0)
        {
            foreach (var slot in _manualBenchQueue.Take(count))
                if (slot is int idx && idx >= 0 && idx < Players.Count) desiredNext.Add(idx);
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                var bi = NextIndexFromWithOffset(bench, _lastBenchIdx, i);
                if (bi >= 0) desiredNext.Add(bi);
            }
        }

        for (int i = 0; i < Players.Count; i++)
        {
            bool shouldBe = desiredNext.Contains(i);
            if (Players[i].IsNextToRotate != shouldBe)
                Players[i].IsNextToRotate = shouldBe;
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
    }

    private void OnRegulationTimeEnded(object? sender, EventArgs e)
    {
        StartButtonText = "End";
        OnPropertyChanged(nameof(Phase));
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

        if (Phase != GamePhase.Setup) return;

        var hasFieldPlayers = Players.Any(p =>
            p.Position is PlayerPosition.Field or PlayerPosition.Goalie);

        OnPropertyChanged(nameof(Phase));
        OnPropertyChanged(nameof(CanStart));
        _ = hasFieldPlayers;
    }

    public bool CanStart =>
        !IsMember &&
        (Phase != GamePhase.Setup ||
         Players.Any(p => p.Position is PlayerPosition.Field or PlayerPosition.Goalie));

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

#if DEBUG
        sw.Stop();
        System.Diagnostics.Debug.WriteLine($"[PERF] RefreshDisplayItems (surgical): {sw.ElapsedMilliseconds} ms  items={DisplayItems.Count}");
#endif
    }

    // ── Player rename ─────────────────────────────────────────────────────

    /// <summary>
    /// Applies a new name to the given player and auto-saves the roster.
    /// Does nothing when the trimmed name is empty or unchanged.
    /// </summary>
    public void RenamePlayer(Player player, string newName)
    {
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
            var snapshot = ToSnapshot();
            await _cloud.SaveAsync(_currentTeamId, snapshot, IsAdmin).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GameViewModel] AutoSave failed: {ex.Message}");
        }
    }

    // ── IDisposable ───────────────────────────────────────────────────────

    public void Dispose()
    {
        _timer.MatchTickOccurred     -= OnMatchTick;
        _timer.CountdownTickOccurred -= OnCountdownTick;
        _timer.RotationDue           -= OnRotationDue;
        _timer.RotationWarning       -= OnRotationWarning;
        _timer.HalfTimeReached       -= OnHalfTimeReached;
        _timer.RegulationTimeEnded   -= OnRegulationTimeEnded;

        if (_timer is IDisposable d) d.Dispose();
    }
}
