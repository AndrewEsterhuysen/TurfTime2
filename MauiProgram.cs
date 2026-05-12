using Microsoft.Extensions.Logging;
using Microsoft.Maui.LifecycleEvents;
using Plugin.Firebase.Auth;
using Plugin.Firebase.Bundled.Shared;
using Plugin.Firebase.CloudMessaging;
#if ANDROID
using Plugin.Firebase.Bundled.Platforms.Android;
#elif IOS
using Plugin.Firebase.Bundled.Platforms.iOS;
#endif

namespace TurfTime2
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                    fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                })
                .ConfigureMauiHandlers(handlers =>
                {
#if ANDROID
                    handlers.AddHandler<DragRow, TurfTime2.Platforms.Android.DragRowHandler>();
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
                        CrossFirebase.Initialize(new CrossFirebaseSettings(
                            isAuthEnabled: true,
                            isCloudMessagingEnabled: true,
                            isCrashlyticsEnabled: false));

                        System.Diagnostics.Debug.WriteLine("[Firebase] ✅ Initialized on iOS (Crashlytics disabled)");
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
