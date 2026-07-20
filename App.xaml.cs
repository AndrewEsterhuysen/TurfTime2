using System.Text.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TurfTime2.Models;
using TurfTime2.Services;

namespace TurfTime2
{
    public partial class App : Application
    {
        private const string TEAM_MODE_KEY = "team_mode";
        private const string TEAM_ID_KEY = "team_id";
        private const string TEAM_NAME_KEY = "team_name";
        private const string USER_ROLE_KEY = "user_role";
        private const string UPDATES_SEEN_MARKER_KEY = "updates_seen_marker";
        private const string DEMO_TEAM_SEEDED_KEY = "demo_team_seeded_v1";
        private const string DEMO_TEAM_ID = "local_demo_team";
        private const string DEMO_TEAM_NAME = "Demo Team";
        private const int DEMO_ROTATION_SECONDS = 20;
        private static int _importCounter;

        /// <summary>Raised when the app is backgrounded (process may be suspended).</summary>
        public static event EventHandler? Sleeping;

        /// <summary>Raised when the app returns to the foreground after sleep.</summary>
        public static event EventHandler? Resumed;

        public App()
        {
            InitializeComponent();

            // Initialize FCM
            _ = InitializeFcmAsync();
        }

        protected override void OnSleep()
        {
            base.OnSleep();
            Sleeping?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnResume()
        {
            base.OnResume();
            Resumed?.Invoke(this, EventArgs.Empty);
#if IOS
            // Keep banner presentation delegate installed (Plugin.Firebase can overwrite it).
            FcmService.InstallIosNotificationDelegate();
#endif
            // Re-save FCM token after resume (token/permission may have changed; team may have been joined).
            _ = FcmService.Instance.EnsureRegisteredForCurrentTeamAsync();
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            var appShell = new AppShell();
            var window = new Window(appShell);

            appShell.Loaded += async (s, e) =>
            {
                await EnsureDemoTeamOnFirstRunAsync();

                // Check if a team was previously selected (after demo bootstrap).
                var teamMode = Preferences.Get(TEAM_MODE_KEY, string.Empty);
                var teamId = Preferences.Get(TEAM_ID_KEY, string.Empty);

                var showWelcome = !Preferences.Get("welcome_dont_show", false);
                var currentMarker = $"{AppInfo.VersionString}:{AppInfo.BuildString}";
                var seenMarker = Preferences.Get(UPDATES_SEEN_MARKER_KEY, string.Empty);
                var showUpdates = !string.Equals(currentMarker, seenMarker, StringComparison.Ordinal);

                // Show updates first (once per install/update), then welcome.
                if (showUpdates)
                {
                    await appShell.Navigation.PushModalAsync(new UpdatesPage(showWelcomeAfterClose: showWelcome), animated: true);
                }
                else if (showWelcome)
                {
                    await appShell.Navigation.PushModalAsync(new WelcomePage(), animated: true);
                }

                if (string.IsNullOrEmpty(teamMode) || string.IsNullOrEmpty(teamId))
                {
                    // No team selected — pre-navigate to Team Details so it's ready when welcome closes
                    System.Diagnostics.Debug.WriteLine("[App] No team selected - will navigate to Team Details after welcome");
                    await Shell.Current.GoToAsync("//SettingsPage/settings/teamdetails");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[App] Team selected: {teamId} - starting at Game page");
                }
            };

            return window;
        }

        private static Task EnsureDemoTeamOnFirstRunAsync()
        {
            if (Preferences.Get(DEMO_TEAM_SEEDED_KEY, false))
            {
                EnsureDemoTeamCountdownPreset();
                return Task.CompletedTask;
            }

            var existingTeamMode = Preferences.Get(TEAM_MODE_KEY, string.Empty);
            var existingTeamId = Preferences.Get(TEAM_ID_KEY, string.Empty);
            if (!string.IsNullOrWhiteSpace(existingTeamMode) && !string.IsNullOrWhiteSpace(existingTeamId))
            {
                Preferences.Set(DEMO_TEAM_SEEDED_KEY, true);
                EnsureDemoTeamCountdownPreset();
                return Task.CompletedTask;
            }

            Preferences.Set($"{DEMO_TEAM_ID}_name", DEMO_TEAM_NAME);
            RegisterLocalTeamId(DEMO_TEAM_ID);

            Preferences.Set(TEAM_MODE_KEY, "local");
            Preferences.Set(TEAM_ID_KEY, DEMO_TEAM_ID);
            Preferences.Set(TEAM_NAME_KEY, DEMO_TEAM_NAME);
            Preferences.Set(USER_ROLE_KEY, "admin");

            Preferences.Set($"setup_team_{DEMO_TEAM_ID}", DEMO_TEAM_NAME);

            var snapshot = BuildDemoRosterSnapshot();
            Preferences.Set($"roster_snapshot_{DEMO_TEAM_ID}", JsonSerializer.Serialize(snapshot));

            Preferences.Set(DEMO_TEAM_SEEDED_KEY, true);
            EnsureDemoTeamCountdownPreset();
            System.Diagnostics.Debug.WriteLine("[App] ✅ Demo team seeded for first launch.");

            return Task.CompletedTask;
        }

        private static void EnsureDemoTeamCountdownPreset()
        {
            try
            {
                var snapshotKey = $"roster_snapshot_{DEMO_TEAM_ID}";
                var snapshotRaw = Preferences.Get(snapshotKey, string.Empty);
                if (!string.IsNullOrWhiteSpace(snapshotRaw))
                {
                    var snapshot = JsonSerializer.Deserialize<RosterSnapshot>(snapshotRaw);
                    if (snapshot is not null && snapshot.CountdownPresetSeconds != DEMO_ROTATION_SECONDS)
                    {
                        snapshot.CountdownPresetSeconds = DEMO_ROTATION_SECONDS;
                        snapshot.LastModifiedUtc = DateTimeOffset.UtcNow;
                        Preferences.Set(snapshotKey, JsonSerializer.Serialize(snapshot));
                    }
                }

                if (string.Equals(Preferences.Get(TEAM_ID_KEY, string.Empty), DEMO_TEAM_ID, StringComparison.Ordinal))
                {
                    Preferences.Set("game.countdownPresetSeconds", DEMO_ROTATION_SECONDS);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] Failed to enforce demo countdown preset: {ex.Message}");
            }
        }

        private static RosterSnapshot BuildDemoRosterSnapshot()
        {
            var players = Enumerable.Range(1, 16)
                .Select(i => new PlayerSnapshot
                {
                    SlotId = i,
                    Name = $"Player {i}",
                    Field = i is >= 1 and <= 5,
                    Goalie = i == 6,
                    Bench = i is >= 7 and <= 9,
                    Inactive = false,
                    CounterSeconds = 0
                })
                .ToList();

            return new RosterSnapshot
            {
                LastModifiedUtc = DateTimeOffset.UtcNow,
                MatchDurationSeconds = 120,
                HalfDurationSeconds = 60,
                MatchRemainingSeconds = 120,
                CurrentHalf = "setup",
                TimerRunning = false,
                CountdownPresetSeconds = DEMO_ROTATION_SECONDS,
                ViewMode = 0,
                TeamAScore = 0,
                TeamBScore = 0,
                Players = players
            };
        }

        private static void RegisterLocalTeamId(string teamId)
        {
            var teamListJson = Preferences.Get("local_team_id_list", "[]");
            try
            {
                var teamIds = JsonSerializer.Deserialize<List<string>>(teamListJson) ?? [];
                if (!teamIds.Contains(teamId))
                {
                    teamIds.Add(teamId);
                    Preferences.Set("local_team_id_list", JsonSerializer.Serialize(teamIds));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] Failed to register local team id: {ex.Message}");
            }
        }

        private async Task InitializeFcmAsync()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("[App] Starting delayed FCM initialization (2s)...");

                // Wait a bit for app to fully initialize (native Firebase + plugin must be ready)
                await Task.Delay(2000);

                var success = await FcmService.Instance.InitializeAsync();

                if (success)
                {
                    // Update token in Firestore (uses the same REST + anonymous auth pattern that works on Android)
                    await FcmService.Instance.UpdateTokenInFirestoreAsync();
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[App] ⚠️ FcmService.InitializeAsync returned false — push notifications may not work.");
                }
            }
            catch (Exception ex)
            {
                // Full details so it appears in crash logs / Console.app even if this is fire-and-forget
                System.Diagnostics.Debug.WriteLine($"[App] ❌ FCM initialization error: {ex.GetType().FullName}: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[App] Stack: {ex.StackTrace}");
                if (ex.InnerException != null)
                    System.Diagnostics.Debug.WriteLine($"[App] Inner: {ex.InnerException}");
            }
        }

