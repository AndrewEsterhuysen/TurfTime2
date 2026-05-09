using TurfTime2.Models;
using TurfTime2.Services;
using TurfTime2.ViewModels;

namespace TurfTime2;

public partial class GamePage : ContentPage
{
    private GameViewModel? _vm;

    // Swipe tracking per-row
    private double _panStartX;
    private const double SwipeThreshold = 60; // px needed to register

    // Long-press cancellation
    private CancellationTokenSource? _startLongPressCts;
    private CancellationTokenSource? _rotateLongPressCts;

    // ── Lifecycle ─────────────────────────────────────────────────────────

    public GamePage()
    {
        InitializeComponent();
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

    // ── Pan / swipe gesture (swipe left = field/goalie, right = bench/inactive) ──

    private void OnPlayerPanUpdated(object sender, PanUpdatedEventArgs e)
    {
        if (_vm is null || _vm.IsMember) return;

        // The Grid is the sender; its BindingContext is the Player
        var grid = sender as BindableObject;
        if (grid?.BindingContext is not Player player) return;

        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _panStartX = e.TotalX;
                break;

            case GestureStatus.Running:
                // Provide visual feedback: shift the row as the user pans
                if (grid is View v)
                    v.TranslationX = e.TotalX;
                break;

            case GestureStatus.Completed:
                var dx = e.TotalX - _panStartX;
                // Reset visual position
                if (grid is View vEnd)
                    vEnd.TranslationX = 0;

                if (Math.Abs(dx) < SwipeThreshold) break;

                if (dx < 0)
                {
                    // Swipe LEFT
                    var next = player.Position == PlayerPosition.Field
                        ? PlayerPosition.Goalie   // already on field → promote to goalie
                        : PlayerPosition.Field;
                    _vm.SetPlayerPosition(player, next);
                }
                else
                {
                    // Swipe RIGHT
                    var next = player.Position == PlayerPosition.Bench
                        ? PlayerPosition.Inactive  // already on bench → mark inactive
                        : PlayerPosition.Bench;
                    _vm.SetPlayerPosition(player, next);
                }
                break;

            case GestureStatus.Canceled:
                if (grid is View vCancel)
                    vCancel.TranslationX = 0;
                break;
        }
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
}
