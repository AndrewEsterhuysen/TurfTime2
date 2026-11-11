import { test, before, after } from "node:test";
import assert from "node:assert/strict";
import type { Server } from "node:http";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StreamableHTTPClientTransport } from "@modelcontextprotocol/sdk/client/streamableHttp.js";
import { createApp } from "../src/app.js";
import { RateLimiter } from "../src/ratelimit.js";
import { setAuditSink } from "../src/audit.js";
import { fakeFirestore } from "./fakeFirestore.js";
import { MemoryStore } from "../src/oauth/store.js";
import { TokenSigner } from "../src/oauth/tokens.js";

const docs = {
  "teams/T1/metadata/info": { teamName: "U12 Girls", kind: "nickname", lastActivityUtc: "2026-11-02T08:00:00Z", createdBy: "alice" },
  "teams/T1/members/alice": { uid: "alice", role: "admin", displayName: "Alice" },
  "teams/T1/members/bob": { uid: "bob", role: "member", displayName: "Bob" },
  "teams/T1/details/kit": { kitText: "Blue shirts", lastModifiedUtc: "2026-11-01T10:00:00Z", updatedByUid: "alice" },
  "teams/T1/details/duties": { dutiesText: "Oranges: Bob", lastModifiedUtc: "2026-10-01T10:00:00Z", updatedByUid: "alice" },
  "teams/T1/details/location": { matchDate: "2026-11-07", matchTime: "09:00", arriveTime: "08:30", locationName: "Main Park", lastModifiedUtc: "2026-11-02T07:00:00Z", updatedByUid: "alice" },
  "teams/T1/messages/m1": { text: "old", userId: "alice", senderName: "Alice", timestamp: "2026-10-30T10:00:00Z" },
  "teams/T1/messages/m2": { text: "Training moved", userId: "alice", senderName: "Alice", timestamp: "2026-11-02T09:00:00Z" },
  "teams/T1/messages/m3": { text: "secret", userId: "bob", senderName: "Bob", timestamp: "2026-11-02T09:05:00Z", isDeleted: true },
  "teams/T1/roster/data": { currentHalf: "secondhalf", timerRunning: true, matchRemainingSeconds: 600, teamAScore: 2, teamBScore: 1, rotationCount: 3,
    controllerUid: "alice", controllerDisplayName: "Alice", lastModifiedUtc: "2026-11-02T09:10:00Z", players: [
    { slotId: 1, name: "Mia", field: true, bench: false, goalie: false, inactive: false, counterSeconds: 120, fieldCell: 6 },
    { slotId: 2, name: "Zoe", field: false, bench: false, goalie: true, inactive: false, counterSeconds: 300, fieldCell: 0 },
    { slotId: 3, name: "Ava", field: true, bench: false, goalie: false, inactive: false, counterSeconds: 60, fieldCell: 1 },
    { slotId: 4, name: "Ella", field: false, bench: true, goalie: false, inactive: false, counterSeconds: 30, fieldCell: 0 },
    { slotId: 5, name: "Ruby", field: false, bench: false, goalie: false, inactive: true, counterSeconds: 0, fieldCell: 0 },
    { slotId: 6, name: "#06 Player", field: false, bench: true, goalie: false, inactive: false, counterSeconds: 0, fieldCell: 0 } ] },
  "teams/T3/metadata/info": { teamName: "Hunters" },
  "teams/T3/members/bob": { uid: "bob", role: "admin", displayName: "Bob" },
  "teams/T3/roster/data": { currentHalf: "setup", lastModifiedUtc: "2026-11-11T07:00:00Z", players: [
    { slotId: 1, name: "Ann", field: true, bench: false, goalie: false, inactive: false, counterSeconds: 0, fieldCell: 3 },
    { slotId: 2, name: "#02 Player", field: true, bench: false, goalie: false, inactive: false, counterSeconds: 0, fieldCell: 7 },
    { slotId: 4, name: "#04 Player", field: false, bench: true, goalie: false, inactive: false, counterSeconds: 0, fieldCell: 0 },
    { slotId: 7, name: "#07 Player", field: false, bench: true, goalie: false, inactive: false, counterSeconds: 0, fieldCell: 0 },
    { slotId: 9, name: "#09 Player", field: false, bench: true, goalie: false, inactive: false, counterSeconds: 0, fieldCell: 0 },
    { slotId: 10, name: "#10 Player", field: false, bench: false, goalie: false, inactive: true, counterSeconds: 0, fieldCell: 0 },
    { slotId: 11, name: "Kim", field: false, bench: false, goalie: false, inactive: true, counterSeconds: 0, fieldCell: 0 },
    { slotId: 12, name: "#12 Player", field: false, bench: false, goalie: false, inactive: true, counterSeconds: 0, fieldCell: 0 } ] },
  "teams/T2/roster/data": { currentHalf: "firsthalf", players: [{ slotId: 1, name: "Secret Kid", field: true, bench: false, goalie: false, inactive: false, counterSeconds: 0, fieldCell: 1 }] },
  "teams/T2/metadata/info": { teamName: "Other team" },
  "teams/T2/members/zed": { uid: "zed", role: "admin" },
  "teams/T2/messages/x1": { text: "not for bob", userId: "zed", senderName: "Zed", timestamp: "2026-11-02T09:00:00Z" },
  "clubs/C1/members/bob": { uid: "bob", role: "member" },
};
const tokens: Record<string, string> = { "tok-bob": "bob", "tok-alice": "alice" };
const fake = fakeFirestore(docs, tokens);
const audits: any[] = [];
let server: Server; let url: URL;

