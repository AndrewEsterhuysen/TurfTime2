namespace TurfTime2.Services;

/// <summary>
/// App-wide Firebase Auth. Prefer this over REST identitytoolkit sign-up so every
/// cloud feature shares one durable anonymous uid on the device.
/// </summary>
public interface IFirebaseAuthService
{
    /// <summary>Firebase Auth uid of the signed-in user, or null if not signed in.</summary>
    string? UserId { get; }

    /// <summary>True when a Firebase user is signed in (anonymous or otherwise).</summary>
    bool IsSignedIn { get; }

    /// <summary>
    /// Ensures an anonymous Firebase user exists and returns the uid.
    /// Safe to call repeatedly; reuses the persisted session.
    /// </summary>
    Task<string?> EnsureSignedInAsync();

    /// <summary>Returns a fresh ID token for the current user, or null.</summary>
    Task<string?> GetIdTokenAsync(bool forceRefresh = false);
}
