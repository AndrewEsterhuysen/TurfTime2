#if IOS || MACCATALYST
using Foundation;
#endif

namespace TurfTime2;

public partial class GamePage : ContentPage
{
    private bool _keepScreenOn = false;

    public GamePage()
    {
        InitializeComponent();

        // Set the WebView source based on platform
        SetWebViewSource();

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
    }

    private void SetWebViewSource()
    {
#if ANDROID
        webView.Source = "file:///android_asset/wwwroot/index.html";
#elif IOS || MACCATALYST
        var indexPath = Path.Combine(NSBundle.MainBundle.BundlePath, "wwwroot", "index.html");
        webView.Source = new UrlWebViewSource { Url = $"file://{indexPath}" };
#elif WINDOWS
        // On Windows, wwwroot files are copied to the output directory
        var indexPath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html");
        System.Diagnostics.Debug.WriteLine($"[WebView] Windows path: {indexPath}, exists: {File.Exists(indexPath)}");
        webView.Source = new UrlWebViewSource { Url = $"file:///{indexPath.Replace("\\", "/")}" };
#endif
        System.Diagnostics.Debug.WriteLine($"[WebView] Source set for platform");
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

        // Sync theme from Preferences to WebView
        SyncThemeToWebView();

        // Sync rotation style from Preferences to WebView
        SyncRotationStyleToWebView();

        // Sync team view preference from Preferences to WebView
        SyncTeamViewToWebView();
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
}