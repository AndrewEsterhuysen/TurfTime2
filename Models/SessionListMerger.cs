using System.Globalization;
using System.Text.Json;

namespace TurfTime2.Models;

/// <summary>Completed matches for a team, plus whether the cloud list could be read.</summary>
public sealed class SessionListResult
{
    public IReadOnlyList<SessionSummary> Sessions { get; init; } = [];

    /// <summary>True when neither the SDK nor the REST list of cloud sessions could be read.</summary>
    public bool CloudFailed { get; init; }

    /// <summary>Last cloud error, for logs.</summary>
    public string? CloudError { get; init; }
}

/// <summary>
/// Pure helpers that build the Nominations match list from cloud session docs
/// (Plugin.Firebase or Firestore REST) and this device's local history.
/// No MAUI or Firebase types, so it can be unit tested on the box.
/// </summary>
public static class SessionListMerger
{
    /// <summary>Summary fields read by the REST list (sessionJson is skipped to keep it small).</summary>
    public static readonly string[] SummaryFieldPaths =
        ["sessionId", "startTime", "endTime", "location", "matchDuration"];

    /// <summary>
    /// Parses one page of a Firestore REST <c>documents.list</c> response.
    /// </summary>
    public static (List<SessionSummary> Items, string? NextPageToken) ParseRestListPage(string body)
    {
        var items = new List<SessionSummary>();
        if (string.IsNullOrWhiteSpace(body)) return (items, null);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        if (root.TryGetProperty("documents", out var docs) && docs.ValueKind == JsonValueKind.Array)
        {
            foreach (var doc in docs.EnumerateArray())
            {
                var s = FromRestDocument(doc);
                if (s is not null) items.Add(s);
            }
        }
        var next = root.TryGetProperty("nextPageToken", out var t) ? t.GetString() : null;
        return (items, string.IsNullOrEmpty(next) ? null : next);
    }

    /// <summary>One REST document → summary. The doc id is the session id when the field is missing.</summary>
    public static SessionSummary? FromRestDocument(JsonElement doc)
    {
        var name = doc.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        var docId = name.Contains('/') ? name[(name.LastIndexOf('/') + 1)..] : name;
        var hasFields = doc.TryGetProperty("fields", out var f) && f.ValueKind == JsonValueKind.Object;

        var sessionId = hasFields ? RestString(f, "sessionId") : "";
        if (string.IsNullOrEmpty(sessionId)) sessionId = docId;
        if (string.IsNullOrEmpty(sessionId)) return null;

        var start = hasFields ? RestTimestamp(f, "startTime") : null;
        var end = hasFields ? RestTimestamp(f, "endTime") : null;
        return new SessionSummary
        {
            SessionId = sessionId,
            StartTime = (start ?? end ?? DateTimeOffset.MinValue).UtcDateTime,
            EndTime = end?.UtcDateTime,
            Location = hasFields ? RestString(f, "location") : "",
            MatchDuration = (hasFields ? RestInt(f, "matchDuration") : 0) / 60
        };
    }

    /// <summary>
    /// One Plugin.Firebase document → summary. Prefers the parsed sessionJson; fills
    /// gaps (e.g. no EndTime in sessionJson) from the doc's own fields.
    /// Returns null when the doc has nothing usable (empty SDK data).
    /// </summary>
    public static SessionSummary? FromSdkDocument(string? docId, IDictionary<string, object>? data, GameSession? parsed)
    {
        var sessionId = parsed?.SessionId;
        if (string.IsNullOrEmpty(sessionId)) sessionId = SdkString(data, "sessionId");
        if (string.IsNullOrEmpty(sessionId)) sessionId = docId ?? "";
        if (string.IsNullOrEmpty(sessionId) || (parsed is null && (data is null || data.Count == 0)))
            return null;

        var start = parsed?.StartTime ?? SdkTimestamp(data, "startTime");
        var end = parsed?.EndTime ?? SdkTimestamp(data, "endTime");
        var location = parsed?.Location;
        if (string.IsNullOrEmpty(location)) location = SdkString(data, "location");
        var durationSeconds = parsed?.MatchDurationSeconds ?? SdkInt(data, "matchDuration");
        return new SessionSummary
        {
            SessionId = sessionId,
            StartTime = (start ?? end ?? DateTimeOffset.MinValue).UtcDateTime,
            EndTime = end?.UtcDateTime,
            Location = location ?? "",
            MatchDuration = durationSeconds / 60
        };
    }

    public static SessionSummary FromSession(GameSession s) => new()
    {
        SessionId = s.SessionId,
        StartTime = s.StartTime.UtcDateTime,
        EndTime = s.EndTime?.UtcDateTime,
        Location = s.Location ?? "",
        MatchDuration = s.MatchDurationSeconds / 60
    };

