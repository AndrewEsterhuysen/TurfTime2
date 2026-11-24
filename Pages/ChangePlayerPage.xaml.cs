using System.Globalization;
using TurfTime2.Helpers;
using TurfTime2.Models;
using TurfTime2.Services;
using TurfTime2.ViewModels;

namespace TurfTime2;

/// <summary>
/// Setup-only picker: assign a club player to one of the 16 roster slots
/// (docs/club-players-spec.md §7.6). Live Game tab swipes are unchanged.
/// </summary>
public partial class ChangePlayerPage : ContentPage
{
    private const string PageLabel = "Change player";

    private static GameViewModel? _targetVm;
    private static Player? _targetSlot;

    private IClubPlayerService? _players;
    private ICloudTeamService? _teams;
    private List<ClubPlayer> _all = [];
    private string _clubId = "";
    private string _teamId = "";
    private string _teamName = "";
    private string _ageGroup = "";
    private int _season;
    private bool _busy;
    private int _loadVersion;

    public ChangePlayerPage()
    {
        InitializeComponent();
    }

    internal static void Begin(GameViewModel vm, Player slot)
    {
        _targetVm = vm;
        _targetSlot = slot;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        DetailsPage.ApplyPageTeamTitle(this, PageLabel);
        _players ??= GetService<IClubPlayerService>();
        _teams ??= GetService<ICloudTeamService>();
        _teamId = Preferences.Get("team_id", string.Empty);
        _teamName = Preferences.Get("team_name", string.Empty);
        _clubId = string.IsNullOrWhiteSpace(_teamId)
            ? ""
            : Preferences.Get($"{_teamId}_club_id", string.Empty);
        if (_targetSlot is not null)
            SlotLabel.Text = string.IsNullOrWhiteSpace(_targetSlot.PlayerId)
                ? $"Slot {_targetSlot.SlotId}: {_targetSlot.Name}"
                : $"Slot {_targetSlot.SlotId}: {_targetSlot.Name} (linked)";
        ClearButton.IsVisible = _targetSlot is not null && !string.IsNullOrWhiteSpace(_targetSlot.PlayerId);
        await LoadAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        // Keep the target for a quick re-appear; cleared when leaving the game flow.
    }

    private async void OnRetryClicked(object? sender, EventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        var version = ++_loadVersion;
        if (string.IsNullOrWhiteSpace(_clubId) || _targetVm is null || _targetSlot is null)
        {
            ShowNotice("Change player is for club teams only.", retry: false);
            return;
        }
        if (_players is null || _teams is null)
        {
            ShowNotice("Club players are not available right now.", retry: true);
            return;
        }

        ShowNotice("Loading…", retry: false);
        var settings = await _teams.GetTeamAgeSettingsAsync(_teamId);
        if (version != _loadVersion) return;
        if (settings is { IsSet: true })
        {
            CacheAge(_teamId, settings.AgeGroup, settings.Season);
            _ageGroup = settings.AgeGroup;
            _season = settings.Season;
        }
        else
        {
            _ageGroup = Preferences.Get($"{_teamId}_age_group", "");
            int.TryParse(Preferences.Get($"{_teamId}_season", ""), out _season);
        }

        if (!ClubAgeGroups.IsKnownAgeGroup(_ageGroup) || _season < 2000)
        {
            NoticeCard.IsVisible = false;
            ListCard.IsVisible = false;
            AgeCard.IsVisible = true;
            FillAgePickers();
            return;
        }

        AgeCard.IsVisible = false;
        var access = await _players.GetAccessAsync(_clubId, _teamId);
        if (version != _loadVersion) return;
        if (!access.CanView)
        {
            ShowNotice(access.Reachable
                ? "Only club admins and coaches can assign club players."
                : "Could not reach the server. Check your connection.", retry: !access.Reachable);
            return;
        }

        var list = await _players.ListAsync(_clubId);
        if (version != _loadVersion) return;
        if (!list.Reachable)
        {
            ShowNotice(list.Error ?? "Could not load club players.", retry: true);
            return;
        }

        _all = list.Players;
        NoticeCard.IsVisible = false;
        ListCard.IsVisible = true;
        AgeSummaryLabel.Text = $"{_ageGroup} · season {_season} (age on 1 January)";
        RenderList();
    }

