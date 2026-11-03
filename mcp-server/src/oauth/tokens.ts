import { SignJWT, jwtVerify } from "jose";
export const SCOPE_READ = "turftimer.read";
export const ACCESS_TTL_SEC = Number(process.env.MCP_ACCESS_TTL_SEC ?? 900);      // 15 min
export const REFRESH_TTL_MS = Number(process.env.MCP_REFRESH_TTL_DAYS ?? 30) * 86400_000; // sliding

export interface AccessClaims { uid: string; scope: string; clientId: string; grantId: string; }

export class TokenSigner {
  private key: Uint8Array;
  constructor(secret: string, readonly issuer: string, readonly audience: string) {
    if (!secret || secret.length < 32) throw new Error("MCP_JWT_SECRET must be at least 32 characters");
    this.key = new TextEncoder().encode(secret);
  }
  async sign(c: AccessClaims): Promise<string> {
    return new SignJWT({ scope: c.scope, client_id: c.clientId, gid: c.grantId })
      .setProtectedHeader({ alg: "HS256", typ: "at+jwt" })
      .setIssuer(this.issuer).setAudience(this.audience).setSubject(c.uid)
      .setIssuedAt().setExpirationTime(`${ACCESS_TTL_SEC}s`).setJti(crypto.randomUUID())
      .sign(this.key);
  }
  async verify(token: string): Promise<AccessClaims> {
    const { payload } = await jwtVerify(token, this.key, { issuer: this.issuer, audience: this.audience, algorithms: ["HS256"] });
    if (!payload.sub || typeof payload.gid !== "string") throw new Error("bad token");
    return { uid: payload.sub, scope: String(payload.scope ?? ""), clientId: String(payload.client_id ?? ""), grantId: payload.gid };
  }
}
