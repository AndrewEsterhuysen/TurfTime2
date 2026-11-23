using System.Net;
using System.Text;
using System.Text.Json;
using TurfTime2.Models;

namespace TurfTime2;

/// <summary>One goal in match order, after score corrections have been applied.</summary>
public sealed record MatchGoal(bool IsUs, string? Scorer, string? Assist, string Half, int? ElapsedSeconds, int ScoreUs, int ScoreThem);

/// <summary>Goals and assists for one of our players.</summary>
public sealed record PlayerGoalTally(string PlayerName, int Goals, int Assists);

/// <summary>
/// Builds the "Goals" section of the match report from a session's JSON (local history or cloud sessionJson).
/// Pure (no MAUI APIs) so it can be unit-tested.
/// </summary>
public static class MatchGoalsReport
{
    public sealed record Result(bool Show, bool EnabledFromSession, IReadOnlyList<MatchGoal> Goals, IReadOnlyList<PlayerGoalTally> Players);

    /// <summary>
    /// Show the section when the session says scorer/assist was enabled (<c>ScorerAssistEnabled</c>, saved from
    /// match start), or — for older sessions without that flag, or if the option was turned on mid-match —
    /// when any goal event carries a scorer.
    /// </summary>
    public static Result Build(JsonElement root)
    {
        bool? flag = null;
        if ((root.TryGetProperty("ScorerAssistEnabled", out var f) || root.TryGetProperty("scorerAssistEnabled", out f))
            && f.ValueKind is JsonValueKind.True or JsonValueKind.False)
            flag = f.GetBoolean();

        var goals = new List<MatchGoal>();
        if ((root.TryGetProperty("Events", out var events) || root.TryGetProperty("logs", out events))
            && events.ValueKind == JsonValueKind.Array)
        {
            foreach (var ev in events.EnumerateArray())
            {
                if (!(ev.TryGetProperty("EventType", out var et) || ev.TryGetProperty("eventType", out et))) continue;
                int type = et.ValueKind == JsonValueKind.Number ? et.GetInt32() : -1;
                if (type != (int)GameEventType.ScoreUs && type != (int)GameEventType.ScoreThem) continue;
                bool isUs = type == (int)GameEventType.ScoreUs;

                JsonElement d = default;
                bool hasDetails = (ev.TryGetProperty("Details", out d) || ev.TryGetProperty("details", out d))
                                  && d.ValueKind == JsonValueKind.Object;
                int delta = hasDetails ? Int(d, "delta") ?? 1 : 1;

                if (delta < 0)
                {
                    // Score correction (double-tap): drop the latest goal for that side.
                    var idx = goals.FindLastIndex(g => g.IsUs == isUs);
                    if (idx >= 0) goals.RemoveAt(idx);
                    continue;
                }

                goals.Add(new MatchGoal(
                    isUs,
                    hasDetails ? Str(d, "scorer") : null,
                    hasDetails ? Str(d, "assist") : null,
                    hasDetails ? Str(d, "half") ?? "" : "",
                    hasDetails ? Int(d, "elapsedSeconds") : null,
                    0, 0));
            }
        }

        // Recompute running score after corrections so it always matches the listed goals.
        int us = 0, them = 0;
        for (int i = 0; i < goals.Count; i++)
        {
            if (goals[i].IsUs) us++; else them++;
            goals[i] = goals[i] with { ScoreUs = us, ScoreThem = them };
        }

        bool anyScorer = goals.Any(g => g.Scorer is not null);
        bool show = flag == true || anyScorer;

        var tallies = new Dictionary<string, (string Name, int G, int A)>(StringComparer.OrdinalIgnoreCase);
        int order = 0;
        var firstSeen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void Add(string name, int g, int a)
        {
            var key = name.Trim();
            if (key.Length == 0) return;
            if (!firstSeen.ContainsKey(key)) firstSeen[key] = order++;
            var cur = tallies.TryGetValue(key, out var t) ? t : (key, 0, 0);
            tallies[key] = (cur.Item1, cur.Item2 + g, cur.Item3 + a);
        }
        foreach (var g in goals.Where(g => g.IsUs))
        {
            if (g.Scorer is not null) Add(g.Scorer, 1, 0);
            if (g.Assist is not null) Add(g.Assist, 0, 1);
        }
        var players = tallies
            .OrderByDescending(kv => kv.Value.G).ThenByDescending(kv => kv.Value.A).ThenBy(kv => firstSeen[kv.Key])
            .Select(kv => new PlayerGoalTally(kv.Value.Name, kv.Value.G, kv.Value.A))
            .ToList();

        return new Result(show, flag.HasValue, goals, players);
    }

    /// <summary>"1st 12:34", "2nd 31:05", or just the half / empty when unknown.</summary>
    public static string FormatTime(MatchGoal g)
    {
        var half = g.Half == "HT" ? "HT" : g.Half;
        if (g.ElapsedSeconds is int s && s >= 0)
            return string.IsNullOrEmpty(half) ? $"{s / 60}:{s % 60:D2}" : $"{half} {s / 60}:{s % 60:D2}";
        return half;
    }

    /// <summary>Plain-text line for one goal, e.g. "Sam (assist: Lee)", "Opponent goal".</summary>
    public static string FormatGoal(MatchGoal g)
    {
        if (!g.IsUs) return "Opponent goal";
        var scorer = g.Scorer ?? "Scorer not recorded";
        return g.Assist is null ? scorer : $"{scorer} (assist: {g.Assist})";
    }

    /// <summary>HTML section for the report; empty string when the section should not be shown.</summary>
    public static string RenderHtml(Result r)
    {
        if (!r.Show) return "";
        static string H(string s) => WebUtility.HtmlEncode(s);
        var sb = new StringBuilder();
        sb.AppendLine("    <div class='section'>");
        sb.AppendLine("        <div class='section-title'>Goals</div>");
        if (r.Goals.Count == 0)
        {
            sb.AppendLine("        <p>No goals scored.</p>");
        }
        else
        {
            sb.AppendLine("        <table>");
            sb.AppendLine("            <tr><th>#</th><th>Game Time</th><th>Goal</th><th>Score</th></tr>");
            for (int i = 0; i < r.Goals.Count; i++)
            {
                var g = r.Goals[i];
                var cls = g.IsUs ? " class='score-event'" : "";
                sb.AppendLine($"            <tr{cls}><td>{i + 1}</td><td>{H(FormatTime(g))}</td><td>{H(FormatGoal(g))}</td><td>{g.ScoreUs} – {g.ScoreThem}</td></tr>");
            }
            sb.AppendLine("        </table>");
        }

        if (r.Players.Count > 0)
        {
            sb.AppendLine("        <div class='section-title' style='margin-top:20px;'>Scorers &amp; Assists</div>");
            sb.AppendLine("        <table>");
            sb.AppendLine("            <tr><th>Player</th><th>Goals</th><th>Assists</th></tr>");
            foreach (var p in r.Players)
                sb.AppendLine($"            <tr><td>{H(p.PlayerName)}</td><td>{p.Goals}</td><td>{p.Assists}</td></tr>");
            sb.AppendLine("        </table>");
        }
        sb.AppendLine("    </div>");
        return sb.ToString();
    }

    private static string? Str(JsonElement d, string key)
        => d.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!.Trim() : null;

    private static int? Int(JsonElement d, string key)
    {
        if (!d.TryGetProperty(key, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var j)) return j;
        return null;
    }
}
