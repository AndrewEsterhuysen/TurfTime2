/**
 * Minimal Firestore REST client that acts AS THE CALLER: every request carries the user's
 * Firebase ID token, so firestore.rules decide what they can read (same path the mobile app
 * already uses in ChatService / TeamDetailsDocService). Reads: GET and :runQuery. Writes (write tools
 * only, scope turftimer.write): POST create and PATCH with an updateMask and a precondition, exactly
 * like the app's REST writes, so production rules decide.
 */
export type FetchLike = (url: string, init?: { method?: string; headers?: Record<string, string>; body?: string }) =>
  Promise<{ ok: boolean; status: number; json(): Promise<any>; text(): Promise<string> }>;

export class FirestoreError extends Error {
  constructor(public status: number, message: string, public body = "") { super(message); }
  /** Precondition failed (document changed since we read it, or exists/doesn't exist). */
  get conflict() { return this.status === 409 || this.status === 412 || /FAILED_PRECONDITION|ABORTED|ALREADY_EXISTS/.test(this.body); }
}

/** Marks a value to be written as a Firestore timestampValue (ISO-8601 string). */
export class Ts { constructor(readonly iso: string) {} }
export const ts = (d: Date | string = new Date()) => new Ts(typeof d === "string" ? d : d.toISOString());

export function encodeValue(v: unknown): any {
  if (v instanceof Ts) return { timestampValue: v.iso };
  if (v === null || v === undefined) return { nullValue: null };
  if (typeof v === "string") return { stringValue: v };
  if (typeof v === "boolean") return { booleanValue: v };
  if (typeof v === "number") return Number.isInteger(v) ? { integerValue: String(v) } : { doubleValue: v };
  if (Array.isArray(v)) return { arrayValue: { values: v.map(encodeValue) } };
  if (typeof v === "object") return { mapValue: { fields: encodeFields(v as Record<string, unknown>) } };
  throw new Error("unsupported value");
}
export function encodeFields(o: Record<string, unknown>): Record<string, any> {
  return Object.fromEntries(Object.entries(o).map(([k, v]) => [k, encodeValue(v)]));
}

export type Precondition = { updateTime: string } | { exists: boolean };

export interface FsDoc { id: string; path: string; updateTime?: string; data: Record<string, unknown>; }

export function decodeValue(v: any): unknown {
  if (v == null) return null;
  if ("stringValue" in v) return v.stringValue;
  if ("integerValue" in v) return Number(v.integerValue);
  if ("doubleValue" in v) return v.doubleValue;
  if ("booleanValue" in v) return v.booleanValue;
  if ("timestampValue" in v) return v.timestampValue;
  if ("nullValue" in v) return null;
  if ("referenceValue" in v) return v.referenceValue;
  if ("geoPointValue" in v) return v.geoPointValue;
  if ("bytesValue" in v) return "[bytes]";
  if ("arrayValue" in v) return (v.arrayValue.values ?? []).map(decodeValue);
  if ("mapValue" in v) return decodeFields(v.mapValue.fields ?? {});
  return null;
}

export function decodeFields(fields: Record<string, any>): Record<string, unknown> {
  const out: Record<string, unknown> = {};
  for (const [k, v] of Object.entries(fields ?? {})) out[k] = decodeValue(v);
  return out;
}

function toDoc(raw: any, root: string): FsDoc {
  const name: string = raw.name;
  const path = name.startsWith(root + "/") ? name.slice(root.length + 1) : name;
  return { id: path.split("/").pop()!, path, updateTime: raw.updateTime, data: decodeFields(raw.fields ?? {}) };
}

export class FirestoreUserClient {
  readonly root: string;
  private readonly base: string;

  constructor(private idToken: string, projectId: string, private fetchImpl: FetchLike = fetch as any,
              baseUrl = process.env.FIRESTORE_REST_BASE ?? "https://firestore.googleapis.com/v1") {
    this.root = `projects/${projectId}/databases/(default)/documents`;
    this.base = `${baseUrl}/${this.root}`;
  }

  private headers() {
    return { Authorization: `Bearer ${this.idToken}`, "Content-Type": "application/json" };
  }

  /** Returns null for 404; throws FirestoreError(403) when rules deny. */
  async getDoc(path: string): Promise<FsDoc | null> {
    const res = await this.fetchImpl(`${this.base}/${encPath(path)}`, { method: "GET", headers: this.headers() });
    if (res.status === 404) return null;
    if (!res.ok) throw new FirestoreError(res.status, `get ${path}: ${res.status}`);
    return toDoc(await res.json(), this.root);
  }

  /** structuredQuery under parentPath ("" = database root). POST :runQuery is a read. */
  async runQuery(parentPath: string, structuredQuery: unknown): Promise<FsDoc[]> {
    const url = parentPath ? `${this.base}/${encPath(parentPath)}:runQuery` : `${this.base}:runQuery`;
    const res = await this.fetchImpl(url, { method: "POST", headers: this.headers(), body: JSON.stringify({ structuredQuery }) });
    if (!res.ok) throw new FirestoreError(res.status, `query ${parentPath || "/"}: ${res.status}`);
    const rows: any[] = await res.json();
    return rows.filter(r => r.document).map(r => toDoc(r.document, this.root));
  }

  /** Create a document with an auto id under collectionPath (same as the app's chat POST). */
  async createDoc(collectionPath: string, data: Record<string, unknown>): Promise<FsDoc> {
    const res = await this.fetchImpl(`${this.base}/${encPath(collectionPath)}`, { method: "POST", headers: this.headers(), body: JSON.stringify({ fields: encodeFields(data) }) });
    if (!res.ok) { const t = await res.text().catch(() => ""); throw new FirestoreError(res.status, `create in ${collectionPath}: ${res.status}`, t); }
    return toDoc(await res.json(), this.root);
  }

  /**
   * Update only the given top-level fields (updateMask), guarded by a precondition, via :commit
   * (one write; same rules evaluation as the app's SDK writes). Returns nothing on success.
   */
  async patchDoc(path: string, data: Record<string, unknown>, pre: Precondition): Promise<void> {
    const write = {
      update: { name: `${this.root}/${path}`, fields: encodeFields(data) },
      updateMask: { fieldPaths: Object.keys(data) },
      currentDocument: pre,
    };
    const res = await this.fetchImpl(`${this.base}:commit`, { method: "POST", headers: this.headers(), body: JSON.stringify({ writes: [write] }) });
    if (!res.ok) { const t = await res.text().catch(() => ""); throw new FirestoreError(res.status, `write ${path}: ${res.status}`, t); }
  }
}

function encPath(p: string) { return p.split("/").map(encodeURIComponent).join("/"); }
