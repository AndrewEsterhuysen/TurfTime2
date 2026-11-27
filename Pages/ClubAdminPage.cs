using TurfTime2.Helpers;
using TurfTime2.Services;

namespace TurfTime2;

/// <summary>
/// Settings > Club admin (to-do item 8, final rules 25 Nov 2026).
/// <list type="bullet">
/// <item>Anyone: redeem a Club Admin invite (no team invite needed).</item>
/// <item>Club owner: invite a club admin (single use, 7 days), promote a club member, remove a club admin.</item>
/// <item>Club owner / admins: club teams list with each team's status, create a team, and per team:
/// open, share the invite, and with full control (no other team admin) edit, manage members,
/// make yourself team admin, or delete. Firestore rules enforce all of it.</item>
/// </list>
/// </summary>
public sealed class ClubAdminPage : ContentPage
{
    private readonly Entry _inviteEntry = new() { Placeholder = "Paste the club admin invite code", ClearButtonVisibility = ClearButtonVisibility.WhileEditing };
    private readonly Entry _nameEntry = new() { Placeholder = "Your name (shown to the club)" };
    private readonly Picker _clubPicker = new() { Title = "Choose a club" };
    private readonly Label _roleLabel = new() { FontSize = 13, Opacity = 0.75 };
    private readonly Label _status = new() { FontSize = 13, Opacity = 0.75, IsVisible = false };
    private readonly VerticalStackLayout _clubSection = new() { Spacing = 10, IsVisible = false };
    private readonly VerticalStackLayout _teamsStack = new() { Spacing = 8 };
    private readonly VerticalStackLayout _ownerSection = new() { Spacing = 8, IsVisible = false };
    private readonly VerticalStackLayout _membersStack = new() { Spacing = 6 };
    private readonly ActivityIndicator _busy = new() { IsRunning = false, IsVisible = false };

    private List<CloudClubSummary> _clubs = new();
    private bool _loading;

