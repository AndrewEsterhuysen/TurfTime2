namespace TurfTime2;

public partial class GamePage : ContentPage
{
    private bool _keepScreenOn = false;

    public GamePage()
    {
        InitializeComponent();

        // Subscribe to rotation style changes
        RotationStylePage.RotationStyleChanged += async (sender, styleNum) =>
        {
            await UpdateRotationStyle(styleNum);
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

    protected override void OnAppearing()
    {
        base.OnAppearing();

        // Keep screen on when page appears
        SetKeepScreenOn(true);

        // Sync theme from Preferences to WebView
        SyncThemeToWebView();

        // Sync rotation style from Preferences to WebView
        SyncRotationStyleToWebView();
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