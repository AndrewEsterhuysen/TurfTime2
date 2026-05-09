using TurfTime2.Models;
using TurfTime2.Services;
using TurfTime2.ViewModels;

namespace TurfTime2;

public partial class GamePage : ContentPage
{
    private GameViewModel? _vm;

    // Long-press support for Start and Rotate buttons
    private CancellationTokenSource? _startLongPressCts;
    private CancellationTokenSource? _rotateLongPressCts;

    public GamePage()
    {
        InitializeComponent();
    }

    // ── Page lifecycle ────────────────────────────────────────────────────

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        SetKeepScreenOn(true);

        if (_vm is null)
            await CreateViewModelAsync();

        // Re-apply team in case it changed while on another tab
        var teamId   = Preferences.Get("team_id",    string.Empty);
        var userRole = Preferences.Get("user_role",  (string?)null);
        if (_vm is not null && teamId != Preferences.Get("_gamepage_last_team", string.Empty))
        {
            Preferences.Set("_gamepage_last_team", teamId);
            await _vm.InitialiseAsync(teamId, userRole);
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        SetKeepScreenOn(false);
    }

    // ── ViewModel factory ─────────────────────────────────────────────────

    private async Task CreateViewModelAsync()
    {
        var timer  = new GameTimerService();
        var cloud  = new CloudRosterService();
        var session = new SessionStorageService();
        var logger = new GameLoggerService(session);
        _vm        = new GameViewModel(timer, logger, cloud);

        BindingContext = _vm;

        var teamId   = Preferences.Get("team_id",   string.Empty);
        var userRole = Preferences.Get("user_role", (string?)null);
        await _vm.InitialiseAsync(teamId, userRole);

        ApplyViewMode(_vm.ViewMode);
        UpdateViewButtonText(_vm.ViewMode);
    }

    // ── Header: match/countdown taps (edit duration/preset) ──────────────

    private async void OnMatchTimerTapped(object sender, TappedEventArgs e)
    {
        if (_vm is null || _vm.Phase != GamePhase.Setup || _vm.IsMember) return;

        var current = (_vm.MatchTimeDisplay.Contains("min"))
            ? _vm.MatchTimeDisplay.Replace(" min", "")
            : $"{(int)TimeSpan.Parse(_vm.MatchTimeDisplay).TotalMinutes}";

        var result = await DisplayPromptAsync(
            "Match Duration",
            "Enter total match time in minutes (e.g. 90):",
            initialValue: current,
            keyboard: Keyboard.Numeric);

        if (result is null) return;
        if (int.TryParse(result, out var minutes) && minutes > 0 && minutes <= 999)
            _vm.SetMatchDuration(minutes);
        else
            await DisplayAlert("Invalid", "Please enter a number between 1 and 999.", "OK");
    }

    private async void OnCountdownTimerTapped(object sender, TappedEventArgs e)
    {
        if (_vm is null || _vm.IsMember) return;

        var result = await DisplayPromptAsync(
            "Rotation Countdown",
            "Enter rotation time as MM:SS (e.g. 2:00).\nTap Auto to calculate optimal.",
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
            && int.TryParse(parts[0], out var m) && m >= 0 && m <= 99
            && int.TryParse(parts[1], out var s) && s >= 0 && s <= 59
            && (m * 60 + s) > 0)
        {
            _vm.SetCountdownPreset(m, s);
        }
        else
        {
            await DisplayAlert("Invalid", "Please enter a valid time in MM:SS format.", "OK");
        }
    }

    // ── Bottom buttons ────────────────────────────────────────────────────

    private void OnStartClicked(object sender, EventArgs e)
    {
        _vm?.ToggleStartPause();
        // Rotation-due flash cleared when user interacts
        if (_vm is not null) _vm.RotationDue = false;
    }

