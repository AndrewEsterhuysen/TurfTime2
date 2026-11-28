namespace TurfTime2.Services;

/// <summary>
/// Windows-only Firebase project settings for the REST layer (Identity Toolkit, securetoken, Firestore).
/// Values come from the iOS <c>GoogleService-Info.plist</c> that is already in the repo (Firebase web API
/// keys identify the project; they are not secrets — access is decided by <c>firestore.rules</c>).
/// </summary>
internal static class WindowsFirebaseConfig
{
    public const string ProjectId = "turf-timer";

    /// <summary>API_KEY from Platforms/iOS/GoogleService-Info.plist.</summary>
    public const string ApiKey = "AIzaSyAgOvpYni34tTa60ozjBQ4CNWddH8n6-CE";

    /// <summary>
    /// That key is an iOS-app key; if it is restricted to the iOS bundle id, Google rejects REST calls
    /// that don't present it. Remove this (and use a dedicated Windows/web key) if the key is ever re-scoped.
    /// </summary>
    public const string IosBundleId = "com.andrewestherhuysen.turftime";

    public const string IdentityToolkitSignUp = "https://identitytoolkit.googleapis.com/v1/accounts:signUp?key=" + ApiKey;
    public const string SecureTokenRefresh = "https://securetoken.googleapis.com/v1/token?key=" + ApiKey;

    public const string DocumentsRoot = "projects/" + ProjectId + "/databases/(default)/documents";
    public const string DocumentsBaseUrl = "https://firestore.googleapis.com/v1/" + DocumentsRoot;
}
