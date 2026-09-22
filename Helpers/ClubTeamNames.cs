namespace TurfTime2.Helpers;

/// <summary>
/// Display helpers for clubbed vs nickname teams.
/// Club identity is <c>clubId</c> in Firestore; names are labels only.
/// </summary>
public static class ClubTeamNames
{
    public const string KindClubbed = "clubbed";
    public const string KindNickname = "nickname";

    /// <summary>Team-first composed label for lists and share captions (truncation keeps the side).</summary>
    public static string ComposeDisplayName(string? teamName, string? clubName)
    {
        var team = (teamName ?? string.Empty).Trim();
        var club = (clubName ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(club))
            return team;
        if (string.IsNullOrEmpty(team))
            return club;
        return $"{team} · {club}";
    }

    public static bool HasClub(string? clubId) => !string.IsNullOrWhiteSpace(clubId);
}
