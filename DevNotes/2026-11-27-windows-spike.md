# Windows spike (2026-11-27)

Branch: `feature/windows-spike` (from `feature/club-admin`). No source changes were needed: the existing
`net10.0-windows10.0.19041` target already builds and launches.

## Environment
- Windows 11 Pro 26100 **ARM64** VM (UTM on Apple Silicon). Shells run as x64 (emulated) but `dotnet` is the win-arm64 SDK 10.0.301.
- Visual Studio Community 2026 18.7.1, workloads .NET MAUI + .NET desktop. `maui-windows` dotnet workload installed.
- Repo cloned to `C:\src\TurfTime2` (short path; avoids MAX_PATH).

## Build (Debug, unpackaged, arm64)
```
dotnet build TurfTime2.csproj -p:TargetFrameworks=net10.0-windows10.0.19041 -f net10.0-windows10.0.19041 -c Debug -p:RuntimeIdentifier=win-arm64 -p:PublishSingleFile=false
```
- `-p:TargetFrameworks=...` is needed: a plain multi-target restore on Windows also tries android/ios and, with a global
  `RuntimeIdentifier`, fails with NU1101 (`Microsoft.NETCore.App.Runtime.Mono.win-arm64`).
- The csproj hard-codes `RuntimeIdentifier=win-x64` for the Windows TFM; the override above builds native arm64. x64 would run under emulation.
- Result: 0 errors, ~500 warnings (mostly obsolete `DisplayAlert`). Output: `bin\Debug\net10.0-windows10.0.19041\win-arm64\TurfTime2.exe`.

## Launch results
- App starts, no crash, window ~1100x600, responsive (~185 MB).
- Plugin.Firebase on Windows is the reference-assembly stub (throws `NotImplementedException`); startup paths are fail-soft, so
  local play works: first-run name prompt, Welcome, Game page with the demo team, Settings, Club admin page, Team Selection.
- Create Club (online) shows the alert "Cloud team service not available". Redeem club admin invite fails silently (no alert seen).
  "Your clubs" says you are not an owner of any club (no auth/Firestore).
- Not exercised: QR camera, chat, push (none on Windows), drag-reorder on the Game page (no Windows handler).

## Next
A Windows-only Firebase Auth REST + Firestore REST layer (see assessment), starting with CloudTeamService / ClubAdminService / ClubPlayerService.
