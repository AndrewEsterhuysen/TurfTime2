// Club Admin role (to-do item 8, final rules 25 Nov 2026). Run via npm test (emulator only).
// "Club admin" = club Owner or Club Admin.
//   1. Only the club Owner grants/removes Club Admin (promote, or single-use invite <= 7 days).
//   2. No team Admin other than the caller: club admin has full control (no games).
//   3. Another team Admin exists: club admin reads all but chat; edits only Details (location, kit, duties).
//   4. Club admins never run a game.  5. Player assignment follows 2 and 3.
//   7. Club admin who is also team Admin has full team Admin rights.
import { test, before, after, beforeEach } from 'node:test';
import { readFileSync } from 'node:fs';
import { initializeTestEnvironment, assertSucceeds, assertFails } from '@firebase/rules-unit-testing';
import {
  doc, getDoc, setDoc, getDocs, collection, collectionGroup, query, where, deleteDoc, updateDoc, writeBatch, Timestamp,
} from 'firebase/firestore';

let env;
const C = 'c1';
const C2 = 'c2';
const FREE = 'tfree';   // club team, no team Admin (teamAdminCount 0)
const SOLO = 'tsolo';   // club team whose only team Admin is cadmin
const OTHER = 'tother'; // club team with another team Admin (coach); created by cadmin
const M = { merge: true };
const inDays = (d) => Timestamp.fromMillis(Date.now() + d * 86400000);

before(async () => {
  env = await initializeTestEnvironment({
    projectId: 'demo-turftimer-clubadmin',
    firestore: { rules: readFileSync(new URL('../../firestore.rules', import.meta.url), 'utf8') },
  });
});
after(async () => { await env.cleanup(); });
const seed = (fn) => env.withSecurityRulesDisabled(async (ctx) => fn(ctx.firestore()));
const as = (uid) => env.authenticatedContext(uid).firestore();

beforeEach(async () => {
  await env.clearFirestore();
  await seed(async (db) => {
    await setDoc(doc(db, `clubs/${C}/metadata/info`), { clubName: 'FC', createdBy: 'cowner' });
    await setDoc(doc(db, `clubs/${C}/members/cowner`), { uid: 'cowner', role: 'owner' });
    await setDoc(doc(db, `clubs/${C}/members/cadmin`), { uid: 'cadmin', role: 'admin' });
    await setDoc(doc(db, `clubs/${C}/members/cmember`), { uid: 'cmember', role: 'member' });
    await setDoc(doc(db, `clubs/${C2}/metadata/info`), { clubName: 'Other', createdBy: 'o2' });
    await setDoc(doc(db, `clubs/${C2}/members/admin2`), { uid: 'admin2', role: 'admin' });

    for (const [t, count] of [[FREE, 0], [SOLO, 1], [OTHER, 1]]) {
      await setDoc(doc(db, `teams/${t}/metadata/info`), {
        teamName: t, createdBy: 'cadmin', clubId: C, clubName: 'FC', kind: 'clubbed', teamAdminCount: count,
      });
      await setDoc(doc(db, `teams/${t}/roster/data`), { version: 2, players: [] });
      await setDoc(doc(db, `teams/${t}/secrets/info`), { inviteCode: 'X', adminCodeHash: 'a'.repeat(64) });
      await setDoc(doc(db, `teams/${t}/secrets/invite`), { inviteCode: 'X' });
      await setDoc(doc(db, `teams/${t}/members/parent`), { uid: 'parent', role: 'member' });
      await setDoc(doc(db, `teams/${t}/sessions/s1`), { endTime: Timestamp.fromMillis(Date.now() - 3 * 86400000) });
      await setDoc(doc(db, `teams/${t}/sessions/s1/nominations/parent`), { nominatorId: 'parent' });
      await setDoc(doc(db, `teams/${t}/logs/l1`), { x: 1 });
      await setDoc(doc(db, `teams/${t}/messages/m1`), { userId: 'parent', text: 'hi' });
      await setDoc(doc(db, `teams/${t}/details/kit`), { shirt: 'red' });
      await setDoc(doc(db, `teams/${t}/details/settings`), { nominationsEnabled: 'true' });
    }
    await setDoc(doc(db, `teams/${OTHER}/members/coach`), { uid: 'coach', role: 'admin' });
    await setDoc(doc(db, `teams/${SOLO}/members/cadmin`), { uid: 'cadmin', role: 'admin' });
    // Open nominations on OTHER (session ended 1 hour ago).
    await setDoc(doc(db, `teams/${OTHER}/sessions/s2`), { endTime: Timestamp.fromMillis(Date.now() - 3600000) });
    await setDoc(doc(db, `teams/${OTHER}/sessions/s2/nominations/parent`), { nominatorId: 'parent' });
    // Legacy club team: no teamAdminCount yet (treated as handed over until backfilled).
    await setDoc(doc(db, 'teams/tlegacy/metadata/info'), { teamName: 'Old', createdBy: 'x', clubId: C });
    await setDoc(doc(db, 'teams/tlegacy/roster/data'), { version: 2, players: [] });
  });
});

