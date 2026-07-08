# Firebase CLI Setup - Complete Guide for Your Environment

## 🎯 Problem
The `firebase` command isn't in your PATH, and credentials from other PowerShell sessions aren't accessible.

## ✅ Solution: Two Options

---

## Option A: Use Full Path (Quick & Simple)

Since `firebase` isn't in your PATH, use the full path for all commands:

### Replace all `firebase` commands with:
```powershell
& "C:\Users\esterha\AppData\Roaming\npm\firebase.cmd"
```

### Examples:

**Check login:**
```powershell
& "C:\Users\esterha\AppData\Roaming\npm\firebase.cmd" login:list
```

**Login:**
```powershell
& "C:\Users\esterha\AppData\Roaming\npm\firebase.cmd" login
```

**List projects:**
```powershell
& "C:\Users\esterha\AppData\Roaming\npm\firebase.cmd" projects:list
```

**Deploy functions:**
```powershell
& "C:\Users\esterha\AppData\Roaming\npm\firebase.cmd" deploy --only functions
```

---

## Option B: Add Firebase to PATH (Permanent Fix)

### Step 1: Add npm folder to PATH

1. Press `Win + X` → **System**
2. Click **Advanced system settings** (right side)
3. Click **Environment Variables** button
4. Under **User variables**, select **Path**
5. Click **Edit**
6. Click **New**
7. Add: `C:\Users\esterha\AppData\Roaming\npm`
8. Click **OK** on all dialogs
9. **Close and reopen PowerShell**

### Step 2: Verify

Open a **new PowerShell window** and run:
```powershell
firebase --version
```

Should show: `15.16.0`

---

## 🔐 Re-authenticate in This Terminal

Since you logged in from a different session, let's authenticate in your current terminal:

### Method 1: Interactive Login (Easiest)

```powershell
& "C:\Users\esterha\AppData\Roaming\npm\firebase.cmd" login
```

- Browser opens automatically
- Sign in with Google account
- Wait for "Success! Logged in"

### Method 2: No Localhost (If browser redirect fails)

```powershell
& "C:\Users\esterha\AppData\Roaming\npm\firebase.cmd" login --no-localhost
```

- Copy the URL shown
- Paste in browser
- Copy the code back
- Paste in terminal

---

## 🚀 Quick Deployment Guide (Using Full Path)

Once authenticated, run these commands in order:

### 1. Verify Login
```powershell
& "C:\Users\esterha\AppData\Roaming\npm\firebase.cmd" login:list
```

**Expected:** Your email address shows up

### 2. Verify Project Access
```powershell
& "C:\Users\esterha\AppData\Roaming\npm\firebase.cmd" projects:list
```

**Expected:** Shows `turftime-6a97b`

### 3. Install Function Dependencies
```powershell
cd functions
npm install
cd ..
```

**Expected:** `added 200+ packages`

### 4: Deploy Cloud Function
```powershell
& "C:\Users\esterha\AppData\Roaming\npm\firebase.cmd" deploy --only functions
```

**Expected:** "Deploy complete!"

### 5. Verify Deployment
```powershell
& "C:\Users\esterha\AppData\Roaming\npm\firebase.cmd" functions:list
```

**Expected:** Shows `sendChatNotification` function

---

## 💡 Create PowerShell Alias (Optional)

To avoid typing the full path every time, add this to your PowerShell profile:

### One-Time Setup:

```powershell
# Open profile in notepad
notepad $PROFILE
```

If file doesn't exist, create it:
```powershell
New-Item -Path $PROFILE -ItemType File -Force
notepad $PROFILE
```

### Add this line:
```powershell
function firebase { & "C:\Users\esterha\AppData\Roaming\npm\firebase.cmd" $args }
```

### Save, close, and reload:
```powershell
. $PROFILE
```

Now you can use `firebase` command directly!

---

## 🐛 Troubleshooting

### "Failed to authenticate" error
**Solution:** Run login command again:
```powershell
& "C:\Users\esterha\AppData\Roaming\npm\firebase.cmd" login --reauth
```

### Browser doesn't open
**Solution:** Use no-localhost option:
```powershell
& "C:\Users\esterha\AppData\Roaming\npm\firebase.cmd" login --no-localhost
```

### Still not working?
**Solution:** Generate CI token:
```powershell
& "C:\Users\esterha\AppData\Roaming\npm\firebase.cmd" login:ci
```
Then set environment variable:
```powershell
[System.Environment]::SetEnvironmentVariable("FIREBASE_TOKEN", "paste-token-here", "User")
```

---

## ✅ Next Step

Run this command to authenticate in your current terminal:

```powershell
& "C:\Users\esterha\AppData\Roaming\npm\firebase.cmd" login
```

Once you see **"Success! Logged in"**, you can proceed with deployment!
