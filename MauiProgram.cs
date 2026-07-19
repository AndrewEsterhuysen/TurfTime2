using Microsoft.Extensions.Logging;
using Microsoft.Maui.LifecycleEvents;
using Plugin.Firebase.Auth;
using Plugin.Firebase.Bundled.Shared;
using Plugin.Firebase.CloudMessaging;
using ZXing.Net.Maui;
using ZXing.Net.Maui.Controls;
#if ANDROID
using Plugin.Firebase.Bundled.Platforms.Android;
#elif IOS
using Foundation; // for NSBundle plist check
using Plugin.Firebase.Bundled.Platforms.iOS;
#endif

namespace TurfTime2
{
    public static class MauiProgram
    {
        /// <summary>
        /// Sets up global handlers as early as possible so that "crashes after a few minutes"
        /// or launch crashes produce useful managed stack traces + diagnostics instead of
        /// opaque SIGABRT / EXC_CRASH from the native runtime.
        /// These are cross-platform but especially valuable on iOS simulator where native
        /// crashes from Firebase or timers can otherwise swallow the root cause.
        /// </summary>
        private static void SetupGlobalExceptionHandling()
        {
            AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            {
                var ex = e.ExceptionObject as Exception;
                System.Diagnostics.Debug.WriteLine("========================================");
                System.Diagnostics.Debug.WriteLine("[CRASH] AppDomain.UnhandledException");
                System.Diagnostics.Debug.WriteLine($"[CRASH] IsTerminating: {e.IsTerminating}");
                System.Diagnostics.Debug.WriteLine($"[CRASH] Exception: {ex?.GetType().FullName}: {ex?.Message}");
                System.Diagnostics.Debug.WriteLine($"[CRASH] Stack:\n{ex?.StackTrace}");
                if (ex?.InnerException != null)
                    System.Diagnostics.Debug.WriteLine($"[CRASH] Inner: {ex.InnerException}");
                System.Diagnostics.Debug.WriteLine("========================================");
            };

            TaskScheduler.UnobservedTaskException += (sender, e) =>
            {
                System.Diagnostics.Debug.WriteLine("========================================");
                System.Diagnostics.Debug.WriteLine("[CRASH] TaskScheduler.UnobservedTaskException");
                System.Diagnostics.Debug.WriteLine($"[CRASH] Exception: {e.Exception?.GetType().FullName}: {e.Exception?.Message}");
                System.Diagnostics.Debug.WriteLine($"[CRASH] Stack:\n{e.Exception?.StackTrace}");
                System.Diagnostics.Debug.WriteLine("[CRASH] (Observed to prevent process tear-down in some hosts)");
                e.SetObserved();
                System.Diagnostics.Debug.WriteLine("========================================");
            };

            System.Diagnostics.Debug.WriteLine("[Diagnostics] Global exception handlers installed (AppDomain + TaskScheduler)");
        }

        public static MauiApp CreateMauiApp()
        {
            // Install handlers BEFORE any other code that might throw unobserved exceptions
            // (Firebase init, timer services, cloud REST, WebView bridges, etc.).
            SetupGlobalExceptionHandling();

            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .UseBarcodeReader()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                    fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                })
                .ConfigureMauiHandlers(handlers =>
                {
#if ANDROID
                    handlers.AddHandler<DragRow, TurfTime2.Platforms.Android.DragRowHandler>();
#elif IOS
                    handlers.AddHandler<DragRow, TurfTime2.Platforms.iOS.DragRowHandler>();
#endif
                });

            // Register Firebase services
            builder.Services.AddSingleton(_ => CrossFirebaseAuth.Current);
            builder.Services.AddSingleton(_ => CrossFirebaseCloudMessaging.Current);

            // Register native game services and view-model
            builder.Services.AddSingleton<Services.ISessionStorageService, Services.SessionStorageService>();
            builder.Services.AddSingleton<Services.ICloudRosterService,    Services.CloudRosterService>();
            builder.Services.AddTransient<Services.IGameTimerService,      Services.GameTimerService>();
            builder.Services.AddTransient<Services.IGameLoggerService,     Services.GameLoggerService>();
            builder.Services.AddTransient<ViewModels.GameViewModel>();
            builder.Services.AddTransient<GamePage>();