// ── Granting Club Admin ────────────────────────────────────────────────────

test('club admin invite: only the club owner can create, read or revoke; max 7 days', async () => {
  const inv = (by, days = 2) => ({ createdBy: by, createdAt: Timestamp.now(), expiresAt: inDays(days) });
  await assertSucceeds(setDoc(doc(as('cowner'), `clubs/${C}/adminInvites/CODE1234`), inv('cowner')));
  await assertFails(setDoc(doc(as('cadmin'), `clubs/${C}/adminInvites/CODE9999`), inv('cadmin')));
  await assertFails(setDoc(doc(as('stranger'), `clubs/${C}/adminInvites/CODE9999`), inv('stranger')));
  await assertFails(setDoc(doc(as('cowner'), `clubs/${C}/adminInvites/CODE8888`), inv('cowner', 8)));
  await assertSucceeds(getDoc(doc(as('cowner'), `clubs/${C}/adminInvites/CODE1234`)));
  await assertFails(getDoc(doc(as('stranger'), `clubs/${C}/adminInvites/CODE1234`)));
  await assertFails(getDocs(collection(as('cadmin'), `clubs/${C}/adminInvites`)));
  await assertFails(deleteDoc(doc(as('cadmin'), `clubs/${C}/adminInvites/CODE1234`)));
  await assertSucceeds(deleteDoc(doc(as('cowner'), `clubs/${C}/adminInvites/CODE1234`)));
});

test('club admin invite: redeem once in a batch with the invite delete; no team invite needed', async () => {
  await seed((db) => setDoc(doc(db, `clubs/${C}/adminInvites/GOODCODE`), {
    createdBy: 'cowner', createdAt: Timestamp.now(), expiresAt: inDays(2),
  }));
  // Without deleting the invite in the same batch: refused.
  await assertFails(setDoc(doc(as('newadmin'), `clubs/${C}/members/newadmin`), {
    uid: 'newadmin', role: 'admin', adminInviteCode: 'GOODCODE',
  }));
  // Wrong code.
  const eve = as('eve');
  const bad = writeBatch(eve);
  bad.set(doc(eve, `clubs/${C}/members/eve`), { uid: 'eve', role: 'admin', adminInviteCode: 'NOPE' });
  await assertFails(bad.commit());
  // Correct code + delete invite.
  const db = as('newadmin');
  const b = writeBatch(db);
  b.set(doc(db, `clubs/${C}/members/newadmin`), { uid: 'newadmin', role: 'admin', adminInviteCode: 'GOODCODE' });
  b.delete(doc(db, `clubs/${C}/adminInvites/GOODCODE`));
  await assertSucceeds(b.commit());
  // Used up: a second person cannot reuse it.
  const db2 = as('second');
  const b2 = writeBatch(db2);
  b2.set(doc(db2, `clubs/${C}/members/second`), { uid: 'second', role: 'admin', adminInviteCode: 'GOODCODE' });
  b2.delete(doc(db2, `clubs/${C}/adminInvites/GOODCODE`));
  await assertFails(b2.commit());
});

