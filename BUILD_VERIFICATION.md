# 📱 Build Verification Guide

## How to Verify Deployed Build

### On Your Phone
1. Open **Turf Timer** app
2. Go to **Settings → Help**
3. At the top, you'll see:
   ```
   Current Build
   v1.0.0 (Build 2) | 1f43abe | 2026-03-24 23:57 UTC
   ```

### On Your Computer

#### Check Current Git Commit:
```powershell
# Short hash (7 characters)
git rev-parse --short HEAD
# Output: 1f43abe

# Full commit info
git log --oneline -1
# Output: 1f43abe Fix rotation reset bug
```

#### Check Last Commit Time:
```powershell
git log -1 --format="%cd" --date=format:"%Y-%m-%d %H:%M"
# Output: 2026-03-24 16:57 (local time)
```

### Cross-Reference Matrix

| What to Check | Phone Display | Computer Command | Must Match? |
|---------------|---------------|------------------|-------------|
| **Version** | `v1.0.0` | Check `.csproj` `ApplicationDisplayVersion` | ✅ Yes |
| **Build Number** | `Build 2` | Check `.csproj` `ApplicationVersion` | ✅ Yes |
| **Git Commit** | `1f43abe` | `git rev-parse --short HEAD` | ✅ Yes (latest code) |
| **Build Time** | `2026-03-24 23:57 UTC` | Recent (within minutes of compile) | ⚠️ Should be recent |

### Verification Examples

#### ✅ **GOOD: Latest Build Deployed**
```
Phone:    v1.0.0 (Build 2) | 1f43abe | 2026-03-24 23:57 UTC
Computer: 1f43abe (git rev-parse --short HEAD)
Status:   ✅ Commit matches → Latest code is on phone
```

#### ❌ **BAD: Old Build on Phone**
```
Phone:    v1.0.0 (Build 2) | a7f3d2e | 2026-03-23 10:15 UTC
Computer: 1f43abe (git rev-parse --short HEAD)
Status:   ❌ Commit mismatch → Old build, need to redeploy
```

#### ⚠️ **WARNING: Uncommitted Changes**
```
Phone:    v1.0.0 (Build 2) | 1f43abe | 2026-03-24 23:57 UTC
Computer: 1f43abe (but 'git status' shows modified files)
Status:   ⚠️ Commit matches, but you have uncommitted changes
         → Phone has last committed version, not latest edits
```

---

## 🔄 Deployment Workflow

### Before Deploying:
```powershell
# 1. Commit your changes
git add .
git commit -m "Fix rotation reset bug"

# 2. Note the commit hash
git rev-parse --short HEAD
# Output: 1f43abe

# 3. Build and deploy to phone
# (Use Visual Studio or CLI)
```

### After Deploying:
```powershell
# 1. Open app on phone
# 2. Settings → Help
# 3. Verify commit hash matches: 1f43abe ✅
```

---

## 🛠️ How It Works

### Build-Time Metadata Extraction

**TurfTime2.csproj** automatically runs before each build:
```xml
<Target Name="GetBuildMetadata" BeforeTargets="BeforeBuild">
  1. Executes: git rev-parse --short HEAD
  2. Captures commit hash: 1f43abe
  3. Gets current UTC time: 2026-03-24 23:57 UTC
  4. Embeds as assembly metadata
</Target>
```

**HelpPage.xaml.cs** reads the metadata at runtime:
```csharp
var assembly = Assembly.GetExecutingAssembly();
var gitCommit = /* extract from metadata */;
var buildTime = /* extract from metadata */;
```

### Zero Manual Effort
- ✅ **Automatic:** Updates on every build
- ✅ **Accurate:** Can't forget to update
- ✅ **Traceable:** Links directly to git history

---

## 📝 Updating Version Numbers

### For New Releases:

Edit **TurfTime2.csproj**:
```xml
<ApplicationDisplayVersion>1.0.1</ApplicationDisplayVersion>  <!-- User-facing version -->
<ApplicationVersion>3</ApplicationVersion>                     <!-- Build number -->
```

### Version History:
- `1.0.0 (Build 1)` - Initial release
- `1.0.0 (Build 2)` - Current
- `1.0.1 (Build 3)` - Next patch
- `1.1.0 (Build 4)` - Next minor version
- `2.0.0 (Build 5)` - Next major version

---

## 🎯 Quick Verification Command

Copy/paste this into PowerShell for quick verification:

```powershell
Write-Host "Current Git Commit: " -NoNewline; git rev-parse --short HEAD
Write-Host "Build Time (UTC):   $(Get-Date -AsUTC -Format 'yyyy-MM-dd HH:mm') UTC"
Write-Host "`nNow check your phone's Help page to verify it matches!"
```

**Example Output:**
```
Current Git Commit: 1f43abe
Build Time (UTC):   2026-03-24 23:57 UTC

Now check your phone's Help page to verify it matches!
```

---

## 💡 Pro Tips

1. **Commit before building** for production to ensure clean version tracking
2. **Screenshot the Help page** after deployment for records
3. **Check git status** before verifying - uncommitted changes won't be in the build
4. **Time may differ by 1-2 minutes** between build and check (that's normal)
5. **Git hash is most important** - if it matches, you have the right code

