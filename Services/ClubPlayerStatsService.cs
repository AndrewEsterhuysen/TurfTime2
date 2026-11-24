using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TurfTime2.Helpers;
using TurfTime2.Models;

namespace TurfTime2.Services;

/// <inheritdoc cref="IClubPlayerStatsService"/>
public sealed class ClubPlayerStatsService : IClubPlayerStatsService
{
    private const string FirebaseProjectId = "turf-timer";
    private const int PageSize = 200;
    private static readonly HttpClient RestHttp = new();

    private readonly IFirebaseAuthService _auth;
    private readonly ICloudTeamService _teams;
    private readonly ISessionStorageService _sessions;

    public ClubPlayerStatsService(IFirebaseAuthService auth, ICloudTeamService teams, ISessionStorageService sessions)
    {
        _auth = auth;
        _teams = teams;
        _sessions = sessions;
    }

    public static string MatchPath(string clubId, string playerId, string sessionId) =>
        $"clubs/{clubId}/players/{playerId}/matches/{sessionId}";

    public static string MatchesPath(string clubId, string playerId) =>
        $"clubs/{clubId}/players/{playerId}/matches";

    private static string RestBase =>
        $"https://firestore.googleapis.com/v1/projects/{FirebaseProjectId}/databases/(default)/documents";

    private static string RestUrl(string path) =>
        $"{RestBase}/{string.Join('/', path.Split('/').Select(Uri.EscapeDataString))}";

    public async Task RecordSessionAsync(string teamId, GameSession session, IReadOnlySet<string>? potmPlayerIds = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(teamId) || session is null) return;
        try
        {
            var clubId = await ResolveClubIdAsync(teamId, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(clubId)) return;

            var season = await ResolveSeasonAsync(teamId, session, cancellationToken).ConfigureAwait(false);
            var teamName = string.IsNullOrWhiteSpace(session.TeamName)
                ? Preferences.Get("team_name", "")
                : session.TeamName;
            var rows = ClubPlayerMatchBuilder.FromSession(session, clubId, teamId, teamName, season, potmPlayerIds);
            if (rows.Count == 0) return;

            var token = await GetTokenAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(token)) return;
            var uid = await _auth.EnsureSignedInAsync().ConfigureAwait(false) ?? "";
            var now = DateTimeOffset.UtcNow;