before(async () => {
  setAuditSink(e => audits.push(e));
  const app = createApp({
    projectId: "turf-timer", fetchImpl: fake.fetch, limiter: new RateLimiter(60, 0),
    baseUrl: "http://127.0.0.1", store: new MemoryStore(), signer: new TokenSigner("x".repeat(32), "http://127.0.0.1", "http://127.0.0.1/mcp"),
    idTokens: { forUid: async u => `tok-${u}` },
    allowFirebaseIdTokens: true, // these tests drive tools directly with Firebase-style tokens; OAuth is covered in oauth.test.ts
    verifyFirebase: async (t: string) => { if (!tokens[t]) throw new Error("bad"); return { uid: tokens[t] }; },
  });
  await new Promise<void>(r => { server = app.listen(0, () => r()); });
  url = new URL(`http://127.0.0.1:${(server.address() as any).port}/mcp`);
});
after(() => server.close());

async function client(token: string) {
  const c = new Client({ name: "test", version: "1" });
  await c.connect(new StreamableHTTPClientTransport(url, { requestInit: { headers: { Authorization: `Bearer ${token}` } } }));
  return c;
}
const call = async (c: Client, name: string, args: any = {}) => {
  const r: any = await c.callTool({ name, arguments: args });
  return { r, data: r.isError ? null : JSON.parse(r.content[0].text) };
};

test("rejects missing/invalid bearer", async () => {
  assert.equal((await fetch(url, { method: "POST", headers: { "content-type": "application/json" }, body: "{}" })).status, 401);
  assert.equal((await fetch(url, { method: "POST", headers: { "content-type": "application/json", authorization: "Bearer nope" }, body: "{}" })).status, 401);
  assert.equal((await fetch(url, { method: "GET" })).status, 405);
});

test("exposes the eight read-only tools and five write tools (annotated)", async () => {
  const c = await client("tok-bob");
  const { tools } = await c.listTools();
  const READ = ["get_changes_since", "get_duties", "get_fixtures", "get_kit", "get_lineup", "get_messages_since", "get_roster", "list_teams"];
  const WRITE = ["post_chat_message", "update_duties", "update_kit", "update_next_fixture", "update_roster"];
  assert.deepEqual(tools.map(t => t.name).sort(), [...READ, ...WRITE].sort());
  for (const t of tools) assert.equal(t.annotations?.readOnlyHint, READ.includes(t.name), t.name);
  await c.close();
});

test("list_teams returns only my teams (skips clubs)", async () => {
  const c = await client("tok-bob");
  const { data } = await call(c, "list_teams");
  assert.deepEqual(data.teams, [{ teamId: "T1", role: "member", displayName: "Bob", teamName: "U12 Girls", kind: "nickname", lastActivityUtc: "2026-11-02T08:00:00Z" },
    { teamId: "T3", role: "admin", displayName: "Bob", teamName: "Hunters" }]);
  await c.close();
});

test("messages since: ordered, deleted text hidden", async () => {
  const c = await client("tok-bob");
  const { data } = await call(c, "get_messages_since", { teamId: "T1", since: "2026-11-01T00:00:00Z" });
  assert.deepEqual(data.messages.map((m: any) => [m.id, m.text]), [["m2", "Training moved"], ["m3", null]]);
  await c.close();
});

test("non-member is denied even where rules only require signedIn (messages)", async () => {
  const c = await client("tok-bob");
  const { r } = await call(c, "get_messages_since", { teamId: "T2", since: "2026-01-01T00:00:00Z" });
  assert.equal(r.isError, true);
  assert.match(r.content[0].text, /Not a member/);
  assert.equal(JSON.stringify(r).includes("not for bob"), false);
  await c.close();
});

test("kit, duties, fixtures", async () => {
  const c = await client("tok-bob");
  assert.equal((await call(c, "get_kit", { teamId: "T1" })).data.kit.kitText, "Blue shirts");
  assert.equal((await call(c, "get_duties", { teamId: "T1" })).data.duties.dutiesText, "Oranges: Bob");
  const f = (await call(c, "get_fixtures", { teamId: "T1" })).data;
  assert.equal(f.fixtures.length, 1);
  assert.equal(f.fixtures[0].locationName, "Main Park");
  await c.close();
});

