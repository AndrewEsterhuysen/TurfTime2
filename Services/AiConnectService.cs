using System.Net.Http.Headers;
using System.Text.Json;

namespace TurfTime2.Services;

/// <summary>
/// "Connect AI assistant" (MCP; read-only or read-and-write, chosen on the consent page). The app proves the user's identity to the MCP server with
/// its own Firebase ID token and receives a short-lived one-time link code that the user types on the
/// server's authorize page. The AI assistant never supplies a user name or uid.
/// Hidden in the UI while <see cref="BaseUrl"/> is empty (i.e. until the server is deployed).
/// </summary>
public static class AiConnectConfig
{
    // Cloud Run service turftimer-mcp (OAuth issuer URL; do not use the -846410659178 alias).
    public const string BaseUrl = "https://turftimer-mcp-co6qd7jw6q-ts.a.run.app";
    public static bool IsEnabled => !string.IsNullOrWhiteSpace(BaseUrl);
}

public sealed record AiLinkCode(string Code, int ExpiresInSeconds);
public sealed record AiGrant(string Id, string ClientName, string LastUsedAt);

public sealed class AiConnectService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly IFirebaseAuthService _auth;

    public AiConnectService(IFirebaseAuthService auth) => _auth = auth;

    private async Task<HttpRequestMessage> RequestAsync(HttpMethod method, string path)
    {
        await _auth.EnsureSignedInAsync().ConfigureAwait(false);
        var token = await _auth.GetIdTokenAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("Not signed in");
        var req = new HttpRequestMessage(method, AiConnectConfig.BaseUrl.TrimEnd('/') + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    public async Task<AiLinkCode> CreateLinkCodeAsync()
    {
        using var req = await RequestAsync(HttpMethod.Post, "/link/code").ConfigureAwait(false);
        using var resp = await Http.SendAsync(req).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
        return new AiLinkCode(doc.RootElement.GetProperty("code").GetString() ?? "",
            doc.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 600);
    }

    public async Task<IReadOnlyList<AiGrant>> ListGrantsAsync()
    {
        using var req = await RequestAsync(HttpMethod.Get, "/link/grants").ConfigureAwait(false);
        using var resp = await Http.SendAsync(req).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
        var list = new List<AiGrant>();
        foreach (var g in doc.RootElement.GetProperty("grants").EnumerateArray())
            list.Add(new AiGrant(g.GetProperty("id").GetString() ?? "",
                g.GetProperty("client_name").GetString() ?? "AI assistant",
                g.TryGetProperty("last_used_at", out var lu) ? lu.GetString() ?? "" : ""));
        return list;
    }

    public async Task RevokeAsync(string grantId)
    {
        using var req = await RequestAsync(HttpMethod.Delete, "/link/grants/" + Uri.EscapeDataString(grantId)).ConfigureAwait(false);
        using var resp = await Http.SendAsync(req).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }
}
