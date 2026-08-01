using System.Text.Json;
using Plugin.Firebase.Firestore;
using Plugin.Firebase.Functions;

namespace TurfTime2.Services;

/// <summary>
/// Shared-team create/join/metadata via Plugin.Firebase Firestore.
/// Invite lookup prefers the SDK; falls back to authenticated Firestore REST if the
/// Plugin.Firebase snapshot cast returns empty (observed with Dictionary reads on device).
/// </summary>
public sealed class CloudTeamService : ICloudTeamService
{
    private const string FirebaseProjectId = "turf-timer";
    private static readonly HttpClient RestHttp = new();

    private readonly IFirebaseAuthService _auth;
    private readonly IFirebaseFirestore _db;

    public CloudTeamService(IFirebaseAuthService auth, IFirebaseFirestore db)
    {
        _auth = auth;
        _db = db;
    }

    /// <summary>
    /// Typed invite_codes document. Plugin.Firebase recommends IFirestoreObject POCOs;
    /// Dictionary&lt;string,object&gt; snapshot.Data has returned null on device even when
    /// the document exists (verified via REST).
    /// </summary>
    private sealed class InviteCodeDoc : IFirestoreObject
    {
        [FirestoreProperty("teamId")]
        public string TeamId { get; set; } = "";

        [FirestoreProperty("teamName")]
        public string TeamName { get; set; } = "";

        [FirestoreProperty("inviteCode")]
        public string InviteCode { get; set; } = "";

        [FirestoreProperty("inviteCodeCompact")]
        public string InviteCodeCompact { get; set; } = "";

        [FirestoreProperty("createdBy")]
        public string CreatedBy { get; set; } = "";
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

            var now = DateTimeOffset.UtcNow;
            await _db.GetDocument($"teams/{teamId}/metadata/info")
                .SetDataAsync(new Dictionary<object, object>
                {
                    ["teamName"] = teamName,
                    ["inviteCode"] = code,
                    ["adminCodeHash"] = adminCodeHash,
                    ["creatorEmail"] = creatorEmail ?? "",
                    ["createdBy"] = uid,
                    ["isActive"] = true,
                    // Used by cleanupDormantTeams Cloud Function (12‑month inactivity purge)
                    ["createdAt"] = now,
                    ["lastActivityUtc"] = now
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

        // 1) Direct doc get via IFirestoreObject POCO (Plugin.Firebase recommended path)
        try
        {
            foreach (var docId in InviteCodeDocumentIds(code))
            {
                var snap = await _db.GetDocument($"invite_codes/{docId}")
                    .GetDocumentSnapshotAsync<InviteCodeDoc>()
                    .ConfigureAwait(false);
                var data = snap?.Data;
                if (data is null || string.IsNullOrEmpty(data.TeamId))
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[CloudTeam] Lookup POCO invite_codes/{docId}: Data null or empty teamId");
                    continue;
                }

                System.Diagnostics.Debug.WriteLine(
                    $"[CloudTeam] Lookup hit (POCO) invite_codes/{docId} → {data.TeamId}");
                return new CloudTeamLookup { TeamId = data.TeamId, TeamName = data.TeamName ?? "" };
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CloudTeam] Lookup POCO invite_codes: {ex.Message}");
        }

        // 2) Dictionary snapshot (secondary SDK path)
        try
        {
            foreach (var docId in InviteCodeDocumentIds(code))
            {
                var snap = await _db.GetDocument($"invite_codes/{docId}")
                    .GetDocumentSnapshotAsync<Dictionary<string, object>>()
                    .ConfigureAwait(false);
                var data = snap?.Data;
                if (data is null) continue;
                var teamId = ReadString(data, "teamId");
                if (string.IsNullOrEmpty(teamId)) continue;
                System.Diagnostics.Debug.WriteLine($"[CloudTeam] Lookup hit (dict) invite_codes/{docId} → {teamId}");
                return new CloudTeamLookup
                {
                    TeamId = teamId,
                    TeamName = ReadString(data, "teamName")
                };
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CloudTeam] Lookup dict invite_codes: {ex.Message}");
        }

        // 3) Collection query on invite_codes by field (works even if doc id form differs)
        try
        {
            foreach (var (field, value) in new[]
                     {
                         ("inviteCode", code),
                         ("inviteCodeCompact", compact),
                         ("inviteCode", compact)
                     })
            {
                if (string.IsNullOrEmpty(value)) continue;
                var querySnap = await _db.GetCollection("invite_codes")
                    .WhereEqualsTo(field, value)
                    .LimitedTo(5)
                    .GetDocumentsAsync<InviteCodeDoc>()
                    .ConfigureAwait(false);

                var hit = ExtractLookupFromInviteQuery(querySnap);
                if (hit != null)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[CloudTeam] Lookup hit invite_codes query {field}={value} → {hit.TeamId}");
                    return hit;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CloudTeam] Lookup invite_codes query: {ex.Message}");
        }

        // 4) teams/.../public/invite via collection-group
        try
        {
            foreach (var fieldValue in new[] { code, compact }.Distinct(StringComparer.Ordinal))
            {
                if (string.IsNullOrEmpty(fieldValue)) continue;
                var querySnap = await _db.GetCollectionGroup("public")
                    .WhereEqualsTo("inviteCode", fieldValue)
                    .LimitedTo(5)
                    .GetDocumentsAsync<Dictionary<string, object>>()
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

        // 5) metadata.inviteCode collection-group
        try
        {
            var querySnap = await _db.GetCollectionGroup("metadata")
                .WhereEqualsTo("inviteCode", code)
                .LimitedTo(5)
                .GetDocumentsAsync<Dictionary<string, object>>()
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

        // 6) Authenticated Firestore REST — proven path when SDK Data is empty but docs exist
        try
        {
            var restHit = await LookupInviteCodeViaRestAsync(code).ConfigureAwait(false);
            if (restHit != null)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[CloudTeam] Lookup hit REST invite → {restHit.TeamId}");
                return restHit;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CloudTeam] Lookup REST: {ex.Message}");
        }

        System.Diagnostics.Debug.WriteLine($"[CloudTeam] Lookup miss for invite code '{code}'");
        return null;
    }

    private async Task<CloudTeamLookup?> LookupInviteCodeViaRestAsync(string normalizedCode)
    {
        var idToken = await _auth.GetIdTokenAsync(forceRefresh: true).ConfigureAwait(false);
        if (string.IsNullOrEmpty(idToken))
        {
            System.Diagnostics.Debug.WriteLine("[CloudTeam] REST lookup: no id token");
            return null;
        }

        foreach (var docId in InviteCodeDocumentIds(normalizedCode))
        {
            // Hyphenated ids are valid; EscapeDataString is safe for both forms.
            var url =
                $"https://firestore.googleapis.com/v1/projects/{FirebaseProjectId}/databases/(default)/documents/invite_codes/{Uri.EscapeDataString(docId)}";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", idToken);

            using var resp = await RestHttp.SendAsync(req).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                System.Diagnostics.Debug.WriteLine($"[CloudTeam] REST invite_codes/{docId}: 404");
                continue;
            }

            if (!resp.IsSuccessStatusCode)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[CloudTeam] REST invite_codes/{docId}: {(int)resp.StatusCode} {body[..Math.Min(200, body.Length)]}");
                continue;
            }

            using var json = JsonDocument.Parse(body);
            if (!json.RootElement.TryGetProperty("fields", out var fields))
                continue;

            var teamId = ReadFirestoreRestString(fields, "teamId");
            if (string.IsNullOrEmpty(teamId)) continue;
            var teamName = ReadFirestoreRestString(fields, "teamName");
            return new CloudTeamLookup { TeamId = teamId, TeamName = teamName };
        }

        return null;
    }

