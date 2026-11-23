/** Tiny fake of the Firestore REST endpoints used by the server (GET doc, POST :runQuery, POST create, PATCH + precondition). */
import { decodeFields, type FetchLike } from "../src/firestore.js";

const ROOT = "projects/turf-timer/databases/(default)/documents";
type Val = string | number | boolean | null | Val[] | { [k: string]: Val };
const enc = (v: Val): any => typeof v === "string" ? (/^\d{4}-\d\d-\d\dT/.test(v) ? { timestampValue: v } : { stringValue: v })
  : typeof v === "number" ? { integerValue: String(v) } : typeof v === "boolean" ? { booleanValue: v }
  : Array.isArray(v) ? { arrayValue: { values: v.map(enc) } }
  : v && typeof v === "object" ? { mapValue: { fields: Object.fromEntries(Object.entries(v).map(([k, x]) => [k, enc(x)])) } }
  : { nullValue: null };

export function fakeFirestore(docs: Record<string, Record<string, Val>>, tokens: Record<string, string>) {
  const calls: { method: string; url: string; body?: any }[] = [];
  let clock = 1;
  const times: Record<string, string> = {};
  const tOf = (p: string) => (times[p] ??= `2026-01-01T00:00:00.${String(clock++).padStart(6, "0")}Z`);
  const raw = (p: string) => ({ name: `${ROOT}/${p}`, updateTime: tOf(p), fields: Object.fromEntries(Object.entries(docs[p]).map(([k, v]) => [k, enc(v)])) });
  const bump = (p: string) => { times[p] = `2026-01-01T00:00:00.${String(clock++).padStart(6, "0")}Z`; };
  const reply = (status: number, body: any) => ({ ok: status < 300, status, json: async () => body, text: async () => JSON.stringify(body) });
  const f: FetchLike = async (url, init) => {
    const method = init?.method ?? "GET";
    calls.push({ method, url, ...(init?.body ? { body: JSON.parse(init.body) } : {}) });
    const uid = tokens[(init?.headers?.Authorization ?? "").replace("Bearer ", "")];
    if (!uid) return reply(401, {});
    let [pathPart, qs] = (url.split(`${ROOT}`)[1] ?? "").split("?");
    let commitWrite: any = null;
    if (url.endsWith(":commit")) {
      // Translate a single-write :commit into the PATCH handling below.
      commitWrite = JSON.parse(init!.body!).writes[0];
      pathPart = "/" + String(commitWrite.update.name).slice(ROOT.length + 1);
      const cd = commitWrite.currentDocument ?? {};
      qs = new URLSearchParams([...commitWrite.updateMask.fieldPaths.map((f: string) => ["updateMask.fieldPaths", f]),
        ...("updateTime" in cd ? [["currentDocument.updateTime", cd.updateTime]] : []), ...("exists" in cd ? [["currentDocument.exists", String(cd.exists)]] : [])]).toString();
    }
    const rel = decodeURIComponent(pathPart).replace(/^\//, "");
    const teamM = /^teams\/([^/]+)\//.exec(rel);
    const isMember = (t: string) => !!docs[`teams/${t}/members/${uid}`];
    if (commitWrite || method === "PATCH" || (method === "POST" && !url.endsWith(":runQuery"))) {
      // Write rule mirror (coarse), including bootstrap creates that production rules allow:
      //   metadata/info of a nickname team or (if club Owner/Admin) a clubbed team;
      //   self member doc; public/invite by createdBy; invite_codes by createdBy;
      //   club metadata (createdBy == uid) and self club-member doc.
      // Roster still requires membership, so create_team must write members before roster (same as the app).
      const data = decodeFields(commitWrite ? commitWrite.update.fields : JSON.parse(init!.body!).fields) as Record<string, Val>;
      const clubOwnerOrAdmin = (clubId: string) => {
        const meta = docs[`clubs/${clubId}/metadata/info`];
        if (meta && meta.createdBy === uid) return true;
        const mem: any = docs[`clubs/${clubId}/members/${uid}`];
        return !!mem && (mem.role === "owner" || mem.role === "admin");
      };
      if (rel.startsWith("invite_codes/")) {
        if (data.createdBy !== uid) return reply(403, {});
      } else if (rel.startsWith("clubs/")) {
        const clubMeta = /^clubs\/([^/]+)\/metadata\/info$/.exec(rel);
        const clubMem = /^clubs\/([^/]+)\/members\/([^/]+)$/.exec(rel);
        if (clubMeta) { if (data.createdBy !== uid) return reply(403, {}); }
        else if (clubMem) { if (clubMem[2] !== uid) return reply(403, {}); }
        else return reply(403, {});
      } else if (teamM && !isMember(teamM[1])) {
        const isMeta = /^teams\/[^/]+\/metadata\/info$/.test(rel);
        const selfMem = /^teams\/[^/]+\/members\/([^/]+)$/.exec(rel);
        const isPublic = /^teams\/[^/]+\/public\//.test(rel);
        const clubId = typeof data.clubId === "string" ? data.clubId : "";
        const okMeta = isMeta && data.createdBy === uid && (!clubId || clubOwnerOrAdmin(clubId));
        const okMem = !!(selfMem && selfMem[1] === uid);
        const okPub = isPublic && data.createdBy === uid;
        if (!okMeta && !okMem && !okPub) return reply(403, {});
      }
      if (method === "POST" && !commitWrite) {
        if (rel.endsWith("/messages") && data.userId !== uid) return reply(403, {});
        const id = `auto${clock++}`; const p = `${rel}/${id}`;
        docs[p] = data; bump(p); return reply(200, raw(p));
      }
      const params = new URLSearchParams(qs ?? "");
      const ut = params.get("currentDocument.updateTime"), ex = params.get("currentDocument.exists");
      if (ut && (!docs[rel] || tOf(rel) !== ut)) return reply(400, { error: { status: "FAILED_PRECONDITION" } });
      if (ex === "false" && docs[rel]) return reply(409, { error: { status: "ALREADY_EXISTS" } });
      if (ex === "true" && !docs[rel]) return reply(404, {});
      const mask = params.getAll("updateMask.fieldPaths");
      docs[rel] = { ...(docs[rel] ?? {}) };
      for (const k of mask) { if (k in data) docs[rel][k] = data[k]; else delete docs[rel][k]; }
      bump(rel); return reply(200, raw(rel));
    }
    if (method !== "GET" && !url.endsWith(":runQuery")) return reply(405, {});
    if (url.endsWith(":runQuery")) {
      const parent = rel.replace(/:runQuery$/, "");
      const q = JSON.parse(init!.body!).structuredQuery;
      const coll = q.from[0].collectionId, all = q.from[0].allDescendants;
      let rows = Object.keys(docs).filter(p => {
        const segs = p.split("/");
        if (segs[segs.length - 2] !== coll) return false;
        return all ? true : segs.slice(0, -2).join("/") === parent;
      });
      const ff = q.where?.fieldFilter;
      if (ff) {
        const fv: any = Object.values(ff.value)[0];
        rows = rows.filter(p => {
          const v = docs[p][ff.field.fieldPath];
          return ff.op === "EQUAL" ? v === fv : typeof v === "string" && Date.parse(v) > Date.parse(fv);
        });
        if (all && ff.field.fieldPath === "uid" && fv !== uid) return reply(403, {}); // rule mirror
      }
      if (q.orderBy) rows.sort((a, b) => Date.parse(String(docs[a].timestamp)) - Date.parse(String(docs[b].timestamp)));
      return reply(200, rows.slice(0, q.limit ?? 1000).map(p => ({ document: raw(p) })));
    }
    // rule mirror: members/details need membership; others signedIn
    const m = /^teams\/([^/]+)\/(members|details)\//.exec(rel);
    if (m && !docs[`teams/${m[1]}/members/${uid}`] && !(m[2] === "members" && rel.endsWith(`/${uid}`))) return reply(403, {});
    return docs[rel] ? reply(200, raw(rel)) : reply(404, {});
  };
  return { fetch: f, calls, docs, bump: (p: string) => bump(p) };
}
