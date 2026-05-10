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
    /// Flat list shown in the swipeable CollectionView.  Contains <see cref="Player"/>
    /// items for active players, then optionally an <see cref="InactiveGroupHeader"/>
    /// followed by inactive players if the group is expanded.
    /// </summary>
    public ObservableCollection<object> DisplayItems { get; } = [];

    // Singleton header object so bindings survive list rebuilds.
    private readonly InactiveGroupHeader _inactiveHeader = new();

    // ── Rotation FIFO pointers (mirrors JS lastFieldIdx / lastBenchIdx) ───
    private int _lastFieldIdx = -1;
    private int _lastBenchIdx = -1;

    // ── Bindable state ────────────────────────────────────────────────────
    private TeamViewMode _viewMode = TeamViewMode.Swipeable;
    private int          _rotationCount = 1;
    private int          _rotationStyle = 1;
    private int          _teamAScore;
    private int          _teamBScore;
    private bool         _rotationDue;
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

    // ── Constructor ───────────────────────────────────────────────────────

    public GameViewModel(
        IGameTimerService   timer,
        IGameLoggerService  logger,
        ICloudRosterService cloud)
    {
        _timer  = timer;
        _logger = logger;
        _cloud  = cloud;

        // Build default 16-player roster
        for (int i = 1; i <= 16; i++)
            Players.Add(new Player { Name = $"Player {i}" });

        // Wire timer events
        _timer.MatchTickOccurred      += OnMatchTick;
        _timer.CountdownTickOccurred  += OnCountdownTick;
        _timer.RotationDue            += OnRotationDue;
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
                _timer.StartSecondHalf();
                _logger.Log(GameEventType.HalfTime, "Half-time");
                _logger.Log(GameEventType.SecondHalfStarted, "Second half started");
                StartButtonText = "Resume";
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

        for (int i = 0; i < count; i++)
            RotateOnce();

        _timer.ResetCountdown(continueRunning: _timer.TimerRunning);
        MarkNextPlayers();
        RefreshRotationPairs();
        RefreshDisplayItems();
        _ = AutoSaveAsync();
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

        // Log the change
        var eventType = newPosition switch
        {
            PlayerPosition.Field    => GameEventType.PlayerToField,
            PlayerPosition.Bench    => GameEventType.PlayerToBench,
            PlayerPosition.Goalie   => GameEventType.PlayerToGoalie,
            PlayerPosition.Inactive => GameEventType.PlayerToInactive,
            _                       => GameEventType.PlayerToInactive
        };
        _logger.Log(eventType, $"{player.Name} moved to {newPosition}",
            player.Name,
            new Dictionary<string, object?> { ["from"] = oldPosition.ToString(), ["to"] = newPosition.ToString() });

#if DEBUG
        sw.Stop(); System.Diagnostics.Debug.WriteLine($"[PERF] Log: {sw.ElapsedMilliseconds} ms"); sw.Restart();
#endif

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

        var player = Players[fromIndex];
        Players.RemoveAt(fromIndex);
        Players.Insert(toIndex, player);

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
        RefreshDisplayItems();
        _logger.Log(GameEventType.PlayerReordered,
            $"{player.Name} moved from {fromIndex + 1} to {toIndex + 1}",
            player.Name,
            new Dictionary<string, object?> { ["fromIndex"] = fromIndex, ["toIndex"] = toIndex });

        _ = AutoSaveAsync();
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
        _timer.CountdownPresetSeconds = minutes * 60 + seconds;
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
        _timer.MatchDurationSeconds   = s.MatchDurationSeconds;
        _timer.CountdownPresetSeconds = s.CountdownPresetSeconds;
        TeamAScore = s.TeamAScore;
        TeamBScore = s.TeamBScore;
        ViewMode   = (TeamViewMode)s.ViewMode;

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
            _logger.StartSession(
                _timer.MatchDurationSeconds,
                _timer.CountdownPresetSeconds);
        }
        else
        {
            _logger.Log(GameEventType.GameResumed, "Match resumed");
        }

        _timer.StartMatch();
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
        _logger.EndSession(Players);
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
            _logger.EndSession(Players);
        }

        _timer.Reset();
        _initialArrangementDone = false;
        _lastFieldIdx = -1;
        _lastBenchIdx = -1;
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
    }

    // ── Rotation algorithm ────────────────────────────────────────────────

    private void RotateOnce()
    {
        var field = FieldCandidates();
        var bench = BenchCandidates();
        if (field.Count == 0 || bench.Count == 0) return;

        var fieldIdx = NextIndexFrom(field, _lastFieldIdx);
        var benchIdx = NextIndexFrom(bench, _lastBenchIdx);
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
        // The old approach cleared all flags then re-set them, causing up to
        // 2×N PropertyChanged events even when the set was unchanged.
        // With ~16 players and 4 bindings per row that was ~30-55 ms of JNI
        // marshalling on every single swipe.
        var field = FieldCandidates();
        var bench = BenchCandidates();
        var count = Math.Min(RotationCount, Math.Min(field.Count, bench.Count));

        var desiredNext = new HashSet<int>();
        for (int i = 0; i < count; i++)
        {
            var fi = NextIndexFromWithOffset(field, _lastFieldIdx, i);
            var bi = NextIndexFromWithOffset(bench, _lastBenchIdx, i);
            if (fi >= 0) desiredNext.Add(fi);
            if (bi >= 0) desiredNext.Add(bi);
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

        for (int i = 0; i < count; i++)
        {
            var fi = NextIndexFromWithOffset(field, _lastFieldIdx, i);
            var bi = NextIndexFromWithOffset(bench, _lastBenchIdx, i);
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
        RotationDue = true;
    }

    private void OnHalfTimeReached(object? sender, EventArgs e)
    {
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

        UpdateCountdownDisplay();
        UpdateTimerLabelText();
    }

    private void UpdateCountdownDisplay()
    {
        var r = _timer.CountdownRemainingSeconds;
        CountdownDisplay = $"{r / 60}:{r % 60:D2}";
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
    /// Updates <see cref="DisplayItems"/> surgically — only inserting or removing
    /// items that actually changed position.  This avoids triggering a full
    /// RecyclerView <c>notifyDataSetChanged</c> (which causes the visible hang)
    /// and instead emits cheap item-inserted / item-removed notifications.
    /// </summary>
    private void RefreshDisplayItems()
    {
#if DEBUG
        var sw = System.Diagnostics.Stopwatch.StartNew();
#endif
        // Build the desired list without touching DisplayItems yet.
        var desired = new List<object>();

        foreach (var p in Players.Where(p => p.Position != PlayerPosition.Inactive))
            desired.Add(p);

        var inactive = Players.Where(p => p.Position == PlayerPosition.Inactive).ToList();
        if (inactive.Count > 0)
        {
            _inactiveHeader.Count = inactive.Count;
            desired.Add(_inactiveHeader);

            if (_inactiveHeader.IsExpanded)
            {
                foreach (var p in inactive)
                    desired.Add(p);
            }
        }

        // Apply surgical diff: remove items that are no longer present or have
        // moved, then insert/move items into the right positions.
        // Simple O(n) pass: walk desired; ensure each slot in DisplayItems matches.
        for (int i = 0; i < desired.Count; i++)
        {
            if (i < DisplayItems.Count)
            {
                if (!ReferenceEquals(DisplayItems[i], desired[i]))
                {
                    // Remove stale items from this position onward and rebuild tail.
                    while (DisplayItems.Count > i)
                        DisplayItems.RemoveAt(DisplayItems.Count - 1);
                    DisplayItems.Add(desired[i]);
                }
                // else: already correct — no notification fired.
            }
            else
            {
                DisplayItems.Add(desired[i]);
            }
        }

        // Trim any trailing items.
        while (DisplayItems.Count > desired.Count)
            DisplayItems.RemoveAt(DisplayItems.Count - 1);

#if DEBUG
        sw.Stop();
        System.Diagnostics.Debug.WriteLine($"[PERF] RefreshDisplayItems (surgical): {sw.ElapsedMilliseconds} ms  items={DisplayItems.Count}");
#endif
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
        _timer.HalfTimeReached       -= OnHalfTimeReached;
        _timer.RegulationTimeEnded   -= OnRegulationTimeEnded;

        if (_timer is IDisposable d) d.Dispose();
    }
}
