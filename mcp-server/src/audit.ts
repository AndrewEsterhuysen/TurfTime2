/**
 * Audit log STUB: one structured JSON line per tool call (Cloud Logging picks up stdout).
 * Never logs tokens or message bodies. Phase 2: persist to a dedicated store (e.g. BigQuery sink).
 */
export interface AuditEntry { uid: string; tool: string; teamId?: string; ok: boolean; count?: number; error?: string; ms: number; }
export type AuditSink = (e: AuditEntry & { ts: string; kind: "mcp_audit" }) => void;
export let auditSink: AuditSink = e => console.log(JSON.stringify(e));
export function setAuditSink(s: AuditSink) { auditSink = s; }
export function audit(e: AuditEntry) { auditSink({ kind: "mcp_audit", ts: new Date().toISOString(), ...e }); }
