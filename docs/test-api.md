# TurfTimer in-app test API

An automation surface so test scripts can call game operations directly and get a structured error when something fails. It complements the Mac XCUITest tap-through harness (`~/tt-uitest`), which can show that a screen looks wrong but not why.

This is **not** the Cloud Run MCP server in `mcp-server/`. MCP is for a user's own AI assistant, with the user's permissions, against Firestore. This API is a Debug-only localhost listener inside the MAUI app, aimed at the simulator.

**Status:** design + compiling skeleton on `feature/test-api`. The box cannot build iOS, so this branch has not been compiled. Next step is to wire the POST endpoints to `GameViewModel` and build a Debug simulator binary on the Mac.

This work does not change squad-size limits (still 16) and is kept separate from any 25-player branch.

## Security

The listener must never ship in an App Store, Play, or TestFlight build:

- The types, DI registration, and listener are wrapped in `#if DEBUG` and are compiled out of Release.
- `HttpListener` binds **`127.0.0.1` only** (not `0.0.0.0` or `::`). Requests whose `Host` is not `127.0.0.1` or `localhost` are rejected.
- There is no authentication. Localhost binding is the security model. Do not punch this through to a LAN or device IP.
- iOS Simulator shares the Mac network stack, so `curl http://127.0.0.1:18765/health` on the Mac reaches the app. Android emulator needs `adb forward tcp:18765 tcp:18765` first.
- A physical-device Debug build would also listen, but only on the device loopback, which scripts on the Mac cannot reach without a port-forward. Prefer the simulator.

Default port: **18765** (`TestApiService.DefaultPort`).

## Architecture

```
test script (curl / Python / later ttc)
    -- HTTP/JSON, 127.0.0.1:18765 -->
TestApiListener (Debug-only HttpListener)
    -- ITestApi -->
TestApiService
    -- MainThread -->
GameViewModel / IGameTimerService / IGameLoggerService / ISessionStorageService
```

`GameViewModel` is constructed by `GamePage.CreateViewModelAsync`, not resolved from DI for the live screen. `GamePage` therefore calls `ITestApi.Attach(vm)` when the view-model is created. Mutations must run on the UI thread.

Every response is JSON:

```json
{ "ok": true, "error": null, "state": { } }
```

- `ok` — whether the call did what was asked.
- `error` — a clear reason on failure (precondition, missing view-model, exception message). `null` on success.
- `state` — a snapshot after the call (same shape as `GET /state`), plus any endpoint-specific fields.

`GameViewModel` methods are often `void` and silently return when `IsMember` is set, or when the phase is wrong (`SetMatchDuration` only works in `Setup`). The API must check those preconditions and return `ok: false` rather than reporting success for a no-op.

HTTP status: `200` success, `400`/`409` precondition or bad request, `404` unknown route or missing report, `501` not wired yet, `500` unexpected exception. The envelope is always present.

## Local vs online teams

- **Local** teams (id starts with `local_`, or `Preferences["team_mode"] == "local"`) store the roster and session history in `Preferences`. The seeded demo team is `local_demo_team` / "Demo Team", created by `App.EnsureDemoTeamOnFirstRunAsync`.
- **Online / shared** teams live in Firestore via `ICloudRosterService` / `ICloudTeamService`. `GameViewModel` still owns the live match. Session reports go through `ISessionStorageService` (cloud first, local fallback).

The test API talks to the in-process view-model, so it works for both. Prefer the demo team for automation: it is local, already seeded, and has a known 16-player layout (slots 1–6 field/goalie, 7–9 bench, 10–16 absent, 20-second rotation).

## Endpoints

Unless noted, POST bodies are JSON objects. Empty bodies are allowed where no fields are required.

### `GET /health`

Liveness. `state` includes `listening`, `port`, and `gameAttached`.

### `GET /state`

Current match snapshot from the attached `GameViewModel`:

- team: `teamId`, `teamMode`, `teamName`, `isAdmin`, `isMember`
- clocks: `phase` (`GamePhase`: Setup, FirstHalf, HalfTime, SecondHalf, Ended, Finished), `matchTimeDisplay`, `countdownDisplay`, `matchDurationMinutes`, `matchTimerOverdue`, `countdownOverdue`, `rotationDue`, `rotationWarning`, `rotationCount`, `startButtonText`, `rotateButtonText`, `viewMode`
- score: `scoreUs` (`TeamAScore`), `scoreThem` (`TeamBScore`)
- roster: `players[]` with `slotId`, `name`, `position` (`PlayerPosition`: Field, Bench, Goalie, Inactive), `fieldCell`, `fieldSeconds`, `isNextToRotate`
- `rotationPairs[]` (`BenchPlayerName`, `FieldPlayerName`)

Fails with a clear error if the Game tab has not created a view-model yet.

### `POST /team/load-demo`

Select the seeded demo team (`local_demo_team`). Intended wiring: ensure the first-run seed exists (today `App.EnsureDemoTeamOnFirstRunAsync` is private), set `team_mode=local`, `team_id=local_demo_team`, `team_name=Demo Team`, `user_role=admin`, then `GameViewModel.InitialiseAsync("local_demo_team", "admin")` and navigate to the Game tab.

There is no public "load demo" method today. Skeleton returns `501` until that helper is extracted.

### `POST /team/players`

Move or rename an existing roster slot. Body:

