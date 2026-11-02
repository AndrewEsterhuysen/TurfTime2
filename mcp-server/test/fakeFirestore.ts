/** Tiny fake of the Firestore REST endpoints used by the server (GET doc, POST :runQuery). */
import type { FetchLike } from "../src/firestore.js";

const ROOT = "projects/turf-timer/databases/(default)/documents";
type Val = string | number | boolean | null;
const enc = (v: Val): any => typeof v === "string" ? (/^\d{4}-\d\d-\d\dT/.test(v) ? { timestampValue: v } : { stringValue: v })
  : typeof v === "number" ? { integerValue: String(v) } : typeof v === "boolean" ? { booleanValue: v } : { nullValue: null };

export function fakeFirestore(docs: Record<string, Record<string, Val>>, tokens: Record<string, string>) {
  const calls: { method: string; url: string }[] = [];
  const raw = (p: string) => ({ name: `${ROOT}/${p}`, fields: Object.fromEntries(Object.entries(docs[p]).map(([k, v]) => [k, enc(v)])) });
  const reply = (status: number, body: any) => ({ ok: status < 300, status, json: async () => body, text: async () => JSON.stringify(body) });
  const f: FetchLike = async (url, init) => {
    calls.push({ method: init?.method ?? "GET", url });
    if (init?.method !== "GET" && !url.endsWith(":runQuery")) return reply(405, {});
    const uid = tokens[(init?.headers?.Authorization ?? "").replace("Bearer ", "")];
    if (!uid) return reply(401, {});
    const rel = decodeURIComponent(url.split(`${ROOT}`)[1] ?? "").replace(/^\//, "");
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
  return { fetch: f, calls };
}
