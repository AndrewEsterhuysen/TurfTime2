namespace TurfTime2.Services;

public interface ICloudTeamService
{
    Task<string?> EnsureSignedInAsync();

    /// <summary>
    /// Creates a standalone nickname team (no club). Returns "success" or "error: …".
    /// </summary>
    Task<string> CreateNicknameTeamAsync(
        string teamId,
        string teamName,
        string inviteCode,
        string adminCodeHash,
        string creatorEmail,
        string displayName);

    /// <summary>
    /// Creates a new club (caller = Owner) and its first team. Returns "success" or "error: …".
    /// </summary>
    Task<string> CreateClubWithTeamAsync(
        string clubId,
        string clubName,
        string teamId,
        string teamName,
        string inviteCode,
        string adminCodeHash,
        string? clubOwnerRecoveryCodeHash,
        string creatorEmail,
        string displayName);

    /// <summary>
    /// Creates a team under an existing club. Caller must be club Owner or Admin.
    /// Returns "success" or "error: …".
    /// </summary>
    Task<string> CreateTeamUnderClubAsync(
        string clubId,
        string teamId,
        string teamName,
        string inviteCode,
        string adminCodeHash,
        string creatorEmail,
        string displayName);

    /// <summary>
    /// Legacy entry point — creates a nickname team. Prefer <see cref="CreateNicknameTeamAsync"/>.
    /// </summary>
    Task<string> CreateTeamAsync(
        string teamId,
        string teamName,
        string inviteCode,
        string adminCodeHash,
        string creatorEmail,
        string displayName);

    Task<CloudTeamLookup?> LookupInviteCodeAsync(string inviteCode);

    /// <summary>
    /// Join via invite code. On success also upserts club membership when the team is clubbed.
    /// </summary>
    Task<CloudTeamJoinResult> JoinByInviteCodeAsync(string inviteCode, string displayName);

    /// <summary>
    /// Owner recovery using Team ID as anchor + a recovery code.
    /// Tries the team's Owner Recovery Code first; if that fails and the team is clubbed,
    /// tries the club's Owner Recovery Code. Same UI fields; branch on which hash matches.
    /// </summary>
    Task<CloudOwnerRecoveryResult> RejoinAsAdminAsync(
        string teamId,
        string adminCode,
        string displayName,
        Func<string, string> hashAdminCode);

    Task<string> UpdateMemberDisplayNameAsync(string teamId, string displayName, string? roleHint = null);

    Task<bool> UpdateInviteCodeAsync(string teamId, string oldCode, string newCode, string teamName);

    /// <summary>
    /// Ensures invite_codes lookup docs exist for a team (self-heal after failed create writes).
    /// </summary>
    Task<bool> EnsureInviteCodePublishedAsync(string teamId, string inviteCode, string teamName);

    /// <summary>Calls Cloud Function requestAdminCodeEmail. Returns success:teamName, not_found, or error:…</summary>
    Task<string> RequestAdminCodeEmailAsync(string teamId);

    /// <summary>
    /// Owner-only hard delete: wipes team cloud data (metadata, members, roster, messages, sessions, invites).
    /// Returns "success", "error: not_owner", or "error: …".
    /// </summary>
    Task<string> DeleteTeamAsOwnerAsync(string teamId);

    /// <summary>True when the signed-in user is metadata.createdBy (team owner).</summary>
    Task<bool> IsTeamOwnerAsync(string teamId);

    /// <summary>Uid of the team owner (metadata.createdBy), or null if unknown.</summary>
    Task<string?> GetTeamOwnerUidAsync(string teamId);

    /// <summary>List members of a shared team (uid, display name, role).</summary>
    Task<IReadOnlyList<CloudTeamMember>> ListMembersAsync(string teamId);

    /// <summary>
    /// Owner-only: set metadata.createdBy to another admin uid and retarget invite_codes ownership.
    /// Returns success or error: …
    /// </summary>
    Task<string> TransferOwnershipAsync(string teamId, string newOwnerUid);

    /// <summary>
    /// Admin-only: elevate an existing team member to <c>role=admin</c>.
    /// Returns success or error: …
    /// </summary>
    Task<string> PromoteMemberToAdminAsync(string teamId, string memberUid);

