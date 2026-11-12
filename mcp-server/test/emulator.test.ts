/**
 * Write tools against the Firestore EMULATOR with the real ../../firestore.rules (the ones live in
 * production). Skipped unless FIRESTORE_EMULATOR_HOST is set. Run: npm run test:emulator
 */
import { test, before, beforeEach } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { FirestoreUserClient, encodeFields, ts } from "../src/firestore.js";
import { postChatMessage, updateKit, updateDuties, updateNextFixture, updateRoster, setWriteLimiters, WriteContext } from "../src/writes.js";
import { createClub, createTeam, setCreateCap, hashAdminCode } from "../src/creates.js";
import { getLineup, listTeams } from "../src/tools.js";
import { RateLimiter, DailyCap } from "../src/ratelimit.js";

const HOST = process.env.FIRESTORE_EMULATOR_HOST;
const skip = !HOST && "FIRESTORE_EMULATOR_HOST not set (run npm run test:emulator)";
const PROJECT = "demo-turftimer-mcp";
const DB = `http://${HOST}/v1/projects/${PROJECT}/databases/(default)/documents`;
const b64 = (o: unknown) => Buffer.from(JSON.stringify(o)).toString("base64url");
const idToken = (uid: string) => {
  const now = Math.floor(Date.now() / 1000);
  return `${b64({ alg: "none", typ: "JWT" })}.${b64({ iss: `https://securetoken.google.com/${PROJECT}`, aud: PROJECT, sub: uid, user_id: uid, iat: now, exp: now + 3600, auth_time: now, firebase: { identities: {}, sign_in_provider: "anonymous" } })}.`;
};
const ctx = (uid: string): WriteContext => ({ uid, fs: new FirestoreUserClient(idToken(uid), PROJECT, fetch as any, `http://${HOST}/v1`), canWrite: true, clientName: "Grok" });
const owner = { Authorization: "Bearer owner", "Content-Type": "application/json" };
const put = (p: string, data: Record<string, unknown>) => fetch(`${DB}/${p}`, { method: "PATCH", headers: owner, body: JSON.stringify({ fields: encodeFields(data) }) });
const get = async (p: string) => { const r: any = await (await fetch(`${DB}/${p}`, { headers: owner })).json(); return r.fields ?? null; };
const P = (slotId: number, name: string, s: string, fieldCell = 0) => ({ slotId, name, field: s === "field", bench: s === "bench", goalie: s === "goalie", inactive: s === "inactive", counterSeconds: 0, fieldCell });

before(async () => {
  if (skip) return;
  const rules = readFileSync(new URL("../../../firestore.rules", import.meta.url), "utf8");
  const r = await fetch(`http://${HOST}/emulator/v1/projects/${PROJECT}:securityRules`, { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ rules: { files: [{ name: "firestore.rules", content: rules }] } }) });
  assert.equal(r.status, 200, await r.text());
});
beforeEach(async () => {
  if (skip) return;
  setWriteLimiters(new RateLimiter(1000, 0), new RateLimiter(1000, 0));
  setCreateCap(new DailyCap(20));
  await fetch(`http://${HOST}/emulator/v1/projects/${PROJECT}/databases/(default)/documents`, { method: "DELETE" });
  await put("teams/T1/metadata/info", { teamName: "Hunters", createdBy: "admin", lastActivityUtc: ts("2026-01-01T00:00:00Z") });
  await put("teams/T1/members/admin", { uid: "admin", role: "admin", displayName: "Coach Andy" });
  await put("teams/T1/members/mem", { uid: "mem", role: "member", displayName: "Mem" });
  await put("teams/T1/details/kit", { arrive: "Blue", lastModifiedUtc: ts("2026-11-01T10:00:00Z"), updatedByUid: "admin" });
  await put("teams/T1/roster/data", { version: 3, currentHalf: "setup", timerRunning: false, controllerUid: "", lastModifiedUtc: ts("2026-11-11T07:00:00Z"),
    nextFieldSlotIds: [0], nextBenchSlotIds: [0], players: [P(1, "#01 Player", "goalie"), P(2, "Ava", "field", 1), P(4, "#04 Player", "bench")] });
});

