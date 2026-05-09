# Dev Session Notes — Admin Recovery Code & Firebase CLI Setup
**Date:** 2026-05-06  
**Branch:** master

---

## ✅ Features Implemented

### 1. Admin Recovery Code
When a team creator creates a new **shared team**, an **Admin Recovery Code** is now generated (format: `XXXX-XXXX-XXXX-XXXX`).

- **Plain-text code** is shown **once** in a popup at team creation — never stored anywhere.
- A **SHA-256 hash** of the code is stored in Firestore under `teams/{teamId}/metadata/info.adminCodeHash`.
- Creator is warned to save the code in a secure location (password manager, etc.).

#### Files changed:
| File | Change |
|---|---|
| `TurfTime2/TeamDetailsPage.xaml.cs` | Added `GenerateAdminCode()`, `HashAdminCode()`, updated `CreateTeamInFirestore()` to store hash, updated `OnCreateTeamClicked()` to show code once, added `RejoinAsAdminInFirestore()`, added `OnRejoinAsAdminClicked()` |
| `TurfTime2/TeamDetailsPage.xaml` | Added "🔑 Recover Admin Access" UI section (orange bordered frame, visible when Shared checkbox is ticked) |
| `functions/index.js` | Added `requestAdminCodeEmail` callable Cloud Function (stub — ready for email extension wiring) |

#### How the rejoin flow works:
1. User ticks **Shared** checkbox on Team Details page
2. "🔑 Recover Admin Access" section appears
3. User enters their **Team ID** + **Admin Recovery Code**
4. App hashes the supplied code and compares against Firestore hash
5. If matched → member document upserted with `role: admin`, all Preferences restored
6. User regains full admin access immediately

---

## 🔧 Firebase CLI — Status & Known Issue

### Problem
`firebase login` fails with:
```
Error: Failed to make request to https://auth.firebase.tools/attest
```
This is caused by `https://auth.firebase.tools/attest` being **blocked** by Windows Defender or a firewall on this machine. Affects all CLI versions including v12.

### Workaround — Service Account Key (TODO)
1. Go to: https://console.firebase.google.com/project/turf-timer/settings/serviceaccounts/adminsdk
2. Click **"Generate new private key"** → download `.json`
3. Save to: `C:\Users\esterha\firebase-service-account.json`
4. Set env variable permanently:
```powershell
[System.Environment]::SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS", "C:\Users\esterha\firebase-service-account.json", "User")
```
5. Deploy:
```powershell
$env:PATH = [System.Environment]::GetEnvironmentVariable("PATH","Machine") + ";" + [System.Environment]::GetEnvironmentVariable("PATH","User")
$env:GOOGLE_APPLICATION_CREDENTIALS = "C:\Users\esterha\firebase-service-account.json"
cd C:\Users\esterha\source\repos\TurfTimer-Rebuild\TurfTime2\functions
firebase deploy --only functions
```

> ⚠️ Never commit the `.json` service account key to Git. Add to `.gitignore`.

### Firebase CLI Path (already done)
`C:\Users\esterha\AppData\Roaming\npm` has been permanently added to the **user PATH**.  
In a new terminal session, refresh with:
```powershell
$env:PATH = [System.Environment]::GetEnvironmentVariable("PATH","Machine") + ";" + [System.Environment]::GetEnvironmentVariable("PATH","User")
```

---

## 📋 Pending / Next Session

- [ ] Resolve `firebase login` attestation block (use Service Account key above)
- [ ] Deploy `functions/index.js` via CLI to publish `requestAdminCodeEmail`
- [ ] (Optional future) Add `creatorEmail` field to team metadata at creation time
- [ ] Install Firebase "Trigger Email from Firestore" extension in Firebase Console (project: turf-timer) — `index.js` already writes to `mail` collection, extension just needs to be configured with an SMTP provider (SendGrid/Mailgun/Gmail) pointed at the `mail` collection
- [ ] Test Admin Recovery Code rejoin flow on physical device

---

## 📁 Key File Locations
| File | Purpose |
|---|---|
| `TurfTime2/TeamDetailsPage.xaml.cs` | Team create/join/admin-rejoin logic |
| `TurfTime2/TeamDetailsPage.xaml` | Team Details UI |
| `functions/index.js` | Firebase Cloud Functions (chat notifications + admin code email stub) |
| `Platforms/Android/AndroidManifest.xml` | Firebase/FCM config |
| `wwwroot/js/roster-manager.js` | Roster sync + debounced cloud save |
| `TurfTime2/GamePageSaveBridge.cs` | C# roster-to-Firestore save bridge |
