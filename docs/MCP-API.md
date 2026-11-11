# TurfTimer MCP API

Goal: let a user's own AI assistant (ChatGPT, Claude, Grok, Copilot, ...) read TurfTimer team data, with **exactly the user's permissions**. Code: `mcp-server/`. Testing guide: [MCP-TESTING.md](MCP-TESTING.md).

## Phases
| Phase | Scope | Status |
|---|---|---|
| 1 | Read-only tools, rate limit, audit-log stub | Done on `feature/mcp-api` |
| 2 | MCP OAuth 2.1 (DCR, PKCE, link-code sign-in, refresh grants, revocation), tightened rules, Cloud Run script, app "Connect AI assistant" page | Done on `feature/mcp-api`; rules live 2026-11-10; server live 2026-11-11 (see Deployment) |
| 3 | Write tools (chat, kit, duties, next match, roster/lineup), scope `turftimer.write` chosen by the user on the consent page | Done on `feature/mcp-api` (see Write tools) |
| 4 | Season fixtures imported from a third-party site; change feed | Later (Andrew: no fixtures list yet) |

## Deployment (live)
- **Service URL:** `https://turftimer-mcp-co6qd7jw6q-ts.a.run.app` (this is `PUBLIC_BASE_URL`/OAuth issuer; use it, not the alias `https://turftimer-mcp-846410659178.australia-southeast1.run.app`, because tokens are bound to the issuer).
- **MCP endpoint:** `https://turftimer-mcp-co6qd7jw6q-ts.a.run.app/mcp`. Discovery: `/.well-known/oauth-authorization-server`, `/.well-known/oauth-protected-resource`. Health: `/health`.
- Cloud Run `turftimer-mcp`, project `turf-timer`, `australia-southeast1`. Min 0 / max 2 instances (scales to zero when idle), CPU only during requests, 1 vCPU, 512Mi, default concurrency (80). Public at the Run level; auth is the server's own OAuth.
- Runtime SA `turftimer-mcp@turf-timer.iam.gserviceaccount.com` (`roles/datastore.user`, Token Creator on itself, Secret Accessor on `mcp-jwt-secret`). Secret `MCP_JWT_SECRET` from Secret Manager `mcp-jwt-secret:latest`.
- Artifact Registry `cloud-run-source-deploy` cleanup policy: keep latest 2 images, delete older than 1 day.
- Budget: AUD 5/month on project `turf-timer` (alerts at 50/90/100% to billing admins), alongside the existing AUD 25 Firebase budget.
- Redeploy: `CONFIRM=deploy FIREBASE_WEB_API_KEY=<web app apiKey> ./deploy/cloudrun.sh` from `mcp-server/`.

## Architecture
```
AI client --(1) discovers /.well-known/oauth-protected-resource, registers (DCR), OAuth code+PKCE-->
   mcp-server /oauth/* (authorization server, same Cloud Run service)
AI client --POST /mcp, Bearer <our access JWT, 15 min>--> mcp-server
   mcp-server: verify JWT -> grant still active? -> uid -> Firebase ID token for that uid (custom token exchange, cached 1h)
   mcp-server --Firestore REST, Bearer <that ID token>--> Firestore (firestore.rules enforce access)
```
Stateless Streamable HTTP (`enableJsonResponse`), fresh MCP server per request; `GET`/`DELETE /mcp` -> 405; `/health` (Cloud Run reserves `/healthz` at its front end; `/healthz` still works locally).

## Who is the user? (impersonation)
TurfTimer users sign in **anonymously** (`FirebaseAuthService.SignInAnonymouslyAsync`). There is no email, Google or Apple account, so a web page cannot "sign in as the app user", and an agent must never be trusted to say which user it is.

Solution: **the app is the identity provider.**
1. In the app, Settings -> **Connect AI assistant** -> *Get code*. The app calls `POST /link/code` with its own Firebase ID token; the server verifies it and stores a one-time code (8 chars, 10 min, hashed, single use) mapped to that uid.
2. The AI client opens our `/oauth/authorize` page. It shows the client's name, the host it returns to and what read-only access means; the user types the code and taps *Allow*.
3. The server maps code -> uid and issues an authorization code to the client's registered redirect URI.

