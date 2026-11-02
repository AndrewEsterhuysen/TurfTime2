/**
 * Bearer auth: the client sends a Firebase ID token (Authorization: Bearer <idToken>).
 * We verify it (signature/audience/expiry) with firebase-admin's verifyIdToken — verification
 * only, no Admin data access — and then forward the SAME token to Firestore REST.
 */
export interface Caller { uid: string; idToken: string; }
export type TokenVerifier = (token: string) => Promise<{ uid: string }>;

export async function defaultVerifier(projectId: string): Promise<TokenVerifier> {
  const { initializeApp, getApps } = await import("firebase-admin/app");
  const { getAuth } = await import("firebase-admin/auth");
  const app = getApps()[0] ?? initializeApp({ projectId });
  return async (t) => { const d = await getAuth(app).verifyIdToken(t, process.env.CHECK_REVOKED === "1"); return { uid: d.uid }; };
}

export function bearer(header: string | undefined): string | null {
  const m = /^Bearer\s+(.+)$/i.exec(header ?? "");
  return m ? m[1].trim() : null;
}
