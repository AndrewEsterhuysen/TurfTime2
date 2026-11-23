using System.Globalization;
using TurfTime2.Helpers;
using TurfTime2.Models;
using TurfTime2.Services;

namespace TurfTime2;

/// <summary>
/// Club player list (docs/club-players-spec.md §7.1–7.3), slice 1: search and add.
/// Opened from Details → Club players for club teams; club owners/admins and coaches
/// (Admins of one of the club's teams) only. Depends on <see cref="IClubPlayerService"/>
/// and <see cref="ICloudTeamService"/> only — no Firebase types here.
/// </summary>
public partial class ClubPlayersPage : ContentPage
{
    private const string PageLabel = "Club players";
    private const int MaxRows = 100;

    private IClubPlayerService? _players;
    private List<ClubPlayer> _all = [];
    private ClubPlayerAccess _access = new();
    private string _clubId = "";
    private string _teamId = "";
    private bool _saving;
    private int _loadVersion;

    public ClubPlayersPage()
    {
        InitializeComponent();
    }

    /// <summary>Club of the current team, or "" for nickname/local teams.</summary>
    internal static string CurrentClubId()
    {
        var teamId = Preferences.Get("team_id", string.Empty);
        return string.IsNullOrWhiteSpace(teamId) ? "" : Preferences.Get($"{teamId}_club_id", string.Empty);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        DetailsPage.ApplyPageTeamTitle(this, PageLabel);
        _players ??= GetService<IClubPlayerService>();
        _teamId = Preferences.Get("team_id", string.Empty);
        _clubId = CurrentClubId();
        var clubName = string.IsNullOrWhiteSpace(_teamId) ? "" : Preferences.Get($"{_teamId}_club_name", string.Empty);
        ClubNameLabel.Text = clubName;
        ClubNameLabel.IsVisible = !string.IsNullOrWhiteSpace(clubName);
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var version = ++_loadVersion;
        HideNotice();
        if (!DetailsCloudSync.IsSharedTeam() || string.IsNullOrWhiteSpace(_clubId))
        {
            ListCard.IsVisible = AddButton.IsVisible = AddCard.IsVisible = false;
            ShowNotice("The club player list is for club teams only.", retry: false);
            return;
        }
        if (_players is null)
        {
            ShowNotice("Club players are not available right now.", retry: true);
            return;
        }

        SetBusy(true);
        try
        {
            _access = await _players.GetAccessAsync(_clubId, _teamId);
            if (version != _loadVersion) return;
            if (!_access.CanView)
            {
                ListCard.IsVisible = AddButton.IsVisible = AddCard.IsVisible = false;
                ShowNotice(_access.Reachable
                        ? "Only club admins and coaches can see the club player list."
                        : "Couldn't reach the server. Check your connection and try again.",
                    retry: !_access.Reachable);
                return;
            }

            var result = await _players.ListAsync(_clubId);
            if (version != _loadVersion) return;
            if (!result.Reachable || result.Error is not null)
            {
                ListCard.IsVisible = false;
                ShowNotice(result.Error ?? "Couldn't load players.", retry: !result.Reachable);
                AddButton.IsVisible = _access.CanAdd && result.Reachable;
                return;
            }

            _all = result.Players;
            ListCard.IsVisible = true;
            AddButton.IsVisible = _access.CanAdd && !AddCard.IsVisible;
            RenderList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ClubPlayersPage] Load: {ex.Message}");
            ShowNotice("Couldn't load players. Check your connection and try again.", retry: true);
        }
        finally
        {
            if (version == _loadVersion) SetBusy(false);
        }
    }

    // ── List ──────────────────────────────────────────────────────────────

    private void RenderList()
    {
        var query = SearchEntry.Text ?? "";
        // Archived players stay in the club (stats kept) but are hidden here in slice 1.
        var matches = _all
            .Where(p => !p.IsArchived && ClubPlayerNames.Matches(p.NameKey, query))
            .ToList();

        PlayerList.Children.Clear();
        foreach (var p in matches.Take(MaxRows))
            PlayerList.Children.Add(BuildRow(p));

        var active = _all.Count(p => !p.IsArchived);
        CountLabel.Text = ClubPlayerNames.Normalize(query).Length == 0
            ? (active == 1 ? "1 player" : $"{active} players")
            : $"{matches.Count} of {active} players";
        if (matches.Count > MaxRows)
            CountLabel.Text += $" · showing {MaxRows}, type to narrow";
        if (active == 0)
            CountLabel.Text = "No players yet. Add the first one below.";

        // Inline "Add X to the club" when nothing matches the typed name exactly (§7.2).
        var typed = ClubPlayerNames.CleanDisplay(query);
        var exact = typed.Length > 0 && _all.Any(p => ClubPlayerNames.IsExactDuplicate(typed, p.Name, p.PreferredName));
        AddFromSearchButton.IsVisible = _access.CanAdd && typed.Length > 0 && !exact;
        AddFromSearchButton.Text = $"Add \"{typed}\" to the club";
    }

    private View BuildRow(ClubPlayer p)
    {
        var details = new List<string>();
        if (ClubAgeGroups.TryParseDateOfBirth(p.DateOfBirth, out var dob))
        {
            details.Add(dob.ToString("d MMM yyyy", CultureInfo.CurrentCulture));
            details.Add(ClubAgeGroups.AgeGroupFor(dob, ClubAgeGroups.CurrentSeason()));
        }
        else
        {
            details.Add("Date of birth missing");
        }
        var current = p.Teams.Where(t => t.RemovedAt is null && !string.IsNullOrWhiteSpace(t.TeamName))
            .Select(t => t.TeamName).Distinct().ToList();
        if (current.Count > 0) details.Add(string.Join(", ", current));

        return new Border
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
    }

    private void OnSearchChanged(object? sender, TextChangedEventArgs e)
    {
        if (ListCard.IsVisible) RenderList();
    }

    // ── Add ───────────────────────────────────────────────────────────────

    private void OnAddClicked(object? sender, EventArgs e) => OpenAddForm("");

    private void OnAddFromSearchClicked(object? sender, EventArgs e) =>
        OpenAddForm(ClubPlayerNames.CleanDisplay(SearchEntry.Text));

    private void OpenAddForm(string name)
    {
        if (!_access.CanAdd) return;
        AddNameEntry.Text = name;
        AddPreferredEntry.Text = "";
        AddDobPicker.MaximumDate = DateTime.Today;
        AddDobPicker.MinimumDate = DateTime.Today.AddYears(-80);
        // Start near a junior's birthday so the wheel isn't on today.
        AddDobPicker.Date = DateTime.Today.AddYears(-10);
        _dobPicked = false;
        UpdateAgeGroupHint();
        AddErrorLabel.IsVisible = false;
        AddCard.IsVisible = true;
        AddButton.IsVisible = false;
        if (string.IsNullOrEmpty(name)) AddNameEntry.Focus();
    }

    private bool _dobPicked;

    private void OnDobSelected(object? sender, DateChangedEventArgs e)
    {
        _dobPicked = true;
        UpdateAgeGroupHint();
    }

    private DateOnly PickedDob() => DateOnly.FromDateTime(((DateTime?)AddDobPicker.Date) ?? DateTime.Today);

    private void UpdateAgeGroupHint()
    {
        var season = ClubAgeGroups.CurrentSeason();
        AddAgeGroupLabel.Text = _dobPicked
            ? $"{ClubAgeGroups.AgeGroupFor(PickedDob(), season)} in {season} (age on 1 January)"
            : "Pick the full date of birth (day, month and year).";
    }

    private void OnAddCancelClicked(object? sender, EventArgs e) => CloseAddForm();

    private void CloseAddForm()
    {
        AddCard.IsVisible = false;
        AddButton.IsVisible = _access.CanAdd;
        AddErrorLabel.IsVisible = false;
    }

    private async void OnAddSaveClicked(object? sender, EventArgs e)
    {
        if (_saving || _players is null) return;
        var name = ClubPlayerNames.CleanDisplay(AddNameEntry.Text);
        var preferred = ClubPlayerNames.CleanDisplay(AddPreferredEntry.Text, ClubPlayerNames.MaxPreferredNameLength);
        if (name.Length == 0) { ShowAddError("Enter the player's name."); return; }
        if (!_dobPicked) { ShowAddError("Pick the player's date of birth."); return; }
        var dob = PickedDob();

        // Near-duplicate guard, exact match only in slice 1 (§7.3).
        var existing = _all.FirstOrDefault(p => ClubPlayerNames.IsExactDuplicate(name, p.Name, p.PreferredName));
        if (existing is not null)
        {
            var when = ClubAgeGroups.TryParseDateOfBirth(existing.DateOfBirth, out var d)
                ? $" ({d.ToString("d MMM yyyy", CultureInfo.CurrentCulture)})"
                : "";
            var addAnyway = await DisplayAlert("Already in the club?",
                $"{existing.DisplayName}{when} is already in the club.", "Add anyway", "Use existing");
            if (!addAnyway)
            {
                CloseAddForm();
                SearchEntry.Text = existing.Name;
                return;
            }
        }

        _saving = true;
        AddSaveButton.IsEnabled = false;
        AddErrorLabel.IsVisible = false;
        try
        {
            var result = await _players.AddAsync(_clubId, name, preferred, dob);
            if (!result.Success || result.Player is null)
            {
                ShowAddError(result.Error ?? "Could not add the player.");
                return;
            }
            _all.Add(result.Player);
            _all.Sort((a, b) => string.Compare(a.NameKey, b.NameKey, StringComparison.Ordinal));
            CloseAddForm();
            SearchEntry.Text = "";
            RenderList();
        }
        finally
        {
            _saving = false;
            AddSaveButton.IsEnabled = true;
        }
    }

    private void ShowAddError(string message)
    {
        AddErrorLabel.Text = message;
        AddErrorLabel.IsVisible = true;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private async void OnRetryClicked(object? sender, EventArgs e) => await LoadAsync();

    private void ShowNotice(string message, bool retry)
    {
        NoticeLabel.Text = message;
        RetryButton.IsVisible = retry;
        NoticeCard.IsVisible = true;
    }

    private void HideNotice() => NoticeCard.IsVisible = false;

    private void SetBusy(bool busy)
    {
        Busy.IsRunning = busy;
        Busy.IsVisible = busy;
    }

    private Style? StyleOf(string key)
        => Resources.TryGetValue(key, out var v) && v is Style s ? s : null;

    private static T? GetService<T>() where T : class
    {
        var services = Application.Current?.Handler?.MauiContext?.Services
                       ?? Shell.Current?.Handler?.MauiContext?.Services;
        return services?.GetService<T>();
    }
}
