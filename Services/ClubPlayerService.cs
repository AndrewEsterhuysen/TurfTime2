using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TurfTime2.Helpers;
using TurfTime2.Models;

namespace TurfTime2.Services;

/// <inheritdoc cref="IClubPlayerService"/>
/// <remarks>
/// Authenticated Firestore REST, matching <see cref="TeamDetailsDocService"/> and
/// <see cref="NominationService"/> (Plugin.Firebase <c>SetDataAsync</c> has been seen to
/// "succeed" with empty documents on device). Rules enforce who may read and write.
/// </remarks>
public sealed class ClubPlayerService : IClubPlayerService
{
    private const string FirebaseProjectId = "turf-timer";
    private const int PageSize = 300;
    private static readonly HttpClient RestHttp = new();

    private readonly IFirebaseAuthService _auth;
    private readonly ICloudTeamService _teams;

    public ClubPlayerService(IFirebaseAuthService auth, ICloudTeamService teams)
    {
        _auth = auth;
        _teams = teams;
    }

    // ── Paths ─────────────────────────────────────────────────────────────

    /// <summary><c>clubs/{clubId}/players</c></summary>
    public static string PlayersPath(string clubId) => $"clubs/{clubId}/players";

    /// <summary><c>clubs/{clubId}/players/{playerId}</c></summary>
    public static string PlayerPath(string clubId, string playerId) => $"clubs/{clubId}/players/{playerId}";

    private static string ClubMemberPath(string clubId, string uid) => $"clubs/{clubId}/members/{uid}";

    private static string RestBase =>
        $"https://firestore.googleapis.com/v1/projects/{FirebaseProjectId}/databases/(default)/documents";

    private static string RestUrl(string path) =>
        $"{RestBase}/{string.Join('/', path.Split('/').Select(Uri.EscapeDataString))}";

    // ── Access ────────────────────────────────────────────────────────────

