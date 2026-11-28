using System.Globalization;
using TurfTime2.Helpers;
using TurfTime2.Models;
using TurfTime2.Services;

namespace TurfTime2;

/// <summary>
/// Club player list (docs/club-players-spec.md §7.1–7.3): search, add, and
    /// (owners/admins) archive, restore and delete.
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
        var matches = _all
            .Where(p => !p.IsArchived && ClubPlayerNames.Matches(p.NameKey, query))
            .ToList();

        PlayerList.Children.Clear();
        foreach (var p in matches.Take(MaxRows))
            PlayerList.Children.Add(BuildRow(p, archived: false));

        var active = _all.Count(p => !p.IsArchived);
        CountLabel.Text = ClubPlayerNames.Normalize(query).Length == 0
            ? (active == 1 ? "1 player" : $"{active} players")
            : $"{matches.Count} of {active} players";
        if (matches.Count > MaxRows)
            CountLabel.Text += $" · showing {MaxRows}, type to narrow";
        if (active == 0)
            CountLabel.Text = "No players yet. Add the first one above.";

        // Inline "Add X to the club" when nothing matches the typed name exactly (§7.2).
        var typed = ClubPlayerNames.CleanDisplay(query);
        var exact = typed.Length > 0 && _all.Any(p => ClubPlayerNames.IsExactDuplicate(typed, p.Name, p.PreferredName));
        AddFromSearchButton.IsVisible = _access.CanAdd && typed.Length > 0 && !exact;
        AddFromSearchButton.Text = $"Add \"{typed}\" to the club";

        var archived = _all
            .Where(p => p.IsArchived && ClubPlayerNames.Matches(p.NameKey, query))
            .ToList();
        var showArchived = _access.IsClubAdmin && ShowArchivedSwitch.IsToggled;
        ArchivedToggleRow.IsVisible = _access.IsClubAdmin;
        ArchivedList.Children.Clear();
        if (showArchived)
        {
            foreach (var p in archived.Take(MaxRows))
                ArchivedList.Children.Add(BuildRow(p, archived: true));
            ArchivedCountLabel.IsVisible = true;
            ArchivedCountLabel.Text = archived.Count == 0
                ? (ClubPlayerNames.Normalize(query).Length == 0
                    ? "No archived players."
                    : "No archived players match this search.")
                : (archived.Count == 1 ? "1 archived player" : $"{archived.Count} archived players");
        }
        else
        {
            ArchivedCountLabel.IsVisible = false;
        }
    }

    private View BuildRow(ClubPlayer p, bool archived)
    {
        var details = new List<string>();
        if (archived) details.Add("Archived");
        if (ClubAgeGroups.TryParseDateOfBirth(p.DateOfBirth, out var dob))
        {
            details.Add(dob.ToString("d MMM yyyy", CultureInfo.CurrentCulture));
            details.Add(ClubAgeGroups.AgeGroupFor(dob, ClubAgeGroups.CurrentSeason()));
        }
        else
        {
            details.Add("Date of birth missing");
        }
        // Prefer teamIds (current assignments). Open teams entries supply the names.
        var current = ClubPlayerAssignments.CurrentTeamNames(p.Teams, p.TeamIds);
        if (current.Count > 0) details.Add(string.Join(", ", current));

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
        if (_access.CanView)
        {
            var tap = new TapGestureRecognizer();
            tap.Tapped += async (_, _) => await OnPlayerTapped(p);
            border.GestureRecognizers.Add(tap);
        }
        return border;
    }

    private void OnShowArchivedToggled(object? sender, ToggledEventArgs e)
    {
        if (ListCard.IsVisible) RenderList();
    }

    private async Task OnPlayerTapped(ClubPlayer player)
    {
        if (_saving || !_access.CanView) return;

        if (!_access.IsClubAdmin)
        {
            await OpenStatsAsync(player);
            return;
        }

        var restoreOrArchive = player.IsArchived ? "Restore" : "Archive";
        var choice = await DisplayActionSheet(player.DisplayName, "Cancel", "Delete", "Edit", "Stats", restoreOrArchive);
        if (string.IsNullOrEmpty(choice) || choice == "Cancel") return;

        if (choice == "Edit")
            OpenEditForm(player);
        else if (choice == "Stats")
            await OpenStatsAsync(player);
        else if (choice == "Archive")
            await ArchivePlayerAsync(player);
        else if (choice == "Restore")
            await RestorePlayerAsync(player);
        else if (choice == "Delete")
            await DeletePlayerAsync(player);
    }

    private async Task OpenStatsAsync(ClubPlayer player)
    {
        ClubPlayerStatsPage.Begin(_clubId, player);
        await Shell.Current.GoToAsync("details/clubplayerstats");
    }

    private async Task ArchivePlayerAsync(ClubPlayer player)
    {
        var teams = ClubPlayerAssignments.CurrentTeamNames(player.Teams, player.TeamIds);
        var teamBit = teams.Count == 0
            ? "They will be hidden from the club list and from Change player, and can be restored later."
            : "They will be taken off " + string.Join(", ", teams)
              + ". They will be hidden from the club list and from Change player, and can be restored later.";
        var go = await DisplayAlert(
            "Archive " + player.DisplayName + "?",
            teamBit,
            "Archive",
            "Cancel");
        if (!go) return;

        await RunPlayerWriteAsync(
            () => _players!.ArchiveAsync(_clubId, player.PlayerId),
            onSuccess: result =>
            {
                ReplacePlayer(result.Player ?? player, ClubPlayer.StatusArchived);
                ShowArchivedSwitch.IsToggled = true;
            },
            failTitle: "Archive player",
            failFallback: "Could not archive the player.");
    }

    private async Task RestorePlayerAsync(ClubPlayer player)
    {
        var go = await DisplayAlert(
            "Restore " + player.DisplayName + "?",
            "They will show up in the club list and in Change player again. They are not put back on a team automatically.",
            "Restore",
            "Cancel");
        if (!go) return;

        await RunPlayerWriteAsync(
            () => _players!.RestoreAsync(_clubId, player.PlayerId),
            onSuccess: result => ReplacePlayer(result.Player ?? player, ClubPlayer.StatusActive),
            failTitle: "Restore player",
            failFallback: "Could not restore the player.");
    }

    private async Task DeletePlayerAsync(ClubPlayer player)
    {
        var teams = ClubPlayerAssignments.CurrentTeamNames(player.Teams, player.TeamIds);
        var teamBit = teams.Count == 0
            ? ""
            : " They will first be taken off " + string.Join(", ", teams) + ".";
        var go = await DisplayAlert(
            "Delete " + player.DisplayName + "?",
            "This cannot be undone. Match reports keep the player's name, but their stats history is removed."
            + teamBit,
            "Delete",
            "Cancel");
        if (!go) return;

        await RunPlayerWriteAsync(
            () => _players!.DeleteAsync(_clubId, player.PlayerId),
            onSuccess: _ => _all.RemoveAll(p => p.PlayerId == player.PlayerId),
            failTitle: "Delete player",
            failFallback: "Could not delete the player.");
    }

    private async Task RunPlayerWriteAsync(
        Func<Task<ClubPlayerWriteResult>> action,
        Action<ClubPlayerWriteResult> onSuccess,
        string failTitle,
        string failFallback)
    {
        if (_saving || _players is null) return;
        _saving = true;
        SetBusy(true);
        try
        {
            var result = await action();
            if (!result.Success)
            {
                await DisplayAlert(failTitle, result.Error ?? failFallback, "OK");
                return;
            }
            onSuccess(result);
            RenderList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ClubPlayersPage] Write: {ex.Message}");
            await DisplayAlert(failTitle, "Could not reach the server. Check your connection and try again.", "OK");
        }
        finally
        {
            _saving = false;
            SetBusy(false);
        }
    }

    private void ReplacePlayer(ClubPlayer updated, string status)
    {
        updated.Status = status;
        var i = _all.FindIndex(p => p.PlayerId == updated.PlayerId);
        if (i >= 0) _all[i] = updated;
        else _all.Add(updated);
        _all.Sort((a, b) => string.Compare(a.NameKey, b.NameKey, StringComparison.Ordinal));
    }

    private void OnSearchChanged(object? sender, TextChangedEventArgs e)
    {
        if (ListCard.IsVisible) RenderList();
    }

    // ── Add ───────────────────────────────────────────────────────────────

    private void OnAddClicked(object? sender, EventArgs e) => OpenAddForm("");

    private void OnAddFromSearchClicked(object? sender, EventArgs e) =>
        OpenAddForm(ClubPlayerNames.CleanDisplay(SearchEntry.Text));

    private ClubPlayer? _editing;

    private void OpenAddForm(string name)
    {
        if (!_access.CanAdd) return;
        _editing = null;
        AddTitleLabel.Text = "Add player";
        AddSaveButton.Text = "Add to club";
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

    /// <summary>Edit an existing player: same form as Add. Club owner and club admins only (the rules agree;
    /// a coach may only change names, so coaches don't get this).</summary>
    private void OpenEditForm(ClubPlayer player)
    {
        if (!_access.IsClubAdmin) return;
        _editing = player;
        AddTitleLabel.Text = "Edit player";
        AddSaveButton.Text = "Save changes";
        AddNameEntry.Text = player.Name;
        AddPreferredEntry.Text = player.PreferredName ?? "";
        AddDobPicker.MaximumDate = DateTime.Today;
        var min = DateTime.Today.AddYears(-80);
        if (ClubAgeGroups.TryParseDateOfBirth(player.DateOfBirth, out var dob))
        {
            var d = dob.ToDateTime(TimeOnly.MinValue);
            AddDobPicker.MinimumDate = d < min ? d : min;
            AddDobPicker.Date = d;
            _dobPicked = true;
        }
        else
        {
            AddDobPicker.MinimumDate = min;
            AddDobPicker.Date = DateTime.Today.AddYears(-10);
            _dobPicked = false; // date of birth missing: it has to be entered to save
        }
        UpdateAgeGroupHint();
        AddErrorLabel.IsVisible = false;
        AddCard.IsVisible = true;
        AddButton.IsVisible = false;
        AddNameEntry.Focus();
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
        _editing = null;
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

        if (_editing is not null)
        {
            await SaveEditAsync(_editing, name, preferred, dob);
            return;
        }

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

    private async Task SaveEditAsync(ClubPlayer player, string name, string preferred, DateOnly dob)
    {
        var oldDob = ClubAgeGroups.TryParseDateOfBirth(player.DateOfBirth, out var od) ? (DateOnly?)od : null;
        var dobChanged = oldDob != dob;
        var nameChanged = !string.Equals(name, player.Name, StringComparison.Ordinal)
                          || !string.Equals(preferred, player.PreferredName ?? "", StringComparison.Ordinal);
        if (!dobChanged && !nameChanged) { CloseAddForm(); return; }

        if (nameChanged)
        {
            var other = _all.FirstOrDefault(p => p.PlayerId != player.PlayerId
                && ClubPlayerNames.IsExactDuplicate(name, p.Name, p.PreferredName));
            if (other is not null)
            {
                var keep = await DisplayAlert("Same name as another player",
                    $"{other.DisplayName} is already in the club with this name.", "Save anyway", "Cancel");
                if (!keep) return;
            }
        }
        if (dobChanged && ClubPlayerAssignments.CurrentTeamNames(player.Teams, player.TeamIds).Count > 0)
        {
            var go = await DisplayAlert("Change date of birth?",
                "This player is on a team. Their age group is worked out from the date of birth; their team " +
                "assignments are not changed or re-checked.", "Save", "Cancel");
            if (!go) return;
        }

        var update = new ClubPlayerUpdate
        {
            Name = nameChanged ? name : null,
            SetPreferredName = nameChanged,
            PreferredName = preferred.Length == 0 ? null : preferred,
            DateOfBirth = dobChanged ? dob : null,
        };

        _saving = true;
        AddSaveButton.IsEnabled = false;
        AddErrorLabel.IsVisible = false;
        try
        {
            var result = await _players!.UpdateAsync(_clubId, player.PlayerId, update);
            if (!result.Success || result.Player is null)
            {
                ShowAddError(result.Error ?? "Could not save the changes. Only a club owner or admin can edit players.");
                return;
            }
            ReplacePlayer(result.Player, player.Status);
            CloseAddForm();
            RenderList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ClubPlayersPage] Edit: {ex.Message}");
            ShowAddError("Could not reach the server. Check your connection and try again.");
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
