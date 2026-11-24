// Membership security (to-do item 10): join proof, role changes, club memberships,
// locked clubId, and secrets. Run via npm test.
import { test, before, after, beforeEach } from 'node:test';
import { readFileSync } from 'node:fs';
import { initializeTestEnvironment, assertSucceeds, assertFails } from '@firebase/rules-unit-testing';
import { doc, getDoc, setDoc, getDocs, collection, query, where, deleteDoc, updateDoc } from 'firebase/firestore';

let env;
const T = 't1';
const C = 'c1';
const HASH = 'a'.repeat(64);
const CLUB_HASH = 'b'.repeat(64);
const M = { merge: true };

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
    await setDoc(doc(db, `clubs/${C}/metadata/info`), { clubName: 'FC', createdBy: 'cowner', ownerRecoveryCodeHash: CLUB_HASH });
    await setDoc(doc(db, `clubs/${C}/secrets/info`), { ownerRecoveryCodeHash: CLUB_HASH });
    await setDoc(doc(db, `clubs/${C}/members/cowner`), { role: 'owner', displayName: 'CO', uid: 'cowner' });
    await setDoc(doc(db, `clubs/${C}/members/cadmin`), { role: 'admin', displayName: 'CA', uid: 'cadmin' });
    await setDoc(doc(db, `teams/${T}/metadata/info`), {
      teamName: 'U10', inviteCode: 'ABCD-EFGH', adminCodeHash: HASH,
      creatorEmail: 'a@example.com', createdBy: 'owner', clubId: C, clubName: 'FC', kind: 'clubbed',
    });
    await setDoc(doc(db, `teams/${T}/secrets/info`), { inviteCode: 'ABCD-EFGH', adminCodeHash: HASH, creatorEmail: 'a@example.com' });
    await setDoc(doc(db, `teams/${T}/members/owner`), { uid: 'owner', role: 'admin', displayName: 'O' });
    await setDoc(doc(db, `teams/${T}/members/mem`), { uid: 'mem', role: 'member', displayName: 'M' });
    await setDoc(doc(db, `invite_codes/ABCDEFGH`), { teamId: T, teamName: 'U10', createdBy: 'owner' });
    await setDoc(doc(db, `teams/nick/metadata/info`), { teamName: 'Nick', createdBy: 'nickown', kind: 'nickname' });
    await setDoc(doc(db, `teams/nick/members/nickown`), { uid: 'nickown', role: 'admin', displayName: 'N' });
  });
});
const as = (uid) => env.authenticatedContext(uid).firestore();

test('membership: signed-in stranger cannot self-create as admin or member without proof', async () => {
  const db = as('stranger');
  await assertFails(setDoc(doc(db, `teams/${T}/members/stranger`), { role: 'admin', uid: 'stranger', displayName: 'S' }));
  await assertFails(setDoc(doc(db, `teams/${T}/members/stranger`), { role: 'member', uid: 'stranger', displayName: 'S' }));
});

test('membership: join as member with a valid invite code; wrong or other-team code fails', async () => {
  await assertSucceeds(setDoc(doc(as('joiner'), `teams/${T}/members/joiner`), {
    role: 'member', uid: 'joiner', displayName: 'J', joinInviteCode: 'ABCDEFGH',
  }));
  await assertFails(setDoc(doc(as('eve'), `teams/${T}/members/eve`), {
    role: 'member', uid: 'eve', displayName: 'E', joinInviteCode: 'NOPE1234',
  }));
  await assertFails(setDoc(doc(as('eve'), `teams/${T}/members/eve`), {
    role: 'admin', uid: 'eve', displayName: 'E', joinInviteCode: 'ABCDEFGH',
  }));
});

test('membership: creator can self-create as admin after writing metadata; stranger cannot', async () => {
  const db = as('newbie');
  await assertSucceeds(setDoc(doc(db, 'teams/tnew/metadata/info'), { teamName: 'New', createdBy: 'newbie', kind: 'nickname' }, M));
  await assertSucceeds(setDoc(doc(db, 'teams/tnew/members/newbie'), { role: 'admin', uid: 'newbie', displayName: 'N' }, M));
  await assertFails(setDoc(doc(as('stranger'), 'teams/tnew/members/stranger'), { role: 'admin', uid: 'stranger', displayName: 'S' }));
});

