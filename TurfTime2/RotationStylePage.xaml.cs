namespace TurfTime2;

public partial class RotationStylePage : ContentPage
{
    private const string RotationStyleKey = "rotation_style";
    private int currentStyle = 1; // Default to option 1

    private Label[] checkMarks;
    private Frame[] frames;

    // Static event for notifying style changes
    public static event EventHandler<int> RotationStyleChanged;

    public RotationStylePage()
    {
        InitializeComponent();

        // Store references to check marks and frames for easy access (only 2 options now)
        checkMarks = new[] { Option1Check, Option5Check };
        frames = new[] { Option1Frame, Option5Frame };

        LoadCurrentStyle();
        UpdateUI();
    }

    private void LoadCurrentStyle()
    {
        if (Preferences.ContainsKey(RotationStyleKey))
        {
            currentStyle = Preferences.Get(RotationStyleKey, 1);
            // Convert old style numbers to new ones (5 -> 5, 2/3/4 -> 1)
            if (currentStyle == 2 || currentStyle == 3 || currentStyle == 4)
            {
                currentStyle = 1; // Default to glowing border
            }
        }
        else
        {
            currentStyle = 1; // Default
        }
    }

    private void SaveStyle(int styleNumber)
    {
        currentStyle = styleNumber;
        Preferences.Set(RotationStyleKey, styleNumber);

        // Notify the WebView to update the style
        RotationStyleChanged?.Invoke(this, styleNumber);

        UpdateUI();
    }

    private void UpdateUI()
    {
        // Hide all check marks first
        foreach (var check in checkMarks)
        {
            check.IsVisible = false;
        }

        // Show check mark based on style (only 1 and 5 are valid now)
        if (currentStyle == 1)
        {
            Option1Check.IsVisible = true;
        }
        else if (currentStyle == 5)
        {
            Option5Check.IsVisible = true;
        }
        else
        {
            // Fallback to option 1
            Option1Check.IsVisible = true;
        }
    }

    private void OnOption1Tapped(object sender, EventArgs e)
    {
        SaveStyle(1);
    }

    private void OnOption5Tapped(object sender, EventArgs e)
    {
        SaveStyle(5);
    }
}
