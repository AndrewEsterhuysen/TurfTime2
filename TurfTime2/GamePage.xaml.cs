namespace TurfTime2;

public partial class GamePage : ContentPage
{
    private bool _keepScreenOn = false;

    public GamePage()
    {
        InitializeComponent();

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
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        // Keep screen on when page appears
        SetKeepScreenOn(true);

        // Sync theme from Preferences to WebView
        SyncThemeToWebView();
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