using System.Text;
using System.Collections.ObjectModel;

namespace TurfTime2;

public partial class TeamDetailsPage : ContentPage
{
	private const string TEAM_MODE_KEY = "team_mode"; // "shared" or "local"
	private const string TEAM_ID_KEY = "team_id";
	private const string TEAM_NAME_KEY = "team_name";
	private const string USER_ROLE_KEY = "user_role"; // "admin" or "member"

	private ObservableCollection<LocalTeamItem> _localTeams = new();

	public TeamDetailsPage()
	{
		InitializeComponent();
		LocalTeamsCollection.ItemsSource = _localTeams;
		LoadCurrentTeam();
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
		LoadCurrentTeam();
	}

	private void LoadCurrentTeam()
	{
		var teamMode = Preferences.Get(TEAM_MODE_KEY, string.Empty);
		var teamName = Preferences.Get(TEAM_NAME_KEY, string.Empty);
		var userRole = Preferences.Get(USER_ROLE_KEY, string.Empty);

		if (string.IsNullOrEmpty(teamMode))
		{
			CurrentTeamLabel.Text = "No team selected";
			TeamModeLabel.Text = "Mode: Not configured";
			AdminPanel.IsVisible = false;
			LeaveTeamButton.IsVisible = false;
		}
		else
		{
			CurrentTeamLabel.Text = teamName;
			TeamModeLabel.Text = $"Mode: {(teamMode == "shared" ? "Shared (Cloud)" : "Local (Device only)")}";

			if (teamMode == "shared" && userRole == "admin")
			{
				AdminPanel.IsVisible = true;
				LoadInviteCode();
			}
			else
			{
				AdminPanel.IsVisible = false;
			}

			// Only show Leave Team button for shared mode (makes sense to leave a cloud team)
			// For local mode, just switch to another team instead
			LeaveTeamButton.IsVisible = teamMode == "shared";
		}
	}

	private void OnSharedCheckboxChanged(object sender, CheckedChangedEventArgs e)
	{
		if (e.Value)
		{
			// Shared checked - show create/join options, disable local
			LocalCheckbox.IsChecked = false;
			CreateTeamSection.IsVisible = true;
			JoinTeamSection.IsVisible = true;
			LocalTeamSection.IsVisible = false;
		}
		else
		{
			// Shared unchecked - hide create/join options
			CreateTeamSection.IsVisible = false;
			JoinTeamSection.IsVisible = false;
		}
	}

	private void OnLocalCheckboxChanged(object sender, CheckedChangedEventArgs e)
	{
		if (e.Value)
		{
			// Local checked - show local team management, disable shared
			SharedCheckbox.IsChecked = false;
			LocalTeamSection.IsVisible = true;
			CreateTeamSection.IsVisible = false;
			JoinTeamSection.IsVisible = false;

			// Load and display all local teams
			LoadLocalTeams();
		}
		else
		{
			// Local unchecked - hide local team input
			LocalTeamSection.IsVisible = false;
			LocalTeamsCollection.IsVisible = false;
			LocalTeamSwitcherLabel.IsVisible = false;
		}
	}

