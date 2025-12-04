namespace TurfTime2;

public partial class GamePage : ContentPage
{
    private bool _keepScreenOn = false;

    public GamePage()
    {
        InitializeComponent();

#if ANDROID
        // Configure WebView for Android to enable vibration
        Microsoft.Maui.Handlers.WebViewHandler.Mapper.AppendToMapping("CustomWebView", (handler, view) =>
        {
            var webView = handler.PlatformView;
            webView.Settings.JavaScriptEnabled = true;
            webView.Settings.DomStorageEnabled = true;

            // Enable hardware acceleration for better performance
            webView.SetLayerType(Android.Views.LayerType.Hardware, null);

            // Add JavaScript interface for vibration
            webView.AddJavascriptInterface(new VibrationBridge(), "VibrationBridge");
        });
#endif
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        
        // Keep screen on when page appears
        SetKeepScreenOn(true);
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

#if ANDROID
    // JavaScript bridge to trigger vibrations from WebView
    private class VibrationBridge : Java.Lang.Object
    {
        [Android.Webkit.JavascriptInterface]
        [Java.Interop.Export("vibrate")]
        public void Vibrate(long duration)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                try
                {
                    var vibrator = Android.App.Application.Context.GetSystemService(Android.Content.Context.VibratorService) as Android.OS.Vibrator;
                    if (vibrator != null && vibrator.HasVibrator)
                    {
                        // If duration is 0, cancel any ongoing vibration
                        if (duration == 0)
                        {
                            vibrator.Cancel();
                            System.Diagnostics.Debug.WriteLine($"[VibrationBridge] Vibration cancelled");
                            return;
                        }

                        if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.O)
                        {
                            vibrator.Vibrate(Android.OS.VibrationEffect.CreateOneShot(duration, Android.OS.VibrationEffect.DefaultAmplitude));
                        }
                        else
                        {
#pragma warning disable CS0618 // Type or member is obsolete
                            vibrator.Vibrate(duration);
#pragma warning restore CS0618
                        }
                        System.Diagnostics.Debug.WriteLine($"[VibrationBridge] Vibrated for {duration}ms");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[VibrationBridge] Error: {ex.Message}");
                }
            });
        }

        [Android.Webkit.JavascriptInterface]
        [Java.Interop.Export("vibratePattern")]
        public void VibratePattern(string patternJson)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                try
                {
                    var pattern = System.Text.Json.JsonSerializer.Deserialize<long[]>(patternJson);
                    if (pattern != null && pattern.Length > 0)
                    {
                        var vibrator = Android.App.Application.Context.GetSystemService(Android.Content.Context.VibratorService) as Android.OS.Vibrator;
                        if (vibrator != null && vibrator.HasVibrator)
                        {
                            if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.O)
                            {
                                vibrator.Vibrate(Android.OS.VibrationEffect.CreateWaveform(pattern, -1));
                            }
                            else
                            {
#pragma warning disable CS0618 // Type or member is obsolete
                                vibrator.Vibrate(pattern, -1);
#pragma warning restore CS0618
                            }
                            System.Diagnostics.Debug.WriteLine($"[VibrationBridge] Vibrated pattern: {patternJson}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[VibrationBridge] Pattern error: {ex.Message}");
                }
            });
        }

        [Android.Webkit.JavascriptInterface]
        [Java.Interop.Export("cancelVibration")]
        public void CancelVibration()
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                try
                {
                    var vibrator = Android.App.Application.Context.GetSystemService(Android.Content.Context.VibratorService) as Android.OS.Vibrator;
                    if (vibrator != null)
                    {
                        vibrator.Cancel();
                        System.Diagnostics.Debug.WriteLine($"[VibrationBridge] Vibration cancelled via dedicated method");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[VibrationBridge] Cancel error: {ex.Message}");
                }
            });
        }
    }
#endif
}