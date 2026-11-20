# Club-level player list and team assignment — specification

Status: **spec only, nothing built.** Branch `feature/club-players` (kept separate
from nominations, PR #13, and the goals report, PR #14). Nothing here is deployed;
any Firestore rules or Cloud Function change needs Andrew's approval before deploy.

Proposed feature id: `ClubPlayerList` (club / shared teams only; candidate paid,
same as other shared-team features). Add it to [`FEATURES.md`](FEATURES.md) when built.

---

## 1. What Andrew asked for

- A club keeps **one player list** (up to ~200 players across all its teams).
- Players are **added to the club once**, then **assigned** from that list to one or
  more teams. Some kids play for up to **3 teams**.
- Each player's record **builds up over time**: the teams they were assigned to, plus
  goals, assists and other stats, each tagged with the **team** and **match date/time**.
  It should be easy to see a player's goals and assists **per team and across teams**.
- **Team creation changes for club teams**: instead of renaming the default
  `#01 Player` … `#16 Player` slots, you **pick players from the club list**.
  Nickname teams that don't belong to a club keep today's rename flow.
- **Type to filter**: as you type a name, the club list narrows. If nobody matches,
  you can **add the player to the club right there**.
- **Eligibility**: a player may play for a team in **their own age group or older**,
  never a younger team. So players need a **birth year**, and teams need an **age group**.
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
- **Age group**: a team label such as `U10`, plus the **season** year it applies to.
- **Borrowed player**: a club player filling in for **one match** on a team they are not
  assigned to.
- **Guest**: a name-only fill-in who isn't in the club list (no stats kept).

## 4. Data model (Firestore)

### 4.1 Club players — `clubs/{clubId}/players/{playerId}`

| Field | Type | Notes |
|---|---|---|
| `name` | string | Display name, e.g. `Sam Lee`. |
| `nameKey` | string | Lower-case, trimmed, single spaces, accents removed. Used for search and duplicate checks. |
| `birthYear` | int or null | **Year only, never full date of birth** (§8). Null = unknown. |
| `teams` | array of `{teamId, teamName, season, assignedAt, removedAt?}` | Assignment history. Kept small (a handful per season). |
| `teamIds` | array of string | **Current** teams only (for filters and rules). Mirrors entries in `teams` with no `removedAt`. |
| `status` | `active` or `archived` | Archived players are hidden from pickers but keep their stats. |
| `totals` | map | `{matches, goals, assists, potm, minutes}` across all teams. **Written only by a Cloud Function** (§6). |
| `byTeam` | map | `{teamId: {matches, goals, assists, potm, minutes}}`. Cloud Function only. |
| `createdAt`, `createdBy`, `updatedAt`, `lastUsedAt` | | `lastUsedAt` feeds the "Recently used" filter. |

### 4.2 Fast list — `clubs/{clubId}/playerIndex/v1` (one document)

Reading 200 player documents each time a picker opens costs 200 reads. Instead, one
small index document holds `[{id, name, nameKey, birthYear, teamIds, status, lastUsedAt}]`
for the whole club (200 × ~120 bytes ≈ 24 KB, far below the 1 MB limit). A Cloud
Function keeps it in sync whenever a player document changes. Pickers read this one
document (with offline cache) and open a full player document only when needed.

### 4.3 Team age group — add to `teams/{teamId}/metadata/info`

| Field | Example | Notes |
|---|---|---|
| `ageGroup` | `U10` | Or `Open` (no age limit). |
| `season` | `2027` | The year the age group applies to. |
| `oldestBirthYear` | `2017` | Derived from `ageGroup` + `season` + the club's age rule (§5). Players born in this year **or later** are eligible. |

The club stores its age rule once in `clubs/{clubId}/metadata/info`
(`ageRule`, e.g. "U-number = age reached during the season year"; see open question 1).

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
| `borrowed` | `true` if they were filling in. |

Using the fixed id `{sessionId}_{playerId}` makes the write **repeatable**: saving the
same match twice overwrites instead of double-counting.

### 4.6 Goal events record ids as well as names

`GoalDetailModal` keeps saving `scorer` / `assist` names (reports and older app versions
read them) and adds `scorerPlayerId` / `assistPlayerId` when the slot has a `playerId`.

## 5. Eligibility (age groups)

- A team accepts players with `birthYear >= team.oldestBirthYear`. That means a player
  can play in their own age group or **play up** (an older team), never **down**.
- Example (club rule: U-number = age reached in the season year): season 2027, `U10`
  → born 2017 or later. A 2018-born U9 player may play up for U10; a 2016-born U11
  player may not play down for U10.
- `Open` teams accept everyone.
- **Unknown birth year**: shown with a "birth year missing" badge and allowed, with a
  prompt to fill it in (open question 2).
- Enforced in the **service** (picker filter and assign call). Rules check it as well
  (§9), so it can't be bypassed by an old client or the MCP API.
- Club admins may grant an **override** for one player on one team (with a short reason),
  for leagues that allow dispensations (open question 3).

## 6. How stats add up

1. **During the match**: nothing new; goals are logged as today, now with player ids.
2. **End game** (club team, shared mode): after the session is saved, the app writes one
   `playerMatches` document per player who was present (plus anyone who scored or assisted),
   in one batch. Offline: Firestore queues the batch and sends it when back online.
3. **Cloud Function** `onPlayerMatchWritten`: compares the old and new document and adds
   the **difference** to the player's `totals` and `byTeam[teamId]`. Because it works on
   the difference, re-saving or editing a match corrects the totals instead of adding twice.
   It also updates `lastUsedAt` and the player index.
4. **Reading**:
   - Player card: `totals` and `byTeam` straight from the player document (1 read).
   - Match-by-match history: query `playerMatches` where `playerId == X`, newest first.
   - Club or team leaderboard: sort the player index (add `goals` / `assists` to the index).
5. **Old matches** (before this feature): optional one-off **backfill** that reads past
   sessions and matches scorer names to club players (open question 10). Depends on the
   Events fix in PR #14, because cloud sessions currently load with no events.

## 7. Screens and flows

### 7.1 Club players screen (club admins and coaches)

Reached from Team Details → Club → **Players**.

- **Search box** at the top. Filters as you type, matching any word in the name
  (`lee` finds `Sam Lee`), ignoring case and accents.
- **Filter chips** under the search (with counts): **Age group** (multi-select),
  **Not on a team**, **On another team**, **On this team**, **Recently used**,
  **Missing birth year**, **Archived**.
- Each row: name, birth year / age group, and small team badges (`U10 Red`, `U11 Blue`).
- Sort: name (default) or birth year.
- **Add** button, **Bulk add**, and tap a row for the player card (teams, totals,
  per-team breakdown, recent matches).

### 7.2 Adding one player — inline "Add X to the club"

- When the search text has no exact match, the list ends with a row:
  **➕ Add "Sam Lee" to the club**.
- Tapping it asks only for **birth year** (a year wheel, defaulting to the youngest
  year eligible for the current team when opened from a team). Name comes from the
  search box.
- From a team picker, the new player is **added to the club and selected for the team**
  in one step.

### 7.3 Near-duplicate guard

Before saving a new player, compare `nameKey` against the club:

- **Exact match** → "Sam Lee (2017) is already in the club" → **Use existing** / **Add anyway**.
- **Close match**: one or two letters different (`Sam Lea`), first/last swapped
  (`Lee Sam`), or nickname vs full name with the same surname and the same or an
  adjacent birth year → "Did you mean Samuel Lee (2017, U10 Red)?"
- Club admins can later **merge** two records (stats and assignments combine).

### 7.4 Bulk add — paste or CSV

- **Paste**: one player per line, `Name, birth year` (e.g. copied from a registration
  spreadsheet). **CSV import** takes the same two columns (header optional).
- A **preview** shows every row with a flag: ✅ new, ⚠️ possible duplicate (tap to pick
  existing or new), ❌ missing/invalid birth year (editable inline).
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
  ("born 2015 — too old for U10").
- **Multi-select**: tick several players, then **Assign (5)**. Shows `12 / 16 slots`.
- A player already on 3 teams shows a soft warning (open question 6).
- Players already on this team are pre-ticked; unticking removes them from the team
  (stats stay).
- On the Game tab for club teams, the pre-game **Rename Player** action becomes
  **Change player** (opens this picker for that one slot). Fixing a spelling is
  **Edit player**, which edits the club record (club admin or coach).

### 7.7 Borrow a player (one match only)

On the Game tab before kick-off: **Borrow a player** → the picker opens across the
whole club, eligible players only, each showing their own team(s).

- The player goes into a free slot with `borrowed: true` for **this match only**.
- Their stats are saved with this team and marked `borrowed` (§4.5), so their card shows
  "2 goals for U11 Blue (borrowed)".
- They are **not** added to the team's assignments; the slot is cleared after End game.
- If the fill-in isn't in the club at all: **Add guest** (name only, no stats kept), or
  add them to the club with §7.2.

## 8. Privacy for juniors

- Store **name and birth year only**. No full date of birth, photo, address, school,
  phone or email for players. Parent contact details are not part of this feature.
- The full club list is visible to **club owners/admins and coaches** (team admins of the
  club's teams), not to every parent. Parents keep seeing just their own team's roster,
  as today.
- Stats are club-internal: never public, never shown outside the club's teams.
- **Delete on request**: club admins can permanently delete a player (player document,
  their `playerMatches`, roster links, index entry). Names already inside old match
  reports stay in those reports unless the matches are deleted too (open question 9).
- Privacy policy needs a sentence about club player lists before release (as done for
  nominations).

## 9. Security rules (outline)

| Path | Read | Create / update | Delete |
|---|---|---|---|
| `clubs/{c}/players/{p}` | Club owner/admin, club **coach** | Create: owner/admin/coach. Edit name/birth year, archive, merge: owner/admin (coach: open question 4). Coach may change `teamIds` / `teams` only to add or remove a team **they admin**. `totals` / `byTeam` never by clients. | Owner/admin |
| `clubs/{c}/playerIndex/v1` | Owner/admin/coach | Cloud Function only | Cloud Function only |
| `clubs/{c}/playerMatches/{id}` | Owner/admin/coach; team members for their own team's matches | Members of `teamId` (whoever ends the game), with `playerId` on that team's roster or `borrowed` | Owner/admin |
| `teams/{t}/metadata/info` | unchanged | `ageGroup` / `season` / `oldestBirthYear`: team admin or club admin | unchanged |

- **Coach** is a new club member role, granted automatically when someone creates or
  becomes admin of a club team (the club membership already exists for them).
- Rules re-check eligibility on assignment using the team's `oldestBirthYear` and the
  player's `birthYear` (or an override entry).
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
4. Missing birth years are collected on the same screen (can be skipped, flagged
   later by the "Missing birth year" chip).
5. Teams that are not migrated keep working exactly as today (names only, no stats).
6. Optional: backfill stats from past sessions (§6, step 5).

Since there are no live users yet, roster version 4 can require the new app version for
club teams (older versions writing the roster would drop `playerId`).

## 11. Related work

- **Goals report (PR #14)**: the report keeps reading scorer/assist **names** from match
  events. This spec adds `scorerPlayerId` / `assistPlayerId` next to them and reuses the
  report's correction logic when counting goals at End game. PR #14's fix to keep match
  events when sessions are loaded is required for any backfill.
- **Nominations (PR #13)**: votes use `nomineeSlotId` + `nomineeName`. For club teams the
  slot can also carry `nomineePlayerId` (copied from the session squad), so POTM wins
  roll into `playerMatches.potm` and the player's totals. Slot-based voting stays valid
  for nickname teams. Merge nominations first.
- **MCP API**: `create_team` / `update_roster` would later accept club player ids; not in v1.
- **Freemium**: club / shared teams only; local play and nickname teams are untouched.

## 12. Suggested build order

1. Data model, rules (with emulator tests) and Cloud Function for the index and totals.
2. Club players screen: search, chips, inline add, near-duplicate guard, bulk paste.
3. Team age group/season, new club-team creation flow, team picker, Change player.
4. Player ids in goal events, End-game `playerMatches`, player card with per-team stats.
5. Migration screen, borrow a player, CSV import, optional backfill.

## 13. Open questions for Andrew

1. **Age-group rule**: how does your league define U-numbers (e.g. "U10 = turns 10 or
   younger in the season year", or a 1 January / 31 December cutoff)? Is one rule per
   club enough?
2. **Birth year required?** Recommendation: required for new players, optional during
   migration (flagged until filled in).
3. **Exceptions**: do any competitions allow playing down (e.g. girls' or mixed teams,
   dispensations)? If so, a club-admin override per player/team with a reason.
4. **Who manages the list?** Recommendation: coaches can add players and assign them to
   **their own** teams; only club admins edit birth years, merge, archive or delete.
5. **Who can see the full club list?** Recommendation: club admins and coaches only.
6. **Team limit**: hard cap of 3 teams per player, or just a warning?
7. **Which stats?** Recommendation for v1: matches, minutes, goals, assists, POTM.
   Others (goalie minutes, clean sheets, cards)?
8. **Seasons**: do teams and assignments roll over each year (new season = new
   assignments, history kept), and should stats be filterable by season?
9. **Deleting a player**: archive by default and hard-delete on a parent's request?
   Should names inside old match reports be removed as well?
10. **Backfill**: do you want past goals/assists imported from existing match reports?
11. **Borrowed stats**: count them in the borrowing team's totals (recommended, marked
    "borrowed"), or only in the player's overall totals?
12. **Extras**: jersey number or preferred name on the player record?
13. **Paid tier**: should the club player list be part of a future club subscription?
