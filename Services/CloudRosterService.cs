using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TurfTime2.Models;

namespace TurfTime2.Services;

/// <summary>
/// Persists the roster snapshot to local <see cref="Preferences"/> and to
/// Firestore via the REST API. Authentication uses Firebase anonymous sign-in.
/// </summary>
public sealed class CloudRosterService : ICloudRosterService
{
    private const string FirebaseApiKey    = "AIzaSyDAKivCFX5kYYZ6SkAQluBNdR92I320glk";
    private const string FirebaseProjectId = "turf-timer";
    private const string FirestoreBase     =
        $"https://firestore.googleapis.com/v1/projects/{FirebaseProjectId}/databases/(default)/documents";

    private static readonly HttpClient _http = new();
    private static string? _idToken;
    private static string? _userId;

    // Debounce: cloud save fires at most once per 2 s of silence
    private CancellationTokenSource? _debounceCts;

    /// <summary>
    /// Shares an already-authenticated token (e.g. from TeamDetailsPage) so roster
    /// writes use the same Firebase identity as team create/join.
    /// </summary>
    public static void SetAuthToken(string idToken, string userId)
    {
        if (string.IsNullOrWhiteSpace(idToken)) return;
        _idToken = idToken;
        _userId  = userId;
        System.Diagnostics.Debug.WriteLine("[CloudRosterService] Auth token received from host");
    }

    // ── ICloudRosterService ───────────────────────────────────────────────

    public async Task SaveAsync(string teamId, RosterSnapshot snapshot, bool isAdmin)
    {
        // Serialize and persist locally on a background thread — Preferences.Set
        // calls SharedPreferences.commit() on Android which is synchronous disk I/O
        // and blocks the UI thread if called directly.
        _ = Task.Run(() => SaveLocal(teamId, snapshot));

        if (!isAdmin) return;                          // members never write to cloud
        if (IsLocalOnlyTeam(teamId)) return;

        // Debounce the cloud write (2 s)
        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;
        try
        {
            await Task.Delay(2000, token).ConfigureAwait(false);
            await UploadToFirestoreAsync(teamId, snapshot).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* superseded by newer save */ }
    }

