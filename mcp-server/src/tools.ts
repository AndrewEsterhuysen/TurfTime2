/**
 * Phase 1 read-only tools. Each maps to an existing Firestore path (see docs/MCP-API.md).
 * Every team-scoped tool first confirms the caller is a member (teams/{t}/members/{uid}),
 * because some rules (messages, sessions, roster) only require signedIn() — the MCP layer
 * must not widen what a user can see beyond "teams I belong to".
 */
import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { FirestoreUserClient, FirestoreError, FsDoc } from "./firestore.js";
import { audit } from "./audit.js";

export interface ToolContext { uid: string; fs: FirestoreUserClient; }

const teamId = z.string().min(1).max(128).regex(/^[^/]+$/, "teamId must not contain '/'");
const isoSince = z.string().datetime({ offset: true }).describe("ISO-8601 timestamp, e.g. 2026-11-01T00:00:00Z");

export class AccessDenied extends Error {}
/** A refusal whose message is safe and useful to show the assistant (validation, guards, scope). */
export class ToolRefused extends Error {}

export async function requireMember(ctx: ToolContext, t: string): Promise<FsDoc> {
  let m: FsDoc | null;
  try { m = await ctx.fs.getDoc(`teams/${t}/members/${ctx.uid}`); }
  catch (e) { if (e instanceof FirestoreError && e.status === 403) m = null; else throw e; }
  if (!m) throw new AccessDenied(`Not a member of team ${t}`);
  return m;
}

const pick = (d: Record<string, unknown>, keys: string[]) =>
  Object.fromEntries(keys.filter(k => k in d).map(k => [k, d[k]]));

export async function listTeams(ctx: ToolContext) {
  // Same collection-group query the app's Select Team uses; rules allow it when uid == auth.uid.
  const rows = await ctx.fs.runQuery("", {
    from: [{ collectionId: "members", allDescendants: true }],
    where: { fieldFilter: { field: { fieldPath: "uid" }, op: "EQUAL", value: { stringValue: ctx.uid } } },
    limit: 200,
  });
  const teams = [];
  for (const r of rows) {
    const parts = r.path.split("/");
    if (parts[0] !== "teams" || parts.length !== 4) continue; // skip clubs/*/members
    const t = parts[1];
    const meta = await ctx.fs.getDoc(`teams/${t}/metadata/info`).catch(() => null);
    teams.push({
      teamId: t,
      role: r.data.role ?? "member",
      displayName: r.data.displayName ?? null,
      ...pick(meta?.data ?? {}, ["teamName", "clubId", "clubName", "kind", "lastActivityUtc"]),
    });
  }
  return { teams };
}

function shapeMessage(d: FsDoc) {
  const deleted = d.data.isDeleted === true;
  return {
    id: d.id,
    timestamp: d.data.timestamp ?? null,
    userId: d.data.userId ?? null,
    senderName: d.data.senderName ?? null,
    text: deleted ? null : d.data.text ?? null,
    isDeleted: deleted,
    isPinned: d.data.isPinned === true,
    ...pick(d.data, ["replyToMessageId", "replyToSenderName", "replyToText", "reactions"]),
  };
}

export async function getMessagesSince(ctx: ToolContext, t: string, since: string, limit = 50) {
  await requireMember(ctx, t);
  const rows = await ctx.fs.runQuery(`teams/${t}`, {
    from: [{ collectionId: "messages" }],
    where: { fieldFilter: { field: { fieldPath: "timestamp" }, op: "GREATER_THAN", value: { timestampValue: new Date(since).toISOString() } } },
    orderBy: [{ field: { fieldPath: "timestamp" }, direction: "ASCENDING" }],
    limit: Math.min(Math.max(limit, 1), 100),
  });
  return { teamId: t, since, messages: rows.map(shapeMessage) };
}

async function getDetailsDoc(ctx: ToolContext, t: string, doc: "kit" | "duties" | "location") {
  await requireMember(ctx, t);
  const d = await ctx.fs.getDoc(`teams/${t}/details/${doc}`);
  return { teamId: t, exists: !!d, [doc]: d?.data ?? null };
}

export const getKit = (ctx: ToolContext, t: string) => getDetailsDoc(ctx, t, "kit");
export const getDuties = (ctx: ToolContext, t: string) => getDetailsDoc(ctx, t, "duties");

/** No fixtures list exists in the schema: only the single next match in details/location. */
export async function getFixtures(ctx: ToolContext, t: string) {
  const r = await getDetailsDoc(ctx, t, "location");
  const loc = (r as any).location as Record<string, unknown> | null;
  const hasMatch = !!loc && !!(loc.matchDate || loc.matchTime || loc.locationName);
  return {
    teamId: t,
    note: "TurfTimer stores only the next scheduled match (teams/{teamId}/details/location); there is no fixtures list yet.",
    fixtures: hasMatch ? [pick(loc!, ["matchDate", "matchTime", "arriveTime", "locationName", "latitude", "longitude", "mapsLink", "lastModifiedUtc", "updatedByDisplayName"])] : [],
  };
}

