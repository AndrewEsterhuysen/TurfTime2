import { test, before, after, beforeEach } from "node:test";
import assert from "node:assert/strict";
import type { Server } from "node:http";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StreamableHTTPClientTransport } from "@modelcontextprotocol/sdk/client/streamableHttp.js";
import { createApp } from "../src/app.js";
import { RateLimiter } from "../src/ratelimit.js";
import { setAuditSink } from "../src/audit.js";
import { setWriteLimiters, WriteContext, WriteRefused } from "../src/writes.js";
import { createClub, createTeam, setCreateCap, hashAdminCode } from "../src/creates.js";
import { DailyCap } from "../src/ratelimit.js";
import { FirestoreUserClient } from "../src/firestore.js";
import { fakeFirestore } from "./fakeFirestore.js";
import { MemoryStore } from "../src/oauth/store.js";
import { TokenSigner } from "../src/oauth/tokens.js";

const P = (slotId: number, name: string, s: "field" | "goalie" | "bench" | "inactive", fieldCell = 0) =>
  ({ slotId, name, field: s === "field", bench: s === "bench", goalie: s === "goalie", inactive: s === "inactive", counterSeconds: 7, fieldCell });
function seed() {
  return {
    "teams/T1/metadata/info": { teamName: "Hunters", createdBy: "admin" },
    "teams/T1/members/admin": { uid: "admin", role: "admin", displayName: "Coach Andy" },
    "teams/T1/members/mem": { uid: "mem", role: "member", displayName: "Mem" },
    "teams/T1/messages/m1": { text: "Training at 5", userId: "mem", senderName: "Mem", timestamp: "2026-11-02T09:00:00Z" },
    "teams/T1/details/kit": { arrive: "Blue", game: "Shin pads", lastModifiedUtc: "2026-11-01T10:00:00Z", updatedByUid: "admin" },
    "teams/T1/roster/data": { version: 3, currentHalf: "setup", timerRunning: false, controllerUid: "", controllerDisplayName: "", lastModifiedUtc: "2026-11-11T07:00:00Z",
      nextFieldSlotIds: [2, 0], nextBenchSlotIds: [4, 7], players: [P(1, "#01 Player", "goalie"), P(2, "Ava", "field", 1), P(3, "Mia", "field", 6), P(4, "#04 Player", "bench"), P(7, "Zoe", "bench"), P(10, "#10 Player", "inactive")] },
    "teams/T2/metadata/info": { teamName: "Other" },
    "teams/T2/members/zed": { uid: "zed", role: "admin", displayName: "Zed" },
    "teams/T2/details/kit": { arrive: "ZED" },
  } as Record<string, any>;
}
const tokens: Record<string, string> = { "tok-admin": "admin", "tok-mem": "mem", "tok-out": "outsider" };
let fake = fakeFirestore(seed(), tokens);
let server: Server; let url: URL;

before(async () => {
  setAuditSink(() => {});
  const app = createApp({
    projectId: "turf-timer", fetchImpl: (u, i) => fake.fetch(u, i), limiter: new RateLimiter(500, 0), ipLimiter: new RateLimiter(5000, 0),
    baseUrl: "http://127.0.0.1", store: new MemoryStore(), signer: new TokenSigner("x".repeat(32), "http://127.0.0.1", "http://127.0.0.1/mcp"),
    idTokens: { forUid: async u => `tok-${u}` },
    allowFirebaseIdTokens: true, allowFirebaseIdTokenWrites: true,
    verifyFirebase: async (t: string) => { if (!tokens[t]) throw new Error("bad"); return { uid: tokens[t] }; },
  });
  await new Promise<void>(r => { server = app.listen(0, () => r()); });
  url = new URL(`http://127.0.0.1:${(server.address() as any).port}/mcp`);
});
after(() => server.close());
beforeEach(() => { fake = fakeFirestore(seed(), tokens); setWriteLimiters(new RateLimiter(1000, 0), new RateLimiter(1000, 0)); setCreateCap(new DailyCap(20)); });

async function call(token: string, name: string, args: any = {}) {
  const c = new Client({ name: "t", version: "1" });
  await c.connect(new StreamableHTTPClientTransport(url, { requestInit: { headers: { Authorization: `Bearer ${token}` } } }));
  const r: any = await c.callTool({ name, arguments: args });
  await c.close();
  return { r, err: r.isError ? r.content[0].text as string : null, data: r.isError ? null : JSON.parse(r.content[0].text) };
}
const writes = () => fake.calls.filter(c => c.method === "PATCH" || (c.method === "POST" && !c.url.endsWith(":runQuery")));
const maskOf = (w: any) => [...w.body.writes[0].updateMask.fieldPaths].sort();
const preOf = (w: any) => w.body.writes[0].currentDocument;

