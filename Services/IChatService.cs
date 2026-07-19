namespace TurfTime2.Services;

/// <summary>Chat message DTO for native UI (no WebView).</summary>
public sealed class ChatMessage
{
    public string Id { get; init; } = "";
    public string Text { get; init; } = "";
    public string UserId { get; init; } = "";
    public string SenderName { get; init; } = "";
    public DateTimeOffset? Timestamp { get; init; }
    public bool IsMine { get; init; }
}

/// <summary>
/// Team chat over Plugin.Firebase Firestore. Pages depend on this interface only.
/// </summary>
public interface IChatService
{
    /// <summary>Start listening to recent messages for a shared team. Returns IDisposable to stop.</summary>
    Task<IDisposable?> SubscribeAsync(string teamId, Action<IReadOnlyList<ChatMessage>> onMessages, Action<Exception>? onError = null);

    Task SendAsync(string teamId, string text, string senderDisplayName);

    Task UpdateDisplayNameAsync(string teamId, string displayName);

    /// <summary>Writes the device FCM token onto the current user's member document.</summary>
    Task<bool> RegisterFcmTokenAsync(string teamId, string fcmToken);

    Task<IReadOnlyDictionary<string, string>> LoadMemberDisplayNamesAsync(string teamId);
}
