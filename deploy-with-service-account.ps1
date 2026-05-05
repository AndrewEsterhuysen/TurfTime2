# Firebase Functions Deployment with Service Account
# This script guides you through creating and using a service account for deployment

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " Firebase Functions Deployment Helper" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

Write-Host "🔐 STEP 1: Create Service Account" -ForegroundColor Yellow
Write-Host "-----------------------------------" -ForegroundColor Yellow
Write-Host ""
Write-Host "Your browser should open the Google Cloud Console." -ForegroundColor White
Write-Host "If it didn't, go to:" -ForegroundColor White
Write-Host "https://console.cloud.google.com/iam-admin/serviceaccounts/create?project=turf-timer" -ForegroundColor Cyan
Write-Host ""
Write-Host "Fill in the form:" -ForegroundColor White
Write-Host "  1. Service account name: firebase-deployer" -ForegroundColor Green
Write-Host "  2. Service account ID: (auto-filled)" -ForegroundColor Green
Write-Host "  3. Description: Deploy Firebase Functions" -ForegroundColor Green
Write-Host "  4. Click 'CREATE AND CONTINUE'" -ForegroundColor Green
Write-Host ""
Write-Host "  5. Grant these roles:" -ForegroundColor Green
Write-Host "     - Cloud Functions Admin" -ForegroundColor Green
Write-Host "     - Firebase Admin" -ForegroundColor Green
Write-Host "     - Service Account User" -ForegroundColor Green
Write-Host "  6. Click 'CONTINUE'" -ForegroundColor Green
Write-Host "  7. Click 'DONE'" -ForegroundColor Green
Write-Host ""

$createComplete = Read-Host "Have you created the service account? (y/n)"
if ($createComplete -ne "y") {
    Write-Host "❌ Please create the service account first, then run this script again." -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "🔑 STEP 2: Download Service Account Key" -ForegroundColor Yellow
Write-Host "---------------------------------------" -ForegroundColor Yellow
Write-Host ""
Write-Host "Now let's get the JSON key file..." -ForegroundColor White
Write-Host ""
Write-Host "Opening service accounts list..." -ForegroundColor White
Start-Process "https://console.cloud.google.com/iam-admin/serviceaccounts?project=turf-timer"
Write-Host ""
Write-Host "In the browser:" -ForegroundColor White
Write-Host "  1. Find 'firebase-deployer@turf-timer.iam.gserviceaccount.com'" -ForegroundColor Green
Write-Host "  2. Click the three dots (⋮) on the right" -ForegroundColor Green
Write-Host "  3. Click 'Manage keys'" -ForegroundColor Green
Write-Host "  4. Click 'ADD KEY' → 'Create new key'" -ForegroundColor Green
Write-Host "  5. Select 'JSON'" -ForegroundColor Green
Write-Host "  6. Click 'CREATE'" -ForegroundColor Green
Write-Host "  7. Save the file to your Downloads folder" -ForegroundColor Green
Write-Host ""

$keyDownloaded = Read-Host "Have you downloaded the JSON key file? (y/n)"
if ($keyDownloaded -ne "y") {
    Write-Host "❌ Please download the key file first, then run this script again." -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "📂 STEP 3: Locate the Key File" -ForegroundColor Yellow
Write-Host "-------------------------------" -ForegroundColor Yellow
Write-Host ""

# Try to find the key automatically
$downloadsPath = "$env:USERPROFILE\Downloads"
$recentJsonFiles = Get-ChildItem -Path $downloadsPath -Filter "*.json" | 
    Where-Object { $_.LastWriteTime -gt (Get-Date).AddHours(-1) } | 
    Sort-Object LastWriteTime -Descending

if ($recentJsonFiles.Count -gt 0) {
    Write-Host "Found recent JSON files in Downloads:" -ForegroundColor White
    for ($i = 0; $i -lt $recentJsonFiles.Count; $i++) {
        Write-Host "  [$i] $($recentJsonFiles[$i].Name) ($(($recentJsonFiles[$i].LastWriteTime).ToString('HH:mm:ss')))" -ForegroundColor Cyan
    }
    Write-Host ""
    $selection = Read-Host "Select the key file number (or press Enter to specify path manually)"

    if ($selection -match '^\d+$' -and [int]$selection -lt $recentJsonFiles.Count) {
        $keyPath = $recentJsonFiles[[int]$selection].FullName
    }
}

if (-not $keyPath) {
    $keyPath = Read-Host "Enter the full path to the JSON key file"
}

if (-not (Test-Path $keyPath)) {
    Write-Host "❌ Error: File not found at: $keyPath" -ForegroundColor Red
    exit 1
}

Write-Host "✅ Key file found: $keyPath" -ForegroundColor Green
Write-Host ""

Write-Host "🚀 STEP 4: Deploy Functions" -ForegroundColor Yellow
Write-Host "---------------------------" -ForegroundColor Yellow
Write-Host ""

# Set the environment variable
$env:GOOGLE_APPLICATION_CREDENTIALS = $keyPath
Write-Host "✅ Set GOOGLE_APPLICATION_CREDENTIALS" -ForegroundColor Green

# Navigate to project
$projectPath = "C:\Users\esterha\source\repos\TurfTimer-Rebuild\TurfTime2"
Set-Location $projectPath
Write-Host "✅ Changed to project directory" -ForegroundColor Green
Write-Host ""

# Deploy
Write-Host "🔄 Deploying functions to Firebase..." -ForegroundColor Cyan
Write-Host ""

$firebasePath = "C:\Users\esterha\AppData\Roaming\npm\firebase.cmd"
& $firebasePath deploy --only functions

if ($LASTEXITCODE -eq 0) {
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Green
    Write-Host " ✅ Deployment Successful!" -ForegroundColor Green
    Write-Host "========================================" -ForegroundColor Green
    Write-Host ""
    Write-Host "📋 Listing deployed functions..." -ForegroundColor Cyan
    & $firebasePath functions:list
    Write-Host ""
    Write-Host "🎉 Your Cloud Function is now live!" -ForegroundColor Green
    Write-Host "Next: Test it by sending a chat message in your app!" -ForegroundColor Yellow
} else {
    Write-Host ""
    Write-Host "❌ Deployment failed. Check the error messages above." -ForegroundColor Red
}

