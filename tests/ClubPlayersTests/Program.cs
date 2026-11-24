using TurfTime2.Helpers;
using TurfTime2.Models;
int fails = 0;
void Check(bool c, string m) { Console.WriteLine((c ? "PASS " : "FAIL ") + m); if (!c) fails++; }
DateOnly D(string iso) { ClubAgeGroups.TryParseDateOfBirth(iso, out var d); return d; }

// 1. Name key normaliser
Check(ClubPlayerNames.Normalize("  Zoë   O'Neil ") == "zoe o'neil", "normalize: trim, single spaces, accents, lower");
Check(ClubPlayerNames.Normalize("JOSÉ\tÁlvarez") == "jose alvarez", "normalize: tabs and accents");
Check(ClubPlayerNames.Normalize(null) == "" && ClubPlayerNames.Normalize("   ") == "", "normalize: empty");
Check(ClubPlayerNames.BuildNameKey("Samuel Lee", "Sam") == "samuel lee sam", "nameKey includes preferred name");
Check(ClubPlayerNames.BuildNameKey("Sam Lee", " sam  lee ") == "sam lee", "nameKey: preferred same as name not repeated");
Check(ClubPlayerNames.BuildNameKey("Sam Lee", null) == "sam lee", "nameKey: no preferred name");
Check(ClubPlayerNames.CleanDisplay("  Sam    Lee ") == "Sam Lee", "clean display name");
Check(ClubPlayerNames.CleanDisplay(new string('a', 100)).Length == 80, "display name capped at 80");

// 2. Search
Check(ClubPlayerNames.Matches("sam lee", "lee"), "search: surname");
Check(ClubPlayerNames.Matches("sam lee", "SA LE"), "search: word prefixes, any case");
Check(ClubPlayerNames.Matches("zoe oneil", "Zoë"), "search: accents ignored");
Check(!ClubPlayerNames.Matches("sam lee", "ee"), "search: mid-word does not match");
Check(ClubPlayerNames.Matches("samuel lee sam", "sam"), "search: preferred name");
Check(ClubPlayerNames.Matches("anything", ""), "search: empty query matches all");

// 3. Exact duplicate
Check(ClubPlayerNames.IsExactDuplicate("sam  LEE", "Sam Lee", null), "duplicate: same name, different case/spaces");
Check(ClubPlayerNames.IsExactDuplicate("Sammy", "Samuel Lee", "Sammy"), "duplicate: matches preferred name");
Check(!ClubPlayerNames.IsExactDuplicate("Sam Lea", "Sam Lee", null), "duplicate: close match is not exact");

// 4. Age groups, 1 January cutoff (spec §5 examples, season 2027)
Check(ClubAgeGroups.AgeOn1Jan(D("2017-01-02"), 2027) == 9, "born 2 Jan 2017 is 9 on 1 Jan 2027");
Check(ClubAgeGroups.AgeGroupFor(D("2017-01-02"), 2027) == "U10", "9 on 1 Jan -> U10 even turning 10 on 2 Jan");
Check(ClubAgeGroups.AgeOn1Jan(D("2017-01-01"), 2027) == 10, "born 1 Jan 2017 is 10 on 1 Jan 2027");
Check(ClubAgeGroups.AgeGroupFor(D("2017-01-01"), 2027) == "U11", "born 1 Jan 2017 -> U11");
Check(ClubAgeGroups.AgeGroupFor(D("2016-12-31"), 2027) == "U11", "born 31 Dec 2016 -> U11");
Check(ClubAgeGroups.MaxAgeOn1Jan("U10") == 9 && ClubAgeGroups.MaxAgeOn1Jan("u17") == 16, "U10 -> 9, U17 -> 16");
Check(ClubAgeGroups.MaxAgeOn1Jan("Open") is null && ClubAgeGroups.MaxAgeOn1Jan("U") is null, "Open / bad label -> no max");
Check(ClubAgeGroups.IsEligible(D("2017-01-02"), "U10", 2027), "U10 player eligible for U10");
Check(!ClubAgeGroups.IsEligible(D("2017-01-01"), "U10", 2027), "U11 player cannot play down to U10");
Check(!ClubAgeGroups.IsEligible(D("2016-12-31"), "U10", 2027), "31 Dec 2016 cannot play U10");
Check(ClubAgeGroups.IsEligible(D("2018-06-15"), "U10", 2027), "younger player may play up");
Check(ClubAgeGroups.IsEligible(D("2010-06-15"), "Open", 2027), "Open accepts everyone");
Check(ClubAgeGroups.IsEligible(D("2012-05-05"), "U17", 2027) && !ClubAgeGroups.IsEligible(D("2009-05-05"), "U17", 2027), "U17 pool: 14 ok, 17 too old");

