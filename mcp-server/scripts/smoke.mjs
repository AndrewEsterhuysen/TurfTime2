// Local smoke test against a running server (npm run build && npm start) and REAL Firestore,
// using YOUR Firebase ID token. Read-only; nothing is written.
//   MCP_URL=http://localhost:8080/mcp ID_TOKEN=<firebase id token> [TEAM_ID=...] npm run smoke
// Get an ID token: sign in with the Firebase Auth REST API (email/password or Apple/Google via
// signInWithIdp) for project turf-timer, or copy one from a debug build.
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StreamableHTTPClientTransport } from "@modelcontextprotocol/sdk/client/streamableHttp.js";

const url = new URL(process.env.MCP_URL ?? "http://localhost:8080/mcp");
const token = process.env.ID_TOKEN;
if (!token) { console.error("Set ID_TOKEN"); process.exit(2); }
const c = new Client({ name: "turftimer-smoke", version: "1" });
await c.connect(new StreamableHTTPClientTransport(url, { requestInit: { headers: { Authorization: `Bearer ${token}` } } }));
console.log("tools:", (await c.listTools()).tools.map(t => t.name).join(", "));
const show = async (name, args = {}) => {
  const r = await c.callTool({ name, arguments: args });
  console.log(`\n== ${name} ${JSON.stringify(args)}${r.isError ? " (error)" : ""}\n${r.content[0].text}`);
  return r.isError ? null : JSON.parse(r.content[0].text);
};
const teams = await show("list_teams");
const teamId = process.env.TEAM_ID ?? teams?.teams?.[0]?.teamId;
if (teamId) {
  const since = new Date(Date.now() - 7 * 864e5).toISOString();
  for (const t of ["get_kit", "get_duties", "get_fixtures"]) await show(t, { teamId });
  await show("get_messages_since", { teamId, since, limit: 5 });
  await show("get_changes_since", { teamId, since });
}
await c.close();
