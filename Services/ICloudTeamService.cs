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

    Task<bool> JoinAsMemberAsync(string teamId, string displayName);

    Task UpdateMemberDisplayNameAsync(string teamId, string displayName);

    Task<bool> UpdateInviteCodeAsync(string teamId, string oldCode, string newCode, string teamName);
}

public sealed class CloudTeamLookup
{
    public string TeamId { get; init; } = "";
    public string TeamName { get; init; } = "";
}
