using System.Text.Json;
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

        public App()
        {
            InitializeComponent();

            // Initialize FCM
            _ = InitializeFcmAsync();
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
    }
}