test('membership: admin recovery hash on create or self-update; wrong hash fails', async () => {
  await assertSucceeds(setDoc(doc(as('recover'), `teams/${T}/members/recover`), {
    role: 'admin', uid: 'recover', displayName: 'R', adminCodeHash: HASH,
  }));
  await assertFails(setDoc(doc(as('eve'), `teams/${T}/members/eve`), {
    role: 'admin', uid: 'eve', displayName: 'E', adminCodeHash: 'c'.repeat(64),
  }));
  // Existing member can self-promote only with the matching hash
  await assertFails(updateDoc(doc(as('mem'), `teams/${T}/members/mem`), { role: 'admin' }));
  await assertSucceeds(updateDoc(doc(as('mem'), `teams/${T}/members/mem`), { role: 'admin', adminCodeHash: HASH }));
});

test('membership: only an existing admin can promote or demote; member cannot change own role', async () => {
  await assertSucceeds(updateDoc(doc(as('owner'), `teams/${T}/members/mem`), { role: 'admin' }));
  await assertSucceeds(updateDoc(doc(as('owner'), `teams/${T}/members/mem`), { role: 'member' }));
  await assertFails(updateDoc(doc(as('mem'), `teams/${T}/members/mem`), { role: 'admin' }));
  await assertFails(updateDoc(doc(as('mem'), `teams/${T}/members/owner`), { role: 'member' }));
});

test('membership: user can leave (delete self); member cannot remove another; admin can', async () => {
  await assertSucceeds(deleteDoc(doc(as('mem'), `teams/${T}/members/mem`)));
  await env.withSecurityRulesDisabled(async (ctx) => {
    await setDoc(doc(ctx.firestore(), `teams/${T}/members/mem`), { uid: 'mem', role: 'member' });
  });
  await assertFails(deleteDoc(doc(as('mem'), `teams/${T}/members/owner`)));
  await assertSucceeds(deleteDoc(doc(as('owner'), `teams/${T}/members/mem`)));
});

test('membership: grandfathered existing members still work (no invite field required on update)', async () => {
  await assertSucceeds(updateDoc(doc(as('mem'), `teams/${T}/members/mem`), { displayName: 'Mem2', fcmTokens: ['t'] }));
  await assertSucceeds(getDoc(doc(as('mem'), `teams/${T}/members/mem`)));
});

test('club membership: stranger cannot self-create as owner or admin', async () => {
  await assertFails(setDoc(doc(as('stranger'), `clubs/${C}/members/stranger`), { role: 'owner', displayName: 'S' }));
  await assertFails(setDoc(doc(as('stranger'), `clubs/${C}/members/stranger`), { role: 'admin', displayName: 'S' }));
  await assertFails(setDoc(doc(as('stranger'), `clubs/${C}/members/stranger`), { role: 'member', displayName: 'S' }));
});

test('club membership: owner is created after club metadata; member requires a club-team join', async () => {
  const db = as('fresh');
  await assertSucceeds(setDoc(doc(db, 'clubs/cnew/metadata/info'), { clubName: 'NewFC', createdBy: 'fresh' }));
  await assertSucceeds(setDoc(doc(db, 'clubs/cnew/members/fresh'), { role: 'owner', displayName: 'F', uid: 'fresh' }));

  // Join the existing club team first, then club member with joinedViaTeamId
  await assertSucceeds(setDoc(doc(as('joiner'), `teams/${T}/members/joiner`), {
    role: 'member', uid: 'joiner', displayName: 'J', joinInviteCode: 'ABCDEFGH',
  }));
  await assertSucceeds(setDoc(doc(as('joiner'), `clubs/${C}/members/joiner`), {
    role: 'member', displayName: 'J', uid: 'joiner', joinedViaTeamId: T,
  }));
  // Wrong team (nickname team is not in the club)
  await assertFails(setDoc(doc(as('nickown'), `clubs/${C}/members/nickown`), {
    role: 'member', displayName: 'N', uid: 'nickown', joinedViaTeamId: 'nick',
  }));
});

