/**
 * Write tools (scope turftimer.write). Every write goes to Firestore AS THE SIGNED-IN USER (their
 * Firebase ID token over REST), so production firestore.rules still decide. On top of the rules the
 * server enforces the app's own roles: chat = any team member; kit, duties, next fixture and the
 * roster/lineup = team admins only (the rules let any member write roster/data, the app does not).
 *
 * Shapes mirror the app exactly (Services/ChatService.cs, TeamDetailsDocService.cs,
 * MatchScheduleService.cs, CloudRosterService.cs). Extra provenance fields are ones the app ignores:
 *   chat:   sentVia = "mcp:<client name>"
 *   others: editedVia = "mcp:<client name>", editedViaAtUtc (timestamp, equals lastModifiedUtc of that edit;
 *           a later app save leaves editedVia in place but moves lastModifiedUtc on, so compare the two)
 */
import { FirestoreError, FsDoc, Ts, ts } from "./firestore.js";
import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { AccessDenied, ToolContext, ToolRefused, requireMember, makeWrap } from "./tools.js";
import { RateLimiter } from "./ratelimit.js";
import { createClub, createTeam } from "./creates.js";

export class WriteRefused extends ToolRefused {}
export const SCOPE_WRITE_HINT =
  "This TurfTimer connection is read-only. To allow changes, disconnect and reconnect TurfTimer in your AI assistant, enter a new code from the app (Settings > Connect AI assistant) and choose 'Allow read and write'.";

// Per-uid write limits (per Cloud Run instance): writes in general, and chat posts separately.
let writeLimiter = new RateLimiter(Number(process.env.WRITE_RATE_CAPACITY ?? 20), Number(process.env.WRITE_RATE_PER_SEC ?? 0.2));
let chatLimiter = new RateLimiter(Number(process.env.CHAT_RATE_CAPACITY ?? 5), Number(process.env.CHAT_RATE_PER_SEC ?? 1 / 30));
export function setWriteLimiters(w: RateLimiter, c: RateLimiter) { writeLimiter = w; chatLimiter = c; }

export interface WriteContext extends ToolContext { canWrite?: boolean; clientName?: string; }

export const via = (ctx: WriteContext) => `mcp:${(ctx.clientName ?? "AI assistant").replace(/[\u0000-\u001f]/g, "").slice(0, 60)}`;
export const clean = (s: string) => s.replace(/\r\n/g, "\n").replace(/[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f]/g, "");

export function gateWrite(ctx: WriteContext, chat = false) {
  if (!ctx.canWrite) throw new WriteRefused(SCOPE_WRITE_HINT);
  if (!writeLimiter.take(ctx.uid) || (chat && !chatLimiter.take(ctx.uid)))
    throw new WriteRefused("Too many changes in a short time. Wait a minute and try again.");
}

async function requireAdmin(ctx: ToolContext, t: string): Promise<FsDoc> {
  const m = await requireMember(ctx, t);
  if (m.data.role !== "admin") throw new AccessDenied(`Only team admins can change this for team ${t}`);
  return m;
}
const displayName = (m: FsDoc) => {
  const n = String(m.data.displayName ?? "").trim();
  return (n || "Someone").slice(0, 40);
};

async function touchActivity(ctx: ToolContext, t: string) {
  // Same as CloudRosterService.TouchTeamLastActivityAsync: keeps cleanupDormantTeams away. Best effort.
  try {
    const meta = await ctx.fs.getDoc(`teams/${t}/metadata/info`);
    if (meta?.updateTime) await ctx.fs.patchDoc(`teams/${t}/metadata/info`, { lastActivityUtc: ts() }, { updateTime: meta.updateTime });
  } catch { /* non-fatal */ }
}

export function conflictWrap<T>(p: Promise<T>): Promise<T> {
  return p.catch(e => {
    if (e instanceof FirestoreError && e.conflict) throw new WriteRefused("Someone changed this at the same moment. Nothing was saved; read it again and retry.");
    throw e;
  });
}