// 5. Date of birth parsing
Check(ClubAgeGroups.TryParseDateOfBirth("2017-03-12", out var dob) && ClubAgeGroups.ToIso(dob) == "2017-03-12", "ISO round trip");
Check(!ClubAgeGroups.TryParseDateOfBirth("12/03/2017", out _) && !ClubAgeGroups.TryParseDateOfBirth("2017", out _)
      && !ClubAgeGroups.TryParseDateOfBirth("2017-02-30", out _), "rejects non-ISO, year-only and impossible dates");

// 6. Model
var p = new ClubPlayer { Name = "Samuel Lee", PreferredName = "Sam" };
Check(p.DisplayName == "Samuel Lee (Sam)" && !p.IsArchived, "display name with preferred name");
Check(new ClubPlayer { Status = "archived" }.IsArchived, "archived status");

// 7. Team type label (Team Admin panel)
Check(ClubTeamNames.ResolveKind("clubbed", "") == ClubTeamNames.KindClubbed, "kind clubbed wins over empty clubId");
Check(ClubTeamNames.ResolveKind("nickname", "some-club") == ClubTeamNames.KindNickname, "kind nickname wins over a leftover clubId");
Check(ClubTeamNames.ResolveKind("", "club-1") == ClubTeamNames.KindClubbed, "missing kind falls back to clubId");
Check(ClubTeamNames.ResolveKind(null, "") == ClubTeamNames.KindNickname, "missing kind and clubId -> nickname");
Check(ClubTeamNames.FormatTypeLabel(false, true, "", "", "") == "Type: Local team (this device only)", "local type label");
Check(ClubTeamNames.FormatTypeLabel(true, false, "clubbed", "c1", "ZZ Test Lambton Lions FC")
      == "Type: Online · Club team (ZZ Test Lambton Lions FC)", "club type label with name");
Check(ClubTeamNames.FormatTypeLabel(true, false, "", "c1", "  ") == "Type: Online · Club team", "club type label without name");
Check(ClubTeamNames.FormatTypeLabel(true, false, "nickname", "", "") == "Type: Online · Nickname team", "nickname type label");
Check(ClubTeamNames.FormatTypeLabel(true, false, "", "", "") == "Type: Online · Nickname team", "older online team with no kind or clubId");
Check(ClubTeamNames.FormatTypeLabel(false, false, "", "", "") == "Type: Not configured", "no team selected");

// 8. Assignment eligibility (must have a known age group; play up, never down)
Check(ClubAgeGroups.IsKnownAgeGroup("u10") && ClubAgeGroups.CanonicalAgeGroup("u10") == "U10", "canonical U10");
Check(ClubAgeGroups.IsEligibleToAssign(D("2017-01-02"), "U10", 2027), "eligible to assign to own group");
Check(ClubAgeGroups.IsEligibleToAssign(D("2018-06-15"), "U10", 2027), "younger may play up");
Check(!ClubAgeGroups.IsEligibleToAssign(D("2017-01-01"), "U10", 2027), "cannot play down");
Check(!ClubAgeGroups.IsEligibleToAssign(D("2017-01-02"), "", 2027), "missing age group: not assignable");
Check(ClubAgeGroups.IsEligibleToAssign(D("2010-06-15"), "Open", 2027), "Open is assignable");
Check(ClubAgeGroups.IneligibilityReason(D("2017-01-01"), "U10", 2027)
      == "age 10 on 1 Jan 2027 — too old for U10", "ineligibility reason");
Check(ClubAgeGroups.IneligibilityReason(D("2017-01-02"), "", 2027)
      == "This team does not have an age group yet.", "missing group reason");
Check(ClubAgeGroups.IneligibilityReason(D("2017-01-02"), "U10", 2027) is null, "no reason when eligible");
Check(ClubAgeGroups.MaxAgeOn1Jan("U10") == 9, "derived maxAgeOn1Jan");

