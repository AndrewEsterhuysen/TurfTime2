using System.Text;
using System.Collections.ObjectModel;
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
		LoadCurrentTeam();

		// This release is local-only. Always ensure Local is selected.
		// SharedCheckbox path is preserved for the future cloud release.
		if (!LocalCheckbox.IsChecked)
			LocalCheckbox.IsChecked = true;  // triggers OnLocalCheckboxChanged -> LoadLocalTeamsAsync
		else
			_ = LoadLocalTeamsAsync();       // already checked — load teams manually
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

	private bool _createTeamExpanded = false;

	private void OnSharedCheckboxChanged(object sender, CheckedChangedEventArgs e)
	{
		if (e.Value)
		{
			LocalCheckbox.IsChecked = false;
			SharedTeamSection.IsVisible = true;
			JoinTeamSection.IsVisible = true;
			RejoinAdminSection.IsVisible = true;
			LocalTeamSection.IsVisible = false;
			_ = LoadSharedTeamsAsync();
		}
		else
		{
			SharedTeamSection.IsVisible = false;
			JoinTeamSection.IsVisible = false;
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
			JoinTeamSection.IsVisible = false;
			_ = LoadLocalTeamsAsync();
		}
		else
		{
			LocalTeamSection.IsVisible = false;
			LocalTeamsCollection.IsVisible = false;
			LocalTeamSwitcherLabel.IsVisible = false;
		}
		UpdateCreateTeamSubSections();
	}

	private void OnCreateTeamHeaderTapped(object sender, EventArgs e)
	{
		_createTeamExpanded = !_createTeamExpanded;
		CreateTeamContent.IsVisible = _createTeamExpanded;
		CreateTeamToggleIcon.Text = _createTeamExpanded ? "▲" : "▼";
		CreateTeamHint.IsVisible = !_createTeamExpanded;
		if (_createTeamExpanded)
			UpdateCreateTeamSubSections();
	}

	private void UpdateCreateTeamSubSections()
	{
		if (!_createTeamExpanded) return;
		// Local-only release: always show local creation, hide shared and no-mode hint.
		// SharedCheckbox / CreateSharedSection are preserved for the future cloud release.
		bool isShared = SharedCheckbox.IsChecked;
		CreateTeamNoModeLabel.IsVisible = false;
		CreateSharedSection.IsVisible = isShared;   // stays false in this release
		CreateLocalSection.IsVisible = true;
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
						result.Add(new SharedTeamItem
						{
							TeamId = teamId,
							TeamName = teamName,
							IsActive = isActive,
							Role = char.ToUpperInvariant(role[0]) + role[1..]
						});
					}
				}
				return result;
			}).ConfigureAwait(true); // resume on the UI thread for collection/UI updates

			_sharedTeams.Clear();
			foreach (var team in newTeams)
				_sharedTeams.Add(team);

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

	private void OnSharedTeamSelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (e.CurrentSelection.FirstOrDefault() is not SharedTeamItem selectedTeam)
			return;

		var currentTeamId = Preferences.Get(TEAM_ID_KEY, string.Empty);

		if (currentTeamId == selectedTeam.TeamId)
		{
			SharedTeamsCollection.SelectedItem = null;
			return;
		}

		System.Diagnostics.Debug.WriteLine($"[TeamDetails] Switching to shared team: {selectedTeam.TeamName}");

		var storedRole = Preferences.Get($"{selectedTeam.TeamId}_role", "member");
		Preferences.Set(TEAM_MODE_KEY, "shared");
		Preferences.Set(TEAM_ID_KEY, selectedTeam.TeamId);
		Preferences.Set(TEAM_NAME_KEY, selectedTeam.TeamName);
		Preferences.Set(USER_ROLE_KEY, storedRole);

		SyncTeamIdToLocalStorage(selectedTeam.TeamId);
		RefreshAppShellMenu();
		SharedTeamsCollection.SelectedItem = null;

		MainThread.BeginInvokeOnMainThread(async () =>
		{
			await DisplayAlert("Team Switched",
				$"Now managing: {selectedTeam.TeamName}\n\n" +
				$"Role: {selectedTeam.Role}",
				"OK");

			LoadCurrentTeam();
			_ = LoadSharedTeamsAsync();
		});
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

	// Static wrapper for GamePage polling
	public static async Task<string?> DownloadRosterFromFirestoreStatic(string teamId)
	{
		try
		{
			if (!await EnsureFirebaseAuthStaticAsync()) return null;

			var baseUrl = $"https://firestore.googleapis.com/v1/projects/{FirebaseProjectId}/databases/(default)/documents";
			var rosterUrl = $"{baseUrl}/teams/{teamId}/roster/data";

			System.Net.Http.HttpResponseMessage response2;
			for (int attempt = 0; attempt < 2; attempt++)
			{
				_httpClient.DefaultRequestHeaders.Authorization =
					new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _firebaseIdToken);
				response2 = await _httpClient.GetAsync(rosterUrl);

				if (response2.StatusCode == System.Net.HttpStatusCode.Unauthorized && attempt == 0)
				{
					System.Diagnostics.Debug.WriteLine("[TeamDetails] Token expired on download, refreshing...");
					_firebaseIdToken = null;
					if (!await EnsureFirebaseAuthStaticAsync()) return null;
					continue;
				}

				if (!response2.IsSuccessStatusCode)
					return null;

				var json2 = await response2.Content.ReadAsStringAsync();

			// Parse Firestore document and convert to roster format
			using var doc2 = System.Text.Json.JsonDocument.Parse(json2);
			if (!doc2.RootElement.TryGetProperty("fields", out var fields))
				return null;

			var rosterDataBuilder = new System.Text.StringBuilder();
			rosterDataBuilder.Append("{");

			// Extract lastModified
			if (fields.TryGetProperty("lastModified", out var timestamp) &&
				timestamp.TryGetProperty("timestampValue", out var ts))
			{
				rosterDataBuilder.Append($"\"lastModified\":\"{ts.GetString()}\",");
			}

			// Extract game state fields
			if (fields.TryGetProperty("matchDurationSeconds", out var matchDuration) &&
				matchDuration.TryGetProperty("integerValue", out var matchDurVal))
			{
				rosterDataBuilder.Append($"\"matchDurationSeconds\":{matchDurVal.GetString()},");
			}

			if (fields.TryGetProperty("halfDurationSeconds", out var halfDuration) &&
				halfDuration.TryGetProperty("integerValue", out var halfDurVal))
			{
				rosterDataBuilder.Append($"\"halfDurationSeconds\":{halfDurVal.GetString()},");
			}

			if (fields.TryGetProperty("matchRemainingSeconds", out var matchRemaining) &&
				matchRemaining.TryGetProperty("integerValue", out var matchRemVal))
			{
				rosterDataBuilder.Append($"\"matchRemainingSeconds\":{matchRemVal.GetString()},");
			}

			if (fields.TryGetProperty("currentHalf", out var currentHalf) &&
				currentHalf.TryGetProperty("stringValue", out var currentHalfVal))
			{
				rosterDataBuilder.Append($"\"currentHalf\":\"{currentHalfVal.GetString()}\",");
			}

			if (fields.TryGetProperty("timerRunning", out var timerRunning) &&
				timerRunning.TryGetProperty("booleanValue", out var timerRunningVal))
			{
				rosterDataBuilder.Append($"\"timerRunning\":{(timerRunningVal.GetBoolean() ? "true" : "false")},");
			}

			if (fields.TryGetProperty("countdownPreset", out var countdownPreset) &&
				countdownPreset.TryGetProperty("integerValue", out var countdownVal))
			{
				rosterDataBuilder.Append($"\"countdownPreset\":{countdownVal.GetString()},");
			}

			if (fields.TryGetProperty("teamAScore", out var teamAScore) &&
				teamAScore.TryGetProperty("integerValue", out var teamAVal))
			{
				rosterDataBuilder.Append($"\"teamAScore\":{teamAVal.GetString()},");
			}

			if (fields.TryGetProperty("teamBScore", out var teamBScore) &&
				teamBScore.TryGetProperty("integerValue", out var teamBVal))
			{
				rosterDataBuilder.Append($"\"teamBScore\":{teamBVal.GetString()},");
			}

			// Extract players array
			if (fields.TryGetProperty("players", out var players) &&
				players.TryGetProperty("arrayValue", out var playersArray) &&
				playersArray.TryGetProperty("values", out var values))
			{
				rosterDataBuilder.Append("\"players\":[");

				bool first = true;
				foreach (var player in values.EnumerateArray())
				{
					if (!first) rosterDataBuilder.Append(",");
					first = false;

					var simplePlayer = ConvertFirestorePlayerToJson(player);
					rosterDataBuilder.Append(simplePlayer);
				}

				rosterDataBuilder.Append("]");
			}
			else
			{
				rosterDataBuilder.Append("\"players\":[]");
			}

			rosterDataBuilder.Append("}");

			return rosterDataBuilder.ToString();
			} // end for loop
			return null;
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"[TeamDetails] DownloadRosterStatic error: {ex.Message}");
			return null;
		}
	}

	private static string ConvertFirestorePlayerToJson(System.Text.Json.JsonElement firestorePlayer)
	{
		var playerBuilder = new System.Text.StringBuilder();
		playerBuilder.Append("{");

		if (firestorePlayer.TryGetProperty("mapValue", out var mapValue) &&
			mapValue.TryGetProperty("fields", out var playerFields))
		{
			bool firstField = true;

			foreach (var field in playerFields.EnumerateObject())
			{
				if (!firstField) playerBuilder.Append(",");
				firstField = false;

				playerBuilder.Append($"\"{field.Name}\":");

				// Convert Firestore value to simple JSON value
				var value = field.Value;
				if (value.TryGetProperty("stringValue", out var stringVal))
				{
					playerBuilder.Append($"\"{stringVal.GetString()}\"");
				}
				else if (value.TryGetProperty("integerValue", out var intVal))
				{
					playerBuilder.Append(intVal.GetString());
				}
				else if (value.TryGetProperty("booleanValue", out var boolVal))
				{
					playerBuilder.Append(boolVal.GetBoolean() ? "true" : "false");
				}
				else if (value.TryGetProperty("doubleValue", out var doubleVal))
				{
					playerBuilder.Append(doubleVal.GetDouble());
				}
				else if (value.TryGetProperty("nullValue", out _))
				{
					playerBuilder.Append("null");
				}
				else
				{
					// Fallback: use empty string
					playerBuilder.Append("\"\"");
				}
			}
		}

		playerBuilder.Append("}");
		return playerBuilder.ToString();
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
			teamName = $"{club} - {team}";
			teamId = GenerateTeamId(club, team);
		}
		else
		{
			await DisplayAlert("Invalid Input", "Please enter either Club + Team or a Nickname.", "OK");
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
			var result = await CreateTeamInFirestore(teamId, teamName, inviteCode, adminCodeHash, creatorEmail);

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
							$"Invite Code (members): {inviteCode}\n\n" +
							$"⚠️ ADMIN RECOVERY CODE:\n{adminCode}\n\n" +
							"Save this admin code in a secure location outside this device (e.g. a password manager). " +
							"You will need it to regain admin access if you reinstall the app or change devices.\n\n" +
							"Next: Open the Game screen to name players, assign positions (field = swipe left, bench = swipe right, goalie = swipe left twice), and set timers." +
							emailNote,
							"OK");

					RefreshAppShellMenu();
					LoadCurrentTeam();

					// Clear inputs
					ClubEntry.Text = string.Empty;
					TeamEntry.Text = string.Empty;
					NicknameEntry.Text = string.Empty;
					CreatorEmailEntry.Text = string.Empty;
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

	#if ANDROID && DEBUG
