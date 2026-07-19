using System.Text.Json;
using Plugin.Firebase.Firestore;
using TurfTime2.Models;

namespace TurfTime2.Services;

/// <summary>
/// Persists the roster snapshot to local <see cref="Preferences"/> and to
/// Firestore via Plugin.Firebase. Nested player maps must be
/// <c>Dictionary&lt;string, object?&gt;</c> — <c>Dictionary&lt;object, object&gt;</c>
/// fails Android ToJavaObject conversion inside lists (upload then silently keeps
/// the empty create-time roster).
/// </summary>
public sealed class CloudRosterService : ICloudRosterService
{
    private const string FirebaseProjectId = "turf-timer";
    private static readonly HttpClient RestHttp = new();

    private readonly IFirebaseAuthService _auth;
    private readonly IFirebaseFirestore _db;

    private CancellationTokenSource? _debounceCts;
    private IDisposable? _watchRegistration;
    private string? _watchTeamId;

    public CloudRosterService(IFirebaseAuthService auth, IFirebaseFirestore db)
    {
        _auth = auth;
        _db = db;
    }

    /// <summary>Legacy no-op — auth is owned by <see cref="IFirebaseAuthService"/>.</summary>
    public static void SetAuthToken(string idToken, string userId)
    {
        System.Diagnostics.Debug.WriteLine("[CloudRosterService] SetAuthToken ignored (SDK auth)");
    }

    public async Task SaveAsync(string teamId, RosterSnapshot snapshot, bool isAdmin)
    {
        _ = Task.Run(() => SaveLocal(teamId, snapshot));

        if (!isAdmin) return;
        if (IsLocalOnlyTeam(teamId)) return;

        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;
        try
        {
            await Task.Delay(2000, token).ConfigureAwait(false);
            await UploadToFirestoreAsync(teamId, snapshot).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* superseded */ }
    }