const newer = (ts: unknown, since: string) => typeof ts === "string" && Date.parse(ts) > Date.parse(since);

export async function getChangesSince(ctx: ToolContext, t: string, since: string) {
  await requireMember(ctx, t);
  const [meta, loc, kit, duties, msgs] = await Promise.all([
    ctx.fs.getDoc(`teams/${t}/metadata/info`).catch(() => null),
    ctx.fs.getDoc(`teams/${t}/details/location`),
    ctx.fs.getDoc(`teams/${t}/details/kit`),
    ctx.fs.getDoc(`teams/${t}/details/duties`),
    getMessagesSince(ctx, t, since, 100),
  ]);
  const changes: { area: string; changedAt: unknown; by?: unknown }[] = [];
  for (const [area, d] of [["fixtures", loc], ["kit", kit], ["duties", duties]] as const)
    if (d && newer(d.data.lastModifiedUtc, since)) changes.push({ area, changedAt: d.data.lastModifiedUtc, by: d.data.updatedByDisplayName ?? d.data.updatedByUid });
  if (meta && newer(meta.data.lastActivityUtc, since)) changes.push({ area: "team", changedAt: meta.data.lastActivityUtc });
  return {
    teamId: t, since,
    changes,
    newMessages: msgs.messages.length,
    newMessagesTruncated: msgs.messages.length >= 100,
    latestMessageAt: msgs.messages.at(-1)?.timestamp ?? null,
  };
}

// ---- Roster / live game (teams/{t}/roster/data) -------------------------------------------------
// Written by the controlling admin's app (CloudRosterService.UploadViaRestAsync); members watch it.
// Only Online (shared) teams have it: Local teams live on one phone and are never in Firestore.
const PHASES: Record<string, string> = { setup: "not started", firsthalf: "first half", first: "first half", halftime: "half time", half: "half time", secondhalf: "second half", second: "second half", ended: "ended" };
const DEFAULT_NAME = /^#\d{2} Player$/;

// "#NN Player" is the app's default name. Active players (field/goalie/bench) are ALWAYS returned, whatever
// their name; only INACTIVE default-named slots are treated as unused and hidden (counted as unusedSlots).
type RosterPlayer = { name: string; status: "goalie" | "field" | "bench" | "inactive"; cell?: number; row?: number; col?: number; secondsOnTimer?: number; defaultName?: boolean };
const isUnusedSlot = (p: RosterPlayer) => p.status === "inactive" && p.defaultName === true;

function shapePlayer(p: any): RosterPlayer {
  const status = p.goalie === true ? "goalie" : p.field === true ? "field" : p.inactive === true ? "inactive" : "bench";
  const out: RosterPlayer = { name: String(p.name ?? ""), status };
  const cell = Number(p.fieldCell ?? 0);
  if (status === "field" && cell >= 1 && cell <= 16) { out.cell = cell; out.row = Math.ceil(cell / 4); out.col = ((cell - 1) % 4) + 1; }
  if (typeof p.counterSeconds === "number") out.secondsOnTimer = p.counterSeconds;
  if (DEFAULT_NAME.test(out.name)) out.defaultName = true;
  return out;
}

async function loadRoster(ctx: ToolContext, t: string) {
  await requireMember(ctx, t);
  const d = await ctx.fs.getDoc(`teams/${t}/roster/data`);
  const players = Array.isArray(d?.data.players) ? (d!.data.players as any[]).map(shapePlayer) : [];
  return { d, players };
}

const NO_ROSTER_NOTE = "No roster saved in the cloud for this team. Only Online (shared) teams sync their roster/game; Local teams stay on the phone.";

export async function getRoster(ctx: ToolContext, t: string, includeUnnamed = false) {
  const { d, players } = await loadRoster(ctx, t);
  const list = players.filter(p => includeUnnamed || !isUnusedSlot(p)).map(({ name, status }) => ({ name, status }));
  return { teamId: t, exists: !!d, ...(d ? {} : { note: NO_ROSTER_NOTE }), lastModifiedUtc: d?.data.lastModifiedUtc ?? null, players: list,
    unusedSlots: includeUnnamed ? 0 : players.filter(isUnusedSlot).length,
    unusedSlotsNote: "Inactive slots still named '#NN Player' (the app default) are unused and hidden unless includeUnnamed is true. Active players are always listed, even with a default name." };
}

