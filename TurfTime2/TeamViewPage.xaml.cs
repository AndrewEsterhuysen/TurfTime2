namespace TurfTime2;

public partial class TeamViewPage : ContentPage
{
    private const string TeamViewKey = "team_view_preference";

    // Static event for notifying view changes
    public static event EventHandler<string> TeamViewChanged;

    public TeamViewPage()
    {
        InitializeComponent();
        LoadCurrentView();
    }

    private void LoadCurrentView()
    {
        var current = Preferences.Get(TeamViewKey, "swipe");
        SwipeViewCheck.IsVisible = current == "swipe";
    }

    private void SaveView(string viewType)
    {
        Preferences.Set(TeamViewKey, viewType);
        TeamViewChanged?.Invoke(this, viewType);
        SwipeViewCheck.IsVisible = viewType == "swipe";
    }

    private void OnSwipeViewTapped(object sender, EventArgs e)
    {
        SaveView("swipe");
    }
}
