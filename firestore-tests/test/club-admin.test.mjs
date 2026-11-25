// Club Admin role (to-do item 8, design agreed 24 Nov 2026). Run via npm test (emulator only).
//   - Only the club Owner grants Club Admin (single-use invite, no team invite needed).
//   - Club team with no team Admin (teamAdminCount 0) is club-managed: club Owner/Admins edit it.
//   - Once a team Admin exists it is handed over: club Owner/Admins read matches/stats/reports only.
//   - Club Owner may remove a team's Admins; a team Admin may leave.
import { test, before, after, beforeEach } from 'node:test';
import { readFileSync } from 'node:fs';
import { initializeTestEnvironment, assertSucceeds, assertFails } from '@firebase/rules-unit-testing';
import {
  doc, getDoc, setDoc, getDocs, collection, deleteDoc, updateDoc, writeBatch, Timestamp,
} from 'firebase/firestore';

let env;
const C = 'c1';
const C2 = 'c2';
const MANAGED = 'tm';   // club team, no team Admin
const HANDED = 'th';    // club team with a team Admin (coach)
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

    for (const [t, count] of [[MANAGED, 0], [HANDED, 1]]) {
      await setDoc(doc(db, `teams/${t}/metadata/info`), {
        teamName: t, createdBy: 'creator', clubId: C, clubName: 'FC', kind: 'clubbed', teamAdminCount: count,
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
    }
    await setDoc(doc(db, `teams/${HANDED}/members/coach`), { uid: 'coach', role: 'admin' });
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

// ── Club-managed team ──────────────────────────────────────────────────────

test('club-managed: club admin creates a team without joining it (teamAdminCount must start at 0)', async () => {
  const db = as('cadmin');
  await assertFails(setDoc(doc(db, 'teams/tnew/metadata/info'), {
    teamName: 'New', createdBy: 'cadmin', clubId: C, kind: 'clubbed', teamAdminCount: 1,
  }));
  await assertSucceeds(setDoc(doc(db, 'teams/tnew/metadata/info'), {
    teamName: 'New', createdBy: 'cadmin', clubId: C, kind: 'clubbed', teamAdminCount: 0,
  }));
  await assertSucceeds(setDoc(doc(db, 'teams/tnew/roster/data'), { version: 2, players: [] }));
  await assertSucceeds(setDoc(doc(db, 'teams/tnew/secrets/invite'), { inviteCode: 'NEW1' }));
  // Non-club user cannot create a team in this club.
  await assertFails(setDoc(doc(as('stranger'), 'teams/tbad/metadata/info'), {
    teamName: 'Bad', createdBy: 'stranger', clubId: C, teamAdminCount: 0,
  }));
});

test('club-managed: club owner and admin edit name, age group, season, roster and details', async () => {
  for (const uid of ['cadmin', 'cowner']) {
    const db = as(uid);
    await assertSucceeds(updateDoc(doc(db, `teams/${MANAGED}/metadata/info`), { teamName: 'U11 Blue', ageGroup: 'U11', season: 2027, maxAgeOn1Jan: 10 }));
    await assertSucceeds(setDoc(doc(db, `teams/${MANAGED}/roster/data`), { version: 2, players: [{ name: 'A' }] }));
    await assertSucceeds(setDoc(doc(db, `teams/${MANAGED}/details/kit`), { shirt: 'blue' }));
    await assertSucceeds(getDoc(doc(db, `teams/${MANAGED}/secrets/invite`)));
    await assertSucceeds(getDoc(doc(db, `teams/${MANAGED}/secrets/info`)));
    await assertSucceeds(setDoc(doc(db, `teams/${MANAGED}/secrets/invite`), { inviteCode: 'Y' }));
  }
  // Locked fields: clubId, createdBy, teamAdminCount.
  const db = as('cadmin');
  await assertFails(updateDoc(doc(db, `teams/${MANAGED}/metadata/info`), { clubId: C2 }));
  await assertFails(updateDoc(doc(db, `teams/${MANAGED}/metadata/info`), { createdBy: 'someone' }));
  await assertFails(updateDoc(doc(db, `teams/${MANAGED}/metadata/info`), { teamAdminCount: 5 }));
  // Club member (not admin) and other-club admin: nothing.
  for (const uid of ['cmember', 'admin2', 'stranger']) {
    await assertFails(updateDoc(doc(as(uid), `teams/${MANAGED}/metadata/info`), { teamName: 'Hack' }));
    await assertFails(setDoc(doc(as(uid), `teams/${MANAGED}/roster/data`), { version: 2, players: [] }));
    await assertFails(getDoc(doc(as(uid), `teams/${MANAGED}/secrets/info`)));
  }
});

test('club-managed: club admin can delete the team (members, sessions, chat, logs, metadata)', async () => {
  const db = as('cadmin');
  await assertSucceeds(deleteDoc(doc(db, `teams/${MANAGED}/members/parent`)));
  await assertSucceeds(deleteDoc(doc(db, `teams/${MANAGED}/sessions/s1/nominations/parent`)));
  await assertSucceeds(deleteDoc(doc(db, `teams/${MANAGED}/sessions/s1`)));
  await assertSucceeds(deleteDoc(doc(db, `teams/${MANAGED}/messages/m1`)));
  await assertSucceeds(deleteDoc(doc(db, `teams/${MANAGED}/logs/l1`)));
  await assertSucceeds(deleteDoc(doc(db, `teams/${MANAGED}/details/kit`)));
  await assertSucceeds(deleteDoc(doc(db, `teams/${MANAGED}/secrets/invite`)));
  await assertSucceeds(deleteDoc(doc(db, `teams/${MANAGED}/secrets/info`)));
  await assertSucceeds(deleteDoc(doc(db, `teams/${MANAGED}/roster/data`)));
  await assertSucceeds(deleteDoc(doc(db, `teams/${MANAGED}/metadata/info`)));
});

test('club-managed: club admin does not get chat read or game writes', async () => {
  const db = as('cadmin');
  await assertFails(getDoc(doc(db, `teams/${MANAGED}/messages/m1`)));
  await assertFails(setDoc(doc(db, `teams/${MANAGED}/sessions/s2`), { x: 1 }));
  await assertFails(setDoc(doc(db, `teams/${MANAGED}/messages/m2`), { userId: 'cadmin', text: 'x' }));
});

// ── Handed-over team: read only ────────────────────────────────────────────

test('handed over: club owner and admin can read matches, stats and reports', async () => {
  for (const uid of ['cadmin', 'cowner']) {
    const db = as(uid);
    await assertSucceeds(getDoc(doc(db, `teams/${HANDED}/sessions/s1`)));
    await assertSucceeds(getDocs(collection(db, `teams/${HANDED}/sessions`)));
    await assertSucceeds(getDocs(collection(db, `teams/${HANDED}/sessions/s1/nominations`)));
    await assertSucceeds(getDoc(doc(db, `teams/${HANDED}/logs/l1`)));
    await assertSucceeds(getDoc(doc(db, `teams/${HANDED}/roster/data`)));
    await assertSucceeds(getDocs(collection(db, `teams/${HANDED}/members`)));
    await assertSucceeds(getDoc(doc(db, `teams/${HANDED}/details/kit`)));
  }
  await assertFails(getDoc(doc(as('admin2'), `teams/${HANDED}/sessions/s1`)));
  await assertFails(getDoc(doc(as('cmember'), `teams/${HANDED}/sessions/s1`)));
});

test('handed over: club owner and admin cannot change the team in any way', async () => {
  for (const uid of ['cadmin', 'cowner']) {
    const db = as(uid);
    await assertFails(updateDoc(doc(db, `teams/${HANDED}/metadata/info`), { teamName: 'X' }));
    await assertFails(updateDoc(doc(db, `teams/${HANDED}/metadata/info`), { ageGroup: 'U12', season: 2027 }));
    await assertFails(setDoc(doc(db, `teams/${HANDED}/roster/data`), { version: 2, players: [] }));
    await assertFails(setDoc(doc(db, `teams/${HANDED}/details/kit`), { shirt: 'x' }));
    await assertFails(deleteDoc(doc(db, `teams/${HANDED}/sessions/s1`)));
    await assertFails(deleteDoc(doc(db, `teams/${HANDED}/logs/l1`)));
    await assertFails(deleteDoc(doc(db, `teams/${HANDED}/members/parent`)));
    await assertFails(getDoc(doc(db, `teams/${HANDED}/secrets/invite`)));
    await assertFails(getDoc(doc(db, `teams/${HANDED}/messages/m1`)));
    await assertFails(setDoc(doc(db, `teams/${HANDED}/secrets/invite`), { inviteCode: 'Z' }));
  }
  await assertFails(deleteDoc(doc(as('cowner'), `teams/${HANDED}/metadata/info`)));
  await assertFails(deleteDoc(doc(as('cadmin'), `teams/${HANDED}/metadata/info`)));
});

test('legacy club team without teamAdminCount is treated as handed over', async () => {
  await assertFails(setDoc(doc(as('cadmin'), 'teams/tlegacy/roster/data'), { version: 2, players: [] }));
  await assertSucceeds(getDoc(doc(as('cadmin'), 'teams/tlegacy/roster/data')));
});

test('handed over: team admin cannot set teamAdminCount; can still edit the team', async () => {
  const db = as('coach');
  await assertFails(updateDoc(doc(db, `teams/${HANDED}/metadata/info`), { teamAdminCount: 0 }));
  await assertSucceeds(updateDoc(doc(db, `teams/${HANDED}/metadata/info`), { teamName: 'Coach name' }));
});

// ── No override, no hand-back ──────────────────────────────────────────────

test('club owner can remove a team admin; club admin cannot; team admin can leave', async () => {
  await assertFails(deleteDoc(doc(as('cadmin'), `teams/${HANDED}/members/coach`)));
  await assertFails(deleteDoc(doc(as('cowner'), `teams/${HANDED}/members/parent`)));
  await assertSucceeds(deleteDoc(doc(as('cowner'), `teams/${HANDED}/members/coach`)));
  await seed((db) => setDoc(doc(db, `teams/${HANDED}/members/coach`), { uid: 'coach', role: 'admin' }));
  await assertSucceeds(deleteDoc(doc(as('coach'), `teams/${HANDED}/members/coach`)));
});

test('back to club-managed once the count reaches 0 (set by the Cloud Function)', async () => {
  await seed(async (db) => {
    await deleteDoc(doc(db, `teams/${HANDED}/members/coach`));
    await setDoc(doc(db, `teams/${HANDED}/metadata/info`), { teamAdminCount: 0 }, M);
  });
  await assertSucceeds(setDoc(doc(as('cadmin'), `teams/${HANDED}/roster/data`), { version: 2, players: [] }));
});

test('club admin cannot make themselves team admin of a club team', async () => {
  // (The team's metadata creator can, as today; see the open question on creator powers.)
  await assertFails(setDoc(doc(as('cadmin'), `teams/${MANAGED}/members/cadmin`), { uid: 'cadmin', role: 'admin' }));
  await assertFails(setDoc(doc(as('cadmin'), `teams/${MANAGED}/members/cadmin`), { uid: 'cadmin', role: 'member' }));
});
