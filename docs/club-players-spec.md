# Club-level player list and team assignment — specification

Status: **slice 3 built** (list + add, assignment, Change player, archive/delete,
and per-player match stats). Branch `feature/player-stats`. Updated 24 Nov 2026.
Nothing here is deployed; any Firestore rules or Cloud Function change needs
Andrew's approval before deploy. See §14–§16 for what is built and what is still to do.

Proposed feature id: `ClubPlayerList` (club / shared teams only; part of a future
club subscription, decision 13). Add it to [`FEATURES.md`](FEATURES.md) when built.

---

## 1. What Andrew asked for

- A club keeps **one player list** (up to ~200 players across all its teams).
- Players are **added to the club once**, then **assigned** from that list to one or
  more teams. Some kids play for more than one team (a warning shows at 3 teams;
  there is no hard cap, decision 6).
- Each player's record **builds up over time**: the teams they were assigned to, plus
  goals, assists and other stats, each tagged with the **team** and **match date/time**.
  It should be easy to see a player's goals and assists **per team and across teams**,
  and to **filter stats by season**.
- **Team creation changes for club teams**: instead of renaming the default
  `#01 Player` … `#16 Player` slots, you **pick players from the club list**.
  Nickname teams that don't belong to a club keep today's rename flow.
- **Type to filter**: as you type a name, the club list narrows. If nobody matches,
  you can **add the player to the club right there**.
- **Eligibility**: a player may play for a team in **their own age group or older**,
  never a younger team. Players can always play up, and can never play down. So
  players need a **full date of birth**, and teams need an **age group**. Age groups
  use the club's 1 January cutoff (§5).
- **Filters**, so nobody scrolls through 200 names.

## 2. What exists today

| Thing | Where | Notes |
|---|---|---|
| Club | `clubs/{clubId}/metadata/info`, `clubs/{clubId}/members/{uid}` | Club roles: `owner`, `admin`, `member`. Joining a club team also adds you to the club as `member`. |
| Team | `teams/{teamId}/metadata/info` | Club teams carry `clubId` / `clubName`. Nickname teams have no club. Team roles: `admin`, `member`; owner = `createdBy`. |
| Roster | `teams/{teamId}/roster/data` (`RosterSnapshot`, version 3) | **16 fixed slots** (`SlotId` 1–16; slots 10–16 start inactive). Each slot holds just a **name**. There is no player identity beyond the slot. |
| Naming players | Game tab, pre-game: tap a player and choose **Rename Player** | Free text. Same for club and nickname teams. |
| Matches | `teams/{teamId}/sessions/{sessionId}` (`sessionJson`) | Goals are events with `scorer` / `assist` **names** (when the scorer/assist option is on). Player stats are keyed by **name** (and `SlotId` on the nominations branch). |

So today a "player" is a name in a slot of one team. The same child in three teams is
three unrelated names, and nothing can add up their goals.

## 3. Concepts

- **Club player**: a person in the club list. Has a stable `playerId`.
- **Assignment**: a club player linked to a team (a roster slot points at the `playerId`).
  Each new season gets **fresh team assignments**; players stay in the club list and
  are reassigned yearly. Assignment history is kept (decision 8).
- **Age group**: a team label such as `U10`, plus the **season** year it applies to.
  The age group is based on the player's age on **1 January of the season year**.
- **Borrowed player**: a club player filling in for **one match** on a team they are not
  assigned to. Borrowed stats are recorded separately and are not added to overall or
  team totals (decision 11).
- **Guest**: a name-only fill-in who isn't in the club list (no stats kept).

## 4. Data model (Firestore)

### 4.1 Club players — `clubs/{clubId}/players/{playerId}`

