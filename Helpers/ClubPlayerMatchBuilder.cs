using TurfTime2.Models;

namespace TurfTime2.Helpers;

/// <summary>
/// Builds per-player match rows from a saved <see cref="GameSession"/> (idempotent
/// by session id). Pure — no I/O — so it can be unit-tested.
/// </summary>
public static class ClubPlayerMatchBuilder
{
    /// <summary>
    /// Club players who were present, or who scored or assisted, or who won POTM.
    /// Players without a <see cref="PlayerStats.PlayerId"/> are skipped (no backfill).
    /// </summary>
    public static List<ClubPlayerMatch> FromSession(
        GameSession session,
        string clubId,
        string teamId,
        string? teamName,
        int season,
        IReadOnlySet<string>? potmPlayerIds = null)
    {
        if (session is null || string.IsNullOrWhiteSpace(clubId) || string.IsNullOrWhiteSpace(teamId))
            return [];

        var goalsById = new Dictionary<string, (int G, int A)>(StringComparer.Ordinal);
        var goalsByName = new Dictionary<string, (int G, int A)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (scorerId, scorerName, assistId, assistName) in OurGoals(session))
        {
            Add(goalsById, scorerId, 1, 0);
            Add(goalsByName, scorerName, 1, 0);
            Add(goalsById, assistId, 0, 1);
            Add(goalsByName, assistName, 0, 1);
        }

        var date = session.StartTime;
        var name = string.IsNullOrWhiteSpace(teamName) ? (session.TeamName ?? "") : teamName;
        var seasonYear = season >= 2000 ? season : date.Year;
        var potm = potmPlayerIds ?? new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<ClubPlayerMatch>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var p in session.Summary?.PlayerStats ?? [])
        {
            if (string.IsNullOrWhiteSpace(p.PlayerId)) continue;
            var (g, a) = TallyFor(p, goalsById, goalsByName);
            var isPotm = potm.Contains(p.PlayerId);
            var present = p.WasPresent != false && (p.WasPresent == true || p.FieldSeconds > 0 || g > 0 || a > 0);
            if (!present && g == 0 && a == 0 && !isPotm) continue;
            seen.Add(p.PlayerId);
            rows.Add(Row(session, clubId, teamId, name, seasonYear, date, p.PlayerId, p.PlayerName, p.FieldSeconds, g, a, isPotm));
        }

        // A linked scorer who somehow missed the summary still gets a row.
        foreach (var id in goalsById.Keys)
        {
            if (string.IsNullOrWhiteSpace(id) || !seen.Add(id)) continue;
            var (g, a) = goalsById[id];
            rows.Add(Row(session, clubId, teamId, name, seasonYear, date, id, "", 0, g, a, potm.Contains(id)));
        }

        return rows;
    }

    public static IReadOnlySet<string> PotmPlayerIds(GameSession session, IEnumerable<int> winnerSlotIds)
    {
        var slots = winnerSlotIds.ToHashSet();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (slots.Count == 0) return ids;
        foreach (var p in session.Summary?.PlayerStats ?? [])
        {
            if (slots.Contains(p.SlotId) && !string.IsNullOrWhiteSpace(p.PlayerId))
                ids.Add(p.PlayerId);
        }
        return ids;
    }

    private static ClubPlayerMatch Row(
        GameSession session, string clubId, string teamId, string teamName, int season,
        DateTimeOffset date, string playerId, string playerName, int fieldSeconds,
        int goals, int assists, bool potm) => new()
    {
        SessionId = session.SessionId,
        PlayerId = playerId,
        ClubId = clubId,
        TeamId = teamId,
        TeamName = teamName ?? "",
        MatchDate = date,
        Season = season,
        Minutes = fieldSeconds > 0 ? fieldSeconds / 60 : 0,
        Goals = goals,
        Assists = assists,
        Potm = potm,
        PlayerName = playerName ?? ""
    };

    private static (int G, int A) TallyFor(
        PlayerStats p,
        Dictionary<string, (int G, int A)> byId,
        Dictionary<string, (int G, int A)> byName)
    {
        if (byId.TryGetValue(p.PlayerId, out var t)) return t;
        if (!string.IsNullOrWhiteSpace(p.PlayerName) && byName.TryGetValue(p.PlayerName, out t))
            return t;
        return (0, 0);
    }

    private static void Add(Dictionary<string, (int G, int A)> map, string? key, int g, int a)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        var cur = map.TryGetValue(key, out var t) ? t : (0, 0);
        map[key] = (cur.Item1 + g, cur.Item2 + a);
    }

    /// <summary>Our goals after score corrections, same rule as <c>MatchGoalsReport</c>.</summary>
    private static List<(string? ScorerId, string? ScorerName, string? AssistId, string? AssistName)> OurGoals(GameSession session)
    {
        var us = new List<(string? Id, string? Name, string? AId, string? AName)>();
        var them = 0;
        foreach (var e in session.Events)
        {
            if (e.EventType != GameEventType.ScoreUs && e.EventType != GameEventType.ScoreThem)
                continue;
            var delta = Int(e, "delta") ?? 1;
            var isUs = e.EventType == GameEventType.ScoreUs;
            if (delta < 0)
            {
                if (isUs)
                {
                    if (us.Count > 0) us.RemoveAt(us.Count - 1);
                }
                else if (them > 0) them--;
                continue;
            }
            if (isUs)
                us.Add((Str(e, "scorerPlayerId"), Str(e, "scorer"), Str(e, "assistPlayerId"), Str(e, "assist")));
            else
                them++;
        }
        return us;
    }

    private static string? Str(GameEvent e, string key)
    {
        if (!e.Details.TryGetValue(key, out var v) || v is null) return null;
        var s = v switch
        {
            string text => text,
            System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.String => je.GetString(),
            _ => v.ToString()
        };
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private static int? Int(GameEvent e, string key)
    {
        if (!e.Details.TryGetValue(key, out var v) || v is null) return null;
        switch (v)
        {
            case int i: return i;
            case long l: return (int)l;
            case string s when int.TryParse(s, out var n): return n;
            case System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.Number && je.TryGetInt32(out var n):
                return n;
            case System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.String && int.TryParse(je.GetString(), out var n2):
                return n2;
            default: return null;
        }
    }
}
