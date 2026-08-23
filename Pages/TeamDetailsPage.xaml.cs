using System.Text;
using System.Collections.ObjectModel;
using TurfTime2.Helpers;
using TurfTime2.Models;
using TurfTime2.Services;

namespace TurfTime2;

public partial class TeamDetailsPage : ContentPage
{
	private const string TEAM_MODE_KEY = "team_mode"; // "shared" or "local"
	private const string TEAM_ID_KEY = "team_id";
	private const string TEAM_NAME_KEY = "team_name";
	private const string USER_ROLE_KEY = "user_role"; // "admin" or "member"

	private ObservableCollection<LocalTeamItem> _localTeams = new();
	private ObservableCollection<SharedTeamItem> _sharedTeams = new();

	/// <summary>Avoid re-publishing the same invite index on every Appear / LoadCurrentTeam.</summary>
	private string? _lastPublishedInviteKey;
	private DateTimeOffset _lastInvitePublishUtc = DateTimeOffset.MinValue;
	private static readonly TimeSpan InvitePublishMinInterval = TimeSpan.FromMinutes(5);

	public TeamDetailsPage()
	{
		InitializeComponent();
		LocalTeamsCollection.ItemsSource = _localTeams;
		SharedTeamsCollection.ItemsSource = _sharedTeams;
		LoadCurrentTeam();
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
		DetailsPage.ApplyPageTeamTitle(this, "Team");

		var teamMode = Preferences.Get(TEAM_MODE_KEY, string.Empty);
		var teamId = Preferences.Get(TEAM_ID_KEY, string.Empty);

		// Paint from local Preferences first so Team Admin taps stay responsive.
		// Cloud role / invite refresh runs in the background (was awaited and blocked UI).
		LoadCurrentTeam();

		if (!string.IsNullOrEmpty(teamMode))
		{
			// Only set when unset — re-assigning true re-fires CheckedChanged and reloads lists every visit.
			if (teamMode == "local" && !LocalCheckbox.IsChecked)
				LocalCheckbox.IsChecked = true;
			else if (teamMode == "shared" && !SharedCheckbox.IsChecked)
				SharedCheckbox.IsChecked = true;
		}

		if (teamMode == "shared" && !string.IsNullOrEmpty(teamId))
			_ = RefreshRoleAndAdminToolsInBackgroundAsync(teamId);
	}

	/// <summary>
	/// Soft-refresh role (and invite display) without gating the page on network.
	/// </summary>
	private async Task RefreshRoleAndAdminToolsInBackgroundAsync(string teamId)
	{
		try
		{
			var role = await RefreshMyRoleFromCloudAsync(teamId).ConfigureAwait(false);
			if (role is null) return;

			await MainThread.InvokeOnMainThreadAsync(() =>
			{
				LoadCurrentTeam(refreshInviteFromCloud: true);
			});
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine(
				$"[TeamDetails] Background role refresh: {ex.Message}");
		}
	}

	/// <summary>
	/// Writes role into all local caches used by Game, Team Admin tools, and team switcher.
	/// </summary>
	private static void ApplyLocalRoleCache(string teamId, string role)
	{
		var normalized = string.IsNullOrWhiteSpace(role)
			? "member"
			: role.Trim().ToLowerInvariant();
		if (normalized is not ("admin" or "member"))
			normalized = "member";

		Preferences.Set($"{teamId}_role", normalized);
		Preferences.Set($"user_role_{teamId}", normalized);

		var currentId = Preferences.Get(TEAM_ID_KEY, string.Empty);
		if (string.Equals(currentId, teamId, StringComparison.Ordinal))
			Preferences.Set(USER_ROLE_KEY, normalized);
	}

	/// <summary>
	/// Pulls the signed-in user's role from Firestore and updates local Preferences.
	/// Returns the normalized role, or null if cloud could not be reached.
	/// </summary>
	private async Task<string?> RefreshMyRoleFromCloudAsync(string teamId)
	{
		if (string.IsNullOrWhiteSpace(teamId) || teamId.StartsWith("local_", StringComparison.Ordinal))
			return null;

		try
		{
			var cloud = ResolveCloudTeam();
			if (cloud is null) return null;

			var cloudRole = await cloud.GetMyRoleAsync(teamId);
			if (string.IsNullOrWhiteSpace(cloudRole))
			{
				System.Diagnostics.Debug.WriteLine(
					$"[TeamDetails] GetMyRole empty for team={teamId}");
				return null;
			}

			var normalized = cloudRole.Trim().ToLowerInvariant();
			ApplyLocalRoleCache(teamId, normalized);
			System.Diagnostics.Debug.WriteLine(
				$"[TeamDetails] Cloud role for {teamId}: {normalized}");
			return normalized;
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine(
				$"[TeamDetails] RefreshMyRoleFromCloud: {ex.Message}");
			return null;
		}
	}

	private void LoadCurrentTeam(bool refreshInviteFromCloud = false)
	{
		var teamMode = Preferences.Get(TEAM_MODE_KEY, string.Empty);
		var teamName = Preferences.Get(TEAM_NAME_KEY, string.Empty);
		var userRole = Preferences.Get(USER_ROLE_KEY, string.Empty);
		var savedDisplayName = UserDisplayName.Get();

		// Prefill join/create/rejoin fields when the user already has a name
		if (!string.IsNullOrEmpty(savedDisplayName))
		{
			if (string.IsNullOrWhiteSpace(JoinDisplayNameEntry.Text))
				JoinDisplayNameEntry.Text = savedDisplayName;
			if (string.IsNullOrWhiteSpace(CreateDisplayNameEntry.Text))
				CreateDisplayNameEntry.Text = savedDisplayName;
			if (string.IsNullOrWhiteSpace(AdminRejoinDisplayNameEntry.Text))
				AdminRejoinDisplayNameEntry.Text = savedDisplayName;
		}

		var isShared = teamMode == "shared";
		var isLocal = teamMode == "local";
		var hasTeam = !string.IsNullOrEmpty(teamMode) && !string.IsNullOrEmpty(Preferences.Get(TEAM_ID_KEY, string.Empty));

		if (!hasTeam)
		{
			CurrentTeamLabel.Text = "No team selected";
			TeamModeLabel.Text = "Mode: Not configured";
			DisplayNameSection.IsVisible = false;
			SharedAdminTools.IsVisible = false;
			LeaveTeamButton.IsVisible = false;
			DeleteLocalTeamButton.IsVisible = false;
			ShareTeamButton.IsVisible = false;
		}
		else
		{
			CurrentTeamLabel.Text = string.IsNullOrEmpty(teamName) ? "(unnamed team)" : teamName;
			TeamModeLabel.Text = $"Mode: {(isShared ? "Shared (Cloud)" : "Local (Device only)")}";
			ShareTeamButton.IsVisible = true;

			DisplayNameSection.IsVisible = isShared;
			if (isShared)
				CurrentDisplayNameEntry.Text = savedDisplayName;

			var isAdmin = isShared && userRole == "admin";
			SharedAdminTools.IsVisible = isAdmin;
			if (isAdmin)
				LoadInviteCode(refreshFromCloud: refreshInviteFromCloud);

			// Owner (club manager) can transfer ownership to another admin
			var teamId = Preferences.Get(TEAM_ID_KEY, string.Empty);
			var isOwner = isShared && Preferences.Get($"{teamId}_isOwner", false);
			TransferOwnershipButton.IsVisible = isOwner;

			LeaveTeamButton.IsVisible = isShared;
			DeleteLocalTeamButton.IsVisible = isLocal;
		}

		// Nav: "Team: {name}"
		DetailsPage.ApplyPageTeamTitle(this, "Team");
	}

	private async void OnSaveDisplayNameClicked(object sender, EventArgs e)
	{
		if (!UserDisplayName.TryValidate(CurrentDisplayNameEntry.Text, out var displayName, out var error))
		{
			await DisplayAlert("Display Name", error, "OK");
			return;
		}

		var teamMode = Preferences.Get(TEAM_MODE_KEY, string.Empty);
		var teamId = Preferences.Get(TEAM_ID_KEY, string.Empty);
		if (teamMode != "shared" || string.IsNullOrEmpty(teamId))
		{
			await DisplayAlert("No Shared Team", "Select a shared team first.", "OK");
			return;
		}

		SaveDisplayNameButton.IsEnabled = false;
		try
		{
			UserDisplayName.Set(displayName);
			CurrentDisplayNameEntry.Text = displayName;

			var cloudResult = await UpdateMemberDisplayNameInFirestore(teamId, displayName);
			if (cloudResult != "success")
			{
				// Local name still saved so chat can stamp messages; cloud sync can retry later
				await DisplayAlert("Saved Locally",
					$"Your name is set on this device, but the cloud update failed:\n{cloudResult}\n\nChat will still use this name on new messages.",
					"OK");
				return;
			}

			await DisplayAlert("Saved", "Your display name was updated for this team.", "OK");
		}
		catch (Exception ex)
		{
			await DisplayAlert("Error", $"Failed to save display name: {ex.Message}", "OK");
		}
		finally
		{
			SaveDisplayNameButton.IsEnabled = true;
		}
	}

	private bool _teamAdminExpanded;
	private bool _changeTeamExpanded;
	private bool _joinTeamExpanded;
	private bool _createTeamExpanded;
	private bool _recoverAdminExpanded;

	private void OnTeamAdminHeaderTapped(object sender, EventArgs e)
	{
		SetTeamAdminExpanded(!_teamAdminExpanded);
		// Mutual exclusive with Change Team
		if (_teamAdminExpanded)
			SetChangeTeamExpanded(false);
	}

	private void OnChangeTeamHeaderTapped(object sender, EventArgs e)
	{
		SetChangeTeamExpanded(!_changeTeamExpanded);
		// Mutual exclusive with Team Admin Panel
		if (_changeTeamExpanded)
			SetTeamAdminExpanded(false);
	}

	private void SetTeamAdminExpanded(bool expanded)
	{
		_teamAdminExpanded = expanded;
		TeamAdminContent.IsVisible = expanded;
		TeamAdminToggleIcon.Text = expanded ? "▲" : "▼";
		TeamAdminHint.IsVisible = !expanded;
	}

	private void SetChangeTeamExpanded(bool expanded)
	{
		_changeTeamExpanded = expanded;
		ChangeTeamContent.IsVisible = expanded;
		ChangeTeamToggleIcon.Text = expanded ? "▲" : "▼";
		ChangeTeamHint.IsVisible = !expanded;

		// Owner/role cloud fan-out is deferred until the user opens Change Team.
		if (expanded && SharedCheckbox.IsChecked)
			_ = RefreshSharedTeamCloudMetadataAsync();
	}

	/// <summary>Join expands above Create; opening Join closes Create (and vice versa).</summary>
	private void OnJoinTeamHeaderTapped(object sender, EventArgs e)
	{
		SetJoinTeamExpanded(!_joinTeamExpanded);
		if (_joinTeamExpanded)
			SetCreateTeamExpanded(false);
	}

	private void OnCreateTeamHeaderTapped(object sender, EventArgs e)
	{
		SetCreateTeamExpanded(!_createTeamExpanded);
		if (_createTeamExpanded)
			SetJoinTeamExpanded(false);
		if (_createTeamExpanded)
			UpdateCreateTeamSubSections();
	}

	private void OnSharedCheckboxChanged(object sender, CheckedChangedEventArgs e)
	{
		if (e.Value)
		{
			LocalCheckbox.IsChecked = false;
			SharedTeamSection.IsVisible = true;
			LocalTeamSection.IsVisible = false;
			RejoinAdminSection.IsVisible = true;
			ChangeTeamModeHint.IsVisible = false;
			AcquireTeamSection.IsVisible = true;
			JoinSubsection.IsVisible = true;
			LocalImportSubsection.IsVisible = false;
			CreateSubsectionTitle.Text = "Create new shared team";
			SetJoinTeamExpanded(false);
			SetCreateTeamExpanded(false);
			SetRecoverAdminExpanded(false);
			_ = LoadSharedTeamsAsync();
		}
		else if (!LocalCheckbox.IsChecked)
		{
			SharedTeamSection.IsVisible = false;
			RejoinAdminSection.IsVisible = false;
			ChangeTeamModeHint.IsVisible = true;
			AcquireTeamSection.IsVisible = false;
			LocalImportSubsection.IsVisible = false;
		}
		else
		{
			SharedTeamSection.IsVisible = false;
			RejoinAdminSection.IsVisible = false;
		}
		UpdateCreateTeamSubSections();
	}

	private void OnLocalCheckboxChanged(object sender, CheckedChangedEventArgs e)
	{
		if (e.Value)
		{
			SharedCheckbox.IsChecked = false;
			LocalTeamSection.IsVisible = true;
			SharedTeamSection.IsVisible = false;
			RejoinAdminSection.IsVisible = false;
			ChangeTeamModeHint.IsVisible = false;
			AcquireTeamSection.IsVisible = true;
			JoinSubsection.IsVisible = false; // invite join is shared-only
			LocalImportSubsection.IsVisible = true; // offline roster QR
			CreateSubsectionTitle.Text = "Create new local team";
			SetJoinTeamExpanded(false);
			SetCreateTeamExpanded(false);
			_ = LoadLocalTeamsAsync();
		}
		else if (!SharedCheckbox.IsChecked)
		{
			LocalTeamSection.IsVisible = false;
			LocalTeamsCollection.IsVisible = false;
			LocalTeamSwitcherLabel.IsVisible = false;
			ChangeTeamModeHint.IsVisible = true;
			AcquireTeamSection.IsVisible = false;
			LocalImportSubsection.IsVisible = false;
		}
		else
		{
			LocalTeamSection.IsVisible = false;
			LocalTeamsCollection.IsVisible = false;
			LocalTeamSwitcherLabel.IsVisible = false;
			LocalImportSubsection.IsVisible = false;
		}
		UpdateCreateTeamSubSections();
	}

