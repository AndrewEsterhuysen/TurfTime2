namespace TurfTime2;

/// <summary>
/// Helper class to manage cloud sync operations across the app
/// </summary>
public static class CloudSyncHelper
{
    // Event that gets raised when manual sync is requested
    public static event EventHandler? ManualSyncRequested;

    /// <summary>
    /// Request a manual cloud sync from anywhere in the app
    /// </summary>
    public static void RequestManualSync()
    {
        System.Diagnostics.Debug.WriteLine("[CloudSync] Manual sync requested");
        ManualSyncRequested?.Invoke(null, EventArgs.Empty);
    }
}
