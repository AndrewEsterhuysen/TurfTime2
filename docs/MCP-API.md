# TurfTimer MCP API

Goal: let a user's own AI agent (Claude, ChatGPT, Grok, Cursor and so on) read and, in later phases, act on TurfTimer team data, with **exactly the user's permission level**. The code lives in `mcp-server/`.

## Phases
| Phase | Scope | Status |
|---|---|---|
| 1 | Read-only tools, Firebase ID-token bearer, rate limit, audit-log stub | **This branch** (`feature/mcp-api`), not deployed |
| 2 | Proper OAuth 2.1 for MCP clients (authorization server that signs in via Firebase Auth), deploy to Cloud Run (`turf-timer`), persisted audit log, shared rate limit | Planned |
| 3 | Write tools (post chat message, edit kit/duties/fixture as admin) behind explicit confirmation, still enforced by firestore.rules | Planned |
| 4 | Real fixtures list or season schedule (needs an app schema change), push-style change feeds | Idea |

## Architecture
```
AI agent --MCP Streamable HTTP (POST /mcp, Bearer <Firebase ID token>)--> mcp-server (Node/TS, Cloud Run)
   mcp-server --Firestore REST, same Bearer token--> Firestore (firestore.rules enforce access)
```
- The transport is the `@modelcontextprotocol/sdk` StreamableHTTPServerTransport. It runs **stateless**, with a fresh server per request and JSON responses, so no session state is shared between users and it scales horizontally.
- `GET` and `DELETE /mcp` return 405, because no server-initiated streams are needed for read-only use. `/healthz` is the health check.

### Why REST with the user's ID token rather than the Admin SDK
- **Rules stay the single source of truth.** Every read goes through `firestore.rules`, exactly like the app's own REST calls (`ChatService`, `TeamDetailsDocService`). With the Admin SDK, rules are bypassed and we would have to re-implement and keep them in sync by hand. Any drift becomes a data leak.
- **Least privilege.** The server holds no service-account credential that can read every team. A compromised server can only see what the tokens passing through it can see.
- **The extra membership check.** Some rules only require `signedIn()`: `messages`, `sessions`, `roster/data`, `metadata/info`. So every team tool *also* reads `teams/{teamId}/members/{uid}` with the user's token and refuses if it is missing. The MCP layer therefore never widens access beyond "teams I belong to". Tightening those rules is a separate decision (see Open questions). Phase 1 does not change the rules.
- `firebase-admin` is used **only** for `verifyIdToken` (signature, audience `turf-timer`, expiry). Set `CHECK_REVOKED=1` on Cloud Run to also check revocation; this needs ADC.

## Auth flow
**Phase 1 (bearer):**
1. The user signs in to Firebase Auth for project `turf-timer`, using the same providers as the app.
2. The client sends `Authorization: Bearer <ID token>` to `/mcp`.
3. The server verifies the token, takes `uid`, and forwards the same token to Firestore REST.
4. ID tokens expire after 1 hour, so the client must refresh them (Firebase refresh token, `securetoken.googleapis.com`).
5. A missing or invalid token gets a 401 with `WWW-Authenticate`.

**Phase 2 (MCP OAuth 2.1, so agents can connect with a "Sign in with TurfTimer" button):**
1. Publish `/.well-known/oauth-protected-resource` on the MCP server, pointing to the authorization server.
2. The authorization server (Cloud Run or a Firebase Function) supports dynamic client registration and PKCE, and its `/authorize` page signs the user in with the Firebase Auth web SDK.
3. On success it issues an access token bound to the `uid`, plus a stored Firebase refresh token.
4. Each MCP request exchanges or refreshes it to a fresh Firebase ID token server-side. The Firestore reads therefore still run as the user, under the rules.
5. Scopes: `turftimer.read` (Phase 1 tools) and `turftimer.write` (Phase 3).

## Phase 1 tools and schema mapping
All tools are annotated `readOnlyHint: true`. The server only issues `GET` and `:runQuery`.

