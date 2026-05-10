using TurfTime2.Models;
using TurfTime2.Services;
using TurfTime2.ViewModels;
#if ANDROID
using AndroidView = Android.Views.View;
using AndroidViewGroup = Android.Views.ViewGroup;
#endif

namespace TurfTime2;

public partial class GamePage : ContentPage
{
    private GameViewModel? _vm;

    private CancellationTokenSource? _startLongPressCts;
    private CancellationTokenSource? _rotateLongPressCts;

    // ── Lifecycle ─────────────────────────────────────────────────────────

    public GamePage()
    {
        InitializeComponent();

#if ANDROID
        // CollectionView (RecyclerView) clips its children by default on Android,
        // which hides the pan animation when a row slides outside its bounds.
        // Disable clipping so the translated row remains visible during the swipe.
        SwipeableRoster.Loaded += (_, _) => DisableAndroidClipping(SwipeableRoster);
#endif
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        SetKeepScreenOn(true);

        var teamId   = Preferences.Get("team_id",   string.Empty);
        var userRole = Preferences.Get("user_role", (string?)null);

        if (_vm is null)
        {
            await CreateViewModelAsync(teamId, userRole);
        }
        else if (teamId != Preferences.Get("_gamepage_last_team", string.Empty))
        {
            Preferences.Set("_gamepage_last_team", teamId);
            await _vm.InitialiseAsync(teamId, userRole);
            ApplyViewMode(_vm.ViewMode);
        }

        // Re-read rotation style preference whenever the page appears
        if (_vm is not null)
        {
            var style = Preferences.Get("rotation_style", 1);
            _vm.UpdateRotationStyle(style);
        }

        RotationStylePage.RotationStyleChanged += OnRotationStyleChanged;
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        SetKeepScreenOn(false);
        RotationStylePage.RotationStyleChanged -= OnRotationStyleChanged;
    }

    // ── ViewModel factory ─────────────────────────────────────────────────

    private async Task CreateViewModelAsync(string teamId, string? userRole)
    {
        var timer   = new GameTimerService();
        var cloud   = new CloudRosterService();
        var session = new SessionStorageService();
        var logger  = new GameLoggerService(session);
        _vm         = new GameViewModel(timer, logger, cloud);

        BindingContext = _vm;

        Preferences.Set("_gamepage_last_team", teamId);
        await _vm.InitialiseAsync(teamId, userRole);
        ApplyViewMode(_vm.ViewMode);
    }

    // ── Rotation style event ──────────────────────────────────────────────

    private void OnRotationStyleChanged(object? sender, int style)
    {
        if (_vm is null) return;
        _vm.UpdateRotationStyle(style);
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

    // Drag state.
    private int      _dragFromIndex;
    private int      _dragTargetIndex;
    private double   _rowHeight = 52;
    private Player?  _currentDragTarget;

    private const double IntentThreshold  = 12;  // dp before we commit to swipe or drag
    private const double SwipeThreshold   = 80;  // dp horizontal to commit a swipe

    private void OnPlayerPanUpdated(object sender, PanUpdatedEventArgs e)
    {
        if (_vm is null || _vm.IsMember) return;
        if (sender is not View row) return;
        if (row.BindingContext is not Player player) return;

        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _panRow    = row;
                _panPlayer = player;
                _panIntent = PanIntent.Unknown;
                _dragFromIndex   = _vm.Players.IndexOf(player);
                _dragTargetIndex = _dragFromIndex;
                if (SwipeableRoster.Height > 0 && _vm.DisplayItems.Count > 0)
                    _rowHeight = SwipeableRoster.Height / _vm.DisplayItems.Count;
#if ANDROID
                DisallowParentInterceptTouch(row);
#endif
                break;

            case GestureStatus.Running:
                if (_panRow is null || _panPlayer is null) return;

                // Commit to an intent once the finger has moved far enough.
                if (_panIntent == PanIntent.Unknown)
                {
                    if (Math.Abs(e.TotalX) < IntentThreshold &&
                        Math.Abs(e.TotalY) < IntentThreshold)
                        break; // not yet decided

                    _panIntent = Math.Abs(e.TotalX) >= Math.Abs(e.TotalY)
                        ? PanIntent.Swipe
                        : PanIntent.Drag;
                }

                if (_panIntent == PanIntent.Swipe)
                {
                    row.TranslationX = e.TotalX;
                }
                else
                {
                    UpdateDragIndicator(_panPlayer, e.TotalY);
                }
                break;

            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                ClearDragIndicator();

                if (_panIntent == PanIntent.Drag)
                {
                    if (e.StatusType == GestureStatus.Completed
                        && _dragTargetIndex != _dragFromIndex
                        && _panPlayer is not null)
                    {
                        _vm.ReorderPlayer(_dragFromIndex, _dragTargetIndex);
                    }
                }
                else
                {
                    // Swipe intent (or unknown = tap, treat as snap-back).
                    CommitSwipe(row, player);
                }

                _panRow    = null;
                _panPlayer = null;
                _panIntent = PanIntent.Unknown;
                break;
        }
    }

