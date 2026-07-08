# Deploy using gcloud CLI (bypasses Firebase CLI auth issues)

$ServiceAccountKey = "C:\Users\esterha\Downloads\turf-timer-b1a2dbcba9cf.json"
$ProjectId = "turf-timer"
$FunctionName = "sendChatNotification"
$Runtime = "nodejs20"
$Region = "us-central1"

Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host " Deploy Firebase Function via gcloud CLI" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan

# Check if gcloud is installed
Write-Host "Checking for gcloud CLI..." -ForegroundColor Yellow
$gcloudPath = Get-Command gcloud -ErrorAction SilentlyContinue

if (-not $gcloudPath) {
    Write-Host "`ngcloud CLI is not installed." -ForegroundColor Red
    Write-Host "`nInstalling gcloud CLI...`n" -ForegroundColor Yellow
    Write-Host "Opening installer download page..." -ForegroundColor Cyan
    Start-Process "https://cloud.google.com/sdk/docs/install"

    Write-Host "`nPlease:" -ForegroundColor White
    Write-Host "  1. Download and install Google Cloud SDK"
    Write-Host "  2. Run this script again after installation`n"

    Read-Host "Press Enter to exit"
    exit 1
}

Write-Host "✅ gcloud CLI found`n" -ForegroundColor Green

# Activate service account
Write-Host "Activating service account..." -ForegroundColor Yellow
try {
    gcloud auth activate-service-account --key-file=$ServiceAccountKey 2>&1 | Out-Host
    Write-Host "✅ Service account activated`n" -ForegroundColor Green
} catch {
    Write-Host "❌ Failed to activate service account: $_`n" -ForegroundColor Red
    exit 1
}

# Set project
Write-Host "Setting project to $ProjectId..." -ForegroundColor Yellow
gcloud config set project $ProjectId

# Deploy function
Write-Host "`nDeploying function $FunctionName..." -ForegroundColor Yellow
Write-Host "This may take a few minutes...`n" -ForegroundColor Cyan

$FunctionsDir = "C:\Users\esterha\source\repos\TurfTimer-Rebuild\TurfTime2\functions"

try {
    gcloud functions deploy $FunctionName `
        --gen2 `
        --runtime=$Runtime `
        --region=$Region `
        --source=$FunctionsDir `
        --entry-point=$FunctionName `
        --trigger-event-filters="type=google.cloud.firestore.document.v1.created" `
        --trigger-event-filters="database=(default)" `
        --trigger-event-filters-path-pattern="document=teams/{teamId}/chat/{messageId}" `
        2>&1 | Out-Host

    Write-Host "`n========================================" -ForegroundColor Green
    Write-Host " ✅ Deployment Successful!" -ForegroundColor Green
    Write-Host "========================================`n" -ForegroundColor Green

    Write-Host "Function deployed to: https://console.firebase.google.com/project/$ProjectId/functions`n" -ForegroundColor Cyan
    Write-Host "Next: Test by sending a chat message in your app!`n" -ForegroundColor Yellow

} catch {
    Write-Host "`n❌ Deployment failed: $_`n" -ForegroundColor Red
    exit 1
}
