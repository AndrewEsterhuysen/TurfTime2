# FCM Push Notifications - .NET MAUI Implementation Guide
## Turf Time Chat Notifications - Part 2

## 📋 Overview

This guide shows how to add Firebase Cloud Messaging (FCM) to your .NET MAUI app so users receive push notifications when they get chat messages.

## ⚠️ Important Prerequisites

Before starting, ensure you've completed:
1. ✅ Firebase Cloud Functions deployed (see `FIREBASE_FUNCTIONS_DEPLOYMENT.md`)
2. ✅ Firebase Blaze plan activated
3. ✅ Cloud Function is working (check logs)

## 📦 Step 1: Add Firebase Configuration Files

### Android Configuration

1. **Download `google-services.json`:**
   - Go to https://console.firebase.google.com
   - Select your project: **turftime-6a97b**
   - Click ⚙️ **Settings** → **Project settings**
   - Scroll to "Your apps" section
   - Find your Android app (com.andrewestherhuysen.turftime)
   - Click **Download google-services.json**

2. **Add to project:**
   - Copy `google-services.json` to: `C:\Users\esterha\source\repos\TurfTimer-Rebuild\TurfTime2\Platforms\Android\`
   - Right-click file in Visual Studio → Properties
   - Set **Build Action** to: `GoogleServicesJson`

### iOS Configuration (Optional - if you support iOS)

1. **Download `GoogleService-Info.plist`:**
   - Same steps as above, but click iOS app
   - Download `GoogleService-Info.plist`

2. **Add to project:**
   - Copy to: `C:\Users\esterha\source\repos\TurfTimer-Rebuild\TurfTime2\Platforms\iOS\`
   - Set **Build Action** to: `BundleResource`

## 📦 Step 2: Install NuGet Packages

### Option A: Using Package Manager Console

```powershell
Install-Package Plugin.Firebase -Version 3.0.3
Install-Package Plugin.Firebase.CloudMessaging -Version 3.0.3
```

### Option B: Using .NET CLI

```powershell
dotnet add package Plugin.Firebase --version 3.0.3
dotnet add package Plugin.Firebase.CloudMessaging --version 3.0.3
```

### Option C: Using Visual Studio NuGet Manager

1. Right-click project → **Manage NuGet Packages**
2. Search for: **Plugin.Firebase**
3. Install version 3.0.3
4. Search for: **Plugin.Firebase.CloudMessaging**
5. Install version 3.0.3

## 🔧 Step 3: Update AndroidManifest.xml

Add the following to `Platforms/Android/AndroidManifest.xml`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<manifest xmlns:android="http://schemas.android.com/apk/res/android">
    <application>
        <!-- Existing content -->

        <!-- Firebase Cloud Messaging -->
        <service
            android:name="com.google.firebase.messaging.FirebaseMessagingService"
            android:exported="false">
            <intent-filter>
                <action android:name="com.google.firebase.MESSAGING_EVENT" />
            </intent-filter>
        </service>

        <meta-data
            android:name="com.google.firebase.messaging.default_notification_icon"
            android:resource="@mipmap/appicon" />

        <meta-data
            android:name="com.google.firebase.messaging.default_notification_color"
            android:resource="@color/colorPrimary" />
    </application>

    <!-- Add permissions -->
    <uses-permission android:name="android.permission.INTERNET" />
    <uses-permission android:name="android.permission.ACCESS_NETWORK_STATE" />
    <uses-permission android:name="android.permission.POST_NOTIFICATIONS" />
</manifest>
```

## 🚀 Step 4: Initialize Firebase in MauiProgram.cs

