# Enhancement: Added Bench Time to Player Statistics in Reports

## Summary
Enhanced the match reports to display **both field time and bench time** for each player, providing complete visibility into how much time each player spent on the field versus on the bench.

## Changes Made

### 1. JavaScript (game-logger.js)
**Updated `aggregatePlayerStats()` method:**

#### Calculation Logic:
```javascript
// Field time is tracked by the counter (existing)
stats.timeOnField = row.counterSeconds || 0;

// Bench time calculation (NEW)
// Bench Time = Game Duration - Field Time
// (assuming player was either on field or bench, not inactive)
const isInactive = row.cbInactive && row.cbInactive.checked;
if (!isInactive && gameDuration > 0) {
    stats.timeOnBench = Math.max(0, gameDuration - stats.timeOnField);
}
```

#### Enhanced Features:
- ✅ Captures all players (even those with no rotations)
- ✅ Calculates bench time automatically
- ✅ Handles inactive players correctly (they have 0 bench time)
- ✅ Property renamed from `timeAsBench` to `timeOnBench` for consistency

### 2. C# HTML Report (ReportsPage.xaml.cs - GenerateHtmlReport)
**Updated Player Statistics table:**

**Before:**
| Player | Field Time | Rotations In | Rotations Out |
|--------|------------|--------------|---------------|

**After:**
| Player | Field Time | Bench Time | Rotations In | Rotations Out |
|--------|------------|------------|--------------|---------------|

#### Example Output:
```html
<tr>
    <td>John Doe</td>
    <td>30:00</td>      <!-- Field Time -->
    <td>15:00</td>      <!-- Bench Time -->
    <td>3</td>          <!-- Rotations In -->
    <td>3</td>          <!-- Rotations Out -->
</tr>
```

### 3. C# PDF Report (ReportsPage.xaml.cs - GeneratePdfFromHtmlAsync)
**Updated QuestPDF table:**
- Added 5th column for Bench Time
- Updated column definitions from 4 to 5 columns
- Added "Bench Time" header with green background
- Display bench time in MM:SS format

## How It Works

### Bench Time Calculation:
```
For Active Players:
Bench Time = Total Game Duration - Field Time

For Inactive Players:
Bench Time = 0 (they don't participate)
```

### Example Scenario:
- **Game Duration:** 45 minutes (2700 seconds)
- **Player A:** 30 minutes on field
  - Field Time: 30:00
  - Bench Time: 15:00 (45 - 30)
- **Player B:** 15 minutes on field
  - Field Time: 15:00
  - Bench Time: 30:00 (45 - 15)
- **Player C:** Marked inactive
  - Field Time: 0:00
  - Bench Time: 0:00 (not calculated for inactive players)

## Data Structure

### Session JSON Format:
```json
{
  "summary": {
    "playerStats": [
      {
        "playerName": "John Doe",
        "timeOnField": 1800,      // 30 minutes in seconds
        "timeOnBench": 900,        // 15 minutes in seconds (NEW)
        "timeAsGoalie": 0,
        "rotationsIn": 3,
        "rotationsOut": 3
      }
    ]
  }
}
```

## Benefits

### For Coaches:
- ✅ **Fair Play Tracking** - See if players are getting equal time
- ✅ **Substitution Analysis** - Understand rotation patterns
- ✅ **Player Development** - Track minutes for development targets
- ✅ **Game Strategy** - Analyze time distribution effectiveness

### For Players/Parents:
- ✅ **Transparency** - Clear visibility of playing time
- ✅ **Progress Tracking** - Monitor playing time trends over season
- ✅ **Fair Treatment** - Verify equitable distribution of minutes

## Report Display Examples

### HTML Report (Browser):
```
Player Statistics
┌──────────────┬────────────┬────────────┬───────────────┬────────────────┐
│ Player       │ Field Time │ Bench Time │ Rotations In  │ Rotations Out  │
├──────────────┼────────────┼────────────┼───────────────┼────────────────┤
│ John Doe     │ 30:00      │ 15:00      │ 3             │ 3              │
│ Jane Smith   │ 25:00      │ 20:00      │ 4             │ 4              │
│ Bob Johnson  │ 20:00      │ 25:00      │ 5             │ 5              │
└──────────────┴────────────┴────────────┴───────────────┴────────────────┘
```

### PDF Report:
Same table structure with:
- Green header row
- Alternating white/gray row backgrounds
- Professional formatting
- Time in MM:SS format

## Testing

### To Verify:
1. **Stop and restart** debugging
2. Play a complete game with rotations
3. End game (long-press Start)
4. Go to Settings → Reports
5. Check Player Statistics table:
   - ✅ Should have 5 columns
   - ✅ Field Time + Bench Time should = Game Duration (for active players)
   - ✅ Times displayed in MM:SS format
6. Test Email PDF:
   - ✅ PDF should also show bench time column

### Sample Test:
- 10-minute game (600 seconds)
- Player A: 6 minutes field → Should show 6:00 field, 4:00 bench
- Player B: 4 minutes field → Should show 4:00 field, 6:00 bench

## Files Modified
1. `wwwroot/js/game-logger.js` - Enhanced `aggregatePlayerStats()` method
2. `TurfTime2/ReportsPage.xaml.cs` - Updated HTML and PDF report generators

## Future Enhancements

### Potential Additions:
- 📊 **Time Charts** - Visual representation of field/bench distribution
- 📈 **Season Averages** - Average field/bench time across all games
- ⚖️ **Fairness Score** - Calculate how equitably time is distributed
- 🎯 **Target Time Tracking** - Set and track playing time goals per player
- 📱 **Push Notifications** - Alert when player hasn't rotated in X minutes