    /// <summary>
    /// Whether a local history entry belongs to this team. Entries record their TeamId
    /// from 3.6.0; older entries only have TeamName, so they match on the team's name.
    /// </summary>
    public static bool BelongsToTeam(GameSession s, string teamId, string? teamName)
    {
        if (!string.IsNullOrWhiteSpace(s.TeamId))
            return string.Equals(s.TeamId, teamId, StringComparison.Ordinal);
        return !string.IsNullOrWhiteSpace(teamName)
               && !string.IsNullOrWhiteSpace(s.TeamName)
               && string.Equals(s.TeamName.Trim(), teamName.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Cloud and local summaries, de-duplicated by SessionId (cloud first, gaps filled
    /// from local), keeping completed matches only (EndTime set), newest first.
    /// </summary>
    public static List<SessionSummary> MergeCompleted(
        IEnumerable<SessionSummary> cloud, IEnumerable<SessionSummary> local)
    {
        var byId = new Dictionary<string, SessionSummary>(StringComparer.Ordinal);
        foreach (var s in cloud.Concat(local))
        {
            if (string.IsNullOrWhiteSpace(s.SessionId)) continue;
            if (!byId.TryGetValue(s.SessionId, out var existing))
            {
                byId[s.SessionId] = new SessionSummary
                {
                    SessionId = s.SessionId,
                    StartTime = s.StartTime,
                    EndTime = s.EndTime,
                    Location = s.Location ?? "",
                    MatchDuration = s.MatchDuration
                };
                continue;
            }
            existing.EndTime ??= s.EndTime;
            if (existing.StartTime == DateTime.MinValue) existing.StartTime = s.StartTime;
            if (string.IsNullOrEmpty(existing.Location)) existing.Location = s.Location ?? "";
            if (existing.MatchDuration == 0) existing.MatchDuration = s.MatchDuration;
        }

        foreach (var s in byId.Values)
            if (s.StartTime == DateTime.MinValue && s.EndTime is { } e) s.StartTime = e;

        return byId.Values
            .Where(s => s.EndTime is not null)
            .OrderByDescending(s => s.StartTime)
            .ToList();
    }

    // ── Value readers ─────────────────────────────────────────────────────

    private static string RestString(JsonElement fields, string name)
        => fields.TryGetProperty(name, out var v) && v.TryGetProperty("stringValue", out var s) ? s.GetString() ?? "" : "";

    private static int RestInt(JsonElement fields, string name)
    {
        if (!fields.TryGetProperty(name, out var v)) return 0;
        if (v.TryGetProperty("integerValue", out var iv)
            && long.TryParse(iv.ValueKind == JsonValueKind.String ? iv.GetString() : iv.GetRawText(),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
            return (int)Math.Clamp(l, int.MinValue, int.MaxValue);
        if (v.TryGetProperty("doubleValue", out var dv) && dv.TryGetDouble(out var d))
            return (int)d;
        return 0;
    }

    private static DateTimeOffset? RestTimestamp(JsonElement fields, string name)
    {
        if (!fields.TryGetProperty(name, out var v)) return null;
        var raw = v.TryGetProperty("timestampValue", out var tv) ? tv.GetString()
                : v.TryGetProperty("stringValue", out var sv) ? sv.GetString()
                : null;
        return ParseTime(raw);
    }

    private static string SdkString(IDictionary<string, object>? d, string key)
        => d is not null && d.TryGetValue(key, out var v) && v is not null ? v.ToString() ?? "" : "";

    private static int SdkInt(IDictionary<string, object>? d, string key)
    {
        if (d is null || !d.TryGetValue(key, out var v) || v is null) return 0;
        try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); }
        catch { return int.TryParse(v.ToString(), out var i) ? i : 0; }
    }

    private static DateTimeOffset? SdkTimestamp(IDictionary<string, object>? d, string key)
    {
        if (d is null || !d.TryGetValue(key, out var v) || v is null) return null;
        switch (v)
        {
            case DateTimeOffset dto: return dto.ToUniversalTime();
            case DateTime dt:
                return new DateTimeOffset(dt.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(dt, DateTimeKind.Utc)
                    : dt.ToUniversalTime());
            case string s: return ParseTime(s);
        }
        try
        {
            var t = v.GetType();
            foreach (var name in new[] { "ToDateTimeOffset", "ToDateTime", "ToDate" })
            {
                switch (t.GetMethod(name, Type.EmptyTypes)?.Invoke(v, null))
                {
                    case DateTimeOffset dto2: return dto2.ToUniversalTime();
                    case DateTime dt2: return new DateTimeOffset(DateTime.SpecifyKind(dt2, DateTimeKind.Utc));
                }
            }
            if ((t.GetProperty("Seconds") ?? t.GetProperty("seconds"))?.GetValue(v) is { } sec)
                return DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(sec, CultureInfo.InvariantCulture));
        }
        catch { /* unknown timestamp type */ }
        return null;
    }

    private static DateTimeOffset? ParseTime(string? raw)
        => raw is not null && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
               DateTimeStyles.RoundtripKind, out var dto)
            ? dto.ToUniversalTime()
            : null;
}