// ---- chat ----
test("chat: a member posts in their own name with the app's message shape; sender comes from the token, not input", async () => {
  const { data } = await call("tok-mem", "post_chat_message", { teamId: "T1", text: "  Running 5 min late  ", senderName: "Coach Andy", userId: "admin" });
  const d = fake.docs[`teams/T1/messages/${data.messageId}`];
  assert.deepEqual(Object.keys(d).sort(), ["senderName", "sentVia", "text", "timestamp", "userId"]);
  assert.equal(d.userId, "mem"); assert.equal(d.senderName, "Mem"); assert.equal(d.text, "Running 5 min late");
  assert.equal(d.sentVia, "mcp:dev token");
  assert.match(String(d.timestamp), /^\d{4}-\d\d-\d\dT/);
  assert.ok(writes()[0].body.fields.timestamp.timestampValue, "timestamp is a Firestore timestamp like the app's");
});

test("chat: reply copies the real quoted text/sender (max 120/40), not caller-supplied text", async () => {
  const { data } = await call("tok-mem", "post_chat_message", { teamId: "T1", text: "ok", replyToMessageId: "m1", replyToText: "fake" });
  const d = fake.docs[`teams/T1/messages/${data.messageId}`];
  assert.equal(d.replyToMessageId, "m1"); assert.equal(d.replyToText, "Training at 5"); assert.equal(d.replyToSenderName, "Mem");
  assert.match((await call("tok-mem", "post_chat_message", { teamId: "T1", text: "ok", replyToMessageId: "nope" })).err!, /not found/);
});

test("chat: validation (empty, >500) and non-members refused; nothing written", async () => {
  assert.match((await call("tok-mem", "post_chat_message", { teamId: "T1", text: "   " })).err!, /empty/);
  assert.match((await call("tok-mem", "post_chat_message", { teamId: "T1", text: "x".repeat(501) })).err!, /500/);
  assert.match((await call("tok-out", "post_chat_message", { teamId: "T1", text: "hi" })).err!, /Not a member/);
  assert.match((await call("tok-admin", "post_chat_message", { teamId: "T2", text: "hi" })).err!, /Not a member/);
  assert.equal(writes().length, 0);
});

test("chat: per-user rate limit", async () => {
  setWriteLimiters(new RateLimiter(1000, 0), new RateLimiter(2, 0));
  assert.equal((await call("tok-mem", "post_chat_message", { teamId: "T1", text: "1" })).err, null);
  assert.equal((await call("tok-mem", "post_chat_message", { teamId: "T1", text: "2" })).err, null);
  assert.match((await call("tok-mem", "post_chat_message", { teamId: "T1", text: "3" })).err!, /Too many/);
  assert.equal((await call("tok-admin", "post_chat_message", { teamId: "T1", text: "other user ok" })).err, null);
});

// ---- kit / duties ----
test("kit: admin changes only the given fields + app metadata; members and non-members refused", async () => {
  const { data, err } = await call("tok-admin", "update_kit", { teamId: "T1", game: "Red socks", arrive: "" });
  assert.equal(err, null);
  const d = fake.docs["teams/T1/details/kit"];
  assert.equal(d.game, "Red socks"); assert.equal(d.arrive, ""); assert.equal(d.updatedByUid, "admin"); assert.equal(d.editedVia, "mcp:dev token");
  assert.equal(d.lastModifiedUtc, d.editedViaAtUtc); assert.equal(d.lastModifiedUtc, data.lastModifiedUtc);
  const w = writes()[0];
  assert.deepEqual(maskOf(w), ["arrive", "editedVia", "editedViaAtUtc", "game", "lastModifiedUtc", "updatedByUid"]);
  assert.ok(preOf(w).updateTime, "precondition on the read version");
  assert.match((await call("tok-mem", "update_kit", { teamId: "T1", game: "x" })).err!, /Only team admins/);
  assert.match((await call("tok-admin", "update_kit", { teamId: "T2", game: "x" })).err!, /Not a member/);
  assert.match((await call("tok-admin", "update_kit", { teamId: "T1" })).err!, /at least one/);
  assert.equal(writes().length, 1);
});