test('club admin invite: expired invite refused; existing club member can upgrade with a valid one', async () => {
  await seed(async (db) => {
    await setDoc(doc(db, `clubs/${C}/adminInvites/OLDCODE1`), { createdBy: 'cowner', expiresAt: inDays(-1) });
    await setDoc(doc(db, `clubs/${C}/adminInvites/NEWCODE1`), { createdBy: 'cowner', expiresAt: inDays(1) });
  });
  const x = as('late');
  const b = writeBatch(x);
  b.set(doc(x, `clubs/${C}/members/late`), { uid: 'late', role: 'admin', adminInviteCode: 'OLDCODE1' });
  b.delete(doc(x, `clubs/${C}/adminInvites/OLDCODE1`));
  await assertFails(b.commit());

  const m = as('cmember');
  await assertFails(updateDoc(doc(m, `clubs/${C}/members/cmember`), { role: 'admin' }));
  const b2 = writeBatch(m);
  b2.update(doc(m, `clubs/${C}/members/cmember`), { role: 'admin', adminInviteCode: 'NEWCODE1' });
  b2.delete(doc(m, `clubs/${C}/adminInvites/NEWCODE1`));
  await assertSucceeds(b2.commit());
});

test('club admin: only the owner can grant or remove the role; club admins cannot', async () => {
  await assertFails(updateDoc(doc(as('cadmin'), `clubs/${C}/members/cmember`), { role: 'admin' }));
  await assertFails(setDoc(doc(as('cadmin'), `clubs/${C}/members/friend`), { uid: 'friend', role: 'admin' }));
  await assertFails(deleteDoc(doc(as('cadmin'), `clubs/${C}/members/cowner`)));
  await assertSucceeds(updateDoc(doc(as('cowner'), `clubs/${C}/members/cmember`), { role: 'admin' }));
  await assertSucceeds(updateDoc(doc(as('cowner'), `clubs/${C}/members/cadmin`), { role: 'member' }));
  await assertSucceeds(deleteDoc(doc(as('cowner'), `clubs/${C}/members/cmember`)));
});

// ── Rule 2: full control when no other team Admin ────────────────────────

test('create (build 78 order): club admin creates a club team, writes secrets, takes the Admin seat', async () => {
  const db = as('cadmin'); const t = 'tnew';
  await assertFails(setDoc(doc(db, `teams/${t}/metadata/info`), {
    teamName: 'New', createdBy: 'cadmin', clubId: C, kind: 'clubbed', teamAdminCount: 1,
  }, M));
  await assertSucceeds(setDoc(doc(db, `teams/${t}/metadata/info`), {
    teamName: 'New', createdBy: 'cadmin', clubId: C, clubName: 'FC', kind: 'clubbed', teamAdminCount: 0,
  }, M));
  await assertSucceeds(setDoc(doc(db, `teams/${t}/secrets/info`), { inviteCode: 'N-1', adminCodeHash: 'c'.repeat(64), creatorEmail: '' }, M));
  await assertSucceeds(setDoc(doc(db, `teams/${t}/secrets/invite`), { inviteCode: 'N-1' }, M));
  await assertSucceeds(setDoc(doc(db, `teams/${t}/members/cadmin`), { role: 'admin', uid: 'cadmin', displayName: 'CA' }, M));
  await assertSucceeds(setDoc(doc(db, `teams/${t}/roster/data`), { version: 2, players: [] }, M));
  await assertSucceeds(setDoc(doc(db, `invite_codes/N1`), { teamId: t, teamName: 'New', createdBy: 'cadmin' }, M));
  await assertFails(setDoc(doc(as('stranger'), 'teams/tbad/metadata/info'), {
    teamName: 'Bad', createdBy: 'stranger', clubId: C, teamAdminCount: 0,
  }));
});

test('create (older builds, no teamAdminCount): only the creator may take the Admin seat', async () => {
  await seed((db) => setDoc(doc(db, `clubs/${C}/members/cadmin2`), { uid: 'cadmin2', role: 'admin' }));
  const db = as('cadmin'); const t = 'told';
  await assertSucceeds(setDoc(doc(db, `teams/${t}/metadata/info`), { teamName: 'Old', createdBy: 'cadmin', clubId: C, clubName: 'FC' }, M));
  await assertSucceeds(setDoc(doc(db, `teams/${t}/secrets/info`), { inviteCode: 'O-1', adminCodeHash: 'd'.repeat(64), creatorEmail: '' }, M));
  await assertFails(setDoc(doc(as('cadmin2'), `teams/${t}/members/cadmin2`), { role: 'admin', uid: 'cadmin2' }, M));
  await assertSucceeds(setDoc(doc(db, `teams/${t}/members/cadmin`), { role: 'admin', uid: 'cadmin' }, M));
});