The uid therefore comes only from a verified app session on the user's phone. No tool takes a user name or uid (tested), 5 wrong codes kill the request, codes expire in 10 min, and the user sees which assistant they are approving. A person without the app (or without having joined a team) gets nothing: there is no code to type and no team membership.

Later option: when the app adds account linking (Sign in with Apple/Google via `linkWithCredential`, which keeps the same uid), the authorize page can offer those providers too.

## Design choice: how the server reads data
Options considered:
| Option | Verdict |
|---|---|
| A. Admin SDK reads + explicit membership checks in one module | Rejected: bypasses rules, so rules must be re-implemented and kept in sync by hand; any drift is a leak; the server SA could read every team. |
| B. Keep the user's Firebase refresh token server-side | Impossible/unsafe here: the app is anonymous, so the only refresh token is the app's own session; exporting it would hand the whole app session to the server and the anonymous account cannot be re-created if lost. |
| **C. (chosen) Mint a Firebase ID token for the authorized uid** | Admin `createCustomToken(uid)` -> Identity Toolkit `signInWithCustomToken` -> 1-hour ID token, cached per uid; Firestore REST is then called **as the user**, so `firestore.rules` remain the single source of truth and "their permissions apply to MCP requests" literally. |

Notes on C: the server's SA needs only `roles/datastore.user` (for its own `mcp_*` collections via Admin) and `roles/iam.serviceAccountTokenCreator` on itself (to sign custom tokens). The custom-token sign-in does not touch the app's anonymous session. Tools additionally check `teams/{t}/members/{uid}` (`requireMember`) as defence in depth. `idtoken.ts` is the only place that mints tokens.

## OAuth endpoints (MCP authorization spec 2025-06-18 / 2025-11-25)
| Endpoint | Spec |
|---|---|
| `GET /.well-known/oauth-protected-resource[/mcp]` | RFC 9728; `resource=<base>/mcp`, `authorization_servers=[<base>]` |
| `GET /.well-known/oauth-authorization-server` (+ `openid-configuration` alias) | RFC 8414 |
| `POST /oauth/register` | RFC 7591 DCR. Confidential by default (`client_secret_post`/`basic`; Copilot Studio requires a secret); `token_endpoint_auth_method: "none"` for public clients |
| `GET/POST /oauth/authorize` | Code flow, **PKCE S256 required**, `state`, `iss` in response (RFC 9207), `resource` checked (RFC 8707). Unknown client or redirect -> error page, never a redirect |
| `POST /oauth/token` | `authorization_code`, `refresh_token`. Access token: HS256 JWT, 15 min, `aud=<base>/mcp`, `sub=uid`, `scope`, `gid`. Refresh: opaque `gid.secret`, 30-day sliding, **rotated on every use; reuse of an old one revokes the grant** |
| `POST /oauth/revoke` | RFC 7009; revokes the whole grant; MCP access stops within 30 s |
| `POST /link/code`, `GET /link/grants`, `DELETE /link/grants/{id}` | App-only (Firebase ID token). Used by the app's Connect AI assistant page |

Scopes: `turftimer.read` and `turftimer.write` (write implies read; anything else -> `invalid_scope`). 401s carry `WWW-Authenticate: Bearer resource_metadata="<base>/.well-known/oauth-protected-resource", scope="turftimer.read turftimer.write"`.
- **The user decides.** If the client asks for write (or sends no scope), the consent page explains read and write and offers **Allow read-only** (default-looking) or **Allow read and write**. If the client asks only for `turftimer.read`, the page offers read-only and a tampered `allow_write` still yields read-only.
- **Grants never widen.** The granted scope is stored on the grant; refresh can narrow (`scope=turftimer.read`) but asking for more than the grant -> `invalid_scope`. Grants made before write existed are `turftimer.read` and stay read-only. `/mcp` also re-checks that the stored grant carries write before honouring a write token.
- Write tools are listed for every connection; on a read-only connection each one returns an error telling the user to reconnect and choose *Allow read and write*.

