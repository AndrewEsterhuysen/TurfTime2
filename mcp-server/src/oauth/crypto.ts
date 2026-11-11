import { createHash, randomBytes, timingSafeEqual } from "node:crypto";
export const rand = (n = 32) => randomBytes(n).toString("base64url");
export const sha256 = (s: string) => createHash("sha256").update(s).digest("base64url");
export function safeEq(a: string, b: string) { const x = Buffer.from(a), y = Buffer.from(b); return x.length === y.length && timingSafeEqual(x, y); }
export const pkceS256 = (verifier: string) => createHash("sha256").update(verifier).digest("base64url");
// Link codes: 8 chars, no 0/O/1/I/L, shown as XXXX-XXXX.
const ALPH = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
export function linkCode() { const b = randomBytes(8); let s = ""; for (const x of b) s += ALPH[x % ALPH.length]; return `${s.slice(0, 4)}-${s.slice(4)}`; }
export const normLinkCode = (c: string) => c.toUpperCase().replace(/[^A-Z0-9]/g, "");