    /// <summary>
    /// Club Owner-only: elevate a club member to club <c>admin</c> (can create teams under the club).
    /// Returns "success" or "error: …".
    /// </summary>
    Task<string> PromoteClubMemberToAdminAsync(string clubId, string memberUid);

    /// <summary>
    /// Admin-only: remove a member from the team (deletes <c>teams/{id}/members/{uid}</c>).
    /// Cannot remove yourself, the owner, or another Admin unless you are the owner.
    /// Returns success or error: …
    /// </summary>
    Task<string> RemoveMemberAsync(string teamId, string memberUid);

    /// <summary>Cloud role for the signed-in user on this team, or null if not a member / error.</summary>
    Task<string?> GetMyRoleAsync(string teamId);

    /// <summary>
    /// Invite code from <c>teams/{id}/metadata/info</c> (for Admins who joined as members and never stored it locally).
    /// Returns null if missing / unreachable.
    /// </summary>
    Task<string?> GetTeamInviteCodeAsync(string teamId);

    /// <summary>Clubs where the signed-in user is Owner or Admin (for “add team to my club”).</summary>
    Task<IReadOnlyList<CloudClubSummary>> ListManagedClubsAsync();

    /// <summary>Teams under a club (for future filters). Caller should be a club member.</summary>
    Task<IReadOnlyList<CloudTeamLookup>> ListTeamsForClubAsync(string clubId);

    /// <summary>Club role for the signed-in user, or null if not a club member.</summary>
    Task<string?> GetMyClubRoleAsync(string clubId);

    /// <summary>True when signed-in user is club Owner (<c>metadata.createdBy</c>).</summary>
    Task<bool> IsClubOwnerAsync(string clubId);
}

public sealed class CloudTeamLookup
{
    public string TeamId { get; init; } = "";
    public string TeamName { get; init; } = "";
    public string ClubId { get; init; } = "";
    public string ClubName { get; init; } = "";
    public string Kind { get; init; } = ""; // clubbed | nickname | ""
}

public sealed class CloudTeamJoinResult
{
    public string Status { get; init; } = ""; // success | already_member | error
    public string TeamId { get; init; } = "";
    public string TeamName { get; init; } = "";
    public string ClubId { get; init; } = "";
    public string ClubName { get; init; } = "";
    public string Message { get; init; } = "";

    public bool IsSuccess =>
        string.Equals(Status, "success", StringComparison.OrdinalIgnoreCase);

    public bool IsAlreadyMember =>
        string.Equals(Status, "already_member", StringComparison.OrdinalIgnoreCase);

    public bool IsOk => IsSuccess || IsAlreadyMember;
}

/// <summary>
/// Result of Recover Owner Access. <see cref="Scope"/> is <c>team</c> or <c>club</c> on success.
/// </summary>
public sealed class CloudOwnerRecoveryResult
{
    public const string ScopeTeam = "team";
    public const string ScopeClub = "club";

    public string Status { get; init; } = ""; // success | error
    public string Scope { get; init; } = "";  // team | club
    public string TeamId { get; init; } = "";
    public string TeamName { get; init; } = "";
    public string ClubId { get; init; } = "";
    public string ClubName { get; init; } = "";
    public string Message { get; init; } = "";

    public bool IsSuccess =>
        string.Equals(Status, "success", StringComparison.OrdinalIgnoreCase);

    public bool IsTeamScope =>
        string.Equals(Scope, ScopeTeam, StringComparison.OrdinalIgnoreCase);

    public bool IsClubScope =>
        string.Equals(Scope, ScopeClub, StringComparison.OrdinalIgnoreCase);
}

public sealed class CloudClubSummary
{
    public string ClubId { get; init; } = "";
    public string ClubName { get; init; } = "";
    public string Role { get; init; } = ""; // owner | admin
}

public sealed class CloudTeamMember
{
    public string Uid { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Role { get; init; } = "member"; // admin | member
    public bool IsAdmin => string.Equals(Role, "admin", StringComparison.OrdinalIgnoreCase);
}
