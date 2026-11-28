# Club Owner recovery fix and club player edit (28 Nov 2026)

## Club Owner recovery (app change only, no rules change, nothing to deploy)
Found while testing the Windows client: recovering a club with the Club Owner Recovery Code moved ownership
(`clubs/{c}/metadata/info.createdBy`) but then tried to give the owner a team Admin seat on the entered "anchor"
team (`teams/{t}/members/{uid}` role admin). The rules refuse that while another team Admin exists (club admins
are Details-only there), so the app showed "Invalid recovery code" after ownership had already moved.

Fix (matches the Club Admin design: recovery restores club Owner powers, gives no automatic team Admin seat):
- `CloudTeamService.RestoreClubOwnerAsync` no longer writes the anchor-team membership. It writes the metadata proof,
  strips `recoveryHash`, and writes `clubs/{c}/members/{uid}` (role owner, now with `uid` so club-membership discovery finds it).
- A failure after the proof step is reported as "accepted, but saving your club membership failed, enter the code again",
  not as an invalid code.
- `TeamDetailsPage` (Recover Owner Access, club scope): no team is selected locally and no team Admin role is cached;
  the message points to Settings > Club admin, where the Owner can remove a team Admin and then join the team.
- Rules unchanged (`firestore.rules` identical). New rules test in `club-owner-recovery.test.mjs` proves: proof step needs the
  right hash; owner membership succeeds; admin seat and member seat on a team with another Admin stay refused;
  the recovered Owner can remove a team Admin and the previous owner cannot.
Team Owner Recovery Code flow is unchanged (it uses adminCodeHash and still restores the team Admin seat).

## Club player edit
Club players > tap a player > Edit (club owner and club admins). Same form as Add: name, preferred name, date of birth.
Uses the existing `IClubPlayerService.UpdateAsync` (PATCH of only the changed fields plus nameKey/dob parts/updatedAt/updatedBy),
so team assignments and stats are untouched. Duplicate-name and date-of-birth-on-a-team confirmations. Works on archived players.
Coaches are unchanged (stats only; rules would allow name edits for coaches, date of birth is owner/admin only).
Existing rules already allow this; `club-players.test.mjs` has a new test using the exact edit field set.
