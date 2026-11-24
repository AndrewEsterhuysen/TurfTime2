# Membership security (to-do item 10)

Status: **deployed 25 Nov 2026 (AEDT), 3.9.1 (74)**. PR #20 remains a draft — do not
merge until Andrew has tested. Rollback of rules: `~/TurfRulesBackup/rollback.sh`
(restores `e1be8d18`).

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
  it to `teams/{t}/secrets/info.adminCodeHash`, falling back to
  `metadata.adminCodeHash` until secrets are migrated.
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

- `teams/{teamId}/secrets/info` — `inviteCode`, `adminCodeHash`, `creatorEmail`
  (readable by team admins and the metadata creator).
- `clubs/{clubId}/secrets/info` — `ownerRecoveryCodeHash` (club owner only).

The app and MCP still **also write the same fields on metadata** so build 73 and
the App Store build can keep reading them. To fully close the world-readable leak,
run the migration (copy, then `--strip`) after those old builds are gone.

Script (dry-run by default; **do not run `--apply` against production yet**):

```
python3 scripts/migrate_membership_secrets.py --project turf-timer
python3 scripts/migrate_membership_secrets.py --project turf-timer --apply
python3 scripts/migrate_membership_secrets.py --project turf-timer --apply --strip
```

Needs gcloud application-default credentials.

## Deploy order

1. Ship **app 3.9.1 (74)** and redeploy the **MCP server** from this branch so new
   joins write `joinInviteCode` / `adminCodeHash` / `joinedViaTeamId` / secrets.
   Rules are still the old, loose set — the gap stays open until step 2.
2. Deploy **firestore.rules** from this branch.
3. Later, when old builds are no longer in use: run the migration `--apply`, then
   `--strip`, so secrets leave metadata. Optional follow-up: stop writing secrets
   onto metadata in the app.

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
