# Clubs greenfield wipe (2026-09-22)

Club + Team is a breaking schema change. **No migration** — delete existing Firebase team data before shipping.

## Collections to clear (project `turf-timer`)

- `teams` (all subcollections)
- `invite_codes`
- `activeControllers`
- `clubs` (if any test docs exist)

## After wipe

1. Deploy updated `firestore.rules` (`firebase deploy --only firestore:rules --project turf-timer`).
2. Recreate clubs/teams from the app (Create → new club + team, nickname, or managed club).
3. Optional: bump a local `cloud_schema_version` preference later to ignore stale `team_id_list` on devices.

## Model reminder

- `clubId` is identity; `clubName` is a label (duplicates allowed).
- Team invites carry `clubId`/`clubName` on `invite_codes`.
- Nickname teams have no club.
