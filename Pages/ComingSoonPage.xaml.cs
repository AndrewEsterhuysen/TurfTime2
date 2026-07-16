namespace TurfTime2;

[QueryProperty(nameof(TitleParam), "title")]
public partial class ComingSoonPage : ContentPage
{
    public ComingSoonPage()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Optional query param sets the navigation bar title (e.g. Kit, Duties, Nominations).
    /// </summary>
    public string TitleParam
    {
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
                Title = Uri.UnescapeDataString(value);
        }
    }
}
