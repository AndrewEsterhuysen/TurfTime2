namespace TurfTime2;

public partial class OptionsPage : ContentPage
{
    public OptionsPage()
    {
        InitializeComponent();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        DetailsPage.ApplyPageTeamTitle(this, "Options");

        var enabled = GoalScoringOptions.IsScorerAssistEnabled();
        EnableGoalDetailsSwitch.IsToggled = enabled;
        ToggleStateLabel.Text = enabled ? "ON" : "OFF";
    }

    private void OnEnableGoalDetailsToggled(object sender, ToggledEventArgs e)
    {
        GoalScoringOptions.SetScorerAssistEnabled(e.Value);
        ToggleStateLabel.Text = e.Value ? "ON" : "OFF";
    }
}
