# Quick Firebase Deploy Instructions

Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host " Firebase Functions - Service Account Deploy" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan

Write-Host "The Firebase CLI auth service is down. Let's use a Service Account instead.`n" -ForegroundColor Yellow

Write-Host "STEP 1: Create Service Account" -ForegroundColor Green
Write-Host "-------------------------------"
Write-Host "Opening Google Cloud Console...`n"
Start-Process "https://console.cloud.google.com/iam-admin/serviceaccounts/create?project=turf-timer"
Start-Sleep -Seconds 2

Write-Host "In the browser, fill in:" -ForegroundColor White
Write-Host "  • Service account name: firebase-deployer"
Write-Host "  • Click 'CREATE AND CONTINUE'"
Write-Host "  • Add roles: Cloud Functions Admin, Firebase Admin, Service Account User"
Write-Host "  • Click 'CONTINUE' then 'DONE'`n"

Read-Host "Press Enter when service account is created"

Write-Host "`nSTEP 2: Download Key File" -ForegroundColor Green
Write-Host "-------------------------"
Write-Host "Opening service accounts list...`n"
Start-Process "https://console.cloud.google.com/iam-admin/serviceaccounts?project=turf-timer"
Start-Sleep -Seconds 2

Write-Host "In the browser:" -ForegroundColor White
Write-Host "  • Find 'firebase-deployer@turf-timer.iam.gserviceaccount.com'"
Write-Host "  • Click the three dots (...) → 'Manage keys'"
Write-Host "  • Click 'ADD KEY' → 'Create new key'"
Write-Host "  • Select 'JSON' and click 'CREATE'"
Write-Host "  • File will download to your Downloads folder`n"

Read-Host "Press Enter when key file is downloaded"

Write-Host "`nSTEP 3: Select Key File" -ForegroundColor Green
Write-Host "-----------------------`n"

$downloadsPath = "$env:USERPROFILE\Downloads"
$recentJsonFiles = Get-ChildItem -Path $downloadsPath -Filter "*.json" | 
    Where-Object { $_.LastWriteTime -gt (Get-Date).AddMinutes(-10) } | 
    Sort-Object LastWriteTime -Descending | Select-Object -First 5

if ($recentJsonFiles.Count -gt 0) {
    Write-Host "Recent JSON files in Downloads:" -ForegroundColor Cyan
    for ($i = 0; $i -lt $recentJsonFiles.Count; $i++) {
        $time = $recentJsonFiles[$i].LastWriteTime.ToString('HH:mm:ss')
        Write-Host "  [$i] $($recentJsonFiles[$i].Name) ($time)"
    }
    Write-Host ""

    $selection = Read-Host "Enter number of key file (or press Enter to type path)"

    if ($selection -match '^\d+$' -and [int]$selection -lt $recentJsonFiles.Count) {
        $keyPath = $recentJsonFiles[[int]$selection].FullName
        Write-Host "Selected: $keyPath`n" -ForegroundColor Green
    }
}

if (-not $keyPath) {
    $keyPath = Read-Host "Enter full path to JSON key file"
}

if (-not (Test-Path $keyPath)) {
    Write-Host "`nError: File not found at: $keyPath" -ForegroundColor Red
    Write-Host "Please run this script again with the correct path.`n"
    exit 1
}

Write-Host "`nSTEP 4: Deploy!" -ForegroundColor Green
Write-Host "---------------`n"

$env:GOOGLE_APPLICATION_CREDENTIALS = $keyPath
Write-Host "Setting credentials..." -ForegroundColor Cyan

Set-Location "C:\Users\esterha\source\repos\TurfTimer-Rebuild\TurfTime2"
Write-Host "Deploying functions...`n" -ForegroundColor Cyan

& "C:\Users\esterha\AppData\Roaming\npm\firebase.cmd" deploy --only functions

if ($LASTEXITCODE -eq 0) {
    Write-Host "`n========================================" -ForegroundColor Green
    Write-Host " SUCCESS!" -ForegroundColor Green
    Write-Host "========================================`n" -ForegroundColor Green

    Write-Host "Deployed functions:" -ForegroundColor Cyan
    & "C:\Users\esterha\AppData\Roaming\npm\firebase.cmd" functions:list

    Write-Host "`nNext: Test by sending a chat message in your app!" -ForegroundColor Yellow
} else {
    Write-Host "`nDeployment failed. Check errors above.`n" -ForegroundColor Red
}
