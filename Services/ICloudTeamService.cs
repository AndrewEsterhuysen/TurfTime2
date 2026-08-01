namespace TurfTime2.Services;

public interface ICloudTeamService
{
    Task<string?> EnsureSignedInAsync();

    /// <summary>Returns "success" or "error: …".</summary>
    Task<string> CreateTeamAsync(
        string teamId,
        string teamName,
        string inviteCode,
        string adminCodeHash,
        string creatorEmail,
        string displayName);

    Task<CloudTeamLookup?> LookupInviteCodeAsync(string inviteCode);

    /// <summary>
    /// Join via invite code. Returns success:teamId:teamName, already_member:…, or error:…
    /// </summary>
    Task<string> JoinByInviteCodeAsync(string inviteCode, string displayName);

    /// <summary>
    /// Rejoin as admin with recovery code. Returns success:teamId:teamName or error:…
    /// </summary>
    Task<string> RejoinAsAdminAsync(string teamId, string adminCode, string displayName, Func<string, string> hashAdminCode);

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

    /// <summary>True when the signed-in user is metadata.createdBy (club manager / owner).</summary>
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
    /// Admin-only: remove a member from the team (deletes <c>teams/{id}/members/{uid}</c>).
    /// Cannot remove yourself, the owner, or another Admin unless you are the owner.
    /// Returns success or error: …
    /// </summary>
    Task<string> RemoveMemberAsync(string teamId, string memberUid);

    /// <summary>Cloud role for the signed-in user on this team, or null if not a member / error.</summary>
    Task<string?> GetMyRoleAsync(string teamId);
}

public sealed class CloudTeamLookup
{
    public string TeamId { get; init; } = "";
    public string TeamName { get; init; } = "";
}

public sealed class CloudTeamMember
{
    public string Uid { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Role { get; init; } = "member"; // admin | member
    public bool IsAdmin => string.Equals(Role, "admin", StringComparison.OrdinalIgnoreCase);
}
