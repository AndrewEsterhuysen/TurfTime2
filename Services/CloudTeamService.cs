using Plugin.Firebase.Firestore;

namespace TurfTime2.Services;

/// <summary>
/// Shared-team create/join/metadata via Plugin.Firebase Firestore (no REST).
/// </summary>
public sealed class CloudTeamService : ICloudTeamService
{
    private readonly IFirebaseAuthService _auth;
    private readonly IFirebaseFirestore _db;

    public CloudTeamService(IFirebaseAuthService auth, IFirebaseFirestore db)
    {
        _auth = auth;
        _db = db;
    }

    public Task<string?> EnsureSignedInAsync() => _auth.EnsureSignedInAsync();

    public async Task<string> CreateTeamAsync(
        string teamId,
        string teamName,
        string inviteCode,
        string adminCodeHash,
        string creatorEmail,
        string displayName)
    {
        var uid = await _auth.EnsureSignedInAsync().ConfigureAwait(false);
        if (uid is null)
            return "error: Could not authenticate with Firebase. Please check your internet connection.";

        try
        {
            await _db.GetDocument($"teams/{teamId}/metadata/info")
                .SetDataAsync(new Dictionary<object, object>
                {
                    ["teamName"] = teamName,
                    ["inviteCode"] = inviteCode,
                    ["adminCodeHash"] = adminCodeHash,
                    ["creatorEmail"] = creatorEmail ?? "",
                    ["createdBy"] = uid,
                    ["isActive"] = true
                }, SetOptions.Merge()).ConfigureAwait(false);

            await _db.GetDocument($"teams/{teamId}/members/{uid}")
                .SetDataAsync(new Dictionary<object, object>
                {
                    ["role"] = "admin",
                    ["displayName"] = displayName ?? ""
                }, SetOptions.Merge()).ConfigureAwait(false);

            await _db.GetDocument($"teams/{teamId}/roster/data")
                .SetDataAsync(new Dictionary<object, object>
                {
                    ["version"] = 2,
                    ["players"] = new List<object>()
                }, SetOptions.Merge()).ConfigureAwait(false);

            try
            {
                await _db.GetDocument($"invite_codes/{inviteCode}")
                    .SetDataAsync(new Dictionary<object, object>
                    {
                        ["teamId"] = teamId,
                        ["teamName"] = teamName,
                        ["createdBy"] = uid
                    }, SetOptions.Merge()).ConfigureAwait(false);
            }
            catch (Exception invEx)
            {
                System.Diagnostics.Debug.WriteLine($"[CloudTeam] invite lookup non-fatal: {invEx.Message}");
            }

            System.Diagnostics.Debug.WriteLine($"[CloudTeam] Created team {teamId}");
            return "success";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CloudTeam] CreateTeam: {ex.Message}");
            return $"error: {ex.Message}";
        }
    }

    public async Task<CloudTeamLookup?> LookupInviteCodeAsync(string inviteCode)
    {
        if (string.IsNullOrWhiteSpace(inviteCode)) return null;
        if (await _auth.EnsureSignedInAsync().ConfigureAwait(false) is null) return null;

        try
        {
            var snap = await _db.GetDocument($"invite_codes/{inviteCode.Trim()}")
                .GetDocumentSnapshotAsync<Dictionary<object, object>>()
                .ConfigureAwait(false);
            var data = snap?.Data;
            if (data is null) return null;

            return new CloudTeamLookup
            {
                TeamId = ReadString(data, "teamId"),
                TeamName = ReadString(data, "teamName")
            };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CloudTeam] LookupInvite: {ex.Message}");
            return null;
        }
    }

    public async Task<bool> JoinAsMemberAsync(string teamId, string displayName)
    {
        var uid = await _auth.EnsureSignedInAsync().ConfigureAwait(false);
        if (uid is null || string.IsNullOrWhiteSpace(teamId)) return false;

        try
        {
            await _db.GetDocument($"teams/{teamId}/members/{uid}")
                .SetDataAsync(new Dictionary<object, object>
                {
                    ["role"] = "member",
                    ["displayName"] = displayName ?? "",
                    ["joinedAt"] = DateTimeOffset.UtcNow
                }, SetOptions.Merge()).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CloudTeam] Join: {ex.Message}");
            return false;
        }
    }

    public async Task UpdateMemberDisplayNameAsync(string teamId, string displayName)
    {
        var uid = await _auth.EnsureSignedInAsync().ConfigureAwait(false);
        if (uid is null || string.IsNullOrWhiteSpace(teamId) || string.IsNullOrWhiteSpace(displayName))
            return;

        await _db.GetDocument($"teams/{teamId}/members/{uid}")
            .SetDataAsync(new Dictionary<object, object>
            {
                ["displayName"] = displayName.Trim()
            }, SetOptions.Merge()).ConfigureAwait(false);
    }

    public async Task<bool> UpdateInviteCodeAsync(string teamId, string oldCode, string newCode, string teamName)
    {
        var uid = await _auth.EnsureSignedInAsync().ConfigureAwait(false);
        if (uid is null) return false;

        try
        {
            await _db.GetDocument($"teams/{teamId}/metadata/info")
                .SetDataAsync(new Dictionary<object, object>
                {
                    ["inviteCode"] = newCode
                }, SetOptions.Merge()).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(oldCode))
            {
                try { await _db.GetDocument($"invite_codes/{oldCode}").DeleteDocumentAsync().ConfigureAwait(false); }
                catch { /* non-fatal */ }
            }

            await _db.GetDocument($"invite_codes/{newCode}")
                .SetDataAsync(new Dictionary<object, object>
                {
                    ["teamId"] = teamId,
                    ["teamName"] = teamName,
                    ["createdBy"] = uid
                }, SetOptions.Merge()).ConfigureAwait(false);

            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CloudTeam] UpdateInvite: {ex.Message}");
            return false;
        }
    }

    private static string ReadString(IDictionary<object, object> d, string key)
    {
        if (d.TryGetValue(key, out var v) && v != null) return v.ToString() ?? "";
        foreach (var kv in d)
            if (kv.Key?.ToString() == key) return kv.Value?.ToString() ?? "";
        return "";
    }
}
