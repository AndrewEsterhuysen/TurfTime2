namespace TurfTime2;

public partial class TeamViewPage : ContentPage
{
    private const string TeamViewKey = "team_view_preference";
    private string currentView = "swipe"; // Default to swipe view

    private Label[] checkMarks;

    // Static event for notifying view changes
    public static event EventHandler<string> TeamViewChanged;

    public TeamViewPage()
    {
        InitializeComponent();

        // Store references to check marks for easy access
        checkMarks = new[] { SwipeViewCheck, TableViewCheck };

        LoadCurrentView();
        UpdateUI();
    }

    private void LoadCurrentView()
    {
        if (Preferences.ContainsKey(TeamViewKey))
        {
            currentView = Preferences.Get(TeamViewKey, "swipe");
        }
        else
        {
            currentView = "swipe"; // Default
        }
    }

    private void SaveView(string viewType)
    {
        currentView = viewType;
        Preferences.Set(TeamViewKey, viewType);
        
        // Notify the WebView to update the view
        TeamViewChanged?.Invoke(this, viewType);
        
        UpdateUI();
    }

    private void UpdateUI()
    {
        // Hide all check marks first
        foreach (var check in checkMarks)
        {
            check.IsVisible = false;
        }

        // Show the check mark for the current view
        if (currentView == "swipe")
        {
            SwipeViewCheck.IsVisible = true;
        }
        else if (currentView == "table")
        {
            TableViewCheck.IsVisible = true;
        }
    }

    private void OnSwipeViewTapped(object sender, EventArgs e)
    {
        SaveView("swipe");
    }

    private void OnTableViewTapped(object sender, EventArgs e)
    {
        SaveView("table");
    }
}
