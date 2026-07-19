using System.Text.Json;
using TurfTime2.Models;
using TurfTime2.Services;

namespace TurfTime2;

/// <summary>
/// Legacy JS session save bridge — routes through <see cref="ISessionStorageService"/>.
/// </summary>
public static class SessionSaveBridge
{
    [System.Runtime.Versioning.SupportedOSPlatform("android")]
    [System.Runtime.Versioning.SupportedOSPlatform("ios")]
    [System.Runtime.Versioning.SupportedOSPlatform("maccatalyst")]
    public static async void SaveSessionToFirestore(string jsonData)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(jsonData)) return;

            using var data = JsonDocument.Parse(jsonData);
            var root = data.RootElement;
            if (!root.TryGetProperty("teamId", out var teamIdEl)) return;
            if (!root.TryGetProperty("sessionData", out var sessionEl)) return;

            var teamId = teamIdEl.GetString();
            var sessionJson = sessionEl.GetString();
            if (string.IsNullOrEmpty(teamId) || string.IsNullOrEmpty(sessionJson)) return;

            var session = JsonSerializer.Deserialize<GameSession>(sessionJson);
            if (session is null) return;

            var services = Application.Current?.Handler?.MauiContext?.Services;
            var storage = services?.GetService<ISessionStorageService>();
            if (storage is null)
            {
                System.Diagnostics.Debug.WriteLine("[SessionSaveBridge] ISessionStorageService unavailable");
                return;
            }

            await storage.SaveSessionAsync(teamId, session).ConfigureAwait(false);
            System.Diagnostics.Debug.WriteLine($"[SessionSaveBridge] ✅ Session saved via SDK for {teamId}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SessionSaveBridge] ERROR: {ex.Message}");
        }
    }
}