    public ClubAdminPage()
    {
        Title = "Club admin";
        var redeemButton = new Button { Text = "Redeem invite" };
        redeemButton.Clicked += OnRedeemClicked;
        var refreshButton = new Button { Text = "Refresh" };
        refreshButton.Clicked += async (_, _) => await LoadClubsAsync();
        var createTeamButton = new Button { Text = "Create a team in this club" };
        createTeamButton.Clicked += OnCreateTeamClicked;
        var inviteButton = new Button { Text = "Invite a club admin" };
        inviteButton.Clicked += OnInviteClubAdminClicked;
        _clubPicker.SelectedIndexChanged += async (_, _) => await LoadSelectedClubAsync();

        _ownerSection.Children.Add(Header("Club admins (owner only)"));
        _ownerSection.Children.Add(Caption("Only you, the club owner, can add or remove club admins. Club admins don't need a team invite. Tap a person to change their role."));
        _ownerSection.Children.Add(inviteButton);
        _ownerSection.Children.Add(_membersStack);

        _clubSection.Children.Add(_roleLabel);
        _clubSection.Children.Add(Header("Club teams"));
        _clubSection.Children.Add(Caption(
            "A team with no team admin is run by the club: club admins can edit it, its players, members and invite, or delete it. " +
            "Once someone else is team admin, club admins can see everything except chat and edit only the Details pages. " +
            "Club admins never run games unless they are the team admin. Tap a team for options."));
        _clubSection.Children.Add(createTeamButton);
        _clubSection.Children.Add(_teamsStack);
        _clubSection.Children.Add(_ownerSection);

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(16),
                Spacing = 12,
                Children =
                {
                    Header("Redeem a club admin invite"),
                    Caption("Got an invite from a club owner? Paste the code (it looks like club-name-abc123/ABCDEFGHJK)."),
                    _nameEntry,
                    _inviteEntry,
                    redeemButton,
                    new BoxView { HeightRequest = 1, Opacity = 0.2, Color = Colors.Gray, Margin = new Thickness(0, 8) },
                    Header("Your clubs"),
                    new HorizontalStackLayout { Spacing = 8, Children = { refreshButton, _busy } },
                    _clubPicker,
                    _status,
                    _clubSection,
                }
            }
        };
    }

    private static Label Header(string text) => new() { Text = text, FontSize = 18, FontAttributes = FontAttributes.Bold };
    private static Label Caption(string text) => new() { Text = text, FontSize = 13, Opacity = 0.75 };

    private static T? Resolve<T>() where T : class
    {
        try { return Application.Current?.Handler?.MauiContext?.Services.GetService(typeof(T)) as T; }
        catch { return null; }
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (string.IsNullOrWhiteSpace(_nameEntry.Text)) _nameEntry.Text = UserDisplayName.Get();
        await LoadClubsAsync();
    }

    private void SetBusy(bool busy) { _busy.IsRunning = busy; _busy.IsVisible = busy; }

    private void ShowStatus(string? text)
    {
        _status.Text = text ?? "";
        _status.IsVisible = !string.IsNullOrWhiteSpace(text);
    }

    private CloudClubSummary? SelectedClub =>
        _clubPicker.SelectedIndex >= 0 && _clubPicker.SelectedIndex < _clubs.Count ? _clubs[_clubPicker.SelectedIndex] : null;

    private async Task LoadClubsAsync(string? selectClubId = null)
    {
        if (_loading) return;
        _loading = true;
        SetBusy(true);
        try
        {
            var svc = Resolve<IClubAdminService>();
            var previous = selectClubId ?? SelectedClub?.ClubId;
            _clubs = svc is null ? new() : (await svc.ListMyAdminClubsAsync()).ToList();
            _clubPicker.Items.Clear();
            foreach (var c in _clubs)
                _clubPicker.Items.Add($"{(string.IsNullOrWhiteSpace(c.ClubName) ? c.ClubId : c.ClubName)} ({(c.Role == "owner" ? "Owner" : "Club admin")})");
            if (_clubs.Count == 0)
            {
                _clubSection.IsVisible = false;
                ShowStatus("You're not an owner or club admin of any club yet. Create a club in Team Selection, or redeem an invite above.");
                return;
            }
            ShowStatus(null);
            var idx = previous is null ? 0 : Math.Max(0, _clubs.FindIndex(c => c.ClubId == previous));
            if (_clubPicker.SelectedIndex == idx) await LoadSelectedClubAsync();
            else _clubPicker.SelectedIndex = idx;
        }
        catch (Exception ex)
        {
            ShowStatus($"Could not load your clubs: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
            _loading = false;
        }
    }

    private async Task LoadSelectedClubAsync()
    {
        var club = SelectedClub;
        _clubSection.IsVisible = club is not null;
        _teamsStack.Children.Clear();
        _membersStack.Children.Clear();
        if (club is null) return;
        var isOwner = club.Role == "owner";
        _roleLabel.Text = isOwner ? "You are the club owner." : "You are a club admin.";
        _ownerSection.IsVisible = isOwner;

        var svc = Resolve<IClubAdminService>();
        if (svc is null) return;
        SetBusy(true);
        try
        {
            var teams = await svc.ListClubTeamsAsync(club.ClubId);
            if (teams.Count == 0)
                _teamsStack.Children.Add(Caption("No teams yet. Create one above."));
            foreach (var t in teams)
                _teamsStack.Children.Add(TeamRow(club, t));

            if (isOwner)
            {
                var members = await svc.ListClubMembersAsync(club.ClubId);
                foreach (var m in members)
                    _membersStack.Children.Add(MemberRow(club, m));
            }
        }
        catch (Exception ex)
        {
            _teamsStack.Children.Add(Caption($"Could not load teams: {ex.Message}"));
        }
        finally
        {
            SetBusy(false);
        }
    }

    private View TeamRow(CloudClubSummary club, ClubTeamStatus t)
    {
        var age = string.IsNullOrWhiteSpace(t.AgeGroup) ? "" : $"{t.AgeGroup}{(t.Season > 0 ? $" · {t.Season}" : "")}";
        var row = new Border
        {
            Padding = new Thickness(12, 10),
            StrokeThickness = 1,
            Stroke = Colors.Gray.WithAlpha(0.4f),
            Content = new VerticalStackLayout
            {
                Spacing = 2,
                Children =
                {
                    new Label { Text = string.IsNullOrWhiteSpace(t.TeamName) ? t.TeamId : t.TeamName, FontAttributes = FontAttributes.Bold },
                    new Label { Text = age, FontSize = 13, IsVisible = age.Length > 0 },
                    new Label { Text = t.StatusText, FontSize = 13, Opacity = 0.75 },
                }
            }
        };
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) => await OnTeamTappedAsync(club, t);
        row.GestureRecognizers.Add(tap);
        return row;
    }

    private View MemberRow(CloudClubSummary club, ClubMemberInfo m)
    {
        var name = string.IsNullOrWhiteSpace(m.DisplayName) ? $"(no name) {m.Uid[..Math.Min(6, m.Uid.Length)]}" : m.DisplayName;
        var role = m.Role switch { "owner" => "Owner", "admin" => "Club admin", _ => "Member" };
        var label = new Label { Text = $"{name} · {role}", Padding = new Thickness(4, 6) };
        if (m.Role != "owner")
        {
            var tap = new TapGestureRecognizer();
            tap.Tapped += async (_, _) => await OnClubMemberTappedAsync(club, m, name);
            label.GestureRecognizers.Add(tap);
        }
        return label;
    }

    // ── Redeem ─────────────────────────────────────────────────────────────

    private async void OnRedeemClicked(object? sender, EventArgs e)
    {
        var svc = Resolve<IClubAdminService>();
        if (svc is null) return;
        if (!UserDisplayName.TryValidate(_nameEntry.Text, out var displayName, out var nameError))
        {
            await DisplayAlert("Your name", nameError ?? "Enter your name.", "OK");
            return;
        }
        SetBusy(true);
        try
        {
            var result = await svc.RedeemInviteAsync(_inviteEntry.Text ?? "", displayName);
            await DisplayAlert(result.Ok ? "Club admin" : "Invite not accepted", result.Message, "OK");
            if (result.Ok)
            {
                UserDisplayName.Set(displayName);
                _inviteEntry.Text = "";
                await LoadClubsAsync(result.ClubId);
            }
        }
        finally { SetBusy(false); }
    }

    // ── Owner: club admins ────────────────────────────────────────────────

    private async void OnInviteClubAdminClicked(object? sender, EventArgs e)
    {
        var club = SelectedClub;
        var svc = Resolve<IClubAdminService>();
        if (club is null || svc is null) return;
        SetBusy(true);
        ClubAdminInviteResult result;
        try { result = await svc.CreateInviteAsync(club.ClubId, club.ClubName); }
        finally { SetBusy(false); }
        if (!result.Ok)
        {
            await DisplayAlert("Invite", result.Error, "OK");
            return;
        }
        var choice = await DisplayActionSheet(
            $"Club admin invite\n{club.ClubId}/{result.Code}\nSingle use · expires {result.ExpiresUtc.ToLocalTime():d MMM}",
            "Done", "Revoke this invite", "Share invite", "Copy code");
        if (choice == "Share invite")
            await DesktopMode.ShareOrCopyTextAsync(this, "Club admin invite", result.InviteText);
        else if (choice == "Copy code")
            await Clipboard.Default.SetTextAsync($"{club.ClubId}/{result.Code}");
        else if (choice == "Revoke this invite")
        {
            var r = await svc.RevokeInviteAsync(club.ClubId, result.Code);
            await DisplayAlert("Invite", r == "success" ? "Invite revoked." : r, "OK");
        }
    }

    private async Task OnClubMemberTappedAsync(CloudClubSummary club, ClubMemberInfo m, string name)
    {
        var svc = Resolve<IClubAdminService>();
        if (svc is null) return;
        var action = m.Role == "admin" ? "Remove club admin role" : "Make club admin";
        var choice = await DisplayActionSheet(name, "Cancel", null, action);
        if (choice != action) return;
        var result = await svc.SetClubMemberRoleAsync(club.ClubId, m.Uid, m.Role == "admin" ? "member" : "admin");
        if (result != "success") await DisplayAlert("Club admins", result.Replace("error: ", ""), "OK");
        await LoadSelectedClubAsync();
    }

    // ── Teams ──────────────────────────────────────────────────────────────

    private async void OnCreateTeamClicked(object? sender, EventArgs e)
    {
        var club = SelectedClub;
        if (club is null) return;
        Services.QrCodeService.RegisterManagedClub(club.ClubId, club.ClubName);
        await DisplayAlert("Create a team",
            $"In Team Selection, go to Create Team > \"Add team under a club you manage\", choose {club.ClubName}. " +
            "You'll start as its team admin. Share the team's invite, then promote someone to team admin later. " +
            "You can leave the team afterwards and stay a club admin.",
            "OK");
        await Shell.Current.GoToAsync("settings/teamdetails");
    }

    private async Task OnTeamTappedAsync(CloudClubSummary club, ClubTeamStatus t)
    {
        var options = new List<string> { "Open this team" };
        if (t.Access == ClubTeamAccess.Full)
            options.AddRange(new[] { "Edit name, age group and season", "Manage members", "Make me team admin" });
        options.Add("Share the team invite");
        if (t.TeamAdmins.Count > 0)
            options.Add("Remove team admin");
        if (!string.IsNullOrEmpty(t.MyTeamRole))
            options.Add("Leave the team (stay club admin)");
        string? destroy = t.Access == ClubTeamAccess.Full ? "Delete this team" : null;

        var name = string.IsNullOrWhiteSpace(t.TeamName) ? t.TeamId : t.TeamName;
        var choice = await DisplayActionSheet($"{name}\n{t.StatusText}", "Cancel", destroy, options.ToArray());
        switch (choice)
        {
            case "Open this team": await OpenTeamAsync(club, t); break;
            case "Edit name, age group and season": await EditTeamAsync(t); break;
            case "Manage members": await ManageMembersAsync(t); break;
            case "Make me team admin": await JoinAsAdminAsync(t); break;
            case "Share the team invite": await ShareInviteAsync(t); break;
            case "Remove team admin": await RemoveTeamAdminAsync(club, t); break;
            case "Leave the team (stay club admin)": await LeaveTeamAsync(t); break;
            case "Delete this team": await DeleteTeamAsync(t); break;
        }
    }

    /// <summary>Club owner only (the list only fills TeamAdmins for the owner; the rules re-check).</summary>
    private async Task RemoveTeamAdminAsync(CloudClubSummary club, ClubTeamStatus t)
    {
        var svc = Resolve<IClubAdminService>();
        if (svc is null || t.TeamAdmins.Count == 0) return;
        var teamName = string.IsNullOrWhiteSpace(t.TeamName) ? t.TeamId : t.TeamName;
        ClubTeamAdminInfo? target = t.TeamAdmins[0];
        if (t.TeamAdmins.Count > 1)
        {
            var labels = t.TeamAdmins.Select((a, i) => a.Label + (t.TeamAdmins.Count(x => x.Label == a.Label) > 1 ? $" ({i + 1})" : "")).ToArray();
            var pick = await DisplayActionSheet("Which team admin?", "Cancel", null, labels);
            var idx = Array.IndexOf(labels, pick);
            if (idx < 0) return;
            target = t.TeamAdmins[idx];
        }
        var ok = await DisplayAlert("Remove team admin",
            $"Remove {target.Label} as team admin of {teamName}?\n\n" +
            "They're removed from the team. If no other team admin is left, club admins get full control of the team, " +
            "and you can then make yourself or someone else team admin. A record of this is kept.",
            "Remove", "Cancel");
        if (!ok) return;
        SetBusy(true);
        try
        {
            var result = await svc.RemoveTeamAdminAsync(club.ClubId, t.TeamId, teamName, target.Uid);
            await DisplayAlert("Team admin", result == "success"
                ? $"{target.Label} was removed as team admin of {teamName}."
                : result.Replace("error: ", ""), "OK");
            await LoadSelectedClubAsync();
        }
        finally { SetBusy(false); }
    }

    private async Task OpenTeamAsync(CloudClubSummary club, ClubTeamStatus t)
    {
        var name = string.IsNullOrWhiteSpace(t.TeamName) ? t.TeamId : t.TeamName;
        if (!string.IsNullOrEmpty(t.MyTeamRole))
        {
            await DisplayAlert("Open team", $"You're on {name} as {(t.IAmTeamAdmin ? "team admin" : "a member")}. Select it in Team Selection.", "OK");
            await Shell.Current.GoToAsync("settings/teamdetails");
            return;
        }
        TeamDetailsPage.OpenTeamAsClubAdmin(t.TeamId, name, club.ClubId, club.ClubName, t.Access);
        var what = t.Access == ClubTeamAccess.Full
            ? "You have full control as a club admin: team details, Details pages, club players and team members. You can't run games (the Game screen is view only) or see chat."
            : "This team has a team admin, so you can view games, reports and players and edit the Details pages (location, kit, duties). Chat stays with team members.";
        await DisplayAlert("Team opened", $"Now viewing: {name}\n\n{what}", "OK");
    }

    private async Task EditTeamAsync(ClubTeamStatus t)
    {
        var svc = Resolve<IClubAdminService>();
        if (svc is null) return;
        var name = await DisplayPromptAsync("Team name", "Name", "Next", "Cancel", initialValue: t.TeamName, maxLength: 80);
        if (string.IsNullOrWhiteSpace(name)) return;
        var age = await DisplayActionSheet("Age group", "Cancel", null, ClubAgeGroups.StandardLabels);
        if (string.IsNullOrEmpty(age) || age == "Cancel") return;
        var seasonText = await DisplayPromptAsync("Season", "Year", "Save", "Cancel",
            initialValue: (t.Season > 0 ? t.Season : ClubAgeGroups.CurrentSeason()).ToString(), maxLength: 4, keyboard: Keyboard.Numeric);
        if (!int.TryParse(seasonText, out var season)) return;
        var result = await svc.UpdateTeamAsync(t.TeamId, name, age, season);
        if (result == "success" && Preferences.Get("team_id", "") == t.TeamId)
            Preferences.Set("team_name", name.Trim());
        await DisplayAlert("Team", result == "success" ? "Saved." : result.Replace("error: ", ""), "OK");
        await LoadSelectedClubAsync();
    }

    private async Task ManageMembersAsync(ClubTeamStatus t)
    {
        var svc = Resolve<IClubAdminService>();
        var teams = Resolve<ICloudTeamService>();
        if (svc is null || teams is null) return;
        var members = await teams.ListMembersAsync(t.TeamId);
        if (members.Count == 0)
        {
            await DisplayAlert("Members", "No one has joined yet. Share the team invite so people can join, then promote someone to team admin here.", "OK");
            return;
        }
        var labels = members.Select(m => $"{(string.IsNullOrWhiteSpace(m.DisplayName) ? m.Uid[..Math.Min(6, m.Uid.Length)] : m.DisplayName)} · {(m.IsAdmin ? "Admin" : "Member")}").ToArray();
        var pick = await DisplayActionSheet("Team members", "Cancel", null, labels);
        var idx = Array.IndexOf(labels, pick);
        if (idx < 0) return;
        var member = members[idx];
        var roleAction = member.IsAdmin ? "Make member" : "Make team admin";
        var action = await DisplayActionSheet(labels[idx], "Cancel", "Remove from team", roleAction);
        string? result = action switch
        {
            "Make team admin" => await svc.SetTeamMemberRoleAsync(t.TeamId, member.Uid, "admin"),
            "Make member" => await svc.SetTeamMemberRoleAsync(t.TeamId, member.Uid, "member"),
            "Remove from team" => await svc.RemoveTeamMemberAsync(t.TeamId, member.Uid),
            _ => null,
        };
        if (result is null) return;
        var note = action == "Make team admin" && result == "success"
            ? "Done. The team now has a team admin, so club admins can only view it and edit Details."
            : result == "success" ? "Done." : result.Replace("error: ", "");
        await DisplayAlert("Members", note, "OK");
        await LoadSelectedClubAsync();
    }

    private async Task JoinAsAdminAsync(ClubTeamStatus t)
    {
        var svc = Resolve<IClubAdminService>();
        if (svc is null) return;
        var ok = await DisplayAlert("Make me team admin",
            "You'll join this team as its team admin, with full team admin rights including running games. You can leave later and stay a club admin.",
            "Join as team admin", "Cancel");
        if (!ok) return;
        var result = await svc.JoinTeamAsync(t.TeamId, "admin", UserDisplayName.Get());
        await DisplayAlert("Team admin", result == "success"
            ? "You're now team admin. Select the team in Team Selection to run games."
            : result.Replace("error: ", ""), "OK");
        await LoadSelectedClubAsync();
    }

    private async Task LeaveTeamAsync(ClubTeamStatus t)
    {
        var svc = Resolve<IClubAdminService>();
        var auth = Resolve<IFirebaseAuthService>();
        var uid = auth?.UserId;
        if (svc is null || string.IsNullOrEmpty(uid)) return;
        var ok = await DisplayAlert("Leave team",
            t.IAmTeamAdmin && t.TeamAdminCount <= 1
                ? "You're the only team admin. After you leave, the team is run by the club again."
                : "You'll leave the team. You stay a club admin.",
            "Leave", "Cancel");
        if (!ok) return;
        var result = await svc.RemoveTeamMemberAsync(t.TeamId, uid);
        if (result == "success")
        {
            Preferences.Remove($"{t.TeamId}_role");
            Preferences.Remove($"user_role_{t.TeamId}");
            if (Preferences.Get("team_id", "") == t.TeamId)
                Preferences.Set("user_role", "clubadmin");
        }
        await DisplayAlert("Leave team", result == "success" ? "You've left the team." : result.Replace("error: ", ""), "OK");
        await LoadSelectedClubAsync();
    }

    private async Task ShareInviteAsync(ClubTeamStatus t)
    {
        var teams = Resolve<ICloudTeamService>();
        if (teams is null) return;
        var code = await teams.GetTeamInviteCodeAsync(t.TeamId);
        if (string.IsNullOrWhiteSpace(code))
        {
            await DisplayAlert("Invite", "Could not read this team's invite code.", "OK");
            return;
        }
        var name = string.IsNullOrWhiteSpace(t.TeamName) ? t.TeamId : t.TeamName;
        await DesktopMode.ShareOrCopyTextAsync(this, "Team invite",
            $"Join {name} on TurfTimer. In the app open Settings > Team Selection > Join team and enter invite code: {code}");
    }

    private async Task DeleteTeamAsync(ClubTeamStatus t)
    {
        var svc = Resolve<IClubAdminService>();
        if (svc is null) return;
        var name = string.IsNullOrWhiteSpace(t.TeamName) ? t.TeamId : t.TeamName;
        var ok = await DisplayAlert("Delete team",
            $"Delete {name}? Its members, matches, reports and chat are removed for everyone. Club players stay in the club list.",
            "Delete", "Cancel");
        if (!ok) return;
        var result = await svc.DeleteTeamAsync(t.TeamId);
        if (result == "success" && Preferences.Get("team_id", "") == t.TeamId)
        {
            Preferences.Remove("team_id");
            Preferences.Remove("team_name");
            Preferences.Remove("team_mode");
            Preferences.Remove("user_role");
        }
        await DisplayAlert("Delete team", result == "success" ? "Team deleted." : result.Replace("error: ", ""), "OK");
        await LoadSelectedClubAsync();
    }
}