    public async Task<RosterSnapshot?> LoadAsync(string teamId)
    {
        var local = LoadLocal(teamId);

        if (IsLocalOnlyTeam(teamId))
            return local;

        try
        {
            var cloud = await DownloadFromFirestoreAsync(teamId).ConfigureAwait(false);
            if (cloud is null) return local;

            if (local is null) return cloud;

            // Timestamp-based conflict resolution: use whichever is newer
            return cloud.LastModifiedUtc > local.LastModifiedUtc ? cloud : local;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CloudRosterService] Load cloud failed: {ex.GetType().FullName}: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[CloudRosterService] Stack: {ex.StackTrace}");
            return local;
        }
    }

    public Task ForceSyncAsync(string teamId, RosterSnapshot snapshot)
    {
        _debounceCts?.Cancel();
        SaveLocal(teamId, snapshot);

        if (IsLocalOnlyTeam(teamId)) return Task.CompletedTask;

        return UploadToFirestoreAsync(teamId, snapshot);
    }

    /// <summary>Pre-warms the Firebase auth token on a background thread.</summary>
    public async Task WarmUpAsync()
    {
        try { await Task.Run(GetAuthTokenAsync).ConfigureAwait(false); }
        catch { /* warm-up is best-effort */ }
    }

    // ── Local storage ─────────────────────────────────────────────────────

    private static bool IsLocalOnlyTeam(string teamId)
    {
        if (string.IsNullOrWhiteSpace(teamId)) return true;
        if (teamId.StartsWith("local_", StringComparison.Ordinal)) return true;
        // Prefer explicit team_mode when set; local_ prefix is authoritative for device teams.
        var mode = Preferences.Get("team_mode", string.Empty);
        return string.Equals(mode, "local", StringComparison.Ordinal);
    }

    private static string LocalKey(string teamId) => $"roster_snapshot_{teamId}";

    private static void SaveLocal(string teamId, RosterSnapshot snapshot)
    {
        try
        {
            Preferences.Set(LocalKey(teamId), JsonSerializer.Serialize(snapshot));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CloudRosterService] Local save failed: {ex.Message}");
        }
    }

    private static RosterSnapshot? LoadLocal(string teamId)
    {
        try
        {
            var raw = Preferences.Get(LocalKey(teamId), string.Empty);
            return string.IsNullOrEmpty(raw) ? null : JsonSerializer.Deserialize<RosterSnapshot>(raw);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CloudRosterService] Local load failed: {ex.Message}");
            return null;
        }
    }

    // ── Firestore REST ────────────────────────────────────────────────────

    private static async Task<string?> GetAuthTokenAsync()
    {
        if (!string.IsNullOrEmpty(_idToken)) return _idToken;

        try
        {
            var url  = $"https://identitytoolkit.googleapis.com/v1/accounts:signUp?key={FirebaseApiKey}";
            var body = JsonSerializer.Serialize(new { returnSecureToken = true });
            var resp = await _http.PostAsync(url,
                new StringContent(body, Encoding.UTF8, "application/json")).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                System.Diagnostics.Debug.WriteLine($"[CloudRosterService] Auth token request failed: {resp.StatusCode} {err}");
                return null;
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            _idToken = doc.RootElement.GetProperty("idToken").GetString();
            _userId  = doc.RootElement.TryGetProperty("localId", out var lid) ? lid.GetString() : null;
            return _idToken;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CloudRosterService] GetAuthTokenAsync exception: {ex.GetType().FullName}: {ex.Message}");
            return null;
        }
    }

    private static async Task UploadToFirestoreAsync(string teamId, RosterSnapshot snapshot)
    {
        var token = await GetAuthTokenAsync().ConfigureAwait(false);
        if (token is null)
        {
            System.Diagnostics.Debug.WriteLine("[CloudRosterService] Auth failed — roster not saved to cloud");
            return;
        }

        var url  = $"{FirestoreBase}/teams/{teamId}/roster/data";
        var body = JsonSerializer.Serialize(ToFirestoreDocument(snapshot));

        for (int attempt = 0; attempt < 2; attempt++)
        {
            var req = new HttpRequestMessage(HttpMethod.Patch, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var resp = await _http.SendAsync(req).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                System.Diagnostics.Debug.WriteLine($"[CloudRosterService] Saved to Firestore (team {teamId})");
                return;
            }

            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized && attempt == 0)
            {
                // Token expired — force refresh
                _idToken = null;
                token    = await GetAuthTokenAsync().ConfigureAwait(false);
                if (token is null) return;
            }
            else
            {
                var err = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                System.Diagnostics.Debug.WriteLine($"[CloudRosterService] Firestore error: {err}");
                return;
            }
        }
    }

    private static async Task<RosterSnapshot?> DownloadFromFirestoreAsync(string teamId)
    {
        var token = await GetAuthTokenAsync().ConfigureAwait(false);
        if (token is null) return null;

        var url = $"{FirestoreBase}/teams/{teamId}/roster/data";
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await _http.SendAsync(req).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;

        var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        return FromFirestoreDocument(json);
    }

    // ── Firestore document conversion ─────────────────────────────────────

    /// <summary>Converts a <see cref="RosterSnapshot"/> to the Firestore REST wire format.</summary>
    private static object ToFirestoreDocument(RosterSnapshot s)
    {
        var playerValues = s.Players.Select(p => new
        {
            mapValue = new
            {
                fields = new Dictionary<string, object>
                {
                    ["slotId"]         = new { integerValue = p.SlotId.ToString() },
                    ["name"]           = new { stringValue  = p.Name },
                    ["field"]          = new { booleanValue = p.Field },
                    ["bench"]          = new { booleanValue = p.Bench },
                    ["goalie"]         = new { booleanValue = p.Goalie },
                    ["inactive"]       = new { booleanValue = p.Inactive },
                    ["counterSeconds"] = new { integerValue = p.CounterSeconds.ToString() }
                }
            }
        }).ToArray();

        var utc = s.LastModifiedUtc.ToString("o");

        // Canonical field names match RosterSnapshot. Dual keys for older bridge/JS writers.
        return new
        {
            fields = new Dictionary<string, object>
            {
                ["version"]                = new { integerValue = s.Version.ToString() },
                ["lastModifiedUtc"]        = new { timestampValue = utc },
                ["lastModified"]           = new { timestampValue = utc },
                ["matchDurationSeconds"]   = new { integerValue = s.MatchDurationSeconds.ToString() },
                ["halfDurationSeconds"]    = new { integerValue = s.HalfDurationSeconds.ToString() },
                ["matchRemainingSeconds"]  = new { integerValue = s.MatchRemainingSeconds.ToString() },
                ["currentHalf"]            = new { stringValue  = s.CurrentHalf },
                ["timerRunning"]           = new { booleanValue = s.TimerRunning },
                ["countdownPresetSeconds"] = new { integerValue = s.CountdownPresetSeconds.ToString() },
                ["countdownPreset"]        = new { integerValue = s.CountdownPresetSeconds.ToString() },
                ["viewMode"]               = new { integerValue = s.ViewMode.ToString() },
                ["teamAScore"]             = new { integerValue = s.TeamAScore.ToString() },
                ["teamBScore"]             = new { integerValue = s.TeamBScore.ToString() },
                ["players"]                = new { arrayValue   = new { values = playerValues } }
            }
        };
    }

    /// <summary>Parses Firestore REST response JSON back to <see cref="RosterSnapshot"/>.</summary>
    private static RosterSnapshot? FromFirestoreDocument(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("fields", out var fields))
                return null;

            const int defaultMatchDuration = 90 * 60;

            var snapshot = new RosterSnapshot
            {
                Version                = ReadInt(fields, "version", 2),
                LastModifiedUtc        = ReadTimestamp(fields, "lastModifiedUtc", "lastModified"),
                MatchDurationSeconds   = Math.Max(1, ReadInt(fields, "matchDurationSeconds", defaultMatchDuration)),
                HalfDurationSeconds    = ReadInt(fields, "halfDurationSeconds", 0),
                MatchRemainingSeconds  = ReadInt(fields, "matchRemainingSeconds", defaultMatchDuration),
                CurrentHalf            = ReadString(fields, "currentHalf", "setup"),
                TimerRunning           = ReadBool(fields, "timerRunning", false),
                CountdownPresetSeconds = ReadInt(fields, "countdownPresetSeconds",
                                            ReadInt(fields, "countdownPreset", 120)),
                ViewMode               = ReadInt(fields, "viewMode", 0),
                TeamAScore             = ReadInt(fields, "teamAScore", 0),
                TeamBScore             = ReadInt(fields, "teamBScore", 0)
            };

            if (fields.TryGetProperty("players", out var playersEl)
                && playersEl.TryGetProperty("arrayValue", out var arr)
                && arr.TryGetProperty("values", out var values))
            {
                foreach (var pElem in values.EnumerateArray())
                {
                    if (!pElem.TryGetProperty("mapValue", out var map)
                        || !map.TryGetProperty("fields", out var pf))
                        continue;

                    snapshot.Players.Add(new PlayerSnapshot
                    {
                        SlotId         = ReadInt(pf, "slotId", 0),
                        Name           = ReadString(pf, "name", string.Empty),
                        Field          = ReadBool(pf, "field", false),
                        Bench          = ReadBool(pf, "bench", false),
                        Goalie         = ReadBool(pf, "goalie", false),
                        Inactive       = ReadBool(pf, "inactive", false),
                        CounterSeconds = ReadInt(pf, "counterSeconds", 0)
                    });
                }
            }

            return snapshot;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CloudRosterService] Parse failed: {ex.Message}");
            return null;
        }
    }

    private static string ReadString(JsonElement fields, string name, string fallback)
    {
        if (!fields.TryGetProperty(name, out var el)) return fallback;
        if (el.TryGetProperty("stringValue", out var s)) return s.GetString() ?? fallback;
        return fallback;
    }

    private static int ReadInt(JsonElement fields, string name, int fallback)
    {
        if (!fields.TryGetProperty(name, out var el)) return fallback;
        if (el.TryGetProperty("integerValue", out var i)
            && int.TryParse(i.GetString(), out var n))
            return n;
        if (el.TryGetProperty("doubleValue", out var d))
            return (int)d.GetDouble();
        return fallback;
    }

    private static bool ReadBool(JsonElement fields, string name, bool fallback)
    {
        if (!fields.TryGetProperty(name, out var el)) return fallback;
        if (el.TryGetProperty("booleanValue", out var b)) return b.GetBoolean();
        return fallback;
    }

    private static DateTimeOffset ReadTimestamp(JsonElement fields, string primary, string alternate)
    {
        foreach (var name in new[] { primary, alternate })
        {
            if (!fields.TryGetProperty(name, out var el)) continue;

            string? raw = null;
            if (el.TryGetProperty("timestampValue", out var ts))
                raw = ts.GetString();
            else if (el.TryGetProperty("stringValue", out var s))
                raw = s.GetString();

            if (!string.IsNullOrEmpty(raw) && DateTimeOffset.TryParse(raw, out var dto))
                return dto;
        }

        return DateTimeOffset.UtcNow;
    }
}
