using System.Text.Json;
using TurfTime2.Models;
using TurfTime2.Services;

namespace TurfTime2;

/// <summary>
/// Compatibility helper for ReportsPage — delegates to <see cref="ISessionStorageService"/> (Plugin.Firebase).
/// </summary>
public static class SessionLoadHelper
{
    private static ISessionStorageService? ResolveSessions()
    {
        try
        {
            return Application.Current?.Handler?.MauiContext?.Services.GetService<ISessionStorageService>();
        }
        catch
        {
            return null;
        }
    }

    public static async Task<List<SessionSummary>> LoadSessionsForTeamAsync(string teamId)
    {
        try
        {
            var svc = ResolveSessions();
            if (svc is null)
            {
                System.Diagnostics.Debug.WriteLine("[SessionLoadHelper] ISessionStorageService unavailable");
                return [];
            }

            var isLocal = teamId.StartsWith("local_", StringComparison.Ordinal)
                          || string.Equals(Preferences.Get("team_mode", ""), "local", StringComparison.Ordinal);
            var list = await svc.LoadSessionSummariesAsync(teamId, isLocal).ConfigureAwait(false);
            return list.ToList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SessionLoadHelper] ERROR: {ex.Message}");
            return [];
        }
    }

    public static async Task<string> LoadSessionDataAsync(string teamId, string sessionId)
    {
        try
        {
            var svc = ResolveSessions();
            if (svc is null) return "";

            var session = await svc.LoadSessionAsync(teamId, sessionId).ConfigureAwait(false);
            if (session is null) return "";
            return JsonSerializer.Serialize(session);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SessionLoadHelper] ERROR: {ex.Message}");
            return "";
        }
    }
}
