namespace TurfTime2.Services;

/// <summary>Chat message DTO for native UI (no WebView).</summary>
public sealed class ChatMessage
{
	public string Id { get; set; } = "";
	public string Text { get; set; } = "";
	public string UserId { get; set; } = "";
	public string SenderName { get; set; } = "";
	public DateTimeOffset? Timestamp { get; set; }
	public bool IsMine { get; set; }

	/// <summary>emoji → userIds who reacted with that emoji.</summary>
	public Dictionary<string, List<string>> Reactions { get; set; } = new(StringComparer.Ordinal);

	public string? ReplyToMessageId { get; set; }
	public string? ReplyToText { get; set; }
	public string? ReplyToSenderName { get; set; }

	public bool IsDeleted { get; set; }
	public DateTimeOffset? DeletedAt { get; set; }

	public bool IsPinned { get; set; }
	public DateTimeOffset? PinnedAt { get; set; }
	public string? PinnedBy { get; set; }

	/// <summary>Local-device wall time for display (sent/received).</summary>
	public string LocalTimeText =>
		Timestamp?.ToLocalTime().ToString("t") ?? "";

	public bool HasReply =>
		!string.IsNullOrWhiteSpace(ReplyToMessageId)
		|| !string.IsNullOrWhiteSpace(ReplyToText);

	public IEnumerable<(string Emoji, int Count, bool Mine)> ReactionSummaries(string myUid)
	{
		foreach (var kv in Reactions.OrderBy(k => k.Key, StringComparer.Ordinal))
		{
			var uids = kv.Value ?? [];
			if (uids.Count == 0) continue;
			yield return (kv.Key, uids.Count,
				uids.Any(u => string.Equals(u, myUid, StringComparison.Ordinal)));
		}
	}
}

/// <summary>Optional fields when sending a message.</summary>
public sealed class ChatSendOptions
{
	public string? ReplyToMessageId { get; init; }
	public string? ReplyToText { get; init; }
	public string? ReplyToSenderName { get; init; }
}

/// <summary>
/// Team chat over Plugin.Firebase Firestore. Pages depend on this interface only.
/// </summary>
public interface IChatService
{
	/// <summary>Start listening to recent messages for a shared team. Returns IDisposable to stop.</summary>
	Task<IDisposable?> SubscribeAsync(string teamId, Action<IReadOnlyList<ChatMessage>> onMessages, Action<Exception>? onError = null);

	Task SendAsync(string teamId, string text, string senderDisplayName, ChatSendOptions? options = null);

	/// <summary>Toggle current user's reaction on a message. Pass the same emoji again to remove.</summary>
	Task ToggleReactionAsync(string teamId, string messageId, string emoji);

	/// <summary>Soft-delete for everyone (author or team admin).</summary>
	Task DeleteForEveryoneAsync(string teamId, string messageId);

	/// <summary>Admin/coach pin or unpin a message.</summary>
	Task SetPinnedAsync(string teamId, string messageId, bool pinned);

	Task UpdateDisplayNameAsync(string teamId, string displayName);

	/// <summary>Writes the device FCM token onto the current user's member document.</summary>
	Task<bool> RegisterFcmTokenAsync(string teamId, string fcmToken);

	Task<IReadOnlyDictionary<string, string>> LoadMemberDisplayNamesAsync(string teamId);
}

/// <summary>Curated reaction set (happy/sad/hot/cold/fire/frozen/ack/thumbs/ok and more).</summary>
public static class ChatReactions
{
	public static readonly string[] All =
	[
		"👍", // thumbs up
		"👎", // thumbs down
		"❤️", // love
		"😂", // happy / laugh
		"😊", // smile
		"😢", // sad
		"😔", // down
		"😮", // surprised
		"🔥", // on fire
		"❄️", // frozen / ice
		"🥵", // hot
		"🥶", // cold face
		"✅", // acknowledge / done
		"👌", // ok
		"🤝", // agree
		"🙏", // thanks
		"👏", // clap
		"💪", // strong
		"🎉", // celebrate
		"👀", // seen
		"💯", // 100
		"⚠️"  // attention
	];
}
