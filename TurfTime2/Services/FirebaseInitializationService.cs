using Microsoft.Maui.Controls;

namespace TurfTime2.Services;

/// <summary>
/// Service to initialize Firebase at app startup using a hidden WebView
/// </summary>
public class FirebaseInitializationService
{
    private static WebView? _firebaseWebView;
    private static bool _isInitialized = false;
    private static TaskCompletionSource<bool>? _initializationTask;

    /// <summary>
    /// Initialize Firebase at app startup
    /// </summary>
    public static async Task<bool> InitializeAsync()
    {
        if (_isInitialized)
        {
            System.Diagnostics.Debug.WriteLine("[Firebase] Already initialized");
            return true;
        }

        if (_initializationTask != null)
        {
            System.Diagnostics.Debug.WriteLine("[Firebase] Initialization already in progress, waiting...");
            return await _initializationTask.Task;
        }

        _initializationTask = new TaskCompletionSource<bool>();

        try
        {
            System.Diagnostics.Debug.WriteLine("[Firebase] 🔥 Starting Firebase initialization...");

            // Create a minimal WebView for Firebase
            _firebaseWebView = new WebView
            {
                IsVisible = false,
                HeightRequest = 1,
                WidthRequest = 1
            };

#if ANDROID
            // Configure WebView for Android
            Microsoft.Maui.Handlers.WebViewHandler.Mapper.AppendToMapping("FirebaseWebView", (handler, view) =>
            {
                if (view == _firebaseWebView)
                {
                    var webView = handler.PlatformView;
                    webView.Settings.JavaScriptEnabled = true;
                    webView.Settings.DomStorageEnabled = true;
                    System.Diagnostics.Debug.WriteLine("[Firebase] Android WebView configured");
                }
            });
#endif

            // Set source based on platform
            // Use minimal firebase-init.html instead of full index.html
            // This avoids loading roster-manager.js which expects game page DOM elements
#if WINDOWS
            var source = new UrlWebViewSource { Url = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot", "firebase-init.html") };
#else
            var source = new UrlWebViewSource { Url = "file:///android_asset/wwwroot/firebase-init.html" };
#endif

            System.Diagnostics.Debug.WriteLine("[Firebase] Loading firebase-init.html (minimal Firebase + team-service only)");
            _firebaseWebView.Source = source;

            // Wait for WebView page to load
            System.Diagnostics.Debug.WriteLine("[Firebase] Waiting for WebView to load...");
            await Task.Delay(1000);

            // Poll for teamService to be ready (module loads asynchronously)
            System.Diagnostics.Debug.WriteLine("[Firebase] Checking if teamService is ready...");
            bool isTeamServiceReady = await VerifyTeamServiceLoaded(_firebaseWebView);

            if (isTeamServiceReady)
            {
                System.Diagnostics.Debug.WriteLine("[Firebase] ✅ teamService loaded and ready");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[Firebase] ⚠️ teamService not ready - team operations may fail");
            }

            _isInitialized = true;
            System.Diagnostics.Debug.WriteLine("[Firebase] ✅ Firebase initialization complete");
            
            _initializationTask.SetResult(true);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Firebase] ❌ Initialization failed: {ex.Message}");
            _initializationTask?.SetResult(false);
            return false;
        }
    }

    /// <summary>
    /// Get the Firebase WebView for JavaScript execution
    /// </summary>
    public static WebView? GetFirebaseWebView()
    {
        return _firebaseWebView;
    }

    /// <summary>
    /// Check if Firebase is initialized
    /// </summary>
    public static bool IsInitialized => _isInitialized;

    /// <summary>
    /// Verify that teamService is loaded and ready
    /// </summary>
    private static async Task<bool> VerifyTeamServiceLoaded(WebView webView)
    {
        try
        {
            // Poll for up to 10 seconds
            for (int i = 0; i < 20; i++)
            {
                var script = @"
                    (function() {
                        try {
                            if (window.teamServiceReady === true) {
                                console.log('[Firebase] teamService is ready');
                                return 'ready';
                            } else if (window.firebaseInitializing === true) {
                                console.log('[Firebase] Still initializing...');
                                return 'initializing';
                            } else {
                                console.log('[Firebase] teamService not ready, state:', {
                                    teamServiceReady: window.teamServiceReady,
                                    firebaseReady: window.firebaseReady,
                                    initializing: window.firebaseInitializing
                                });
                                return 'not_ready';
                            }
                        } catch (error) {
                            console.error('[Firebase] Error checking teamService:', error);
                            return 'error';
                        }
                    })();
                ";

                var result = await webView.EvaluateJavaScriptAsync(script);

                if (result == "ready")
                {
                    System.Diagnostics.Debug.WriteLine($"[Firebase] teamService ready after {i * 500}ms");
                    return true;
                }

                if (result == "error")
                {
                    System.Diagnostics.Debug.WriteLine($"[Firebase] Error checking teamService");
                    return false;
                }

                // Wait 500ms before checking again
                await Task.Delay(500);
            }

            System.Diagnostics.Debug.WriteLine($"[Firebase] Timeout waiting for teamService");
            return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Firebase] Error verifying teamService: {ex.Message}");
            return false;
        }
    }
}
