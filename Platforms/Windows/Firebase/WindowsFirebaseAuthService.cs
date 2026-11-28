using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TurfTime2.Services;

/// <summary>
/// Windows-only <see cref="IFirebaseAuthService"/>: Firebase Auth over REST (Identity Toolkit + securetoken).
/// A Windows install is its own anonymous user. The uid + refresh token are kept on disk, encrypted with the
/// Windows user's DPAPI key, so the same identity is reused on every launch.
/// A new anonymous user is only created when there is no stored identity or the server says the stored one is
/// gone for good (never on a transient network error, so a flaky connection can't silently change who you are).
/// </summary>
public sealed class WindowsFirebaseAuthService : IFirebaseAuthService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _loaded;
    private string? _uid;
    private string? _refreshToken;
    private string? _idToken;
    private DateTimeOffset _idTokenExpiry = DateTimeOffset.MinValue;

    public string? UserId => _uid;
    public bool IsSignedIn => !string.IsNullOrEmpty(_uid);

    private static string StorePath => Path.Combine(FileSystem.AppDataDirectory, "firebase-auth.bin");

    public async Task<string?> EnsureSignedInAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await EnsureLockedAsync(forceRefresh: false).ConfigureAwait(false) ? _uid : null;
        }
        finally { _gate.Release(); }
    }

    public async Task<string?> GetIdTokenAsync(bool forceRefresh = false)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await EnsureLockedAsync(forceRefresh).ConfigureAwait(false) ? _idToken : null;
        }
        finally { _gate.Release(); }
    }

    private async Task<bool> EnsureLockedAsync(bool forceRefresh)
    {
        try
        {
            if (!_loaded) { LoadFromDisk(); _loaded = true; }

            if (!string.IsNullOrEmpty(_uid) && !string.IsNullOrEmpty(_refreshToken))
            {
                var fresh = !string.IsNullOrEmpty(_idToken) && _idTokenExpiry > DateTimeOffset.UtcNow.AddMinutes(2);
                if (fresh && !forceRefresh) return true;

                var refreshed = await RefreshAsync().ConfigureAwait(false);
                if (refreshed == RefreshResult.Ok) return true;
                if (refreshed == RefreshResult.Transient) return false;
                // Definitively gone (user deleted / token revoked): fall through to a new anonymous identity.
                System.Diagnostics.Debug.WriteLine("[WinAuth] Stored identity rejected by server — creating a new anonymous user.");
                _uid = null; _refreshToken = null; _idToken = null;
            }

            return await SignUpAnonymousAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WinAuth] ❌ {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> SignUpAnonymousAsync()
    {
        using var req = NewRequest(WindowsFirebaseConfig.IdentityToolkitSignUp, "{\"returnSecureToken\":true}", "application/json");
        using var resp = await Http.SendAsync(req).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            System.Diagnostics.Debug.WriteLine($"[WinAuth] signUp failed {(int)resp.StatusCode}: {Trim(text)}");
            return false;
        }
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        _uid = root.GetProperty("localId").GetString();
        _idToken = root.GetProperty("idToken").GetString();
        _refreshToken = root.GetProperty("refreshToken").GetString();
        _idTokenExpiry = DateTimeOffset.UtcNow.AddSeconds(ParseSeconds(root, "expiresIn"));
        SaveToDisk();
        Mirror();
        System.Diagnostics.Debug.WriteLine($"[WinAuth] ✅ New anonymous user {Short(_uid)}…");
        return !string.IsNullOrEmpty(_uid) && !string.IsNullOrEmpty(_idToken);
    }

    private enum RefreshResult { Ok, Transient, Rejected }

    private async Task<RefreshResult> RefreshAsync()
    {
        try
        {
            var body = "grant_type=refresh_token&refresh_token=" + Uri.EscapeDataString(_refreshToken!);
            using var req = NewRequest(WindowsFirebaseConfig.SecureTokenRefresh, body, "application/x-www-form-urlencoded");
            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                System.Diagnostics.Debug.WriteLine($"[WinAuth] refresh failed {(int)resp.StatusCode}: {Trim(text)}");
                var gone = resp.StatusCode == HttpStatusCode.BadRequest
                           && (text.Contains("TOKEN_EXPIRED") || text.Contains("USER_NOT_FOUND")
                               || text.Contains("USER_DISABLED") || text.Contains("INVALID_REFRESH_TOKEN"));
                return gone ? RefreshResult.Rejected : RefreshResult.Transient;
            }
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            _idToken = root.GetProperty("id_token").GetString();
            if (root.TryGetProperty("refresh_token", out var rt) && !string.IsNullOrEmpty(rt.GetString()))
                _refreshToken = rt.GetString();
            if (root.TryGetProperty("user_id", out var uid) && !string.IsNullOrEmpty(uid.GetString()))
                _uid = uid.GetString();
            _idTokenExpiry = DateTimeOffset.UtcNow.AddSeconds(ParseSeconds(root, "expires_in"));
            SaveToDisk();
            Mirror();
            return RefreshResult.Ok;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            System.Diagnostics.Debug.WriteLine($"[WinAuth] refresh network error: {ex.Message}");
            return RefreshResult.Transient;
        }
    }

    private static HttpRequestMessage NewRequest(string url, string body, string contentType)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, contentType) };
        // The Firebase key in the repo is the iOS app's key; present the app id so key restrictions accept it.
        req.Headers.TryAddWithoutValidation("X-Ios-Bundle-Identifier", WindowsFirebaseConfig.IosBundleId);
        return req;
    }

    private void Mirror()
    {
        // Same Preferences the Plugin.Firebase-based service keeps, so the rest of the app sees one uid.
        if (string.IsNullOrEmpty(_uid)) return;
        if (!string.Equals(Preferences.Get("user_id", string.Empty), _uid, StringComparison.Ordinal)) Preferences.Set("user_id", _uid);
        if (!string.Equals(Preferences.Get("chat_user_id", string.Empty), _uid, StringComparison.Ordinal)) Preferences.Set("chat_user_id", _uid);
    }

    private void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(StorePath)) return;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(StorePath), null, DataProtectionScope.CurrentUser);
            using var doc = JsonDocument.Parse(plain);
            _uid = doc.RootElement.GetProperty("uid").GetString();
            _refreshToken = doc.RootElement.GetProperty("refreshToken").GetString();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WinAuth] Could not read stored identity: {ex.Message}");
            _uid = null; _refreshToken = null;
        }
    }

    private void SaveToDisk()
    {
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(new { uid = _uid, refreshToken = _refreshToken });
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllBytes(StorePath, ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WinAuth] Could not store identity: {ex.Message}");
        }
    }

    private static double ParseSeconds(JsonElement root, string name)
        => root.TryGetProperty(name, out var e) && double.TryParse(e.GetString(), System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 3300;

    private static string Short(string? s) => string.IsNullOrEmpty(s) ? "" : s[..Math.Min(8, s.Length)];
    private static string Trim(string s) => s.Length <= 300 ? s : s[..300];
}