| Field | Type | Notes |
|---|---|---|
| `name` | string | Display name, e.g. `Sam Lee`. |
| `preferredName` | string or null | Optional preferred name (decision 12). No jersey number. |
| `nameKey` | string | Lower-case, trimmed, single spaces, accents removed, from `name` and `preferredName`. Used for search and duplicate checks. |
| `dateOfBirth` | string (ISO date, `YYYY-MM-DD`) or null | **Full date of birth** (day, month and year), required for new players (decision 2). Null only during migration until filled in. |
| `teams` | array of `{teamId, teamName, season, assignedAt, removedAt?}` | Assignment history. Kept small (a handful per season). Fresh assignments each year; history stays. |
| `teamIds` | array of string | **Current** teams only (for filters and rules). Mirrors entries in `teams` with no `removedAt`. |
| `status` | `active` or `archived` | Archived players are hidden from pickers but keep their stats. |
| `totals` | map | `{matches, goals, assists, potm, minutes}` across the player's own (non-borrowed) assignments. **Written only by a Cloud Function** (§6). Borrowed matches are excluded. |
| `byTeam` | map | `{teamId: {matches, goals, assists, potm, minutes}}` for assigned teams only. Cloud Function only. Borrowed matches are excluded. |
| `borrowed` | map | `{matches, goals, assists, potm, minutes}` for one-match fill-ins only, marked borrowed. Cloud Function only. **Not** added to `totals` or `byTeam`. |
| `borrowedByTeam` | map | `{teamId: {matches, goals, assists, potm, minutes}}` for borrowed appearances, so the player card can show e.g. "2 goals for U11 Blue (borrowed)" in one read. Cloud Function only. **Not** added to `totals` or `byTeam`. |
| `createdAt`, `createdBy`, `updatedAt`, `lastUsedAt` | | `lastUsedAt` feeds the "Recently used" filter. |

### 4.2 Fast list — `clubs/{clubId}/playerIndex/v1` (one document)

Reading 200 player documents each time a picker opens costs 200 reads. Instead, one
small index document holds `[{id, name, preferredName, nameKey, dateOfBirth, teamIds, status, lastUsedAt}]`
for the whole club (200 × ~140 bytes ≈ 28 KB, far below the 1 MB limit). A Cloud
Function keeps it in sync whenever a player document changes. Pickers read this one
document (with offline cache) and open a full player document only when needed.

### 4.3 Team age group — add to `teams/{teamId}/metadata/info`

| Field | Example | Notes |
|---|---|---|
| `ageGroup` | `U10` | Or `Open` (no age limit). |
| `season` | `2027` | The year the age group applies to. |
| `maxAgeOn1Jan` | `9` | Derived from `ageGroup`: U10 → 9. Players whose age on 1 January of `season` is this age **or younger** are eligible (they may play up). |

The club stores its age rule once in `clubs/{clubId}/metadata/info`
(`ageRule`: age on **1 January of the season year**; one rule per club, decision 1).

### 4.4 Roster slots link to players — `teams/{teamId}/roster/data`

Keep the 16 slots; the game engine, rotations and Field View don't change. Each
`PlayerSnapshot` gains:

| Field | Notes |
|---|---|
| `playerId` | Club player id, or empty for nickname teams, guests and unused slots. |
| `borrowed` | `true` for a one-match fill-in (§7.7). Cleared after End game. |

`name` stays in the slot as a copy so older screens, reports and QR share keep working.
Roster `Version` goes from 3 to 4.

### 4.5 Per-match stats — `clubs/{clubId}/playerMatches/{sessionId}_{playerId}`

One document per player per match, written at **End game** for club teams:

