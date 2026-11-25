using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TurfTime2.Services;

/// <inheritdoc cref="IClubAdminService"/>
/// <remarks>Authenticated Firestore REST (like <see cref="ClubPlayerService"/>); rules enforce access.</remarks>
public sealed class ClubAdminService : IClubAdminService
{
    private const string FirebaseProjectId = "turf-timer";
    private const string KnownClubsKey = "club_admin_club_ids";
    private const string InviteAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private static readonly HttpClient RestHttp = new();

    private readonly IFirebaseAuthService _auth;
    private readonly ICloudTeamService _teams;

    public ClubAdminService(IFirebaseAuthService auth, ICloudTeamService teams)
    {
        _auth = auth;
        _teams = teams;
    }

    // ── REST plumbing ──────────────────────────────────────────────────────

    private static string DocsRoot => $"projects/{FirebaseProjectId}/databases/(default)/documents";
    private static string RestBase => $"https://firestore.googleapis.com/v1/{DocsRoot}";
    private static string RestUrl(string path) => $"{RestBase}/{string.Join('/', path.Split('/').Select(Uri.EscapeDataString))}";
    private static string FullName(string path) => $"{DocsRoot}/{path}";

    private static Dictionary<string, object> Str(string v) => new() { ["stringValue"] = v ?? "" };
    private static Dictionary<string, object> Int(int v) => new() { ["integerValue"] = v.ToString(CultureInfo.InvariantCulture) };
    private static Dictionary<string, object> Ts(DateTimeOffset v) => new() { ["timestampValue"] = v.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) };

    private async Task<string?> TokenAsync()
    {
        var t = await _auth.GetIdTokenAsync(false).ConfigureAwait(false);
        if (string.IsNullOrEmpty(t)) t = await _auth.GetIdTokenAsync(true).ConfigureAwait(false);
        return string.IsNullOrEmpty(t) ? null : t;
    }

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpMethod method, string url, object? body = null)
    {
        var token = await TokenAsync().ConfigureAwait(false);
        if (token is null) return (HttpStatusCode.Unauthorized, "");
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await RestHttp.SendAsync(req).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        FirestoreUsageMeter.RecordRestBody(method.Method, url, text);
        return (resp.StatusCode, text);
    }

    private static bool Ok(HttpStatusCode s) => (int)s is >= 200 and < 300;

    private static string Error(HttpStatusCode s, string body, string fallback)
    {
        if (s == HttpStatusCode.Forbidden || body.Contains("PERMISSION_DENIED", StringComparison.Ordinal))
            return $"error: {fallback}";
        if (s == HttpStatusCode.Unauthorized) return "error: Could not sign in. Check your internet connection.";
        return $"error: {fallback} ({(int)s})";
    }

    private static string S(JsonElement fields, string key) =>
        fields.TryGetProperty(key, out var v) && v.TryGetProperty("stringValue", out var s) ? s.GetString() ?? "" : "";

    private static int I(JsonElement fields, string key, int fallback)
    {
        if (!fields.TryGetProperty(key, out var v)) return fallback;
        if (v.TryGetProperty("integerValue", out var iv)
            && int.TryParse(iv.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return n;
        if (v.TryGetProperty("doubleValue", out var dv) && dv.TryGetDouble(out var d)) return (int)d;
        return fallback;
    }

    private async Task<JsonElement?> GetFieldsAsync(string path)
    {
        var (status, body) = await SendAsync(HttpMethod.Get, RestUrl(path)).ConfigureAwait(false);
        if (!Ok(status) || string.IsNullOrEmpty(body)) return null;
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("fields", out var f) ? f.Clone() : JsonDocument.Parse("{}").RootElement.Clone();
    }

    private async Task<List<(string Path, JsonElement Fields)>> RunCollectionGroupQueryAsync(string collectionId, string field, string value, int limit)
    {
        var body = new Dictionary<string, object>
        {
            ["structuredQuery"] = new Dictionary<string, object>
            {
                ["from"] = new object[] { new Dictionary<string, object> { ["collectionId"] = collectionId, ["allDescendants"] = true } },
                ["where"] = new Dictionary<string, object>
                {
                    ["fieldFilter"] = new Dictionary<string, object>
                    {
                        ["field"] = new Dictionary<string, object> { ["fieldPath"] = field },
                        ["op"] = "EQUAL",
                        ["value"] = Str(value),
                    }
                },
                ["limit"] = limit,
            }
        };
        var (status, text) = await SendAsync(HttpMethod.Post, $"{RestBase}:runQuery", body).ConfigureAwait(false);
        var rows = new List<(string, JsonElement)>();
        if (!Ok(status) || string.IsNullOrEmpty(text))
        {
            System.Diagnostics.Debug.WriteLine($"[ClubAdmin] runQuery {collectionId} {(int)status}: {text[..Math.Min(200, text.Length)]}");
            return rows;
        }
        using var doc = JsonDocument.Parse(text);
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("document", out var d)) continue;
            var name = d.GetProperty("name").GetString() ?? "";
            var i = name.IndexOf("/documents/", StringComparison.Ordinal);
            var path = i >= 0 ? name[(i + "/documents/".Length)..] : name;
            var fields = d.TryGetProperty("fields", out var f) ? f.Clone() : default;
            rows.Add((path, fields));
        }
        return rows;
    }

    // ── Clubs ──────────────────────────────────────────────────────────────

    private static HashSet<string> KnownClubIds() =>
        Preferences.Get(KnownClubsKey, "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);

    private static void RememberClub(string clubId)
    {
        var ids = KnownClubIds();
        if (ids.Add(clubId)) Preferences.Set(KnownClubsKey, string.Join(',', ids));
    }

    private async Task<string> ClubNameAsync(string clubId)
    {
        var f = await GetFieldsAsync($"clubs/{clubId}/metadata/info").ConfigureAwait(false);
        return f is { } fields ? S(fields, "clubName") : "";
    }

    public async Task<IReadOnlyList<CloudClubSummary>> ListMyAdminClubsAsync()
    {
        var uid = await _auth.EnsureSignedInAsync().ConfigureAwait(false);
        if (uid is null) return Array.Empty<CloudClubSummary>();
        var found = new Dictionary<string, CloudClubSummary>(StringComparer.Ordinal);

        try
        {
            foreach (var c in await _teams.ListManagedClubsAsync().ConfigureAwait(false))
                found[c.ClubId] = c;
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ClubAdmin] managed clubs: {ex.Message}"); }

        var candidates = new HashSet<string>(KnownClubIds(), StringComparer.Ordinal);
        try
        {
            foreach (var (path, fields) in await RunCollectionGroupQueryAsync("members", "uid", uid, 200).ConfigureAwait(false))
            {
                var parts = path.Split('/');
                if (parts.Length == 4 && parts[0] == "clubs" && S(fields, "role") is "owner" or "admin")
                    candidates.Add(parts[1]);
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ClubAdmin] club memberships: {ex.Message}"); }

        foreach (var clubId in candidates)
        {
            if (found.ContainsKey(clubId)) continue;
            var role = (await _teams.GetMyClubRoleAsync(clubId).ConfigureAwait(false))?.Trim().ToLowerInvariant();
            if (role is not ("owner" or "admin")) continue;
            found[clubId] = new CloudClubSummary { ClubId = clubId, ClubName = await ClubNameAsync(clubId).ConfigureAwait(false), Role = role };
        }

        foreach (var id in found.Keys) RememberClub(id);
        return found.Values.OrderBy(c => c.ClubName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<IReadOnlyList<ClubTeamStatus>> ListClubTeamsAsync(string clubId)
    {
        var uid = await _auth.EnsureSignedInAsync().ConfigureAwait(false);
        if (uid is null || string.IsNullOrWhiteSpace(clubId)) return Array.Empty<ClubTeamStatus>();
        var list = new List<ClubTeamStatus>();
        foreach (var (path, f) in await RunCollectionGroupQueryAsync("metadata", "clubId", clubId, 200).ConfigureAwait(false))
        {
            var parts = path.Split('/');
            if (parts.Length != 4 || parts[0] != "teams") continue;
            var teamId = parts[1];
            var mine = await GetFieldsAsync($"teams/{teamId}/members/{uid}").ConfigureAwait(false);
            list.Add(new ClubTeamStatus
            {
                TeamId = teamId,
                TeamName = S(f, "teamName"),
                ClubId = clubId,
                ClubName = S(f, "clubName"),
                AgeGroup = S(f, "ageGroup"),
                Season = I(f, "season", 0),
                TeamAdminCount = I(f, "teamAdminCount", -1),
                MyTeamRole = mine is { } m ? S(m, "role") : "",
            });
        }
        return list.OrderBy(t => t.TeamName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<ClubTeamEffectiveRole?> GetEffectiveRoleAsync(string teamId)
    {
        if (string.IsNullOrWhiteSpace(teamId) || teamId.StartsWith("local_", StringComparison.Ordinal)) return null;
        var uid = await _auth.EnsureSignedInAsync().ConfigureAwait(false);
        if (uid is null) return null;
        try
        {
            var meta = await GetFieldsAsync($"teams/{teamId}/metadata/info").ConfigureAwait(false);
            var clubId = meta is { } mf ? S(mf, "clubId") : "";
            var mine = await GetFieldsAsync($"teams/{teamId}/members/{uid}").ConfigureAwait(false);
            var teamRole = mine is { } m ? S(m, "role").Trim().ToLowerInvariant() : "";
            if (teamRole is "admin" or "member")
                return new ClubTeamEffectiveRole { Role = teamRole, ClubId = clubId };
            if (string.IsNullOrEmpty(clubId)) return null;
            var clubRole = (await _teams.GetMyClubRoleAsync(clubId).ConfigureAwait(false))?.Trim().ToLowerInvariant();
            if (clubRole is not ("owner" or "admin")) return null;
            var count = meta is { } mf2 ? I(mf2, "teamAdminCount", -1) : -1;
            return new ClubTeamEffectiveRole
            {
                Role = "clubadmin",
                Access = count == 0 ? ClubTeamAccess.Full : ClubTeamAccess.Limited,
                ClubId = clubId,
            };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ClubAdmin] effective role: {ex.Message}");
            return null;
        }
    }

    // ── Club Admin invites (owner) ────────────────────────────────────────

    private static string NewInviteCode()
    {
        Span<byte> bytes = stackalloc byte[10];
        RandomNumberGenerator.Fill(bytes);
        var sb = new StringBuilder(10);
        foreach (var b in bytes) sb.Append(InviteAlphabet[b % InviteAlphabet.Length]);
        return sb.ToString();
    }

    public static string FormatInviteText(string clubName, string clubId, string code, DateTimeOffset expiresUtc) =>
        $"You're invited to be a club admin for {clubName} in TurfTimer.\n" +
        $"In TurfTimer open Settings > Club admin > Redeem an invite and paste this code:\n\n{clubId}/{code}\n\n" +
        $"Single use. Expires {expiresUtc.ToLocalTime():d MMM yyyy}.";

    public async Task<ClubAdminInviteResult> CreateInviteAsync(string clubId, string clubName)
    {
        var uid = await _auth.EnsureSignedInAsync().ConfigureAwait(false);
        if (uid is null) return new ClubAdminInviteResult { Error = "Could not sign in. Check your internet connection." };
        var now = DateTimeOffset.UtcNow;
        var expires = now.AddDays(7).AddMinutes(-5); // rules cap at 7 days; allow for clock skew
        var code = NewInviteCode();
        var body = new Dictionary<string, object>
        {
            ["fields"] = new Dictionary<string, object>
            {
                ["createdBy"] = Str(uid),
                ["createdAt"] = Ts(now),
                ["expiresAt"] = Ts(expires),
            }
        };
        var (status, text) = await SendAsync(HttpMethod.Post,
            $"{RestUrl($"clubs/{clubId}/adminInvites")}?documentId={code}", body).ConfigureAwait(false);
        if (!Ok(status))
            return new ClubAdminInviteResult { Error = Error(status, text, "Only the club owner can create club admin invites.")[7..] };
        return new ClubAdminInviteResult
        {
            Ok = true,
            Code = code,
            ExpiresUtc = expires,
            InviteText = FormatInviteText(string.IsNullOrWhiteSpace(clubName) ? "the club" : clubName, clubId, code, expires),
        };
    }

    public async Task<string> RevokeInviteAsync(string clubId, string code)
    {
        var (status, text) = await SendAsync(HttpMethod.Delete, RestUrl($"clubs/{clubId}/adminInvites/{code}")).ConfigureAwait(false);
        return Ok(status) || status == HttpStatusCode.NotFound ? "success" : Error(status, text, "Only the club owner can revoke invites.");
    }

    private static readonly Regex InvitePattern = new(@"([A-Za-z0-9_\-]+)\s*/\s*([A-Za-z0-9]{10})\b", RegexOptions.Compiled);

    public static bool TryParseInvite(string? text, out string clubId, out string code)
    {
        clubId = ""; code = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        var matches = InvitePattern.Matches(text);
        if (matches.Count == 0) return false;
        var m = matches[^1];
        clubId = m.Groups[1].Value;
        code = m.Groups[2].Value.ToUpperInvariant();
        return true;
    }

    public async Task<ClubAdminRedeemResult> RedeemInviteAsync(string inviteText, string displayName)
    {
        if (!TryParseInvite(inviteText, out var clubId, out var code))
            return new ClubAdminRedeemResult { Message = "That doesn't look like a club admin invite. Paste the whole code, like club-name-abc123/ABCDEFGHJK." };
        var uid = await _auth.EnsureSignedInAsync().ConfigureAwait(false);
        if (uid is null) return new ClubAdminRedeemResult { Message = "Could not sign in. Check your internet connection." };

        var memberPath = $"clubs/{clubId}/members/{uid}";
        var existing = await GetFieldsAsync(memberPath).ConfigureAwait(false);
        var currentRole = existing is { } ef ? S(ef, "role") : "";
        var clubName = await ClubNameAsync(clubId).ConfigureAwait(false);
        if (currentRole is "owner" or "admin")
        {
            RememberClub(clubId);
            return new ClubAdminRedeemResult { Ok = true, ClubId = clubId, ClubName = clubName, Message = "You are already an admin of this club." };
        }

        object memberWrite = existing is null
            ? new Dictionary<string, object>
            {
                ["update"] = new Dictionary<string, object>
                {
                    ["name"] = FullName(memberPath),
                    ["fields"] = new Dictionary<string, object>
                    {
                        ["role"] = Str("admin"),
                        ["uid"] = Str(uid),
                        ["displayName"] = Str(displayName ?? ""),
                        ["addedAt"] = Ts(DateTimeOffset.UtcNow),
                        ["adminInviteCode"] = Str(code),
                    }
                },
                ["currentDocument"] = new Dictionary<string, object> { ["exists"] = false },
            }
            : new Dictionary<string, object>
            {
                ["update"] = new Dictionary<string, object>
                {
                    ["name"] = FullName(memberPath),
                    ["fields"] = new Dictionary<string, object> { ["role"] = Str("admin"), ["adminInviteCode"] = Str(code) }
                },
                ["updateMask"] = new Dictionary<string, object> { ["fieldPaths"] = new[] { "role", "adminInviteCode" } },
                ["currentDocument"] = new Dictionary<string, object> { ["exists"] = true },
            };
        var body = new Dictionary<string, object>
        {
            ["writes"] = new object[]
            {
                memberWrite,
                new Dictionary<string, object> { ["delete"] = FullName($"clubs/{clubId}/adminInvites/{code}") },
            }
        };
        var (status, text) = await SendAsync(HttpMethod.Post, $"{RestBase}:commit", body).ConfigureAwait(false);
        if (!Ok(status))
            return new ClubAdminRedeemResult { Message = "That invite isn't valid. It may have been used, revoked or expired. Ask the club owner for a new one." };
        RememberClub(clubId);
        return new ClubAdminRedeemResult { Ok = true, ClubId = clubId, ClubName = clubName, Message = $"You're now a club admin of {(string.IsNullOrWhiteSpace(clubName) ? clubId : clubName)}." };
    }

    // ── Club members (owner promotes / removes Club Admins) ───────────────

    public async Task<IReadOnlyList<ClubMemberInfo>> ListClubMembersAsync(string clubId)
    {
        var list = new List<ClubMemberInfo>();
        string? pageToken = null;
        do
        {
            var url = $"{RestUrl($"clubs/{clubId}/members")}?pageSize=300" + (pageToken is null ? "" : $"&pageToken={Uri.EscapeDataString(pageToken)}");
            var (status, text) = await SendAsync(HttpMethod.Get, url).ConfigureAwait(false);
            if (!Ok(status) || string.IsNullOrEmpty(text)) break;
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("documents", out var docs))
            {
                foreach (var d in docs.EnumerateArray())
                {
                    var name = d.GetProperty("name").GetString() ?? "";
                    var f = d.TryGetProperty("fields", out var ff) ? ff : default;
                    list.Add(new ClubMemberInfo
                    {
                        Uid = name[(name.LastIndexOf('/') + 1)..],
                        DisplayName = f.ValueKind == JsonValueKind.Object ? S(f, "displayName") : "",
                        Role = f.ValueKind == JsonValueKind.Object ? (S(f, "role") is { Length: > 0 } r ? r : "member") : "member",
                    });
                }
            }
            pageToken = doc.RootElement.TryGetProperty("nextPageToken", out var npt) ? npt.GetString() : null;
        } while (!string.IsNullOrEmpty(pageToken));
        return list.OrderBy(m => m.Role switch { "owner" => 0, "admin" => 1, _ => 2 })
            .ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<string> PatchAsync(string path, Dictionary<string, object> fields, string deniedMessage, bool mustExist = true)
    {
        var mask = string.Join("&", fields.Keys.Select(k => $"updateMask.fieldPaths={Uri.EscapeDataString(k)}"));
        var url = $"{RestUrl(path)}?{mask}" + (mustExist ? "&currentDocument.exists=true" : "");
        var (status, text) = await SendAsync(HttpMethod.Patch, url, new Dictionary<string, object> { ["fields"] = fields }).ConfigureAwait(false);
        return Ok(status) ? "success" : Error(status, text, deniedMessage);
    }

    public Task<string> SetClubMemberRoleAsync(string clubId, string memberUid, string role)
    {
        if (role is not ("admin" or "member")) return Task.FromResult("error: Role must be admin or member.");
        return PatchAsync($"clubs/{clubId}/members/{memberUid}", new() { ["role"] = Str(role) },
            "Only the club owner can add or remove club admins.");
    }

    // ── Teams (full control) ───────────────────────────────────────────────

    public async Task<string> JoinTeamAsync(string teamId, string role, string displayName)
    {
        if (role is not ("admin" or "member")) return "error: Role must be admin or member.";
        var uid = await _auth.EnsureSignedInAsync().ConfigureAwait(false);
        if (uid is null) return "error: Could not sign in. Check your internet connection.";
        var body = new Dictionary<string, object>
        {
            ["fields"] = new Dictionary<string, object>
            {
                ["role"] = Str(role),
                ["uid"] = Str(uid),
                ["displayName"] = Str(displayName ?? ""),
                ["joinedAt"] = Ts(DateTimeOffset.UtcNow),
            }
        };
        var (status, text) = await SendAsync(HttpMethod.Post,
            $"{RestUrl($"teams/{teamId}/members")}?documentId={Uri.EscapeDataString(uid)}", body).ConfigureAwait(false);
        if (status == HttpStatusCode.Conflict) return "error: You are already on this team.";
        return Ok(status) ? "success" : Error(status, text, "This team already has a team admin, so only they can add people.");
    }

    public Task<string> SetTeamMemberRoleAsync(string teamId, string memberUid, string role)
    {
        if (role is not ("admin" or "member")) return Task.FromResult("error: Role must be admin or member.");
        return PatchAsync($"teams/{teamId}/members/{memberUid}", new() { ["role"] = Str(role) },
            "This team has another team admin, so only they can change roles.");
    }

    public async Task<string> RemoveTeamMemberAsync(string teamId, string memberUid)
    {
        var (status, text) = await SendAsync(HttpMethod.Delete, RestUrl($"teams/{teamId}/members/{memberUid}")).ConfigureAwait(false);
        return Ok(status) ? "success" : Error(status, text, "This team has another team admin, so only they can remove members.");
    }

    public Task<string> UpdateTeamAsync(string teamId, string teamName, string ageGroup, int season)
    {
        var name = (teamName ?? "").Trim();
        if (name.Length == 0 || name.Length > 80) return Task.FromResult("error: Enter a team name (up to 80 characters).");
        var age = Helpers.ClubAgeGroups.CanonicalAgeGroup(ageGroup);
        if (age is null) return Task.FromResult("error: Choose a valid age group.");
        if (season is < 2000 or > 2100) return Task.FromResult("error: Enter a valid season year.");
        var fields = new Dictionary<string, object>
        {
            ["teamName"] = Str(name),
            ["ageGroup"] = Str(age),
            ["season"] = Int(season),
        };
        if (Helpers.ClubAgeGroups.MaxAgeOn1Jan(age) is int max) fields["maxAgeOn1Jan"] = Int(max);
        return PatchAsync($"teams/{teamId}/metadata/info", fields,
            "This team has another team admin, so only they can change it.");
    }

    public async Task<string> DeleteTeamAsync(string teamId)
    {
        var (status, text) = await SendAsync(HttpMethod.Delete,
            $"{RestUrl($"teams/{teamId}/metadata/info")}?currentDocument.exists=true").ConfigureAwait(false);
        return Ok(status) ? "success" : Error(status, text, "This team has another team admin, so only they can delete it.");
    }
}
