using Plugin.Firebase.Auth;

namespace TurfTime2.Services;

/// <summary>
/// Plugin.Firebase.Auth adapter — single durable anonymous session for cloud features.
/// </summary>
public sealed class FirebaseAuthService : IFirebaseAuthService
{
    private readonly IFirebaseAuth _auth;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FirebaseAuthService(IFirebaseAuth auth)
    {
        _auth = auth;
    }

    public string? UserId => _auth.CurrentUser?.Uid;

    public bool IsSignedIn => _auth.CurrentUser != null;

    public async Task<string?> EnsureSignedInAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_auth.CurrentUser is { } existing && !string.IsNullOrEmpty(existing.Uid))
            {
                Preferences.Set("user_id", existing.Uid);
                Preferences.Set("chat_user_id", existing.Uid);
                return existing.Uid;
            }

            System.Diagnostics.Debug.WriteLine("[FirebaseAuth] Signing in anonymously via Plugin.Firebase…");
            var user = await _auth.SignInAnonymouslyAsync().ConfigureAwait(false);
            var uid = user?.Uid;
            if (!string.IsNullOrEmpty(uid))
            {
                Preferences.Set("user_id", uid);
                Preferences.Set("chat_user_id", uid);
                System.Diagnostics.Debug.WriteLine($"[FirebaseAuth] ✅ Signed in as {uid[..Math.Min(8, uid.Length)]}…");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[FirebaseAuth] ❌ SignInAnonymouslyAsync returned no uid");
            }

            return uid;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FirebaseAuth] ❌ EnsureSignedInAsync: {ex.GetType().FullName}: {ex.Message}");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> GetIdTokenAsync(bool forceRefresh = false)
    {
        try
        {
            await EnsureSignedInAsync().ConfigureAwait(false);
            var user = _auth.CurrentUser;
            if (user is null)
                return null;

            var result = await user.GetIdTokenResultAsync(forceRefresh).ConfigureAwait(false);
            return result?.Token;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FirebaseAuth] ❌ GetIdTokenAsync: {ex.Message}");
            return null;
        }
    }
}
