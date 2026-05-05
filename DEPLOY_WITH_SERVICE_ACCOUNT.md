# 🔑 Deploy Cloud Functions with Service Account (Recommended)

Firebase CLI's `auth.firebase.tools` service is having issues. The modern, more reliable approach is to use a **Google Cloud Service Account**.

## 📋 **Step-by-Step Instructions**

### **1. Create Service Account in Google Cloud Console**

#### **Open Google Cloud Console:**
https://console.cloud.google.com/iam-admin/serviceaccounts?project=turf-timer

#### **Click "Create Service Account"**

#### **Fill in details:**
- **Service account name:** `firebase-deployer`
- **Description:** `Service account for deploying Firebase Functions`
- Click **"Create and Continue"**

#### **Grant roles:**
Add these roles:
- ✅ `Cloud Functions Admin`
- ✅ `Firebase Admin`
- ✅ `Service Account User`

Click **"Continue"** then **"Done"**

---

### **2. Create and Download Key**

#### **Click on the newly created service account**

#### **Go to "Keys" tab**

#### **Click "Add Key" → "Create new key"**

#### **Select "JSON"**

#### **Click "Create"**

A JSON file will be downloaded (e.g., `turf-timer-abc123.json`)

---

### **3. Deploy Using Service Account**

#### **Option A: Use Environment Variable**

```powershell
# Set the path to your downloaded key file
$env:GOOGLE_APPLICATION_CREDENTIALS = "C:\Users\esterha\Downloads\turf-timer-abc123.json"

# Verify the project
cd C:\Users\esterha\source\repos\TurfTimer-Rebuild\TurfTime2
firebase use turf-timer

# Deploy!
firebase deploy --only functions
```

#### **Option B: Use gcloud CLI**

If you have gcloud CLI installed:

```powershell
# Authenticate with service account
gcloud auth activate-service-account --key-file="C:\Users\esterha\Downloads\turf-timer-abc123.json"

# Set project
gcloud config set project turf-timer

# Deploy via Firebase CLI
cd C:\Users\esterha\source\repos\TurfTimer-Rebuild\TurfTime2
firebase deploy --only functions
```

---

### **4. Verify Deployment**

```powershell
firebase functions:list
```

You should see:
```
✔ sendChatNotification(us-central1) [2nd gen]
  Updated: [timestamp]
  Runtime: Node.js 20
```

---

## 🚀 **Quick Deploy Script**

Save this as `deploy-functions.ps1`:

```powershell
# Firebase Cloud Functions Deploy Script
# Usage: .\deploy-functions.ps1 "C:\path\to\service-account-key.json"

param(
    [Parameter(Mandatory=$true)]
    [string]$ServiceAccountKeyPath
)

if (-not (Test-Path $ServiceAccountKeyPath)) {
    Write-Error "Service account key file not found: $ServiceAccountKeyPath"
    exit 1
}

Write-Host "🔑 Setting Google Application Credentials..." -ForegroundColor Cyan
$env:GOOGLE_APPLICATION_CREDENTIALS = $ServiceAccountKeyPath

Write-Host "📂 Navigating to project directory..." -ForegroundColor Cyan
cd C:\Users\esterha\source\repos\TurfTimer-Rebuild\TurfTime2

Write-Host "🎯 Setting Firebase project..." -ForegroundColor Cyan
& "C:\Users\esterha\AppData\Roaming\npm\firebase.cmd" use turf-timer

Write-Host "🚀 Deploying functions..." -ForegroundColor Cyan
& "C:\Users\esterha\AppData\Roaming\npm\firebase.cmd" deploy --only functions

Write-Host "✅ Deployment complete!" -ForegroundColor Green
Write-Host "📋 Listing deployed functions..." -ForegroundColor Cyan
& "C:\Users\esterha\AppData\Roaming\npm\firebase.cmd" functions:list
```

Then run:
```powershell
.\deploy-functions.ps1 "C:\Users\esterha\Downloads\turf-timer-abc123.json"
```

---

## 🛡️ **Security Best Practices**

### **DO:**
- ✅ Store service account key in a secure location
- ✅ Add `*.json` to `.gitignore` to prevent accidental commits
- ✅ Restrict service account permissions to only what's needed
- ✅ Rotate keys periodically

### **DON'T:**
- ❌ Commit service account keys to version control
- ❌ Share keys in chat/email
- ❌ Give more permissions than necessary

---

## 🔄 **Alternative: Deploy from GitHub Actions**

Once you have the service account key, you can also set up automatic deployment:

1. **Add key to GitHub Secrets:**
   - Go to: https://github.com/AndrewEsterhuysen/TurfTime2/settings/secrets/actions
   - Click "New repository secret"
   - Name: `GCP_SERVICE_ACCOUNT_KEY`
   - Value: (paste the entire contents of the JSON file)

2. **GitHub Actions will automatically deploy** when you push to `functions/`

---

## 📞 **Need Help?**

If you encounter any issues:
1. Verify the service account has the correct permissions
2. Check that the JSON key file path is correct
3. Ensure Firebase project ID is `turf-timer`
4. Check Cloud Functions logs in Google Cloud Console