**Redirect URIs accepted at registration** (`src/oauth/redirects.ts`): `https://chatgpt.com/connector/oauth/<id>`, `https://chatgpt.com/connector_platform_oauth_redirect`, `https://claude.ai|claude.com/api/mcp/auth_callback`, `https://grok.com/connectors-oauth-exchange-code/`, `https://www.cursor.com/agents/mcp/oauth/callback` (Grok Bot, exact), `https://global.consent.azure-apim.net/redirect[/<id>]`, `https://vscode.dev/redirect`, and loopback `http://127.0.0.1|localhost|[::1]:<any port>/...` (port-agnostic match per RFC 8252). Add more with `MCP_EXTRA_REDIRECTS`. Not implemented: Client ID Metadata Documents (CIMD); all four clients fall back to DCR.

**Storage** (server-only Firestore collections, rules `if false`, TTL field `ttl`): `mcp_clients`, `mcp_auth_requests`, `mcp_auth_codes`, `mcp_link_codes`, `mcp_grants`. Secrets, codes and refresh tokens are stored only as SHA-256 hashes.

**App TODOs:** the Connect AI assistant page (code + connected assistants + Disconnect) is in the app but **hidden until `AiConnectConfig.BaseUrl` is set** (Services/AiConnectService.cs). A fuller Settings "revoke" screen polish is a TODO; the server endpoints exist.

## Firestore rules changes (branch only; not deployed)
| Path | Before | After |
|---|---|---|
| `teams/{t}/messages` read | any signed-in | member **or team owner** |
| `teams/{t}/messages` create / reaction update | any signed-in | member |
| `teams/{t}/sessions` read / create, update | any signed-in | member or owner / member |
| `teams/{t}/roster/data` read, write | any signed-in | member or owner |
| `teams/{t}/logs` | member | member or owner |
| `activeControllers/{t}` read | any signed-in | member |
| `mcp_*` | (default deny) | explicit `if false` |
| Unchanged (needed before joining): `teams/{t}/metadata/info`, `teams/{t}/public/*`, `invite_codes`, `clubs/*/metadata/info` | any signed-in | any signed-in |

Why "or owner": `DeleteTeamAsOwnerAsync` deletes `members/*` first and then lists messages, sessions and logs to delete them; the owner is no longer a member at that point (this already failed for `logs` before).

App-impact trace: join by invite reads `invite_codes`, `public`, `metadata` before the member doc exists (kept open); Select Team uses the collection-group `members` query (`uid == me`, unchanged); create team writes `metadata` -> `members/{uid}` -> `roster/data` (member exists before roster); owner recovery writes `members/{uid}` before anything else; chat/sessions/roster/controller writes all run after joining. The collection-group `public` and `metadata` queries in CloudTeamService are **already denied today** (no `{path=**}` rule) and the app falls back to `invite_codes`; unchanged.

Residual risk: a user on an old build whose member doc is missing would lose chat/roster access. Run `mcp-server/scripts/audit-membership.mjs` (read-only) before deploying the rules. Tests: `firestore-tests/` (emulator, 10 tests).

Still open (not changed): `metadata/info` contains `inviteCode`, `adminCodeHash` and `creatorEmail` and is readable by any signed-in user; the legacy root `/messages` and `rosters/` paths.

## Tools and schema mapping
All tools are annotated `readOnlyHint: true`. The server only issues `GET` and `:runQuery`.