    private async void OnStartPressed(object sender, EventArgs e)
    {
        // Long-press Start → restart game (mirrors JS 1000ms long press)
        _startLongPressCts?.Cancel();
        _startLongPressCts = new CancellationTokenSource();
        var token = _startLongPressCts.Token;
        try
        {
            await Task.Delay(1000, token);
            if (_vm is not null && !_vm.IsMember)
            {
                var confirmed = await DisplayAlert(
                    "Restart Game",
                    "Are you sure you want to restart the match?",
                    "Restart", "Cancel");
                if (confirmed)
                    _vm.RestartGameCommand();
            }
        }
        catch (OperationCanceledException) { /* normal tap cancelled long press */ }
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
        // Long-press Rotate → show rotation count picker (mirrors JS 500ms)
        _rotateLongPressCts?.Cancel();
        _rotateLongPressCts = new CancellationTokenSource();
        var token = _rotateLongPressCts.Token;
        try
        {
            await Task.Delay(500, token);
            await ShowRotationCountDialog();
        }
        catch (OperationCanceledException) { /* tap */ }
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
        var result  = await DisplayActionSheet(
            $"Rotate how many players?",
            "Cancel", null, options);

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

    private void OnTeamAScoreClicked(object sender, EventArgs e)
        => _vm?.IncrementTeamAScore();

    private void OnTeamBScoreClicked(object sender, EventArgs e)
        => _vm?.IncrementTeamBScore();

    // ── Swipe handlers ────────────────────────────────────────────────────

    private void OnSwipeToField(object sender, EventArgs e)
    {
        if (sender is SwipeItem si && si.BindingContext is Player p)
            _vm?.SetPlayerPosition(p, PlayerPosition.Field);
    }

    private void OnSwipeToBench(object sender, EventArgs e)
    {
        if (sender is SwipeItem si && si.BindingContext is Player p)
            _vm?.SetPlayerPosition(p, PlayerPosition.Bench);
    }

    private void OnSwipeToInactive(object sender, EventArgs e)
    {
        if (sender is SwipeItem si && si.BindingContext is Player p)
            _vm?.SetPlayerPosition(p, PlayerPosition.Inactive);
    }

    // ── Player selection (tap = set next-to-rotate) ───────────────────────

    private void OnPlayerSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_vm is null) return;
        if (e.CurrentSelection.FirstOrDefault() is Player p)
        {
            // Tapping a field player sets them as next to rotate out;
            // tapping a bench player sets them as next to rotate in.
            if (p.Position == PlayerPosition.Field)
                _vm.SetNextFieldPlayer(p);
            else if (p.Position == PlayerPosition.Bench)
                _vm.SetNextBenchPlayer(p);
        }
        // Deselect immediately so the item can be tapped again
        ((CollectionView)sender).SelectedItem = null;
    }

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
            TeamViewMode.Swipeable => "VIEW_A",
            TeamViewMode.Table     => "VIEW_B",
            _                      => "VIEW_C"
        };
    }

    // ── Table view builder (VIEW_B) ───────────────────────────────────────

    private void BuildTableView()
    {
        if (_vm is null) return;
        TableStack.Children.Clear();

        // Header row
        var header = new Grid
        {
            ColumnDefinitions = Columns(new[] { "*", "Auto", "Auto", "Auto", "Auto" }),
            BackgroundColor   = Color.FromArgb("#1b5e20"),
            Padding           = new Thickness(8, 4)
        };
        AddHeaderCell(header, "Player", 0);
        AddHeaderCell(header, "⚽", 1);
        AddHeaderCell(header, "💺", 2);
        AddHeaderCell(header, "🥅", 3);
        AddHeaderCell(header, "❌", 4);
        TableStack.Children.Add(header);

        foreach (var p in _vm.Players)
        {
            if (_vm.Phase != GamePhase.Setup && !_vm.ShowInactivePlayers
                && p.Position == PlayerPosition.Inactive)
                continue;

            TableStack.Children.Add(BuildPlayerRow(p));
        }
    }

    private View BuildPlayerRow(Player p)
    {
        var bg = new Color[] { Color.FromArgb("#2e7d32"), Color.FromArgb("#1b5e20") };
        var idx = _vm!.Players.IndexOf(p);

        var grid = new Grid
        {
            ColumnDefinitions = Columns(new[] { "*", "Auto", "Auto", "Auto", "Auto" }),
            BackgroundColor   = p.Position switch
            {
                PlayerPosition.Field    => Color.FromArgb("#388e3c"),
                PlayerPosition.Bench    => Color.FromArgb("#1565c0"),
                PlayerPosition.Goalie   => Color.FromArgb("#f57f17"),
                PlayerPosition.Inactive => Color.FromArgb("#424242"),
                _                       => bg[idx % 2]
            },
            Padding = new Thickness(8, 2)
        };

        // Name + time label
        var nameLabel = new Label
        {
            Text          = p.Name,
            TextColor     = Colors.White,
            FontSize      = 14,
            FontAttributes = p.IsNextToRotate ? FontAttributes.Bold : FontAttributes.None,
            VerticalOptions = LayoutOptions.Center
        };
        Grid.SetColumn(nameLabel, 0);
        grid.Children.Add(nameLabel);

        // Position checkboxes
        var positions = new[] { PlayerPosition.Field, PlayerPosition.Bench, PlayerPosition.Goalie, PlayerPosition.Inactive };
        for (int col = 1; col <= 4; col++)
        {
            var pos = positions[col - 1];
            var rb  = new RadioButton
            {
                IsChecked       = p.Position == pos,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions   = LayoutOptions.Center
            };
            var captured = pos;
            rb.CheckedChanged += (s, e) =>
            {
                if (e.Value)
                    _vm?.SetPlayerPosition(p, captured);
            };
            Grid.SetColumn(rb, col);
            grid.Children.Add(rb);
        }

        return grid;
    }

    private static ColumnDefinitionCollection Columns(string[] widths)
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
            Text            = text,
            TextColor       = Colors.White,
            FontAttributes  = FontAttributes.Bold,
            FontSize        = 13,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions   = LayoutOptions.Center
        };
        Grid.SetColumn(label, col);
        grid.Children.Add(label);
    }

    // ── Keep screen on ────────────────────────────────────────────────────

    private void SetKeepScreenOn(bool on)
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