test('club membership: only the owner can promote to club admin; self cannot', async () => {
  await assertSucceeds(setDoc(doc(as('joiner'), `teams/${T}/members/joiner`), {
    role: 'member', uid: 'joiner', displayName: 'J', joinInviteCode: 'ABCDEFGH',
  }));
  await assertSucceeds(setDoc(doc(as('joiner'), `clubs/${C}/members/joiner`), {
    role: 'member', displayName: 'J', uid: 'joiner', joinedViaTeamId: T,
  }));
  await assertFails(updateDoc(doc(as('joiner'), `clubs/${C}/members/joiner`), { role: 'admin' }));
  await assertSucceeds(updateDoc(doc(as('cowner'), `clubs/${C}/members/joiner`), { role: 'admin' }));
  await assertFails(updateDoc(doc(as('cadmin'), `clubs/${C}/members/joiner`), { role: 'member' }));
});

test('club recovery: createdBy can change only with a matching recoveryHash', async () => {
  await assertFails(updateDoc(doc(as('eve'), `clubs/${C}/metadata/info`), {
    createdBy: 'eve', previousOwnerUid: 'cowner', ownershipRecoveredAt: 1,
  }));
  await assertFails(updateDoc(doc(as('eve'), `clubs/${C}/metadata/info`), {
    createdBy: 'eve', previousOwnerUid: 'cowner', ownershipRecoveredAt: 1, recoveryHash: 'c'.repeat(64),
  }));
  await assertSucceeds(updateDoc(doc(as('eve'), `clubs/${C}/metadata/info`), {
    createdBy: 'eve', previousOwnerUid: 'cowner', ownershipRecoveredAt: 1, recoveryHash: CLUB_HASH,
  }));
});

test('clubId is locked after create; team admin cannot re-point the team', async () => {
  await assertFails(updateDoc(doc(as('owner'), `teams/${T}/metadata/info`), { clubId: 'other' }));
  await assertSucceeds(updateDoc(doc(as('owner'), `teams/${T}/metadata/info`), { teamName: 'U10 Blue' }));
  // Nickname team stays without a clubId
  await assertSucceeds(updateDoc(doc(as('nickown'), 'teams/nick/metadata/info'), { teamName: 'Nick 2' }));
  await assertFails(updateDoc(doc(as('nickown'), 'teams/nick/metadata/info'), { clubId: C }));
});

test('secrets: team admins can read; members and strangers cannot; owner can write at create', async () => {
  await assertSucceeds(getDoc(doc(as('owner'), `teams/${T}/secrets/info`)));
  await assertFails(getDoc(doc(as('mem'), `teams/${T}/secrets/info`)));
  await assertFails(getDoc(doc(as('stranger'), `teams/${T}/secrets/info`)));
  const db = as('newbie');
  await assertSucceeds(setDoc(doc(db, 'teams/tnew/metadata/info'), { teamName: 'New', createdBy: 'newbie' }, M));
  await assertSucceeds(setDoc(doc(db, 'teams/tnew/secrets/info'), { inviteCode: 'ZZZZ-YYYY', adminCodeHash: HASH, creatorEmail: '' }, M));
  await assertSucceeds(getDoc(doc(as('newbie'), 'teams/tnew/secrets/info')));
});

test('secrets: club owner can read club secrets; others cannot', async () => {
  await assertSucceeds(getDoc(doc(as('cowner'), `clubs/${C}/secrets/info`)));
  await assertFails(getDoc(doc(as('cadmin'), `clubs/${C}/secrets/info`)));
  await assertFails(getDoc(doc(as('stranger'), `clubs/${C}/secrets/info`)));
});

test('invite_codes: get-by-id works for signed-in; list/query is denied', async () => {
  await assertSucceeds(getDoc(doc(as('stranger'), 'invite_codes/ABCDEFGH')));
  await assertFails(getDocs(query(collection(as('stranger'), 'invite_codes'), where('teamId', '==', T))));
  await assertFails(getDocs(collection(as('stranger'), 'invite_codes')));
});

test('club owner can take a team admin seat on a club team (recovery path)', async () => {
  await assertSucceeds(setDoc(doc(as('cowner'), `teams/${T}/members/cowner`), {
    role: 'admin', uid: 'cowner', displayName: 'CO',
  }, M));
});