| Tool | Input | Firestore source | Notes |
|---|---|---|---|
| `list_teams` | – | collection-group `members` where `uid == me` (the same query as Select Team), then `teams/{t}/metadata/info` | Returns `teamId`, `role`, `displayName`, `teamName`, `clubId`, `clubName`, `kind`, `lastActivityUtc`. `clubs/*/members` hits are skipped. |
| `get_messages_since` | `teamId`, `since` (ISO), `limit` ≤ 100 | `teams/{t}/messages` where `timestamp > since`, ordered by `timestamp` ascending | Fields: `text`, `userId`, `senderName`, `timestamp`, `isDeleted`, `isPinned`, `replyTo*`, `reactions`. Soft-deleted messages come back with `text: null`. |
| `get_kit` | `teamId` | `teams/{t}/details/kit` | `arrive`, `warmUp`, `game`, `departure`, `nonPlaying`, `specialEvent`, `lastModifiedUtc`, `updatedByUid` |
| `get_duties` | `teamId` | `teams/{t}/details/duties` | `officer`, `canteen`, `groundsSetup`, `groundsPackup`, `other`, `lastModifiedUtc`, `updatedByUid` |
| `get_fixtures` | `teamId` | `teams/{t}/details/location` | **There is no fixtures collection.** The app stores only the *next* match (`matchDate`, `matchTime`, `arriveTime`, `locationName`, `latitude`, `longitude`, `mapsLink`). The tool returns 0 or 1 fixtures plus a note saying so. |
| `get_changes_since` | `teamId`, `since` | `lastModifiedUtc` on details/location, kit and duties; `metadata/info.lastActivityUtc`; message count since | A summary, not a diff. There is no change log in the schema. |
| `get_roster` | `teamId`, `includeUnnamed?` | `teams/{t}/roster/data` → `players[]` | Player `name` + `status` (goalie/field/bench/inactive) only. Active players (field/goalie/bench) are always listed, even with the app default name "#NN Player"; only inactive default-named slots are hidden (counted in `unusedSlots`) unless `includeUnnamed`. Online (shared) teams only; Local teams are never in Firestore. |
| `get_lineup` | `teamId` | `teams/{t}/roster/data` (written by the controlling admin, watched by members) | `onField` (name, status, `cell` 1-16 row-major on the 4x4 Field View grid with `row`/`col`, `secondsOnTimer`), `bench` (all bench players, whatever their name; `defaultName: true` marks "#NN Player"), named `inactive` players, `unusedSlots` (count of inactive default-named slots), and `game` (`phase` from `currentHalf`, `timerRunning`, `matchRemainingSeconds`, score us/them, `rotationCount`, `controlledBy` display name, `lastModifiedUtc`). No uids. |

Not exposed: `sessions` (game reports), `logs`, `invite_codes`, `activeControllers` and club admin data. These are candidates for later phases. Sessions are past games, not fixtures.

## Write tools (scope `turftimer.write`)
All writes go to Firestore **as the signed-in user** (their Firebase ID token over REST; single-write `:commit` with an `updateMask` and a `currentDocument` precondition), so the live rules still decide. No Admin SDK / service-account write ever touches team data. The server additionally enforces the app's roles (rules alone let any member write `roster/data`).

| Tool | Who | Args | Firestore write (app shape) |
|---|---|---|---|
| `post_chat_message` | any member | `teamId`, `text` (≤500 after trim), `replyToMessageId?` | POST `teams/{t}/messages` (auto id): `text`, `userId` = token uid, `senderName` = `members/{uid}.displayName` (≤40, "Someone" if blank), `timestamp` (timestampValue), `sentVia: "mcp:<client name>"`; reply adds `replyToMessageId`, `replyToText` (≤120) and `replyToSenderName` (≤40) copied from the real message. Creates trigger the usual `sendChatNotification` push. |
| `update_kit` | admin | `teamId`, any of `arrive`, `warmUp`, `game`, `departure`, `nonPlaying`, `specialEvent` (≤1000; `""` clears) | `teams/{t}/details/kit`: given fields + `lastModifiedUtc` (timestamp), `updatedByUid`, `editedVia`, `editedViaAtUtc` |
| `update_duties` | admin | `teamId`, any of `officer`, `canteen`, `groundsSetup`, `groundsPackup`, `other` | `teams/{t}/details/duties`: same pattern |
| `update_next_fixture` | admin | `teamId`, any of `matchDate` (YYYY-MM-DD), `matchTime`/`arriveTime` (HH:mm -> stored `HH:mm:ss` like .NET `TimeSpan`), `locationName` (≤120), `latitude`/`longitude` (decimal strings), `mapsLink` (https) | `teams/{t}/details/location`: given fields + `schemaVersion: 1`, `lastModifiedUtc`, `updatedByUid`, `updatedByDisplayName`, `editedVia`, `editedViaAtUtc` (a new doc gets every field, empty strings) |
| `update_roster` | admin | `teamId`, `changes[1..16]` of `{ player (name or slot number / "#4"), newName? (≤40), status? field/goalie/bench/inactive, cell? 1-16 }` | `teams/{t}/roster/data`: whole `players[]` (order and other per-player fields kept), `lastModifiedUtc`, `lastModified`, `editedVia`, `editedViaAtUtc`, and `nextFieldSlotIds`/`nextBenchSlotIds` only when a moved player was queued (set to 0). Never touches timers, scores or controller fields. Also bumps `metadata/info.lastActivityUtc` like the app. |

