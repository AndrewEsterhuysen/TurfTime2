using System.Windows.Input;
using TurfTime2.Models;

namespace TurfTime2;

public partial class GoalDetailModal : ContentPage
{
    public ICommand RecordGoalCommand { get; }
    public ICommand CancelCommand { get; }

    public IReadOnlyList<Player> FieldPlayers { get; private set; } = [];
    public IReadOnlyList<Player> FieldPlayersWithNone { get; private set; } = [];

    public Player? SelectedScorer { get; set; }
    public Player? SelectedAssist { get; set; }

    private readonly Func<string?, string?, Task> _onRecordGoal;
    private bool _completed = false;

    /// <summary>
    /// Creates a modal for recording goal details (scorer and assist).
    /// </summary>
    /// <param name="fieldPlayers">List of players currently on field to select scorer/assist from</param>
    /// <param name="onRecordGoal">Callback when goal is recorded with (scorer, assist) player names</param>
    public GoalDetailModal(IReadOnlyList<Player> fieldPlayers, Func<string?, string?, Task> onRecordGoal)
    {
        InitializeComponent();

        FieldPlayers = fieldPlayers;
        _onRecordGoal = onRecordGoal;

        // Create a list with an empty option for "no assist"
        var withNone = new List<Player>();
        var nonePlayer = new Player { Name = "(None)" };
        withNone.Add(nonePlayer);
        withNone.AddRange(fieldPlayers);
        FieldPlayersWithNone = withNone.AsReadOnly();

        RecordGoalCommand = new Command(async () => await OnRecordGoalAsync());
        CancelCommand = new Command(async () => await OnCancelAsync());

        BindingContext = this;
    }

    private async Task OnRecordGoalAsync()
    {
        if (SelectedScorer is null)
        {
            await DisplayAlert("Error", "Please select a player who scored the goal.", "OK");
            return;
        }

        _completed = true;
        var scorerName = SelectedScorer.Name;
        var assistName = SelectedAssist?.Name == "(None)" ? null : SelectedAssist?.Name;

        try
        {
            await _onRecordGoal(scorerName, assistName);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GoalDetailModal] Error recording goal: {ex.Message}");
            await DisplayAlert("Error", "Failed to record goal details.", "OK");
        }
        finally
        {
            await CloseModalAsync();
        }
    }

    private async Task OnCancelAsync()
    {
        _completed = true;
        await CloseModalAsync();
    }

    private async Task CloseModalAsync()
    {
        var navigation = Navigation ?? Shell.Current?.Navigation;
        if (navigation?.ModalStack.Count > 0)
        {
            await navigation.PopModalAsync();
            return;
        }

        if (Shell.Current is not null)
        {
            await Shell.Current.GoToAsync("..");
        }
    }

    private async void OnCancelToolbarClicked(object? sender, EventArgs e)
    {
        await OnCancelAsync();
    }

    protected override bool OnBackButtonPressed()
    {
        if (!_completed)
        {
            _ = OnCancelAsync();
            return true;
        }
        return base.OnBackButtonPressed();
    }
}
