# Player of the match nominations — specification

Status: **scaffold only**. This document is the design for `feature/nominations`.
Nothing in this spec has been deployed. **Production Firestore rules must not be
deployed until Andrew approves.**

Feature id: `TeamNominations` (already in [`FEATURES.md`](FEATURES.md)).

---

## 1. What exists today (and why the path is not `games/{gameId}`)

TurfTimer does **not** store games as a top-level `games/{gameId}` collection.
Completed matches are **sessions**, already used by Reports:

| Concept | Where it lives | Notes |
|---------|----------------|-------|
| Online / shared team | `teams/{teamId}/metadata/info`, `teams/{teamId}/members/{uid}` | Member roles: `admin` or `member`. Team creator is `metadata.createdBy` (owner). |
| Club (optional) | `clubs/{clubId}/…` | Independent of match data. |
| Roster / players | `teams/{teamId}/roster/data` (`PlayerSnapshot`) | Players have a stable **`SlotId`** (1–16 today) and a **name**. They do **not** have Firebase accounts or a `playerId`. |
| Kit / duties / location | `teams/{teamId}/details/{kit,duties,location}` | Admin write, member read. Details tab is **hidden for local teams**. |
| Chat | `teams/{teamId}/messages/{messageId}` | Identity is the signed-in member's `displayName`. |
| **Game / report** | **`teams/{teamId}/sessions/{sessionId}`** | Written on End game for `team_mode=shared`. Fields include `sessionId`, `startTime`, `endTime`, `sessionJson` (full `GameSession`). |
| Local teams | `team_id` prefix `local_…`, `team_mode=local` | Sessions stay in device Preferences (`roster.sessionHistory.v1`). No Firestore. |

Firestore rules already grant team members read, and members create/update, on
`teams/{teamId}/sessions/{sessionId}`. Deleting a team (`DeleteTeamAsOwner`)
wipes `sessions` documents but **does not recurse into subcollections**.

**Chosen path**

```
teams/{teamId}/sessions/{sessionId}/nominations/{nominatorId}
```

`nominatorId` is the Firebase Auth uid of the team member, which makes one
nomination per member per game naturally unique (create or overwrite the same
doc). A top-level `games/{gameId}/nominations` path would not match how Reports,
session delete, or `canReadTeam(teamId)` work today.

A sibling admin-only status doc holds the close flag:

```
teams/{teamId}/sessions/{sessionId}/nominationsMeta/status
```

---

## 2. Who nominates? (decided for v1)

The product line “players nominate another player” does not map cleanly onto
the current data model.

- **Players** are junior roster records (`SlotId` + name). They have no login.
- **Team members** are the only signed-in people: the coach/admin and parents
  who joined the shared team. Auth is a durable anonymous Firebase uid on the
  device; the name teammates see is `members/{uid}.displayName`.

### Options

| Option | How it works | Pros | Cons |
|--------|----------------|------|------|
| **A. Team members vote (recommended)** | Each signed-in Admin or Member casts one vote for a **roster player** on that session. `nominatorId` = member uid. `nomineeSlotId` + denormalized `nomineeName` identify the player. | Fits accounts that actually exist. One vote per device/account is enforceable in rules. Matches “parent nominating on behalf.” | It is a parent/coach vote, not a player vote. A household that shares one phone shares one vote. |
| **B. Player accounts vote** | Each player would need a login (or a child profile). | Closest to “players nominate.” | Not built. Would require collecting data from juniors and a new identity model. Not recommended. |
| **C. Linked parent → child** | Member account is linked to one (or more) roster slots. The parent votes “as” that player. One nomination per linked child, and optionally block voting for your own child. | Closer to a player ballot while keeping adults as the account holders. | Link table does not exist. Needs a UI to attach children to members. |
| **D. Admin records a single official POTM** | No vote; the coach picks the winner. | Simple. | Not a nomination from the team. |