test("duties: creates the doc when missing (exists=false precondition)", async () => {
  const { err } = await call("tok-admin", "update_duties", { teamId: "T1", canteen: "Bob", officer: "Sue" });
  assert.equal(err, null);
  assert.deepEqual(preOf(writes()[0]), { exists: false });
  assert.equal(fake.docs["teams/T1/details/duties"].canteen, "Bob");
});

test("lost-update protection: a concurrent change between read and write is refused", async () => {
  const orig = fake.fetch;
  fake.fetch = (async (u: string, i: any) => {
    if (String(u).endsWith(":commit")) fake.bump("teams/T1/details/kit"); // the app saved in between
    return orig(u, i);
  }) as any;
  assert.match((await call("tok-admin", "update_kit", { teamId: "T1", game: "x" })).err!, /changed this at the same moment/);
  assert.equal(fake.docs["teams/T1/details/kit"].game, "Shin pads");
});

// ---- next fixture ----
test("fixture: app formats (yyyy-MM-dd, HH:mm:ss), validation, display name, schemaVersion", async () => {
  const { err, data } = await call("tok-admin", "update_next_fixture", { teamId: "T1", matchDate: "2026-11-14", matchTime: "9:30", arriveTime: "08:45", locationName: "Main Park", latitude: "-27.47", longitude: "153.02", mapsLink: "https://maps.google.com/?q=x" });
  assert.equal(err, null);
  const d = fake.docs["teams/T1/details/location"];
  assert.equal(d.matchDate, "2026-11-14"); assert.equal(d.matchTime, "09:30:00"); assert.equal(d.arriveTime, "08:45:00");
  assert.equal(d.schemaVersion, 1); assert.equal(d.updatedByDisplayName, "Coach Andy"); assert.equal(d.updatedByUid, "admin");
  assert.equal(data.fixture.locationName, "Main Park");
  for (const [k, v, re] of [["matchDate", "2026-02-30", /real date/], ["matchTime", "25:00", /24-hour/], ["latitude", "91", /between/], ["mapsLink", "http://x", /https/], ["mapsLink", "javascript:alert(1)", /https/]] as const)
    assert.match((await call("tok-admin", "update_next_fixture", { teamId: "T1", [k]: v })).err!, re, k);
  assert.match((await call("tok-mem", "update_next_fixture", { teamId: "T1", matchTime: "10:00" })).err!, /Only team admins/);
});

test("fixture: partial update keeps other fields", async () => {
  await call("tok-admin", "update_next_fixture", { teamId: "T1", matchDate: "2026-11-14", locationName: "Main Park" });
  await call("tok-admin", "update_next_fixture", { teamId: "T1", matchTime: "10:15" });
  const d = fake.docs["teams/T1/details/location"];
  assert.equal(d.locationName, "Main Park"); assert.equal(d.matchTime, "10:15:00"); assert.equal(d.arriveTime, "");
});

// ---- roster / lineup ----
const players = () => fake.docs["teams/T1/roster/data"].players as any[];
const bySlot = (n: number) => players().find(p => p.slotId === n);

test("roster: rename, place on a free cell, bench/inactive; keeps other player fields and only patches roster fields", async () => {
  const { err, data } = await call("tok-admin", "update_roster", { teamId: "T1", changes: [
    { player: "#04 Player", newName: "Lily", status: "field", cell: 16 }, { player: "zoe", status: "inactive" }, { player: 10, status: "bench" }] });
  assert.equal(err, null, err!);
  assert.deepEqual([bySlot(4).name, bySlot(4).field, bySlot(4).fieldCell, bySlot(4).bench], ["Lily", true, 16, false]);
  assert.deepEqual([bySlot(7).inactive, bySlot(7).bench, bySlot(7).fieldCell], [true, false, 0]);
  assert.equal(bySlot(10).bench, true);
  assert.equal(bySlot(4).counterSeconds, 7, "untouched fields preserved");
  assert.deepEqual(players().map(p => p.slotId), [1, 2, 3, 4, 7, 10], "order preserved");
  const mask = maskOf(writes()[0]);
  assert.deepEqual(mask, ["editedVia", "editedViaAtUtc", "lastModified", "lastModifiedUtc", "nextBenchSlotIds", "players"]);
  assert.deepEqual(fake.docs["teams/T1/roster/data"].nextBenchSlotIds, [0, 0], "moved players dropped from rotation queues");
  assert.equal(fake.docs["teams/T1/roster/data"].controllerUid, "", "controller fields untouched");
  assert.ok(data.changes.length >= 3);
  assert.ok(fake.docs["teams/T1/metadata/info"].lastActivityUtc, "team activity touched like the app");
});

