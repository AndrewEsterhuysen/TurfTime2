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

    /// <summary>
    /// Prefer the stored <c>kind</c> field when present; older teams may omit it,
    /// in which case a non-empty <paramref name="clubId"/> means clubbed.
    /// </summary>
    public static string ResolveKind(string? kind, string? clubId)
    {
        if (string.Equals(kind, KindClubbed, StringComparison.OrdinalIgnoreCase))
            return KindClubbed;
        if (string.Equals(kind, KindNickname, StringComparison.OrdinalIgnoreCase))
            return KindNickname;
        return HasClub(clubId) ? KindClubbed : KindNickname;
    }

    /// <summary>
    /// Short type line for the Team Admin panel, visible to every member.
    /// </summary>
    public static string FormatTypeLabel(bool isShared, bool isLocal, string? kind, string? clubId, string? clubName)
    {
        if (isLocal)
            return "Type: Local team (this device only)";
        if (!isShared)
            return "Type: Not configured";

        if (string.Equals(ResolveKind(kind, clubId), KindClubbed, StringComparison.OrdinalIgnoreCase))
        {
            var name = (clubName ?? string.Empty).Trim();
            return string.IsNullOrEmpty(name)
                ? "Type: Online · Club team"
                : $"Type: Online · Club team ({name})";
        }

        return "Type: Online · Nickname team";
    }
}
