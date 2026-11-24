# Membership security (to-do item 10)

Status: **deployed 25 Nov 2026 (AEDT), 3.9.1 (74), then dual-write removal in
3.9.1 (76)**. Member share (3.9.1 build 77) is prepared on `feature/member-share`
but the Firestore rules and the `--member-invite` migration have **not** been
deployed or applied — Andrew must approve that rules deploy. PR #20 and PR #21
remain drafts — do not merge until Andrew has tested. Rollback of rules:
`~/TurfRulesBackup/rollback.sh` (restores `ac9ea929`, the 74 ruleset).

Deploy record (all times Australia/Sydney, AEDT):

- TestFlight upload of 3.9.1 build 74: delivery UUID `d6617b57-2cdd-4174-887b-9c5181e083b9`
  (altool succeeded at 6:47:14 AM AEDT).
- MCP Cloud Run: revision `turftimer-mcp-00009-hw4` (was `turftimer-mcp-00008-97w`),
  100% traffic, health check 200 on `/.well-known/oauth-authorization-server`.
  URL unchanged: `https://turftimer-mcp-co6qd7jw6q-ts.a.run.app`.
- Rules backup: `~/TurfRulesBackup/firestore-prod-20261125-065358.rules` (live
  `e1be8d18-d849-439b-9670-18855a30db07`). New live ruleset
  `ac9ea929-147e-4901-8f27-d0040fb75fb3` (deployed 6:54:23 AM AEDT).
- Migration (turf-timer): 11 teams, 3 clubs. Dry-run then `--apply` (11 team +
  3 club secret writes). Verified every secrets/info doc matched metadata.
  Then `--apply --strip` (14 strips). After strip, secrets remain and secret
  fields are gone from metadata. One pre-existing test team,
  `ios-simulator-vz4dyx`, has no `adminCodeHash` and no `createdBy` (not caused
  by the migration).

Dual-write removal (3.9.1 build 76, same day):

- TestFlight upload of 3.9.1 build 76 (from `feature/ai-connect-url`, which
  includes this fix and the Connect AI URL page): delivery UUID
  `774bbe56-424f-40e7-a8b2-51d3eba26b68` (altool succeeded at 9:24:54 AM AEDT).
- MCP Cloud Run: revision `turftimer-mcp-00010-bk8` (was `turftimer-mcp-00009-hw4`),
  100% traffic, health check 200 on `/.well-known/oauth-authorization-server`.
  URL unchanged: `https://turftimer-mcp-co6qd7jw6q-ts.a.run.app`.
- Rules backup: `~/TurfRulesBackup/firestore-prod-20261125-092029.rules` (live
  `ac9ea929-147e-4901-8f27-d0040fb75fb3`). New live ruleset
  `f0f39fe1-4524-409c-b94e-37dc417e7efb` (deployed 9:20:29 AM AEDT).
  `rollback.sh` now restores `ac9ea929`.
- Cloud Functions: all five updated to Node.js 22 (Node 20 was decommissioned
  30 Oct 2026). `requestAdminRecoveryEmail` and dormant-team cleanup now read
  `secrets/info` and delete the secrets subcollection. Deploy warning only:
  no Artifact Registry cleanup policy in `australia-southeast1`.
- Follow-up `--apply --strip`: scanned 12 teams, 3 clubs. Only
  `qr-test-25-nov-i7jzry` still had secret fields on metadata (created by the
  still-dual-writing build 75). Invite and admin hash were stripped; an empty
  leftover `creatorEmail` was then deleted (the script now also strips empty
  present fields). After that, metadata has no secret keys; `secrets/info`
  remains; `invite_codes/KVD4HQCJ` and `invite_codes/KVD4-HQCJ` still exist and
  point at that team.

