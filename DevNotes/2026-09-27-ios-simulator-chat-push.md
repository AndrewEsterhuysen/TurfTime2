# iOS Simulator: chat push / unread chips (2026-09-27)

## Symptom

Cross-device chat unread worked for:

- Android Emulator → **iOS Device** (banner + Multi-Chat chip)
- iOS Device → **Android Emulator** (notification + unread)

**iOS Simulator** did **not** show a notification or light the Team Chat Identifier unread flag. Opening Chat still showed the new message (Firestore sync when the Chat page subscribes).

## Investigation

1. Restarted Simulator `D54318A8-…` (iPhone 17 / iOS 26.5) and redeployed Debug `3.4.0 (37)` / `857e489`.
2. Prefs on Simulator: `chat_unread_team_ids=[]`, `chat_unread_count=0`; online teams include `u17g-adamstown-rosebuds-sz4338`; `chat_user_id=JlV3diqBQ4OUXwh0QGvVP0RuYZN2`.
3. Cloud Function `sendChatNotification` for Android_Emulator → that team:

   ```text
   Members=3, skippedSender=1, tokens=2 […]
   Sent. Success=2, Failure=0
   ```

   FCM accepted delivery to the two non-sender tokens (device + simulator). Real iPhone received; Simulator did not surface `WillPresent` / `IncrementFromPush`.
4. Simulator `apsd` is connected (development) and topic `com.andrewestherhuysen.turftime` is enabled after launch — registration path looks healthy; **delivery to the Simulator process is unreliable**.
5. Unread chips are **push-driven only** (`ChatBadgeHelper.IncrementFromPush` from FCM / `WillPresent`). ChatPage’s Firestore listener runs only while Chat is open for the selected team; leaving Chat disposes it. That is intentional (minimize always-on Firestore listeners / billed read volume).

## Conclusion

**Simulator-only / APNs-to-sim flakiness**, not a Multi-Chat or Cloud Function regression. Physical iOS + Android paths remain the source of truth for push → unread.

Earlier Multi-Chat work *did* see `[FCM] WillPresent foreground banner` on Simulator during debugging — so Simulator push can work intermittently, but must not be relied on for unread QA.

## What we will not do

- No background Firestore chat listeners or polling just to mark unread on Simulator (cost at scale).

## QA guidance

| Target | Chat message sync | Push banner / unread chip |
|--------|-------------------|---------------------------|
| iOS Device | Yes | Yes (authoritative) |
| Android Emulator / device | Yes | Yes |
| iOS Simulator | Yes (when Chat open) | **Do not rely on** |

After Simulator reboot/redeploy: open **Chat** once so FCM token re-registers; still treat push as best-effort on Simulator.

## Related

- `docs/guides/APNS_SETUP.md` (Simulator caveat)
- `Helpers/ChatBadgeHelper.cs` — `IncrementFromPush` / `UpdateFromMessages`
- `Services/FcmService.cs` — `TurfTimeNotificationDelegate.WillPresentNotification`
- `functions/index.js` — `sendChatNotification`
