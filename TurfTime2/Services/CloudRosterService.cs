using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TurfTime2.Models;

namespace TurfTime2.Services;

/// <summary>
/// Persists the roster snapshot to local <see cref="Preferences"/> and to
/// Firestore via the REST API.  Authentication uses Firebase anonymous sign-in
/// (same approach as the existing <see cref="FirebaseSaveBridge"/>).
/// </summary>
public sealed class CloudRosterService : ICloudRosterService
{
    private const string FirebaseApiKey   = "AIzaSyDAKivCFX5kYYZ6SkAQluBNdR92I320glk";
    private const string FirebaseProjectId = "turf-timer";
    private const string FirestoreBase    =
        $"https://firestore.googleapis.com/v1/projects/{FirebaseProjectId}/databases/(default)/documents";

    private static readonly HttpClient _http = new();
    private static string? _idToken;

    // Debounce: cloud save fires at most once per 2 s of silence
    private CancellationTokenSource? _debounceCts;

    // ── ICloudRosterService ───────────────────────────────────────────────

    public async Task SaveAsync(string teamId, RosterSnapshot snapshot, bool isAdmin)
    {
        // Serialize and persist locally on a background thread — Preferences.Set
        // calls SharedPreferences.commit() on Android which is synchronous disk I/O
        // and blocks the UI thread if called directly.
        _ = Task.Run(() => SaveLocal(teamId, snapshot));

        if (!isAdmin) return;                          // members never write to cloud
        if (teamId.StartsWith("local_", StringComparison.Ordinal)) return;

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

        if (teamId.StartsWith("local_", StringComparison.Ordinal))
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
            System.Diagnostics.Debug.WriteLine($"[CloudRosterService] Load cloud failed: {ex.Message}");
            return local;
        }
    }

    public Task ForceSyncAsync(string teamId, RosterSnapshot snapshot)
    {
        _debounceCts?.Cancel();
        SaveLocal(teamId, snapshot);

        if (teamId.StartsWith("local_", StringComparison.Ordinal)) return Task.CompletedTask;

        return UploadToFirestoreAsync(teamId, snapshot);
    }

    /// <summary>Pre-warms the Firebase auth token on a background thread.</summary>
    public async Task WarmUpAsync()
    {
        try { await Task.Run(GetAuthTokenAsync).ConfigureAwait(false); }
        catch { /* warm-up is best-effort */ }
    }

    // ── Local storage ─────────────────────────────────────────────────────

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

        var url  = $"https://identitytoolkit.googleapis.com/v1/accounts:signUp?key={FirebaseApiKey}";
        var body = JsonSerializer.Serialize(new { returnSecureToken = true });
        var resp = await _http.PostAsync(url,
            new StringContent(body, Encoding.UTF8, "application/json")).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
        _idToken = doc.RootElement.GetProperty("idToken").GetString();
        return _idToken;
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
                    ["name"]           = new { stringValue  = p.Name },
                    ["field"]          = new { booleanValue = p.Field },
                    ["bench"]          = new { booleanValue = p.Bench },
                    ["goalie"]         = new { booleanValue = p.Goalie },
                    ["inactive"]       = new { booleanValue = p.Inactive },
                    ["counterSeconds"] = new { integerValue = p.CounterSeconds.ToString() }
                }
            }
        }).ToArray();

        return new
        {
            fields = new Dictionary<string, object>
            {
                ["version"]                = new { integerValue = s.Version.ToString() },
                ["lastModifiedUtc"]        = new { timestampValue = s.LastModifiedUtc.ToString("o") },
                ["matchDurationSeconds"]   = new { integerValue = s.MatchDurationSeconds.ToString() },
                ["halfDurationSeconds"]    = new { integerValue = s.HalfDurationSeconds.ToString() },
                ["matchRemainingSeconds"]  = new { integerValue = s.MatchRemainingSeconds.ToString() },
                ["currentHalf"]            = new { stringValue  = s.CurrentHalf },
                ["timerRunning"]           = new { booleanValue = s.TimerRunning },
                ["countdownPresetSeconds"] = new { integerValue = s.CountdownPresetSeconds.ToString() },
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
            using var doc   = JsonDocument.Parse(json);
            var fields = doc.RootElement.GetProperty("fields");

            static string Str(JsonElement e)   => e.GetProperty("stringValue").GetString() ?? string.Empty;
            static int    Int(JsonElement e)   => int.Parse(e.GetProperty("integerValue").GetString() ?? "0");
            static bool   Bool(JsonElement e)  => e.GetProperty("booleanValue").GetBoolean();

            const int defaultMatchDuration = 90 * 60;

            var snapshot = new RosterSnapshot
            {
                Version                = Int(fields.GetProperty("version")),
                LastModifiedUtc        = DateTimeOffset.Parse(Str(fields.GetProperty("lastModifiedUtc"))),
                MatchDurationSeconds   = fields.TryGetProperty("matchDurationSeconds", out var mds) && Int(mds) > 0 ? Int(mds) : defaultMatchDuration,
                HalfDurationSeconds    = Int(fields.GetProperty("halfDurationSeconds")),
                MatchRemainingSeconds  = Int(fields.GetProperty("matchRemainingSeconds")),
                CurrentHalf            = Str(fields.GetProperty("currentHalf")),
                TimerRunning           = Bool(fields.GetProperty("timerRunning")),
                CountdownPresetSeconds = Int(fields.GetProperty("countdownPresetSeconds")),
                ViewMode               = Int(fields.GetProperty("viewMode")),
                TeamAScore             = Int(fields.GetProperty("teamAScore")),
                TeamBScore             = Int(fields.GetProperty("teamBScore"))
            };

            foreach (var pElem in fields.GetProperty("players").GetProperty("arrayValue")
                                        .GetProperty("values").EnumerateArray())
            {
                var pf = pElem.GetProperty("mapValue").GetProperty("fields");
                snapshot.Players.Add(new PlayerSnapshot
                {
                    Name           = Str(pf.GetProperty("name")),
                    Field          = Bool(pf.GetProperty("field")),
                    Bench          = Bool(pf.GetProperty("bench")),
                    Goalie         = Bool(pf.GetProperty("goalie")),
                    Inactive       = Bool(pf.GetProperty("inactive")),
                    CounterSeconds = Int(pf.GetProperty("counterSeconds"))
                });
            }

            return snapshot;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CloudRosterService] Parse failed: {ex.Message}");
            return null;
        }
    }
}
