# Google Play Store Release Checklist

## Pre-Build Checklist

### ✅ Code & Configuration
- [ ] Update version in `TurfTime2.csproj`:
  - [ ] `ApplicationDisplayVersion` (e.g., "1.0.0")
  - [ ] `ApplicationVersion` (integer, must increment)
- [ ] Test all features thoroughly
- [ ] Test on multiple Android devices/versions
- [ ] Verify app works offline
- [ ] Check screen wake lock works
- [ ] Test vibration alerts
- [ ] Verify data persistence
- [ ] Test rotation calculations
- [ ] Verify all timers work correctly

### ✅ Keystore & Signing
- [ ] Keystore file exists: `turftime.keystore`
- [ ] Keystore passwords stored securely
- [ ] `.gitignore` excludes keystore files
- [ ] Keystore backed up to secure location

### ✅ Privacy & Legal
- [ ] Privacy Policy updated and accessible
- [ ] Privacy Policy URL ready (host on GitHub Pages or website)
- [ ] Email address for support configured
- [ ] Terms of Service (if needed)

## Build Checklist

### ✅ Build Process
- [ ] Clean solution: `dotnet clean -c Release`
- [ ] Build succeeds in Release mode
- [ ] AAB file generated: `com.andrewestherhuysen.turftime-Signed.aab`
- [ ] AAB is properly signed (verify with `jarsigner`)
- [ ] File size is reasonable (<50 MB)

### ✅ Testing Release Build
- [ ] Install release APK/AAB on test device
- [ ] Test all features in release build
- [ ] Check app icon displays correctly
- [ ] Verify splash screen shows
- [ ] Test on Android 5.0 (API 21) minimum
- [ ] Test on latest Android version
- [ ] Test on different screen sizes

## Google Play Console Checklist

### ✅ App Setup (First Release Only)
- [ ] Create app in Play Console
- [ ] Select "App" (not Game)
- [ ] Select "Free" distribution
- [ ] Choose app category: Sports

### ✅ Store Listing
- [ ] App name: "Turf Time" (max 50 chars)
- [ ] Short description (max 80 chars)
- [ ] Full description (max 4000 chars)
- [ ] App icon: 512x512 PNG uploaded
- [ ] Feature graphic: 1024x500 uploaded
- [ ] Screenshots: At least 2 phone screenshots uploaded
- [ ] App category: Sports
- [ ] Tags added (soccer, coach, rotation, etc.)
- [ ] Email address for user support
- [ ] Privacy Policy URL added

### ✅ Content Rating
- [ ] Complete content rating questionnaire
- [ ] Verify rating is appropriate (likely "Everyone")
- [ ] Submit for rating

### ✅ Target Audience & Content
- [ ] Select target age group
- [ ] Declare ads policy (No ads)
- [ ] Complete Data Safety form:
  - [ ] No data collected
  - [ ] No data shared
  - [ ] All data stored locally

### ✅ App Access
- [ ] Select "All functionality is available"
- [ ] No special access needed
- [ ] No restrictions

### ✅ Release
- [ ] Upload AAB to Production track
- [ ] Add release name: "1.0.0"
- [ ] Add release notes
- [ ] Review all sections
- [ ] Submit for review

## Post-Submission Checklist

### ✅ Monitor Review
- [ ] Check email for Play Console notifications
- [ ] Respond to any reviewer questions within 24 hours
- [ ] Monitor review status in console

### ✅ After Approval
- [ ] Test app from Play Store
- [ ] Share Play Store link
- [ ] Monitor reviews and ratings
- [ ] Respond to user feedback
- [ ] Plan for updates

### ✅ Ongoing Maintenance
- [ ] Monitor crash reports in Play Console
- [ ] Track user reviews
- [ ] Plan feature updates
- [ ] Keep dependencies updated
- [ ] Test on new Android versions

## Timeline Expectations

- **Review Time**: 1-3 days (first submission may take longer)
- **Rejection**: If rejected, fix issues and resubmit
- **Updates**: Subsequent updates review faster (hours to 1 day)

## Common Rejection Reasons

1. **Missing Privacy Policy**: Ensure URL is accessible
2. **Icon Issues**: Must be 512x512, no transparency
3. **Screenshot Requirements**: Need at least 2
4. **Content Rating**: Must complete questionnaire
5. **Data Safety**: Must declare data handling

## Support Resources

- **Play Console**: https://play.google.com/console
- **Help Center**: https://support.google.com/googleplay/android-developer
- **Policy Center**: https://play.google.com/about/developer-content-policy/

---

**Remember**: Keep keystore and passwords SECURE. You cannot update your app without them!