**Decision for v1 (Andrew, 20 Nov 2026): kids nominate through their parents’
accounts.** Parents vote on behalf of their children using their own signed-in
member account. In practice this is Option C *without* the parent–child link
table, delivered on the Option A mechanics:

- Each signed-in Admin or Member casts **one** vote per session for a roster
  player (`nominatorId` = member uid). The parent is voting for their child.
- Voting is open from End game until an Admin closes it or 24 hours after the
  session’s end time, whichever comes first (see §3.2, §5, §7).
- There is **no** link between a member and a roster slot in v1. The
  parent–child link table (full Option C) is **deferred** to a later version.
- Because there is no link, the app does **not** restrict who a parent can
  nominate: any present / active player in the session squad may be chosen,
  including the parent’s own child (see §7).
- A household that shares one phone still shares one vote; families with more
  than one child on the team get one vote per member account, not per child.

Option B (player accounts) and Option D (admin-only pick) are not planned.

---

## 3. Data model

### 3.1 Nomination document

Path: `teams/{teamId}/sessions/{sessionId}/nominations/{nominatorId}`

| Field | Type | Description |
|-------|------|-------------|
| `nominatorId` | string | Must equal the document id and `request.auth.uid`. |
| `nominatorDisplayName` | string | Denormalized member display name at vote time (audit only; **not shown** in the team tally). |
| `nomineeSlotId` | int | Roster `SlotId` from the **session squad**, not “whoever occupies that slot next week.” |
| `nomineeName` | string | Player name denormalized at vote time (names can change later). Max 80 characters. |
| `timestamp` | timestamp | Server / client UTC time of the latest vote (updated if the member changes their pick). |

No separate tally document. The live tally is the nominations collection,
aggregated on the client from a snapshot listener (same pattern as chat).

### 3.2 Window / close document

Path: `teams/{teamId}/sessions/{sessionId}/nominationsMeta/status`

| Field | Type | Description |
|-------|------|-------------|
| `closed` | bool | Admin has closed voting early. |
| `closedAt` | timestamp | When it was closed. |
| `closedBy` | string | Admin uid. |

Voting closes at whichever comes first: an Admin closes it (this doc,
`closed: true`), or 24 hours after `sessions/{sessionId}.endTime`. The
24-hour auto-close is enforced from `endTime` in rules (see §5); no extra
field is required for that.

### 3.3 Session squad (done)

Session schema v2 (`SessionStorageService.SessionSchemaVersion = 2`) adds
`SlotId` and `WasPresent` to each `GameSession.Summary.PlayerStats` entry, so
every new session keeps its own squad snapshot. `WasPresent` is true when the
player was not Absent at full time, or played some minutes before being moved
to Absent (e.g. injury). `Models/NominationSquad.cs` turns the snapshot into
the eligible nominee list (present players only).

Older (v1) sessions have no slot ids. For those, `NominationSquad` falls back
to the current shared roster: session player names are matched to current
roster slots, and a player counts as present if they logged any field, bench or
goalie time (absent players were recorded with zero time). Players added to
the roster after the match never match, so they stay ineligible.

### 3.4 Client types

`Models/Nomination.cs` — `Nomination`, `NominationTally` (+ `Winners`),
`NominationTallyEntry` (+ `IsLeader`), `NominationTallyCalculator` (pure
tally maths: one ballot per uid, counts by slot, latest name, joint leaders,
open/closed), `NominationBallot`, `NominationSubmitResult`.
`Models/NominationSquad.cs` — eligible (present) nominees for a session.
`Services/INominationService` — `LoadBallotAsync`, `SubmitNominationAsync`
(transaction), `CloseVotingAsync` (Admin), `WatchTallyAsync` (snapshot
listener) and `GetTallyAsync` (one-shot, used by the match report).

---

## 4. UI flow (iOS and Android — same pages)

Details → Nominations already exists as a “Coming soon” card and is only
reachable on **shared** teams (the Details tab is hidden for local teams).
The scaffold points that card at a placeholder `NominationsPage`. End-game
and Reports entry points are specified here but **not wired** in the first
commit.

