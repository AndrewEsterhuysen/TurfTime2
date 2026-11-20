// Player of the match nominations rules (docs/nominations-spec.md §5).
// Run: npm test (Firestore emulator only; never production).
import { test, before, after, beforeEach } from 'node:test';
import { readFileSync } from 'node:fs';
import { initializeTestEnvironment, assertSucceeds, assertFails } from '@firebase/rules-unit-testing';
import { doc, getDoc, setDoc, getDocs, collection, deleteDoc, Timestamp } from 'firebase/firestore';

let env;
const T = 'teamN';
const S = 's1';
const NOM = `teams/${T}/sessions/${S}/nominations`;
const META = `teams/${T}/sessions/${S}/nominationsMeta/status`;
const rulesPath = process.env.RULES_FILE || new URL('../../firestore.rules', import.meta.url);

before(async () => {
  env = await initializeTestEnvironment({
    projectId: 'demo-turftimer-nominations',
    firestore: { rules: readFileSync(rulesPath, 'utf8') },
  });
});
after(async () => { await env.cleanup(); });

const hoursAgo = (h) => Timestamp.fromMillis(Date.now() - h * 3600 * 1000);
const seed = async (fn) => env.withSecurityRulesDisabled(async (ctx) => fn(ctx.firestore()));

beforeEach(async () => {
  await env.clearFirestore();
  await seed(async (db) => {
    await setDoc(doc(db, `teams/${T}/metadata/info`), { teamName: 'U10', createdBy: 'owner' });
    await setDoc(doc(db, `teams/${T}/members/owner`), { uid: 'owner', role: 'admin', displayName: 'Coach' });
    await setDoc(doc(db, `teams/${T}/members/mem`), { uid: 'mem', role: 'member', displayName: 'Parent A' });
    await setDoc(doc(db, `teams/${T}/members/mem2`), { uid: 'mem2', role: 'member', displayName: 'Parent B' });
    // Ended 1 hour ago: voting open.
    await setDoc(doc(db, `teams/${T}/sessions/${S}`), {
      sessionId: S, startTime: hoursAgo(2), endTime: hoursAgo(1), sessionJson: '{}',
    });
  });
});

const as = (uid) => env.authenticatedContext(uid).firestore();
const vote = (uid, slot = 3, name = 'Cat') => ({
  nominatorId: uid, nominatorDisplayName: 'Parent', nomineeSlotId: slot, nomineeName: name, timestamp: Timestamp.now(),
});

test('nominations: non-member cannot vote or read the tally', async () => {
  const db = as('stranger');
  await assertFails(setDoc(doc(db, `${NOM}/stranger`), vote('stranger')));
  await assertFails(getDocs(collection(db, NOM)));
  await assertFails(getDoc(doc(db, META)));
  await assertFails(setDoc(doc(env.unauthenticatedContext().firestore(), `${NOM}/x`), vote('x')));
});

test('nominations: member creates a vote under their own uid', async () => {
  await assertSucceeds(setDoc(doc(as('mem'), `${NOM}/mem`), vote('mem')));
});

test('nominations: member cannot vote under another uid or with a bad payload', async () => {
  const db = as('mem');
  await assertFails(setDoc(doc(db, `${NOM}/mem2`), vote('mem2')));
  await assertFails(setDoc(doc(db, `${NOM}/mem2`), vote('mem')));
  await assertFails(setDoc(doc(db, `${NOM}/mem`), { ...vote('mem'), nomineeSlotId: 0 }));
  await assertFails(setDoc(doc(db, `${NOM}/mem`), { ...vote('mem'), nomineeName: '' }));
  await assertFails(setDoc(doc(db, `${NOM}/mem`), { ...vote('mem'), nomineeName: 'x'.repeat(81) }));
  await assertFails(setDoc(doc(db, `${NOM}/mem`), { ...vote('mem'), voters: ['a'] }));
  const { timestamp, ...noTs } = vote('mem');
  await assertFails(setDoc(doc(db, `${NOM}/mem`), noTs));
});

