import { test, before, after } from "node:test";
import assert from "node:assert/strict";
import type { Server } from "node:http";
import { createHash } from "node:crypto";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StreamableHTTPClientTransport } from "@modelcontextprotocol/sdk/client/streamableHttp.js";
import { createApp } from "../src/app.js";
import { RateLimiter } from "../src/ratelimit.js";
import { setAuditSink } from "../src/audit.js";
import { fakeFirestore } from "./fakeFirestore.js";
import { MemoryStore } from "../src/oauth/store.js";
import { TokenSigner } from "../src/oauth/tokens.js";
import { isAllowedRedirect } from "../src/oauth/redirects.js";

const docs = {
  "teams/T1/metadata/info": { teamName: "U12 Girls", createdBy: "alice" },
  "teams/T1/members/bob": { uid: "bob", role: "member", displayName: "Bob" },
  "teams/T1/details/kit": { kitText: "Blue shirts" },
  "teams/T2/metadata/info": { teamName: "Other" },
  "teams/T2/members/zed": { uid: "zed", role: "admin" },
  "teams/T2/details/kit": { kitText: "ZED ONLY" },
};
// App-side Firebase ID tokens (what the TurfTimer app holds) -> uid
const appTokens: Record<string, string> = { "app-bob": "bob", "app-zed": "zed" };
// Firestore-side tokens our IdTokenSource mints per uid
const fsTokens: Record<string, string> = { "fs-bob": "bob", "fs-zed": "zed" };
const fake = fakeFirestore(docs, fsTokens);
let server: Server; let base: string; let store: MemoryStore;
const CB = "https://claude.ai/api/mcp/auth_callback";

before(async () => {
  setAuditSink(() => {});
  store = new MemoryStore();
  const http = await import("node:http");
  const s0 = http.createServer(); await new Promise<void>(r => s0.listen(0, () => r()));
  const port = (s0.address() as any).port; s0.close();
  base = `http://127.0.0.1:${port}`;
  const app = createApp({
    projectId: "turf-timer", baseUrl: base, store, fetchImpl: fake.fetch, limiter: new RateLimiter(100, 0), oauthLimiter: new RateLimiter(1000, 0),
    signer: new TokenSigner("s".repeat(40), base, `${base}/mcp`),
    verifyFirebase: async t => { if (!appTokens[t]) throw new Error("bad"); return { uid: appTokens[t] }; },
    idTokens: { forUid: async u => `fs-${u}` },
  });
  await new Promise<void>(r => { server = app.listen(port, () => r()); });
});
after(() => server.close());

const j = (u: string, body?: any, headers: Record<string, string> = {}) =>
  fetch(base + u, body === undefined ? { headers } : { method: "POST", headers: { "content-type": "application/json", ...headers }, body: JSON.stringify(body) });
const form = (u: string, body: Record<string, string>, headers: Record<string, string> = {}) =>
  fetch(base + u, { method: "POST", redirect: "manual", headers: { "content-type": "application/x-www-form-urlencoded", ...headers }, body: new URLSearchParams(body).toString() });
const verifier = "v".repeat(50);
const challenge = createHash("sha256").update(verifier).digest("base64url");

async function register(extra: any = {}) {
  const r = await j("/oauth/register", { client_name: "Claude", redirect_uris: [CB], ...extra });
  assert.equal(r.status, 201); return r.json() as Promise<any>;
}
async function linkCode(appTok = "app-bob") {
  const r = await fetch(base + "/link/code", { method: "POST", headers: { authorization: `Bearer ${appTok}` } });
  assert.equal(r.status, 200); return (await r.json() as any).code as string;
}
async function authorize(client: any, code: string, uri = CB) {
  const q = new URLSearchParams({ response_type: "code", client_id: client.client_id, redirect_uri: uri, code_challenge: challenge, code_challenge_method: "S256", state: "st1", scope: "turftimer.read", resource: `${base}/mcp` });
  const page = await fetch(`${base}/oauth/authorize?${q}`);
  assert.equal(page.status, 200);
  const html = await page.text();
  assert.match(html, /Claude/); assert.match(html, /claude\.ai/);
  const reqId = /name="request_id" value="([^"]+)"/.exec(html)![1];
  return form("/oauth/authorize", { request_id: reqId, link_code: code, action: "allow" });
}
async function fullFlow(client: any, appTok = "app-bob") {
  const res = await authorize(client, await linkCode(appTok));
  assert.equal(res.status, 303);
  const loc = new URL(res.headers.get("location")!);
  assert.equal(loc.origin + loc.pathname, CB); assert.equal(loc.searchParams.get("state"), "st1"); assert.equal(loc.searchParams.get("iss"), base);
  const tr = await form("/oauth/token", { grant_type: "authorization_code", code: loc.searchParams.get("code")!, redirect_uri: CB, code_verifier: verifier, client_id: client.client_id, ...(client.client_secret ? { client_secret: client.client_secret } : {}) });
  assert.equal(tr.status, 200); return tr.json() as Promise<any>;
}
async function mcp(token: string) {
  const c = new Client({ name: "t", version: "1" });
  await c.connect(new StreamableHTTPClientTransport(new URL(base + "/mcp"), { requestInit: { headers: { Authorization: `Bearer ${token}` } } }));
  return c;
}