Member share (3.9.1 build 77, `feature/member-share`): any team member can see
the invite code and use the Share page (QR, link, and code). New joiners always
become Members. Regenerating or revoking the invite stays admin-only.
`adminCodeHash`, `creatorEmail`, and club recovery hashes stay on `secrets/info`
(admin or club owner only). Because Firestore cannot do field-level reads, the
invite also lives at `teams/{t}/secrets/invite` (`inviteCode` only). Members may
read that document; they cannot read or write `secrets/info`, and they cannot
change `secrets/invite`. `invite_codes/{code}` lookup is unchanged.

Build 77 writes `secrets/invite` on create, regenerate, and admin self-heal.
That write is already allowed under the live `f0f39fe1` admin/owner secrets
rules, so MCP and Functions may be redeployed before the rules change. Members
cannot *read* the new document until the rules are deployed and the migration
copies the invite for existing teams (or an admin opens Team Details on 77,
which self-heals the doc). Until then, members who already have the code cached
from joining can still share; members without a cache see N/A.

Do **not** deploy these rules or run `--apply --member-invite` until Andrew
approves. Dry-run:

```
python3 scripts/migrate_membership_secrets.py --project turf-timer --member-invite
```

## What was wrong

Live rules (before this branch) let any signed-in user:

1. Create their own `teams/{t}/members/{uid}` doc with any role (including admin),
   with no invite or recovery-code check.
2. Create their own `clubs/{c}/members/{uid}` doc as owner or admin.
3. Change a team's `clubId` (any team admin).
4. Read `inviteCode`, `adminCodeHash` and `creatorEmail` on
   `teams/{t}/metadata/info` (world-readable to every signed-in user). The same
   applied to `clubs/{c}/metadata/info.ownerRecoveryCodeHash`.

The MCP server's `requireAdmin` only checks the membership doc, so (1) was enough
to take over a team through the AI tools as well.

Existing memberships are grandfathered: this change tightens **new** writes, not
rows already in the database.

## How joining now proves a code (no Cloud Functions)

The app still looks up the team with a **get-by-id** of `invite_codes/{compactCode}`
(you must already know the code). Collection queries that would list every invite
code are denied.

When the joiner writes their member doc they include `joinInviteCode` (the compact
code, e.g. `TQAQTN2K`). Rules allow `role: member` only if that document exists and
its `teamId` matches the team being joined. Firestore rules cannot compute SHA-256,
so the proof is “this write names a real invite_codes doc for this team,” not a
hash compare.

- **New team:** the creator writes `metadata/info` first (`createdBy` = themselves),
  then their member doc as `admin`. Rules allow that only when
  `metadata.createdBy == auth.uid`.
- **Admin / owner recovery:** the client hashes the Team Owner Recovery Code
  (SHA-256, uppercase) and writes `adminCodeHash` on the member doc. Rules compare
  it to `teams/{t}/secrets/info.adminCodeHash` only (no metadata fallback).
  Rules `get()` the secrets doc even when the recoverer cannot read it yet.
- **Promotion:** only an existing team admin can change someone else's role
  (`admin` or `member`). A member cannot self-promote without the recovery hash.
- **Leave:** a user may delete their own membership. An admin may remove others.
  Rules do not block removing the last admin (not simple to count in rules); the
  app already refuses removing the team Owner and refuses self-remove (Leave is
  used instead).

Club memberships: the creator may write `role: owner` only after club metadata
exists with `createdBy == auth.uid`. Joining a club team then writes
`clubs/{c}/members/{uid}` as `member` with `joinedViaTeamId` set to that team;
rules require the caller to already be a member of that team and the team to
belong to the club. Club admins are granted only by the club owner (an update).
Club owner recovery must present `recoveryHash` on the metadata `createdBy`
update, then may take a **team admin** seat on a team of that club (needed so
today's recovery flow still works; the fuller club-admin design is not built
here).

`clubId` on a team is immutable after create — team admins and club owners cannot
re-point a team. It may be set only at create, and only by a club owner/admin when
the team is club-managed.

## Secrets

New teams and clubs also write:

- `teams/{teamId}/secrets/invite` — `inviteCode` only (readable by any team
  member; create/update/delete by team admins or the metadata creator).
