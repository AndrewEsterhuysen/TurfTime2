# Quick Reference: Reports Feature

## ✅ What Works Now

### Both Local and Cloud Teams:
- ✅ Session data captured during games
- ✅ Reports accessible via Settings → Reports
- ✅ HTML report generation with Turf Time branding
- ✅ Email HTML functionality
- ✅ Email PDF functionality
- ✅ Player statistics (field time, rotations)
- ✅ Match timeline with all events
- ✅ Automatic session save when game ends

### Local Teams Specific:
- ✅ Data stored in browser localStorage
- ✅ No internet required
- ✅ Complete offline functionality
- ✅ Privacy - data stays on device

### Cloud Teams Specific:
- ✅ Data synced to Firestore
- ✅ Multi-device access to reports
- ✅ Team-wide report visibility
- ✅ Cloud backup of all sessions

## 🎮 How to Use

### 1. Play a Game
- Open a team (local or cloud)
- Go to Game page
- Press **Start** to begin
- Play the match (swipe players, do rotations, etc.)
- **Long-press Start button for 1 second** to end game
  - This saves the session and makes it available for reports

### 2. View Reports
- Go to Settings → Reports
- The most recent session will display automatically
- Scroll through the report to see:
  - Match summary (date, time, duration, location)
  - Match timeline (chronological list of all events)
  - Player statistics (field time, rotations for each player)

### 3. Email Reports
- **Email HTML**: Sends beautifully formatted HTML email
  - Click "📧 Email HTML"
  - Email composer opens with full report in body
  - Add recipients and send

- **Email PDF**: Generates PDF and attaches to email
  - Click "📄 Email PDF"
  - Wait for PDF generation
  - Email composer opens with PDF attached
  - Add recipients and send

## 🔍 Troubleshooting

### "No match data available yet"
**Cause:** No completed games
**Solution:** Play a game and long-press Start to end it

### Report shows but data looks incomplete
**Cause:** Game ended mid-session or not properly ended
**Solution:** Complete full game cycle (Start → Play → Long-press Start to end)

### Can't see email buttons
**Cause:** No report data loaded
**Solution:** Ensure you have completed at least one game session

### Local team report not showing
**Cause:** localStorage might be cleared
**Solution:** 
- Check browser console for errors
- Play another game to create new session
- localStorage data: `roster.sessionHistory.v1`

### Cloud team report not showing
**Cause:** Firestore sync might have failed
**Solution:**
- Check internet connection
- Check Visual Studio debug output for Firestore errors
- Verify Firebase authentication succeeded
- Check Firestore Console for session documents

## 📊 What Gets Tracked

### Match Information:
- Start date and time
- End date and time
- Match duration (total minutes)
- Location (if specified)
- Rotation interval

### Events Logged:
- Game started
- Game paused
- Game resumed
- Half time reached
- Second half started
- Game ended/restarted
- Each rotation executed
- Player position changes
- Score changes (if tracked)

### Player Statistics:
- Total time on field (minutes:seconds)
- Total rotations in
- Total rotations out
- Time as goalie (if applicable)
- Time on bench (if applicable)

## 🎯 Best Practices

### For Accurate Reports:
1. ✅ Start the game timer when match begins
2. ✅ Use rotations and swipes as normal during play
3. ✅ Pause if taking breaks (preserves timing)
4. ✅ **Always end game with long-press** (don't just exit app)
5. ✅ Avoid force-closing the app mid-game

### For Local Teams:
- Don't clear browser data/cache
- Keep localStorage enabled
- Session history limited to 20 sessions (oldest auto-deleted)

### For Cloud Teams:
- Ensure internet connection when ending games
- Sessions stored indefinitely in Firestore
- All team members can view all sessions
- Consider privacy when sharing reports

## 🆘 Known Limitations

### Current Limitations:
- Only most recent session shown (no multi-session view yet)
- Cannot delete individual sessions from UI
- Cannot edit session data after save
- Email functionality requires email app on device
- PDF generation requires internet (for fonts/resources)

### Storage Limits:
- **localStorage**: ~5-10MB per origin (browser dependent)
- **Firestore**: 1GB free tier, then paid
- **Session History**: Local teams keep 20 sessions max

## 📝 File Locations

### Modified Files:
- `wwwroot/js/game-logger.js` - Session tracking and save logic
- `TurfTime2/ReportsPage.xaml` - Reports UI
- `TurfTime2/ReportsPage.xaml.cs` - Report loading and generation
- `TurfTime2/SessionLoadHelper.cs` - Firestore session loader
- `TurfTime2/SessionSaveBridge.cs` - Firestore session saver

### Storage Locations:
- **Local**: Browser localStorage key `roster.sessionHistory.v1`
- **Cloud**: Firestore path `teams/{teamId}/sessions/{sessionId}`
