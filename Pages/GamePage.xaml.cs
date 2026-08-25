using TurfTime2.Models;
using TurfTime2.Services;
using TurfTime2.ViewModels;

namespace TurfTime2;

public partial class GamePage : ContentPage
{
    private const string DemoTeamId = "local_demo_team";
    /// <summary>How often to re-check cloud role while Game is open (Promote to Admin without leaving tab).</summary>
    private static readonly TimeSpan RolePollInterval = TimeSpan.FromSeconds(8);

    private GameViewModel? _vm;

    private CancellationTokenSource? _startLongPressCts;
    private CancellationTokenSource? _rotateLongPressCts;
    private CancellationTokenSource? _tapToRotateLongPressCts;
    private CancellationTokenSource? _rolePollCts;

    /// <summary>True when Rotate long-press opened the count dialog — suppress the following Clicked rotate.</summary>
    private bool _rotateLongPressHandled;

    /// <summary>True when Tap-to-Rotate long-press opened the count dialog — suppress short-tap rotate on release.</summary>
    private bool _tapToRotateLongPressHandled;

    // ── Lifecycle ─────────────────────────────────────────────────────────

    public GamePage()
    {
        InitializeComponent();
        EnsureFieldBandScrollTransparency();

#if IOS
        // Full-row pan blocks UICollectionView scrolling on iOS; use swipe + handle-only pan.
        if (Resources.TryGetValue("RosterSelector", out var selectorObj)
            && selectorObj is RosterItemTemplateSelector selector
            && Resources.TryGetValue("PlayerRowTemplateIos", out var iosTemplateObj)
            && iosTemplateObj is DataTemplate iosTemplate)
        {
            selector.PlayerTemplate = iosTemplate;
        }
#endif
    }

    /// <summary>
    /// Bench/Absent ScrollViews must not paint an opaque platform background behind tokens
    /// (XAML BackgroundColor=Transparent alone is often ignored on Android/iOS).
    /// </summary>
    private void EnsureFieldBandScrollTransparency()
    {
        WireTransparentScroll(BenchTokenScroll);
        WireTransparentScroll(InactiveTokenScroll);
    }

    private static void WireTransparentScroll(ScrollView? scroll)
    {
        if (scroll is null) return;
        scroll.BackgroundColor = Colors.Transparent;
        scroll.HandlerChanged -= OnFieldBandScrollHandlerChanged;
        scroll.HandlerChanged += OnFieldBandScrollHandlerChanged;
        ApplyNativeScrollTransparency(scroll);
    }

    private static void OnFieldBandScrollHandlerChanged(object? sender, EventArgs e)
    {
        if (sender is ScrollView scroll)
            ApplyNativeScrollTransparency(scroll);
    }

    private static void ApplyNativeScrollTransparency(ScrollView scroll)
    {
        scroll.BackgroundColor = Colors.Transparent;
#if ANDROID
        if (scroll.Handler?.PlatformView is Android.Views.View native)
        {
            native.SetBackgroundColor(Android.Graphics.Color.Transparent);
            native.Background = null;
        }
#elif IOS || MACCATALYST
        if (scroll.Handler?.PlatformView is UIKit.UIScrollView ui)
        {
            ui.BackgroundColor = UIKit.UIColor.Clear;
            ui.Opaque = false;
        }
#endif
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        SetKeepScreenOn(true);

        // App-level sleep/resume: listener dies after long iOS suspend; re-attach on resume.
        App.Sleeping += OnAppSleeping;
        App.Resumed  += OnAppResumed;

        var teamId = Preferences.Get("team_id", string.Empty);
        var userRole = Preferences.Get("user_role", (string?)null);

        // Shared teams: refresh role from cloud (Promote to Admin) without leave/rejoin.
        userRole = await RefreshCloudRoleAsync(teamId, userRole);
        await ApplyTeamAndRoleToViewModelAsync(teamId, userRole, forceFromAppear: true);

        // Re-read rotation style preference whenever the page appears
        if (_vm is not null)
        {
            var style = Preferences.Get("rotation_style", 1);
            _vm.UpdateRotationStyle(style);

            // Re-apply timer settings in case they were changed in Settings → Timers.
            // Keep the seeded demo team values intact on first-run experience.
            // Members / Watch Only must NOT overwrite cloud-mirrored timer/countdown with local prefs.
            var isDemoTeam = string.Equals(teamId, DemoTeamId, StringComparison.Ordinal);
            if (!isDemoTeam && !_vm.IsMember)
            {
                _vm.UpdateMatchDurationFromPreferences();
                _vm.UpdateCountdownPresetFromPreferences();
            }
            _vm.UpdateRotationWarningSeconds(Preferences.Get("game.rotationWarningSeconds", 10));
        }

        RotationStylePage.RotationStyleChanged += OnRotationStyleChanged;
        DragState.NativeSwipeReleased += OnNativeSwipeReleased;
        DragState.NativeLongPressBegan += OnNativeLongPressBegan;
        DragState.NativeLongPressEnded += OnNativeLongPressEnded;

        // Re-subscribe every time the page appears (OnDisappearing unsubscribes).
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            _vm.ControlRequestReceived += OnControlRequestReceived;
        }

        // Keep checking role while Game stays open so Promote takes effect without tab switching.
        StartRolePolling(teamId);

        // Re-assert bright timer colours after layout (Android theme can dull default White).
        EnsureTimerLabelContrast();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        SetKeepScreenOn(false);
        StopRolePolling();

        App.Sleeping -= OnAppSleeping;
        App.Resumed  -= OnAppResumed;