test('full control: club owner and admin edit team, roster players, settings, invite and secrets', async () => {
  for (const uid of ['cadmin', 'cowner']) {
    const db = as(uid);
    await assertSucceeds(updateDoc(doc(db, `teams/${FREE}/metadata/info`), { teamName: 'U11 Blue', ageGroup: 'U11', season: 2027, maxAgeOn1Jan: 10 }));
    await assertSucceeds(updateDoc(doc(db, `teams/${FREE}/roster/data`), { players: [{ name: 'A' }], lastModified: 1 }));
    await assertSucceeds(setDoc(doc(db, `teams/${FREE}/details/settings`), { nominationsEnabled: 'false' }));
    await assertSucceeds(setDoc(doc(db, `teams/${FREE}/details/kit`), { shirt: 'blue' }));
    await assertSucceeds(getDoc(doc(db, `teams/${FREE}/secrets/info`)));
    await assertSucceeds(setDoc(doc(db, `teams/${FREE}/secrets/invite`), { inviteCode: 'Y' }));
    await assertSucceeds(setDoc(doc(db, `teams/${FREE}/secrets/info`), { inviteCode: 'Y' }, M));
  }
  const db = as('cadmin');
  await assertFails(updateDoc(doc(db, `teams/${FREE}/metadata/info`), { clubId: C2 }));
  await assertFails(updateDoc(doc(db, `teams/${FREE}/metadata/info`), { createdBy: 'someone' }));
  await assertFails(updateDoc(doc(db, `teams/${FREE}/metadata/info`), { teamAdminCount: 5 }));
  for (const uid of ['cmember', 'admin2', 'stranger']) {
    await assertFails(updateDoc(doc(as(uid), `teams/${FREE}/metadata/info`), { teamName: 'Hack' }));
    await assertFails(updateDoc(doc(as(uid), `teams/${FREE}/roster/data`), { players: [] }));
    await assertFails(getDoc(doc(as(uid), `teams/${FREE}/secrets/info`)));
    await assertFails(setDoc(doc(as(uid), `teams/${FREE}/details/kit`), { shirt: 'x' }));
  }
});

test('full control: members and roles (promote, remove, join as Admin or Member)', async () => {
  const db = as('cadmin');
  await assertSucceeds(updateDoc(doc(db, `teams/${FREE}/members/parent`), { role: 'admin' }));
  await seed((d) => setDoc(doc(d, `teams/${FREE}/members/parent`), { uid: 'parent', role: 'member' }));
  await assertFails(updateDoc(doc(db, `teams/${FREE}/members/parent`), { role: 'owner' }));
  await assertSucceeds(deleteDoc(doc(db, `teams/${FREE}/members/parent`)));
  await assertSucceeds(setDoc(doc(as('cowner'), `teams/${FREE}/members/cowner`), { role: 'member', uid: 'cowner' }));
  await assertSucceeds(setDoc(doc(db, `teams/${FREE}/members/cadmin`), { role: 'admin', uid: 'cadmin' }));
  // Cannot add someone else.
  await assertFails(setDoc(doc(db, `teams/${FREE}/members/friend`), { role: 'admin', uid: 'friend' }));
});

test('full control: delete the team (metadata, sessions, logs, settings, secrets); not chat', async () => {
  const db = as('cadmin');
  await assertSucceeds(deleteDoc(doc(db, `teams/${FREE}/sessions/s1/nominations/parent`)));
  await assertSucceeds(deleteDoc(doc(db, `teams/${FREE}/sessions/s1`)));
  await assertSucceeds(deleteDoc(doc(db, `teams/${FREE}/logs/l1`)));
  await assertSucceeds(deleteDoc(doc(db, `teams/${FREE}/details/settings`)));
  await assertSucceeds(deleteDoc(doc(db, `teams/${FREE}/secrets/invite`)));
  await assertSucceeds(deleteDoc(doc(db, `teams/${FREE}/secrets/info`)));
  await assertSucceeds(deleteDoc(doc(db, `teams/${FREE}/roster/data`)));
  // Chat stays team-members only; the deleteTeamTree Cloud Function removes it.
  await assertFails(deleteDoc(doc(db, `teams/${FREE}/messages/m1`)));
  await assertSucceeds(deleteDoc(doc(db, `teams/${FREE}/metadata/info`)));
});