// 9. Assignment history (fresh each season, history kept, team-count warning only)
var now = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
var hist = new List<ClubPlayerTeamEntry>();
hist = ClubPlayerAssignments.Assign(hist, "t1", "U10 Green", 2026, now);
Check(hist.Count == 1 && ClubPlayerAssignments.CurrentTeamIds(hist).SequenceEqual(["t1"]), "first assign");
hist = ClubPlayerAssignments.Assign(hist, "t1", "U10 Green", 2026, now);
Check(hist.Count == 1, "same season re-assign is a no-op (no duplicate entry)");
hist = ClubPlayerAssignments.Assign(hist, "t2", "U11 Blue", 2026, now);
hist = ClubPlayerAssignments.Assign(hist, "t3", "U12 Red", 2026, now);
Check(ClubPlayerAssignments.CurrentTeamIds(hist).Count == 3, "three current teams");
Check(ClubPlayerAssignments.ShouldWarnTeamCount(hist, "t4"), "warn when a fourth team would be added");
Check(!ClubPlayerAssignments.ShouldWarnTeamCount(hist, "t1"), "no warning when already on that team");
var later = now.AddDays(1);
hist = ClubPlayerAssignments.Unassign(hist, "t2", 2026, later);
Check(hist.Count == 3 && hist.Single(x => x.TeamId == "t2").RemovedAt == later, "unassign sets removedAt, keeps history");
Check(ClubPlayerAssignments.CurrentTeamIds(hist).SequenceEqual(["t1", "t3"]), "current teams drop the unassigned one");
hist = ClubPlayerAssignments.Assign(hist, "t2", "U11 Blue", 2026, later.AddDays(1));
Check(hist.Count == 3 && hist.Single(x => x.TeamId == "t2").RemovedAt is null, "re-assign same season reopens the entry");
hist = ClubPlayerAssignments.Assign(hist, "t1", "U10 Green", 2027, later.AddYears(1));
Check(hist.Count(x => x.TeamId == "t1") == 2 && hist.Count(x => x.TeamId == "t1" && x.RemovedAt is null) == 2,
      "new season adds a fresh assignment and keeps last year's");

// Unassign must drop the player from the team even when the season doesn't match
// (or when a prior season's entry was left open). A no-op here is what left a
// player listed on a team after Clear this slot.
var mismatch = ClubPlayerAssignments.Unassign(hist, "t1", 2026, later.AddYears(1).AddDays(1));
Check(ClubPlayerAssignments.CurrentTeamIds(mismatch).All(id => id != "t1"),
      "unassign with an older season still closes every open entry for that team");
Check(mismatch.Count(x => x.TeamId == "t1" && x.RemovedAt is null) == 0,
      "no open t1 entry remains after unassign");
Check(ClubPlayerAssignments.CurrentTeamNames(mismatch, ClubPlayerAssignments.CurrentTeamIds(mismatch))
          .All(n => n != "U10 Green"),
      "current team names omit the unassigned team");

// teamIds already cleared (successful write) should hide the name even if a
// teams entry was left without removedAt (parse miss).
var staleOpen = new List<ClubPlayerTeamEntry>
{
    new() { TeamId = "t1", TeamName = "U10 Green", Season = 2026 }
};
Check(ClubPlayerAssignments.CurrentTeamNames(staleOpen, []).Count == 0,
      "empty teamIds hides a stale open teams entry");
Check(ClubPlayerAssignments.CurrentTeamNames(staleOpen, ["t1"]).SequenceEqual(["U10 Green"]),
      "teamIds still containing t1 still shows the name");


// 10. Per-match club player stats (playerId on the session, no backfill of nameless slots)
GameEvent Ev(int delta, string? scorer = null, string? scorerId = null, string? assist = null, string? assistId = null)
{
    var d = new Dictionary<string, object?> { ["delta"] = delta };
    if (scorer is not null) d["scorer"] = scorer;
    if (scorerId is not null) d["scorerPlayerId"] = scorerId;
    if (assist is not null) d["assist"] = assist;
    if (assistId is not null) d["assistPlayerId"] = assistId;
    return new GameEvent("e", DateTimeOffset.UtcNow, GameEventType.ScoreUs, "Goal", scorer, d);
}

