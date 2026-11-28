namespace TurfTime2;

public partial class DetailsPage : ContentPage
{
    private const string TeamNameKey = "team_name";
    private const string PageLabel = "Details";

    public DetailsPage()
    {
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        ApplyPageTeamTitle(this, PageLabel);
        // Local teams: only Reports is shown, so the tab stays consistent
        // without exposing shared-team options (Location, Kit, Duties, Nominations).
        // Shared teams: unchanged — only Nominations is hidden when the switch is off.
        var shared = DetailsCloudSync.IsSharedTeam();
        LocationCard.IsVisible = shared;
        KitCard.IsVisible = shared;
        DutiesCard.IsVisible = shared;
        try { await TeamNominationsOptions.RefreshAsync(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Details] Nominations setting: {ex.Message}"); }
        NominationsCard.IsVisible = shared && TeamNominationsOptions.ShowDetailsTile();
        if (Helpers.DesktopMode.IsWindows)
        {
            // Setup client: nominations and match reports come from played games, which are phone-only.
            NominationsCard.IsVisible = false;
            ReportsCard.IsVisible = false;
        }
        await RefreshClubPlayersCardAsync(shared);
    }

    /// <summary>
    /// Club players: club teams only, for club owners/admins and coaches (Admins of a club team).
    /// The page and the rules re-check access; this only decides whether to show the tile.
    /// </summary>
    private async Task RefreshClubPlayersCardAsync(bool shared)
    {
        var clubId = ClubPlayersPage.CurrentClubId();
        if (!shared || string.IsNullOrWhiteSpace(clubId))
        {
            ClubPlayersCard.IsVisible = false;
            return;
        }

        var teamAdmin = string.Equals(Preferences.Get("user_role", "member"), "admin", StringComparison.OrdinalIgnoreCase);
        var cacheKey = $"club_role_{clubId}";
        var cachedClubRole = Preferences.Get(cacheKey, string.Empty);
        ClubPlayersCard.IsVisible = teamAdmin || cachedClubRole is "owner" or "admin";
        if (teamAdmin) return;

        try
        {
            var services = Application.Current?.Handler?.MauiContext?.Services
                           ?? Shell.Current?.Handler?.MauiContext?.Services;
            if (services?.GetService(typeof(Services.ICloudTeamService)) is not Services.ICloudTeamService teams) return;
            var role = (await teams.GetMyClubRoleAsync(clubId))?.Trim().ToLowerInvariant() ?? "";
            Preferences.Set(cacheKey, role);
            if (string.Equals(ClubPlayersPage.CurrentClubId(), clubId, StringComparison.Ordinal))
                ClubPlayersCard.IsVisible = role is "owner" or "admin";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Details] Club role: {ex.Message}");
        }
    }

    /// <summary>
    /// Sets nav title as "Page: TeamName" with the team name in italics.
    /// Plain <see cref="Page.Title"/> cannot mix fonts, so a Shell TitleView is used.
    /// </summary>
    internal static void ApplyPageTeamTitle(ContentPage page, string pageName)
        => ApplyPageTeamTitle(page, pageName, Preferences.Get(TeamNameKey, string.Empty));

    /// <summary>
    /// Same as <see cref="ApplyPageTeamTitle(ContentPage, string)"/> but with an explicit team name
    /// (e.g. Chat, whose selected team can differ from the app-wide Game/Details team).
    /// </summary>
    internal static void ApplyPageTeamTitle(ContentPage page, string pageName, string? teamName)
    {
        var name = teamName?.Trim();

        // Accessibility / platform fallback (single style)
        page.Title = string.IsNullOrEmpty(name) ? pageName : $"{pageName}: {name}";

        var titleColor = ResolveNavTitleColor();
        var label = new Label
        {
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            HorizontalTextAlignment = TextAlignment.Center,
            LineBreakMode = LineBreakMode.TailTruncation,
            MaxLines = 1,
            FontSize = 17,
            TextColor = titleColor,
        };

        if (string.IsNullOrEmpty(name))
        {
            label.Text = pageName;
        }
        else
        {
            label.FormattedText = new FormattedString
            {
                Spans =
                {
                    new Span
                    {
                        Text = $"{pageName}: ",
                        FontSize = 17,
                        TextColor = titleColor,
                    },
                    new Span
                    {
                        Text = name,
                        FontSize = 17,
                        FontAttributes = FontAttributes.Italic,
                        TextColor = titleColor,
                    },
                }
            };
        }

        Shell.SetTitleView(page, label);
    }

    private static Color ResolveNavTitleColor()
    {
        var isDark = Application.Current?.RequestedTheme == AppTheme.Dark;
        return isDark ? Colors.White : Colors.Black;
    }

    private async void OnLocationTapped(object sender, EventArgs e)
    {
        await NavigateAsync("details/location", "Location");
    }

    private async void OnKitTapped(object sender, EventArgs e)
    {
        await NavigateAsync("details/kit", "Kit");
    }

    private async void OnDutiesTapped(object sender, EventArgs e)
    {
        await NavigateAsync("details/duties", "Duties");
    }

    private async void OnNominationsTapped(object sender, EventArgs e)
    {
        await NavigateAsync("details/nominations", "Nominations");
    }

    private async void OnReportsTapped(object sender, EventArgs e)
    {
        await NavigateAsync("details/reports", "Reports");
    }

    private async void OnClubPlayersTapped(object sender, EventArgs e)
    {
        await NavigateAsync("details/clubplayers", "Club players");
    }

    private async Task NavigateAsync(string route, string featureName)
    {
        try
        {
            await Shell.Current.GoToAsync(route);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Details] Navigation error ({featureName}): {ex.Message}");
            await DisplayAlert("Error", $"Could not open {featureName}.", "OK");
        }
    }
}
