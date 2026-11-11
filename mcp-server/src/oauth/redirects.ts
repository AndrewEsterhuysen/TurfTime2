/**
 * Redirect URI policy for Dynamic Client Registration. Exact-match is enforced later at /authorize
 * (the redirect_uri must equal one registered for the client); this decides what may be registered.
 */
const HTTPS_ALLOW: RegExp[] = [
  /^https:\/\/chatgpt\.com\/connector\/oauth\/[A-Za-z0-9_-]+$/,           // ChatGPT apps/connectors (per-connector callback)
  /^https:\/\/chatgpt\.com\/connector_platform_oauth_redirect$/,          // ChatGPT legacy
  /^https:\/\/(claude\.ai|claude\.com)\/api\/mcp\/auth_callback$/,         // Claude.ai / Desktop / mobile custom connectors
  /^https:\/\/grok\.com\/connectors-oauth-exchange-code\/?$/,              // Grok custom connectors
  /^https:\/\/www\.cursor\.com\/agents\/mcp\/oauth\/callback$/,           // Grok Bot / Cursor agents connectors (exact)
  /^https:\/\/global\.consent\.azure-apim\.net\/redirect(\/[A-Za-z0-9._-]+)?$/, // Copilot Studio / Power Platform
  /^https:\/\/vscode\.dev\/redirect$/,                                     // VS Code (GitHub Copilot) web redirect
];

export function extraAllowed(): RegExp[] {
  // Comma-separated exact https URLs, e.g. MCP_EXTRA_REDIRECTS="https://example.com/cb"
  return (process.env.MCP_EXTRA_REDIRECTS ?? "").split(",").map(s => s.trim()).filter(Boolean)
    .map(u => new RegExp("^" + u.replace(/[.*+?^${}()|[\]\\]/g, "\\$&") + "$"));
}

export function isAllowedRedirect(uri: string): boolean {
  let u: URL;
  try { u = new URL(uri); } catch { return false; }
  if (u.hash) return false;
  // Loopback for native/CLI clients (VS Code 127.0.0.1:33418, Claude Code, MCP Inspector), any port.
  if (u.protocol === "http:" && (u.hostname === "127.0.0.1" || u.hostname === "localhost" || u.hostname === "[::1]")) return true;
  if (u.protocol !== "https:") return false;
  const noQuery = `${u.origin}${u.pathname}`;
  return [...HTTPS_ALLOW, ...extraAllowed()].some(r => r.test(noQuery)) && !u.username && !u.password;
}

/** RFC 8252 §7.3: loopback redirects match ignoring the port. */
export function redirectMatches(registered: string[], requested: string): boolean {
  if (registered.includes(requested)) return true;
  try {
    const r = new URL(requested);
    if (r.protocol !== "http:" || !["127.0.0.1", "localhost", "[::1]"].includes(r.hostname)) return false;
    return registered.some(x => { const g = new URL(x); return g.protocol === "http:" && g.hostname === r.hostname && g.pathname === r.pathname && g.search === r.search; });
  } catch { return false; }
}
