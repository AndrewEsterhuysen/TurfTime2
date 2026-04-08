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
		// Re-initialize chat when returning to page
		if (ChatWebView.Source != null)
		{
			ChatWebView.Reload();
		}
	}

	private void LoadChatInterface()
	{
		var htmlSource = new HtmlWebViewSource
		{
			Html = GetChatHtml()
		};
		ChatWebView.Source = htmlSource;
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

	private string GetChatHtml()
	{
		return @"
<!DOCTYPE html>
<html>
<head>
    <meta name='viewport' content='width=device-width, initial-scale=1.0, maximum-scale=1.0, user-scalable=no'>
    <style>
        * {
            margin: 0;
            padding: 0;
            box-sizing: border-box;
        }
        
        body {
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Arial, sans-serif;
            background: #e5ddd5;
            padding: 10px;
            overflow-x: hidden;
        }
        
        #messages {
            display: flex;
            flex-direction: column;
            gap: 8px;
            padding-bottom: 20px;
        }
        
        .message {
            display: flex;
            flex-direction: column;
            max-width: 75%;
            word-wrap: break-word;
            animation: fadeIn 0.3s ease-in;
        }
        
        @keyframes fadeIn {
            from { opacity: 0; transform: translateY(10px); }
            to { opacity: 1; transform: translateY(0); }
        }
        
        .message.own {
            align-self: flex-end;
        }
        
        .message.other {
            align-self: flex-start;
        }
        
        .message-bubble {
            padding: 8px 12px;
            border-radius: 8px;
            position: relative;
            box-shadow: 0 1px 2px rgba(0,0,0,0.1);
        }
        
        .message.own .message-bubble {
            background: #dcf8c6;
            border-bottom-right-radius: 2px;
        }
        
        .message.other .message-bubble {
            background: white;
            border-bottom-left-radius: 2px;
        }
        
        .message-user {
            font-size: 11px;
            font-weight: bold;
            color: #128c7e;
            margin-bottom: 2px;
        }
        
        .message.own .message-user {
            color: #075e54;
        }
        
        .message-text {
            font-size: 14px;
            line-height: 1.4;
            color: #333;
            white-space: pre-wrap;
            word-break: break-word;
        }
        
        .message-time {
            font-size: 10px;
            color: #667781;
            text-align: right;
            margin-top: 4px;
        }
        
        .loading {
            text-align: center;
            color: #667781;
            font-size: 14px;
            padding: 20px;
        }
        
        .char-count {
            position: fixed;
            bottom: 10px;
            right: 10px;
            background: rgba(0,0,0,0.7);
            color: white;
            padding: 5px 10px;
            border-radius: 15px;
            font-size: 11px;
            display: none;
        }
    </style>
</head>
<body>
    <div id='messages'>
        <div class='loading'>Loading messages...</div>
    </div>
    <div id='charCount' class='char-count'></div>

    <!-- Firebase SDK v10 (Modular) -->
    <script type='module'>
        import { initializeApp } from 'https://www.gstatic.com/firebasejs/10.7.1/firebase-app.js';
        import { getFirestore, collection, addDoc, query, orderBy, limit, onSnapshot, serverTimestamp } from 'https://www.gstatic.com/firebasejs/10.7.1/firebase-firestore.js';
        import { getAuth, signInAnonymously, onAuthStateChanged } from 'https://www.gstatic.com/firebasejs/10.7.1/firebase-auth.js';

        const firebaseConfig = {
            apiKey: 'AIzaSyDAKivCFX5kYYZ6SkAQluBNdR92I320glk',
            authDomain: 'turf-timer.firebaseapp.com',
            projectId: 'turf-timer',
            storageBucket: 'turf-timer.firebasestorage.app',
            messagingSenderId: '1046768934531',
            appId: '1:1046768934531:web:02ee36e7e02f2b90b39e0e',
            measurementId: 'G-PHVV7HVYPY'
        };

        const app = initializeApp(firebaseConfig);
        const db = getFirestore(app);
        const auth = getAuth(app);

        let currentUserId = null;
        let messagesContainer = document.getElementById('messages');

        // Authenticate anonymously
        console.log('[Chat] 🔄 Starting anonymous authentication...');
        signInAnonymously(auth)
            .then(() => console.log('[Chat] ✅ Sign-in initiated'))
            .catch(err => console.error('[Chat] ❌ Auth error:', err));

        onAuthStateChanged(auth, (user) => {
            if (user) {
                currentUserId = user.uid;
                console.log('[Chat] ✅ User authenticated:', currentUserId.substring(0, 8) + '...');
                startChatListener();
            } else {
                console.log('[Chat] ⚠️ No user authenticated');
            }
        });

        // Listen to chat messages in real-time
        function startChatListener() {
            console.log('[Chat] 🔄 Starting message listener...');
            const messagesRef = collection(db, 'messages');
            const q = query(messagesRef, orderBy('timestamp', 'asc'), limit(100));

            onSnapshot(q, (snapshot) => {
                console.log(`[Chat] 📩 Received ${snapshot.size} messages`);
                messagesContainer.innerHTML = '';

                snapshot.forEach((doc) => {
                    const data = doc.data();
                    console.log('[Chat] 📨 Message:', data.text.substring(0, 30) + '...', 'from', data.userId.substring(0, 8));
                    displayMessage(data);
                });

                // Scroll to bottom
                window.scrollTo(0, document.body.scrollHeight);
            }, (error) => {
                console.error('[Chat] ❌ Snapshot error:', error);
            });
        }

        // Display a message
        function displayMessage(data) {
            const messageDiv = document.createElement('div');
            messageDiv.className = `message ${data.userId === currentUserId ? 'own' : 'other'}`;

            const bubble = document.createElement('div');
            bubble.className = 'message-bubble';

            // User identifier (first 8 chars of user ID)
            const userSpan = document.createElement('div');
            userSpan.className = 'message-user';
            userSpan.textContent = data.userId === currentUserId ? 'You' : `User ${data.userId.substring(0, 8)}`;
            bubble.appendChild(userSpan);

            // Message text
            const textSpan = document.createElement('div');
            textSpan.className = 'message-text';
            textSpan.textContent = data.text;
            bubble.appendChild(textSpan);

            // Timestamp
            const timeSpan = document.createElement('div');
            timeSpan.className = 'message-time';
            if (data.timestamp) {
                const date = data.timestamp.toDate();
                timeSpan.textContent = formatTime(date);
            } else {
                timeSpan.textContent = 'Sending...';
            }
            bubble.appendChild(timeSpan);

            messageDiv.appendChild(bubble);
            messagesContainer.appendChild(messageDiv);
        }

        // Format timestamp
        function formatTime(date) {
            const now = new Date();
            const isToday = date.toDateString() === now.toDateString();
            
            const timeStr = date.toLocaleTimeString('en-US', { 
                hour: 'numeric', 
                minute: '2-digit',
                hour12: true 
            });

            if (isToday) {
                return timeStr;
            } else {
                const dateStr = date.toLocaleDateString('en-US', { 
                    month: 'short', 
                    day: 'numeric' 
                });
                return `${dateStr} ${timeStr}`;
            }
        }

        // Send a message (called from C# code)
        window.sendMessage = async function(text) {
            if (!text || !currentUserId) {
                console.error('[Chat] ❌ Cannot send - missing text or userId');
                return;
            }

            console.log('[Chat] 📤 Sending message:', text.substring(0, 30) + '...');
            try {
                const docRef = await addDoc(collection(db, 'messages'), {
                    text: text.substring(0, 500), // Enforce 500 char limit
                    userId: currentUserId,
                    timestamp: serverTimestamp()
                });
                console.log('[Chat] ✅ Message sent, ID:', docRef.id);
            } catch (error) {
                console.error('[Chat] ❌ Send error:', error);
            }
        };

        console.log('[Chat] Initialized');
    </script>
</body>
</html>
";
	}
}