test("discovery: 401 challenge points to protected-resource metadata, which points to the AS", async () => {
  const r = await fetch(base + "/mcp", { method: "POST", headers: { "content-type": "application/json" }, body: "{}" });
  assert.equal(r.status, 401);
  assert.match(r.headers.get("www-authenticate")!, new RegExp(`resource_metadata="${base}/.well-known/oauth-protected-resource"`));
  const prm: any = await (await j("/.well-known/oauth-protected-resource")).json();
  assert.equal(prm.resource, `${base}/mcp`); assert.deepEqual(prm.authorization_servers, [base]);
  const asm: any = await (await j("/.well-known/oauth-authorization-server")).json();
  assert.equal(asm.issuer, base); assert.deepEqual(asm.code_challenge_methods_supported, ["S256"]);
  assert.ok(asm.registration_endpoint && asm.revocation_endpoint);
});

test("DCR redirect allowlist covers ChatGPT, Claude, Grok, Copilot Studio, VS Code, loopback; rejects others", () => {
  for (const u of ["https://chatgpt.com/connector/oauth/abc_123", "https://chatgpt.com/connector_platform_oauth_redirect", CB, "https://claude.com/api/mcp/auth_callback",
    "https://grok.com/connectors-oauth-exchange-code/", "https://global.consent.azure-apim.net/redirect/cr123-5fturftimer", "https://global.consent.azure-apim.net/redirect",
    "https://vscode.dev/redirect", "http://127.0.0.1:33418/", "http://localhost:6274/oauth/callback"])
    assert.equal(isAllowedRedirect(u), true, u);
  for (const u of ["https://evil.com/cb", "http://chatgpt.com/connector/oauth/x", "https://chatgpt.com.evil.com/connector/oauth/x", "https://claude.ai/other", "javascript:alert(1)", "https://user:pw@grok.com/connectors-oauth-exchange-code/"])
    assert.equal(isAllowedRedirect(u), false, u);
});

test("DCR rejects non-allowlisted redirect", async () => {
  assert.equal((await j("/oauth/register", { redirect_uris: ["https://evil.com/cb"] })).status, 400);
});

test("full flow (confidential client): link code -> consent -> token -> MCP call as that user only", async () => {
  const client = await register();
  assert.ok(client.client_secret, "confidential by default (Copilot Studio needs a secret)");
  const tok = await fullFlow(client);
  assert.equal(tok.token_type, "Bearer"); assert.equal(tok.scope, "turftimer.read"); assert.ok(tok.expires_in <= 900);
  const c = await mcp(tok.access_token);
  const r: any = await c.callTool({ name: "get_kit", arguments: { teamId: "T1" } });
  assert.equal(JSON.parse(r.content[0].text).kit.kitText, "Blue shirts");
  const r2: any = await c.callTool({ name: "get_kit", arguments: { teamId: "T2" } });
  assert.equal(r2.isError, true); assert.equal(JSON.stringify(r2).includes("ZED ONLY"), false);
  await c.close();
});

test("impersonation: identity comes only from the app's link code; tool args cannot change the user", async () => {
  const client = await register({ token_endpoint_auth_method: "none" });
  const tok = await fullFlow(client, "app-bob");
  const c = await mcp(tok.access_token);
  const { tools } = await c.listTools();
  for (const t of tools) assert.equal(Object.keys((t.inputSchema as any).properties ?? {}).some(k => /uid|user/i.test(k)), false, t.name);
  const r: any = await c.callTool({ name: "list_teams", arguments: { uid: "zed" } as any });
  assert.equal(JSON.stringify(r).includes("T2"), false);
  await c.close();
});

test("token endpoint: secret required, PKCE enforced, codes single-use, redirect must match", async () => {
  const client = await register();
  const res = await authorize(client, await linkCode());
  const code = new URL(res.headers.get("location")!).searchParams.get("code")!;
  assert.equal((await form("/oauth/token", { grant_type: "authorization_code", code, redirect_uri: CB, code_verifier: verifier, client_id: client.client_id })).status, 401);
  const basic = "Basic " + Buffer.from(`${client.client_id}:${client.client_secret}`).toString("base64");
  assert.equal((await form("/oauth/token", { grant_type: "authorization_code", code, redirect_uri: CB, code_verifier: "w".repeat(50) }, { authorization: basic })).status, 400);
  // code was consumed by the failed attempt (single use)
  assert.equal((await form("/oauth/token", { grant_type: "authorization_code", code, redirect_uri: CB, code_verifier: verifier }, { authorization: basic })).status, 400);
});

