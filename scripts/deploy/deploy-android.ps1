# Android Multi-Device Deployment Script
# Deploys to phone, emulator, or both

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("phone", "emulator", "both", "choose")]
    [string]$Target = "choose"
)

Write-Host "=== TurfTime Android Deployment ===" -ForegroundColor Cyan
Write-Host ""

# Get list of connected devices
Write-Host "Detecting connected devices..." -ForegroundColor Yellow
$devices = adb devices | Select-String "device$" | ForEach-Object { ($_ -split "\s+")[0] }

if ($devices.Count -eq 0) {
    Write-Host "ERROR: No devices connected!" -ForegroundColor Red
    Write-Host "Please connect a device or start an emulator." -ForegroundColor Yellow
    exit 1
}

Write-Host "Found $($devices.Count) device(s):" -ForegroundColor Green
$emulators = @()
$phones = @()

foreach ($device in $devices) {
    if ($device -match "emulator-") {
        $emulators += $device
        Write-Host "  [E] $device (Emulator)" -ForegroundColor Cyan
    } else {
        $phones += $device
        Write-Host "  [P] $device (Physical Device)" -ForegroundColor Green
    }
}
Write-Host ""

# Determine target devices
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
            Write-Host "Invalid choice. Defaulting to both." -ForegroundColor Yellow
            $Target = "both"
        }
    }
}

switch ($Target) {
    "phone" {
        if ($phones.Count -eq 0) {
            Write-Host "ERROR: No physical devices connected!" -ForegroundColor Red
            exit 1
        }
        $targetDevices = $phones
    }
    "emulator" {
        if ($emulators.Count -eq 0) {
            Write-Host "ERROR: No emulators running!" -ForegroundColor Red
            exit 1
        }
        $targetDevices = $emulators
    }
    "both" {
        $targetDevices = $devices
    }
}

Write-Host ""
Write-Host "Building APK..." -ForegroundColor Yellow
dotnet build -f net10.0-android -c Debug

if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}

Write-Host "Build successful!" -ForegroundColor Green
Write-Host ""

# Find the APK
$apkPath = Get-ChildItem -Path "bin\Debug\net10.0-android" -Filter "*.apk" -Recurse | Select-Object -First 1

if (-not $apkPath) {
    Write-Host "ERROR: APK not found!" -ForegroundColor Red
    exit 1
}

Write-Host "APK: $($apkPath.FullName)" -ForegroundColor Cyan
Write-Host ""

# Deploy to each target device
foreach ($device in $targetDevices) {
    $deviceType = if ($device -match "emulator-") { "Emulator" } else { "Phone" }
    Write-Host "Deploying to $deviceType ($device)..." -ForegroundColor Yellow

    # Uninstall old version (suppress errors if not installed)
    adb -s $device uninstall com.andrewestherhuysen.turftime 2>$null

    # Install new version
    adb -s $device install -r $apkPath.FullName

    if ($LASTEXITCODE -eq 0) {
        Write-Host "  ✓ Deployed successfully to $device" -ForegroundColor Green

        # Optional: Launch app
        Write-Host "  Launching app..." -ForegroundColor Cyan
        adb -s $device shell am start -n com.andrewestherhuysen.turftime/crc6414c25a36c7c51ce0.MainActivity
    } else {
        Write-Host "  ✗ Deployment failed to $device" -ForegroundColor Red
    }
    Write-Host ""
}

Write-Host "=== Deployment Complete ===" -ForegroundColor Cyan
