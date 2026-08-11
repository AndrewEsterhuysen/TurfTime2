using TurfTime2.Models;

namespace TurfTime2.Helpers;

/// <summary>
/// Pure helpers for schedule completeness, past/upcoming, and status labels.
/// </summary>
public static class MatchScheduleEvaluator
{
    public static bool HasAnyContent(MatchSchedule? s)
    {
        if (s is null) return false;
        return !string.IsNullOrWhiteSpace(s.MatchDate)
            || !string.IsNullOrWhiteSpace(s.MatchTime)
            || !string.IsNullOrWhiteSpace(s.ArriveTime)
            || !string.IsNullOrWhiteSpace(s.LocationName)
            || !string.IsNullOrWhiteSpace(s.Latitude)
            || !string.IsNullOrWhiteSpace(s.MapsLink);
    }

    public static bool IsComplete(MatchSchedule? s)
    {
        if (s is null) return false;
        return !string.IsNullOrWhiteSpace(s.MatchDate)
            && !string.IsNullOrWhiteSpace(s.MatchTime);
    }

    /// <summary>
    /// Preferred “be at ground” instant: match date + arrive time, else match time.
    /// Returns null if date/time cannot be parsed.
    /// </summary>
    public static DateTime? GetEffectiveArrivalLocal(MatchSchedule? s)
    {
        if (s is null || string.IsNullOrWhiteSpace(s.MatchDate))
            return null;

        if (!DateTime.TryParse(s.MatchDate, out var date))
            return null;

        var timeStr = !string.IsNullOrWhiteSpace(s.ArriveTime) ? s.ArriveTime : s.MatchTime;
        if (string.IsNullOrWhiteSpace(timeStr) || !TimeSpan.TryParse(timeStr, out var time))
            return null;

        return date.Date + time;
    }

    public static DateTime? GetKickoffLocal(MatchSchedule? s)
    {
        if (s is null || string.IsNullOrWhiteSpace(s.MatchDate) || string.IsNullOrWhiteSpace(s.MatchTime))
            return null;
        if (!DateTime.TryParse(s.MatchDate, out var date)) return null;
        if (!TimeSpan.TryParse(s.MatchTime, out var time)) return null;
        return date.Date + time;
    }

    public static MatchScheduleStatus Evaluate(MatchSchedule? s, DateTime? nowLocal = null)
    {
        if (s is null || !HasAnyContent(s))
            return MatchScheduleStatus.NotSet;

        if (!IsComplete(s))
            return MatchScheduleStatus.Incomplete;

        var when = GetEffectiveArrivalLocal(s) ?? GetKickoffLocal(s);
        if (when is null)
            return MatchScheduleStatus.Incomplete;

        var now = nowLocal ?? DateTime.Now;
        return when.Value < now ? MatchScheduleStatus.Past : MatchScheduleStatus.Upcoming;
    }

    public static string StatusLabel(MatchScheduleStatus status) => status switch
    {
        MatchScheduleStatus.NotSet => "No match scheduled",
        MatchScheduleStatus.Incomplete => "Incomplete — set match date and time",
        MatchScheduleStatus.Upcoming => "Upcoming",
        MatchScheduleStatus.Past => "Past match — schedule is outdated",
        _ => string.Empty
    };

    public static string FormatLastUpdated(MatchSchedule? s)
    {
        if (s is null || s.LastModifiedUtc == default)
            return string.Empty;

        var local = s.LastModifiedUtc.ToLocalTime();
        var who = string.IsNullOrWhiteSpace(s.UpdatedByDisplayName)
            ? null
            : s.UpdatedByDisplayName.Trim();

        var stamp = local.ToString("d MMM yyyy, h:mm tt");
        return who is null ? $"Updated {stamp}" : $"Updated {stamp} · by {who}";
    }

    public static string FormatSourceLine(MatchSchedule? s, bool isSharedTeam)
    {
        if (!isSharedTeam)
            return "On this device only";

        if (s is null)
            return "Team schedule · not loaded yet";

        if (s.IsOfflineCache)
            return "Last known · offline (could not refresh)";

        if (s.FromCloud)
            return "Team schedule (shared)";

        return "Team schedule · local cache";
    }
}
