# iOS and Mac Build Setup Checklist

## Overview
This document outlines the steps to build and deploy the TurfTime2 .NET MAUI app to iOS and macOS devices from Visual Studio on Windows, using a Mac as a build host.

---

## Hardware Setup
- ✅ Windows 11 VM running in UTM on Apple Mac
- ✅ Visual Studio 2026 (18.6.1) installed on Windows
- ✅ Apple Mac host available for remote build

---

## Mac Setup Requirements

### 1. Install Xcode
- [x] Download and install **Xcode** from Mac App Store (~10-15 GB)
- [x] Open Xcode at least once to accept license agreement
- [x] Install **iOS platform support** in Xcode
  - Go to Xcode → Settings → Platforms
  - Ensure iOS is installed/downloaded

### 2. Configure Xcode Command Line Tools
Open Terminal on Mac and run:
```bash
sudo xcode-select --switch /Applications/Xcode.app/Contents/Developer
xcode-select -p  # Verify: should show /Applications/Xcode.app/Contents/Developer
xcodebuild -version  # Verify Xcode version
```

### 3. Enable Remote Login (SSH)
- [x] System Settings → General → Sharing → Remote Login (turn ON)
- [x] Note your Mac username and IP address  ## andrewesterhuysen@10.0.0.248

### 4. Configure Apple Developer Account
- [x] Open Xcode → Settings → Accounts
- [x] Add your Apple ID
- [x] Select your team
- [ ] Create an **iOS Development** certificate if needed (Manage Certificates)

### 5. .NET Runtime
- [ ] **No manual installation needed** - Visual Studio Pair to Mac automatically installs .NET runtime to:
  - `/Users/[username]/Library/Caches/maui/PairToMac/Runtimes/dotnet`

---

## Visual Studio Setup (Windows)

### 1. Pair to Mac
- [x] Open Visual Studio 2026
- [x] Tools → iOS → Pair to Mac (or similar menu)
- [x] Enter Mac IP address
- [x] Enter Mac username and password
- [x] Wait for connection and automatic .NET runtime installation

### 2. Verify Connection Success
Check for these indicators in the Pair to Mac log:
- ✅ `A compatible dotnet runtime was found`
- ✅ `Xcode version: [actual version]` (not 0.0)
- ✅ `Host '[Mac Name]' is configured correctly`
- ✅ `SSH connection to '[Mac Name]' has been established`
- ❌ Avoid: "remote iOS SDK was not found" (means Xcode not properly installed)

---

## Project Configuration

### 1. Target Frameworks
Current configuration:
```xml
<TargetFrameworks>net10.0-android;net10.0-ios;net10.0-windows10.0.19041</TargetFrameworks>
```

### 2. iOS Code Signing
- [x] Debug configuration added with automatic signing
- [x] Release configuration already configured
```xml
<CodesignKey>Apple Development</CodesignKey>
<CodesignProvision>Automatic</CodesignProvision>
```

### 3. Optional: Add Mac Catalyst Support
- [x] To build native macOS apps (not just iOS), add to TargetFrameworks:
```xml
<TargetFrameworks>net10.0-android;net10.0-ios;net10.0-maccatalyst;net10.0-windows10.0.19041</TargetFrameworks>
```

---

## Deploy to iPhone

### 1. Prepare iPhone
- [x] Connect iPhone to **Mac** via USB (not Windows VM)
- [x] On iPhone: Tap **Trust This Computer** when prompted
- [x] Enable Developer Mode on iPhone (iOS 16+):
  - Settings → Privacy & Security → Developer Mode → ON
  - Restart iPhone when prompted

### 2. Deploy from Visual Studio
- [x] Ensure Pair to Mac is connected (check toolbar)
- [x] Select **Debug** configuration
- [x] Select target framework: `net10.0-ios`
- [x] Select your iPhone from device dropdown
- [ ] Press **F5** or click **Start Debugging**

### 3. Trust Developer on iPhone (First Time)
If you see "Untrusted Developer" on iPhone:
- [ ] Settings → General → VPN & Device Management
- [ ] Tap on your Apple ID/Developer name
- [ ] Tap **Trust**

---

## Troubleshooting

