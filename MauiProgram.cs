using Microsoft.Extensions.Logging;

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
