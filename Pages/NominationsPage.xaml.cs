using TurfTime2.Services;

namespace TurfTime2;

/// <summary>
/// Placeholder player-of-the-match page. Registered at details/nominations
/// and opened from the Details tab. End-game and Reports entry points are
/// specified in docs/nominations-spec.md but not wired yet.
/// Optional query param <c>sessionId</c> is accepted for a later deep link.
/// </summary>
[QueryProperty(nameof(SessionId), "sessionId")]
public partial class NominationsPage : ContentPage
{
    private const string PageLabel = "Nominations";
    private INominationService? _nominations;

    public NominationsPage()
    {
        InitializeComponent();
    }

    /// <summary>Optional session to open directly (from End game or Reports, later).</summary>
    public string SessionId { get; set; } = "";

    protected override void OnAppearing()
    {
        base.OnAppearing();
        DetailsPage.ApplyPageTeamTitle(this, PageLabel);
        _nominations ??= GetService<INominationService>();
        if (!string.IsNullOrWhiteSpace(SessionId))
        {
            System.Diagnostics.Debug.WriteLine(
                $"[NominationsPage] sessionId={SessionId} (UI not wired to a session yet)");
        }
    }

    private static T? GetService<T>() where T : class
    {
        var services = Application.Current?.Handler?.MauiContext?.Services
                       ?? Shell.Current?.Handler?.MauiContext?.Services;
        return services?.GetService<T>();
    }
}
