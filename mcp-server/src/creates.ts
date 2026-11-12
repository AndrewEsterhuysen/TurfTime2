/**
 * create_club / create_team (scope turftimer.write). Writes go to Firestore AS THE SIGNED-IN USER,
 * in the same order and shape as the app (Services/CloudTeamService.cs CreateClubWithTeamAsync /
 * CreateTeamUnderClubAsync / CreateNicknameTeamAsync, and Pages/TeamDetailsPage.xaml.cs generators).
 *
 * Club (caller becomes Owner, the app's club-admin role):
 *   clubs/{clubId}/metadata/info  — clubName, createdBy, createdAt, isActive, ownerRecoveryCodeHash
 *   clubs/{clubId}/members/{uid}  — role "owner", displayName, addedAt
 *   clubId is "club-{slug}-{6 char}" like GenerateTeamId("club", clubName).
 *
 * Standalone (nickname) team:
 *   teams/{id}/metadata/info — teamName, inviteCode, adminCodeHash, creatorEmail, createdBy, kind "nickname",
 *                              isActive, createdAt, lastActivityUtc
 *   teams/{id}/members/{uid} — role "admin", displayName, uid   (makes the creator admin; Select Team finds this)
 *   teams/{id}/roster/data   — version 2, players []            (the app writes an empty list at create)
 *   invite_codes/{compact}   — teamId, teamName, clubId, clubName, inviteCode, inviteCodeCompact, createdBy
 *   teams/{id}/public/invite — same payload (optional in the app; non-fatal)
 *
 * Team under a club (caller must be that club's Owner or Club Admin — same as CreateTeamUnderClubAsync):
 *   same team docs, plus metadata.clubId, clubName and kind "clubbed"; teamId is "{team}-{club}-{6 char}".
 *
 * Sequential writes, not one batch: roster rules use exists(members/{uid}), which sees the pre-batch state.
 * Production rules already allow all of this (any signed-in user may create a nickname team or a club;
 * a clubbed team's metadata create requires isClubOwnerOrAdmin). No rules change.
 */
import { createHash, randomInt } from "crypto";
import { FirestoreError, ts } from "./firestore.js";
import { DailyCap } from "./ratelimit.js";
import { listTeams } from "./tools.js";
import { WriteContext, WriteRefused, gateWrite, clean, conflictWrap } from "./writes.js";

const DEFAULT_CREATE_CAP = 20;
let createCap = new DailyCap(Number(process.env.CREATE_DAILY_CAP ?? DEFAULT_CREATE_CAP));
export function setCreateCap(c: DailyCap) { createCap = c; }
const capHint = () => `You've already created ${createCap.limit} clubs or teams today through this connection. Try again tomorrow.`;

const ID_CHARS = "abcdefghijklmnopqrstuvwxyz0123456789";
const CODE_CHARS = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // same as the app: no 0,O,1,I
const PLACEHOLDERS = new Set(["Admin", "Member", "Someone", "Teammate"]);

function shortId(): string {
  let s = "";
  for (let i = 0; i < 6; i++) s += ID_CHARS[randomInt(ID_CHARS.length)];
  return s;
}
function slugify(...parts: string[]): string {
  const base = parts.join("-").toLowerCase().replace(/[_\s]+/g, "-").replace(/[^a-z0-9-]/g, "")
    .replace(/-+/g, "-").replace(/^-|-$/g, "");
  return base.length > 40 ? base.slice(0, 40).replace(/-$/, "") : base;
}
/** Same as TeamDetailsPage.GenerateTeamId: slug of the parts plus a 6-char suffix. */
function generateId(...parts: string[]): string {
  return `${slugify(...parts) || "team"}-${shortId()}`;
}
function generateInviteCode(): string {
  let c = "";
  for (let i = 0; i < 8; i++) {
    c += CODE_CHARS[randomInt(CODE_CHARS.length)];
    if (i === 3) c += "-";
  }
  return c;
}
/** 16-character owner recovery code, format XXXX-XXXX-XXXX-XXXX, same charset as the app. */
function generateAdminCode(): string {
  const seg = () => { let s = ""; for (let i = 0; i < 4; i++) s += CODE_CHARS[randomInt(CODE_CHARS.length)]; return s; };
  return [seg(), seg(), seg(), seg()].join("-");
}
/** SHA-256 hex (lowercase) of the uppercase-normalised code — TeamDetailsPage.HashAdminCode. */
export function hashAdminCode(code: string): string {
  return createHash("sha256").update(code.toUpperCase(), "utf8").digest("hex");
}
function compactInvite(code: string): string {
  return [...code].filter(ch => /[A-Za-z0-9]/.test(ch)).join("");
}

