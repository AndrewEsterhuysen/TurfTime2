// Club player list rules (docs/club-players-spec.md §4.1, §9).
// Run: npm test (Firestore emulator only; never production).
import { test, before, after, beforeEach } from 'node:test';
import { readFileSync } from 'node:fs';
import { initializeTestEnvironment, assertSucceeds, assertFails } from '@firebase/rules-unit-testing';
import { doc, getDoc, setDoc, updateDoc, getDocs, collection, deleteDoc, Timestamp } from 'firebase/firestore';

let env;
const C = 'clubA';
const OTHER = 'clubB';
const P = `clubs/${C}/players`;
const rulesPath = process.env.RULES_FILE || new URL('../../firestore.rules', import.meta.url);

before(async () => {
  env = await initializeTestEnvironment({
    projectId: 'demo-turftimer-clubplayers',
    firestore: { rules: readFileSync(rulesPath, 'utf8') },
  });
});
after(async () => { await env.cleanup(); });

const seed = async (fn) => env.withSecurityRulesDisabled(async (ctx) => fn(ctx.firestore()));
const as = (uid) => env.authenticatedContext(uid).firestore();

// Users:
//   owner     club creator (metadata.createdBy)
//   cadmin    club member role admin
//   coach     club member (role member) + Admin of club team t1, coachTeamId = t1
//   coach2    Admin of t1 but no coachTeamId recorded yet
//   fakecoach coachTeamId = t1 but only a *member* of t1
//   othercoach Admin of team tx in another club, coachTeamId = tx
//   parent    club member, team member of t1
//   stranger  nothing
beforeEach(async () => {
  await env.clearFirestore();
  await seed(async (db) => {
    await setDoc(doc(db, `clubs/${C}/metadata/info`), { clubName: 'Club A', createdBy: 'owner' });
    await setDoc(doc(db, `clubs/${C}/members/owner`), { uid: 'owner', role: 'owner' });
    await setDoc(doc(db, `clubs/${C}/members/cadmin`), { uid: 'cadmin', role: 'admin' });
    await setDoc(doc(db, `clubs/${C}/members/coach`), { uid: 'coach', role: 'member', coachTeamId: 't1' });
    await setDoc(doc(db, `clubs/${C}/members/coach2`), { uid: 'coach2', role: 'member' });
    await setDoc(doc(db, `clubs/${C}/members/fakecoach`), { uid: 'fakecoach', role: 'member', coachTeamId: 't1' });
    await setDoc(doc(db, `clubs/${C}/members/othercoach`), { uid: 'othercoach', role: 'member', coachTeamId: 'tx' });
    await setDoc(doc(db, `clubs/${C}/members/parent`), { uid: 'parent', role: 'member' });
    await setDoc(doc(db, `clubs/${OTHER}/metadata/info`), { clubName: 'Club B', createdBy: 'bowner' });

    await setDoc(doc(db, `teams/t1/metadata/info`), { teamName: 'U10 Red', createdBy: 'owner', clubId: C });
    await setDoc(doc(db, `teams/t1/members/coach`), { uid: 'coach', role: 'admin' });
    await setDoc(doc(db, `teams/t1/members/coach2`), { uid: 'coach2', role: 'admin' });
    await setDoc(doc(db, `teams/t1/members/fakecoach`), { uid: 'fakecoach', role: 'member' });
    await setDoc(doc(db, `teams/t1/members/parent`), { uid: 'parent', role: 'member' });
    await setDoc(doc(db, `teams/tx/metadata/info`), { teamName: 'B U10', createdBy: 'bowner', clubId: OTHER });
    await setDoc(doc(db, `teams/tx/members/othercoach`), { uid: 'othercoach', role: 'admin' });

    await setDoc(doc(db, `${P}/p1`), {
      playerId: 'p1', name: 'Sam Lee', preferredName: null, nameKey: 'sam lee', dateOfBirth: '2017-03-12',
      teams: [], teamIds: [], status: 'active', createdBy: 'owner', createdAt: Timestamp.now(),
      totals: { matches: 3, goals: 2, assists: 1, potm: 0, minutes: 90 },
    });
  });
});

const newPlayer = (uid, extra = {}) => ({
  playerId: 'p2', name: 'Ava Brown', preferredName: null, nameKey: 'ava brown', dateOfBirth: '2016-07-01',
  teams: [], teamIds: [], status: 'active', createdBy: uid, createdAt: Timestamp.now(),
  updatedAt: Timestamp.now(), lastUsedAt: Timestamp.now(), ...extra,
});

test('club players: owner, club admin and coach can read the list', async () => {
  for (const uid of ['owner', 'cadmin', 'coach']) {
    await assertSucceeds(getDocs(collection(as(uid), P)));
    await assertSucceeds(getDoc(doc(as(uid), `${P}/p1`)));
  }
});

test('club players: parents, strangers, fake and other-club coaches cannot read', async () => {
  for (const uid of ['parent', 'stranger', 'coach2', 'fakecoach', 'othercoach']) {
    await assertFails(getDocs(collection(as(uid), P)));
    await assertFails(getDoc(doc(as(uid), `${P}/p1`)));
  }
  await assertFails(getDocs(collection(env.unauthenticatedContext().firestore(), P)));
});

