namespace TurfTime2.Models;

/// <summary>A player who may be nominated for a session (present / active in that match).</summary>
public sealed record NominationCandidate(int SlotId, string Name);

/// <summary>
/// Eligible nominees for a session: players who were present / active in that
/// match's squad (docs/nominations-spec.md §3.3, §7).
/// </summary>
public static class NominationSquad
{
    /// <summary>
    /// Returns the present players for <paramref name="session"/>, ordered by slot.
    /// <para>
    /// Schema v2 sessions carry <see cref="PlayerStats.SlotId"/> and
    /// <see cref="PlayerStats.WasPresent"/>, so the current roster is not needed.
    /// </para>
    /// <para>
    /// Older sessions (no slot ids) fall back to <paramref name="currentRoster"/>:
    /// player names from the session are matched to current roster slots, and a
    /// player counts as present if they logged any field, bench or goalie time
    /// (absent players were recorded with zero time). If the session has no player
    /// stats at all, the non-absent players on the current roster are used.
    /// <paramref name="usedRosterFallback"/> is true whenever the roster was needed.
    /// </para>
    /// </summary>
    public static IReadOnlyList<NominationCandidate> GetEligible(
        GameSession? session,
        IReadOnlyList<PlayerSnapshot>? currentRoster,
        out bool usedRosterFallback)
    {
        usedRosterFallback = false;
        var stats = session?.Summary?.PlayerStats ?? [];

        if (stats.Any(s => s.SlotId > 0))
        {
            return stats
                .Where(s => s.SlotId > 0 && IsPresent(s))
                .GroupBy(s => s.SlotId)
                .Select(g => new NominationCandidate(g.Key, CleanName(g.First().PlayerName, g.Key)))
                .OrderBy(c => c.SlotId)
                .ToList();
        }

        usedRosterFallback = true;
        var roster = currentRoster ?? [];

        if (stats.Count == 0)
        {
            return roster
                .Where(p => p.SlotId > 0 && !p.Inactive)
                .Select(p => new NominationCandidate(p.SlotId, CleanName(p.Name, p.SlotId)))
                .OrderBy(c => c.SlotId)
                .ToList();
        }

        // Legacy v1 session: absent players were stored with zero time. If nobody has
        // any time (e.g. ended at kick-off), treat everyone listed as present.
        var anyTime = stats.Any(s => s.FieldSeconds + s.BenchSeconds + s.GoalieSeconds > 0);
        var presentNames = new HashSet<string>(
            stats.Where(s => !anyTime || s.FieldSeconds + s.BenchSeconds + s.GoalieSeconds > 0)
                 .Select(s => Normalize(s.PlayerName))
                 .Where(n => n.Length > 0),
            StringComparer.OrdinalIgnoreCase);

        return roster
            .Where(p => p.SlotId > 0 && presentNames.Contains(Normalize(p.Name)))
            .GroupBy(p => p.SlotId)
            .Select(g => new NominationCandidate(g.Key, CleanName(g.First().Name, g.Key)))
            .OrderBy(c => c.SlotId)
            .ToList();
    }

    /// <summary>True when <paramref name="slotId"/> is an eligible nominee for the session.</summary>
    public static bool IsEligible(
        GameSession? session,
        IReadOnlyList<PlayerSnapshot>? currentRoster,
        int slotId)
        => slotId >= 1 && GetEligible(session, currentRoster, out _).Any(c => c.SlotId == slotId);

    private static bool IsPresent(PlayerStats s)
        => s.WasPresent ?? (s.FieldSeconds + s.BenchSeconds + s.GoalieSeconds > 0);

    private static string Normalize(string? name) => (name ?? string.Empty).Trim();

    private static string CleanName(string? name, int slotId)
    {
        var n = Normalize(name);
        if (n.Length == 0) n = Player.DefaultName(slotId);
        return n.Length > 80 ? n[..80] : n;
    }
}
