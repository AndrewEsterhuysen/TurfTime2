# Build Release AAB for Google Play Store
Write-Host "🏗️ Building Turf Time Release AAB..." -ForegroundColor Green

# Clean previous builds
Write-Host "🧹 Cleaning previous builds..." -ForegroundColor Yellow
dotnet clean -c Release

# Build AAB
Write-Host "📦 Building Android App Bundle..." -ForegroundColor Yellow
dotnet publish -f net10.0-android -c Release /p:AndroidPackageFormat=aab

if ($LASTEXITCODE -eq 0) {
    Write-Host "✅ Build successful!" -ForegroundColor Green
    Write-Host "📁 Output: bin\Release\net10.0-android\publish\" -ForegroundColor Cyan
    
    # Open output folder
    Start-Process "bin\Release\net10.0-android\publish"
} else {
    Write-Host "❌ Build failed! Check errors above." -ForegroundColor Red
}