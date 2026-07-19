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
            var code = NormalizeInviteCode(inviteCode);
            if (string.IsNullOrEmpty(code))
                return "error: Invalid invite code.";

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

            // Join indexes (invite_codes + optional public/invite). Never fail create if these fail —
            // metadata.inviteCode is already written and join has multiple lookup strategies.
            try
            {
                await UpsertInviteCodeLookupAsync(code, teamId, teamName, uid).ConfigureAwait(false);
            }
            catch (Exception invEx)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[CloudTeam] Invite index write non-fatal: {invEx.Message}");
            }

            // No post-create server re-read. Plugin.Firebase GetDocumentSnapshotAsync(Source.Server)
            // often returns Data=null even after a successful SetDataAsync, which incorrectly failed
            // create after the first SDK pass (that pass returned success after writes only).
            System.Diagnostics.Debug.WriteLine(
                $"[CloudTeam] CreateTeam OK team={teamId} invite={code} uid={uid[..Math.Min(8, uid.Length)]}…");
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

        var code = NormalizeInviteCode(inviteCode);
        if (string.IsNullOrEmpty(code)) return null;
        var compact = CompactInviteCode(code);

        // 1) Root invite_codes/{id} — production rules allow read/create for signed-in users
        try
        {
            foreach (var docId in InviteCodeDocumentIds(code))
            {
                var snap = await _db.GetDocument($"invite_codes/{docId}")
                    .GetDocumentSnapshotAsync<Dictionary<object, object>>()
                    .ConfigureAwait(false);
                var data = snap?.Data;
                if (data is null) continue;
                var teamId = ReadString(data, "teamId");
                if (string.IsNullOrEmpty(teamId)) continue;
                System.Diagnostics.Debug.WriteLine($"[CloudTeam] Lookup hit invite_codes/{docId} → {teamId}");
                return new CloudTeamLookup
                {
                    TeamId = teamId,
                    TeamName = ReadString(data, "teamName")
                };
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CloudTeam] Lookup invite_codes: {ex.Message}");
        }

        // 2) teams/.../public/invite via collection-group (after rules allow public/*)
        try
        {
            foreach (var fieldValue in new[] { code, compact }.Distinct(StringComparer.Ordinal))
            {
                if (string.IsNullOrEmpty(fieldValue)) continue;
                var querySnap = await _db.GetCollectionGroup("public")
                    .WhereEqualsTo("inviteCode", fieldValue)
                    .LimitedTo(5)
                    .GetDocumentsAsync<Dictionary<object, object>>()
                    .ConfigureAwait(false);

                var hit = ExtractLookupFromQuery(querySnap, preferTeamIdFromPath: true);
                if (hit != null)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[CloudTeam] Lookup hit public CG inviteCode={fieldValue} → {hit.TeamId}");
                    return hit;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CloudTeam] Lookup public CG: {ex.Message}");
        }

        // 3) metadata.inviteCode collection-group
        try
        {
            var querySnap = await _db.GetCollectionGroup("metadata")
                .WhereEqualsTo("inviteCode", code)
                .LimitedTo(5)
                .GetDocumentsAsync<Dictionary<object, object>>()
                .ConfigureAwait(false);

            var hit = ExtractLookupFromQuery(querySnap, preferTeamIdFromPath: true);
            if (hit != null)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[CloudTeam] Lookup hit metadata CG → {hit.TeamId}");
                try
                {
                    await UpsertInviteCodeLookupAsync(code, hit.TeamId, hit.TeamName, _auth.UserId ?? "")
                        .ConfigureAwait(false);
                }
                catch { /* heal best-effort */ }
                return hit;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CloudTeam] Lookup metadata CG: {ex.Message}");
        }

        System.Diagnostics.Debug.WriteLine($"[CloudTeam] Lookup miss for invite code '{code}'");
        return null;
    }

    private static CloudTeamLookup? ExtractLookupFromQuery(
        IQuerySnapshot<Dictionary<object, object>>? querySnap,
        bool preferTeamIdFromPath)
    {
        if (querySnap?.Documents == null) return null;
        foreach (var doc in querySnap.Documents)
        {
            var data = doc.Data;
            if (data is null) continue;

            string? teamId = null;
            if (preferTeamIdFromPath)
            {
                // teams/{teamId}/public/invite  or  teams/{teamId}/metadata/info
                teamId = doc.Reference?.Parent?.Parent?.Id;
            }
            if (string.IsNullOrEmpty(teamId))
                teamId = ReadString(data, "teamId");
            if (string.IsNullOrEmpty(teamId)) continue;

            var teamName = ReadString(data, "teamName");
            return new CloudTeamLookup { TeamId = teamId, TeamName = teamName };
        }
        return null;
    }

    /// <summary>
    /// Writes join indexes. Root <c>invite_codes</c> is allowed by production rules
    /// (create if signed in). <c>teams/.../public/invite</c> is optional until rules include it.
    /// Neither path may fail team create if the other succeeds.
    /// </summary>
    private async Task UpsertInviteCodeLookupAsync(string code, string teamId, string teamName, string createdBy)
    {
        var compact = CompactInviteCode(code);
        var payload = new Dictionary<object, object>
        {
            ["teamId"] = teamId,
            ["teamName"] = teamName ?? "",
            ["inviteCode"] = code,
            ["inviteCodeCompact"] = compact,
            ["createdBy"] = createdBy ?? ""
        };

        var anyOk = false;
        Exception? last = null;

        // 1) Root invite_codes — primary for O(1) join (explicit rules allow create)
        foreach (var docId in InviteCodeDocumentIds(code))
        {
            try
            {
                await _db.GetDocument($"invite_codes/{docId}")
                    .SetDataAsync(payload, SetOptions.Merge())
                    .ConfigureAwait(false);
                System.Diagnostics.Debug.WriteLine($"[CloudTeam] Upserted invite_codes/{docId}");
                anyOk = true;
            }
            catch (Exception ex)
            {
                last = ex;
                System.Diagnostics.Debug.WriteLine($"[CloudTeam] invite_codes/{docId}: {ex.Message}");
            }
        }

        // 2) Under teams/ — optional (needs rules match for public/*)
        try
        {
            await _db.GetDocument($"teams/{teamId}/public/invite")
                .SetDataAsync(payload, SetOptions.Merge())
                .ConfigureAwait(false);
            System.Diagnostics.Debug.WriteLine($"[CloudTeam] Upserted teams/{teamId}/public/invite");
            anyOk = true;
        }
        catch (Exception ex)
        {
            last = ex;
            System.Diagnostics.Debug.WriteLine(
                $"[CloudTeam] teams/.../public/invite skipped: {ex.Message}");
        }

        // Join can still use metadata.inviteCode via collection-group if both index paths fail.
        if (!anyOk && last != null)
            System.Diagnostics.Debug.WriteLine(
                $"[CloudTeam] No invite index written; metadata.inviteCode remains for CG lookup. Last={last.Message}");
    }

    /// <summary>Normalized invite codes: uppercase, keep alphanumerics and single dash form.</summary>
    internal static string NormalizeInviteCode(string? inviteCode)
    {
        if (string.IsNullOrWhiteSpace(inviteCode)) return "";
        // Uppercase, strip spaces; preserve hyphens used in display codes (TQAQ-TN2K).
        var raw = inviteCode.Trim().ToUpperInvariant().Replace(" ", "", StringComparison.Ordinal);
        // Collapse multiple hyphens / surrounding junk
        var chars = raw.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray();
        return new string(chars);
    }

    /// <summary>Alphanumeric-only form of an invite code (TQAQ-TN2K → TQAQTN2K).</summary>
    internal static string CompactInviteCode(string normalizedCode)
        => new string((normalizedCode ?? "").Where(char.IsLetterOrDigit).ToArray());

    /// <summary>Document id variants to write/read for an invite code.</summary>
    private static IEnumerable<string> InviteCodeDocumentIds(string normalizedCode)
    {
        // Prefer compact id first (hyphen-free) — safer as a Firestore document id.
        var compact = CompactInviteCode(normalizedCode);
        if (!string.IsNullOrEmpty(compact))
            yield return compact;
        if (!string.IsNullOrEmpty(normalizedCode) &&
            !string.Equals(compact, normalizedCode, StringComparison.Ordinal))
            yield return normalizedCode;
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
            var code = NormalizeInviteCode(newCode);
            if (string.IsNullOrEmpty(code)) return false;

            await _db.GetDocument($"teams/{teamId}/metadata/info")
                .SetDataAsync(new Dictionary<object, object>
                {
                    ["inviteCode"] = code
                }, SetOptions.Merge()).ConfigureAwait(false);

            // Clear previous public invite pointer (overwrite below) and legacy root docs.
            var oldNorm = NormalizeInviteCode(oldCode);
            if (!string.IsNullOrEmpty(oldNorm))
            {
                foreach (var docId in InviteCodeDocumentIds(oldNorm))
                {
                    try
                    {
                        await _db.GetDocument($"invite_codes/{docId}").DeleteDocumentAsync().ConfigureAwait(false);
                    }
                    catch { /* non-fatal */ }
                }
            }

            await UpsertInviteCodeLookupAsync(code, teamId, teamName, uid).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CloudTeam] UpdateInvite: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Re-publishes invite_codes/{code} for an existing team (self-heal for teams created
    /// before invite lookup writes were required).
    /// </summary>
    public async Task<bool> EnsureInviteCodePublishedAsync(string teamId, string inviteCode, string teamName)
    {
        var uid = await _auth.EnsureSignedInAsync().ConfigureAwait(false);
        if (uid is null) return false;
        var code = NormalizeInviteCode(inviteCode);
        if (string.IsNullOrEmpty(code) || string.IsNullOrWhiteSpace(teamId)) return false;

        try
        {
            await UpsertInviteCodeLookupAsync(code, teamId, teamName ?? "", uid).ConfigureAwait(false);
            // Keep metadata.inviteCode aligned for collection-group fallback.
            await _db.GetDocument($"teams/{teamId}/metadata/info")
                .SetDataAsync(new Dictionary<object, object>
                {
                    ["inviteCode"] = code,
                    ["teamName"] = teamName ?? ""
                }, SetOptions.Merge()).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CloudTeam] EnsureInviteCodePublished: {ex.Message}");
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
