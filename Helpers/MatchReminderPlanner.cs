using TurfTime2.Models;

namespace TurfTime2.Helpers;

public enum MatchReminderKind
{
    DayBefore = 1,
    Morning = 2,
    Leave = 3
}

public sealed record PlannedReminder(
    MatchReminderKind Kind,
    DateTime FireLocal,
    string Title,
    string Body);

/// <summary>
/// Pure planner: which local notifications to schedule from schedule + options.
/// </summary>
public static class MatchReminderPlanner
{
    public static IReadOnlyList<PlannedReminder> Plan(
        MatchSchedule? schedule,
        DateTime? nowLocal = null)
    {
        var list = new List<PlannedReminder>();
        if (!MatchReminderOptions.IsEnabled)
            return list;

        if (MatchScheduleEvaluator.Evaluate(schedule) != MatchScheduleStatus.Upcoming)
            return list;

        var kickoff = MatchScheduleEvaluator.GetKickoffLocal(schedule);
        var arrive = MatchScheduleEvaluator.GetEffectiveArrivalLocal(schedule) ?? kickoff;
        if (kickoff is null || arrive is null || schedule is null)
            return list;

        var now = nowLocal ?? DateTime.Now;
        var venue = string.IsNullOrWhiteSpace(schedule.LocationName)
            ? "the ground"
            : schedule.LocationName.Trim();
        var kickoffText = kickoff.Value.ToString("ddd d MMM · h:mm tt");
        var arriveText = arrive.Value.ToString("h:mm tt");

        if (MatchReminderOptions.DayBefore)
        {
            var fire = kickoff.Value.Date.AddDays(-1)
                .AddHours(MatchReminderOptions.DayBeforeHour);
            if (fire > now)
            {
                list.Add(new PlannedReminder(
                    MatchReminderKind.DayBefore,
                    fire,
                    "Match tomorrow",
                    $"Kickoff {kickoffText} · arrive {arriveText} at {venue}"));
            }
        }

        if (MatchReminderOptions.Morning)
        {
            var fire = kickoff.Value.Date.AddHours(MatchReminderOptions.MorningHour);
            if (fire > now)
            {
                list.Add(new PlannedReminder(
                    MatchReminderKind.Morning,
                    fire,
                    "Match day",
                    $"Today · kickoff {kickoff.Value:h:mm tt} · arrive {arriveText} at {venue}"));
            }
        }

        if (MatchReminderOptions.Leave)
        {
            var fire = arrive.Value.AddMinutes(-MatchReminderOptions.LeaveBufferMinutes);
            if (fire > now)
            {
                list.Add(new PlannedReminder(
                    MatchReminderKind.Leave,
                    fire,
                    "Time to leave",
                    $"Leave now · arrive by {arriveText} at {venue} ({MatchReminderOptions.LeaveBufferMinutes} min buffer)"));
            }
        }

        return list;
    }

    /// <summary>Stable notification ids per team + kind (positive int).</summary>
    public static int NotificationId(string teamId, MatchReminderKind kind)
    {
        unchecked
        {
            var h = teamId?.GetHashCode() ?? 0;
            // Keep in a dedicated positive range away from chat local ids.
            var baseId = 700_000 + (Math.Abs(h) % 90_000);
            return baseId * 10 + (int)kind;
        }
    }
}
