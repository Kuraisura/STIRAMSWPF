using System.Diagnostics;
using System.Management;

namespace RAMSOfficial.Services;

/// <summary>
/// Detects any USB HID keyboard-emulation RFID reader connected to this machine.
///
/// How it works:
///   USB RFID readers that use keyboard emulation enumerate in Windows as
///   Win32_Keyboard entries whose DeviceID contains USB VID/PID tokens
///   (e.g. "HID\VID_08FF&amp;PID_0009\…"). Built-in laptop/desktop keyboards
///   typically appear under ACPI, I2C or PS/2 paths that do NOT contain
///   VID_ or PID_ — so this check works safely across hardware configurations
///   without requiring any vendor-specific serial number.
///
/// Event model:
///   WMI PnP-device arrival/removal events trigger a re-scan within ~2 s of
///   any USB device being plugged or unplugged. App.xaml.cs also polls every
///   2 s as a safety-net fallback.
/// </summary>
public sealed class SimpleRfidDetectionService : IDisposable
{
    private volatile bool _isReaderConnected;
    private bool _disposed;
    private ManagementEventWatcher? _insertWatcher;
    private ManagementEventWatcher? _removeWatcher;

    public event EventHandler<bool>? ReaderConnectionChanged;

    public bool IsReaderConnected
    {
        get => _isReaderConnected;
        private set
        {
            if (_isReaderConnected == value) return;
            _isReaderConnected = value;
            Debug.WriteLine($"[RfidDetection] Reader connected: {value}");
            OnReaderConnectionChanged(value);
        }
    }

    public SimpleRfidDetectionService()
    {
        StartUsbMonitoring();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Synchronously checks whether any USB HID RFID reader (or keyboard-class
    /// USB device) is currently connected. Updates <see cref="IsReaderConnected"/>
    /// and returns the result.
    /// </summary>
    public bool CheckForReaderSync()
    {
        try
        {
            IsReaderConnected = DetectUsbHidKeyboard();
            return IsReaderConnected;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RfidDetection] CheckForReaderSync error: {ex.Message}");
            IsReaderConnected = false;
            return false;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Core detection
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true when at least one Win32_Keyboard entry contains USB VID/PID
    /// tokens — the universal signature of a USB HID keyboard-class device,
    /// which includes keyboard-emulation RFID readers from any vendor.
    /// </summary>
    private static bool DetectUsbHidKeyboard()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, Name FROM Win32_Keyboard");

            foreach (ManagementObject obj in searcher.Get())
            {
                var deviceId = obj["DeviceID"]?.ToString() ?? string.Empty;

                // USB HID devices always embed VID_ (Vendor ID) and PID_ (Product ID)
                // in their device path; built-in keyboards (ACPI / I2C / PS/2) do not.
                if (deviceId.Contains("VID_", StringComparison.OrdinalIgnoreCase) &&
                    deviceId.Contains("PID_", StringComparison.OrdinalIgnoreCase))
                {
                    var name = obj["Name"]?.ToString() ?? "(unknown)";
                    Debug.WriteLine($"[RfidDetection] USB HID keyboard/reader found: {name} | {deviceId}");
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RfidDetection] WMI query error: {ex.Message}");
        }

        Debug.WriteLine("[RfidDetection] No USB HID keyboard or RFID reader detected.");
        return false;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // USB plug/unplug monitoring
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Subscribes to WMI PnP-device arrival and removal events so that RFID
    /// reader connection/disconnection is detected within ~2 seconds.
    /// </summary>
    private void StartUsbMonitoring()
    {
        try
        {
            const string insertWql =
                "SELECT * FROM __InstanceCreationEvent WITHIN 2 " +
                "WHERE TargetInstance ISA 'Win32_PnPEntity'";
            _insertWatcher = new ManagementEventWatcher(insertWql);
            _insertWatcher.EventArrived += OnUsbDeviceChanged;
            _insertWatcher.Start();

            const string removeWql =
                "SELECT * FROM __InstanceDeletionEvent WITHIN 2 " +
                "WHERE TargetInstance ISA 'Win32_PnPEntity'";
            _removeWatcher = new ManagementEventWatcher(removeWql);
            _removeWatcher.EventArrived += OnUsbDeviceChanged;
            _removeWatcher.Start();

            Debug.WriteLine("[RfidDetection] USB plug/unplug monitoring active.");
        }
        catch (Exception ex)
        {
            // WMI monitoring is best-effort; App.xaml.cs polls every 2 s as fallback.
            Debug.WriteLine($"[RfidDetection] Cannot start USB monitoring: {ex.Message}");
        }
    }

    private void OnUsbDeviceChanged(object sender, EventArrivedEventArgs e)
    {
        Debug.WriteLine("[RfidDetection] USB device change detected — re-scanning.");
        CheckForReaderSync();
    }

    private void OnReaderConnectionChanged(bool isConnected)
    {
        try
        {
            ReaderConnectionChanged?.Invoke(this, isConnected);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RfidDetection] Event dispatch error: {ex.Message}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Dispose
    // ─────────────────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _insertWatcher?.Stop();
            _insertWatcher?.Dispose();
            _removeWatcher?.Stop();
            _removeWatcher?.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RfidDetection] Dispose error: {ex.Message}");
        }

        Debug.WriteLine("[RfidDetection] Disposed.");
    }
}