- `teams/{teamId}/secrets/info` — `inviteCode`, `adminCodeHash`, `creatorEmail`
  (readable by team admins and the metadata creator).
- `clubs/{clubId}/secrets/info` — `ownerRecoveryCodeHash` (club owner only).

From 3.9.1 (76) the app, MCP server, and Cloud Functions write and read recovery
fields **only** on `secrets/info` (plus `invite_codes` for join). From 3.9.1 (77)
the join invite is also written to `secrets/invite` so members can share it.
Team and club metadata must not contain `inviteCode`, `adminCodeHash`,
`creatorEmail`, or `ownerRecoveryCodeHash` — rules reject creates and updates
that would leave those keys on the document. After the 74 migration `--strip`,
re-run `--apply --strip` for any team created by a still-dual-writing build
(for example a Simulator test team). Then `--apply --member-invite` copies
`secrets/info.inviteCode` into `secrets/invite` for existing teams (does not
remove the copy on `secrets/info`).

Script (dry-run by default):

```
python3 scripts/migrate_membership_secrets.py --project turf-timer
python3 scripts/migrate_membership_secrets.py --project turf-timer --member-invite
python3 scripts/migrate_membership_secrets.py --project turf-timer --apply
python3 scripts/migrate_membership_secrets.py --project turf-timer --apply --strip
python3 scripts/migrate_membership_secrets.py --project turf-timer --apply --member-invite
```

Needs gcloud application-default credentials.

## Deploy order

1. Ship **app 3.9.1 (74)** and redeploy the **MCP server** from this branch so new
   joins write `joinInviteCode` / `adminCodeHash` / `joinedViaTeamId` / secrets.
   Rules are still the old, loose set — the gap stays open until step 2.
2. Deploy **firestore.rules** from this branch.
3. When old builds are no longer in use: run the migration `--apply`, then
   `--strip`. Ship the app/MCP that stop writing secrets onto metadata, tighten
   the rules so metadata cannot contain those fields, and re-strip any teams
   created in the dual-write window.

Do not reverse (2) before (1): old builds do not send join proof, so after a rules
deploy they cannot join a team, recover as owner, or add a club membership on join.

## What old builds (TestFlight 73, App Store) will see after the rules deploy

Works:

- Teams they already belong to (grandfathered memberships).
- Creating a new nickname team or club (metadata first, then creator as admin/owner).
- Invite lookup **get-by-id** (`invite_codes/{code}`).

Breaks or degrades:

- **Join by invite** — old clients write a member doc without `joinInviteCode`.
  The write is denied.
- **Owner recovery (team)** — old clients write `role: admin` without
  `adminCodeHash`. Denied unless they are already `metadata.createdBy` (the usual
  “I just deleted my own member doc” rejoin still works for the original creator).
- **Club owner recovery** — metadata `createdBy` change is denied without
  `recoveryHash`.
- **Club membership on join** — old clients omit `joinedViaTeamId`, so the club
  member write is denied (they may still be on the team).
- **Listing/querying `invite_codes`** (a fallback in lookup) is denied. Get-by-id
  still works, so most lookups succeed if the document id matches the code.
- Invite display and recovery-hash **reads** still work while those fields remain
  on metadata. After `--strip`, old builds cannot show the invite code or check a
  recovery code from metadata.

## Club admin design (not built, kept compatible)

Agreed later work: club owner assigns club admins (no team invite); club admins
fully control club teams that have no active team admin; once a team admin
accepts, club admins are read-only on that team; no override; the owner can
remove team admins; a team admin leaving returns the team to club management.
This branch does not implement that product behaviour. It only prevents self-serve
club admin/owner and lets the club owner take a team admin seat (recovery).

## Testing

`firestore-tests` (`npm test`) is run with `node --test --test-concurrency=1`
because the suites share the `demo-turftimer` emulator project. Running the
files in parallel races `clearFirestore()` and produces flaky permission /
evaluation errors. Existing app-flow and rules tests are kept; new coverage
is in `test/membership-security.test.mjs`.
