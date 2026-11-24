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

Console.WriteLine(fails == 0 ? "ALL PASS" : $"{fails} FAILED");
return fails;