	private void OnRecoverAdminHeaderTapped(object sender, EventArgs e)
	{
		SetRecoverAdminExpanded(!_recoverAdminExpanded);
	}

	private void SetJoinTeamExpanded(bool expanded)
	{
		_joinTeamExpanded = expanded;
		JoinTeamContent.IsVisible = expanded;
		JoinTeamToggleIcon.Text = expanded ? "▲" : "▼";
		JoinTeamHint.IsVisible = !expanded;
	}

	private void SetCreateTeamExpanded(bool expanded)
	{
		_createTeamExpanded = expanded;
		CreateTeamContent.IsVisible = expanded;
		CreateTeamToggleIcon.Text = expanded ? "▲" : "▼";
		CreateTeamHint.IsVisible = !expanded;
		if (expanded)
			UpdateCreateTeamSubSections();
	}

	private void SetRecoverAdminExpanded(bool expanded)
	{
		_recoverAdminExpanded = expanded;
		RecoverAdminContent.IsVisible = expanded;
		RecoverAdminToggleIcon.Text = expanded ? "▲" : "▼";
		RecoverAdminHint.IsVisible = !expanded;
	}

	private void UpdateCreateTeamSubSections()
	{
		if (!_createTeamExpanded) return;
		bool isShared = SharedCheckbox.IsChecked;
		bool isLocal  = LocalCheckbox.IsChecked;
		CreateSharedSection.IsVisible = isShared;
		CreateLocalSection.IsVisible  = isLocal;
	}

	private async void OnLeaveTeamButtonClicked(object sender, EventArgs e)
	{
		var teamId = Preferences.Get(TEAM_ID_KEY, string.Empty);
		var teamName = Preferences.Get(TEAM_NAME_KEY, "this team");
		var teamMode = Preferences.Get(TEAM_MODE_KEY, string.Empty);
		if (teamMode != "shared" || string.IsNullOrEmpty(teamId))
		{
			await DisplayAlert("No Shared Team", "Select a shared team first.", "OK");
			return;
		}

		var role = Preferences.Get($"{teamId}_role", Preferences.Get(USER_ROLE_KEY, "member"));
		var item = new SharedTeamItem
		{
			TeamId = teamId,
			TeamName = teamName,
			IsActive = true,
			Role = string.IsNullOrEmpty(role) ? "Member" : char.ToUpperInvariant(role[0]) + role[1..]
		};

		var confirm = await DisplayAlert(
			"Leave Team?",
			$"Are you sure you want to leave '{teamName}'?\n\n" +
			"This removes the team from this device. You will need an invite code (or admin recovery) to rejoin.\n\n" +
			"(You can also swipe left on the team under Change Team.)",
			"Leave",
			"Cancel");
		if (!confirm)
			return;

		await LeaveSharedTeamAsync(item);
	}

	private async void OnDeleteLocalTeamButtonClicked(object sender, EventArgs e)
	{
		var teamId = Preferences.Get(TEAM_ID_KEY, string.Empty);
		var teamName = Preferences.Get(TEAM_NAME_KEY, "this team");
		var teamMode = Preferences.Get(TEAM_MODE_KEY, string.Empty);
		if (teamMode != "local" || string.IsNullOrEmpty(teamId))
		{
			await DisplayAlert("No Local Team", "Select a local team first.", "OK");
			return;
		}

		var item = new LocalTeamItem
		{
			TeamId = teamId,
			TeamName = teamName,
			IsActive = true
		};

		var confirm = await DisplayAlert(
			"Delete Team?",
			$"Are you sure you want to delete '{teamName}'?\n\n" +
			"⚠️ This will permanently delete:\n" +
			"  • Team information\n" +
			"  • Roster data\n" +
			"  • Game logs\n" +
			"  • All associated settings\n\n" +
			"This action cannot be undone.\n\n" +
			"(You can also swipe left on the team under Change Team.)",
			"Delete",
			"Cancel");
		if (!confirm)
			return;

		await DeleteLocalTeam(item);
	}

