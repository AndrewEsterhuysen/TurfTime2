# Building Turf Time for Google Play Store

## Prerequisites
1. ✅ Visual Studio 2022 with .NET MAUI workload
2. ✅ Android SDK installed
3. ✅ Keystore generated (see KEYSTORE_GUIDE.md)
4. ✅ Passwords stored securely

## Build Steps

### Setup Passwords (One-Time)

**You have two options for storing keystore passwords:**

#### Option A: Directory.Build.props (Recommended - Easiest for repeated builds)

1. **Edit `Directory.Build.props`** (already created in project root):
   ```xml
   <AndroidSigningStorePassword>YOUR_ACTUAL_PASSWORD</AndroidSigningStorePassword>
   <AndroidSigningKeyPassword>YOUR_ACTUAL_PASSWORD</AndroidSigningKeyPassword>
   ```

2. **Replace placeholders** with your actual keystore passwords
3. **This file is in .gitignore** - it will NEVER be committed to Git ✅
4. **Backup this file securely** along with your keystore

#### Option B: Environment Variables (Use each time you build)

Set before each build session:
```powershell
$env:AndroidSigningStorePassword = "your_keystore_password"
$env:AndroidSigningKeyPassword = "your_key_password"
```

---

### Option 1: Visual Studio GUI

1. **Set Build Configuration**
   - Select `Release` configuration
   - Select `net10.0-android` framework

2. **Configure Signing** (if using Visual Studio signing - not needed if using Directory.Build.props)
   - Right-click project → Properties
   - Go to Android → Package Signing
   - Check "Sign the .APK file using the following keystore details"
   - Browse to `turftime.keystore`
   - Enter alias: `turftime`
   - Enter passwords

3. **Build**
   - Right-click project → `Publish`
   - OR: Build → Archive...
   - Select Android → Ad Hoc
   - Click `Distribute`

### Option 2: Command Line (Recommended)

```powershell
# If using Directory.Build.props, passwords are already set - just build!
dotnet clean -c Release
dotnet publish -f net10.0-android -c Release /p:AndroidPackageFormat=aab

# If using environment variables, set them first:
$env:AndroidSigningStorePassword = "your_password"
$env:AndroidSigningKeyPassword = "your_password"
dotnet clean -c Release
dotnet publish -f net10.0-android -c Release /p:AndroidPackageFormat=aab
```

### Output Locations

**AAB (Android App Bundle)** - For Google Play Store:
```
bin\Release\net10.0-android\publish\com.andrewestherhuysen.turftime-Signed.aab
```

**APK** - For direct distribution/testing:
```
bin\Release\net10.0-android\publish\com.andrewestherhuysen.turftime-Signed.apk
```

## Verify the Build

### Check Signing
```powershell
# For AAB
jarsigner -verify -verbose -certs bin\Release\net10.0-android\publish\com.andrewestherhuysen.turftime-Signed.aab

# For APK
jarsigner -verify -verbose -certs bin\Release\net10.0-android\publish\com.andrewestherhuysen.turftime-Signed.apk
```

Should show: "jar verified" and your certificate details.

### Check Contents
```powershell
# List AAB contents
jar -tf bin\Release\net10.0-android\publish\com.andrewestherhuysen.turftime-Signed.aab
```

## Test Before Upload

### Install on Device
```powershell
# Install APK
adb install bin\Release\net10.0-android\publish\com.andrewestherhuysen.turftime-Signed.apk

# Test all features:
# - Match timer
# - Rotation
# - Screen wake lock
# - Vibration alerts
# - Data persistence
```

### Test on Multiple Devices
- Different Android versions (API 21+)
- Different screen sizes
- Different manufacturers

## Upload to Google Play Console

1. **Go to**: https://play.google.com/console
2. **Create App** (first time only)
   - App name: "Turf Time"
   - Default language: English (US)
   - App or game: App
   - Free or paid: Free

3. **Upload AAB**
   - Go to: Production → Create new release
   - Upload: `com.andrewestherhuysen.turftime-Signed.aab`
   - Release name: "1.0.0 (1)"
   - Release notes: "Initial release - Soccer team rotation manager"

4. **Complete Store Listing**
   - App details (from StoreAssets/PlayStore/README.md)
   - Graphics (screenshots, icon, feature graphic)
   - Categorization: Sports
   - Contact details & Privacy Policy
   - Content rating questionnaire
   - Target audience: Everyone (or appropriate rating)

5. **Submit for Review**
   - Review all sections
   - Submit for review (can take 1-3 days)

## Version Updates

For future updates:

1. **Update Version Numbers** in `TurfTime2.csproj`:
   ```xml
   <ApplicationDisplayVersion>1.0.1</ApplicationDisplayVersion>
   <ApplicationVersion>2</ApplicationVersion>
   ```
   - `ApplicationDisplayVersion`: What users see (1.0.1)
   - `ApplicationVersion`: Version code (must increase: 1, 2, 3...)

2. **Rebuild and Upload**
   - Build new AAB with same keystore
   - Upload to Play Console
   - Add release notes

## Troubleshooting

### Build Fails
- Check Android SDK is updated
- Clean solution: `dotnet clean`
- Delete `bin` and `obj` folders
- Restart Visual Studio

### Keystore Issues
- Verify keystore path is correct
- Check passwords are correct
- Ensure keystore file exists

### Upload Rejected
- Check version code is higher than previous
- Ensure AAB is signed correctly
- Verify package name matches

## Resources
- [Google Play Console](https://play.google.com/console)
- [Android Publishing Guide](https://learn.microsoft.com/en-us/dotnet/maui/android/deployment/)
- [.NET MAUI Documentation](https://learn.microsoft.com/en-us/dotnet/maui/)
