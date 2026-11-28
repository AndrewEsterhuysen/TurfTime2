// Club owner way back in (item 8, decision 1): the club Owner (not club admins) may remove a
// team Admin of a CLUB team at any time. Club admins cannot; it only applies to club teams
// and to members whose role is admin. Removals are recorded at clubs/{c}/auditLog.
import { test, before, after, beforeEach } from 'node:test';
import { readFileSync } from 'node:fs';
import { initializeTestEnvironment, assertSucceeds, assertFails } from '@firebase/rules-unit-testing';
import { doc, setDoc, getDoc, deleteDoc, updateDoc, serverTimestamp, Timestamp } from 'firebase/firestore';

let env;
const C = 'c1';
const T = 'tclub';
const ago = (hours) => Timestamp.fromMillis(Date.now() - hours * 3600000);

before(async () => {
  env = await initializeTestEnvironment({
    projectId: 'demo-turftimer-recovery',
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
    await setDoc(doc(db, 'clubs/c2/metadata/info'), { clubName: 'Other', createdBy: 'o2' });
    await setDoc(doc(db, `teams/${T}/metadata/info`), {
      teamName: 'U10', createdBy: 'coach', clubId: C, clubName: 'FC', kind: 'clubbed', teamAdminCount: 2,
    });
    await setDoc(doc(db, `teams/${T}/members/coach`), { uid: 'coach', role: 'admin' });
    await setDoc(doc(db, `teams/${T}/members/fresh`), { uid: 'fresh', role: 'admin' });
    await setDoc(doc(db, `teams/${T}/members/oldmember`), { uid: 'oldmember', role: 'member' });
    // Nickname (non-club) team: owner of club C has no standing here.
    await setDoc(doc(db, 'teams/tnick/metadata/info'), { teamName: 'Nick', createdBy: 'nickowner', kind: 'nickname', teamAdminCount: 1 });
    await setDoc(doc(db, 'teams/tnick/members/nickadmin'), { uid: 'nickadmin', role: 'admin' });
    // A club team of the other club.
    await setDoc(doc(db, 'teams/tother/metadata/info'), { teamName: 'O', createdBy: 'x', clubId: 'c2', kind: 'clubbed', teamAdminCount: 1 });
    await setDoc(doc(db, 'teams/tother/members/oadmin'), { uid: 'oadmin', role: 'admin' });
  });
});

test('owner may remove a team Admin of a club team at any time', async () => {
  await assertSucceeds(deleteDoc(doc(as('cowner'), `teams/${T}/members/coach`)));
  await assertSucceeds(deleteDoc(doc(as('cowner'), `teams/${T}/members/fresh`)));
});

test('a club admin can never remove a team Admin this way', async () => {
  await assertFails(deleteDoc(doc(as('cadmin'), `teams/${T}/members/coach`)));
});

test('non-club team: no removal, and the owner of another club has no standing', async () => {
  await assertFails(deleteDoc(doc(as('cowner'), 'teams/tnick/members/nickadmin')));
  await assertFails(deleteDoc(doc(as('cowner'), 'teams/tother/members/oadmin')));
});

test('owner is blocked when the target is not a team Admin', async () => {
  await assertFails(deleteDoc(doc(as('cowner'), `teams/${T}/members/oldmember`)));
});

test('after removal the team has no other Admin: club owner gets full control back', async () => {
  await assertSucceeds(deleteDoc(doc(as('cowner'), `teams/${T}/members/coach`)));
  // Function recounts admins; simulate it.
  await seed(async (db) => {
    await deleteDoc(doc(db, `teams/${T}/members/fresh`));
    await setDoc(doc(db, `teams/${T}/metadata/info`), { teamAdminCount: 0 }, { merge: true });
  });
  await assertSucceeds(setDoc(doc(as('cowner'), `teams/${T}/members/cowner`), { uid: 'cowner', role: 'admin' }));
  await assertSucceeds(updateDoc(doc(as('cowner'), `teams/${T}/metadata/info`), { teamName: 'U10 renamed' }));
});

test('removal record: owner creates (as server time), others cannot, never edited', async () => {
  const rec = (actor) => ({
    action: 'remove_team_admin', actorUid: actor, targetUid: 'coach', teamId: T, teamName: 'U10', at: serverTimestamp(),
  });
  await assertSucceeds(setDoc(doc(as('cowner'), `clubs/${C}/auditLog/a1`), rec('cowner')));
  await assertFails(setDoc(doc(as('cadmin'), `clubs/${C}/auditLog/a2`), rec('cadmin')));
  await assertFails(setDoc(doc(as('cowner'), `clubs/${C}/auditLog/a3`), { ...rec('cowner'), at: ago(500) }));
  await assertFails(updateDoc(doc(as('cowner'), `clubs/${C}/auditLog/a1`), { teamName: 'x' }));
  await assertFails(deleteDoc(doc(as('cowner'), `clubs/${C}/auditLog/a1`)));
  await assertSucceeds(getDoc(doc(as('cowner'), `clubs/${C}/auditLog/a1`)));
  await assertFails(getDoc(doc(as('cadmin'), `clubs/${C}/auditLog/a1`)));
});

// Club Owner recovery (app flow, 28 Nov 2026): the code restores club Owner powers and nothing more.
// No team Admin seat is taken on the anchor team (the app no longer tries); the rules still refuse it
// while another team Admin exists, which is the design (club admins are Details-only on such teams).
test('club recovery: metadata proof + owner membership succeed; no team Admin seat; owner can then remove a team Admin', async () => {
  const HASH = 'd'.repeat(64);
  await seed(async (db) => {
    await setDoc(doc(db, `clubs/${C}/secrets/info`), { ownerRecoveryCodeHash: HASH });
  });
  const eve = as('eve');
  // 1) proof step (rules compare recoveryHash to clubs/{c}/secrets/info.ownerRecoveryCodeHash)
  await assertFails(updateDoc(doc(eve, `clubs/${C}/metadata/info`), {
    createdBy: 'eve', previousOwnerUid: 'cowner', ownershipRecoveredAt: Timestamp.now(), recoveryHash: 'e'.repeat(64),
  }));
  await assertSucceeds(updateDoc(doc(eve, `clubs/${C}/metadata/info`), {
    createdBy: 'eve', previousOwnerUid: 'cowner', ownershipRecoveredAt: Timestamp.now(), recoveryHash: HASH,
  }));
  // 2) owner membership doc, with uid so club membership discovery finds it
  await assertSucceeds(setDoc(doc(eve, `clubs/${C}/members/eve`), {
    role: 'owner', uid: 'eve', displayName: 'Eve', addedAt: Timestamp.now(), rejoinedAt: Timestamp.now(),
  }, { merge: true }));
  // 3) no automatic team Admin seat while another team Admin exists (what the old anchor write attempted)
  await assertFails(setDoc(doc(eve, `teams/${T}/members/eve`), {
    role: 'admin', displayName: 'Eve', uid: 'eve', rejoinedAt: Timestamp.now(),
  }, { merge: true }));
  await assertFails(setDoc(doc(eve, `teams/${T}/members/eve`), { role: 'member', displayName: 'Eve', uid: 'eve' }));
  // 4) but club Owner powers work: remove a team Admin, and the previous owner lost theirs
  await assertSucceeds(deleteDoc(doc(eve, `teams/${T}/members/coach`)));
  await assertFails(deleteDoc(doc(as('cowner'), `teams/${T}/members/fresh`)));
});