| Field | Notes |
|---|---|
| `playerId`, `teamId`, `teamName`, `sessionId` | |
| `matchStartUtc`, `season` | Match date/time and season. |
| `goals`, `assists` | Counts after score corrections (same logic as PR #14's report). |
| `goalEvents` | `[{half, elapsedSeconds, assistPlayerId?}]` and assists the same way, so the timeline can be rebuilt. |
| `minutes`, `goalieMinutes` | From the session's player stats. |
| `potm` | `true` when the player won (or jointly won) player of the match. |
| `borrowed` | `true` if they were filling in. Borrowed matches are rolled into the player's `borrowed` / `borrowedByTeam` maps only, never into `totals` or `byTeam`. |

Using the fixed id `{sessionId}_{playerId}` makes the write **repeatable**: saving the
same match twice overwrites instead of double-counting.

### 4.6 Goal events record ids as well as names

`GoalDetailModal` keeps saving `scorer` / `assist` names (reports and older app versions
read them) and adds `scorerPlayerId` / `assistPlayerId` when the slot has a `playerId`.

## 5. Eligibility (age groups)

- The age group is based on the player's **age on 1 January of the season year**
  (decision 1). A player who is 9 on 1 January is U10, even if they turn 10 on 2 January.
- A team with age group UN in season S accepts players whose age on 1 January of S is
  **N − 1 or younger**. That means a player can play in their own age group or **play
  up** (an older team), never **down**. Players can always play up.
- Example: season 2027, `U10` → `maxAgeOn1Jan` is 9. A player born 2 January 2017 is 9
  on 1 January 2027 and may play for U10 or older. A player born 1 January 2017 (or
  31 December 2016) is 10 on 1 January 2027, is U11, and may not play down for U10. A
  player born in 2018 is 8 or 9 on 1 January 2027 and may play up for U10.
- `Open` teams accept everyone.
- **Missing date of birth**: required for new players. During migration a missing date
  of birth is shown with a "date of birth missing" badge and allowed, with a prompt to
  fill it in (decision 2).
- Enforced in the **service** (picker filter and assign call). Rules check it as well
  (§9), so it can't be bypassed by an old client or the MCP API.
- There is **no admin override to play down**. Players can never play down (decision 3).

## 6. How stats add up

**As built (slice 3, no Cloud Function):**

1. **During the match**: goal events still store scorer/assist **names** (so existing
   reports are unchanged). New events also store `scorerPlayerId` / `assistPlayerId`
   when the slot is linked to a club player. The session summary's `PlayerStats`
   includes `PlayerId` going forward.
2. **End game** (club team, shared mode): after the session document is saved, the app
   upserts one document per linked club player who was present (or who scored / assisted)
   at `clubs/{clubId}/players/{playerId}/matches/{sessionId}`. Re-saving the same
   session overwrites the same document (idempotent). Players without a `playerId` on
   the slot are skipped — there is **no backfill** of past reports (decision 10).
3. **Totals** are computed on the client from those match documents, filtered by season
   (decision 8). Clients still never write `totals` / `byTeam` / `borrowed` /
   `borrowedByTeam` on the player document. A Cloud Function and the flat
   `playerMatches` collection in the earlier draft are not used.
4. **POTM**: written onto the same match documents once voting has closed (an Admin
   closes it, or the first time someone on the team opens Nominations or Reports after
   the 24-hour window). Until then the match row has `potm: false`.
5. **Borrow** was dropped and is not recorded.

v1 stats are matches, minutes (field time, whole minutes), goals, assists and POTM
(decision 7).

## 7. Screens and flows

### 7.1 Club players screen (club admins and coaches)

Reached from Team Details → Club → **Players**.

- **Search box** at the top. Filters as you type, matching any word in the name or
  preferred name (`lee` finds `Sam Lee`), ignoring case and accents.
- **Filter chips** under the search (with counts): **Age group** (multi-select),
  **Not on a team**, **On another team**, **On this team**, **Recently used**,
  **Missing date of birth**, **Archived**.
- Each row: name (and preferred name if set), date of birth / age group, and small team
  badges (`U10 Red`, `U11 Blue`).
- Sort: name (default) or date of birth.
- **Add** button, **Bulk add**, and tap a row for the player card (teams, totals,
  per-team breakdown, borrowed stats shown separately, recent matches, filterable by
  season).

### 7.2 Adding one player — inline "Add X to the club"

- When the search text has no exact match, the list ends with a row:
  **➕ Add "Sam Lee" to the club**.
- Tapping it asks for **date of birth** (a date picker, defaulting to a date that makes
  the player eligible for the current team when opened from a team). Name comes from the
  search box. Date of birth is required. Preferred name can be added later on the
  player record.
- From a team picker, the new player is **added to the club and selected for the team**
  in one step.

### 7.3 Near-duplicate guard

Before saving a new player, compare `nameKey` against the club:

- **Exact match** → "Sam Lee (12 Mar 2017) is already in the club" → **Use existing** / **Add anyway**.
- **Close match**: one or two letters different (`Sam Lea`), first/last swapped
  (`Lee Sam`), or nickname vs full name with the same surname and the same or an
  adjacent year of birth → "Did you mean Samuel Lee (12 Mar 2017, U10 Red)?"
- Club admins can later **merge** two records (stats and assignments combine).

### 7.4 Bulk add — paste or CSV

- **Paste**: one player per line, `Name, date of birth` (e.g. copied from a registration
  spreadsheet; date as ISO `YYYY-MM-DD` or a clearly parsed local date). **CSV import**
  takes the same two columns (header optional).
- A **preview** shows every row with a flag: ✅ new, ⚠️ possible duplicate (tap to pick
  existing or new), ❌ missing/invalid date of birth (editable inline).
- Option on the preview: **assign all to team…** (only eligible rows are assigned).
- Nothing is written until **Import**.

### 7.5 Creating a club team (changed flow)

Today: club → team name → (later) rename default slots on the Game tab.

New, for club teams:

1. Club → **team name**, **age group** and **season**.
2. **Pick players** screen (§7.6). Selected players fill slots 1, 2, 3 … in order;
   unused slots stay empty / inactive (not `#NN Player`).
3. Done. Coaches can come back to the picker any time from the roster.

Nickname teams (no club) keep today's flow exactly: default `#01 Player` slots and
**Rename Player** on the Game tab.

### 7.6 Team player picker (multi-select assign)

- Same search box and chips as §7.1, with **Eligible only** switched on by default.
  Turning it off shows ineligible players greyed with the reason
  ("age 10 on 1 Jan 2027 — too old for U10"). There is no way to assign them; players
  can never play down.
- **Multi-select**: tick several players, then **Assign (5)**. Shows `12 / 16 slots`.
- A player already on 3 teams shows a soft warning only; there is no hard cap
  (decision 6).
- Players already on this team are pre-ticked; unticking removes them from the team
  (stats stay).
- On the Game tab for club teams, the pre-game **Rename Player** action becomes
  **Change player** (opens this picker for that one slot). Fixing a spelling is
  **Edit player**, which edits the club record. Coaches may edit name and preferred
  name; only club admins may edit date of birth, merge, archive or delete (decision 4).

### 7.7 Borrow a player (one match only)

On the Game tab before kick-off: **Borrow a player** → the picker opens across the
whole club, eligible players only, each showing their own team(s).

- The player goes into a free slot with `borrowed: true` for **this match only**.
- Their stats are saved with this team and marked `borrowed` (§4.5). They are recorded
  in the player's `borrowed` and `borrowedByTeam` maps only, **not** added to `totals`
  or to the borrowing team's `byTeam` totals (decision 11). The card shows them
  separately, e.g. "2 goals for U11 Blue (borrowed)".
- They are **not** added to the team's assignments; the slot is cleared after End game.
- If the fill-in isn't in the club at all: **Add guest** (name only, no stats kept), or
  add them to the club with §7.2.

## 8. Privacy for juniors

- Store **name, optional preferred name, and full date of birth**. No photo, address,
  school, phone or email for players. Parent contact details are not part of this
  feature. Full date of birth is now stored (decision 2), so the **privacy policy
  needs wording** about that before release (as done for nominations).
- The full club list is visible to **club owners/admins and coaches** (team admins of the
  club's teams), not to every parent (decision 5). Parents keep seeing just their own
  team's roster, as today.
- Stats are club-internal: never public, never shown outside the club's teams.
- Club admins can **archive or delete** a player from club or local teams (player
  document, their `playerMatches`, roster links, index entry). Names already inside
  old match reports are **never removed** from those reports (decision 9).
- Privacy policy also needs a sentence about club player lists before release.

## 9. Security rules (outline)

| Path | Read | Create / update | Delete |
|---|---|---|---|
| `clubs/{c}/players/{p}` | Club owner/admin, club **coach** | Create: owner/admin/coach. Edit date of birth, archive, merge: owner/admin only (decision 4). Coaches may edit `name` / `preferredName`. Coach may change `teamIds` / `teams` only to add or remove a team **they admin**. `totals` / `byTeam` / `borrowed` / `borrowedByTeam` never by clients. | Owner/admin |
| `clubs/{c}/playerIndex/v1` | Owner/admin/coach | Cloud Function only | Cloud Function only |
| `clubs/{c}/playerMatches/{id}` | Owner/admin/coach; team members for their own team's matches | Members of `teamId` (whoever ends the game), with `playerId` on that team's roster or `borrowed` | Owner/admin |
| `teams/{t}/metadata/info` | unchanged | `ageGroup` / `season` / `maxAgeOn1Jan`: team admin or club admin | unchanged |

- **Coach** is a new club member role, granted automatically when someone creates or
  becomes admin of a club team (the club membership already exists for them).
- Rules re-check eligibility on assignment using the team's `maxAgeOn1Jan` (age on
  1 January of the team's `season`) and the player's `dateOfBirth`. There is no
  play-down override.
- Emulator tests for every row above, as for nominations.
- Team delete (`DeleteTeamAsOwner`) and the scheduled `cleanupDormantTeams` must also
  remove that team from players' `teamIds` (history entries get `removedAt`; stats stay).

## 10. Migrating existing club teams

1. On first open after the update, a club admin sees **Link players to the club**
   for each club team.
2. For every slot with a real name (not `#NN Player`): if the club has the same
   `nameKey`, suggest linking; otherwise suggest creating a new club player.
3. **Same name on two teams** (e.g. U10 and U11) is shown as "probably the same child
   playing up" with **Same player** / **Different players**.
4. Missing dates of birth are collected on the same screen (can be skipped, flagged
   later by the "Missing date of birth" chip).
5. Teams that are not migrated keep working exactly as today (names only, no stats).

Past goals and assists from existing match reports are **not** backfilled (decision 10).

Since there are no live users yet, roster version 4 can require the new app version for
club teams (older versions writing the roster would drop `playerId`).

## 11. Related work

- **Goals report (PR #14)**: the report keeps reading scorer/assist **names** from match
  events. This spec adds `scorerPlayerId` / `assistPlayerId` next to them and reuses the
  report's correction logic when counting goals at End game.
- **Nominations (PR #13)**: votes use `nomineeSlotId` + `nomineeName`. For club teams the
  slot can also carry `nomineePlayerId` (copied from the session squad), so POTM wins
  roll into `playerMatches.potm` and the player's totals. Slot-based voting stays valid
  for nickname teams. Merge nominations first.
- **MCP API**: `create_team` / `update_roster` would later accept club player ids; not in v1.
- **Freemium**: club / shared teams only; the club player list is part of a future club
  subscription (decision 13). Local play and nickname teams are untouched.

## 12. Suggested build order

1. Data model, rules (with emulator tests) and Cloud Function for the index and totals
   (including the separate `borrowed` maps).
2. Club players screen: search, chips, inline add, near-duplicate guard, bulk paste.
3. Team age group/season, new club-team creation flow, team picker, Change player.
4. Player ids in goal events, End-game `playerMatches`, player card with per-team and
   borrowed stats.
5. Migration screen, borrow a player, CSV import.

## 13. Decisions (23 Nov 2026)

1. **Age-group rule**: one rule per club. U-numbers use a 1 January cutoff: a player
   who is 9 on 1 January of the season year is U10, even if they turn 10 on 2 January.
2. **Date of birth**: full date of birth (day, month and year) is required, not just
   the year.
3. **Playing down**: no playing down. Players can always play up, and can never play
   down. There is no admin override.
4. **Who manages the list**: coaches can add players and assign them to their own
   teams; only club admins edit birthdays, merge, archive or delete.
5. **Who can see the full club list**: club admins and coaches only.
6. **Team limit**: warning only, no hard cap of 3 teams.
7. **Which stats**: v1 stats are matches, minutes, goals, assists and POTM. Others
   (goalie minutes, clean sheets, cards) come later.
8. **Seasons**: stats are filterable by season. Fresh team assignments each year;
   history is kept. Players stay in the club list and are reassigned yearly. Players
   can always play up.
9. **Deleting a player**: names are never removed from old match reports — once a
   name is in a report, leave it there. Archiving and deleting players from club
   teams or local teams is still allowed (see decision 4).
10. **Backfill**: no backfill of past goals or assists from existing match reports.
11. **Borrowed stats**: counted separately, marked "borrowed", and not added to the
    borrowing team's totals or to the player's overall totals.
12. **Extras**: preferred name field on the player record, not jersey number.
13. **Paid tier**: the club player list is part of a future club subscription.

## 14. Slice 1 (built 23 Nov 2026)

- **Model**: `Models/ClubPlayer.cs` (§4.1 fields; stats maps read-only for clients).
  Helpers `Helpers/ClubPlayerNames.cs` (nameKey: lower-case, trimmed, single spaces,
  accents removed; search; exact duplicate) and `Helpers/ClubAgeGroups.cs` (age on
  1 January of the season, `U{age+1}`, play up never down). Console tests:
  `tests/ClubPlayersTests` (`dotnet run`).
- **Service**: `IClubPlayerService` / `ClubPlayerService` (list, add, update; REST like
  the other services). Reads the `players` collection directly; the `playerIndex/v1`
  document and its Cloud Function are a follow-up.
- **Coach, as implemented in the rules**: rules can't search a club's teams, so instead
  of a stored `coach` role the caller's club membership carries `coachTeamId`. The rule
  `isClubCoach` checks live that the caller is Admin of that team **and** that team's
  `clubId` is this club. The app sets `coachTeamId` when a team Admin opens Club players
  from that team. Losing team Admin removes coach access straight away.
- **Rules** for `clubs/{c}/players/{p}`: read and create for owner/admin/coach; full ISO
  date of birth required on create; no team assignments on create yet; coaches edit
  name / preferred name only; owner/admin edit date of birth, archive, delete; clients
  never write `totals` / `byTeam` / `borrowed` / `borrowedByTeam`. Tests:
  `firestore-tests/test/club-players.test.mjs`.
- **Screen**: Details → **Club players** (club teams; shown to team Admins and club
  owners/admins). Search, list (name, preferred name, date of birth, age group this
  season, current teams), Add player form, inline "Add "X" to the club", and the exact
  duplicate prompt (Use existing / Add anyway). Archived players are hidden.

## 15. Slice 2 (built 24 Nov 2026)

- **Team age group and season** on `teams/{teamId}/metadata/info` (`ageGroup`, `season`,
  `maxAgeOn1Jan`). Set when creating a team under a club, and editable in Team Admin
  for existing club teams. Open has no age limit.
- **Roster v4**: each slot may store `playerId` (and a reserved `borrowed` flag). The
  16-slot game engine, rotations and live Game tab swipes are unchanged. `name` stays
  in the slot as a copy.
- **Assignment**: coaches and club admins assign from **Change player** (setup-phase
  name tap on a club team). Eligibility is play-up only, 1 January cutoff, enforced in
  `ClubPlayerService` and in the Firestore rules (one team added or removed per write;
  `assignmentTeamId` names that team; `dobYear`/`dobMonth`/`dobDay` support the age
  check). Team count is a warning only (3+ current teams). History is kept in
  `teams` / `teamIds` (fresh each season, `removedAt` on unassign).
- **Change player** replaces Rename Player on club teams. Nickname and local teams
  keep Rename Player. Clearing a slot unassigns the player and restores `#NN Player`.
- **Borrow** was later dropped (not built).
- **Tests**: `tests/ClubPlayersTests` (eligibility + assignment history) and
  `firestore-tests/test/club-players.test.mjs` (assign, unassign, no play-down, Open,
  coach may only assign to their own team).
- Archive, restore and delete for owners/admins were added after slice 2 (tap a player
  row). Coaches do not see those actions.

## 16. Slice 3 (player stats, built 24 Nov 2026)

- Per-match rows at `clubs/{clubId}/players/{playerId}/matches/{sessionId}` (§6).
- Details → Club players → tap a player → **Stats** (owners, admins and coaches).
  Season filter, totals, and a per-match list (team, date, minutes, goals, assists,
  POTM).
- Rules: club managers may read; a member of the match's team (or a club owner/admin)
  may write. Tests in `firestore-tests/test/club-players.test.mjs`. Helper tests in
  `tests/ClubPlayersTests`.
- Production rules for this subcollection are **not deployed** until Andrew approves.

Still to do: filter chips, close-match duplicates, bulk add/CSV, a fuller player card,
merge UI, `playerIndex` Cloud Function, migration screen (link existing named slots),
removing teams from players on team delete, privacy policy wording for date of birth.
Borrow was dropped.