var kickoff = new DateTimeOffset(2027, 3, 14, 9, 0, 0, TimeSpan.Zero);
var sess = new GameSession
{
    SessionId = "game-1",
    StartTime = kickoff,
    TeamName = "U10 Green",
    Summary = new GameSessionSummary(0, 3600, [
        new PlayerStats("Wayne Rooney", 2700, 900, 0, 0, 0, SlotId: 1, WasPresent: true, PlayerId: "p-rooney"),
        new PlayerStats("Ada Hegerberg", 1800, 1800, 0, 0, 0, SlotId: 2, WasPresent: true, PlayerId: "p-ada"),
        new PlayerStats("Guest", 600, 0, 0, 0, 0, SlotId: 3, WasPresent: true, PlayerId: ""),
        new PlayerStats("Absent Kid", 0, 0, 0, 0, 0, SlotId: 4, WasPresent: false, PlayerId: "p-absent"),
    ])
};
sess.Events.Add(Ev(1, "Wayne Rooney", "p-rooney", "Ada Hegerberg", "p-ada"));
sess.Events.Add(Ev(1, "Wayne Rooney", "p-rooney"));
sess.Events.Add(Ev(-1)); // correction drops the second Rooney goal
sess.Events.Add(new GameEvent("them", DateTimeOffset.UtcNow, GameEventType.ScoreThem, "Goal", null,
    new Dictionary<string, object?> { ["delta"] = 1 }));

var built = ClubPlayerMatchBuilder.FromSession(sess, "club-1", "team-1", "U10 Green", 2027);
Check(built.Count == 2, "guest and absent skipped; two linked players present");
var rooney = built.Single(m => m.PlayerId == "p-rooney");
Check(rooney.Goals == 1 && rooney.Assists == 0 && rooney.Minutes == 45 && rooney.SecondsPlayed == 2700 && rooney.Season == 2027, "Rooney: 1 goal after correction, 45:00");
Check(rooney.SessionId == "game-1" && rooney.TeamName == "U10 Green" && rooney.MatchDate == kickoff, "match identity");
var ada = built.Single(m => m.PlayerId == "p-ada");
Check(ada.Goals == 0 && ada.Assists == 1 && ada.Minutes == 30 && ada.SecondsPlayed == 1800 && !ada.Potm, "Ada: 1 assist, 30:00");

// Idempotent rebuild is identical
var built2 = ClubPlayerMatchBuilder.FromSession(sess, "club-1", "team-1", "U10 Green", 2027);
Check(built2.Count == 2 && built2.Single(m => m.PlayerId == "p-rooney").Goals == 1, "re-build of the same session is a no-op (same counts)");

// Name-only goals still attribute to the linked player on this session (ids missing mid-match)
var nameOnly = new GameSession
{
    SessionId = "game-2",
    StartTime = kickoff,
    Summary = new GameSessionSummary(0, 0, [
        new PlayerStats("Wayne Rooney", 600, 0, 0, 0, 0, SlotId: 1, WasPresent: true, PlayerId: "p-rooney"),
    ])
};
nameOnly.Events.Add(Ev(1, "Wayne Rooney", scorerId: null));
var named = ClubPlayerMatchBuilder.FromSession(nameOnly, "club-1", "team-1", "U10 Green", 2027);
Check(named.Count == 1 && named[0].Goals == 1 && named[0].PlayerId == "p-rooney", "fallback to name when scorerPlayerId is missing");

// POTM by slot, and a player with no minutes still counted if they won
var potmIds = ClubPlayerMatchBuilder.PotmPlayerIds(sess, [1]);
Check(potmIds.SetEquals(["p-rooney"]), "POTM slot 1 -> Rooney's playerId");
var withPotm = ClubPlayerMatchBuilder.FromSession(sess, "club-1", "team-1", "U10 Green", 2027, potmIds);
Check(withPotm.Single(m => m.PlayerId == "p-rooney").Potm && !withPotm.Single(m => m.PlayerId == "p-ada").Potm, "POTM flag on the winner only");

// Totals and season filter
var s2026 = new ClubPlayerMatch { SessionId = "a", Season = 2026, Minutes = 40, Goals = 1, Assists = 0, Potm = true };
var s2027a = new ClubPlayerMatch { SessionId = "b", Season = 2027, Minutes = 45, Goals = 1, Assists = 1, Potm = false };
var s2027b = new ClubPlayerMatch { SessionId = "c", Season = 2027, Minutes = 50, Goals = 2, Assists = 0, Potm = true };
var all = ClubPlayerSeasonTotals.From([s2026, s2027a, s2027b]);
Check(all.Matches == 3 && all.Minutes == 135 && all.Goals == 4 && all.Assists == 1 && all.Potm == 2, "all-seasons totals");
var y2027 = ClubPlayerSeasonTotals.From(new[] { s2026, s2027a, s2027b }.Where(m => m.Season == 2027));
Check(y2027.Matches == 2 && y2027.Goals == 3 && y2027.Potm == 1 && y2027.Minutes == 95, "season 2027 filter");

