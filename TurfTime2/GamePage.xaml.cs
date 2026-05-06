#if IOS || MACCATALYST
using Foundation;
#endif

namespace TurfTime2;

public partial class GamePage : ContentPage
{
    private bool _keepScreenOn = false;
    private string _lastLoadedTeamId = string.Empty; // Track last loaded team to avoid unnecessary reloads
    private static bool _isInitializingForFirebase = false; // Flag to skip navigation when initializing Firebase
    private static bool _memberPollingActive = false;
    private static CancellationTokenSource? _memberPollCts;

    // Public property to expose WebView for Firebase interactions
    public WebView? GameWebView => webView;

    // Public method to indicate we're just initializing Firebase
    public static void SetFirebaseInitializationMode(bool isInitializing)
    {
        _isInitializingForFirebase = isInitializing;
        System.Diagnostics.Debug.WriteLine($"[GamePage] Firebase initialization mode: {isInitializing}");
    }

    public GamePage()
    {
        InitializeComponent();

        // Set the WebView source based on platform
        SetWebViewSource();

        // When the WebView finishes loading the HTML page, synchronise the team.
        // This is the most reliable trigger on Windows: DOMContentLoaded has already
        // fired, rosterManagerInstance is guaranteed to be ready, so the first attempt
        // in SyncTeamIdToWebView will always get 'reloaded' (no retries needed).
        webView.Navigated += OnWebViewNavigated;

        // Subscribe to rotation style changes
        RotationStylePage.RotationStyleChanged += async (sender, styleNum) =>
        {
            await UpdateRotationStyle(styleNum);
        };

        // Subscribe to team view changes
        TeamViewPage.TeamViewChanged += async (sender, viewType) =>
        {
            await UpdateTeamView(viewType);
        };

        // Subscribe to manual sync requests
        CloudSyncHelper.ManualSyncRequested += async (sender, e) =>
        {
            await TriggerManualSync();
        };

#if ANDROID
        // Configure WebView for Android
        Microsoft.Maui.Handlers.WebViewHandler.Mapper.AppendToMapping("CustomWebView", (handler, view) =>
        {
            var webView = handler.PlatformView;
            webView.Settings.JavaScriptEnabled = true;
            webView.Settings.DomStorageEnabled = true;

            // Enable hardware acceleration for better performance
            webView.SetLayerType(Android.Views.LayerType.Hardware, null);
        });
#endif

#if IOS
        // Configure WebView for iOS
        Microsoft.Maui.Handlers.WebViewHandler.Mapper.AppendToMapping("CustomWebView", (handler, view) =>
        {
            var webView = handler.PlatformView;
            webView.Configuration.Preferences.SetValueForKey(
                Foundation.NSNumber.FromBoolean(true),
                new Foundation.NSString("allowFileAccessFromFileURLs")
            );
            webView.Configuration.Preferences.SetValueForKey(
                Foundation.NSNumber.FromBoolean(true),
                new Foundation.NSString("allowUniversalAccessFromFileURLs")
            );
        });
#endif
#if WINDOWS
        // Bridge JavaScript console.log/error to C# Debug output for Windows diagnostics
        Microsoft.Maui.Handlers.WebViewHandler.Mapper.AppendToMapping("CustomWebViewWindows", (handler, view) =>
        {
            var platformWebView = handler.PlatformView;

            // Set up WebMessageReceived handler on the platform-specific WebView2 control
            platformWebView.WebMessageReceived += (s, e) =>
            {
                try
                {
                    var json = System.Text.Json.JsonDocument.Parse(e.WebMessageAsJson);
                    var type = json.RootElement.GetProperty("type").GetString();
                    var msg = json.RootElement.GetProperty("msg").GetString();
                    var prefix = type == "error" ? "❌" : type == "warn" ? "⚠️" : "ℹ️";
                    System.Diagnostics.Debug.WriteLine($"[JS {prefix}] {msg}");
                }
                catch { }
            };
        });

        webView.Navigated += async (s, e) =>
        {
            if (e.Result != WebNavigationResult.Success) return;
            await webView.EvaluateJavaScriptAsync(@"
                (function() {
                    const origLog = console.log;
                    const origError = console.error;
                    const origWarn = console.warn;
                    console.log = function(...args) {
                        origLog.apply(console, args);
                        window.chrome?.webview?.postMessage({type:'log', msg: args.join(' ')});
                    };
                    console.error = function(...args) {
                        origError.apply(console, args);
                        window.chrome?.webview?.postMessage({type:'error', msg: args.join(' ')});
                    };
                    console.warn = function(...args) {
                        origWarn.apply(console, args);
                        window.chrome?.webview?.postMessage({type:'warn', msg: args.join(' ')});
                    };
                })();
            ");
        };
#endif
    }

    private void SetWebViewSource()
    {
        var teamId   = Preferences.Get("team_id",   string.Empty);
        var teamName = Preferences.Get("team_name", string.Empty);

        // Pass the current team directly in the URL so JavaScript can read it
        // via URLSearchParams at DOMContentLoaded time — before any C#→JS bridge call.
        // This eliminates the race condition on Windows where EvaluateJavaScriptAsync
        // loses to DOMContentLoaded and the RosterManager initialises with a stale team.
        var query = string.IsNullOrEmpty(teamId)
            ? string.Empty
            : $"?tid={Uri.EscapeDataString(teamId)}&tn={Uri.EscapeDataString(teamName)}";

#if ANDROID
        webView.Source = $"file:///android_asset/wwwroot/index.html{query}";
#elif IOS || MACCATALYST
        var indexPath = Path.Combine(NSBundle.MainBundle.BundlePath, "wwwroot", "index.html");
        webView.Source = new UrlWebViewSource { Url = $"file://{indexPath}{query}" };
#elif WINDOWS
        var indexPath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html");
        System.Diagnostics.Debug.WriteLine($"[WebView] Windows path: {indexPath}, exists: {File.Exists(indexPath)}");
        webView.Source = new UrlWebViewSource { Url = $"file:///{indexPath.Replace("\\", "/")}{query}" };
#endif
        System.Diagnostics.Debug.WriteLine($"[WebView] Source set — team: {(string.IsNullOrEmpty(teamId) ? "(none)" : teamId)}");
    }

    private async Task TriggerManualSync()
    {
        try
        {
            System.Diagnostics.Debug.WriteLine("[CloudSync] Triggering manual sync via JavaScript");

            // Call the JavaScript sync function
            await webView.EvaluateJavaScriptAsync("if (window.rosterManagerInstance) { window.rosterManagerInstance.syncWithCloud(); }");

            System.Diagnostics.Debug.WriteLine("[CloudSync] Manual sync triggered successfully");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CloudSync] Error triggering manual sync: {ex.Message}");
        }
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        // Keep screen on when page appears
        SetKeepScreenOn(true);

        // Inject save bridge (needs to happen before team sync attempts save)
        _ = Task.Run(async () =>
        {
            await Task.Delay(500); // Wait for WebView to be ready
            await MainThread.InvokeOnMainThreadAsync(async () => await InjectSaveBridge());
        });

        // NOTE: Don't start polling here - team info not yet synced!
        // Polling will start after SyncTeamIdToWebView() completes

        // Sync current team ID to WebView FIRST (before loading roster)
        SyncTeamIdToWebView();

        // Sync theme from Preferences to WebView
        SyncThemeToWebView();

        // Sync rotation style from Preferences to WebView
        SyncRotationStyleToWebView();

        // Sync team view preference from Preferences to WebView
        SyncTeamViewToWebView();
    }

