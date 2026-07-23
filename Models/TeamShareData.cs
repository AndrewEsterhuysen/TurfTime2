using System.Text.Json.Serialization;

namespace TurfTime2.Models;

/// <summary>
/// Payload carried in team QR codes / deep links.
/// <list type="bullet">
/// <item><see cref="KindLocal"/> — offline roster share (name + players).</item>
/// <item><see cref="KindShared"/> — cloud join: invite code only; roster lives in Firestore.</item>
/// </list>
/// </summary>
public sealed class TeamShareData
{
    public const string KindLocal = "local";
    public const string KindShared = "shared";

    /// <summary>"local" (default) or "shared". Missing Kind with players is treated as local for older QRs.</summary>
    public string Kind { get; set; } = KindLocal;

    /// <summary>Invite code for <see cref="KindShared"/> join. Empty for local roster shares.</summary>
    public string InviteCode { get; set; } = string.Empty;

    public string TeamName { get; set; } = string.Empty;
    public string TeamId { get; set; } = string.Empty;
    public List<TeamSharePlayer> Players { get; set; } = [];
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>UI-only label (not written into the QR payload).</summary>
    [JsonIgnore]
    public string DisplayTitle { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsSharedJoin =>
        string.Equals(Kind, KindShared, StringComparison.OrdinalIgnoreCase);
}

public sealed class TeamSharePlayer
{
    public string Name { get; set; } = string.Empty;
    public string Position { get; set; } = string.Empty;
}
