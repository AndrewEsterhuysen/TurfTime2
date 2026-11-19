#if DEBUG
using TurfTime2.ViewModels;

namespace TurfTime2.Services;

/// <summary>
/// Debug-only in-app test/automation API. Compiled out of Release.
/// See docs/test-api.md.
/// </summary>
public interface ITestApi
{
    void Start();
    void Stop();
    void Attach(GameViewModel? viewModel);
    TestApiResult GetState();
    Task<TestApiResult> GetReportsAsync();
    Task<TestApiResult> GetReportAsync(string sessionId);
    IReadOnlyList<TestApiError> GetErrors();
}

/// <summary>JSON envelope for every test API response: { ok, error, state }.</summary>
public sealed class TestApiResult
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public object? State { get; init; }

    public static TestApiResult Success(object? state = null)
        => new() { Ok = true, Error = null, State = state };

    public static TestApiResult Fail(string error, object? state = null)
        => new() { Ok = false, Error = error, State = state };
}

public sealed class TestApiError
{
    public DateTimeOffset At { get; init; }
    public string Source { get; init; } = "";
    public string Type { get; init; } = "";
    public string Message { get; init; } = "";
    public string? StackTrace { get; init; }
}
#endif
