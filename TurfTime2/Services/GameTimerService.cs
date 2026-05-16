using TurfTime2.Models;

namespace TurfTime2.Services;

/// <summary>
/// Concrete implementation of <see cref="IGameTimerService"/>.
/// Uses a single <see cref="PeriodicTimer"/> ticking every second on a background thread;
/// all state mutations and event raises are marshalled to the UI thread via
/// <see cref="MainThread.BeginInvokeOnMainThread"/>.
/// </summary>
public sealed class GameTimerService : IGameTimerService, IDisposable
{
    // ── IGameTimerService state ────────────────────────────────────────────
    private int _matchDurationSeconds = 90 * 60;
    public int MatchDurationSeconds
    {
        get => _matchDurationSeconds;
        set
        {
            _matchDurationSeconds = value;
            // Keep the displayed time in sync while no game is running yet.
            if (Phase == GamePhase.Setup)
                MatchRemainingSeconds = value;
        }
    }

    public int  MatchRemainingSeconds  { get; private set; }
    public int  HalfDurationSeconds    { get; private set; }
    public int  CountdownPresetSeconds { get; set; } = 2 * 60;
    public int  CountdownRemainingSeconds { get; private set; }
    public bool TimerRunning           { get; private set; }
    public GamePhase Phase             { get; private set; } = GamePhase.Setup;

    // ── Events ────────────────────────────────────────────────────────────
    public event EventHandler<int>? MatchTickOccurred;
    public event EventHandler<int>? CountdownTickOccurred;
    public event EventHandler?      RotationDue;
    public event EventHandler?      HalfTimeReached;
    public event EventHandler?      RegulationTimeEnded;

    // ── Internals ─────────────────────────────────────────────────────────
    private CancellationTokenSource? _cts;
    private bool _countdownRunning;

    public GameTimerService()
    {
        // Phase is already GamePhase.Setup (default), so the property setter
        // will have set MatchRemainingSeconds = MatchDurationSeconds.
        // Initialise countdown here.
        CountdownRemainingSeconds = CountdownPresetSeconds;
    }

    // ── Control ───────────────────────────────────────────────────────────

    public void StartMatch()
    {
        if (TimerRunning) return;

        if (Phase == GamePhase.Setup)
        {
            // First start: initialise halves
            HalfDurationSeconds    = MatchDurationSeconds / 2;
            MatchRemainingSeconds  = HalfDurationSeconds;
            Phase = GamePhase.FirstHalf;
        }

        TimerRunning = true;
        _countdownRunning = true;
        StartBackgroundLoop();
    }

    public void PauseMatch()
    {
        if (!TimerRunning) return;
        TimerRunning = false;
        _countdownRunning = false;
        StopBackgroundLoop();
    }

    public void ResumeMatch()
    {
        if (TimerRunning) return;
        TimerRunning = true;
        _countdownRunning = true;
        StartBackgroundLoop();
    }

    public void StartSecondHalf()
    {
        Phase = GamePhase.SecondHalf;
        MatchRemainingSeconds = HalfDurationSeconds;
    }

    public void Reset()
    {
        StopBackgroundLoop();
        TimerRunning          = false;
        _countdownRunning     = false;
        Phase                 = GamePhase.Setup;
        HalfDurationSeconds   = 0;
        MatchRemainingSeconds = MatchDurationSeconds;
        CountdownRemainingSeconds = CountdownPresetSeconds;
    }

    public void ResetCountdown(bool continueRunning)
    {
        CountdownRemainingSeconds = CountdownPresetSeconds;
        _countdownRunning = continueRunning && TimerRunning;
    }

    // ── Background loop ───────────────────────────────────────────────────

    private void StartBackgroundLoop()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Task.Run(() => TickLoopAsync(token), token);
    }

    private void StopBackgroundLoop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private async Task TickLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                MainThread.BeginInvokeOnMainThread(OnTick);
            }
        }
        catch (OperationCanceledException) { /* normal stop */ }
    }

    private void OnTick()
    {
        if (!TimerRunning) return;

        // ── Match timer ───────────────────────────────────────────────────
        MatchRemainingSeconds--;
        MatchTickOccurred?.Invoke(this, MatchRemainingSeconds);

        if (MatchRemainingSeconds <= 0)
        {
            if (Phase == GamePhase.FirstHalf)
            {
                Phase = GamePhase.HalfTime;
                // Reset countdown to preset and keep it running so it counts
                // through zero into negative — showing half-time overtime.
                CountdownRemainingSeconds = CountdownPresetSeconds;
                _countdownRunning = true;
                HalfTimeReached?.Invoke(this, EventArgs.Empty);
            }
            else if (Phase == GamePhase.SecondHalf)
            {
                Phase = GamePhase.Ended;
                // Allow overtime — timer continues counting negative seconds
                RegulationTimeEnded?.Invoke(this, EventArgs.Empty);
            }
        }

        // ── Countdown timer ───────────────────────────────────────────────
        if (_countdownRunning)
        {
            CountdownRemainingSeconds--;
            CountdownTickOccurred?.Invoke(this, CountdownRemainingSeconds);

            // During half-time the countdown runs into negative (showing overtime).
            // RotationDue is not fired and the countdown does not stop until the
            // user presses the 1/2 Time button to start the second half.
            if (CountdownRemainingSeconds == 0 && Phase != GamePhase.HalfTime)
            {
                _countdownRunning = false;
                RotationDue?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    // ── IDisposable ───────────────────────────────────────────────────────

    public void Dispose()
    {
        StopBackgroundLoop();
    }
}
