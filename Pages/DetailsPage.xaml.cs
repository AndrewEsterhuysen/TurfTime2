namespace TurfTime2;

public partial class DetailsPage : ContentPage
{
    private const string TeamNameKey = "team_name";
    private const string PageLabel = "Details";

    public DetailsPage()
    {
        InitializeComponent();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        ApplyPageTeamTitle(this, PageLabel);
    }

    /// <summary>
    /// Sets nav title as "Page: TeamName" with the team name in italics.
    /// Plain <see cref="Page.Title"/> cannot mix fonts, so a Shell TitleView is used.
    /// </summary>
    internal static void ApplyPageTeamTitle(ContentPage page, string pageName)
    {
        var name = Preferences.Get(TeamNameKey, string.Empty)?.Trim();

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
        await NavigateAsync("details/comingsoon?title=Kit", "Kit");
    }

    private async void OnDutiesTapped(object sender, EventArgs e)
    {
        await NavigateAsync("details/comingsoon?title=Duties", "Duties");
    }

    private async void OnNominationsTapped(object sender, EventArgs e)
    {
        await NavigateAsync("details/comingsoon?title=Nominations", "Nominations");
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
