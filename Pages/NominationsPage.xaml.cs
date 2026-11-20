using TurfTime2.Models;
using TurfTime2.Services;

namespace TurfTime2;

/// <summary>
/// Player of the match (docs/nominations-spec.md §4). Shared iOS/Android page.
/// Opened from Details → Nominations, the End-game prompt, or a Reports session
/// (optional query param <c>sessionId</c>). Depends on <see cref="INominationService"/>
/// and <see cref="ISessionStorageService"/> only — no Firebase types here.
/// </summary>
[QueryProperty(nameof(SessionId), "sessionId")]
public partial class NominationsPage : ContentPage
{
    private const string PageLabel = "Nominations";

    private INominationService? _nominations;
    private ISessionStorageService? _sessions;
    private IDisposable? _tallyWatch;
    private List<SessionSummary> _matches = [];
    private NominationBallot? _ballot;
    private NominationTally? _tally;
    private string _teamId = "";
    private string _loadedSessionId = "";
    private bool _submitting;
    private bool _suppressPicker;
    private int _loadVersion;

    public NominationsPage()
    {
        InitializeComponent();
    }

    /// <summary>Optional session to open directly (from End game or Reports).</summary>
    public string SessionId { get; set; } = "";

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        DetailsPage.ApplyPageTeamTitle(this, PageLabel);
        _nominations ??= GetService<INominationService>();
        _sessions ??= GetService<ISessionStorageService>();

        try
        {
            await LoadMatchesAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[NominationsPage] Load: {ex.Message}");
            HideAll();
            ShowNotice("Couldn't load matches. Check your connection and try again.", retry: true);
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        StopWatch();
        _loadedSessionId = "";
    }

    // ── Matches ───────────────────────────────────────────────────────────

    private static bool IsLocalTeam(string teamId) =>
        string.IsNullOrWhiteSpace(teamId)
        || teamId.StartsWith("local_", StringComparison.Ordinal)
        || string.Equals(Preferences.Get("team_mode", string.Empty), "local", StringComparison.Ordinal);

    private static bool IsAdmin() =>
        !string.Equals(Preferences.Get("user_role", "admin"), "member", StringComparison.OrdinalIgnoreCase);

    private async Task LoadMatchesAsync()
    {
        _teamId = Preferences.Get("team_id", string.Empty);
        if (IsLocalTeam(_teamId))
        {
            HideAll();
            ShowNotice("Player of the match is available on shared teams only.");
            return;
        }

        if (_nominations is null || _sessions is null)
        {
            HideAll();
            ShowNotice("Nominations are unavailable right now.");
            return;
        }

        NoticeCard.IsVisible = false;
        WindowLabel.Text = "Loading matches...";
        MatchCard.IsVisible = true;

        // Cloud sessions (SDK, then REST list) merged with this team's games in local
        // history, so a game that is on the phone but not yet listed from the cloud shows.
        var result = await _sessions.LoadCompletedSessionsAsync(_teamId, Preferences.Get("team_name", string.Empty));
        _matches = result.Sessions.ToList();
        if (result.CloudFailed)
            System.Diagnostics.Debug.WriteLine($"[NominationsPage] Cloud list failed: {result.CloudError}");

        // Deep link to a match that isn't in the list yet (e.g. just archived).
        if (!string.IsNullOrWhiteSpace(SessionId) && _matches.All(m => m.SessionId != SessionId))
            _matches.Insert(0, new SessionSummary { SessionId = SessionId, StartTime = DateTime.UtcNow });

        if (_matches.Count == 0)
        {
            HideAll();
            if (result.CloudFailed)
                ShowNotice("Couldn't load matches. Check your connection and try again.", retry: true);
            else
                ShowNotice("No completed matches yet. Finish a game to nominate a player of the match.");
            return;
        }

        _suppressPicker = true;
        SessionPicker.Items.Clear();
        foreach (var m in _matches)
            SessionPicker.Items.Add(FormatMatch(m));
        var index = Math.Max(0, _matches.FindIndex(m => m.SessionId == SessionId));
        SessionPicker.SelectedIndex = index;
        _suppressPicker = false;

        await OpenSessionAsync(_matches[index].SessionId);
    }

    private static string FormatMatch(SessionSummary s)
    {
        var start = s.StartTime.Kind == DateTimeKind.Utc ? s.StartTime.ToLocalTime() : s.StartTime;
        var text = $"{start:ddd d MMM, h:mm tt}";
        if (!string.IsNullOrWhiteSpace(s.Location)) text += $" · {s.Location}";
        return text;
    }

