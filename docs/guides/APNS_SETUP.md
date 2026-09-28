# APNs setup for Turf Time iOS push (required)

Cloud Function logs showed:

```text
messaging/third-party-auth-error
APNs auth failed. Upload an APNs Authentication Key (.p8) in
Firebase Console → Project settings → Cloud Messaging → Apple app configuration.
```

Without this key, **Firebase cannot deliver any push to iOS**, even when the device has a valid FCM token.

## One-time setup (Apple + Firebase)

### 1. Create an APNs Auth Key in Apple Developer

1. Open [Apple Developer → Keys](https://developer.apple.com/account/resources/authkeys/list)
2. **+** create a key
3. Name: e.g. `TurfTime APNs`
4. Enable **Apple Push Notifications service (APNs)**
5. Continue → Register → **Download** the `.p8` file (only once)
6. Note:
   - **Key ID** (10 characters)
   - **Team ID** (`YT6V9JS4F9` for this project)

### 2. Upload the key to Firebase

1. [Firebase Console](https://console.firebase.google.com/) → project **turf-timer**
2. ⚙ **Project settings** → **Cloud Messaging**
3. Under **Apple app configuration** / iOS app `com.andrewestherhuysen.turftime`:
   - Upload the **.p8** APNs Authentication Key
   - Enter **Key ID** and **Team ID**
4. Save

One key works for both Development and Production builds (unlike certificates).

### 3. Confirm app side

- Debug builds use `aps-environment` = **development** (`Platforms/iOS/Entitlements.plist`)
- Release uses **production** (`Entitlements.Release.plist`)
- App must prompt for Notifications and show a toggle under iOS **Settings → Turf Time → Notifications**

### 4. Verify end-to-end

1. Open Turf Time on **both** devices while on a shared team (or open **Chat**)
2. Allow notification permission when asked
3. Confirm logs: `[FCM] ✅ Token saved for team=…`
4. Background or lock the **receiving** device
5. Send a chat from the other device
6. Expect a notification; Cloud Function log should show `Success=1, Failure=0`

### iOS Simulator caveat (unread / Multi-Chat chips)

**Do not use the iOS Simulator as the authority for chat push or unread chips.**

- Unread Team Chat Identifiers and the Chat tab badge update from **FCM / APNs** (`IncrementFromPush` / `WillPresent`), not from a standing Firestore chat listener (keeps billed reads low).
- Opening Chat still loads messages over Firestore, so the Simulator can show new text **without** ever having received a push.
- Cloud Function may report `Success≥1` for the Simulator’s FCM token while the Simulator never presents a banner — APNs delivery to Simulator is intermittent (seen working during Multi-Chat debugging, then missing after Android/iOS cross-tests and Simulator restart).
- **QA push + unread on a physical iPhone** (and Android). Simulator remains fine for UI and in-Chat sync.

See `DevNotes/2026-09-27-ios-simulator-chat-push.md`.

## Related client paths

| Step | Where |
|------|--------|
| Get FCM token | `FcmService.InitializeAsync` |
| Save on member doc | `ChatService.RegisterFcmTokenAsync` → `fcmTokens[]` |
| Trigger | Cloud Function `sendChatNotification` on `teams/{teamId}/messages/{id}` create |
| Android channel | `general` (matches function + manifest) |
