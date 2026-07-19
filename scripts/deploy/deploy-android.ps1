# Android Multi-Device Deployment Script
# Deploys to phone, emulator, both, or an explicit adb serial.
#
# Usage:
#   ./scripts/deploy/deploy-android.ps1
#   ./scripts/deploy/deploy-android.ps1 -Target phone
#   ./scripts/deploy/deploy-android.ps1 -Target ZY227KSJL3
#
# Always uninstalls first so Debug can replace a store/release-signed install.

param(
    [Parameter(Mandatory = $false)]
    [string]$Target = "choose"
)

$ErrorActionPreference = "Stop"
$PackageId = "com.andrewestherhuysen.turftime"
$Tfm = "net10.0-android"
$Config = "Debug"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = (Resolve-Path (Join-Path $ScriptDir "../..")).Path
$ProjectFile = Join-Path $RepoRoot "TurfTime2.csproj"

Set-Location $RepoRoot

if (-not (Test-Path $ProjectFile)) {
    Write-Host "ERROR: Project not found: $ProjectFile" -ForegroundColor Red
    exit 1
}

Write-Host "=== TurfTime Android Deployment ===" -ForegroundColor Cyan
Write-Host "Repo: $RepoRoot"
Write-Host ""

Write-Host "Detecting connected devices..." -ForegroundColor Yellow
$devices = @(adb devices | Select-String "device$" | ForEach-Object { ($_ -split "\s+")[0] })

if ($devices.Count -eq 0) {
    Write-Host "ERROR: No devices connected!" -ForegroundColor Red
    Write-Host "Please connect a device or start an emulator." -ForegroundColor Yellow
    exit 1
}

Write-Host "Found $($devices.Count) device(s):" -ForegroundColor Green
$emulators = @()
$phones = @()

foreach ($device in $devices) {
    if ($device -match "^emulator-") {
        $emulators += $device
        Write-Host "  [E] $device (Emulator)" -ForegroundColor Cyan
    }
    else {
        $phones += $device
        Write-Host "  [P] $device (Physical Device)" -ForegroundColor Green
    }
}
Write-Host ""

$targetDevices = @()

if ($Target -eq "choose") {
    Write-Host "Select deployment target:" -ForegroundColor Yellow
    Write-Host "  1) Phone only"
    Write-Host "  2) Emulator only"
    Write-Host "  3) Both"
    Write-Host ""
    $choice = Read-Host "Enter choice (1-3)"

    switch ($choice) {
        "1" { $Target = "phone" }
        "2" { $Target = "emulator" }
        "3" { $Target = "both" }
        default {
            Write-Host "Invalid choice. Defaulting to phone." -ForegroundColor Yellow
            $Target = "phone"
        }
    }
}

switch -Regex ($Target) {
    "^phone$" {
        if ($phones.Count -eq 0) {
            Write-Host "ERROR: No physical devices connected!" -ForegroundColor Red
            exit 1
        }
        $targetDevices = $phones
    }
    "^emulator$" {
        if ($emulators.Count -eq 0) {
            Write-Host "ERROR: No emulators running!" -ForegroundColor Red
            exit 1
        }
        $targetDevices = $emulators
    }
    "^both$" {
        $targetDevices = $devices
    }
    default {
        if ($devices -contains $Target) {
            $targetDevices = @($Target)
        }
        else {
            Write-Host "ERROR: Unknown target '$Target' (use phone|emulator|both|SERIAL)." -ForegroundColor Red
            exit 1
        }
    }
}

Write-Host ""
Write-Host "Building APK..." -ForegroundColor Yellow
dotnet build $ProjectFile -f $Tfm -c $Config
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}

Write-Host "Build successful!" -ForegroundColor Green
Write-Host ""

# Prefer the signed fat APK at the TFM root (not nested android-arm64 copies).
$apkDir = Join-Path $RepoRoot "bin\$Config\$Tfm"
$signedRoot = Join-Path $apkDir "$PackageId-Signed.apk"
$unsignedRoot = Join-Path $apkDir "$PackageId.apk"

if (Test-Path $signedRoot) {
    $apkPath = Get-Item $signedRoot
}
elseif (Test-Path $unsignedRoot) {
    $apkPath = Get-Item $unsignedRoot
}
else {
    $apkPath = Get-ChildItem -Path $apkDir -Filter "*-Signed.apk" -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
}

if (-not $apkPath) {
    Write-Host "ERROR: APK not found under $apkDir" -ForegroundColor Red
    exit 1
}

Write-Host "APK: $($apkPath.FullName)" -ForegroundColor Cyan
Write-Host ""

foreach ($device in $targetDevices) {
    $deviceType = if ($device -match "^emulator-") { "Emulator" } else { "Phone" }
    Write-Host "Deploying to $deviceType ($device)..." -ForegroundColor Yellow

    # Uninstall so Debug can replace release/store signature.
    adb -s $device uninstall $PackageId 2>$null | Out-Null

    adb -s $device install -r $apkPath.FullName
    if ($LASTEXITCODE -eq 0) {
        Write-Host "  ✓ Deployed successfully to $device" -ForegroundColor Green

        $activity = (adb -s $device shell cmd package resolve-activity --brief $PackageId 2>$null |
            Select-Object -Last 1).ToString().Trim()
        if ($activity -and $activity.Contains("/")) {
            Write-Host "  Launching $activity ..." -ForegroundColor Cyan
            adb -s $device shell am start -n $activity | Out-Null
        }
        else {
            Write-Host "  Launching via monkey ..." -ForegroundColor Cyan
            adb -s $device shell monkey -p $PackageId -c android.intent.category.LAUNCHER 1 2>$null | Out-Null
        }
    }
    else {
        Write-Host "  ✗ Deployment failed to $device" -ForegroundColor Red
        exit 1
    }
    Write-Host ""
}

Write-Host "=== Deployment Complete ===" -ForegroundColor Cyan