1. **After End game (future).** When a shared-team match is archived to
   `sessions/{sessionId}`, the controller (and other members on the team)
   can be offered “Nominate player of the match.” That opens `NominationsPage`
   with `sessionId` set. The Game tab stays as it is if they dismiss.
2. **From Reports (future).** A “Nominations” action on the selected session
   opens the same page for that report.
3. **From Details → Nominations (scaffold entry).** The page lists recent
   sessions for the current shared team (same source as Reports). The user
   picks a match, or lands directly on one if `sessionId` was passed.
4. **Nominate screen.** Eligible players from that session’s squad. The
   current user’s pick is highlighted. Tapping another player changes the
   vote until the window closes. Self-nomination is blocked when we have a
   reliable link (see §7).
5. **Live tally.** Visible to every team member while the page is open:
   nominee name + vote count, leaders marked, ties shown as joint leaders.
   Individual voters are **not** listed. When the window is closed, the page
   becomes view-only and shows the result (or joint winners).
6. **Admin close (future).** An Admin can close voting early from this page.

---

## 5. Firestore rules (patched on this branch — do not deploy without Andrew’s OK)

`nominationsOpen` below implements both close triggers: writes are denied
once 24 hours have passed since `endTime` **or** an Admin has set
`nominationsMeta/status.closed`, whichever happens first.

These rules are now in `firestore.rules` on `feature/nominations`, next to the
existing `teams/{teamId}/sessions/{sessionId}` block, using the file’s own
helpers (`isTeamMember`, `isTeamAdmin`, `isTeamOwner`, `canReadTeam`).
**They are not deployed. Do not run `firebase deploy --only firestore:rules`
until Andrew approves.**

Differences from the first proposal: the payload must also have only the
known fields (`hasOnly`) and a real `timestamp`; `endTime` must be a
timestamp; the team owner may delete votes and the status doc during the
`DeleteTeamAsOwner` cascade (when they are no longer a member); and the
status doc must carry a boolean `closed`.

```
// Player of the match nominations (docs/nominations-spec.md §5).
// One vote per member per session: doc id = voter uid, so a new vote overwrites the old one.
// Voting closes when an Admin sets nominationsMeta/status.closed, or 24 hours after the
// session's endTime, whichever comes first. Keep in sync with INominationService.
function sessionDocPath(teamId, sessionId) {
  return /databases/$(database)/documents/teams/$(teamId)/sessions/$(sessionId);
}

function nominationsStatusPath(teamId, sessionId) {
  return /databases/$(database)/documents/teams/$(teamId)/sessions/$(sessionId)/nominationsMeta/status;
}

function sessionEnded(teamId, sessionId) {
  return exists(sessionDocPath(teamId, sessionId))
    && 'endTime' in get(sessionDocPath(teamId, sessionId)).data
    && get(sessionDocPath(teamId, sessionId)).data.endTime is timestamp;
}

function nominationsOpen(teamId, sessionId) {
  return sessionEnded(teamId, sessionId)
    && request.time <= get(sessionDocPath(teamId, sessionId)).data.endTime + duration.value(24, 'h')
    && (
      !exists(nominationsStatusPath(teamId, sessionId))
      || get(nominationsStatusPath(teamId, sessionId)).data.get('closed', false) != true
    );
}

function nominationPayloadOk(nominatorId) {
  let d = request.resource.data;
  return d.keys().hasAll(['nominatorId', 'nomineeSlotId', 'nomineeName', 'timestamp'])
    && d.keys().hasOnly(['nominatorId', 'nominatorDisplayName', 'nomineeSlotId', 'nomineeName', 'timestamp'])
    && d.nominatorId == request.auth.uid
    && d.nominatorId == nominatorId
    && d.nomineeSlotId is int
    && d.nomineeSlotId >= 1
    && d.nomineeName is string
    && d.nomineeName.size() > 0
    && d.nomineeName.size() <= 80
    && d.timestamp is timestamp
    && (!('nominatorDisplayName' in d)
        || (d.nominatorDisplayName is string && d.nominatorDisplayName.size() <= 80));
}

match /teams/{teamId}/sessions/{sessionId}/nominations/{nominatorId} {
  allow read: if canReadTeam(teamId);

  allow create: if isTeamMember(teamId)
    && nominatorId == request.auth.uid
    && nominationPayloadOk(nominatorId)
    && nominationsOpen(teamId, sessionId);

  allow update: if isTeamMember(teamId)
    && nominatorId == request.auth.uid
    && resource.data.nominatorId == request.auth.uid
    && nominationPayloadOk(nominatorId)
    && nominationsOpen(teamId, sessionId);

  // Members may withdraw while open; Admins may remove a vote at any time;
  // the team owner may delete during the DeleteTeamAsOwner cascade.
  allow delete: if (
      isTeamMember(teamId)
      && resource.data.nominatorId == request.auth.uid
      && nominationsOpen(teamId, sessionId)
    )
    || isTeamAdmin(teamId)
    || isTeamOwner(teamId);
}

// Admin-only close flag for a session's nominations.
match /teams/{teamId}/sessions/{sessionId}/nominationsMeta/{docId} {
  allow read: if docId == 'status' && canReadTeam(teamId);
  allow create, update: if docId == 'status'
    && isTeamAdmin(teamId)
    && request.resource.data.closed is bool;
  allow delete: if docId == 'status' && (isTeamAdmin(teamId) || isTeamOwner(teamId));
}
```

