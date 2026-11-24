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
    public int Minutes { get; set; }
    public int Goals { get; set; }
    public int Assists { get; set; }
    public bool Potm { get; set; }
    public string PlayerName { get; set; } = "";
}

/// <summary>v1 season (or all-seasons) totals computed from <see cref="ClubPlayerMatch"/> rows.</summary>
public sealed class ClubPlayerSeasonTotals
{
    public int Matches { get; set; }
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
            t.Minutes += m.Minutes;
            t.Goals += m.Goals;
            t.Assists += m.Assists;
            if (m.Potm) t.Potm++;
        }
        return t;
    }
}
