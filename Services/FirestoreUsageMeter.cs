namespace TurfTime2.Services;

/// <summary>
/// DEBUG-only counters for Firestore / Cloud REST / listener / callable traffic.
/// No-ops in Release. Filter device logs for <c>[FirestoreUsage]</c>.
/// </summary>
/// <remarks>
/// Firebase data-read investigation (turf-timer): continuous billed traffic was dominated by
/// (1) controller heartbeat waking roster listeners, (2) unconditional REST re-gets on watch,
/// and (3) Cloud Function <c>releaseStaleGameControllers</c> scanning all roster docs every minute.
/// Mitigations: <c>activeControllers</c> index + DEBUG metering here. Watch always-REST (Fix A)
/// remains a follow-up if client reads stay high after End/Reset releases the controller.
/// </remarks>
public static class FirestoreUsageMeter
{
#if DEBUG
    private static readonly object Gate = new();
    private static readonly Dictionary<string, long> Totals = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, long> Window = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, bool> Flags = new(StringComparer.Ordinal);

    private static CancellationTokenSource? _logCts;
    private static DateTimeOffset _windowStartedUtc = DateTimeOffset.UtcNow;
    private static DateTimeOffset _startedUtc = DateTimeOffset.UtcNow;
#endif

    public static void StartPeriodicLogging(TimeSpan? interval = null)
    {
#if DEBUG
        StopPeriodicLogging();
        _startedUtc = DateTimeOffset.UtcNow;
        _windowStartedUtc = _startedUtc;
        _logCts = new CancellationTokenSource();
        var token = _logCts.Token;
        var period = interval ?? TimeSpan.FromSeconds(60);
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(period, token).ConfigureAwait(false);
                    LogSummary(flushWindow: true);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[FirestoreUsage] log loop: {ex.Message}");
                }
            }
        }, token);
        System.Diagnostics.Debug.WriteLine("[FirestoreUsage] Periodic logging started");
#endif
    }

    public static void StopPeriodicLogging()
    {
#if DEBUG
        try { _logCts?.Cancel(); } catch { /* ignore */ }
        try { _logCts?.Dispose(); } catch { /* ignore */ }
        _logCts = null;
#endif
    }

    public static void SetFlag(string name, bool value)
    {
#if DEBUG
        lock (Gate) Flags[name] = value;
#endif
    }

    public static void Increment(string name, long delta = 1)
    {
#if DEBUG
        if (string.IsNullOrEmpty(name) || delta == 0) return;
        lock (Gate)
        {
            Totals[name] = Totals.GetValueOrDefault(name) + delta;
            Window[name] = Window.GetValueOrDefault(name) + delta;
        }
#endif
    }

    public static void RecordListener(string kind)
        => Increment($"Listener.{kind}");

    public static void RecordListenerError(string kind)
        => Increment($"ListenerError.{kind}");

    public static void RecordSdk(string op, string kind)
        => Increment($"Sdk.{op}.{kind}");

    public static void RecordHeartbeat()
        => Increment("HeartbeatPatch");

    public static void RecordCallable(string name)
        => Increment($"Callable.{name}");

    public static void RecordAuthTokenRefresh()
        => Increment("AuthTokenRefresh");

    public static void RecordRolePoll()
        => Increment("RolePollGet");

    /// <summary>
    /// Categorize a Firestore REST call from method + URL and optionally response body size.
    /// </summary>
    public static void RecordRest(string method, string? url, int? responseBytes = null)
    {
#if DEBUG
        var kind = ClassifyUrl(url);
        var verb = ClassifyMethod(method);
        Increment($"Rest.{verb}.{kind}");
        if (responseBytes is > 0)
            Increment($"RestBytes.{verb}.{kind}", responseBytes.Value);
#endif
    }

    public static void RecordRestBody(string method, string? url, string? body)
    {
#if DEBUG
        RecordRest(method, url, body?.Length);
#endif
    }

    public static string GetSummary(bool flushWindow = false)
    {
#if DEBUG
        lock (Gate)
        {
            return BuildSummaryUnlocked(flushWindow);
        }
#else
        return string.Empty;
#endif
    }

    public static void LogSummary(bool flushWindow = false)
    {
#if DEBUG
        System.Diagnostics.Debug.WriteLine(GetSummary(flushWindow));
#endif
    }