    // OnNavigatedTo fires reliably on Windows Shell tab navigation where
    // OnAppearing is sometimes skipped.  It is safe to call the same syncs
    // here — SyncTeamIdToWebView guards against unnecessary reloads via
    // _lastLoadedTeamId, and the other syncs are cheap JS calls.
    protected override void OnNavigatedTo(NavigatedToEventArgs args)
    {
        base.OnNavigatedTo(args);
        SetKeepScreenOn(true);

        // Inject save bridge
        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            await MainThread.InvokeOnMainThreadAsync(async () => await InjectSaveBridge());
        });

        SyncTeamIdToWebView();
        SyncThemeToWebView();
        SyncRotationStyleToWebView();
        SyncTeamViewToWebView();
    }

    // Fires when the WebView finishes loading index.html (including on first load and
    // after a team-change reload triggered by SyncTeamIdToWebView).
    // The team ID was already embedded in the URL by SetWebViewSource(), so
    // JavaScript reads it via URLSearchParams at DOMContentLoaded — no team sync needed here.
    // We only need to re-apply the thin C# preferences (theme, rotation, view).
    private async void OnWebViewNavigated(object? sender, WebNavigatedEventArgs e)
    {
        if (e.Result != WebNavigationResult.Success)
        {
            System.Diagnostics.Debug.WriteLine($"[GamePage] WebView navigation FAILED: {e.Result}");
            return;
        }

        System.Diagnostics.Debug.WriteLine($"[GamePage] WebView navigation complete - injecting bridge");

        // Inject C# save bridge into JavaScript
        await InjectSaveBridge();

        SyncThemeToWebView();
        SyncRotationStyleToWebView();
        SyncTeamViewToWebView();
    }

    private static bool _bridgeInjected = false;
    private static bool _pollingStarted = false;

    private async Task InjectSaveBridge()
    {
        if (_bridgeInjected)
        {
            System.Diagnostics.Debug.WriteLine("[GamePage] 🔧 Bridge already injected, skipping");
            return;
        }

        System.Diagnostics.Debug.WriteLine("[GamePage] 🔧 Starting bridge injection...");

        try
        {
            var script = @"
                window.csharpSaveRoster = async function(teamId, rosterData) {
                    try {
                        const rosterJson = JSON.stringify(rosterData);
                        console.log('[C# Bridge] Saving via C# for team:', teamId);

                        // Store in localStorage with special key for C# to pick up
                        localStorage.setItem('_pending_save_team', teamId);
                        localStorage.setItem('_pending_save_data', rosterJson);
                        localStorage.setItem('_pending_save_trigger', Date.now().toString());

                        // Poll for result (C# will set this after saving)
                        return new Promise((resolve) => {
                            let attempts = 0;
                            const checkResult = setInterval(() => {
                                const result = localStorage.getItem('_pending_save_result');
                                if (result || attempts++ > 50) { // 5 seconds max
                                    clearInterval(checkResult);
                                    localStorage.removeItem('_pending_save_result');
                                    resolve(result || 'error:timeout');
                                }
                            }, 100);
                        });
                    } catch (error) {
                        console.error('[C# Bridge] Save error:', error);
                        return 'error:' + error.message;
                    }
                };

                window.csharpSaveSession = {
                    postMessage: function(jsonData) {
                        try {
                            console.log('[C# Bridge] Saving session via C#');
                            localStorage.setItem('_pending_session_save_data', jsonData);
                            localStorage.setItem('_pending_session_save_trigger', Date.now().toString());
                        } catch (error) {
                            console.error('[C# Bridge] Session save error:', error);
                        }
                    }
                };

                console.log('[C# Bridge] ✓ C# save bridge injected');
                'bridge_injected';
            ";

            var result = await webView.EvaluateJavaScriptAsync(script);
            System.Diagnostics.Debug.WriteLine($"[GamePage] ✓ C# save bridge injected (result: {result})");
            _bridgeInjected = true;

            // Start polling for save requests (only once)
            if (!_pollingStarted)
            {
                _pollingStarted = true;
                _ = Task.Run(PollForSaveRequests);
                System.Diagnostics.Debug.WriteLine("[GamePage] ✓ Polling task started");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GamePage] ❌ Bridge injection failed: {ex.Message}");
        }
    }

    private async Task PollForSaveRequests()
    {
        System.Diagnostics.Debug.WriteLine("[GamePage] 🔄 Polling loop started");

        while (true)
        {
            try
            {
                await Task.Delay(200); // Check every 200ms

                // Check for roster save requests
                var trigger = await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    try
                    {
                        if (webView != null && webView.Handler != null)
                        {
                            return await webView.EvaluateJavaScriptAsync("localStorage.getItem('_pending_save_trigger')");
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[GamePage] Polling roster trigger check failed: {ex.Message}");
                    }
                    return null;
                });

                if (!string.IsNullOrEmpty(trigger) && trigger != "null")
                {
                    System.Diagnostics.Debug.WriteLine($"[GamePage] 📤 Roster save trigger detected: {trigger}");

                    // Get save data
                    var teamId = await MainThread.InvokeOnMainThreadAsync(async () =>
                        await webView.EvaluateJavaScriptAsync("localStorage.getItem('_pending_save_team')"));
                    var rosterJson = await MainThread.InvokeOnMainThreadAsync(async () =>
                        await webView.EvaluateJavaScriptAsync("localStorage.getItem('_pending_save_data')"));

                    // Clear trigger
                    await MainThread.InvokeOnMainThreadAsync(async () =>
                        await webView.EvaluateJavaScriptAsync("localStorage.removeItem('_pending_save_trigger')"));

                    if (!string.IsNullOrEmpty(teamId) && teamId != "null" &&
                        !string.IsNullOrEmpty(rosterJson) && rosterJson != "null")
                    {
                        // Clean up quoted strings from JavaScript
                        teamId = teamId?.Trim('"') ?? "";
                        rosterJson = rosterJson?.Trim('"').Replace("\\\"", "\"") ?? "";

                        // Save to Firestore
                        var result = await FirebaseSaveBridge.SaveRosterToFirestore(teamId, rosterJson);

                        // Set result for JavaScript
                        await MainThread.InvokeOnMainThreadAsync(async () =>
                            await webView.EvaluateJavaScriptAsync($"localStorage.setItem('_pending_save_result', '{result}')"));
                    }
                }

                // Check for session save requests
                var sessionTrigger = await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    try
                    {
                        if (webView != null && webView.Handler != null)
                        {
                            return await webView.EvaluateJavaScriptAsync("localStorage.getItem('_pending_session_save_trigger')");
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[GamePage] Polling session trigger check failed: {ex.Message}");
                    }
                    return null;
                });

                if (!string.IsNullOrEmpty(sessionTrigger) && sessionTrigger != "null")
                {
                    System.Diagnostics.Debug.WriteLine($"[GamePage] 🔵 Session save trigger detected: {sessionTrigger}");

                    // Get session data
                    var sessionJson = await MainThread.InvokeOnMainThreadAsync(async () =>
                    {
                        try
                        {
                            if (webView != null && webView.Handler != null)
                            {
                                return await webView.EvaluateJavaScriptAsync("localStorage.getItem('_pending_session_save_data')");
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[GamePage] Failed to get session data: {ex.Message}");
                        }
                        return null;
                    });

                    System.Diagnostics.Debug.WriteLine($"[GamePage] 🔵 Session data length: {sessionJson?.Length ?? 0}");

                    // Clear trigger
                    await MainThread.InvokeOnMainThreadAsync(async () =>
                    {
                        try
                        {
                            if (webView != null && webView.Handler != null)
                            {
                                await webView.EvaluateJavaScriptAsync("localStorage.removeItem('_pending_session_save_trigger')");
                            }
                        }
                        catch { }
                    });

                    if (!string.IsNullOrEmpty(sessionJson) && sessionJson != "null")
                    {
                        // Clean up quoted strings from JavaScript
                        sessionJson = sessionJson?.Trim('"').Replace("\\\"", "\"") ?? "";

                        System.Diagnostics.Debug.WriteLine($"[GamePage] 📤 Calling SessionSaveBridge.SaveSessionToFirestore()...");
                        System.Diagnostics.Debug.WriteLine($"[GamePage] 📤 Session JSON preview: {sessionJson.Substring(0, Math.Min(200, sessionJson.Length))}...");

                        // Save session to Firestore
                        SessionSaveBridge.SaveSessionToFirestore(sessionJson);

                        System.Diagnostics.Debug.WriteLine($"[GamePage] ✅ SessionSaveBridge call completed");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[GamePage] ⚠️ Session data is null or empty");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[GamePage] Poll error: {ex.Message}");
            }
        }
    }

    // Public method to force roster reload (called when team changes)
    public void ForceTeamReload()
    {
        System.Diagnostics.Debug.WriteLine($"[GamePage] 🔄 Force reload requested - clearing last loaded team");
        _lastLoadedTeamId = string.Empty; // Reset to force reload on next OnAppearing
    }

    private async void SyncTeamIdToWebView()
    {
        try
        {
            // Get current team ID from Preferences
            var teamId = Preferences.Get("team_id", string.Empty);
            var teamName = Preferences.Get("team_name", string.Empty);

            // If no team selected, don't load game data
            if (string.IsNullOrEmpty(teamId))
            {
                System.Diagnostics.Debug.WriteLine($"[GamePage] ❌ No team selected - skipping roster load");

                // If we're just initializing for Firebase, don't navigate away
                if (_isInitializingForFirebase)
                {
                    System.Diagnostics.Debug.WriteLine($"[GamePage] In Firebase initialization mode - skipping navigation");
                    return;
                }

                // Show alert to user
                await DisplayAlert("No Team Selected", 
                    "Please select or create a team in Settings → Team Details before using the Game page.", 
                    "Go to Settings");

                // Navigate to Team Details
                await Shell.Current.GoToAsync("//SettingsPage/settings/teamdetails");
                return;
            }

            // Check if team has changed since last load
            if (teamId == _lastLoadedTeamId && !string.IsNullOrEmpty(_lastLoadedTeamId))
            {
                System.Diagnostics.Debug.WriteLine($"[GamePage] ℹ️ Team unchanged ({teamId}) - skipping roster reload to preserve game state");
                return; // Don't reload if team hasn't changed
            }

            System.Diagnostics.Debug.WriteLine($"[GamePage] 📋 Team changed: '{_lastLoadedTeamId}' → '{teamId}' - reloading roster");
            // NOTE: _lastLoadedTeamId is intentionally NOT set here.
            // It is only set inside the loop on a confirmed successful reload.
            // Setting it optimistically before the loop causes the next OnAppearing()
            // to skip the reload when all retries returned 'pending' (WebView not ready),
            // leaving the old team's roster on screen permanently.

            System.Diagnostics.Debug.WriteLine($"[GamePage] Syncing team ID to localStorage and reloading roster...");

            // Get team mode and role from Preferences
            var teamMode = Preferences.Get("team_mode", string.Empty);
            var userRole = Preferences.Get("user_role", string.Empty);

            // Check if there's cached roster data from C# (downloaded after join)
            string? cachedRosterJson = null;
            if (teamMode == "shared")
            {
                cachedRosterJson = Preferences.Get($"roster_{teamId}_json", null);
                if (cachedRosterJson != null)
                {
                    System.Diagnostics.Debug.WriteLine($"[GamePage] Found cached roster data for {teamId} ({cachedRosterJson.Length} chars)");
                    System.Diagnostics.Debug.WriteLine($"[GamePage] Roster preview: {cachedRosterJson.Substring(0, Math.Min(300, cachedRosterJson.Length))}...");
                }
            }

            // The JS bridge is only needed for in-session team switches where the WebView
            // is already loaded. For initial load the team was already passed via URL params.
            var rosterInjection = cachedRosterJson != null 
                ? $@"
                    const rosterData = {cachedRosterJson};
                    const storageKey = 'roster_{EscapeJavaScript(teamId)}.v1';
                    localStorage.setItem(storageKey, JSON.stringify(rosterData));
                    console.log('[GamePage C#→JS] ✓ Injected roster data from C# cache');
                "
                : "";

            var script = $@"
                (function() {{
                    try {{
                        localStorage.setItem('team_id', '{EscapeJavaScript(teamId)}');
                        localStorage.setItem('team_name', '{EscapeJavaScript(teamName)}');
                        localStorage.setItem('team_mode', '{EscapeJavaScript(teamMode)}');
                        localStorage.setItem('user_role', '{EscapeJavaScript(userRole)}');
                        console.log('[GamePage C#→JS] Synced: team_mode=' + '{EscapeJavaScript(teamMode)}' + ', user_role=' + '{EscapeJavaScript(userRole)}');

                        {rosterInjection}

                        if (typeof window.reloadRosterForTeam === 'function' && window.rosterManagerInstance) {{
                            window.reloadRosterForTeam('{EscapeJavaScript(teamId)}');
                            return 'reloaded';
                        }} else if (window.rosterManagerInstance) {{
                            window.rosterManagerInstance.reloadForTeam('{EscapeJavaScript(teamId)}');
                            return 'reloaded_direct';
                        }} else {{
                            return 'pending';
                        }}
                    }} catch (error) {{
                        return 'error:' + error.message;
                    }}
                }})();
            ";

            string? result = null;
            for (int attempt = 1; attempt <= 5; attempt++)
            {
                await Task.Delay(attempt == 1 ? 300 : 500);
                try
                {
                    result = await webView.EvaluateJavaScriptAsync(script);
                    System.Diagnostics.Debug.WriteLine($"[GamePage] Team sync attempt {attempt}: {result}");
                    if (result == "reloaded" || result == "reloaded_direct")
                    {
                        _lastLoadedTeamId = teamId;
                        break;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[GamePage] Team sync attempt {attempt} exception: {ex.Message}");
                }
            }

            // If the JS bridge never succeeded (e.g. WebView not yet fully initialised),
            // fall back to reloading the WebView source with the team in the URL.
            // SetWebViewSource() embeds ?tid=teamId so DOMContentLoaded picks it up reliably.
            if (result != "reloaded" && result != "reloaded_direct")
            {
                System.Diagnostics.Debug.WriteLine($"[GamePage] ⚠️ JS bridge failed — reloading WebView with team URL");
                _lastLoadedTeamId = teamId; // Prevent re-entry when Navigated fires
                SetWebViewSource();
            }

            System.Diagnostics.Debug.WriteLine($"[GamePage] ✅ Team sync final result: {result ?? "url-reload"} for team: {teamId}");

            // NOW start member polling (after team info is synced to preferences)
            StartMemberPollingIfNeeded(teamId);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GamePage] ❌ Error syncing team ID: {ex.Message}");
        }
    }

    private string EscapeJavaScript(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return text.Replace("\\", "\\\\")
                   .Replace("'", "\\'")
                   .Replace("\"", "\\\"")
                   .Replace("\n", "\\n")
                   .Replace("\r", "\\r");
    }

    private async void SyncThemeToWebView()
    {
        try
        {
            // Small delay to ensure WebView is fully loaded
            await Task.Delay(100);

            var theme = Preferences.Get("AppTheme", "classic");
            await webView.EvaluateJavaScriptAsync($"setTheme('{theme}')");
            System.Diagnostics.Debug.WriteLine($"[Theme] Synced theme to WebView: {theme}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Theme] Error syncing theme: {ex.Message}");
        }
    }

    private async void SyncRotationStyleToWebView()
    {
        try
        {
            // Small delay to ensure WebView is fully loaded
            await Task.Delay(150);

            var rotationStyle = Preferences.Get("rotation_style", 1);
            await webView.EvaluateJavaScriptAsync($"setRotationStyleFromMAUI({rotationStyle})");
            System.Diagnostics.Debug.WriteLine($"[RotationStyle] Synced rotation style to WebView: {rotationStyle}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RotationStyle] Error syncing rotation style: {ex.Message}");
        }
    }

    private async Task UpdateRotationStyle(int styleNum)
    {
        try
        {
            await webView.EvaluateJavaScriptAsync($"setRotationStyleFromMAUI({styleNum})");
            System.Diagnostics.Debug.WriteLine($"[RotationStyle] Updated rotation style in WebView: {styleNum}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RotationStyle] Error updating rotation style: {ex.Message}");
        }
    }

    private async void SyncTeamViewToWebView()
    {
        try
        {
            // Small delay to ensure WebView is fully loaded
            await Task.Delay(200);

            var teamView = Preferences.Get("team_view_preference", "swipe");
            await webView.EvaluateJavaScriptAsync($"setTeamViewFromMAUI('{teamView}')");
            System.Diagnostics.Debug.WriteLine($"[TeamView] Synced team view to WebView: {teamView}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TeamView] Error syncing team view: {ex.Message}");
        }
    }

    private async Task UpdateTeamView(string viewType)
    {
        try
        {
            await webView.EvaluateJavaScriptAsync($"setTeamViewFromMAUI('{viewType}')");
            System.Diagnostics.Debug.WriteLine($"[TeamView] Updated team view in WebView: {viewType}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TeamView] Error updating team view: {ex.Message}");
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();

        // Allow screen to sleep when page disappears
        SetKeepScreenOn(false);

        // Stop member polling when leaving the page
        StopMemberPolling();
    }

    private void SetKeepScreenOn(bool keepOn)
    {
        _keepScreenOn = keepOn;
        
#if ANDROID
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                var activity = Platform.CurrentActivity;
                if (activity?.Window != null)
                {
                    if (keepOn)
                    {
                        activity.Window.AddFlags(Android.Views.WindowManagerFlags.KeepScreenOn);
                        System.Diagnostics.Debug.WriteLine("[ScreenWakeLock] Screen wake lock ENABLED");
                    }
                    else
                    {
                        activity.Window.ClearFlags(Android.Views.WindowManagerFlags.KeepScreenOn);
                        System.Diagnostics.Debug.WriteLine("[ScreenWakeLock] Screen wake lock DISABLED");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ScreenWakeLock] Error: {ex.Message}");
            }
        });
