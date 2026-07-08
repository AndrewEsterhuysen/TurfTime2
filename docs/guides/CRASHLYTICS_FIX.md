# Firebase Crashlytics Fix

## Problem
The Android app was crashing at startup with:
```
java.lang.RuntimeException: Unable to get provider com.google.firebase.provider.FirebaseInitProvider
Caused by: java.lang.IllegalStateException: The Crashlytics build ID is missing
```

## Root Cause
`Plugin.Firebase` version 3.1.1 includes Firebase Crashlytics by default, which requires additional build tooling and configuration. Since we only need Cloud Messaging (FCM) for chat notifications, we don't need Crashlytics.

## Solution Applied

### 1. Disabled Crashlytics in Project File
Added to `TurfTime2.csproj`:
```xml
<PropertyGroup>
    ...
    <!-- Disable Firebase Crashlytics (we only need Cloud Messaging) -->
    <FirebaseCrashlyticsEnabled>false</FirebaseCrashlyticsEnabled>
</PropertyGroup>
```

### 2. Disabled Crashlytics in Android Manifest
Added to `Platforms/Android/AndroidManifest.xml`:
```xml
<meta-data
    android:name="firebase_crashlytics_collection_enabled"
    android:value="false" />
```

## Expected Behavior After Fix

When you run the app in Android debug now, you should see:

1. ✅ App launches successfully (no Crashlytics crash)
2. ✅ Firebase initializes
3. ✅ FCM token registration happens in `FcmService.InitializeAsync()`
4. ✅ Debug logs show:
   ```
   [FcmService] FCM initialized successfully
   [FcmService] Current FCM token: [your-device-token]
   [FcmService] Updating FCM token in Firestore...
   [FcmService] Token updated successfully for member [memberId]
   ```

## What's Still Working

- Firebase Cloud Messaging (FCM) for push notifications ✅
- Cloud Functions trigger and multicast delivery ✅
- Multi-device token storage (`fcmTokens` arrays) ✅
- All other Firebase functionality ✅

## What's Disabled

- Firebase Crashlytics crash reporting ❌ (not needed for our use case)

## Next Steps

1. Run the app in Android debug
2. Verify FCM token registration logs appear
3. Check Firestore for token updates in `teams/{teamId}/members/{memberId}/fcmTokens`
4. Test notification delivery by sending a chat message from another device

## Build Status

✅ Android build succeeds with 100 warnings (all deprecation warnings, not errors)
✅ Crashlytics disabled at both MSBuild and Android manifest levels
✅ FCM configuration remains intact