test("emulator+real rules: member posts chat (app shape + sentVia) and teammates' rules-read works", { skip }, async () => {
  const r = await postChatMessage(ctx("mem"), "T1", "hello team");
  const f = await get(`teams/T1/messages/${r.messageId}`);
  assert.equal(f.userId.stringValue, "mem"); assert.equal(f.senderName.stringValue, "Mem"); assert.ok(f.timestamp.timestampValue); assert.equal(f.sentVia.stringValue, "mcp:Grok");
});

test("emulator+real rules: rules themselves block chat impersonation and non-member writes", { skip }, async () => {
  const c = ctx("mem");
  await assert.rejects(c.fs.createDoc("teams/T1/messages", { text: "x", userId: "admin", senderName: "Coach Andy", timestamp: ts() }), (e: any) => e.status === 403);
  await assert.rejects(postChatMessage(ctx("outsider"), "T1", "hi"), /Not a member/);
});

test("emulator+real rules: admin kit, duties (create), fixture and roster writes are allowed and land in app shape", { skip }, async () => {
  const a = ctx("admin");
  await updateKit(a, "T1", { game: "Red socks" });
  await updateDuties(a, "T1", { canteen: "Bob" });
  await updateNextFixture(a, "T1", { matchDate: "2026-11-14", matchTime: "09:30", locationName: "Main Park" });
  await updateRoster(a, "T1", [{ player: "#04 Player", newName: "Lily", cell: 6 }]);
  const kit = await get("teams/T1/details/kit");
  assert.equal(kit.game.stringValue, "Red socks"); assert.equal(kit.arrive.stringValue, "Blue"); assert.ok(kit.lastModifiedUtc.timestampValue); assert.equal(kit.editedVia.stringValue, "mcp:Grok");
  assert.equal((await get("teams/T1/details/duties")).canteen.stringValue, "Bob");
  const loc = await get("teams/T1/details/location");
  assert.equal(loc.matchTime.stringValue, "09:30:00"); assert.equal(loc.schemaVersion.integerValue, "1"); assert.equal(loc.latitude.stringValue, "");
  const roster = await get("teams/T1/roster/data");
  const lily = roster.players.arrayValue.values.map((v: any) => v.mapValue.fields).find((p: any) => p.slotId.integerValue === "4");
  assert.equal(lily.name.stringValue, "Lily"); assert.equal(lily.field.booleanValue, true); assert.equal(lily.fieldCell.integerValue, "6"); assert.equal(lily.counterSeconds.integerValue, "0");
  assert.ok(roster.lastModifiedUtc.timestampValue > "2026-11-11T07:00:00Z");
  assert.ok((await get("teams/T1/metadata/info")).lastActivityUtc.timestampValue > "2026-01-01T00:00:00Z", "activity touched (admin metadata update allowed by rules)");
  const lu: any = await getLineup(a, "T1");
  assert.deepEqual(lu.onField.map((p: any) => p.name), ["Ava", "Lily", "#01 Player"]);
});

test("emulator+real rules: member can't change kit (server refuses; rules would too)", { skip }, async () => {
  await assert.rejects(updateKit(ctx("mem"), "T1", { game: "x" }), /Only team admins/);
  await assert.rejects(ctx("mem").fs.patchDoc("teams/T1/details/kit", { game: "x" }, { exists: true }), (e: any) => e.status === 403);
});

test("emulator: stale updateTime precondition is rejected by real Firestore and reported as a conflict", { skip }, async () => {
  const a = ctx("admin");
  const cur = await a.fs.getDoc("teams/T1/details/kit");
  await put("teams/T1/details/kit", { arrive: "Changed by app", lastModifiedUtc: ts() });
  await assert.rejects(a.fs.patchDoc("teams/T1/details/kit", { game: "x" }, { updateTime: cur!.updateTime! }), (e: any) => e.conflict === true);
});

