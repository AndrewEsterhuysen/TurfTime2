// READ-ONLY pre-deploy check for the tightened firestore.rules. Lists users who have written
// messages to a team but have no teams/{t}/members/{uid} doc (they would lose chat access).
// Run with Application Default Credentials:  node scripts/audit-membership.mjs  (never writes)
import { initializeApp } from "firebase-admin/app";
import { getFirestore } from "firebase-admin/firestore";
const db = getFirestore(initializeApp({ projectId: process.env.FIREBASE_PROJECT_ID ?? "turf-timer" }));
let teams = 0, gaps = 0;
for (const t of (await db.collection("teams").listDocuments())) {
  teams++;
  const members = new Set((await t.collection("members").select().get()).docs.map(d => d.id));
  const senders = new Set((await t.collection("messages").orderBy("timestamp", "desc").limit(200).select("userId").get()).docs.map(d => d.get("userId")).filter(Boolean));
  const missing = [...senders].filter(u => !members.has(u));
  if (missing.length) { gaps += missing.length; console.log(JSON.stringify({ team: t.id, membersCount: members.size, sendersWithoutMemberDoc: missing.map(u => u.slice(0, 6) + "…") })); }
}
console.log(JSON.stringify({ teams, sendersWithoutMemberDoc: gaps }));
