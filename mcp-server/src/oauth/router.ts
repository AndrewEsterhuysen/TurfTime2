/**
 * OAuth 2.1 authorization server + protected-resource metadata for remote MCP
 * (MCP authorization spec 2025-06-18 / 2025-11-25): RFC 9728, RFC 8414, RFC 7591 DCR, PKCE S256,
 * RFC 8707 resource indicators, RFC 7009 revocation.
 *
 * IDENTITY: TurfTimer users are anonymous Firebase users, so there is no username/password to type.
 * The user proves who they are with a one-time LINK CODE generated inside the app (Settings ->
 * Connect AI assistant), which calls POST /link/code with its own Firebase ID token. The AI client
 * never supplies a user name or uid; the uid comes only from that verified app session.
 */
import express from "express";
import { Store } from "./store.js";
import { isAllowedRedirect, redirectMatches } from "./redirects.js";
import { rand, sha256, safeEq, pkceS256, linkCode, normLinkCode } from "./crypto.js";
import { TokenSigner, SCOPE_READ, ACCESS_TTL_SEC, REFRESH_TTL_MS } from "./tokens.js";
import { RateLimiter } from "../ratelimit.js";
import type { TokenVerifier } from "../auth.js";
import { bearer } from "../auth.js";

export interface OAuthOptions { baseUrl: string; store: Store; signer: TokenSigner; verifyFirebase: TokenVerifier; ipLimiter?: RateLimiter; }

const LINK_TTL_MS = 10 * 60_000, CODE_TTL_MS = 5 * 60_000, REQ_TTL_MS = 15 * 60_000, MAX_LINK_TRIES = 5;
const esc = (s: unknown) => String(s ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]!));
const oerr = (res: express.Response, status: number, error: string, desc?: string) => void res.status(status).set("Cache-Control", "no-store").json({ error, ...(desc ? { error_description: desc } : {}) });

function page(res: express.Response, title: string, body: string, status = 200) {
  res.status(status).set({
    "Content-Type": "text/html; charset=utf-8", "Cache-Control": "no-store", "X-Frame-Options": "DENY",
    "Content-Security-Policy": "default-src 'none'; style-src 'unsafe-inline'; form-action 'self' https: http://127.0.0.1:* http://localhost:*; frame-ancestors 'none'",
    "Referrer-Policy": "no-referrer",
  }).send(`<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>${esc(title)}</title>
<style>body{font-family:system-ui,sans-serif;max-width:440px;margin:40px auto;padding:0 16px;color:#123}h1{font-size:22px}.box{background:#f2f6f2;border-radius:10px;padding:12px 14px;margin:14px 0}
input{font-size:22px;letter-spacing:3px;text-transform:uppercase;width:100%;padding:10px;box-sizing:border-box}button{font-size:17px;padding:10px 18px;margin:12px 8px 0 0;border-radius:8px;border:0;background:#1b6b35;color:#fff}button.no{background:#888}.err{color:#b00}small{color:#567}</style></head><body>${body}</body></html>`);
}

function parseScope(s: unknown): string | null {
  const req = String(s ?? SCOPE_READ).split(/\s+/).filter(Boolean);
  if (req.length === 0) return SCOPE_READ;
  return req.every(x => x === SCOPE_READ) ? SCOPE_READ : null;
}

function clientAuth(req: express.Request): { id?: string; secret?: string } {
  const b = /^Basic\s+(.+)$/i.exec(req.header("authorization") ?? "");
  if (b) {
    const [id, secret] = Buffer.from(b[1], "base64").toString().split(":");
    return { id: decodeURIComponent(id ?? ""), secret: decodeURIComponent(secret ?? "") };
  }
  return { id: req.body?.client_id, secret: req.body?.client_secret };
}