test("authorize: unknown client/redirect shows error page and never redirects; missing PKCE redirects with error", async () => {
  const client = await register();
  const bad = await fetch(`${base}/oauth/authorize?response_type=code&client_id=${client.client_id}&redirect_uri=${encodeURIComponent("https://evil.com/cb")}&code_challenge=x&code_challenge_method=S256`, { redirect: "manual" });
  assert.equal(bad.status, 400);
  const nopkce = await fetch(`${base}/oauth/authorize?response_type=code&client_id=${client.client_id}&redirect_uri=${encodeURIComponent(CB)}`, { redirect: "manual" });
  assert.equal(nopkce.status, 302); assert.match(nopkce.headers.get("location")!, /error=invalid_request/);
  const write = await fetch(`${base}/oauth/authorize?response_type=code&client_id=${client.client_id}&redirect_uri=${encodeURIComponent(CB)}&code_challenge=x&code_challenge_method=S256&scope=turftimer.write`, { redirect: "manual" });
  assert.match(write.headers.get("location")!, /error=invalid_scope/);
});

test("wrong link code: re-prompts, then denies after 5 tries; link codes are single use", async () => {
  const client = await register();
  const q = new URLSearchParams({ response_type: "code", client_id: client.client_id, redirect_uri: CB, code_challenge: challenge, code_challenge_method: "S256" });
  const reqId = /name="request_id" value="([^"]+)"/.exec(await (await fetch(`${base}/oauth/authorize?${q}`)).text())![1];
  for (let i = 0; i < 4; i++) assert.equal((await form("/oauth/authorize", { request_id: reqId, link_code: "AAAA-AAAA", action: "allow" })).status, 400);
  const last = await form("/oauth/authorize", { request_id: reqId, link_code: "AAAA-AAAA", action: "allow" });
  assert.equal(last.status, 303); assert.match(last.headers.get("location")!, /error=access_denied/);
  const code = await linkCode();
  assert.equal((await authorize(client, code)).status, 303);
  assert.equal((await authorize(client, code)).status, 400);
});

test("refresh rotates; reuse of an old refresh token kills the grant; revoke stops MCP access", async () => {
  const client = await register({ token_endpoint_auth_method: "none" });
  const t1 = await fullFlow(client);
  const t2: any = await (await form("/oauth/token", { grant_type: "refresh_token", refresh_token: t1.refresh_token, client_id: client.client_id })).json();
  assert.ok(t2.access_token && t2.refresh_token !== t1.refresh_token);
  assert.equal((await form("/oauth/token", { grant_type: "refresh_token", refresh_token: t1.refresh_token, client_id: client.client_id })).status, 400);
  assert.equal((await form("/oauth/token", { grant_type: "refresh_token", refresh_token: t2.refresh_token, client_id: client.client_id })).status, 400, "grant revoked on reuse");

  const t3 = await fullFlow(client);
  assert.equal((await form("/oauth/revoke", { token: t3.refresh_token, client_id: client.client_id })).status, 200);
  const r = await fetch(base + "/mcp", { method: "POST", headers: { "content-type": "application/json", accept: "application/json, text/event-stream", authorization: `Bearer ${t3.access_token}` }, body: JSON.stringify({ jsonrpc: "2.0", id: 1, method: "tools/list" }) });
  assert.equal(r.status, 401);
});

test("app endpoints: list and revoke my grants; other users cannot see or revoke them", async () => {
  const client = await register({ token_endpoint_auth_method: "none" });
  await fullFlow(client, "app-zed");
  const mine: any = await (await j("/link/grants", undefined, { authorization: "Bearer app-zed" })).json();
  assert.ok(mine.grants.length >= 1 && mine.grants[0].client_name === "Claude");
  const bobs: any = await (await j("/link/grants", undefined, { authorization: "Bearer app-bob" })).json();
  assert.equal(bobs.grants.some((g: any) => g.id === mine.grants[0].id), false);
  await fetch(`${base}/link/grants/${mine.grants[0].id}`, { method: "DELETE", headers: { authorization: "Bearer app-bob" } });
  assert.ok(await store.get("grants", mine.grants[0].id), "bob cannot revoke zed's grant");
  await fetch(`${base}/link/grants/${mine.grants[0].id}`, { method: "DELETE", headers: { authorization: "Bearer app-zed" } });
  assert.equal(await store.get("grants", mine.grants[0].id), null);
  assert.equal((await fetch(base + "/link/code", { method: "POST" })).status, 401);
});

test("raw Firebase ID tokens are NOT accepted on /mcp in production mode", async () => {
  const r = await fetch(base + "/mcp", { method: "POST", headers: { "content-type": "application/json", authorization: "Bearer app-bob" }, body: "{}" });
  assert.equal(r.status, 401);
});

test("oauth endpoints are rate limited per IP", async () => {
  const { oauthRouter } = await import("../src/oauth/router.js");
  const express = (await import("express")).default;
  const a = express(); a.use(oauthRouter({ baseUrl: "http://x", store: new MemoryStore(), signer: new TokenSigner("s".repeat(40), "http://x", "http://x/mcp"), verifyFirebase: async () => ({ uid: "u" }), ipLimiter: new RateLimiter(2, 0) }));
  const s = a.listen(0); const u = `http://127.0.0.1:${(s.address() as any).port}/oauth/register`;
  const codes = [];
  for (let i = 0; i < 3; i++) codes.push((await fetch(u, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ redirect_uris: [CB] }) })).status);
  s.close();
  assert.deepEqual(codes, [201, 201, 429]);
});