    private static string ReadFirestoreRestString(JsonElement fields, string fieldName)
    {
        if (!fields.TryGetProperty(fieldName, out var field)) return "";
        if (field.TryGetProperty("stringValue", out var sv))
            return sv.GetString() ?? "";
        return "";
    }

    private static CloudTeamLookup? ExtractLookupFromInviteQuery(IQuerySnapshot<InviteCodeDoc>? querySnap)
    {
        if (querySnap?.Documents == null) return null;
        foreach (var doc in querySnap.Documents)
        {
            var data = doc.Data;
            if (data is null || string.IsNullOrEmpty(data.TeamId)) continue;
            return new CloudTeamLookup { TeamId = data.TeamId, TeamName = data.TeamName ?? "" };
        }
        return null;
    }

    private static CloudTeamLookup? ExtractLookupFromQuery(
        IQuerySnapshot<Dictionary<string, object>>? querySnap,
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
                .GetDocumentSnapshotAsync<Dictionary<string, object>>()
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
                .GetDocumentSnapshotAsync<Dictionary<string, object>>()
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
            var existing = await doc.GetDocumentSnapshotAsync<Dictionary<string, object>>().ConfigureAwait(false);

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
                .GetHttpsCallable("requestAdminRecoveryEmail")
                .CallAsync<Dictionary<string, object>>(payload)
                .ConfigureAwait(false);

            System.Diagnostics.Debug.WriteLine($"[CloudTeam] requestAdminRecoveryEmail (SDK) → {result}");
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
            var functionUrl = "https://us-central1-turf-timer.cloudfunctions.net/requestAdminRecoveryEmail";
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

    public async Task<bool> IsTeamOwnerAsync(string teamId)
    {
        if (string.IsNullOrWhiteSpace(teamId)) return false;
        var uid = await _auth.EnsureSignedInAsync().ConfigureAwait(false);
        if (uid is null) return false;
        try
        {
            var ownerUid = await GetTeamOwnerUidAsync(teamId).ConfigureAwait(false);
            return !string.IsNullOrEmpty(ownerUid)
                && string.Equals(ownerUid, uid, StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CloudTeam] IsTeamOwner: {ex.Message}");
            return false;
        }
    }

    public async Task<string?> GetTeamOwnerUidAsync(string teamId)
    {
        if (string.IsNullOrWhiteSpace(teamId)) return null;
        try
        {
            var snap = await _db.GetDocument($"teams/{teamId}/metadata/info")
                .GetDocumentSnapshotAsync<Dictionary<string, object>>()
                .ConfigureAwait(false);
            var createdBy = ReadString(snap?.Data, "createdBy");
            if (!string.IsNullOrWhiteSpace(createdBy))
                return createdBy.Trim();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CloudTeam] GetTeamOwnerUid SDK: {ex.Message}");
        }

        // REST fallback when SDK Data is empty or missing createdBy
        try
        {
            return await GetTeamOwnerUidViaRestAsync(teamId).ConfigureAwait(false);
        }
        catch (Exception restEx)
        {
            System.Diagnostics.Debug.WriteLine($"[CloudTeam] GetTeamOwnerUid REST: {restEx.Message}");
            return null;
        }
    }

    private async Task<string?> GetTeamOwnerUidViaRestAsync(string teamId)
    {
        var meta = await GetTeamMetadataViaRestAsync(teamId).ConfigureAwait(false);
        return meta?.CreatedBy;
    }

    private sealed class TeamMetadataRest
    {
        public string CreatedBy { get; init; } = "";
        public string InviteCode { get; init; } = "";
        public string TeamName { get; init; } = "";
    }

    private async Task<TeamMetadataRest?> GetTeamMetadataViaRestAsync(string teamId)
    {
        var idToken = await _auth.GetIdTokenAsync(forceRefresh: false).ConfigureAwait(false);
        if (string.IsNullOrEmpty(idToken))
            idToken = await _auth.GetIdTokenAsync(forceRefresh: true).ConfigureAwait(false);
        if (string.IsNullOrEmpty(idToken)) return null;

        var url =
            $"https://firestore.googleapis.com/v1/projects/{FirebaseProjectId}/databases/(default)/documents/teams/{Uri.EscapeDataString(teamId)}/metadata/info";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", idToken);
        using var resp = await RestHttp.SendAsync(req).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        if (!resp.IsSuccessStatusCode) return null;
        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var json = JsonDocument.Parse(body);
        if (!json.RootElement.TryGetProperty("fields", out var fields)) return null;
        var createdBy = ReadFirestoreRestString(fields, "createdBy").Trim();
        var invite = ReadFirestoreRestString(fields, "inviteCode").Trim();
        var teamName = ReadFirestoreRestString(fields, "teamName").Trim();
        return new TeamMetadataRest
        {
            CreatedBy = createdBy,
            InviteCode = invite,
            TeamName = teamName
        };
    }

