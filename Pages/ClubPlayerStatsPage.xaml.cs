using System.Globalization;
using TurfTime2.Helpers;
using TurfTime2.Models;
using TurfTime2.Services;

namespace TurfTime2;

/// <summary>
/// Per-player running tally (docs/club-players-spec.md decision 7): matches, field time,
/// goals, assists and POTM, filterable by season. Club owners, admins and coaches only.
/// </summary>
public partial class ClubPlayerStatsPage : ContentPage
{
    private const string PageLabel = "Stats";
    private const string AllSeasons = "All seasons";

    private static string _clubId = "";
    private static ClubPlayer? _player;

    private IClubPlayerStatsService? _stats;
    private IClubPlayerService? _players;
    private List<ClubPlayerMatch> _all = [];
    private bool _loading;
    private bool _suppressSeason;
    private int _loadVersion;

    public ClubPlayerStatsPage()
    {
        InitializeComponent();
    }

    internal static void Begin(string clubId, ClubPlayer player)
    {
        _clubId = clubId;
        _player = player;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        DetailsPage.ApplyPageTeamTitle(this, PageLabel);
        NameLabel.Text = _player?.DisplayName ?? "Player stats";
        _stats ??= GetService<IClubPlayerStatsService>();
        _players ??= GetService<IClubPlayerService>();
        await LoadAsync();
    }

    private async void OnRetryClicked(object? sender, EventArgs e) => await LoadAsync();

    private void OnSeasonChanged(object? sender, EventArgs e)
    {
        if (!_suppressSeason) Render();
    }

    private async Task LoadAsync()
    {
        var version = ++_loadVersion;
        if (string.IsNullOrWhiteSpace(_clubId) || _player is null)
        {
            ShowNotice("Stats are available on club players only.", retry: false);
            return;
        }
        if (_stats is null)
        {
            ShowNotice("Player stats are not available right now.", retry: true);
            return;
        }

        var teamId = Preferences.Get("team_id", "");
        if (_players is not null)
        {
            var access = await _players.GetAccessAsync(_clubId, teamId);
            if (version != _loadVersion) return;
            if (!access.CanView)
            {
                ShowNotice(access.Reachable
                    ? "Only club admins and coaches can see player stats."
                    : "Could not reach the server. Check your connection.", retry: !access.Reachable);
                return;
            }
        }

        _loading = true;
        Busy.IsVisible = Busy.IsRunning = true;
        ShowNotice("Loading…", retry: false);
        try
        {
            var result = await _stats.ListMatchesAsync(_clubId, _player.PlayerId);
            if (version != _loadVersion) return;
            if (!result.Reachable || result.Error is not null)
            {
                ShowNotice(result.Error ?? "Could not load stats.", retry: true);
                return;
            }

            _all = result.Matches;
            NoticeCard.IsVisible = false;
            FilterCard.IsVisible = TotalsCard.IsVisible = ListCard.IsVisible = true;
            FillSeasons();
            Render();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ClubPlayerStats] Load: {ex.Message}");
            ShowNotice("Could not reach the server. Check your connection and try again.", retry: true);
        }
        finally
        {
            _loading = false;
            Busy.IsVisible = Busy.IsRunning = false;
        }
    }

    private void FillSeasons()
    {
        _suppressSeason = true;
        var previous = SeasonPicker.SelectedItem as string;
        SeasonPicker.Items.Clear();
        SeasonPicker.Items.Add(AllSeasons);
        foreach (var year in _all.Select(m => m.Season).Where(s => s >= 2000).Distinct().OrderByDescending(s => s))
            SeasonPicker.Items.Add(year.ToString(CultureInfo.InvariantCulture));
        var current = ClubAgeGroups.CurrentSeason().ToString(CultureInfo.InvariantCulture);
        if (!SeasonPicker.Items.Contains(current))
            SeasonPicker.Items.Add(current);

        var pick = previous is not null && SeasonPicker.Items.Contains(previous)
            ? previous
            : AllSeasons;
        SeasonPicker.SelectedItem = pick;
        _suppressSeason = false;
    }

    private void Render()
    {
        var selected = SeasonPicker.SelectedItem as string ?? AllSeasons;
        var rows = string.Equals(selected, AllSeasons, StringComparison.Ordinal)
            ? _all
            : int.TryParse(selected, out var year)
                ? _all.Where(m => m.Season == year).ToList()
                : _all;

        var totals = ClubPlayerSeasonTotals.From(rows);
        TotalsLabel.Text =
            $"{totals.Matches} match{(totals.Matches == 1 ? "" : "es")}  ·  {ClubPlayerFieldTime.Format(totals.SecondsPlayed)}  ·  " +
            $"{totals.Goals} goal{(totals.Goals == 1 ? "" : "s")}  ·  {totals.Assists} assist{(totals.Assists == 1 ? "" : "s")}  ·  " +
            $"{totals.Potm} POTM";

        MatchList.Children.Clear();
        EmptyLabel.IsVisible = rows.Count == 0;
        foreach (var m in rows)
            MatchList.Children.Add(BuildRow(m));
    }

    private View BuildRow(ClubPlayerMatch m)
    {
        var date = m.MatchDate == DateTimeOffset.MinValue
            ? ""
            : m.MatchDate.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture);
        var bits = new List<string>();
        if (!string.IsNullOrWhiteSpace(date)) bits.Add(date);
        if (!string.IsNullOrWhiteSpace(m.TeamName)) bits.Add(m.TeamName);
        var title = bits.Count > 0 ? string.Join(" · ", bits) : "Match";

        var line = $"{m.Goals} goal{(m.Goals == 1 ? "" : "s")}, {m.Assists} assist{(m.Assists == 1 ? "" : "s")} · {ClubPlayerFieldTime.Format(m.EffectiveSeconds)}";
        if (m.Potm) line += " · POTM";

        return new Border
        {
            Style = StyleOf("PvCardTinted"),
            Padding = new Thickness(12, 8),
            Content = new VerticalStackLayout
            {
                Spacing = 2,
                Children =
                {
                    new Label { Style = StyleOf("PvBodyStrong"), Text = title },
                    new Label { Style = StyleOf("PvCaption"), Text = line },
                }
            }
        };
    }

    private void ShowNotice(string message, bool retry)
    {
        FilterCard.IsVisible = TotalsCard.IsVisible = ListCard.IsVisible = false;
        NoticeCard.IsVisible = true;
        NoticeLabel.Text = message;
        RetryButton.IsVisible = retry;
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