        // Stop Firestore listener / recovery pulls while Game is not visible.
        _vm?.PauseCloudMirror();

        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.ControlRequestReceived -= OnControlRequestReceived;
        }
        RotationStylePage.RotationStyleChanged -= OnRotationStyleChanged;
        DragState.NativeSwipeReleased -= OnNativeSwipeReleased;
        DragState.NativeLongPressBegan -= OnNativeLongPressBegan;
        DragState.NativeLongPressEnded -= OnNativeLongPressEnded;
    }

    private void StartRolePolling(string teamId)
    {
        StopRolePolling();
        if (string.IsNullOrEmpty(teamId)
            || teamId.StartsWith("local_", StringComparison.Ordinal)
            || !string.Equals(Preferences.Get("team_mode", string.Empty), "shared", StringComparison.Ordinal))
            return;

        _rolePollCts = new CancellationTokenSource();
        var token = _rolePollCts.Token;
        _ = PollCloudRoleLoopAsync(teamId, token);
    }

    private void StopRolePolling()
    {
        try { _rolePollCts?.Cancel(); }
        catch { /* ignore */ }
        try { _rolePollCts?.Dispose(); }
        catch { /* ignore */ }
        _rolePollCts = null;
    }

    private async Task PollCloudRoleLoopAsync(string teamId, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(RolePollInterval, token).ConfigureAwait(false);
                if (token.IsCancellationRequested) break;

                // Team may have switched while we were delayed.
                var currentTeam = Preferences.Get("team_id", string.Empty);
                if (!string.Equals(currentTeam, teamId, StringComparison.Ordinal))
                    break;

                var localRole = Preferences.Get("user_role", (string?)null);
                var cloudRole = await RefreshCloudRoleAsync(teamId, localRole).ConfigureAwait(false);

                var changed = !string.Equals(
                    localRole ?? string.Empty,
                    cloudRole ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase);
                var vmMismatch = _vm is not null
                    && (_vm.IsCloudAdmin != !string.Equals(cloudRole, "member", StringComparison.Ordinal));

                if (!changed && !vmMismatch)
                    continue;

                System.Diagnostics.Debug.WriteLine(
                    $"[GamePage] Role poll detected change local={localRole} cloud={cloudRole} — re-init");

                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    if (token.IsCancellationRequested) return;
                    await ApplyTeamAndRoleToViewModelAsync(teamId, cloudRole, forceFromAppear: false);
                });
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[GamePage] Role poll: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Pulls role from Firestore and updates Preferences. Returns the role to use (cloud or previous).
    /// </summary>
    private async Task<string?> RefreshCloudRoleAsync(string teamId, string? fallbackRole)
    {
        if (string.IsNullOrEmpty(teamId)
            || teamId.StartsWith("local_", StringComparison.Ordinal)
            || !string.Equals(Preferences.Get("team_mode", string.Empty), "shared", StringComparison.Ordinal))
            return fallbackRole;

        try
        {
            var services = Handler?.MauiContext?.Services
                ?? Application.Current?.Handler?.MauiContext?.Services;
            var cloudTeam = services?.GetService<ICloudTeamService>();
            if (cloudTeam is null) return fallbackRole;

            var cloudRole = await cloudTeam.GetMyRoleAsync(teamId).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(cloudRole))
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[GamePage] Cloud role refresh returned empty for team={teamId}");
                return fallbackRole;
            }

            var normalized = cloudRole.Trim().ToLowerInvariant();
            Preferences.Set("user_role", normalized);
            Preferences.Set($"{teamId}_role", normalized);
            Preferences.Set($"user_role_{teamId}", normalized);
            System.Diagnostics.Debug.WriteLine(
                $"[GamePage] Cloud role refresh team={teamId} role={normalized}");
            return normalized;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GamePage] Role refresh: {ex.Message}");
            return fallbackRole;
        }
    }

    private async Task ApplyTeamAndRoleToViewModelAsync(string teamId, string? userRole, bool forceFromAppear)
    {
        var sessionViewOnly = Preferences.Get($"session_view_only_{teamId}", false)
            && !string.Equals(userRole, "member", StringComparison.Ordinal);
        var isMember = string.Equals(userRole, "member", StringComparison.Ordinal) || sessionViewOnly;
        var lastTeam = Preferences.Get("_gamepage_last_team", string.Empty);
        var vmRoleMismatch = _vm is not null
            && (_vm.IsCloudAdmin != !string.Equals(userRole, "member", StringComparison.Ordinal));

        if (_vm is null)
        {
            await CreateViewModelAsync(teamId, userRole);
            return;
        }

        // On appear: re-init for members / team switch / role change.
        // On poll: only re-init when role actually changed (vmRoleMismatch) or team changed.
        var shouldReinit = teamId != lastTeam
            || vmRoleMismatch
            || (forceFromAppear && isMember);

        if (!shouldReinit)
            return;

        Preferences.Set("_gamepage_last_team", teamId);
        await _vm.InitialiseAsync(teamId, userRole);
        ApplyViewMode(_vm.ViewMode);
        await WarmUpRosterRowsAsync();
    }

    private void OnAppSleeping(object? sender, EventArgs e)
    {
        // Backgrounding does not always fire OnDisappearing while still on Game.
        _vm?.PauseCloudMirror();
        System.Diagnostics.Debug.WriteLine("[GamePage] App sleeping — cloud mirror paused");
    }

    private async void OnAppResumed(object? sender, EventArgs e)
    {
        if (_vm is null) return;
        // Members and admins in Watch Only both need the cloud mirror.
        if (!_vm.IsMember) return;

        System.Diagnostics.Debug.WriteLine("[GamePage] App resumed — re-attaching cloud mirror");
        try
        {
            await _vm.ResumeCloudMirrorAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GamePage] Resume cloud mirror failed: {ex.Message}");
        }
    }

    // ── ViewModel factory ─────────────────────────────────────────────────

    /// <summary>
    /// Immediately stops all timers and resets match state. Called by TeamDetailsPage
    /// before switching teams so no timer state bleeds into the next team's session.
    /// </summary>
    public void ResetMatchState() => _vm?.ResetMatchState();

    /// <summary>Live GameViewModel for Team Settings (e.g. Relinquish Match Control).</summary>
    public GameViewModel? ViewModel => _vm;

    private async Task CreateViewModelAsync(string teamId, string? userRole)
    {
        var services = Handler?.MauiContext?.Services
            ?? Application.Current?.Handler?.MauiContext?.Services;

        var timer = services?.GetService<IGameTimerService>() ?? new GameTimerService();
        var cloud = services?.GetService<ICloudRosterService>()
            ?? throw new InvalidOperationException("ICloudRosterService is not registered");
        var session = services?.GetService<ISessionStorageService>()
            ?? throw new InvalidOperationException("ISessionStorageService is not registered");
        var logger = services?.GetService<IGameLoggerService>() ?? new GameLoggerService(session);
        _vm = new GameViewModel(timer, logger, cloud);

        BindingContext = _vm;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _vm.ControlRequestReceived += OnControlRequestReceived;

        Preferences.Set("_gamepage_last_team", teamId);
        await _vm.InitialiseAsync(teamId, userRole);
        ApplyViewMode(_vm.ViewMode);

        // Warm up the CollectionView's RecyclerView row handlers so that
        // the first Rotate doesn't pay the DragLayoutViewGroup inflation
        // cost (which causes frame jank at rotation time).
        await WarmUpRosterRowsAsync();
    }

    /// <summary>
    /// Forces the RecyclerView to inflate all visible DragRow native views
    /// at startup, avoiding first-rotation jank caused by lazy inflation.
    /// </summary>
    private async Task WarmUpRosterRowsAsync()
    {
        if (_vm is null || _vm.DisplayItems.Count == 0) return;

        // Wait one frame for the layout pass to complete, then trigger a
        // scroll to the end and back to ensure all visible rows are inflated.
        await Task.Yield();

        var roster = SwipeableRoster;
        if (!roster.IsVisible || _vm.DisplayItems.Count == 0) return;

        int last = _vm.DisplayItems.Count - 1;
        roster.ScrollTo(last, position: ScrollToPosition.End,   animate: false);
        await Task.Yield();
        roster.ScrollTo(0,    position: ScrollToPosition.Start, animate: false);

        System.Diagnostics.Debug.WriteLine($"[GamePage] 🔥 Roster rows warmed up ({_vm.DisplayItems.Count} items)");
    }

    // ── Rotation style event ──────────────────────────────────────────────

    private void OnRotationStyleChanged(object? sender, int style)
    {
        if (_vm is null) return;
        _vm.UpdateRotationStyle(style);
    }

    // Fallback for when MAUI's PanGestureRecognizer drops the Completed event
    // (e.g. the finger exits the view bounds during a fast swipe on Android).
    // The native layer fires NativeSwipeReleased on every ACTION_UP/CANCEL that
    // was NOT a confirmed drag, so we check _panRow before acting.
    //
    // _nativeReleaseHandled is a one-shot guard: Android can deliver both
    // ACTION_UP and ACTION_CANCEL for the same gesture, causing double-fires.
    // The first arrival claims the token; the second sees it taken and returns.
    private volatile bool _nativeReleaseHandled;

    private void OnNativeSwipeReleased()
    {
        if (_panRow is null) return; // MAUI already delivered Completed — nothing to do

        // Claim the one-shot token. If another native callback already claimed it,
        // this is a duplicate UP/CANCEL for the same gesture — ignore it.
        if (_nativeReleaseHandled)
        {
            System.Diagnostics.Debug.WriteLine("[DRAG] 🛡️ NativeSwipeReleased — duplicate UP/CANCEL, ignoring.");
            return;
        }
        _nativeReleaseHandled = true;

        var row    = _panRow;
        var player = _panPlayer;

        System.Diagnostics.Debug.WriteLine(
            $"[DRAG] 🛡️ NativeSwipeReleased fallback — MAUI dropped Completed for '{player?.Name}'. " +
            $"Snapping row back from TranslationX={row.TranslationX:F1}");

        // Run on the UI thread; native callbacks arrive on the Android touch thread.
        Dispatcher.Dispatch(() =>
        {
            if (_panIntent == PanIntent.Swipe)
                CommitSwipe(row, player!);
            else
                _ = row.TranslateTo(0, 0, 180, Easing.SpringOut);

            if (player is not null) player.IsDragging = false;
            ClearDragIndicator();
            _panRow    = null;
            _panPlayer = null;
            _panIntent = PanIntent.Unknown;
            DragState.LongPressConfirmed = false;
        });
    }

    private void OnNativeLongPressBegan(object? bindingContext)
    {
        if (_vm is null || _vm.IsMember) return;
        if (bindingContext is not Player player) return;

        Dispatcher.Dispatch(() =>
        {
            player.IsDragging = true;
            System.Diagnostics.Debug.WriteLine($"[DRAG] 🎨 Long-press visual armed for '{player.Name}'");
        });
    }

    private void OnNativeLongPressEnded(object? bindingContext)
    {
        if (bindingContext is not Player player) return;

        Dispatcher.Dispatch(() =>
        {
            // Keep active drag visuals until pan completion owns cleanup.
            if (ReferenceEquals(_panPlayer, player) && _panIntent == PanIntent.Drag)
                return;
            player.IsDragging = false;
        });
    }

    // ── Unified pan handler ───────────────────────────────────────────────
    //
    // ONE PanGestureRecognizer on the outer Grid handles both intents:
    //   • Horizontal (|dx| > |dy| after 12 dp)  → swipe to change position
    //   • Vertical   (|dy| > |dx| after 12 dp)  → drag to reorder
    //
    // This avoids the Android parent-wins problem that occurred when a child
    // Label had its own PanGestureRecognizer inside the Grid's recognizer.

    private enum PanIntent { Unknown, Swipe, Drag }

    // Per-touch-sequence state.
    private View?      _panRow;
    private Player?    _panPlayer;
    private PanIntent  _panIntent;

    // Drag state — these are DisplayItems (visual) indices, NOT Players indices.
    // Players is kept in insertion order; DisplayItems is sorted Field→Goalie→Bench.
    // We convert to Players indices only when calling ReorderPlayer.
    private int      _dragFromIndex;
    private int      _dragTargetIndex;
    private double   _rowHeight = 52;
    private Player?  _currentDragTarget;
    // Previous TotalX/Y from the last Running event. Using per-frame deltas instead
    // of absolute (Total - start) prevents layout-shift drift: each time a drag-target
    // BoxView appears/disappears the layout shifts and corrupts the absolute offset,
    // whereas deltas only care about how much the finger moved since the last frame.
    private double   _prevTotalX;
    private double   _prevTotalY;

    private const double IntentThreshold  = 6;   // dp before we commit to swipe or drag
    private const double SwipeThreshold   = 90;  // dp horizontal to commit a swipe
    private const double IosSwipeBump     = 34;  // visual travel before applying iOS swipe action
    private const uint   IosSwipeBumpMs   = 100;
    private const uint   IosSwipeReturnMs = 140;
    // Require a clearly horizontal movement to call it a swipe; anything more vertical
    // is treated as a drag so reordering feels immediate without a long-press.
    private const double SwipeDragBias    = 1.5; // |dx| must exceed |dy| * bias to be a swipe

    // ── Drag diagnostics ─────────────────────────────────────────────────
    // Set to true to suppress swipe entirely and force every pan into drag mode.
    // This lets drag be tested in isolation without horizontal gestures interfering.
    // IMPORTANT: revert to false before shipping.
    private const bool DragOnlyMode = false;

    // Running event counter — distinguishes a real drag stream from single-event noise.
    private int _panRunningCount;

    // Cumulative Y displacement since drag intent was committed (dp).
    // Used for index targeting so layout shifts don't corrupt the threshold math.
    private double _dragTotalY;
    private readonly HashSet<Player> _iosSwipeInFlight = new();

    private static View ResolvePanRowView(object sender)
    {
        if (sender is not View view)
            throw new InvalidOperationException("Pan sender is not a View.");

        // iOS: pan is attached only to the ☰ handle — translate the whole DragRow.
        var ancestor = view;
        while (ancestor is not null)
        {
            if (ancestor is DragRow)
                return ancestor;
            ancestor = ancestor.Parent as View;
        }

        return view;
    }

    private void OnPlayerPanUpdated(object sender, PanUpdatedEventArgs e)
    {
        if (_vm is null || _vm.IsMember) return;
        if (sender is not View) return;
        var row = ResolvePanRowView(sender);
        if (row.BindingContext is not Player player) return;

        switch (e.StatusType)
        {
            case GestureStatus.Started:
                // If _panRow is already set, the previous sequence ended without us
                // receiving Completed/Canceled (e.g. emulator focus loss, finger lift
                // outside the view). Clean up the orphaned state so the new gesture
                // isn't permanently blocked.
                if (_panRow is not null)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[DRAG] ⚠️ Started fired while _panRow was still set (orphaned state). Cleaning up previous row '{_panPlayer?.Name}'.");
                    _panRow.TranslationX = 0;
                    _panRow.TranslationY = 0;
                    if (_panPlayer is not null) _panPlayer.IsDragging = false;
                    ClearDragIndicator();
                    _panRow    = null;
                    _panPlayer = null;
                    _panIntent = PanIntent.Unknown;
                }

                _panRow    = row;
                _panPlayer = player;
                _panIntent = PanIntent.Unknown;
                _panRunningCount = 0;
                _nativeReleaseHandled = false; // reset one-shot guard for this new gesture
                // Capture actual row height from the live view so drag threshold
                // arithmetic matches the real pixel-to-dp row size. Fall back to
                // the default only if the height hasn't been measured yet (first frame).
                if (row.Height > 0)
                    _rowHeight = row.Height;
                System.Diagnostics.Debug.WriteLine(
                    $"[DRAG] 📏 Row height captured: {_rowHeight:F1} dp (view.Height={row.Height:F1})");
                // Use the visual (DisplayItems) index so drag arithmetic matches what the user sees.
                int visualFrom = -1;
                for (int vi = 0; vi < _vm.DisplayItems.Count; vi++)
                    if (ReferenceEquals(_vm.DisplayItems[vi], player)) { visualFrom = vi; break; }
                _dragFromIndex   = visualFrom;
                _dragTargetIndex = _dragFromIndex;
                _prevTotalX = e.TotalX;
                _prevTotalY = e.TotalY;
                _dragTotalY = 0;

                // If native long-press already fired before this pan stream started,
                // switch to drag visuals immediately (before the first movement frame).
                if (DragState.LongPressConfirmed)
                {
                    _panIntent = PanIntent.Drag;
                    player.IsDragging = true;
                    System.Diagnostics.Debug.WriteLine(
                        $"[DRAG] ✋ Long-press already confirmed at Started — pre-arming drag visuals for '{player.Name}'.");
                }
                System.Diagnostics.Debug.WriteLine(
                    $"[DRAG] ✅ Started — player='{player.Name}' visualFromIndex={_dragFromIndex} " +
                    $"playersIndex={_vm.Players.IndexOf(player)} displayCount={_vm.DisplayItems.Count} " +
                    $"rowHeight={_rowHeight:F1} DragOnlyMode={DragOnlyMode} intent={_panIntent}");
                break;

            case GestureStatus.Running:
                if (_panRow is null || _panPlayer is null) return;

                // Always operate on the locked row — the sender may be a different
                // row if the finger slides between items on Android.
                var activeRow    = _panRow;
                var activePlayer = _panPlayer;

                // MAUI's PanGestureRecognizer already delivers TotalX/Y in dp on all
                // platforms — no density conversion needed.
                double totalX = e.TotalX;
                double totalY = e.TotalY;

                _panRunningCount++;
                if (_panRunningCount == 1)
                    System.Diagnostics.Debug.WriteLine(
                        $"[DRAG] 🔄 First Running event — TotalX={totalX:F1} TotalY={totalY:F1}");

                // Commit to an intent once the finger has moved far enough.
                if (_panIntent == PanIntent.Unknown)
                {
                    if (Math.Abs(totalX) < IntentThreshold &&
                        Math.Abs(totalY) < IntentThreshold)
                    {
                        break; // not yet decided
                    }

                    if (DragOnlyMode)
                    {
                        // Isolation: ignore horizontal bias, always drag.
                        _panIntent = PanIntent.Drag;
                        System.Diagnostics.Debug.WriteLine(
                            $"[DRAG] 🔒 DragOnlyMode=true — forcing Drag intent. TotalX={totalX:F1} TotalY={totalY:F1}");
                    }
                    else
                    {
                        _panIntent = Math.Abs(totalX) >= Math.Abs(totalY) * SwipeDragBias
                                ? PanIntent.Swipe
                                : PanIntent.Drag;
                            System.Diagnostics.Debug.WriteLine(
                                $"[DRAG] 🎯 Intent decided: {_panIntent} — " +
                                $"TotalX={totalX:F1} TotalY={totalY:F1} bias={SwipeDragBias} " +
                                $"absX={Math.Abs(totalX):F1} vs absY*bias={Math.Abs(totalY) * SwipeDragBias:F1}");
                        }

                        // Drag requires long-press confirmation; swipe does not.
                        // If drag intent was detected but the user hasn't held long enough,
                        // discard this gesture — the parent scroller handles it.
                        if (_panIntent == PanIntent.Drag && !DragState.LongPressConfirmed)
                        {
                            System.Diagnostics.Debug.WriteLine("[DRAG] ⏳ Drag intent but long-press not confirmed — discarding");
                            _panRow    = null;
                            _panPlayer = null;
                            _panIntent = PanIntent.Unknown;
                            break;
                        }

                        // Seed prev values at commit point so the first delta is zero
                        // and the row begins translating smoothly from its resting position.
                        _prevTotalX = totalX;
                        _prevTotalY = totalY;
                        _dragTotalY = 0;

                        // Arm the visual drag indicator on the source row.
                        if (_panIntent == PanIntent.Drag && activePlayer is not null)
                            activePlayer.IsDragging = true;
                }

                // Per-frame delta — immune to layout shifts caused by BoxView
                // indicator rows appearing/disappearing as the drag target changes.
                double dx = totalX - _prevTotalX;
                double dy = totalY - _prevTotalY;
                _prevTotalX = totalX;
                _prevTotalY = totalY;

                if (_panIntent == PanIntent.Swipe)
                {
                    if (DragOnlyMode)
                    {
                        // Should not reach here in isolation mode, but guard anyway.
                        System.Diagnostics.Debug.WriteLine("[DRAG] ⛔ DragOnlyMode: suppressed swipe translation");
                    }
                    else
                    {
                        activeRow.TranslationX += dx;
                    }
                }
                else
                {
                    activeRow.TranslationY += dy;
                    _dragTotalY += dy;
                    // Log every 5th frame so the output is readable but complete.
                    if (_panRunningCount % 10 == 0)
                        System.Diagnostics.Debug.WriteLine(
                            $"[DRAG] 📍 Dragging '{activePlayer.Name}' — " +
                            $"dy={dy:F1} TranslationY={activeRow.TranslationY:F1} dragTotalY={_dragTotalY:F1} " +
                            $"visualFrom={_dragFromIndex} visualTarget={_dragTargetIndex} rowH={_rowHeight:F1} displayCount={_vm?.DisplayItems.Count}");
                    UpdateDragIndicator(activePlayer, _dragTotalY);
                }
                break;

            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                if (_panRow is null)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[DRAG] ⚠️ {e.StatusType} received but _panRow is null — gesture may have been stolen by ScrollView.");
                    break;
                }
                var endRow    = _panRow;
                var endPlayer = _panPlayer;

                System.Diagnostics.Debug.WriteLine(
                    $"[DRAG] 🏁 {e.StatusType} — intent={_panIntent} player='{endPlayer?.Name}' " +
                    $"runningEvents={_panRunningCount} from={_dragFromIndex} target={_dragTargetIndex} " +
                    $"TranslationY={endRow.TranslationY:F1}");

                ClearDragIndicator();

                if (_panIntent == PanIntent.Drag)
                {
                    endRow.TranslationY = 0;
                    if (endPlayer is not null) endPlayer.IsDragging = false;

                    if (e.StatusType == GestureStatus.Completed
                        && _dragTargetIndex != _dragFromIndex
                        && endPlayer is not null
                        && _vm is not null)
                    {
                        // _dragFromIndex / _dragTargetIndex are DisplayItems (visual) indices.
                        // ReorderPlayer expects Players (canonical) indices — look them up by reference.
                        var fromPlayer   = _vm.DisplayItems.ElementAtOrDefault(_dragFromIndex)   as Player;
                        var targetPlayer = _vm.DisplayItems.ElementAtOrDefault(_dragTargetIndex) as Player;
                        int playersFrom   = fromPlayer   is not null ? _vm.Players.IndexOf(fromPlayer)   : -1;
                        int playersTarget = targetPlayer is not null ? _vm.Players.IndexOf(targetPlayer) : -1;

                        System.Diagnostics.Debug.WriteLine(
                            $"[DRAG] ✅ Reordering '{endPlayer.Name}' visualFrom={_dragFromIndex} → visualTarget={_dragTargetIndex} " +
                            $"playersFrom={playersFrom} → playersTarget={playersTarget}");

                        if (playersFrom >= 0 && playersTarget >= 0)
                            _vm.ReorderPlayer(playersFrom, playersTarget);
                        else
                            System.Diagnostics.Debug.WriteLine(
                                $"[DRAG] ⚠️ Could not resolve Players indices — fromPlayer={fromPlayer?.Name ?? "null"} targetPlayer={targetPlayer?.Name ?? "null"}");
                    }
                    else if (_dragTargetIndex == _dragFromIndex)
                    {
                        System.Diagnostics.Debug.WriteLine("[DRAG] ℹ️ Drag ended at same index — no reorder.");
                    }
                }
                else
                {
                    CommitSwipe(endRow, endPlayer!);
                }

                _panRow    = null;
                _panPlayer = null;
                _panIntent = PanIntent.Unknown;
                DragState.LongPressConfirmed = false;
                break;
        }
    }

    // Snaps back immediately if threshold not met. If the threshold is met,
    // runs BOTH animation stages (bump + snap-back) to completion BEFORE calling
    // SetPlayerPosition. This is critical: SetPlayerPosition → RefreshDisplayItems
    // → DisplayItems.Move() fires RecyclerView notifyItemMoved, which can rebind
    // this view holder to a different player while the row is mid-animation —
    // orphaning the in-flight TranslationX and breaking the next gesture. By
    // completing the animation first the row is at TranslationX = 0 when
    // RecyclerView reorders, so there is nothing to fight against.
    private async void CommitSwipe(View row, Player player)
    {
        double totalX = row.TranslationX;

        System.Diagnostics.Debug.WriteLine(
            $"[SWIPE] CommitSwipe '{player.Name}' TranslationX={totalX:F1} threshold={SwipeThreshold}");

        if (Math.Abs(totalX) < SwipeThreshold)
        {
            System.Diagnostics.Debug.WriteLine("[SWIPE] Below threshold — snapping back");
            _ = row.TranslateTo(0, 0, 180, Easing.SpringOut);
            return;
        }

        bool swipeLeft   = totalX < 0;
        var  newPosition = swipeLeft
            ? (player.Position == PlayerPosition.Field
                ? PlayerPosition.Goalie
                : PlayerPosition.Field)
            : (player.Position == PlayerPosition.Bench
                ? PlayerPosition.Inactive
                : PlayerPosition.Bench);

        System.Diagnostics.Debug.WriteLine(
            $"[SWIPE] ✅ Committed — swipeLeft={swipeLeft} '{player.Position}' → '{newPosition}'");

        // Stage 1: bump in the swipe direction.
        double bump = swipeLeft ? -30 : 30;
        await row.TranslateTo(bump, 0, 60, Easing.CubicOut);

        // Stage 2: snap back to rest.  Row is at TranslationX == 0 before the
        // collection is mutated, so RecyclerView notifyItemMoved fires cleanly.
        await row.TranslateTo(0, 0, 140, Easing.SpringOut);

        System.Diagnostics.Debug.WriteLine(
            $"[SWIPE] 🎬 Animation complete — calling SetPlayerPosition for '{player.Name}'");

        // Apply the state change now that the animation is fully settled.
        _vm?.SetPlayerPosition(player, newPosition);
    }

    private void UpdateDragIndicator(Player dragging, double totalY)
    {
        // Move insertion target by whole rows (rounded to nearest slot) so the
        // yellow line stays visually anchored to the dragged row/handle position.
        double snapUnit = Math.Max(_rowHeight, 10);
        int delta = (int)Math.Round(totalY / snapUnit, MidpointRounding.AwayFromZero);
        // Clamp against DisplayItems (visual list) so we never address a slot that
        // doesn't exist on screen — Players may be a different size/order.
        int maxVisual = (_vm?.DisplayItems.Count ?? 1) - 1;
        int newTarget = Math.Clamp(_dragFromIndex + delta, 0, maxVisual);

        System.Diagnostics.Debug.WriteLine(
            $"[DRAG] 🎯 UpdateDragIndicator — totalY={totalY:F1} snapUnit={snapUnit:F1} delta={delta} " +
            $"fromVisual={_dragFromIndex} newTarget={newTarget} maxVisual={maxVisual}");

        if (newTarget == _dragTargetIndex) return;

        ClearDragIndicator();
        _dragTargetIndex = newTarget;

        if (newTarget != _dragFromIndex)
        {
            // Look up the player at the visual target slot in DisplayItems.
            _currentDragTarget = _vm?.DisplayItems.ElementAtOrDefault(newTarget) as Player;
            if (_currentDragTarget is not null)
            {
                _currentDragTarget.IsDragTarget = true;
                System.Diagnostics.Debug.WriteLine(
                    $"[DRAG] 🔴 IsDragTarget=true on '{_currentDragTarget.Name}' (visualIdx={newTarget})");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[DRAG] ⚠️ No Player found at DisplayItems[{newTarget}] (header or out of range)");
            }
        }
    }

    private void ClearDragIndicator()
    {
        if (_currentDragTarget is null) return;
        _currentDragTarget.IsDragTarget = false;
        _currentDragTarget = null;
    }

