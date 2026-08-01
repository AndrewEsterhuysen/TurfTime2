using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Microsoft.Maui.Controls.Shapes;
using TurfTime2.Helpers;
using TurfTime2.Services;
#if IOS
using Foundation;
using UIKit;
#endif

namespace TurfTime2;

public partial class ChatPage : ContentPage
{
	private ObservableCollection<object> _listItems = new();
	private readonly List<ChatMessage> _messages = new();
	private readonly Dictionary<string, string> _memberNames = new(StringComparer.Ordinal);
	private IChatService? _chat;
	private IFirebaseAuthService? _auth;
	private IDisposable? _subscription;
	private string _teamId = string.Empty;
	private string _myUid = string.Empty;
	private int _fcmRegisterInFlight;
	private bool _tabBarHiddenForInput;
	private bool _isAdmin;
	private ChatMessage? _replyTo;
	private ChatMessage? _pinned;
	private DateTime _pointerDownUtc;
	private ChatMessage? _pointerMessage;
	private int _rebuildGeneration;

#if IOS
	private NSObject? _keyboardFrameObserver;
#endif

	public ChatPage()
	{
		InitializeComponent();
		MessagesList.ItemsSource = _listItems;
		MessagesList.ItemTemplate = new ChatItemTemplateSelector
		{
			DayTemplate = new DataTemplate(CreateDayHeaderView),
			MessageTemplate = new DataTemplate(CreateMessageView)
		};
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		DetailsPage.ApplyPageTeamTitle(this, "Chat");
		ResolveServices();
		ApplyThemeToInputBar();
		SubscribeKeyboardAvoidance();

		_teamId = Preferences.Get("team_id", string.Empty);
		var mode = Preferences.Get("team_mode", string.Empty);
		_isAdmin = IsCurrentUserAdmin(_teamId);
		UnpinButton.IsVisible = _isAdmin;

		// Clear tab + app-icon unread as soon as Chat is open.
		ChatBadgeHelper.SetChatVisible(true, _teamId);

		if (mode != "shared" || string.IsNullOrEmpty(_teamId) || _teamId.StartsWith("local_"))
		{
			_messages.Clear();
			ReplaceListItems(
			[
				new ChatDayHeader { Label = "Info" },
				new ChatMessage
				{
					Text = "Chat is available for shared (cloud) teams only.",
					SenderName = "Turf Time",
					IsMine = false,
					Timestamp = DateTimeOffset.Now
				}
			]);
			return;
		}

		await EnsureDisplayNameForSharedTeamAsync();
		await StartChatAsync();
		_ = RegisterFcmTokenAsync();
	}

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		ChatBadgeHelper.SetChatVisible(false, _teamId);
		// Still mark read at leave so badge doesn't reappear for already-seen messages.
		if (!string.IsNullOrEmpty(_teamId))
			ChatBadgeHelper.MarkRead(_teamId);

