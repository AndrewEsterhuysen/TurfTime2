// Existing-app regression tests: mirrors the real client operations in Services/*.cs
// (CloudTeamService, CloudRosterService, ChatService, SessionStorageService,
// MatchScheduleService, TeamDetailsDocService). Every "app:" test must pass on both the
// previous production rules and the current rules. Run: npm test
// Override rules file: RULES_FILE=/path/to/firestore.rules npm test
import { test, before, after, beforeEach } from 'node:test';
import { readFileSync } from 'node:fs';
import { initializeTestEnvironment, assertSucceeds, assertFails } from '@firebase/rules-unit-testing';
import { doc, getDoc, setDoc, getDocs, collection, collectionGroup, query, where, deleteDoc, updateDoc, orderBy, limit } from 'firebase/firestore';

let env;
const T = 'teamA';
const rulesPath = process.env.RULES_FILE || new URL('../../firestore.rules', import.meta.url);
before(async () => {
  env = await initializeTestEnvironment({
    projectId: 'demo-turftimer-app',
    firestore: { rules: readFileSync(rulesPath, 'utf8') },
  });
});
after(async () => { await env.cleanup(); });
beforeEach(async () => {
  await env.clearFirestore();
  await env.withSecurityRulesDisabled(async (ctx) => {
    const db = ctx.firestore();
    await setDoc(doc(db, `teams/${T}/metadata/info`), { teamName: 'U10', inviteCode: 'XYZ-789', createdBy: 'owner', isActive: true });
    await setDoc(doc(db, `teams/${T}/members/owner`), { uid: 'owner', role: 'admin', displayName: 'Owner' });
    await setDoc(doc(db, `teams/${T}/members/coach2`), { uid: 'coach2', role: 'admin', displayName: 'Coach2' });
    await setDoc(doc(db, `teams/${T}/members/mem`), { uid: 'mem', role: 'member', displayName: 'Mem' });
    await setDoc(doc(db, `teams/${T}/roster/data`), { version: 2, players: [], controllerUid: '' });
    await setDoc(doc(db, `teams/${T}/details/location`), { venue: 'Park' });
    await setDoc(doc(db, `teams/${T}/details/kit`), { kit: 'Blue' });
    await setDoc(doc(db, `teams/${T}/details/duties`), { duties: 'Oranges' });
    await setDoc(doc(db, `teams/${T}/messages/m1`), { text: 'hi', userId: 'mem', senderName: 'Mem', timestamp: 1 });
    await setDoc(doc(db, `teams/${T}/messages/m2`), { text: 'coach', userId: 'owner', senderName: 'Owner', timestamp: 2 });
    await setDoc(doc(db, `teams/${T}/sessions/s1`), { sessionId: 's1' });
    await setDoc(doc(db, `teams/${T}/logs/l1`), { x: 1 });
    await setDoc(doc(db, `teams/${T}/public/invite`), { inviteCode: 'XYZ-789', createdBy: 'owner' });
    await setDoc(doc(db, `invite_codes/XYZ789`), { teamId: T, teamName: 'U10', createdBy: 'owner' });
    await setDoc(doc(db, `activeControllers/${T}`), { uid: 'mem' });
  });
});
const as = (uid) => env.authenticatedContext(uid).firestore();
const M = { merge: true };