test("roster: app rules — new goalie benches the old one; occupied cell swaps (field->field) or subs (bench->field)", async () => {
  await call("tok-admin", "update_roster", { teamId: "T1", changes: [{ player: "Zoe", status: "goalie" }] });
  assert.equal(bySlot(7).goalie, true); assert.equal(bySlot(1).bench, true); assert.equal(bySlot(1).goalie, false);
  await call("tok-admin", "update_roster", { teamId: "T1", changes: [{ player: "Ava", cell: 6 }] });
  assert.deepEqual([bySlot(2).fieldCell, bySlot(3).fieldCell, bySlot(3).field], [6, 1, true]);
  await call("tok-admin", "update_roster", { teamId: "T1", changes: [{ player: 4, cell: 1 }] });
  assert.deepEqual([bySlot(4).field, bySlot(4).fieldCell, bySlot(3).bench, bySlot(3).fieldCell], [true, 1, true, 0]);
});

test("roster: validation and ambiguity", async () => {
  assert.match((await call("tok-admin", "update_roster", { teamId: "T1", changes: [{ player: "Nobody", status: "bench" }] })).err!, /No player/);
  assert.match((await call("tok-admin", "update_roster", { teamId: "T1", changes: [{ player: "Ava", status: "bench", cell: 3 }] })).err!, /cell only applies/);
  assert.match((await call("tok-admin", "update_roster", { teamId: "T1", changes: [{ player: "Ava", newName: " " }] })).err!, /1 to 40/);
  assert.equal((await call("tok-admin", "update_roster", { teamId: "T1", changes: [{ player: "Ava", cell: 17 }] })).r.isError, true);
  await call("tok-admin", "update_roster", { teamId: "T1", changes: [{ player: "Zoe", newName: "Ava" }] });
  assert.match((await call("tok-admin", "update_roster", { teamId: "T1", changes: [{ player: "Ava", status: "bench" }] })).err!, /More than one/);
});

test("roster: members and non-members refused even though rules let members write roster/data", async () => {
  assert.match((await call("tok-mem", "update_roster", { teamId: "T1", changes: [{ player: "Ava", status: "bench" }] })).err!, /Only team admins/);
  assert.match((await call("tok-out", "update_roster", { teamId: "T1", changes: [{ player: "Ava", status: "bench" }] })).err!, /Not a member/);
  assert.equal(writes().length, 0);
});

test("roster: game guard — refused while an admin controls the game or the clock runs", async () => {
  fake.docs["teams/T1/roster/data"].controllerUid = "admin2"; fake.docs["teams/T1/roster/data"].controllerDisplayName = "Sam";
  assert.match((await call("tok-admin", "update_roster", { teamId: "T1", changes: [{ player: "Ava", status: "bench" }] })).err!, /Sam is running the game/);
  fake.docs["teams/T1/roster/data"].controllerUid = ""; fake.docs["teams/T1/roster/data"].timerRunning = true;
  assert.match((await call("tok-admin", "update_roster", { teamId: "T1", changes: [{ player: "Ava", status: "bench" }] })).err!, /clock is running/);
  assert.equal(writes().length, 0);
});

test("roster: precondition — the admin phone saving in between wins; we refuse instead of overwriting", async () => {
  const orig = fake.fetch;
  fake.fetch = (async (u: string, i: any) => { if (String(u).endsWith(":commit")) fake.bump("teams/T1/roster/data"); return orig(u, i); }) as any;
  assert.match((await call("tok-admin", "update_roster", { teamId: "T1", changes: [{ player: "Ava", status: "bench" }] })).err!, /same moment/);
  assert.equal(bySlot(2).field, true);
});

test("audit marks writes and never logs message text", async () => {
  const seen: any[] = []; setAuditSink(e => seen.push(e));
  await call("tok-mem", "post_chat_message", { teamId: "T1", text: "secret plan" });
  setAuditSink(() => {});
  assert.equal(seen[0].write, true); assert.equal(JSON.stringify(seen).includes("secret plan"), false);
});

// ---- create_club / create_team ----
const wctx = (uid: string, canWrite = true): WriteContext =>
  ({ uid, fs: new FirestoreUserClient(`tok-${uid}`, "turf-timer", (u, i) => fake.fetch(u, i)), canWrite, clientName: "Grok" });

