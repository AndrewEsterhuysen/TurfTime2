import express from "express";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StreamableHTTPServerTransport } from "@modelcontextprotocol/sdk/server/streamableHttp.js";
import { bearer, TokenVerifier } from "./auth.js";
import { FirestoreUserClient, FetchLike } from "./firestore.js";
import { RateLimiter } from "./ratelimit.js";
import { registerTools } from "./tools.js";
import { oauthRouter } from "./oauth/router.js";
import { Store } from "./oauth/store.js";
import { TokenSigner, SCOPE_READ, SCOPE_WRITE, SCOPE_ALL, hasScope } from "./oauth/tokens.js";
import { registerWriteTools } from "./writes.js";
import { IdTokenSource } from "./idtoken.js";

export interface AppOptions {
  projectId: string;
  baseUrl: string;                 // public https origin, e.g. https://mcp.turftimer.app
  store: Store;
  signer: TokenSigner;
  verifyFirebase: TokenVerifier;   // verifies TurfTimer app Firebase ID tokens (for /link/*)
  idTokens: IdTokenSource;         // Firebase ID token for an already-authorized uid
  fetchImpl?: FetchLike;
  limiter?: RateLimiter;
  oauthLimiter?: RateLimiter;
  ipLimiter?: RateLimiter;
  linkLimiter?: RateLimiter;
  /** Dev only: also accept raw Firebase ID tokens on /mcp (ALLOW_FIREBASE_ID_TOKENS=1). */
  allowFirebaseIdTokens?: boolean;
  /** Tests only: raw Firebase ID tokens also get write access. */
  allowFirebaseIdTokenWrites?: boolean;
}

export function createApp(o: AppOptions) {
  const app = express();
  app.set("trust proxy", 1); // Cloud Run front end
  app.use(express.json({ limit: "64kb" }));
  const limiter = o.limiter ?? new RateLimiter(Number(process.env.RATE_CAPACITY ?? 60), Number(process.env.RATE_PER_SEC ?? 1));
  const ipLimiter = o.ipLimiter ?? new RateLimiter(120, 2);
  const grantSeen = new Map<string, { at: number; clientName: string }>(); // grantId -> checkedAt (30s cache so revocation applies within 30s)

  // Cloud Run's front end reserves /healthz, so /health is the public check; /healthz kept for local use.
  app.get(["/health", "/healthz"], (_q, r) => { r.json({ ok: true }); });
  app.use(oauthRouter({ baseUrl: o.baseUrl, store: o.store, signer: o.signer, verifyFirebase: o.verifyFirebase, ipLimiter: o.oauthLimiter, linkLimiter: o.linkLimiter }));

  const challenge = (extra = "") =>
    `Bearer resource_metadata="${o.baseUrl}/.well-known/oauth-protected-resource", scope="${SCOPE_ALL}"${extra}`;

  type Who = { uid: string; idToken: string; canWrite: boolean; clientName: string };
  async function identify(token: string): Promise<Who | "insufficient_scope" | null> {
    try {
      const c = await o.signer.verify(token);
      if (!hasScope(c.scope, SCOPE_READ)) return "insufficient_scope";
      let seen = grantSeen.get(c.grantId);
      if (!seen || Date.now() - seen.at > 30_000) {
        const g = await o.store.get("grants", c.grantId);
        if (!g || g.uid !== c.uid) { grantSeen.delete(c.grantId); return null; }
        // Write needs BOTH the token and the stored grant to carry it (old read-only grants never write).
        if (hasScope(c.scope, SCOPE_WRITE) && !hasScope(String(g.scope ?? ""), SCOPE_WRITE)) return "insufficient_scope";
        seen = { at: Date.now(), clientName: String(g.clientName ?? "AI assistant") };
        grantSeen.set(c.grantId, seen);
        if (grantSeen.size > 10_000) grantSeen.delete(grantSeen.keys().next().value!);
      }
      return { uid: c.uid, idToken: await o.idTokens.forUid(c.uid), canWrite: hasScope(c.scope, SCOPE_WRITE), clientName: seen.clientName };
    } catch { /* not ours */ }
    if (o.allowFirebaseIdTokens) {
      try { const { uid } = await o.verifyFirebase(token); return { uid, idToken: token, canWrite: !!o.allowFirebaseIdTokenWrites, clientName: "dev token" }; } catch { /* fallthrough */ }
    }
    return null;
  }

  // Stateless Streamable HTTP: a fresh server + transport per POST (no session state to leak between users).
  app.post("/mcp", async (req, res) => {
    if (!ipLimiter.take(req.ip ?? "?")) return void res.status(429).json({ error: "rate_limited" });
    const token = bearer(req.header("authorization"));
    if (!token) return void res.status(401).set("WWW-Authenticate", challenge()).json({ error: "unauthorized" });
    const who = await identify(token);
    if (who === "insufficient_scope") return void res.status(403).set("WWW-Authenticate", challenge(', error="insufficient_scope"')).json({ error: "insufficient_scope" });
    if (!who) return void res.status(401).set("WWW-Authenticate", challenge(', error="invalid_token"')).json({ error: "invalid_token" });
    if (!limiter.take(who.uid)) return void res.status(429).set("Retry-After", "5").json({ error: "rate_limited" });

    const server = new McpServer({ name: "turftimer", version: "0.3.0" });
    const ctx = { uid: who.uid, fs: new FirestoreUserClient(who.idToken, o.projectId, o.fetchImpl), canWrite: who.canWrite, clientName: who.clientName };
    registerTools(server, ctx);
    registerWriteTools(server, ctx);
    const transport = new StreamableHTTPServerTransport({ sessionIdGenerator: undefined, enableJsonResponse: true });
    res.on("close", () => { transport.close(); server.close(); });
    try {
      await server.connect(transport);
      await transport.handleRequest(req, res, req.body);
    } catch {
      if (!res.headersSent) res.status(500).json({ jsonrpc: "2.0", error: { code: -32603, message: "Internal error" }, id: null });
    }
  });
  const notAllowed = (_q: express.Request, r: express.Response) => { r.status(405).set("Allow", "POST").json({ error: "method_not_allowed" }); };
  app.get("/mcp", notAllowed);
  app.delete("/mcp", notAllowed);
  return app;
}
