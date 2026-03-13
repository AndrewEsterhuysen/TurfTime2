namespace TurfTime2;

public partial class SkinsPage : ContentPage
{
    private const string ThemePreferenceKey = "AppTheme";

    public SkinsPage()
    {
        InitializeComponent();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        LoadCurrentTheme();
    }

    private void LoadCurrentTheme()
    {
        var theme = Preferences.Get(ThemePreferenceKey, "classic");
        UpdateCheckmarks(theme);
    }

    private void UpdateCheckmarks(string selectedTheme)
    {
        classicCheckmark.IsVisible = selectedTheme == "classic";
        modernCheckmark.IsVisible = selectedTheme == "modern";

        classicFrame.BackgroundColor = selectedTheme == "classic" 
            ? Color.FromArgb("#3d8c41") 
            : Color.FromArgb("#2e7d32");

        modernFrame.BackgroundColor = selectedTheme == "modern" 
            ? Color.FromArgb("#3d8c41") 
            : Color.FromArgb("#2e7d32");
    }

    private async void OnClassicTapped(object sender, EventArgs e)
    {
        await SetTheme("classic");
    }

    private async void OnModernTapped(object sender, EventArgs e)
    {
        await SetTheme("modern");
    }

    private async Task SetTheme(string theme)
    {
        // Save preference
        Preferences.Set(ThemePreferenceKey, theme);

        // Update UI
        UpdateCheckmarks(theme);

        // Try to update the GamePage WebView if it exists
        try
        {
            var webView = await FindGamePageWebView();
            if (webView != null)
            {
                await webView.EvaluateJavaScriptAsync($"setTheme('{theme}')");
                await DisplayAlertAsync("Theme Changed", $"{char.ToUpper(theme[0]) + theme.Substring(1)} theme applied!", "OK");
            }
            else
            {
                await DisplayAlertAsync("Theme Saved", $"{char.ToUpper(theme[0]) + theme.Substring(1)} theme will be applied when you return to the Game page.", "OK");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error setting theme: {ex.Message}");
            await DisplayAlertAsync("Theme Saved", $"{char.ToUpper(theme[0]) + theme.Substring(1)} theme will be applied when you return to the Game page.", "OK");
        }
    }

    private async Task<WebView?> FindGamePageWebView()
    {
        await Task.Delay(50); // Small delay to ensure UI is ready

        try
        {
            // The GamePage is already loaded in the Shell, we just need to find it
            // Since Shell uses templates, the page might not be instantiated yet
            // So we'll just return null and let the theme sync happen when user returns to Game page
            return null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error finding WebView: {ex.Message}");
        }

        return null;
    }
}
