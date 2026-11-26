namespace TurfTime2.Services;

/// <summary>
/// Club Admin role (to-do item 8, final rules 25 Nov 2026). Club owner grants or removes Club
/// Admins (promote or single-use invite); club admins list club teams and act on them. Firestore
/// rules decide what each call may do:
/// <list type="bullet">
/// <item>No team Admin other than you: full control (team, roster players, members, roles,
/// settings, invite, delete) but never running a game.</item>
/// <item>Another team Admin exists: read everything except chat; edit Details only.</item>
/// <item>Club admin who is also team Admin: normal team Admin rights.</item>
/// </list>
/// </summary>
public interface IClubAdminService
{
    /// <summary>Clubs where you are Owner or Club Admin (from your teams, your club memberships and redeemed invites).</summary>
    Task<IReadOnlyList<CloudClubSummary>> ListMyAdminClubsAsync();

    /// <summary>Teams in the club with each team's status for you. Club owner/admins only.</summary>
    Task<IReadOnlyList<ClubTeamStatus>> ListClubTeamsAsync(string clubId);

    /// <summary>
    /// Your effective role on a team: <c>admin</c> / <c>member</c> (team membership) or, when you are
    /// not a member, <c>clubadmin</c> with <see cref="ClubTeamAccess"/> full/limited. Null when none.
    /// </summary>
    Task<ClubTeamEffectiveRole?> GetEffectiveRoleAsync(string teamId);

    /// <summary>Club owner only: create a single-use Club Admin invite (valid 7 days).</summary>
    Task<ClubAdminInviteResult> CreateInviteAsync(string clubId, string clubName);

    /// <summary>Club owner only: revoke an unused invite.</summary>
    Task<string> RevokeInviteAsync(string clubId, string code);

    /// <summary>Redeem an invite (text containing <c>clubId/CODE</c>). Returns the club on success.</summary>
    Task<ClubAdminRedeemResult> RedeemInviteAsync(string inviteText, string displayName);

    /// <summary>Club members with their club role (club members only can list).</summary>
    Task<IReadOnlyList<ClubMemberInfo>> ListClubMembersAsync(string clubId);

    /// <summary>Club owner only: set a club member's role to <c>admin</c> or <c>member</c>.</summary>
    Task<string> SetClubMemberRoleAsync(string clubId, string memberUid, string role);

    /// <summary>
    /// Refresh your team Admin activity stamp (<c>members/{uid}.seenAtUtc</c>, server time) on every
    /// team where you are Admin. Throttled; call on app start/resume. Best effort.
    /// </summary>
    Task TouchMyAdminActivityAsync();

    /// <summary>
    /// Club owner only: remove a team Admin of a club team who has been inactive for
    /// <see cref="ClubTeamAccess.InactiveAfter"/> or more, and record who removed whom and when
    /// (<c>clubs/{c}/auditLog</c>). The rules re-check the owner, club team and inactivity.
    /// </summary>
    Task<string> RemoveInactiveTeamAdminAsync(string clubId, string teamId, string teamName, string adminUid);

    /// <summary>Full control: join the team yourself as <c>admin</c> or <c>member</c>.</summary>
    Task<string> JoinTeamAsync(string teamId, string role, string displayName);

    /// <summary>Full control: set a team member's role (admin/member).</summary>
    Task<string> SetTeamMemberRoleAsync(string teamId, string memberUid, string role);

    /// <summary>Full control: remove a member from the team.</summary>
    Task<string> RemoveTeamMemberAsync(string teamId, string memberUid);

    /// <summary>Full control: rename and set age group / season.</summary>
    Task<string> UpdateTeamAsync(string teamId, string teamName, string ageGroup, int season);

    /// <summary>Full control: delete the team (the server removes the rest of the team tree).</summary>
    Task<string> DeleteTeamAsync(string teamId);
}

public static class ClubTeamAccess
{
    public const string TeamAdmin = "teamadmin"; // you are a team Admin (full team Admin rights)
    public const string Full = "full";           // no other team Admin: full control, no games
    public const string Limited = "limited";     // another team Admin: read all but chat, Details only
    /// <summary>Owner way back in: a team Admin idle this long can be removed by the club owner. Must match firestore.rules.</summary>
    public static readonly TimeSpan InactiveAfter = TimeSpan.FromDays(1);
}

public sealed class ClubTeamStatus
{
    public string TeamId { get; init; } = "";
    public string TeamName { get; init; } = "";
    public string ClubId { get; init; } = "";
    public string ClubName { get; init; } = "";
    public string AgeGroup { get; init; } = "";
    public int Season { get; init; }
    /// <summary>Team Admins (server-maintained); -1 when not yet known.</summary>
    public int TeamAdminCount { get; init; } = -1;
    /// <summary>Your team membership role (admin/member) or empty.</summary>
    public string MyTeamRole { get; init; } = "";
    /// <summary>Club owner only: team Admins inactive for <see cref="ClubTeamAccess.InactiveAfter"/> or more.</summary>
    public IReadOnlyList<ClubTeamAdminInfo> InactiveAdmins { get; init; } = Array.Empty<ClubTeamAdminInfo>();

    public bool IAmTeamAdmin => string.Equals(MyTeamRole, "admin", StringComparison.OrdinalIgnoreCase);

    public string Access => IAmTeamAdmin
        ? ClubTeamAccess.TeamAdmin
        : TeamAdminCount == 0 ? ClubTeamAccess.Full : ClubTeamAccess.Limited;

    public string StatusText => BaseStatusText + (InactiveAdmins.Count == 0
        ? ""
        : InactiveAdmins.Count == 1
            ? $"\n{InactiveAdmins[0].Label} hasn't been active for {InactiveAdmins[0].LastSeenText}"
            : $"\n{InactiveAdmins.Count} team admins haven't been active for a while");

    private string BaseStatusText => Access switch
    {
        ClubTeamAccess.TeamAdmin => TeamAdminCount > 1 ? "You are a team admin" : "You are the team admin",
        ClubTeamAccess.Full => "No team admin yet · club manages it",
        _ => TeamAdminCount < 0 ? "Team admin status not known yet · view only" : "Has a team admin · view and Details only",
    };
}

public sealed class ClubTeamAdminInfo
{
    public string Uid { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public DateTimeOffset LastSeenUtc { get; init; }
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? "A team admin" : DisplayName;
    public string LastSeenText
    {
        get
        {
            var d = DateTimeOffset.UtcNow - LastSeenUtc;
            return d.TotalDays >= 2 ? $"{(int)d.TotalDays} days" : $"{Math.Max(1, (int)d.TotalHours)} hours";
        }
    }
}

public sealed class ClubTeamEffectiveRole
{
    public string Role { get; init; } = "";   // admin | member | clubadmin
    public string Access { get; init; } = ""; // ClubTeamAccess for clubadmin; "" otherwise
    public string ClubId { get; init; } = "";
}

public sealed class ClubAdminInviteResult
{
    public bool Ok { get; init; }
    public string Code { get; init; } = "";
    public string InviteText { get; init; } = "";
    public DateTimeOffset ExpiresUtc { get; init; }
    public string Error { get; init; } = "";
}

public sealed class ClubAdminRedeemResult
{
    public bool Ok { get; init; }
    public string ClubId { get; init; } = "";
    public string ClubName { get; init; } = "";
    public string Message { get; init; } = "";
}

public sealed class ClubMemberInfo
{
    public string Uid { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Role { get; init; } = "member"; // owner | admin | member
}
