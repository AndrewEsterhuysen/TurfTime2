# iOS Deployment Checklist for Turf Time

## ✅ Completed Changes

### 1. Project Configuration
- ✅ Added `net10.0-ios` target framework to TurfTime2.csproj
- ✅ Added iOS minimum version (15.0)
- ✅ Added iOS Release configuration with code signing settings
- ✅ Created iOS Entitlements.plist

### 2. Privacy & Permissions
- ✅ Added location permission descriptions to Info.plist:
  - `NSLocationWhenInUseUsageDescription`
  - `NSLocationAlwaysAndWhenInUseUsageDescription`
- ✅ Added App Transport Security settings for HTTP/HTTPS access

### 3. WebView Configuration
- ✅ Added iOS-specific WebView configuration in GamePage.xaml.cs
- ✅ iOS screen wake lock already implemented (UIApplication.IdleTimerDisabled)

### 4. UI Configuration
- ✅ Set UIViewControllerBasedStatusBarAppearance to false for better control

---

## 📋 Additional Steps Required Before Deployment

### 1. Apple Developer Account Setup
- [ ] Enroll in the Apple Developer Program ($99/year)
- [ ] Create an App ID on Apple Developer Portal:
  - Bundle ID: `com.andrewestherhuysen.turftime`
  - Enable Location Services capability
- [ ] Create provisioning profiles:
  - Development profile (for testing)
  - Distribution profile (for App Store)

### 2. Code Signing in Visual Studio
- [ ] Install Xcode on a Mac (required for iOS development)
- [ ] Pair Visual Studio with Mac (Tools → iOS → Pair to Mac)
- [ ] Import your Apple Developer certificates
- [ ] Select the appropriate provisioning profile in project properties

### 3. App Icons & Launch Screen
- [ ] Verify app icon renders correctly on iOS (Resources\AppIcon\)
- [ ] Test splash screen on iOS (Resources\Splash\)
- [ ] Consider iOS-specific icon guidelines (rounded corners applied automatically)

### 4. Testing Requirements
- [ ] Test on iOS Simulator (various screen sizes)
- [ ] Test on physical iOS device via development provisioning
- [ ] Verify location services work correctly:
  - First-time permission prompt
  - "Allow While Using App" flow
  - Fallback when location denied
- [ ] Test WebView content loads properly
- [ ] Verify screen stays on during game (IdleTimerDisabled)
- [ ] Test all tabs in TabBar navigation
- [ ] Test opening external Google Maps links

### 5. App Store Requirements
- [ ] Create App Store listing in App Store Connect
- [ ] Prepare app screenshots (required sizes):
  - 6.7" (iPhone 14 Pro Max)
  - 6.5" (iPhone 11 Pro Max)
  - 5.5" (iPhone 8 Plus)
  - 12.9" iPad Pro (if supporting iPad)
- [ ] Write app description and keywords
- [ ] Create privacy policy URL (already exists: docs/privacy.html)
- [ ] Set app category (likely Sports)
- [ ] Set age rating
- [ ] Add support URL and marketing URL

### 6. Build for Release
```bash
# Clean the solution first
dotnet clean

# Build iOS release
dotnet build -f net10.0-ios -c Release

# Or publish with specific runtime
dotnet publish -f net10.0-ios -c Release -r ios-arm64
```

### 7. Archive and Upload
- [ ] In Visual Studio: Build → Archive for Publishing
- [ ] Sign with Distribution certificate
- [ ] Upload to App Store Connect
- [ ] Submit for App Review

---

## 🔍 iOS-Specific Features to Consider

### Current Features That Work Differently on iOS:
1. **Location Services**: Requires user permission (implemented ✅)
2. **Screen Wake Lock**: Uses UIApplication.IdleTimerDisabled (implemented ✅)
3. **WebView**: Uses WKWebView with custom configuration (implemented ✅)
4. **External Links**: Opens in Safari via Launcher.OpenAsync (should work ✅)

### Potential Issues to Watch For:
1. **File Access**: WebView local file access configured
2. **HTTP Access**: App Transport Security allows arbitrary loads
3. **Safe Area**: Consider iPhone notch/Dynamic Island in UI layout
4. **Dark Mode**: Test with iOS dark mode vs your custom theme switcher
5. **Keyboard**: Test form entry on smaller iPhone screens
6. **Tab Bar**: Verify iOS tab bar styling looks good

---

## 🧪 Testing Scenarios

### Location Feature Tests:
1. First app launch → Permission prompt appears
2. Allow location → Get Location button works
3. Deny location → Proper error message shown
4. Location disabled in Settings → Proper error message
5. Airplane mode → Graceful degradation

### WebView Tests:
1. Game page loads HTML content
2. JavaScript executes (timer functionality)
3. Theme switching works
4. Local storage persists data
5. No console errors in Web Inspector

### Navigation Tests:
1. All 4 tabs navigate correctly
2. Tab state persists when switching
3. Back button behavior (if applicable)
4. Deep linking (if implemented)

---

## 📱 Device Compatibility

**Minimum iOS Version**: 15.0
**Supported Devices**:
- iPhone (Portrait + Landscape)
- iPad (All orientations)

**Test Devices Recommended**:
- iPhone SE (small screen)
- iPhone 14/15 (standard)
- iPhone 14 Pro Max (large + Dynamic Island)
- iPad Pro 12.9" (if supporting iPad)

