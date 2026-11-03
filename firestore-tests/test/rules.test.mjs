// Firestore rules tests. Run: npm test (needs Java 11+ for the emulator).
import { test, before, after, beforeEach } from 'node:test';
import { readFileSync } from 'node:fs';
import { initializeTestEnvironment, assertSucceeds, assertFails } from '@firebase/rules-unit-testing';
import { doc, getDoc, setDoc, getDocs, collection, collectionGroup, query, where, deleteDoc, updateDoc } from 'firebase/firestore';

let env;
const T = 'team1';
before(async () => {
  env = await initializeTestEnvironment({
    projectId: 'demo-turftimer',
    firestore: { rules: readFileSync(new URL('../../firestore.rules', import.meta.url), 'utf8') },
  });
});
after(async () => { await env.cleanup(); });
beforeEach(async () => {
  await env.clearFirestore();
  await env.withSecurityRulesDisabled(async (ctx) => {
    const db = ctx.firestore();
    await setDoc(doc(db, `teams/${T}/metadata/info`), { teamName: 'U10', inviteCode: 'ABC-123', createdBy: 'owner' });
    await setDoc(doc(db, `teams/${T}/members/owner`), { uid: 'owner', role: 'admin', displayName: 'O' });
    await setDoc(doc(db, `teams/${T}/members/mem`), { uid: 'mem', role: 'member', displayName: 'M' });
    await setDoc(doc(db, `teams/${T}/roster/data`), { players: [] });
    await setDoc(doc(db, `teams/${T}/messages/m1`), { text: 'hi', userId: 'mem', senderName: 'M', timestamp: 1 });
    await setDoc(doc(db, `teams/${T}/sessions/s1`), { x: 1 });
    await setDoc(doc(db, `teams/${T}/public/invite`), { inviteCode: 'ABC-123' });
    await setDoc(doc(db, `invite_codes/ABC123`), { teamId: T });
    await setDoc(doc(db, `activeControllers/${T}`), { uid: 'mem' });
    await setDoc(doc(db, `mcp_grants/g1`), { uid: 'mem' });
  });
});
const as = (uid) => env.authenticatedContext(uid).firestore();

test('non-member cannot read team data', async () => {
  const db = as('stranger');
  await assertFails(getDocs(collection(db, `teams/${T}/messages`)));
  await assertFails(getDoc(doc(db, `teams/${T}/messages/m1`)));
  await assertFails(getDocs(collection(db, `teams/${T}/sessions`)));
  await assertFails(getDoc(doc(db, `teams/${T}/roster/data`)));
  await assertFails(setDoc(doc(db, `teams/${T}/roster/data`), { players: ['x'] }));
  await assertFails(setDoc(doc(db, `teams/${T}/sessions/s2`), { x: 2 }));
  await assertFails(getDoc(doc(db, `activeControllers/${T}`)));
});
test('non-member cannot post a message', async () => {
  await assertFails(setDoc(doc(as('stranger'), `teams/${T}/messages/m2`), { text: 'x', userId: 'stranger', senderName: 'S', timestamp: 2 }));
});
test('member can read team data', async () => {
  const db = as('mem');
  await assertSucceeds(getDocs(collection(db, `teams/${T}/messages`)));
  await assertSucceeds(getDocs(collection(db, `teams/${T}/sessions`)));
  await assertSucceeds(getDoc(doc(db, `teams/${T}/roster/data`)));
  await assertSucceeds(updateDoc(doc(db, `teams/${T}/roster/data`), { players: ['a'] }));
  await assertSucceeds(setDoc(doc(db, `teams/${T}/sessions/s2`), { x: 2 }));
  await assertSucceeds(getDoc(doc(db, `activeControllers/${T}`)));
});
test('join flow: pre-membership lookups still work', async () => {
  const db = as('joiner');
  await assertSucceeds(getDoc(doc(db, `invite_codes/ABC123`)));
  await assertSucceeds(getDoc(doc(db, `teams/${T}/metadata/info`)));
  await assertSucceeds(getDoc(doc(db, `teams/${T}/public/invite`)));
});
test('pre-existing (unchanged): collection-group public query is denied, app falls back to invite_codes', async () => {
  // No {path=**}/public rule exists on master either; CloudTeamService catches this and continues.
  await assertFails(getDocs(query(collectionGroup(as('joiner'), 'public'), where('inviteCode', '==', 'ABC-123'))));
});
test('Select Team: collection-group members query for own uid', async () => {
  await assertSucceeds(getDocs(query(collectionGroup(as('mem'), 'members'), where('uid', '==', 'mem'))));
});
test('owner delete cascade: owner can still list after members are gone', async () => {
  await env.withSecurityRulesDisabled(async (ctx) => {
    await deleteDoc(doc(ctx.firestore(), `teams/${T}/members/owner`));
    await deleteDoc(doc(ctx.firestore(), `teams/${T}/members/mem`));
  });
  const db = as('owner');
  await assertSucceeds(getDocs(collection(db, `teams/${T}/messages`)));
  await assertSucceeds(getDocs(collection(db, `teams/${T}/sessions`)));
  await assertSucceeds(deleteDoc(doc(db, `teams/${T}/roster/data`)));
});
test('removed member loses access', async () => {
  await env.withSecurityRulesDisabled(async (ctx) => deleteDoc(doc(ctx.firestore(), `teams/${T}/members/mem`)));
  await assertFails(getDocs(collection(as('mem'), `teams/${T}/messages`)));
});
test('mcp_* collections are server-only', async () => {
  await assertFails(getDoc(doc(as('mem'), 'mcp_grants/g1')));
  for (const c of ['mcp_clients', 'mcp_auth_codes', 'mcp_auth_requests', 'mcp_grants', 'mcp_link_codes']) {
    await assertFails(getDoc(doc(as('mem'), `${c}/x`)));
    await assertFails(setDoc(doc(as('mem'), `${c}/x`), { a: 1 }));
  }
});
test('unauthenticated cannot read anything', async () => {
  const db = env.unauthenticatedContext().firestore();
  await assertFails(getDoc(doc(db, `teams/${T}/metadata/info`)));
  await assertFails(getDocs(collection(db, `teams/${T}/messages`)));
});