    public async Task<RosterSnapshot?> LoadAsync(string teamId, bool preferCloud = false)
    {
        var local = LoadLocal(teamId);

        if (IsLocalOnlyTeam(teamId))
            return local;

        try
        {
            var cloud = await DownloadFromFirestoreAsync(teamId).ConfigureAwait(false);
            if (cloud is null) return local;
            if (local is null) return cloud;

            // Members must always prefer cloud (admin is source of truth).
            if (preferCloud) return cloud;

            // Prefer cloud when newer, or when local has no positioned players yet.
            if (cloud.LastModifiedUtc >= local.LastModifiedUtc) return cloud;
            if (!local.Players.Any(p => p.Field || p.Bench || p.Goalie || p.Inactive)
                && cloud.Players.Count > 0)
                return cloud;

            return local;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[CloudRosterService] Load cloud failed: {ex.GetType().FullName}: {ex.Message}");
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

    public async Task WarmUpAsync()
    {
        try { await _auth.EnsureSignedInAsync().ConfigureAwait(false); }
        catch { /* best-effort */ }
    }

    public IDisposable? WatchRoster(string teamId, Action<RosterSnapshot> onUpdate)
    {
        if (string.IsNullOrWhiteSpace(teamId) || IsLocalOnlyTeam(teamId))
            return null;

        StopWatch();

        try
        {
            _watchTeamId = teamId;
            var doc = _db.GetDocument($"teams/{teamId}/roster/data");
            _watchRegistration = doc.AddSnapshotListener<Dictionary<string, object>>(
                snap =>
                {
                    try
                    {
                        var data = snap?.Data;
                        if (data is null) return;
                        var roster = FromDictionary(data);
                        if (roster is null) return;
                        SaveLocal(teamId, roster);
                        onUpdate(roster);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[CloudRosterService] Watch callback: {ex.Message}");
                    }
                },
                error =>
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[CloudRosterService] Watch error: {error.Message}");
                });

            System.Diagnostics.Debug.WriteLine(
                $"[CloudRosterService] Watching teams/{teamId}/roster/data");
            return new WatchHandle(this);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[CloudRosterService] WatchRoster failed: {ex.Message}");
            return null;
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
        private CloudRosterService? _owner;
        public WatchHandle(CloudRosterService owner) => _owner = owner;
        public void Dispose()
        {
            var o = Interlocked.Exchange(ref _owner, null);
            o?.StopWatch();
        }
    }

    private static bool IsLocalOnlyTeam(string teamId)
    {
        if (string.IsNullOrWhiteSpace(teamId)) return true;
        if (teamId.StartsWith("local_", StringComparison.Ordinal)) return true;
        var mode = Preferences.Get("team_mode", string.Empty);
        return string.Equals(mode, "local", StringComparison.Ordinal);
    }

    private static string LocalKey(string teamId) => $"roster_snapshot_{teamId}";

    private static void SaveLocal(string teamId, RosterSnapshot snapshot)
    {
        try { Preferences.Set(LocalKey(teamId), JsonSerializer.Serialize(snapshot)); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[CloudRosterService] Local save failed: {ex.Message}");
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
            System.Diagnostics.Debug.WriteLine(
                $"[CloudRosterService] Local load failed: {ex.Message}");
            return null;
        }
    }

    private async Task UploadToFirestoreAsync(string teamId, RosterSnapshot snapshot)
    {
        if (await _auth.EnsureSignedInAsync().ConfigureAwait(false) is null)
        {
            System.Diagnostics.Debug.WriteLine(
                "[CloudRosterService] Auth failed — roster not saved to cloud");
            return;
        }

        try
        {
            // Dictionary<string, object?> is required so nested player maps convert
            // via Plugin.Firebase's IDictionary<string, object?> ToHashMap path.
            // Dictionary<object, object> inside a list throws ToJavaObject on Android.
            var payload = ToFirestorePayload(snapshot);
            var doc = _db.GetDocument($"teams/{teamId}/roster/data");
            await doc.SetDataAsync(payload).ConfigureAwait(false);
            System.Diagnostics.Debug.WriteLine(
                $"[CloudRosterService] Saved to Firestore (team {teamId}, players={snapshot.Players.Count})");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[CloudRosterService] Firestore upload: {ex.Message}");
            // REST fallback so members can still mirror when SDK write fails
            try
            {
                await UploadViaRestAsync(teamId, snapshot).ConfigureAwait(false);
            }
            catch (Exception restEx)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[CloudRosterService] REST upload failed: {restEx.Message}");
            }
        }
    }

    private async Task UploadViaRestAsync(string teamId, RosterSnapshot snapshot)
    {
        var idToken = await _auth.GetIdTokenAsync(forceRefresh: true).ConfigureAwait(false);
        if (string.IsNullOrEmpty(idToken)) return;

        var url =
            $"https://firestore.googleapis.com/v1/projects/{FirebaseProjectId}/databases/(default)/documents/teams/{Uri.EscapeDataString(teamId)}/roster/data";

        var fields = new Dictionary<string, object>
        {
            ["version"] = new { integerValue = snapshot.Version.ToString() },
            ["lastModifiedUtc"] = new { timestampValue = snapshot.LastModifiedUtc.UtcDateTime.ToString("o") },
            ["lastModified"] = new { timestampValue = snapshot.LastModifiedUtc.UtcDateTime.ToString("o") },
            ["matchDurationSeconds"] = new { integerValue = snapshot.MatchDurationSeconds.ToString() },
            ["halfDurationSeconds"] = new { integerValue = snapshot.HalfDurationSeconds.ToString() },
            ["matchRemainingSeconds"] = new { integerValue = snapshot.MatchRemainingSeconds.ToString() },
            ["currentHalf"] = new { stringValue = snapshot.CurrentHalf ?? "setup" },
            ["timerRunning"] = new { booleanValue = snapshot.TimerRunning },
            ["countdownPresetSeconds"] = new { integerValue = snapshot.CountdownPresetSeconds.ToString() },
            ["countdownPreset"] = new { integerValue = snapshot.CountdownPresetSeconds.ToString() },
            ["viewMode"] = new { integerValue = snapshot.ViewMode.ToString() },
            ["teamAScore"] = new { integerValue = snapshot.TeamAScore.ToString() },
            ["teamBScore"] = new { integerValue = snapshot.TeamBScore.ToString() },
            ["players"] = new
            {
                arrayValue = new
                {
                    values = snapshot.Players.Select(p => new
                    {
                        mapValue = new
                        {
                            fields = new Dictionary<string, object>
                            {
                                ["slotId"] = new { integerValue = p.SlotId.ToString() },
                                ["name"] = new { stringValue = p.Name ?? "" },
                                ["field"] = new { booleanValue = p.Field },
                                ["bench"] = new { booleanValue = p.Bench },
                                ["goalie"] = new { booleanValue = p.Goalie },
                                ["inactive"] = new { booleanValue = p.Inactive },
                                ["counterSeconds"] = new { integerValue = p.CounterSeconds.ToString() }
                            }
                        }
                    }).ToList()
                }
            }
        };

        var body = JsonSerializer.Serialize(new { fields });
        using var req = new HttpRequestMessage(HttpMethod.Patch, url + "?currentDocument.exists=true")
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };
        // Upsert: try patch; if 404 create with POST to parent... use updateMask-less patch with allow missing via update
        // Firestore REST: PATCH with updateMask optional; for create use PATCH without currentDocument
        req.RequestUri = new Uri(url); // allow create
        req.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", idToken);

        using var resp = await RestHttp.SendAsync(req).ConfigureAwait(false);
        var respBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[CloudRosterService] REST upload {(int)resp.StatusCode}: {respBody[..Math.Min(200, respBody.Length)]}");
            return;
        }

