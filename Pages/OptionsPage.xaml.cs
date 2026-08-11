using Microsoft.Extensions.DependencyInjection;
using TurfTime2.Helpers;
using TurfTime2.Services;

namespace TurfTime2;

public partial class OptionsPage : ContentPage
{
    private bool _loading;
    private IMatchReminderService? _reminders;

    private static readonly int[] LeaveBuffers = [30, 45, 60, 90];

    public OptionsPage()
    {
        InitializeComponent();
        LeaveBufferPicker.ItemsSource = LeaveBuffers.Select(m => $"{m} minutes").ToList();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        DetailsPage.ApplyPageTeamTitle(this, "Options");

        _loading = true;
        try
        {
            var enabled = GoalScoringOptions.IsScorerAssistEnabled();
            EnableGoalDetailsSwitch.IsToggled = enabled;
            ToggleStateLabel.Text = enabled ? "ON" : "OFF";

            RemindersEnabledSwitch.IsToggled = MatchReminderOptions.IsEnabled;
            RemindersMasterLabel.Text = MatchReminderOptions.IsEnabled ? "ON" : "OFF";
            ReminderDetailsPanel.IsVisible = MatchReminderOptions.IsEnabled;

            DayBeforeSwitch.IsToggled = MatchReminderOptions.DayBefore;
            MorningSwitch.IsToggled = MatchReminderOptions.Morning;
            LeaveSwitch.IsToggled = MatchReminderOptions.Leave;

            var buf = MatchReminderOptions.LeaveBufferMinutes;
            var idx = Array.IndexOf(LeaveBuffers, buf);
            LeaveBufferPicker.SelectedIndex = idx >= 0 ? idx : 1;

            _reminders = GetService<IMatchReminderService>();
            if (_reminders is not null && MatchReminderOptions.IsEnabled)
                await _reminders.RescheduleForCurrentTeamAsync();

            RefreshReminderStatus();
        }
        finally
        {
            _loading = false;
        }
    }

    private void OnEnableGoalDetailsToggled(object sender, ToggledEventArgs e)
    {
        if (_loading) return;
        GoalScoringOptions.SetScorerAssistEnabled(e.Value);
        ToggleStateLabel.Text = e.Value ? "ON" : "OFF";
    }

    private async void OnRemindersEnabledToggled(object sender, ToggledEventArgs e)
    {
        if (_loading) return;

        MatchReminderOptions.SetEnabled(e.Value);
        RemindersMasterLabel.Text = e.Value ? "ON" : "OFF";
        ReminderDetailsPanel.IsVisible = e.Value;

        if (e.Value)
        {
            // Reflect one-click defaults on the detail switches
            _loading = true;
            DayBeforeSwitch.IsToggled = MatchReminderOptions.DayBefore;
            MorningSwitch.IsToggled = MatchReminderOptions.Morning;
            LeaveSwitch.IsToggled = MatchReminderOptions.Leave;
            _loading = false;

            _reminders ??= GetService<IMatchReminderService>();
            if (_reminders is not null)
            {
                var ok = await _reminders.EnsurePermissionAsync();
                if (!ok)
                {
                    RemindersStatusLabel.Text = "Allow notifications in system Settings to receive reminders.";
                }
                await _reminders.RescheduleForCurrentTeamAsync();
            }
        }
        else
        {
            _reminders ??= GetService<IMatchReminderService>();
            _reminders?.CancelAll();
            if (_reminders is not null)
                await _reminders.RescheduleForCurrentTeamAsync();
        }

        RefreshReminderStatus();
    }

    private async void OnReminderDetailToggled(object sender, ToggledEventArgs e)
    {
        if (_loading) return;

        MatchReminderOptions.DayBefore = DayBeforeSwitch.IsToggled;
        MatchReminderOptions.Morning = MorningSwitch.IsToggled;
        MatchReminderOptions.Leave = LeaveSwitch.IsToggled;

        // Keep master consistent
        if (!DayBeforeSwitch.IsToggled && !MorningSwitch.IsToggled && !LeaveSwitch.IsToggled
            && MatchReminderOptions.IsEnabled)
        {
            // Leave master on but nothing scheduled — user choice
        }

        _reminders ??= GetService<IMatchReminderService>();
        if (_reminders is not null)
            await _reminders.RescheduleForCurrentTeamAsync();
        RefreshReminderStatus();
    }

    private async void OnLeaveBufferChanged(object? sender, EventArgs e)
    {
        if (_loading) return;
        if (LeaveBufferPicker.SelectedIndex < 0 || LeaveBufferPicker.SelectedIndex >= LeaveBuffers.Length)
            return;

        MatchReminderOptions.LeaveBufferMinutes = LeaveBuffers[LeaveBufferPicker.SelectedIndex];
        _reminders ??= GetService<IMatchReminderService>();
        if (_reminders is not null)
            await _reminders.RescheduleForCurrentTeamAsync();
        RefreshReminderStatus();
    }

    private void RefreshReminderStatus()
    {
        _reminders ??= GetService<IMatchReminderService>();
        RemindersStatusLabel.Text = _reminders?.GetStatusSummary()
            ?? (MatchReminderOptions.IsEnabled ? "Reminders enabled" : "Reminders off");
    }

    private static T? GetService<T>() where T : class
    {
        var services = Application.Current?.Handler?.MauiContext?.Services
            ?? Shell.Current?.Handler?.MauiContext?.Services;
        return services?.GetService<T>();
    }
}