---

## 🚀 Post-Deployment

### Monitor:
- [ ] Crash reports in App Store Connect
- [ ] User reviews and ratings
- [ ] Location permission acceptance rate
- [ ] Performance metrics

### Consider Adding:
- [ ] iOS widgets (MAUI 9+ supports widgets)
- [ ] Apple Watch companion app
- [ ] Siri Shortcuts integration
- [ ] iCloud sync for settings
- [ ] Apple Sign-In (if user accounts added)

---

## 📚 Resources

- [MAUI iOS Documentation](https://learn.microsoft.com/dotnet/maui/ios/)
- [Apple Developer Portal](https://developer.apple.com)
- [App Store Review Guidelines](https://developer.apple.com/app-store/review/guidelines/)
- [iOS Human Interface Guidelines](https://developer.apple.com/design/human-interface-guidelines/ios)

---

## ⚠️ Important Notes

1. **Mac Required**: You MUST have a Mac to build for iOS, even with Visual Studio on Windows
2. **Xcode Required**: Latest Xcode must be installed on the Mac
3. **Paid Developer Account**: $99/year Apple Developer Program membership required
4. **Code Signing**: Most complex part - take time to set up correctly
5. **Review Time**: App Store review typically takes 1-3 days

---

## 🆘 Common Issues & Solutions

### "No provisioning profile found"
- Solution: Create provisioning profile in Apple Developer Portal
- Make sure Bundle ID matches exactly: `com.andrewestherhuysen.turftime`

### "Could not connect to Mac"
- Solution: Ensure both machines on same network
- Check firewall settings
- Use Pair to Mac in Visual Studio

### "Location permission not working"
- Solution: Check Info.plist has NSLocationWhenInUseUsageDescription
- Verify entitlements include location capability

### "WebView not loading content"
- Solution: Check App Transport Security settings
- Verify WKWebView configuration
- Check for JavaScript console errors

---

## 🔥 Firebase iOS Launch Crash Diagnostics (added 2026)

The most common cause of "deploys to simulator, then immediately or soon crashes" on iOS is:

`+[FIRApp configure]` throwing an NSException because `GoogleService-Info.plist` is not present at the **root** of the final `.app` bundle (with `LogicalName="GoogleService-Info.plist"` and matching `BUNDLE_ID`).

### What we added to make this visible
- In `MauiProgram.cs` (iOS `FinishedLaunching`): runtime check using `NSBundle.MainBundle.PathForResource("GoogleService-Info", "plist")` + `File.Exists`.
- Prominent `[iOS Firebase] DIAGNOSTIC` blocks are written to Debug output **before** calling `CrossFirebase.Initialize`.
- Global `AppDomain.UnhandledException` + `TaskScheduler.UnobservedTaskException` handlers installed at the absolute start of `CreateMauiApp()` so later crashes (timers, cloud REST, token refresh, etc.) produce full managed stacks in the log instead of raw SIGABRT.
- `FirebaseInitializationService` (the legacy WebView/JS Firebase path) now early-returns on iOS with a clear log (it is obsolete; Android never uses it for DB — we use native + REST).
- Extra try/catch + full stack logging in `FcmService`, `GameTimerService.TickLoopAsync`/`OnTick`, `CloudRosterService`, `App.InitializeFcmAsync`.

### How to verify after a build/deploy that crashes
1. In Console.app on the Mac: filter by process "TurfTime2" or the simulator device name while the app is launching or running.
2. Look for these exact strings:
   - `[iOS Firebase] DIAGNOSTIC — GoogleService-Info.plist check`
   - `PathForResource... = ...` and `File.Exists on that path: False`
   - `[CRASH] AppDomain.UnhandledException` or `UnobservedTaskException`
   - `[FCM]`, `[CloudRosterService]`, `[GameTimer]`, `[App]`
3. From a terminal (on this Mac):
   ```
   # Find the most recent simulator app bundle for this app and check the plist
   find ~/Library/Developer/CoreSimulator -path '*TurfTime2.app/GoogleService-Info.plist' -ls 2>/dev/null | tail -5
   ```
4. After a crash, the exact bundle that crashed is usually still on disk (see the .ips or the translated crash report for the long path under `data/Containers/Bundle/Application/.../TurfTime2.app`).

### If the plist is missing at runtime
- Clean everything: delete `bin/`, `obj/`, and the Pair-to-Mac cache under `~/Library/Caches/maui/PairToMac/Builds/TurfTime2`.
- Delete the app from the simulator (long-press icon → remove, or `xcrun simctl uninstall <device> com.andrewestherhuysen.turftime`).
- Rebuild from the Windows Rider side (forces fresh transfer of resources).
- The .csproj now has redundant but resilient `<BundleResource>` entries + a `VerifyFirebasePlist` target that prints during build.

### Android remains the reference
All the "Firebase database" work (roster, sessions, tokens, team create/join) that works on Android uses the **REST** path to Firestore (after anonymous identitytoolkit sign-up). The same code runs on iOS. Native `Plugin.Firebase` is only used for Auth bootstrap + FCM push tokens. Do not introduce iOS-only native Firestore code unless the REST path is proven broken.

---

**Last Updated**: Firebase iOS crash hardening pass
**Project**: Turf Time (com.andrewestherhuysen.turftime)
**Current Version**: 1.0.3 (Build 11)