test('nominations: second vote replaces the first (same doc id, update)', async () => {
  const db = as('mem');
  await assertSucceeds(setDoc(doc(db, `${NOM}/mem`), vote('mem', 3, 'Cat')));
  await assertSucceeds(setDoc(doc(db, `${NOM}/mem`), vote('mem', 5, 'Eve')));
  const all = await getDocs(collection(as('owner'), NOM));
  const mine = all.docs.filter((d) => d.data().nominatorId === 'mem');
  if (mine.length !== 1 || mine[0].data().nomineeSlotId !== 5) throw new Error('expected exactly one ballot with the new pick');
  const own = await assertSucceeds(getDoc(doc(db, `${NOM}/mem`)));
  if (own.data().nomineeSlotId !== 5) throw new Error('member should read their own new pick');
});

test('nominations: another user cannot edit or delete someone else\'s vote', async () => {
  await assertSucceeds(setDoc(doc(as('mem'), `${NOM}/mem`), vote('mem')));
  const other = as('mem2');
  await assertFails(setDoc(doc(other, `${NOM}/mem`), vote('mem', 1, 'Ann')));
  await assertFails(setDoc(doc(other, `${NOM}/mem`), vote('mem2', 1, 'Ann')));
  await assertFails(deleteDoc(doc(other, `${NOM}/mem`)));
});

test('nominations: member may withdraw own vote while open; admin may remove any vote', async () => {
  await assertSucceeds(setDoc(doc(as('mem'), `${NOM}/mem`), vote('mem')));
  await assertSucceeds(deleteDoc(doc(as('mem'), `${NOM}/mem`)));
  await assertSucceeds(setDoc(doc(as('mem2'), `${NOM}/mem2`), vote('mem2')));
  await assertSucceeds(deleteDoc(doc(as('owner'), `${NOM}/mem2`)));
});

test('nominations: vote more than 24 hours after endTime is denied', async () => {
  await seed((db) => setDoc(doc(db, `teams/${T}/sessions/${S}`), { sessionId: S, endTime: hoursAgo(25) }));
  await assertFails(setDoc(doc(as('mem'), `${NOM}/mem`), vote('mem')));
  // Changing an existing vote after the window is denied too.
  await seed((db) => setDoc(doc(db, `${NOM}/mem`), vote('mem')));
  await assertFails(setDoc(doc(as('mem'), `${NOM}/mem`), vote('mem', 5, 'Eve')));
  await assertFails(deleteDoc(doc(as('mem'), `${NOM}/mem`)));
});

test('nominations: vote just inside 24 hours is allowed', async () => {
  await seed((db) => setDoc(doc(db, `teams/${T}/sessions/${S}`), { sessionId: S, endTime: hoursAgo(23.9) }));
  await assertSucceeds(setDoc(doc(as('mem'), `${NOM}/mem`), vote('mem')));
});

test('nominations: vote on a match without an end time or missing session is denied', async () => {
  await seed((db) => setDoc(doc(db, `teams/${T}/sessions/${S}`), { sessionId: S }));
  await assertFails(setDoc(doc(as('mem'), `${NOM}/mem`), vote('mem')));
  await assertFails(setDoc(doc(as('mem'), `teams/${T}/sessions/nope/nominations/mem`), vote('mem')));
});

test('nominations: vote after an Admin closes voting is denied', async () => {
  await assertSucceeds(setDoc(doc(as('mem'), `${NOM}/mem`), vote('mem')));
  await assertSucceeds(setDoc(doc(as('owner'), META), { closed: true, closedAt: Timestamp.now(), closedBy: 'owner' }));
  await assertFails(setDoc(doc(as('mem'), `${NOM}/mem`), vote('mem', 5, 'Eve')));
  await assertFails(setDoc(doc(as('mem2'), `${NOM}/mem2`), vote('mem2')));
  // Admin can still remove a vote after close.
  await assertSucceeds(deleteDoc(doc(as('owner'), `${NOM}/mem`)));
});