Emulator tests: `firestore-tests/test/nominations.test.mjs` (run `npm test` in
`firestore-tests/`; emulator only). They cover: non-member denied, member
create, bad payloads denied, second vote replaces the first (same id is an
update), another user cannot edit or delete someone’s vote, withdraw / Admin
remove, vote after 24h denied, vote just inside 24h allowed, no `endTime` or
missing session denied, vote after Admin close denied, Admin-only status
writes, members and late joiners can read the tally, and the owner cascade.

When `DeleteTeamAsOwner` (and any future “delete this session”) runs, it must
also delete `sessions/{sessionId}/nominations/*` and `nominationsMeta/status`.

---

## 6. Service behaviour

`INominationService.SubmitNominationAsync`:

1. Reject local teams (`local_` prefix or `team_mode=local`) and fail soft.
2. `EnsureSignedInAsync`; nominator is that uid.
3. Run a Firestore transaction: read the session (must exist, `endTime` set),
   read `nominationsMeta/status` (must not be `closed`), confirm the window
   (`now <= endTime + 24h`), confirm the nominee was **present / active** in
   the session squad (§3.3), then `Set` the nomination doc (overwrite). There
   is **no** self-nomination or own-child check in v1 (§7). The document id is
   the nominator uid, so a second vote replaces the first rather than creating
   a second ballot.
4. Fail soft on network errors; do not crash the app.

The transaction runs over authenticated Firestore REST first
(`beginTransaction` → `batchGet` → `commit`), like chat and Details, because
Plugin.Firebase `SetDataAsync` has written empty documents on device. If REST
cannot be reached, the Plugin.Firebase `RunTransactionAsync` path runs the
same checks. Rules (§5) enforce the same window server-side.

`INominationService.WatchTallyAsync`:

- Snapshot-listen on `teams/{teamId}/sessions/{sessionId}/nominations`
  and the `nominationsMeta/status` doc for closed state; a timer re-emits at
  the 24-hour close so the page turns view-only on its own.
- Aggregate counts by `nomineeSlotId`, keeping the latest `nomineeName`.
- Include the current user’s pick so the UI can highlight it.
- Do **not** return a voter list to the UI.
- Return `IDisposable` to stop the listener, same pattern as `IChatService.SubscribeAsync`.