    private async void OnSessionSelected(object? sender, EventArgs e)
    {
        if (_suppressPicker) return;
        var i = SessionPicker.SelectedIndex;
        if (i < 0 || i >= _matches.Count) return;
        try { await OpenSessionAsync(_matches[i].SessionId); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[NominationsPage] Select: {ex.Message}"); }
    }

    private async Task OpenSessionAsync(string sessionId)
    {
        if (_nominations is null || string.IsNullOrWhiteSpace(sessionId)) return;
        if (sessionId == _loadedSessionId && _tallyWatch is not null) return;

        var version = ++_loadVersion;
        StopWatch();
        _loadedSessionId = sessionId;
        _ballot = null;
        _tally = null;
        SubmitStatusLabel.IsVisible = false;
        WindowLabel.Text = "Loading...";
        NominateCard.IsVisible = false;
        TallyCard.IsVisible = false;
        CloseVotingButton.IsVisible = false;

        var ballot = await _nominations.LoadBallotAsync(_teamId, sessionId);
        if (version != _loadVersion) return;
        if (!ballot.SessionFound)
        {
            WindowLabel.Text = "This match isn't available yet. If it just finished, try again in a moment.";
            return;
        }
        _ballot = ballot;
        Render();

        var watch = await _nominations.WatchTallyAsync(_teamId, sessionId,
            tally => MainThread.BeginInvokeOnMainThread(() =>
            {
                if (version != _loadVersion) return;
                _tally = tally;
                Render();
            }),
            ex => System.Diagnostics.Debug.WriteLine($"[NominationsPage] Tally: {ex.Message}"));

        if (version != _loadVersion) { watch?.Dispose(); return; }
        _tallyWatch = watch;
        if (watch is null)
            WindowLabel.Text = "Couldn't connect to the live tally. Check your connection.";
    }

    private void StopWatch()
    {
        try { _tallyWatch?.Dispose(); } catch { /* ignore */ }
        _tallyWatch = null;
    }

    // ── Rendering ─────────────────────────────────────────────────────────

    private void Render()
    {
        if (_ballot is null) return;
        var tally = _tally;
        var open = tally?.IsOpen
                   ?? NominationTallyCalculator.IsOpen(_ballot.EndTime, closedByAdmin: false, DateTimeOffset.UtcNow);

        WindowLabel.Text = DescribeWindow(open, tally?.ClosedByAdmin == true, _ballot.EndTime);

        // Nominate: only while open (view-only with the result once closed).
        NominateCard.IsVisible = open;
        FallbackLabel.IsVisible = open && _ballot.UsedRosterFallback;
        NomineeList.Children.Clear();
        if (open)
        {
            if (_ballot.Candidates.Count == 0)
            {
                NomineeList.Children.Add(new Label
                {
                    Style = StyleOf("PvCaption"),
                    Text = "No present players were recorded for this match."
                });
            }
            foreach (var c in _ballot.Candidates)
                NomineeList.Children.Add(BuildNomineeButton(c, tally?.MyNomineeSlotId == c.SlotId));
        }

        RenderTally(tally, open);
        CloseVotingButton.IsVisible = open && tally is not null && IsAdmin();
    }

    private Button BuildNomineeButton(NominationCandidate c, bool isMine)
    {
        var button = new Button
        {
            Style = StyleOf(isMine ? "PvButtonPrimary" : "PvButtonOutline"),
            Text = isMine ? $"✓ {c.Name}  (your pick)" : c.Name,
            IsEnabled = !_submitting
        };
        button.Clicked += async (_, _) => await SubmitAsync(c);
        return button;
    }

    private void RenderTally(NominationTally? tally, bool open)
    {
        TallyCard.IsVisible = tally is not null;
        if (tally is null) return;

        TallyHeader.Text = open ? "Live tally" : "Result";
        TallyList.Children.Clear();
        TallyEmptyLabel.IsVisible = tally.Entries.Count == 0;
        TallyEmptyLabel.Text = open ? "No votes yet." : "No votes were cast.";
        var joint = tally.Leaders.Count > 1;

        foreach (var e in tally.Entries)
        {
            var tag = !e.IsLeader ? ""
                : open ? (joint ? "Joint leader" : "Leader")
                : (joint ? "Joint player of the match" : "Player of the match");

            var row = new Grid
            {
                ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
                ColumnSpacing = 8
            };
            var names = new VerticalStackLayout { Spacing = 0 };
            names.Children.Add(new Label
            {
                Style = StyleOf(e.IsLeader ? "PvAccentText" : "PvBody"),
                Text = e.IsLeader ? $"★ {e.NomineeName}" : e.NomineeName
            });
            if (tag.Length > 0)
                names.Children.Add(new Label { Style = StyleOf("PvCaption"), Text = tag });
            row.Add(names, 0, 0);
            row.Add(new Label
            {
                Style = StyleOf("PvBodyStrong"),
                Text = e.Votes == 1 ? "1 vote" : $"{e.Votes} votes",
                VerticalOptions = LayoutOptions.Center
            }, 1, 0);
            TallyList.Children.Add(row);
        }
    }

    private static string DescribeWindow(bool open, bool closedByAdmin, DateTimeOffset? endTime)
    {
        if (open && endTime is { } end)
        {
            var closes = (end + NominationTallyCalculator.VotingWindow).ToLocalTime();
            return $"Voting is open until {closes:ddd h:mm tt}, or until an Admin closes it.";
        }
        if (closedByAdmin) return "Voting was closed by an Admin.";
        return endTime is null ? "Voting opens when the match has ended." : "Voting closed 24 hours after the match.";
    }

    // ── Actions ───────────────────────────────────────────────────────────

    private async Task SubmitAsync(NominationCandidate c)
    {
        if (_nominations is null || _ballot is null || _submitting) return;
        if (_tally?.MyNomineeSlotId == c.SlotId) return;

        _submitting = true;
        SubmitStatusLabel.Text = $"Saving your vote for {c.Name}...";
        SubmitStatusLabel.IsVisible = true;
        Render();
        try
        {
            var result = await _nominations.SubmitNominationAsync(_teamId, _ballot.SessionId, c.SlotId, c.Name);
            SubmitStatusLabel.Text = result.Succeeded
                ? $"You nominated {c.Name}."
                : result.Message ?? "Couldn't save your vote.";

            // Refresh the window state when the server says voting has closed.
            if (result.Status is NominationSubmitStatus.WindowClosed or NominationSubmitStatus.PermissionDenied)
            {
                var fresh = await _nominations.GetTallyAsync(_teamId, _ballot.SessionId);
                if (fresh is not null) _tally = fresh;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[NominationsPage] Submit: {ex.Message}");
            SubmitStatusLabel.Text = "Couldn't save your vote. Please try again.";
        }
        finally
        {
            _submitting = false;
            Render();
        }
    }

    private async void OnCloseVotingClicked(object? sender, EventArgs e)
    {
        if (_nominations is null || _ballot is null) return;
        try
        {
            var confirm = await DisplayAlertAsync(
                "Close voting?",
                "No more votes can be cast or changed. The current leaders become the result.",
                "Close voting", "Cancel");
            if (!confirm) return;

            CloseVotingButton.IsEnabled = false;
            var ok = await _nominations.CloseVotingAsync(_teamId, _ballot.SessionId);
            if (!ok)
                await DisplayAlertAsync("Nominations", "Couldn't close voting. Only Admins can close it.", "OK");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[NominationsPage] Close: {ex.Message}");
        }
        finally
        {
            CloseVotingButton.IsEnabled = true;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private void HideAll()
    {
        MatchCard.IsVisible = false;
        NominateCard.IsVisible = false;
        TallyCard.IsVisible = false;
        CloseVotingButton.IsVisible = false;
    }

    private void ShowNotice(string text, bool retry = false)
    {
        NoticeLabel.Text = text;
        RetryButton.IsVisible = retry;
        NoticeCard.IsVisible = true;
    }

    private async void OnRetryClicked(object? sender, EventArgs e)
    {
        RetryButton.IsEnabled = false;
        try
        {
            NoticeCard.IsVisible = false;
            await LoadMatchesAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[NominationsPage] Retry: {ex.Message}");
            ShowNotice("Couldn't load matches. Check your connection and try again.", retry: true);
        }
        finally
        {
            RetryButton.IsEnabled = true;
        }
    }

    private Style? StyleOf(string key)
        => Resources.TryGetValue(key, out var v) && v is Style s ? s : null;

    private static T? GetService<T>() where T : class
    {
        var services = Application.Current?.Handler?.MauiContext?.Services
                       ?? Shell.Current?.Handler?.MauiContext?.Services;
        return services?.GetService<T>();
    }
}
