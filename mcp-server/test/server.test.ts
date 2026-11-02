import { test, before, after } from "node:test";
import assert from "node:assert/strict";
import type { Server } from "node:http";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StreamableHTTPClientTransport } from "@modelcontextprotocol/sdk/client/streamableHttp.js";
import { createApp } from "../src/app.js";
import { RateLimiter } from "../src/ratelimit.js";
import { setAuditSink } from "../src/audit.js";
import { fakeFirestore } from "./fakeFirestore.js";

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
    projectId: "turf-timer", fetchImpl: fake.fetch, limiter: new RateLimiter(25, 0),
    verify: async t => { if (!tokens[t]) throw new Error("bad"); return { uid: tokens[t] }; },
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

test("exposes exactly the six read-only tools", async () => {
  const c = await client("tok-bob");
  const { tools } = await c.listTools();
  assert.deepEqual(tools.map(t => t.name).sort(), ["get_changes_since", "get_duties", "get_fixtures", "get_kit", "get_messages_since", "list_teams"]);
  for (const t of tools) assert.equal(t.annotations?.readOnlyHint, true);
  await c.close();
});

test("list_teams returns only my teams (skips clubs)", async () => {
  const c = await client("tok-bob");
  const { data } = await call(c, "list_teams");
  assert.deepEqual(data.teams, [{ teamId: "T1", role: "member", displayName: "Bob", teamName: "U12 Girls", kind: "nickname", lastActivityUtc: "2026-11-02T08:00:00Z" }]);
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

test("input validation rejects path injection", async () => {
  const c = await client("tok-bob");
  const r: any = await c.callTool({ name: "get_kit", arguments: { teamId: "T1/members/x" } });
  assert.equal(r.isError, true);
  await c.close();
});

test("server never issues writes; audit has no tokens or message text", () => {
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