Update your `MauiProgram.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Plugin.Firebase.Auth;
using Plugin.Firebase.Bundled.Shared;
using Plugin.Firebase.Firestore;
using Plugin.Firebase.CloudMessaging;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        // Register Firebase services
        builder.Services.AddSingleton(_ => CrossFirebaseAuth.Current);
        builder.Services.AddSingleton(_ => CrossFirebaseFirestore.Current);
        builder.Services.AddSingleton(_ => CrossFirebaseCloudMessaging.Current);

        // Initialize Firebase
        builder.ConfigureLifecycleEvents(events =>
        {
#if ANDROID
            events.AddAndroid(android => android
                .OnCreate((activity, bundle) =>
                {
                    CrossFirebase.Initialize(activity, bundle, new CrossFirebaseSettings(
                        isAuthEnabled: true,
                        isFirestoreEnabled: true,
                        isCloudMessagingEnabled: true));

                    System.Diagnostics.Debug.WriteLine("[Firebase] ✅ Initialized on Android");
                }));
#elif IOS
            events.AddiOS(iOS => iOS
                .FinishedLaunching((app, options) =>
                {
                    CrossFirebase.Initialize(new CrossFirebaseSettings(
                        isAuthEnabled: true,
                        isFirestoreEnabled: true,
                        isCloudMessagingEnabled: true));

                    System.Diagnostics.Debug.WriteLine("[Firebase] ✅ Initialized on iOS");
                    return true;
                }));
#endif
        });

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
```

## 📱 Step 5: Create FCM Service Helper

Create a new file: `TurfTime2/Services/FcmService.cs`

```csharp
using Plugin.Firebase.CloudMessaging;
using System.Diagnostics;

namespace TurfTime2.Services;

public class FcmService
{
    private static FcmService? _instance;
    public static FcmService Instance => _instance ??= new FcmService();

    private string? _currentToken;
    private bool _isInitialized;

    private FcmService() { }

    public async Task<bool> InitializeAsync()
    {
        if (_isInitialized)
            return true;

        try
        {
            Debug.WriteLine("[FCM] 🔔 Initializing Firebase Cloud Messaging...");

            // Request notification permission (required for Android 13+ and iOS)
            var permissionGranted = await CrossFirebaseCloudMessaging.Current.RequestPermissionAsync();

            if (!permissionGranted)
            {
                Debug.WriteLine("[FCM] ❌ Notification permission denied");
                return false;
            }

            Debug.WriteLine("[FCM] ✅ Notification permission granted");

            // Get FCM token
            _currentToken = await CrossFirebaseCloudMessaging.Current.GetTokenAsync();
            Debug.WriteLine($"[FCM] ✅ Token received: {_currentToken?.Substring(0, 20)}...");

            // Subscribe to token refresh events
            CrossFirebaseCloudMessaging.Current.TokenChanged += OnTokenChanged;

            // Subscribe to notification received events
            CrossFirebaseCloudMessaging.Current.NotificationReceived += OnNotificationReceived;
            CrossFirebaseCloudMessaging.Current.NotificationTapped += OnNotificationTapped;

            _isInitialized = true;
            Debug.WriteLine("[FCM] ✅ FCM initialized successfully");

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FCM] ❌ Initialization error: {ex.Message}");
            return false;
        }
    }

    private void OnTokenChanged(object? sender, FCMTokenChangedEventArgs e)
    {
        _currentToken = e.Token;
        Debug.WriteLine($"[FCM] 🔄 Token refreshed: {_currentToken?.Substring(0, 20)}...");

        // Update token in Firestore
        _ = UpdateTokenInFirestoreAsync(_currentToken);
    }

    private void OnNotificationReceived(object? sender, FCMNotificationEventArgs e)
    {
        Debug.WriteLine($"[FCM] 📩 Notification received: {e.Notification.Title}");
        Debug.WriteLine($"[FCM] 📩 Body: {e.Notification.Body}");

        // You can show an in-app notification here if needed
    }

    private void OnNotificationTapped(object? sender, FCMNotificationEventArgs e)
    {
        Debug.WriteLine($"[FCM] 👆 Notification tapped: {e.Notification.Title}");

        // Navigate to chat page
        if (e.Notification.Data != null && e.Notification.Data.ContainsKey("teamId"))
        {
            var teamId = e.Notification.Data["teamId"];
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                try
                {
                    await Shell.Current.GoToAsync($"//ChatPage?teamId={teamId}");
                    Debug.WriteLine($"[FCM] ✅ Navigated to chat for team: {teamId}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[FCM] ❌ Navigation error: {ex.Message}");
                }
            });
        }
    }

    public async Task<string?> GetTokenAsync()
    {
        if (_currentToken != null)
            return _currentToken;

        try
        {
            _currentToken = await CrossFirebaseCloudMessaging.Current.GetTokenAsync();
            return _currentToken;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FCM] ❌ Error getting token: {ex.Message}");
            return null;
        }
    }

    public async Task UpdateTokenInFirestoreAsync(string? token = null)
    {
        try
        {
            token ??= _currentToken;

            if (string.IsNullOrEmpty(token))
            {
                Debug.WriteLine("[FCM] ⚠️ No token to update");
                return;
            }

            var teamId = Preferences.Get("team_id", "");
            var userId = Preferences.Get("user_id", "");

            if (string.IsNullOrEmpty(teamId) || string.IsNullOrEmpty(userId))
            {
                Debug.WriteLine("[FCM] ⚠️ No team or user ID, skipping token update");
                return;
            }

            Debug.WriteLine($"[FCM] 💾 Updating FCM token in Firestore for user: {userId}");

            // Update FCM token in Firestore member document
            var memberRef = Plugin.Firebase.Firestore.CrossFirebaseFirestore.Current
                .Instance
                .GetCollection($"teams/{teamId}/members")
                .GetDocument(userId);

            await memberRef.UpdateAsync(new Dictionary<string, object>
            {
                ["fcmToken"] = token,
                ["tokenUpdatedAt"] = DateTime.UtcNow
            });

            Debug.WriteLine("[FCM] ✅ Token updated in Firestore");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FCM] ❌ Error updating token: {ex.Message}");
        }
    }

    public async Task RemoveTokenFromFirestoreAsync()
    {
        try
        {
            var teamId = Preferences.Get("team_id", "");
            var userId = Preferences.Get("user_id", "");

            if (string.IsNullOrEmpty(teamId) || string.IsNullOrEmpty(userId))
                return;

            Debug.WriteLine($"[FCM] 🗑️ Removing FCM token from Firestore");

            var memberRef = Plugin.Firebase.Firestore.CrossFirebaseFirestore.Current
                .Instance
                .GetCollection($"teams/{teamId}/members")
                .GetDocument(userId);

            await memberRef.UpdateAsync(new Dictionary<string, object>
            {
                ["fcmToken"] = Plugin.Firebase.Firestore.FieldValue.Delete
            });

            Debug.WriteLine("[FCM] ✅ Token removed from Firestore");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FCM] ❌ Error removing token: {ex.Message}");
        }
    }
}
```

