namespace TurfTime2.Helpers;

/// <summary>
/// The Windows build is a setup-only client for club back-office staff (clubs, teams, players, kit, duties).
/// Game play, chat, nominations, push and QR scanning stay phone-only. Shared pages use this to hide or explain
/// those parts without <c>#if</c> blocks. Always false on iOS and Android.
/// </summary>
internal static class DesktopMode
{
    public static bool IsWindows => DeviceInfo.Platform == DevicePlatform.WinUI;

    /// <summary>
    /// Phone: the normal share sheet (unchanged). Windows: copy the text to the clipboard and say so — back-office
    /// staff paste it into email, and the Windows share pane is unreliable for unpackaged apps.
    /// </summary>
    public static async Task ShareOrCopyTextAsync(Page page, string title, string text)
    {
        if (!IsWindows)
        {
            await Share.Default.RequestAsync(new ShareTextRequest { Title = title, Text = text });
            return;
        }
        await Clipboard.Default.SetTextAsync(text);
        await page.DisplayAlertAsync(title, "Copied to the clipboard. Paste it into an email or message.\n\n" + text, "OK");
    }

    /// <summary>Explain why an action isn't available in the Windows app, and what to do instead.</summary>
    public static Task ShowUnsupportedAsync(Page page, string feature, string? instead = null)
        => page.DisplayAlertAsync(
            "Not available on Windows",
            instead is null
                ? $"{feature} is only available in the phone app."
                : $"{feature} is only available in the phone app.\n\n{instead}",
            "OK");
}
