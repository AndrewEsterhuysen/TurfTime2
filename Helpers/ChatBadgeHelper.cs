namespace TurfTime2.Helpers;

/// <summary>
/// Tracks unread team chat for the Chat tab title and the app-icon badge.
/// Cleared when the Chat page is opened / visible.
/// </summary>
public static class ChatBadgeHelper
{
	private const string UnreadCountKey = "chat_unread_count";
	private const string LastReadPrefix = "chat_last_read_utc_";

	/// <summary>True while ChatPage is on screen (appearing and not disappeared).</summary>
	public static bool IsChatVisible { get; private set; }

	public static event Action? Changed;

	public static int UnreadCount => Math.Max(0, Preferences.Get(UnreadCountKey, 0));

	public static void SetChatVisible(bool visible, string? teamId = null)
	{
		IsChatVisible = visible;
		if (visible)
			MarkRead(teamId ?? Preferences.Get("team_id", string.Empty));
	}

	public static void MarkRead(string teamId)
	{
		if (!string.IsNullOrWhiteSpace(teamId))
			Preferences.Set(LastReadPrefix + teamId, DateTimeOffset.UtcNow.ToString("o"));

		SetCount(0);
	}

	/// <summary>
	/// Recompute unread from the latest snapshot when chat is not visible.
	/// Counts others' non-deleted messages after last-read watermark.
	/// </summary>
	public static void UpdateFromMessages(string teamId, IReadOnlyList<Services.ChatMessage> messages)
	{
		if (string.IsNullOrWhiteSpace(teamId))
			return;

		if (IsChatVisible)
		{
			MarkRead(teamId);
			return;
		}

		var lastRead = GetLastRead(teamId);
		var unread = 0;
		foreach (var m in messages)
		{
			if (m.IsMine || m.IsDeleted) continue;
			if (m.Timestamp is null) continue;
			if (m.Timestamp > lastRead)
				unread++;
		}

		SetCount(unread);
	}

	/// <summary>Called when an FCM chat push arrives while the user is not on Chat.</summary>
	public static void IncrementFromPush()
	{
		if (IsChatVisible)
			return;
		SetCount(UnreadCount + 1);
	}

	public static DateTimeOffset GetLastRead(string teamId)
	{
		var raw = Preferences.Get(LastReadPrefix + teamId, string.Empty);
		if (DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
			    System.Globalization.DateTimeStyles.RoundtripKind, out var dto))
			return dto.ToUniversalTime();
		return DateTimeOffset.MinValue;
	}

	private static void SetCount(int count)
	{
		count = Math.Max(0, count);
		var prev = Preferences.Get(UnreadCountKey, 0);
		if (prev == count)
		{
			// Still apply badge on cold start so icon matches prefs.
			ApplyIconBadge(count);
			return;
		}

		Preferences.Set(UnreadCountKey, count);
		ApplyIconBadge(count);
		try { Changed?.Invoke(); }
		catch { /* ignore subscriber errors */ }
	}

	public static void ApplyIconBadge(int count)
	{
		count = Math.Max(0, count);
		try
		{
			MainThread.BeginInvokeOnMainThread(() =>
			{
				try
				{
#if IOS
					UIKit.UIApplication.SharedApplication.ApplicationIconBadgeNumber = count;
#elif ANDROID
					// Android launcher badges are OEM-specific; clearing posted chat
					// notifications when count hits 0 is the portable approach.
					if (count <= 0)
					{
						var context = Android.App.Application.Context;
						var mgr = AndroidX.Core.App.NotificationManagerCompat.From(context);
						mgr.CancelAll();
					}
#endif
					// Always notify shell tab title listeners
				}
				catch (Exception ex)
				{
					System.Diagnostics.Debug.WriteLine($"[ChatBadge] icon: {ex.Message}");
				}
			});
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"[ChatBadge] ApplyIconBadge: {ex.Message}");
		}
	}

	/// <summary>Tab title with optional unread count.</summary>
	public static string ChatTabTitle =>
		UnreadCount > 0 ? $"Chat ({UnreadCount})" : "Chat";
}

/// <summary>Central navigation into the Chat tab (notifications, deep links).</summary>
public static class ChatNavigation
{
	public static void OpenChat()
	{
		_ = MainThread.InvokeOnMainThreadAsync(async () =>
		{
			try
			{
				if (Shell.Current is not null)
					await Shell.Current.GoToAsync("//ChatPage");
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"[ChatNav] {ex.Message}");
			}
		});
	}
}
