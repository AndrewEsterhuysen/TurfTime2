# Testing the TurfTimer MCP server with ChatGPT, Claude, Grok and Copilot

Status: **nothing is deployed.** All steps below need the approvals at the end first.
MCP URL after deploy: `https://<cloud-run-url>/mcp` (call it `$MCP`; base `$BASE`). Scopes: `turftimer.read`, `turftimer.write` (chosen by the user on the consent page).

## 0. Before any client
1. Andrew approves and runs `mcp-server/deploy/cloudrun.sh` (see "Approvals").
2. Sanity checks (no auth needed):
   ```
   curl -s $BASE/.well-known/oauth-protected-resource
   curl -s $BASE/.well-known/oauth-authorization-server
   curl -si -X POST $MCP -H 'content-type: application/json' -d '{}' | grep -i www-authenticate
   ```
3. Build the app with `AiConnectConfig.BaseUrl = "$BASE"` (Services/AiConnectService.cs). This is a TestFlight or internal-track build only; the Settings card stays hidden in store builds until we decide.
4. On the phone: join a test team. Then **Settings → Connect AI assistant → Get code** when a client asks for it (the code is valid once, for 10 minutes).
5. Expected in every client: tools `list_teams`, `get_kit`, `get_duties`, `get_fixtures` (next match only), `get_messages_since`, `get_changes_since`, `get_roster`, `get_lineup` (read), plus `post_chat_message`, `update_kit`, `update_duties`, `update_next_fixture`, `update_roster`, `create_club`, `create_team` (need *Allow read and write*). Try: "What do I need to bring on Saturday, and who's on canteen?" For a club manager: "Create a club called Cape Crusaders, then a U13 and a U15 team from this spreadsheet, and add the players on each row."
6. Negative test: ask about a team you have not joined (the result is "Not a member"). Disconnect from the app; within about 30 s the client gets 401 and must reconnect.

## 1. ChatGPT (developer mode)
- **Plan:** developer mode (full MCP connectors) is on web for Plus, Pro, Business and Enterprise/Edu (Business/Enterprise: a workspace admin enables it and may need to publish the connector). Verify in-product, because OpenAI changes this often.
- **Steps:**
  1. Go to chatgpt.com → Settings → Apps & connectors → Advanced settings → Developer mode on.
  2. Choose Create (Add custom connector): name "TurfTimer", URL `$MCP`, auth OAuth.
  3. ChatGPT discovers the server, registers through DCR with redirect `https://chatgpt.com/connector/oauth/<id>` (allowed), and opens our authorize page. Enter the app code, then Allow.
  4. In a chat, pick the connector from the + / Developer mode menu.
- **Notes:**
  - ChatGPT prefers CIMD (Client ID Metadata Documents), which we don't implement; it falls back to DCR.
  - If it shows a custom client ID/secret form instead, register manually (see §6) and paste the values.

## 2. Claude (custom connectors)
- **Plan:** custom connectors work on Free (limited to 1), Pro, Max, Team and Enterprise. On Team and Enterprise an Owner adds the connector under Organization settings → Connectors.
- **Steps:**
  1. Go to claude.ai → Settings → Connectors → Add custom connector. URL `$MCP`. Leave "Advanced" (client ID/secret) empty so DCR is used.
  2. Click Connect. The redirect is `https://claude.ai/api/mcp/auth_callback` (allowed). Enter the app code, then Allow.
  3. In a chat, enable TurfTimer in the tools menu. The connector also syncs to Claude Desktop and mobile.
- **Claude Code (CLI):** `claude mcp add --transport http turftimer $MCP`, then `/mcp` → Authenticate. It uses a loopback redirect on any port (allowed).

## 3. Grok
- **Plan:** custom connectors at grok.com/connectors. On Business and Enterprise, an admin adds the connector for the team. Consumer availability depends on the plan (SuperGrok); verify in-product.
- **Steps:**
  1. Go to grok.com/connectors → New Connector → Custom. URL `$MCP`, auth OAuth.
  2. If Grok asks for a client ID/secret, register first (§6) with redirect `https://grok.com/connectors-oauth-exchange-code/`.
  3. Connect, enter the app code, then Allow.
- **Notes:**
  - Grok requires PKCE (we require S256) and rejects localhost, so it can only be tested against the deployed https URL.

## 4. Microsoft Copilot
**4a. Copilot Studio (agents for M365 Copilot / Teams)**
- **Plan:** needs a Copilot Studio licence or trial and a Power Platform environment. Publishing to M365 Copilot needs tenant admin approval.
- **Steps:**
  1. Open the agent → Tools → Add a tool → New tool → Model Context Protocol. Server URL `$MCP`, auth OAuth 2.0 → **Dynamic discovery**.
  2. Copilot Studio registers through DCR and **requires a client secret**. We issue one by default (`client_secret_post`). The redirect is `https://global.consent.azure-apim.net/redirect/<connector-id>` (allowed).
  3. Create the connection → sign in → enter the app code, then Allow. Test in the Test pane.