function metaOf(teamId: string) { return fake.docs[`teams/${teamId}/metadata/info`]; }
function memberOf(teamId: string, uid = "admin") { return fake.docs[`teams/${teamId}/members/${uid}`]; }

test("create_team: standalone nickname team matches the app (metadata, admin member, empty roster, invite index) and appears for the creator as admin", async () => {
  const { data, err } = await call("tok-admin", "create_team", { name: "Hunter Hunters", creatorEmail: "andy@example.com" });
  assert.equal(err, null, err!);
  assert.match(data.teamId, /^hunter-hunters-[a-z0-9]{6}$/);
  assert.equal(data.kind, "nickname");
  assert.equal(data.role, "admin");
  assert.equal(data.isOwner, true);
  assert.match(data.inviteCode, /^[ABCDEFGHJKLMNPQRSTUVWXYZ23456789]{4}-[ABCDEFGHJKLMNPQRSTUVWXYZ23456789]{4}$/);
  assert.match(data.ownerRecoveryCode, /^([ABCDEFGHJKLMNPQRSTUVWXYZ23456789]{4}-){3}[ABCDEFGHJKLMNPQRSTUVWXYZ23456789]{4}$/);
  const t = data.teamId;
  const m = metaOf(t);
  assert.equal(m.teamName, "Hunter Hunters");
  assert.equal(m.kind, "nickname");
  assert.equal(m.createdBy, "admin");
  assert.equal(m.isActive, true);
  assert.equal(m.inviteCode, data.inviteCode);
  assert.equal(m.adminCodeHash, hashAdminCode(data.ownerRecoveryCode));
  assert.equal(m.creatorEmail, "andy@example.com");
  assert.ok(m.createdAt); assert.ok(m.lastActivityUtc);
  assert.equal("clubId" in m, false);
  assert.deepEqual(memberOf(t), { role: "admin", displayName: "Coach Andy", uid: "admin" });
  assert.deepEqual(fake.docs[`teams/${t}/roster/data`], { version: 2, players: [] });
  const compact = data.inviteCode.replace("-", "");
  const inv = fake.docs[`invite_codes/${compact}`];
  assert.deepEqual(inv, { teamId: t, teamName: "Hunter Hunters", clubId: "", clubName: "", inviteCode: data.inviteCode, inviteCodeCompact: compact, createdBy: "admin" });
  assert.deepEqual(fake.docs[`teams/${t}/public/invite`], inv);
  const listed = (await call("tok-admin", "list_teams")).data.teams;
  const row = listed.find((x: any) => x.teamId === t);
  assert.deepEqual({ teamId: row.teamId, role: row.role, teamName: row.teamName, kind: row.kind, displayName: row.displayName },
    { teamId: t, role: "admin", teamName: "Hunter Hunters", kind: "nickname", displayName: "Coach Andy" });
});

test("create_club then create_team under that club: docs match the app; club member (not admin) is refused; club Admin is allowed", async () => {
  const club = (await call("tok-admin", "create_club", { clubName: "Cape Crusaders", displayName: "Coach Andy" })).data;
  assert.equal(club.role, "owner"); assert.equal(club.isOwner, true);
  assert.match(club.clubId, /^club-cape-crusaders-[a-z0-9]{6}$/);
  const c = club.clubId;
  const cm = fake.docs[`clubs/${c}/metadata/info`];
  assert.equal(cm.clubName, "Cape Crusaders");
  assert.equal(cm.createdBy, "admin");
  assert.equal(cm.isActive, true);
  assert.equal(cm.ownerRecoveryCodeHash, hashAdminCode(club.ownerRecoveryCode));
  assert.deepEqual({ role: fake.docs[`clubs/${c}/members/admin`].role, displayName: fake.docs[`clubs/${c}/members/admin`].displayName },
    { role: "owner", displayName: "Coach Andy" });

  const team = (await call("tok-admin", "create_team", { name: "U13", clubId: c })).data;
  assert.equal(team.kind, "clubbed");
  assert.equal(team.clubId, c);
  assert.equal(team.clubName, "Cape Crusaders");
  assert.match(team.teamId, /^u13-cape-crusaders-[a-z0-9]{6}$/);
  const tm = metaOf(team.teamId);
  assert.equal(tm.kind, "clubbed"); assert.equal(tm.clubId, c); assert.equal(tm.clubName, "Cape Crusaders");
  assert.equal(memberOf(team.teamId).role, "admin");
  const listed = (await call("tok-admin", "list_teams")).data.teams.find((x: any) => x.teamId === team.teamId);
  assert.equal(listed.role, "admin"); assert.equal(listed.clubId, c); assert.equal(listed.clubName, "Cape Crusaders");

  // mem is not a club admin
  assert.match((await call("tok-mem", "create_team", { name: "U14", clubId: c, displayName: "Mem Mem" })).err!, /Only the club Owner or a Club Admin/);
  assert.equal(Object.keys(fake.docs).some(k => k.startsWith("teams/u14-")), false);

  // promote mem to club admin (as the app's PromoteClubMemberToAdmin would)
  fake.docs[`clubs/${c}/members/mem`] = { role: "admin", displayName: "Mem" };
  const u14 = (await call("tok-mem", "create_team", { name: "U14", clubId: c })).data;
  assert.equal(u14.kind, "clubbed");
  assert.equal(memberOf(u14.teamId, "mem").role, "admin");
  assert.equal(memberOf(u14.teamId, "mem").uid, "mem");
});