#if ANDROID

#endif



    // ── Inactive group header tap ─────────────────────────────────────────

    private void OnInactiveHeaderTapped(object sender, TappedEventArgs e)
    {
        _vm?.ToggleInactiveExpanded();
    }

    // ── Header taps ───────────────────────────────────────────────────────

    private async void OnMatchTimerTapped(object sender, TappedEventArgs e)
    {
        if (_vm is null || _vm.Phase != GamePhase.Setup || _vm.IsMember) return;

        var result = await DisplayPromptAsync(
            "Match Duration",
            "Enter total match time in minutes (e.g. 90):",
            initialValue: $"{_vm.MatchDurationMinutes}",
            keyboard: Keyboard.Numeric);

        if (result is null) return;
        if (int.TryParse(result, out var m) && m > 0 && m <= 999)
            _vm.SetMatchDuration(m);
        else
            await DisplayAlert("Invalid", "Please enter a number between 1 and 999.", "OK");
    }

    private async void OnCountdownTimerTapped(object sender, TappedEventArgs e)
    {
        if (_vm is null || _vm.IsMember) return;

        var result = await DisplayPromptAsync(
            "Rotation Countdown",
            "Enter rotation time as MM:SS (e.g. 2:00).\nEnter \"Auto\" to calculate optimal.",
            initialValue: _vm.CountdownDisplay,
            keyboard: Keyboard.Default,
            accept: "Set",
            cancel: "Cancel");

        if (result is null) return;

        if (result.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            var opt = _vm.CalculateOptimalRotationTime();
            if (opt.HasValue)
                _vm.SetCountdownPreset(opt.Value.minutes, opt.Value.seconds);
            else
                await DisplayAlert("Auto", "Assign field and bench players first.", "OK");
            return;
        }

        var parts = result.Split(':');
        if (parts.Length == 2
            && int.TryParse(parts[0], out var mm) && mm >= 0
            && int.TryParse(parts[1], out var ss) && ss >= 0 && ss < 60
            && (mm * 60 + ss) > 0)
        {
            _vm.SetCountdownPreset(mm, ss);
        }
        else
        {
            await DisplayAlert("Invalid", "Use MM:SS format, e.g. 2:30.", "OK");
        }
    }

    // ── Player name edit ──────────────────────────────────────────────────

    private async void OnPlayerSwiped(object sender, SwipedEventArgs e)
    {
        if (_vm is null || _vm.IsMember) return;
        if (sender is not BindableObject { BindingContext: Player player }) return;
        if (sender is not View row) return;
        if (!_iosSwipeInFlight.Add(player)) return;

        try
        {
            bool swipeLeft = e.Direction == SwipeDirection.Left;
            var newPosition = swipeLeft
                ? (player.Position == PlayerPosition.Field
                    ? PlayerPosition.Goalie
                    : PlayerPosition.Field)
                : (player.Position == PlayerPosition.Bench
                    ? PlayerPosition.Inactive
                    : PlayerPosition.Bench);

            System.Diagnostics.Debug.WriteLine(
                $"[SWIPE] iOS SwipeGesture — swipeLeft={swipeLeft} '{player.Position}' → '{newPosition}'");

            // iOS uses SwipeGestureRecognizer (no live pan translation), so animate a
            // short directional bump first to match Android's visible swipe affordance.
            double bump = swipeLeft ? -IosSwipeBump : IosSwipeBump;
            await row.TranslateTo(bump, 0, IosSwipeBumpMs, Easing.CubicOut);
            await row.TranslateTo(0, 0, IosSwipeReturnMs, Easing.SpringOut);
            _vm.SetPlayerPosition(player, newPosition);
        }
        finally
        {
            row.TranslationX = 0;
            _iosSwipeInFlight.Remove(player);
        }
    }

    private async void OnPlayerNameTapped(object sender, TappedEventArgs e)
    {
        if (_vm is null) return;
        // View-only: cloud members, Watch Only, or locked by another controller.
        if (_vm.IsMember) return;
        if (sender is not BindableObject bindable) return;
        if (bindable.BindingContext is not Player player) return;

        if (_vm.Phase == GamePhase.Setup)
        {
            // Pre-game: rename the player.
            var result = await DisplayPromptAsync(
                title: "Rename Player",
                message: string.Empty,
                accept: "Save",
                cancel: "Cancel",
                placeholder: player.Name,
                initialValue: player.Name,
                keyboard: Keyboard.Default);

            if (result is null) return;
            _vm.RenamePlayer(player, result);
        }
        else
        {
            // Live: Manual uses Bench→Field pair seeding; other bases auto-pair via TapPlayerQueue.
            if (_vm.IsManualRotationBasis)
                _vm.TryManualPairTap(player);
            else
                _vm.TapPlayerQueue(player);
        }
    }

    // ── Bottom button handlers ────────────────────────────────────────────

    private async void OnSessionViewOnlyToggleClicked(object sender, EventArgs e)
    {
        if (_vm is null || !_vm.CanUseSessionViewOnly) return;

        try
        {
            if (_vm.IsSessionViewOnly)
            {
                var ok = await DisplayAlertAsync(
                    "Take Control?",
                    "You will run the game on this device again (setup only).\n\n" +
                    "During a live match, only one Admin holds control — use Request control on the yellow banner.",
                    "Take Control",
                    "Cancel");
                if (!ok) return;
                await _vm.SetSessionViewOnlyAsync(false);
            }
            else
            {
                var ok = await DisplayAlertAsync(
                    "Watch Only?",
                    "This device will be view-only until you tap Take Control.\n\n" +
                    "Your Admin role is unchanged. During a live match started by another Admin, " +
                    "use Request control on the yellow banner instead.",
                    "Watch Only",
                    "Cancel");
                if (!ok) return;
                await _vm.SetSessionViewOnlyAsync(true);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GamePage] SessionViewOnly toggle: {ex.Message}");
            await DisplayAlertAsync("Error", "Could not change control mode.", "OK");
        }
    }

    private async void OnViewOnlyBannerTapped(object? sender, TappedEventArgs e)
    {
        if (_vm is null) return;

        try
        {
            // Vacant control after Relinquish / server auto-release
            if (_vm.CanTakeVacantControl)
            {
                var take = await DisplayAlertAsync(
                    "Take Control?",
                    "No Admin is controlling this match right now.\n\n" +
                    "Take control to run timers and rotations on this device.",
                    "Take Control",
                    "Cancel");
                if (!take) return;

                var takeResult = await _vm.TakeVacantControlAsync();
                if (takeResult == "success")
                {
                    await DisplayAlertAsync("You have control", "You are now running the match.", "OK");
                }
                else
                {
                    var msg = takeResult.StartsWith("error:", StringComparison.Ordinal)
                        ? takeResult["error:".Length..].Trim()
                        : takeResult;
                    await DisplayAlertAsync("Could Not Take Control", msg, "OK");
                }
                return;
            }

            if (!_vm.CanRequestControl) return;

            var who = _vm.ControllerDisplayName;
            var ok = await DisplayAlertAsync(
                "Request Control?",
                $"Ask {who} to hand over match control to you?\n\n" +
                "They will get Accept / Reject on their device. Only one Admin can control at a time.",
                "Request",
                "Cancel");
            if (!ok) return;

            var result = await _vm.RequestControlAsync();
            if (result == "success")
            {
                await DisplayAlertAsync(
                    "Request Sent",
                    $"Waiting for {who} to accept or reject…\n\n" +
                    "Keep the Game tab open on their device.",
                    "OK");
            }
            else
            {
                var msg = result.StartsWith("error:", StringComparison.Ordinal)
                    ? result["error:".Length..].Trim()
                    : result;
                await DisplayAlertAsync("Could Not Request", msg, "OK");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GamePage] Banner control action: {ex.Message}");
            await DisplayAlertAsync("Error", "Could not complete control action.", "OK");
        }
    }

    private async void OnControlRequestReceived(object? sender, (string RequesterName, string RequestId) e)
    {
        if (_vm is null) return;

        try
        {
            var accept = await DisplayAlertAsync(
                "Control Request",
                $"{e.RequesterName} wants to control the match.\n\n" +
                "Accept to hand over control (you become view-only). Reject to keep control.",
                "Accept",
                "Reject");

            if (accept)
                await _vm.AcceptControlRequestAsync(e.RequestId);
            else
                await _vm.RejectControlRequestAsync(e.RequestId);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GamePage] Control request dialog: {ex.Message}");
        }
    }

    private async void OnStartClicked(object sender, EventArgs e)
    {
        if (_vm is null) return;

        // Setup with no field/goalie roles: explain how to assign instead of silently no-op
        if (_vm.Phase == GamePhase.Setup && !_vm.HasPlayersReadyToStart)
        {
            await DisplayAlertAsync(
                "Assign Players First",
                "Assign players to roles on the roster before starting:\n\n" +
                "• Swipe left = Field\n" +
                "• Swipe right = Bench\n" +
                "• Swipe left ×2 = Goalie",
                "OK");
            return;
        }

        // Capture before toggle so we only auto-switch on the real "Start" from setup.
        var startingFromSetup = _vm.Phase == GamePhase.Setup;

        _vm.ToggleStartPause();
        _vm.RotationDue = false;

        // After kickoff, show the next-rotation call-out immediately.
        if (startingFromSetup
            && _vm.Phase is GamePhase.FirstHalf or GamePhase.SecondHalf)
        {
            _vm.ViewMode = TeamViewMode.Rotation;
            ApplyViewMode(TeamViewMode.Rotation);
        }
    }

    private async void OnStartPressed(object sender, EventArgs e)
    {
        _startLongPressCts?.Cancel();
        _startLongPressCts = new CancellationTokenSource();
        var token = _startLongPressCts.Token;
        try
        {
            await Task.Delay(1000, token);
            if (_vm is not null && !_vm.IsMember)
            {
                var confirmed = await DisplayAlert("Restart Game",
                    "Restart the match from the beginning?", "Restart", "Cancel");
                if (confirmed) _vm.RestartGameCommand();
            }
        }
        catch (OperationCanceledException) { }
    }

    private void OnStartReleased(object sender, EventArgs e)
        => _startLongPressCts?.Cancel();

    private void OnRotateClicked(object sender, EventArgs e)
    {
        // Long-press already opened the rotation-count sheet — do not also rotate.
        if (_rotateLongPressHandled)
        {
            _rotateLongPressHandled = false;
            return;
        }

        ExecuteRotationTrigger("button");
    }

    private async void OnRotatePressed(object sender, EventArgs e)
    {
        _rotateLongPressHandled = false;
        _rotateLongPressCts?.Cancel();
        _rotateLongPressCts = new CancellationTokenSource();
        var token = _rotateLongPressCts.Token;
        try
        {
            await Task.Delay(1000, token);
            if (_vm is null || _vm.IsMember) return;

            _rotateLongPressHandled = true;
            await ShowRotationCountDialog();
        }
        catch (OperationCanceledException) { /* short press — Clicked will rotate */ }
    }

    private void OnRotateReleased(object sender, EventArgs e)
        => _rotateLongPressCts?.Cancel();

    private void OnTapToRotateClicked(object sender, EventArgs e)
    {
        if (_tapToRotateLongPressHandled)
        {
            _tapToRotateLongPressHandled = false;
            return;
        }

        ExecuteRotationTrigger("tap-to-rotate zone");
    }

    private async void OnTapToRotatePressed(object sender, EventArgs e)
    {
        _tapToRotateLongPressHandled = false;
        _tapToRotateLongPressCts?.Cancel();
        _tapToRotateLongPressCts = new CancellationTokenSource();
        var token = _tapToRotateLongPressCts.Token;
        try
        {
            await Task.Delay(1000, token);
            if (_vm is null || _vm.IsMember) return;

            _tapToRotateLongPressHandled = true;
            await ShowRotationCountDialog();
        }
        catch (OperationCanceledException) { /* short press — Clicked will rotate */ }
    }

    private void OnTapToRotateReleased(object sender, EventArgs e)
        => _tapToRotateLongPressCts?.Cancel();

    private void ExecuteRotationTrigger(string source)
    {
        if (_vm is null || _vm.IsMember) return;

        System.Diagnostics.Debug.WriteLine($"[GamePage] 👆 Rotate triggered from {source}");
        _vm.ExecuteRotations();
        _vm.RotationDue = false;
        AnimateRotateBtn();

        // Resync: scroll the roster back to the first row so the user can
        // immediately see who has just rotated onto the field.
        if (SwipeableRoster.IsVisible && _vm.DisplayItems.Count > 0)
            SwipeableRoster.ScrollTo(0, position: ScrollToPosition.Start, animate: false);
    }

    private const string ResetRotationClockOption = "Reset Clk";

    /// <summary>
    /// Long-press sheet: Reset Clk (restart rotation countdown, no swaps), then 1 … max bench.
    /// Changing the count fully reseeds FIFO queues.
    /// </summary>
    private async Task ShowRotationCountDialog()
    {
        if (_vm is null || _vm.IsMember) return;

        var max = _vm.MaxRotationCount;
        // Still allow Reset Clk even when no bench players are assigned yet.
        var countOptions = max >= 1
            ? Enumerable.Range(1, max).Select(n => n.ToString())
            : Enumerable.Empty<string>();
        var options = new[] { ResetRotationClockOption }.Concat(countOptions).ToArray();

        var result = await DisplayActionSheetAsync(
            "Rotate how many players?",
            "Cancel",
            destruction: null,
            options);

        if (result is null || result == "Cancel") return;

        if (result == ResetRotationClockOption)
        {
            _vm.ResetRotationClock();
            System.Diagnostics.Debug.WriteLine("[GamePage] ⏱ Reset Clk — rotation countdown restarted");
            return;
        }

        if (max < 1)
        {
            await DisplayAlertAsync("Rotation Count", "Assign bench players first.", "OK");
            return;
        }

        if (!int.TryParse(result, out var count)) return;

        var previous = _vm.RotationCount;
        _vm.RotationCount = count;
        if (count != previous)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[GamePage] 🔢 RotationCount changed {previous} → {count} — full reseed");
            _vm.ReseedRotationQueues();
        }
    }

    private void OnViewButtonClicked(object sender, EventArgs e)
    {
        if (_vm is null || _vm.IsMember) return;
        var next = _vm.ViewMode switch
        {
            TeamViewMode.Swipeable => TeamViewMode.Rotation,
            TeamViewMode.Rotation  => TeamViewMode.Field,
            _                      => TeamViewMode.Swipeable
        };
        _vm.ViewMode = next;
        ApplyViewMode(next);
    }

    // ── Score buttons ─────────────────────────────────────────────────────
    // Android does not reliably fire two TapGestureRecognizers with different
    // NumberOfTapsRequired on the same element.  We handle both in a single
    // handler using a short-interval timer: second tap within 300 ms = undo (−1).

    private const int DoubleTapWindowMs = 300;

    private DateTime _lastTeamATap = DateTime.MinValue;
    private CancellationTokenSource? _teamATapCts;

    private DateTime _lastTeamBTap = DateTime.MinValue;
    private CancellationTokenSource? _teamBTapCts;

    private async void OnTeamAScoreTapped(object sender, TappedEventArgs e)
    {
        // View-only / locked co-admin: scores are display-only (controller owns scoring).
        if (_vm is null || _vm.IsMember) return;

        var now = DateTime.UtcNow;
        if ((now - _lastTeamATap).TotalMilliseconds <= DoubleTapWindowMs)
        {
            // Double-tap: cancel the pending increment, decrement instead.
            _teamATapCts?.Cancel();
            _lastTeamATap = DateTime.MinValue;
            _vm.DecrementTeamAScore();
            return;
        }

        _lastTeamATap = now;
        _teamATapCts?.Cancel();
        _teamATapCts = new CancellationTokenSource();
        var cts = _teamATapCts;

        try
        {
            await Task.Delay(DoubleTapWindowMs, cts.Token);

            // Show goal detail modal only when scorer/assist reporting is enabled.
            if (!GoalScoringOptions.IsScorerAssistEnabled())
            {
                _vm.IncrementTeamAScore();
                return;
            }

            var fieldPlayers = _vm.GetFieldPlayers();
            var modal = new GoalDetailModal(fieldPlayers, async (scorer, assist) =>
            {
                _vm.IncrementTeamAScore(scorer, assist);
                await Task.CompletedTask;
            });

            await Navigation.PushModalAsync(modal);
        }
        catch (TaskCanceledException) { /* superseded by double-tap */ }
    }

    private async void OnTeamBScoreTapped(object sender, TappedEventArgs e)
    {
        // View-only / locked co-admin: scores are display-only (controller owns scoring).
        if (_vm is null || _vm.IsMember) return;

        var now = DateTime.UtcNow;
        if ((now - _lastTeamBTap).TotalMilliseconds <= DoubleTapWindowMs)
        {
            _teamBTapCts?.Cancel();
            _lastTeamBTap = DateTime.MinValue;
            _vm.DecrementTeamBScore();
            return;
        }

        _lastTeamBTap = now;
        _teamBTapCts?.Cancel();
        _teamBTapCts = new CancellationTokenSource();
        var cts = _teamBTapCts;

        try
        {
            await Task.Delay(DoubleTapWindowMs, cts.Token);
            _vm.IncrementTeamBScore();
        }
        catch (TaskCanceledException) { /* superseded by double-tap */ }
    }

    // ── View switching ────────────────────────────────────────────────────

    private void ApplyViewMode(TeamViewMode mode)
    {
        // Cloud snapshot / InitialiseAsync can raise ViewMode/IsMember off the UI thread.
        // Touching IsVisible there crashes iOS with UIKitThreadAccessException (SIGABRT).
        if (!MainThread.IsMainThread)
        {
            MainThread.BeginInvokeOnMainThread(() => ApplyViewMode(mode));
            return;
        }

        // View-only always stays on Field View (no Team / Rotation access).
        var effective = _vm?.IsMember == true ? TeamViewMode.Field : mode;

        SwipeableRoster.IsVisible = effective == TeamViewMode.Swipeable;
        RotationView.IsVisible    = effective == TeamViewMode.Rotation;
        FieldView.IsVisible       = effective == TeamViewMode.Field;
        UpdateViewButtonText(effective);

        if (effective == TeamViewMode.Field)
            AlignFieldViewBackground();
    }

    /// <summary>
    /// Aspect-fill the pitch art and bottom-align it in Field View.
    /// Painted stadium benches were removed from field_view_bg.png (720×940); only the UI Bench remains.
    /// </summary>
    private void OnFieldViewSizeChanged(object? sender, EventArgs e)
        => AlignFieldViewBackground();

    private void AlignFieldViewBackground()
    {
        if (FieldViewBackground is null || FieldView is null) return;
        if (!FieldView.IsVisible) return;

        var viewW = FieldView.Width;
        var viewH = FieldView.Height;
        if (viewW <= 0 || viewH <= 0) return;

        // Intrinsic size of Resources/Images/field_view_bg.png
        // (full art 1280px; bottom cropped to leave one painted bench row under the goal)
        const double imgW = 720;
        const double imgH = 1035;

        var scale = Math.Max(viewW / imgW, viewH / imgH);
        var drawnW = imgW * scale;
        var drawnH = imgH * scale;

        FieldViewBackground.WidthRequest = drawnW;
        FieldViewBackground.HeightRequest = drawnH;
        FieldViewBackground.Margin = new Thickness(0);
        // Top-left layout, then shift to center horizontally and bottom-align vertically.
        FieldViewBackground.TranslationX = (viewW - drawnW) / 2.0;
        FieldViewBackground.TranslationY = viewH - drawnH;

        System.Diagnostics.Debug.WriteLine(
            $"[GamePage] FieldBg align view={viewW:0}x{viewH:0} drawn={drawnW:0}x{drawnH:0} " +
            $"ty={FieldViewBackground.TranslationY:0.0}");
    }

    private void UpdateViewButtonText(TeamViewMode mode)
    {
        // Button text shows what view will be shown when pressed (admin only).
        ViewBtn.Text = mode switch
        {
            TeamViewMode.Swipeable => "View: Rotation",
            TeamViewMode.Rotation  => "View: Field",
            _                      => "View: Team"
        };
    }

    // ── Keep screen on ────────────────────────────────────────────────────

    private static void SetKeepScreenOn(bool on)
    {
#if ANDROID
        var activity = Platform.CurrentActivity;
        if (activity?.Window is { } window)
        {
            if (on)
                    window.AddFlags(Android.Views.WindowManagerFlags.KeepScreenOn);
                else
                    window.ClearFlags(Android.Views.WindowManagerFlags.KeepScreenOn);
        }
#endif
    }

    // ── Rotation alert (vibrate + flash) ─────────────────────────────────

    /// <summary>High-contrast timer white (matches iOS / sideline readability on dark header).</summary>
    private static readonly Color TimerBrightWhite = Color.FromArgb("#FFFFFF");
    private static readonly Color TimerOverdueRed  = Color.FromArgb("#FF1744");

    // Track previous overdue states so we can detect the false→true edge
    // and vibrate exactly once when a timer first crosses zero.
    private bool _wasMatchTimerOverdue;
    private bool _wasCountdownOverdue;

    // Animation cancellation tokens — cancelled when overdue ends.
    private CancellationTokenSource? _matchPulseCts;
    private CancellationTokenSource? _countdownPulseCts;

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GameViewModel.RotationDue) && _vm?.RotationDue == true)
            _ = TriggerRotationAlertAsync();

        if (e.PropertyName == nameof(GameViewModel.RotationWarning) && _vm?.RotationWarning == true)
            _ = TriggerRotationWarningAsync();

        if (e.PropertyName == nameof(GameViewModel.MatchTimerOverdue))
            HandleMatchTimerOverdue(_vm?.MatchTimerOverdue == true);

        if (e.PropertyName == nameof(GameViewModel.CountdownOverdue))
            HandleCountdownOverdue(_vm?.CountdownOverdue == true);

        // Role or shared ViewMode changes: re-apply Team / Rotation / Field visibility.
        if (e.PropertyName is nameof(GameViewModel.IsMember)
            or nameof(GameViewModel.IsAdmin)
            or nameof(GameViewModel.ViewMode))
        {
            if (_vm is not null)
                ApplyViewMode(_vm.ViewMode);
        }
    }

    /// <summary>
    /// Force match + rotate timer labels to bright white full opacity.
    /// Android can pick up the app-wide Label theme (dull grey on light AppTheme) unless we re-assert.
    /// </summary>
    private void EnsureTimerLabelContrast()
    {
        if (MatchTimerLabel is not null && _matchPulseCts is null)
        {
            MatchTimerLabel.TextColor = TimerBrightWhite;
            MatchTimerLabel.Opacity = 1;
        }
        if (CountdownLabel is not null && _countdownPulseCts is null)
        {
            CountdownLabel.TextColor = TimerBrightWhite;
            CountdownLabel.Opacity = 1;
        }
    }

    private void HandleMatchTimerOverdue(bool overdue)
    {
        if (overdue && !_wasMatchTimerOverdue)
        {
            // Crossed zero — vibrate once.
            try { Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(1000)); }
            catch { /* not supported */ }
        }
        _wasMatchTimerOverdue = overdue;

        if (overdue)
            StartOverduePulse(ref _matchPulseCts, MatchTimerLabel, StartBtn);
        else
            StopOverduePulse(ref _matchPulseCts, MatchTimerLabel, StartBtn);
    }

    private void HandleCountdownOverdue(bool overdue)
    {
        if (overdue && !_wasCountdownOverdue)
        {
            try { Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(1000)); }
            catch { /* not supported */ }
        }
        _wasCountdownOverdue = overdue;

        if (overdue)
            StartOverduePulse(ref _countdownPulseCts, CountdownLabel, RotateBtn);
        else
            StopOverduePulse(ref _countdownPulseCts, CountdownLabel, RotateBtn);
    }

    private static void StartOverduePulse(
        ref CancellationTokenSource? cts,
        Label label,
        Button button)
    {
        // Already pulsing — nothing to do.
        if (cts is not null) return;

        label.TextColor  = TimerOverdueRed;
        button.TextColor = TimerOverdueRed;

        var token = new CancellationTokenSource();
        cts = token;
        _ = RunPulseLoopAsync(label, button, token.Token);
    }

    private static void StopOverduePulse(
        ref CancellationTokenSource? cts,
        Label label,
        Button button)
    {
        cts?.Cancel();
        cts = null;

        // Restore normal high-contrast appearance (bright white, full opacity).
        label.CancelAnimations();
        button.CancelAnimations();
        label.Opacity  = 1;
        button.Opacity = 1;
        label.TextColor  = TimerBrightWhite;
        button.TextColor = TimerBrightWhite;
    }

    /// <summary>
    /// Loops a smooth opacity pulse (1→0.2→1) on the two views until
    /// the cancellation token is cancelled.
    /// </summary>
    private static async Task RunPulseLoopAsync(
        VisualElement label,
        VisualElement button,
        CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.WhenAll(
                    label.FadeTo(0.2, 500, Easing.SinInOut),
                    button.FadeTo(0.2, 500, Easing.SinInOut));

                if (ct.IsCancellationRequested) break;

                await Task.WhenAll(
                    label.FadeTo(1.0, 500, Easing.SinInOut),
                    button.FadeTo(1.0, 500, Easing.SinInOut));
            }
        }
        catch (TaskCanceledException) { /* normal cancellation */ }
    }

    private async Task TriggerRotationAlertAsync()
    {
        // Vibrate for the configured Rotation Duration (default 1 s).
        var durationMs = Preferences.Get("game.rotationDurationMs", 1000);
        try { Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(durationMs)); }
        catch { /* vibration not supported on this device */ }

        // Clear the flag first so the VM can continue (e.g. start the next
        // countdown) while the animation plays.
        if (_vm is not null)
            _vm.RotationDue = false;

        // Yield one frame so the rotation layout rebuild (RefreshDisplayItems +
        // DragLayoutViewGroup inflate) can complete before we start competing
        // for the UI thread with six Animate() callbacks.
        await Task.Delay(200);

        // Flash the page background white for the same duration as the vibration.
        await FlashBackgroundAsync(durationMs);
    }

    private Task TriggerRotationWarningAsync()
    {
        // Vibrate for the configured Rotation Warning Duration (default 0.5 s).
        var durationMs = Preferences.Get("game.rotationWarningDurationMs", 500);
        try { Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(durationMs)); }
        catch { /* vibration not supported on this device */ }

        if (_vm is not null)
            _vm.RotationWarning = false;

        return Task.CompletedTask;
    }

    private async Task FlashBackgroundAsync(int durationMs = 240)
    {
        var orig = BackgroundColor ?? Colors.Black;
        float or = (float)orig.Red, og = (float)orig.Green, ob = (float)orig.Blue;

        // Each flash cycle is 60 ms in + 60 ms out = 120 ms.
        // Run enough cycles to fill the requested duration (at least 1).
        int cycles = Math.Max(1, durationMs / 120);
        for (int i = 0; i < cycles; i++)
        {
            var tcsIn  = new TaskCompletionSource();
            var tcsOut = new TaskCompletionSource();

            this.Animate("flashIn",
                callback: t => BackgroundColor = new Color(
                    or + (1f - or) * (float)t,
                    og + (1f - og) * (float)t,
                    ob + (1f - ob) * (float)t),
                length: 60,
                finished: (_, _) => tcsIn.TrySetResult());
            await tcsIn.Task;

            this.Animate("flashOut",
                callback: t => BackgroundColor = new Color(
                    1f - (1f - or) * (float)t,
                    1f - (1f - og) * (float)t,
                    1f - (1f - ob) * (float)t),
                length: 60,
                finished: (_, _) => tcsOut.TrySetResult());
            await tcsOut.Task;
        }

        BackgroundColor = orig;
    }

    // ── Rotate button animation ───────────────────────────────────────────

    private async void AnimateRotateBtn()
    {
        await RotateBtn.ScaleTo(0.92, 80);
        await RotateBtn.ScaleTo(1.0,  80);
    }

