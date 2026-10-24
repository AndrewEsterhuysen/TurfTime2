// One-off: set uid = doc id on every teams/*/members/* doc missing it.
// Doc id is the member's Firebase Auth uid (all app writes use members/{auth uid}).
// Usage (needs Admin creds via ADC / GOOGLE_APPLICATION_CREDENTIALS):
//   cd functions && node ../scripts/firebase/backfill-member-uid.js          # dry run
//   cd functions && node ../scripts/firebase/backfill-member-uid.js --apply  # write
const { initializeApp } = require('firebase-admin/app');
const { getFirestore } = require('firebase-admin/firestore');
initializeApp({ projectId: 'turf-timer' });
const db = getFirestore();
const apply = process.argv.includes('--apply');
(async () => {
  const snap = await db.collectionGroup('members').get();
  let total = 0, missing = 0;
  const batch = [];
  for (const d of snap.docs) {
    if (!d.ref.path.startsWith('teams/')) continue;
    total++;
    if (d.get('uid') !== d.id) { missing++; batch.push(d.ref); }
  }
  console.log(`team member docs: ${total}, needing uid: ${missing}${apply ? '' : ' (dry run)'}`);
  if (!apply) return;
  for (let i = 0; i < batch.length; i += 400) {
    const b = db.batch();
    batch.slice(i, i + 400).forEach((r) => b.set(r, { uid: r.id }, { merge: true }));
    await b.commit();
  }
  console.log('done');
})().catch((e) => { console.error(e); process.exit(1); });