function gateCreate(ctx: WriteContext) {
  gateWrite(ctx);
  if (createCap.atLimit(ctx.uid)) throw new WriteRefused(capHint());
}
function recordCreate(ctx: WriteContext) {
  createCap.take(ctx.uid);
}

function normName(raw: unknown, label: string): string {
  const n = clean(String(raw ?? "")).trim();
  if (!n) throw new WriteRefused(`Give a ${label} (it cannot be empty).`);
  if (n.length > 80) throw new WriteRefused(`${label} is longer than 80 characters.`);
  return n;
}
function normEmail(raw: unknown): string {
  const e = clean(String(raw ?? "")).trim();
  if (!e) return "";
  if (e.length > 200 || !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(e))
    throw new WriteRefused("creatorEmail must be a valid email address, or omitted.");
  return e;
}
function normDisplay(raw: unknown): string | undefined {
  if (raw === undefined || raw === null) return undefined;
  const n = clean(String(raw)).trim();
  if (n.length < 2 || n.length > 40)
    throw new WriteRefused("displayName must be 2 to 40 characters, so teammates know who you are in Chat.");
  if (PLACEHOLDERS.has(n))
    throw new WriteRefused("Please choose a personal displayName (not a role like Admin or Member).");
  return n;
}

async function resolveDisplayName(ctx: WriteContext, explicit?: string): Promise<string> {
  const given = normDisplay(explicit);
  if (given) return given;
  try {
    const { teams } = await listTeams(ctx);
    for (const t of teams) {
      const n = String(t.displayName ?? "").trim();
      if (n.length >= 2 && n.length <= 40 && !PLACEHOLDERS.has(n)) return n;
    }
  } catch { /* fall through */ }
  throw new WriteRefused("Give a displayName (2–40 characters, a personal name) so teammates know who you are in Chat.");
}

async function writeNew(ctx: WriteContext, path: string, data: Record<string, unknown>): Promise<void> {
  await conflictWrap(ctx.fs.patchDoc(path, data, { exists: false }));
}
async function freshId(ctx: WriteContext, kind: "team" | "club", ...parts: string[]): Promise<string> {
  for (let i = 0; i < 6; i++) {
    const id = kind === "club" ? generateId("club", ...parts) : generateId(...parts);
    const path = kind === "club" ? `clubs/${id}/metadata/info` : `teams/${id}/metadata/info`;
    const existing = await ctx.fs.getDoc(path).catch(() => null);
    if (!existing) return id;
  }
  throw new WriteRefused("Could not allocate a unique id. Try again.");
}
async function freshInvite(ctx: WriteContext): Promise<string> {
  for (let i = 0; i < 8; i++) {
    const code = generateInviteCode();
    const existing = await ctx.fs.getDoc(`invite_codes/${compactInvite(code)}`).catch(() => null);
    if (!existing) return code;
  }
  throw new WriteRefused("Could not allocate a unique invite code. Try again.");
}

const SAVE_TEAM = "Save the Team Owner Recovery Code now in a password manager; it is not shown again. Members join with the invite code. The team appears under Select Team in the app for you as admin (pull to refresh).";
const SAVE_CLUB = "Save the Club Owner Recovery Code now in a password manager; it is not shown again. Next, call create_team with this clubId for each team, then add players with the players argument or update_roster.";
const SPREADSHEET = "Spreadsheet workflow: create_club once, then create_team (clubId + name, optional players) for each row, or create_team with only a name for a standalone nickname team. After create, use update_roster to name or place players (a brand-new empty roster is initialised to the app's 16 default bench slots).";

