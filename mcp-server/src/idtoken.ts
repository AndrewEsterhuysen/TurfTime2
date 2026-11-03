/**
 * Gets a Firebase ID token FOR A UID THAT WE HAVE ALREADY AUTHENTICATED (via our OAuth grant), so
 * Firestore reads still go through firestore.rules exactly as the app's own reads do.
 * Admin createCustomToken(uid) -> Identity Toolkit signInWithCustomToken -> ID token (1h), cached.
 * The returned Firebase refresh token is discarded; the app's own anonymous session is untouched.
 * Requires: runtime SA can sign blobs (roles/iam.serviceAccountTokenCreator on itself) and the
 * project's Web API key (FIREBASE_WEB_API_KEY; not a secret, same as the app's config).
 */
export interface IdTokenSource { forUid(uid: string): Promise<string>; }

export async function firebaseIdTokenSource(projectId: string, apiKey: string): Promise<IdTokenSource> {
  const { initializeApp, getApps } = await import("firebase-admin/app");
  const { getAuth } = await import("firebase-admin/auth");
  const auth = getAuth(getApps()[0] ?? initializeApp({ projectId }));
  const cache = new Map<string, { t: string; exp: number }>();
  return {
    async forUid(uid) {
      const hit = cache.get(uid);
      if (hit && hit.exp > Date.now() + 300_000) return hit.t;
      const custom = await auth.createCustomToken(uid, { via: "mcp" });
      const r = await fetch(`https://identitytoolkit.googleapis.com/v1/accounts:signInWithCustomToken?key=${encodeURIComponent(apiKey)}`, {
        method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ token: custom, returnSecureToken: true }),
      });
      if (!r.ok) throw new Error(`signInWithCustomToken ${r.status}`);
      const j: any = await r.json();
      cache.set(uid, { t: j.idToken, exp: Date.now() + Number(j.expiresIn ?? 3600) * 1000 });
      if (cache.size > 5000) cache.delete(cache.keys().next().value!);
      return j.idToken;
    },
  };
}