    public async Task<IReadOnlyList<CloudTeamMember>> ListMembersAsync(string teamId)
    {
        var list = new List<CloudTeamMember>();
        if (string.IsNullOrWhiteSpace(teamId)) return list;
        if (await _auth.EnsureSignedInAsync().ConfigureAwait(false) is null) return list;

        // REST is primary — Plugin.Firebase Dictionary snapshot.Data is often empty/unreadable
        // on device (same class of bug as chat/roster), which produced truncated UIDs as "names".
        try
        {
            list = await ListMembersViaRestAsync(teamId).ConfigureAwait(false);
            System.Diagnostics.Debug.WriteLine(
                $"[CloudTeam] ListMembers REST team={teamId} count={list.Count}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CloudTeam] ListMembers REST: {ex.Message}");
        }

        if (list.Count == 0)
        {
            try
            {
                list = await ListMembersViaSdkAsync(teamId).ConfigureAwait(false);
                System.Diagnostics.Debug.WriteLine(
                    $"[CloudTeam] ListMembers SDK team={teamId} count={list.Count}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CloudTeam] ListMembers SDK: {ex.Message}");
            }
        }

        // Prefer chat display names when member.displayName is missing (older joins / empty profile).
        if (list.Count > 0 && list.Any(m => LooksLikeMissingDisplayName(m.DisplayName, m.Uid)))
        {
            try
            {
                await EnrichMemberNamesFromChatAsync(teamId, list).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[CloudTeam] EnrichMemberNamesFromChat: {ex.Message}");
            }
        }

        // Final fallback for any still-empty labels
        for (var i = 0; i < list.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(list[i].DisplayName)) continue;
            var id = list[i].Uid;
            list[i] = new CloudTeamMember
            {
                Uid = id,
                DisplayName = id.Length > 8 ? id[..8] + "…" : id,
                Role = list[i].Role
            };
        }

        return list
            .OrderByDescending(m => m.IsAdmin)
            .ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<List<CloudTeamMember>> ListMembersViaRestAsync(string teamId)
    {
        var list = new List<CloudTeamMember>();
        var idToken = await _auth.GetIdTokenAsync(forceRefresh: false).ConfigureAwait(false);
        if (string.IsNullOrEmpty(idToken))
            idToken = await _auth.GetIdTokenAsync(forceRefresh: true).ConfigureAwait(false);
        if (string.IsNullOrEmpty(idToken))
            throw new InvalidOperationException("No Firebase id token");

        var url =
            $"https://firestore.googleapis.com/v1/projects/{FirebaseProjectId}/databases/(default)/documents/teams/{Uri.EscapeDataString(teamId)}/members?pageSize=100";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", idToken);

        using var resp = await RestHttp.SendAsync(req).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            return list;
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"REST list members {(int)resp.StatusCode}: {body[..Math.Min(200, body.Length)]}");
        }

        using var json = JsonDocument.Parse(body);
        if (!json.RootElement.TryGetProperty("documents", out var documents))
            return list;

        foreach (var doc in documents.EnumerateArray())
        {
            var namePath = doc.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var uid = namePath.Contains('/')
                ? namePath[(namePath.LastIndexOf('/') + 1)..]
                : namePath;
            if (string.IsNullOrEmpty(uid)) continue;

            var role = "member";
            var displayName = "";
            if (doc.TryGetProperty("fields", out var fields))
            {
                role = ReadFirestoreRestString(fields, "role");
                if (string.IsNullOrWhiteSpace(role)) role = "member";
                displayName = ReadFirestoreRestString(fields, "displayName").Trim();
            }

            list.Add(new CloudTeamMember
            {
                Uid = uid,
                DisplayName = displayName,
                Role = role.Trim().ToLowerInvariant()
            });
        }

