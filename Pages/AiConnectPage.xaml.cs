using Microsoft.Extensions.DependencyInjection;
using TurfTime2.Services;

namespace TurfTime2;

public partial class AiConnectPage : ContentPage
{
    private AiConnectService? _svc;

    public AiConnectPage()
    {
        InitializeComponent();
    }

    private AiConnectService? Svc()
    {
        if (_svc != null) return _svc;
        var services = Application.Current?.Handler?.MauiContext?.Services
            ?? Shell.Current?.Handler?.MauiContext?.Services;
        var auth = services?.GetService<IFirebaseAuthService>();
        return auth == null ? null : (_svc = new AiConnectService(auth));
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadGrantsAsync();
    }

    private async void OnGetCodeClicked(object sender, EventArgs e)
    {
        var svc = Svc();
        if (svc == null) return;
        GetCodeButton.IsEnabled = false;
        try
        {
            var code = await svc.CreateLinkCodeAsync();
            CodeLabel.Text = code.Code;
            ExpiryLabel.Text = $"Valid once, for {Math.Max(1, code.ExpiresInSeconds / 60)} minutes.";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AiConnect] code: {ex.Message}");
            await DisplayAlert("AI assistant", "Could not get a code. Check your connection and try again.", "OK");
        }
        finally
        {
            GetCodeButton.IsEnabled = true;
        }
    }

    private async Task LoadGrantsAsync()
    {
        var svc = Svc();
        if (svc == null) return;
        GrantsList.Children.Clear();
        try
        {
            var grants = await svc.ListGrantsAsync();
            NoGrantsLabel.IsVisible = grants.Count == 0;
            foreach (var g in grants)
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitionCollection(new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)) };
                row.Add(new Label { Text = g.ClientName, VerticalOptions = LayoutOptions.Center }, 0, 0);
                var revoke = new Button { Text = "Disconnect" };
                var id = g.Id;
                revoke.Clicked += async (_, _) =>
                {
                    if (!await DisplayAlert("Disconnect", $"Stop {g.ClientName} accessing your TurfTimer data?", "Disconnect", "Cancel")) return;
                    try { await svc.RevokeAsync(id); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[AiConnect] revoke: {ex.Message}"); }
                    await LoadGrantsAsync();
                };
                row.Add(revoke, 1, 0);
                GrantsList.Children.Add(row);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AiConnect] grants: {ex.Message}");
            NoGrantsLabel.IsVisible = true;
        }
    }
}