test("changes since", async () => {
  const c = await client("tok-bob");
  const { data } = await call(c, "get_changes_since", { teamId: "T1", since: "2026-11-01T12:00:00Z" });
  assert.deepEqual(data.changes.map((x: any) => x.area).sort(), ["fixtures", "team"]);
  assert.equal(data.newMessages, 2);
  await c.close();
});

test("lineup: on-field by grid cell with goalie, bench, game status; no uids", async () => {
  const c = await client("tok-bob");
  const { data } = await call(c, "get_lineup", { teamId: "T1" });
  assert.deepEqual(data.onField.map((p: any) => [p.name, p.status, p.cell ?? null, p.row ?? null, p.col ?? null]),
    [["Ava", "field", 1, 1, 1], ["Mia", "field", 6, 2, 2], ["Zoe", "goalie", null, null, null]]);
  assert.deepEqual(data.bench.map((p: any) => p.name), ["Ella", "#06 Player"]);
  assert.equal(data.bench[1].defaultName, true);
  assert.deepEqual(data.inactive.map((p: any) => p.name), ["Ruby"]);
  assert.equal(data.unusedSlots, 0);
  assert.deepEqual(data.game, { phase: "second half", timerRunning: true, matchRemainingSeconds: 600, score: { us: 2, them: 1 }, rotationCount: 3, controlledBy: "Alice", lastModifiedUtc: "2026-11-02T09:10:00Z" });
  assert.equal(JSON.stringify(data).includes("alice"), false); // no controllerUid
  await c.close();
});

test("roster: names and status only; unnamed slots optional", async () => {
  const c = await client("tok-bob");
  const { data } = await call(c, "get_roster", { teamId: "T1" });
  assert.deepEqual(data.players.map((p: any) => Object.keys(p).sort()), Array(6).fill(["name", "status"]));
  assert.equal(data.unusedSlots, 0);
  assert.equal((await call(c, "get_roster", { teamId: "T1", includeUnnamed: true })).data.players.length, 6);
  await c.close();
});

test("regression: default-named bench players are listed; only inactive default slots are hidden", async () => {
  const c = await client("tok-bob");
  const lu = (await call(c, "get_lineup", { teamId: "T3" })).data;
  assert.deepEqual(lu.onField.map((p: any) => p.name), ["Ann", "#02 Player"]);
  assert.deepEqual(lu.bench.map((p: any) => p.name), ["#04 Player", "#07 Player", "#09 Player"]);
  assert.ok(lu.bench.every((p: any) => p.defaultName === true && p.status === "bench"));
  assert.deepEqual(lu.inactive.map((p: any) => p.name), ["Kim"]);
  assert.equal(lu.unusedSlots, 2);
  const ro = (await call(c, "get_roster", { teamId: "T3" })).data;
  assert.deepEqual(ro.players.filter((p: any) => p.status === "bench").map((p: any) => p.name), ["#04 Player", "#07 Player", "#09 Player"]);
  assert.equal(ro.players.length, 6);
  assert.equal(ro.unusedSlots, 2);
  assert.equal((await call(c, "get_roster", { teamId: "T3", includeUnnamed: true })).data.players.length, 8);
  await c.close();
});

test("roster/lineup: non-members denied, no data leaked", async () => {
  const c = await client("tok-bob");
  for (const tool of ["get_roster", "get_lineup"]) {
    const { r } = await call(c, tool, { teamId: "T2" });
    assert.equal(r.isError, true);
    assert.equal(JSON.stringify(r).includes("Secret Kid"), false);
  }
  const a = await client("tok-alice");
  // alice is not in T2 either
  assert.equal((await call(a, "get_lineup", { teamId: "T2" })).r.isError, true);
  await a.close();
  await c.close();
});

test("input validation rejects path injection", async () => {
  const c = await client("tok-bob");
  const r: any = await c.callTool({ name: "get_kit", arguments: { teamId: "T1/members/x" } });
  assert.equal(r.isError, true);
  await c.close();
});

test("read tools never issue writes; audit has no tokens or message text", () => {
  for (const x of fake.calls) assert.ok(x.method === "GET" || x.url.endsWith(":runQuery"), `${x.method} ${x.url}`);
  assert.ok(audits.length > 0 && audits.every(a => a.kind === "mcp_audit" && a.uid));
  const s = JSON.stringify(audits);
  assert.equal(s.includes("tok-"), false);
  assert.equal(s.includes("Training moved"), false);
});

test("rate limit returns 429", async () => {
  const lim = new RateLimiter(2, 0);
  assert.equal(lim.take("u"), true); assert.equal(lim.take("u"), true); assert.equal(lim.take("u"), false);
  assert.equal(lim.take("other"), true);
});