        return list;
    }

    private async Task<List<CloudTeamMember>> ListMembersViaSdkAsync(string teamId)
    {
        var list = new List<CloudTeamMember>();
        var snap = await _db.GetCollection($"teams/{teamId}/members")
            .GetDocumentsAsync<Dictionary<string, object>>()
            .ConfigureAwait(false);
        if (snap?.Documents is null) return list;

        foreach (var doc in snap.Documents)
        {
            var id = doc.Reference?.Id;
            if (string.IsNullOrEmpty(id) || doc.Data is null) continue;
            var role = CoerceString(GetField(doc.Data, "role"));
            if (string.IsNullOrWhiteSpace(role)) role = "member";
            var name = CoerceString(GetField(doc.Data, "displayName")).Trim();
            list.Add(new CloudTeamMember
            {
                Uid = id,
                DisplayName = name,
                Role = role.Trim().ToLowerInvariant()
            });
        }

        return list;
    }

    /// <summary>
    /// Fills blank member names from the latest chat <c>senderName</c> for each uid
    /// (same names shown in Chat).
    /// </summary>
    private async Task EnrichMemberNamesFromChatAsync(string teamId, List<CloudTeamMember> members)
    {
        var idToken = await _auth.GetIdTokenAsync(forceRefresh: false).ConfigureAwait(false);
        if (string.IsNullOrEmpty(idToken))
            idToken = await _auth.GetIdTokenAsync(forceRefresh: true).ConfigureAwait(false);
        if (string.IsNullOrEmpty(idToken)) return;

        // pageSize keeps this light; newest-first so we keep the latest name per uid.
        var url =
            $"https://firestore.googleapis.com/v1/projects/{FirebaseProjectId}/databases/(default)/documents/teams/{Uri.EscapeDataString(teamId)}/messages?pageSize=100&orderBy=timestamp%20desc";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", idToken);

        using var resp = await RestHttp.SendAsync(req).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return;

        using var json = JsonDocument.Parse(body);
        if (!json.RootElement.TryGetProperty("documents", out var documents))
            return;

        var namesByUid = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var doc in documents.EnumerateArray())
        {
            if (!doc.TryGetProperty("fields", out var fields)) continue;
            var userId = ReadFirestoreRestString(fields, "userId");
            var senderName = ReadFirestoreRestString(fields, "senderName").Trim();
            if (string.IsNullOrEmpty(userId) || string.IsNullOrWhiteSpace(senderName))
                continue;
            // First hit is newest (orderBy timestamp desc)
            if (!namesByUid.ContainsKey(userId))
                namesByUid[userId] = senderName;
        }

        if (namesByUid.Count == 0) return;

        for (var i = 0; i < members.Count; i++)
        {
            var m = members[i];
            if (!LooksLikeMissingDisplayName(m.DisplayName, m.Uid)) continue;
            if (!namesByUid.TryGetValue(m.Uid, out var chatName) || string.IsNullOrWhiteSpace(chatName))
                continue;
            members[i] = new CloudTeamMember
            {
                Uid = m.Uid,
                DisplayName = chatName.Trim(),
                Role = m.Role
            };
        }
    }

    private static bool LooksLikeMissingDisplayName(string? name, string uid)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        var n = name.Trim();
        // Truncated uid fallback from older builds
        if (n.EndsWith('…') && uid.StartsWith(n.TrimEnd('…'), StringComparison.Ordinal))
            return true;
        if (string.Equals(n, uid, StringComparison.Ordinal)) return true;
        if (n.StartsWith("System.", StringComparison.Ordinal) || n.Contains("Dictionary", StringComparison.Ordinal))
            return true;
        return false;
    }

    public async Task<string> TransferOwnershipAsync(string teamId, string newOwnerUid)
    {
        if (string.IsNullOrWhiteSpace(teamId) || string.IsNullOrWhiteSpace(newOwnerUid))
            return "error: Missing team or new owner.";

        var uid = await _auth.EnsureSignedInAsync().ConfigureAwait(false);
        if (uid is null)
            return "error: Could not authenticate with Firebase. Please check your internet connection.";

        if (string.Equals(uid, newOwnerUid, StringComparison.Ordinal))
            return "error: You already own this team.";

        try
        {
            // REST-backed owner check (SDK metadata often omits createdBy).
            var ownerUid = await GetTeamOwnerUidAsync(teamId).ConfigureAwait(false);
            if (string.IsNullOrEmpty(ownerUid) || !string.Equals(ownerUid, uid, StringComparison.Ordinal))
                return "error: not_owner";

            var restMeta = await GetTeamMetadataViaRestAsync(teamId).ConfigureAwait(false);
            var teamName = restMeta?.TeamName ?? "";
            var inviteCode = NormalizeInviteCode(restMeta?.InviteCode ?? "");

            try
            {
                var metaSnap = await _db.GetDocument($"teams/{teamId}/metadata/info")
                    .GetDocumentSnapshotAsync<Dictionary<string, object>>()
                    .ConfigureAwait(false);
                if (metaSnap?.Data is not null)
                {
                    if (string.IsNullOrWhiteSpace(teamName))
                        teamName = ReadString(metaSnap.Data, "teamName");
                    if (string.IsNullOrEmpty(inviteCode))
                        inviteCode = NormalizeInviteCode(ReadString(metaSnap.Data, "inviteCode"));
                }
            }
            catch { /* optional fill-in */ }

            if (string.IsNullOrEmpty(inviteCode))
                inviteCode = NormalizeInviteCode(Preferences.Get($"{teamId}_invite_code", string.Empty));

            var targetRole = await GetMemberRoleViaRestAsync(teamId, newOwnerUid).ConfigureAwait(false);
            if (targetRole is null)
            {
                var memberSnap = await _db.GetDocument($"teams/{teamId}/members/{newOwnerUid}")
                    .GetDocumentSnapshotAsync<Dictionary<string, object>>()
                    .ConfigureAwait(false);
                if (memberSnap?.Data is null)
                    return "error: That person is not a member of this team.";
                targetRole = ReadString(memberSnap.Data, "role");
                if (string.IsNullOrWhiteSpace(targetRole)) targetRole = "member";
            }

            if (!string.Equals(targetRole, "admin", StringComparison.OrdinalIgnoreCase))
                return "error: Ownership can only transfer to an Admin. Promote them to Admin first, then transfer.";

            await _db.GetDocument($"teams/{teamId}/metadata/info")
                .SetDataAsync(new Dictionary<object, object>
                {
                    ["createdBy"] = newOwnerUid,
                    ["lastActivityUtc"] = DateTimeOffset.UtcNow,
                    ["ownershipTransferredAt"] = DateTimeOffset.UtcNow,
                    ["previousOwnerUid"] = uid
                }, SetOptions.Merge()).ConfigureAwait(false);

            // Retarget invite_codes ownership so new owner can regenerate/delete codes
            if (!string.IsNullOrEmpty(inviteCode))
            {
                try
                {
                    await UpsertInviteCodeLookupAsync(inviteCode, teamId, teamName, newOwnerUid)
                        .ConfigureAwait(false);
                }
                catch (Exception invEx)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[CloudTeam] Transfer invite rebind non-fatal: {invEx.Message}");
                }
            }

            System.Diagnostics.Debug.WriteLine(
                $"[CloudTeam] Ownership transferred team={teamId} from {uid[..Math.Min(6, uid.Length)]}… to {newOwnerUid[..Math.Min(6, newOwnerUid.Length)]}…");
            return "success";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CloudTeam] TransferOwnership: {ex.Message}");
            return $"error: {ex.Message}";
        }
    }

    public async Task<string?> GetMyRoleAsync(string teamId)
    {
        if (string.IsNullOrWhiteSpace(teamId)) return null;
        var uid = await _auth.EnsureSignedInAsync().ConfigureAwait(false);
        if (uid is null) return null;
        return await GetMemberRoleViaRestAsync(teamId, uid).ConfigureAwait(false);
    }

    public async Task<string> PromoteMemberToAdminAsync(string teamId, string memberUid)
    {
        if (string.IsNullOrWhiteSpace(teamId) || string.IsNullOrWhiteSpace(memberUid))
            return "error: Missing team or member.";

        var uid = await _auth.EnsureSignedInAsync().ConfigureAwait(false);
        if (uid is null)
            return "error: Could not authenticate with Firebase. Please check your internet connection.";

        if (string.Equals(uid, memberUid, StringComparison.Ordinal))
            return "error: You are already an Admin.";

        try
        {
            // Caller must be an admin on this team
            var selfSnap = await _db.GetDocument($"teams/{teamId}/members/{uid}")
                .GetDocumentSnapshotAsync<Dictionary<string, object>>()
                .ConfigureAwait(false);
            var selfRole = ReadString(selfSnap?.Data, "role");
            if (!string.Equals(selfRole, "admin", StringComparison.OrdinalIgnoreCase))
            {
                // REST fallback if SDK Data empty
                var restSelf = await GetMemberRoleViaRestAsync(teamId, uid).ConfigureAwait(false);
                if (!string.Equals(restSelf, "admin", StringComparison.OrdinalIgnoreCase))
                    return "error: Only Admins can promote members.";
            }

            var targetRole = await GetMemberRoleViaRestAsync(teamId, memberUid).ConfigureAwait(false);
            if (targetRole is null)
            {
                // Try SDK existence check
                var targetSnap = await _db.GetDocument($"teams/{teamId}/members/{memberUid}")
                    .GetDocumentSnapshotAsync<Dictionary<string, object>>()
                    .ConfigureAwait(false);
                if (targetSnap?.Data is null)
                    return "error: That person is not a member of this team.";
                targetRole = ReadString(targetSnap.Data, "role");
                if (string.IsNullOrWhiteSpace(targetRole)) targetRole = "member";
            }

            if (string.Equals(targetRole, "admin", StringComparison.OrdinalIgnoreCase))
                return "error: That person is already an Admin.";

            // REST patch first (reliable on device), then verify
            Exception? lastWriteError = null;
            try
            {
                await PatchMemberRoleViaRestAsync(teamId, memberUid, "admin").ConfigureAwait(false);
            }
            catch (Exception restEx)
            {
                lastWriteError = restEx;
                System.Diagnostics.Debug.WriteLine(
                    $"[CloudTeam] Promote REST failed, trying SDK: {restEx.Message}");
            }

            var verified = await GetMemberRoleViaRestAsync(teamId, memberUid).ConfigureAwait(false);
            if (!string.Equals(verified, "admin", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await _db.GetDocument($"teams/{teamId}/members/{memberUid}")
                        .SetDataAsync(new Dictionary<object, object>
                        {
                            ["role"] = "admin",
                            ["promotedAt"] = DateTimeOffset.UtcNow,
                            ["promotedBy"] = uid
                        }, SetOptions.Merge()).ConfigureAwait(false);
                }
                catch (Exception sdkEx)
                {
                    lastWriteError = sdkEx;
                    System.Diagnostics.Debug.WriteLine(
                        $"[CloudTeam] Promote SDK failed: {sdkEx.Message}");
                }

                verified = await GetMemberRoleViaRestAsync(teamId, memberUid).ConfigureAwait(false);
            }

            if (!string.Equals(verified, "admin", StringComparison.OrdinalIgnoreCase))
            {
                var detail = lastWriteError?.Message ?? "role still not admin after write";
                return $"error: Promote did not stick in the cloud ({detail}). Check network and try again.";
            }

            System.Diagnostics.Debug.WriteLine(
                $"[CloudTeam] Promoted member {memberUid[..Math.Min(6, memberUid.Length)]}… to admin on {teamId}");
            return "success";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CloudTeam] PromoteMember: {ex.Message}");
            return $"error: {ex.Message}";
        }
    }

    private async Task<string?> GetMemberRoleViaRestAsync(string teamId, string memberUid)
    {
        try
        {
            var idToken = await _auth.GetIdTokenAsync(forceRefresh: false).ConfigureAwait(false);
            if (string.IsNullOrEmpty(idToken))
                idToken = await _auth.GetIdTokenAsync(forceRefresh: true).ConfigureAwait(false);
            if (string.IsNullOrEmpty(idToken)) return null;

            var url =
                $"https://firestore.googleapis.com/v1/projects/{FirebaseProjectId}/databases/(default)/documents/teams/{Uri.EscapeDataString(teamId)}/members/{Uri.EscapeDataString(memberUid)}";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", idToken);

            using var resp = await RestHttp.SendAsync(req).ConfigureAwait(false);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null;
            if (!resp.IsSuccessStatusCode)
                return null;

            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var json = JsonDocument.Parse(body);
            if (!json.RootElement.TryGetProperty("fields", out var fields))
                return "member";
            var role = ReadFirestoreRestString(fields, "role");
            return string.IsNullOrWhiteSpace(role) ? "member" : role.Trim().ToLowerInvariant();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CloudTeam] GetMemberRoleViaRest: {ex.Message}");
            return null;
        }
    }

    private async Task PatchMemberRoleViaRestAsync(string teamId, string memberUid, string role)
    {
        var idToken = await _auth.GetIdTokenAsync(forceRefresh: false).ConfigureAwait(false);
        if (string.IsNullOrEmpty(idToken))
            idToken = await _auth.GetIdTokenAsync(forceRefresh: true).ConfigureAwait(false);
        if (string.IsNullOrEmpty(idToken))
            throw new InvalidOperationException("No Firebase id token");

        var fields = new Dictionary<string, object>
        {
            ["role"] = new Dictionary<string, object> { ["stringValue"] = role },
            ["promotedAt"] = new Dictionary<string, object>
            {
                ["stringValue"] = DateTimeOffset.UtcNow.ToString("o")
            }
        };

        var fieldPaths = string.Join("&",
            fields.Keys.Select(k => $"updateMask.fieldPaths={Uri.EscapeDataString(k)}"));
        var url =
            $"https://firestore.googleapis.com/v1/projects/{FirebaseProjectId}/databases/(default)/documents/teams/{Uri.EscapeDataString(teamId)}/members/{Uri.EscapeDataString(memberUid)}?{fieldPaths}";

        var json = JsonSerializer.Serialize(new { fields });
        using var req = new HttpRequestMessage(HttpMethod.Patch, url)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", idToken);

        using var resp = await RestHttp.SendAsync(req).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"REST promote {(int)resp.StatusCode}: {body[..Math.Min(200, body.Length)]}");
        }
    }

    public async Task<string> RemoveMemberAsync(string teamId, string memberUid)
    {
        if (string.IsNullOrWhiteSpace(teamId) || string.IsNullOrWhiteSpace(memberUid))
            return "error: Missing team or member.";

        var uid = await _auth.EnsureSignedInAsync().ConfigureAwait(false);
        if (uid is null)
            return "error: Could not authenticate with Firebase. Please check your internet connection.";

        if (string.Equals(uid, memberUid, StringComparison.Ordinal))
            return "error: You cannot remove yourself. Use Leave Team instead.";

        try
        {
            // Caller must be an admin
            var selfRole = await GetMemberRoleViaRestAsync(teamId, uid).ConfigureAwait(false);
            if (!string.Equals(selfRole, "admin", StringComparison.OrdinalIgnoreCase))
            {
                var selfSnap = await _db.GetDocument($"teams/{teamId}/members/{uid}")
                    .GetDocumentSnapshotAsync<Dictionary<string, object>>()
                    .ConfigureAwait(false);
                selfRole = ReadString(selfSnap?.Data, "role");
                if (!string.Equals(selfRole, "admin", StringComparison.OrdinalIgnoreCase))
                    return "error: Only Admins can remove members.";
            }

            var targetRole = await GetMemberRoleViaRestAsync(teamId, memberUid).ConfigureAwait(false);
            if (targetRole is null)
            {
                var targetSnap = await _db.GetDocument($"teams/{teamId}/members/{memberUid}")
                    .GetDocumentSnapshotAsync<Dictionary<string, object>>()
                    .ConfigureAwait(false);
                if (targetSnap?.Data is null)
                    return "error: That person is not a member of this team.";
                targetRole = ReadString(targetSnap.Data, "role");
                if (string.IsNullOrWhiteSpace(targetRole)) targetRole = "member";
            }

            // Owner from metadata.createdBy — MUST use REST-backed helper. SDK Data often
            // omits createdBy, which previously made every Admin removal fail with
            // "Only the team Owner can remove another Admin" even for the real owner.
            var ownerUid = await GetTeamOwnerUidAsync(teamId).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(ownerUid)
                && string.Equals(ownerUid, memberUid, StringComparison.Ordinal))
            {
                return "error: Cannot remove the team Owner. Transfer ownership first, then remove them.";
            }

            // Only the owner may remove another Admin (co-admins can remove Members only).
            var iAmOwner = !string.IsNullOrEmpty(ownerUid)
                && string.Equals(ownerUid, uid, StringComparison.Ordinal);
            if (string.Equals(targetRole, "admin", StringComparison.OrdinalIgnoreCase) && !iAmOwner)
            {
                if (!string.IsNullOrEmpty(ownerUid))
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[CloudTeam] RemoveMember denied: caller={uid[..Math.Min(8, uid.Length)]}… " +
                        $"owner={ownerUid[..Math.Min(8, ownerUid.Length)]}… targetAdmin");
                    return "error: Only the team Owner can remove another Admin. " +
                           "If you created this team, open View Team Members and confirm you show as (Owner). " +
                           "A reinstall can create a new device identity — use Transfer Ownership from the original Owner device, or contact support.";
                }

                // Owner field unreadable: allow any Admin to remove co-admins so orphaned
                // admin docs are not permanently marooned.
                System.Diagnostics.Debug.WriteLine(
                    "[CloudTeam] Owner unresolved — allowing Admin to remove co-admin");
            }

            Exception? lastError = null;
            try
            {
                await DeleteMemberViaRestAsync(teamId, memberUid).ConfigureAwait(false);
            }
            catch (Exception restEx)
            {
                lastError = restEx;
                System.Diagnostics.Debug.WriteLine(
                    $"[CloudTeam] RemoveMember REST failed, trying SDK: {restEx.Message}");
                try
                {
                    await _db.GetDocument($"teams/{teamId}/members/{memberUid}")
                        .DeleteDocumentAsync()
                        .ConfigureAwait(false);
                    lastError = null;
                }
                catch (Exception sdkEx)
                {
                    lastError = sdkEx;
                    System.Diagnostics.Debug.WriteLine(
                        $"[CloudTeam] RemoveMember SDK failed: {sdkEx.Message}");
                }
            }

            // Verify gone
            var stillThere = await GetMemberRoleViaRestAsync(teamId, memberUid).ConfigureAwait(false);
            if (stillThere is not null)
            {
                var detail = lastError?.Message ?? "member doc still present after delete";
                return $"error: Remove did not stick in the cloud ({detail}). Check network and try again.";
            }

            // If they held match control, clear the vacant lock so another Admin can take over.
            try
            {
                await ClearControllerIfUidAsync(teamId, memberUid).ConfigureAwait(false);
            }
            catch (Exception ctrlEx)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[CloudTeam] RemoveMember controller clear non-fatal: {ctrlEx.Message}");
            }

            System.Diagnostics.Debug.WriteLine(
                $"[CloudTeam] Removed member {memberUid[..Math.Min(6, memberUid.Length)]}… from {teamId}");
            return "success";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CloudTeam] RemoveMember: {ex.Message}");
            return $"error: {ex.Message}";
        }
    }

    private async Task DeleteMemberViaRestAsync(string teamId, string memberUid)
    {
        var idToken = await _auth.GetIdTokenAsync(forceRefresh: false).ConfigureAwait(false);
        if (string.IsNullOrEmpty(idToken))
            idToken = await _auth.GetIdTokenAsync(forceRefresh: true).ConfigureAwait(false);
        if (string.IsNullOrEmpty(idToken))
            throw new InvalidOperationException("No Firebase id token");

        var url =
            $"https://firestore.googleapis.com/v1/projects/{FirebaseProjectId}/databases/(default)/documents/teams/{Uri.EscapeDataString(teamId)}/members/{Uri.EscapeDataString(memberUid)}";

        using var req = new HttpRequestMessage(HttpMethod.Delete, url);
        req.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", idToken);

        using var resp = await RestHttp.SendAsync(req).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        // 404 = already gone → treat as success
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            return;
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"REST remove member {(int)resp.StatusCode}: {body[..Math.Min(200, body.Length)]}");
        }
    }

    /// <summary>
    /// If the removed user was the live match controller, clear control fields so the seat is vacant.
    /// </summary>
    private async Task ClearControllerIfUidAsync(string teamId, string memberUid)
    {
        var idToken = await _auth.GetIdTokenAsync(forceRefresh: false).ConfigureAwait(false);
        if (string.IsNullOrEmpty(idToken))
            idToken = await _auth.GetIdTokenAsync(forceRefresh: true).ConfigureAwait(false);
        if (string.IsNullOrEmpty(idToken)) return;

        var getUrl =
            $"https://firestore.googleapis.com/v1/projects/{FirebaseProjectId}/databases/(default)/documents/teams/{Uri.EscapeDataString(teamId)}/roster/data";
        using (var getReq = new HttpRequestMessage(HttpMethod.Get, getUrl))
        {
            getReq.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", idToken);
            using var getResp = await RestHttp.SendAsync(getReq).ConfigureAwait(false);
            if (!getResp.IsSuccessStatusCode) return;
            var getBody = await getResp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var json = JsonDocument.Parse(getBody);
            if (!json.RootElement.TryGetProperty("fields", out var fields)) return;
            var controllerUid = ReadFirestoreRestString(fields, "controllerUid");
            if (string.IsNullOrEmpty(controllerUid)
                || !string.Equals(controllerUid, memberUid, StringComparison.Ordinal))
                return;
        }

        var fieldsPatch = new Dictionary<string, object>
        {
            ["controllerUid"] = new Dictionary<string, object> { ["stringValue"] = "" },
            ["controllerDisplayName"] = new Dictionary<string, object> { ["stringValue"] = "" },
            ["controlRequestUid"] = new Dictionary<string, object> { ["stringValue"] = "" },
            ["controlRequestDisplayName"] = new Dictionary<string, object> { ["stringValue"] = "" },
            ["controlRequestId"] = new Dictionary<string, object> { ["stringValue"] = "" },
            ["controllerHeartbeatUtc"] = new Dictionary<string, object>
            {
                ["timestampValue"] = "1970-01-01T00:00:00.000Z"
            }
        };
        var fieldPaths = string.Join("&",
            fieldsPatch.Keys.Select(k => $"updateMask.fieldPaths={Uri.EscapeDataString(k)}"));
        var patchUrl =
            $"https://firestore.googleapis.com/v1/projects/{FirebaseProjectId}/databases/(default)/documents/teams/{Uri.EscapeDataString(teamId)}/roster/data?{fieldPaths}";
        var payload = JsonSerializer.Serialize(new { fields = fieldsPatch });
        using var patchReq = new HttpRequestMessage(HttpMethod.Patch, patchUrl)
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json")
        };
        patchReq.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", idToken);
        using var patchResp = await RestHttp.SendAsync(patchReq).ConfigureAwait(false);
        if (patchResp.IsSuccessStatusCode)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[CloudTeam] Cleared match controller after removing {memberUid[..Math.Min(6, memberUid.Length)]}…");
        }
    }

    public async Task<string> DeleteTeamAsOwnerAsync(string teamId)
    {
        if (string.IsNullOrWhiteSpace(teamId))
            return "error: Missing team id.";

        var uid = await _auth.EnsureSignedInAsync().ConfigureAwait(false);
        if (uid is null)
            return "error: Could not authenticate with Firebase. Please check your internet connection.";

        try
        {
            // Ownership + invite: REST first (SDK metadata Data is often empty on device).
            var restMeta = await GetTeamMetadataViaRestAsync(teamId).ConfigureAwait(false);
            string? createdBy = restMeta?.CreatedBy;
            string inviteRaw = restMeta?.InviteCode ?? "";

            if (string.IsNullOrWhiteSpace(createdBy) || string.IsNullOrWhiteSpace(inviteRaw))
            {
                try
                {
                    var metaSnap = await _db.GetDocument($"teams/{teamId}/metadata/info")
                        .GetDocumentSnapshotAsync<Dictionary<string, object>>()
                        .ConfigureAwait(false);
                    var meta = metaSnap?.Data;
                    if (meta is not null)
                    {
                        if (string.IsNullOrWhiteSpace(createdBy))
                            createdBy = ReadString(meta, "createdBy");
                        if (string.IsNullOrWhiteSpace(inviteRaw))
                            inviteRaw = ReadString(meta, "inviteCode");
                    }
                }
                catch { /* best-effort SDK fill-in */ }
            }

            // Prefer Preferences invite if cloud field missing (still allow delete).
            if (string.IsNullOrWhiteSpace(inviteRaw))
                inviteRaw = Preferences.Get($"{teamId}_invite_code", string.Empty);

            if (string.IsNullOrWhiteSpace(createdBy) && restMeta is null)
            {
                // Neither REST nor SDK could load metadata
                var stillOwner = await IsTeamOwnerAsync(teamId).ConfigureAwait(false);
                if (!stillOwner)
                    return "error: Team not found in the cloud (it may already be deleted).";
            }

            if (string.IsNullOrWhiteSpace(createdBy)
                || !string.Equals(createdBy.Trim(), uid, StringComparison.Ordinal))
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[CloudTeam] DeleteTeamAsOwner not_owner caller={uid[..Math.Min(8, uid.Length)]}… " +
                    $"createdBy={(string.IsNullOrEmpty(createdBy) ? "(empty)" : createdBy[..Math.Min(8, createdBy.Length)] + "…")}");
                return "error: not_owner";
            }

            var inviteCode = NormalizeInviteCode(inviteRaw);

            // 1) Invite indexes (owner createdBy matches invite_codes delete rule)
            if (!string.IsNullOrEmpty(inviteCode))
            {
                foreach (var docId in InviteCodeDocumentIds(inviteCode))
                {
                    try
                    {
                        await DeleteDocumentViaRestAsync($"invite_codes/{docId}").ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[CloudTeam] Delete invite_codes/{docId} REST: {ex.Message}");
                        try
                        {
                            await _db.GetDocument($"invite_codes/{docId}").DeleteDocumentAsync()
                                .ConfigureAwait(false);
                        }
                        catch (Exception sdkEx)
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"[CloudTeam] Delete invite_codes/{docId}: {sdkEx.Message}");
                        }
                    }
                }
            }

            // 2) Subcollections under the team
            await DeleteCollectionDocsAsync($"teams/{teamId}/members").ConfigureAwait(false);
            await DeleteCollectionDocsAsync($"teams/{teamId}/messages").ConfigureAwait(false);
            await DeleteCollectionDocsAsync($"teams/{teamId}/sessions").ConfigureAwait(false);
            await DeleteCollectionDocsAsync($"teams/{teamId}/logs").ConfigureAwait(false);
            await DeleteCollectionDocsAsync($"teams/{teamId}/public").ConfigureAwait(false);

            // 3) Known singleton docs (REST then SDK)
            foreach (var path in new[]
                     {
                         $"teams/{teamId}/roster/data",
                         $"teams/{teamId}/public/invite",
                         $"teams/{teamId}/metadata/info"
                     })
            {
                try
                {
                    await DeleteDocumentViaRestAsync(path).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[CloudTeam] Delete REST {path}: {ex.Message}");
                    try
                    {
                        await _db.GetDocument(path).DeleteDocumentAsync().ConfigureAwait(false);
                    }
                    catch (Exception sdkEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"[CloudTeam] Delete {path}: {sdkEx.Message}");
                    }
                }
            }

            System.Diagnostics.Debug.WriteLine($"[CloudTeam] DeleteTeamAsOwner OK team={teamId}");
            return "success";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CloudTeam] DeleteTeamAsOwner: {ex.Message}");
            return $"error: {ex.Message}";
        }
    }

    private async Task DeleteDocumentViaRestAsync(string documentPath)
    {
        var idToken = await _auth.GetIdTokenAsync(forceRefresh: false).ConfigureAwait(false);
        if (string.IsNullOrEmpty(idToken))
            idToken = await _auth.GetIdTokenAsync(forceRefresh: true).ConfigureAwait(false);
        if (string.IsNullOrEmpty(idToken))
            throw new InvalidOperationException("No Firebase id token");

        var url =
            $"https://firestore.googleapis.com/v1/projects/{FirebaseProjectId}/databases/(default)/documents/{documentPath}";
        using var req = new HttpRequestMessage(HttpMethod.Delete, url);
        req.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", idToken);
        using var resp = await RestHttp.SendAsync(req).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            return;
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            throw new InvalidOperationException(
                $"REST delete {(int)resp.StatusCode}: {body[..Math.Min(200, body.Length)]}");
        }
    }

    private async Task DeleteCollectionDocsAsync(string collectionPath)
    {
        try
        {
            var snap = await _db.GetCollection(collectionPath)
                .GetDocumentsAsync<Dictionary<string, object>>()
                .ConfigureAwait(false);
            if (snap?.Documents is null) return;

            foreach (var doc in snap.Documents)
            {
                var id = doc.Reference?.Id;
                if (string.IsNullOrEmpty(id)) continue;
                try
                {
                    await _db.GetDocument($"{collectionPath}/{id}").DeleteDocumentAsync()
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[CloudTeam] Delete {collectionPath}/{id}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[CloudTeam] List/delete {collectionPath}: {ex.Message}");
        }
    }

    private static string ReadString(IDictionary<string, object>? d, string key)
        => CoerceString(GetField(d, key));

    private static string ReadString(IDictionary<object, object>? d, string key)
    {
        if (d is null) return "";
        if (d.TryGetValue(key, out var v) && v != null) return CoerceString(v);
        foreach (var kv in d)
            if (kv.Key?.ToString() == key && kv.Value != null) return CoerceString(kv.Value);
        return "";
    }

    private static object? GetField(IDictionary<string, object>? d, string key)
    {
        if (d is null) return null;
        if (d.TryGetValue(key, out var v) && v != null) return v;
        foreach (var kv in d)
            if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase) && kv.Value != null)
                return kv.Value;
        return null;
    }

    /// <summary>
    /// Unwraps Plugin.Firebase / REST-style field values to a plain string.
    /// Avoids ToString() dumps like "System.Collections.Generic.Dictionary…".
    /// </summary>
    private static string CoerceString(object? v)
    {
        if (v is null) return "";
        if (v is string s) return s;
        if (v is IDictionary<string, object> sd)
        {
            if (sd.TryGetValue("stringValue", out var sv) && sv != null)
                return CoerceString(sv);
            return "";
        }
        if (v is IDictionary<object, object> od)
        {
            foreach (var kv in od)
            {
                if (string.Equals(kv.Key?.ToString(), "stringValue", StringComparison.OrdinalIgnoreCase))
                    return CoerceString(kv.Value);
            }
            return "";
        }
        if (v is System.Collections.IDictionary idict)
        {
            foreach (System.Collections.DictionaryEntry e in idict)
            {
                if (string.Equals(e.Key?.ToString(), "stringValue", StringComparison.OrdinalIgnoreCase))
                    return CoerceString(e.Value);
            }
            return "";
        }

        var text = v.ToString() ?? "";
        if (text.StartsWith("System.", StringComparison.Ordinal)
            || text.Contains("Dictionary", StringComparison.Ordinal))
            return "";
        return text;
    }
}
