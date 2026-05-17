# TurfTime2 – Development Session Summary
**Branch:** master  
**Date:** 2025  
**Scope:** Timer/Report UX, Roster Ordering, Swipe/Drag Gesture Reliability

---

## Overview

This session covered a wide range of issues across the TurfTime2 .NET MAUI app, from timer lifecycle correctness and report data integrity, through roster ordering and performance, to a deep investigation into unreliable swipe and drag gestures on Android. Each issue is documented below with its symptom, root cause, and the fix applied.

---

## Issue 1 – Report Missing End Time and Duration

### Symptom
The generated match report did not show the match End Time or total match duration. Only the Start Time was present.

### Root Cause
The `ReportsPage.GenerateHtmlReport()` method was not reading the `EndTime` field from `GameSession`, and no duration calculation was performed.

### Fix
Updated `ReportsPage` to read `session.EndTime` and calculate actual duration as `EndTime - StartTime`, then include both in the HTML report output.

---

## Issue 2 – Countdown Preset Reverting to Default After App Restart

### Symptom
The Rotate timer countdown (e.g. set to 1:30) reverted to the default 2:00 every time the app was redeployed or restarted.

### Root Cause
The countdown preset was stored only in memory and never persisted to device storage.

### Fix
Used `Preferences` (MAUI key-value store) to persist the preset under `game.countdownPresetSeconds`. The `GameViewModel` constructor now restores the saved value on startup.

---

## Issue 3 – Half-Time Button Auto-Pausing Timers Instead of Being User-Driven

### Symptom
When the countdown reached the half-time mark, all timers automatically paused. The user wanted the timers to keep running until they explicitly pressed "1/2 Time".

### Root Cause
The `OnHalfTimeReached` event handler was calling `PauseGame()` automatically rather than only changing the button label.

### Fix
Changed `OnHalfTimeReached` to update the button label to "1/2 Time" only. Pressing "1/2 Time" then pauses the timers, and pressing "Resume" restarts them — matching the intended user-driven flow.

---

## Issue 4 – Vibration Not Firing at Countdown Zero (First Rotation)

### Symptom
The device did not vibrate when the Rotate countdown first reached zero. Subsequent rotations vibrated correctly.

### Root Cause
Two problems:
1. `GamePage.OnAppearing()` was not re-subscribing `_vm.PropertyChanged` for an already-created ViewModel, so the rotation alert handler was disconnected after navigation.
2. The first rotation countdown tick was processing entirely on the UI thread, causing the alert to be delayed or dropped under heavy UI load.

### Fix
- Added `_vm.PropertyChanged += OnViewModelPropertyChanged` re-subscription in `OnAppearing()` to handle reused ViewModels.
- Moved countdown arithmetic to the background tick thread in `GameTimerService`, leaving only UI notifications to be marshalled to the main thread. This eliminated the first-rotation delay.

---

## Issue 5 – Local Reports Not Loading

### Symptom
The Reports page showed no sessions after a match, even though sessions were being recorded.

### Root Cause
`ReportsPage` was attempting to read sessions from WebView/localStorage, but the app's native implementation stores them in `Preferences` under `roster.sessionHistory.v1`. Additionally, property names in the JSON were PascalCase in C# (`StartTime`, `EndTime`) but the deserialiser was expecting camelCase.

### Fix
- Updated `ReportsPage` to load sessions from `Preferences` (`roster.sessionHistory.v1` and `roster.currentSession.v1`).
- Aligned `GameSession`/`GameEvent` JSON property names to match PascalCase serialisation.

---

## Issue 6 – Player Name Editing Not Available in Native MAUI Roster

### Symptom
In the WebView-based roster, player names could be tapped and edited inline. The native MAUI roster had no equivalent.

### Root Cause
The native roster had no rename gesture or prompt; the feature existed only in the WebView JavaScript implementation (`roster-manager.js`).

### Fix
- Added `RenamePlayer(Player player, string newName)` to `GameViewModel` (updates the model and autosaves).
- Added `OnPlayerNameTapped` handler in `GamePage.xaml.cs` that opens a `DisplayPromptAsync` rename dialog.
- Attached a `TapGestureRecognizer` on the player name label in the row template.

---

## Issue 7 – Roster Display Order After Rotation Was Incorrect

### Symptom
After a rotation, active players were not visually grouped by role. The order appeared random or based on insertion order.

### Root Cause
`RefreshDisplayItems()` rebuilt the `ObservableCollection` destructively (Clear + re-add), and no sort order was applied during the rebuild.

### Fix
- Changed `RefreshDisplayItems()` to sort display items as: **Field players → Goalie → Bench → Inactive header → Inactive players**.
- Replaced the destructive rebuild with a move-aware diff using `ObservableCollection.Move` to avoid unnecessary row recreation and `DragRowHandler` churn.

---

## Issue 8 – Roster Refresh Causing Sluggish UI and Log Spam

### Symptom
After a rotation, many `[DragRowHandler] 🏗️ Created DragLayoutViewGroup` and `[PERF] RefreshDisplayItems` log entries were emitted. The roster UI felt sluggish for the first rotation but not subsequent ones.

### Root Cause
- `RefreshDisplayItems()` was clearing and re-adding all items, forcing MAUI/RecyclerView to re-inflate every row handler.
- Row handlers (`DragLayoutViewGroup`) were only inflated on first render, so the first rotation bore the cost of all handler creation.