test('app: coach creates a new nickname team (CreateTeam order: metadata, members, roster, invite)', async () => {
  const db = as('newcoach'); const t = 'teamNew';
  await assertSucceeds(setDoc(doc(db, `teams/${t}/metadata/info`), { teamName: 'New', inviteCode: 'NEW-111', createdBy: 'newcoach', kind: 'nickname', isActive: true }, M));
  await assertSucceeds(setDoc(doc(db, `teams/${t}/members/newcoach`), { role: 'admin', displayName: 'NC', uid: 'newcoach' }, M));
  await assertSucceeds(setDoc(doc(db, `teams/${t}/roster/data`), { version: 2, players: [] }, M));
  await assertSucceeds(setDoc(doc(db, `invite_codes/NEW111`), { teamId: t, teamName: 'New', createdBy: 'newcoach' }, M));
  await assertSucceeds(setDoc(doc(db, `teams/${t}/public/invite`), { inviteCode: 'NEW-111', createdBy: 'newcoach' }, M));
  await assertSucceeds(getDoc(doc(db, `teams/${t}/roster/data`)));
});
test('app: club owner creates club + team under club', async () => {
  const db = as('clubboss');
  await assertSucceeds(setDoc(doc(db, `clubs/c1/metadata/info`), { clubName: 'FC', createdBy: 'clubboss' }, M));
  await assertSucceeds(setDoc(doc(db, `clubs/c1/members/clubboss`), { role: 'owner', displayName: 'B' }, M));
  await assertSucceeds(setDoc(doc(db, `teams/tc/metadata/info`), { teamName: 'U9', createdBy: 'clubboss', clubId: 'c1', clubName: 'FC' }, M));
  await assertSucceeds(setDoc(doc(db, `teams/tc/members/clubboss`), { role: 'admin', uid: 'clubboss' }, M));
  await assertSucceeds(setDoc(doc(db, `teams/tc/roster/data`), { version: 2, players: [] }, M));
});
test('app: join by invite code, then use team as member', async () => {
  const db = as('joiner');
  await assertSucceeds(getDoc(doc(db, `invite_codes/XYZ789`)));
  await assertSucceeds(getDocs(query(collection(db, 'invite_codes'), where('teamId', '==', T))));
  await assertSucceeds(getDoc(doc(db, `teams/${T}/metadata/info`)));
  await assertSucceeds(getDoc(doc(db, `teams/${T}/members/joiner`)));
  await assertSucceeds(setDoc(doc(db, `teams/${T}/members/joiner`), { role: 'member', displayName: 'J', uid: 'joiner', joinedAt: 1 }, M));
  await assertSucceeds(getDoc(doc(db, `teams/${T}/roster/data`)));
  await assertSucceeds(getDocs(query(collection(db, `teams/${T}/messages`), orderBy('timestamp', 'desc'), limit(50))));
  await assertSucceeds(setDoc(doc(db, `teams/${T}/messages/j1`), { text: 'hello', userId: 'joiner', senderName: 'J', timestamp: 5 }));
  await assertSucceeds(getDocs(collection(db, `teams/${T}/sessions`)));
  await assertSucceeds(getDoc(doc(db, `teams/${T}/details/location`)));
});
test('app: member re-join when already member (self update of allowed fields)', async () => {
  await assertSucceeds(setDoc(doc(as('mem'), `teams/${T}/members/mem`), { displayName: 'Mem2', uid: 'mem', rejoinedAt: 2 }, M));
  await assertSucceeds(updateDoc(doc(as('mem'), `teams/${T}/members/mem`), { fcmTokens: ['tok'], tokenUpdatedAt: 3 }));
});
test('app: Select Team / ListMyTeamMemberships collection-group query', async () => {
  await assertSucceeds(getDocs(query(collectionGroup(as('mem'), 'members'), where('uid', '==', 'mem'))));
});
test('app: members list (ListMembers / chat sender names)', async () => {
  await assertSucceeds(getDocs(collection(as('mem'), `teams/${T}/members`)));
  await assertSucceeds(getDocs(collection(as('owner'), `teams/${T}/members`)));
});
test('app: admin manages members (promote, remove) and transfers ownership', async () => {
  const db = as('owner');
  await assertSucceeds(updateDoc(doc(db, `teams/${T}/members/mem`), { role: 'admin' }));
  await assertSucceeds(deleteDoc(doc(db, `teams/${T}/members/mem`)));
  await assertSucceeds(updateDoc(doc(db, `teams/${T}/roster/data`), { controllerUid: '' }));
  await assertSucceeds(deleteDoc(doc(db, `activeControllers/${T}`)));
  await assertSucceeds(updateDoc(doc(db, `teams/${T}/metadata/info`), { createdBy: 'coach2', previousOwnerUid: 'owner' }));
});
test('app: admin edits team metadata and invite code', async () => {
  const db = as('owner');
  await assertSucceeds(updateDoc(doc(db, `teams/${T}/metadata/info`), { teamName: 'U11', lastActivityUtc: 9 }));
  await assertSucceeds(setDoc(doc(db, `invite_codes/NEW222`), { teamId: T, teamName: 'U11', createdBy: 'owner' }));
  await assertSucceeds(deleteDoc(doc(db, `invite_codes/XYZ789`)));
  await assertSucceeds(setDoc(doc(db, `teams/${T}/public/invite`), { inviteCode: 'NEW-222', createdBy: 'owner' }, M));
});
test('app: roster — admin writes, member reads and PATCHes (game control)', async () => {
  await assertSucceeds(setDoc(doc(as('owner'), `teams/${T}/roster/data`), { players: [{ n: 'A' }] }, M));
  await assertSucceeds(getDoc(doc(as('mem'), `teams/${T}/roster/data`)));
  await assertSucceeds(updateDoc(doc(as('mem'), `teams/${T}/roster/data`), { controllerUid: 'mem', heartbeat: 1 }));
});
test('app: activeControllers — admin upsert/delete, member read', async () => {
  await assertSucceeds(setDoc(doc(as('coach2'), `activeControllers/${T}`), { uid: 'coach2' }, M));
  await assertSucceeds(getDoc(doc(as('mem'), `activeControllers/${T}`)));
  await assertSucceeds(deleteDoc(doc(as('owner'), `activeControllers/${T}`)));
});
test('app: details location/kit/duties — admins write, members read', async () => {
  for (const d of ['location', 'kit', 'duties']) {
    await assertSucceeds(setDoc(doc(as('owner'), `teams/${T}/details/${d}`), { v: 'x', updatedByUid: 'owner' }, M));
    await assertSucceeds(getDoc(doc(as('mem'), `teams/${T}/details/${d}`)));
    await assertFails(setDoc(doc(as('mem'), `teams/${T}/details/${d}`), { v: 'y' }, M));
    await assertFails(getDoc(doc(as('stranger'), `teams/${T}/details/${d}`)));
  }
});
test('app: chat — send, react, soft-delete, pin, hard delete', async () => {
  const mem = as('mem'), own = as('owner');
  await assertSucceeds(setDoc(doc(mem, `teams/${T}/messages/n1`), { text: 'yo', userId: 'mem', senderName: 'Mem', timestamp: 3 }));
  await assertFails(setDoc(doc(mem, `teams/${T}/messages/n2`), { text: 'x'.repeat(501), userId: 'mem', senderName: 'Mem', timestamp: 3 }));
  await assertFails(setDoc(doc(mem, `teams/${T}/messages/n3`), { text: 'spoof', userId: 'owner', senderName: 'O', timestamp: 3 }));
  await assertSucceeds(updateDoc(doc(mem, `teams/${T}/messages/m2`), { reactions: { '👍': ['mem'] } }));
  await assertSucceeds(updateDoc(doc(mem, `teams/${T}/messages/m1`), { isDeleted: true, deletedAt: 4 }));
  await assertSucceeds(updateDoc(doc(own, `teams/${T}/messages/m2`), { isPinned: true, pinnedAt: 5, pinnedBy: 'owner' }));
  await assertFails(updateDoc(doc(mem, `teams/${T}/messages/m2`), { isPinned: true, pinnedAt: 5, pinnedBy: 'mem' }));
  await assertSucceeds(deleteDoc(doc(mem, `teams/${T}/messages/n1`)));
  await assertSucceeds(deleteDoc(doc(own, `teams/${T}/messages/m1`)));
});
test('app: sessions — member saves/updates/lists, admin deletes', async () => {
  await assertSucceeds(setDoc(doc(as('mem'), `teams/${T}/sessions/s2`), { sessionId: 's2' }));
  await assertSucceeds(setDoc(doc(as('owner'), `teams/${T}/sessions/s1`), { sessionId: 's1', v: 2 }, M));
  await assertSucceeds(getDoc(doc(as('mem'), `teams/${T}/sessions/s1`)));
  await assertSucceeds(getDocs(collection(as('mem'), `teams/${T}/sessions`)));
  await assertSucceeds(deleteDoc(doc(as('owner'), `teams/${T}/sessions/s2`)));
  await assertFails(deleteDoc(doc(as('mem'), `teams/${T}/sessions/s1`)));
});
test('app: logs — members read/write', async () => {
  await assertSucceeds(setDoc(doc(as('mem'), `teams/${T}/logs/l2`), { x: 2 }));
  await assertSucceeds(getDocs(collection(as('mem'), `teams/${T}/logs`)));
});
test('app: owner recovery rejoin (self member create + ownership fields)', async () => {
  await env.withSecurityRulesDisabled(async (ctx) => deleteDoc(doc(ctx.firestore(), `teams/${T}/members/owner`)));
  const db = as('owner');
  await assertSucceeds(setDoc(doc(db, `teams/${T}/members/owner`), { role: 'admin', uid: 'owner', displayName: 'O' }, M));
  await assertSucceeds(getDoc(doc(db, `teams/${T}/roster/data`)));
});
test('app: owner DeleteTeamAsOwner cascade (members first, then messages/logs/roster/metadata)', async () => {
  const db = as('owner');
  await assertSucceeds(deleteDoc(doc(db, `invite_codes/XYZ789`)));
  for (const u of ['mem', 'coach2', 'owner']) await assertSucceeds(deleteDoc(doc(db, `teams/${T}/members/${u}`)));
  const msgs = await assertSucceeds(getDocs(collection(db, `teams/${T}/messages`)));
  for (const d of msgs.docs) await assertSucceeds(deleteDoc(d.ref));
  await assertSucceeds(getDocs(collection(db, `teams/${T}/sessions`)));
  const logs = await assertSucceeds(getDocs(collection(db, `teams/${T}/logs`)));
  for (const d of logs.docs) await assertSucceeds(deleteDoc(d.ref));
  await assertSucceeds(deleteDoc(doc(db, `teams/${T}/roster/data`)));
});
test('app: legacy per-user paths unchanged', async () => {
  await assertSucceeds(setDoc(doc(as('mem'), `rosters/mem/x/y`), { a: 1 }));
  await assertSucceeds(setDoc(doc(as('mem'), `logs/mem`), { a: 1 }));
  await assertFails(getDoc(doc(as('mem'), `logs/other`)));
});
test('security: non-member denied team data', async () => {
  const db = as('stranger');
  await assertFails(getDoc(doc(db, `teams/${T}/roster/data`)));
  await assertFails(getDocs(collection(db, `teams/${T}/messages`)));
  await assertFails(getDocs(collection(db, `teams/${T}/sessions`)));
  await assertFails(getDocs(collection(db, `teams/${T}/logs`)));
  await assertFails(getDocs(collection(db, `teams/${T}/members`)));
  await assertFails(getDoc(doc(db, `activeControllers/${T}`)));
  await assertFails(updateDoc(doc(db, `teams/${T}/messages/m1`), { reactions: { x: ['stranger'] } }));
  await assertFails(updateDoc(doc(db, `teams/${T}/metadata/info`), { teamName: 'hax' }));
  await assertFails(updateDoc(doc(as('mem'), `teams/${T}/members/mem`), { role: 'admin' }));
});
