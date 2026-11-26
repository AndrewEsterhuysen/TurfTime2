// Club owner way back in (item 8, decision 1): the club Owner (not club admins) may remove a
// team Admin of a CLUB team once that Admin's activity stamp (members/{uid}.seenAtUtc) is at
// least 1 day old. The stamp can only be written by the member, only as server time.
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
    await setDoc(doc(db, `teams/${T}/members/coach`), { uid: 'coach', role: 'admin', seenAtUtc: ago(48) });
    await setDoc(doc(db, `teams/${T}/members/fresh`), { uid: 'fresh', role: 'admin', seenAtUtc: ago(23) });
    await setDoc(doc(db, `teams/${T}/members/edge`), { uid: 'edge', role: 'admin', seenAtUtc: ago(25) });
    await setDoc(doc(db, `teams/${T}/members/nostamp`), { uid: 'nostamp', role: 'admin' });
    await setDoc(doc(db, `teams/${T}/members/oldmember`), { uid: 'oldmember', role: 'member', seenAtUtc: ago(500) });
    // Nickname (non-club) team: owner of club C has no standing here.
    await setDoc(doc(db, 'teams/tnick/metadata/info'), { teamName: 'Nick', createdBy: 'nickowner', kind: 'nickname', teamAdminCount: 1 });
    await setDoc(doc(db, 'teams/tnick/members/nickadmin'), { uid: 'nickadmin', role: 'admin', seenAtUtc: ago(500) });
    await setDoc(doc(db, 'teams/tnick/members/cowner2'), { uid: 'cowner2', role: 'member' });
    // A club team of the other club.
    await setDoc(doc(db, 'teams/tother/metadata/info'), { teamName: 'O', createdBy: 'x', clubId: 'c2', kind: 'clubbed', teamAdminCount: 1 });
    await setDoc(doc(db, 'teams/tother/members/oadmin'), { uid: 'oadmin', role: 'admin', seenAtUtc: ago(500) });
  });
});

test('owner may remove a team Admin inactive for 1 day or more', async () => {
  await assertSucceeds(deleteDoc(doc(as('cowner'), `teams/${T}/members/coach`)));
  await assertSucceeds(deleteDoc(doc(as('cowner'), `teams/${T}/members/edge`)));
});

test('owner is blocked when the team Admin was active within 1 day', async () => {
  await assertFails(deleteDoc(doc(as('cowner'), `teams/${T}/members/fresh`)));
});

test('owner is blocked when there is no activity stamp, or the target is not a team Admin', async () => {
  await assertFails(deleteDoc(doc(as('cowner'), `teams/${T}/members/nostamp`)));
  await assertFails(deleteDoc(doc(as('cowner'), `teams/${T}/members/oldmember`)));
});

test('a club admin can never remove an inactive team Admin', async () => {
  await assertFails(deleteDoc(doc(as('cadmin'), `teams/${T}/members/coach`)));
  await assertFails(deleteDoc(doc(as('cadmin'), `teams/${T}/members/edge`)));
});

test('non-club team: no removal, and the owner of another club has no standing', async () => {
  await assertFails(deleteDoc(doc(as('cowner'), 'teams/tnick/members/nickadmin')));
  await assertFails(deleteDoc(doc(as('cowner'), 'teams/tother/members/oadmin')));
});

test('after removal the team has no other Admin: club owner gets full control back', async () => {
  await assertSucceeds(deleteDoc(doc(as('cowner'), `teams/${T}/members/coach`)));
  // Function recounts admins; simulate it. Remove the other stale Admins too.
  await seed(async (db) => {
    for (const u of ['fresh', 'edge', 'nostamp']) await deleteDoc(doc(db, `teams/${T}/members/${u}`));
    await setDoc(doc(db, `teams/${T}/metadata/info`), { teamAdminCount: 0 }, { merge: true });
  });
  await assertSucceeds(setDoc(doc(as('cowner'), `teams/${T}/members/cowner`), { uid: 'cowner', role: 'admin' }));
  await assertSucceeds(updateDoc(doc(as('cowner'), `teams/${T}/metadata/info`), { teamName: 'U10 renamed' }));
});

test('activity stamp: only the member writes it, only as server time', async () => {
  const mine = doc(as('coach'), `teams/${T}/members/coach`);
  await assertSucceeds(updateDoc(mine, { seenAtUtc: serverTimestamp() }));
  await assertFails(updateDoc(mine, { seenAtUtc: ago(2) }));            // forged into the past
  await assertFails(updateDoc(mine, { seenAtUtc: ago(-100) }));         // future
  await assertFails(updateDoc(doc(as('coach'), `teams/${T}/members/fresh`), { seenAtUtc: serverTimestamp() })); // not mine
  // Another team Admin can't reset someone's stamp; nor can the owner.
  await assertFails(updateDoc(doc(as('fresh'), `teams/${T}/members/edge`), { seenAtUtc: serverTimestamp() }));
  await assertFails(updateDoc(doc(as('cowner'), `teams/${T}/members/edge`), { seenAtUtc: serverTimestamp() }));
  // Unrelated self updates still work and keep the stamp.
  await assertSucceeds(updateDoc(doc(as('edge'), `teams/${T}/members/edge`), { displayName: 'Ed' }));
});

test('activity stamp: not forgeable at create', async () => {
  await assertFails(setDoc(doc(as('newguy'), `teams/${T}/members/newguy`), { uid: 'newguy', role: 'member', seenAtUtc: ago(2) }));
});

test('removal record: owner creates (as server time), others cannot, never edited', async () => {
  const rec = (actor) => ({
    action: 'remove_inactive_team_admin', actorUid: actor, targetUid: 'coach', teamId: T, teamName: 'U10', at: serverTimestamp(),
  });
  await assertSucceeds(setDoc(doc(as('cowner'), `clubs/${C}/auditLog/a1`), rec('cowner')));
  await assertFails(setDoc(doc(as('cadmin'), `clubs/${C}/auditLog/a2`), rec('cadmin')));
  await assertFails(setDoc(doc(as('cowner'), `clubs/${C}/auditLog/a3`), { ...rec('cowner'), at: ago(500) }));
  await assertFails(updateDoc(doc(as('cowner'), `clubs/${C}/auditLog/a1`), { teamName: 'x' }));
  await assertFails(deleteDoc(doc(as('cowner'), `clubs/${C}/auditLog/a1`)));
  await assertSucceeds(getDoc(doc(as('cowner'), `clubs/${C}/auditLog/a1`)));
  await assertFails(getDoc(doc(as('cadmin'), `clubs/${C}/auditLog/a1`)));
});
