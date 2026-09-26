using System.Text.Json;

namespace TurfTime2.Helpers;

/// <summary>
/// Tracks unread team chat for the Chat tab title, per-team strip flags, and the app-icon badge.
/// Per-team flags are cleared only when that team's conversation is viewed.
/// </summary>
public static class ChatBadgeHelper
{
	private const string UnreadCountKey = "chat_unread_count";
	private const string UnreadTeamsKey = "chat_unread_team_ids";
	private const string LastReadPrefix = "chat_last_read_utc_";
	public const string ChatSelectedTeamIdKey = "chat_selected_team_id";

	/// <summary>True while ChatPage is on screen (appearing and not disappeared).</summary>
	public static bool IsChatVisible { get; private set; }

	/// <summary>Team id whose conversation is open on Chat (may differ from Game team).</summary>
	public static string? VisibleChatTeamId { get; private set; }

	public static event Action? Changed;

	public static int UnreadCount => Math.Max(0, Preferences.Get(UnreadCountKey, 0));

	public static void SetChatVisible(bool visible, string? teamId = null)
	{
		IsChatVisible = visible;
		VisibleChatTeamId = visible ? teamId : null;
		if (visible && !string.IsNullOrWhiteSpace(teamId))
			MarkRead(teamId);
	}

	/// <summary>Mark one team's chat as read and recompute the global badge sum.</summary>
	public static void MarkRead(string teamId)
	{
		if (!string.IsNullOrWhiteSpace(teamId))
		{
			Preferences.Set(LastReadPrefix + teamId, DateTimeOffset.UtcNow.ToString("o"));
			ClearTeamUnreadFlag(teamId);
		}

		RecomputeGlobalCountFromFlags();
	}

	/// <summary>
	/// Recompute unread for a team from the latest snapshot when that conversation is not visible.
	/// </summary>
	public static void UpdateFromMessages(string teamId, IReadOnlyList<Services.ChatMessage> messages)
	{
		if (string.IsNullOrWhiteSpace(teamId))
			return;

		if (IsChatVisible && string.Equals(VisibleChatTeamId, teamId, StringComparison.Ordinal))
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

		if (unread > 0)
			SetTeamUnreadFlag(teamId, true);
		else
			ClearTeamUnreadFlag(teamId);

		RecomputeGlobalCountFromFlags();
	}

	/// <summary>Called when an FCM chat push arrives while the user is not viewing that team chat.</summary>
	public static void IncrementFromPush(string? teamId = null)
	{
		// Already looking at this team's conversation — Firestore snapshot handles it.
		if (IsChatVisible
		    && !string.IsNullOrWhiteSpace(teamId)
		    && string.Equals(VisibleChatTeamId, teamId, StringComparison.Ordinal))
			return;

		if (!string.IsNullOrWhiteSpace(teamId))
		{
			SetTeamUnreadFlag(teamId, true);
			RecomputeGlobalCountFromFlags();
			return;
		}

		// No teamId in payload: still bump the tab badge, but cannot light a strip chip.
		if (IsChatVisible)
			return;

		SetCount(UnreadCount + 1);
	}

	public static bool HasUnread(string teamId)
	{
		if (string.IsNullOrWhiteSpace(teamId)) return false;
		return GetUnreadTeamIds().Contains(teamId);
	}

	public static IReadOnlyList<string> GetUnreadTeamIds()
	{
		try
		{
			var json = Preferences.Get(UnreadTeamsKey, "[]");
			return JsonSerializer.Deserialize<List<string>>(json) ?? [];
		}
		catch
		{
			return [];
		}
	}

	public static DateTimeOffset GetLastRead(string teamId)
	{
		var raw = Preferences.Get(LastReadPrefix + teamId, string.Empty);
		if (DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
			    System.Globalization.DateTimeStyles.RoundtripKind, out var dto))
			return dto.ToUniversalTime();
		return DateTimeOffset.MinValue;
	}

	private static void SetTeamUnreadFlag(string teamId, bool unread)
	{
		var set = GetUnreadTeamIds().ToHashSet(StringComparer.Ordinal);
		if (unread)
		{
			if (!set.Add(teamId))
			{
				try { Changed?.Invoke(); } catch { /* ignore */ }
				return;
			}
		}
		else
		{
			if (!set.Remove(teamId))
			{
				try { Changed?.Invoke(); } catch { /* ignore */ }
				return;
			}
		}

		Preferences.Set(UnreadTeamsKey, JsonSerializer.Serialize(set.ToList()));
		try { Changed?.Invoke(); }
		catch { /* ignore */ }
	}

	private static void ClearTeamUnreadFlag(string teamId)
		=> SetTeamUnreadFlag(teamId, false);

	private static void RecomputeGlobalCountFromFlags()
	{
		var n = GetUnreadTeamIds().Count;
		SetCount(Math.Max(n, 0));
	}

	private static void SetCount(int count)
	{
		count = Math.Max(0, count);
		var prev = Preferences.Get(UnreadCountKey, 0);
		if (prev == count)
		{
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
					if (count <= 0)
					{
						var context = Android.App.Application.Context;
						var mgr = AndroidX.Core.App.NotificationManagerCompat.From(context);
						mgr.CancelAll();
					}
#endif
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
	public static void OpenChat(string? teamId = null)
	{
		if (!string.IsNullOrWhiteSpace(teamId))
			Preferences.Set(ChatBadgeHelper.ChatSelectedTeamIdKey, teamId.Trim());

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
