# View Initialization Bug Fix

## Problem
When the app opened, users were seeing the table view instead of the swipe view, and no players were visible.

## Root Causes

### 1. Missing Firebase Methods (Critical)
The Firebase cloud sync methods were never added to `roster-manager.js`:
- `initFirebase()` - Initialize Firebase auth
- `syncWithCloud()` - Timestamp-based sync
- `saveToFirestore()` - Upload to cloud
- `loadFromFirestore()` - Download from cloud
- `applyLoadedData()` - **THIS WAS THE BREAKING ISSUE**

The code called `this.applyLoadedData(model)` on line 2744, but the method didn't exist. This caused a JavaScript error that broke initialization, resulting in:
- Players not loading from localStorage
- View not initializing correctly
- Potential fallback to table view with no data

### 2. View Mode Saved in localStorage
The `saveToStorage()` method was saving `viewMode` to localStorage. On subsequent app opens, if the user had switched to table view before closing, the app would reopen in table view instead of the default swipe view.

## Fixes Applied

### Fix 1: Added All Firebase Cloud Sync Methods
Added complete Firebase integration to `roster-manager.js`:

```javascript
// ============================================================================
// FIREBASE CLOUD SYNC METHODS
// ============================================================================

async initFirebase() { ... }
async syncWithCloud() { ... }
async saveToFirestore(model) { ... }
async loadFromFirestore() { ... }
applyLoadedData(model) { ... }

// ============================================================================
// END FIREBASE CLOUD SYNC METHODS
// ============================================================================
```

**Key Features:**
- Anonymous authentication (no login required)
- Timestamp-based conflict resolution (prevents data loss)
- Offline-first (localStorage always works)
- Auto-sync on connection restore
- Background cloud saves (non-blocking)

### Fix 2: Updated saveToStorage() for Dual-Save
Modified `saveToStorage()` to save to both localStorage AND Firebase:

```javascript
// Save to localStorage (always works, even offline)
localStorage.setItem(this.STORAGE_KEY, JSON.stringify(model));

// Also save to Firebase cloud (background, non-blocking)
if (this.firebaseReady) {
    this.saveToFirestore(model).catch(err => {
        console.error('[RosterManager] Background cloud save failed:', err);
    });
}
```

### Fix 3: Don't Restore viewMode from Saved Data
Modified `applyLoadedData()` to NOT restore `viewMode` from localStorage:

```javascript
// Don't restore viewMode from saved data - always use preference
// This ensures the app starts with the user's preferred view
```

This ensures the app always starts in the user's **preferred** view (swipe by default), not the last view they were using.

## How It Works Now

### On App Startup:
1. **Build Rows** - Create 16 empty player rows
2. **Load localStorage** - Apply saved data if exists (players, times, scores)
3. **Initialize Firebase** - Authenticate anonymously and sync with cloud
4. **Initialize View** - Show swipe view (default) or table view (if preference set)
5. **Mark Next Players** - Highlight rotation candidates

### Swipe View (Default):
- All players visible and swipeable
- Swipe left: move towards Field → Goalie
- Swipe right: move towards Bench → Inactive
- Long press: reorder players
- Tap name: edit name (during setup) or set as next to rotate (during game)

### Table View (Optional):
- Checkbox-based player management
- Shows all players during setup
- Hides inactive players during game (unless toggled)

## Expected Behavior After Fix

✅ App opens in swipe view by default
✅ All 16 players visible
✅ Saved roster data loads correctly
✅ Cloud sync works in background
✅ No JavaScript errors in console

## Testing Checklist

- [ ] Open app - should show swipe view
- [ ] Check console for Firebase init messages
- [ ] Assign players to positions via swipe
- [ ] Close and reopen app - roster should be saved
- [ ] Check cloud sync logs in console
- [ ] Test offline mode (airplane mode) - should still work
- [ ] Test multi-device sync (if possible)

## Console Log Examples

**Successful Initialization:**
```
[RosterManager] ✓ Loaded from localStorage
[Firebase] 🔄 Initializing Firebase authentication...
[Firebase] ✓ Authenticated anonymously: abc123xyz
[Firebase] 🔄 Starting sync check...
[Firebase] ✓ Data is in sync
```

**First Time User (No Saved Data):**
```
[Firebase] 🔄 Initializing Firebase authentication...
[Firebase] ✓ Authenticated anonymously: abc123xyz
[Firebase] ℹ️ No local data to sync
[Firebase] ℹ️ No cloud data found
```

## Files Modified

1. `wwwroot/js/roster-manager.js`
   - Added Firebase cloud sync methods (5 new methods, ~150 lines)
   - Updated `saveToStorage()` to dual-save (localStorage + Firebase)
   - Updated `applyLoadedData()` to not restore viewMode

## Build Status
✅ Build successful - ready for testing

## Next Steps
1. Test app startup behavior
2. Enable Anonymous auth in Firebase Console (if not already done)
3. Monitor console for sync messages
4. Test multi-device sync if possible
