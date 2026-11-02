import express from "express";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StreamableHTTPServerTransport } from "@modelcontextprotocol/sdk/server/streamableHttp.js";
import { bearer, TokenVerifier } from "./auth.js";
import { FirestoreUserClient, FetchLike } from "./firestore.js";
import { RateLimiter } from "./ratelimit.js";
import { registerTools } from "./tools.js";

export interface AppOptions { projectId: string; verify: TokenVerifier; fetchImpl?: FetchLike; limiter?: RateLimiter; }

export function createApp(o: AppOptions) {
  const app = express();
  app.use(express.json({ limit: "64kb" }));
  const limiter = o.limiter ?? new RateLimiter(Number(process.env.RATE_CAPACITY ?? 60), Number(process.env.RATE_PER_SEC ?? 1));
  const ipLimiter = new RateLimiter(120, 2);

  app.get("/healthz", (_q, r) => { r.json({ ok: true }); });

  // Stateless Streamable HTTP: a fresh server + transport per POST (no session state to leak between users).
  app.post("/mcp", async (req, res) => {
    if (!ipLimiter.take(req.ip ?? "?")) return void res.status(429).json({ error: "rate_limited" });
    const token = bearer(req.header("authorization"));
    if (!token) return void res.status(401).set("WWW-Authenticate", 'Bearer realm="turftimer-mcp"').json({ error: "missing_bearer" });
    let uid: string;
    try { uid = (await o.verify(token)).uid; }
    catch { return void res.status(401).set("WWW-Authenticate", 'Bearer error="invalid_token"').json({ error: "invalid_token" }); }
    if (!limiter.take(uid)) return void res.status(429).set("Retry-After", "5").json({ error: "rate_limited" });

    const server = new McpServer({ name: "turftimer", version: "0.1.0" });
    registerTools(server, { uid, fs: new FirestoreUserClient(token, o.projectId, o.fetchImpl) });
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
