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

        // Store references to check marks and frames for easy access
        checkMarks = new[] { Option1Check, Option2Check, Option3Check, Option4Check, Option5Check };
        frames = new[] { Option1Frame, Option2Frame, Option3Frame, Option4Frame, Option5Frame };

        LoadCurrentStyle();
        UpdateUI();
    }

    private void LoadCurrentStyle()
    {
        if (Preferences.ContainsKey(RotationStyleKey))
        {
            currentStyle = Preferences.Get(RotationStyleKey, 1);
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

        // Show the check mark for the current style (1-indexed)
        if (currentStyle >= 1 && currentStyle <= 5)
        {
            checkMarks[currentStyle - 1].IsVisible = true;
        }
    }

    private void OnOption1Tapped(object sender, EventArgs e)
    {
        SaveStyle(1);
    }

    private void OnOption2Tapped(object sender, EventArgs e)
    {
        SaveStyle(2);
    }

    private void OnOption3Tapped(object sender, EventArgs e)
    {
        SaveStyle(3);
    }

    private void OnOption4Tapped(object sender, EventArgs e)
    {
        SaveStyle(4);
    }

    private void OnOption5Tapped(object sender, EventArgs e)
    {
        SaveStyle(5);
    }
}
