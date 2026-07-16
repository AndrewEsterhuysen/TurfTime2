# Enable Cloud Based Features

**Status (2026-07-16): Phase 1 + 2 implemented**

Cloud/shared team UI is re-enabled. Roster and session cloud paths are aligned with
expanded local data (including goal scorer/assist in session events). Everything remains **free**.

---

## What was done

### Phase 1 — UI unlock

| File | Change |
|------|--------|
| `Pages/TeamDetailsPage.xaml` | Shared/Local checkboxes + labels visible; shared list, join, rejoin, create-shared restored; local sections default hidden until mode selected |
| `Pages/TeamDetailsPage.xaml.cs` | Mode-aware `OnAppearing`; `UpdateCreateTeamSubSections` branches on Shared vs Local |
| `AppShell.xaml` | Location tab no longer hard-hidden |
| `AppShell.xaml.cs` | `ChatTab` and `LocationTab` visible only when `team_mode != local` |
| `Pages/HelpPage.xaml.cs` | Shared teams + view-only mode section |

### Phase 2 — Data path hardening

| File | Change |
|------|--------|
| `Services/CloudRosterService.cs` | Defensive Firestore read (optional fields; `lastModified` **or** `lastModifiedUtc`; countdown dual keys); write both timestamp/countdown keys; `SetAuthToken`; skip cloud when local mode |
| `Helpers/GamePageSaveBridge.cs` | Canonical field names aligned with `RosterSnapshot` / `CloudRosterService`; forwards auth to cloud services |
| `Services/SessionStorageService.cs` | Full `GameSession` in `sessionJson` (events + scorer/assist); top-level `schemaVersion`, scores, teamName, location; `SetAuthToken`; auth retry |
| `Services/GameLoggerService.cs` | Cloud save only when `team_mode == shared`; better logging |

### Data model notes

- **Roster** (`teams/{id}/roster/data`): live game state — no scorer/assist fields (correct; those are session events).
- **Sessions** (`teams/{id}/sessions/{sessionId}`): full `sessionJson` carries events with `Details.scorer` / `Details.assist`.
- **Local teams** (`local_*` / `team_mode=local`): never written to Firestore.

---

## Manual QA checklist

- [ ] Create **Local** team → play → reports with scorer/assist (device only)
- [ ] Create **Shared** team → admin invite code shown
- [ ] Join as **member** on second device → view-only banner; no cloud roster writes
- [ ] Shared admin play → second device sees roster/scores
- [ ] Shared game end → session appears in cloud Reports with goals/assists
- [ ] Chat + Location tabs only for shared mode
- [ ] Airplane mode: local play still works; cloud fails soft

---

## Historical enable steps (reference)

The original local-only release hid UI only; services already existed. Phase 1 reverted those hides.
Phase 2 was required because local session data (scorer/assist) and dual roster writers had drifted.

See also: `docs/FREEMIUM_RULES.md`, `docs/FEATURES.md`, `AGENTS.md`.