            // Initialize Firebase
            builder.ConfigureLifecycleEvents(events =>
            {
#if ANDROID
                events.AddAndroid(android => android
                    .OnCreate((activity, bundle) =>
                    {
                        CrossFirebase.Initialize(activity, new CrossFirebaseSettings(
                            isAuthEnabled: true,
                            isCloudMessagingEnabled: true,
                            isCrashlyticsEnabled: false));

                        System.Diagnostics.Debug.WriteLine("[Firebase] ✅ Initialized on Android (Crashlytics disabled)");
                    }));
#elif IOS
                events.AddiOS(iOS => iOS
                    .FinishedLaunching((app, options) =>
                    {
                        // === iOS Firebase diagnostic block (critical for launch crashes) ===
                        // The native FIRApp.configure (called inside CrossFirebase.Initialize / Plugin.Firebase)
                        // REQUIRES GoogleService-Info.plist to be present at the ROOT of the .app bundle
                        // with LogicalName "GoogleService-Info.plist" and a matching BUNDLE_ID.
                        // Even if the .csproj declares it correctly, Pair-to-Mac / remote builds can
                        // drop the resource. This check + try/catch turns the previous opaque
                        // "+[FIRApp configure] threw NSException → SIGABRT" into actionable logs.
                        try
                        {
                            var plistPath = NSBundle.MainBundle.PathForResource("GoogleService-Info", "plist");
                            bool plistExists = !string.IsNullOrEmpty(plistPath) && System.IO.File.Exists(plistPath);

                            System.Diagnostics.Debug.WriteLine("========================================");
                            System.Diagnostics.Debug.WriteLine("[iOS Firebase] DIAGNOSTIC — GoogleService-Info.plist check at FinishedLaunching");
                            System.Diagnostics.Debug.WriteLine($"[iOS Firebase] NSBundle.MainBundle.PathForResource(\"GoogleService-Info\", \"plist\") = {plistPath ?? "(null)"}");
                            System.Diagnostics.Debug.WriteLine($"[iOS Firebase] File.Exists on that path: {plistExists}");
                            System.Diagnostics.Debug.WriteLine($"[iOS Firebase] Bundle identifier reported by NSBundle: {NSBundle.MainBundle.BundleIdentifier}");
                            if (!plistExists)
                            {
                                System.Diagnostics.Debug.WriteLine("[iOS Firebase] ⚠️⚠️⚠️ CRITICAL: plist MISSING from bundle root. FIRApp.configure WILL throw.");
                                System.Diagnostics.Debug.WriteLine("[iOS Firebase] Fix: ensure BundleResource with LogicalName=GoogleService-Info.plist in .csproj survived the build.");
                            }
                            System.Diagnostics.Debug.WriteLine("========================================");

                            CrossFirebase.Initialize(new CrossFirebaseSettings(
                                isAuthEnabled: true,
                                isCloudMessagingEnabled: true,
                                isCrashlyticsEnabled: false));

                            // Required for iOS push: wires UNUserNotificationCenter + remote registration.
                            // Without this, iOS never shows the notification permission prompt and Settings
                            // will not offer a Notifications toggle for Turf Time.
                            FirebaseCloudMessagingImplementation.Initialize();

                            System.Diagnostics.Debug.WriteLine("[Firebase] ✅ Initialized on iOS (Crashlytics disabled + FCM ready)");
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine("========================================");
                            System.Diagnostics.Debug.WriteLine("[iOS Firebase] ❌ EXCEPTION during CrossFirebase.Initialize / FIRApp.configure");
                            System.Diagnostics.Debug.WriteLine($"[iOS Firebase] {ex.GetType().FullName}: {ex.Message}");
                            System.Diagnostics.Debug.WriteLine($"[iOS Firebase] Stack:\n{ex.StackTrace}");
                            if (ex.InnerException != null)
                                System.Diagnostics.Debug.WriteLine($"[iOS Firebase] Inner: {ex.InnerException}");
                            System.Diagnostics.Debug.WriteLine("[iOS Firebase] This is the most common cause of immediate post-deploy SIGABRT on iOS simulator.");
                            System.Diagnostics.Debug.WriteLine("========================================");
                            // Re-throw so we still get the normal crash report with this context in the log,
                            // but at least we have the diagnostic printed first.
                            throw;
                        }

                        return true;
                    }));
#endif
            });

#if DEBUG
            builder.Logging.AddDebug();
#endif

#if ANDROID && DEBUG
            // Bypass SSL certificate validation on Android emulators whose system CA
            // trust store lacks Google Trust Services root certificates.
            // This only affects Debug builds and is never included in Release.
            Microsoft.Maui.Handlers.WebViewHandler.Mapper.AppendToMapping("DebugSslBypass", (handler, view) =>
            {
                handler.PlatformView.SetWebViewClient(new DebugSslWebViewClient());
            });
#endif

            return builder.Build();
        }
    }
}