#elif IOS || MACCATALYST
        MainThread.BeginInvokeOnMainThread(() =>
        {
            UIKit.UIApplication.SharedApplication.IdleTimerDisabled = keepOn;
            System.Diagnostics.Debug.WriteLine($"[ScreenWakeLock] iOS idle timer disabled: {keepOn}");
        });
#endif
    }

    // ============================================================================
    // MEMBER POLLING - Real-time roster updates for shared team members
    // ============================================================================

    private void StartMemberPollingIfNeeded(string teamId = "")
    {
        // If no teamId passed, try to get from preferences
        if (string.IsNullOrEmpty(teamId))
        {
            teamId = Preferences.Get("selected_team_id", "");
        }

        if (string.IsNullOrEmpty(teamId))
        {
            System.Diagnostics.Debug.WriteLine($"[GamePage] Polling check - No team ID available");
            return;
        }

        var teamMode = Preferences.Get("team_mode", "local");
        var userRole = Preferences.Get("user_role", "admin");

        System.Diagnostics.Debug.WriteLine($"[GamePage] Polling check - teamId:{teamId}, mode:{teamMode}, role:{userRole}, active:{_memberPollingActive}");

        if (teamMode == "shared" && userRole == "member" && !_memberPollingActive)
        {
            System.Diagnostics.Debug.WriteLine($"[GamePage] Starting member polling for team: {teamId}");
            _memberPollingActive = true;
            _memberPollCts = new CancellationTokenSource();
            _ = Task.Run(() => MemberPollingLoop(teamId, _memberPollCts.Token));
        }
        else
        {
            System.Diagnostics.Debug.WriteLine($"[GamePage] Polling NOT started - conditions not met");
        }
    }

    private void StopMemberPolling()
    {
        if (_memberPollingActive)
        {
            System.Diagnostics.Debug.WriteLine("[GamePage] ?? Stopping member polling");
            _memberPollingActive = false;
            _memberPollCts?.Cancel();
            _memberPollCts?.Dispose();
            _memberPollCts = null;
        }
    }

    private async Task MemberPollingLoop(string teamId, CancellationToken ct)
    {
        try
        {
            System.Diagnostics.Debug.WriteLine($"[GamePage] Member polling loop started for team: {teamId}");

            // Poll immediately on start, then every 10 seconds
            while (!ct.IsCancellationRequested && _memberPollingActive)
            {
                if (string.IsNullOrEmpty(teamId))
                {
                    System.Diagnostics.Debug.WriteLine("[GamePage] Team ID is empty, stopping poll");
                    break;
                }

                System.Diagnostics.Debug.WriteLine($"[GamePage] Polling for roster updates: {teamId}");

                var freshRoster = await TeamDetailsPage.DownloadRosterFromFirestoreStatic(teamId);

                if (!string.IsNullOrEmpty(freshRoster))
                {
                    var cachedRoster = Preferences.Get($"roster_{teamId}_json", "");

                    if (freshRoster != cachedRoster)
                    {
                        System.Diagnostics.Debug.WriteLine($"[GamePage] Roster updated! Refreshing UI...");
                        Preferences.Set($"roster_{teamId}_json", freshRoster);

                        await MainThread.InvokeOnMainThreadAsync(async () =>
                        {
                            await InjectFreshRoster(teamId, freshRoster);
                        });
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[GamePage] No roster changes");
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[GamePage] Failed to download roster");
                }

                // Wait 10 seconds before next poll
                await Task.Delay(10000, ct);
            }

            System.Diagnostics.Debug.WriteLine($"[GamePage] Member polling loop exited for team: {teamId}");
        }
        catch (TaskCanceledException)
        {
            System.Diagnostics.Debug.WriteLine("[GamePage] Member polling cancelled");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GamePage] Polling error: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[GamePage] Stack trace: {ex.StackTrace}");
        }
    }

    private async Task InjectFreshRoster(string teamId, string rosterJson)
    {
        try
        {
            var escapedJson = EscapeJavaScript(rosterJson);
            var script = $@"
                (function() {{
                    try {{
                        console.log('[GamePage C#->JS] Starting roster injection...');
                        console.log('[GamePage C#->JS] Team ID: {EscapeJavaScript(teamId)}');
                        console.log('[GamePage C#->JS] Roster data length: {rosterJson.Length} chars');

                        const rosterData = JSON.parse('{escapedJson}');
                        console.log('[GamePage C#->JS] Parsed roster data:', rosterData);
                        console.log('[GamePage C#->JS] Player count:', rosterData.players ? rosterData.players.length : 0);

                        const storageKey = 'roster_{EscapeJavaScript(teamId)}.v1';
                        console.log('[GamePage C#->JS] Storage key:', storageKey);

                        localStorage.setItem(storageKey, JSON.stringify(rosterData));
                        console.log('[GamePage C#->JS] Updated roster in localStorage');

                        if (typeof window.rosterManagerInstance !== 'undefined') {{
                            console.log('[GamePage C#->JS] rosterManagerInstance found');

                            if (typeof window.rosterManagerInstance.loadFromStorage === 'function') {{
                                console.log('[GamePage C#->JS] Calling loadFromStorage()...');
                                window.rosterManagerInstance.loadFromStorage();
                                console.log('[GamePage C#->JS] Roster reloaded successfully');
                            }} else {{
                                console.error('[GamePage C#->JS] loadFromStorage method not found!');
                            }}
                        }} else {{
                            console.error('[GamePage C#->JS] rosterManagerInstance not found!');
                        }}
                    }} catch (error) {{
                        console.error('[GamePage C#->JS] Error:', error.message, error.stack);
                    }}
                }})();
            ";

            await webView.EvaluateJavaScriptAsync(script);
            System.Diagnostics.Debug.WriteLine($"[GamePage] Fresh roster injected");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GamePage] Inject error: {ex.Message}");
        }
    }
}