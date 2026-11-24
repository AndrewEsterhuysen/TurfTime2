using TurfTime2.Models;

namespace TurfTime2.Services;

/// <summary>
/// Per-match club player stats at <c>clubs/{clubId}/players/{playerId}/matches/{sessionId}</c>.
/// Written when a club-team match is saved (and again if the match is edited or POTM is set).
/// Totals are computed on read. Club owners, admins and coaches only.
/// </summary>
public interface IClubPlayerStatsService
{
    /// <summary>
    /// Upserts one match row per linked club player in <paramref name="session"/>.
    /// No-ops when the team is not a club team. Re-saves overwrite the same session id.
    /// </summary>
    Task RecordSessionAsync(string teamId, GameSession session, IReadOnlySet<string>? potmPlayerIds = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-writes the session's match rows with POTM set for the winning slots.
    /// Safe to call more than once (idempotent).
    /// </summary>
    Task ApplyPotmAsync(string teamId, string sessionId, IEnumerable<int> winnerSlotIds,
        CancellationToken cancellationToken = default);

    /// <summary>All stored matches for this player, newest first.</summary>
    Task<ClubPlayerMatchListResult> ListMatchesAsync(string clubId, string playerId,
        CancellationToken cancellationToken = default);
}

public sealed class ClubPlayerMatchListResult
{
    public bool Reachable { get; init; }
    public string? Error { get; init; }
    public List<ClubPlayerMatch> Matches { get; init; } = [];
}
