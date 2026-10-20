namespace TurfTime2.Services;

/// <summary>
/// Simple string-field documents under <c>teams/{teamId}/details/{docName}</c> (Kit, Duties).
/// Same backend/auth as <see cref="IMatchScheduleService"/>: Firestore REST first, SDK fallback.
/// Admin writes; members read. Fail soft when offline.
/// </summary>
public interface ITeamDetailsDocService
{
    /// <summary>
    /// Download the doc. Check <see cref="TeamDetailsDoc.Reachable"/> / <see cref="TeamDetailsDoc.Exists"/>
    /// to tell "offline" from "not created yet".
    /// </summary>
    Task<TeamDetailsDoc?> LoadAsync(string teamId, string docName);

    /// <summary>Upload all fields (overwrites the listed fields). Returns false on failure.</summary>
    Task<bool> SaveAsync(string teamId, string docName, IReadOnlyDictionary<string, string> fields);
}

public sealed class TeamDetailsDoc
{
    /// <summary>False when the cloud could not be reached (use local cache).</summary>
    public bool Reachable { get; init; }
    /// <summary>True when the doc exists in the cloud.</summary>
    public bool Exists { get; init; }
    public Dictionary<string, string> Fields { get; init; } = new();
    public DateTimeOffset LastModifiedUtc { get; init; }
}
