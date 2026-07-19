using System.Text.Json;
using TurfTime2.Models;
using TurfTime2.Services;

namespace TurfTime2;

/// <summary>
/// Legacy JS roster save bridge — now routes through <see cref="ICloudRosterService"/> (Plugin.Firebase).
/// Prefer calling ICloudRosterService from C# directly for new code.
/// </summary>
public static class FirebaseSaveBridge
{
    /// <summary>No-op — auth is owned by <see cref="IFirebaseAuthService"/>.</summary>
    public static void SetAuthToken(string idToken, string userId)
    {
        System.Diagnostics.Debug.WriteLine("[FirebaseBridge] SetAuthToken ignored (SDK auth)");
    }

    public static async Task<string> SaveRosterToFirestore(string teamId, string rosterDataJson)
    {
        try
        {
            System.Diagnostics.Debug.WriteLine($"[FirebaseBridge] SaveRoster (SDK) for team: {teamId}");
            if (string.IsNullOrWhiteSpace(teamId) || teamId.StartsWith("local_", StringComparison.Ordinal))
                return "ok:local";

            var services = Application.Current?.Handler?.MauiContext?.Services;
            var rosterSvc = services?.GetService<ICloudRosterService>();
            if (rosterSvc is null)
                return "error:no_service";

            var snapshot = JsonSerializer.Deserialize<RosterSnapshot>(rosterDataJson);
            if (snapshot is null)
                return "error:bad_json";

            await rosterSvc.ForceSyncAsync(teamId, snapshot).ConfigureAwait(false);
            return "ok";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FirebaseBridge] SaveRoster: {ex.Message}");
            return $"error:{ex.Message}";
        }
    }
}