	private async Task LoadLocalTeamsAsync()
	{
		try
		{
			System.Diagnostics.Debug.WriteLine("[TeamDetails] Loading local teams...");

			// Read from Preferences on a background thread to avoid blocking the UI thread.
			var newTeams = await Task.Run(() =>
			{
				var teamListJson = Preferences.Get("local_team_id_list", "[]");
				var teamIds = System.Text.Json.JsonSerializer.Deserialize<List<string>>(teamListJson) ?? [];
				var currentTeamId = Preferences.Get(TEAM_ID_KEY, string.Empty);
				var currentTeamMode = Preferences.Get(TEAM_MODE_KEY, string.Empty);

				var result = new List<LocalTeamItem>();
				foreach (var teamId in teamIds)
				{
					var teamName = Preferences.Get($"{teamId}_name", string.Empty);
					if (!string.IsNullOrEmpty(teamName))
					{
						result.Add(new LocalTeamItem
						{
							TeamId = teamId,
							TeamName = teamName,
							IsActive = currentTeamMode == "local" && currentTeamId == teamId
						});
					}
				}
				return result;
			}).ConfigureAwait(true); // resume on the UI thread for collection/UI updates

			// Batch all ObservableCollection changes — each Add fires a layout pass on Android.
			_localTeams.Clear();
			foreach (var team in newTeams)
				_localTeams.Add(team);

			System.Diagnostics.Debug.WriteLine($"[TeamDetails] Found {_localTeams.Count} local teams");

			var hasTeams = _localTeams.Count > 0;
			LocalTeamSwitcherLabel.Text = hasTeams ? $"Your Teams ({_localTeams.Count})" : string.Empty;
			LocalTeamsCollection.IsVisible = hasTeams;
			LocalTeamSwitcherLabel.IsVisible = hasTeams;
			TeamSeparator.IsVisible = hasTeams;
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"Error loading local teams: {ex.Message}");
		}
	}

	private async Task LoadSharedTeamsAsync()
	{
		try
		{
			System.Diagnostics.Debug.WriteLine("[TeamDetails] Loading shared teams...");

			// Read from Preferences on a background thread to avoid blocking the UI thread.
			var newTeams = await Task.Run(() =>
			{
				var teamListJson = Preferences.Get("team_id_list", "[]");
				var teamIds = System.Text.Json.JsonSerializer.Deserialize<List<string>>(teamListJson) ?? [];
				var currentTeamId = Preferences.Get(TEAM_ID_KEY, string.Empty);
				var currentTeamMode = Preferences.Get(TEAM_MODE_KEY, string.Empty);

				var result = new List<SharedTeamItem>();
				foreach (var teamId in teamIds)
				{
					var teamName = Preferences.Get($"{teamId}_name", string.Empty);
					if (!string.IsNullOrEmpty(teamName))
					{
						var isActive = currentTeamMode == "shared" && currentTeamId == teamId;
						var role = Preferences.Get($"{teamId}_role", "member");
						if (string.IsNullOrEmpty(role)) role = "member";
						var isOwner = Preferences.Get($"{teamId}_isOwner", false);
						result.Add(new SharedTeamItem
						{
							TeamId = teamId,
							TeamName = teamName,
							IsActive = isActive,
							Role = char.ToUpperInvariant(role[0]) + role[1..],
							IsOwner = isOwner
						});
					}
				}
				return result;
			}).ConfigureAwait(true); // resume on the UI thread for collection/UI updates

			_sharedTeams.Clear();
			foreach (var team in newTeams)
				_sharedTeams.Add(team);

			// Defer per-team cloud owner/role probes until Change Team is expanded —
			// OnAppearing used to fire this every visit and jam the UI / auth gate.
			if (_changeTeamExpanded)
				_ = RefreshSharedTeamCloudMetadataAsync();

			System.Diagnostics.Debug.WriteLine($"[TeamDetails] Found {_sharedTeams.Count} shared teams");

			var hasTeams = _sharedTeams.Count > 0;
			SharedTeamSwitcherLabel.Text = hasTeams ? $"Your Shared Teams ({_sharedTeams.Count})" : string.Empty;
			SharedTeamsCollection.IsVisible = hasTeams;
			SharedTeamSwitcherLabel.IsVisible = hasTeams;
			SharedTeamSeparator.IsVisible = hasTeams;
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"Error loading shared teams: {ex.Message}");
		}
	}

	private async void OnSharedTeamSelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (e.CurrentSelection.FirstOrDefault() is not SharedTeamItem selectedTeam)
			return;

		var currentTeamId = Preferences.Get(TEAM_ID_KEY, string.Empty);

		if (currentTeamId == selectedTeam.TeamId)
		{
			// Same team re-selected: still refresh cloud role (e.g. just promoted).
			SharedTeamsCollection.SelectedItem = null;
			var refreshed = await RefreshMyRoleFromCloudAsync(selectedTeam.TeamId);
			LoadCurrentTeam();
			if (!string.IsNullOrEmpty(refreshed))
			{
				selectedTeam.Role = char.ToUpperInvariant(refreshed[0]) + refreshed[1..];
				FindGamePage()?.ResetMatchState();
			}
			return;
		}

		System.Diagnostics.Debug.WriteLine($"[TeamDetails] Switching to shared team: {selectedTeam.TeamName}");
		SharedTeamsCollection.SelectedItem = null;

		// Prefer cloud role over stale local cache (join stored "member"; promote updates cloud only).
		var role = await RefreshMyRoleFromCloudAsync(selectedTeam.TeamId)
		           ?? Preferences.Get($"{selectedTeam.TeamId}_role", "member");
		if (string.IsNullOrWhiteSpace(role)) role = "member";
		role = role.Trim().ToLowerInvariant();

		FindGamePage()?.ResetMatchState();

		Preferences.Set(TEAM_MODE_KEY, "shared");
		Preferences.Set(TEAM_ID_KEY, selectedTeam.TeamId);
		Preferences.Set(TEAM_NAME_KEY, selectedTeam.TeamName);
		ApplyLocalRoleCache(selectedTeam.TeamId, role);

		SyncTeamIdToLocalStorage(selectedTeam.TeamId);
		RefreshAppShellMenu();

		var roleLabel = char.ToUpperInvariant(role[0]) + role[1..];
		selectedTeam.Role = roleLabel;

		await DisplayAlert("Team Switched",
			$"Now managing: {selectedTeam.TeamName}\n\n" +
			$"Role: {roleLabel}",
			"OK");

		LoadCurrentTeam();
		_ = LoadSharedTeamsAsync();
	}

	private void OnSharedTeamItemTapped(object sender, EventArgs e)
	{
		if (sender is Frame frame && frame.BindingContext is SharedTeamItem tappedTeam)
			SharedTeamsCollection.SelectedItem = tappedTeam;
	}

	private void OnLocalTeamSelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		System.Diagnostics.Debug.WriteLine($"[TeamDetails] SelectionChanged fired. Selection count: {e.CurrentSelection.Count}");

		if (e.CurrentSelection.FirstOrDefault() is not LocalTeamItem selectedTeam)
		{
			System.Diagnostics.Debug.WriteLine($"[TeamDetails] No team selected (CurrentSelection is null or not LocalTeamItem)");
			return;
		}

		System.Diagnostics.Debug.WriteLine($"[TeamDetails] Team selected: {selectedTeam.TeamName} (ID: {selectedTeam.TeamId})");

		var currentTeamId = Preferences.Get(TEAM_ID_KEY, string.Empty);
		System.Diagnostics.Debug.WriteLine($"[TeamDetails] Current team ID: {currentTeamId}");

		if (currentTeamId == selectedTeam.TeamId)
		{
			System.Diagnostics.Debug.WriteLine($"[TeamDetails] Already on this team, ignoring selection");
			LocalTeamsCollection.SelectedItem = null;
			return;
		}

		// Clear selection immediately to prevent double-tap issues on Android
		LocalTeamsCollection.SelectedItem = null;

		MainThread.BeginInvokeOnMainThread(async () =>
		{
			if (!await ConfirmTeamChangeAsync())
			{
				System.Diagnostics.Debug.WriteLine($"[TeamDetails] Team switch cancelled by user");
				return;
			}

			System.Diagnostics.Debug.WriteLine($"[TeamDetails] Switching to team: {selectedTeam.TeamName}");

			// Reset match state on the live GamePage ViewModel before updating Preferences
			// so timers stop immediately and don't bleed into the new team's session.
			FindGamePage()?.ResetMatchState();

			Preferences.Set(TEAM_MODE_KEY, "local");
			Preferences.Set(TEAM_ID_KEY, selectedTeam.TeamId);
			Preferences.Set(TEAM_NAME_KEY, selectedTeam.TeamName);
			Preferences.Set(USER_ROLE_KEY, "admin"); // Local mode = always admin

			SyncTeamIdToLocalStorage(selectedTeam.TeamId);
			RefreshAppShellMenu();

			await DisplayAlert("Team Switched",
				$"Now managing: {selectedTeam.TeamName}\n\n" +
				"Your roster, chat, and logs are now for this team.",
				"OK");

			System.Diagnostics.Debug.WriteLine($"[TeamDetails] Dialog closed, refreshing UI");

			LoadCurrentTeam();
			_ = LoadLocalTeamsAsync();

			var gamePage = FindGamePage();
			if (gamePage != null)
			{
				System.Diagnostics.Debug.WriteLine($"[TeamDetails] Forcing Game page reload after team switch");
				var currentPage = Application.Current?.MainPage?.Navigation?.NavigationStack?.LastOrDefault();
				if (currentPage is GamePage)
				{
					await Shell.Current.GoToAsync("//SettingsPage");
					await Task.Delay(100);
					await Shell.Current.GoToAsync("//GamePage");
				}
			}

				System.Diagnostics.Debug.WriteLine($"[TeamDetails] UI refresh complete");
				});
			}

			// Alternative handler for TapGestureRecognizer (Android fallback)
	private void OnTeamItemTapped(object sender, EventArgs e)
	{
		System.Diagnostics.Debug.WriteLine($"[TeamDetails] TapGesture fired on Frame");

		if (sender is Frame frame && frame.BindingContext is LocalTeamItem tappedTeam)
		{
			System.Diagnostics.Debug.WriteLine($"[TeamDetails] Tapped team: {tappedTeam.TeamName}");

			// Manually trigger selection
			LocalTeamsCollection.SelectedItem = tappedTeam;
		}
		else
		{
			System.Diagnostics.Debug.WriteLine($"[TeamDetails] TapGesture - BindingContext is not LocalTeamItem");
		}
	}

	// Sync team ID to localStorage and trigger JavaScript roster reload
	// Find the GamePage regardless of platform navigation model
	private static GamePage? FindGamePage()
	{
		// NavigationStack works on Android / iOS modal navigation
		var fromStack = Application.Current?.MainPage?.Navigation?.NavigationStack
			?.OfType<GamePage>().FirstOrDefault();
		if (fromStack != null) return fromStack;

		// Shell item traversal — required on Windows where tab pages are not in NavigationStack
		if (Shell.Current is { } shell)
		{
			foreach (var item in shell.Items)
				foreach (var section in item.Items)
					foreach (var content in section.Items)
						if (content.Content is GamePage gp)
							return gp;
		}

		return null;
	}

	private const string SuppressTeamSwitchWarningKey = "suppress_team_switch_warning";

	/// <summary>
	/// Shows a confirmation dialog before switching or creating a team, warning the user
	/// that scores and timers will be reset. Respects a "don't show this again" preference.
	/// Returns <c>true</c> if the user confirmed (or the warning is suppressed), <c>false</c> to cancel.
	/// </summary>
	private async Task<bool> ConfirmTeamChangeAsync()
	{
		if (Preferences.Get(SuppressTeamSwitchWarningKey, false))
			return true;

		// Custom dialog with three logical options: Cancel | Don't show again | Continue
		bool confirmed = false;
		bool dontShowAgain = false;

		var tcs = new TaskCompletionSource<bool>();

		var dontShowCheckbox = new CheckBox { Color = Colors.DodgerBlue, VerticalOptions = LayoutOptions.Center };
		var dontShowLabel    = new Label
		{
			Text              = "Don't show this again",
			VerticalOptions   = LayoutOptions.Center,
			TextColor         = Application.Current?.RequestedTheme == AppTheme.Dark ? Colors.White : Colors.Black,
			FontSize          = 14
		};
		var dontShowRow = new HorizontalStackLayout
		{
			Spacing  = 6,
			Children = { dontShowCheckbox, dontShowLabel }
		};

		var continueBtn = new Button
		{
			Text            = "Continue",
			BackgroundColor = Colors.DodgerBlue,
			TextColor       = Colors.White,
			CornerRadius    = 8,
			HeightRequest   = 44
		};
		var cancelBtn = new Button
		{
			Text            = "Cancel",
			BackgroundColor = Colors.Gray,
			TextColor       = Colors.White,
			CornerRadius    = 8,
			HeightRequest   = 44
		};

		continueBtn.Clicked += async (_, _) =>
		{
			dontShowAgain = dontShowCheckbox.IsChecked;
			await Navigation.PopModalAsync(animated: false);
			confirmed = true;
			tcs.TrySetResult(true);
		};
		cancelBtn.Clicked += async (_, _) =>
		{
			await Navigation.PopModalAsync(animated: false);
			tcs.TrySetResult(false);
		};

		var popup = new ContentPage
		{
			BackgroundColor = Colors.Transparent,
			Content = new Frame
			{
				BackgroundColor = Application.Current?.RequestedTheme == AppTheme.Dark
					? Color.FromArgb("#2C2C2E")
					: Colors.White,
				CornerRadius    = 14,
				Padding         = new Thickness(24),
				VerticalOptions = LayoutOptions.Center,
				Margin          = new Thickness(28, 0),
				Content = new VerticalStackLayout
				{
					Spacing  = 16,
					Children =
					{
						new Label
						{
							Text       = "⚠️ Switch Team?",
							FontSize   = 18,
							FontAttributes = FontAttributes.Bold,
							HorizontalOptions = LayoutOptions.Center
						},
						new Label
						{
							Text       = "Switching teams will reset all current scores, timers, and counters.\n\nAny unsaved match data will be lost.",
							FontSize   = 14,
							HorizontalTextAlignment = TextAlignment.Center
						},
						dontShowRow,
						new Grid
						{
							ColumnDefinitions = new ColumnDefinitionCollection
							{
								new ColumnDefinition { Width = GridLength.Star },
								new ColumnDefinition { Width = GridLength.Star }
							},
							Children = { cancelBtn, continueBtn }
						}
					}
				}
			}
		};

		Grid.SetColumn(cancelBtn,   0);
		Grid.SetColumn(continueBtn, 1);

		await Navigation.PushModalAsync(popup, animated: false);
		await tcs.Task;

		if (dontShowAgain)
			Preferences.Set(SuppressTeamSwitchWarningKey, true);

		return confirmed;
	}

	private void SyncTeamIdToLocalStorage(string teamId)
	{
		// GamePage is now native MVVM; team ID is read from Preferences on OnAppearing.
		System.Diagnostics.Debug.WriteLine($"[TeamDetails] Team '{teamId}' set in Preferences - GamePage will reload on next tab switch.");
	}

	private async Task<string?> DownloadRosterFromFirestore(string teamId)
	{
		return await DownloadRosterFromFirestoreStatic(teamId);
	}

	// Static wrapper — uses ICloudRosterService (Plugin.Firebase), not REST.
	public static async Task<string?> DownloadRosterFromFirestoreStatic(string teamId)
	{
		try
		{
			var services = Application.Current?.Handler?.MauiContext?.Services;
			var rosterSvc = services?.GetService<Services.ICloudRosterService>();
			if (rosterSvc is null)
			{
				System.Diagnostics.Debug.WriteLine("[TeamDetails] ICloudRosterService unavailable");
				return null;
			}

			var snap = await rosterSvc.LoadAsync(teamId);
			if (snap is null) return null;
			return System.Text.Json.JsonSerializer.Serialize(snap);
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"[TeamDetails] DownloadRoster SDK: {ex.Message}");
			return null;
		}
	}

	// Delete team handler (invoked by swipe gesture)
	private async void OnDeleteTeamSwipe(object sender, EventArgs e)
	{
		if (sender is SwipeItem swipeItem && swipeItem.CommandParameter is LocalTeamItem teamToDelete)
		{
			System.Diagnostics.Debug.WriteLine($"[TeamDetails] Delete swipe triggered for: {teamToDelete.TeamName}");

			// Confirm deletion with user
			var confirm = await DisplayAlert(
				"Delete Team?", 
				$"Are you sure you want to delete '{teamToDelete.TeamName}'?\n\n" +
				"⚠️ This will permanently delete:\n" +
				"  • Team information\n" +
				"  • Roster data\n" +
				"  • Game logs\n" +
				"  • All associated settings\n\n" +
				"This action cannot be undone.",
				"Delete", 
				"Cancel");

			if (confirm)
			{
				await DeleteLocalTeam(teamToDelete);
			}
			else
			{
				System.Diagnostics.Debug.WriteLine($"[TeamDetails] Delete cancelled by user");
			}
		}
	}

	// Rename team handler (invoked by swipe gesture)
	private async void OnRenameTeamSwipe(object sender, EventArgs e)
	{
		if (sender is SwipeItem swipeItem && swipeItem.CommandParameter is LocalTeamItem teamToRename)
		{
			System.Diagnostics.Debug.WriteLine($"[TeamDetails] Rename swipe triggered for: {teamToRename.TeamName}");

			// Prompt user for new name
			string newName = await DisplayPromptAsync(
				"Rename Team", 
				$"Enter new name for '{teamToRename.TeamName}':",
				initialValue: teamToRename.TeamName,
				maxLength: 50,
				keyboard: Keyboard.Text);

			if (!string.IsNullOrWhiteSpace(newName) && newName.Trim() != teamToRename.TeamName)
			{
				await RenameLocalTeam(teamToRename, newName.Trim());
			}
			else
			{
				System.Diagnostics.Debug.WriteLine($"[TeamDetails] Rename cancelled or unchanged");
			}
		}
	}

	private async Task RenameLocalTeam(LocalTeamItem team, string newName)
	{
		try
		{
			System.Diagnostics.Debug.WriteLine($"[TeamDetails] Renaming team: {team.TeamName} → {newName}");

			// Update team name in Preferences
			Preferences.Set($"{team.TeamId}_name", newName);

			// If this is the current team, update current team name too
			var currentTeamId = Preferences.Get(TEAM_ID_KEY, string.Empty);
			if (currentTeamId == team.TeamId)
			{
				Preferences.Set(TEAM_NAME_KEY, newName);
				LoadCurrentTeam();  // Refresh current team display
			}

			// Refresh team list to show new name
			_ = LoadLocalTeamsAsync();

			System.Diagnostics.Debug.WriteLine($"[TeamDetails] ✅ Team renamed successfully");

			await DisplayAlert("Team Renamed", 
				$"'{team.TeamName}' has been renamed to '{newName}'.\n\n" +
				"All data (roster, logs, chat history) remains intact.", 
				"OK");
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"[TeamDetails] ❌ Rename error: {ex.Message}");
			await DisplayAlert("Error", $"Failed to rename team: {ex.Message}", "OK");
		}
	}

	private async Task DeleteLocalTeam(LocalTeamItem team)
	{
		try
		{
			System.Diagnostics.Debug.WriteLine($"[TeamDetails] Deleting team: {team.TeamName} (ID: {team.TeamId})");

			// Check if trying to delete the currently active team
			var currentTeamId = Preferences.Get(TEAM_ID_KEY, string.Empty);
			var isCurrentTeam = currentTeamId == team.TeamId;

			// Remove from team ID list
			var teamListJson = Preferences.Get("local_team_id_list", "[]");
			var teamIds = System.Text.Json.JsonSerializer.Deserialize<List<string>>(teamListJson) ?? new List<string>();

			if (teamIds.Remove(team.TeamId))
			{
				var updatedJson = System.Text.Json.JsonSerializer.Serialize(teamIds);
				Preferences.Set("local_team_id_list", updatedJson);
			}

			// Delete ALL data associated with this team
			// This prevents orphaned data from accumulating on the device
			DeleteAllTeamData(team.TeamId);

			System.Diagnostics.Debug.WriteLine($"[TeamDetails] ✅ Team and all associated data deleted");

			// If deleted the current team, clear current team selection
			if (isCurrentTeam)
			{
				Preferences.Remove(TEAM_MODE_KEY);
				Preferences.Remove(TEAM_ID_KEY);
				Preferences.Remove(TEAM_NAME_KEY);
				Preferences.Remove(USER_ROLE_KEY);

				await DisplayAlert("Team Deleted", 
					$"'{team.TeamName}' and all associated data have been permanently deleted.\n\n" +
					"You are no longer on any team. Select or create a team to continue.", 
					"OK");

				LoadCurrentTeam();
			}
			else
			{
				await DisplayAlert("Team Deleted", 
					$"'{team.TeamName}' and all associated data have been permanently deleted.", 
					"OK");
			}

			// Refresh team list
			_ = LoadLocalTeamsAsync();
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"[TeamDetails] ❌ Delete team error: {ex.Message}");
			await DisplayAlert("Error", $"Failed to delete team: {ex.Message}", "OK");
		}
	}

	private void DeleteAllTeamData(string teamId)
	{
		try
		{
			System.Diagnostics.Debug.WriteLine($"[TeamDetails] Cleaning up all data for team ID: {teamId}");

			// List of known data keys that use team ID as prefix
			var dataKeysToDelete = new[]
			{
				$"{teamId}_name",           // Team metadata
				$"{teamId}_roster",         // Roster data (if stored per-team)
				$"{teamId}_logs",           // Game logs (if stored per-team)
				$"{teamId}_invite_code",    // Invite code (for shared teams)
				$"{teamId}_role",           // Local role cache for shared teams
				$"{teamId}_isOwner",        // Club manager / creator flag
				$"{teamId}_settings",       // Team-specific settings
				$"{teamId}_history",        // Match history
				$"{teamId}_stats",          // Team statistics
				$"team_mode_{teamId}",
				$"user_role_{teamId}",
			};

			// Remove all known team-associated data
			foreach (var key in dataKeysToDelete)
			{
				if (Preferences.ContainsKey(key))
				{
					Preferences.Remove(key);
					System.Diagnostics.Debug.WriteLine($"[TeamDetails] Deleted: {key}");
				}
			}

			// Note: If roster/logs are stored globally (not per-team), they won't be affected
			// This only cleans up team-specific data that uses the team ID as a key prefix
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"[TeamDetails] Warning: Error cleaning team data: {ex.Message}");
			// Don't throw - deletion should continue even if cleanup partially fails
		}
	}

	private void OnClubTeamChanged(object sender, TextChangedEventArgs e)
	{
		// If either Club or Team has text, disable Nickname
		if (!string.IsNullOrWhiteSpace(ClubEntry.Text) || !string.IsNullOrWhiteSpace(TeamEntry.Text))
		{
			NicknameEntry.IsEnabled = false;
			NicknameEntry.Text = string.Empty;
		}
		else
		{
			NicknameEntry.IsEnabled = true;
		}
	}

	private void OnNicknameChanged(object sender, TextChangedEventArgs e)
	{
		// If Nickname has text, disable Club and Team
		if (!string.IsNullOrWhiteSpace(NicknameEntry.Text))
		{
			ClubEntry.IsEnabled = false;
			TeamEntry.IsEnabled = false;
			ClubEntry.Text = string.Empty;
			TeamEntry.Text = string.Empty;
		}
		else
		{
			ClubEntry.IsEnabled = true;
			TeamEntry.IsEnabled = true;
		}
	}

	private async void OnCreateTeamClicked(object sender, EventArgs e)
	{
		string teamName;
		string teamId;

		// Validate input before showing the spinner
		if (!string.IsNullOrWhiteSpace(NicknameEntry.Text))
		{
			teamName = NicknameEntry.Text.Trim();
			teamId = GenerateTeamId(teamName);
		}
		else if (!string.IsNullOrWhiteSpace(ClubEntry.Text) && !string.IsNullOrWhiteSpace(TeamEntry.Text))
		{
			var club = ClubEntry.Text.Trim();
			var team = TeamEntry.Text.Trim();
			// Single display string: Team first so truncation still shows which side (e.g. "U17 Boys - Manchester…")
			teamName = $"{team} - {club}";
			teamId = GenerateTeamId(team, club);
		}
		else
		{
			await DisplayAlert("Invalid Input", "Please enter either Team + Club or a Nickname.", "OK");
			return;
		}

		if (!UserDisplayName.TryValidate(CreateDisplayNameEntry.Text, out var displayName, out var nameError))
		{
			await DisplayAlert("Display Name Required", nameError, "OK");
			return;
		}

		// Show loading state
		CreateSharedTeamButton.IsEnabled = false;
		CreateTeamLoadingSection.IsVisible = true;
		CreateTeamSpinner.IsRunning = true;

		try
		{
			var inviteCode = GenerateInviteCode();
			var adminCode = GenerateAdminCode();
			var adminCodeHash = HashAdminCode(adminCode);
			var creatorEmail = CreatorEmailEntry.Text?.Trim() ?? string.Empty;
			var result = await CreateTeamInFirestore(teamId, teamName, inviteCode, adminCodeHash, creatorEmail, displayName);

				if (result == "success")
				{
					// Save locally as well for Phase 1 compatibility
					Preferences.Set($"{teamId}_invite_code", inviteCode);
					Preferences.Set($"{teamId}_name", teamName);
					// Creator is the team owner (club manager) — only they may hard-delete the cloud team.
					Preferences.Set($"{teamId}_isOwner", true);
					RegisterTeamId(teamId);
					UserDisplayName.Set(displayName);

					// Set as current team
						Preferences.Set(TEAM_MODE_KEY, "shared");
						Preferences.Set(TEAM_ID_KEY, teamId);
						Preferences.Set(TEAM_NAME_KEY, teamName);
						Preferences.Set(USER_ROLE_KEY, "admin");
						Preferences.Set($"{teamId}_role", "admin");

						// ALSO store per-team keys for GamePage polling
						Preferences.Set($"team_mode_{teamId}", "shared");
						Preferences.Set($"user_role_{teamId}", "admin");

					var emailNote = !string.IsNullOrWhiteSpace(creatorEmail)
						? $"\n\nA recovery reminder has been sent to:\n{creatorEmail}"
						: "\n\n⚠️ No email provided — save this code now, it will NOT be shown again.";

						await DisplayAlert("Team Created!",
							$"Team: {teamName}\n\n" +
							$"Team ID: {teamId}\n\n" +
							$"Your chat name: {displayName}\n\n" +
							$"Invite Code (members): {inviteCode}\n\n" +
							$"⚠️ OWNER RECOVERY CODE:\n{adminCode}\n\n" +
							"Save this Owner recovery code in a secure location outside this device (e.g. a password manager). " +
							"You will need it to regain admin access if you reinstall the app or change devices.\n\n" +
							"Next: Open the Game screen to name players, assign positions (field = swipe left, bench = swipe right, goalie = swipe left twice), and set timers." +
							emailNote,
							"OK");

					RefreshAppShellMenu();
					LoadCurrentTeam();

					// Persist FCM token on the new admin member doc so others can notify this device.
					_ = FcmService.Instance.EnsureRegisteredForCurrentTeamAsync();

					// Clear inputs
					ClubEntry.Text = string.Empty;
					TeamEntry.Text = string.Empty;
					NicknameEntry.Text = string.Empty;
					CreatorEmailEntry.Text = string.Empty;
					// Keep CreateDisplayNameEntry — useful if they create another team
			}
			else
			{
				await DisplayAlert("Error", $"Failed to create team in cloud: {result}", "OK");
			}
		}
		catch (Exception ex)
		{
			await DisplayAlert("Error", $"Failed to create team: {ex.Message}", "OK");
		}
		finally
		{
			// Always restore UI regardless of success or failure
			CreateTeamSpinner.IsRunning = false;
			CreateTeamLoadingSection.IsVisible = false;
			CreateSharedTeamButton.IsEnabled = true;
		}
	}

