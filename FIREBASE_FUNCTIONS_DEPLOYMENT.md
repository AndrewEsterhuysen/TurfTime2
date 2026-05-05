# Firebase Cloud Functions Deployment Guide
## Push Notifications for Turf Time Chat

## 📋 Prerequisites Completed
✅ Firebase CLI installed (version 15.16.0)
✅ Cloud Functions code created (`functions/index.js`)
✅ Package configuration created (`functions/package.json`)
✅ Firebase config created (`.firebaserc`, `firebase.json`)

## 🚀 Step-by-Step Deployment

### Step 1: Authenticate with Firebase

Open a **NEW PowerShell window** (to get updated PATH) and run:

```powershell
firebase login
```

This will:
1. Open your browser
2. Ask you to sign in with your Google account (the one you use for Firebase)
3. Grant permissions to Firebase CLI
4. Confirm successful login

**If `firebase` command not found:**
```powershell
& "C:\Users\esterha\AppData\Roaming\npm\firebase.cmd" login
```

### Step 2: Install Function Dependencies

Navigate to the functions directory and install packages:

```powershell
cd functions
npm install
cd ..
```

This installs:
- `firebase-admin` - Server SDK for Firebase
- `firebase-functions` - Cloud Functions framework

**Expected output:**
```
added 200+ packages in ~30s
```

### Step 3: Upgrade to Firebase Blaze Plan

⚠️ **IMPORTANT:** Cloud Functions require the Blaze (pay-as-you-go) plan.

1. Go to https://console.firebase.google.com
2. Select your project: **turftime-6a97b**
3. Click on ⚙️ **Settings** (bottom left)
4. Click **Usage and billing**
5. Click **Modify plan**
6. Select **Blaze (pay as you go)**
7. Enter your billing information

**Cost estimate for your app:**
- **Free tier:** 2 million function invocations/month
- **Your usage:** ~1,000-5,000 messages/month
- **Expected cost:** $0.00/month (well within free tier)

You only pay if you exceed:
- 2M invocations
- 400K GB-seconds compute
- 200K GHz-seconds compute
- 5GB network egress

### Step 4: Deploy Cloud Function

From your project root directory:

```powershell
firebase deploy --only functions
```

**If `firebase` command not found:**
```powershell
& "C:\Users\esterha\AppData\Roaming\npm\firebase.cmd" deploy --only functions
```

**Expected output:**
```
=== Deploying to 'turftime-6a97b'...

i  deploying functions
i  functions: preparing codebase for deployment
✔  functions: functions folder uploaded successfully
i  functions: creating Node.js 18 function sendChatNotification...
✔  functions[sendChatNotification(us-central1)] Successful create operation.

✔  Deploy complete!

Function URL (sendChatNotification): (trigger: Firestore document create)
   Resource: teams/{teamId}/chat/{messageId}
```

### Step 5: Verify Deployment

Check if function is deployed:

```powershell
firebase functions:list
```

**Expected output:**
```
┌───────────────────────────┬────────────────────┬────────────┐
│ Name                      │ Trigger            │ Location   │
├───────────────────────────┼────────────────────┼────────────┤
│ sendChatNotification      │ Firestore          │ us-central1│
└───────────────────────────┴────────────────────┴────────────┘
```

### Step 6: View Function Logs

To monitor function execution in real-time:

```powershell
firebase functions:log --only sendChatNotification
```

Or view logs in Firebase Console:
1. Go to https://console.firebase.google.com
2. Select project: **turftime-6a97b**
3. Click **Functions** (left menu)
4. Click **Logs** tab

## 🧪 Testing

### Test 1: Send a Test Message

1. Open your app
2. Go to Chat page
3. Send a message from one device
4. Check if other team members receive notification

### Test 2: Check Function Logs

```powershell
firebase functions:log --only sendChatNotification
```

**Expected log output when message is sent:**
```
New message in team team16-qt4y3z from John Doe
Sending notification to 3 members
Notification sent. Success: 3, Failure: 0
```

### Test 3: Monitor in Firebase Console

1. Go to Firebase Console → Functions → Logs
2. Send a test message
3. Refresh logs to see execution

**Successful log entry:**
```
INFO: New message in team team16-qt4y3z from John Doe
INFO: Sending notification to 3 members
INFO: Notification sent. Success: 3, Failure: 0
```

## 🔧 Troubleshooting

### Issue 1: "firebase: command not found"

**Solution:**
Close and reopen PowerShell, or use full path:
```powershell
& "C:\Users\esterha\AppData\Roaming\npm\firebase.cmd" <command>
```

### Issue 2: "Functions require Blaze plan"

**Solution:**
Upgrade to Blaze plan in Firebase Console (see Step 3 above)

### Issue 3: "No tokens to send to"

**Cause:** Team members don't have FCM tokens registered

**Solution:** 
Wait for Step 7 (C# implementation) where we'll register FCM tokens

### Issue 4: Deploy fails with authentication error

**Solution:**
```powershell
firebase logout
firebase login
```

### Issue 5: Function deployed but not triggering

**Checks:**
1. Verify Firestore path: `teams/{teamId}/chat/{messageId}`
2. Check function logs: `firebase functions:log`
3. Verify billing is enabled (Blaze plan required)

## 📊 Monitoring

### View Real-Time Logs:
```powershell
firebase functions:log --follow
```

### View Specific Function Logs:
```powershell
firebase functions:log --only sendChatNotification
```

### View Recent Errors Only:
```powershell
firebase functions:log --only sendChatNotification | Select-String "ERROR"
```

## 🔄 Updating the Function

After making changes to `functions/index.js`:

```powershell
cd functions
npm install  # If you added dependencies
cd ..
firebase deploy --only functions
```

**Hot Reload:**
Functions automatically update on the next invocation after deployment (no app restart needed)

## 🗑️ Deleting the Function (if needed)

```powershell
firebase functions:delete sendChatNotification
```

## 💰 Cost Monitoring

View usage and billing:
1. Firebase Console → Settings → Usage and billing
2. Set budget alerts to avoid unexpected charges
3. Typical usage: Well within free tier

**Recommended: Set a budget alert at $1.00**

## 📝 Next Steps

After successful deployment:
1. ✅ Function is now live and ready
2. ⏭️ Next: Implement FCM in .NET MAUI app (Step 7)
3. ⏭️ Then: Register FCM tokens to Firestore
4. ⏭️ Finally: Test end-to-end notifications

## 🎯 Quick Command Reference

```powershell
# Login
firebase login

# Deploy
firebase deploy --only functions

# View logs
firebase functions:log

# List functions
firebase functions:list

# Delete function
firebase functions:delete sendChatNotification
```

## 🆘 Getting Help

If you encounter issues:
1. Check Firebase Console → Functions → Logs
2. Run: `firebase functions:log --only sendChatNotification`
3. Verify Blaze plan is active
4. Check function execution count in Firebase Console

---

**Status: Ready to Deploy** ✅
All files created and ready. Follow steps above to deploy!