- **Lineup rules = the app's** (`GameViewModel.SetPlayerPosition` / `PlaceOrSwapOnFieldCell`): one goalie (the old goalie goes to the bench); leaving Field clears the cell; field->occupied cell swaps cells; bench/goalie->occupied cell sends the occupant to the bench; `field` without a cell keeps the current cell or takes the first free one.
- **Game guard:** `update_roster` is refused while `controllerUid` is set (an admin's phone is running the game and saves every ~2 s) or `timerRunning` is true. The app's free co-admins and members apply the newer snapshot (newer `lastModifiedUtc`).
- **Lost updates:** every write carries the `updateTime` it read (or `exists:false` for a new doc); if the app saved in between, Firestore rejects it and the tool says "changed at the same moment ... retry".
- **Provenance:** `editedVia` stays after a later app save (the app's updateMask doesn't touch it), so "last edit was by an assistant" = `editedViaAtUtc == lastModifiedUtc`.
- **Limits:** per uid per instance: writes 20 burst / 1 per 5 s; chat 5 burst / 1 per 30 s (`WRITE_RATE_*`, `CHAT_RATE_*`). Audit lines carry `write: true`, never message text.
- **App caveats:** chat shows assistant posts as normal messages from the user (no badge yet; the app ignores `sentVia`). Members' phones that are already showing a live mirror don't redraw for a cell-only move (the app's `RosterLayoutChanged` ignores `fieldCell`) until the next real change or reopening the Game screen.

## Safety
- **Rate limits:** per uid on `/mcp` (60 burst, 1/s), per IP on `/mcp` and on OAuth endpoints (30 burst), per uid on `/link/code`. In-memory per instance (max 2 instances).
- **Audit stub:** one JSON line per tool call (`kind: mcp_audit`, uid, tool, teamId, ok, count, ms); never tokens or message text.
- **Input validation:** `teamId` cannot contain `/`; `since` must be ISO-8601.
- **Authorize page:** `X-Frame-Options: DENY`, CSP `frame-ancestors 'none'`, `no-store`, HTML-escaped client names.
- Raw Firebase ID tokens are rejected on `/mcp` unless `ALLOW_FIREBASE_ID_TOKENS=1` (local dev only).

## Run and test
```
cd mcp-server && npm ci && npm test       # 48 tests: read + write tools, scopes, full OAuth flow against fake Firestore
npm run test:emulator                     # 5 write tests on the Firestore emulator with the real firestore.rules (Java 21+, firestore-tests deps installed)
cd firestore-tests && npm ci && npm test  # 28 rules + app-flow tests on the Firestore emulator (Java 21+)
cp deploy/env.example deploy/env.local    # fill in, then: set -a; . deploy/env.local; set +a; npm run build && npm start
```
Deploy: `CONFIRM=deploy FIREBASE_WEB_API_KEY=... ./deploy/cloudrun.sh` (needs approval; see MCP-TESTING.md).
