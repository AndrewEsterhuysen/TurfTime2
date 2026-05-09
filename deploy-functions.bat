@echo off
echo ==========================================
echo  TurfTimer - Firebase Functions Deploy
echo ==========================================
echo.

:: Set Service Account credentials
set GOOGLE_APPLICATION_CREDENTIALS=C:\Users\esterha\firebase-service-account.json

:: Check if service account key exists
if not exist "%GOOGLE_APPLICATION_CREDENTIALS%" (
    echo ERROR: Service account key not found at:
    echo   %GOOGLE_APPLICATION_CREDENTIALS%
    echo.
    echo Please download it from:
    echo   https://console.firebase.google.com/project/turf-timer/settings/serviceaccounts/adminsdk
    echo.
    echo Then save it as:
    echo   C:\Users\esterha\firebase-service-account.json
    echo.
    pause
    exit /b 1
)

:: Refresh PATH to include npm global bin
set PATH=%PATH%;C:\Users\esterha\AppData\Roaming\npm

:: Check firebase CLI is available
where firebase >nul 2>&1
if %ERRORLEVEL% neq 0 (
    echo ERROR: firebase CLI not found.
    echo Run:  npm install -g firebase-tools
    echo.
    pause
    exit /b 1
)

echo Using credentials: %GOOGLE_APPLICATION_CREDENTIALS%
echo.

:: Change to functions directory
cd /d C:\Users\esterha\source\repos\TurfTimer-Rebuild\TurfTime2\functions

echo Deploying Firebase Cloud Functions...
echo.
firebase deploy --only functions

echo.
if %ERRORLEVEL% equ 0 (
    echo ==========================================
    echo  Deploy SUCCESS
    echo ==========================================
) else (
    echo ==========================================
    echo  Deploy FAILED - check output above
    echo ==========================================
)

echo.
pause