test('club players: team Admin becomes coach after recording coachTeamId on their membership', async () => {
  const db = as('coach2');
  await assertFails(getDocs(collection(db, P)));
  await assertSucceeds(updateDoc(doc(db, `clubs/${C}/members/coach2`), { coachTeamId: 't1' }));
  await assertSucceeds(getDocs(collection(db, P)));
});

test('club players: coach access ends when they stop being team Admin', async () => {
  await seed(async (db) => setDoc(doc(db, `teams/t1/members/coach`), { uid: 'coach', role: 'member' }));
  await assertFails(getDocs(collection(as('coach'), P)));
});

test('club players: owner, admin and coach can add a player', async () => {
  for (const uid of ['owner', 'cadmin', 'coach']) {
    await assertSucceeds(setDoc(doc(as(uid), `${P}/new_${uid}`), newPlayer(uid, { playerId: `new_${uid}` })));
  }
});

test('club players: parents and strangers cannot add', async () => {
  for (const uid of ['parent', 'stranger', 'fakecoach', 'othercoach']) {
    await assertFails(setDoc(doc(as(uid), `${P}/p2`), newPlayer(uid)));
  }
});

test('club players: create needs a valid full date of birth and a clean payload', async () => {
  const db = as('coach');
  await assertFails(setDoc(doc(db, `${P}/p2`), newPlayer('coach', { dateOfBirth: null })));
  await assertFails(setDoc(doc(db, `${P}/p2`), newPlayer('coach', { dateOfBirth: '2016' })));
  await assertFails(setDoc(doc(db, `${P}/p2`), newPlayer('coach', { dateOfBirth: '2016-13-01' })));
  await assertFails(setDoc(doc(db, `${P}/p2`), newPlayer('coach', { name: '  ' })));
  await assertFails(setDoc(doc(db, `${P}/p2`), newPlayer('coach', { status: 'archived' })));
  await assertFails(setDoc(doc(db, `${P}/p2`), newPlayer('coach', { createdBy: 'owner' })));
  await assertFails(setDoc(doc(db, `${P}/p2`), newPlayer('coach', { playerId: 'other' })));
  await assertFails(setDoc(doc(db, `${P}/p2`), newPlayer('coach', { jersey: 7 })));
  await assertFails(setDoc(doc(db, `${P}/p2`), newPlayer('coach', { teamIds: ['t1'] })));
  const { dateOfBirth, ...noDob } = newPlayer('coach');
  await assertFails(setDoc(doc(db, `${P}/p2`), noDob));
});

test('club players: clients never write stats maps', async () => {
  const stats = { matches: 1, goals: 9, assists: 0, potm: 0, minutes: 40 };
  for (const key of ['totals', 'byTeam', 'borrowed', 'borrowedByTeam']) {
    await assertFails(setDoc(doc(as('owner'), `${P}/p2`), newPlayer('owner', { [key]: stats })));
    await assertFails(updateDoc(doc(as('owner'), `${P}/p1`), { [key]: stats }));
    await assertFails(updateDoc(doc(as('coach'), `${P}/p1`), { [key]: stats }));
  }
});

test('club players: coach may edit name and preferred name only', async () => {
  const db = as('coach');
  await assertSucceeds(updateDoc(doc(db, `${P}/p1`), {
    name: 'Samuel Lee', preferredName: 'Sam', nameKey: 'samuel lee sam', updatedAt: Timestamp.now(), updatedBy: 'coach',
  }));
  await assertFails(updateDoc(doc(db, `${P}/p1`), { dateOfBirth: '2017-03-13' }));
  await assertFails(updateDoc(doc(db, `${P}/p1`), { status: 'archived' }));
  await assertFails(updateDoc(doc(db, `${P}/p1`), { createdBy: 'coach' }));
  await assertFails(deleteDoc(doc(db, `${P}/p1`)));
});

test('club players: owner/admin edit date of birth, archive and delete', async () => {
  await assertSucceeds(updateDoc(doc(as('cadmin'), `${P}/p1`), { dateOfBirth: '2017-03-13' }));
  await assertSucceeds(updateDoc(doc(as('owner'), `${P}/p1`), { status: 'archived' }));
  await assertFails(updateDoc(doc(as('owner'), `${P}/p1`), { status: 'deleted' }));
  await assertFails(updateDoc(doc(as('owner'), `${P}/p1`), { dateOfBirth: '12/03/2017' }));
  await assertSucceeds(deleteDoc(doc(as('cadmin'), `${P}/p1`)));
});

test('club players: parents cannot edit or delete', async () => {
  await assertFails(updateDoc(doc(as('parent'), `${P}/p1`), { name: 'X' }));
  await assertFails(deleteDoc(doc(as('parent'), `${P}/p1`)));
});

test('club members: self-update may set coachTeamId but not role', async () => {
  await assertSucceeds(updateDoc(doc(as('parent'), `clubs/${C}/members/parent`), { coachTeamId: 't1' }));
  await assertFails(updateDoc(doc(as('parent'), `clubs/${C}/members/parent`), { role: 'admin' }));
  // A parent pointing coachTeamId at a team they don't admin still gets nothing.
  await assertFails(getDocs(collection(as('parent'), P)));
});
