#if DEBUG
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TurfTime2.ViewModels;

namespace TurfTime2.Services;

/// <summary>
/// Debug-only localhost HTTP listener for automated tests.
/// Binds 127.0.0.1 only. Not compiled into Release / store builds.
/// This skeleton has not been built on the box (no iOS workload here).
/// </summary>
public sealed class TestApiService : ITestApi, IDisposable
{
    public const int DefaultPort = 18765;
    private const int MaxErrors = 100;
    private const string ListenerPrefix = "http://127.0.0.1:18765/";

    private static readonly ConcurrentQueue<TestApiError> SharedErrors = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly ISessionStorageService _sessions;
    private readonly object _gate = new();
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private GameViewModel? _vm;
    private bool _listening;

    public TestApiService(ISessionStorageService sessions)
    {
        _sessions = sessions;
    }

    /// <summary>Safe to call before DI has started the service (global exception hooks).</summary>
    public static void RecordError(string source, Exception ex)
    {
        SharedErrors.Enqueue(new TestApiError
        {
            At = DateTimeOffset.UtcNow,
            Source = source,
            Type = ex.GetType().FullName ?? ex.GetType().Name,
            Message = ex.Message,
            StackTrace = ex.ToString()
        });
        while (SharedErrors.Count > MaxErrors && SharedErrors.TryDequeue(out _))
        { /* trim */ }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_listening) return;
            try
            {
                var listener = new HttpListener();
                listener.Prefixes.Add(ListenerPrefix);
                listener.Start();
                _listener = listener;
                _cts = new CancellationTokenSource();
                _loop = Task.Run(() => ListenLoopAsync(_cts.Token));
                _listening = true;
                System.Diagnostics.Debug.WriteLine($"[TestApi] Listening on {ListenerPrefix}");
            }
            catch (Exception ex)
            {
                RecordError("TestApi.Start", ex);
                System.Diagnostics.Debug.WriteLine($"[TestApi] Failed to start listener: {ex}");
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _listening = false;
            try { _cts?.Cancel(); } catch { /* ignore */ }
            try { _listener?.Stop(); } catch { /* ignore */ }
            try { _listener?.Close(); } catch { /* ignore */ }
            _listener = null;
            _cts?.Dispose();
            _cts = null;
        }
    }

    public void Attach(GameViewModel? viewModel)
    {
        _vm = viewModel;
        System.Diagnostics.Debug.WriteLine(
            $"[TestApi] Attached GameViewModel: {(viewModel is null ? "null" : viewModel.TeamName)}");
    }

    public void Dispose() => Stop();

    public IReadOnlyList<TestApiError> GetErrors() => SharedErrors.ToArray();

    public TestApiResult GetState()
    {
        var vm = _vm;
        if (vm is null)
            return TestApiResult.Fail(
                "Game page is not ready. Open the Game tab in a Debug simulator build.",
                BuildMeta());

        // Public properties only — safe to read off the UI thread for a snapshot.
        try
        {
            return TestApiResult.Success(BuildState(vm));
        }
        catch (Exception ex)
        {
            RecordError("TestApi.GetState", ex);
            return TestApiResult.Fail(ex.Message, BuildMeta());
        }
    }

    public async Task<TestApiResult> GetReportsAsync()
    {
        var teamId = Preferences.Get("team_id", string.Empty);
        if (string.IsNullOrEmpty(teamId))
            return TestApiResult.Fail("No team selected.", BuildMeta());

        var isLocal = teamId.StartsWith("local_", StringComparison.Ordinal)
            || string.Equals(Preferences.Get("team_mode", string.Empty), "local", StringComparison.Ordinal);

        try
        {
            var list = await _sessions.LoadSessionSummariesAsync(teamId, isLocal).ConfigureAwait(false);
            return TestApiResult.Success(new
            {
                teamId,
                reports = list.Select(s => new
                {
                    s.SessionId,
                    s.StartTime,
                    s.EndTime,
                    s.Location,
                    s.MatchDuration
                }).ToList()
            });
        }
        catch (Exception ex)
        {
            RecordError("TestApi.GetReports", ex);
            return TestApiResult.Fail(ex.Message, BuildMeta());
        }
    }

    public async Task<TestApiResult> GetReportAsync(string sessionId)
    {
        var teamId = Preferences.Get("team_id", string.Empty);
        if (string.IsNullOrEmpty(teamId))
            return TestApiResult.Fail("No team selected.", BuildMeta());
        if (string.IsNullOrWhiteSpace(sessionId))
            return TestApiResult.Fail("Missing report id.", BuildMeta());

        try
        {
            var session = await _sessions.LoadSessionAsync(teamId, sessionId).ConfigureAwait(false);
            if (session is null)
                return TestApiResult.Fail($"Report '{sessionId}' was not found.", BuildMeta());
            return TestApiResult.Success(session);
        }
        catch (Exception ex)
        {
            RecordError("TestApi.GetReport", ex);
            return TestApiResult.Fail(ex.Message, BuildMeta());
        }
    }

    private object BuildMeta() => new
    {
        listening = _listening,
        port = DefaultPort,
        gameAttached = _vm is not null
    };

    private object BuildState(GameViewModel vm)
    {
        return new
        {
            listening = _listening,
            port = DefaultPort,
            gameAttached = true,
            teamId = Preferences.Get("team_id", string.Empty),
            teamMode = Preferences.Get("team_mode", string.Empty),
            teamName = vm.TeamName,
            isAdmin = vm.IsAdmin,
            isMember = vm.IsMember,
            phase = vm.Phase.ToString(),
            matchTimeDisplay = vm.MatchTimeDisplay,
            countdownDisplay = vm.CountdownDisplay,
            matchDurationMinutes = vm.MatchDurationMinutes,
            matchTimerOverdue = vm.MatchTimerOverdue,
            countdownOverdue = vm.CountdownOverdue,
            rotationDue = vm.RotationDue,
            rotationWarning = vm.RotationWarning,
            rotationCount = vm.RotationCount,
            startButtonText = vm.StartButtonText,
            rotateButtonText = vm.RotateButtonText,
            viewMode = vm.ViewMode.ToString(),
            scoreUs = vm.TeamAScore,
            scoreThem = vm.TeamBScore,
            players = vm.Players.Select(p => new
            {
                slotId = p.SlotId,
                name = p.Name,
                position = p.Position.ToString(),
                fieldCell = p.FieldCell,
                fieldSeconds = p.FieldSeconds,
                isNextToRotate = p.IsNextToRotate
            }).ToList(),
            rotationPairs = vm.RotationPairs.Select(p => new
            {
                benchPlayerName = p.BenchPlayerName,
                fieldPlayerName = p.FieldPlayerName
            }).ToList()
        };
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        var listener = _listener;
        if (listener is null) return;

        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync().WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (HttpListenerException)
            {
                if (ct.IsCancellationRequested) break;
                continue;
            }
            catch (Exception ex)
            {
                RecordError("TestApi.ListenLoop", ex);
                continue;
            }

            try
            {
                await HandleRequestAsync(ctx).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                RecordError("TestApi.HandleRequest", ex);
                try
                {
                    await WriteResultAsync(ctx.Response, TestApiResult.Fail(ex.Message, BuildMeta()), 500)
                        .ConfigureAwait(false);
                }
                catch
                {
                    /* ignore */ }
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;

        var host = req.UserHostName ?? req.Headers["Host"] ?? "";
        if (!IsLocalHost(host))
        {
            await WriteResultAsync(res, TestApiResult.Fail("Request rejected: localhost only."), 403)
                .ConfigureAwait(false);
            return;
        }

        var path = req.Url?.AbsolutePath?.TrimEnd('/') ?? "";
        if (path.Length == 0) path = "/";
        var method = req.HttpMethod.ToUpperInvariant();

        if (method == "GET" && path == "/health")
        {
            await WriteResultAsync(res, TestApiResult.Success(BuildMeta()), 200).ConfigureAwait(false);
            return;
        }

        if (method == "GET" && path == "/state")
        {
            var result = GetState();
            await WriteResultAsync(res, result, result.Ok ? 200 : 409).ConfigureAwait(false);
            return;
        }

        if (method == "GET" && path == "/errors")
        {
            await WriteResultAsync(res, TestApiResult.Success(new { errors = GetErrors() }), 200)
                .ConfigureAwait(false);
            return;
        }

        if (method == "GET" && path == "/reports")
        {
            var result = await GetReportsAsync().ConfigureAwait(false);
            await WriteResultAsync(res, result, result.Ok ? 200 : 409).ConfigureAwait(false);
            return;
        }

        if (method == "GET" && path.StartsWith("/reports/", StringComparison.Ordinal))
        {
            var id = Uri.UnescapeDataString(path["/reports/".Length..]);
            var result = await GetReportAsync(id).ConfigureAwait(false);
            var status = result.Ok ? 200 : (result.Error?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true ? 404 : 409);
            await WriteResultAsync(res, result, status).ConfigureAwait(false);
            return;
        }

        if (method == "POST" && IsKnownPostRoute(path))
        {
            var result = TestApiResult.Fail(
                $"Endpoint {method} {path} is not wired yet. Next step: call the GameViewModel method documented in docs/test-api.md.",
                GetState().State ?? BuildMeta());
            await WriteResultAsync(res, result, 501).ConfigureAwait(false);
            return;
        }

        await WriteResultAsync(res, TestApiResult.Fail($"Unknown route: {method} {path}"), 404)
            .ConfigureAwait(false);
    }

    private static bool IsKnownPostRoute(string path) => path is
        "/team/load-demo" or
        "/team/players" or
        "/game/settings" or
        "/game/start" or
        "/game/pause" or
        "/game/rotate" or
        "/game/halftime" or
        "/game/second-half" or
        "/game/end" or
        "/game/reset" or
        "/game/reset-rotation-clock";

    private static bool IsLocalHost(string host)
    {
        var name = host.Split(':')[0].Trim();
        return name.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || name.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task WriteResultAsync(HttpListenerResponse res, TestApiResult result, int status)
    {
        var json = JsonSerializer.Serialize(result, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        res.StatusCode = status;
        res.ContentType = "application/json; charset=utf-8";
        res.ContentEncoding = Encoding.UTF8;
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        res.OutputStream.Close();
    }
}
#endif
