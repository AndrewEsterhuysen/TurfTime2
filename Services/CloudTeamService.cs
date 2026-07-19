using System.Text.Json;
using Plugin.Firebase.Firestore;
using Plugin.Firebase.Functions;

namespace TurfTime2.Services;

/// <summary>
/// Shared-team create/join/metadata via Plugin.Firebase Firestore (no Firestore REST).
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
            var code = inviteCode.Trim().ToUpperInvariant();
            await _db.GetDocument($"teams/{teamId}/metadata/info")
                .SetDataAsync(new Dictionary<object, object>
                {
                    ["teamName"] = teamName,
                    ["inviteCode"] = code,
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
                await _db.GetDocument($"invite_codes/{code}")
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
            var code = inviteCode.Trim().ToUpperInvariant();
            var snap = await _db.GetDocument($"invite_codes/{code}")
                .GetDocumentSnapshotAsync<Dictionary<object, object>>()
                .ConfigureAwait(false);
            var data = snap?.Data;
            if (data is null) return null;

            var teamId = ReadString(data, "teamId");
            if (string.IsNullOrEmpty(teamId)) return null;

            return new CloudTeamLookup
            {
                TeamId = teamId,
                TeamName = ReadString(data, "teamName")
            };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CloudTeam] LookupInvite: {ex.Message}");
            return null;
        }
    }

    public async Task<string> JoinByInviteCodeAsync(string inviteCode, string displayName)
    {
        var uid = await _auth.EnsureSignedInAsync().ConfigureAwait(false);
        if (uid is null)
            return "error: Could not authenticate with Firebase. Please check your internet connection.";

        try
        {
            var lookup = await LookupInviteCodeAsync(inviteCode).ConfigureAwait(false);
            if (lookup is null || string.IsNullOrEmpty(lookup.TeamId))
                return $"error: Invite code '{inviteCode}' not found. Please check the code and try again.";

            var teamId = lookup.TeamId;
            var teamName = lookup.TeamName;

            var existing = await _db.GetDocument($"teams/{teamId}/members/{uid}")
                .GetDocumentSnapshotAsync<Dictionary<object, object>>()
                .ConfigureAwait(false);
            if (existing?.Data is not null)
                return $"already_member:{teamId}:{teamName}";

            await _db.GetDocument($"teams/{teamId}/members/{uid}")
                .SetDataAsync(new Dictionary<object, object>
                {
                    ["role"] = "member",
                    ["displayName"] = displayName ?? "",
                    ["joinedAt"] = DateTimeOffset.UtcNow
                }, SetOptions.Merge()).ConfigureAwait(false);

            return $"success:{teamId}:{teamName}";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CloudTeam] JoinByInvite: {ex.Message}");
            return $"error: {ex.Message}";
        }
    }

    public async Task<string> RejoinAsAdminAsync(
        string teamId,
        string adminCode,
        string displayName,
        Func<string, string> hashAdminCode)
    {
        var uid = await _auth.EnsureSignedInAsync().ConfigureAwait(false);
        if (uid is null)
            return "error: Could not authenticate with Firebase. Please check your internet connection.";

        try
        {
            var snap = await _db.GetDocument($"teams/{teamId}/metadata/info")
                .GetDocumentSnapshotAsync<Dictionary<object, object>>()
                .ConfigureAwait(false);
            var data = snap?.Data;
            if (data is null)
                return $"error: Team '{teamId}' not found. Check the Team ID and try again.";

            var storedHash = ReadString(data, "adminCodeHash");
            var teamName = ReadString(data, "teamName");
            if (string.IsNullOrEmpty(storedHash))
                return "error: This team does not have an admin recovery code configured.";

            var suppliedHash = hashAdminCode(adminCode.Trim());
            if (!string.Equals(suppliedHash, storedHash, StringComparison.OrdinalIgnoreCase))
                return "error: Invalid admin code. Please check the code and try again.";

            await _db.GetDocument($"teams/{teamId}/members/{uid}")
                .SetDataAsync(new Dictionary<object, object>
                {
                    ["role"] = "admin",
                    ["displayName"] = displayName ?? "",
                    ["rejoinedAt"] = DateTimeOffset.UtcNow
                }, SetOptions.Merge()).ConfigureAwait(false);

            return $"success:{teamId}:{teamName}";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CloudTeam] RejoinAsAdmin: {ex.Message}");
            return $"error: {ex.Message}";
        }
    }

    public async Task<string> UpdateMemberDisplayNameAsync(string teamId, string displayName, string? roleHint = null)
    {
        if (string.IsNullOrWhiteSpace(teamId) || string.IsNullOrWhiteSpace(displayName))
            return "error: Missing team or display name.";

        var uid = await _auth.EnsureSignedInAsync().ConfigureAwait(false);
        if (uid is null)
            return "error: Could not authenticate with Firebase. Please check your internet connection.";

        try
        {
            var name = displayName.Trim();
            if (name.Length > 40) name = name[..40];

            var doc = _db.GetDocument($"teams/{teamId}/members/{uid}");
            var existing = await doc.GetDocumentSnapshotAsync<Dictionary<object, object>>().ConfigureAwait(false);

            if (existing?.Data is not null)
            {
                await doc.SetDataAsync(new Dictionary<object, object>
                {
                    ["displayName"] = name
                }, SetOptions.Merge()).ConfigureAwait(false);
            }
            else
            {
                await doc.SetDataAsync(new Dictionary<object, object>
                {
                    ["role"] = roleHint ?? "member",
                    ["displayName"] = name,
                    ["joinedAt"] = DateTimeOffset.UtcNow
                }, SetOptions.Merge()).ConfigureAwait(false);
            }

            return "success";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CloudTeam] UpdateDisplayName: {ex.Message}");
            return $"error: {ex.Message}";
        }
    }

    public async Task<bool> UpdateInviteCodeAsync(string teamId, string oldCode, string newCode, string teamName)
    {
        var uid = await _auth.EnsureSignedInAsync().ConfigureAwait(false);
        if (uid is null) return false;

        try
        {
            var code = newCode.Trim().ToUpperInvariant();
            await _db.GetDocument($"teams/{teamId}/metadata/info")
                .SetDataAsync(new Dictionary<object, object>
                {
                    ["inviteCode"] = code
                }, SetOptions.Merge()).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(oldCode))
            {
                try
                {
                    await _db.GetDocument($"invite_codes/{oldCode.Trim().ToUpperInvariant()}")
                        .DeleteDocumentAsync().ConfigureAwait(false);
                }
                catch { /* non-fatal */ }
            }

            await _db.GetDocument($"invite_codes/{code}")
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

    public async Task<string> RequestAdminCodeEmailAsync(string teamId)
    {
        if (await _auth.EnsureSignedInAsync().ConfigureAwait(false) is null)
            return "error: Could not authenticate. Please check your internet connection.";

        // Prefer Plugin.Firebase.Functions when the generic CallAsync works; otherwise HTTPS callable.
        try
        {
            var payload = JsonSerializer.Serialize(new { teamId });
            var result = await CrossFirebaseFunctions.Current
                .GetHttpsCallable("requestAdminCodeEmail")
                .CallAsync<Dictionary<string, object>>(payload)
                .ConfigureAwait(false);

            System.Diagnostics.Debug.WriteLine($"[CloudTeam] requestAdminCodeEmail (SDK) → {result}");
            if (result is null)
                return await RequestAdminCodeEmailHttpFallbackAsync(teamId).ConfigureAwait(false);

            string? status = null;
            string? teamName = teamId;
            if (result.TryGetValue("status", out var st)) status = st?.ToString();
            if (result.TryGetValue("teamName", out var tn)) teamName = tn?.ToString() ?? teamId;
            if (status == "not_found") return "not_found";
            return $"success:{teamName}";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CloudTeam] Functions callable failed, HTTP fallback: {ex.Message}");
            return await RequestAdminCodeEmailHttpFallbackAsync(teamId).ConfigureAwait(false);
        }
    }

    private async Task<string> RequestAdminCodeEmailHttpFallbackAsync(string teamId)
    {
        try
        {
            var idToken = await _auth.GetIdTokenAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(idToken))
                return "error: Could not authenticate.";

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", idToken);
            var functionUrl = "https://us-central1-turf-timer.cloudfunctions.net/requestAdminCodeEmail";
            var payload = JsonSerializer.Serialize(new { data = new { teamId } });
            var response = await client.PostAsync(functionUrl,
                new StringContent(payload, System.Text.Encoding.UTF8, "application/json")).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return $"error: Server returned {(int)response.StatusCode}.";

            using var doc = JsonDocument.Parse(body);
            var result = doc.RootElement.GetProperty("result");
            var status = result.TryGetProperty("status", out var statusEl) ? statusEl.GetString() : null;
            if (status == "not_found") return "not_found";
            var teamName = result.TryGetProperty("teamName", out var tnEl) ? tnEl.GetString() ?? teamId : teamId;
            return $"success:{teamName}";
        }
        catch (Exception ex)
        {
            return $"error: {ex.Message}";
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
