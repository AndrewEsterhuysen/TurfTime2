namespace TurfTime2.Models;

/// <summary>
/// One club player's record for one match, stored at
/// <c>clubs/{clubId}/players/{playerId}/matches/{sessionId}</c>.
/// Totals are computed on read; clients never write <see cref="ClubPlayer.Totals"/>.
/// </summary>
public sealed class ClubPlayerMatch
{
    public string SessionId { get; set; } = "";
    public string PlayerId { get; set; } = "";
    public string ClubId { get; set; } = "";
    public string TeamId { get; set; } = "";
    public string TeamName { get; set; } = "";
    public DateTimeOffset MatchDate { get; set; }
    public int Season { get; set; }
    /// <summary>Whole minutes (FieldSeconds / 60, truncated). Kept so older rows and writers stay valid.</summary>
    public int Minutes { get; set; }
    /// <summary>Exact field time in seconds. Null on rows written before this field existed.</summary>
    public int? SecondsPlayed { get; set; }
    public int Goals { get; set; }
    public int Assists { get; set; }
    public bool Potm { get; set; }
    public string PlayerName { get; set; } = "";

    /// <summary>Exact seconds when known; otherwise minutes × 60 for rows written before seconds were stored.</summary>
    public int EffectiveSeconds => SecondsPlayed ?? Math.Max(0, Minutes) * 60;
}

/// <summary>v1 season (or all-seasons) totals computed from <see cref="ClubPlayerMatch"/> rows.</summary>
public sealed class ClubPlayerSeasonTotals
{
    public int Matches { get; set; }
    public int SecondsPlayed { get; set; }
    /// <summary>Whole minutes of <see cref="SecondsPlayed"/> (truncated), for callers that still want minutes.</summary>
    public int Minutes { get; set; }
    public int Goals { get; set; }
    public int Assists { get; set; }
    public int Potm { get; set; }

    public static ClubPlayerSeasonTotals From(IEnumerable<ClubPlayerMatch> matches)
    {
        var t = new ClubPlayerSeasonTotals();
        foreach (var m in matches)
        {
            t.Matches++;
            t.SecondsPlayed += m.EffectiveSeconds;
            t.Goals += m.Goals;
            t.Assists += m.Assists;
            if (m.Potm) t.Potm++;
        }
        t.Minutes = t.SecondsPlayed / 60;
        return t;
    }
}

public static class ClubPlayerFieldTime
{
    /// <summary>Same format as the match report field-time column: m:ss (e.g. 1:25).</summary>
    public static string Format(int seconds)
    {
        var t = Math.Max(0, seconds);
        return $"{t / 60}:{t % 60:D2}";
    }
}
