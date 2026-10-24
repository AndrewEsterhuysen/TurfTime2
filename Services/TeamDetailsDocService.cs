using System.Text;
using System.Text.Json;
using Plugin.Firebase.Firestore;
using TurfTime2.Helpers;

namespace TurfTime2.Services;

/// <inheritdoc cref="ITeamDetailsDocService"/>
public sealed class TeamDetailsDocService : ITeamDetailsDocService
{
    private const string FirebaseProjectId = "turf-timer";
    private const string LastModifiedField = "lastModifiedUtc";
    private const string UpdatedByUidField = "updatedByUid";
    private static readonly HttpClient RestHttp = new();

    private readonly IFirebaseAuthService _auth;
    private readonly IFirebaseFirestore _db;

    public TeamDetailsDocService(IFirebaseAuthService auth, IFirebaseFirestore db)
    {
        _auth = auth;
        _db = db;
    }

    private static string DocPath(string teamId, string docName) => $"teams/{teamId}/details/{docName}";

    private static string RestUrl(string teamId, string docName) =>
        $"https://firestore.googleapis.com/v1/projects/{FirebaseProjectId}/databases/(default)/documents/teams/{Uri.EscapeDataString(teamId)}/details/{Uri.EscapeDataString(docName)}";

    public async Task<TeamDetailsDoc?> LoadAsync(string teamId, string docName)
    {
        if (string.IsNullOrWhiteSpace(teamId)) return null;
        try
        {
            if (await _auth.EnsureSignedInAsync().ConfigureAwait(false) is null)
                return new TeamDetailsDoc { Reachable = false };
        }
        catch { return new TeamDetailsDoc { Reachable = false }; }

        try
        {
            var rest = await DownloadViaRestAsync(teamId, docName).ConfigureAwait(false);
            if (rest is not null) return rest;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TeamDetailsDoc] REST download {docName}: {ex.Message}");
        }

        try
        {
            var snap = await _db.GetDocument(DocPath(teamId, docName))
                .GetDocumentSnapshotAsync<Dictionary<string, object>>()
                .ConfigureAwait(false);
            FirestoreUsageMeter.RecordSdk("Get", docName);
            if (snap?.Data is null)
                return new TeamDetailsDoc { Reachable = true, Exists = false };

            var fields = new Dictionary<string, string>();
            DateTimeOffset lm = default;
            foreach (var kv in snap.Data)
            {
                var v = kv.Value?.ToString() ?? string.Empty;
                if (kv.Key == LastModifiedField) DateTimeOffset.TryParse(v, out lm);
                else fields[kv.Key] = v;
            }
            return new TeamDetailsDoc { Reachable = true, Exists = true, Fields = fields, LastModifiedUtc = lm };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TeamDetailsDoc] SDK download {docName}: {ex.Message}");
            return new TeamDetailsDoc { Reachable = false };
        }
    }

    private async Task<TeamDetailsDoc?> DownloadViaRestAsync(string teamId, string docName)
    {
        var idToken = await GetTokenAsync().ConfigureAwait(false);
        if (string.IsNullOrEmpty(idToken)) return null;

        var url = RestUrl(teamId, docName);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", idToken);

        using var resp = await RestHttp.SendAsync(req).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            FirestoreUsageMeter.RecordRest("GET", url, 0);
            return new TeamDetailsDoc { Reachable = true, Exists = false };
        }
        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        FirestoreUsageMeter.RecordRestBody("GET", url, body);
        if (!resp.IsSuccessStatusCode)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[TeamDetailsDoc] REST download {(int)resp.StatusCode}: {body[..Math.Min(160, body.Length)]}");
            return null;
        }

        using var json = JsonDocument.Parse(body);
        var fields = new Dictionary<string, string>();
        DateTimeOffset lm = default;
        if (json.RootElement.TryGetProperty("fields", out var f))
        {
            foreach (var p in f.EnumerateObject())
            {
                if (p.Value.TryGetProperty("timestampValue", out var tsv))
                {
                    if (p.Name == LastModifiedField) DateTimeOffset.TryParse(tsv.GetString(), out lm);
                    continue;
                }
                if (p.Value.TryGetProperty("stringValue", out var sv))
                    fields[p.Name] = sv.GetString() ?? string.Empty;
            }
        }
        return new TeamDetailsDoc { Reachable = true, Exists = true, Fields = fields, LastModifiedUtc = lm };
    }

    public async Task<bool> SaveAsync(string teamId, string docName, IReadOnlyDictionary<string, string> fields)
    {
        if (string.IsNullOrWhiteSpace(teamId) || fields is null) return false;
        string? uid;
        try { uid = await _auth.EnsureSignedInAsync().ConfigureAwait(false); }
        catch { uid = null; }
        if (uid is null) return false;

        var now = DateTimeOffset.UtcNow;
        try
        {
            await UploadViaRestAsync(teamId, docName, fields, now, uid).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TeamDetailsDoc] REST upload {docName}: {ex.Message}");
        }

        try
        {
            var payload = new Dictionary<string, object?>();
            foreach (var kv in fields) payload[kv.Key] = kv.Value ?? string.Empty;
            payload[LastModifiedField] = now.ToString("o");
            payload[UpdatedByUidField] = uid;
            await _db.GetDocument(DocPath(teamId, docName)).SetDataAsync(payload).ConfigureAwait(false);
            FirestoreUsageMeter.RecordSdk("Set", docName);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TeamDetailsDoc] SDK upload {docName}: {ex.Message}");
            return false;
        }
    }

    private async Task UploadViaRestAsync(string teamId, string docName,
        IReadOnlyDictionary<string, string> fields, DateTimeOffset now, string uid)
    {
        var idToken = await GetTokenAsync().ConfigureAwait(false);
        if (string.IsNullOrEmpty(idToken))
            throw new InvalidOperationException("No Firebase id token for details upload");

        var restFields = new Dictionary<string, object>();
        foreach (var kv in fields)
            restFields[kv.Key] = new Dictionary<string, object> { ["stringValue"] = kv.Value ?? "" };
        restFields[LastModifiedField] = new Dictionary<string, object>
        {
            ["timestampValue"] = now.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")
        };
        restFields[UpdatedByUidField] = new Dictionary<string, object> { ["stringValue"] = uid };

        var mask = string.Join("&", restFields.Keys.Select(k => $"updateMask.fieldPaths={Uri.EscapeDataString(k)}"));
        var patchUrl = RestUrl(teamId, docName) + "?" + mask;

        using var patch = new HttpRequestMessage(HttpMethod.Patch, patchUrl);
        patch.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", idToken);
        patch.Content = new StringContent(JsonSerializer.Serialize(new { fields = restFields }), Encoding.UTF8, "application/json");

        using var resp = await RestHttp.SendAsync(patch).ConfigureAwait(false);
        var respBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        FirestoreUsageMeter.RecordRestBody("PATCH", patchUrl, respBody);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Details upload {(int)resp.StatusCode}: {respBody[..Math.Min(200, respBody.Length)]}");
        System.Diagnostics.Debug.WriteLine($"[TeamDetailsDoc] REST upload OK {docName} team={teamId}");
    }

    private async Task<string?> GetTokenAsync()
    {
        var t = await _auth.GetIdTokenAsync(forceRefresh: false).ConfigureAwait(false);
        if (string.IsNullOrEmpty(t))
            t = await _auth.GetIdTokenAsync(forceRefresh: true).ConfigureAwait(false);
        return t;
    }
}
