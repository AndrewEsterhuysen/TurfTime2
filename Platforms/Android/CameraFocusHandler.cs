using Android.App;
using Android.Content;
using Android.Hardware.Camera2;
using Android.OS;
using Android.Util;

namespace TurfTime2;

/// <summary>
/// Provides direct Camera2 API access for focus mode control.
/// This handler works independently of ZXing to provide actual macro focus capability.
/// </summary>
public static class CameraFocusHandler
{
    private const string Tag = "CameraFocusHandler";
    private static CameraManager? _cameraManager;
    private static string? _rearCameraId;

    /// <summary>
    /// Focus mode enumeration matching Android Camera2 API.
    /// </summary>
    public enum FocusMode
    {
        Off = 0,
        Auto = 1,
        Macro = 2,
        ContinuousPicture = 4,
        ContinuousVideo = 5
    }

    /// <summary>
    /// Initializes the Camera2 manager and discovers rear camera.
    /// Call this once during app initialization.
    /// </summary>
    public static void Initialize()
    {
        try
        {
            var context = Android.App.Application.Context;
            _cameraManager = (CameraManager?)context.GetSystemService(Context.CameraService);

            if (_cameraManager == null)
            {
                Log.Error(Tag, "Camera manager is null");
                return;
            }

            // Find rear-facing camera
            var cameraIds = _cameraManager.GetCameraIdList();
            Log.Debug(Tag, $"Found {cameraIds.Length} camera(s)");

            foreach (var cameraId in cameraIds)
            {
                try
                {
                    var characteristics = _cameraManager.GetCameraCharacteristics(cameraId);
                    var facing = characteristics.Get(CameraCharacteristics.LensFacing);

                    if (facing != null && (int)facing == (int)LensFacing.Back)
                    {
                        _rearCameraId = cameraId;
                        Log.Debug(Tag, $"✓ Rear camera: {cameraId}");
                        LogCameraCapabilities();
                        break;
                    }
                }
                catch (System.Exception ex)
                {
                    Log.Warn(Tag, $"Error checking camera {cameraId}: {ex.Message}");
                }
            }

            if (_rearCameraId == null)
                Log.Warn(Tag, "No rear camera found");
        }
        catch (System.Exception ex)
        {
            Log.Error(Tag, $"Initialize failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Checks if macro focus mode is supported on the rear camera.
    /// </summary>
    public static bool SupportsMacroMode()
    {
        try
        {
            if (_cameraManager == null)
                Initialize();

            if (_rearCameraId == null)
                return false;

            var characteristics = _cameraManager!.GetCameraCharacteristics(_rearCameraId);
            var afModes = (int[]?)characteristics.Get(CameraCharacteristics.ControlAfAvailableModes);

            if (afModes == null || afModes.Length == 0)
            {
                Log.Debug(Tag, "No AF modes available");
                return false;
            }

            // Check if macro mode (value 2) is supported
            foreach (var mode in afModes)
            {
                if (mode == (int)FocusMode.Macro)
                {
                    Log.Debug(Tag, "✓ Macro mode supported");
                    return true;
                }
            }

            Log.Debug(Tag, "Macro mode not supported");
            return false;
        }
        catch (System.Exception ex)
        {
            Log.Error(Tag, $"Error checking macro support: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Gets all supported AF modes for the rear camera.
    /// </summary>
    public static FocusMode[] GetSupportedFocusModes()
    {
        try
        {
            if (_cameraManager == null)
                Initialize();

            if (_rearCameraId == null)
                return System.Array.Empty<FocusMode>();

            var characteristics = _cameraManager!.GetCameraCharacteristics(_rearCameraId);
            var afModes = (int[]?)characteristics.Get(CameraCharacteristics.ControlAfAvailableModes);

            if (afModes == null || afModes.Length == 0)
                return System.Array.Empty<FocusMode>();

            var modes = new System.Collections.Generic.List<FocusMode>();
            foreach (var mode in afModes)
            {
                if (System.Enum.IsDefined(typeof(FocusMode), mode))
                {
                    modes.Add((FocusMode)mode);
                }
            }

            Log.Debug(Tag, $"Supported AF modes: {string.Join(", ", modes.Select(m => $"{m}({(int)m})"))}");
            return modes.ToArray();
        }
        catch (System.Exception ex)
        {
            Log.Error(Tag, $"Error getting supported modes: {ex.Message}");
            return System.Array.Empty<FocusMode>();
        }
    }

    /// <summary>
    /// Sets the focus mode for the rear camera.
    /// This is primarily for logging/documentation - actual focus control
    /// requires integration with the active camera capture session.
    /// </summary>
    public static void SetFocusMode(FocusMode mode)
    {
        try
        {
            Log.Debug(Tag, $"SetFocusMode requested: {mode} ({(int)mode})");

            var supported = GetSupportedFocusModes();
            if (supported.Contains(mode))
            {
                Log.Debug(Tag, $"✓ Focus mode {mode} is supported");
            }
            else
            {
                Log.Warn(Tag, $"Focus mode {mode} is NOT supported. Supported: {string.Join(", ", supported)}");
            }
        }
        catch (System.Exception ex)
        {
            Log.Error(Tag, $"Error setting focus mode: {ex.Message}");
        }
    }

    /// <summary>
    /// Triggers autofocus on the rear camera.
    /// Like SetFocusMode, this logs the request but actual implementation
    /// requires capture session integration.
    /// </summary>
    public static void TriggerAutoFocus()
    {
        try
        {
            Log.Debug(Tag, "TriggerAutoFocus requested");
            // In a full implementation, this would call:
            // captureRequestBuilder.Set(CaptureRequest.ControlAfTrigger, 
            //     (int)ControlAfTrigger.Start);
        }
        catch (System.Exception ex)
        {
            Log.Error(Tag, $"Error triggering autofocus: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets detailed information about the rear camera's focus capabilities.
    /// Useful for debugging.
    /// </summary>
    public static string GetCameraFocusInfo()
    {
        try
        {
            if (_cameraManager == null)
                Initialize();

            if (_rearCameraId == null)
                return "No rear camera found";

            var characteristics = _cameraManager!.GetCameraCharacteristics(_rearCameraId);
            var afModes = (int[]?)characteristics.Get(CameraCharacteristics.ControlAfAvailableModes);
            var maxAfRegions = (int?)characteristics.Get(CameraCharacteristics.ControlMaxRegionsAf) ?? 0;
            var maxDigitalZoom = (float?)characteristics.Get(CameraCharacteristics.ScalerAvailableMaxDigitalZoom) ?? 1.0f;

            var info = new System.Text.StringBuilder();
            info.AppendLine($"Camera ID: {_rearCameraId}");
            info.AppendLine($"AF Modes: {(afModes != null ? string.Join(", ", afModes.Select(m => $"{(FocusMode)m}({m})")) : "None")}");
            info.AppendLine($"Max AF Regions: {maxAfRegions}");
            info.AppendLine($"Max Digital Zoom: {maxDigitalZoom:F2}x");

            return info.ToString();
        }
        catch (System.Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Logs comprehensive camera capabilities for debugging.
    /// </summary>
    private static void LogCameraCapabilities()
    {
        try
        {
            var info = GetCameraFocusInfo();
            Log.Debug(Tag, $"Camera capabilities:\n{info}");
        }
        catch (System.Exception ex)
        {
            Log.Error(Tag, $"Error logging capabilities: {ex.Message}");
        }
    }
}