### Pair to Mac Issues
| Issue | Solution |
|-------|----------|
| "Xcode version: 0.0" | Install full Xcode app, not just Command Line Tools |
| "remote iOS SDK was not found" | Install iOS platform in Xcode |
| Authentication cancelled | Re-enter Mac credentials, check SSH is enabled |
| Connection lost | Check network, verify Mac is not sleeping |

### Deployment Issues
| Issue | Solution |
|-------|----------|
| "No valid iOS code signing keys" | Add Apple ID in Xcode → Settings → Accounts |
| iPhone not showing up | Connect to Mac (not VM), check in Xcode Devices window |
| "iPhone is locked" | Unlock iPhone |
| Build errors | Check Output window and Pair to Mac logs |
| `unable to build chain to self-signed root` | Install Apple WWDR intermediate certificate (see below) |
| `errSecInternalComponent` on framework signing | Unlock the login Keychain for the remote build agent (see below) |

### Fix: `unable to build chain` + `errSecInternalComponent`

These two errors appear together when the Mac Keychain either lacks the Apple intermediate
certificate or is locked while the Pair-to-Mac build daemon tries to sign frameworks
(e.g. `GoogleDataTransport.framework`).

**Step 1 – Install the Apple WWDR intermediate certificate**

Open Terminal on the Mac and run:
```bash
# Download the current G3 intermediate (covers certificates created via API)
curl -O https://www.apple.com/certificateauthority/AppleWWDRCAG3.cer
open AppleWWDRCAG3.cer        # opens Keychain Access — click "Add"
```
If the certificate still shows as untrusted, also install the Apple Root CA:
```bash
curl -O https://www.apple.com/appleca/AppleIncRootCertificate.cer
open AppleIncRootCertificate.cer
```
Verify in **Keychain Access** (`login` or `System` keychain) that
*Apple Worldwide Developer Relations Certification Authority* is listed as **trusted**.

**Step 2 – Allow the build daemon to access the Keychain without prompting**

The remote build runs as your Mac user but without a GUI session, so a locked Keychain
causes `errSecInternalComponent`. Fix with one of the options below (Option A is safer).

*Option A – Add codesign to the Keychain ACL (recommended)*
```bash
# Unlock your login keychain (enter your Mac login password when prompted)
security unlock-keychain ~/Library/Keychains/login.keychain-db

# Grant /usr/bin/codesign permanent access to your signing identity's private key:
# 1. Open Keychain Access
# 2. Find the private key for "Apple Development: Created via API (BZW4P28WC4)"
# 3. Double-click → Access Control tab → click "+" → add /usr/bin/codesign
# 4. Select "Allow all applications to access this item" (simpler, but less strict)
```

*Option B – Keep the login Keychain unlocked for remote sessions*
```bash
# Disable auto-lock so the daemon can always reach the key
security set-keychain-settings -t 0 ~/Library/Keychains/login.keychain-db
```
> ⚠️  Option B keeps the keychain permanently unlocked. Acceptable on a private Mac;
> avoid on a shared machine.

**Step 3 – Clean and rebuild**

After completing the Keychain steps, do a full clean build from Visual Studio:
1. Build → Clean Solution
2. Build → Rebuild Solution (with `net10.0-ios` selected)

---

## Project Information
- **Project Name**: TurfTime2
- **App ID**: com.andrewestherhuysen.turftime
- **Display Name**: Turf Time
- **Version**: 2.0.0 (Build 3)
- **Minimum iOS Version**: 15.0
- **Repository**: https://github.com/AndrewEsterhuysen/TurfTime2

---

## Key Architecture Notes
- **Pair to Mac** uses SSH to connect Windows Visual Studio to Mac build host
- Mac performs actual compilation using Xcode and .NET
- Windows VM on Mac can reach Mac host via network (UTM networking)
- .NET MAUI runtime automatically installed by Visual Studio to Mac cache directory
- No manual .NET MAUI installation required on Mac for Pair to Mac workflow

---

## Future Enhancements Discussed
- [ ] Add Mac Catalyst target for native macOS builds
- [ ] Configure runtime identifiers for Mac (Intel/Apple Silicon)
- [ ] Review platform-specific code for Mac compatibility

---

**Document Created**: Based on setup session for iOS/Mac deployment
**Last Updated**: Added codesign trust-chain and errSecInternalComponent fix