// ---- Chat ----------------------------------------------------------------------------------------
export async function postChatMessage(ctx: WriteContext, t: string, text: string, replyToMessageId?: string) {
  gateWrite(ctx, true);
  const me = await requireMember(ctx, t);
  const body = clean(text).trim();
  if (!body) throw new WriteRefused("Message is empty.");
  if (body.length > 500) throw new WriteRefused("Message is longer than 500 characters (the app's limit).");
  // Identity comes ONLY from the verified token + the member doc, never from tool input.
  const msg: Record<string, unknown> = { text: body, userId: ctx.uid, senderName: displayName(me), timestamp: ts(), sentVia: via(ctx) };
  if (replyToMessageId) {
    const orig = await ctx.fs.getDoc(`teams/${t}/messages/${replyToMessageId}`);
    if (!orig) throw new WriteRefused("The message being replied to was not found.");
    msg.replyToMessageId = orig.id;
    if (orig.data.isDeleted !== true && orig.data.text) msg.replyToText = String(orig.data.text).slice(0, 120);
    if (orig.data.senderName) msg.replyToSenderName = String(orig.data.senderName).slice(0, 40);
  }
  const d = await ctx.fs.createDoc(`teams/${t}/messages`, msg);
  return { teamId: t, posted: true, messageId: d.id, senderName: msg.senderName, timestamp: (msg.timestamp as Ts).iso, sentVia: msg.sentVia };
}

// ---- Kit / Duties (teams/{t}/details/kit|duties) -----------------------------------------------
export const KIT_FIELDS = ["arrive", "warmUp", "game", "departure", "nonPlaying", "specialEvent"] as const;
export const DUTIES_FIELDS = ["officer", "canteen", "groundsSetup", "groundsPackup", "other"] as const;

async function updateDetails(ctx: WriteContext, t: string, doc: "kit" | "duties", allowed: readonly string[], input: Record<string, unknown>) {
  gateWrite(ctx);
  await requireAdmin(ctx, t);
  const changes: Record<string, unknown> = {};
  for (const k of allowed) if (typeof input[k] === "string") {
    const v = clean(input[k] as string).trim();
    if (v.length > 1000) throw new WriteRefused(`${k} is longer than 1000 characters.`);
    changes[k] = v;
  }
  if (!Object.keys(changes).length) throw new WriteRefused(`Give at least one of: ${allowed.join(", ")}.`);
  const path = `teams/${t}/details/${doc}`;
  const cur = await ctx.fs.getDoc(path);
  const now = ts();
  const data = { ...changes, lastModifiedUtc: now, updatedByUid: ctx.uid, editedVia: via(ctx), editedViaAtUtc: now };
  await conflictWrap(ctx.fs.patchDoc(path, data, cur?.updateTime ? { updateTime: cur.updateTime } : { exists: false }));
  return { teamId: t, updated: doc, fields: changes, lastModifiedUtc: now.iso };
}
export const updateKit = (ctx: WriteContext, t: string, a: Record<string, unknown>) => updateDetails(ctx, t, "kit", KIT_FIELDS, a);
export const updateDuties = (ctx: WriteContext, t: string, a: Record<string, unknown>) => updateDetails(ctx, t, "duties", DUTIES_FIELDS, a);

// ---- Next fixture (teams/{t}/details/location; MatchScheduleService) ----------------------------
const LOCATION_FIELDS = ["matchDate", "matchTime", "arriveTime", "locationName", "latitude", "longitude", "mapsLink"] as const;

