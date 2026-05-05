# Push Notifications - Quick Reference Card

## 🚀 Quick Deploy Commands

### Deploy Cloud Function (First Time)
```powershell
# Navigate to project
cd C:\Users\esterha\source\repos\TurfTimer-Rebuild\TurfTime2

# Login to Firebase
& "C:\Users\esterha\AppData\Roaming\npm\firebase.cmd" login

# Install dependencies
cd functions
npm install
cd ..

# Deploy
& "C:\Users\esterha\AppData\Roaming\npm\firebase.cmd" deploy --only functions
```

### Update Cloud Function (After Changes)
```powershell
cd C:\Users\esterha\source\repos\TurfTimer-Rebuild\TurfTime2
& "C:\Users\esterha\AppData\Roaming\npm\firebase.cmd" deploy --only functions
```

### View Logs
```powershell
& "C:\Users\esterha\AppData\Roaming\npm\firebase.cmd" functions:log --only sendChatNotification
```

## 📦 Add FCM to App

### Install Packages
```powershell
dotnet add package Plugin.Firebase --version 3.0.3
dotnet add package Plugin.Firebase.CloudMessaging --version 3.0.3
```

### Files to Update
1. `Platforms/Android/google-services.json` (download from Firebase Console)
2. `Platforms/Android/AndroidManifest.xml` (add FCM config)
3. `MauiProgram.cs` (initialize Firebase)
4. `Services/FcmService.cs` (create new file)
5. `App.xaml.cs` (initialize FCM)

## 🧪 Testing Checklist

- [ ] Cloud Function deployed
- [ ] App has FCM packages installed
- [ ] google-services.json in project
- [ ] App generates FCM token (check Debug Output)
- [ ] Token saved to Firestore (check Firebase Console)
- [ ] Close app completely
- [ ] Send message from another device
- [ ] Receive push notification
- [ ] Tap notification → Opens ChatPage

## 🐛 Quick Troubleshooting

### No notification received?
1. Check Cloud Function logs: `firebase functions:log`
2. Verify FCM token in Firestore: `teams/{teamId}/members/{userId}/fcmToken`
3. Check app Debug Output for FCM initialization
4. Verify notification permission granted (app settings)
5. Ensure app is closed or backgrounded

### "firebase: command not found"?
Use full path: `& "C:\Users\esterha\AppData\Roaming\npm\firebase.cmd" <command>`

### "Functions require Blaze plan"?
Upgrade in Firebase Console → Settings → Usage and billing → Blaze plan

### Function deployed but not working?
1. Check function logs: `firebase functions:log`
2. Verify Firestore path: `teams/{teamId}/chat/{messageId}`
3. Ensure member documents have `fcmToken` field
4. Check internet connection

## 💰 Cost

**Expected: $0.00/month** (within free tier)

Free tier includes:
- 2M function invocations/month
- Unlimited FCM messages
- 50K Firestore reads, 20K writes

## 📚 Documentation

- **Full deployment guide:** `FIREBASE_FUNCTIONS_DEPLOYMENT.md`
- **App integration guide:** `FCM_MAUI_IMPLEMENTATION.md`
- **Complete summary:** `PUSH_NOTIFICATIONS_SUMMARY.md`

## 🎯 Architecture

```
Message sent → Firestore → Cloud Function → FCM → User's Device
```

## ✅ Success Indicators

**Cloud Function:**
```
INFO: New message in team team16-qt4y3z from John Doe
INFO: Sending notification to 3 members
INFO: Notification sent. Success: 3, Failure: 0
```

**App Debug Output:**
```
[FCM] ✅ Notification permission granted
[FCM] ✅ Token received: dGhpcyBpcyBhIHRlc3Q...
[FCM] ✅ Token updated in Firestore
[FCM] 📩 Notification received: 💬 Team Name
```

## 🔗 Quick Links

- Firebase Console: https://console.firebase.google.com
- Your Project: turftime-6a97b
- Functions Dashboard: https://console.firebase.google.com/project/turftime-6a97b/functions
- Firestore Database: https://console.firebase.google.com/project/turftime-6a97b/firestore

---

**Next Step:** Open `FIREBASE_FUNCTIONS_DEPLOYMENT.md` and follow Step 1!