### Fix
- Adopted the move-aware diff (see Issue 7), dramatically reducing row recreation.
- Added `WarmUpRosterRowsAsync()` to `GamePage.OnAppearing()`: scrolls the roster to the bottom and back at startup, forcing all row handlers to be inflated before the first rotation.

---

## Issue 9 – Swipe Gesture Drag Indices Out of Sync After Reordering

### Symptom
After roster reordering, swiping a player row caused the wrong player to change position.

### Root Cause
The drag start/end indices were calculated against the canonical `Players` list, but after `RefreshDisplayItems()` the visual order (`DisplayItems`) differs from the canonical order.

### Fix
Changed `OnPlayerPanUpdated` to use `DisplayItems` visual indices throughout drag operations, resolving back to `Players` only at reorder commit time.

---

## Issue 10 – Swipe Animation Racing With Position Commit

### Symptom
After a swipe, the row would snap back incorrectly or land in the wrong position because the position was committed before the animation finished.

### Root Cause
`CommitSwipe()` was calling `SetPlayerPosition(...)` synchronously while the bump and snap-back animations were still running.

### Fix
Rewrote `CommitSwipe()` as `async void`, awaiting both the bump animation and the snap-back animation before calling `_vm?.SetPlayerPosition(...)`.

---

## Issue 11 – Bottom Roster Rows Could Not Be Swiped (RecyclerView Touch Stealing)

### Symptom
Swiping rows near the bottom of the roster list did nothing. Debug logs showed repeated `DOWN` events followed immediately by `🔓 Finger slipped — timer cancelled, scrolling`, with no MAUI pan gesture starting.

### Root Cause
When the `CollectionView`/RecyclerView has been scrolled down, Android's RecyclerView intercepts horizontal touch events by default, stealing the gesture stream before MAUI's `PanGestureRecognizer` could start.

### Fix
Added early horizontal-intent detection in `DragLayoutViewGroup.DispatchTouchEvent()`: on `ACTION_MOVE`, if `dx >= dy` and the slip threshold is exceeded, `Parent?.RequestDisallowInterceptTouchEvent(true)` is called immediately. This stops RecyclerView from claiming the touch stream for horizontal swipes.

---

## Issue 12 – `NativeSwipeReleased` Firing Twice Per Gesture

### Symptom
Debug logs showed `[GamePage] NativeSwipeReleased` being fired twice for each swipe, potentially causing double position commits or double snap-back animations.

### Root Cause
`DragLayoutViewGroup` could invoke `DragState.NativeSwipeReleased` both from the `_swipeLocked` path and the fallback path on the same gesture, triggering the MAUI handler twice.

### Fix
Added a one-shot `_nativeReleaseHandled` boolean flag in `GamePage`. It is set on first invocation and reset at `GestureStatus.Started`, ensuring only the first `NativeSwipeReleased` callback per gesture is acted upon.

---

## Issue 13 – Swipe Only Reliable From Extreme Edges of a Row

### Symptom
Swiping from the **center** of a player row frequently did not move the row. Swiping from the left or right edges of the row worked reliably. The problem was consistent across all rows.

### Root Cause
The `PanGestureRecognizer` was attached to the inner `Grid`, but the `Border` element (occupying the centre 70% column) and its child `Labels` were consuming touch events before the pan recognizer could start. The 15% edge columns were empty (no child views), so touches there propagated directly to the `Grid` and started the pan correctly.

Additionally, the name `Label` had its own `TapGestureRecognizer`, which also consumed touches in the center area.

### Fix applied in `GamePage.xaml` and `GamePage.xaml.cs`:

1. **`Border` marked `InputTransparent="True"`** — The Border and all its children (drag handle, position icon, name label, field-time label) no longer consume touch events. All touches pass through to the parent `Grid`.
2. **`TapGestureRecognizer` moved from the name `Label` to the outer `Grid`** — The rename tap now coexists with the `PanGestureRecognizer` on the same `Grid`, so neither competes with the other.
3. **Name `Label` marked `InputTransparent="True"`** — Belt-and-suspenders: even with the Border transparent, the label itself is also marked non-interactive.
4. **`OnPlayerNameTapped` handler updated** — Changed `sender is not Label` to `sender is not BindableObject` so the handler works correctly when the sender is the `Grid`.

---

## Files Modified in This Session

| File | Changes |
|------|---------|
| `TurfTime2/ViewModels/GameViewModel.cs` | Countdown preset persistence, half-time semantics, `RenamePlayer()`, move-aware `RefreshDisplayItems()`, roster sort order |
| `TurfTime2/GamePage.xaml.cs` | Lifecycle re-subscribe fix, `WarmUpRosterRowsAsync()`, `OnPlayerNameTapped`, drag index fix, `CommitSwipe()` async rewrite, `_nativeReleaseHandled` guard, handler updated for Grid sender |
| `TurfTime2/GamePage.xaml` | Roster row template: `Border InputTransparent`, `TapGestureRecognizer` moved to Grid, name Label `InputTransparent` |
| `TurfTime2/Platforms/Android/DragLayoutViewGroup.cs` | Early horizontal interception, one-shot release fallback |
| `TurfTime2/GameTimerService.cs` | Countdown arithmetic moved off UI thread |
| `TurfTime2/ReportsPage.xaml.cs` | End Time, duration display, local Preferences loading |
| `TurfTime2/Models/Player.cs` | `FieldTimeDisplay` property change notification |

---

*Document generated from development session chat summary.*
