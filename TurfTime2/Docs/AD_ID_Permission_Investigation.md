# Android Advertising ID (AD_ID) — Investigation & Resolution

**Date:** 2025  
**Relevant file:** `Platforms/Android/AndroidManifest.xml`

---

## Background

The Google Play **Data Safety** form asks whether your app collects or shares the **Advertising ID** — a unique, user-resettable identifier provided by Google Play Services. Even if your own code never reads it, the permission can be injected into your app's merged manifest by transitive SDK dependencies, which counts as "using" it under Google's policy.

---

## Investigation

### Step 1 — Source code scan

A search across all `.cs`, `.xaml`, `.xml`, `.json`, and `.js` files found **no direct calls** to:
- `AdvertisingIdClient.getAdvertisingIdInfo()`
- `AdvertisingIdentifier`
- `AD_ID` in app-owned source files

### Step 2 — Library manifest scan

The Android build system extracts each AAR/JAR dependency into `obj/<config>/net10.0-android/lp/<n>/jl/`. Scanning all `AndroidManifest.xml` files in those folders revealed that **several transitive Google libraries inject `AD_ID`**:

| `lp/` folder | Android package name | Why present |
|---|---|---|
| `lp/305`, `lp/342` | `com.google.android.gms.ads_identifier` | The Advertising ID library itself |
| `lp/310`, `lp/350` | `com.google.android.gms.measurement.api` | Firebase Analytics/measurement SDK |
| `lp/313`, `lp/343` | `com.google.firebase.measurement_impl` | Firebase measurement implementation |
| `lp/314`, `lp/341` | `com.google.android.gms.measurement.sdk.api` | Firebase measurement SDK API |

These are all pulled in **transitively** via `Plugin.Firebase.CloudMessaging` — not declared directly in `TurfTime2.csproj`.

### Step 3 — Final merged manifest confirmation

The final merged manifest at `obj/Release/net10.0-android/android/manifest/AndroidManifest.xml` **contained the permission**:

```xml
<!-- Include required permissions for Advertising Id -->
<uses-permission android:name="com.google.android.gms.permission.AD_ID" />
```

This confirmed the app would declare the permission on the Play Store, even though it never uses the Advertising ID.

---

## Resolution

The `AD_ID` permission was suppressed by adding a `tools:node="remove"` override to the **app's own** `AndroidManifest.xml`. This instructs the manifest merger to strip the permission even if a library requests it:

```xml
<!-- Remove AD_ID permission injected transitively by Firebase/Google Play Services measurement libraries.
	 This app does not use advertising features; FCM push notifications do not require it. -->
<uses-permission android:name="com.google.android.gms.permission.AD_ID" tools:node="remove" />
```

After a clean rebuild, the merged manifest **no longer contained** `AD_ID` — only the comment appeared, confirming the removal worked.

---

## Key Concepts

### Why does `tools:node="remove"` work?
The Android manifest merger processes all library manifests and the app manifest together. A `tools:node="remove"` directive on any element tells the merger to actively delete that element from the output, overriding any library that requests it.

### Why does FCM not need AD_ID?
Firebase Cloud Messaging (push notifications) only requires:
- `android.permission.INTERNET`
- `android.permission.POST_NOTIFICATIONS` (Android 13+)

The `AD_ID` permission is needed by Firebase **Analytics** to associate events with a user's advertising profile. Since this app disables Crashlytics (`FirebaseCrashlyticsEnabled=false`) and does not use Firebase Analytics, the permission serves no functional purpose.

### What if you re-enable Firebase Analytics in the future?
Remove the `tools:node="remove"` line and update the Google Play Data Safety form accordingly:
- Collect: **Device or Other IDs → Advertising ID**
- Purpose: **Analytics**
- Shared: **No** (unless you explicitly share it with ad networks)
- Used for tracking: **No** (unless used for cross-app tracking)

---

## Google Play Data Safety Form Answer (current state)

| Question | Answer |
|---|---|
| Does your app collect or share Advertising ID? | **No** |
| Reason | Permission removed via `tools:node="remove"`; no code reads it |

---

## Files Changed

| File | Change |
|---|---|
| `Platforms/Android/AndroidManifest.xml` | Added `<uses-permission android:name="com.google.android.gms.permission.AD_ID" tools:node="remove" />` |
