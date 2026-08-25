# Feature catalog (Turf Time)

Canonical list of user-facing capabilities for freemium planning.  
**Policy today:** all listed features are **free** (`IEntitlementService` / free-all returns true for everything).

When adding a capability that might ever be free vs paid, add a row here in the **same PR**.

See also: [`FREEMIUM_RULES.md`](FREEMIUM_RULES.md).

---

## Status legend

| Status | Meaning |
|--------|---------|
| **Free** | Ships free; intended to stay free |
| **Free (candidate paid)** | Free now; may require entitlement later |
| **Planned paid** | Not monetized yet; design as gateable |
| **Not shipped** | Not in product yet |

---

## Catalog

| Feature id | Description | Status today | Notes |
|------------|-------------|--------------|--------|
| `LocalGame` | Match timer, rotation, on-field play | Free | Never gate |
| `RotationBasis` | Options: Sequential / Time Based / Position Based / Manual selection of who rotates | Free | Device Preference `game.rotationBasis` (default Time Based); Manual keeps countdown reminder; never gate |
| `FieldView` | Game tab Field View (pitch + right-gutter Bench + Absent strip behind goalie) | Free | Default surface for view-only; admin third View toggle; Absent stays visible during match; never gate |
| `FieldViewPlacement` | Interactive Field View: 4×4 pitch cells; Setup tap-place/rename; live: Bench taps = rotation queue, Field arm→Bench (token=direct sub, area=FIFO), Absent→Bench for late arrivals (no live Field→Absent) | Free | Reuses `SetPlayerPosition` / live substitute helpers / `SwapPlayerRoles`; never gate |
| `LocalReports` | Reports from on-device session history | Free | Never gate |
| `LocalTeams` | Create/edit teams stored on device | Free | Never gate |
| `QrTeamShare` | Share/import team via QR (device-to-device) | Free | Offline-friendly; keep free |
| `GoalScorerAssistSelect` | Select scorer/assist for reports | Free | |
| `CloudTeamSync` | Sync roster/team state via Firebase/Firestore | Free (candidate paid) | **UI unlocked 2026-07-16**; gate at create/enable-sync + service write later |
| `CloudSessionArchive` | Cloud-backed session history (full sessionJson incl. scorer/assist) | Free (candidate paid) | **Enabled** for `team_mode=shared` |
| `MultiDeviceAdmin` | Admin role, multi-device team administration | Free (candidate paid) | **UI unlocked** (create/join/rejoin) |
| `PushTeamAlerts` | FCM / push for team events | Free (candidate paid) | Use no-op when disabled |
| `CloudTeamJoin` | Join existing cloud team | Free (candidate paid) | **UI unlocked** |
| `CloudLocationShare` | Location under Details tab (match venue / GPS) for shared teams | Free (candidate paid) | **Details tab unlocked** for shared mode; Location is a Details submenu |
| `CloudMatchSchedule` | Sync match date/time/arrive/venue for shared teams (`teams/{id}/details/location`) + live watch | Free (candidate paid) | Admin writes; members watch; local Preferences mirror; status/updated UI on Location page |
| `MatchReminders` | Local match reminders (day before / morning / leave before arrive) from Location schedule + Settings → Options | Free | Device-local notifications; reschedule on schedule sync + option changes; not system Clock alarms |
| `TeamChat` | Chat tab for shared teams | Free (candidate paid) | **Tab unlocked** for shared mode. Identity: user-entered **display name** stored on `teams/{{id}}/members/{{uid}}.displayName` + local `user_name`; messages denormalize `senderName` for UI/push |
| `TeamKit` | Kit under Details (arrive / warm-up / game / departure / non-playing / special event) | Free | Admin edit; members view-only; team-scoped Preferences (same pattern as Location) |
| `TeamDuties` | Match-day duties under Details (duty officer, canteen, grounds setup/pack-up, other) | Free | Admin edit; members view-only; team-scoped Preferences |
| `TeamNominations` | Nominations under Details | Not shipped | Placeholder “Coming soon…” under Details |

---

## Adding a feature

1. Choose a stable `FeatureId` (PascalCase, no spaces).  
2. Add a row with status.  
3. If code has a `Feature` enum, add the same id there.  
4. Call sites that perform the capability should eventually go through `IEntitlementService.IsEnabled(...)`.  
5. Cloud entry points should use a single access helper (future paywall).

---

## Monetization intent (not active)

When adoption supports paid tiers, **default plan**:

- Keep pure local play, local reports, and QR share **free**.  
- Consider entitlements for **cloud sync, multi-device admin, push, cloud archives**.  
- Prefer **additive** paid value; do not gut free experience.

Exact pricing and IAP vs subscription are product decisions; architecture only requires feature ids + entitlement checks.
