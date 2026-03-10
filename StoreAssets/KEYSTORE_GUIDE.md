# Android Keystore Generation Guide

## Step 1: Generate Keystore (First Time Only)

Run this command in PowerShell from the project root:

```powershell
# Make sure you have JDK installed (comes with Android SDK)
# Find keytool location (usually in Android SDK or JDK folder)

# Generate keystore
keytool -genkeypair -v -keystore turftime.keystore -alias turftime -keyalg RSA -keysize 2048 -validity 10000

# You will be prompted for:
# - Keystore password (REMEMBER THIS!)
# - Key password (REMEMBER THIS!)
# - Your name
# - Organizational unit (optional)
# - Organization name (e.g., "Andrew Esterhuysen")
# - City/Locality
# - State/Province  
# - Country code (e.g., "ZA" for South Africa)
```

## Step 2: Store Passwords Securely

**CRITICAL**: Save these passwords in a secure location (password manager):
- Keystore password
- Key password
- Keystore file location

**DO NOT**:
- Commit the keystore to Git
- Share passwords publicly
- Lose the keystore file (you cannot update your app without it!)

## Step 3: Update TurfTime2.csproj

Edit the Android Release configuration in `TurfTime2.csproj`:

```xml
<AndroidSigningStorePass>YOUR_KEYSTORE_PASSWORD</AndroidSigningStorePass>
<AndroidSigningKeyPass>YOUR_KEY_PASSWORD</AndroidSigningKeyPass>
```

**Better approach - use environment variables**:

```xml
<AndroidSigningStorePass>$(AndroidSigningStorePassword)</AndroidSigningStorePass>
<AndroidSigningKeyPass>$(AndroidSigningKeyPassword)</AndroidSigningKeyPass>
```

Then set environment variables before building:
```powershell
$env:AndroidSigningStorePassword = "your_keystore_password"
$env:AndroidSigningKeyPassword = "your_key_password"
```

## Step 4: Add to .gitignore

Add to `.gitignore`:
```
# Android Keystore - DO NOT COMMIT!
*.keystore
*.jks
keystore.properties
```

## Keystore Location
Place the generated `turftime.keystore` file in the project root directory (same level as TurfTime2.csproj).

## Backup Your Keystore
**IMMEDIATELY** backup your keystore file to:
1. Secure cloud storage (encrypted)
2. External hard drive
3. Password manager (if it supports file attachments)

**If you lose this file, you can never update your app on Google Play!**