            foreach (var row in rows)
            {
                var ok = await UpsertMatchAsync(row, uid, now, token, cancellationToken).ConfigureAwait(false);
                if (!ok)
                    System.Diagnostics.Debug.WriteLine($"[PlayerStats] upsert failed player={row.PlayerId} session={row.SessionId}");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PlayerStats] RecordSession: {ex.Message}");
        }
    }

    public async Task ApplyPotmAsync(string teamId, string sessionId, IEnumerable<int> winnerSlotIds,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(teamId) || string.IsNullOrWhiteSpace(sessionId)) return;
        try
        {
            var session = await _sessions.LoadSessionAsync(teamId, sessionId).ConfigureAwait(false);
            if (session is null) return;
            var potm = ClubPlayerMatchBuilder.PotmPlayerIds(session, winnerSlotIds);
            await RecordSessionAsync(teamId, session, potm, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PlayerStats] ApplyPotm: {ex.Message}");
        }
    }

    public async Task<ClubPlayerMatchListResult> ListMatchesAsync(string clubId, string playerId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clubId) || string.IsNullOrWhiteSpace(playerId))
            return new ClubPlayerMatchListResult { Reachable = false, Error = "Missing player." };
        try
        {
            if (await _auth.EnsureSignedInAsync().ConfigureAwait(false) is null)
                return new ClubPlayerMatchListResult { Reachable = false, Error = "Not signed in." };
            var token = await GetTokenAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(token))
                return new ClubPlayerMatchListResult { Reachable = false, Error = "Not signed in." };

            var matches = new List<ClubPlayerMatch>();
            string? pageToken = null;
            do
            {
                var url = $"{RestUrl(MatchesPath(clubId, playerId))}?pageSize={PageSize}"
                          + (pageToken is null ? "" : $"&pageToken={Uri.EscapeDataString(pageToken)}");
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var resp = await RestHttp.SendAsync(req, cancellationToken).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                FirestoreUsageMeter.RecordRestBody("GET", url, body);
                if (resp.StatusCode == HttpStatusCode.Forbidden)
                    return new ClubPlayerMatchListResult { Reachable = true, Error = "Only club admins and coaches can see player stats." };
                if (!resp.IsSuccessStatusCode)
                    return new ClubPlayerMatchListResult { Reachable = false, Error = $"Could not load stats ({(int)resp.StatusCode})." };

                using var json = JsonDocument.Parse(body);
                if (json.RootElement.TryGetProperty("documents", out var docs))
                {
                    foreach (var d in docs.EnumerateArray())
                    {
                        var m = ParseMatch(d);
                        if (m is not null) matches.Add(m);
                    }
                }
                pageToken = json.RootElement.TryGetProperty("nextPageToken", out var npt) ? npt.GetString() : null;
            } while (!string.IsNullOrEmpty(pageToken));

            matches.Sort((a, b) => b.MatchDate.CompareTo(a.MatchDate));
            return new ClubPlayerMatchListResult { Reachable = true, Matches = matches };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PlayerStats] List: {ex.Message}");
            return new ClubPlayerMatchListResult { Reachable = false, Error = "Could not reach the server. Check your connection." };
        }
    }

    private async Task<string> ResolveClubIdAsync(string teamId, CancellationToken ct)
    {
        var cached = Preferences.Get($"{teamId}_club_id", "");
        if (!string.IsNullOrWhiteSpace(cached)) return cached;

        var token = await GetTokenAsync().ConfigureAwait(false);
        if (string.IsNullOrEmpty(token)) return "";
        var path = $"teams/{teamId}/metadata/info";
        using var req = new HttpRequestMessage(HttpMethod.Get, RestUrl(path));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await RestHttp.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        FirestoreUsageMeter.RecordRestBody("GET", path, body);
        if (!resp.IsSuccessStatusCode) return "";
        using var json = JsonDocument.Parse(body);
        if (!json.RootElement.TryGetProperty("fields", out var f)) return "";
        return ReadString(f, "clubId");
    }

    private async Task<int> ResolveSeasonAsync(string teamId, GameSession session, CancellationToken ct)
    {
        try
        {
            var settings = await _teams.GetTeamAgeSettingsAsync(teamId).ConfigureAwait(false);
            if (settings is { Season: >= 2000 }) return settings.Season;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PlayerStats] season: {ex.Message}");
        }
        if (int.TryParse(Preferences.Get($"{teamId}_season", ""), out var cached) && cached >= 2000)
            return cached;
        return session.StartTime.Year;
    }

    private async Task<bool> UpsertMatchAsync(ClubPlayerMatch row, string uid, DateTimeOffset now, string token,
        CancellationToken ct)
    {
        var fields = new Dictionary<string, object>
        {
            ["sessionId"] = Str(row.SessionId),
            ["playerId"] = Str(row.PlayerId),
            ["clubId"] = Str(row.ClubId),
            ["teamId"] = Str(row.TeamId),
            ["teamName"] = Str(row.TeamName),
            ["matchDate"] = Ts(row.MatchDate),
            ["season"] = Int(row.Season),
            ["minutes"] = Int(row.Minutes),
            ["secondsPlayed"] = Int(row.EffectiveSeconds),
            ["goals"] = Int(row.Goals),
            ["assists"] = Int(row.Assists),
            ["potm"] = new Dictionary<string, object> { ["booleanValue"] = row.Potm },
            ["playerName"] = Str(row.PlayerName),
            ["updatedAt"] = Ts(now),
            ["updatedBy"] = Str(uid),
        };
        var ok = await PatchMatchAsync(row, fields, token, ct).ConfigureAwait(false);
        // Current production rules (before the secondsPlayed deploy) reject the new
        // field via hasOnly. Retry without it so goals/assists still save.
        if (!ok && fields.Remove("secondsPlayed"))
            ok = await PatchMatchAsync(row, fields, token, ct).ConfigureAwait(false);
        return ok;
    }

    private async Task<bool> PatchMatchAsync(ClubPlayerMatch row, Dictionary<string, object> fields,
        string token, CancellationToken ct)
    {
        var path = MatchPath(row.ClubId, row.PlayerId, row.SessionId);
        var mask = string.Join("&", fields.Keys.Select(k => $"updateMask.fieldPaths={Uri.EscapeDataString(k)}"));
        var url = $"{RestUrl(path)}?{mask}";
        using var patch = new HttpRequestMessage(HttpMethod.Patch, url);
        patch.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        patch.Content = new StringContent(JsonSerializer.Serialize(new { fields }), Encoding.UTF8, "application/json");
        using var resp = await RestHttp.SendAsync(patch, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        FirestoreUsageMeter.RecordRestBody("PATCH", url, body);
        if (!resp.IsSuccessStatusCode)
            System.Diagnostics.Debug.WriteLine($"[PlayerStats] PATCH {(int)resp.StatusCode}: {body[..Math.Min(200, body.Length)]}");
        return resp.IsSuccessStatusCode;
    }

    private async Task<string?> GetTokenAsync()
    {
        var t = await _auth.GetIdTokenAsync(forceRefresh: false).ConfigureAwait(false);
        if (string.IsNullOrEmpty(t))
            t = await _auth.GetIdTokenAsync(forceRefresh: true).ConfigureAwait(false);
        return t;
    }

    internal static ClubPlayerMatch? ParseMatch(JsonElement doc)
    {
        if (!doc.TryGetProperty("fields", out var f)) return null;
        var sessionId = ReadString(f, "sessionId");
        var playerId = ReadString(f, "playerId");
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(playerId))
        {
            var name = doc.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var id = name[(name.LastIndexOf('/') + 1)..];
            if (string.IsNullOrWhiteSpace(sessionId)) sessionId = id;
        }
        return new ClubPlayerMatch
        {
            SessionId = sessionId,
            PlayerId = playerId,
            ClubId = ReadString(f, "clubId"),
            TeamId = ReadString(f, "teamId"),
            TeamName = ReadString(f, "teamName"),
            MatchDate = ReadTimestamp(f, "matchDate") ?? DateTimeOffset.MinValue,
            Season = (int)ReadNumber(f, "season"),
            Minutes = (int)ReadNumber(f, "minutes"),
            SecondsPlayed = ReadOptionalInt(f, "secondsPlayed"),
            Goals = (int)ReadNumber(f, "goals"),
            Assists = (int)ReadNumber(f, "assists"),
            Potm = ReadBool(f, "potm"),
            PlayerName = ReadString(f, "playerName"),
        };
    }

    private static Dictionary<string, object> Str(string? v) => new() { ["stringValue"] = v ?? "" };
    private static Dictionary<string, object> Int(int v) =>
        new() { ["integerValue"] = v.ToString(CultureInfo.InvariantCulture) };
    private static Dictionary<string, object> Ts(DateTimeOffset v) =>
        new() { ["timestampValue"] = v.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) };

    private static string ReadString(JsonElement fields, string key) =>
        fields.TryGetProperty(key, out var v) && v.TryGetProperty("stringValue", out var s) ? s.GetString() ?? "" : "";

    private static long ReadNumber(JsonElement fields, string key) => ReadOptionalInt(fields, key) ?? 0;

    private static int? ReadOptionalInt(JsonElement fields, string key)
    {
        if (!fields.TryGetProperty(key, out var v)) return null;
        if (v.TryGetProperty("integerValue", out var i) && long.TryParse(i.GetString(), out var l)) return (int)l;
        if (v.TryGetProperty("doubleValue", out var d) && d.TryGetDouble(out var dbl)) return (int)dbl;
        return null;
    }

    private static bool ReadBool(JsonElement fields, string key) =>
        fields.TryGetProperty(key, out var v) && v.TryGetProperty("booleanValue", out var b) && b.GetBoolean();

    private static DateTimeOffset? ReadTimestamp(JsonElement fields, string key) =>
        fields.TryGetProperty(key, out var v) && v.TryGetProperty("timestampValue", out var t)
        && DateTimeOffset.TryParse(t.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto)
            ? dto
            : null;
}