        protected override void OnAppLinkRequestReceived(Uri uri)
        {
            base.OnAppLinkRequestReceived(uri);

            _ = MainThread.InvokeOnMainThreadAsync(async () =>
            {
                try
                {
                    if (!QrCodeService.TryParseTeamShareData(uri.ToString(), out var teamData, out var parseError) || teamData is null)
                    {
                        System.Diagnostics.Debug.WriteLine($"[App] Import payload parse failed: {parseError}");
                        await ShowAlertAsync("Invalid QR Link", "This QR code doesn't contain valid Turf Time team data.");
                        return;
                    }

                    var importedTeamId = QrCodeService.ImportTeamToLocal(teamData);
                    await ShowAlertAsync("Team Imported", $"Imported '{teamData.TeamName}' and switched to that team.");

                    if (Shell.Current is not null)
                    {
                        await Shell.Current.GoToAsync(AppShell.TeamDetailsRoute);
                    }

                    System.Diagnostics.Debug.WriteLine($"[App] ✅ Imported shared team via app link. TeamId={importedTeamId}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[App] OnAppLinkRequestReceived error: {ex.GetType().FullName}: {ex.Message}");
                    await ShowAlertAsync("Import Failed", "Could not import team from link.");
                }
            });
        }

        private static bool TryExtractImportPayload(Uri uri, out string payload)
        {
            payload = string.Empty;
            var query = uri.Query;
            if (string.IsNullOrWhiteSpace(query))
            {
                return false;
            }

            if (query.StartsWith('?'))
            {
                query = query[1..];
            }

            foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var idx = pair.IndexOf('=');
                if (idx <= 0 || idx == pair.Length - 1)
                {
                    continue;
                }

                var key = Uri.UnescapeDataString(pair[..idx]);
                if (!key.Equals("import", StringComparison.OrdinalIgnoreCase) &&
                    !key.Equals("team", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                payload = Uri.UnescapeDataString(pair[(idx + 1)..]).Trim();
                return !string.IsNullOrWhiteSpace(payload);
            }

            return false;
        }

        private static bool TryDecodeTeamShareData(string payload, out TeamShareData? teamData, out string error)
        {
            teamData = null;
            error = string.Empty;
            try
            {
                var base64 = payload.Replace('-', '+').Replace('_', '/');
                switch (base64.Length % 4)
                {
                    case 2:
                        base64 += "==";
                        break;
                    case 3:
                        base64 += "=";
                        break;
                }

                var bytes = Convert.FromBase64String(base64);
                var json = Encoding.UTF8.GetString(bytes);
                teamData = JsonSerializer.Deserialize<TeamShareData>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (teamData is null || string.IsNullOrWhiteSpace(teamData.TeamName) || teamData.Players.Count == 0)
                {
                    error = "missing fields";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static string ImportSharedTeam(TeamShareData teamData)
        {
            var teamId = BuildUniqueImportedTeamId(teamData.TeamId);
            var teamName = string.IsNullOrWhiteSpace(teamData.TeamName) ? "Imported Team" : teamData.TeamName.Trim();

            var players = new List<Player>();
            var snapshots = new List<PlayerSnapshot>();

            int slot = 1;
            foreach (var sharePlayer in teamData.Players)
            {
                var playerName = string.IsNullOrWhiteSpace(sharePlayer.Name) ? $"Player {slot}" : sharePlayer.Name.Trim();
                var position = ParsePosition(sharePlayer.Position);

                players.Add(new Player
                {
                    SlotId = slot,
                    Name = playerName,
                    Position = position
                });

                snapshots.Add(new PlayerSnapshot
                {
                    SlotId = slot,
                    Name = playerName,
                    Field = position == PlayerPosition.Field,
                    Bench = position == PlayerPosition.Bench,
                    Goalie = position == PlayerPosition.Goalie,
                    Inactive = position == PlayerPosition.Inactive,
                    CounterSeconds = 0
                });

                slot++;
            }

            while (players.Count < 16)
            {
                var fillerSlot = players.Count + 1;
                players.Add(new Player { SlotId = fillerSlot, Name = $"Player {fillerSlot}", Position = PlayerPosition.None });
                snapshots.Add(new PlayerSnapshot { SlotId = fillerSlot, Name = $"Player {fillerSlot}" });
            }

            var snapshot = new RosterSnapshot
            {
                LastModifiedUtc = DateTimeOffset.UtcNow,
                MatchDurationSeconds = 90 * 60,
                HalfDurationSeconds = 45 * 60,
                MatchRemainingSeconds = 90 * 60,
                CurrentHalf = "setup",
                TimerRunning = false,
                CountdownPresetSeconds = 2 * 60,
                TeamAScore = 0,
                TeamBScore = 0,
                Players = snapshots
            };

            Preferences.Set($"{teamId}_name", teamName);
            Preferences.Set($"{teamId}_players", JsonSerializer.Serialize(players));
            Preferences.Set($"roster_snapshot_{teamId}", JsonSerializer.Serialize(snapshot));
            Preferences.Set($"setup_team_{teamId}", teamName);

            RegisterLocalTeamId(teamId);

            Preferences.Set(TEAM_MODE_KEY, "local");
            Preferences.Set(TEAM_ID_KEY, teamId);
            Preferences.Set(TEAM_NAME_KEY, teamName);
            Preferences.Set(USER_ROLE_KEY, "admin");

            return teamId;
        }

        private static PlayerPosition ParsePosition(string? position)
        {
            if (Enum.TryParse<PlayerPosition>(position, true, out var parsed))
            {
                return parsed;
            }

            return PlayerPosition.None;
        }

        private static string BuildUniqueImportedTeamId(string? sourceTeamId)
        {
            var normalizedSource = string.IsNullOrWhiteSpace(sourceTeamId)
                ? "imported"
                : new string(sourceTeamId.Trim().Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-').ToArray());

            if (string.IsNullOrWhiteSpace(normalizedSource))
            {
                normalizedSource = "imported";
            }

            var baseId = normalizedSource.StartsWith("local_", StringComparison.Ordinal)
                ? normalizedSource
                : $"local_{normalizedSource}";

            var localIdsJson = Preferences.Get("local_team_id_list", "[]");
            List<string> localIds;
            try
            {
                localIds = JsonSerializer.Deserialize<List<string>>(localIdsJson) ?? [];
            }
            catch
            {
                localIds = [];
            }

            if (!localIds.Contains(baseId, StringComparer.Ordinal))
            {
                return baseId;
            }

            var unique = $"{baseId}_{Interlocked.Increment(ref _importCounter):D2}";
            while (localIds.Contains(unique, StringComparer.Ordinal))
            {
                unique = $"{baseId}_{Interlocked.Increment(ref _importCounter):D2}";
            }

            return unique;
        }

        private static Task ShowAlertAsync(string title, string message)
        {
            var page = Application.Current?.Windows.FirstOrDefault()?.Page
                       ?? Shell.Current?.CurrentPage;
            if (page is null)
            {
                System.Diagnostics.Debug.WriteLine($"[App] {title}: {message}");
                return Task.CompletedTask;
            }

            return page.DisplayAlert(title, message, "OK");
        }
    }
}