export async function createClub(ctx: WriteContext, a: { clubName: string; displayName?: string }) {
  gateCreate(ctx);
  const clubName = normName(a.clubName, "club name");
  const displayName = await resolveDisplayName(ctx, a.displayName);
  const clubId = await freshId(ctx, "club", clubName);
  const ownerRecoveryCode = generateAdminCode();
  const now = ts();
  try {
    await writeNew(ctx, `clubs/${clubId}/metadata/info`, {
      clubName, createdBy: ctx.uid, createdAt: now, isActive: true,
      ownerRecoveryCodeHash: hashAdminCode(ownerRecoveryCode),
    });
    await writeNew(ctx, `clubs/${clubId}/members/${ctx.uid}`, {
      role: "owner", displayName, addedAt: now,
    });
  } catch (e) {
    if (e instanceof WriteRefused) throw e;
    if (e instanceof FirestoreError && e.status === 403)
      throw new WriteRefused("Permission denied by TurfTimer rules while creating the club.");
    throw e;
  }
  recordCreate(ctx);
  return {
    clubId, clubName, role: "owner", isOwner: true,
    ownerRecoveryCode,
    note: `${SAVE_CLUB} ${SPREADSHEET}`,
  };
}

async function requireClubOwnerOrAdmin(ctx: WriteContext, clubId: string) {
  const meta = await ctx.fs.getDoc(`clubs/${clubId}/metadata/info`).catch(e => {
    if (e instanceof FirestoreError && e.status === 403) return null;
    throw e;
  });
  if (!meta) throw new WriteRefused(`Club "${clubId}" was not found.`);
  const clubName = String(meta.data.clubName ?? "").trim();
  if (meta.data.createdBy === ctx.uid) return { clubName, role: "owner" as const };
  const mem = await ctx.fs.getDoc(`clubs/${clubId}/members/${ctx.uid}`).catch(e => {
    if (e instanceof FirestoreError && (e.status === 403 || e.status === 404)) return null;
    throw e;
  });
  const role = String(mem?.data.role ?? "");
  if (role === "owner" || role === "admin") return { clubName, role: role as "owner" | "admin" };
  throw new WriteRefused("Only the club Owner or a Club Admin can create teams under this club.");
}

export type CreateTeamArgs = {
  name: string;
  clubId?: string;
  displayName?: string;
  creatorEmail?: string;
  players?: string[];
};

function defaultSlot(slotId: number, name?: string) {
  return {
    slotId, name: name ?? `#${String(slotId).padStart(2, "0")} Player`,
    field: false, bench: true, goalie: false, inactive: false, counterSeconds: 0, fieldCell: 0,
  };
}
/** 16-slot bench roster like GameViewModel's first open of the Game screen, with the given names in slots 1..n. */
export function seededRoster(names: string[], now = ts()) {
  if (names.length < 1 || names.length > 16) throw new WriteRefused("Give 1 to 16 player names.");
  const cleaned: string[] = [];
  for (const raw of names) {
    const n = clean(raw).trim();
    if (!n || n.length > 40) throw new WriteRefused("Each player name must be 1 to 40 characters.");
    cleaned.push(n);
  }
  const players = Array.from({ length: 16 }, (_, i) => defaultSlot(i + 1, cleaned[i]));
  return {
    version: 2,
    lastModifiedUtc: now,
    lastModified: now,
    matchDurationSeconds: 90 * 60,
    halfDurationSeconds: 0,
    matchRemainingSeconds: 90 * 60,
    currentHalf: "setup",
    timerRunning: false,
    countdownPresetSeconds: 0,
    countdownPreset: 0,
    countdownRemainingSeconds: 0,
    viewMode: 2, // TeamViewMode.Field — a new/reset roster in the app starts on Field View
    teamAScore: 0,
    teamBScore: 0,
    rotationCount: 1,
    lastFieldSlotId: 0,
    lastBenchSlotId: 0,
    nextFieldSlotIds: [] as number[],
    nextBenchSlotIds: [] as number[],
    controllerUid: "",
    controllerDisplayName: "",
    players,
  };
}

