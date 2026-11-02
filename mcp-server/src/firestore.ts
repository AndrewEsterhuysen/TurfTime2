/**
 * Minimal Firestore REST client that acts AS THE CALLER: every request carries the user's
 * Firebase ID token, so firestore.rules decide what they can read (same path the mobile app
 * already uses in ChatService / TeamDetailsDocService). Read-only: only GET and :runQuery.
 */
export type FetchLike = (url: string, init?: { method?: string; headers?: Record<string, string>; body?: string }) =>
  Promise<{ ok: boolean; status: number; json(): Promise<any>; text(): Promise<string> }>;

export class FirestoreError extends Error {
  constructor(public status: number, message: string) { super(message); }
}

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
}

function encPath(p: string) { return p.split("/").map(encodeURIComponent).join("/"); }