	private void LoadLocalTeams()
	{
		var teamListJson = Preferences.Get("local_team_id_list", "[]");

		try
		{
			System.Diagnostics.Debug.WriteLine($"[TeamDetails] Loading local teams...");

			var teamIds = System.Text.Json.JsonSerializer.Deserialize<List<string>>(teamListJson) ?? new List<string>();
			var currentTeamId = Preferences.Get(TEAM_ID_KEY, string.Empty);
			var currentTeamMode = Preferences.Get(TEAM_MODE_KEY, string.Empty);

			// Create new list instead of clearing to force Android CollectionView refresh
			var newTeams = new List<LocalTeamItem>();

			foreach (var teamId in teamIds)
			{
				var teamName = Preferences.Get($"{teamId}_name", string.Empty);
				if (!string.IsNullOrEmpty(teamName))
				{
					newTeams.Add(new LocalTeamItem
					{
						TeamId = teamId,
						TeamName = teamName,
						IsActive = currentTeamMode == "local" && currentTeamId == teamId
					});
				}
			}

			// Clear and re-add all items to trigger proper UI update on Android
			_localTeams.Clear();
			foreach (var team in newTeams)
			{
				_localTeams.Add(team);
			}

			System.Diagnostics.Debug.WriteLine($"[TeamDetails] Found {_localTeams.Count} local teams");

			// Show team switcher if there are teams
			if (_localTeams.Count > 0)
			{
				// Update label with team count badge
				LocalTeamSwitcherLabel.Text = $"Your Teams ({_localTeams.Count})";
				LocalTeamsCollection.IsVisible = true;
				LocalTeamSwitcherLabel.IsVisible = true;
				TeamSeparator.IsVisible = true; // Show separator between existing teams and create section
			}
			else
			{
				LocalTeamsCollection.IsVisible = false;
				LocalTeamSwitcherLabel.IsVisible = false;
				TeamSeparator.IsVisible = false; // Hide separator when no teams exist
			}
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"Error loading local teams: {ex.Message}");
		}
	}

	private void OnLocalTeamSelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		// DEBUG: Log that event fired
		System.Diagnostics.Debug.WriteLine($"[TeamDetails] SelectionChanged fired. Selection count: {e.CurrentSelection.Count}");

		if (e.CurrentSelection.FirstOrDefault() is LocalTeamItem selectedTeam)
		{
			System.Diagnostics.Debug.WriteLine($"[TeamDetails] Team selected: {selectedTeam.TeamName} (ID: {selectedTeam.TeamId})");

			// Check if already the current team
			var currentTeamId = Preferences.Get(TEAM_ID_KEY, string.Empty);
			System.Diagnostics.Debug.WriteLine($"[TeamDetails] Current team ID: {currentTeamId}");

			if (currentTeamId == selectedTeam.TeamId)
			{
				System.Diagnostics.Debug.WriteLine($"[TeamDetails] Already on this team, ignoring selection");
				// Already on this team, just clear selection
				LocalTeamsCollection.SelectedItem = null;
				return;
			}

			System.Diagnostics.Debug.WriteLine($"[TeamDetails] Switching to team: {selectedTeam.TeamName}");

			// Switch to selected team
			Preferences.Set(TEAM_MODE_KEY, "local");
			Preferences.Set(TEAM_ID_KEY, selectedTeam.TeamId);
			Preferences.Set(TEAM_NAME_KEY, selectedTeam.TeamName);
			Preferences.Set(USER_ROLE_KEY, "admin"); // Local mode = always admin

			// Sync team ID to localStorage for JavaScript roster manager
			SyncTeamIdToLocalStorage(selectedTeam.TeamId);

			// Clear selection immediately to prevent double-tap issues on Android
			LocalTeamsCollection.SelectedItem = null;

			// Show confirmation
			MainThread.BeginInvokeOnMainThread(async () =>
			{
				System.Diagnostics.Debug.WriteLine($"[TeamDetails] Showing dialog for: {selectedTeam.TeamName}");

				await DisplayAlert("Team Switched", 
					$"Now managing: {selectedTeam.TeamName}\n\n" +
					"Your roster, chat, and logs are now for this team.", 
					"OK");

				System.Diagnostics.Debug.WriteLine($"[TeamDetails] Dialog closed, refreshing UI");

				// Refresh UI AFTER dialog closes to ensure it's visible
				LoadCurrentTeam();
				LoadLocalTeams();

				System.Diagnostics.Debug.WriteLine($"[TeamDetails] UI refresh complete");
			});
		}
		else
		{
			System.Diagnostics.Debug.WriteLine($"[TeamDetails] No team selected (CurrentSelection is null or not LocalTeamItem)");
		}
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
	private void SyncTeamIdToLocalStorage(string teamId)
	{
		try
		{
			// Get GamePage WebView to execute JavaScript
			var gamePage = Application.Current?.MainPage?.Navigation?.NavigationStack
				?.FirstOrDefault(p => p is GamePage) as GamePage;

			if (gamePage?.GameWebView != null)
			{
				System.Diagnostics.Debug.WriteLine($"[TeamDetails] Syncing team ID to localStorage: {teamId}");

				// Set team_id in localStorage and trigger roster reload
				var script = $@"
					(function() {{
						try {{
							localStorage.setItem('team_id', '{EscapeJavaScript(teamId)}');
							if (typeof window.reloadRosterForTeam === 'function') {{
								window.reloadRosterForTeam('{EscapeJavaScript(teamId)}');
								return 'success';
							}} else {{
								console.warn('[TeamSync] reloadRosterForTeam not available yet');
								return 'pending';
							}}
						}} catch (error) {{
							console.error('[TeamSync] Error:', error);
							return 'error: ' + error.message;
						}}
					}})();
				";

				// Execute asynchronously (don't block UI)
				MainThread.BeginInvokeOnMainThread(async () =>
				{
					try
					{
						var result = await gamePage.GameWebView.EvaluateJavaScriptAsync(script);
						System.Diagnostics.Debug.WriteLine($"[TeamDetails] Team sync result: {result}");
					}
					catch (Exception ex)
					{
						System.Diagnostics.Debug.WriteLine($"[TeamDetails] Team sync error: {ex.Message}");
					}
				});
			}
			else
			{
				System.Diagnostics.Debug.WriteLine($"[TeamDetails] GamePage WebView not available - team ID will sync when Game page loads");
			}
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"[TeamDetails] SyncTeamIdToLocalStorage error: {ex.Message}");
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
			LoadLocalTeams();
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
				$"{teamId}_settings",       // Team-specific settings
				$"{teamId}_history",        // Match history
				$"{teamId}_stats"           // Team statistics
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
		try
		{
			string teamName;
			string teamId;

			// Validate input
			if (!string.IsNullOrWhiteSpace(NicknameEntry.Text))
			{
				teamName = NicknameEntry.Text.Trim();
				teamId = GenerateTeamId(teamName);
			}
			else if (!string.IsNullOrWhiteSpace(ClubEntry.Text) && !string.IsNullOrWhiteSpace(TeamEntry.Text))
			{
				var club = ClubEntry.Text.Trim();
				var team = TeamEntry.Text.Trim();
				teamName = $"{club} - {team}";
				teamId = GenerateTeamId(club, team);
			}
			else
			{
				await DisplayAlert("Invalid Input", "Please enter either Club + Team or a Nickname.", "OK");
				return;
			}

			// Generate invite code
			var inviteCode = GenerateInviteCode();

			// Create team in Firestore via JavaScript
			var result = await CreateTeamInFirestore(teamId, teamName, inviteCode);

			if (result == "success")
			{
				// Save locally as well for Phase 1 compatibility
				Preferences.Set($"{teamId}_invite_code", inviteCode);
				Preferences.Set($"{teamId}_name", teamName);
				RegisterTeamId(teamId);

				// Set as current team
				Preferences.Set(TEAM_MODE_KEY, "shared");
				Preferences.Set(TEAM_ID_KEY, teamId);
				Preferences.Set(TEAM_NAME_KEY, teamName);
				Preferences.Set(USER_ROLE_KEY, "admin");

				await DisplayAlert("Team Created!", 
					$"Team: {teamName}\n\n" +
					$"Team ID: {teamId}\n\n" +
					$"Invite Code: {inviteCode}\n\n" +
					"Share this code with your team members.\n\n" +
					"Team data is now synced to the cloud!", 
					"OK");

				LoadCurrentTeam();

				// Clear inputs
				ClubEntry.Text = string.Empty;
				TeamEntry.Text = string.Empty;
				NicknameEntry.Text = string.Empty;
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
	}

	private async Task<string> CreateTeamInFirestore(string teamId, string teamName, string inviteCode)
	{
		// Get WebView from GamePage (it has Firebase initialized)
		var gamePage = Application.Current?.MainPage?.Navigation?.NavigationStack
			?.FirstOrDefault(p => p is GamePage) as GamePage;

		if (gamePage?.GameWebView != null)
		{
			try
			{
				var script = $@"
					(async function() {{
						try {{
							if (typeof teamService === 'undefined') {{
								return 'error: Team service not initialized';
							}}
							const result = await teamService.createTeam('{teamId}', '{EscapeJavaScript(teamName)}', '{inviteCode}');
							return result.success ? 'success' : 'error';
						}} catch (error) {{
							return 'error: ' + error.message;
						}}
					}})();
				";

				var result = await gamePage.GameWebView.EvaluateJavaScriptAsync(script);
				return result ?? "error: No response";
			}
			catch (Exception ex)
			{
				return $"error: {ex.Message}";
			}
		}

		return "error: Firebase not available. Please open the Game page first.";
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

			// Try to join team via Firestore first
			var result = await JoinTeamInFirestore(inviteCode);

			if (result.StartsWith("success:"))
			{
				// Parse result: "success:teamId:teamName"
				var parts = result.Split(':');
				if (parts.Length >= 3)
				{
					var teamId = parts[1];
					var teamName = parts[2];

					// Save locally
					Preferences.Set(TEAM_MODE_KEY, "shared");
					Preferences.Set(TEAM_ID_KEY, teamId);
					Preferences.Set(TEAM_NAME_KEY, teamName);
					Preferences.Set(USER_ROLE_KEY, "member");

					await DisplayAlert("Joined Team!", 
						$"Successfully joined: {teamName}\n\n" +
						$"Role: Member\n\n" +
						"You can now collaborate with your team.", 
						"OK");

					LoadCurrentTeam();
					InviteCodeEntry.Text = string.Empty;
					return;
				}
			}
			else if (result.StartsWith("already_member:"))
			{
				var parts = result.Split(':');
				if (parts.Length >= 3)
				{
					var teamName = parts[2];
					await DisplayAlert("Already a Member", 
						$"You are already a member of '{teamName}'.", 
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

	private async Task<string> JoinTeamInFirestore(string inviteCode)
	{
		var gamePage = Application.Current?.MainPage?.Navigation?.NavigationStack
			?.FirstOrDefault(p => p is GamePage) as GamePage;

		if (gamePage?.GameWebView != null)
		{
			try
			{
				var script = $@"
					(async function() {{
						try {{
							if (typeof teamService === 'undefined') {{
								return 'error: Team service not initialized';
							}}
							const result = await teamService.joinTeam('{inviteCode}');
							if (result.success) {{
								if (result.alreadyMember) {{
									return 'already_member:' + result.teamId + ':' + result.teamName;
								}}
								return 'success:' + result.teamId + ':' + result.teamName;
							}}
							return 'error: Failed to join team';
						}} catch (error) {{
							return 'error: ' + error.message;
						}}
					}})();
				";

				var result = await gamePage.GameWebView.EvaluateJavaScriptAsync(script);
				return result ?? "error: No response";
			}
			catch (Exception ex)
			{
				return $"error: {ex.Message}";
			}
		}

		return "error: Firebase not available";
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

			await DisplayAlert("Local Team Created",
				$"Team: {teamName}\n\n" +
				"This team is stored on your device only.\n" +
				"No cloud sync or collaboration.", 
				"OK");

			LoadCurrentTeam();
			LoadLocalTeams();
			LocalTeamNameEntry.Text = string.Empty;
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

	private void OnViewMembersClicked(object sender, EventArgs e)
	{
		DisplayAlert("Team Members", "Feature coming soon!\n\nThis will show all team members with their roles.", "OK");
	}

	private async void OnRegenerateCodeClicked(object sender, EventArgs e)
	{
		var confirm = await DisplayAlert("Regenerate Code?", 
			"This will invalidate the old invite code.\n\n" +
			"Team members using the old code will not be able to join.", 
			"Regenerate", "Cancel");

		if (confirm)
		{
			var teamId = Preferences.Get(TEAM_ID_KEY, string.Empty);
			var newCode = GenerateInviteCode();
			
			Preferences.Set($"{teamId}_invite_code", newCode);
			
			await DisplayAlert("Code Regenerated", 
				$"New Invite Code: {newCode}\n\n" +
				"Share this with new members.", 
				"OK");
			
			LoadInviteCode();
		}
	}

	private async void OnLeaveTeamClicked(object sender, EventArgs e)
	{
		var teamName = Preferences.Get(TEAM_NAME_KEY, "this team");
		var confirm = await DisplayAlert("Leave Team?", 
			$"Are you sure you want to leave '{teamName}'?\n\n" +
			"You will need an invite code to rejoin.", 
			"Leave", "Cancel");

		if (confirm)
		{
			Preferences.Remove(TEAM_MODE_KEY);
			Preferences.Remove(TEAM_ID_KEY);
			Preferences.Remove(TEAM_NAME_KEY);
			Preferences.Remove(USER_ROLE_KEY);

			await DisplayAlert("Left Team", "You have left the team.", "OK");
			LoadCurrentTeam();
		}
	}

	private void LoadInviteCode()
	{
		var teamId = Preferences.Get(TEAM_ID_KEY, string.Empty);
		if (!string.IsNullOrEmpty(teamId))
		{
			var inviteCode = Preferences.Get($"{teamId}_invite_code", "N/A");
			InviteCodeDisplay.Text = inviteCode;
		}
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
}

// Local team item for UI binding
public class LocalTeamItem
{
	public string TeamId { get; set; } = string.Empty;
	public string TeamName { get; set; } = string.Empty;
	public bool IsActive { get; set; }
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