// ── Rule 4: never run a game ───────────────────────────────────────────────

test('club admins never run a game: no game state, clock, sessions or control', async () => {
  for (const t of [FREE, OTHER]) {
    const db = as('cadmin');
    await assertFails(updateDoc(doc(db, `teams/${t}/roster/data`), { timerRunning: true }));
    await assertFails(updateDoc(doc(db, `teams/${t}/roster/data`), { controllerUid: 'cadmin' }));
    await assertFails(updateDoc(doc(db, `teams/${t}/roster/data`), { teamAScore: 1 }));
    await assertFails(setDoc(doc(db, `teams/${t}/sessions/s9`), { x: 1 }));
    await assertFails(setDoc(doc(db, `activeControllers/${t}`), { uid: 'cadmin' }));
    await assertFails(getDoc(doc(db, `teams/${t}/messages/m1`)));
    await assertFails(setDoc(doc(db, `teams/${t}/messages/m9`), { userId: 'cadmin', text: 'x' }));
  }
  // Not even player edits while the clock is running.
  await seed((d) => setDoc(doc(d, `teams/${FREE}/roster/data`), { timerRunning: true }, M));
  await assertFails(updateDoc(doc(as('cadmin'), `teams/${FREE}/roster/data`), { players: [{ name: 'B' }] }));
});

// ── Rule 7: club admin who is the team Admin ──────────────────────────────

test('club admin who is the team Admin has full team Admin rights, including games', async () => {
  const db = as('cadmin');
  await assertSucceeds(updateDoc(doc(db, `teams/${SOLO}/roster/data`), { timerRunning: true, controllerUid: 'cadmin' }));
  await assertSucceeds(setDoc(doc(db, `teams/${SOLO}/sessions/s9`), { x: 1 }));
  await assertSucceeds(getDoc(doc(db, `teams/${SOLO}/messages/m1`)));
  await assertSucceeds(updateDoc(doc(db, `teams/${SOLO}/members/parent`), { role: 'admin' }));
});

test('club owner on a team whose only Admin is a club admin is limited (another Admin exists)', async () => {
  const db = as('cowner');
  await assertFails(updateDoc(doc(db, `teams/${SOLO}/roster/data`), { players: [] }));
  await assertFails(deleteDoc(doc(db, `teams/${SOLO}/members/cadmin`)));
  await assertSucceeds(setDoc(doc(db, `teams/${SOLO}/details/kit`), { shirt: 'gold' }));
});

// ── Rule 3: another team Admin exists ─────────────────────────────────────

test('another team Admin: club admins read everything except chat', async () => {
  for (const uid of ['cadmin', 'cowner']) {
    const db = as(uid);
    await assertSucceeds(getDocs(collection(db, `teams/${OTHER}/sessions`)));
    await assertSucceeds(getDocs(collection(db, `teams/${OTHER}/sessions/s2/nominations`))); // still open
    await assertSucceeds(getDoc(doc(db, `teams/${OTHER}/logs/l1`)));
    await assertSucceeds(getDoc(doc(db, `teams/${OTHER}/roster/data`)));
    await assertSucceeds(getDocs(collection(db, `teams/${OTHER}/members`)));
    await assertSucceeds(getDoc(doc(db, `teams/${OTHER}/details/settings`)));
    await assertSucceeds(getDoc(doc(db, `teams/${OTHER}/secrets/invite`)));
    await assertFails(getDoc(doc(db, `teams/${OTHER}/secrets/info`)));
    await assertFails(getDocs(collection(db, `teams/${OTHER}/messages`)));
  }
  await assertFails(getDoc(doc(as('admin2'), `teams/${OTHER}/sessions/s1`)));
  await assertFails(getDoc(doc(as('cmember'), `teams/${OTHER}/sessions/s1`)));
});