```json
{ "slotId": 7, "position": "field", "cell": 4, "name": "Sam" }
```

Maps to:

- `GameViewModel.SetPlayerPosition(player, PlayerPosition)` — Field, Bench, Goalie, or Inactive (Absent)
- `GameViewModel.PlaceOrSwapOnFieldCell(player, cell)` when `cell` is set (outfield cells 1–16)
- `GameViewModel.RenamePlayer(player, name)` when `name` is set

This does **not** add or remove roster slots. Squad size stays at 16; player add/remove lives on Team Details and is out of scope here. Mid-game swaps can later use `LiveSubstituteWithBenchPlayer` / `LiveSubstituteWithNextBench`.

### `POST /game/settings`

```json
{ "matchMinutes": 40, "rotationSeconds": 20, "rotationCount": 1, "rotationWarningSeconds": 10 }
```

Maps to `SetMatchDuration` (Setup phase only), `SetCountdownPreset` (minutes + seconds derived from `rotationSeconds`), `RotationCount` / `ReseedRotationQueues`, and `UpdateRotationWarningSeconds`. Values are also stored in `Preferences` (`game.matchDurationMinutes`, `game.countdownPresetSeconds`).

### Game control

These are explicit actions. Do not blindly call `ToggleStartPause` and hope: that one method starts, pauses, resumes, presses half-time, starts the second half, ends, or resets, depending on `Phase` and `TimerRunning`. The API should check phase first and return a precise error (for example "cannot start second half: phase is FirstHalf, not HalfTime").

| Endpoint | When it is valid | Real method |
|---|---|---|
| `POST /game/start` | `Setup`, or a paused `FirstHalf` / `SecondHalf` | `ToggleStartPause` → private `StartOrResumeGame` (`IGameTimerService.StartMatch`) |
| `POST /game/pause` | Running `FirstHalf` / `SecondHalf` (or running overdue half-time before the break is taken) | `ToggleStartPause` → private `PauseGame` (`PauseMatch`) |
| `POST /game/rotate` | Coach wants the queued swaps | `ExecuteRotations` (then `ResetCountdown`) |
| `POST /game/halftime` | `Phase == HalfTime` and the timer is still running (button shows "1/2 Time") | `ToggleStartPause` → `PauseMatch` + `IGameLoggerService.Log(HalfTime)` |
| `POST /game/second-half` | `Phase == HalfTime` and the timer is paused (button shows "2nd Half") | `ToggleStartPause` → `IGameTimerService.StartSecondHalf`, `ResumeMatch`, `ResetCountdown` |
| `POST /game/end` | `Phase == Ended` (regulation finished; button shows "End") | `ToggleStartPause` → private `EndGame` (`IGameLoggerService.EndSession`, `MarkFinished`) |
| `POST /game/reset` | `Phase == Finished` (after End; the UI is a press-and-hold "Reset") | `RestartGameCommand` → private `RestartGame` (`IGameTimerService.Reset`, restore start configuration) |
| `POST /game/reset-rotation-clock` | Skip a due rotation without swapping | `ResetRotationClock` |

`ResetMatchState` is different: Team Details calls it when switching teams, and it is not the press-and-hold Reset.

Half-time sequence, matching the demo script: wait until `phase` is `HalfTime`, `POST /game/halftime`, wait 2–3 seconds, `POST /game/second-half`.

### `GET /reports`

Lists archived sessions for the current team via `ISessionStorageService.LoadSessionSummariesAsync(teamId, isLocalTeam)` (`SessionSummary`: `sessionId`, `startTime`, `endTime`, `location`, `matchDuration`). Local history also sits in `Preferences["roster.sessionHistory.v1"]` (`IGameLoggerService.GetSessionHistory`).

### `GET /reports/{id}`

Full `GameSession` via `ISessionStorageService.LoadSessionAsync` (events, scores, `GameSessionSummary` / `PlayerStats` including field time and rotation counts). This is what the Reports page emails.

### `GET /errors`

Ring buffer of exceptions captured from `AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException`, and failures inside the test API itself. Each item: `at`, `source`, `type`, `message`, `stackTrace`. This is the gap the tap-through tests cannot fill.

## Skeleton (this branch)

Compiled only in Debug:

- `Services/ITestApi.cs` — `ITestApi`, `TestApiResult`, `TestApiError`
- `Services/TestApiService.cs` — localhost listener, `GET /health`, `GET /state`, `GET /errors`, `GET /reports`, `GET /reports/{id}`; POST routes return `501` until wired
- `MauiProgram.cs` — singleton registration + `Start()`; exception hooks record into the error buffer
- `Pages/GamePage.xaml.cs` — `Attach(viewModel)` after the live `GameViewModel` is created

Release behaviour is unchanged: the symbols do not exist.

## Next step

1. Build Debug `net10.0-ios` / `iossimulator-arm64` on the Mac and confirm `GET /health` and `GET /state` against the simulator.
2. Wire each POST route to the methods in the table, with precondition checks and a post-action `state` snapshot, all on `MainThread`.
3. Optionally extend `~/tt-uitest/ttc` with HTTP commands so a script can mix taps (alerts, navigation) with API calls (start, rotate, assert roster times).
4. Keep this on `feature/test-api`. Do not merge to `master` until Andrew has tried a Debug simulator build.