`INominationService.CloseVotingAsync` (Admin) writes `nominationsMeta/status`
(`closed`, `closedAt`, `closedBy`). `GetTallyAsync` is a one-shot read for the
match report.

---

## 7. Edge cases

### Self-nomination / parents voting for their own child — allowed in v1

Decision (Andrew, 20 Nov 2026): **parents may vote for their own child.** A
parent’s child may legitimately be player of the match.

- v1 has **no block** of any kind: no uid ↔ slot check, no `displayName`
  comparison, no “this is my child” tick. Any member may nominate any present player
  in the session squad.
- The parent–child link table is deferred. If it is added later, whether to
  restrict own-child votes will be a separate decision; the default remains
  allowed.

### Late joins and players who were absent / not on the field

- **Nominees:** only players who were **present / active** in that
  **session’s squad** (decided by Andrew, 20 Nov 2026). Players marked absent
  or inactive for the match cannot be nominated. Players added to the roster
  *after* the match are not eligible for that session either. The session
  snapshot must therefore record each player’s active/absent status for the
  match (alongside `SlotId`, see §3.3).
- **Nominators:** any **current** team member may vote while the window is
  open, including members who joined the team after the game. We do not
  store membership history, so “must have been a member at kick-off” is not
  enforceable today. Confirmed by Andrew (20 Nov 2026).

### Changing a vote — allowed until close

Members may change (or withdraw) their nomination until the window closes.
The same document is overwritten, so the tally never double-counts.
Justification: sideline conversations often happen after the final whistle;
a locked-in first tap would feel harsh. Once closed, the result is frozen.

### Voting window

- **Opens** when the session is saved with an `endTime` (End game).
- **Closes** at whichever comes first (decided 20 Nov 2026): an Admin closes it, or **24 hours after
  `endTime`**.
- 24 hours covers the trip home and evening chat without leaving last
  weekend’s vote open all season.

### Removed or renamed players

Tally and eligibility use the session snapshot plus the denormalized
`nomineeName` on each vote. A later roster delete or rename does not
orphan the result.

### Local teams

**Disabled** in v1. Local teams have no shared member list and no cloud
session doc to hang a live tally on. The Details tab (the Nominations
entry) is already hidden. A single-device Preferences tally would be
misleading on a family phone, so we will not add one. Confirmed by Andrew
(20 Nov 2026): nominations stay a **shared-team-only** feature. Shared-team
features may later become paid under a subscription model, so local-team
users would get nominations by moving to a (possibly paid) shared team.

### Ties

Ties stand as joint winners (decided by Andrew, 20 Nov 2026). The UI shows
all joint leaders as player of the match. There is no automatic tie-break
(e.g. by minutes played) and no Admin override or deciding vote: it is a
player nomination, so the team’s votes decide.

### Offline writes

When wired, rely on Firestore’s offline persistence: a vote queues and the
listener catches up when the device is back online. Until then, the stub
fails soft and shows that voting is not wired. The app must remain usable
offline for local play (existing freemium rule).

### Privacy for juniors

- Votes are **team-internal** (same audience as roster, chat, and reports).
- The live tally shows **player names and counts only**, not who voted.
- `nominatorId` / `nominatorDisplayName` are stored for rules and optional
  admin audit, not for a public roll.
- Do not publish nominations outside the team. The match report includes
  the result (winner names only, no voters) in v1, per Andrew (20 Nov 2026).
- The public privacy policy gets a short sentence before this ships
  (“shared teams may store player-of-the-match votes, visible only to the
  team”) in `StoreAssets/PRIVACY_POLICY.md` / `docs/privacy.html` (approved by
  Andrew, 20 Nov 2026, §10 Q8).
- Rationale (Andrew): the vote is the team’s players nominating their peers,
  whether cast through a parent’s account or directly, so the spec does not
  frame it as “no data from children.”

---

## 8. Out of scope for the first scaffold commit

