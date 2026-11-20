using System.Text.Json;
using TurfTime2;
using TurfTime2.Models;
int fails = 0;
void Check(bool c, string m) { Console.WriteLine((c ? "PASS " : "FAIL ") + m); if (!c) fails++; }

string Ev(GameEventType t, int delta, string? scorer = null, string? assist = null, string half = "1st", int el = 60)
{
    var d = new Dictionary<string, object?> { ["team"] = t == GameEventType.ScoreUs ? "Us" : "Them", ["delta"] = delta, ["half"] = half, ["elapsedSeconds"] = el };
    if (scorer != null) d["scorer"] = scorer;
    if (assist != null) d["assist"] = assist;
    return JsonSerializer.Serialize(new GameEvent("x", DateTimeOffset.UtcNow, t, "d", null, d));
}
JsonElement Root(bool? flag, params string[] evs)
{
    var f = flag is null ? "" : $"\"ScorerAssistEnabled\":{(flag.Value ? "true" : "false")},";
    return JsonDocument.Parse("{" + f + "\"Events\":[" + string.Join(",", evs) + "]}").RootElement;
}

// 1. Real model round trip: GameSession serialize -> deserialize -> serialize (cloud path) keeps details
var s = new GameSession { ScorerAssistEnabled = true };
s.Events.Add(JsonSerializer.Deserialize<GameEvent>(Ev(GameEventType.ScoreUs, 1, "Sam", "Lee", "1st", 754))!);
var json2 = JsonSerializer.Serialize(JsonSerializer.Deserialize<GameSession>(JsonSerializer.Serialize(s), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }));
var r0 = MatchGoalsReport.Build(JsonDocument.Parse(json2).RootElement);
Check(r0.Show && r0.EnabledFromSession && r0.Goals.Count == 1 && r0.Goals[0].Scorer == "Sam" && r0.Goals[0].Assist == "Lee", "cloud round-trip keeps scorer/assist + flag");
Check(MatchGoalsReport.FormatTime(r0.Goals[0]) == "1st 12:34", "time format: " + MatchGoalsReport.FormatTime(r0.Goals[0]));

// 2. Full match with opponent goal and correction
var r = MatchGoalsReport.Build(Root(true,
    Ev(GameEventType.ScoreUs, 1, "Sam", "Lee", "1st", 120),
    Ev(GameEventType.ScoreThem, 1, null, null, "1st", 300),
    Ev(GameEventType.ScoreUs, 1, "Lee", null, "1st", 400),
    Ev(GameEventType.ScoreUs, -1, null, null, "1st", 410),       // correction removes Lee's goal
    Ev(GameEventType.ScoreUs, 1, "Sam", "Ava", "2nd", 1500),
    Ev(GameEventType.ScoreUs, 1, "Ava", "Sam", "2nd", 1700)));
Check(r.Goals.Count == 4, "correction removed latest Us goal: count=" + r.Goals.Count);
Check(r.Goals.Select(MatchGoalsReport.FormatGoal).SequenceEqual(new[] { "Sam (assist: Lee)", "Opponent goal", "Sam (assist: Ava)", "Ava (assist: Sam)" }), "goal lines");
Check(r.Goals[^1].ScoreUs == 3 && r.Goals[^1].ScoreThem == 1, "running score recomputed");
Check(string.Join(";", r.Players.Select(p => $"{p.PlayerName}:{p.Goals}/{p.Assists}")) == "Sam:2/1;Ava:1/1;Lee:0/1", "tallies: " + string.Join(";", r.Players.Select(p => $"{p.PlayerName}:{p.Goals}/{p.Assists}")));
var html = MatchGoalsReport.RenderHtml(r);
Check(html.Contains("<div class='section-title'>Goals</div>") && html.Contains("Scorers &amp; Assists") && html.Contains("2nd 25:00"), "html has sections");

// 3. Off: flag false and no scorers -> nothing
var off = MatchGoalsReport.Build(Root(false, Ev(GameEventType.ScoreUs, 1), Ev(GameEventType.ScoreThem, 1)));
Check(!off.Show && MatchGoalsReport.RenderHtml(off) == "", "disabled -> no section");
// 4. Legacy (no flag): inferred from scorer data
Check(MatchGoalsReport.Build(Root(null, Ev(GameEventType.ScoreUs, 1, "Sam"))).Show, "legacy with scorer -> shown");
Check(!MatchGoalsReport.Build(Root(null, Ev(GameEventType.ScoreUs, 1))).Show, "legacy without scorer -> hidden");
Check(!MatchGoalsReport.Build(Root(null)).Show, "legacy no events -> hidden");
// 5. Enabled, 0-0
var nil = MatchGoalsReport.Build(Root(true));
Check(nil.Show && MatchGoalsReport.RenderHtml(nil).Contains("No goals scored."), "enabled 0-0 -> 'No goals scored.'");
// 6. HTML escaping
Check(MatchGoalsReport.RenderHtml(MatchGoalsReport.Build(Root(true, Ev(GameEventType.ScoreUs, 1, "<b>Jo</b>")))).Contains("&lt;b&gt;Jo&lt;/b&gt;"), "names html-escaped");
// 7. camelCase keys
Check(MatchGoalsReport.Build(JsonDocument.Parse("{\"scorerAssistEnabled\":true,\"logs\":[{\"eventType\":16,\"details\":{\"delta\":1,\"scorer\":\"Kim\"}}]}").RootElement).Players[0].PlayerName == "Kim", "camelCase keys");
Console.WriteLine(fails == 0 ? "ALL PASS" : $"{fails} FAILED");
return fails;
