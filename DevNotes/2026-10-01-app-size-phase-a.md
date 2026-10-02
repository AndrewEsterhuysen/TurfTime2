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

## After Release rebuild (blocked pending Xcode license)

`dotnet publish` Release failed: `actool exited with code 69` — **Xcode license not agreed**. Re-run after:

```bash
sudo xcodebuild -license
SKIP_VERSION_BUMP=1 ./scripts/build/MAC-build-release-ios.sh
./scripts/build/measure-ipa-size.sh bin/Release/net10.0-ios/ios-arm64/publish/TurfTime2.ipa
```

| Metric | Size |
|--------|------|
| IPA zip | _(pending)_ |
| Unpacked `.app` | _(pending)_ |
| `Frameworks/` | _(pending)_ |
| Header dirs remaining | _(pending)_ |
| ImageSharp / QuestPDF | _(pending)_ |

## Deferred

- **Phase B** — modular `Plugin.Firebase.*` (drop Analytics/Crashlytics/Storage/…).
- **Phase C** — REST-only Firestore (drop gRPC binaries).
- **Phase D** — `MtouchLink=Full`.
