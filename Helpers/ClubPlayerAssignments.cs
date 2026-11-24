using TurfTime2.Models;

namespace TurfTime2.Helpers;

/// <summary>
/// Pure assignment helpers for <see cref="ClubPlayer.Teams"/> / <see cref="ClubPlayer.TeamIds"/>
/// (docs/club-players-spec.md §4.1, decision 6 and 8).
/// </summary>
public static class ClubPlayerAssignments
{
    /// <summary>Warn (do not block) when assigning a player who is already on this many teams.</summary>
    public const int TeamCountWarningAt = 3;

    public static List<string> CurrentTeamIds(IEnumerable<ClubPlayerTeamEntry> teams) =>
        teams
            .Where(t => t.RemovedAt is null && !string.IsNullOrWhiteSpace(t.TeamId))
            .Select(t => t.TeamId)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// True when assigning <paramref name="teamId"/> would take the player to
    /// <see cref="TeamCountWarningAt"/> or more current teams (the existing team is ignored).
    /// </summary>
    public static bool ShouldWarnTeamCount(IEnumerable<ClubPlayerTeamEntry> teams, string teamId)
    {
        var current = CurrentTeamIds(teams);
        if (current.Contains(teamId, StringComparer.Ordinal)) return false;
        return current.Count >= TeamCountWarningAt;
    }

    public static List<ClubPlayerTeamEntry> Assign(
        IEnumerable<ClubPlayerTeamEntry> existing,
        string teamId,
        string teamName,
        int season,
        DateTimeOffset now)
    {
        var list = existing.Select(Clone).ToList();
        var open = list.Find(t =>
            string.Equals(t.TeamId, teamId, StringComparison.Ordinal)
            && t.Season == season
            && t.RemovedAt is null);
        if (open is not null)
        {
            if (!string.IsNullOrWhiteSpace(teamName))
                open.TeamName = teamName;
            return list;
        }

        var closed = list.FindLast(t =>
            string.Equals(t.TeamId, teamId, StringComparison.Ordinal)
            && t.Season == season
            && t.RemovedAt is not null);
        if (closed is not null)
        {
            closed.RemovedAt = null;
            closed.AssignedAt = now;
            if (!string.IsNullOrWhiteSpace(teamName))
                closed.TeamName = teamName;
            return list;
        }

        list.Add(new ClubPlayerTeamEntry
        {
            TeamId = teamId,
            TeamName = teamName ?? "",
            Season = season,
            AssignedAt = now
        });
        return list;
    }

    /// <summary>
    /// Current team names, preferring <paramref name="teamIds"/> when it is the
    /// source of truth (entries in <see cref="ClubPlayer.Teams"/> with no
    /// <see cref="ClubPlayerTeamEntry.RemovedAt"/> are used only to look up names,
    /// or as a fallback when <paramref name="teamIds"/> is null).
    /// </summary>
    public static List<string> CurrentTeamNames(
        IEnumerable<ClubPlayerTeamEntry> teams,
        IReadOnlyList<string>? teamIds)
    {
        var list = teams as IList<ClubPlayerTeamEntry> ?? teams.ToList();
        var ids = teamIds is null
            ? CurrentTeamIds(list)
            : teamIds.Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        var names = new List<string>();
        foreach (var id in ids)
        {
            var name = list.LastOrDefault(t =>
                           string.Equals(t.TeamId, id, StringComparison.Ordinal) && t.RemovedAt is null)?.TeamName
                       ?? list.LastOrDefault(t =>
                           string.Equals(t.TeamId, id, StringComparison.Ordinal))?.TeamName;
            if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name, StringComparer.Ordinal))
                names.Add(name);
        }
        return names;
    }

    /// <summary>
    /// Close every open assignment to <paramref name="teamId"/>, regardless of
    /// season. A season mismatch must not leave the player listed on the team.
    /// </summary>
    public static List<ClubPlayerTeamEntry> Unassign(
        IEnumerable<ClubPlayerTeamEntry> existing,
        string teamId,
        int season,
        DateTimeOffset now)
    {
        _ = season; // kept so callers can pass the team's current season
        var list = existing.Select(Clone).ToList();
        foreach (var t in list)
        {
            if (string.Equals(t.TeamId, teamId, StringComparison.Ordinal)
                && t.RemovedAt is null)
                t.RemovedAt = now;
        }
        return list;
    }

    private static ClubPlayerTeamEntry Clone(ClubPlayerTeamEntry t) => new()
    {
        TeamId = t.TeamId,
        TeamName = t.TeamName,
        Season = t.Season,
        AssignedAt = t.AssignedAt,
        RemovedAt = t.RemovedAt
    };
}