private static readonly HttpClient _httpClient = new HttpClient(new Xamarin.Android.Net.AndroidMessageHandler
{
	ServerCertificateCustomValidationCallback = (_, _, _, _) => true
});
#elif ANDROID
private static readonly HttpClient _httpClient = new HttpClient(new Xamarin.Android.Net.AndroidMessageHandler());
#else
private static readonly HttpClient _httpClient = new HttpClient();
#endif
private static string? _firebaseIdToken;
private static string? _firebaseUserId;
private const string FirebaseApiKey = "AIzaSyDAKivCFX5kYYZ6SkAQluBNdR92I320glk";
private const string FirebaseProjectId = "turf-timer";

private async Task<bool> EnsureFirebaseAuthAsync()
{
if (!string.IsNullOrEmpty(_firebaseIdToken))
return true;

if (Connectivity.NetworkAccess != NetworkAccess.Internet)
{
System.Diagnostics.Debug.WriteLine("[Firebase] No internet connection - skipping auth");
return false;
}

try
{
System.Diagnostics.Debug.WriteLine("[Firebase] Signing in anonymously via REST API...");
var url = $"https://identitytoolkit.googleapis.com/v1/accounts:signUp?key={FirebaseApiKey}";
var body = System.Text.Json.JsonSerializer.Serialize(new { returnSecureToken = true });
var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
var response = await _httpClient.PostAsync(url, content);
var json = await response.Content.ReadAsStringAsync();
System.Diagnostics.Debug.WriteLine($"[Firebase] Auth response: {response.StatusCode}");
if (!response.IsSuccessStatusCode)
{
System.Diagnostics.Debug.WriteLine($"[Firebase] Auth failed: {json}");
return false;
}
var doc = System.Text.Json.JsonDocument.Parse(json);
_firebaseIdToken = doc.RootElement.GetProperty("idToken").GetString();
_firebaseUserId = doc.RootElement.GetProperty("localId").GetString();
System.Diagnostics.Debug.WriteLine($"[Firebase] Authenticated as user: {_firebaseUserId?.Substring(0, 8)}...");
FirebaseSaveBridge.SetAuthToken(_firebaseIdToken!, _firebaseUserId!);
return true;
}
catch (Exception ex)
{
System.Diagnostics.Debug.WriteLine($"[Firebase] Auth exception: {ex.Message}");
return false;
}
}

