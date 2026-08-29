using System.Text.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TurfTime2.Helpers;
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
        private const string DEMO_TEAM_SEEDED_KEY = "demo_team_seeded_v1";
        private const string DEMO_TEAM_ID = "local_demo_team";
        private const string DEMO_TEAM_NAME = "Demo Team";
        private const int DEMO_ROTATION_SECONDS = 20;
        private static int _importCounter;

        /// <summary>Deep link waiting for Shell / first page (cold start from Messages etc.).</summary>
        private static Uri? _pendingDeepLink;
        private static readonly object PendingDeepLinkLock = new();
        private static string? _lastHandledDeepLink;
        private static DateTime _lastHandledDeepLinkUtc = DateTime.MinValue;

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

        /// <summary>
        /// Queue a deep link before <see cref="Application.Current"/> is ready
        /// (platform open can race app construction).
        /// </summary>
        public static void EnqueuePendingDeepLink(Uri uri)
        {
            if (uri is null) return;
            lock (PendingDeepLinkLock)
                _pendingDeepLink = uri;
            System.Diagnostics.Debug.WriteLine($"[App] Enqueued pending deep link: {uri}");
        }

        /// <summary>
        /// Entry point for platform code (iOS OpenUrl / Android VIEW intent) and MAUI app links.
        /// </summary>
        public void HandleIncomingDeepLink(Uri uri)
        {
            if (uri is null) return;
            System.Diagnostics.Debug.WriteLine($"[App] HandleIncomingDeepLink: {uri}");
            EnqueuePendingDeepLink(uri);
            _ = ProcessPendingDeepLinkAsync();
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
#if IOS
            // LocalNotification / Plugin.Firebase may overwrite the notification center delegate.
            FcmService.InstallIosNotificationDelegate();
#endif
            // Refresh shared match schedule watch after suspend (also reschedules reminders).
            _ = EnsureMatchScheduleSyncAsync();
            _ = RescheduleMatchRemindersAsync();
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            var appShell = new AppShell();
            var window = new Window(appShell);

            appShell.Loaded += async (s, e) =>
            {
                await EnsureDemoTeamOnFirstRunAsync();

                // Require a display name before Welcome / deep-link join so Chat and
                // shared-team join always have a name ready (first launch or cleared prefs).
                await EnsureDisplayNameOnLaunchAsync();

                // Check if a team was previously selected (after demo bootstrap).
                var teamMode = Preferences.Get(TEAM_MODE_KEY, string.Empty);
                var teamId = Preferences.Get(TEAM_ID_KEY, string.Empty);

                // Shared-team match schedule: load + live watch (fail soft).
                _ = EnsureMatchScheduleSyncAsync();

                // If we launched from a deep link, skip Welcome so join/import alerts can show.
                var hasPendingLink = false;
                lock (PendingDeepLinkLock)
                    hasPendingLink = _pendingDeepLink is not null;

                // First-run / optional welcome modal (user can opt out permanently).
                if (!hasPendingLink && !Preferences.Get("welcome_dont_show", false))
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

                // Cold-start deep links (Messages → turf://…) often arrive before Shell is ready.
                await ProcessPendingDeepLinkAsync();
            };

            return window;
        }

        /// <summary>
        /// Blocks until the user has a valid display name (Preferences <c>user_name</c>).
        /// Shown as a small prompt on first launch (and whenever the name is missing).
        /// </summary>
        private static async Task EnsureDisplayNameOnLaunchAsync()
        {
            if (UserDisplayName.TryValidate(UserDisplayName.Get(), out _, out _))
                return;

            await WaitForUiReadyAsync();

            var page = Current?.Windows.FirstOrDefault()?.Page
                       ?? Shell.Current?.CurrentPage
                       ?? Shell.Current;
            if (page is null)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[App] EnsureDisplayNameOnLaunch: no page yet — will not block startup");
                return;
            }

            // Loop until a valid personal name is saved (required for Chat / shared join).
            while (!UserDisplayName.TryValidate(UserDisplayName.Get(), out _, out _))
            {
                string? entered;
                try
                {
                    // cancel: null → platform may still show a dismiss control; treat dismiss as retry.
                    entered = await page.DisplayPromptAsync(
                        title: "Your name",
                        message:
                            "Before you start, enter the name teammates will see in Chat and when you join a shared team " +
                            $"(at least {UserDisplayName.MinLength} characters). You can change it later under Team Details.",
                        accept: "Continue",
                        cancel: null,
                        placeholder: "e.g. Alex or Coach Sam",
                        maxLength: UserDisplayName.MaxLength,
                        keyboard: Keyboard.Text,
                        initialValue: UserDisplayName.Get());
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[App] Display name prompt failed: {ex.Message}");
                    return;
                }

                if (entered is null)
                {
                    await page.DisplayAlert(
                        "Name required",
                        "A display name is required so teammates know who you are when you join or chat.",
                        "OK");
                    continue;
                }

                if (!UserDisplayName.TryValidate(entered, out var normalized, out var error))
                {
                    await page.DisplayAlert(
                        "Name required",
                        string.IsNullOrWhiteSpace(error)
                            ? $"Enter a name of at least {UserDisplayName.MinLength} characters."
                            : error,
                        "OK");
                    continue;
                }

                UserDisplayName.Set(normalized);
                System.Diagnostics.Debug.WriteLine(
                    $"[App] ✅ Display name set on launch: {normalized}");
                return;
            }
        }

        private static Task EnsureDemoTeamOnFirstRunAsync()
        {
            if (Preferences.Get(DEMO_TEAM_SEEDED_KEY, false))
            {
                EnsureDemoTeamDefaults();
                return Task.CompletedTask;
            }

            var existingTeamMode = Preferences.Get(TEAM_MODE_KEY, string.Empty);
            var existingTeamId = Preferences.Get(TEAM_ID_KEY, string.Empty);
            if (!string.IsNullOrWhiteSpace(existingTeamMode) && !string.IsNullOrWhiteSpace(existingTeamId))
            {
                Preferences.Set(DEMO_TEAM_SEEDED_KEY, true);
                EnsureDemoTeamDefaults();
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
            EnsureDemoTeamDefaults();
            System.Diagnostics.Debug.WriteLine("[App] ✅ Demo team seeded for first launch.");

            return Task.CompletedTask;
        }

        /// <summary>
        /// Default Demo Team outfield formation: SlotIds 1–5 → FieldCells 1, 4, 5, 8, 10.
        /// Slot 6 remains Goalie (no outfield cell).
        /// </summary>
        private static readonly IReadOnlyDictionary<int, int> DemoFieldCellBySlot =
            new Dictionary<int, int>
            {
                [1] = 1,
                [2] = 4,
                [3] = 5,
                [4] = 8,
                [5] = 10
            };

        /// <summary>Idempotent demo-team migrations (countdown, names, field cells, Absent).</summary>
        private static void EnsureDemoTeamDefaults()
        {
            EnsureDemoTeamCountdownPreset();
            EnsureDemoTeamPlayerNames();
            EnsureDemoTeamFieldCells();
            EnsureDemoTeamAbsentPlayers();
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

        /// <summary>
        /// Rewrites legacy demo defaults ("Player 1") to <see cref="Player.DefaultName"/> ("#01 Player").
        /// Custom renamed players are left alone.
        /// </summary>
        private static void EnsureDemoTeamPlayerNames()
        {
            try
            {
                var snapshotKey = $"roster_snapshot_{DEMO_TEAM_ID}";
                var snapshotRaw = Preferences.Get(snapshotKey, string.Empty);
                if (string.IsNullOrWhiteSpace(snapshotRaw)) return;

                var snapshot = JsonSerializer.Deserialize<RosterSnapshot>(snapshotRaw);
                if (snapshot?.Players is null || snapshot.Players.Count == 0) return;

                var changed = false;
                foreach (var ps in snapshot.Players)
                {
                    if (ps.SlotId is < 1 or > 16) continue;
                    if (!IsLegacyDefaultPlayerName(ps.Name, ps.SlotId)) continue;

                    ps.Name = Player.DefaultName(ps.SlotId);
                    changed = true;
                }

                if (!changed) return;

                snapshot.LastModifiedUtc = DateTimeOffset.UtcNow;
                Preferences.Set(snapshotKey, JsonSerializer.Serialize(snapshot));
                System.Diagnostics.Debug.WriteLine("[App] ✅ Demo team player names migrated to #NN Player.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] Failed to migrate demo player names: {ex.Message}");
            }
        }

        /// <summary>True for empty names or legacy "Player N" / "Player 0N" defaults.</summary>
        private static bool IsLegacyDefaultPlayerName(string? name, int slotId)
        {
            if (string.IsNullOrWhiteSpace(name)) return true;
            var trimmed = name.Trim();
            if (string.Equals(trimmed, $"Player {slotId}", StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(trimmed, $"Player {slotId:D2}", StringComparison.OrdinalIgnoreCase))
                return true;
            // Already on the new convention.
            if (string.Equals(trimmed, Player.DefaultName(slotId), StringComparison.Ordinal))
                return false;
            return false;
        }

        /// <summary>
        /// Backfills missing outfield grid cells on Demo Team so Field View shows tokens.
        /// Only writes when <see cref="PlayerSnapshot.Field"/> is true and <c>FieldCell</c> is unset (0);
        /// leaves Goalie and already-placed cells alone.
        /// </summary>
        private static void EnsureDemoTeamFieldCells()
        {
            try
            {
                var snapshotKey = $"roster_snapshot_{DEMO_TEAM_ID}";
                var snapshotRaw = Preferences.Get(snapshotKey, string.Empty);
                if (string.IsNullOrWhiteSpace(snapshotRaw)) return;

                var snapshot = JsonSerializer.Deserialize<RosterSnapshot>(snapshotRaw);
                if (snapshot?.Players is null || snapshot.Players.Count == 0) return;

                var changed = false;
                foreach (var ps in snapshot.Players)
                {
                    if (!ps.Field || ps.Goalie) continue;
                    if (FieldGrid.Normalize(ps.FieldCell) is not null) continue;
                    if (!DemoFieldCellBySlot.TryGetValue(ps.SlotId, out var cell)) continue;

                    ps.FieldCell = cell;
                    changed = true;
                }

                if (!changed) return;

                snapshot.LastModifiedUtc = DateTimeOffset.UtcNow;
                Preferences.Set(snapshotKey, JsonSerializer.Serialize(snapshot));
                System.Diagnostics.Debug.WriteLine("[App] ✅ Demo team field cells assigned (1,4,5,8,10).");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] Failed to migrate demo field cells: {ex.Message}");
            }
        }

        /// <summary>
        /// Demo slots 10–16 should be Absent (Bench is only 7–9). Older seeds left role flags
        /// unset → SnapshotPosition maps to Bench. A device-local start configuration can also
        /// re-apply Bench after ApplySnapshot — repair both the roster snapshot and that layout.
        /// </summary>
        private static void EnsureDemoTeamAbsentPlayers()
        {
            try
            {
                var snapshotKey = $"roster_snapshot_{DEMO_TEAM_ID}";
                var snapshotRaw = Preferences.Get(snapshotKey, string.Empty);
                if (!string.IsNullOrWhiteSpace(snapshotRaw))
                {
                    var snapshot = JsonSerializer.Deserialize<RosterSnapshot>(snapshotRaw);
                    if (snapshot?.Players is { Count: > 0 })
                    {
                        var changed = false;
                        foreach (var ps in snapshot.Players)
                        {
                            if (ps.SlotId is < 10 or > 16) continue;
                            // Leave anyone the user moved onto Field/Goalie alone.
                            if (ps.Field || ps.Goalie) continue;
                            if (ps.Inactive && !ps.Bench)
                                continue;

                            ps.Inactive = true;
                            ps.Bench = false;
                            ps.Field = false;
                            ps.Goalie = false;
                            ps.FieldCell = 0;
                            changed = true;
                        }

                        if (changed)
                        {
                            snapshot.LastModifiedUtc = DateTimeOffset.UtcNow;
                            Preferences.Set(snapshotKey, JsonSerializer.Serialize(snapshot));
                            System.Diagnostics.Debug.WriteLine(
                                "[App] ✅ Demo team slots 10–16 migrated to Absent (roster).");
                        }
                    }
                }

                // Start config is restored after ApplySnapshot and was still parking 10–16 on Bench.
                RepairDemoTeamStartConfiguration();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] Failed to migrate demo Absent players: {ex.Message}");
            }
        }

        /// <summary>
        /// Rewrites Demo Team start-configuration rows so slots 10–16 are Absent (Inactive).
        /// Clears the key if the payload is corrupt.
        /// </summary>
        private static void RepairDemoTeamStartConfiguration()
        {
            const string startConfigKey = "team_start_configuration_v1_" + DEMO_TEAM_ID;
            try
            {
                var raw = Preferences.Get(startConfigKey, string.Empty);
                if (string.IsNullOrWhiteSpace(raw))
                    return;

                using var doc = JsonDocument.Parse(raw);
                if (!doc.RootElement.TryGetProperty("Rows", out var rowsEl) &&
                    !doc.RootElement.TryGetProperty("rows", out rowsEl))
                {
                    Preferences.Remove(startConfigKey);
                    return;
                }

                var rows = new List<Dictionary<string, int>>();
                var changed = false;
                foreach (var row in rowsEl.EnumerateArray())
                {
                    var slotId = row.TryGetProperty("SlotId", out var s) ? s.GetInt32()
                        : row.TryGetProperty("slotId", out var s2) ? s2.GetInt32() : 0;
                    var position = row.TryGetProperty("Position", out var p) ? p.GetInt32()
                        : row.TryGetProperty("position", out var p2) ? p2.GetInt32() : (int)PlayerPosition.Bench;
                    var fieldCell = row.TryGetProperty("FieldCell", out var f) ? f.GetInt32()
                        : row.TryGetProperty("fieldCell", out var f2) ? f2.GetInt32() : 0;

                    if (slotId is >= 10 and <= 16 && position != (int)PlayerPosition.Inactive)
                    {
                        position = (int)PlayerPosition.Inactive;
                        fieldCell = 0;
                        changed = true;
                    }

                    rows.Add(new Dictionary<string, int>
                    {
                        ["SlotId"] = slotId,
                        ["Position"] = position,
                        ["FieldCell"] = fieldCell
                    });
                }

                if (!changed)
                    return;

                var payload = JsonSerializer.Serialize(new { Rows = rows });
                Preferences.Set(startConfigKey, payload);
                System.Diagnostics.Debug.WriteLine(
                    "[App] ✅ Demo team start configuration: slots 10–16 → Absent.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[App] Demo start config repair failed ({ex.Message}); clearing key.");
                Preferences.Remove(startConfigKey);
            }
        }

        private static RosterSnapshot BuildDemoRosterSnapshot()
        {
            var players = Enumerable.Range(1, 16)
                .Select(i => new PlayerSnapshot
                {
                    SlotId = i,
                    Name = Player.DefaultName(i),
                    Field = i is >= 1 and <= 5,
                    Goalie = i == 6,
                    Bench = i is >= 7 and <= 9,
                    // Slots 10–16 start Absent (not Bench). Unset flags would otherwise
                    // map to Bench via SnapshotPosition after the Stack → Absent change.
                    Inactive = i is >= 10 and <= 16,
                    CounterSeconds = 0,
                    FieldCell = DemoFieldCellBySlot.TryGetValue(i, out var cell) ? cell : 0
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

#if IOS
                // After LocalNotification + FCM both touch the notification center, keep FCM last
                // so chat foreground banners still work.
                FcmService.InstallIosNotificationDelegate();
#endif
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
            System.Diagnostics.Debug.WriteLine($"[App] OnAppLinkRequestReceived: {uri}");
            HandleIncomingDeepLink(uri);
        }

        private async Task ProcessPendingDeepLinkAsync()
        {
            Uri? uri;
            lock (PendingDeepLinkLock)
            {
                uri = _pendingDeepLink;
                _pendingDeepLink = null;
            }

            if (uri is null)
                return;

            // Deduplicate: platform OpenUrl + MAUI OnAppLinkRequestReceived often both fire.
            var key = uri.ToString();
            if (string.Equals(key, _lastHandledDeepLink, StringComparison.OrdinalIgnoreCase) &&
                (DateTime.UtcNow - _lastHandledDeepLinkUtc).TotalSeconds < 4)
            {
                System.Diagnostics.Debug.WriteLine($"[App] Skipping duplicate deep link: {key}");
                return;
            }

            _lastHandledDeepLink = key;
            _lastHandledDeepLinkUtc = DateTime.UtcNow;

            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                try
                {
                    await WaitForUiReadyAsync();

                    // Welcome modal blocks alerts — pop it so join/import is visible.
                    await DismissWelcomeModalIfPresentAsync();

                    System.Diagnostics.Debug.WriteLine($"[App] Processing deep link: {uri}");

                    var linkText = uri.ToString();
                    // iOS often delivers OAuth / App Invite / unrelated custom-scheme URLs
                    // (e.g. com.…:) on cold start. Those are not team shares — ignore quietly.
                    if (!QrCodeService.LooksLikeTurfTeamLink(linkText))
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[App] Ignoring non-Turf deep link: {linkText}");
                        return;
                    }

                    if (!QrCodeService.TryParseTeamShareData(linkText, out var teamData, out var parseError) ||
                        teamData is null)
                    {
                        System.Diagnostics.Debug.WriteLine($"[App] Deep link parse failed: {parseError}");
                        await ShowAlertAsync(
                            "Invalid Link",
                            string.IsNullOrWhiteSpace(parseError)
                                ? "This link is not a valid Turf Time team invite or import."
                                : parseError);
                        return;
                    }

                    if (teamData.IsSharedJoin)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[App] Deep link shared join invite={teamData.InviteCode}");
                        await HandleSharedJoinAppLinkAsync(teamData.InviteCode);
                        return;
                    }

                    var importedTeamId = QrCodeService.ImportTeamToLocal(teamData);
                    await ShowAlertAsync(
                        "Team Imported",
                        $"Imported '{teamData.TeamName}' and switched to that team.");

                    if (Shell.Current is not null)
                        await Shell.Current.GoToAsync(AppShell.TeamDetailsRoute);

                    System.Diagnostics.Debug.WriteLine(
                        $"[App] ✅ Imported local team via deep link. TeamId={importedTeamId}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[App] ProcessPendingDeepLink error: {ex.GetType().FullName}: {ex.Message}");
                    await ShowAlertAsync("Import Failed", "Could not process team link.");
                }
            });
        }

        private static async Task WaitForUiReadyAsync()
        {
            for (var i = 0; i < 40; i++)
            {
                var page = Current?.Windows.FirstOrDefault()?.Page ?? Shell.Current?.CurrentPage;
                if (page is not null && Shell.Current is not null)
                    return;
                await Task.Delay(100);
            }
        }

        private static async Task DismissWelcomeModalIfPresentAsync()
        {
            try
            {
                var nav = Shell.Current?.Navigation;
                if (nav is null)
                    return;

                while (nav.ModalStack.Count > 0 &&
                       nav.ModalStack[^1] is WelcomePage)
                {
                    await nav.PopModalAsync(animated: false);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] Dismiss welcome: {ex.Message}");
            }
        }

        private async Task HandleSharedJoinAppLinkAsync(string inviteCode)
        {
            var code = QrCodeService.NormalizeInviteCode(inviteCode);
            if (string.IsNullOrEmpty(code))
            {
                await ShowAlertAsync("Invalid QR Link", "Shared-team link is missing an invite code.");
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[App] HandleSharedJoinAppLinkAsync code={code}");

            var displayName = UserDisplayName.Get();
            if (!UserDisplayName.TryValidate(displayName, out displayName, out _))
            {
                // Keep invite so user can re-open link after setting a name; also show clear UI.
                Preferences.Set("pending_join_invite", code);
                await ShowAlertAsync(
                    "Display Name Required",
                    $"To join with invite {code}, open Team Details → set your display name, then open the invite link again (or use Join with invite code).");
                if (Shell.Current is not null)
                    await Shell.Current.GoToAsync(AppShell.TeamDetailsRoute);
                return;
            }

            var cloud = Handler?.MauiContext?.Services?.GetService<ICloudTeamService>()
                ?? Current?.Handler?.MauiContext?.Services?.GetService<ICloudTeamService>();
            if (cloud is null)
            {
                await ShowAlertAsync("Unavailable", "Cloud team service is not available.");
                return;
            }

            var result = await cloud.JoinByInviteCodeAsync(code, displayName);
            System.Diagnostics.Debug.WriteLine($"[App] JoinByInvite result: {result}");

            if (result.StartsWith("success:", StringComparison.Ordinal) ||
                result.StartsWith("already_member:", StringComparison.Ordinal))
            {
                var parts = result.Split(':', 3);
                if (parts.Length >= 3)
                {
                    Preferences.Remove("pending_join_invite");
                    var teamId = parts[1];
                    var teamName = parts[2];
                    var role = "member";
                    var isOwner = false;
                    if (result.StartsWith("already_member:", StringComparison.Ordinal))
                    {
                        try
                        {
                            role = await cloud.GetMyRoleAsync(teamId) ?? "member";
                            isOwner = await cloud.IsTeamOwnerAsync(teamId);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[App] already_member role: {ex.Message}");
                        }
                    }

                    QrCodeService.ApplySharedJoinLocalState(
                        teamId, teamName, displayName, code, role, isOwner);
                    _ = FcmService.Instance.EnsureRegisteredForCurrentTeamAsync();
                    _ = EnsureMatchScheduleSyncAsync();
                    await ShowAlertAsync(
                        result.StartsWith("success:", StringComparison.Ordinal) ? "Joined Team!" : "Team Restored",
                        $"Team: {teamName}\nChat name: {displayName}");
                    if (Shell.Current is not null)
                        await Shell.Current.GoToAsync(AppShell.TeamDetailsRoute);
                    return;
                }
            }

            var msg = result.StartsWith("error:", StringComparison.Ordinal)
                ? result["error:".Length..].Trim()
                : result;
            await ShowAlertAsync("Join Failed", string.IsNullOrWhiteSpace(msg) ? "Could not join team." : msg);
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
                var playerName = string.IsNullOrWhiteSpace(sharePlayer.Name)
                    ? Player.DefaultName(slot)
                    : sharePlayer.Name.Trim();
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
                players.Add(new Player { SlotId = fillerSlot, Name = Player.DefaultName(fillerSlot), Position = PlayerPosition.Bench });
                snapshots.Add(new PlayerSnapshot { SlotId = fillerSlot, Name = Player.DefaultName(fillerSlot) });
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
                return parsed == PlayerPosition.None ? PlayerPosition.Bench : parsed;

            return PlayerPosition.Bench;
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

        /// <summary>Start/refresh shared match schedule listener for current team (no-op for local).</summary>
        internal static async Task EnsureMatchScheduleSyncAsync()
        {
            try
            {
                var services = Current?.Handler?.MauiContext?.Services
                    ?? Shell.Current?.Handler?.MauiContext?.Services;
                var host = services?.GetService<MatchScheduleSyncHost>();
                if (host is null) return;
                await host.EnsureForCurrentTeamAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] Match schedule sync: {ex.Message}");
            }
        }

        internal static async Task RescheduleMatchRemindersAsync()
        {
            try
            {
                var services = Current?.Handler?.MauiContext?.Services
                    ?? Shell.Current?.Handler?.MauiContext?.Services;
                var reminders = services?.GetService<IMatchReminderService>();
                if (reminders is null) return;
                await reminders.RescheduleForCurrentTeamAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] Match reminders: {ex.Message}");
            }
        }
    }
}