		UnsubscribeKeyboardAvoidance();
		RestoreChatChrome();
		_subscription?.Dispose();
		_subscription = null;
	}

	private static bool IsCurrentUserAdmin(string teamId)
	{
		var role = Preferences.Get("user_role", string.Empty);
		if (string.IsNullOrEmpty(role) && !string.IsNullOrEmpty(teamId))
			role = Preferences.Get($"user_role_{teamId}", string.Empty);
		if (string.IsNullOrEmpty(role) && !string.IsNullOrEmpty(teamId))
			role = Preferences.Get($"{teamId}_role", string.Empty);
		return string.Equals(role, "admin", StringComparison.OrdinalIgnoreCase);
	}

	private void ResolveServices()
	{
		var services = Handler?.MauiContext?.Services
			?? Application.Current?.Handler?.MauiContext?.Services;
		_chat ??= services?.GetService<IChatService>();
		_auth ??= services?.GetService<IFirebaseAuthService>();
	}

	private async Task StartChatAsync()
	{
		if (_chat is null)
		{
			ResolveServices();
			if (_chat is null)
			{
				System.Diagnostics.Debug.WriteLine("[Chat] IChatService not registered");
				return;
			}
		}

		try
		{
			if (_auth is not null)
				_myUid = await _auth.EnsureSignedInAsync() ?? string.Empty;
			if (string.IsNullOrEmpty(_myUid))
				_myUid = Preferences.Get("user_id", string.Empty);

			var names = await _chat.LoadMemberDisplayNamesAsync(_teamId);
			_memberNames.Clear();
			foreach (var kv in names)
				_memberNames[kv.Key] = kv.Value;
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"[Chat] member names: {ex.Message}");
		}

		_subscription?.Dispose();
		_subscription = await _chat.SubscribeAsync(
			_teamId,
			msgs => MainThread.BeginInvokeOnMainThread(() =>
			{
				_messages.Clear();
				foreach (var m in msgs)
				{
					var display = m.IsMine
						? "You"
						: (string.IsNullOrWhiteSpace(m.SenderName) ? "Teammate" : m.SenderName);
					m.SenderName = display;
					_messages.Add(m);
				}

				RebuildList();
				ChatBadgeHelper.UpdateFromMessages(_teamId, _messages);
				ScrollMessagesToEnd(animate: false);
			}),
			ex => System.Diagnostics.Debug.WriteLine($"[Chat] listen error: {ex.Message}"));

		var name = UserDisplayName.Get();
		if (!string.IsNullOrWhiteSpace(name) && _chat is not null)
			await _chat.UpdateDisplayNameAsync(_teamId, name);
	}

	private void RebuildList()
	{
		// Prefer most recently pinned
		_pinned = _messages
			.Where(m => m.IsPinned && !m.IsDeleted)
			.OrderByDescending(m => m.PinnedAt ?? DateTimeOffset.MinValue)
			.FirstOrDefault();
		UpdatePinnedBar();

		// Build a complete snapshot, then swap ItemsSource atomically.
		// CollectionView.Clear() mid-layout was causing iOS:
		// "invalid index path ... data source counts: [(0:0)]" and a broken Chat list.
		var next = new List<object>(_messages.Count + 8);
		DateTime? lastDay = null;
		foreach (var m in _messages)
		{
			var localDay = m.Timestamp?.ToLocalTime().Date;
			if (localDay is { } day && day != lastDay)
			{
				next.Add(new ChatDayHeader { Label = FormatDayHeader(day) });
				lastDay = day;
			}

			next.Add(m);
		}

		ReplaceListItems(next);
	}

	/// <summary>
	/// Replace the chat list without intermediate empty-state layout passes.
	/// </summary>
	private void ReplaceListItems(IReadOnlyList<object> items)
	{
		var gen = Interlocked.Increment(ref _rebuildGeneration);
		var source = new ObservableCollection<object>(items);
		_listItems = source;

		// Detach first so UICollectionView does not reconcile Clear→Add on a live source.
		MessagesList.ItemsSource = null;
		MessagesList.ItemsSource = source;

		// Drop stale ScrollTo from an older rebuild generation.
		_ = gen;
	}

	private static string FormatDayHeader(DateTime localDay)
	{
		var today = DateTime.Today;
		if (localDay == today) return "Today";
		if (localDay == today.AddDays(-1)) return "Yesterday";
		if (localDay.Year == today.Year)
			return localDay.ToString("dddd, MMM d");
		return localDay.ToString("MMM d, yyyy");
	}

	private void UpdatePinnedBar()
	{
		if (_pinned is null || _pinned.IsDeleted)
		{
			PinnedBar.IsVisible = false;
			return;
		}

		PinnedBar.IsVisible = true;
		var who = string.IsNullOrWhiteSpace(_pinned.SenderName) ? "Message" : _pinned.SenderName;
		PinnedTextLabel.Text = $"{who}: {_pinned.Text}";
		UnpinButton.IsVisible = _isAdmin;
	}

	private View CreateDayHeaderView()
	{
		var label = new Label
		{
			FontSize = 12,
			FontAttributes = FontAttributes.Bold,
			TextColor = Color.FromArgb("#666666"),
			HorizontalOptions = LayoutOptions.Center,
			HorizontalTextAlignment = TextAlignment.Center,
			Margin = new Thickness(0, 10, 0, 6)
		};
		label.SetBinding(Label.TextProperty, nameof(ChatDayHeader.Label));
		return label;
	}

	private View CreateMessageView()
	{
		// Built once per template instance; bind to ChatMessage.
		var root = new VerticalStackLayout { Spacing = 2, Padding = new Thickness(4) };

		var nameLabel = new Label { FontSize = 11, TextColor = Color.FromArgb("#666666") };
		nameLabel.SetBinding(Label.TextProperty, nameof(ChatMessage.SenderName));
		nameLabel.SetBinding(Label.IsVisibleProperty, new Binding(nameof(ChatMessage.IsMine), converter: new InvertBoolConverter()));

		var replyQuote = new Border
		{
			StrokeThickness = 0,
			BackgroundColor = Color.FromArgb("#00000012"),
			Padding = new Thickness(8, 4),
			Margin = new Thickness(0, 0, 0, 4)
		};
		replyQuote.StrokeShape = new RoundRectangle { CornerRadius = 6 };
		var replyStack = new VerticalStackLayout { Spacing = 1 };
		var replyName = new Label { FontSize = 10, FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#2e7d32") };
		var replyText = new Label { FontSize = 11, TextColor = Color.FromArgb("#555555"), LineBreakMode = LineBreakMode.TailTruncation, MaxLines = 2 };
		replyStack.Children.Add(replyName);
		replyStack.Children.Add(replyText);
		replyQuote.Content = replyStack;

		var textLabel = new Label { FontSize = 15, LineBreakMode = LineBreakMode.WordWrap };

		var timeLabel = new Label
		{
			FontSize = 10,
			TextColor = Color.FromArgb("#888888"),
			HorizontalOptions = LayoutOptions.End
		};
		timeLabel.SetBinding(Label.TextProperty, nameof(ChatMessage.LocalTimeText));

		var pinBadge = new Label
		{
			Text = "📌",
			FontSize = 11,
			IsVisible = false
		};

		var metaRow = new Grid { ColumnDefinitions = new ColumnDefinitionCollection { new(), new(GridLength.Auto) } };
		metaRow.Add(pinBadge, 0);
		metaRow.Add(timeLabel, 1);

		var bubbleStack = new VerticalStackLayout { Spacing = 2 };
		bubbleStack.Children.Add(nameLabel);
		bubbleStack.Children.Add(replyQuote);
		bubbleStack.Children.Add(textLabel);
		bubbleStack.Children.Add(metaRow);

		var border = new Border
		{
			Padding = new Thickness(10, 8),
			StrokeThickness = 0,
			Content = bubbleStack,
			MaximumWidthRequest = 320
		};
		border.StrokeShape = new RoundRectangle { CornerRadius = 12 };

		var reactionsRow = new HorizontalStackLayout
		{
			Spacing = 4,
			Margin = new Thickness(4, 2, 4, 0)
		};

		var column = new VerticalStackLayout { Spacing = 0, Children = { border, reactionsRow } };
		root.Children.Add(column);

		// Gestures: short press = actions, long press (~450ms) = reaction picker
		var pointer = new PointerGestureRecognizer();
		pointer.PointerPressed += (_, _) =>
		{
			_pointerDownUtc = DateTime.UtcNow;
			_pointerMessage = border.BindingContext as ChatMessage;
		};
		pointer.PointerReleased += async (_, _) =>
		{
			var msg = _pointerMessage ?? border.BindingContext as ChatMessage;
			_pointerMessage = null;
			if (msg is null || msg.Id.StartsWith("local-", StringComparison.Ordinal)) return;
			var held = (DateTime.UtcNow - _pointerDownUtc).TotalMilliseconds;
			if (held >= 450)
				await ShowReactionPickerAsync(msg);
			else
				await ShowMessageActionsAsync(msg);
		};
		border.GestureRecognizers.Add(pointer);

		// CollectionView sets BindingContext after the template factory returns.
		root.BindingContextChanged += (_, _) =>
		{
			if (root.BindingContext is not ChatMessage msg)
				return;

			ApplyMessageVisuals(msg, border, textLabel, replyQuote, replyName, replyText, pinBadge, reactionsRow, column);
		};

		if (root.BindingContext is ChatMessage already)
			ApplyMessageVisuals(already, border, textLabel, replyQuote, replyName, replyText, pinBadge, reactionsRow, column);

		return root;
	}

	private void ApplyMessageVisuals(
		ChatMessage msg,
		Border border,
		Label textLabel,
		Border replyQuote,
		Label replyName,
		Label replyText,
		Label pinBadge,
		HorizontalStackLayout reactionsRow,
		VerticalStackLayout column)
	{
		border.BackgroundColor = msg.IsMine
			? Color.FromArgb("#DCF8C6")
			: Color.FromArgb("#FFFFFF");
		column.HorizontalOptions = msg.IsMine ? LayoutOptions.End : LayoutOptions.Start;
		reactionsRow.HorizontalOptions = msg.IsMine ? LayoutOptions.End : LayoutOptions.Start;

		pinBadge.IsVisible = msg.IsPinned && !msg.IsDeleted;

		if (msg.IsDeleted)
		{
			textLabel.Text = "This message was deleted";
			textLabel.FontAttributes = FontAttributes.Italic;
			textLabel.TextColor = Color.FromArgb("#888888");
			textLabel.FormattedText = null;
			replyQuote.IsVisible = false;
			reactionsRow.Children.Clear();
			reactionsRow.IsVisible = false;
			return;
		}

		textLabel.FontAttributes = FontAttributes.None;
		textLabel.TextColor = Colors.Black;
		textLabel.FormattedText = BuildMentionFormatted(msg.Text);
		if (textLabel.FormattedText is null)
			textLabel.Text = msg.Text;

		if (msg.HasReply)
		{
			replyQuote.IsVisible = true;
			replyName.Text = string.IsNullOrWhiteSpace(msg.ReplyToSenderName) ? "Reply" : msg.ReplyToSenderName;
			replyText.Text = msg.ReplyToText ?? "";
		}
		else
		{
			replyQuote.IsVisible = false;
		}

		// Reaction chips
		reactionsRow.Children.Clear();
		var any = false;
		foreach (var (emoji, count, mine) in msg.ReactionSummaries(_myUid))
		{
			any = true;
			var chip = new Border
			{
				StrokeThickness = mine ? 1.5 : 0,
				Stroke = mine ? Color.FromArgb("#2e7d32") : Colors.Transparent,
				BackgroundColor = Color.FromArgb("#F0F0F0"),
				Padding = new Thickness(8, 3),
				Content = new Label
				{
					Text = $"{emoji} {count}",
					FontSize = 13,
					TextColor = Colors.Black
				}
			};
			chip.StrokeShape = new RoundRectangle { CornerRadius = 12 };

			var capturedEmoji = emoji;
			var tap = new TapGestureRecognizer();
			tap.Tapped += async (_, _) => await OnReactionChipTappedAsync(msg, capturedEmoji);
			chip.GestureRecognizers.Add(tap);

			reactionsRow.Children.Add(chip);
		}
		reactionsRow.IsVisible = any;
	}

	private FormattedString? BuildMentionFormatted(string? text)
	{
		if (string.IsNullOrEmpty(text) || !text.Contains('@'))
			return null;

		// Prefer longer display names first so "@Coach Sam" wins over "@Coach"
		var names = _memberNames.Values
			.Where(n => !string.IsNullOrWhiteSpace(n))
			.Select(n => n.Trim())
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.OrderByDescending(n => n.Length)
			.ToList();

		var myName = UserDisplayName.Get();
		if (!string.IsNullOrWhiteSpace(myName))
			names.Insert(0, myName.Trim());

		if (names.Count == 0)
			return null;

		var pattern = string.Join("|", names.Select(Regex.Escape));
		var rx = new Regex(@"@(" + pattern + @")\b", RegexOptions.IgnoreCase);
		if (!rx.IsMatch(text))
			return null;

		var fs = new FormattedString();
		var last = 0;
		foreach (Match m in rx.Matches(text))
		{
			if (m.Index > last)
			{
				fs.Spans.Add(new Span { Text = text[last..m.Index], TextColor = Colors.Black, FontSize = 15 });
			}
			fs.Spans.Add(new Span
			{
				Text = m.Value,
				TextColor = Color.FromArgb("#1565C0"),
				FontAttributes = FontAttributes.Bold,
				FontSize = 15
			});
			last = m.Index + m.Length;
		}
		if (last < text.Length)
			fs.Spans.Add(new Span { Text = text[last..], TextColor = Colors.Black, FontSize = 15 });

		return fs;
	}

	private async Task ShowReactionPickerAsync(ChatMessage msg)
	{
		if (msg.IsDeleted || _chat is null) return;

		// Present as action sheet of emojis in chunks + cancel
		// MAUI DisplayActionSheet has a practical limit — use a simple modal page grid instead.
		var page = new ContentPage { Title = "React" };
		var grid = new FlexLayout
		{
			Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap,
			JustifyContent = Microsoft.Maui.Layouts.FlexJustify.SpaceEvenly,
			Padding = 12
		};

		foreach (var emoji in ChatReactions.All)
		{
			var e = emoji;
			var btn = new Button
			{
				Text = e,
				FontSize = 28,
				WidthRequest = 56,
				HeightRequest = 56,
				BackgroundColor = Color.FromArgb("#F5F5F5"),
				Margin = 4
			};
			btn.Clicked += async (_, _) =>
			{
				await page.Navigation.PopModalAsync();
				await ApplyReactionAsync(msg, e);
			};
			grid.Children.Add(btn);
		}

		var cancel = new Button { Text = "Cancel", Margin = new Thickness(12) };
		cancel.Clicked += async (_, _) => await page.Navigation.PopModalAsync();

		page.Content = new ScrollView
		{
			Content = new VerticalStackLayout
			{
				Children =
				{
					new Label
					{
						Text = "Choose a reaction",
						FontAttributes = FontAttributes.Bold,
						Margin = new Thickness(12, 16, 12, 8),
						HorizontalTextAlignment = TextAlignment.Center
					},
					grid,
					cancel
				}
			}
		};

		await Navigation.PushModalAsync(new NavigationPage(page));
	}

	private async Task ShowMessageActionsAsync(ChatMessage msg)
	{
		if (msg.IsDeleted) return;

		var options = new List<string> { "React", "Reply" };
		if (_isAdmin)
			options.Add(msg.IsPinned ? "Unpin" : "Pin");
		if (msg.IsMine || _isAdmin)
			options.Add("Delete for everyone");

		var choice = await DisplayActionSheet(
			"Message",
			"Cancel",
			null,
			options.ToArray());

		if (string.IsNullOrEmpty(choice) || choice == "Cancel") return;

		switch (choice)
		{
			case "React":
				await ShowReactionPickerAsync(msg);
				break;
			case "Reply":
				BeginReply(msg);
				break;
			case "Pin":
				await SetPinAsync(msg, true);
				break;
			case "Unpin":
				await SetPinAsync(msg, false);
				break;
			case "Delete for everyone":
				await DeleteMessageAsync(msg);
				break;
		}
	}

	private async Task OnReactionChipTappedAsync(ChatMessage msg, string emoji)
	{
		// Tap chip → list of who reacted; optional toggle for me.
		if (_chat is null) return;

		var uids = msg.Reactions.TryGetValue(emoji, out var list) ? list : [];
		var names = uids.Select(ResolveName).ToList();
		var body = names.Count == 0 ? "No one yet." : string.Join("\n", names);

		var toggleMine = await DisplayAlert(
			$"{emoji}  {uids.Count}",
			body,
			"Toggle mine",
			"Close");

		if (toggleMine)
			await ApplyReactionAsync(msg, emoji);
	}

	private async Task ApplyReactionAsync(ChatMessage msg, string emoji)
	{
		if (_chat is null || string.IsNullOrEmpty(msg.Id)) return;
		try
		{
			HapticFeedback.Default.Perform(HapticFeedbackType.Click);
		}
		catch { /* optional */ }

		// Optimistic local toggle
		OptimisticToggleReaction(msg, emoji);
		RebuildList();

		try
		{
			await _chat.ToggleReactionAsync(_teamId, msg.Id, emoji);
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"[Chat] reaction failed: {ex.Message}");
			await DisplayAlert("Chat", "Could not update reaction.", "OK");
		}
	}

	private void OptimisticToggleReaction(ChatMessage msg, string emoji)
	{
		var uid = _myUid;
		if (string.IsNullOrEmpty(uid)) return;

		var hadThis = msg.Reactions.TryGetValue(emoji, out var existing)
			&& existing.Any(u => string.Equals(u, uid, StringComparison.Ordinal));

		foreach (var key in msg.Reactions.Keys.ToList())
		{
			msg.Reactions[key] = msg.Reactions[key]
				.Where(u => !string.Equals(u, uid, StringComparison.Ordinal))
				.ToList();
			if (msg.Reactions[key].Count == 0)
				msg.Reactions.Remove(key);
		}

		if (!hadThis)
		{
			if (!msg.Reactions.TryGetValue(emoji, out var list))
			{
				list = [];
				msg.Reactions[emoji] = list;
			}
			list.Add(uid);
		}
	}

	private string ResolveName(string uid)
	{
		if (string.Equals(uid, _myUid, StringComparison.Ordinal))
			return "You";
		if (_memberNames.TryGetValue(uid, out var n) && !string.IsNullOrWhiteSpace(n))
			return n;
		return "Teammate";
	}

	private void BeginReply(ChatMessage msg)
	{
		_replyTo = msg;
		ReplyBar.IsVisible = true;
		ReplyBarName.Text = msg.IsMine ? "You" : msg.SenderName;
		ReplyBarText.Text = msg.IsDeleted ? "Deleted message" : msg.Text;
		MessageEntry.Focus();
	}

	private void OnCancelReplyClicked(object? sender, EventArgs e)
	{
		_replyTo = null;
		ReplyBar.IsVisible = false;
	}

	private async Task SetPinAsync(ChatMessage msg, bool pinned)
	{
		if (_chat is null || !_isAdmin) return;
		try
		{
			await _chat.SetPinnedAsync(_teamId, msg.Id, pinned);
			msg.IsPinned = pinned;
			RebuildList();
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"[Chat] pin: {ex.Message}");
			await DisplayAlert("Chat", "Could not update pin. Admins only.", "OK");
		}
	}

	private async void OnUnpinClicked(object? sender, EventArgs e)
	{
		if (_pinned is not null)
			await SetPinAsync(_pinned, false);
	}

	private async Task DeleteMessageAsync(ChatMessage msg)
	{
		if (_chat is null) return;
		var ok = await DisplayAlert(
			"Delete for everyone?",
			"This removes the message for all team members.",
			"Delete",
			"Cancel");
		if (!ok) return;

		try
		{
			await _chat.DeleteForEveryoneAsync(_teamId, msg.Id);
			msg.IsDeleted = true;
			RebuildList();
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"[Chat] delete: {ex.Message}");
			await DisplayAlert("Chat", "Could not delete message.", "OK");
		}
	}

	private async Task EnsureDisplayNameForSharedTeamAsync()
	{
		if (!string.IsNullOrWhiteSpace(UserDisplayName.Get()))
			return;

		var entered = await DisplayPromptAsync(
			"Your name in Chat",
			"Teammates need a name to see who is messaging. Enter a display name:",
			accept: "Save",
			cancel: "Later",
			placeholder: "e.g. Alex or Coach Sam",
			maxLength: UserDisplayName.MaxLength,
			keyboard: Keyboard.Text);

		if (!UserDisplayName.TryValidate(entered, out var displayName, out _))
			return;

		UserDisplayName.Set(displayName);
		if (_chat is not null)
			await _chat.UpdateDisplayNameAsync(_teamId, displayName);
	}

	private async Task RegisterFcmTokenAsync()
	{
		if (Interlocked.CompareExchange(ref _fcmRegisterInFlight, 1, 0) != 0)
			return;
		try
		{
			await FcmService.Instance.EnsureRegisteredForCurrentTeamAsync();
			var token = await FcmService.Instance.GetTokenAsync();
			System.Diagnostics.Debug.WriteLine(
				string.IsNullOrEmpty(token)
					? "[Chat] ❌ FCM still has no token after EnsureRegistered"
					: $"[Chat] ✅ FCM ready token={token[..Math.Min(16, token.Length)]}…");
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"[Chat] RegisterFcmToken: {ex.Message}");
		}
		finally
		{
			Interlocked.Exchange(ref _fcmRegisterInFlight, 0);
		}
	}

	private async void OnSendClicked(object? sender, EventArgs e)
	{
		var message = MessageEntry.Text?.Trim();
		if (string.IsNullOrWhiteSpace(message) || _chat is null)
			return;

		var displayName = UserDisplayName.Get();
		if (string.IsNullOrWhiteSpace(displayName))
		{
			var entered = await DisplayPromptAsync(
				"Your name in Chat",
				"Enter a display name so teammates know who sent this message:",
				accept: "Send",
				cancel: "Cancel",
				placeholder: "e.g. Alex or Coach Sam",
				maxLength: UserDisplayName.MaxLength,
				keyboard: Keyboard.Text);

			if (!UserDisplayName.TryValidate(entered, out displayName, out var error))
			{
				if (!string.IsNullOrWhiteSpace(entered))
					await DisplayAlert("Display Name", error ?? "Please enter a valid name.", "OK");
				return;
			}

			UserDisplayName.Set(displayName);
			await _chat.UpdateDisplayNameAsync(_teamId, displayName);
		}

		ChatSendOptions? options = null;
		if (_replyTo is not null)
		{
			options = new ChatSendOptions
			{
				ReplyToMessageId = _replyTo.Id,
				ReplyToText = _replyTo.IsDeleted ? "Deleted message" : Truncate(_replyTo.Text, 120),
				ReplyToSenderName = _replyTo.IsMine ? displayName : _replyTo.SenderName
			};
		}

		var optimisticId = $"local-{Guid.NewGuid():N}";
		var optimistic = new ChatMessage
		{
			Id = optimisticId,
			Text = message,
			UserId = _myUid,
			SenderName = "You",
			Timestamp = DateTimeOffset.Now,
			IsMine = true,
			ReplyToMessageId = options?.ReplyToMessageId,
			ReplyToText = options?.ReplyToText,
			ReplyToSenderName = options?.ReplyToSenderName
		};
		_messages.Add(optimistic);
		RebuildList();
		MessageEntry.Text = string.Empty;
		_replyTo = null;
		ReplyBar.IsVisible = false;
		ScrollMessagesToEnd(animate: true);

		try
		{
			await _chat.SendAsync(_teamId, message, displayName, options);
			_ = RegisterFcmTokenAsync();
		}
		catch (Exception ex)
		{
			var doomed = _messages.FirstOrDefault(m => m.Id == optimisticId);
			if (doomed is not null)
				_messages.Remove(doomed);
			RebuildList();
			MessageEntry.Text = message;

			System.Diagnostics.Debug.WriteLine(
				$"[Chat] Send failed: {ex.GetType().FullName}: {ex.Message}");
			await DisplayAlert("Chat", "Could not send message. Check your connection.", "OK");
		}
	}

	private static string Truncate(string s, int max) =>
		s.Length <= max ? s : s[..max];

	private void OnMessageEntryFocused(object? sender, FocusEventArgs e) =>
		SetChatTabBarVisible(false);

	private void OnMessageEntryUnfocused(object? sender, FocusEventArgs e)
	{
		SetChatTabBarVisible(true);
#if !IOS
		RootGrid.Padding = default;
#endif
	}

	private void SetChatTabBarVisible(bool visible)
	{
		try
		{
			Shell.SetTabBarIsVisible(this, visible);
			_tabBarHiddenForInput = !visible;
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"[Chat] SetTabBarIsVisible({visible}): {ex.Message}");
		}
	}

	private void RestoreChatChrome()
	{
		RootGrid.Padding = default;
		if (_tabBarHiddenForInput || !Shell.GetTabBarIsVisible(this))
			SetChatTabBarVisible(true);
		_tabBarHiddenForInput = false;
	}

	private void ScrollMessagesToEnd(bool animate = true)
	{
		if (_listItems.Count == 0) return;
		var target = _listItems[^1];
		var gen = _rebuildGeneration;
		var delayMs = animate ? 80 : 40;

		// Let the new ItemsSource finish its first layout pass (esp. iOS CollectionView).
		_ = Task.Run(async () =>
		{
			await Task.Delay(delayMs).ConfigureAwait(false);
			await MainThread.InvokeOnMainThreadAsync(() =>
			{
				if (gen != _rebuildGeneration || _listItems.Count == 0)
					return;
				try
				{
					var item = _listItems.Contains(target) ? target : _listItems[^1];
					MessagesList.ScrollTo(item, position: ScrollToPosition.End, animate: animate);
				}
				catch (Exception ex)
				{
					System.Diagnostics.Debug.WriteLine($"[Chat] ScrollTo end: {ex.Message}");
				}
			});
		});
	}

	private void SubscribeKeyboardAvoidance()
	{
#if IOS
		if (_keyboardFrameObserver is not null)
			return;

		_keyboardFrameObserver = UIKeyboard.Notifications.ObserveWillChangeFrame((_, args) =>
		{
			var endFrame = args.FrameEnd;
			var screenHeight = UIScreen.MainScreen.Bounds.Height;
			var overlap = Math.Max(0, screenHeight - endFrame.Y);

			MainThread.BeginInvokeOnMainThread(() =>
			{
				if (!IsLoaded) return;

				RootGrid.Padding = overlap > 0.5
					? new Thickness(0, 0, 0, overlap)
					: default;

				if (overlap > 0.5)
					ScrollMessagesToEnd(animate: false);
			});
		});
#endif
	}

	private void UnsubscribeKeyboardAvoidance()
	{
#if IOS
		_keyboardFrameObserver?.Dispose();
		_keyboardFrameObserver = null;
#endif
	}

	private void ApplyThemeToInputBar()
	{
		var theme = Preferences.Get("AppTheme", "classic");
		if (theme == "modern")
		{
			InputBar.BackgroundColor = Color.FromArgb("#1b263b");
			SendButton.BackgroundColor = Color.FromArgb("#00d9ff");
			SendButton.TextColor = Color.FromArgb("#0d1b2a");
		}
		else
		{
			InputBar.BackgroundColor = Color.FromArgb("#2e7d32");
			SendButton.BackgroundColor = Color.FromArgb("#FF6B35");
			SendButton.TextColor = Colors.White;
		}

		MessageEntry.TextColor = Color.FromArgb("#111111");
		MessageEntry.PlaceholderColor = Color.FromArgb("#888888");
		MessageEntry.BackgroundColor = Colors.Transparent;
	}

	private sealed class ChatDayHeader
	{
		public string Label { get; set; } = "";
	}

	private sealed class ChatItemTemplateSelector : DataTemplateSelector
	{
		public DataTemplate? DayTemplate { get; set; }
		public DataTemplate? MessageTemplate { get; set; }

		protected override DataTemplate OnSelectTemplate(object item, BindableObject container) =>
			item is ChatDayHeader
				? DayTemplate!
				: MessageTemplate!;
	}

	private sealed class InvertBoolConverter : IValueConverter
	{
		public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
			=> value is not true;
		public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
			=> throw new NotSupportedException();
	}
}