    // Snaps back immediately if threshold not met, or applies the position
    // change first (so color/icon update is instant) then does a short bounce.
    private void CommitSwipe(View row, Player player)
    {
        double totalX = row.TranslationX;

        if (Math.Abs(totalX) < SwipeThreshold)
        {
            _ = row.TranslateTo(0, 0, 180, Easing.SpringOut);
            return;
        }

        bool swipeLeft    = totalX < 0;
        var  newPosition  = swipeLeft
            ? (player.Position == PlayerPosition.Field
                ? PlayerPosition.Goalie
                : PlayerPosition.Field)
            : (player.Position == PlayerPosition.Bench
                ? PlayerPosition.Inactive
                : PlayerPosition.Bench);

        // Apply the change BEFORE animating so the recycled/rebound view
        // already has the correct color when it snaps back — no off-screen hang.
        _vm?.SetPlayerPosition(player, newPosition);

        // Brief overshoot in the swipe direction then spring to centre.
        double bump = swipeLeft ? -30 : 30;
        _ = row.TranslateTo(bump, 0, 60, Easing.CubicOut)
               .ContinueWith(_ => Dispatcher.Dispatch(
                   () => row.TranslateTo(0, 0, 140, Easing.SpringOut)));
    }

    private void UpdateDragIndicator(Player dragging, double totalY)
    {
        int delta     = (int)(totalY / Math.Max(_rowHeight, 10));
        int newTarget = Math.Clamp(_dragFromIndex + delta, 0, _vm!.Players.Count - 1);

        if (newTarget == _dragTargetIndex) return;

        ClearDragIndicator();
        _dragTargetIndex = newTarget;

        if (newTarget != _dragFromIndex)
        {
            _currentDragTarget = _vm.Players.ElementAtOrDefault(newTarget);
            if (_currentDragTarget is not null)
                _currentDragTarget.IsDragTarget = true;
        }
    }

    private void ClearDragIndicator()
    {
        if (_currentDragTarget is null) return;
        _currentDragTarget.IsDragTarget = false;
        _currentDragTarget = null;
    }

#if ANDROID
    private static void DisallowParentInterceptTouch(View view)
    {
        if (view.Handler?.PlatformView is AndroidView native)
            native.Parent?.RequestDisallowInterceptTouchEvent(true);
    }
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

    // ── Bottom button handlers ────────────────────────────────────────────

    private void OnStartClicked(object sender, EventArgs e)
    {
        _vm?.ToggleStartPause();
        if (_vm is not null) _vm.RotationDue = false;
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
        _vm?.ExecuteRotations();
        if (_vm is not null) _vm.RotationDue = false;
        AnimateRotateBtn();

        // Resync: scroll the roster back to the first row so the user can
        // immediately see who has just rotated onto the field.
        if (SwipeableRoster.IsVisible && _vm?.DisplayItems.Count > 0)
            SwipeableRoster.ScrollTo(_vm.DisplayItems[0], animate: false);
    }

    private async void OnRotatePressed(object sender, EventArgs e)
    {
        _rotateLongPressCts?.Cancel();
        _rotateLongPressCts = new CancellationTokenSource();
        var token = _rotateLongPressCts.Token;
        try
        {
            await Task.Delay(500, token);
            await ShowRotationCountDialog();
        }
        catch (OperationCanceledException) { }
    }

    private void OnRotateReleased(object sender, EventArgs e)
        => _rotateLongPressCts?.Cancel();

    private async Task ShowRotationCountDialog()
    {
        if (_vm is null) return;
        var max = _vm.MaxRotationCount;
        if (max < 1)
        {
            await DisplayAlert("Rotation Count", "Assign bench players first.", "OK");
            return;
        }
        var options = Enumerable.Range(1, max).Select(n => n.ToString()).ToArray();
        var result  = await DisplayActionSheet("Rotate how many players?", "Cancel", null, options);
        if (result is null || result == "Cancel") return;
        if (int.TryParse(result, out var count))
            _vm.RotationCount = count;
    }

    private void OnViewButtonClicked(object sender, EventArgs e)
    {
        if (_vm is null) return;
        var next = _vm.ViewMode switch
        {
            TeamViewMode.Swipeable => TeamViewMode.Table,
            TeamViewMode.Table     => TeamViewMode.Rotation,
            _                      => TeamViewMode.Swipeable
        };
        _vm.ViewMode = next;
        ApplyViewMode(next);
        UpdateViewButtonText(next);
    }

    // ── Score buttons ─────────────────────────────────────────────────────

    private void OnTeamAScoreClicked(object sender, EventArgs e) => _vm?.IncrementTeamAScore();
    private void OnTeamBScoreClicked(object sender, EventArgs e) => _vm?.IncrementTeamBScore();

    // ── View switching ────────────────────────────────────────────────────

    private void ApplyViewMode(TeamViewMode mode)
    {
        SwipeableRoster.IsVisible = mode == TeamViewMode.Swipeable;
        TableView.IsVisible       = mode == TeamViewMode.Table;
        RotationView.IsVisible    = mode == TeamViewMode.Rotation;

        if (mode == TeamViewMode.Table)
            BuildTableView();
    }