        System.Diagnostics.Debug.WriteLine(
            $"[CloudRosterService] REST upload OK (team {teamId}, players={snapshot.Players.Count})");
    }

    private async Task<RosterSnapshot?> DownloadFromFirestoreAsync(string teamId)
    {
        if (await _auth.EnsureSignedInAsync().ConfigureAwait(false) is null)
            return null;

        // 1) SDK dictionary path
        try
        {
            var snap = await _db.GetDocument($"teams/{teamId}/roster/data")
                .GetDocumentSnapshotAsync<Dictionary<string, object>>()
                .ConfigureAwait(false);
            if (snap?.Data is not null)
            {
                var roster = FromDictionary(snap.Data);
                if (roster is not null && roster.Players.Count > 0)
                    return roster;
                // Empty players array from create-time doc — try REST for fuller parse, or return empty
                if (roster is not null)
                    return roster;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[CloudRosterService] Firestore download: {ex.Message}");
        }

        // 2) REST fallback (same pattern that fixed invite lookup)
        try
        {
            return await DownloadViaRestAsync(teamId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[CloudRosterService] REST download: {ex.Message}");
            return null;
        }
    }

    private async Task<RosterSnapshot?> DownloadViaRestAsync(string teamId)
    {
        var idToken = await _auth.GetIdTokenAsync(forceRefresh: true).ConfigureAwait(false);
        if (string.IsNullOrEmpty(idToken)) return null;

        var url =
            $"https://firestore.googleapis.com/v1/projects/{FirebaseProjectId}/databases/(default)/documents/teams/{Uri.EscapeDataString(teamId)}/roster/data";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", idToken);

        using var resp = await RestHttp.SendAsync(req).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[CloudRosterService] REST download {(int)resp.StatusCode}: {body[..Math.Min(160, body.Length)]}");
            return null;
        }

        using var json = JsonDocument.Parse(body);
        if (!json.RootElement.TryGetProperty("fields", out var fields))
            return null;

        return FromRestFields(fields);
    }

    /// <summary>
    /// Build a Plugin.Firebase-compatible write payload.
    /// Nested maps must be Dictionary&lt;string, object?&gt; (not Dictionary&lt;object, object&gt;).
    /// </summary>
    private static Dictionary<string, object?> ToFirestorePayload(RosterSnapshot s)
    {
        var players = s.Players.Select(p => (object?)new Dictionary<string, object?>
        {
            ["slotId"] = p.SlotId,
            ["name"] = p.Name ?? "",
            ["field"] = p.Field,
            ["bench"] = p.Bench,
            ["goalie"] = p.Goalie,
            ["inactive"] = p.Inactive,
            ["counterSeconds"] = p.CounterSeconds
        }).ToList();

        var utc = s.LastModifiedUtc;
        return new Dictionary<string, object?>
        {
            ["version"] = s.Version,
            ["lastModifiedUtc"] = utc,
            ["lastModified"] = utc,
            ["matchDurationSeconds"] = s.MatchDurationSeconds,
            ["halfDurationSeconds"] = s.HalfDurationSeconds,
            ["matchRemainingSeconds"] = s.MatchRemainingSeconds,
            ["currentHalf"] = s.CurrentHalf ?? "setup",
            ["timerRunning"] = s.TimerRunning,
            ["countdownPresetSeconds"] = s.CountdownPresetSeconds,
            ["countdownPreset"] = s.CountdownPresetSeconds,
            ["viewMode"] = s.ViewMode,
            ["teamAScore"] = s.TeamAScore,
            ["teamBScore"] = s.TeamBScore,
            ["players"] = players
        };
    }

    private static RosterSnapshot? FromDictionary(IDictionary<string, object> fields)
    {
        try
        {
            const int defaultMatch = 90 * 60;
            var snapshot = new RosterSnapshot
            {
                Version = ReadInt(fields, "version", 2),
                LastModifiedUtc = ReadTimestamp(fields, "lastModifiedUtc", "lastModified"),
                MatchDurationSeconds = Math.Max(1, ReadInt(fields, "matchDurationSeconds", defaultMatch)),
                HalfDurationSeconds = ReadInt(fields, "halfDurationSeconds", 0),
                MatchRemainingSeconds = ReadInt(fields, "matchRemainingSeconds", defaultMatch),
                CurrentHalf = ReadString(fields, "currentHalf", "setup"),
                TimerRunning = ReadBool(fields, "timerRunning", false),
                CountdownPresetSeconds = ReadInt(fields, "countdownPresetSeconds",
                    ReadInt(fields, "countdownPreset", 120)),
                ViewMode = ReadInt(fields, "viewMode", 0),
                TeamAScore = ReadInt(fields, "teamAScore", 0),
                TeamBScore = ReadInt(fields, "teamBScore", 0)
            };

            if (fields.TryGetValue("players", out var playersObj) && playersObj is not null)
            {
                IEnumerable<object>? list = playersObj switch
                {
                    IEnumerable<object> o => o,
                    System.Collections.IEnumerable e => e.Cast<object>(),
                    _ => null
                };

                if (list is not null)
                {
                    foreach (var item in list)
                    {
                        var map = AsStringObjectMap(item);
                        if (map is null) continue;
                        snapshot.Players.Add(new PlayerSnapshot
                        {
                            SlotId = ReadInt(map, "slotId", 0),
                            Name = ReadString(map, "name", string.Empty),
                            Field = ReadBool(map, "field", false),
                            Bench = ReadBool(map, "bench", false),
                            Goalie = ReadBool(map, "goalie", false),
                            Inactive = ReadBool(map, "inactive", false),
                            CounterSeconds = ReadInt(map, "counterSeconds", 0)
                        });
                    }
                }
            }

            return snapshot;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[CloudRosterService] FromDictionary: {ex.Message}");
            return null;
        }
    }

    private static IDictionary<string, object>? AsStringObjectMap(object item)
    {
        switch (item)
        {
            case IDictionary<string, object> s:
                return s;
            case IDictionary<object, object> o:
                return o.ToDictionary(
                    kv => kv.Key?.ToString() ?? "",
                    kv => kv.Value!);
            case System.Collections.IDictionary dict:
            {
                var map = new Dictionary<string, object>();
                foreach (System.Collections.DictionaryEntry entry in dict)
                {
                    var key = entry.Key?.ToString();
                    if (key is null || entry.Value is null) continue;
                    map[key] = entry.Value;
                }
                return map;
            }
            default:
                return null;
        }
    }

    private static RosterSnapshot? FromRestFields(JsonElement fields)
    {
        try
        {
            const int defaultMatch = 90 * 60;
            var snapshot = new RosterSnapshot
            {
                Version = ReadRestInt(fields, "version", 2),
                LastModifiedUtc = ReadRestTimestamp(fields, "lastModifiedUtc", "lastModified"),
                MatchDurationSeconds = Math.Max(1, ReadRestInt(fields, "matchDurationSeconds", defaultMatch)),
                HalfDurationSeconds = ReadRestInt(fields, "halfDurationSeconds", 0),
                MatchRemainingSeconds = ReadRestInt(fields, "matchRemainingSeconds", defaultMatch),
                CurrentHalf = ReadRestString(fields, "currentHalf", "setup"),
                TimerRunning = ReadRestBool(fields, "timerRunning", false),
                CountdownPresetSeconds = ReadRestInt(fields, "countdownPresetSeconds",
                    ReadRestInt(fields, "countdownPreset", 120)),
                ViewMode = ReadRestInt(fields, "viewMode", 0),
                TeamAScore = ReadRestInt(fields, "teamAScore", 0),
                TeamBScore = ReadRestInt(fields, "teamBScore", 0)
            };

            if (fields.TryGetProperty("players", out var playersField)
                && playersField.TryGetProperty("arrayValue", out var arr)
                && arr.TryGetProperty("values", out var values))
            {
                foreach (var item in values.EnumerateArray())
                {
                    if (!item.TryGetProperty("mapValue", out var mapVal)) continue;
                    if (!mapVal.TryGetProperty("fields", out var pf)) continue;
                    snapshot.Players.Add(new PlayerSnapshot
                    {
                        SlotId = ReadRestInt(pf, "slotId", 0),
                        Name = ReadRestString(pf, "name", ""),
                        Field = ReadRestBool(pf, "field", false),
                        Bench = ReadRestBool(pf, "bench", false),
                        Goalie = ReadRestBool(pf, "goalie", false),
                        Inactive = ReadRestBool(pf, "inactive", false),
                        CounterSeconds = ReadRestInt(pf, "counterSeconds", 0)
                    });
                }
            }

            return snapshot;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[CloudRosterService] FromRestFields: {ex.Message}");
            return null;
        }
    }

    private static object? Get(IDictionary<string, object> d, string key)
        => d.TryGetValue(key, out var v) ? v : null;

    private static int ReadInt(IDictionary<string, object> d, string key, int fallback)
    {
        var v = Get(d, key);
        return v switch
        {
            int i => i,
            long l => (int)l,
            double dbl => (int)dbl,
            float f => (int)f,
            string s when int.TryParse(s, out var i) => i,
            _ => fallback
        };
    }

    private static bool ReadBool(IDictionary<string, object> d, string key, bool fallback)
    {
        var v = Get(d, key);
        return v switch
        {
            bool b => b,
            string s when bool.TryParse(s, out var b) => b,
            _ => fallback
        };
    }

    private static string ReadString(IDictionary<string, object> d, string key, string fallback)
        => Get(d, key)?.ToString() ?? fallback;

    private static DateTimeOffset ReadTimestamp(IDictionary<string, object> d, params string[] keys)
    {
        foreach (var key in keys)
        {
            var v = Get(d, key);
            switch (v)
            {
                case DateTimeOffset dto:
                    return dto;
                case DateTime dt:
                    return new DateTimeOffset(dt.ToUniversalTime());
                case string s when DateTimeOffset.TryParse(s, out var parsed):
                    return parsed;
            }
        }
        return DateTimeOffset.UtcNow;
    }

    private static int ReadRestInt(JsonElement fields, string name, int fallback)
    {
        if (!fields.TryGetProperty(name, out var f)) return fallback;
        if (f.TryGetProperty("integerValue", out var iv)
            && int.TryParse(iv.GetString(), out var i)) return i;
        if (f.TryGetProperty("doubleValue", out var dv)) return (int)dv.GetDouble();
        return fallback;
    }

    private static bool ReadRestBool(JsonElement fields, string name, bool fallback)
    {
        if (!fields.TryGetProperty(name, out var f)) return fallback;
        if (f.TryGetProperty("booleanValue", out var bv)) return bv.GetBoolean();
        return fallback;
    }

    private static string ReadRestString(JsonElement fields, string name, string fallback)
    {
        if (!fields.TryGetProperty(name, out var f)) return fallback;
        if (f.TryGetProperty("stringValue", out var sv)) return sv.GetString() ?? fallback;
        return fallback;
    }

    private static DateTimeOffset ReadRestTimestamp(JsonElement fields, params string[] names)
    {
        foreach (var name in names)
        {
            if (!fields.TryGetProperty(name, out var f)) continue;
            if (f.TryGetProperty("timestampValue", out var tv)
                && DateTimeOffset.TryParse(tv.GetString(), out var dto))
                return dto;
            if (f.TryGetProperty("stringValue", out var sv)
                && DateTimeOffset.TryParse(sv.GetString(), out var dto2))
                return dto2;
        }
        return DateTimeOffset.UtcNow;
    }
}
