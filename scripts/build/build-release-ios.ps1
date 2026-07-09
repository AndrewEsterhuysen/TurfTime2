# Build signed iOS Release IPA for App Store Connect
# Usage: pwsh -File ./scripts/build/build-release-ios.ps1

Write-Host "Building Turf Time signed iOS release..." -ForegroundColor Green

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptDir "../..")
$projectFile = Join-Path $repoRoot "TurfTime2.csproj"

Write-Host "Repo: $repoRoot" -ForegroundColor Cyan
Write-Host "Project: $projectFile" -ForegroundColor Cyan

dotnet clean $projectFile -f net10.0-ios -r ios-arm64 -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet publish $projectFile -f net10.0-ios -c Release -r ios-arm64 -p:ArchiveOnBuild=true -p:BuildIpa=true -p:EnableCodeSigning=true
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$outputDir = Join-Path $repoRoot "bin/Release/net10.0-ios/ios-arm64/publish"
$ipaFile = Join-Path $outputDir "TurfTime2.ipa"

if (-not (Test-Path $ipaFile)) {
    Write-Error "Signed IPA not found: $ipaFile"
    exit 1
}

Write-Host "Build successful." -ForegroundColor Green
Write-Host "IPA: $ipaFile" -ForegroundColor Cyan
