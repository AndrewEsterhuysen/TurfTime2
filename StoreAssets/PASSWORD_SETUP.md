# 🔐 Keystore Password Setup

## Quick Start

After generating your keystore (see `StoreAssets/KEYSTORE_GUIDE.md`), you need to configure the passwords for building:

### 1. Edit Directory.Build.props

Open the file **`Directory.Build.props`** in the project root and replace the placeholders:

```xml
<AndroidSigningStorePassword>YOUR_ACTUAL_KEYSTORE_PASSWORD</AndroidSigningStorePassword>
<AndroidSigningKeyPassword>YOUR_ACTUAL_KEY_PASSWORD</AndroidSigningKeyPassword>
```

### 2. Verify It's Ignored by Git

Check that `Directory.Build.props` is listed in `.gitignore`:

```powershell
# Run this to verify it won't be committed:
git status --ignored

# You should see:
# !! Directory.Build.props
```

### 3. Build Your App

Now you can build without entering passwords each time:

```powershell
dotnet publish -f net10.0-android -c Release /p:AndroidPackageFormat=aab
```

---

## Security Checklist

- [x] `Directory.Build.props` is in `.gitignore`
- [ ] `Directory.Build.props` contains your actual passwords
- [ ] `Directory.Build.props` is backed up securely (alongside your keystore)
- [ ] You've verified it's not committed to Git: `git status --ignored`

---

## Backup Strategy

**Store these securely together:**
1. `turftime.keystore` file
2. `Directory.Build.props` file (with passwords)
3. Written record of:
   - Keystore password
   - Key password
   - Key alias: `turftime`

**Backup Locations (use at least 2):**
- 🔒 Password manager (1Password, Bitwarden, etc.)
- 💾 Encrypted cloud storage (Google Drive, OneDrive, Dropbox - in encrypted folder)
- 🔐 External hard drive (encrypted)
- 📝 Physical safe (written down)

---

## If You Clone on Another Machine

1. Clone the repository
2. Copy `Directory.Build.props.example` to `Directory.Build.props`
3. Edit `Directory.Build.props` with your actual passwords
4. Place `turftime.keystore` in project root
5. Build!

---

## Alternative: Environment Variables

If you prefer NOT to store passwords in a file, use environment variables each build session:

```powershell
$env:AndroidSigningStorePassword = "your_password"
$env:AndroidSigningKeyPassword = "your_password"
dotnet publish -f net10.0-android -c Release
```

**Pros:** No password file on disk  
**Cons:** Must set every time you open a new terminal

---

## Troubleshooting

### "AndroidSigningStorePassword is not set"

**Solution:** Edit `Directory.Build.props` with your actual passwords.

### "Keystore password is incorrect"

**Solution:** Double-check the password in `Directory.Build.props` matches your keystore.

### "Directory.Build.props shows in Git"

**DANGER!** ⚠️ Remove it immediately:
```powershell
git rm --cached Directory.Build.props
git commit -m "Remove sensitive file"
```

Then verify `.gitignore` contains:
```
Directory.Build.props
```

---

**Remember:** If someone gets access to both your keystore AND passwords, they can impersonate your app. Keep them secure!
