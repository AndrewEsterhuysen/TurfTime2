namespace TurfTime2;

public partial class ChatPage : ContentPage
{
	public ChatPage()
	{
		InitializeComponent();
		LoadChatInterface();
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
		// Rebuild with the current team ID in case the team changed since last visit
		LoadChatInterface();
	}

	private void LoadChatInterface()
	{
		var teamId = Preferences.Get("team_id", string.Empty);
		var htmlSource = new HtmlWebViewSource
		{
			Html = GetChatHtml(teamId)
		};
		ChatWebView.Source = htmlSource;
		ApplyThemeToInputBar();
	}

	private void ApplyThemeToInputBar()
	{
		var theme = Preferences.Get("AppTheme", "classic");
		if (theme == "modern")
		{
			InputBar.BackgroundColor   = Color.FromArgb("#1b263b");
			MessageEntry.TextColor     = Color.FromArgb("#e0e0e0");
			MessageEntry.PlaceholderColor = Color.FromArgb("#6688aa");
			SendButton.BackgroundColor = Color.FromArgb("#00d9ff");
			SendButton.TextColor       = Color.FromArgb("#0d1b2a");
		}
		else
		{
			InputBar.BackgroundColor   = Color.FromArgb("#2e7d32");
			MessageEntry.TextColor     = Colors.White;
			MessageEntry.PlaceholderColor = Color.FromArgb("#AAFFAA");
			SendButton.BackgroundColor = Color.FromArgb("#FF6B35");
			SendButton.TextColor       = Colors.White;
		}
	}

	private async void OnSendClicked(object sender, EventArgs e)
	{
		var message = MessageEntry.Text?.Trim();
		if (string.IsNullOrWhiteSpace(message))
			return;

		// Send message via JavaScript
		await ChatWebView.EvaluateJavaScriptAsync($"sendMessage('{EscapeJavaScript(message)}')");
		
		// Clear input
		MessageEntry.Text = string.Empty;
	}

	private string EscapeJavaScript(string text)
	{
		return text.Replace("'", "\\'")
				   .Replace("\n", "\\n")
				   .Replace("\r", "\\r")
				   .Replace("\"", "\\\"");
	}

	private string GetChatHtml(string teamId)
	{
		if (string.IsNullOrEmpty(teamId))
		{
			return @"<!DOCTYPE html><html><body style='font-family:sans-serif;padding:20px;color:#667781;text-align:center;'>
				<p style='margin-top:40px;font-size:16px;'>No team selected.</p>
				<p style='font-size:13px;'>Go to Settings → Team Details to create or join a team.</p>
				</body></html>";
		}

		var safeTeamId = teamId.Replace("'", "\\'").Replace("\\", "\\\\");

		// Resolve theme colours from app Preferences
		var theme = Preferences.Get("AppTheme", "classic");
		string bodyBg, ownBubbleBg, otherBubbleBg, msgText,
			   ownUserColor, otherUserColor, timestampColor, loadingColor;

		if (theme == "modern")
		{
			bodyBg         = "#0d1b2a";
			ownBubbleBg    = "#1a3550";
			otherBubbleBg  = "#1b263b";
			msgText        = "#e0e0e0";
			ownUserColor   = "#00d9ff";
			otherUserColor = "rgba(224,224,224,0.75)";
			timestampColor = "rgba(224,224,224,0.5)";
			loadingColor   = "rgba(224,224,224,0.55)";
		}
		else // classic
		{
			bodyBg         = "#1b5e20";
			ownBubbleBg    = "#2e7d32";
			otherBubbleBg  = "rgba(0,0,0,0.22)";
			msgText        = "#ffffff";
			ownUserColor   = "#FF6B35";
			otherUserColor = "rgba(255,255,255,0.75)";
			timestampColor = "rgba(255,255,255,0.55)";
			loadingColor   = "rgba(255,255,255,0.6)";
		}

		return $@"
<!DOCTYPE html>
<html>
<head>
	<meta name='viewport' content='width=device-width, initial-scale=1.0, maximum-scale=1.0, user-scalable=no'>
	<style>
		* {{
			margin: 0;
			padding: 0;
			box-sizing: border-box;
		}}

		body {{
			font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Arial, sans-serif;
			background: {bodyBg};
			padding: 10px;
			overflow-x: hidden;
		}}

		#messages {{
			display: flex;
			flex-direction: column;
			align-items: flex-start;
			gap: 8px;
			padding-bottom: 20px;
		}}

		.message {{
			display: flex;
			flex-direction: column;
			max-width: 75%;
			word-wrap: break-word;
			animation: fadeIn 0.3s ease-in;
		}}

		@keyframes fadeIn {{
			from {{ opacity: 0; transform: translateY(10px); }}
			to {{ opacity: 1; transform: translateY(0); }}
		}}

		.message.own {{
			align-self: flex-end;
		}}

		.message.other {{
			align-self: flex-start;
		}}

		.message-bubble {{
			padding: 8px 12px;
			border-radius: 8px;
			position: relative;
			box-shadow: 0 1px 2px rgba(0,0,0,0.1);
		}}

		.message.own .message-bubble {{
			background: {ownBubbleBg};
			border-bottom-right-radius: 2px;
		}}

		.message.other .message-bubble {{
			background: {otherBubbleBg};
			border-bottom-left-radius: 2px;
		}}

		.message-user {{
			font-size: 11px;
			font-weight: bold;
			color: {otherUserColor};
			margin-bottom: 2px;
		}}

		.message.own .message-user {{
			color: {ownUserColor};
		}}

		.message-text {{
			font-size: 14px;
			line-height: 1.4;
			color: {msgText};
			white-space: pre-wrap;
			word-break: break-word;
		}}

		.message-time {{
			font-size: 10px;
			color: {timestampColor};
			text-align: right;
			margin-top: 4px;
		}}

		.loading {{
			text-align: center;
			color: {loadingColor};
			font-size: 14px;
			padding: 20px;
		}}

		.char-count {{
			position: fixed;
			bottom: 10px;
			right: 10px;
			background: rgba(0,0,0,0.7);
			color: white;
			padding: 5px 10px;
			border-radius: 15px;
			font-size: 11px;
			display: none;
		}}
	</style>