private static string? _firebaseUserId;

private static Services.ICloudTeamService? ResolveCloudTeam()
{
	try
	{
		return Application.Current?.Handler?.MauiContext?.Services.GetService<Services.ICloudTeamService>();
	}
	catch { return null; }
}

private static Services.IFirebaseAuthService? ResolveAuth()
{
	try
	{
		return Application.Current?.Handler?.MauiContext?.Services.GetService<Services.IFirebaseAuthService>();
	}
	catch { return null; }
}

private async Task<bool> EnsureFirebaseAuthAsync()
{
	if (Connectivity.NetworkAccess != NetworkAccess.Internet)
	{
		System.Diagnostics.Debug.WriteLine("[Firebase] No internet connection - skipping auth");
		return false;
	}

	var auth = ResolveAuth();
	if (auth is null)
	{
		System.Diagnostics.Debug.WriteLine("[Firebase] IFirebaseAuthService not available");
		return false;
	}

	var uid = await auth.EnsureSignedInAsync();
	if (string.IsNullOrEmpty(uid))
		return false;

	_firebaseUserId = uid;
	return true;
}

private static async Task<bool> EnsureFirebaseAuthStaticAsync()
{
	var auth = ResolveAuth();
	if (auth is null) return false;
	var uid = await auth.EnsureSignedInAsync();
	if (string.IsNullOrEmpty(uid)) return false;
	_firebaseUserId = uid;
	return true;
}

