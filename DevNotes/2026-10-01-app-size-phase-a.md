# App size Phase A (2026-10-01)

Branch: `reduce-app-size`

## Baseline (3.4.0 b38 TestFlight IPA)

| Metric | Size |
|--------|------|
| IPA zip | ~46 MB |
| Unpacked `.app` | ~144 MB |
| `Frameworks/` | ~72 MB |
| `grpc` + `grpcpp` **PrivateHeaders** | ~40 MB |
| App Store listing (~install) | ~170 MB |

## Phase A changes

1. **A1** — MSBuild target `StripNativeFrameworkHeaders` deletes `Frameworks/*/Headers` and `PrivateHeaders` before `_CodesignAppBundle`.
2. **A2** — Clean Release rebuild (confirm ImageSharp / QuestPDF gone).
3. **A3** — Welcome splash `BaseSize` 256→128; exclude `wwwroot/**` and `dotnet_bot`; `SatelliteResourceLanguages=en`.
4. **A4** — Release `DebugType=none` / `DebugSymbols=false` (iOS + Android).
5. **A5** — `scripts/build/measure-ipa-size.sh`.

## Strip proof (Debug sim Frameworks copy, 2026-10-01)

Manual delete of `Headers` / `PrivateHeaders` under `Frameworks/`:

| | Size |
|--|------|
| Before | **85 MB** |
| After | **33 MB** |
| Saved | **~52 MB** |
| Header dirs left | 0 |

ImageSharp / QuestPDF are **not** in the current NuGet graph (stale IPA leftovers). Clean Release rebuild should drop them + `dotnet_bot`.

## After Release rebuild (2026-10-02, strip script fixed)

First MSBuild `RemoveDir` wildcards did **not** expand on macOS (IPA still had headers). Fixed via `scripts/build/strip-ios-framework-headers.sh` called before `_CodesignAppBundle`.

| Metric | Before (b38) | After (3.4.1 b39 Phase A) |
|--------|--------------|---------------------------|
| IPA zip | ~46 MB | **29 MB** |
| Unpacked `.app` | ~144 MB | **81 MB** |
| `Frameworks/` | ~72 MB | **19 MB** (74028→21304 KB at strip) |
| Header dirs | 36 | **0** |
| ImageSharp / QuestPDF / `dotnet_bot` | present (stale) | **gone** |

App Store listing size should drop similarly (~170 MB → ~90–100 MB install estimate).

Desktop copy: `~/Desktop/TurfTime2-3.4.1-b39-TestFlight.ipa`

## Deferred

- **Phase B** — modular `Plugin.Firebase.*` (drop Analytics/Crashlytics/Storage/…).
- **Phase C** — REST-only Firestore (drop gRPC binaries).
- **Phase D** — `MtouchLink=Full`.