| Tool | Input | Firestore source | Notes |
|---|---|---|---|
| `list_teams` | – | collection-group `members` where `uid == me` (the same query as Select Team), then `teams/{t}/metadata/info` | Returns `teamId`, `role`, `displayName`, `teamName`, `clubId`, `clubName`, `kind`, `lastActivityUtc`. `clubs/*/members` hits are skipped. |
| `get_messages_since` | `teamId`, `since` (ISO), `limit` ≤ 100 | `teams/{t}/messages` where `timestamp > since`, ordered by `timestamp` ascending | Fields: `text`, `userId`, `senderName`, `timestamp`, `isDeleted`, `isPinned`, `replyTo*`, `reactions`. Soft-deleted messages come back with `text: null`. |
| `get_kit` | `teamId` | `teams/{t}/details/kit` | `arrive`, `warmUp`, `game`, `departure`, `nonPlaying`, `specialEvent`, `lastModifiedUtc`, `updatedByUid` |
| `get_duties` | `teamId` | `teams/{t}/details/duties` | `officer`, `canteen`, `groundsSetup`, `groundsPackup`, `other`, `lastModifiedUtc`, `updatedByUid` |
| `get_fixtures` | `teamId` | `teams/{t}/details/location` | **There is no fixtures collection.** The app stores only the *next* match (`matchDate`, `matchTime`, `arriveTime`, `locationName`, `latitude`, `longitude`, `mapsLink`). The tool returns 0 or 1 fixtures plus a note saying so. |
| `get_changes_since` | `teamId`, `since` | `lastModifiedUtc` on details/location, kit and duties; `metadata/info.lastActivityUtc`; message count since | A summary, not a diff. There is no change log in the schema. |

Not exposed in Phase 1: `roster/data`, `sessions` (game reports), `logs`, `invite_codes`, `activeControllers` and club admin data. These are candidates for later phases. Sessions are past games, not fixtures.

## Safety
- **Rate limit:** an in-memory token bucket per uid (default 60 burst, 1/s refill; env `RATE_CAPACITY` and `RATE_PER_SEC`), plus a coarse per-IP limit. It is per instance; Phase 2 moves to Redis or Firestore if more than one instance runs.
- **Audit stub:** one JSON line per tool call (`kind: mcp_audit`, with uid, tool, teamId, ok, count and ms) goes to stdout, which Cloud Logging picks up. It never contains tokens or message text.
- **Input validation:** `teamId` must not contain `/`, which prevents path traversal into other collections. `since` must be ISO-8601.
- **Errors:** 403 from Firestore maps to "Permission denied by TurfTimer rules". Internal errors are not echoed.

## Run, test, smoke
```
cd mcp-server
npm ci
npm test          # builds, then runs 11 end-to-end tests: real MCP client <-> server <-> fake Firestore REST
npm start         # PORT=8080, FIREBASE_PROJECT_ID=turf-timer
MCP_URL=http://localhost:8080/mcp ID_TOKEN=<your Firebase ID token> npm run smoke
```
- The Firestore emulator is not used yet: the dev box has no Java. To use it, run `firebase emulators:start --only firestore` and start the server with `FIRESTORE_REST_BASE=http://127.0.0.1:8080/v1`. Note that the emulator accepts unsigned tokens, so pair it with a test verifier.
- **Deploy (Phase 2; not done):**
  `gcloud run deploy turftimer-mcp --source mcp-server --project turf-timer --region <region> --allow-unauthenticated`
  `--allow-unauthenticated` is needed because auth happens in-app via the Firebase token.

## Open questions
1. **Tighten the rules?** `messages`, `sessions` and `roster/data` are readable or writable by any signed-in user. The MCP server compensates with a membership check, but the app's own exposure remains.
2. **Fixtures:** do we want a real `teams/{t}/fixtures/{id}` collection (season schedule)? Today there is only the next match.
3. **Change feed:** `get_changes_since` is coarse. Should the app write a `teams/{t}/changes` log, or a `lastModifiedUtc` on the roster?
4. **Hosting:** Cloud Run (recommended: Streamable HTTP, long requests, simple OAuth endpoints) or a second Firebase Functions codebase?
5. **Which agents first?** This affects the Phase 2 OAuth details (redirect URIs, dynamic client registration).