export function oauthRouter(o: OAuthOptions) {
  const r = express.Router();
  const resource = `${o.baseUrl}/mcp`;
  const ipLimit = o.ipLimiter ?? new RateLimiter(Number(process.env.OAUTH_RATE_CAPACITY ?? 30), 0.5);
  const limited = (req: express.Request, res: express.Response) => { if (ipLimit.take(req.ip ?? "?")) return false; oerr(res, 429, "slow_down"); return true; };
  r.use(express.urlencoded({ extended: false, limit: "16kb" }));

  // ---- Discovery -------------------------------------------------------------------------
  const prm = { resource, authorization_servers: [o.baseUrl], scopes_supported: [SCOPE_READ], bearer_methods_supported: ["header"], resource_name: "TurfTimer (read-only)" };
  r.get(["/.well-known/oauth-protected-resource", "/.well-known/oauth-protected-resource/mcp"], (_q, s) => void s.json(prm));
  const asm = {
    issuer: o.baseUrl,
    authorization_endpoint: `${o.baseUrl}/oauth/authorize`,
    token_endpoint: `${o.baseUrl}/oauth/token`,
    registration_endpoint: `${o.baseUrl}/oauth/register`,
    revocation_endpoint: `${o.baseUrl}/oauth/revoke`,
    response_types_supported: ["code"],
    grant_types_supported: ["authorization_code", "refresh_token"],
    code_challenge_methods_supported: ["S256"],
    token_endpoint_auth_methods_supported: ["none", "client_secret_post", "client_secret_basic"],
    revocation_endpoint_auth_methods_supported: ["none", "client_secret_post", "client_secret_basic"],
    scopes_supported: [SCOPE_READ],
    service_documentation: "https://github.com/AndrewEsterhuysen/TurfTime2/blob/feature/mcp-api/docs/MCP-API.md",
  };
  r.get(["/.well-known/oauth-authorization-server", "/.well-known/openid-configuration"], (_q, s) => void s.json(asm));

  // ---- RFC 7591 Dynamic Client Registration ----------------------------------------------
  r.post("/oauth/register", express.json({ limit: "16kb" }), async (req, res) => {
    if (limited(req, res)) return;
    const b = req.body ?? {};
    const uris: unknown = b.redirect_uris;
    if (!Array.isArray(uris) || uris.length === 0 || uris.length > 10 || !uris.every(u => typeof u === "string"))
      return oerr(res, 400, "invalid_redirect_uri", "redirect_uris required");
    const bad = (uris as string[]).filter(u => !isAllowedRedirect(u));
    if (bad.length) return oerr(res, 400, "invalid_redirect_uri", `redirect_uri not allowed: ${bad[0]}`);
    const method = b.token_endpoint_auth_method ?? "client_secret_post";
    if (!["none", "client_secret_post", "client_secret_basic"].includes(method)) return oerr(res, 400, "invalid_client_metadata", "unsupported token_endpoint_auth_method");
    const gt: string[] = b.grant_types ?? ["authorization_code", "refresh_token"];
    if (!gt.every(g => ["authorization_code", "refresh_token"].includes(g))) return oerr(res, 400, "invalid_client_metadata", "unsupported grant_types");
    if (b.response_types && !(b.response_types as string[]).every(x => x === "code")) return oerr(res, 400, "invalid_client_metadata", "unsupported response_types");
    const name = String(b.client_name ?? "AI assistant").slice(0, 80);
    const id = `mcpc_${rand(18)}`;
    // Confidential by default (Copilot Studio requires a secret); public clients use PKCE only.
    const secret = method === "none" ? undefined : `mcps_${rand(32)}`;
    const now = Math.floor(Date.now() / 1000);
    await o.store.set("clients", id, { name, redirectUris: uris, method, secretHash: secret ? sha256(secret) : null, createdAt: Date.now() });
    res.status(201).set("Cache-Control", "no-store").json({
      client_id: id, client_id_issued_at: now, ...(secret ? { client_secret: secret, client_secret_expires_at: 0 } : {}),
      client_name: name, redirect_uris: uris, token_endpoint_auth_method: method,
      grant_types: ["authorization_code", "refresh_token"], response_types: ["code"], scope: SCOPE_READ,
    });
  });

  // ---- Authorize (GET: show page; POST: link code + consent) -------------------------------
  r.get("/oauth/authorize", async (req, res) => {
    const q = req.query as Record<string, string>;
    const client = q.client_id ? await o.store.get("clients", q.client_id) : null;
    // Never redirect to an unverified redirect_uri: show an error page instead.
    if (!client) return page(res, "Error", `<h1>Unknown app</h1><p class="err">This connection request has an unknown client_id.</p>`, 400);
    if (!q.redirect_uri || !redirectMatches(client.redirectUris, q.redirect_uri))
      return page(res, "Error", `<h1>Invalid redirect</h1><p class="err">redirect_uri is not registered for this client.</p>`, 400);
    const back = (err: string, desc: string) => {
      const u = new URL(q.redirect_uri); u.searchParams.set("error", err); u.searchParams.set("error_description", desc);
      if (q.state) u.searchParams.set("state", q.state); u.searchParams.set("iss", o.baseUrl); res.redirect(302, u.toString());
    };
    if (q.response_type !== "code") return back("unsupported_response_type", "only code");
    if (!q.code_challenge || q.code_challenge_method !== "S256") return back("invalid_request", "PKCE S256 required");
    const scope = parseScope(q.scope);
    if (!scope) return back("invalid_scope", `only ${SCOPE_READ}`);
    if (q.resource && q.resource.replace(/\/$/, "") !== resource && q.resource.replace(/\/$/, "") !== o.baseUrl) return back("invalid_target", "unknown resource");
    const reqId = rand(24);
    await o.store.set("auth_requests", reqId, {
      clientId: q.client_id, redirectUri: q.redirect_uri, state: q.state ?? null, scope,
      challenge: q.code_challenge, tries: 0, expiresAt: Date.now() + REQ_TTL_MS,
    });
    page(res, "Connect TurfTimer", consentHtml(reqId, client.name, q.redirect_uri));
  });

  r.post("/oauth/authorize", async (req, res) => {
    if (limited(req, res)) return;
    const reqId = String(req.body?.request_id ?? "");
    const ar = reqId ? await o.store.get("auth_requests", reqId) : null;
    if (!ar) return page(res, "Expired", `<h1>Request expired</h1><p>Start the connection again from your AI assistant.</p>`, 400);
    const client = await o.store.get("clients", ar.clientId);
    if (!client) return page(res, "Error", `<h1>Unknown app</h1>`, 400);
    const finish = (params: Record<string, string>) => {
      const u = new URL(ar.redirectUri);
      for (const [k, v] of Object.entries(params)) u.searchParams.set(k, v);
      if (ar.state) u.searchParams.set("state", ar.state);
      u.searchParams.set("iss", o.baseUrl);
      res.redirect(303, u.toString());
    };
    if (req.body?.action !== "allow") { await o.store.delete("auth_requests", reqId); return finish({ error: "access_denied" }); }

    const codeId = sha256(normLinkCode(String(req.body?.link_code ?? "")));
    const link = await o.store.take("link_codes", codeId);
    if (!link) {
      ar.tries = (ar.tries ?? 0) + 1;
      if (ar.tries >= MAX_LINK_TRIES) { await o.store.delete("auth_requests", reqId); return finish({ error: "access_denied", error_description: "too many attempts" }); }
      await o.store.set("auth_requests", reqId, ar);
      return page(res, "Connect TurfTimer", consentHtml(reqId, client.name, ar.redirectUri, "That code is wrong or has expired. Get a new one in the app."), 400);
    }
    await o.store.delete("auth_requests", reqId);
    const code = rand(32);
    await o.store.set("auth_codes", sha256(code), {
      uid: link.uid, clientId: ar.clientId, redirectUri: ar.redirectUri, scope: ar.scope, challenge: ar.challenge, expiresAt: Date.now() + CODE_TTL_MS,
    });
    finish({ code });
  });

  // ---- Token -----------------------------------------------------------------------------
  r.post("/oauth/token", express.json({ limit: "16kb" }), async (req, res) => {
    if (limited(req, res)) return;
    const b = req.body ?? {};
    const ca = clientAuth(req);
    const client = ca.id ? await o.store.get("clients", ca.id) : null;
    if (!client) return oerr(res, 401, "invalid_client");
    if (client.secretHash && !(ca.secret && safeEq(sha256(ca.secret), client.secretHash))) return oerr(res, 401, "invalid_client");

    if (b.grant_type === "authorization_code") {
      const ac = b.code ? await o.store.take("auth_codes", sha256(String(b.code))) : null;
      if (!ac || ac.clientId !== ca.id) return oerr(res, 400, "invalid_grant");
      if (b.redirect_uri && b.redirect_uri !== ac.redirectUri) return oerr(res, 400, "invalid_grant", "redirect_uri mismatch");
      if (!b.code_verifier || !safeEq(pkceS256(String(b.code_verifier)), ac.challenge)) return oerr(res, 400, "invalid_grant", "PKCE verification failed");
      const grantId = rand(16);
      const rt = rand(32);
      await o.store.set("grants", grantId, {
        uid: ac.uid, clientId: ca.id, clientName: client.name, scope: ac.scope, rtHash: sha256(rt), prevRtHash: null,
        createdAt: Date.now(), lastUsedAt: Date.now(), expiresAt: Date.now() + REFRESH_TTL_MS,
      });
      return issue(res, { uid: ac.uid, scope: ac.scope, clientId: ca.id!, grantId }, `${grantId}.${rt}`);
    }
    if (b.grant_type === "refresh_token") {
      const [grantId, rt] = String(b.refresh_token ?? "").split(".");
      const g = grantId && rt ? await o.store.get("grants", grantId) : null;
      if (!g || g.clientId !== ca.id) return oerr(res, 400, "invalid_grant");
      const h = sha256(rt);
      if (g.prevRtHash && safeEq(h, g.prevRtHash)) { await o.store.delete("grants", grantId); return oerr(res, 400, "invalid_grant", "refresh token reuse"); }
      if (!safeEq(h, g.rtHash)) return oerr(res, 400, "invalid_grant");
      const scope = b.scope ? parseScope(b.scope) : g.scope;
      if (!scope) return oerr(res, 400, "invalid_scope");
      const next = rand(32);
      await o.store.set("grants", grantId, { ...g, rtHash: sha256(next), prevRtHash: h, lastUsedAt: Date.now(), expiresAt: Date.now() + REFRESH_TTL_MS });
      return issue(res, { uid: g.uid, scope, clientId: ca.id!, grantId }, `${grantId}.${next}`);
    }
    oerr(res, 400, "unsupported_grant_type");
  });

  async function issue(res: express.Response, c: { uid: string; scope: string; clientId: string; grantId: string }, refresh: string) {
    res.set({ "Cache-Control": "no-store", Pragma: "no-cache" }).json({
      access_token: await o.signer.sign(c), token_type: "Bearer", expires_in: ACCESS_TTL_SEC, refresh_token: refresh, scope: c.scope,
    });
  }

  // ---- RFC 7009 revocation (refresh or access token; revokes the whole grant) ------------
  r.post("/oauth/revoke", express.json({ limit: "16kb" }), async (req, res) => {
    if (limited(req, res)) return;
    const ca = clientAuth(req);
    const client = ca.id ? await o.store.get("clients", ca.id) : null;
    if (client?.secretHash && !(ca.secret && safeEq(sha256(ca.secret), client.secretHash))) return oerr(res, 401, "invalid_client");
    const t = String(req.body?.token ?? "");
    let grantId: string | undefined;
    if (t.includes(".") && t.split(".").length === 2) grantId = t.split(".")[0];
    else { try { grantId = (await o.signer.verify(t)).grantId; } catch { /* unknown token: still 200 */ } }
    if (grantId) { const g = await o.store.get("grants", grantId); if (g && (!ca.id || g.clientId === ca.id)) await o.store.delete("grants", grantId); }
    res.status(200).end();
  });

  // ---- App-facing endpoints (Firebase ID token from the TurfTimer app) -------------------
  const appUid = async (req: express.Request) => { const t = bearer(req.header("authorization")); if (!t) throw new Error("no"); return (await o.verifyFirebase(t)).uid; };
  const uidLimit = new RateLimiter(10, 0.05);
  r.post("/link/code", async (req, res) => {
    let uid: string; try { uid = await appUid(req); } catch { return void res.status(401).json({ error: "invalid_token" }); }
    if (!uidLimit.take(uid)) return void res.status(429).json({ error: "rate_limited" });
    const code = linkCode();
    await o.store.set("link_codes", sha256(normLinkCode(code)), { uid, expiresAt: Date.now() + LINK_TTL_MS });
    res.set("Cache-Control", "no-store").json({ code, expires_in: LINK_TTL_MS / 1000 });
  });
  // TODO(app): Settings -> "Connected AI assistants" screen: list these and offer Revoke.
  r.get("/link/grants", async (req, res) => {
    let uid: string; try { uid = await appUid(req); } catch { return void res.status(401).json({ error: "invalid_token" }); }
    const gs = await o.store.listBy("grants", "uid", uid);
    res.set("Cache-Control", "no-store").json({ grants: gs.map(g => ({ id: g.id, client_name: g.clientName, scope: g.scope, created_at: new Date(g.createdAt).toISOString(), last_used_at: new Date(g.lastUsedAt).toISOString() })) });
  });
  r.delete("/link/grants/:id", async (req, res) => {
    let uid: string; try { uid = await appUid(req); } catch { return void res.status(401).json({ error: "invalid_token" }); }
    const g = await o.store.get("grants", req.params.id);
    if (g && g.uid === uid) await o.store.delete("grants", req.params.id);
    res.status(204).end();
  });
  return r;
}

function consentHtml(reqId: string, clientName: string, redirectUri: string, error?: string) {
  const host = (() => { try { return new URL(redirectUri).host; } catch { return "?"; } })();
  return `<h1>Connect TurfTimer</h1>
<div class="box"><b>${esc(clientName)}</b> <small>(returns to ${esc(host)})</small> wants <b>read-only</b> access to the teams you have joined in TurfTimer:
kit, duties, next match, members and team chat messages. It cannot post, edit or delete anything.</div>
<p>Open <b>TurfTimer → Settings → Connect AI assistant</b> on your phone and enter the code shown there.</p>
${error ? `<p class="err">${esc(error)}</p>` : ""}
<form method="post" action="/oauth/authorize" autocomplete="off">
<input type="hidden" name="request_id" value="${esc(reqId)}">
<input name="link_code" placeholder="XXXX-XXXX" maxlength="12" autofocus>
<button name="action" value="allow">Allow</button><button class="no" name="action" value="deny">Cancel</button>
</form><p><small>Only connect apps you trust. You can disconnect from the app or by revoking in the assistant.</small></p>`;
}
