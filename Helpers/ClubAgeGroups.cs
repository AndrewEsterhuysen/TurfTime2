using System.Globalization;

namespace TurfTime2.Helpers;

/// <summary>
/// Age groups with the club's 1 January cutoff (docs/club-players-spec.md §5, decision 1).
/// A player's age group is based on their age on 1 January of the season year:
/// 9 on 1 Jan 2027 → U10 for season 2027, even if they turn 10 on 2 January.
/// Players may play in their own age group or older (play up), never younger.
/// Pure (no MAUI types) so it can be unit-tested.
/// </summary>
public static class ClubAgeGroups
{
    public const string Open = "Open";

    /// <summary>Parse an ISO <c>YYYY-MM-DD</c> date of birth.</summary>
    public static bool TryParseDateOfBirth(string? iso, out DateOnly dob) =>
        DateOnly.TryParseExact(iso?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out dob);

    public static string ToIso(DateOnly dob) => dob.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Age in whole years on 1 January of <paramref name="season"/>.</summary>
    public static int AgeOn1Jan(DateOnly dob, int season)
    {
        var age = season - dob.Year;
        // Birthday not reached by 1 Jan unless born on 1 Jan.
        if (dob.Month != 1 || dob.Day != 1) age--;
        return age;
    }

    /// <summary>The player's own age group for a season, e.g. "U10". Age on 1 Jan N → U(N+1).</summary>
    public static string AgeGroupFor(DateOnly dob, int season) => $"U{AgeOn1Jan(dob, season) + 1}";

    /// <summary>"U10" → 9 (oldest eligible age on 1 Jan). Open → null (no limit). Unknown → null.</summary>
    public static int? MaxAgeOn1Jan(string? ageGroup)
    {
        var g = ageGroup?.Trim() ?? "";
        if (g.Length < 2 || (g[0] != 'U' && g[0] != 'u')) return null;
        return int.TryParse(g[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0
            ? n - 1
            : null;
    }

    /// <summary>
    /// True when the player may play for a team of <paramref name="ageGroup"/> in <paramref name="season"/>:
    /// own age group or older (play up), never younger. Open teams accept everyone.
    /// </summary>
    public static bool IsEligible(DateOnly dob, string? ageGroup, int season)
    {
        if (string.Equals(ageGroup?.Trim(), Open, StringComparison.OrdinalIgnoreCase)) return true;
        var max = MaxAgeOn1Jan(ageGroup);
        return max is null || AgeOn1Jan(dob, season) <= max.Value;
    }

    /// <summary>Default season: the current calendar year (local time).</summary>
    public static int CurrentSeason() => DateTime.Now.Year;
}
