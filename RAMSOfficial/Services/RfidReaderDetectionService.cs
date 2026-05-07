using System.Management;
using System.Diagnostics;

namespace RAMSOfficial.Services;

/// <summary>
/// Monitors USB devices and detects any connected USB HID keyboard-emulation
/// RFID reader. No vendor-specific serial number is required — any USB RFID
/// reader that presents itself as a HID keyboard will be detected.
/// </summary>
public class RfidReaderDetectionService : IDisposable
{
    private ManagementEventWatcher? _insertWatcher;
    private ManagementEventWatcher? _removeWatcher;
    private bool _isReaderConnected = false;

    public bool IsReaderConnected
    {
        get => _isReaderConnected;
        private set
        {
            if (_isReaderConnected != value)
            {
                _isReaderConnected = value;
                Debug.WriteLine($"[RfidReaderDetection] IsReaderConnected → {value}");
                ReaderConnectionChanged?.Invoke(this, value);
            }
        }
    }

    public event EventHandler<bool>? ReaderConnectionChanged;

    public RfidReaderDetectionService()
    {
        CheckForReader();
        StartMonitoring();
    }

    /// <summary>
    /// Start WMI monitoring for USB device insertions and removals.
    /// </summary>
    private void StartMonitoring()
    {
        try
        {
            var insertQuery = new WqlEventQuery(
                "SELECT * FROM __InstanceCreationEvent WITHIN 2 WHERE TargetInstance ISA 'Win32_USBHub'");
            _insertWatcher = new ManagementEventWatcher(insertQuery);
            _insertWatcher.EventArrived += (_, _) =>
            {
                Debug.WriteLine("[RfidReaderDetection] USB device inserted — re-scanning.");
                CheckForReader();
            };
            _insertWatcher.Start();

            var removeQuery = new WqlEventQuery(
                "SELECT * FROM __InstanceDeletionEvent WITHIN 2 WHERE TargetInstance ISA 'Win32_USBHub'");
            _removeWatcher = new ManagementEventWatcher(removeQuery);
            _removeWatcher.EventArrived += (_, _) =>
            {
                Debug.WriteLine("[RfidReaderDetection] USB device removed — re-scanning.");
                CheckForReader();
            };
            _removeWatcher.Start();

            Debug.WriteLine("[RfidReaderDetection] USB monitoring started.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RfidReaderDetection] Failed to start USB monitoring: {ex.Message}");
        }
    }

    /// <summary>
    /// Scans Win32_Keyboard for any USB HID device (identified by VID_/PID_ in
    /// the DeviceID). All USB RFID readers that use keyboard emulation appear here.
    /// </summary>
    public void CheckForReader()
    {
        try
        {
            bool found = false;

            using (var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, Name FROM Win32_Keyboard"))
            {
                foreach (ManagementObject device in searcher.Get())
                {
                    var deviceId = device["DeviceID"]?.ToString() ?? string.Empty;

                    // USB HID keyboards always contain VID_ and PID_ in their device path.
                    if (deviceId.Contains("VID_", StringComparison.OrdinalIgnoreCase) &&
                        deviceId.Contains("PID_", StringComparison.OrdinalIgnoreCase))
                    {
                        var name = device["Name"]?.ToString() ?? "(unknown)";
                        Debug.WriteLine($"[RfidReaderDetection] USB HID keyboard/reader: {name} | {deviceId}");
                        found = true;
                        break;
                    }
                }
            }

            IsReaderConnected = found;

            if (found)
                Debug.WriteLine("[RfidReaderDetection] RFID reader (USB HID keyboard) DETECTED.");
            else
                Debug.WriteLine("[RfidReaderDetection] No USB HID RFID reader found.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RfidReaderDetection] Error checking for reader: {ex.Message}");
            IsReaderConnected = false;
        }
    }

    /// <summary>
    /// Returns a list of all connected USB/HID devices for diagnostics.
    /// </summary>
    public List<string> GetAllUsbDevices()
    {
        var devices = new List<string>();

        try
        {
            devices.Add("=== HID KEYBOARDS ===");
            using (var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, Name, Description FROM Win32_Keyboard WHERE DeviceID LIKE '%HID%'"))
            {
                foreach (ManagementObject device in searcher.Get())
                {
                    var deviceId = device["DeviceID"]?.ToString() ?? "N/A";
                    var description = device["Description"]?.ToString() ?? "N/A";
                    var name = device["Name"]?.ToString() ?? "N/A";

                    devices.Add($"Name: {name}");
                    devices.Add($"  Description: {description}");
                    devices.Add($"  DeviceID: {deviceId}");
                    devices.Add("---");
                }
            }

            devices.Add("\n=== USB/HID DEVICES ===");
            using (var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, Name, Description FROM Win32_PnPEntity " +
                "WHERE DeviceID LIKE '%USB%' OR DeviceID LIKE '%HID%'"))
            {
                foreach (ManagementObject device in searcher.Get())
                {
                    var deviceId = device["DeviceID"]?.ToString() ?? "N/A";
                    var description = device["Description"]?.ToString() ?? "N/A";
                    var name = device["Name"]?.ToString() ?? "N/A";

                    devices.Add($"Name: {name}");
                    devices.Add($"  Description: {description}");
                    devices.Add($"  DeviceID: {deviceId}");
                    devices.Add("---");
                }
            }
        }
        catch (Exception ex)
        {
            devices.Add($"Error: {ex.Message}");
        }

        return devices;
    }

    /// <summary>
    /// Searches all connected devices for entries containing the given text.
    /// Useful for diagnosing which device is detected.
    /// </summary>
    public List<string> SearchDevices(string searchText)
    {
        var foundDevices = new List<string>();

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_PnPEntity");
            foreach (ManagementObject device in searcher.Get())
            {
                var deviceId = device["DeviceID"]?.ToString() ?? "";
                var description = device["Description"]?.ToString() ?? "";
                var name = device["Name"]?.ToString() ?? "";

                if (deviceId.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                    description.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                    name.Contains(searchText, StringComparison.OrdinalIgnoreCase))
                {
                    foundDevices.Add($"MATCH:");
                    foundDevices.Add($"   Name: {name}");
                    foundDevices.Add($"   Description: {description}");
                    foundDevices.Add($"   DeviceID: {deviceId}");
                    foundDevices.Add("---");
                }
            }
        }
        catch (Exception ex)
        {
            foundDevices.Add($"Error: {ex.Message}");
        }

        return foundDevices;
    }

    public void Dispose()
    {
        _insertWatcher?.Stop();
        _insertWatcher?.Dispose();
        _removeWatcher?.Stop();
        _removeWatcher?.Dispose();

        Debug.WriteLine("[RfidReaderDetection] USB monitoring stopped.");
    }
}
