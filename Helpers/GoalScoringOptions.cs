namespace TurfTime2;

public static class GoalScoringOptions
{
    public const string EnableScorerAssistKey = "game.enableScorerAssist";
    public const bool EnableScorerAssistDefault = true;

    public static bool IsScorerAssistEnabled()
        => Preferences.Get(EnableScorerAssistKey, EnableScorerAssistDefault);

    public static void SetScorerAssistEnabled(bool enabled)
        => Preferences.Set(EnableScorerAssistKey, enabled);
}
