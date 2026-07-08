# Session Save Debugging Guide

## Changes Made

I've added comprehensive logging throughout the session save flow to help debug why session data isn't being written to Firestore.

## What to Look For in Logs

### 1. When Game Starts (Press "Start" button)
You should see:
```
[GameLogger] ⭐ startSession() called
[GameLogger] ⭐ Match Duration: XXXX seconds
[GameLogger] ⭐ Rotation Interval: XXXX seconds
[GameLogger] ✅ New session created: {session-id}
[GameLogger] ✅ Session saved to localStorage
```

### 2. When Game Ends (Long-press "Start" button for 1 second)
You should see:
```
[RosterManager] 🔄 restartGame() called
[RosterManager] 📊 Logger exists, ending current session...
[RosterManager] 📊 Current session ID: {session-id}
[RosterManager] 🔚 Calling logger.endSession()...
[GameLogger] ⚠️ endSession() called
[GameLogger] 📊 Current session ID: {session-id}
[GameLogger] 📊 Session logs count: XX
[GameLogger] 📊 Summary calculated: {...}
[GameLogger] 🔄 Calling archiveSession()...
[GameLogger] 🗄️ archiveSession() called
[GameLogger] 📦 Archiving session: {session-id}
[GameLogger] ✅ Saved to localStorage history
[GameLogger] ☁️ Attempting Firestore save...
```

### 3. Firestore Save Attempt (JavaScript side)
You should see:
```
[GameLogger] 🔵 saveSessionToFirestore() called
[GameLogger] 🔵 Team ID from localStorage: {team-id}
[GameLogger] ✅ C# bridge found, preparing to save
[GameLogger] 📤 Session ID: {session-id}
[GameLogger] 📤 Session data length: XXXX
[GameLogger] 📤 Calling window.csharpSaveSession.postMessage()...
[GameLogger] ✅ Message posted to C# bridge
```

**If you see errors here, check:**
- ⚠️ No team ID available - means localStorage doesn't have 'roster.teamId'
- ❌ C# session save bridge NOT available - means window.csharpSaveSession is not defined

### 4. C# Bridge Detection (C# side)
You should see in Visual Studio Debug Output:
```
[GamePage] 🔵 Session save trigger detected: {timestamp}
[GamePage] 🔵 Session data length: XXXX
[GamePage] 📤 Calling SessionSaveBridge.SaveSessionToFirestore()...
[GamePage] ✅ SessionSaveBridge call completed
```

### 5. Firestore Save (C# side)
You should see:
```
[SessionSaveBridge] ==========================================
[SessionSaveBridge] 🔵 Received request to save session
[SessionSaveBridge] 🔵 JSON data length: XXXX
[SessionSaveBridge] ✅ Team ID: {team-id}
[SessionSaveBridge] ✅ Session Data Length: XXXX
[SessionSaveBridge] ✅ Session ID: {session-id}
[SessionSaveBridge] 🔑 Getting Firebase auth token...
[SessionSaveBridge] ✅ Auth token obtained
[SessionSaveBridge] 🔄 Converting to Firestore format...
[SessionSaveBridge] ✅ Firestore JSON Length: XXXX
[SessionSaveBridge] 📤 Document Path: https://firestore.googleapis.com/v1/projects/turftime-6a97b/databases/(default)/documents/teams/{team-id}/sessions/{session-id}
[SessionSaveBridge] ✅ Session saved successfully
```

## Common Issues to Debug

### Issue 1: No Session Started
**Symptoms:** You press "End" but never see "startSession() called"
**Cause:** Session wasn't started when you pressed "Start"
**Check:** Look for startSession logs when you first press "Start" button

### Issue 2: Team ID Missing
**Symptoms:** `⚠️ No team ID available`
**Cause:** localStorage doesn't have 'roster.teamId'
**Check:** Run this in browser console: `localStorage.getItem('roster.teamId')`
**Fix:** Team ID should be set when loading the game page

### Issue 3: C# Bridge Not Available
**Symptoms:** `❌ C# session save bridge NOT available!`
**Cause:** window.csharpSaveSession is not defined
**Check:** The bridge injection happens in GamePage.xaml.cs InjectSaveBridge()
**Fix:** Verify bridge is injected by checking browser console for: `[C# Bridge] ✓ C# save bridge injected`

### Issue 4: Polling Not Detecting Trigger
**Symptoms:** JavaScript shows "Message posted" but C# never detects it
**Cause:** C# polling loop not reading localStorage correctly
**Check:** Run in browser console: `localStorage.getItem('_pending_session_save_trigger')`
**Fix:** Should show a timestamp, if it persists then C# polling isn't clearing it

### Issue 5: Firestore Auth Failure
**Symptoms:** `❌ ERROR: Could not get auth token`
**Cause:** Firebase authentication failed
**Check:** Network errors, Firebase configuration issues
**Fix:** Verify Firebase project ID and API key are correct

## How to Test

1. **Start the app** (make sure it's a full restart to get the new logging)
2. **Press "Start"** - Watch for startSession logs
3. **Do some actions** (swipes, rotations, etc.)
4. **Long-press "Start" for 1 second** - Should trigger restartGame()
5. **Watch both browser console and Visual Studio Debug Output**
6. **Check Firestore Console** - Go to Firebase Console → Firestore Database → teams/{your-team-id}/sessions

## Expected Firestore Structure

```
teams/
  {team-id}/
    sessions/
      {session-id}/
        sessionId: string
        startTime: timestamp
        endTime: timestamp
        location: string (optional)
        matchDuration: number
        rotationInterval: number
        logs: array of log entries
        summary: object with playerStats
```

## Next Steps Based on Logs

Share the complete log output (both browser console and VS Debug Output) from:
1. Pressing "Start"
2. Playing for a bit
3. Long-pressing "Start" to end the game

This will show exactly where the flow is breaking.