test('nominations: only Admins write the close status; members can read it', async () => {
  await assertFails(setDoc(doc(as('mem'), META), { closed: true, closedAt: Timestamp.now(), closedBy: 'mem' }));
  await assertFails(setDoc(doc(as('owner'), `teams/${T}/sessions/${S}/nominationsMeta/other`), { closed: true }));
  await assertFails(setDoc(doc(as('owner'), META), { closed: 'yes' }));
  await assertSucceeds(setDoc(doc(as('owner'), META), { closed: true, closedAt: Timestamp.now(), closedBy: 'owner' }));
  await assertSucceeds(getDoc(doc(as('mem'), META)));
});

const seedTwoVotes = () => seed(async (db) => {
  await setDoc(doc(db, `${NOM}/mem`), vote('mem'));
  await setDoc(doc(db, `${NOM}/mem2`), vote('mem2', 1, 'Ann'));
  await setDoc(doc(db, `teams/${T}/members/late`), { uid: 'late', role: 'member' });
});

test('nominations: while open, a member reads only their own vote (live tally is Admin-only)', async () => {
  await seedTwoVotes();
  const mem = as('mem');
  await assertSucceeds(getDoc(doc(mem, `${NOM}/mem`)));
  await assertFails(getDoc(doc(mem, `${NOM}/mem2`)));
  await assertFails(getDocs(collection(mem, NOM)));
  // Reading your own not-yet-cast vote is allowed (returns "missing").
  await assertSucceeds(getDoc(doc(as('late'), `${NOM}/late`)));
  await assertFails(getDocs(collection(as('late'), NOM)));
  await assertSucceeds(setDoc(doc(as('late'), `${NOM}/late`), vote('late')));
});

test('nominations: while open, Admins read the whole tally', async () => {
  await seedTwoVotes();
  const snap = await assertSucceeds(getDocs(collection(as('owner'), NOM)));
  if (snap.size !== 2) throw new Error(`expected 2 votes, got ${snap.size}`);
  await assertSucceeds(getDoc(doc(as('owner'), `${NOM}/mem2`)));
});

test('nominations: after an Admin closes voting, members (and late joiners) read the result', async () => {
  await seedTwoVotes();
  await seed((db) => setDoc(doc(db, META), { closed: true, closedAt: Timestamp.now(), closedBy: 'owner' }));
  const snap = await assertSucceeds(getDocs(collection(as('mem'), NOM)));
  if (snap.size !== 2) throw new Error(`expected 2 votes, got ${snap.size}`);
  await assertSucceeds(getDoc(doc(as('mem'), `${NOM}/mem2`)));
  await assertSucceeds(getDocs(collection(as('late'), NOM)));
});

test('nominations: 24 hours after the match, members read the result', async () => {
  await seedTwoVotes();
  await seed((db) => setDoc(doc(db, `teams/${T}/sessions/${S}`), { sessionId: S, endTime: hoursAgo(25) }));
  const snap = await assertSucceeds(getDocs(collection(as('mem2'), NOM)));
  if (snap.size !== 2) throw new Error(`expected 2 votes, got ${snap.size}`);
});

test('nominations: non-members cannot read votes even after close', async () => {
  await seedTwoVotes();
  await seed((db) => setDoc(doc(db, META), { closed: true }));
  await assertFails(getDocs(collection(as('stranger'), NOM)));
  await assertFails(getDoc(doc(as('stranger'), `${NOM}/mem`)));
});

test('nominations: owner cascade can clear votes and status after members are gone', async () => {
  await seed(async (db) => {
    await setDoc(doc(db, `${NOM}/mem`), vote('mem'));
    await setDoc(doc(db, META), { closed: true });
    await deleteDoc(doc(db, `teams/${T}/members/owner`));
  });
  const db = as('owner');
  await assertSucceeds(getDocs(collection(db, NOM)));
  await assertSucceeds(deleteDoc(doc(db, `${NOM}/mem`)));
  await assertSucceeds(deleteDoc(doc(db, META)));
});