function normTime(v: string, name: string): string {
  if (v === "") return "";
  const m = /^(\d{1,2}):(\d{2})(?::(\d{2}))?$/.exec(v.trim());
  if (!m || +m[1] > 23 || +m[2] > 59 || +(m[3] ?? 0) > 59) throw new WriteRefused(`${name} must be a 24-hour time like 09:30.`);
  return `${m[1].padStart(2, "0")}:${m[2]}:${m[3] ?? "00"}`; // .NET TimeSpan.ToString() format the app writes
}
function normDate(v: string): string {
  if (v === "") return "";
  const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(v.trim());
  const d = m ? new Date(Date.UTC(+m[1], +m[2] - 1, +m[3])) : null;
  if (!m || !d || d.getUTCMonth() !== +m[2] - 1 || d.getUTCDate() !== +m[3]) throw new WriteRefused("matchDate must be a real date like 2026-11-14.");
  return `${m[1]}-${m[2]}-${m[3]}`;
}
function normCoord(v: string, name: string, max: number): string {
  if (v === "") return "";
  const n = Number(v);
  if (!/^-?\d{1,3}(\.\d+)?$/.test(v.trim()) || Math.abs(n) > max) throw new WriteRefused(`${name} must be a number between -${max} and ${max}.`);
  return v.trim();
}

