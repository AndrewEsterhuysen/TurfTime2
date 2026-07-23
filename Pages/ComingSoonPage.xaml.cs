namespace TurfTime2;

[QueryProperty(nameof(TitleParam), "title")]
public partial class ComingSoonPage : ContentPage
{
    private string _pageLabel = "Coming soon";

    public ComingSoonPage()
    {
        InitializeComponent();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        DetailsPage.ApplyPageTeamTitle(this, _pageLabel);
    }

    /// <summary>
    /// Optional query param sets the base page name (e.g. Kit, Duties, Nominations).
    /// Combined with the active team in the nav title: "Kit: My Team" (team italic).
    /// </summary>
    public string TitleParam
    {
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
                _pageLabel = Uri.UnescapeDataString(value);
            DetailsPage.ApplyPageTeamTitle(this, _pageLabel);
        }
    }
}
