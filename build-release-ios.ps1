# Build Release IPA/Archive for Apple App Store Connect
# To run this script (PowerShell 7+): pwsh -File ./build-release-ios.ps1

Write-Host "Building Turf Time Release iOS archive..." -ForegroundColor Green

# Ensure we run from the repository root even if invoked elsewhere.
Set-Location $PSScriptRoot

# Clean previous builds
Write-Host "Cleaning previous builds..." -ForegroundColor Yellow
dotnet clean

# Build iOS archive + IPA
Write-Host "Publishing iOS Release (Archive + IPA)..." -ForegroundColor Yellow
dotnet publish -f net10.0-ios -c Release -r ios-arm64 -p:ArchiveOnBuild=true -p:BuildIpa=true

if ($LASTEXITCODE -eq 0) {
    Write-Host "Build successful!" -ForegroundColor Green
    Write-Host "Expected output folder:" -ForegroundColor Cyan
    Write-Host "bin/Release/net10.0-ios/ios-arm64/publish" -ForegroundColor Cyan

    # Open output folder (works on macOS and Windows)
    $outputDir = Join-Path $PSScriptRoot "bin/Release/net10.0-ios/ios-arm64/publish"
    if (Test-Path $outputDir) {
        Start-Process $outputDir
    }
} else {
    Write-Host "Build failed! Check errors above." -ForegroundColor Red
}