#if ANDROID
    // ── Android clip-children fix is no longer needed ─────────────────────
    // SwipeableRoster is now a ScrollView (not CollectionView/RecyclerView),
    // so there is no RecyclerView to intercept touches or clip children.
#endif

    // ── Field View drag / drop placement ──────────────────────────────────

    private const string FieldDragSlotIdKey = "turftime.fieldDrag.slotId";

    /// <summary>Cancels a pending single-tap arm when a double-tap rename arrives.</summary>
    private CancellationTokenSource? _fieldSingleTapCts;

    /// <summary>Slot waiting to be armed after the double-tap delay window.</summary>
    private int? _pendingFieldTapSlotId;

    /// <summary>
    /// Android often drops custom <see cref="DataPackage.Properties"/> during drag;
    /// keep a page-level fallback for the active drag source.
    /// </summary>
    private int _activeDragSlotId;

    private void OnFieldPlayerDragStarting(object? sender, DragStartingEventArgs e)
    {
        if (_vm is null || _vm.IsMember)
        {
            e.Cancel = true;
            return;
        }

        Player? player = null;
        if (sender is BindableObject bo)
        {
            if (bo.BindingContext is FieldCellSlot slot)
                player = slot.Player;
            else if (bo.BindingContext is Player p)
                player = p;
        }

        if (player is null)
        {
            e.Cancel = true;
            return;
        }

        CancelPendingFieldSingleTap();
        _vm.ClearFieldTapPlaceSelection();
        BeginFieldDrag(player.SlotId, e);
    }

    private void BeginFieldDrag(int slotId, DragStartingEventArgs e)
    {
        _activeDragSlotId = slotId;
        e.Data.Properties[FieldDragSlotIdKey] = slotId;
        // Text payload helps Android's drop pipeline accept the gesture.
        e.Data.Text = slotId.ToString();
    }

    private void OnFieldCellDragOver(object? sender, DragEventArgs e)
        => e.AcceptedOperation = (_vm is not null && !_vm.IsMember)
            ? DataPackageOperation.Copy
            : DataPackageOperation.None;

    private void OnFieldZoneDragOver(object? sender, DragEventArgs e)
        => OnFieldCellDragOver(sender, e);

    private void OnFieldCellDrop(object? sender, DropEventArgs e)
    {
        if (_vm is null || _vm.IsMember) return;
        if (!TryGetDraggedPlayer(e, out var player)) return;
        if (sender is not BindableObject { BindingContext: FieldCellSlot slot }) return;

        _vm.ClearFieldTapPlaceSelection();
        _vm.PlaceOrSwapOnFieldCell(player, slot.CellNumber);
    }

    private void OnFieldCellTapped(object? sender, TappedEventArgs e)
    {
        if (_vm is null || _vm.IsMember) return;
        if (sender is not BindableObject { BindingContext: FieldCellSlot slot }) return;
        FlushPendingFieldSingleTap();
        if (!_vm.HasFieldTapPlaceSelection) return;
        _vm.TryCompleteFieldTapPlaceOnCell(slot.CellNumber);
    }

    private void OnBenchBandTapped(object? sender, TappedEventArgs e)
    {
        if (_vm is null || _vm.IsMember) return;
        FlushPendingFieldSingleTap();
        System.Diagnostics.Debug.WriteLine(
            $"[GamePage] Bench TAP armed={_vm.HasFieldTapPlaceSelection} slot={_vm.FieldTapPlaceSlotId}");
        var ok = _vm.TryCompleteFieldTapPlaceOnPosition(PlayerPosition.Bench);
        System.Diagnostics.Debug.WriteLine($"[GamePage] Bench TAP complete ok={ok}");
    }

    private void OnGoalieBandTapped(object? sender, TappedEventArgs e)
    {
        if (_vm is null || _vm.IsMember) return;
        FlushPendingFieldSingleTap();
        _vm.TryCompleteFieldTapPlaceOnPosition(PlayerPosition.Goalie);
    }

    private void OnInactiveBandTapped(object? sender, TappedEventArgs e)
    {
        if (_vm is null || _vm.IsMember) return;
        FlushPendingFieldSingleTap();
        // Live Field→Absent is blocked inside TryComplete; Setup still allows move-only.
        _vm.TryCompleteFieldTapPlaceOnPosition(PlayerPosition.Inactive);
    }

    private void OnBenchBandDragOver(object? sender, DragEventArgs e)
    {
        OnFieldCellDragOver(sender, e);
        var can = CanAcceptFieldDrop(e);
        System.Diagnostics.Debug.WriteLine($"[GamePage] Bench DragOver can={can} sender={sender?.GetType().Name}");
        if (BenchDropHint is not null)
            BenchDropHint.IsVisible = can;
    }

    private void OnBenchBandDragLeave(object? sender, DragEventArgs e)
    {
        if (BenchDropHint is not null)
            BenchDropHint.IsVisible = false;
    }

    private void OnBenchBandDrop(object? sender, DropEventArgs e)
    {
        if (BenchDropHint is not null)
            BenchDropHint.IsVisible = false;
        if (_vm is null || _vm.IsMember) return;
        if (!TryGetDraggedPlayer(e, out var player))
        {
            System.Diagnostics.Debug.WriteLine("[GamePage] Bench DROP — no dragged player in package");
            return;
        }

        System.Diagnostics.Debug.WriteLine(
            $"[GamePage] Bench DROP ← {player.Name} ({player.Position}) live={_vm.IsMatchInProgress} sender={sender?.GetType().Name}");
        CancelPendingFieldSingleTap();
        _vm.ClearFieldTapPlaceSelection();

        // Live: Field/Goalie onto Bench area → FIFO substitute (not a plain demotion).
        if (_vm.IsMatchInProgress
            && player.Position is PlayerPosition.Field or PlayerPosition.Goalie)
        {
            var ok = _vm.LiveSubstituteWithNextBench(player);
            System.Diagnostics.Debug.WriteLine($"[GamePage] Bench DROP live FIFO sub ok={ok}");
            return;
        }

        _vm.SetPlayerPosition(player, PlayerPosition.Bench);
    }

    private void OnFieldViewTokenDragOver(object? sender, DragEventArgs e)
        => OnFieldCellDragOver(sender, e);

    /// <summary>
    /// Drop onto a token (shared template). Live Field/Goalie → Bench token = direct substitute.
    /// </summary>
    private void OnFieldViewTokenDrop(object? sender, DropEventArgs e)
    {
        if (_vm is null || _vm.IsMember) return;
        if (!TryGetDraggedPlayer(e, out var dragged)) return;
        if (sender is not BindableObject { BindingContext: Player target }) return;
        if (ReferenceEquals(dragged, target)) return;

        System.Diagnostics.Debug.WriteLine(
            $"[GamePage] Token DROP {dragged.Name} ({dragged.Position}) → {target.Name} ({target.Position})");
        CancelPendingFieldSingleTap();
        _vm.ClearFieldTapPlaceSelection();

        if (_vm.IsMatchInProgress)
        {
            if (target.Position == PlayerPosition.Bench
                && dragged.Position is PlayerPosition.Field or PlayerPosition.Goalie)
            {
                var ok = _vm.LiveSubstituteWithBenchPlayer(dragged, target);
                System.Diagnostics.Debug.WriteLine($"[GamePage] Token DROP live direct sub ok={ok}");
                return;
            }

            // Other live token-to-token drops are ignored (use Bench area / cells).
            return;
        }

        // Setup: dropping onto a Bench/Absent token parks there; otherwise swap roles.
        if (target.Position is PlayerPosition.Bench or PlayerPosition.Inactive)
            _vm.SetPlayerPosition(dragged, target.Position);
        else
            _vm.SwapPlayerRoles(dragged, target);
    }

    private void OnGoalieBandDragOver(object? sender, DragEventArgs e)
    {
        OnFieldCellDragOver(sender, e);
        if (GoalieDropHint is not null)
            GoalieDropHint.IsVisible = CanAcceptFieldDrop(e);
    }

    private void OnGoalieBandDragLeave(object? sender, DragEventArgs e)
    {
        if (GoalieDropHint is not null)
            GoalieDropHint.IsVisible = false;
    }

    private void OnGoalieBandDrop(object? sender, DropEventArgs e)
    {
        if (GoalieDropHint is not null)
            GoalieDropHint.IsVisible = false;
        if (_vm is null || _vm.IsMember) return;
        if (!TryGetDraggedPlayer(e, out var player)) return;
        _vm.SetPlayerPosition(player, PlayerPosition.Goalie);
    }

    private void OnInactiveBandDragOver(object? sender, DragEventArgs e)
    {
        OnFieldCellDragOver(sender, e);
        if (InactiveDropHint is not null)
            InactiveDropHint.IsVisible = CanAcceptFieldDrop(e);
    }

    private void OnInactiveBandDragLeave(object? sender, DragEventArgs e)
    {
        if (InactiveDropHint is not null)
            InactiveDropHint.IsVisible = false;
    }

    private void OnInactiveBandDrop(object? sender, DropEventArgs e)
    {
        if (InactiveDropHint is not null)
            InactiveDropHint.IsVisible = false;
        if (_vm is null || _vm.IsMember) return;
        if (!TryGetDraggedPlayer(e, out var player)) return;
        _vm.ClearFieldTapPlaceSelection();

        // Live: players leave the pitch only via the Bench — block Field/Goalie → Absent.
        if (_vm.IsMatchInProgress
            && player.Position is PlayerPosition.Field or PlayerPosition.Goalie)
            return;

        // Setup (and non-pitch sources): move only (no swap).
        _vm.SetPlayerPosition(player, PlayerPosition.Inactive);
    }

    private bool CanAcceptFieldDrop(DragEventArgs e)
        => _vm is not null && !_vm.IsMember
           && (e.Data.Properties.ContainsKey(FieldDragSlotIdKey) || _activeDragSlotId > 0);

    /// <summary>
    /// Setup: arm tap-to-place / complete move-swap (unchanged).
    /// Live: Manual = Bench then Field seeds rotation pairs; Field first then Bench = live sub.
    /// Non-Manual: Bench = rotation queue; Field/Goalie/Absent = arm for Bench move.
    /// </summary>
    private void OnFieldViewPlayerTapped(object? sender, TappedEventArgs e)
    {
        if (_vm is null || _vm.IsMember) return;
        if (sender is not BindableObject bindable) return;

        var player = ResolveFieldViewPlayer(bindable);
        if (player is null) return;

        if (_vm.IsMatchInProgress)
        {
            CancelPendingFieldSingleTap();

            // Completing an armed Field/Goalie/Absent onto a Bench token (instant sub / late arrival).
            // Must run before Manual pair seeding and before TapPlayerQueue.
            if (_vm.HasFieldTapPlaceSelection && player.Position == PlayerPosition.Bench)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[GamePage] Live Bench token tap while armed slot={_vm.FieldTapPlaceSlotId} → complete onto {player.Name}");
                var ok = _vm.TryCompleteFieldTapPlaceOntoPlayer(player);
                System.Diagnostics.Debug.WriteLine($"[GamePage] Live Bench token complete ok={ok}");
                return;
            }

            // Manual: Bench→Field pair seeding (returns false on Field with no pending Bench).
            if (_vm.IsManualRotationBasis
                && player.Position is PlayerPosition.Bench or PlayerPosition.Field or PlayerPosition.Goalie
                && _vm.TryManualPairTap(player))
                return;

            // Field/Goalie/Absent → arm for Bench placement (Field-first live sub / late arrival).
            if (player.Position is PlayerPosition.Field or PlayerPosition.Goalie or PlayerPosition.Inactive)
            {
                _vm.ToggleFieldTapPlaceSelection(player);
                return;
            }

            // Non-Manual Bench → auto-paired rotation queue.
            if (_vm.HasFieldTapPlaceSelection)
                _vm.ClearFieldTapPlaceSelection();
            _vm.TapPlayerQueue(player);
            return;
        }

        // Setup: if already armed, complete onto this player (swap / move-only rules).
        if (_vm.HasFieldTapPlaceSelection)
        {
            if (_vm.FieldTapPlaceSlotId == player.SlotId)
            {
                _vm.ClearFieldTapPlaceSelection();
                return;
            }

            _vm.TryCompleteFieldTapPlaceOntoPlayer(player);
            return;
        }

        // Arm immediately so a following Bench/Absent tap cannot race a delay window.
        _vm.ToggleFieldTapPlaceSelection(player);
    }

    private async void OnFieldViewPlayerDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_vm is null || _vm.IsMember) return;
        if (sender is not BindableObject bindable) return;

        // Single-tap may have armed already on Android — discard for rename.
        CancelPendingFieldSingleTap();
        _vm.ClearFieldTapPlaceSelection();

        var player = ResolveFieldViewPlayer(bindable);
        if (player is null) return;

        // Rename only makes sense in Setup (and Finished / pre-start).
        if (_vm.Phase is not GamePhase.Setup and not GamePhase.Finished)
            return;

        var result = await DisplayPromptAsync(
            title: "Rename Player",
            message: string.Empty,
            accept: "Save",
            cancel: "Cancel",
            placeholder: player.Name,
            initialValue: player.Name,
            keyboard: Keyboard.Default);
        if (result is null) return;
        _vm.RenamePlayer(player, result);
    }

    private static Player? ResolveFieldViewPlayer(BindableObject bindable)
        => bindable.BindingContext switch
        {
            Player p => p,
            FieldCellSlot { Player: Player pl } => pl,
            _ => null
        };

    /// <summary>
    /// Apply a deferred source-arm immediately so a quick destination tap (common on Android)
    /// still completes the move instead of cancelling the pending selection.
    /// </summary>
    private void FlushPendingFieldSingleTap()
    {
        var slotId = _pendingFieldTapSlotId;
        CancelPendingFieldSingleTap();
        if (_vm is null || slotId is not int id) return;
        if (_vm.HasFieldTapPlaceSelection) return;
        var match = _vm.Players.FirstOrDefault(p => p.SlotId == id);
        if (match is not null)
            _vm.ArmFieldTapPlaceSelection(match);
    }

    private void CancelPendingFieldSingleTap()
    {
        try { _fieldSingleTapCts?.Cancel(); }
        catch { /* ignore */ }
        _fieldSingleTapCts?.Dispose();
        _fieldSingleTapCts = null;
        _pendingFieldTapSlotId = null;
    }

    private bool TryGetDraggedPlayer(DropEventArgs e, out Player player)
    {
        player = null!;
        if (_vm is null) return false;

        var slotId = 0;
        // DropEventArgs.Data is DataPackageView — custom Properties may be empty on Android.
        if (e.Data.Properties.TryGetValue(FieldDragSlotIdKey, out var raw))
        {
            slotId = raw switch
            {
                int i => i,
                long l => (int)l,
                string s when int.TryParse(s, out var parsed) => parsed,
                _ => 0
            };
        }

        // Android fallback when Properties are stripped mid-drag.
        if (slotId <= 0)
            slotId = _activeDragSlotId;

        if (slotId <= 0) return false;

        var match = _vm.Players.FirstOrDefault(p => p.SlotId == slotId);
        if (match is null) return false;
        player = match;
        _activeDragSlotId = 0;
        return true;
    }
}