test("create_team: optional players seeds a 16-slot bench roster; update_roster can fill an empty one", async () => {
  const { data, err } = await call("tok-admin", "create_team", { name: "Seeds", players: ["Ava", "Mia"] });
  assert.equal(err, null, err!);
  const players = fake.docs[`teams/${data.teamId}/roster/data`].players as any[];
  assert.equal(players.length, 16);
  assert.equal(players[0].name, "Ava"); assert.equal(players[0].bench, true); assert.equal(players[0].slotId, 1);
  assert.equal(players[1].name, "Mia");
  assert.equal(players[2].name, "#03 Player");
  assert.equal(players[15].name, "#16 Player");
  assert.equal(data.players.map((p: any) => p.name).join(","), "Ava,Mia");
  assert.equal(data.unusedSlots, 14);

  const empty = (await call("tok-admin", "create_team", { name: "EmptyRoster" })).data;
  const { data: ro, err: e2 } = await call("tok-admin", "update_roster", { teamId: empty.teamId, changes: [{ player: 1, newName: "Lily" }, { player: 2, newName: "Zoe", status: "goalie" }] });
  assert.equal(e2, null, e2!);
  const rp = fake.docs[`teams/${empty.teamId}/roster/data`].players as any[];
  assert.equal(rp.length, 16);
  assert.equal(rp[0].name, "Lily"); assert.equal(rp[1].name, "Zoe"); assert.equal(rp[1].goalie, true);
  assert.equal(rp[2].name, "#03 Player");
});

test("create_team: missing write scope, empty or bad name, unknown club — nothing written", async () => {
  await assert.rejects(createTeam(wctx("admin", false), { name: "Nope" }), (e: any) => e instanceof WriteRefused && /read-only/.test(e.message));
  await assert.rejects(createClub(wctx("admin", false), { clubName: "Nope" }), (e: any) => e instanceof WriteRefused && /read-only/.test(e.message));
  assert.match((await call("tok-admin", "create_team", { name: "   " })).err!, /cannot be empty/);
  assert.match((await call("tok-admin", "create_club", { clubName: "\t  " })).err!, /cannot be empty/);
  assert.match((await call("tok-admin", "create_team", { name: "Ok", clubId: "no-such-club" })).err!, /not found/i);
  const created = Object.keys(fake.docs).filter(k => k.includes("nope") || k.includes("no-such") || /teams\/(ok|n)-/.test(k));
  assert.deepEqual(created, []);
  // failed attempts did not consume the daily cap
  setCreateCap(new DailyCap(1));
  assert.equal((await call("tok-admin", "create_team", { name: "AfterFails" })).err, null);
});

test("create_club and create_team share the 20-per-day cap (per uid); another user is unaffected", async () => {
  setCreateCap(new DailyCap(2));
  assert.equal((await call("tok-admin", "create_club", { clubName: "A" })).err, null);
  assert.equal((await call("tok-admin", "create_team", { name: "B" })).err, null);
  assert.match((await call("tok-admin", "create_team", { name: "C" })).err!, /already created 2 clubs or teams today/);
  assert.match((await call("tok-admin", "create_club", { clubName: "D" })).err!, /already created 2 clubs or teams today/);
  const other = (await call("tok-mem", "create_team", { name: "Mems Team", displayName: "Mem Two" })).data;
  assert.equal(other.kind, "nickname");
  assert.equal(memberOf(other.teamId, "mem").uid, "mem");
});