private static async Task<bool> EnsureFirebaseAuthStaticAsync()
{
if (!string.IsNullOrEmpty(_firebaseIdToken))
return true;

try
{
System.Diagnostics.Debug.WriteLine("[Firebase] Static: signing in anonymously...");
var url = $"https://identitytoolkit.googleapis.com/v1/accounts:signUp?key={FirebaseApiKey}";
var body = System.Text.Json.JsonSerializer.Serialize(new { returnSecureToken = true });
var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
var response = await _httpClient.PostAsync(url, content);
if (!response.IsSuccessStatusCode) return false;
var json = await response.Content.ReadAsStringAsync();
var doc = System.Text.Json.JsonDocument.Parse(json);
_firebaseIdToken = doc.RootElement.GetProperty("idToken").GetString();
_firebaseUserId = doc.RootElement.GetProperty("localId").GetString();
System.Diagnostics.Debug.WriteLine($"[Firebase] Static: authenticated");
FirebaseSaveBridge.SetAuthToken(_firebaseIdToken!, _firebaseUserId!);
return true;
}
catch (Exception ex)
{
System.Diagnostics.Debug.WriteLine($"[Firebase] Static auth exception: {ex.Message}");
return false;
}
}

private async Task<string> CreateTeamInFirestore(string teamId, string teamName, string inviteCode, string adminCodeHash, string creatorEmail)
{
System.Diagnostics.Debug.WriteLine($"[TeamDetails] CreateTeamInFirestore called for team: {teamName}");

if (!await EnsureFirebaseAuthAsync())
return "error: Could not authenticate with Firebase. Please check your internet connection.";

try
{
var baseUrl = $"https://firestore.googleapis.com/v1/projects/{FirebaseProjectId}/databases/(default)/documents";
_httpClient.DefaultRequestHeaders.Authorization =
new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _firebaseIdToken);

// 1. Create team metadata
var metadataUrl = $"{baseUrl}/teams/{teamId}/metadata?documentId=info";
var metadataBody = System.Text.Json.JsonSerializer.Serialize(new
{
fields = new
{
	teamName = new { stringValue = teamName },
	inviteCode = new { stringValue = inviteCode },
	adminCodeHash = new { stringValue = adminCodeHash },
	creatorEmail = new { stringValue = creatorEmail },
	createdBy = new { stringValue = _firebaseUserId },
	isActive = new { booleanValue = true }
}
});
var metadataResponse = await _httpClient.PostAsync(metadataUrl,
new StringContent(metadataBody, System.Text.Encoding.UTF8, "application/json"));
if (!metadataResponse.IsSuccessStatusCode)
{
var err = await metadataResponse.Content.ReadAsStringAsync();
System.Diagnostics.Debug.WriteLine($"[Firebase] Metadata write failed: {err}");
return $"error: {err}";
}
System.Diagnostics.Debug.WriteLine("[Firebase] Team metadata created");

// 2. Add creator as admin member
var memberUrl = $"{baseUrl}/teams/{teamId}/members?documentId={_firebaseUserId}";
var memberBody = System.Text.Json.JsonSerializer.Serialize(new
{
fields = new
{
role = new { stringValue = "admin" },
displayName = new { stringValue = "Admin" }
}
});
await _httpClient.PostAsync(memberUrl,
new StringContent(memberBody, System.Text.Encoding.UTF8, "application/json"));
System.Diagnostics.Debug.WriteLine("[Firebase] Admin member added");

// 3. Initialize empty roster
var rosterUrl = $"{baseUrl}/teams/{teamId}/roster?documentId=data";
var rosterBody = System.Text.Json.JsonSerializer.Serialize(new
{
fields = new
{
version = new { integerValue = "2" },
players = new { arrayValue = new { values = Array.Empty<object>() } }
}
});
await _httpClient.PostAsync(rosterUrl,
new StringContent(rosterBody, System.Text.Encoding.UTF8, "application/json"));
System.Diagnostics.Debug.WriteLine("[Firebase] Empty roster initialized");

// 4. Write invite code lookup document — enables O(1) GET lookup at join time
// (avoids a collectionGroup query which requires special Firestore security rules)
var inviteCodeLookupUrl = $"{baseUrl}/invite_codes?documentId={inviteCode}";
var inviteCodeLookupBody = System.Text.Json.JsonSerializer.Serialize(new
{
	fields = new
	{
		teamId = new { stringValue = teamId },
		teamName = new { stringValue = teamName },
		createdBy = new { stringValue = _firebaseUserId }
	}
});
var inviteCodeLookupResponse = await _httpClient.PostAsync(inviteCodeLookupUrl,
	new StringContent(inviteCodeLookupBody, System.Text.Encoding.UTF8, "application/json"));
if (inviteCodeLookupResponse.IsSuccessStatusCode)
	System.Diagnostics.Debug.WriteLine("[Firebase] Invite code lookup document created");
else
	System.Diagnostics.Debug.WriteLine($"[Firebase] Invite code lookup write failed (non-fatal): {await inviteCodeLookupResponse.Content.ReadAsStringAsync()}");

System.Diagnostics.Debug.WriteLine($"[TeamDetails] Team '{teamName}' created successfully in Firestore");
return "success";
}
catch (Exception ex)
{
System.Diagnostics.Debug.WriteLine($"[TeamDetails] Exception creating team: {ex.Message}");
return $"error: {ex.Message}";
}
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

					// ALSO store per-team keys for GamePage polling
					Preferences.Set($"team_mode_{teamId}", "shared");
					Preferences.Set($"user_role_{teamId}", "member");

					RegisterTeamId(teamId);

					await DisplayAlert("Joined Team!", 
							$"Successfully joined: {teamName}\n\n" +
							$"Role: Member\n\n" +
							"You can now collaborate with your team.", 
							"OK");

						SyncTeamIdToLocalStorage(teamId);
						RefreshAppShellMenu();
						LoadCurrentTeam();
						InviteCodeEntry.Text = string.Empty;
						return;
				}
			}
			else if (result.StartsWith("already_member:"))
			{
				var parts = result.Split(':', 3);
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
						await DisplayAlert("Missing Info", "Please enter both the Team ID and your Admin Recovery Code.", "OK");
						return;
					}

					RejoinAsAdminButton.IsEnabled = false;

					var result = await RejoinAsAdminInFirestore(teamId, adminCode);

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
						RegisterTeamId(restoredTeamId);

						await DisplayAlert("Admin Access Restored",
							$"You have rejoined '{restoredTeamName}' as Admin.\n\n" +
							"Your team data is intact in the cloud.",
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

				private async Task<string> JoinTeamInFirestore(string inviteCode)
			{
				System.Diagnostics.Debug.WriteLine($"[TeamDetails] JoinTeamInFirestore - invite code: {inviteCode}");

		if (!await EnsureFirebaseAuthAsync())
			return "error: Could not authenticate with Firebase. Please check your internet connection.";

		try
		{
			var baseUrl = $"https://firestore.googleapis.com/v1/projects/{FirebaseProjectId}/databases/(default)/documents";
			_httpClient.DefaultRequestHeaders.Authorization =
				new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _firebaseIdToken);

			// 1. Look up the invite code via the flat lookup document (O(1) GET, no collectionGroup query needed)
			var lookupUrl = $"{baseUrl}/invite_codes/{inviteCode.ToUpperInvariant()}";
			var lookupResponse = await _httpClient.GetAsync(lookupUrl);

			if (!lookupResponse.IsSuccessStatusCode)
			{
				System.Diagnostics.Debug.WriteLine($"[TeamDetails] Invite code not found: {inviteCode}");
				return $"error: Invite code '{inviteCode}' not found. Please check the code and try again.";
			}

			var lookupJson = await lookupResponse.Content.ReadAsStringAsync();
			System.Diagnostics.Debug.WriteLine($"[TeamDetails] Lookup response: {lookupJson}");

			// 2. Parse teamId and teamName from the lookup document
			string? teamId = null;
			string? teamName = null;

			using var lookupDoc = System.Text.Json.JsonDocument.Parse(lookupJson);
			if (lookupDoc.RootElement.TryGetProperty("fields", out var fields))
			{
				if (fields.TryGetProperty("teamId", out var tid) &&
					tid.TryGetProperty("stringValue", out var tidVal))
					teamId = tidVal.GetString();

				if (fields.TryGetProperty("teamName", out var tn) &&
					tn.TryGetProperty("stringValue", out var tnVal))
					teamName = tnVal.GetString();
			}

			if (string.IsNullOrEmpty(teamId))
				return $"error: Invite code '{inviteCode}' not found. Please check the code and try again.";

			System.Diagnostics.Debug.WriteLine($"[TeamDetails] Found team: {teamId} ({teamName})");

			// 3. Check if already a member
			var memberUrl = $"{baseUrl}/teams/{teamId}/members/{_firebaseUserId}";
			var memberCheck = await _httpClient.GetAsync(memberUrl);

			if (memberCheck.IsSuccessStatusCode)
			{
				System.Diagnostics.Debug.WriteLine($"[TeamDetails] Already a member of: {teamId}");
				return $"already_member:{teamId}:{teamName}";
			}

			// 4. Add self as member
			var addMemberUrl = $"{baseUrl}/teams/{teamId}/members?documentId={_firebaseUserId}";
			var addMemberBody = System.Text.Json.JsonSerializer.Serialize(new
			{
				fields = new
				{
					role = new { stringValue = "member" },
					joinedAt = new { timestampValue = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") },
					displayName = new { stringValue = "Member" }
				}
			});

			var addMemberResponse = await _httpClient.PostAsync(addMemberUrl,
				new StringContent(addMemberBody, System.Text.Encoding.UTF8, "application/json"));

			if (!addMemberResponse.IsSuccessStatusCode)
			{
				var err = await addMemberResponse.Content.ReadAsStringAsync();
				System.Diagnostics.Debug.WriteLine($"[TeamDetails] Add member failed: {err}");
				return $"error: Failed to join team: {err}";
			}

			System.Diagnostics.Debug.WriteLine($"[TeamDetails] ✅ Joined team: {teamId}");

			// Download roster data immediately after joining
			try
			{
				var rosterData = await DownloadRosterFromFirestore(teamId);
				if (rosterData != null)
				{
					// Store roster in Preferences so GamePage can load it
					Preferences.Set($"roster_{teamId}_json", rosterData);
					System.Diagnostics.Debug.WriteLine($"[TeamDetails] ✓ Downloaded and cached roster for {teamId}");
				}
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"[TeamDetails] ⚠️ Roster download failed: {ex.Message}");
				// Don't fail the join if roster download fails - user can still join
			}

			return $"success:{teamId}:{teamName}";
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"[TeamDetails] JoinTeamInFirestore error: {ex.Message}");
			return $"error: {ex.Message}";
		}
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
	/// Calls the 'requestAdminCodeEmail' Cloud Function which sends a recovery
	/// reminder email to the creator's registered address for the given team.
	/// Returns "success:teamName" or "error:message" or "not_found".
	/// </summary>
	private async Task<string> RequestAdminCodeEmailAsync(string teamId)
	{
		System.Diagnostics.Debug.WriteLine($"[TeamDetails] RequestAdminCodeEmailAsync - team: {teamId}");

		if (!await EnsureFirebaseAuthAsync())
			return "error: Could not authenticate. Please check your internet connection.";

		try
		{
			// Cloud Run callable functions are invoked via HTTPS POST
			// URL format: https://{region}-{projectId}.cloudfunctions.net/{functionName}
			var functionUrl = $"https://us-central1-{FirebaseProjectId}.cloudfunctions.net/requestAdminCodeEmail";

			_httpClient.DefaultRequestHeaders.Authorization =
				new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _firebaseIdToken);

			var payload = System.Text.Json.JsonSerializer.Serialize(new
			{
				data = new { teamId = teamId }
			});

			var response = await _httpClient.PostAsync(functionUrl,
				new StringContent(payload, System.Text.Encoding.UTF8, "application/json"));

			var responseBody = await response.Content.ReadAsStringAsync();
			System.Diagnostics.Debug.WriteLine($"[TeamDetails] Cloud Function response: {response.StatusCode} - {responseBody}");

			if (!response.IsSuccessStatusCode)
				return $"error: Server returned {(int)response.StatusCode}. Check the Team ID and try again.";

			using var doc = System.Text.Json.JsonDocument.Parse(responseBody);
			var result = doc.RootElement.GetProperty("result");

			var status = result.TryGetProperty("status", out var statusEl)
				? statusEl.GetString() : null;

			if (status == "not_found")
				return "not_found";

			var teamName = result.TryGetProperty("teamName", out var tnEl)
				? tnEl.GetString() ?? teamId : teamId;

			return $"success:{teamName}";
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"[TeamDetails] RequestAdminCodeEmailAsync error: {ex.Message}");
			return $"error: {ex.Message}";
		}
	}
	/// Returns "success:teamId:teamName" on success, or an "error:..." string on failure.
	/// </summary>
	private async Task<string> RejoinAsAdminInFirestore(string teamId, string adminCode)
	{
		System.Diagnostics.Debug.WriteLine($"[TeamDetails] RejoinAsAdminInFirestore - team: {teamId}");

		if (!await EnsureFirebaseAuthAsync())
			return "error: Could not authenticate with Firebase. Please check your internet connection.";

		try
		{
			var baseUrl = $"https://firestore.googleapis.com/v1/projects/{FirebaseProjectId}/databases/(default)/documents";
			_httpClient.DefaultRequestHeaders.Authorization =
				new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _firebaseIdToken);

			// 1. Fetch team metadata to get the stored hash
			var metadataUrl = $"{baseUrl}/teams/{teamId}/metadata/info";
			var metadataResponse = await _httpClient.GetAsync(metadataUrl);

			if (!metadataResponse.IsSuccessStatusCode)
				return $"error: Team '{teamId}' not found. Check the Team ID and try again.";

			var metadataJson = await metadataResponse.Content.ReadAsStringAsync();
			using var metadataDoc = System.Text.Json.JsonDocument.Parse(metadataJson);

			if (!metadataDoc.RootElement.TryGetProperty("fields", out var fields))
				return "error: Team metadata is invalid.";

			string? storedHash = null;
			string? teamName = null;

			if (fields.TryGetProperty("adminCodeHash", out var hashEl) &&
				hashEl.TryGetProperty("stringValue", out var hashVal))
				storedHash = hashVal.GetString();

			if (fields.TryGetProperty("teamName", out var tnEl) &&
				tnEl.TryGetProperty("stringValue", out var tnVal))
				teamName = tnVal.GetString();

			if (string.IsNullOrEmpty(storedHash))
				return "error: This team does not have an admin recovery code configured.";

			// 2. Verify code
			var suppliedHash = HashAdminCode(adminCode.Trim());
			if (!string.Equals(suppliedHash, storedHash, StringComparison.OrdinalIgnoreCase))
				return "error: Invalid admin code. Please check the code and try again.";

			// 3. Upsert member document with admin role
			var memberUrl = $"{baseUrl}/teams/{teamId}/members?documentId={_firebaseUserId}";
			var memberBody = System.Text.Json.JsonSerializer.Serialize(new
			{
				fields = new
				{
					role = new { stringValue = "admin" },
					displayName = new { stringValue = "Admin" },
					rejoinedAt = new { timestampValue = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") }
				}
			});
			var memberResponse = await _httpClient.PostAsync(memberUrl,
				new StringContent(memberBody, System.Text.Encoding.UTF8, "application/json"));

			if (!memberResponse.IsSuccessStatusCode)
			{
				var err = await memberResponse.Content.ReadAsStringAsync();
				return $"error: Failed to restore admin access: {err}";
			}

			System.Diagnostics.Debug.WriteLine($"[TeamDetails] ✅ Admin access restored for team: {teamId}");
			return $"success:{teamId}:{teamName}";
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"[TeamDetails] RejoinAsAdminInFirestore error: {ex.Message}");
			return $"error: {ex.Message}";
		}
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

		if (!confirm) return;

		var teamId = Preferences.Get(TEAM_ID_KEY, string.Empty);
		var oldCode = Preferences.Get($"{teamId}_invite_code", string.Empty);
		var newCode = GenerateInviteCode();

		// Save locally immediately
		Preferences.Set($"{teamId}_invite_code", newCode);
		LoadInviteCode();

		// Sync to Firestore in the background (non-blocking)
		_ = Task.Run(async () =>
		{
			try
			{
				if (!await EnsureFirebaseAuthAsync()) return;

				var baseUrl = $"https://firestore.googleapis.com/v1/projects/{FirebaseProjectId}/databases/(default)/documents";
				_httpClient.DefaultRequestHeaders.Authorization =
					new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _firebaseIdToken);

				// 1. Update the team metadata document with the new invite code
				var metadataUrl = $"{baseUrl}/teams/{teamId}/metadata/info?updateMask.fieldPaths=inviteCode";
				var metadataBody = System.Text.Json.JsonSerializer.Serialize(new
				{
					fields = new { inviteCode = new { stringValue = newCode } }
				});
				await _httpClient.PatchAsync(metadataUrl,
					new StringContent(metadataBody, System.Text.Encoding.UTF8, "application/json"));
				System.Diagnostics.Debug.WriteLine("[Firebase] Team metadata updated with new invite code");

				// 2. Delete the old invite_codes lookup document (best-effort)
				if (!string.IsNullOrEmpty(oldCode))
				{
					await _httpClient.DeleteAsync($"{baseUrl}/invite_codes/{oldCode}");
					System.Diagnostics.Debug.WriteLine($"[Firebase] Old invite code lookup deleted: {oldCode}");
				}

				// 3. Create the new invite_codes lookup document
				var inviteCodeLookupUrl = $"{baseUrl}/invite_codes?documentId={newCode}";
				var teamName = Preferences.Get(TEAM_NAME_KEY, string.Empty);
				var lookupBody = System.Text.Json.JsonSerializer.Serialize(new
				{
					fields = new
					{
						teamId = new { stringValue = teamId },
						teamName = new { stringValue = teamName },
						createdBy = new { stringValue = _firebaseUserId }
					}
				});
				await _httpClient.PostAsync(inviteCodeLookupUrl,
					new StringContent(lookupBody, System.Text.Encoding.UTF8, "application/json"));
				System.Diagnostics.Debug.WriteLine($"[Firebase] New invite code lookup created: {newCode}");
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
					players.Add(new Player { Name = $"Player {i}", Position = PlayerPosition.None });
				}
			}

			var teamData = QrCodeService.CreateFromCurrentTeam(teamName, teamId, players);
			var modal = new QrShareModal(teamData);
			await Navigation.PushModalAsync(modal);
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"[TeamDetails] OnShareTeamClicked error: {ex.Message}");
			await DisplayAlert("Error", $"Failed to share team: {ex.Message}", "OK");
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
			RefreshAppShellMenu();
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
