#if DEBUG
using Android.Net.Http;
using Android.Webkit;

namespace TurfTime2;

/// <summary>
/// Debug-only WebViewClient that bypasses SSL certificate errors.
/// This allows testing Firebase and other HTTPS services on Android emulators
/// that may have outdated system CA trust stores.
/// WARNING: Never use in Release builds.
/// </summary>
internal class DebugSslWebViewClient : WebViewClient
{
    public override void OnReceivedSslError(Android.Webkit.WebView? view, SslErrorHandler? handler, SslError? error)
    {
        handler?.Proceed();
    }
}
#endif
