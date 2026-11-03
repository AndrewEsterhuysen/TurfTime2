/**
 * Server-only OAuth state. Firestore collections mcp_<kind> (rules: read/write false; Admin SDK only).
 * MemoryStore is used by tests and local dev.
 */
export type Kind = "clients" | "auth_requests" | "auth_codes" | "grants" | "link_codes";
export type Rec = Record<string, any> & { expiresAt?: number };

export interface Store {
  get(kind: Kind, id: string): Promise<Rec | null>;
  set(kind: Kind, id: string, rec: Rec): Promise<void>;
  delete(kind: Kind, id: string): Promise<void>;
  /** Atomic read-and-delete (single-use codes). */
  take(kind: Kind, id: string): Promise<Rec | null>;
  listBy(kind: Kind, field: string, value: string): Promise<Array<Rec & { id: string }>>;
}

const live = (r: Rec | null | undefined) => (r && (r.expiresAt == null || r.expiresAt > Date.now()) ? r : null);

export class MemoryStore implements Store {
  private m = new Map<string, Rec>();
  private k(kind: Kind, id: string) { return `${kind}/${id}`; }
  async get(kind: Kind, id: string) { return live(this.m.get(this.k(kind, id))) ? { ...this.m.get(this.k(kind, id))! } : null; }
  async set(kind: Kind, id: string, rec: Rec) { this.m.set(this.k(kind, id), { ...rec }); }
  async delete(kind: Kind, id: string) { this.m.delete(this.k(kind, id)); }
  async take(kind: Kind, id: string) { const r = await this.get(kind, id); this.m.delete(this.k(kind, id)); return r; }
  async listBy(kind: Kind, field: string, value: string) {
    const out: Array<Rec & { id: string }> = [];
    for (const [key, r] of this.m) if (key.startsWith(kind + "/") && live(r) && r[field] === value) out.push({ ...r, id: key.slice(kind.length + 1) });
    return out;
  }
}

export async function firestoreStore(projectId: string): Promise<Store> {
  const { initializeApp, getApps } = await import("firebase-admin/app");
  const { getFirestore } = await import("firebase-admin/firestore");
  const db = getFirestore(getApps()[0] ?? initializeApp({ projectId }));
  const col = (k: Kind) => db.collection(`mcp_${k}`);
  // expiresAt is stored as epoch ms and also as a Date in ttl (configure a Firestore TTL policy on "ttl").
  return {
    async get(k, id) { const s = await col(k).doc(id).get(); return live(s.exists ? (s.data() as Rec) : null); },
    async set(k, id, rec) { await col(k).doc(id).set({ ...rec, ...(rec.expiresAt ? { ttl: new Date(rec.expiresAt) } : {}) }); },
    async delete(k, id) { await col(k).doc(id).delete(); },
    async take(k, id) {
      return db.runTransaction(async tx => {
        const ref = col(k).doc(id); const s = await tx.get(ref);
        if (!s.exists) return null; tx.delete(ref); return live(s.data() as Rec);
      });
    },
    async listBy(k, field, value) {
      const q = await col(k).where(field, "==", value).limit(100).get();
      return q.docs.map(d => ({ ...(d.data() as Rec), id: d.id })).filter(r => live(r));
    },
  };
}