export async function getLineup(ctx: ToolContext, t: string) {
  const { d, players } = await loadRoster(ctx, t);
  if (!d) return { teamId: t, exists: false, note: NO_ROSTER_NOTE, onField: [], bench: [], inactive: [] };
  const g = d.data;
  const byCell = (a: RosterPlayer, b: RosterPlayer) => (a.cell ?? 99) - (b.cell ?? 99);

  const phaseRaw = String(g.currentHalf ?? "setup").toLowerCase();
  return {
    teamId: t,
    exists: true,
    game: {
      phase: PHASES[phaseRaw] ?? phaseRaw,
      timerRunning: g.timerRunning === true,
      matchRemainingSeconds: g.matchRemainingSeconds ?? null,
      score: { us: g.teamAScore ?? 0, them: g.teamBScore ?? 0 },
      rotationCount: g.rotationCount ?? null,
      controlledBy: (g.controllerDisplayName as string) || null,
      lastModifiedUtc: g.lastModifiedUtc ?? null,
    },
    gridNote: "Pitch is the app's 4x4 Field View grid: cell 1-16 row-major, row 1 = top row as shown in the app.",
    onField: players.filter(p => p.status === "goalie" || p.status === "field").sort(byCell),
    bench: players.filter(p => p.status === "bench"),
    inactive: players.filter(p => p.status === "inactive" && !isUnusedSlot(p)),
    unusedSlots: players.filter(isUnusedSlot).length,
    unusedSlotsNote: "Inactive slots still named '#NN Player' (the app default) are unused and only counted. Field, goalie and bench players are always listed; defaultName=true marks an app default name.",
  };
}

type Handler = (args: any) => Promise<unknown>;

export function makeWrap(ctx: ToolContext) {
  return (tool: string, fn: Handler, write = false) => async (args: any) => {
    const start = Date.now();
    try {
      const result: any = await fn(args);
      const count = result?.teams?.length ?? result?.messages?.length ?? result?.changes?.length ?? result?.players?.length ?? result?.onField?.length;
      audit({ uid: ctx.uid, tool, teamId: args?.teamId, ok: true, count, ms: Date.now() - start, ...(write ? { write: true } : {}) });
      return { content: [{ type: "text" as const, text: JSON.stringify(result, null, 2) }], structuredContent: result };
    } catch (e: any) {
      const msg = e instanceof AccessDenied || e instanceof ToolRefused ? e.message
        : e instanceof FirestoreError ? (e.status === 403 ? "Permission denied by TurfTimer rules" : `Firestore error ${e.status}`)
        : "Internal error";
      audit({ uid: ctx.uid, tool, teamId: args?.teamId, ok: false, error: msg.slice(0, 120), ms: Date.now() - start, ...(write ? { write: true } : {}) });
      return { isError: true, content: [{ type: "text" as const, text: msg }] };
    }
  };
}

export function registerTools(server: McpServer, ctx: ToolContext) {
  const wrap = makeWrap(ctx);
  const ro = { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false };

  server.registerTool("list_teams", { title: "List my teams", description: "Teams the signed-in user belongs to, with their role (admin/member).", inputSchema: {}, annotations: ro },
    wrap("list_teams", () => listTeams(ctx)));
  server.registerTool("get_messages_since", { title: "Team chat since", description: "Team chat messages newer than an ISO timestamp (oldest first, max 100). Deleted messages have text=null.",
    inputSchema: { teamId, since: isoSince, limit: z.number().int().min(1).max(100).optional() }, annotations: ro },
    wrap("get_messages_since", a => getMessagesSince(ctx, a.teamId, a.since, a.limit)));
  server.registerTool("get_kit", { title: "Kit", description: "Details → Kit for a team.", inputSchema: { teamId }, annotations: ro },
    wrap("get_kit", a => getKit(ctx, a.teamId)));
  server.registerTool("get_duties", { title: "Duties", description: "Details → Duties for a team.", inputSchema: { teamId }, annotations: ro },
    wrap("get_duties", a => getDuties(ctx, a.teamId)));
  server.registerTool("get_fixtures", { title: "Next match", description: "The team's scheduled match (date, time, arrive time, venue). TurfTimer currently stores only the next match.", inputSchema: { teamId }, annotations: ro },
    wrap("get_fixtures", a => getFixtures(ctx, a.teamId)));
  server.registerTool("get_changes_since", { title: "What changed", description: "Summary of team changes since an ISO timestamp: fixtures/kit/duties edits, team activity and new chat message count.",
    inputSchema: { teamId, since: isoSince }, annotations: ro },
    wrap("get_changes_since", a => getChangesSince(ctx, a.teamId, a.since)));
  server.registerTool("get_roster", { title: "Team roster", description: "Player names on an Online (shared) team's game roster, with status (goalie/field/bench/inactive). Names only; no contact details. Local-only teams are not stored in the cloud and cannot be read. Inactive slots still named with the app default ('#01 Player') are unused and hidden unless includeUnnamed is true; active players are always listed whatever their name.",
    inputSchema: { teamId, includeUnnamed: z.boolean().optional() }, annotations: ro },
    wrap("get_roster", a => getRoster(ctx, a.teamId, a.includeUnnamed)));
  server.registerTool("get_lineup", { title: "Current lineup", description: "Who is on the field now (with 4x4 pitch grid cell/row/col and goalie), who is on the bench (always all bench players, including default names like '#04 Player'), named inactive players and a count of unused inactive default slots, plus game phase, score, clock and last update, for an Online (shared) team. This is the latest state saved by the admin running the game; Local-only teams are not in the cloud.",
    inputSchema: { teamId }, annotations: ro },
    wrap("get_lineup", a => getLineup(ctx, a.teamId)));
}
