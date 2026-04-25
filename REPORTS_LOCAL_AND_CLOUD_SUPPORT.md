# Reports Feature: Local and Cloud Team Support

## Overview
The Reports feature now supports **both local and cloud teams**. Session data is handled differently based on the team mode to optimize performance and storage.

## How It Works

### 1. Session Data Storage

#### Local Teams (team_mode = "local")
- ✅ Sessions saved to **localStorage** only
- ✅ No Firestore/cloud storage used
- ✅ Data persists on the device
- ✅ No internet required to save or view reports

#### Cloud Teams (team_mode = "shared" or team ID doesn't start with "local_")
- ✅ Sessions saved to **localStorage** (for backup/offline access)
- ✅ Sessions saved to **Firestore** (for cloud sync and multi-device access)
- ✅ Team members can view reports from any device
- ✅ Reports persist even if app is uninstalled (stored in cloud)

### 2. Session Save Flow

#### When Game Ends (Long-press Start button for 1 second):

**JavaScript Side:**
1. `restartGame()` called → `logger.endSession()` 
2. `endSession()` → `archiveSession()`
3. `archiveSession()` → Saves to localStorage history
4. `archiveSession()` → Calls `saveSessionToFirestore(session)`
5. `saveSessionToFirestore()` checks team ID:
   - If `teamId.startsWith('local_')` → Skip Firestore (local team)
   - Otherwise → Send to C# bridge for Firestore save (cloud team)

**C# Side (Cloud Teams Only):**
1. `GamePage.cs` polling detects `_pending_session_save_trigger`
2. Calls `SessionSaveBridge.SaveSessionToFirestore(jsonData)`
3. Authenticates with Firebase
4. Converts to Firestore format
5. Saves to `teams/{teamId}/sessions/{sessionId}`

### 3. Report Loading Flow

#### When Opening Reports Page:

**ReportsPage.xaml.cs:**
1. Gets `team_id` and `team_mode` from Preferences
2. Routes to appropriate loader:
   - **Local Teams** → `LoadLocalSessionsAsync()` → Reads from localStorage via JavaScript
   - **Cloud Teams** → `LoadCloudSessionsAsync()` → Reads from Firestore via `SessionLoadHelper`

#### Local Team Loading:
```csharp
1. Inject JavaScript into WebView
2. Read localStorage.getItem('roster.sessionHistory.v1')
3. Parse JSON and extract most recent session
4. Generate HTML report
```

#### Cloud Team Loading:
```csharp
1. Call SessionLoadHelper.LoadSessionsForTeamAsync(teamId)
2. Get list of sessions from Firestore
3. Load most recent session data
4. Generate HTML report
```

### 4. Data Structure

#### localStorage Format:
```json
{
  "sessions": [
    {
      "sessionId": "uuid-string",
      "startTime": "2024-04-25T16:30:00.000Z",
      "endTime": "2024-04-25T17:15:00.000Z",
      "location": null,
      "matchDuration": 5400,
      "rotationInterval": 120,
      "logs": [
        {
          "id": "uuid",
          "timestamp": "2024-04-25T16:30:05.000Z",
          "eventType": "game_started",
          "description": "Match started - 90 minute game",
          "playerName": null,
          "details": {...}
        }
      ],
      "summary": {
        "totalRotations": 15,
        "duration": 2700,
        "playerStats": [
          {
            "playerName": "John Doe",
            "timeOnField": 1800,
            "timeAsBench": 900,
            "timeAsGoalie": 0,
            "rotationsIn": 3,
            "rotationsOut": 3
          }
        ]
      }
    }
  ]
}
```

#### Firestore Format:
```
teams/{teamId}/sessions/{sessionId}
  ├── sessionId: string
  ├── startTime: timestamp
  ├── endTime: timestamp
  ├── location: string
  ├── matchDuration: number
  ├── rotationInterval: number
  ├── logs: array<map>
  └── summary: map
      ├── totalRotations: number
      ├── duration: number
      └── playerStats: array<map>
```

## Benefits of This Approach

### For Local Teams:
- ✅ **No cloud dependency** - Works completely offline
- ✅ **Privacy** - Data stays on device
- ✅ **Performance** - No network calls needed
- ✅ **Storage** - No Firestore costs

### For Cloud Teams:
- ✅ **Multi-device access** - View reports from any device
- ✅ **Team sharing** - All team members can see reports
- ✅ **Backup** - Data persists in cloud
- ✅ **Historical analysis** - Can track team performance over time

## Logging and Debugging

### JavaScript Console Logs:
```
[GameLogger] 🔵 saveSessionToFirestore() called
[GameLogger] 🔵 Team ID from localStorage: local_abc123
[GameLogger] ℹ️ Local team detected, skipping Firestore save (localStorage only)
```

OR for cloud teams:
```
[GameLogger] 🔵 saveSessionToFirestore() called
[GameLogger] 🔵 Team ID from localStorage: team16-qt4y3z
[GameLogger] ✅ C# bridge found, preparing to save to Firestore
[GameLogger] 📤 Session ID: abc-123-def-456
[GameLogger] ✅ Message posted to C# bridge for Firestore save
```

### Visual Studio Debug Output:
```
[ReportsPage] Team ID: local_abc123, Mode: local
[ReportsPage] Loading sessions from localStorage for local team
[ReportsPage] Session history JSON length: 5432
[ReportsPage] Found 3 local sessions
[ReportsPage] Latest session length: 1234
```

OR for cloud teams:
```
[ReportsPage] Team ID: team16-qt4y3z, Mode: shared
[ReportsPage] Loading sessions from Firestore for cloud team
[SessionLoadHelper] Loading sessions for team: team16-qt4y3z
[SessionLoadHelper] ✅ Loaded 5 sessions
```

## Testing the Feature

### Test Local Team Reports:
1. Create a local team
2. Play a game (press Start, do some actions, long-press Start to end)
3. Go to Settings → Reports
4. Should see the match report
5. Check browser console for "Local team detected, skipping Firestore save"
6. Check Firestore Console - NO session documents should appear

### Test Cloud Team Reports:
1. Create or join a cloud team
2. Play a game (press Start, do some actions, long-press Start to end)
3. Go to Settings → Reports
4. Should see the match report
5. Check browser console for "Message posted to C# bridge"
6. Check Firestore Console - Session document should appear under teams/{teamId}/sessions/

### Test Email Functionality:
1. View any report (local or cloud)
2. Press "Email HTML" - Should open email composer with HTML report
3. Press "Email PDF" - Should generate PDF and open email with attachment

## Future Enhancements

### Potential Features:
- 📊 **Multiple Report Selection** - View/compare multiple sessions
- 📈 **Statistics Dashboard** - Aggregate stats across all sessions
- 🔍 **Search/Filter** - Find sessions by date, location, players
- 📤 **Export to CSV** - Download session data for external analysis
- 🏆 **Player Rankings** - Track individual player performance over time
- 📅 **Season Summaries** - View cumulative stats for a season

### Technical Improvements:
- ⚡ **Caching** - Cache recently loaded reports
- 🔄 **Background Sync** - Auto-sync local sessions to cloud when internet available
- 💾 **Session Compression** - Reduce localStorage/Firestore storage size
- 🎨 **Custom Report Templates** - Let users customize report appearance
