# Build signed Android Release artifacts (.aab + .apk)
# Usage: pwsh -File ./scripts/build/build-release-android.ps1

Write-Host "Building Turf Time signed Android release..." -ForegroundColor Green

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptDir "../..")
$projectFile = Join-Path $repoRoot "TurfTime2.csproj"

Write-Host "Repo: $repoRoot" -ForegroundColor Cyan
Write-Host "Project: $projectFile" -ForegroundColor Cyan

dotnet clean $projectFile -f net10.0-android -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet publish $projectFile -f net10.0-android -c Release /p:AndroidPackageFormat=aab /p:AndroidKeyStore=true
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$outputDir = Join-Path $repoRoot "bin/Release/net10.0-android/publish"
$signedAab = Join-Path $outputDir "com.andrewestherhuysen.turftime-Signed.aab"
$signedApk = Join-Path $outputDir "com.andrewestherhuysen.turftime-Signed.apk"

if (-not (Test-Path $signedAab)) {
    Write-Error "Signed AAB not found: $signedAab"
    exit 1
}

if (-not (Test-Path $signedApk)) {
    Write-Error "Signed APK not found: $signedApk"
    exit 1
}

Write-Host "Build successful." -ForegroundColor Green
Write-Host "AAB: $signedAab" -ForegroundColor Cyan
Write-Host "APK: $signedApk" -ForegroundColor Cyan