## 🎯 Step 6: Initialize FCM in App.xaml.cs

Update your `App.xaml.cs`:

```csharp
using TurfTime2.Services;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();

        MainPage = new AppShell();

        // Initialize FCM
        _ = InitializeFcmAsync();
    }

    private async Task InitializeFcmAsync()
    {
        try
        {
            // Wait a bit for app to fully initialize
            await Task.Delay(2000);

            var success = await FcmService.Instance.InitializeAsync();

            if (success)
            {
                // Update token in Firestore
                await FcmService.Instance.UpdateTokenInFirestoreAsync();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[App] ❌ FCM initialization error: {ex.Message}");
        }
    }

    protected override void OnStart()
    {
        base.OnStart();
        System.Diagnostics.Debug.WriteLine("[App] 🚀 App started");
    }

    protected override void OnSleep()
    {
        base.OnSleep();
        System.Diagnostics.Debug.WriteLine("[App] 😴 App going to sleep");
    }

    protected override void OnResume()
    {
        base.OnResume();
        System.Diagnostics.Debug.WriteLine("[App] 👋 App resumed");
    }
}
```

## 🧪 Step 7: Test the Implementation

### Test 1: Verify Token Generation

1. Run the app
2. Check Debug Output for:
```
[FCM] 🔔 Initializing Firebase Cloud Messaging...
[FCM] ✅ Notification permission granted
[FCM] ✅ Token received: dGhpcyBpcyBhIHRlc3...
[FCM] 💾 Updating FCM token in Firestore for user: abc123
[FCM] ✅ Token updated in Firestore
```

