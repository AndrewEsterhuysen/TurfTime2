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

    /// <summary>Calls Cloud Function requestAdminCodeEmail. Returns success:teamName, not_found, or error:…</summary>
    Task<string> RequestAdminCodeEmailAsync(string teamId);
}

public sealed class CloudTeamLookup
{
    public string TeamId { get; init; } = "";
    public string TeamName { get; init; } = "";
}