test('another team Admin: club admins edit Details (location, kit, duties) only', async () => {
  for (const uid of ['cadmin', 'cowner']) {
    const db = as(uid);
    await assertSucceeds(setDoc(doc(db, `teams/${OTHER}/details/location`), { venue: 'Oval' }));
    await assertSucceeds(setDoc(doc(db, `teams/${OTHER}/details/kit`), { shirt: 'white' }));
    await assertSucceeds(setDoc(doc(db, `teams/${OTHER}/details/duties`), { duties: 'Oranges' }));
    await assertFails(setDoc(doc(db, `teams/${OTHER}/details/settings`), { nominationsEnabled: 'false' }));
    await assertFails(updateDoc(doc(db, `teams/${OTHER}/metadata/info`), { teamName: 'X' }));
    await assertFails(updateDoc(doc(db, `teams/${OTHER}/roster/data`), { players: [] }));
    await assertFails(updateDoc(doc(db, `teams/${OTHER}/members/parent`), { role: 'admin' }));
    await assertFails(deleteDoc(doc(db, `teams/${OTHER}/members/parent`)));
    await assertFails(deleteDoc(doc(db, `teams/${OTHER}/members/coach`)));
    await assertFails(setDoc(doc(db, `teams/${OTHER}/members/${uid}`), { role: 'admin', uid }));
    await assertFails(setDoc(doc(db, `teams/${OTHER}/secrets/invite`), { inviteCode: 'Z' }));
    await assertFails(deleteDoc(doc(db, `teams/${OTHER}/sessions/s1`)));
    await assertFails(deleteDoc(doc(db, `teams/${OTHER}/metadata/info`)));
  }
});

test('creator of a club team has no standing powers once another Admin runs it (no hidden override)', async () => {
  // cadmin created OTHER (metadata.createdBy) but is not a member there.
  const db = as('cadmin');
  await assertFails(getDoc(doc(db, `teams/${OTHER}/messages/m1`)));
  await assertFails(setDoc(doc(db, `teams/${OTHER}/members/cadmin`), { role: 'admin', uid: 'cadmin' }));
  await assertFails(deleteDoc(doc(db, `teams/${OTHER}/logs/l1`)));
});

test('legacy club team without teamAdminCount is treated as having another Admin', async () => {
  await assertFails(updateDoc(doc(as('cadmin'), 'teams/tlegacy/roster/data'), { players: [] }));
  await assertSucceeds(getDoc(doc(as('cadmin'), 'teams/tlegacy/roster/data')));
});

test('team admin cannot set teamAdminCount; can still edit the team', async () => {
  const db = as('coach');
  await assertFails(updateDoc(doc(db, `teams/${OTHER}/metadata/info`), { teamAdminCount: 0 }));
  await assertSucceeds(updateDoc(doc(db, `teams/${OTHER}/metadata/info`), { teamName: 'Coach name' }));
});

test('team admin leaves; once the count reaches 0 club admins have full control again', async () => {
  await assertSucceeds(deleteDoc(doc(as('coach'), `teams/${OTHER}/members/coach`)));
  await seed((db) => setDoc(doc(db, `teams/${OTHER}/metadata/info`), { teamAdminCount: 0 }, M));
  await assertSucceeds(updateDoc(doc(as('cadmin'), `teams/${OTHER}/roster/data`), { players: [] }));
});

test('club teams list: club admins can query their club teams; others cannot', async () => {
  await assertSucceeds(getDocs(query(collectionGroup(as('cadmin'), 'metadata'), where('clubId', '==', C))));
  await assertSucceeds(getDocs(query(collectionGroup(as('cowner'), 'metadata'), where('clubId', '==', C))));
  await assertFails(getDocs(query(collectionGroup(as('cmember'), 'metadata'), where('clubId', '==', C))));
  await assertFails(getDocs(query(collectionGroup(as('admin2'), 'metadata'), where('clubId', '==', C))));
  await assertFails(getDocs(collectionGroup(as('cadmin'), 'metadata')));
});