### Test 2: Verify Token in Firestore

1. Go to Firebase Console → Firestore
2. Navigate to: `teams/{your-team-id}/members/{your-user-id}`
3. Check if `fcmToken` field exists with a long string value

### Test 3: Send Test Notification

1. Keep app **closed** or **backgrounded**
2. From another device, send a chat message
3. You should receive a push notification!

### Test 4: Notification Tap Navigation

1. Receive a notification
2. Tap on it
3. App should open and navigate to ChatPage

## 🐛 Troubleshooting

### Issue 1: Permission Denied

**Symptoms:**
```
[FCM] ❌ Notification permission denied
```

**Solution:**
- Android 13+: Check Settings → Apps → Turf Time → Notifications → Enable
- Reinstall app to get permission prompt again

### Issue 2: Token Not Received

**Symptoms:**
```
[FCM] ❌ Error getting token: ...
```

**Solutions:**
1. Check `google-services.json` is in correct location
2. Verify Build Action is set to `GoogleServicesJson`
3. Clean and rebuild solution
4. Check internet connection

### Issue 3: Token Not Saved to Firestore

**Symptoms:**
- Token generated but not in Firestore

**Solutions:**
1. Check user is authenticated
2. Verify `team_id` and `user_id` are in Preferences
3. Check Firestore security rules allow write

### Issue 4: Notifications Not Received

**Checklist:**
- ✅ Cloud Function deployed and active
- ✅ Blaze plan enabled
- ✅ FCM token saved to Firestore
- ✅ Member document has `fcmToken` field
- ✅ App is closed or backgrounded
- ✅ Internet connection active

**Debug:**
1. Check Cloud Function logs: `firebase functions:log`
2. Verify function is triggered when message sent
3. Check for "Sending notification to X members"
4. Look for FCM errors in logs

## 📊 Monitoring

### View FCM Delivery Stats

1. Firebase Console → Cloud Messaging
2. View delivery success rates
3. Check for failed deliveries

### Debug Logs

**Android Debug Output:**
```
[FCM] 📩 Notification received: 💬 Team Name
[FCM] 📩 Body: John: Hello everyone!
```

**Cloud Function Logs:**
```
Sending notification to 3 members
Notification sent. Success: 3, Failure: 0
```

## 🎯 Next Steps After Implementation

1. ✅ Test with multiple devices
2. ✅ Verify notification delivery when app is closed
3. ✅ Test notification tap navigation
4. ✅ Monitor Cloud Function costs (should be $0)
5. ✅ Consider adding notification preferences (mute, do not disturb)

## 🔒 Security Considerations

- FCM tokens are stored securely in Firestore
- Tokens are device-specific and expire automatically
- Invalid tokens are automatically cleaned up by Cloud Function
- Only team members receive notifications (filtered by Cloud Function)

## 💰 Cost Summary

**Monthly Estimate (100 users, 10 messages/day):**
- Cloud Functions: $0.00 (within free tier)
- FCM: $0.00 (completely free)
- Firestore: ~$0.01 (token storage)

**Total: Essentially free!**

---

## ✅ Completion Checklist

- [ ] `google-services.json` added to project
- [ ] NuGet packages installed
- [ ] AndroidManifest.xml updated
- [ ] MauiProgram.cs updated with Firebase initialization
- [ ] FcmService.cs created
- [ ] App.xaml.cs updated
- [ ] App runs without errors
- [ ] FCM token generated (check Debug Output)
- [ ] Token saved to Firestore (check Firebase Console)
- [ ] Test notification received when app closed
- [ ] Notification tap opens ChatPage

Once all items checked, push notifications are fully implemented! 🎉