export async function createTeam(ctx: WriteContext, a: CreateTeamArgs) {
  gateCreate(ctx);
  const teamName = normName(a.name, "team name");
  const creatorEmail = normEmail(a.creatorEmail);
  const displayName = await resolveDisplayName(ctx, a.displayName);
  const clubId = typeof a.clubId === "string" ? a.clubId.trim() : "";
  if (clubId && clubId.includes("/")) throw new WriteRefused("clubId must not contain '/'.");

  let clubName = "";
  let kind: "nickname" | "clubbed" = "nickname";
  if (clubId) {
    const club = await requireClubOwnerOrAdmin(ctx, clubId);
    clubName = club.clubName;
    kind = "clubbed";
  }

  const teamId = await freshId(ctx, "team", ...(kind === "clubbed" ? [teamName, clubName] : [teamName]));
  const inviteCode = await freshInvite(ctx);
  const ownerRecoveryCode = generateAdminCode();
  const now = ts();
  const compact = compactInvite(inviteCode);

  const meta: Record<string, unknown> = {
    teamName, inviteCode, adminCodeHash: hashAdminCode(ownerRecoveryCode),
    creatorEmail, createdBy: ctx.uid, kind, isActive: true, createdAt: now, lastActivityUtc: now,
  };
  if (kind === "clubbed") { meta.clubId = clubId; meta.clubName = clubName; }

  const invitePayload = {
    teamId, teamName, clubId: clubId || "", clubName, inviteCode, inviteCodeCompact: compact, createdBy: ctx.uid,
  };

  const roster = a.players?.length
    ? { ...seededRoster(a.players, now), editedVia: `mcp:${(ctx.clientName ?? "AI assistant").replace(/[\u0000-\u001f]/g, "").slice(0, 60)}`, editedViaAtUtc: now }
    : { version: 2, players: [] as unknown[] };

  try {
    // Same order as CreateTeamCoreAsync: metadata, members, roster, then invite index (non-fatal).
    await writeNew(ctx, `teams/${teamId}/metadata/info`, meta);
    await writeNew(ctx, `teams/${teamId}/members/${ctx.uid}`, { role: "admin", displayName, uid: ctx.uid });
    await writeNew(ctx, `teams/${teamId}/roster/data`, roster);
    try { await writeNew(ctx, `invite_codes/${compact}`, invitePayload); } catch (e) {
      if (!(e instanceof FirestoreError) && !(e instanceof WriteRefused)) throw e;
    }
    if (compact !== inviteCode) {
      try { await writeNew(ctx, `invite_codes/${inviteCode}`, invitePayload); } catch { /* non-fatal, same as the app */ }
    }
    try { await writeNew(ctx, `teams/${teamId}/public/invite`, invitePayload); } catch { /* optional in the app */ }
  } catch (e) {
    if (e instanceof WriteRefused) throw e;
    if (e instanceof FirestoreError && e.status === 403)
      throw new WriteRefused("Permission denied by TurfTimer rules while creating the team.");
    throw e;
  }
  recordCreate(ctx);

  const named = a.players?.length ? (roster as ReturnType<typeof seededRoster>).players.filter(p => !/^#\d{2} Player$/.test(p.name)).map(p => ({ slotId: p.slotId, name: p.name, status: "bench" as const })) : undefined;
  return {
    teamId, teamName, kind, role: "admin", isOwner: true,
    ...(kind === "clubbed" ? { clubId, clubName } : {}),
    inviteCode, ownerRecoveryCode,
    ...(named ? { players: named, unusedSlots: 16 - named.length } : { players: [], noteRoster: "Roster is empty, as when the app creates a team. Pass players to create_team, or call update_roster (it will initialise the 16 default bench slots) — you do not need to open the Game screen first." }),
    note: `${SAVE_TEAM} ${SPREADSHEET}`,
  };
}