    private void FillAgePickers()
    {
        AgeGroupPicker.Items.Clear();
        foreach (var label in ClubAgeGroups.StandardLabels)
            AgeGroupPicker.Items.Add(label);
        var current = ClubAgeGroups.CanonicalAgeGroup(_ageGroup);
        if (current is not null)
            AgeGroupPicker.SelectedIndex = Array.IndexOf(ClubAgeGroups.StandardLabels, current);
        SeasonEntry.Text = _season >= 2000 ? _season.ToString(CultureInfo.InvariantCulture)
            : ClubAgeGroups.CurrentSeason().ToString(CultureInfo.InvariantCulture);
    }

    private async void OnSaveAgeClicked(object? sender, EventArgs e)
    {
        if (_busy || _teams is null) return;
        var idx = AgeGroupPicker.SelectedIndex;
        if (idx < 0 || idx >= ClubAgeGroups.StandardLabels.Length)
        {
            await DisplayAlert("Age group", "Choose an age group.", "OK");
            return;
        }
        if (!int.TryParse(SeasonEntry.Text?.Trim(), out var season) || season < 2000 || season > 2100)
        {
            await DisplayAlert("Season", "Enter a valid season year.", "OK");
            return;
        }
        _busy = true;
        SaveAgeButton.IsEnabled = false;
        try
        {
            var age = ClubAgeGroups.StandardLabels[idx];
            var result = await _teams.UpdateTeamAgeSettingsAsync(_teamId, age, season);
            if (result != "success")
            {
                await DisplayAlert("Age group", result.StartsWith("error:", StringComparison.OrdinalIgnoreCase)
                    ? result[6..].Trim()
                    : result, "OK");
                return;
            }
            CacheAge(_teamId, age, season);
            _ageGroup = age;
            _season = season;
            await LoadAsync();
        }
        finally
        {
            _busy = false;
            SaveAgeButton.IsEnabled = true;
        }
    }

    private void OnSearchChanged(object? sender, TextChangedEventArgs e)
    {
        if (ListCard.IsVisible) RenderList();
    }

    private void RenderList()
    {
        PlayerList.Children.Clear();
        var query = SearchEntry.Text ?? "";
        var matches = _all
            .Where(p => !p.IsArchived && ClubPlayerNames.Matches(p.NameKey, query))
            .ToList();
        var eligible = new List<ClubPlayer>();
        var ineligible = 0;
        foreach (var p in matches)
        {
            if (ClubAgeGroups.TryParseDateOfBirth(p.DateOfBirth, out var dob)
                && ClubAgeGroups.IsEligibleToAssign(dob, _ageGroup, _season))
                eligible.Add(p);
            else
                ineligible++;
        }

        CountLabel.Text = eligible.Count == 1 ? "1 eligible player" : $"{eligible.Count} eligible players";
        foreach (var p in eligible.Take(80))
            PlayerList.Children.Add(BuildRow(p));
        IneligibleLabel.Text = ineligible == 0
            ? ""
            : ineligible == 1
                ? "1 player is hidden — too old for this team (no playing down)."
                : $"{ineligible} players are hidden — too old for this team (no playing down).";
        IneligibleLabel.IsVisible = ineligible > 0;
    }

