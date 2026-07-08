# 🚀 Deployment Checklist

## ✅ Ready to Deploy!

Your FCM multi-device push notification system is **ready for production**. Here's what's been completed:

### **1. MAUI Client (✅ Complete)**
- ✅ FCM initialization with proper permissions
- ✅ Automatic token registration on app startup
- ✅ Firebase authentication for Firestore writes
- ✅ Multi-device token array support
- ✅ Token refresh event handling
- ✅ Token cleanup on app uninstall/logout

### **2. Cloud Function (⏳ Pending Deploy)**
- ✅ Code committed to git (commit `4fa5c85` + `f6ee44a`)
- ✅ Multi-device token array support
- ✅ Backward compatibility with single token
- ✅ Invalid token cleanup
- ✅ ESLint passing
- ⏳ **Needs deployment to Firebase**

---

## 🎯 **Deployment Steps**

### **Option A: Firebase CLI (Recommended)**

#### **1. Re-authenticate:**
```bash
cd C:\Users\esterha\source\repos\TurfTimer-Rebuild\TurfTime2
firebase logout
firebase login
```

#### **2. Deploy:**
```bash
firebase deploy --only functions
```

#### **3. Verify:**
```bash
firebase functions:list
```

You should see:
```
✔ sendChatNotification(us-central1) [2nd gen]
  Trigger: Firestore teams/{teamId}/chat/{messageId}
  Runtime: Node.js 20
  Status: ACTIVE
```

---

### **Option B: Firebase Console Upload (Alternative)**

If CLI auth continues to fail, you can manually upload the function code:

#### **1. Zip the function code:**
```powershell
cd C:\Users\esterha\source\repos\TurfTimer-Rebuild\TurfTime2\functions
Compress-Archive -Path index.js,package.json,package-lock.json,eslint.config.js -DestinationPath ../functions-deploy.zip -Force
```

#### **2. Go to Firebase Console:**
https://console.firebase.google.com/project/turf-timer/functions

#### **3. Click "Edit" on `sendChatNotification` function**

#### **4. Upload `functions-deploy.zip`**

#### **5. Deploy**

---

### **Option C: GitHub Actions CI/CD (Best for Future)**

Set up automatic deployment on every push to `master`:

#### **1. Create `.github/workflows/deploy-functions.yml`:**
```yaml
name: Deploy Firebase Functions

on:
  push:
    branches:
      - master
    paths:
      - 'functions/**'

jobs:
  deploy:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v3

      - name: Setup Node.js
        uses: actions/setup-node@v3
        with:
          node-version: '20'

      - name: Install dependencies
        run: |
          cd functions
          npm ci

      - name: Deploy to Firebase
        uses: w9jds/firebase-action@master
        with:
          args: deploy --only functions
        env:
          FIREBASE_TOKEN: ${{ secrets.FIREBASE_TOKEN }}
```

#### **2. Generate Firebase token:**
```bash
firebase login:ci
```

Copy the token that's printed.

#### **3. Add token to GitHub:**
- Go to: https://github.com/AndrewEsterhuysen/TurfTime2/settings/secrets/actions
- Click "New repository secret"
- Name: `FIREBASE_TOKEN`
- Value: (paste the token)
- Click "Add secret"

#### **4. Push to GitHub:**
```bash
git push origin master
```

The function will deploy automatically! 🎉

---

## 🧪 **Testing After Deployment**

### **1. Run MAUI App:**
```
Launch on Android device/emulator
```

### **2. Check Debug Output:**
```
[FCM] 🔔 Initializing Firebase Cloud Messaging...
[FCM] ✅ Notification permission granted
[FCM] ✅ Token received: eyJhbGc...
[FCM] ✅ FCM initialized successfully
[FCM] 💾 Updating FCM token in Firestore for user: {userId}
[FCM] ✅ Auth token acquired
[FCM] ➕ Adding new device token. Total devices: 1
[FCM] ✅ Token saved to Firestore successfully
```

### **3. Verify in Firestore:**
```
Go to: https://console.firebase.google.com/project/turf-timer/firestore
Navigate to: teams/{teamId}/members/{userId}
Check: fcmTokens array should contain your device token
```

### **4. Test Notification:**
```
1. Open app on Device A
2. Open app on Device B (different device, same or different user)
3. Send a chat message from Device A
4. Device B should receive notification! 🎉
```

---

## 🐛 **Troubleshooting**

### **Problem: "Authentication Error: Your credentials are no longer valid"**
**Solution:** Run `firebase logout` then `firebase login`

### **Problem: "No token to update"**
**Solution:** Check that:
1. Firebase is initialized in `MauiProgram.cs`
2. `google-services.json` is in `Platforms/Android`
3. App has notification permissions

### **Problem: "No team or user ID, skipping token update"**
**Solution:** Make sure user has joined a cloud team (not local team)

### **Problem: "Token already registered"**
**Solution:** This is normal! Token is already saved. No action needed.

### **Problem: No notification received**
**Solution:** Check:
1. Cloud Function is deployed and active
2. Sender and receiver are in the same team
3. Receiver is not the message sender (function excludes sender)
4. Device has network connectivity
5. Notification permissions are granted

---

## 📝 **Summary**

| Component | Status |
|-----------|--------|
| MAUI FCM Integration | ✅ Complete |
| Firebase Authentication | ✅ Complete |
| Multi-Device Token Support | ✅ Complete |
| Cloud Function Code | ✅ Complete & Committed |
| Cloud Function Deployment | ⏳ Pending |
| ESLint Configuration | ✅ Complete |
| Documentation | ✅ Complete |

**Next Action:** Deploy the Cloud Function using one of the options above! 🚀
