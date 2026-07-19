# Firebase SDK migration (Option B)

**Branch:** `feature/firebase-sdk`  
**Goal:** One uniform .NET MAUI Firebase stack — **Plugin.Firebase only** for Auth, Firestore, FCM, and client Cloud Functions. **No Firestore/Identity Toolkit REST** and **no Chat WebView Firebase JS**.

## Product rules

- Still **one freemium app**; cloud remains optional infrastructure.
- **No Plugin.Firebase types in Pages/ViewModels** long-term — use `IFirebaseAuthService`, `IChatService`, `ICloudTeamService`, etc.
- Local-only teams must keep working with zero cloud success.

## Architecture

| Layer | Responsibility |
|-------|----------------|
| `IFirebaseAuthService` | Durable anonymous uid via `Plugin.Firebase.Auth` |
| `IFirebaseFirestore` (plugin) | Document/query/listener APIs |
| `ICloudRosterService` | Roster local + Firestore |
| `ISessionStorageService` | Sessions local + Firestore |
| `IChatService` | Messages + member profile + FCM token field |
| `ICloudTeamService` | Team create/join/metadata/invite codes |
| `FcmService` | Native FCM + write `fcmTokens` via Firestore SDK |
| Cloud Functions (Node) | Server triggers (`sendChatNotification`) — **unchanged host language** |

## Migration phases

1. **Foundation** — enable Firestore in `CrossFirebaseSettings`; auth service; DI. ✅  
2. **Data services** — roster, sessions, FCM token writes off REST. ✅  
3. **Chat** — native UI + `IChatService` listeners; delete WebView JS Firebase. ✅  
4. **Teams** — extract `TeamDetailsPage` REST into `ICloudTeamService`. ✅  
5. **Cleanup** — bridges now delegate to SDK services (no Identity Toolkit / Firestore REST). ✅  
6. **Verify** — Android + iOS Debug device deploy (build 2.0.0/20). ✅  

### Remaining polish (optional)

- Delete unused WebView Firebase JS assets under `wwwroot/js/firebase/` if nothing else references them.  
- Exercise create/join/chat/push E2E on devices; upload APNs key for iOS push delivery.  
- Admin email: SDK Functions callable with HTTPS callable fallback (not Firestore REST).

## Out of scope

- Moving Cloud Functions from Node to C#  
- Dual store apps / product forks  
- Monetization paywall wiring (still free-all entitlements)