    public async Task<ClubPlayerAccess> GetAccessAsync(string clubId, string? teamId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clubId)) return new ClubPlayerAccess();
        string? uid;
        try { uid = await _auth.EnsureSignedInAsync().ConfigureAwait(false); }
        catch { uid = null; }
        if (uid is null) return new ClubPlayerAccess { Reachable = false };

        try
        {
            var clubRole = (await _teams.GetMyClubRoleAsync(clubId).ConfigureAwait(false))?.Trim().ToLowerInvariant();
            if (clubRole is "owner" or "admin")
                return new ClubPlayerAccess { Role = clubRole };

            if (string.IsNullOrWhiteSpace(teamId)) return new ClubPlayerAccess();
            var teamRole = await _teams.GetMyRoleAsync(teamId).ConfigureAwait(false);
            if (!string.Equals(teamRole, "admin", StringComparison.OrdinalIgnoreCase))
                return new ClubPlayerAccess();

            // Team Admin of a club team = coach (§9). Record it on the club membership so
            // the rules can check it (they re-verify team Admin + team.clubId live).
            await EnsureCoachLinkAsync(clubId, teamId, uid, cancellationToken).ConfigureAwait(false);
            return new ClubPlayerAccess { Role = "coach" };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ClubPlayers] Access: {ex.Message}");
            return new ClubPlayerAccess { Reachable = false };
        }
    }

    private async Task EnsureCoachLinkAsync(string clubId, string teamId, string uid, CancellationToken ct)
    {
        var token = await GetTokenAsync().ConfigureAwait(false);
        if (string.IsNullOrEmpty(token)) return;
        var path = ClubMemberPath(clubId, uid);

        using (var get = new HttpRequestMessage(HttpMethod.Get, RestUrl(path)))
        {
            get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await RestHttp.SendAsync(get, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            FirestoreUsageMeter.RecordRestBody("GET", path, body);

            Dictionary<string, object> fields;
            string[] mask;
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                // Joining a club team normally creates this; create it as a plain member if missing.
                fields = new()
                {
                    ["role"] = Str("member"),
                    ["uid"] = Str(uid),
                    ["displayName"] = Str(UserDisplayName.Get()),
                    ["coachTeamId"] = Str(teamId),
                    ["addedAt"] = Ts(DateTimeOffset.UtcNow),
                };
                mask = ["role", "uid", "displayName", "coachTeamId", "addedAt"];
            }
            else if (resp.IsSuccessStatusCode)
            {
                using var json = JsonDocument.Parse(body);
                if (json.RootElement.TryGetProperty("fields", out var f)
                    && ReadString(f, "coachTeamId") == teamId)
                    return;
                fields = new() { ["coachTeamId"] = Str(teamId) };
                mask = ["coachTeamId"];
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[ClubPlayers] Member read {(int)resp.StatusCode}");
                return;
            }

            await PatchAsync(path, fields, mask, mustExist: null, token, ct).ConfigureAwait(false);
        }
    }

    // ── List ──────────────────────────────────────────────────────────────

    public async Task<ClubPlayerListResult> ListAsync(string clubId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clubId))
            return new ClubPlayerListResult { Reachable = false, Error = "No club." };
        try
        {
            if (await _auth.EnsureSignedInAsync().ConfigureAwait(false) is null)
                return new ClubPlayerListResult { Reachable = false, Error = "Not signed in." };
            var token = await GetTokenAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(token))
                return new ClubPlayerListResult { Reachable = false, Error = "Not signed in." };

            var players = new List<ClubPlayer>();
            string? pageToken = null;
            do
            {
                var url = $"{RestUrl(PlayersPath(clubId))}?pageSize={PageSize}"
                          + (pageToken is null ? "" : $"&pageToken={Uri.EscapeDataString(pageToken)}");
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var resp = await RestHttp.SendAsync(req, cancellationToken).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                FirestoreUsageMeter.RecordRestBody("GET", url, body);
                if (resp.StatusCode == HttpStatusCode.Forbidden)
                    return new ClubPlayerListResult { Reachable = true, Error = "Only club admins and coaches can see the club player list." };
                if (!resp.IsSuccessStatusCode)
                    return new ClubPlayerListResult { Reachable = false, Error = $"Could not load players ({(int)resp.StatusCode})." };

                using var json = JsonDocument.Parse(body);
                if (json.RootElement.TryGetProperty("documents", out var docs))
                {
                    foreach (var d in docs.EnumerateArray())
                    {
                        var p = ParsePlayer(d);
                        if (p is not null) players.Add(p);
                    }
                }
                pageToken = json.RootElement.TryGetProperty("nextPageToken", out var npt) ? npt.GetString() : null;
            } while (!string.IsNullOrEmpty(pageToken));

            players.Sort((a, b) => string.Compare(a.NameKey, b.NameKey, StringComparison.Ordinal));
            return new ClubPlayerListResult { Reachable = true, Players = players };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ClubPlayers] List: {ex.Message}");
            return new ClubPlayerListResult { Reachable = false, Error = "Could not reach the server. Check your connection." };
        }
    }

    // ── Add ───────────────────────────────────────────────────────────────

    public async Task<ClubPlayerWriteResult> AddAsync(string clubId, string name, string? preferredName, DateOnly dateOfBirth,
        CancellationToken cancellationToken = default)
    {
        var cleanName = ClubPlayerNames.CleanDisplay(name);
        var cleanPreferred = ClubPlayerNames.CleanDisplay(preferredName, ClubPlayerNames.MaxPreferredNameLength);
        if (string.IsNullOrWhiteSpace(clubId)) return ClubPlayerWriteResult.Fail("No club.");
        if (cleanName.Length == 0) return ClubPlayerWriteResult.Fail("Enter the player's name.");
        if (dateOfBirth > DateOnly.FromDateTime(DateTime.Today))
            return ClubPlayerWriteResult.Fail("Date of birth can't be in the future.");

        try
        {
            var uid = await _auth.EnsureSignedInAsync().ConfigureAwait(false);
            var token = uid is null ? null : await GetTokenAsync().ConfigureAwait(false);
            if (uid is null || string.IsNullOrEmpty(token)) return ClubPlayerWriteResult.Fail("Sign in to add players.");

            var playerId = NewPlayerId();
            var now = DateTimeOffset.UtcNow;
            var player = new ClubPlayer
            {
                PlayerId = playerId,
                Name = cleanName,
                PreferredName = cleanPreferred.Length == 0 ? null : cleanPreferred,
                NameKey = ClubPlayerNames.BuildNameKey(cleanName, cleanPreferred),
                DateOfBirth = ClubAgeGroups.ToIso(dateOfBirth),
                Status = ClubPlayer.StatusActive,
                CreatedAt = now,
                CreatedBy = uid,
                UpdatedAt = now,
                LastUsedAt = now,
            };

            var fields = new Dictionary<string, object>
            {
                ["playerId"] = Str(playerId),
                ["name"] = Str(player.Name),
                ["preferredName"] = player.PreferredName is null ? Null() : Str(player.PreferredName),
                ["nameKey"] = Str(player.NameKey),
                ["dateOfBirth"] = Str(player.DateOfBirth),
                ["dobYear"] = Int(dateOfBirth.Year),
                ["dobMonth"] = Int(dateOfBirth.Month),
                ["dobDay"] = Int(dateOfBirth.Day),
                ["teams"] = EmptyArray(),
                ["teamIds"] = EmptyArray(),
                ["status"] = Str(ClubPlayer.StatusActive),
                ["createdAt"] = Ts(now),
                ["createdBy"] = Str(uid),
                ["updatedAt"] = Ts(now),
                ["lastUsedAt"] = Ts(now),
            };

            var url = $"{RestUrl(PlayersPath(clubId))}?documentId={Uri.EscapeDataString(playerId)}";
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Content = new StringContent(JsonSerializer.Serialize(new { fields }), Encoding.UTF8, "application/json");
            using var resp = await RestHttp.SendAsync(req, cancellationToken).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            FirestoreUsageMeter.RecordRestBody("POST", url, body);
            if (resp.StatusCode == HttpStatusCode.Forbidden)
                return ClubPlayerWriteResult.Fail("Only club admins and coaches can add players.");
            if (!resp.IsSuccessStatusCode)
            {
                System.Diagnostics.Debug.WriteLine($"[ClubPlayers] Add {(int)resp.StatusCode}: {body[..Math.Min(200, body.Length)]}");
                return ClubPlayerWriteResult.Fail("Could not add the player. Please try again.");
            }
            return new ClubPlayerWriteResult { Success = true, Player = player };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ClubPlayers] Add: {ex.Message}");
            return ClubPlayerWriteResult.Fail("Could not reach the server. Check your connection.");
        }
    }

    // ── Update ────────────────────────────────────────────────────────────

    public async Task<ClubPlayerWriteResult> UpdateAsync(string clubId, string playerId, ClubPlayerUpdate update,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clubId) || string.IsNullOrWhiteSpace(playerId) || update is null)
            return ClubPlayerWriteResult.Fail("Missing player.");
        if (update.Status is not null && update.Status is not (ClubPlayer.StatusActive or ClubPlayer.StatusArchived))
            return ClubPlayerWriteResult.Fail("Unknown status.");

        try
        {
            var uid = await _auth.EnsureSignedInAsync().ConfigureAwait(false);
            var token = uid is null ? null : await GetTokenAsync().ConfigureAwait(false);
            if (uid is null || string.IsNullOrEmpty(token)) return ClubPlayerWriteResult.Fail("Sign in to edit players.");

            // Current values are needed to rebuild nameKey from name + preferred name.
            var path = PlayerPath(clubId, playerId);
            ClubPlayer? current;
            using (var get = new HttpRequestMessage(HttpMethod.Get, RestUrl(path)))
            {
                get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var resp = await RestHttp.SendAsync(get, cancellationToken).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                FirestoreUsageMeter.RecordRestBody("GET", path, body);
                if (resp.StatusCode == HttpStatusCode.NotFound) return ClubPlayerWriteResult.Fail("Player not found.");
                if (!resp.IsSuccessStatusCode) return ClubPlayerWriteResult.Fail("Could not load the player.");
                using var json = JsonDocument.Parse(body);
                current = ParsePlayer(json.RootElement);
            }
            if (current is null) return ClubPlayerWriteResult.Fail("Player not found.");

            var fields = new Dictionary<string, object>();
            if (update.Name is not null)
            {
                var n = ClubPlayerNames.CleanDisplay(update.Name);
                if (n.Length == 0) return ClubPlayerWriteResult.Fail("Enter the player's name.");
                current.Name = n;
                fields["name"] = Str(n);
            }
            if (update.SetPreferredName)
            {
                var p = ClubPlayerNames.CleanDisplay(update.PreferredName, ClubPlayerNames.MaxPreferredNameLength);
                current.PreferredName = p.Length == 0 ? null : p;
                fields["preferredName"] = current.PreferredName is null ? Null() : Str(current.PreferredName);
            }
            if (fields.Count > 0)
            {
                current.NameKey = ClubPlayerNames.BuildNameKey(current.Name, current.PreferredName);
                fields["nameKey"] = Str(current.NameKey);
            }
            if (update.DateOfBirth is { } dob)
            {
                current.DateOfBirth = ClubAgeGroups.ToIso(dob);
                fields["dateOfBirth"] = Str(current.DateOfBirth);
                fields["dobYear"] = Int(dob.Year);
                fields["dobMonth"] = Int(dob.Month);
                fields["dobDay"] = Int(dob.Day);
            }
            if (update.Status is not null)
            {
                current.Status = update.Status;
                fields["status"] = Str(update.Status);
            }
            if (fields.Count == 0) return new ClubPlayerWriteResult { Success = true, Player = current };

            var now = DateTimeOffset.UtcNow;
            current.UpdatedAt = now;
            fields["updatedAt"] = Ts(now);
            fields["updatedBy"] = Str(uid);

            var ok = await PatchAsync(path, fields, fields.Keys.ToArray(), mustExist: true, token, cancellationToken)
                .ConfigureAwait(false);
            return ok
                ? new ClubPlayerWriteResult { Success = true, Player = current }
                : ClubPlayerWriteResult.Fail("Could not save. Only club admins can change date of birth or archive players.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ClubPlayers] Update: {ex.Message}");
            return ClubPlayerWriteResult.Fail("Could not reach the server. Check your connection.");
        }
    }

    // ── Assign / unassign ─────────────────────────────────────────────────

    public async Task<ClubPlayerWriteResult> AssignToTeamAsync(string clubId, string playerId, string teamId,
        string teamName, string ageGroup, int season, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clubId) || string.IsNullOrWhiteSpace(playerId) || string.IsNullOrWhiteSpace(teamId))
            return ClubPlayerWriteResult.Fail("Missing team or player.");
        if (season < 2000 || season > 2100)
            return ClubPlayerWriteResult.Fail("Set a valid season year.");
        if (!ClubAgeGroups.IsKnownAgeGroup(ageGroup))
            return ClubPlayerWriteResult.Fail("Set this team's age group before assigning players.");

        var loaded = await LoadPlayerAsync(clubId, playerId, cancellationToken).ConfigureAwait(false);
        if (loaded.Error is not null) return loaded;
        var current = loaded.Player!;

        if (!ClubAgeGroups.TryParseDateOfBirth(current.DateOfBirth, out var dob))
            return ClubPlayerWriteResult.Fail("This player needs a date of birth before they can be assigned.");
        if (!ClubAgeGroups.IsEligibleToAssign(dob, ageGroup, season))
            return ClubPlayerWriteResult.Fail(ClubAgeGroups.IneligibilityReason(dob, ageGroup, season)
                                              ?? "This player is not eligible for the team.");

        var warning = ClubPlayerAssignments.ShouldWarnTeamCount(current.Teams, teamId)
            ? $"This player is already assigned to {ClubPlayerAssignments.TeamCountWarningAt} or more teams this season."
            : null;

        var now = DateTimeOffset.UtcNow;
        var teams = ClubPlayerAssignments.Assign(current.Teams, teamId, teamName ?? "", season, now);
        var teamIds = ClubPlayerAssignments.CurrentTeamIds(teams);
        return await PatchAssignmentAsync(clubId, current, teams, teamIds, teamId, dob, now, warning, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ClubPlayerWriteResult> UnassignFromTeamAsync(string clubId, string playerId, string teamId,
        int season, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clubId) || string.IsNullOrWhiteSpace(playerId) || string.IsNullOrWhiteSpace(teamId))
            return ClubPlayerWriteResult.Fail("Missing team or player.");

        var loaded = await LoadPlayerAsync(clubId, playerId, cancellationToken).ConfigureAwait(false);
        if (loaded.Error is not null) return loaded;
        var current = loaded.Player!;

        var now = DateTimeOffset.UtcNow;
        var teams = ClubPlayerAssignments.Unassign(current.Teams, teamId, season, now);
        var teamIds = ClubPlayerAssignments.CurrentTeamIds(teams);
        ClubAgeGroups.TryParseDateOfBirth(current.DateOfBirth, out var dob);
        return await PatchAssignmentAsync(clubId, current, teams, teamIds, teamId, dob, now, warning: null, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ClubPlayerWriteResult> LoadPlayerAsync(string clubId, string playerId, CancellationToken ct)
    {
        var token = await GetTokenAsync().ConfigureAwait(false);
        if (string.IsNullOrEmpty(token)) return ClubPlayerWriteResult.Fail("Sign in to assign players.");
        var path = PlayerPath(clubId, playerId);
        using var get = new HttpRequestMessage(HttpMethod.Get, RestUrl(path));
        get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await RestHttp.SendAsync(get, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        FirestoreUsageMeter.RecordRestBody("GET", path, body);
        if (resp.StatusCode == HttpStatusCode.NotFound) return ClubPlayerWriteResult.Fail("Player not found.");
        if (!resp.IsSuccessStatusCode) return ClubPlayerWriteResult.Fail("Could not load the player.");
        using var json = JsonDocument.Parse(body);
        var player = ParsePlayer(json.RootElement);
        return player is null
            ? ClubPlayerWriteResult.Fail("Could not load the player.")
            : new ClubPlayerWriteResult { Success = true, Player = player };
    }

    private async Task<ClubPlayerWriteResult> PatchAssignmentAsync(
        string clubId, ClubPlayer current, List<ClubPlayerTeamEntry> teams, List<string> teamIds,
        string assignmentTeamId, DateOnly dob, DateTimeOffset now, string? warning, CancellationToken ct)
    {
        var token = await GetTokenAsync().ConfigureAwait(false);
        if (string.IsNullOrEmpty(token)) return ClubPlayerWriteResult.Fail("Sign in to assign players.");
        var uid = await _auth.EnsureSignedInAsync().ConfigureAwait(false) ?? "";

        current.Teams = teams;
        current.TeamIds = teamIds;
        current.UpdatedAt = now;
        current.LastUsedAt = now;

        var fields = new Dictionary<string, object>
        {
            ["teams"] = TeamsArray(teams),
            ["teamIds"] = StringArray(teamIds),
            ["assignmentTeamId"] = Str(assignmentTeamId),
            ["updatedAt"] = Ts(now),
            ["updatedBy"] = Str(uid),
            ["lastUsedAt"] = Ts(now),
        };
        if (dob != default)
        {
            fields["dobYear"] = Int(dob.Year);
            fields["dobMonth"] = Int(dob.Month);
            fields["dobDay"] = Int(dob.Day);
        }

        var path = PlayerPath(clubId, current.PlayerId);
        var ok = await PatchAsync(path, fields, fields.Keys.ToArray(), mustExist: true, token, ct)
            .ConfigureAwait(false);
        if (!ok)
            return ClubPlayerWriteResult.Fail("Could not update the assignment. Check the team's age group and try again.");
        return new ClubPlayerWriteResult { Success = true, Player = current, Warning = warning };
    }

    // ── REST helpers ──────────────────────────────────────────────────────

    private static async Task<bool> PatchAsync(string path, Dictionary<string, object> fields, string[] mask,
        bool? mustExist, string token, CancellationToken ct)
    {
        var query = string.Join("&", mask.Select(k => $"updateMask.fieldPaths={Uri.EscapeDataString(k)}"));
        if (mustExist is { } e) query += $"&currentDocument.exists={(e ? "true" : "false")}";
        var url = $"{RestUrl(path)}?{query}";
        using var patch = new HttpRequestMessage(HttpMethod.Patch, url);
        patch.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        patch.Content = new StringContent(JsonSerializer.Serialize(new { fields }), Encoding.UTF8, "application/json");
        using var resp = await RestHttp.SendAsync(patch, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        FirestoreUsageMeter.RecordRestBody("PATCH", url, body);
        if (!resp.IsSuccessStatusCode)
            System.Diagnostics.Debug.WriteLine($"[ClubPlayers] PATCH {(int)resp.StatusCode}: {body[..Math.Min(200, body.Length)]}");
        return resp.IsSuccessStatusCode;
    }

    private static Dictionary<string, object> Str(string? v) => new() { ["stringValue"] = v ?? "" };
    private static Dictionary<string, object> Null() => new() { ["nullValue"] = null! };
    private static Dictionary<string, object> EmptyArray() => new() { ["arrayValue"] = new Dictionary<string, object>() };
    private static Dictionary<string, object> Int(int v) => new() { ["integerValue"] = v.ToString(CultureInfo.InvariantCulture) };

    private static Dictionary<string, object> StringArray(IEnumerable<string> ids) =>
        new()
        {
            ["arrayValue"] = new Dictionary<string, object>
            {
                ["values"] = ids.Select(id => (object)Str(id)).ToList()
            }
        };

    private static Dictionary<string, object> TeamsArray(IEnumerable<ClubPlayerTeamEntry> teams) =>
        new()
        {
            ["arrayValue"] = new Dictionary<string, object>
            {
                ["values"] = teams.Select(entry => (object)new Dictionary<string, object>
                {
                    ["mapValue"] = new Dictionary<string, object> { ["fields"] = TeamEntryFields(entry) }
                }).ToList()
            }
        };

    private static Dictionary<string, object> TeamEntryFields(ClubPlayerTeamEntry t)
    {
        var fields = new Dictionary<string, object>
        {
            ["teamId"] = Str(t.TeamId),
            ["teamName"] = Str(t.TeamName),
            ["season"] = Int(t.Season)
        };
        if (t.AssignedAt is { } assigned) fields["assignedAt"] = Ts(assigned);
        if (t.RemovedAt is { } removed) fields["removedAt"] = Ts(removed);
        return fields;
    }
    private static Dictionary<string, object> Ts(DateTimeOffset v) =>
        new() { ["timestampValue"] = v.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) };

    /// <summary>20-char id like Firestore auto ids.</summary>
    private static string NewPlayerId() => Guid.NewGuid().ToString("N")[..20];

    private async Task<string?> GetTokenAsync()
    {
        var t = await _auth.GetIdTokenAsync(forceRefresh: false).ConfigureAwait(false);
        if (string.IsNullOrEmpty(t))
            t = await _auth.GetIdTokenAsync(forceRefresh: true).ConfigureAwait(false);
        return t;
    }

    // ── Parsing (REST document → model) ───────────────────────────────────

    internal static ClubPlayer? ParsePlayer(JsonElement doc)
    {
        if (!doc.TryGetProperty("name", out var nameEl)) return null;
        var docName = nameEl.GetString() ?? "";
        var id = docName[(docName.LastIndexOf('/') + 1)..];
        if (!doc.TryGetProperty("fields", out var f)) return new ClubPlayer { PlayerId = id };

        var p = new ClubPlayer
        {
            PlayerId = id,
            Name = ReadString(f, "name"),
            PreferredName = NullIfEmpty(ReadString(f, "preferredName")),
            NameKey = ReadString(f, "nameKey"),
            DateOfBirth = NullIfEmpty(ReadString(f, "dateOfBirth")),
            Status = NullIfEmpty(ReadString(f, "status")) ?? ClubPlayer.StatusActive,
            CreatedBy = ReadString(f, "createdBy"),
            CreatedAt = ReadTimestamp(f, "createdAt"),
            UpdatedAt = ReadTimestamp(f, "updatedAt"),
            LastUsedAt = ReadTimestamp(f, "lastUsedAt"),
            Totals = ReadStats(f, "totals"),
            Borrowed = ReadStats(f, "borrowed"),
            ByTeam = ReadStatsMap(f, "byTeam"),
            BorrowedByTeam = ReadStatsMap(f, "borrowedByTeam"),
        };
        if (string.IsNullOrEmpty(p.NameKey))
            p.NameKey = ClubPlayerNames.BuildNameKey(p.Name, p.PreferredName);

        foreach (var v in ReadArray(f, "teamIds"))
            if (v.TryGetProperty("stringValue", out var s) && !string.IsNullOrEmpty(s.GetString()))
                p.TeamIds.Add(s.GetString()!);

        foreach (var v in ReadArray(f, "teams"))
        {
            if (!v.TryGetProperty("mapValue", out var mv) || !mv.TryGetProperty("fields", out var tf)) continue;
            p.Teams.Add(new ClubPlayerTeamEntry
            {
                TeamId = ReadString(tf, "teamId"),
                TeamName = ReadString(tf, "teamName"),
                Season = (int)ReadNumber(tf, "season"),
                AssignedAt = ReadTimestamp(tf, "assignedAt"),
                RemovedAt = ReadTimestamp(tf, "removedAt"),
            });
        }
        return p;
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static string ReadString(JsonElement fields, string key) =>
        fields.TryGetProperty(key, out var v) && v.TryGetProperty("stringValue", out var s) ? s.GetString() ?? "" : "";

    private static long ReadNumber(JsonElement fields, string key)
    {
        if (!fields.TryGetProperty(key, out var v)) return 0;
        if (v.TryGetProperty("integerValue", out var i) && long.TryParse(i.GetString(), out var l)) return l;
        if (v.TryGetProperty("doubleValue", out var d) && d.TryGetDouble(out var dbl)) return (long)dbl;
        return 0;
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement fields, string key) =>
        fields.TryGetProperty(key, out var v) && v.TryGetProperty("timestampValue", out var t)
        && DateTimeOffset.TryParse(t.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto)
            ? dto
            : null;

    private static IEnumerable<JsonElement> ReadArray(JsonElement fields, string key)
    {
        if (fields.TryGetProperty(key, out var v) && v.TryGetProperty("arrayValue", out var a)
            && a.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Array)
            return values.EnumerateArray().ToList();
        return [];
    }

    private static ClubPlayerStats ReadStats(JsonElement fields, string key)
    {
        if (!fields.TryGetProperty(key, out var v) || !v.TryGetProperty("mapValue", out var mv)
            || !mv.TryGetProperty("fields", out var sf))
            return new ClubPlayerStats();
        return StatsFrom(sf);
    }

    private static ClubPlayerStats StatsFrom(JsonElement sf) => new()
    {
        Matches = (int)ReadNumber(sf, "matches"),
        Goals = (int)ReadNumber(sf, "goals"),
        Assists = (int)ReadNumber(sf, "assists"),
        Potm = (int)ReadNumber(sf, "potm"),
        Minutes = (int)ReadNumber(sf, "minutes"),
    };

    private static Dictionary<string, ClubPlayerStats> ReadStatsMap(JsonElement fields, string key)
    {
        var map = new Dictionary<string, ClubPlayerStats>();
        if (!fields.TryGetProperty(key, out var v) || !v.TryGetProperty("mapValue", out var mv)
            || !mv.TryGetProperty("fields", out var tf))
            return map;
        foreach (var team in tf.EnumerateObject())
        {
            if (team.Value.TryGetProperty("mapValue", out var tm) && tm.TryGetProperty("fields", out var sf))
                map[team.Name] = StatsFrom(sf);
        }
        return map;
    }
}