    private View BuildRow(ClubPlayer p)
    {
        var details = new List<string>();
        if (ClubAgeGroups.TryParseDateOfBirth(p.DateOfBirth, out var dob))
        {
            details.Add(dob.ToString("d MMM yyyy", CultureInfo.CurrentCulture));
            details.Add(ClubAgeGroups.AgeGroupFor(dob, _season));
        }
        var current = ClubPlayerAssignments.CurrentTeamIds(p.Teams);
        if (current.Count > 0)
        {
            var names = p.Teams.Where(t => t.RemovedAt is null && !string.IsNullOrWhiteSpace(t.TeamName))
                .Select(t => t.TeamName).Distinct().ToList();
            if (names.Count > 0) details.Add(string.Join(", ", names));
        }
        if (string.Equals(p.PlayerId, _targetSlot?.PlayerId, StringComparison.Ordinal))
            details.Add("This slot");
        else if (_targetVm?.Players.Any(s =>
                     string.Equals(s.PlayerId, p.PlayerId, StringComparison.Ordinal)) == true)
            details.Add("On this team");

        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) => await OnPlayerChosen(p);
        var border = new Border
        {
            Style = StyleOf("PvCardTinted"),
            Padding = new Thickness(12, 8),
            Content = new VerticalStackLayout
            {
                Spacing = 2,
                Children =
                {
                    new Label { Style = StyleOf("PvBodyStrong"), Text = p.DisplayName },
                    new Label { Style = StyleOf("PvCaption"), Text = string.Join(" · ", details) },
                }
            }
        };
        border.GestureRecognizers.Add(tap);
        return border;
    }

    private async Task OnPlayerChosen(ClubPlayer player)
    {
        if (_busy || _players is null || _targetVm is null || _targetSlot is null) return;

        if (ClubPlayerAssignments.ShouldWarnTeamCount(player.Teams, _teamId))
        {
            var go = await DisplayAlert(
                "Already on several teams",
                $"{player.DisplayName} is already assigned to {ClubPlayerAssignments.TeamCountWarningAt} or more teams. You can still assign them — this is a warning only.",
                "Assign anyway",
                "Cancel");
            if (!go) return;
        }

        var other = _targetVm.Players.FirstOrDefault(s =>
            s != _targetSlot && string.Equals(s.PlayerId, player.PlayerId, StringComparison.Ordinal));
        if (other is not null)
        {
            var go = await DisplayAlert(
                "Already on this team",
                $"{player.DisplayName} is already in slot {other.SlotId}. Move them to slot {_targetSlot.SlotId}?",
                "Move",
                "Cancel");
            if (!go) return;
        }

        _busy = true;
        try
        {
            var result = await _players.AssignToTeamAsync(
                _clubId, player.PlayerId, _teamId, _teamName, _ageGroup, _season);
            if (!result.Success)
            {
                await DisplayAlert("Change player", result.Error ?? "Could not assign the player.", "OK");
                return;
            }
            if (!string.IsNullOrWhiteSpace(result.Warning))
                await DisplayAlert("Assigned", result.Warning, "OK");
            _targetVm.LinkClubPlayer(_targetSlot, player.PlayerId, player.DisplayName);
            await Navigation.PopAsync();
        }
        finally
        {
            _busy = false;
        }
    }

    private async void OnClearClicked(object? sender, EventArgs e)
    {
        if (_busy || _targetVm is null || _targetSlot is null) return;
        if (string.IsNullOrWhiteSpace(_targetSlot.PlayerId))
        {
            _targetVm.UnlinkClubPlayer(_targetSlot);
            await Navigation.PopAsync();
            return;
        }

        var go = await DisplayAlert(
            "Clear this slot",
            $"Remove {_targetSlot.Name} from this team's assignments? The slot will go back to {Player.DefaultName(_targetSlot.SlotId)}.",
            "Clear",
            "Cancel");
        if (!go) return;

        _busy = true;
        try
        {
            if (_players is not null && !string.IsNullOrWhiteSpace(_clubId))
            {
                var season = _season >= 2000 ? _season : ClubAgeGroups.CurrentSeason();
                var result = await _players.UnassignFromTeamAsync(
                    _clubId, _targetSlot.PlayerId, _teamId, season);
                if (!result.Success)
                {
                    await DisplayAlert("Change player", result.Error ?? "Could not remove the assignment.", "OK");
                    return;
                }
            }
            _targetVm.UnlinkClubPlayer(_targetSlot);
            await Navigation.PopAsync();
        }
        finally
        {
            _busy = false;
        }
    }

    private void ShowNotice(string message, bool retry)
    {
        AgeCard.IsVisible = false;
        ListCard.IsVisible = false;
        NoticeCard.IsVisible = true;
        NoticeLabel.Text = message;
        RetryButton.IsVisible = retry;
    }

    private static void CacheAge(string teamId, string ageGroup, int season)
    {
        Preferences.Set($"{teamId}_age_group", ageGroup);
        Preferences.Set($"{teamId}_season", season.ToString(CultureInfo.InvariantCulture));
    }

    private static Style? StyleOf(string key)
        => Application.Current?.Resources.TryGetValue(key, out var s) == true ? s as Style : null;

    private static T? GetService<T>() where T : class
    {
        var services = Application.Current?.Handler?.MauiContext?.Services;
        return services?.GetService<T>();
    }
}