- **Fallback:** choose "Manual" OAuth instead and paste the client ID and secret from §6 (use the redirect URL Copilot Studio shows you).

**4b. GitHub Copilot in VS Code (agent mode)**
- **Plan:** any Copilot plan, including Free. On Copilot Business/Enterprise, the org policy "MCP servers in Copilot" must be enabled.
- **Steps:**
  1. Add `.vscode/mcp.json` (do NOT commit it to the app repo):
     ```json
     { "servers": { "turftimer": { "type": "http", "url": "https://<cloud-run-url>/mcp" } } }
     ```
  2. Start the server → VS Code runs OAuth through DCR with redirect `http://127.0.0.1:33418/` or `https://vscode.dev/redirect` (both allowed) → enter the app code.
  3. In Copilot Chat agent mode, enable the TurfTimer tools.
  4. Manage or sign out: Accounts menu → Manage Trusted MCP servers.

## 5. Local testing (before deploy, no production data)
- Run `cd mcp-server && cp deploy/env.example deploy/env.local`, fill it in with `MCP_STORE=memory`, then `npm run build && npm start`.
- MCP Inspector: `npx @modelcontextprotocol/inspector` → Streamable HTTP `http://127.0.0.1:8080/mcp` → OAuth flow (loopback redirect allowed).
- Local runs still read the **real** `turf-timer` Firestore as the linked user, and need ADC that can sign custom tokens. Use a test team.
- Getting a link code locally: the app build must point at the local URL (not reachable from a phone), so in practice the code is easier to create on the deployed service. The automated test `npm test` covers the whole flow against fake Firestore.

## 6. Manual client registration (when a client wants a client ID/secret)
```
curl -s -X POST $BASE/oauth/register -H 'content-type: application/json' \
  -d '{"client_name":"Grok","redirect_uris":["https://grok.com/connectors-oauth-exchange-code/"],"token_endpoint_auth_method":"client_secret_post"}'
```
Store the returned `client_secret` in the client only; it is shown once and stored here only as a hash.

## 7. Known limits
- No CIMD; DCR only. Redirects must be on the allowlist (`src/oauth/redirects.ts`, or `MCP_EXTRA_REDIRECTS`).
- Write tools need a connection approved with *Allow read and write*; older connections are read-only until reconnected. `get_fixtures` returns the next match only (no season list yet). `create_club` / `create_team` share a 20-per-day cap; save the Owner Recovery Codes they return (they are not shown again, and no reminder email is sent).
- Rate limits and revocation cache are per instance (max 2 instances); revocation applies within 30 s.
- Without deploying the tightened rules, the server still refuses non-members (tool-level `requireMember`). The app's own exposure (any signed-in user can read chat, sessions and roster) remains until the rules are deployed.

## Approvals needed from Andrew (production actions)

Done: items 1-5 (rules 2026-11-10; Cloud Run service, secret, IAM, TTL policies 2026-11-11). Live URL: `https://turftimer-mcp-co6qd7jw6q-ts.a.run.app` (see MCP-API.md, Deployment). Remaining: 6-8.

1. **Deploy the Cloud Run service** `turftimer-mcp` in `turf-timer` (region `australia-southeast1`, public ingress, min 0 / max 2 instances). Command: `CONFIRM=deploy ./deploy/cloudrun.sh`.
2. **Create the secret** `mcp-jwt-secret` in Secret Manager (the script generates it; it is never printed).
3. **IAM:** create SA `turftimer-mcp@turf-timer.iam.gserviceaccount.com` with `roles/datastore.user`, Token Creator on itself (custom tokens), and Secret Accessor on the secret. Enable the Run, Secret Manager, IAM Credentials, Cloud Build and Artifact Registry APIs.
4. **Firestore TTL policies** on field `ttl` for `mcp_auth_requests`, `mcp_auth_codes`, `mcp_link_codes`, `mcp_grants` (in the script).
5. **Deploy the firestore.rules** from this branch (`firebase deploy --only firestore:rules`), after running `scripts/audit-membership.mjs` (read-only) to check no active user lacks a member doc. **Deploy the rules before or together with an app release.** They are compatible with the current 3.4.2 build (traced), so no app update is required for them.
6. **OAuth consent / branding:** there is no Google OAuth consent screen, because we are our own authorization server. What Andrew should OK is the consent page wording, the app name shown, and any custom domain (e.g. `mcp.turftimer.app`) with its DNS and Cloud Run domain mapping. A Firebase authorized domain is NOT needed (no Firebase web sign-in on the page).
7. **App build** with `AiConnectConfig.BaseUrl` set, to TestFlight and the Play internal track (and later the store). Not App Store review.
8. Optional: in the Claude Team/Enterprise or Copilot tenant, admin-level connector install.