    private void UpdateViewButtonText(TeamViewMode mode)
    {
        ViewBtn.Text = mode switch
        {
            TeamViewMode.Swipeable => "Swipe",
            TeamViewMode.Table     => "Table",
            _                      => "Rotation"
        };
    }

    // ── Table view builder (VIEW_B) ───────────────────────────────────────

    private void BuildTableView()
    {
        if (_vm is null) return;
        TableStack.Children.Clear();

        var header = new Grid
        {
            ColumnDefinitions = Columns("*", "Auto", "Auto", "Auto", "Auto"),
            BackgroundColor   = Color.FromArgb("#1a4a1e"),
            Padding           = new Thickness(8, 4)
        };
        AddHeaderCell(header, "Player",  0);
        AddHeaderCell(header, "⚽",      1);
        AddHeaderCell(header, "💺",      2);
        AddHeaderCell(header, "🥅",      3);
        AddHeaderCell(header, "❌",      4);
        TableStack.Children.Add(header);

        bool odd = false;
        foreach (var p in _vm.Players)
        {
            if (_vm.Phase != GamePhase.Setup && p.Position == PlayerPosition.Inactive) continue;

            var rowBg = p.Position switch
            {
                PlayerPosition.Field    => Color.FromArgb("#388e3c"),
                PlayerPosition.Bench    => Color.FromArgb("#1565c0"),
                PlayerPosition.Goalie   => Color.FromArgb("#f57f17"),
                PlayerPosition.Inactive => Color.FromArgb("#424242"),
                _                       => odd ? Color.FromArgb("#2a6b2e") : Color.FromArgb("#1b5e20")
            };
            odd = !odd;

            var row = new Grid
            {
                ColumnDefinitions = Columns("*", "Auto", "Auto", "Auto", "Auto"),
                BackgroundColor   = rowBg,
                Padding           = new Thickness(8, 3)
            };

            var nameLabel = new Label
            {
                Text            = p.Name,
                TextColor       = Colors.White,
                FontSize        = 14,
                FontAttributes  = p.IsNextToRotate ? FontAttributes.Bold : FontAttributes.None,
                VerticalOptions = LayoutOptions.Center
            };
            Grid.SetColumn(nameLabel, 0);
            row.Children.Add(nameLabel);

            var positions = new[] { PlayerPosition.Field, PlayerPosition.Bench, PlayerPosition.Goalie, PlayerPosition.Inactive };
            for (int col = 1; col <= 4; col++)
            {
                var pos     = positions[col - 1];
                var rb      = new RadioButton { IsChecked = p.Position == pos, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center };
                var captured = pos;
                var captured_p = p;
                rb.CheckedChanged += (s, ev) => { if (ev.Value) _vm?.SetPlayerPosition(captured_p, captured); };
                Grid.SetColumn(rb, col);
                row.Children.Add(rb);
            }

            // Separator line
            var separator = new BoxView { HeightRequest = 1, Color = Color.FromArgb("#ffffff22"), HorizontalOptions = LayoutOptions.Fill };

            TableStack.Children.Add(row);
            TableStack.Children.Add(separator);
        }
    }

    private static ColumnDefinitionCollection Columns(params string[] widths)
    {
        var defs = new ColumnDefinitionCollection();
        foreach (var w in widths)
            defs.Add(w == "*"
                ? new ColumnDefinition { Width = GridLength.Star }
                : new ColumnDefinition { Width = GridLength.Auto });
        return defs;
    }

    private static void AddHeaderCell(Grid grid, string text, int col)
    {
        var label = new Label
        {
            Text              = text,
            TextColor         = Colors.White,
            FontAttributes    = FontAttributes.Bold,
            FontSize          = 12,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions   = LayoutOptions.Center
        };
        Grid.SetColumn(label, col);
        grid.Children.Add(label);
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

    // ── Rotate button animation ───────────────────────────────────────────

    private async void AnimateRotateBtn()
    {
        await RotateBtn.ScaleTo(0.92, 80);
        await RotateBtn.ScaleTo(1.0,  80);
    }

#if ANDROID
    // ── Android clip-children fix ─────────────────────────────────────────
    // CollectionView (RecyclerView) and each row's ViewGroup clip their children
    // by default, which prevents a translated child from being visible outside
    // its own bounding box during the swipe animation.
    private static void DisableAndroidClipping(Microsoft.Maui.Controls.View mauiView)
    {
        var nativeView = mauiView.Handler?.PlatformView as AndroidViewGroup;
        if (nativeView is null) return;

        // Disable on the RecyclerView itself and its parent chain up to 3 levels.
        AndroidViewGroup? current = nativeView;
        for (int i = 0; i < 4 && current is not null; i++)
        {
            current.SetClipChildren(false);
            current.SetClipToPadding(false);
            current = current.Parent as AndroidViewGroup;
        }
    }
#endif
}
