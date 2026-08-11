using Plugin.LocalNotification;
using Plugin.LocalNotification.Core.Models;
using TurfTime2.Helpers;
using TurfTime2.Models;

namespace TurfTime2.Services;

/// <summary>
/// Schedules local notifications for match day using Plugin.LocalNotification.
/// </summary>
public sealed class MatchReminderService : IMatchReminderService
{
    public const string ChannelId = "turftime_match_reminders";

    private readonly IMatchScheduleService _schedule;
    private string _lastStatus = "Reminders off";

    public MatchReminderService(IMatchScheduleService schedule)
    {
        _schedule = schedule;
    }

    public async Task<bool> EnsurePermissionAsync()
    {
        try
        {
#if ANDROID || IOS
            var enabled = await LocalNotificationCenter.Current.AreNotificationsEnabled();
            if (enabled)
            {
                ReclaimIosNotificationDelegate();
                return true;
            }

            var granted = await LocalNotificationCenter.Current.RequestNotificationPermission();
            // Plugin.LocalNotification installs its own UNUserNotificationCenter.Delegate on iOS,
            // which would swallow FCM chat banners — reclaim ours after permission APIs.
            ReclaimIosNotificationDelegate();
            return granted;
#else
            await Task.CompletedTask;
            return false;
#endif
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MatchReminder] Permission: {ex.Message}");
            ReclaimIosNotificationDelegate();
            return false;
        }
    }

    public async Task RescheduleForCurrentTeamAsync()
    {
        try
        {
            var teamId = Preferences.Get("team_id", string.Empty);
            var previousTeam = MatchReminderOptions.LastScheduledTeamId;

            if (!string.IsNullOrEmpty(previousTeam) &&
                !string.Equals(previousTeam, teamId, StringComparison.Ordinal))
            {
                CancelForTeam(previousTeam);
            }

            if (string.IsNullOrWhiteSpace(teamId))
            {
                CancelAll();
                _lastStatus = "No team selected";
                MatchReminderOptions.LastScheduledTeamId = string.Empty;
                return;
            }

            if (!MatchReminderOptions.IsEnabled)
            {
                CancelForTeam(teamId);
                _lastStatus = "Reminders off";
                MatchReminderOptions.LastScheduledTeamId = teamId;
                return;
            }

            var allowed = await EnsurePermissionAsync().ConfigureAwait(false);
            if (!allowed)
            {
                CancelForTeam(teamId);
                _lastStatus = "Notifications not allowed — enable in system Settings";
                MatchReminderOptions.LastScheduledTeamId = teamId;
                return;
            }

            // Prefer LastKnown from sync host, else local prefs.
            MatchSchedule? schedule = MatchScheduleSyncHost.LastKnown;
            if (schedule is null || !string.Equals(schedule.TeamId, teamId, StringComparison.Ordinal))
                schedule = _schedule.LoadLocal(teamId);

            // Cancel then re-add for this team
            CancelForTeam(teamId);

            var plan = MatchReminderPlanner.Plan(schedule);
            if (plan.Count == 0)
            {
                var status = MatchScheduleEvaluator.Evaluate(schedule);
                _lastStatus = status switch
                {
                    MatchScheduleStatus.NotSet => "No match scheduled",
                    MatchScheduleStatus.Incomplete => "Schedule incomplete — set date & time",
                    MatchScheduleStatus.Past => "Match is in the past — no reminders",
                    _ => "No future reminder times"
                };
                MatchReminderOptions.LastScheduledTeamId = teamId;
                return;
            }

            var scheduled = 0;
            foreach (var item in plan)
            {
                var id = MatchReminderPlanner.NotificationId(teamId, item.Kind);
                var request = new NotificationRequest
                {
                    NotificationId = id,
                    Title = item.Title,
                    Description = item.Body,
                    CategoryType = NotificationCategoryType.Reminder,
                    Schedule = new NotificationRequestSchedule
                    {
                        NotifyTime = item.FireLocal
                    },
                    Android =
                    {
                        ChannelId = ChannelId,
                        Priority = Plugin.LocalNotification.Core.Models.AndroidOption.AndroidPriority.High
                    }
                };

#if ANDROID || IOS
                await LocalNotificationCenter.Current.Show(request).ConfigureAwait(false);
                scheduled++;
                System.Diagnostics.Debug.WriteLine(
                    $"[MatchReminder] Scheduled {item.Kind} id={id} at {item.FireLocal:g}");
#else
                await Task.CompletedTask;
#endif
            }

            ReclaimIosNotificationDelegate();

            MatchReminderOptions.LastScheduledTeamId = teamId;
            _lastStatus = scheduled == 1
                ? $"1 reminder scheduled"
                : $"{scheduled} reminders scheduled";

            // Append next fire for confidence
            var next = plan.OrderBy(p => p.FireLocal).First();
            _lastStatus += $" · next {next.FireLocal:ddd d MMM h:mm tt}";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MatchReminder] Reschedule: {ex.Message}");
            _lastStatus = "Could not schedule reminders";
            ReclaimIosNotificationDelegate();
        }
    }

    /// <summary>
    /// Plugin.LocalNotification's UseLocalNotification / permission APIs replace the iOS
    /// UNUserNotificationCenter.Delegate. Chat FCM needs our delegate for foreground banners.
    /// </summary>
    private static void ReclaimIosNotificationDelegate()
    {
#if IOS
        try
        {
            FcmService.InstallIosNotificationDelegate();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MatchReminder] Reclaim iOS delegate: {ex.Message}");
        }
#endif
    }

    public void CancelForTeam(string teamId)
    {
        if (string.IsNullOrWhiteSpace(teamId)) return;
        try
        {
            var ids = new[]
            {
                MatchReminderPlanner.NotificationId(teamId, MatchReminderKind.DayBefore),
                MatchReminderPlanner.NotificationId(teamId, MatchReminderKind.Morning),
                MatchReminderPlanner.NotificationId(teamId, MatchReminderKind.Leave)
            };
#if ANDROID || IOS
            LocalNotificationCenter.Current.Cancel(ids);
#endif
            ReclaimIosNotificationDelegate();
            System.Diagnostics.Debug.WriteLine($"[MatchReminder] Cancelled team={teamId}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MatchReminder] Cancel: {ex.Message}");
            ReclaimIosNotificationDelegate();
        }
    }

    public void CancelAll()
    {
        var team = MatchReminderOptions.LastScheduledTeamId;
        if (!string.IsNullOrEmpty(team))
            CancelForTeam(team);
        var current = Preferences.Get("team_id", string.Empty);
        if (!string.IsNullOrEmpty(current) && !string.Equals(current, team, StringComparison.Ordinal))
            CancelForTeam(current);
    }

    public string GetStatusSummary() => _lastStatus;
}
