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
                        RosterSnapshot? roster = null;
                        if (data is not null)
                            roster = FromDictionary(data);

                        // Plugin.Firebase on iOS often delivers change events with null/empty
                        // Data after long suspend, or fails nested map casts. Fall back to REST
                        // so members still mirror the admin without continuous polling.
                        if (roster is null || roster.Players.Count == 0)
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"[CloudRosterService] Watch empty/null Data for {teamId} — REST fallback");
                            _ = DeliverViaRestAsync(teamId, onUpdate);
                            return;
                        }

                        SaveLocal(teamId, roster);
                        onUpdate(roster);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[CloudRosterService] Watch callback: {ex.Message}");
                        _ = DeliverViaRestAsync(teamId, onUpdate);
                    }
                },
                error =>
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[CloudRosterService] Watch error: {error.Message}");
                    _ = DeliverViaRestAsync(teamId, onUpdate);
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

    /// <summary>
    /// One-shot REST pull used when the SDK snapshot listener has no usable Data.
    /// Does not loop — only runs when the listener fires (or errors).
    /// </summary>
    private async Task DeliverViaRestAsync(string teamId, Action<RosterSnapshot> onUpdate)
    {
        // Ignore stale callbacks after the watch was stopped / swapped.
        if (!string.Equals(_watchTeamId, teamId, StringComparison.Ordinal))
            return;

        try
        {
            var rest = await DownloadFromFirestoreAsync(teamId).ConfigureAwait(false);
            if (rest is null || rest.Players.Count == 0) return;
            if (!string.Equals(_watchTeamId, teamId, StringComparison.Ordinal))
                return;

            SaveLocal(teamId, rest);
            onUpdate(rest);
            System.Diagnostics.Debug.WriteLine(
                $"[CloudRosterService] REST fallback delivered players={rest.Players.Count} for {teamId}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[CloudRosterService] REST fallback failed: {ex.Message}");
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

        // PRIMARY: authenticated Firestore REST.
        // Plugin.Firebase SetDataAsync on Android often reports success while writing an
        // empty document (no fields) for nested player maps — verified against justinb-w14nvh
        // where updateTime advanced but REST GET returned zero fields. REST writes fields
        // correctly and is what members read.
        try
        {
            await UploadViaRestAsync(teamId, snapshot).ConfigureAwait(false);
            return;
        }
        catch (Exception restEx)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[CloudRosterService] REST upload failed, trying SDK: {restEx.Message}");
        }

        try
        {
            var payload = ToFirestorePayload(snapshot);
            var doc = _db.GetDocument($"teams/{teamId}/roster/data");
            await doc.SetDataAsync(payload).ConfigureAwait(false);
            System.Diagnostics.Debug.WriteLine(
                $"[CloudRosterService] SDK SetDataAsync completed (team {teamId}, players={snapshot.Players.Count}) — verify via REST if members cannot see state");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[CloudRosterService] Firestore SDK upload: {ex.Message}");
        }
    }

    private async Task UploadViaRestAsync(string teamId, RosterSnapshot snapshot)
    {
        var idToken = await _auth.GetIdTokenAsync(forceRefresh: false).ConfigureAwait(false);
        if (string.IsNullOrEmpty(idToken))
            idToken = await _auth.GetIdTokenAsync(forceRefresh: true).ConfigureAwait(false);
        if (string.IsNullOrEmpty(idToken))
            throw new InvalidOperationException("No Firebase id token for roster upload");

        var url =
            $"https://firestore.googleapis.com/v1/projects/{FirebaseProjectId}/databases/(default)/documents/teams/{Uri.EscapeDataString(teamId)}/roster/data";

        // RFC3339 UTC — Firestore timestampValue
        var ts = snapshot.LastModifiedUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

        var playerValues = snapshot.Players.Select(p => new Dictionary<string, object>
        {
            ["mapValue"] = new Dictionary<string, object>
            {
                ["fields"] = new Dictionary<string, object>
                {
                    ["slotId"] = new Dictionary<string, object> { ["integerValue"] = p.SlotId.ToString() },
                    ["name"] = new Dictionary<string, object> { ["stringValue"] = p.Name ?? "" },
                    ["field"] = new Dictionary<string, object> { ["booleanValue"] = p.Field },
                    ["bench"] = new Dictionary<string, object> { ["booleanValue"] = p.Bench },
                    ["goalie"] = new Dictionary<string, object> { ["booleanValue"] = p.Goalie },
                    ["inactive"] = new Dictionary<string, object> { ["booleanValue"] = p.Inactive },
                    ["counterSeconds"] = new Dictionary<string, object> { ["integerValue"] = p.CounterSeconds.ToString() }
                }
            }
        }).ToList();

        var fields = new Dictionary<string, object>
        {
            ["version"] = new Dictionary<string, object> { ["integerValue"] = snapshot.Version.ToString() },
            ["lastModifiedUtc"] = new Dictionary<string, object> { ["timestampValue"] = ts },
            ["lastModified"] = new Dictionary<string, object> { ["timestampValue"] = ts },
            ["matchDurationSeconds"] = new Dictionary<string, object> { ["integerValue"] = snapshot.MatchDurationSeconds.ToString() },
            ["halfDurationSeconds"] = new Dictionary<string, object> { ["integerValue"] = snapshot.HalfDurationSeconds.ToString() },
            ["matchRemainingSeconds"] = new Dictionary<string, object> { ["integerValue"] = snapshot.MatchRemainingSeconds.ToString() },
            ["currentHalf"] = new Dictionary<string, object> { ["stringValue"] = snapshot.CurrentHalf ?? "setup" },
            ["timerRunning"] = new Dictionary<string, object> { ["booleanValue"] = snapshot.TimerRunning },
            ["countdownPresetSeconds"] = new Dictionary<string, object> { ["integerValue"] = snapshot.CountdownPresetSeconds.ToString() },
            ["countdownPreset"] = new Dictionary<string, object> { ["integerValue"] = snapshot.CountdownPresetSeconds.ToString() },
            ["viewMode"] = new Dictionary<string, object> { ["integerValue"] = snapshot.ViewMode.ToString() },
            ["teamAScore"] = new Dictionary<string, object> { ["integerValue"] = snapshot.TeamAScore.ToString() },
            ["teamBScore"] = new Dictionary<string, object> { ["integerValue"] = snapshot.TeamBScore.ToString() },
            ["players"] = new Dictionary<string, object>
            {
                ["arrayValue"] = new Dictionary<string, object>
                {
                    ["values"] = playerValues
                }
            }
        };

        var body = JsonSerializer.Serialize(new { fields });
        using var req = new HttpRequestMessage(HttpMethod.Patch, url)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", idToken);

        using var resp = await RestHttp.SendAsync(req).ConfigureAwait(false);
        var respBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"REST upload {(int)resp.StatusCode}: {respBody[..Math.Min(240, respBody.Length)]}");
        }

        // Confirm players actually landed (guards against silent empty writes).
        try
        {
            using var verify = JsonDocument.Parse(respBody);
            var playerCount = 0;
            if (verify.RootElement.TryGetProperty("fields", out var f)
                && f.TryGetProperty("players", out var pl)
                && pl.TryGetProperty("arrayValue", out var av)
                && av.TryGetProperty("values", out var vals))
            {
                playerCount = vals.GetArrayLength();
            }

            System.Diagnostics.Debug.WriteLine(
                $"[CloudRosterService] REST upload OK team={teamId} players={playerCount}/{snapshot.Players.Count}");
            if (playerCount == 0 && snapshot.Players.Count > 0)
                throw new InvalidOperationException("REST upload returned document with 0 players");
        }
        catch (InvalidOperationException) { throw; }
        catch
        {
            System.Diagnostics.Debug.WriteLine(
                $"[CloudRosterService] REST upload OK team={teamId} (response parse skipped)");
        }
    }

    private async Task<RosterSnapshot?> DownloadFromFirestoreAsync(string teamId)
    {
        if (await _auth.EnsureSignedInAsync().ConfigureAwait(false) is null)
            return null;

        // REST first: SDK snapshot.Data is often empty/null or offline-cached empty
        // after failed/empty SetDataAsync writes.
        try
        {
            var rest = await DownloadViaRestAsync(teamId).ConfigureAwait(false);
            if (rest is not null && rest.Players.Count > 0)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[CloudRosterService] REST download team={teamId} players={rest.Players.Count}");
                return rest;
            }

            if (rest is not null)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[CloudRosterService] REST download team={teamId} has 0 players — trying SDK");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[CloudRosterService] REST download: {ex.Message}");
        }

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
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[CloudRosterService] Firestore SDK download: {ex.Message}");
        }

        return null;
    }

    private async Task<RosterSnapshot?> DownloadViaRestAsync(string teamId)
    {
        // Prefer cached token; force-refresh only if missing (avoids thrashing Auth on every pull).
        var idToken = await _auth.GetIdTokenAsync(forceRefresh: false).ConfigureAwait(false);
        if (string.IsNullOrEmpty(idToken))
            idToken = await _auth.GetIdTokenAsync(forceRefresh: true).ConfigureAwait(false);
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
            if (v is null) continue;

            switch (v)
            {
                case DateTimeOffset dto:
                    return dto.ToUniversalTime();
                case DateTime dt:
                    return new DateTimeOffset(
                        dt.Kind == DateTimeKind.Unspecified
                            ? DateTime.SpecifyKind(dt, DateTimeKind.Utc)
                            : dt.ToUniversalTime());
                case string s when DateTimeOffset.TryParse(s,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out var parsed):
                    return parsed.ToUniversalTime();
            }

            // Plugin.Firebase / native Firestore Timestamp (Seconds + Nanoseconds, or ToDateTime).
            var parsedNative = TryParseFirestoreTimestamp(v);
            if (parsedNative.HasValue)
                return parsedNative.Value;
        }

        // Do NOT use UtcNow: that stamps "now" onto incomplete parses and then blocks
        // real admin updates whose lastModifiedUtc is older than that synthetic stamp.
        return DateTimeOffset.MinValue;
    }

    private static DateTimeOffset? TryParseFirestoreTimestamp(object v)
    {
        try
        {
            var t = v.GetType();

            // Timestamp.ToDateTime() / ToDateTimeOffset()
            foreach (var name in new[] { "ToDateTimeOffset", "ToDateTime", "ToDate" })
            {
                var m = t.GetMethod(name, Type.EmptyTypes);
                if (m is null) continue;
                var result = m.Invoke(v, null);
                switch (result)
                {
                    case DateTimeOffset dto:
                        return dto.ToUniversalTime();
                    case DateTime dt:
                        return new DateTimeOffset(
                            dt.Kind == DateTimeKind.Unspecified
                                ? DateTime.SpecifyKind(dt, DateTimeKind.Utc)
                                : dt.ToUniversalTime());
                }
            }

            // Seconds / Nanoseconds (Firestore Timestamp)
            var secProp = t.GetProperty("Seconds") ?? t.GetProperty("seconds");
            if (secProp?.GetValue(v) is long secL)
            {
                var nanoProp = t.GetProperty("Nanoseconds")
                    ?? t.GetProperty("nanoseconds")
                    ?? t.GetProperty("Nanos")
                    ?? t.GetProperty("nanos");
                var nanos = nanoProp?.GetValue(v) switch
                {
                    int n => n,
                    long nl => (int)nl,
                    _ => 0
                };
                return DateTimeOffset.FromUnixTimeSeconds(secL).AddTicks(nanos / 100);
            }
            if (secProp?.GetValue(v) is int secI)
            {
                return DateTimeOffset.FromUnixTimeSeconds(secI);
            }
        }
        catch
        {
            /* ignore */
        }

        return null;
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
                && DateTimeOffset.TryParse(tv.GetString(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out var dto))
                return dto.ToUniversalTime();
            if (f.TryGetProperty("stringValue", out var sv)
                && DateTimeOffset.TryParse(sv.GetString(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out var dto2))
                return dto2.ToUniversalTime();
        }
        // Same rationale as ReadTimestamp: never invent "now".
        return DateTimeOffset.MinValue;
    }
}
