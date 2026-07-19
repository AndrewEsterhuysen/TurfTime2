using System.Collections.ObjectModel;
using Microsoft.Maui.Controls.Shapes;
using TurfTime2.Helpers;
using TurfTime2.Services;

namespace TurfTime2;

public partial class ChatPage : ContentPage
{
	private readonly ObservableCollection<ChatMessage> _messages = new();
	private IChatService? _chat;
	private IFirebaseAuthService? _auth;
	private IDisposable? _subscription;
	private string _teamId = string.Empty;
	private int _fcmRegisterInFlight;

	public ChatPage()
	{
		InitializeComponent();
		MessagesList.ItemsSource = _messages;
		MessagesList.ItemTemplate = new DataTemplate(() =>
		{
			var nameLabel = new Label { FontSize = 11, TextColor = Color.FromArgb("#666666") };
			nameLabel.SetBinding(Label.TextProperty, nameof(ChatMessage.SenderName));

			var textLabel = new Label { FontSize = 15, LineBreakMode = LineBreakMode.WordWrap };
			textLabel.SetBinding(Label.TextProperty, nameof(ChatMessage.Text));

			var timeLabel = new Label { FontSize = 10, TextColor = Color.FromArgb("#888888"), HorizontalOptions = LayoutOptions.End };
			timeLabel.SetBinding(Label.TextProperty, new Binding(nameof(ChatMessage.Timestamp), stringFormat: "{0:t}"));

			var stack = new VerticalStackLayout { Spacing = 2, Children = { nameLabel, textLabel, timeLabel } };
			var border = new Border
			{
				Padding = new Thickness(10, 8),
				StrokeThickness = 0,
				Content = stack,
				MaximumWidthRequest = 320
			};
			border.StrokeShape = new RoundRectangle { CornerRadius = 12 };

			border.SetBinding(Border.BackgroundColorProperty, new Binding(nameof(ChatMessage.IsMine), converter: new MineToColorConverter()));
			textLabel.SetBinding(Label.TextColorProperty, new Binding(nameof(ChatMessage.IsMine), converter: new MineToTextColorConverter()));
			nameLabel.SetBinding(Label.IsVisibleProperty, new Binding(nameof(ChatMessage.IsMine), converter: new InvertBoolConverter()));
			border.SetBinding(View.HorizontalOptionsProperty, new Binding(nameof(ChatMessage.IsMine), converter: new MineToAlignConverter()));

			return new Grid { Padding = 4, Children = { border } };
		});
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		ResolveServices();
		ApplyThemeToInputBar();

		_teamId = Preferences.Get("team_id", string.Empty);
		var mode = Preferences.Get("team_mode", string.Empty);
		if (mode != "shared" || string.IsNullOrEmpty(_teamId) || _teamId.StartsWith("local_"))
		{
			_messages.Clear();
			_messages.Add(new ChatMessage
			{
				Text = "Chat is available for shared (cloud) teams only.",
				SenderName = "Turf Time",
				IsMine = false
			});
			return;
		}

		await EnsureDisplayNameForSharedTeamAsync();
		await StartChatAsync();
		_ = RegisterFcmTokenAsync();
	}

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		_subscription?.Dispose();
		_subscription = null;
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

		_subscription?.Dispose();
		_subscription = await _chat.SubscribeAsync(
			_teamId,
			msgs => MainThread.BeginInvokeOnMainThread(() =>
			{
				_messages.Clear();
				foreach (var m in msgs)
				{
					var display = m.IsMine ? "You" : (string.IsNullOrWhiteSpace(m.SenderName) ? "Teammate" : m.SenderName);
					_messages.Add(new ChatMessage
					{
						Id = m.Id,
						Text = m.Text,
						UserId = m.UserId,
						SenderName = display,
						Timestamp = m.Timestamp,
						IsMine = m.IsMine
					});
				}

				if (_messages.Count > 0)
					MessagesList.ScrollTo(_messages[^1], position: ScrollToPosition.End, animate: false);
			}),
			ex => System.Diagnostics.Debug.WriteLine($"[Chat] listen error: {ex.Message}"));

		// Sync display name to member profile
		var name = UserDisplayName.Get();
		if (!string.IsNullOrWhiteSpace(name) && _chat is not null)
			await _chat.UpdateDisplayNameAsync(_teamId, name);
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
			var ok = await FcmService.Instance.InitializeAsync();
			var token = await FcmService.Instance.GetTokenAsync();
			if (string.IsNullOrEmpty(token) || _chat is null)
			{
				System.Diagnostics.Debug.WriteLine($"[Chat] FCM register skip initOk={ok} token={(token != null)}");
				return;
			}

			var saved = await _chat.RegisterFcmTokenAsync(_teamId, token);
			System.Diagnostics.Debug.WriteLine(saved
				? "[Chat] ✅ FCM token registered via ChatService"
				: "[Chat] ❌ FCM token registration failed");
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

		try
		{
			await _chat.SendAsync(_teamId, message, displayName);
			MessageEntry.Text = string.Empty;
			_ = RegisterFcmTokenAsync();
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"[Chat] Send failed: {ex.Message}");
			await DisplayAlert("Chat", "Could not send message. Check your connection.", "OK");
		}
	}

	private void ApplyThemeToInputBar()
	{
		var theme = Preferences.Get("AppTheme", "classic");
		if (theme == "modern")
		{
			InputBar.BackgroundColor = Color.FromArgb("#1b263b");
			MessageEntry.TextColor = Color.FromArgb("#e0e0e0");
			MessageEntry.PlaceholderColor = Color.FromArgb("#6688aa");
			SendButton.BackgroundColor = Color.FromArgb("#00d9ff");
			SendButton.TextColor = Color.FromArgb("#0d1b2a");
		}
		else
		{
			InputBar.BackgroundColor = Color.FromArgb("#2e7d32");
			MessageEntry.TextColor = Colors.White;
			MessageEntry.PlaceholderColor = Color.FromArgb("#AAFFAA");
			SendButton.BackgroundColor = Color.FromArgb("#FF6B35");
			SendButton.TextColor = Colors.White;
		}
	}

	private sealed class MineToColorConverter : IValueConverter
	{
		public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
			=> value is true ? Color.FromArgb("#DCF8C6") : Color.FromArgb("#FFFFFF");
		public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
	}

	private sealed class MineToTextColorConverter : IValueConverter
	{
		public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
			=> Colors.Black;
		public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
	}

	private sealed class MineToAlignConverter : IValueConverter
	{
		public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
			=> value is true ? LayoutOptions.End : LayoutOptions.Start;
		public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
	}

	private sealed class InvertBoolConverter : IValueConverter
	{
		public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
			=> value is not true;
		public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
	}
}