</head>
<body>
	<div id='messages'>
		<div class='loading'>Loading messages...</div>
	</div>
	<div id='charCount' class='char-count'></div>

	<!-- Firebase SDK v10 (Modular) -->
	<script type='module'>
		import {{ initializeApp }} from 'https://www.gstatic.com/firebasejs/10.7.1/firebase-app.js';
		import {{ getFirestore, collection, addDoc, query, orderBy, limit, onSnapshot, serverTimestamp }} from 'https://www.gstatic.com/firebasejs/10.7.1/firebase-firestore.js';
		import {{ getAuth, signInAnonymously, onAuthStateChanged }} from 'https://www.gstatic.com/firebasejs/10.7.1/firebase-auth.js';

		const firebaseConfig = {{
			apiKey: 'AIzaSyDAKivCFX5kYYZ6SkAQluBNdR92I320glk',
			authDomain: 'turf-timer.firebaseapp.com',
			projectId: 'turf-timer',
			storageBucket: 'turf-timer.firebasestorage.app',
			messagingSenderId: '1046768934531',
			appId: '1:1046768934531:web:02ee36e7e02f2b90b39e0e',
			measurementId: 'G-PHVV7HVYPY'
		}};

		const TEAM_ID = '{safeTeamId}';

		const app = initializeApp(firebaseConfig);
		const db = getFirestore(app);
		const auth = getAuth(app);

		let currentUserId = null;
		let messagesContainer = document.getElementById('messages');

		// Wait for Firebase to restore the persisted anonymous user from IndexedDB before
		// calling signInAnonymously. Calling it eagerly races with IndexedDB restoration and
		// produces a NEW uid on each page reload, breaking the own/other comparison.
		onAuthStateChanged(auth, (user) => {{
			if (user) {{
				currentUserId = user.uid;
				console.log('[Chat] ✅ User authenticated:', currentUserId.substring(0, 8) + '...');
				if (!window._chatListenerStarted) {{
					window._chatListenerStarted = true;
					startChatListener();
				}}
			}} else {{
				// No persisted user — sign in anonymously once
				console.log('[Chat] No existing user, signing in anonymously...');
				signInAnonymously(auth).catch(err => console.error('[Chat] ❌ Auth error:', err));
			}}
		}});

		// Listen to team-scoped chat messages in real-time
		function startChatListener() {{
			console.log('[Chat] 🔄 Starting message listener for team:', TEAM_ID);
			const messagesRef = collection(db, 'teams', TEAM_ID, 'messages');
			const q = query(messagesRef, orderBy('timestamp', 'asc'), limit(100));

			onSnapshot(q, (snapshot) => {{
				console.log(`[Chat] 📩 Received ${{snapshot.size}} messages`);
				messagesContainer.innerHTML = '';

				snapshot.forEach((doc) => {{
					const data = doc.data();
					displayMessage(data);
				}});

				window.scrollTo(0, document.body.scrollHeight);
			}}, (error) => {{
				console.error('[Chat] ❌ Snapshot error:', error);
			}});
		}}

		// Display a message
		function displayMessage(data) {{
			const messageDiv = document.createElement('div');
			messageDiv.className = `message ${{data.userId === currentUserId ? 'own' : 'other'}}`;

			const bubble = document.createElement('div');
			bubble.className = 'message-bubble';

			const userSpan = document.createElement('div');
			userSpan.className = 'message-user';
			userSpan.textContent = data.userId === currentUserId ? 'You' : `User ${{data.userId.substring(0, 8)}}`;
			bubble.appendChild(userSpan);

			const textSpan = document.createElement('div');
			textSpan.className = 'message-text';
			textSpan.textContent = data.text;
			bubble.appendChild(textSpan);

			const timeSpan = document.createElement('div');
			timeSpan.className = 'message-time';
			if (data.timestamp) {{
				timeSpan.textContent = formatTime(data.timestamp.toDate());
			}} else {{
				timeSpan.textContent = 'Sending...';
			}}
			bubble.appendChild(timeSpan);

			messageDiv.appendChild(bubble);
			messagesContainer.appendChild(messageDiv);
		}}

		function formatTime(date) {{
			const now = new Date();
			const timeStr = date.toLocaleTimeString('en-US', {{ hour: 'numeric', minute: '2-digit', hour12: true }});
			if (date.toDateString() === now.toDateString()) return timeStr;
			const dateStr = date.toLocaleDateString('en-US', {{ month: 'short', day: 'numeric' }});
			return `${{dateStr}} ${{timeStr}}`;
		}}

		// Send a message (called from C# code)
		window.sendMessage = async function(text) {{
			if (!text || !currentUserId) {{
				console.error('[Chat] ❌ Cannot send - missing text or userId');
				return;
			}}

			console.log('[Chat] 📤 Sending message to team:', TEAM_ID);
			try {{
				const docRef = await addDoc(collection(db, 'teams', TEAM_ID, 'messages'), {{
					text: text.substring(0, 500),
					userId: currentUserId,
					timestamp: serverTimestamp()
				}});
				console.log('[Chat] ✅ Message sent, ID:', docRef.id);
			}} catch (error) {{
				console.error('[Chat] ❌ Send error:', error);
			}}
		}};

		console.log('[Chat] Initialized for team:', TEAM_ID);
	</script>
</body>
</html>
";
	}
}