#if DEBUG
    private static string ClassifyMethod(string? method)
    {
        var m = (method ?? "GET").Trim().ToUpperInvariant();
        return m switch
        {
            "GET" => "Get",
            "PATCH" => "Patch",
            "POST" => "Post",
            "PUT" => "Put",
            "DELETE" => "Delete",
            _ => m
        };
    }

    private static string ClassifyUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "Unknown";
        var u = url;
        if (u.Contains("/roster/", StringComparison.OrdinalIgnoreCase)) return "Roster";
        if (u.Contains("/messages", StringComparison.OrdinalIgnoreCase)) return "Chat";
        if (u.Contains("/members/", StringComparison.OrdinalIgnoreCase)) return "Member";
        if (u.Contains("/details/", StringComparison.OrdinalIgnoreCase)
            || u.Contains("/location", StringComparison.OrdinalIgnoreCase)) return "Schedule";
        if (u.Contains("/sessions/", StringComparison.OrdinalIgnoreCase)) return "Session";
        if (u.Contains("/metadata/", StringComparison.OrdinalIgnoreCase)) return "Metadata";
        if (u.Contains("/invite", StringComparison.OrdinalIgnoreCase)) return "Invite";
        if (u.Contains("cloudfunctions.net", StringComparison.OrdinalIgnoreCase)
            || u.Contains("cloudfunctions", StringComparison.OrdinalIgnoreCase)) return "CallableHttp";
        if (u.Contains("identitytoolkit", StringComparison.OrdinalIgnoreCase)
            || u.Contains("securetoken", StringComparison.OrdinalIgnoreCase)) return "AuthHttp";
        if (u.Contains("/documents/", StringComparison.OrdinalIgnoreCase)) return "FirestoreOther";
        return "Other";
    }

    private static string BuildSummaryUnlocked(bool flushWindow)
    {
        var now = DateTimeOffset.UtcNow;
        var flagParts = Flags.Count == 0
            ? "none"
            : string.Join(' ', Flags.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));

        var teamMode = Preferences.Get("team_mode", string.Empty);
        var teamId = Preferences.Get("team_id", string.Empty);
        var teamShort = string.IsNullOrEmpty(teamId)
            ? "none"
            : teamId.Length <= 12 ? teamId : teamId[..12] + "…";

        var windowSecs = Math.Max(1, (now - _windowStartedUtc).TotalSeconds);
        var totalSecs = Math.Max(1, (now - _startedUtc).TotalSeconds);

        var topWindow = TopEntries(Window, 8);
        var topBytes = TopEntries(
            Window.Where(kv => kv.Key.StartsWith("RestBytes.", StringComparison.Ordinal)),
            5);
        var topTotals = TopEntries(Totals, 8);

        var sb = new System.Text.StringBuilder();
        sb.Append("[FirestoreUsage] ");
        sb.Append($"uptime={totalSecs:0}s window={windowSecs:0}s ");
        sb.Append($"teamMode={teamMode} team={teamShort} ");
        sb.Append($"flags[{flagParts}] ");
        sb.Append($"windowTop[{topWindow}] ");
        if (!string.IsNullOrEmpty(topBytes))
            sb.Append($"windowBytes[{topBytes}] ");
        sb.Append($"lifetimeTop[{topTotals}]");

        if (flushWindow)
        {
            Window.Clear();
            _windowStartedUtc = now;
        }

        return sb.ToString();
    }

    private static string TopEntries(IEnumerable<KeyValuePair<string, long>> source, int n)
    {
        var list = source
            .Where(kv => kv.Value != 0)
            .OrderByDescending(kv => kv.Value)
            .Take(n)
            .Select(kv => $"{kv.Key}={kv.Value}")
            .ToList();
        return list.Count == 0 ? "—" : string.Join(", ", list);
    }
#endif
}