test("emulator+real rules: create_club and create_team (nickname and under the club) match the app and appear for the creator as admin", { skip }, async () => {
  const a = ctx("admin");
  const club = await createClub(a, { clubName: "Cape Crusaders", displayName: "Coach Andy" });
  assert.match(club.clubId, /^club-cape-crusaders-[a-z0-9]{6}$/);
  const cmeta = await get(`clubs/${club.clubId}/metadata/info`);
  assert.equal(cmeta.clubName.stringValue, "Cape Crusaders");
  assert.equal(cmeta.createdBy.stringValue, "admin");
  assert.equal(cmeta.isActive.booleanValue, true);
  assert.equal(cmeta.ownerRecoveryCodeHash.stringValue, hashAdminCode(club.ownerRecoveryCode));
  assert.ok(cmeta.createdAt.timestampValue);
  const cmem = await get(`clubs/${club.clubId}/members/admin`);
  assert.equal(cmem.role.stringValue, "owner");
  assert.equal(cmem.displayName.stringValue, "Coach Andy");

  const nick = await createTeam(a, { name: "Hunter Hunters", creatorEmail: "andy@example.com" });
  assert.equal(nick.kind, "nickname");
  const nmeta = await get(`teams/${nick.teamId}/metadata/info`);
  assert.equal(nmeta.teamName.stringValue, "Hunter Hunters");
  assert.equal(nmeta.kind.stringValue, "nickname");
  assert.equal(nmeta.createdBy.stringValue, "admin");
  assert.equal(nmeta.adminCodeHash.stringValue, hashAdminCode(nick.ownerRecoveryCode));
  assert.equal(nmeta.inviteCode.stringValue, nick.inviteCode);
  assert.equal("clubId" in nmeta, false);
  const nmem = await get(`teams/${nick.teamId}/members/admin`);
  assert.equal(nmem.role.stringValue, "admin");
  assert.equal(nmem.uid.stringValue, "admin");
  const nros = await get(`teams/${nick.teamId}/roster/data`);
  assert.equal(nros.version.integerValue, "2");
  assert.deepEqual(nros.players.arrayValue.values ?? [], []);
  const compact = nick.inviteCode.replace("-", "");
  const inv = await get(`invite_codes/${compact}`);
  assert.equal(inv.teamId.stringValue, nick.teamId);
  assert.equal(inv.createdBy.stringValue, "admin");

  const under = await createTeam(a, { name: "U13", clubId: club.clubId, players: ["Ava"] });
  assert.equal(under.kind, "clubbed");
  assert.equal(under.clubName, "Cape Crusaders");
  const umeta = await get(`teams/${under.teamId}/metadata/info`);
  assert.equal(umeta.kind.stringValue, "clubbed");
  assert.equal(umeta.clubId.stringValue, club.clubId);
  assert.equal(umeta.clubName.stringValue, "Cape Crusaders");
  const uros = await get(`teams/${under.teamId}/roster/data`);
  const ava = uros.players.arrayValue.values.map((v: any) => v.mapValue.fields).find((p: any) => p.slotId.integerValue === "1");
  assert.equal(ava.name.stringValue, "Ava"); assert.equal(ava.bench.booleanValue, true);
  assert.equal(uros.players.arrayValue.values.length, 16);

  const listed: any = await listTeams(a);
  const ids = listed.teams.map((t: any) => t.teamId);
  assert.ok(ids.includes(nick.teamId) && ids.includes(under.teamId));
  assert.ok(listed.teams.every((t: any) => t.teamId === "T1" || t.role === "admin"));
  const clubbed = listed.teams.find((t: any) => t.teamId === under.teamId);
  assert.equal(clubbed.clubId, club.clubId); assert.equal(clubbed.kind, "clubbed");
});

test("emulator+real rules: only the club Owner or a Club Admin can create a team under the club", { skip }, async () => {
  const owner = ctx("admin");
  const club = await createClub(owner, { clubName: "FC", displayName: "Coach Andy" });
  await assert.rejects(createTeam(ctx("mem"), { name: "U9", clubId: club.clubId, displayName: "Mem Two" }), /Only the club Owner or a Club Admin/);
  // Rules themselves also deny a clubbed metadata create when the caller is not Owner/Admin.
  const mem = ctx("mem");
  await assert.rejects(mem.fs.patchDoc(`teams/sneaky/metadata/info`, { teamName: "U9", createdBy: "mem", clubId: club.clubId, clubName: "FC", kind: "clubbed", isActive: true }, { exists: false }), (e: any) => e.status === 403);
  // A nickname team is still allowed for any signed-in user (same as the app).
  const nick = await createTeam(mem, { name: "Mems Nickname", displayName: "Mem Two" });
  assert.equal(nick.kind, "nickname");
  assert.equal((await get(`teams/${nick.teamId}/members/mem`)).role.stringValue, "admin");
});
