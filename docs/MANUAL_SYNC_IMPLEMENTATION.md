# Manual Cloud Sync Button Implementation

## Overview
Added a manual sync button to the Settings menu that allows users to trigger cloud synchronization on demand.

## Implementation Details

### 1. UI Changes (SettingsPage.xaml)
- Added new "☁️ Sync Now" button with description "Manually sync roster data with cloud"
- Positioned after the Team View option
- Uses consistent styling with other settings items (green frame, icon, description)
- Visual feedback: background color changes briefly when tapped

### 2. Helper Class (CloudSyncHelper.cs)
Created a static helper class to facilitate cross-tab communication:
```csharp
public static class CloudSyncHelper
{
    public static event EventHandler? ManualSyncRequested;
    
    public static void RequestManualSync()
    {
        ManualSyncRequested?.Invoke(null, EventArgs.Empty);
    }
}
```

**Why needed**: Settings and Game tabs are separate pages in the MAUI Shell app. The WebView (which runs the JavaScript) is in GamePage, but the sync button is in SettingsPage. The helper class bridges this gap using the Observer pattern.

### 3. Settings Page Handler (SettingsPage.xaml.cs)
- Added `OnSyncNowTapped` method that:
  1. Changes button background color for visual feedback
  2. Calls `CloudSyncHelper.RequestManualSync()`
  3. Restores button color after 500ms
  4. Shows confirmation alert: "Sync request sent. Check the Game tab for sync status."

### 4. Game Page Sync Integration (GamePage.xaml.cs)
- Subscribed to `CloudSyncHelper.ManualSyncRequested` event in constructor
- Added `TriggerManualSync()` method that:
  1. Logs sync trigger to debug console
  2. Calls JavaScript via WebView: `window.rosterManagerInstance.syncWithCloud()`
  3. Handles any errors gracefully

### 5. JavaScript Exposure (roster-manager.js)
- Modified DOMContentLoaded listener to expose rosterManagerInstance to window object:
  ```javascript
  window.rosterManagerInstance = rosterManagerInstance;
  ```
- This allows C# code to call: `window.rosterManagerInstance.syncWithCloud()`

## How It Works

### User Flow:
1. User navigates to Settings tab
2. Taps "☁️ Sync Now" button
3. Button briefly changes color (visual feedback)
4. Alert confirms: "Sync request sent"
5. Behind the scenes:
   - SettingsPage fires event via CloudSyncHelper
   - GamePage (running in background) receives event
   - GamePage calls JavaScript sync method via WebView
   - JavaScript executes timestamp-based sync logic (see FIREBASE_SYNC_STRATEGY.md)

### Technical Flow:
```
SettingsPage (UI)
    ↓ OnSyncNowTapped
CloudSyncHelper.RequestManualSync()
    ↓ Event fired
GamePage.TriggerManualSync()
    ↓ WebView.EvaluateJavaScriptAsync
JavaScript: rosterManagerInstance.syncWithCloud()
    ↓ Executes Firebase sync
    Compare timestamps
    Upload or download as needed
```

## Why Manual Sync?

The app already has **automatic sync** that triggers:
- On app startup (when Firebase auth completes)
- When connection is restored (after being offline)

**Manual sync adds**:
- User control and visibility
- Debugging capability
- Immediate sync without waiting for triggers
- Peace of mind for users unsure if auto-sync worked

## Testing Checklist

Before testing, ensure:
- [ ] Anonymous authentication is enabled in Firebase Console
  (Firebase Console → Authentication → Sign-in method → Anonymous → Enable)

### Test Scenarios:

1. **Basic Sync Test**
   - Open Settings tab
   - Tap "Sync Now"
   - Verify alert appears
   - Switch to Game tab
   - Check browser console for sync logs

2. **Offline Sync Test**
   - Enable airplane mode
   - Make roster changes
   - Tap "Sync Now" (should queue for later)
   - Disable airplane mode
   - Verify changes sync to cloud

3. **Multi-Device Test**
   - Make changes on Device A
   - Tap "Sync Now" on Device A
   - Open app on Device B
   - Verify Device B shows Device A's changes

4. **Conflict Resolution Test**
   - Make changes on Device A (don't sync)
   - Make different changes on Device B
   - Tap "Sync Now" on Device B (uploads B's data)
   - Tap "Sync Now" on Device A (should download B's data if B is newer)

## Console Log Examples

When manual sync is triggered, you should see:
```
[CloudSync] Manual sync requested
[CloudSync] Triggering manual sync via JavaScript
[Firebase] 🔄 Starting manual sync check...
[Firebase] ⬆️ Local data is newer - uploading to cloud
```

or

```
[Firebase] ⬇️ Cloud data is newer - downloading
[Firebase] ✓ Applied cloud data to roster
```

## Files Modified

1. `TurfTime2/SettingsPage.xaml` - Added sync button UI
2. `TurfTime2/SettingsPage.xaml.cs` - Added sync button handler
3. `TurfTime2/GamePage.xaml.cs` - Added sync event subscription and trigger method
4. `wwwroot/js/roster-manager.js` - Exposed rosterManagerInstance to window object
5. `TurfTime2/CloudSyncHelper.cs` - New file for cross-tab communication

## Build Status
✅ Build successful - ready for testing

## Next Steps
1. Enable Anonymous auth in Firebase Console
2. Test sync functionality on device/emulator
3. Monitor console logs for sync behavior
4. Test offline scenarios
5. Test multi-device sync if possible
