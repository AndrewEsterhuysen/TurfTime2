using System.Text.Json;
using Plugin.Firebase.Firestore;
using TurfTime2.Models;

namespace TurfTime2.Services;

/// <summary>
/// Persists the roster snapshot to local <see cref="Preferences"/> and to
/// Firestore via Plugin.Firebase (no REST).
/// </summary>
public sealed class CloudRosterService : ICloudRosterService
{
    private readonly IFirebaseAuthService _auth;
    private readonly IFirebaseFirestore _db;

    private CancellationTokenSource? _debounceCts;

    public CloudRosterService(IFirebaseAuthService auth, IFirebaseFirestore db)
    {
        _auth = auth;
        _db = db;
    }

    /// <summary>Legacy no-op — auth is owned by <see cref="IFirebaseAuthService"/>.</summary>
    public static void SetAuthToken(string idToken, string userId)
    {
        // Kept so older call sites compile during migration; token sharing is no longer used.
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
            return cloud.LastModifiedUtc > local.LastModifiedUtc ? cloud : local;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CloudRosterService] Load cloud failed: {ex.GetType().FullName}: {ex.Message}");
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
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[CloudRosterService] Local save failed: {ex.Message}"); }
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

    private async Task UploadToFirestoreAsync(string teamId, RosterSnapshot snapshot)
    {
        if (await _auth.EnsureSignedInAsync().ConfigureAwait(false) is null)
        {
            System.Diagnostics.Debug.WriteLine("[CloudRosterService] Auth failed — roster not saved to cloud");
            return;
        }

        try
        {
            var doc = _db.GetDocument($"teams/{teamId}/roster/data");
            await doc.SetDataAsync(ToDictionary(snapshot), SetOptions.Merge()).ConfigureAwait(false);
            System.Diagnostics.Debug.WriteLine($"[CloudRosterService] Saved to Firestore (team {teamId})");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CloudRosterService] Firestore upload: {ex.Message}");
        }
    }

    private async Task<RosterSnapshot?> DownloadFromFirestoreAsync(string teamId)
    {
        if (await _auth.EnsureSignedInAsync().ConfigureAwait(false) is null)
            return null;

        try
        {
            var snap = await _db.GetDocument($"teams/{teamId}/roster/data")
                .GetDocumentSnapshotAsync<Dictionary<string, object>>()
                .ConfigureAwait(false);
            if (snap?.Data is null)
                return null;
            return FromDictionary(snap.Data);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CloudRosterService] Firestore download: {ex.Message}");
            return null;
        }
    }

    private static Dictionary<object, object> ToDictionary(RosterSnapshot s)
    {
        var players = s.Players.Select(p => (object)new Dictionary<object, object>
        {
            ["slotId"] = p.SlotId,
            ["name"] = p.Name,
            ["field"] = p.Field,
            ["bench"] = p.Bench,
            ["goalie"] = p.Goalie,
            ["inactive"] = p.Inactive,
            ["counterSeconds"] = p.CounterSeconds
        }).ToList();

        var utc = s.LastModifiedUtc;
        return new Dictionary<object, object>
        {
            ["version"] = s.Version,
            ["lastModifiedUtc"] = utc,
            ["lastModified"] = utc,
            ["matchDurationSeconds"] = s.MatchDurationSeconds,
            ["halfDurationSeconds"] = s.HalfDurationSeconds,
            ["matchRemainingSeconds"] = s.MatchRemainingSeconds,
            ["currentHalf"] = s.CurrentHalf,
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

            if (fields.TryGetValue("players", out var playersObj) && playersObj is IEnumerable<object> list)
            {
                foreach (var item in list)
                {
                    IDictionary<string, object>? map = item switch
                    {
                        IDictionary<string, object> s => s,
                        IDictionary<object, object> o => o.ToDictionary(
                            kv => kv.Key?.ToString() ?? "",
                            kv => kv.Value!),
                        _ => null
                    };
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

            return snapshot;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CloudRosterService] FromDictionary: {ex.Message}");
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
}

