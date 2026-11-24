using TurfTime2.Models;

namespace TurfTime2.Services;

/// <summary>
/// Club player list at <c>clubs/{clubId}/players/{playerId}</c> (docs/club-players-spec.md §4.1).
/// Club owners/admins and coaches (Admins of one of the club's teams) only.
/// Slice 1 reads the players collection directly; the one-document player index
/// (<c>clubs/{clubId}/playerIndex/v1</c>, §4.2) is a follow-up.
/// Fail soft: network errors return <see cref="ClubPlayerListResult.Reachable"/> = false, never throw.
/// </summary>
public interface IClubPlayerService
{
    /// <summary>
    /// Works out the caller's access for this club, opened from <paramref name="teamId"/>.
    /// When the caller is Admin of that club team (a coach), records the team on their club
    /// membership (<c>coachTeamId</c>) so the rules recognise them as a coach.
    /// </summary>
    Task<ClubPlayerAccess> GetAccessAsync(string clubId, string? teamId, CancellationToken cancellationToken = default);

    /// <summary>All players in the club (active and archived), sorted by name.</summary>
    Task<ClubPlayerListResult> ListAsync(string clubId, CancellationToken cancellationToken = default);

    /// <summary>Adds a player. Full date of birth required (decision 2).</summary>
    Task<ClubPlayerWriteResult> AddAsync(string clubId, string name, string? preferredName, DateOnly dateOfBirth,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the listed fields. Coaches may change name / preferred name only; date of birth
    /// and status (archive) are club owner/admin only (decision 4). Stats are never written.
    /// </summary>
    Task<ClubPlayerWriteResult> UpdateAsync(string clubId, string playerId, ClubPlayerUpdate update,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Assigns the player to <paramref name="teamId"/> for <paramref name="season"/> (play up only).
    /// Team-count over the warning threshold is returned in <see cref="ClubPlayerWriteResult.Warning"/>, not as a failure.
    /// </summary>
    Task<ClubPlayerWriteResult> AssignToTeamAsync(string clubId, string playerId, string teamId, string teamName,
        string ageGroup, int season, CancellationToken cancellationToken = default);

    /// <summary>Removes the player from <paramref name="teamId"/> for <paramref name="season"/>; history is kept.</summary>
    Task<ClubPlayerWriteResult> UnassignFromTeamAsync(string clubId, string playerId, string teamId, int season,
        CancellationToken cancellationToken = default);
}

public sealed class ClubPlayerAccess
{
    /// <summary>"owner", "admin", "coach" or "" (no access).</summary>
    public string Role { get; init; } = "";
    public bool Reachable { get; init; } = true;
    public bool CanView => Role is "owner" or "admin" or "coach";
    public bool CanAdd => CanView;
    /// <summary>Edit date of birth, merge, archive, delete.</summary>
    public bool IsClubAdmin => Role is "owner" or "admin";
}

public sealed class ClubPlayerListResult
{
    public bool Reachable { get; init; }
    public string? Error { get; init; }
    public List<ClubPlayer> Players { get; init; } = [];
}

public sealed class ClubPlayerWriteResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? Warning { get; init; }
    public ClubPlayer? Player { get; init; }

    public static ClubPlayerWriteResult Fail(string error) => new() { Error = error };
}

/// <summary>Fields to change; null means "leave as is".</summary>
public sealed class ClubPlayerUpdate
{
    public string? Name { get; init; }
    /// <summary>Set <see cref="SetPreferredName"/> to change it (empty clears it).</summary>
    public string? PreferredName { get; init; }
    public bool SetPreferredName { get; init; }
    public DateOnly? DateOfBirth { get; init; }
    public string? Status { get; init; }
}