// Field time must keep leftover seconds (85s is 1:25, not truncated to 1 min)
var shortSess = new GameSession
{
    SessionId = "game-short",
    StartTime = kickoff,
    TeamName = "U10 Green",
    Summary = new GameSessionSummary(0, 120, [
        new PlayerStats("Wayne Rooney", 85, 0, 0, 0, 0, SlotId: 1, WasPresent: true, PlayerId: "p-rooney"),
        new PlayerStats("Ada Hegerberg", 40, 0, 0, 0, 0, SlotId: 2, WasPresent: true, PlayerId: "p-ada"),
    ])
};
var shortRows = ClubPlayerMatchBuilder.FromSession(shortSess, "club-1", "team-1", "U10 Green", 2027);
var shortRooney = shortRows.Single(m => m.PlayerId == "p-rooney");
var shortAda = shortRows.Single(m => m.PlayerId == "p-ada");
Check(shortRooney.SecondsPlayed == 85 && shortRooney.Minutes == 1, "85 field seconds stored exactly; minutes kept as truncated 1 for compat");
Check(shortAda.SecondsPlayed == 40 && shortAda.Minutes == 0, "40 field seconds stored exactly; minutes 0");
Check(ClubPlayerFieldTime.Format(shortRooney.EffectiveSeconds) == "1:25", "format 85s as 1:25 like the match report");
Check(ClubPlayerFieldTime.Format(0) == "0:00" && ClubPlayerFieldTime.Format(60) == "1:00", "format 0s and 60s");
var shortTotals = ClubPlayerSeasonTotals.From(shortRows);
Check(shortTotals.SecondsPlayed == 125 && shortTotals.Minutes == 2
      && ClubPlayerFieldTime.Format(shortTotals.SecondsPlayed) == "2:05",
      "totals sum seconds (85+40=125) not truncated per-match minutes (1+0)");

// Legacy row with only minutes still displays as whole minutes
var legacyMinutes = new ClubPlayerMatch { SessionId = "old-min", Season = 2027, Minutes = 45 };
Check(legacyMinutes.SecondsPlayed is null && legacyMinutes.EffectiveSeconds == 2700
      && ClubPlayerFieldTime.Format(legacyMinutes.EffectiveSeconds) == "45:00",
      "pre-secondsPlayed row falls back to minutes x 60");

// No playerId on the summary -> no row (no backfill)
var legacy = new GameSession
{
    SessionId = "old",
    StartTime = kickoff,
    Summary = new GameSessionSummary(0, 0, [new PlayerStats("Old Name", 900, 0, 0, 0, 0, SlotId: 1, WasPresent: true)])
};
legacy.Events.Add(Ev(1, "Old Name"));
Check(ClubPlayerMatchBuilder.FromSession(legacy, "club-1", "team-1", "U10 Green", 2027).Count == 0,
      "legacy session without playerId is not backfilled");


// 11. Invite / recovery code helpers (membership security)
Check(InviteCodes.Normalize(" tqaq-tn2k ") == "TQAQ-TN2K", "normalize invite: upper, trim, keep hyphen");
Check(InviteCodes.Compact("TQAQ-TN2K") == "TQAQTN2K", "compact invite strips hyphen");
Check(InviteCodes.HashAdminCode("abcd-efgh-ijkl-mnop") == InviteCodes.HashAdminCode("ABCD-EFGH-IJKL-MNOP"), "hash is case-insensitive");
Check(InviteCodes.HashAdminCode("TEST-CODE").Length == 64, "hash is 64 hex chars");
Check(InviteCodes.HashAdminCode("TEST-CODE") != InviteCodes.HashAdminCode("TEST-CODF"), "different codes hash differently");
Check(InviteCodes.Normalize("") == "" && InviteCodes.Compact(null) == "", "empty/null codes");

Console.WriteLine(fails == 0 ? "ALL PASS" : $"{fails} FAILED");
return fails;
