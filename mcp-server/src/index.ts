import { createApp } from "./app.js";
import { defaultVerifier } from "./auth.js";
import { firestoreStore, MemoryStore } from "./oauth/store.js";
import { TokenSigner } from "./oauth/tokens.js";
import { firebaseIdTokenSource } from "./idtoken.js";

const need = (k: string) => { const v = process.env[k]; if (!v) { console.error(`missing env ${k}`); process.exit(1); } return v; };
const projectId = process.env.FIREBASE_PROJECT_ID ?? "turf-timer";
const port = Number(process.env.PORT ?? 8080);
const baseUrl = need("PUBLIC_BASE_URL").replace(/\/$/, "");
const store = process.env.MCP_STORE === "memory" ? new MemoryStore() : await firestoreStore(projectId);
const app = createApp({
  projectId, baseUrl, store,
  signer: new TokenSigner(need("MCP_JWT_SECRET"), baseUrl, `${baseUrl}/mcp`),
  verifyFirebase: await defaultVerifier(projectId),
  idTokens: await firebaseIdTokenSource(projectId, need("FIREBASE_WEB_API_KEY")),
  allowFirebaseIdTokens: process.env.ALLOW_FIREBASE_ID_TOKENS === "1",
});
app.listen(port, () => console.log(JSON.stringify({ kind: "startup", projectId, port, baseUrl, store: process.env.MCP_STORE ?? "firestore" })));
