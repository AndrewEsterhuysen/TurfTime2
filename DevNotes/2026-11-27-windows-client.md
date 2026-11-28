# Windows setup client (feature/windows-spike)

Setup-only Windows app for club back-office staff: clubs, teams, club players, kit and duties.
Game play, chat, nominations, reports, push, QR scan, local (device-only) teams stay phone-only.
iOS and Android code paths are unchanged (every change is behind `DesktopMode.IsWindows` or `#if WINDOWS`).

## How it works
- `Platforms/Windows/Firebase/` implements `IFirebaseAuthService` and Plugin.Firebase's `IFirebaseFirestore`
  (and the document/collection/query/batch interfaces) over the Identity Toolkit, securetoken and Firestore REST APIs,
  using the signed-in user's ID token, so the Firestore rules apply exactly as on the phone. No admin credentials.
  Every existing service (CloudTeamService, ClubAdminService, ClubPlayerService, ...) runs unchanged on top.
- Registered only on Windows in `MauiProgram.cs`.
- Auth: anonymous sign-up on first use, refresh token afterwards. uid + refresh token are stored DPAPI-encrypted in
  `%LOCALAPPDATA%\User Name\com.andrewestherhuysen.turftime\Data\firebase-auth.bin`. A new anonymous user is only created
  when nothing is stored or the server definitively rejects the stored identity (never on a network blip).
- The API key is the iOS key from `GoogleService-Info.plist`; it works over REST when the request carries
  `X-Ios-Bundle-Identifier: com.andrewestherhuysen.turftime` (the key is restricted to the iOS bundle id).
- Realtime listeners are emulated by 8 s polling. Transactions are not supported (NominationService only, phone-only).
- Errors: failed Firestore calls and unhandled exceptions are appended to `%LOCALAPPDATA%\TurfTimer\windows-errors.log`.

## Identity
A Windows install is a new anonymous user. Ways in: redeem a club admin invite (Settings > Club admin), join a team with
its invite code, create a club/team, or Recover Owner Access with a Team Owner or Club Owner recovery code.
There is no cross-device account linking.

## Known gaps
- Tabs, windows and layout are phone layouts; nothing was redesigned for desktop.
- Local teams, QR import/scan, Game, Chat, Nominations, Reports, Timers, Options are hidden or show "Not available on Windows".
- Club Owner recovery: fixed on feature/club-admin (28 Nov): recovery no longer tries to take a team Admin seat. See 2026-11-28-club-recovery-and-player-edit.md.
- Distribution (unsigned dev build only): see 2026-11-27-windows-spike.md.
