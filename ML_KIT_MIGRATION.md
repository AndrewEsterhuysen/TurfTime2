# ML Kit Barcode Scanning Migration Plan

## Migration Steps

1. **Remove ZXing packages** from csproj
   - ZXing.Net
   - ZXing.Net.Maui
   - ZXing.Net.Maui.Controls

2. **Add ML Kit package** to csproj
   - Google.Mlkit.BarcodeScanning

3. **Update AndroidManifest.xml** for permissions and ML Kit model

4. **Rewrite QrImportPage.xaml**
   - Replace CameraBarcodeReaderView with custom camera view
   - Keep same button layout and UX

5. **Rewrite QrImportPage.xaml.cs**
   - Replace ZXing initialization with ML Kit
   - Implement ML Kit barcode detection
   - Add focus mode control using Camera2 API
   - Implement macro mode auto-activation

6. **Create MLKitCameraFocus.cs** (replaces AndroidNativeCameraFocus.cs)
   - Direct Camera2 integration
   - Actual focus mode setting (not just queries)
   - Macro mode auto-detection

7. **Testing**
   - Build for Android
   - Test on real device with close-up QR codes
   - Verify focus switches to macro when needed
   - Test QR detection works

## Key Differences from ZXing

| Aspect | ZXing | ML Kit |
|--------|-------|--------|
| API | Event-based detection | Callback-based detection |
| Camera Management | Internal black box | ML Kit camera integration |
| Focus Control | Not exposed | Accessible via Camera2 |
| Setup | Simple | More complex setup |
| Macro Mode | Cannot set | Can set automatically |

## Expected Benefits

✅ Actual macro focus mode control
✅ Auto-switch to macro for close QR codes
✅ Better performance
✅ More modern library
✅ Active Google maintenance