- Wiring End game or Reports buttons.
- Writing or listening to Firestore.
- Deploying or even patching `firestore.rules` (the first commit had a snippet + this doc only; the rules are now patched on the branch, §5, but not deployed).
- Adding `SlotId` to `PlayerStats`.
- Parent–child member links.
- MCP tools or push notifications.
- Adding the result to the match report. This **is in v1 scope** (§10 Q7);
  it is only left out of this first scaffold commit and follows once the
  service is wired.
- Changing maximum squad size (separate branch).

---

## 9. Implementation notes

- No Plugin.Firebase types in Pages or ViewModels (`AGENTS.md`).
- Register `INominationService` in `MauiProgram` DI; pages resolve via the
  existing `Handler.MauiContext.Services` pattern.
- Fail soft; local-only play must keep working.
- iOS and Android share `NominationsPage`.
- Feature remains free; `TeamNominations` stays in the catalog. Monetization,
  if any, would be later via entitlements like other shared-team features.

---

## 10. Open questions for Andrew

1. ~~**Who nominates?**~~ **Answered (20 Nov 2026):** kids nominate through
   their parents’ accounts. Each signed-in member casts one vote on behalf of
   their child (Option A mechanics). The parent–child link table (full
   Option C) is deferred to a later version.
2. ~~**May a parent vote for their own child?**~~ **Answered (20 Nov 2026):**
   yes. No restriction in v1; parents can vote for any present player in the squad.
3. ~~**Voting window:**~~ **Answered (20 Nov 2026):** both triggers. Voting
   auto-closes 24 hours after the session’s `endTime`, and an Admin may close
   it early. Whichever comes first wins.
4. ~~**May members who joined the team after the game still vote?**~~
   **Answered (20 Nov 2026):** yes. Any current member may vote while voting
   is open, including members who joined after the match.
5. ~~**Are absent / inactive players eligible?**~~ **Answered (20 Nov 2026):**
   no. Only players who were present / active in that match’s squad can be
   nominated.
6. ~~**Ties:**~~ **Answered (20 Nov 2026):** ties stand as joint winners. No
   Admin tie-break or deciding vote, because this is a player nomination.
7. ~~**Should the match report / email include the POTM result?**~~
   **Answered (20 Nov 2026):** yes, in v1. The match report includes the
   nomination result (winner or joint winners) once voting has closed.
8. ~~**Privacy policy wording**~~ **Answered (20 Nov 2026):** yes, add a
   sentence about team-internal votes before this ships: votes stay inside
   the team, the tally shows names and counts only, and nothing is published
   outside the team.
9. ~~**Nominations on local (offline) teams?**~~ **Answered (20 Nov 2026):**
   disabled in v1. Andrew wants value-adding features to stay shared-team
   only, because shared-team features may later become paid under a
   subscription model.

---

## 11. Next steps (after this scaffold)

1. Andrew answers the remaining questions in §10 (3–9). Who nominates and
   own-child voting are decided (§2, §7): no self-nomination check is needed
   in the service or rules for v1.
2. ✅ Add `SlotId` to the session player stats / squad snapshot (done:
   `PlayerStats.SlotId` + `WasPresent`, session schema v2, roster fallback for
   older sessions; §3.3).
3. Implement `NominationService` (transactional submit + tally listener) and
   emulator rules tests. ✅ Done: service (§6), rules patched on the branch
   and emulator tests passing (§5). Not deployed.
4. Build the real `NominationsPage` (session picker, nominee list, live tally).
5. Add End-game prompt and a Reports entry, behind the same page.
   Include the nomination result (winner or joint winners) in the match
   report once voting has closed.
6. Extend `DeleteTeamAsOwner` (and session delete, if added) to clear
   nominations subcollections.
7. Show Andrew the rules diff; **deploy only with his approval**.
8. TestFlight on a shared team; merge only after he is happy.
9. Later version: parent–child link table (full Option C), if clubs want
   one vote per player.
