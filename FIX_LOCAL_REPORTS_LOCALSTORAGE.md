# Fix: Local Team Reports Not Loading from localStorage

## Problem
When viewing reports for a local team, the app was attempting to authenticate with Firestore instead of reading from localStorage. This was happening because:

1. WebViews in different MAUI pages don't automatically share localStorage
2. Each WebView has its own storage context based on the URL/origin it loads
3. ReportsPage WebView was initially empty, creating a different storage context than GamePage

## Root Cause
**WebView localStorage is origin-based**. When you load different content or no content at all, the WebView creates a separate localStorage instance. This meant:

- GamePage WebView: Loaded `wwwroot/index.html` → Had access to session data
- ReportsPage WebView: Loaded inline HTML → Had NO access to session data

## Solution
Load the **same origin** (`wwwroot/index.html`) in ReportsPage WebView before executing JavaScript to read localStorage.

### Code Changes:

**Before:**
```csharp
// Load inline HTML - creates different storage context
var minimalHtml = @"<!DOCTYPE html><html>...";
ReportWebView.Source = new HtmlWebViewSource { Html = minimalHtml };
```

**After:**
```csharp
// Load from same origin as GamePage - shares storage context
#if WINDOWS
    var indexPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot", "index.html");
    ReportWebView.Source = new UrlWebViewSource { Url = indexPath };
#else
    ReportWebView.Source = new UrlWebViewSource { Url = "file:///android_asset/wwwroot/index.html" };
#endif
```

## How It Works Now

### Local Teams:
1. ReportsPage loads `wwwroot/index.html` (same as GamePage)
2. Both WebViews now share the same localStorage context
3. JavaScript can read `roster.sessionHistory.v1` from localStorage
4. No Firestore authentication needed
5. Reports load instantly from local storage

### Cloud Teams:
1. ReportsPage checks team_mode = "shared"
2. Calls `LoadCloudSessionsAsync()`
3. Authenticates with Firebase
4. Loads sessions from Firestore
5. Works as before

## Testing

### To Verify Local Teams Work:
1. Stop and restart debugging (Hot Reload may not apply)
2. Create/open a local team (team ID starts with `local_`)
3. Play a game and long-press Start to end
4. Go to Settings → Reports
5. Check Visual Studio Debug Output:
   ```
   [ReportsPage] Team ID: local_abc123, Mode: local
   [ReportsPage] Loading local sessions for team: local_abc123
   [ReportsPage] Loading wwwroot/index.html to access localStorage
   [ReportsPage] WebView ready, executing localStorage script
   [ReportsPage] Found X local sessions
   [ReportsPage] ✅ Local report loaded successfully
   ```
6. Should NOT see any Firestore authentication logs

### To Verify Cloud Teams Still Work:
1. Create/join a cloud team
2. Play a game and long-press Start to end
3. Go to Settings → Reports
4. Check Visual Studio Debug Output:
   ```
   [ReportsPage] Team ID: team16-qt4y3z, Mode: shared
   [ReportsPage] Loading sessions from Firestore for cloud team
   [SessionLoadHelper] Loading sessions for team: team16-qt4y3z
   ```
5. Should see Firestore authentication logs (this is expected for cloud teams)

## Additional Logging Added

Enhanced logging throughout `LoadLocalSessionsAsync()`:
- ✅ Team ID and mode detection
- ✅ WebView loading confirmation
- ✅ localStorage read attempt
- ✅ JSON parsing stages
- ✅ Success/failure indicators
- ✅ Detailed error messages with stack traces

## Key Takeaway

**WebView localStorage is origin-based!**
- Same origin = Shared storage
- Different origin = Separate storage
- Inline HTML = Different origin than file URLs

Always load from the same origin when you need to share data between WebViews.

## Files Modified
- `TurfTime2/ReportsPage.xaml.cs` - Updated `LoadLocalSessionsAsync()` method
