using System.Text;
using System.Text.Json;
using Plugin.Firebase.Firestore;
using TurfTime2.Helpers;
using TurfTime2.Models;

namespace TurfTime2.Services;

/// <summary>
/// Local Preferences + Firestore <c>teams/{teamId}/details/location</c>.
/// Watch uses SDK listener with REST fallback (same pattern as roster).
/// </summary>
public sealed class MatchScheduleService : IMatchScheduleService
{
    private const string FirebaseProjectId = "turf-timer";
    private const string DocPathSuffix = "details/location";
    private static readonly HttpClient RestHttp = new();

    private readonly IFirebaseAuthService _auth;
    private readonly IFirebaseFirestore _db;

    private CancellationTokenSource? _debounceCts;
    private IDisposable? _watchRegistration;
    private string? _watchTeamId;

    public MatchScheduleService(IFirebaseAuthService auth, IFirebaseFirestore db)
    {
        _auth = auth;
        _db = db;
    }

    public MatchSchedule? LoadLocal(string teamId)
    {
        if (string.IsNullOrWhiteSpace(teamId)) return null;

        try
        {
            var matchDate = Preferences.Get(TeamKey(teamId, "setup_match_date"), string.Empty);
            var matchTime = Preferences.Get(TeamKey(teamId, "setup_match_time"), string.Empty);
            var arriveTime = Preferences.Get(TeamKey(teamId, "setup_arrive_time"), string.Empty);
            var locationName = Preferences.Get(TeamKey(teamId, "setup_location_name"), string.Empty);
            var latitude = Preferences.Get(TeamKey(teamId, "setup_latitude"), string.Empty);
            var longitude = Preferences.Get(TeamKey(teamId, "setup_longitude"), string.Empty);
            var mapsLink = Preferences.Get(TeamKey(teamId, "setup_maps_link"), string.Empty);
            var lastMod = Preferences.Get(TeamKey(teamId, "setup_schedule_last_modified_utc"), string.Empty);
            var byUid = Preferences.Get(TeamKey(teamId, "setup_schedule_updated_by_uid"), string.Empty);
            var byName = Preferences.Get(TeamKey(teamId, "setup_schedule_updated_by_name"), string.Empty);

            var s = new MatchSchedule
            {
                TeamId = teamId,
                MatchDate = matchDate,
                MatchTime = matchTime,
                ArriveTime = arriveTime,
                LocationName = locationName,
                Latitude = latitude,
                Longitude = longitude,
                MapsLink = mapsLink,
                UpdatedByUid = string.IsNullOrWhiteSpace(byUid) ? null : byUid,
                UpdatedByDisplayName = string.IsNullOrWhiteSpace(byName) ? null : byName,
                FromCloud = false,
                IsOfflineCache = false
            };

            if (DateTimeOffset.TryParse(lastMod, out var lm))
                s.LastModifiedUtc = lm;

            if (!MatchScheduleEvaluator.HasAnyContent(s) && s.LastModifiedUtc == default)
                return null;

            return s;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MatchSchedule] LoadLocal: {ex.Message}");
            return null;
        }
    }

    public void SaveLocal(string teamId, MatchSchedule schedule)
    {
        if (string.IsNullOrWhiteSpace(teamId) || schedule is null) return;

        try
        {
            schedule.TeamId = teamId;
            Preferences.Set(TeamKey(teamId, "setup_match_date"), schedule.MatchDate ?? string.Empty);
            Preferences.Set(TeamKey(teamId, "setup_match_time"), schedule.MatchTime ?? string.Empty);
            Preferences.Set(TeamKey(teamId, "setup_arrive_time"), schedule.ArriveTime ?? string.Empty);
            Preferences.Set(TeamKey(teamId, "setup_location_name"), schedule.LocationName ?? string.Empty);
            Preferences.Set(TeamKey(teamId, "setup_latitude"), schedule.Latitude ?? string.Empty);
            Preferences.Set(TeamKey(teamId, "setup_longitude"), schedule.Longitude ?? string.Empty);
            Preferences.Set(TeamKey(teamId, "setup_maps_link"), schedule.MapsLink ?? string.Empty);

            if (schedule.LastModifiedUtc != default)
                Preferences.Set(TeamKey(teamId, "setup_schedule_last_modified_utc"), schedule.LastModifiedUtc.ToString("o"));
            Preferences.Set(TeamKey(teamId, "setup_schedule_updated_by_uid"), schedule.UpdatedByUid ?? string.Empty);
            Preferences.Set(TeamKey(teamId, "setup_schedule_updated_by_name"), schedule.UpdatedByDisplayName ?? string.Empty);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MatchSchedule] SaveLocal: {ex.Message}");
        }
    }

    public async Task SaveAsync(string teamId, MatchSchedule schedule, bool isAdmin)
    {
        if (schedule.LastModifiedUtc == default)
            schedule.LastModifiedUtc = DateTimeOffset.UtcNow;

        SaveLocal(teamId, schedule);

        if (!isAdmin || IsLocalOnlyTeam(teamId))
            return;

        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;
        try
        {
            await Task.Delay(800, token).ConfigureAwait(false);
            await UploadAsync(teamId, schedule).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* superseded */ }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MatchSchedule] SaveAsync cloud: {ex.Message}");
        }
    }

    public async Task ForceSyncAsync(string teamId, MatchSchedule schedule)
    {
        _debounceCts?.Cancel();
        if (schedule.LastModifiedUtc == default)
            schedule.LastModifiedUtc = DateTimeOffset.UtcNow;
        SaveLocal(teamId, schedule);
        if (IsLocalOnlyTeam(teamId)) return;
        try
        {
            await UploadAsync(teamId, schedule).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MatchSchedule] ForceSync: {ex.Message}");
        }
    }

    public async Task<MatchSchedule?> LoadAsync(string teamId, bool preferCloud = false)
    {
        var local = LoadLocal(teamId);

        if (IsLocalOnlyTeam(teamId))
            return local;

        try
        {
            var cloud = await DownloadAsync(teamId).ConfigureAwait(false);
            if (cloud is null)
            {
                if (local is not null)
                    local.IsOfflineCache = true;
                return local;
            }

            cloud.FromCloud = true;
            SaveLocal(teamId, cloud);

            if (local is null || preferCloud)
                return cloud;

            if (cloud.LastModifiedUtc >= local.LastModifiedUtc)
                return cloud;

            return local;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MatchSchedule] LoadAsync: {ex.Message}");
            if (local is not null)
                local.IsOfflineCache = true;
            return local;
        }
    }

    public IDisposable? WatchSchedule(string teamId, Action<MatchSchedule> onUpdate)
    {
        if (string.IsNullOrWhiteSpace(teamId) || IsLocalOnlyTeam(teamId))
            return null;

        StopWatch();

        try
        {
            _watchTeamId = teamId;
            var doc = _db.GetDocument($"teams/{teamId}/{DocPathSuffix}");
            _watchRegistration = doc.AddSnapshotListener<Dictionary<string, object>>(
                snap =>
                {
                    try
                    {
                        MatchSchedule? schedule = null;
                        if (snap?.Data is not null)
                            schedule = FromDictionary(teamId, snap.Data);

                        if (schedule is not null && MatchScheduleEvaluator.HasAnyContent(schedule))
                        {
                            schedule.FromCloud = true;
                            SaveLocal(teamId, schedule);
                            onUpdate(schedule);
                        }

                        _ = DeliverViaRestAsync(teamId, onUpdate);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MatchSchedule] Watch: {ex.Message}");
                        _ = DeliverViaRestAsync(teamId, onUpdate);
                    }
                },
                error =>
                {
                    System.Diagnostics.Debug.WriteLine($"[MatchSchedule] Watch error: {error.Message}");
                    _ = DeliverViaRestAsync(teamId, onUpdate);
                });

            System.Diagnostics.Debug.WriteLine($"[MatchSchedule] Watching teams/{teamId}/{DocPathSuffix}");
            return new WatchHandle(this);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MatchSchedule] WatchSchedule failed: {ex.Message}");
            return null;
        }
    }

    public async Task WarmUpAsync()
    {
        try { await _auth.EnsureSignedInAsync().ConfigureAwait(false); }
        catch { /* best-effort */ }
    }

    // ── Cloud I/O ─────────────────────────────────────────────────────────

    private async Task UploadAsync(string teamId, MatchSchedule schedule)
    {
        if (await _auth.EnsureSignedInAsync().ConfigureAwait(false) is null)
            return;

        try
        {
            await UploadViaRestAsync(teamId, schedule).ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MatchSchedule] REST upload: {ex.Message}");
        }

        try
        {
            var payload = ToFirestorePayload(schedule);
            await _db.GetDocument($"teams/{teamId}/{DocPathSuffix}")
                .SetDataAsync(payload)
                .ConfigureAwait(false);
            System.Diagnostics.Debug.WriteLine($"[MatchSchedule] SDK SetData team={teamId}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MatchSchedule] SDK upload: {ex.Message}");
        }
    }

    private async Task UploadViaRestAsync(string teamId, MatchSchedule schedule)
    {
        var idToken = await _auth.GetIdTokenAsync(forceRefresh: false).ConfigureAwait(false);
        if (string.IsNullOrEmpty(idToken))
            idToken = await _auth.GetIdTokenAsync(forceRefresh: true).ConfigureAwait(false);
        if (string.IsNullOrEmpty(idToken))
            throw new InvalidOperationException("No Firebase id token for schedule upload");

        var url =
            $"https://firestore.googleapis.com/v1/projects/{FirebaseProjectId}/databases/(default)/documents/teams/{Uri.EscapeDataString(teamId)}/{DocPathSuffix}";

        var ts = schedule.LastModifiedUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        var fields = new Dictionary<string, object>
        {
            ["schemaVersion"] = new Dictionary<string, object> { ["integerValue"] = schedule.SchemaVersion.ToString() },
            ["matchDate"] = new Dictionary<string, object> { ["stringValue"] = schedule.MatchDate ?? "" },
            ["matchTime"] = new Dictionary<string, object> { ["stringValue"] = schedule.MatchTime ?? "" },
            ["arriveTime"] = new Dictionary<string, object> { ["stringValue"] = schedule.ArriveTime ?? "" },
            ["locationName"] = new Dictionary<string, object> { ["stringValue"] = schedule.LocationName ?? "" },
            ["latitude"] = new Dictionary<string, object> { ["stringValue"] = schedule.Latitude ?? "" },
            ["longitude"] = new Dictionary<string, object> { ["stringValue"] = schedule.Longitude ?? "" },
            ["mapsLink"] = new Dictionary<string, object> { ["stringValue"] = schedule.MapsLink ?? "" },
            ["lastModifiedUtc"] = new Dictionary<string, object> { ["timestampValue"] = ts },
            ["updatedByUid"] = new Dictionary<string, object> { ["stringValue"] = schedule.UpdatedByUid ?? "" },
            ["updatedByDisplayName"] = new Dictionary<string, object> { ["stringValue"] = schedule.UpdatedByDisplayName ?? "" }
        };

        var body = JsonSerializer.Serialize(new { fields });
        var fieldNames = new[]
        {
            "schemaVersion", "matchDate", "matchTime", "arriveTime", "locationName",
            "latitude", "longitude", "mapsLink", "lastModifiedUtc", "updatedByUid", "updatedByDisplayName"
        };
        var mask = string.Join("&", fieldNames.Select(f => $"updateMask.fieldPaths={Uri.EscapeDataString(f)}"));
        var patchUrl = url + "?" + mask;

        using var patch = new HttpRequestMessage(HttpMethod.Patch, patchUrl);
        patch.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", idToken);
        patch.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var resp = await RestHttp.SendAsync(patch).ConfigureAwait(false);
        var respBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Schedule upload {(int)resp.StatusCode}: {respBody[..Math.Min(200, respBody.Length)]}");
        }

        System.Diagnostics.Debug.WriteLine($"[MatchSchedule] REST upload OK team={teamId}");
    }

    private async Task<MatchSchedule?> DownloadAsync(string teamId)
    {
        if (await _auth.EnsureSignedInAsync().ConfigureAwait(false) is null)
            return null;

        try
        {
            var rest = await DownloadViaRestAsync(teamId).ConfigureAwait(false);
            if (rest is not null)
                return rest;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MatchSchedule] REST download: {ex.Message}");
        }

        try
        {
            var snap = await _db.GetDocument($"teams/{teamId}/{DocPathSuffix}")
                .GetDocumentSnapshotAsync<Dictionary<string, object>>()
                .ConfigureAwait(false);
            if (snap?.Data is not null)
                return FromDictionary(teamId, snap.Data);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MatchSchedule] SDK download: {ex.Message}");
        }

        return null;
    }

    private async Task<MatchSchedule?> DownloadViaRestAsync(string teamId)
    {
        var idToken = await _auth.GetIdTokenAsync(forceRefresh: false).ConfigureAwait(false);
        if (string.IsNullOrEmpty(idToken))
            idToken = await _auth.GetIdTokenAsync(forceRefresh: true).ConfigureAwait(false);
        if (string.IsNullOrEmpty(idToken)) return null;

        var url =
            $"https://firestore.googleapis.com/v1/projects/{FirebaseProjectId}/databases/(default)/documents/teams/{Uri.EscapeDataString(teamId)}/{DocPathSuffix}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", idToken);

        using var resp = await RestHttp.SendAsync(req).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[MatchSchedule] REST download {(int)resp.StatusCode}: {body[..Math.Min(160, body.Length)]}");
            return null;
        }

        using var json = JsonDocument.Parse(body);
        if (!json.RootElement.TryGetProperty("fields", out var fields))
            return null;

        return FromRestFields(teamId, fields);
    }

    private async Task DeliverViaRestAsync(string teamId, Action<MatchSchedule> onUpdate)
    {
        if (!string.Equals(_watchTeamId, teamId, StringComparison.Ordinal))
            return;

        try
        {
            var rest = await DownloadViaRestAsync(teamId).ConfigureAwait(false);
            if (rest is null) return;
            if (!string.Equals(_watchTeamId, teamId, StringComparison.Ordinal))
                return;

            rest.FromCloud = true;
            SaveLocal(teamId, rest);
            onUpdate(rest);
            System.Diagnostics.Debug.WriteLine($"[MatchSchedule] REST watch deliver team={teamId}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MatchSchedule] REST watch: {ex.Message}");
        }
    }

    private void StopWatch()
    {
        try { _watchRegistration?.Dispose(); }
        catch { /* ignore */ }
        _watchRegistration = null;
        _watchTeamId = null;
    }

    private sealed class WatchHandle : IDisposable
    {
        private MatchScheduleService? _owner;
        public WatchHandle(MatchScheduleService owner) => _owner = owner;
        public void Dispose()
        {
            var o = Interlocked.Exchange(ref _owner, null);
            o?.StopWatch();
        }
    }

    // ── Mapping ───────────────────────────────────────────────────────────

    private static Dictionary<string, object?> ToFirestorePayload(MatchSchedule s) => new()
    {
        ["schemaVersion"] = s.SchemaVersion,
        ["matchDate"] = s.MatchDate ?? "",
        ["matchTime"] = s.MatchTime ?? "",
        ["arriveTime"] = s.ArriveTime ?? "",
        ["locationName"] = s.LocationName ?? "",
        ["latitude"] = s.Latitude ?? "",
        ["longitude"] = s.Longitude ?? "",
        ["mapsLink"] = s.MapsLink ?? "",
        ["lastModifiedUtc"] = s.LastModifiedUtc.ToUniversalTime().ToString("o"),
        ["updatedByUid"] = s.UpdatedByUid ?? "",
        ["updatedByDisplayName"] = s.UpdatedByDisplayName ?? ""
    };

    private static MatchSchedule? FromDictionary(string teamId, IDictionary<string, object> data)
    {
        try
        {
            var s = new MatchSchedule
            {
                TeamId = teamId,
                SchemaVersion = ReadInt(data, "schemaVersion", MatchSchedule.CurrentSchemaVersion),
                MatchDate = ReadString(data, "matchDate"),
                MatchTime = ReadString(data, "matchTime"),
                ArriveTime = ReadString(data, "arriveTime"),
                LocationName = ReadString(data, "locationName"),
                Latitude = ReadString(data, "latitude"),
                Longitude = ReadString(data, "longitude"),
                MapsLink = ReadString(data, "mapsLink"),
                UpdatedByUid = NullIfEmpty(ReadString(data, "updatedByUid")),
                UpdatedByDisplayName = NullIfEmpty(ReadString(data, "updatedByDisplayName")),
                FromCloud = true
            };

            if (data.TryGetValue("lastModifiedUtc", out var raw) && raw is not null)
            {
                if (raw is DateTimeOffset dtoRaw)
                    s.LastModifiedUtc = dtoRaw;
                else if (raw is DateTime dt)
                    s.LastModifiedUtc = new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
                else if (DateTimeOffset.TryParse(raw.ToString(), out var dtoParsed))
                    s.LastModifiedUtc = dtoParsed;
            }

            return s;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MatchSchedule] FromDictionary: {ex.Message}");
            return null;
        }
    }

    private static MatchSchedule? FromRestFields(string teamId, JsonElement fields)
    {
        try
        {
            var s = new MatchSchedule
            {
                TeamId = teamId,
                SchemaVersion = ReadRestInt(fields, "schemaVersion", MatchSchedule.CurrentSchemaVersion),
                MatchDate = ReadRestString(fields, "matchDate"),
                MatchTime = ReadRestString(fields, "matchTime"),
                ArriveTime = ReadRestString(fields, "arriveTime"),
                LocationName = ReadRestString(fields, "locationName"),
                Latitude = ReadRestString(fields, "latitude"),
                Longitude = ReadRestString(fields, "longitude"),
                MapsLink = ReadRestString(fields, "mapsLink"),
                UpdatedByUid = NullIfEmpty(ReadRestString(fields, "updatedByUid")),
                UpdatedByDisplayName = NullIfEmpty(ReadRestString(fields, "updatedByDisplayName")),
                FromCloud = true
            };

            var ts = ReadRestTimestamp(fields, "lastModifiedUtc");
            if (ts is not null)
                s.LastModifiedUtc = ts.Value;
            else if (DateTimeOffset.TryParse(ReadRestString(fields, "lastModifiedUtc"), out var dto))
                s.LastModifiedUtc = dto;

            return s;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MatchSchedule] FromRestFields: {ex.Message}");
            return null;
        }
    }

    private static string TeamKey(string teamId, string baseKey) => $"{baseKey}_{teamId}";

    private static bool IsLocalOnlyTeam(string teamId)
    {
        if (string.IsNullOrWhiteSpace(teamId)) return true;
        if (teamId.StartsWith("local_", StringComparison.Ordinal)) return true;
        var mode = Preferences.Get("team_mode", string.Empty);
        return string.Equals(mode, "local", StringComparison.Ordinal);
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static string ReadString(IDictionary<string, object> data, string key)
    {
        if (!data.TryGetValue(key, out var v) || v is null) return string.Empty;
        return v.ToString() ?? string.Empty;
    }

    private static int ReadInt(IDictionary<string, object> data, string key, int fallback)
    {
        if (!data.TryGetValue(key, out var v) || v is null) return fallback;
        if (v is int i) return i;
        if (v is long l) return (int)l;
        if (int.TryParse(v.ToString(), out var p)) return p;
        return fallback;
    }

    private static string ReadRestString(JsonElement fields, string key)
    {
        if (!fields.TryGetProperty(key, out var el)) return string.Empty;
        if (el.TryGetProperty("stringValue", out var s)) return s.GetString() ?? string.Empty;
        return string.Empty;
    }

    private static int ReadRestInt(JsonElement fields, string key, int fallback)
    {
        if (!fields.TryGetProperty(key, out var el)) return fallback;
        if (el.TryGetProperty("integerValue", out var iv) && int.TryParse(iv.GetString(), out var n))
            return n;
        return fallback;
    }

    private static DateTimeOffset? ReadRestTimestamp(JsonElement fields, string key)
    {
        if (!fields.TryGetProperty(key, out var el)) return null;
        if (el.TryGetProperty("timestampValue", out var ts) &&
            DateTimeOffset.TryParse(ts.GetString(), out var dto))
            return dto;
        return null;
    }
}