private async Task<string> CreateTeamInFirestore(string teamId, string teamName, string inviteCode, string adminCodeHash, string creatorEmail, string displayName)
{
	System.Diagnostics.Debug.WriteLine($"[TeamDetails] CreateTeamInFirestore (SDK) for team: {teamName}");
	var cloud = ResolveCloudTeam();
	if (cloud is null)
		return "error: Cloud team service not available";
	return await cloud.CreateTeamAsync(teamId, teamName, inviteCode, adminCodeHash, creatorEmail, displayName);
}
private void RegisterTeamId(string teamId)
	{
		var teamListJson = Preferences.Get("team_id_list", "[]");

		try
		{
			var teamIds = System.Text.Json.JsonSerializer.Deserialize<List<string>>(teamListJson) ?? new List<string>();

			if (!teamIds.Contains(teamId))
			{
				teamIds.Add(teamId);
				var updatedJson = System.Text.Json.JsonSerializer.Serialize(teamIds);
				Preferences.Set("team_id_list", updatedJson);
			}
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"Error registering team ID: {ex.Message}");
		}
	}

	private async void OnJoinTeamClicked(object sender, EventArgs e)
	{
		try
		{
			var inviteCode = InviteCodeEntry.Text?.Trim().ToUpperInvariant();

			if (string.IsNullOrWhiteSpace(inviteCode))
			{
				await DisplayAlert("Invalid Code", "Please enter an invite code.", "OK");
				return;
			}

			if (!UserDisplayName.TryValidate(JoinDisplayNameEntry.Text, out var displayName, out var nameError))
			{
				await DisplayAlert("Display Name Required", nameError, "OK");
				return;
			}

			// Try to join team via Firestore first
			var result = await JoinTeamInFirestore(inviteCode, displayName);

			if (result.StartsWith("success:"))
			{
				// Parse result: "success:teamId:teamName" — limit to 3 parts so colons in team name are preserved
				var parts = result.Split(':', 3);
				if (parts.Length >= 3)
				{
					var teamId = parts[1];
					var teamName = parts[2];

					// Save locally
					Preferences.Set(TEAM_MODE_KEY, "shared");
					Preferences.Set(TEAM_ID_KEY, teamId);
					Preferences.Set(TEAM_NAME_KEY, teamName);
					Preferences.Set(USER_ROLE_KEY, "member");
					Preferences.Set($"{teamId}_role", "member");
					Preferences.Set($"{teamId}_name", teamName);
					Preferences.Set($"{teamId}_isOwner", false);
					Preferences.Set($"{teamId}_invite_code", QrCodeService.NormalizeInviteCode(inviteCode));
					UserDisplayName.Set(displayName);

					// ALSO store per-team keys for GamePage polling
					Preferences.Set($"team_mode_{teamId}", "shared");
					Preferences.Set($"user_role_{teamId}", "member");

					RegisterTeamId(teamId);

					await DisplayAlert("Joined Team!", 
							$"Successfully joined: {teamName}\n\n" +
							$"Role: Member\n" +
							$"Chat name: {displayName}\n\n" +
							"You can now collaborate with your team.", 
							"OK");

						SyncTeamIdToLocalStorage(teamId);
						RefreshAppShellMenu();
						LoadCurrentTeam();
						// Save this device's FCM token so chat pushes can reach it.
						_ = FcmService.Instance.EnsureRegisteredForCurrentTeamAsync();
						InviteCodeEntry.Text = string.Empty;
						return;
				}
			}
			else if (result.StartsWith("already_member:"))
			{
				var parts = result.Split(':', 3);
				if (parts.Length >= 3)
				{
					// Common after Debug reinstall: Preferences wiped, but Firebase Auth UID
					// still matches teams/{id}/members/{uid}. Re-bind local team selection.
					var existingTeamId = parts[1];
					var teamName = parts[2];
					await RestoreExistingSharedMembershipAsync(
						existingTeamId, teamName, displayName, inviteCode);

					var role = Preferences.Get(USER_ROLE_KEY, "member");
					var roleLabel = string.IsNullOrEmpty(role)
						? "Member"
						: char.ToUpperInvariant(role[0]) + role[1..];
					var ownerNote = Preferences.Get($"{existingTeamId}_isOwner", false)
						? "\nOwner: Yes (club manager)"
						: string.Empty;

					await DisplayAlert(
						"Team Restored",
						$"You were already on '{teamName}' in the cloud.\n\n" +
						$"This device's local team list was rebuilt.\n" +
						$"Role: {roleLabel}{ownerNote}\n" +
						$"Chat name: {displayName}",
						"OK");
					InviteCodeEntry.Text = string.Empty;
					return;
				}
			}

			// If Firebase fails, fall back to local search (Phase 1 compatibility)
			var foundTeam = FindTeamByInviteCode(inviteCode);

			if (foundTeam != null)
			{
				var currentTeamId = Preferences.Get(TEAM_ID_KEY, string.Empty);
				if (currentTeamId == foundTeam.TeamId)
				{
					await DisplayAlert("Already a Member", 
						$"You are already a member of '{foundTeam.TeamName}'.", 
						"OK");
				}
				else
				{
					Preferences.Set(TEAM_MODE_KEY, "shared");
					Preferences.Set(TEAM_ID_KEY, foundTeam.TeamId);
					Preferences.Set(TEAM_NAME_KEY, foundTeam.TeamName);
					Preferences.Set(USER_ROLE_KEY, "member");
					Preferences.Set($"{foundTeam.TeamId}_role", "member");

					// ALSO store per-team keys for GamePage polling
					Preferences.Set($"team_mode_{foundTeam.TeamId}", "shared");
					Preferences.Set($"user_role_{foundTeam.TeamId}", "member");

					RegisterTeamId(foundTeam.TeamId);

					await DisplayAlert("Joined Team!", 
						$"Successfully joined: {foundTeam.TeamName}\n\n" +
						$"Role: Member\n\n" +
						"(Local team - no cloud sync)", 
						"OK");

					LoadCurrentTeam();
				}
			}
			else
			{
				await DisplayAlert("Invalid Code", 
					$"Invite code '{inviteCode}' not found.\n\n" +
					result, 
					"OK");
			}

				InviteCodeEntry.Text = string.Empty;
				}
				catch (Exception ex)
				{
					await DisplayAlert("Error", $"Failed to join team: {ex.Message}", "OK");
				}
			}

			private async void OnRejoinAsAdminClicked(object sender, EventArgs e)
			{
				try
				{
					var teamId = AdminRejoinTeamIdEntry.Text?.Trim();
					var adminCode = AdminRejoinCodeEntry.Text?.Trim();

					if (string.IsNullOrWhiteSpace(teamId) || string.IsNullOrWhiteSpace(adminCode))
					{
						await DisplayAlert("Missing Info", "Please enter both the Team ID and your Owner Recovery Code.", "OK");
						return;
					}

					if (!UserDisplayName.TryValidate(AdminRejoinDisplayNameEntry.Text, out var displayName, out var nameError))
					{
						await DisplayAlert("Display Name Required", nameError, "OK");
						return;
					}

					RejoinAsAdminButton.IsEnabled = false;

					var result = await RejoinAsAdminInFirestore(teamId, adminCode, displayName);

					if (result.StartsWith("success:"))
					{
						var parts = result.Split(':', 3);
						var restoredTeamId = parts[1];
						var restoredTeamName = parts.Length >= 3 ? parts[2] : restoredTeamId;

						Preferences.Set(TEAM_MODE_KEY, "shared");
						Preferences.Set(TEAM_ID_KEY, restoredTeamId);
						Preferences.Set(TEAM_NAME_KEY, restoredTeamName);
						Preferences.Set(USER_ROLE_KEY, "admin");
						Preferences.Set($"{restoredTeamId}_role", "admin");
						Preferences.Set($"team_mode_{restoredTeamId}", "shared");
						Preferences.Set($"user_role_{restoredTeamId}", "admin");
						Preferences.Set($"{restoredTeamId}_name", restoredTeamName);
						// Recovery code reclaims Owner (createdBy) for this Firebase UID.
						var isOwner = true;
						try
						{
							var c = ResolveCloudTeam();
							if (c is not null)
								isOwner = await c.IsTeamOwnerAsync(restoredTeamId);
						}
						catch { /* non-fatal — still treat as owner after successful reclaim */ }
						Preferences.Set($"{restoredTeamId}_isOwner", isOwner);
						UserDisplayName.Set(displayName);
						RegisterTeamId(restoredTeamId);

						await DisplayAlert("Owner Access Restored",
							$"You have rejoined '{restoredTeamName}' as Owner (and Admin).\n\n" +
							$"Chat name: {displayName}\n\n" +
							"Your team data is intact in the cloud. This device is now the club-manager Owner account.",
							"OK");

						SyncTeamIdToLocalStorage(restoredTeamId);
						RefreshAppShellMenu();
						LoadCurrentTeam();
						_ = LoadSharedTeamsAsync();

						AdminRejoinTeamIdEntry.Text = string.Empty;
						AdminRejoinCodeEntry.Text = string.Empty;
					}
					else
					{
						var message = result.StartsWith("error:") ? result[6..] : result;
						await DisplayAlert("Rejoin Failed", message, "OK");
					}
				}
				catch (Exception ex)
				{
					await DisplayAlert("Error", $"Failed to rejoin as admin: {ex.Message}", "OK");
				}
					finally
					{
						RejoinAsAdminButton.IsEnabled = true;
					}
				}

				private async void OnRequestAdminCodeEmailClicked(object sender, EventArgs e)
				{
					try
					{
						var teamId = EmailReminderTeamIdEntry.Text?.Trim();

						if (string.IsNullOrWhiteSpace(teamId))
						{
							await DisplayAlert("Missing Info", "Please enter the Team ID to receive a recovery reminder.", "OK");
							return;
						}

						RequestAdminCodeEmailButton.IsEnabled = false;

						var result = await RequestAdminCodeEmailAsync(teamId);

						if (result.StartsWith("success:"))
						{
							var teamName = result[8..];
							await DisplayAlert("Email Sent",
								$"A recovery reminder has been sent to the email address registered for '{teamName}'.\n\n" +
								"If you don't receive it within a few minutes, check your spam folder.\n\n" +
								"Note: If no email was registered when the team was created, no email will be delivered.",
								"OK");
							EmailReminderTeamIdEntry.Text = string.Empty;
						}
						else if (result == "not_found")
						{
							await DisplayAlert("Team Not Found",
								$"No team found with ID '{teamId}'.\n\nPlease check the Team ID and try again.",
								"OK");
						}
						else
						{
							var message = result.StartsWith("error:") ? result[6..] : result;
							await DisplayAlert("Request Failed", message, "OK");
						}
					}
					catch (Exception ex)
					{
						await DisplayAlert("Error", $"Failed to send recovery email: {ex.Message}", "OK");
					}
					finally
					{
						RequestAdminCodeEmailButton.IsEnabled = true;
					}
				}

	private async Task<string> JoinTeamInFirestore(string inviteCode, string displayName)
	{
		System.Diagnostics.Debug.WriteLine($"[TeamDetails] JoinTeamInFirestore (SDK) - invite code: {inviteCode}");
		var cloud = ResolveCloudTeam();
		if (cloud is null)
			return "error: Cloud team service not available";

		var result = await cloud.JoinByInviteCodeAsync(inviteCode, displayName);
		if (result.StartsWith("success:", StringComparison.Ordinal) ||
		    result.StartsWith("already_member:", StringComparison.Ordinal))
		{
			var parts = result.Split(':');
			if (parts.Length >= 2)
			{
				var teamId = parts[1];
				try
				{
					// Seed the canonical local key used by ICloudRosterService / GameViewModel.
					var services = Application.Current?.Handler?.MauiContext?.Services;
					var rosterSvc = services?.GetService<Services.ICloudRosterService>();
					if (rosterSvc is not null)
					{
						var snap = await rosterSvc.LoadAsync(teamId, preferCloud: true);
						System.Diagnostics.Debug.WriteLine(
							snap is null
								? $"[TeamDetails] No cloud roster yet for {teamId} (admin may not have configured)"
								: $"[TeamDetails] Seeded local roster for {teamId}: {snap.Players.Count} players");
					}
				}
				catch (Exception ex)
				{
					System.Diagnostics.Debug.WriteLine($"[TeamDetails] Roster download after join: {ex.Message}");
				}
			}
		}
		return result;
	}

	/// <summary>
	/// Rebuilds Preferences / team switcher after cloud says already_member
	/// (local data lost, Firebase identity still linked to the team).
	/// </summary>
	private async Task RestoreExistingSharedMembershipAsync(
		string teamId,
		string teamName,
		string displayName,
		string? inviteCode)
	{
		UserDisplayName.Set(displayName);
		_ = UpdateMemberDisplayNameInFirestore(teamId, displayName);

		var role = "member";
		var isOwner = false;
		var cloud = ResolveCloudTeam();
		if (cloud is not null)
		{
			try
			{
				role = await cloud.GetMyRoleAsync(teamId) ?? "member";
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"[TeamDetails] Restore role: {ex.Message}");
			}

			try
			{
				isOwner = await cloud.IsTeamOwnerAsync(teamId);
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"[TeamDetails] Restore owner: {ex.Message}");
			}
		}

		QrCodeService.ApplySharedJoinLocalState(
			teamId, teamName, displayName, inviteCode, role, isOwner);

		// If join did not carry a code, pull metadata.inviteCode (Owner/Admin panel + Share).
		if (string.IsNullOrWhiteSpace(Preferences.Get($"{teamId}_invite_code", string.Empty)))
			await EnsureLocalInviteCodeAsync(teamId);

		try
		{
			var services = Application.Current?.Handler?.MauiContext?.Services;
			var rosterSvc = services?.GetService<Services.ICloudRosterService>();
			if (rosterSvc is not null)
				await rosterSvc.LoadAsync(teamId, preferCloud: true);
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"[TeamDetails] Restore roster: {ex.Message}");
		}

		SyncTeamIdToLocalStorage(teamId);
		RefreshAppShellMenu();
		LoadCurrentTeam();
		_ = LoadSharedTeamsAsync();
		_ = FcmService.Instance.EnsureRegisteredForCurrentTeamAsync();
	}

	private TeamInfo? FindTeamByInviteCode(string inviteCode)
	{
		// In Phase 1, we search through locally created teams stored in Preferences
		// In Phase 2, this will query Firestore instead

		// Get all preference keys
		var allKeys = GetAllPreferenceKeys();

		foreach (var key in allKeys)
		{
			// Look for invite code keys (format: "{teamId}_invite_code")
			if (key.EndsWith("_invite_code"))
			{
				var storedCode = Preferences.Get(key, string.Empty).ToUpperInvariant();

				if (storedCode == inviteCode)
				{
					// Extract team ID from key
					var teamId = key.Replace("_invite_code", "");

					// Get team name from metadata
					var teamName = Preferences.Get($"{teamId}_name", string.Empty);

					if (!string.IsNullOrEmpty(teamName))
					{
						return new TeamInfo
						{
							TeamId = teamId,
							TeamName = teamName,
							InviteCode = storedCode
						};
					}
				}
			}
		}

		return null;
	}

	private List<string> GetAllPreferenceKeys()
	{
		// Note: .NET MAUI Preferences doesn't provide a direct way to enumerate all keys
		// So we'll use a registry approach by maintaining a list of team IDs
		var teamListJson = Preferences.Get("team_id_list", "[]");

		try
		{
			// Simple JSON parsing for team ID list
			var teamIds = System.Text.Json.JsonSerializer.Deserialize<List<string>>(teamListJson) ?? new List<string>();
			return teamIds.SelectMany(id => new[] { $"{id}_invite_code", $"{id}_name" }).ToList();
		}
		catch
		{
			return new List<string>();
		}
	}

	private class TeamInfo
	{
		public string TeamId { get; set; } = string.Empty;
		public string TeamName { get; set; } = string.Empty;
		public string InviteCode { get; set; } = string.Empty;
	}

	/// <summary>
	/// Calls the 'requestAdminCodeEmail' Cloud Function (Plugin.Firebase.Functions / HTTP fallback).
	/// </summary>
	private async Task<string> RequestAdminCodeEmailAsync(string teamId)
	{
		System.Diagnostics.Debug.WriteLine($"[TeamDetails] RequestAdminCodeEmailAsync (SDK) - team: {teamId}");
		var cloud = ResolveCloudTeam();
		if (cloud is null)
			return "error: Cloud team service not available";
		return await cloud.RequestAdminCodeEmailAsync(teamId);
	}

	private async Task<string> RejoinAsAdminInFirestore(string teamId, string adminCode, string displayName)
	{
		System.Diagnostics.Debug.WriteLine($"[TeamDetails] RejoinAsAdminInFirestore (SDK) - team: {teamId}");
		var cloud = ResolveCloudTeam();
		if (cloud is null)
			return "error: Cloud team service not available";
		return await cloud.RejoinAsAdminAsync(teamId, adminCode, displayName, HashAdminCode);
	}

	/// <summary>
	/// Updates (or creates) the current user's member.displayName for a shared team.
	/// Merge write preserves fcmTokens and role.
	/// </summary>
	private async Task<string> UpdateMemberDisplayNameInFirestore(string teamId, string displayName)
	{
		var cloud = ResolveCloudTeam();
		if (cloud is null)
			return "error: Cloud team service not available";
		return await cloud.UpdateMemberDisplayNameAsync(
			teamId, displayName, Preferences.Get(USER_ROLE_KEY, "member"));
	}

	private async void OnSetLocalTeamClicked(object sender, EventArgs e)
	{
		try
		{
			var teamName = LocalTeamNameEntry.Text?.Trim();

			if (string.IsNullOrWhiteSpace(teamName))
			{
				await DisplayAlert("Invalid Name", "Please enter a team name.", "OK");
				return;
			}

			if (!await ConfirmTeamChangeAsync())
			{
				System.Diagnostics.Debug.WriteLine($"[TeamDetails] New team creation cancelled by user");
				return;
			}

			// Reset match state on the live GamePage ViewModel before switching team context.
			FindGamePage()?.ResetMatchState();

			var teamId = $"local_{GenerateShortId()}";

			// Save team metadata
			Preferences.Set($"{teamId}_name", teamName);

			// Register team ID in local team list
			RegisterLocalTeamId(teamId);

			// Set as current team
			Preferences.Set(TEAM_MODE_KEY, "local");
			Preferences.Set(TEAM_ID_KEY, teamId);
			Preferences.Set(TEAM_NAME_KEY, teamName);
			Preferences.Set(USER_ROLE_KEY, "admin"); // Local mode = always admin

			// Sync team ID to JavaScript
			SyncTeamIdToLocalStorage(teamId);

			// Trigger menu refresh
			RefreshAppShellMenu();

			await DisplayAlert("Local Team Created",
				$"Team: {teamName}\n\n" +
				"This team is stored on your device only.\n" +
				"No cloud sync or collaboration.\n\n" +
				"Next: Open the Game screen to name players, assign positions (field = swipe left, bench = swipe right, goalie = swipe left twice), and set timers.",
				"OK");

			LoadCurrentTeam();
			_ = LoadLocalTeamsAsync();
			LocalTeamNameEntry.Text = string.Empty;

			// Force reload Game page on next navigation
			var gamePage = FindGamePage();
			if (gamePage != null)
			{
				System.Diagnostics.Debug.WriteLine($"[TeamDetails] Marking Game page for reload after team creation");
				// GamePage re-initialises via OnAppearing when team changes

				// If currently on Game page, navigate away and back
				var currentPage = Shell.Current.CurrentPage;
				if (currentPage is GamePage)
				{
					await Shell.Current.GoToAsync("//SettingsPage");
					await Task.Delay(100);
					await Shell.Current.GoToAsync("//GamePage");
				}
			}
		}
		catch (Exception ex)
		{
			await DisplayAlert("Error", $"Failed to create local team: {ex.Message}", "OK");
		}
	}

	private void RegisterLocalTeamId(string teamId)
	{
		var teamListJson = Preferences.Get("local_team_id_list", "[]");

		try
		{
			var teamIds = System.Text.Json.JsonSerializer.Deserialize<List<string>>(teamListJson) ?? new List<string>();

			if (!teamIds.Contains(teamId))
			{
				teamIds.Add(teamId);
				var updatedJson = System.Text.Json.JsonSerializer.Serialize(teamIds);
				Preferences.Set("local_team_id_list", updatedJson);
			}
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"Error registering local team ID: {ex.Message}");
		}
	}

	private async void OnViewMembersClicked(object sender, EventArgs e)
	{
		try
		{
			var teamId = Preferences.Get(TEAM_ID_KEY, string.Empty);
			if (string.IsNullOrEmpty(teamId))
			{
				await DisplayAlert("No Team", "Select a shared team first.", "OK");
				return;
			}

			var cloud = ResolveCloudTeam();
			if (cloud is null)
			{
				await DisplayAlert("Unavailable", "Cloud team service is not available.", "OK");
				return;
			}

			var members = await cloud.ListMembersAsync(teamId);
			if (members.Count == 0)
			{
				await DisplayAlert("Team Members", "No members found (or could not load).", "OK");
				return;
			}

			// Resolve real owner (metadata.createdBy) so every viewer sees Owner, not only self-as-owner.
			var ownerUid = await cloud.GetTeamOwnerUidAsync(teamId);

			var body = string.Join("\n", members.Select(m =>
			{
				string role;
				if (!string.IsNullOrEmpty(ownerUid)
				    && string.Equals(m.Uid, ownerUid, StringComparison.Ordinal))
					role = "Owner";
				else if (m.IsAdmin)
					role = "Admin";
				else
					role = "Member";
				return $"• {m.DisplayName}  ({role})";
			}));

			await DisplayAlert("Team Members", body, "OK");
		}
		catch (Exception ex)
		{
			await DisplayAlert("Error", $"Could not load members: {ex.Message}", "OK");
		}
	}

	private async void OnRelinquishControlClicked(object sender, EventArgs e)
	{
		try
		{
			var teamId = Preferences.Get(TEAM_ID_KEY, string.Empty);
			var mode = Preferences.Get(TEAM_MODE_KEY, string.Empty);
			if (string.IsNullOrEmpty(teamId) || mode != "shared")
			{
				await DisplayAlert("Shared Team", "Select a shared team first.", "OK");
				return;
			}

			var confirm = await DisplayAlert(
				"Relinquish Match Control?",
				"Stop controlling the live match on this device.\n\n" +
				"Other Admins will no longer be locked to view-only and can Start or take control.\n\n" +
				"This does not end the match — it only releases the single-controller lock.",
				"Relinquish",
				"Cancel");
			if (!confirm) return;

			RelinquishControlButton.IsEnabled = false;

			// Prefer the live GameViewModel so local state updates immediately.
			var gamePage = FindGamePage();
			var vm = gamePage?.ViewModel;
			string result;
			if (vm is not null)
			{
				result = await vm.RelinquishControlAsync();
			}
			else
			{
				// Game not loaded — clear cloud lock directly if we are the controller.
				var roster = Handler?.MauiContext?.Services?.GetService<ICloudRosterService>()
				             ?? Application.Current?.Handler?.MauiContext?.Services
					             ?.GetService<ICloudRosterService>();
				if (roster is null)
				{
					await DisplayAlert("Unavailable", "Could not reach cloud services.", "OK");
					return;
				}

				var uid = await roster.GetSignedInUidAsync() ?? "";
				var snap = await roster.LoadAsync(teamId, preferCloud: true);
				var controller = snap?.ControllerUid?.Trim() ?? "";
				if (string.IsNullOrEmpty(controller))
				{
					await DisplayAlert("No Controller", "Nobody currently holds match control.", "OK");
					return;
				}

				if (!string.IsNullOrEmpty(uid)
				    && !string.Equals(controller, uid, StringComparison.Ordinal))
				{
					await DisplayAlert(
						"Not Controlling",
						$"{snap?.ControllerDisplayName ?? "Another Admin"} is controlling the match.\n\n" +
						"Ask them to Relinquish, Accept your Request control, or wait for auto-release if they went offline (~90s).",
						"OK");
					return;
				}

				await roster.PatchGameControlAsync(teamId, "", "", "", "", "", DateTimeOffset.UnixEpoch);
				result = "success";
			}

			if (result == "success")
			{
				await DisplayAlert(
					"Control Released",
					"Match control was relinquished. Another Admin can now run the game.",
					"OK");
			}
			else
			{
				var msg = result.StartsWith("error:", StringComparison.Ordinal)
					? result["error:".Length..].Trim()
					: result;
				await DisplayAlert("Could Not Relinquish", msg, "OK");
			}
		}
		catch (Exception ex)
		{
			await DisplayAlert("Error", $"Could not relinquish control: {ex.Message}", "OK");
		}
		finally
		{
			RelinquishControlButton.IsEnabled = true;
		}
	}

	private async void OnPromoteToAdminClicked(object sender, EventArgs e)
	{
		try
		{
			var teamId = Preferences.Get(TEAM_ID_KEY, string.Empty);
			if (string.IsNullOrEmpty(teamId))
			{
				await DisplayAlert("No Team", "Select a shared team first.", "OK");
				return;
			}

			var cloud = ResolveCloudTeam();
			if (cloud is null)
			{
				await DisplayAlert("Unavailable", "Cloud team service is not available.", "OK");
				return;
			}

			var selfUid = await cloud.EnsureSignedInAsync()
			              ?? Preferences.Get("chat_user_id", string.Empty);
			var members = await cloud.ListMembersAsync(teamId);
			var candidates = members
				.Where(m => !m.IsAdmin
				            && !string.Equals(m.Uid, selfUid, StringComparison.Ordinal))
				.ToList();

			if (candidates.Count == 0)
			{
				await DisplayAlert(
					"No Members to Promote",
					"Everyone on this team is already an Admin, or there are no other members yet.\n\n" +
					"Share the invite code so someone can join as a Member first.",
					"OK");
				return;
			}

			var labels = candidates.Select(m => m.DisplayName).ToArray();
			var choice = await DisplayActionSheet(
				"Promote to Admin…",
				"Cancel",
				null,
				labels);

			if (string.IsNullOrEmpty(choice) || choice == "Cancel")
				return;

			var target = candidates.FirstOrDefault(m => m.DisplayName == choice);
			if (target is null || candidates.Count(m => m.DisplayName == choice) > 1)
			{
				var uniqueLabels = candidates
					.Select(m => $"{m.DisplayName} ({m.Uid[..Math.Min(6, m.Uid.Length)]}…)")
					.ToArray();
				choice = await DisplayActionSheet("Promote to Admin…", "Cancel", null, uniqueLabels);
				if (string.IsNullOrEmpty(choice) || choice == "Cancel")
					return;
				var idx = Array.IndexOf(uniqueLabels, choice);
				if (idx < 0) return;
				target = candidates[idx];
			}

			var confirm = await DisplayAlert(
				"Promote to Admin?",
				$"Make {target.DisplayName} an Admin?\n\n" +
				"They will be able to run games, edit Location/Kit/Duties, and manage the team " +
				"(same as you, except only the Owner can delete the team or transfer ownership).\n\n" +
				"Tip: if two Admins control a live match at once, one should use Watch Only on the Game page.",
				"Promote",
				"Cancel");
			if (!confirm) return;

			var result = await cloud.PromoteMemberToAdminAsync(teamId, target.Uid);
			if (result == "success")
			{
				await DisplayAlert(
					"Promoted",
					$"{target.DisplayName} is now an Admin.\n\n" +
					"Ask them to open the Game tab (or switch away and back) so their device picks up Admin controls.",
					"OK");
			}
			else
			{
				var msg = result.StartsWith("error:", StringComparison.Ordinal)
					? result["error:".Length..].Trim()
					: result;
				await DisplayAlert("Could Not Promote", msg, "OK");
			}
		}
		catch (Exception ex)
		{
			await DisplayAlert("Error", $"Could not promote member: {ex.Message}", "OK");
		}
	}

	private async void OnRemoveMemberClicked(object sender, EventArgs e)
	{
		try
		{
			var teamId = Preferences.Get(TEAM_ID_KEY, string.Empty);
			if (string.IsNullOrEmpty(teamId))
			{
				await DisplayAlert("No Team", "Select a shared team first.", "OK");
				return;
			}

			var cloud = ResolveCloudTeam();
			if (cloud is null)
			{
				await DisplayAlert("Unavailable", "Cloud team service is not available.", "OK");
				return;
			}

			var selfUid = await cloud.EnsureSignedInAsync()
			              ?? Preferences.Get("chat_user_id", string.Empty);
			// Prefer live cloud owner (REST), not a stale Preferences flag from an older install.
			var ownerUid = await cloud.GetTeamOwnerUidAsync(teamId);
			var iAmOwner = !string.IsNullOrEmpty(ownerUid)
			               && !string.IsNullOrEmpty(selfUid)
			               && string.Equals(ownerUid, selfUid, StringComparison.Ordinal);
			// If ownership cannot be resolved, still list Admins so they can be cleaned up.
			var canRemoveAdmins = iAmOwner || string.IsNullOrEmpty(ownerUid);
			Preferences.Set($"{teamId}_isOwner", iAmOwner);

			var members = await cloud.ListMembersAsync(teamId);
			// Everyone except self; owner (or unresolved ownership) may remove other Admins;
			// co-admins only Members. Owner never appears (self filter + server createdBy check).
			var candidates = members
				.Where(m => !string.Equals(m.Uid, selfUid, StringComparison.Ordinal))
				.Where(m => canRemoveAdmins || !m.IsAdmin)
				.Where(m => string.IsNullOrEmpty(ownerUid)
				            || !string.Equals(m.Uid, ownerUid, StringComparison.Ordinal))
				.ToList();

			if (candidates.Count == 0)
			{
				await DisplayAlert(
					"No One to Remove",
					canRemoveAdmins
						? "There are no other removable members on this team."
						: "There are no Members to remove.\n\n" +
						  "Only the team Owner can remove other Admins. Use Leave Team to leave yourself.",
					"OK");
				return;
			}

			static string MemberRemoveLabel(CloudTeamMember m, string? owner)
			{
				if (!string.IsNullOrEmpty(owner)
				    && string.Equals(m.Uid, owner, StringComparison.Ordinal))
					return $"{m.DisplayName} (Owner)";
				if (m.IsAdmin) return $"{m.DisplayName} (Admin)";
				return m.DisplayName;
			}

			var labels = candidates.Select(m => MemberRemoveLabel(m, ownerUid)).ToArray();
			var choice = await DisplayActionSheet(
				"Remove Member…",
				"Cancel",
				null,
				labels);

			if (string.IsNullOrEmpty(choice) || choice == "Cancel")
				return;

			var target = candidates.FirstOrDefault(m =>
				string.Equals(MemberRemoveLabel(m, ownerUid), choice, StringComparison.Ordinal));
			if (target is null || candidates.Count(m =>
				    string.Equals(MemberRemoveLabel(m, ownerUid), choice, StringComparison.Ordinal)) > 1)
			{
				var uniqueLabels = candidates
					.Select(m =>
					{
						var role = (!string.IsNullOrEmpty(ownerUid)
						            && string.Equals(m.Uid, ownerUid, StringComparison.Ordinal))
							? "Owner"
							: m.IsAdmin ? "Admin" : "Member";
						return $"{m.DisplayName} ({role}, {m.Uid[..Math.Min(6, m.Uid.Length)]}…)";
					})
					.ToArray();
				choice = await DisplayActionSheet("Remove Member…", "Cancel", null, uniqueLabels);
				if (string.IsNullOrEmpty(choice) || choice == "Cancel")
					return;
				var idx = Array.IndexOf(uniqueLabels, choice);
				if (idx < 0) return;
				target = candidates[idx];
			}

			var roleNote = target.IsAdmin ? " (Admin)" : "";
			var confirm = await DisplayAlert(
				"Remove Member?",
				$"Remove {target.DisplayName}{roleNote} from this team?\n\n" +
				"They will lose access immediately and must rejoin with the invite code " +
				"(or admin recovery if they were an Admin).\n\n" +
				"This does not delete their device data — only cloud membership.",
				"Remove",
				"Cancel");
			if (!confirm) return;

			var result = await cloud.RemoveMemberAsync(teamId, target.Uid);
			if (result == "success")
			{
				await DisplayAlert(
					"Member Removed",
					$"{target.DisplayName} is no longer on this team.",
					"OK");
			}
			else
			{
				var msg = result.StartsWith("error:", StringComparison.Ordinal)
					? result["error:".Length..].Trim()
					: result;
				await DisplayAlert("Could Not Remove", msg, "OK");
			}
		}
		catch (Exception ex)
		{
			await DisplayAlert("Error", $"Could not remove member: {ex.Message}", "OK");
		}
	}

	private async void OnTransferOwnershipClicked(object sender, EventArgs e)
	{
		try
		{
			var teamId = Preferences.Get(TEAM_ID_KEY, string.Empty);
			var teamName = Preferences.Get(TEAM_NAME_KEY, "this team");
			if (string.IsNullOrEmpty(teamId))
			{
				await DisplayAlert("No Team", "Select a shared team first.", "OK");
				return;
			}

			var cloud = ResolveCloudTeam();
			if (cloud is null)
			{
				await DisplayAlert("Unavailable", "Cloud team service is not available.", "OK");
				return;
			}

			if (!await cloud.IsTeamOwnerAsync(teamId))
			{
				await DisplayAlert(
					"Owner Only",
					"Only the team owner (club manager) can transfer ownership.",
					"OK");
				Preferences.Set($"{teamId}_isOwner", false);
				TransferOwnershipButton.IsVisible = false;
				return;
			}

			var selfUid = await cloud.EnsureSignedInAsync() ?? Preferences.Get("chat_user_id", string.Empty);
			var members = await cloud.ListMembersAsync(teamId);
			var otherAdmins = members
				.Where(m => m.IsAdmin && !string.Equals(m.Uid, selfUid, StringComparison.Ordinal))
				.ToList();

			if (otherAdmins.Count == 0)
			{
				await DisplayAlert(
					"No Other Admins",
					"Ownership can only transfer to another Admin.\n\n" +
					"Use Promote to Admin first, then transfer ownership.",
					"OK");
				return;
			}

			// DisplayActionSheet: cancel + admin display names
			var labels = otherAdmins.Select(a => a.DisplayName).ToArray();
			var choice = await DisplayActionSheet(
				"Transfer ownership to…",
				"Cancel",
				null,
				labels);

			if (string.IsNullOrEmpty(choice) || choice == "Cancel")
				return;

			var target = otherAdmins.FirstOrDefault(a => a.DisplayName == choice);
			// Disambiguate duplicate names by re-prompting with uid suffix if needed
			if (target is null || otherAdmins.Count(a => a.DisplayName == choice) > 1)
			{
				var uniqueLabels = otherAdmins
					.Select(a => $"{a.DisplayName} ({a.Uid[..Math.Min(6, a.Uid.Length)]}…)")
					.ToArray();
				choice = await DisplayActionSheet("Transfer ownership to…", "Cancel", null, uniqueLabels);
				if (string.IsNullOrEmpty(choice) || choice == "Cancel")
					return;
				var idx = Array.IndexOf(uniqueLabels, choice);
				if (idx < 0) return;
				target = otherAdmins[idx];
			}

			var confirm = await DisplayAlert(
				"Transfer Ownership?",
				$"Make {target.DisplayName} the owner of '{teamName}'?\n\n" +
				"You will remain an Admin and can still run games, but only they can Delete the team from the cloud.\n\n" +
				"You can leave the team afterward if you wish.",
				"Transfer",
				"Cancel");
			if (!confirm) return;

			TransferOwnershipButton.IsEnabled = false;
			var result = await cloud.TransferOwnershipAsync(teamId, target.Uid);
			if (result == "success")
			{
				Preferences.Set($"{teamId}_isOwner", false);
				TransferOwnershipButton.IsVisible = false;
				await DisplayAlert(
					"Ownership Transferred",
					$"{target.DisplayName} is now the owner of '{teamName}'.",
					"OK");
				_ = LoadSharedTeamsAsync();
				LoadCurrentTeam();
			}
			else
			{
				var msg = result.StartsWith("error:", StringComparison.Ordinal)
					? result["error:".Length..].Trim()
					: result;
				await DisplayAlert("Transfer Failed", string.IsNullOrWhiteSpace(msg) ? result : msg, "OK");
			}
		}
		catch (Exception ex)
		{
			await DisplayAlert("Error", $"Transfer failed: {ex.Message}", "OK");
		}
		finally
		{
			TransferOwnershipButton.IsEnabled = true;
		}
	}

	private async void OnRegenerateCodeClicked(object sender, EventArgs e)
	{
		var confirm = await DisplayAlert("Regenerate Code?", 
			"This will invalidate the old invite code.\n\n" +
			"Team members using the old code will not be able to join.", 
			"Regenerate", "Cancel");

		if (!confirm) return;

		var teamId = Preferences.Get(TEAM_ID_KEY, string.Empty);
		var oldCode = Preferences.Get($"{teamId}_invite_code", string.Empty);
		var newCode = GenerateInviteCode();

		// Save locally immediately
		Preferences.Set($"{teamId}_invite_code", newCode);
		_lastPublishedInviteKey = null; // allow self-heal publish for the new code
		LoadInviteCode(refreshFromCloud: false);

		// Sync to Firestore in the background (Plugin.Firebase — non-blocking)
		_ = Task.Run(async () =>
		{
			try
			{
				var cloud = ResolveCloudTeam();
				if (cloud is null) return;
				var teamName = Preferences.Get(TEAM_NAME_KEY, string.Empty);
				var ok = await cloud.UpdateInviteCodeAsync(teamId, oldCode, newCode, teamName);
				System.Diagnostics.Debug.WriteLine(ok
					? $"[Firebase] Invite code synced via SDK: {newCode}"
					: "[Firebase] Invite code sync failed (non-fatal)");
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"[Firebase] Invite code sync error (non-fatal): {ex.Message}");
			}
		});

		await DisplayAlert("Code Regenerated", 
			$"New Invite Code: {newCode}\n\n" +
			"Share this with new members.", 
			"OK");
	}

	private async void OnShareTeamClicked(object sender, EventArgs e)
	{
		try
		{
			var teamId = Preferences.Get(TEAM_ID_KEY, string.Empty);
			var teamMode = Preferences.Get(TEAM_MODE_KEY, string.Empty);
			var teamName = Preferences.Get(TEAM_NAME_KEY, string.Empty);

			if (string.IsNullOrEmpty(teamId) || string.IsNullOrEmpty(teamMode))
			{
				await DisplayAlert("No Team", "Please select a team first.", "OK");
				return;
			}

			// Shared (cloud) team: QR carries only kind + invite code — roster/metadata come from Firestore.
			if (teamMode == "shared")
			{
				// Prefer cached code so the button responds immediately; refresh cloud in background.
				var cached = Preferences.Get($"{teamId}_invite_code", string.Empty);
				var inviteCode = (!string.IsNullOrWhiteSpace(cached) && cached != "N/A")
					? QrCodeService.NormalizeInviteCode(cached)
					: string.Empty;

				if (string.IsNullOrEmpty(inviteCode))
					inviteCode = await EnsureLocalInviteCodeAsync(teamId) ?? string.Empty;

				if (string.IsNullOrWhiteSpace(inviteCode) || inviteCode == "N/A")
				{
					await DisplayAlert(
						"No Invite Code",
						"Could not load this team's invite code from the cloud. Check your connection, or ask the Owner to open Team Admin (which publishes the code), then try again.",
						"OK");
					return;
				}

				PublishInviteCodeBestEffort(teamId, inviteCode);
				// Soft refresh in case another Admin regenerated (does not block the QR modal).
				_ = RefreshInviteCodeFromCloudAsync(teamId);

				var sharedData = QrCodeService.CreateSharedJoinInvite(inviteCode, teamName);
				await Navigation.PushModalAsync(new QrShareModal(sharedData));
				return;
			}

			// Local team: encode full roster for offline import
			var players = new List<Player>();
			var playersJson = Preferences.Get($"{teamId}_players", string.Empty);
			if (!string.IsNullOrEmpty(playersJson))
			{
				try
				{
					players = System.Text.Json.JsonSerializer.Deserialize<List<Player>>(playersJson) ?? [];
				}
				catch (Exception ex)
				{
					System.Diagnostics.Debug.WriteLine($"[TeamDetails] Failed to parse player list for QR: {ex.Message}");
				}
			}

			if (players.Count == 0)
			{
				for (var i = 1; i <= 16; i++)
				{
					players.Add(new Player { Name = Player.DefaultName(i), Position = PlayerPosition.None });
				}
			}

			var teamData = QrCodeService.CreateFromCurrentTeam(teamName, teamId, players);
			await Navigation.PushModalAsync(new QrShareModal(teamData));
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"[TeamDetails] OnShareTeamClicked error: {ex.Message}");
			await DisplayAlert("Error", $"Failed to share team: {ex.Message}", "OK");
		}
	}

	private async void OnImportTeamClicked(object sender, EventArgs e)
	{
		try
		{
			await Navigation.PushModalAsync(new QrImportPage());
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"[TeamDetails] OnImportTeamClicked error: {ex.Message}");
			await DisplayAlert("Error", "Failed to open Team Import.", "OK");
		}
	}

	/// <summary>
	/// Swipe right on a shared team row — leave that team (device-side). Same on iOS and Android.
	/// </summary>
	private async void OnLeaveSharedTeamSwipe(object sender, EventArgs e)
	{
		if (sender is not SwipeItem swipeItem || swipeItem.CommandParameter is not SharedTeamItem teamToLeave)
			return;

		System.Diagnostics.Debug.WriteLine($"[TeamDetails] Leave swipe triggered for: {teamToLeave.TeamName}");

		var ownerNote = teamToLeave.IsOwner
			? "\n\n⚠️ You are the team OWNER. Leaving does NOT delete the cloud team for other members/admins.\n" +
			  "To remove it for everyone, swipe the other way and choose Delete.\n" +
			  "If left abandoned, the cloud team is auto-removed after 12 months with no activity."
			: "";

		var confirm = await DisplayAlert(
			"Leave Team?",
			$"Are you sure you want to leave '{teamToLeave.TeamName}'?\n\n" +
			"This removes the team from this device. You will need an invite code (or admin recovery) to rejoin." +
			ownerNote,
			"Leave",
			"Cancel");

		if (!confirm)
		{
			System.Diagnostics.Debug.WriteLine("[TeamDetails] Leave cancelled by user");
			return;
		}

		await LeaveSharedTeamAsync(teamToLeave);
	}

	/// <summary>
	/// Swipe left — owner-only hard delete of the shared team in Firebase (all members lose access).
	/// </summary>
	private async void OnDeleteSharedTeamSwipe(object sender, EventArgs e)
	{
		if (sender is not SwipeItem swipeItem || swipeItem.CommandParameter is not SharedTeamItem team)
			return;

		// Live cloud owner uid (REST) — do not trust stale Preferences/IsOwner alone.
		var cloud = ResolveCloudTeam();
		var isOwner = false;
		if (cloud is not null)
		{
			try
			{
				isOwner = await cloud.IsTeamOwnerAsync(team.TeamId);
				Preferences.Set($"{team.TeamId}_isOwner", isOwner);
				team.IsOwner = isOwner;
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"[TeamDetails] IsTeamOwner check: {ex.Message}");
				isOwner = team.IsOwner; // last resort
			}
		}
		else
		{
			isOwner = team.IsOwner;
		}

		if (!isOwner)
		{
			await DisplayAlert(
				"Owner Only",
				$"Only the team owner (club manager who created '{team.TeamName}') can delete it from the cloud.\n\n" +
				"If this device reinstalled, Firebase may have a new identity — the cloud still lists the original Owner. " +
				"Use Leave to remove yourself, or delete from the original Owner device.\n\n" +
				"Co-admins should Leave rather than Delete.",
				"OK");
			return;
		}

		var confirm = await DisplayAlert(
			"Delete Team Forever?",
			$"Delete '{team.TeamName}' for EVERYONE?\n\n" +
			"This permanently removes cloud data including:\n" +
			"  • Team metadata & invite code\n" +
			"  • Member list\n" +
			"  • Shared roster\n" +
			"  • Chat & session history (when present)\n\n" +
			"Other admins and members will lose access. This cannot be undone.",
			"Delete Forever",
			"Cancel");

		if (!confirm)
			return;

		await DeleteSharedTeamAsOwnerAsync(team);
	}

	private async Task DeleteSharedTeamAsOwnerAsync(SharedTeamItem team)
	{
		try
		{
			var cloud = ResolveCloudTeam();
			if (cloud is null)
			{
				await DisplayAlert("Unavailable", "Cloud team service is not available. Check your connection.", "OK");
				return;
			}

			var result = await cloud.DeleteTeamAsOwnerAsync(team.TeamId);
			if (result == "error: not_owner")
			{
				Preferences.Set($"{team.TeamId}_isOwner", false);
				team.IsOwner = false;
				await DisplayAlert(
					"Owner Only",
					"Firebase did not accept this device as the team Owner.\n\n" +
					"Common cause after many redeploys: a new anonymous sign-in id, while " +
					"metadata.createdBy still points at the original Owner account.\n\n" +
					"Try again on the device that originally created the team, or use Leave " +
					"on this device and clean up from the true Owner.",
					"OK");
				return;
			}

			if (!result.StartsWith("success", StringComparison.Ordinal))
			{
				var msg = result.StartsWith("error:", StringComparison.Ordinal)
					? result["error:".Length..].Trim()
					: result;
				await DisplayAlert("Delete Failed", string.IsNullOrWhiteSpace(msg) ? result : msg, "OK");
				return;
			}

			// Local cleanup (same as leave, plus owner flag)
			await LeaveSharedTeamAsync(team, skipConfirmMessage: true);
			Preferences.Remove($"{team.TeamId}_isOwner");

			await DisplayAlert(
				"Team Deleted",
				$"'{team.TeamName}' was removed from Firebase and this device.",
				"OK");
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"[TeamDetails] ❌ Delete shared team: {ex.Message}");
			await DisplayAlert("Error", $"Failed to delete team: {ex.Message}", "OK");
		}
	}

	private Task RefreshSharedTeamCloudMetadataAsync()
		=> Task.WhenAll(RefreshSharedTeamOwnerFlagsAsync(), RefreshSharedTeamRolesFromCloudAsync());

	private async Task RefreshSharedTeamOwnerFlagsAsync()
	{
		try
		{
			var cloud = ResolveCloudTeam();
			if (cloud is null) return;

			var changed = false;
			foreach (var team in _sharedTeams.ToList())
			{
				var owner = await cloud.IsTeamOwnerAsync(team.TeamId).ConfigureAwait(false);
				Preferences.Set($"{team.TeamId}_isOwner", owner);
				if (team.IsOwner != owner)
				{
					team.IsOwner = owner;
					changed = true;
				}
			}

			if (changed)
			{
				await MainThread.InvokeOnMainThreadAsync(ReloadSharedTeamsCollection);
			}
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"[TeamDetails] Refresh owner flags: {ex.Message}");
		}
	}

	/// <summary>
	/// Re-reads each shared team's cloud role for this device (picks up Promote to Admin).
	/// </summary>
	private async Task RefreshSharedTeamRolesFromCloudAsync()
	{
		try
		{
			var cloud = ResolveCloudTeam();
			if (cloud is null) return;

			var listChanged = false;
			var currentTeamRoleChanged = false;
			var currentId = Preferences.Get(TEAM_ID_KEY, string.Empty);

			foreach (var team in _sharedTeams.ToList())
			{
				var role = await cloud.GetMyRoleAsync(team.TeamId).ConfigureAwait(false);
				if (string.IsNullOrWhiteSpace(role)) continue;

				var normalized = role.Trim().ToLowerInvariant();
				var previous = Preferences.Get($"{team.TeamId}_role", string.Empty);
				ApplyLocalRoleCache(team.TeamId, normalized);

				var label = char.ToUpperInvariant(normalized[0]) + normalized[1..];
				if (!string.Equals(team.Role, label, StringComparison.OrdinalIgnoreCase))
				{
					team.Role = label;
					listChanged = true;
				}

				if (string.Equals(team.TeamId, currentId, StringComparison.Ordinal)
				    && !string.Equals(previous, normalized, StringComparison.OrdinalIgnoreCase))
				{
					currentTeamRoleChanged = true;
				}
			}

			if (listChanged || currentTeamRoleChanged)
			{
				await MainThread.InvokeOnMainThreadAsync(() =>
				{
					// Only re-run invite/cloud side effects when the *current* role actually changed.
					if (currentTeamRoleChanged)
						LoadCurrentTeam(refreshInviteFromCloud: true);
					if (listChanged)
						ReloadSharedTeamsCollection();
				});
			}
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"[TeamDetails] Refresh roles: {ex.Message}");
		}
	}

	private void ReloadSharedTeamsCollection()
	{
		var copy = _sharedTeams.ToList();
		_sharedTeams.Clear();
		foreach (var t in copy)
			_sharedTeams.Add(t);
	}

	private async Task LeaveSharedTeamAsync(SharedTeamItem team, bool skipConfirmMessage = false)
	{
		try
		{
			System.Diagnostics.Debug.WriteLine($"[TeamDetails] Leaving shared team: {team.TeamName} ({team.TeamId})");

			var currentTeamId = Preferences.Get(TEAM_ID_KEY, string.Empty);
			var isCurrentTeam = currentTeamId == team.TeamId;

			// Remove from shared team ID list
			var teamListJson = Preferences.Get("team_id_list", "[]");
			var teamIds = System.Text.Json.JsonSerializer.Deserialize<List<string>>(teamListJson) ?? new List<string>();
			if (teamIds.Remove(team.TeamId))
			{
				Preferences.Set("team_id_list", System.Text.Json.JsonSerializer.Serialize(teamIds));
			}

			// Local cleanup for this team (cloud membership docs are not deleted here unless owner delete already ran)
			DeleteAllTeamData(team.TeamId);
			Preferences.Remove($"{team.TeamId}_role");
			Preferences.Remove($"team_mode_{team.TeamId}");
			Preferences.Remove($"user_role_{team.TeamId}");
			// Keep isOwner only if still on list — cleared on delete path

			if (isCurrentTeam)
			{
				Preferences.Remove(TEAM_MODE_KEY);
				Preferences.Remove(TEAM_ID_KEY);
				Preferences.Remove(TEAM_NAME_KEY);
				Preferences.Remove(USER_ROLE_KEY);

				if (!skipConfirmMessage)
				{
					await DisplayAlert(
						"Left Team",
						$"You have left '{team.TeamName}'.\n\n" +
						"No team is selected. Join or select another shared team to continue.",
						"OK");
				}
			}
			else if (!skipConfirmMessage)
			{
				await DisplayAlert("Left Team", $"You have left '{team.TeamName}'.", "OK");
			}

			LoadCurrentTeam();
			_ = LoadSharedTeamsAsync();
			RefreshAppShellMenu();
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"[TeamDetails] ❌ Leave team error: {ex.Message}");
			if (!skipConfirmMessage)
				await DisplayAlert("Error", $"Failed to leave team: {ex.Message}", "OK");
			else
				throw;
		}
	}

	private void LoadInviteCode(bool refreshFromCloud = false)
	{
		var teamId = Preferences.Get(TEAM_ID_KEY, string.Empty);
		if (string.IsNullOrEmpty(teamId))
			return;

		// Show cached value immediately. Cloud refresh is optional — every Appear used to
		// refresh+re-publish invite indexes (multiple Firestore writes) and jank the UI.
		var inviteCode = Preferences.Get($"{teamId}_invite_code", string.Empty);
		var hasCache = !string.IsNullOrWhiteSpace(inviteCode) && inviteCode != "N/A";
		InviteCodeDisplay.Text = hasCache ? inviteCode : "Loading…";

		if (refreshFromCloud || !hasCache)
			_ = RefreshInviteCodeFromCloudAsync(teamId);
	}

	/// <summary>
	/// Loads <c>metadata.inviteCode</c> from Firestore into Preferences.
	/// Cloud wins when available so regenerated codes sync across Admin devices.
	/// Falls back to the local cache if cloud is unreachable or empty.
	/// </summary>
	private async Task<string?> EnsureLocalInviteCodeAsync(string teamId)
	{
		var existing = Preferences.Get($"{teamId}_invite_code", string.Empty);
		var existingNorm = (!string.IsNullOrWhiteSpace(existing) && existing != "N/A")
			? QrCodeService.NormalizeInviteCode(existing)
			: string.Empty;

		try
		{
			var cloud = ResolveCloudTeam();
			if (cloud is null)
				return string.IsNullOrEmpty(existingNorm) ? null : existingNorm;

			var code = QrCodeService.NormalizeInviteCode(await cloud.GetTeamInviteCodeAsync(teamId));
			if (!string.IsNullOrEmpty(code))
			{
				if (!string.Equals(code, existingNorm, StringComparison.Ordinal))
				{
					Preferences.Set($"{teamId}_invite_code", code);
					System.Diagnostics.Debug.WriteLine(
						$"[TeamDetails] Invite code refreshed from cloud: {existingNorm} → {code}");
				}
				return code;
			}

			return string.IsNullOrEmpty(existingNorm) ? null : existingNorm;
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"[TeamDetails] EnsureLocalInviteCode: {ex.Message}");
			return string.IsNullOrEmpty(existingNorm) ? null : existingNorm;
		}
	}

	private async Task RefreshInviteCodeFromCloudAsync(string teamId)
	{
		var code = await EnsureLocalInviteCodeAsync(teamId).ConfigureAwait(false);
		await MainThread.InvokeOnMainThreadAsync(() =>
		{
			InviteCodeDisplay.Text = string.IsNullOrEmpty(code) ? "N/A" : code;
		});

		if (!string.IsNullOrEmpty(code))
			PublishInviteCodeBestEffort(teamId, code);
	}

	private void PublishInviteCodeBestEffort(string teamId, string inviteCode)
	{
		// Self-heal invite_codes index — but not on every page visit (was causing Android jank
		// via repeated Firestore upserts of dashed + compact ids + public/invite).
		var key = $"{teamId}|{QrCodeService.NormalizeInviteCode(inviteCode)}";
		var now = DateTimeOffset.UtcNow;
		if (string.Equals(_lastPublishedInviteKey, key, StringComparison.Ordinal)
		    && now - _lastInvitePublishUtc < InvitePublishMinInterval)
		{
			return;
		}

		_lastPublishedInviteKey = key;
		_lastInvitePublishUtc = now;

		var teamName = Preferences.Get(TEAM_NAME_KEY, string.Empty);
		_ = Task.Run(async () =>
		{
			try
			{
				var cloud = ResolveCloudTeam();
				if (cloud is null) return;
				var ok = await cloud.EnsureInviteCodePublishedAsync(teamId, inviteCode, teamName);
				System.Diagnostics.Debug.WriteLine(
					ok ? $"[TeamDetails] Invite code published for join: {inviteCode}"
					   : $"[TeamDetails] Invite code publish failed: {inviteCode}");
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"[TeamDetails] Invite publish: {ex.Message}");
			}
		});
	}

	// Team ID generation with auto-suffix to prevent conflicts
	private string GenerateTeamId(params string[] parts)
	{
		var baseName = string.Join("-", parts)
			.ToLowerInvariant()
			.Replace(" ", "-")
			.Replace("_", "-");
		
		// Remove special characters
		baseName = new string(baseName.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
		
		// Add unique suffix
		var suffix = GenerateShortId();
		
		return $"{baseName}-{suffix}";
	}

	private string EscapeJavaScript(string text)
	{
		return text.Replace("\\", "\\\\")
				   .Replace("'", "\\'")
				   .Replace("\"", "\\\"")
				   .Replace("\n", "\\n")
				   .Replace("\r", "\\r");
	}

	private string GenerateShortId()
	{
		// Generate 6-character alphanumeric ID
		const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
		var random = new Random();
		var id = new StringBuilder(6);
		
		for (int i = 0; i < 6; i++)
		{
			id.Append(chars[random.Next(chars.Length)]);
		}
		
		return id.ToString();
	}

	private string GenerateInviteCode()
	{
		// Generate 8-character uppercase alphanumeric code
		const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // Exclude ambiguous chars (0,O,1,I)
		var random = new Random();
		var code = new StringBuilder(8);

		for (int i = 0; i < 8; i++)
		{
			code.Append(chars[random.Next(chars.Length)]);
			if (i == 3) code.Append('-'); // Add dash in middle for readability
		}

		return code.ToString();
	}

	/// <summary>Generates a 16-character admin recovery code (format: XXXX-XXXX-XXXX-XXXX).</summary>
	private string GenerateAdminCode()
	{
		const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
		var random = new Random();
		var segments = new string[4];
		for (int s = 0; s < 4; s++)
		{
			var seg = new StringBuilder(4);
			for (int i = 0; i < 4; i++)
				seg.Append(chars[random.Next(chars.Length)]);
			segments[s] = seg.ToString();
		}
		return string.Join("-", segments);
	}

	/// <summary>Returns the SHA-256 hex digest of the given plain-text code (uppercase-normalised).</summary>
	private static string HashAdminCode(string code)
	{
		var bytes = System.Security.Cryptography.SHA256.HashData(
			System.Text.Encoding.UTF8.GetBytes(code.ToUpperInvariant()));
		return Convert.ToHexString(bytes).ToLowerInvariant();
	}

	// Trigger AppShell to refresh menu item availability
	private void RefreshAppShellMenu()
	{
		try
		{
			// Force AppShell to re-evaluate menu item availability
			if (Application.Current?.MainPage is AppShell appShell)
			{
				appShell.RefreshMenu();
				System.Diagnostics.Debug.WriteLine("[TeamDetails] Menu refresh triggered");
			}
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"[TeamDetails] Error refreshing menu: {ex.Message}");
		}
	}
}

// Local team item for UI binding
public class LocalTeamItem
{
	public string TeamId { get; set; } = string.Empty;
	public string TeamName { get; set; } = string.Empty;
	public bool IsActive { get; set; }
}

// Shared (cloud) team item for UI binding
public class SharedTeamItem
{
	public string TeamId { get; set; } = string.Empty;
	public string TeamName { get; set; } = string.Empty;
	public bool IsActive { get; set; }
	public string Role { get; set; } = string.Empty;
	/// <summary>True when this device's Firebase user created the team (club manager / owner).</summary>
	public bool IsOwner { get; set; }
	public string RoleLabel => IsOwner ? "Owner" : Role;
}

// Converter to show checkmark for active team
public class BoolToCheckConverter : IValueConverter
{
	public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
	{
		return value is bool isActive && isActive ? "✓" : "";
	}

	public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
	{
		throw new NotImplementedException();
	}
}