export async function updateNextFixture(ctx: WriteContext, t: string, a: Record<string, unknown>) {
  gateWrite(ctx);
  const me = await requireAdmin(ctx, t);
  const changes: Record<string, string> = {};
  for (const k of LOCATION_FIELDS) {
    if (typeof a[k] !== "string") continue;
    let v = clean(a[k] as string).trim();
    if (k === "matchDate") v = normDate(v);
    else if (k === "matchTime" || k === "arriveTime") v = normTime(v, k);
    else if (k === "latitude") v = normCoord(v, k, 90);
    else if (k === "longitude") v = normCoord(v, k, 180);
    else if (k === "mapsLink" && v && (!/^https:\/\//i.test(v) || v.length > 500)) throw new WriteRefused("mapsLink must be an https:// link (max 500 characters).");
    else if (k === "locationName" && v.length > 120) throw new WriteRefused("locationName is longer than 120 characters.");
    changes[k] = v;
  }
  if (!Object.keys(changes).length) throw new WriteRefused(`Give at least one of: ${LOCATION_FIELDS.join(", ")}.`);
  const path = `teams/${t}/details/location`;
  const cur = await ctx.fs.getDoc(path);
  const now = ts();
  // A new doc gets every field the app reads (empty strings), like MatchScheduleService's full upload.
  const base = cur ? {} : Object.fromEntries(LOCATION_FIELDS.map(k => [k, ""]));
  const data = { ...base, ...changes, schemaVersion: 1, lastModifiedUtc: now, updatedByUid: ctx.uid, updatedByDisplayName: displayName(me), editedVia: via(ctx), editedViaAtUtc: now };
  await conflictWrap(ctx.fs.patchDoc(path, data, cur?.updateTime ? { updateTime: cur.updateTime } : { exists: false }));
  const after = { ...(cur?.data ?? {}), ...base, ...changes };
  return { teamId: t, updated: "next fixture", fixture: Object.fromEntries(LOCATION_FIELDS.map(k => [k, after[k] ?? ""])), lastModifiedUtc: now.iso };
}

// ---- Roster / lineup (teams/{t}/roster/data; mirrors GameViewModel.SetPlayerPosition/PlaceOrSwapOnFieldCell) ----
export type PlayerStatus = "field" | "goalie" | "bench" | "inactive";
export interface PlayerChange { player: string | number; newName?: string; status?: PlayerStatus; cell?: number; }

type P = Record<string, unknown> & { slotId: number; name: string; field: boolean; bench: boolean; goalie: boolean; inactive: boolean; fieldCell: number };
const statusOf = (p: P): PlayerStatus => p.goalie ? "goalie" : p.field ? "field" : p.inactive ? "inactive" : "bench";
function setStatus(p: P, s: PlayerStatus, cell = 0) {
  p.field = s === "field"; p.goalie = s === "goalie"; p.bench = s === "bench"; p.inactive = s === "inactive";
  p.fieldCell = s === "field" ? cell : 0; // leaving Field clears the cell (Player.Position setter)
}

function findPlayer(players: P[], ref: string | number): P {
  // number or "#7" = slot number; otherwise exact name (case-insensitive), falling back to a bare slot number.
  const r = String(ref).trim();
  const slot = typeof ref === "number" ? ref : /^#\d{1,2}$/.test(r) ? Number(r.slice(1)) : null;
  if (slot !== null) {
    const p = players.find(x => x.slotId === slot);
    if (!p) throw new WriteRefused(`No player in slot #${slot}.`);
    return p;
  }
  const hits = players.filter(x => x.name.trim().toLowerCase() === r.toLowerCase());
  if (hits.length === 1) return hits[0];
  if (hits.length > 1) throw new WriteRefused(`More than one player is named "${r}"; use their slot number (${hits.map(h => "#" + h.slotId).join(", ")}).`);
  if (/^\d{1,2}$/.test(r)) { const p = players.find(x => x.slotId === Number(r)); if (p) return p; }
  throw new WriteRefused(`No player "${r}" on this roster. Use get_roster (includeUnnamed: true) to see names and slot numbers.`);
}

export async function updateRoster(ctx: WriteContext, t: string, changes: PlayerChange[]) {
  gateWrite(ctx);
  await requireAdmin(ctx, t);
  if (!Array.isArray(changes) || changes.length === 0 || changes.length > 16) throw new WriteRefused("Give 1 to 16 changes.");
  const path = `teams/${t}/roster/data`;
  const cur = await ctx.fs.getDoc(path);
  // A brand-new Online team (just created by the app or create_team) has version:2 and players:[].
  // Initialise the same 16 default bench slots GameViewModel builds on first open, so a club
  // manager can name players with update_roster without opening the Game screen first.
  const empty = !cur || !Array.isArray(cur.data.players) || !cur.data.players.length;
  const g = empty ? {} : cur.data;
  if (!empty) {
    // Game guard: never fight the controlling admin's phone (it saves every ~2 s and would overwrite us).
    const controller = String(g.controllerUid ?? "").trim();
    if (controller) throw new WriteRefused(`${String(g.controllerDisplayName || "An admin")} is running the game on their phone right now, so the lineup can't be changed from here. Change it in the app, or try again after the game is reset/finished (control is released automatically about a minute after their phone goes quiet).`);
    if (g.timerRunning === true) throw new WriteRefused("The match clock is running, so the lineup can't be changed from here. Pause or finish the game first.");
  }

  const players = (empty
    ? Array.from({ length: 16 }, (_, i) => ({ slotId: i + 1, name: `#${String(i + 1).padStart(2, "0")} Player`, field: false, bench: true, goalie: false, inactive: false, counterSeconds: 0, fieldCell: 0 }))
    : (g.players as any[]).map(p => ({ ...p, slotId: Number(p.slotId ?? 0), name: String(p.name ?? ""), field: p.field === true, bench: p.bench === true, goalie: p.goalie === true, inactive: p.inactive === true, fieldCell: Number(p.fieldCell ?? 0) }))
  ) as P[];
  const touched = new Set<number>();
  const log: string[] = [];
  for (const c of changes) {
    const p = findPlayer(players, c.player);
    if (c.newName !== undefined) {
      const n = clean(c.newName).trim();
      if (!n || n.length > 40) throw new WriteRefused("newName must be 1 to 40 characters.");
      if (n !== p.name) { log.push(`renamed "${p.name}" to "${n}"`); p.name = n; }
    }
    const status: PlayerStatus | undefined = c.status ?? (c.cell !== undefined ? "field" : undefined);
    if (c.cell !== undefined && status !== "field") throw new WriteRefused("cell only applies to status 'field'.");
    if (!status) continue;
    const before = statusOf(p);
    if (status === "goalie") {
      for (const o of players) if (o !== p && o.goalie) { setStatus(o, "bench"); touched.add(o.slotId); log.push(`${o.name} moved from goalie to bench`); }
      setStatus(p, "goalie");
    } else if (status === "field") {
      const target = c.cell ?? (before === "field" && p.fieldCell >= 1 ? p.fieldCell : (() => {
        const used = new Set(players.filter(o => o !== p && o.field).map(o => o.fieldCell));
        for (let i = 1; i <= 16; i++) if (!used.has(i)) return i;
        throw new WriteRefused("All 16 pitch cells are taken.");
      })());
      if (!Number.isInteger(target) || target < 1 || target > 16) throw new WriteRefused("cell must be 1 to 16 (4x4 grid, row by row from the top left).");
      const occupant = players.find(o => o !== p && o.field && o.fieldCell === target);
      const fromCell = before === "field" ? p.fieldCell : 0;
      setStatus(p, "field", target);
      if (occupant) {
        if (fromCell >= 1) { occupant.fieldCell = fromCell; log.push(`${occupant.name} swapped to cell ${fromCell}`); }
        else { setStatus(occupant, "bench"); log.push(`${occupant.name} moved from cell ${target} to bench`); }
        touched.add(occupant.slotId);
      }
    } else setStatus(p, status);
    touched.add(p.slotId);
    log.push(`${p.name}: ${before} -> ${statusOf(p)}${p.field ? ` (cell ${p.fieldCell})` : ""}`);
  }

  const now = ts();
  const data: Record<string, unknown> = { players, lastModifiedUtc: now, lastModified: now, editedVia: via(ctx), editedViaAtUtc: now };
  if (empty) data.version = 2;
  // Rotation "next on/off" queues hold slotIds; drop moved players (0 = empty slot, as the app does).
  for (const q of ["nextFieldSlotIds", "nextBenchSlotIds"]) {
    const arr = Array.isArray(g[q]) ? (g[q] as unknown[]).map(Number) : null;
    if (arr && arr.some(id => touched.has(id))) data[q] = arr.map(id => touched.has(id) ? 0 : id);
  }
  await conflictWrap(ctx.fs.patchDoc(path, data, cur?.updateTime ? { updateTime: cur.updateTime } : { exists: false }));
  await touchActivity(ctx, t);
  const shape = (p: P) => ({ slotId: p.slotId, name: p.name, status: statusOf(p), ...(p.field ? { cell: p.fieldCell } : {}) });
  return { teamId: t, updated: "roster", changes: log, players: players.map(shape), lastModifiedUtc: now.iso };
}

// ---- Registration --------------------------------------------------------------------------------
const teamId = z.string().min(1).max(128).regex(/^[^/]+$/, "teamId must not contain '/'");
const str = (max: number) => z.string().max(max);

export function registerWriteTools(server: McpServer, ctx: WriteContext) {
  const wrap = makeWrap(ctx);
  const w = { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false };
  const wi = { ...w, idempotentHint: true };
  const note = " Needs a read-and-write connection (scope turftimer.write); Online (shared) teams only.";
  server.registerTool("post_chat_message", {
    title: "Post to team chat",
    description: "Post a message to the team chat AS the signed-in user (their own name; any team member can post). Teammates get the usual push notification. Max 500 characters. Optionally reply to a message id from get_messages_since." + note,
    inputSchema: { teamId, text: z.string().min(1).max(2000), replyToMessageId: z.string().min(1).max(128).regex(/^[^/]+$/).optional() }, annotations: w,
  }, wrap("post_chat_message", a => postChatMessage(ctx, a.teamId, a.text, a.replyToMessageId), true));
  server.registerTool("update_kit", {
    title: "Update kit (admin)",
    description: "Team admins: change Details > Kit. Only the fields you pass change; pass \"\" to clear one." + note,
    inputSchema: { teamId, arrive: str(1000).optional(), warmUp: str(1000).optional(), game: str(1000).optional(), departure: str(1000).optional(), nonPlaying: str(1000).optional(), specialEvent: str(1000).optional() }, annotations: wi,
  }, wrap("update_kit", a => updateKit(ctx, a.teamId, a), true));
  server.registerTool("update_duties", {
    title: "Update duties (admin)",
    description: "Team admins: change Details > Duties. Only the fields you pass change; pass \"\" to clear one." + note,
    inputSchema: { teamId, officer: str(1000).optional(), canteen: str(1000).optional(), groundsSetup: str(1000).optional(), groundsPackup: str(1000).optional(), other: str(1000).optional() }, annotations: wi,
  }, wrap("update_duties", a => updateDuties(ctx, a.teamId, a), true));
  server.registerTool("update_next_fixture", {
    title: "Update next match (admin)",
    description: "Team admins: change the team's next match (the app stores only one). matchDate YYYY-MM-DD; matchTime/arriveTime 24-hour HH:mm; latitude/longitude decimal; mapsLink https URL. Only the fields you pass change; pass \"\" to clear one." + note,
    inputSchema: { teamId, matchDate: str(10).optional(), matchTime: str(8).optional(), arriveTime: str(8).optional(), locationName: str(120).optional(), latitude: str(20).optional(), longitude: str(20).optional(), mapsLink: str(500).optional() }, annotations: wi,
  }, wrap("update_next_fixture", a => updateNextFixture(ctx, a.teamId, a), true));
  server.registerTool("update_roster", {
    title: "Edit roster / lineup (admin)",
    description: "Team admins: rename players, set status (field, goalie, bench, inactive) and place players on the 4x4 pitch grid (cell 1-16, row by row from top left), applied together in one save. Follows the app's rules: a new goalie sends the old goalie to the bench; moving onto an occupied cell swaps with a field player or sends the occupant to the bench. Identify a player by name or by slot number (e.g. 4 or \"#4\"). Refused while an admin is running the game on their phone or the clock is running. On a brand-new team whose roster is still empty (just created, Game screen not opened), this initialises the app's 16 default bench slots (\"#01 Player\"…\"#16 Player\") and then applies your changes — use slot numbers 1-16 to name players. Or pass players to create_team." + note,
    inputSchema: { teamId, changes: z.array(z.object({
      player: z.union([z.string().min(1).max(60), z.number().int().min(1).max(99)]),
      newName: z.string().min(1).max(40).optional(),
      status: z.enum(["field", "goalie", "bench", "inactive"]).optional(),
      cell: z.number().int().min(1).max(16).optional(),
    })).min(1).max(16) }, annotations: w,
  }, wrap("update_roster", a => updateRoster(ctx, a.teamId, a.changes), true));
  server.registerTool("create_club", {
    title: "Create a club",
    description: "Create a TurfTimer club with you as Owner (the same as Create Club in the app). Returns clubId and a Club Owner Recovery Code — save that code, it is not shown again. Spreadsheet workflow for a club manager: call create_club once, then create_team with this clubId (and the team name) for each team in the sheet, optionally passing players, or call update_roster afterwards. Needs a read-and-write connection. At most 20 clubs and teams combined per connection per day.",
    inputSchema: { clubName: z.string().min(1).max(80), displayName: z.string().min(2).max(40).optional() }, annotations: w,
  }, wrap("create_club", a => createClub(ctx, a), true));
  server.registerTool("create_team", {
    title: "Create a team",
    description: "Create an Online team with you as admin, identical to one made in the app. Pass clubId to add the team to a club you Own or Admin (create the club first with create_club). Omit clubId and pass name (the team's nickname) for a standalone team. Returns teamId, an invite code (for members to join) and a Team Owner Recovery Code — save that code, it is not shown again. Optional players (1-16 names) fills the roster in the same call (16 bench slots, named players first, the rest '#NN Player'); otherwise the roster is empty like the app and you can fill it with update_roster. Spreadsheet workflow: create_club, then create_team for each row, then players. Needs a read-and-write connection. At most 20 clubs and teams combined per connection per day.",
    inputSchema: {
      name: z.string().min(1).max(80).describe("Team nickname (standalone) or team name (when clubId is set)"),
      clubId: z.string().min(1).max(128).regex(/^[^/]+$/, "clubId must not contain '/'").optional(),
      displayName: z.string().min(2).max(40).optional(),
      creatorEmail: z.string().max(200).optional(),
      players: z.array(z.string().min(1).max(40)).min(1).max(16).optional(),
    }, annotations: w,
  }, wrap("create_team", a => createTeam(ctx, a), true));
}
