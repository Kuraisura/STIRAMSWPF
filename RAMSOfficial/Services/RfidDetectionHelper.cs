using System.Diagnostics;
using System.Management;

namespace RAMSOfficial.Services;

/// <summary>
/// Diagnostic helper for RFID reader detection.
/// Identifies any USB HID keyboard-emulation RFID reader without requiring
/// a vendor-specific serial number — works with any brand or model.
/// </summary>
public static class RfidDetectionHelper
{
    /// <summary>
    /// Runs a full diagnostic scan and returns a detailed report.
    /// </summary>
    public static RfidDetectionReport RunDiagnostics()
    {
        var report = new RfidDetectionReport();

        Debug.WriteLine("[RfidDiag] ── RFID Reader Diagnostic Scan ──");

        try
        {
            CheckHidKeyboards(report);
            CheckUsbCompositeDevices(report);

            // A reader is considered connected when any USB HID keyboard device is found.
            report.IsReaderConnected = report.UsbHidKeyboardFound;

            PrintDiagnosticSummary(report);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RfidDiag] Error: {ex.Message}");
            report.ErrorMessage = ex.Message;
        }

        return report;
    }

    private static void CheckHidKeyboards(RfidDetectionReport report)
    {
        Debug.WriteLine("[RfidDiag] Checking HID Keyboards…");

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, Name FROM Win32_Keyboard");

            foreach (ManagementObject keyboard in searcher.Get())
            {
                var deviceId = keyboard["DeviceID"]?.ToString() ?? string.Empty;
                var name = keyboard["Name"]?.ToString() ?? string.Empty;

                report.HidKeyboardCount++;
                Debug.WriteLine($"[RfidDiag]   Keyboard: {name} | {deviceId}");

                // USB HID devices always contain VID_ and PID_ in their device path.
                if (deviceId.Contains("VID_", StringComparison.OrdinalIgnoreCase) &&
                    deviceId.Contains("PID_", StringComparison.OrdinalIgnoreCase))
                {
                    Debug.WriteLine($"[RfidDiag]   ✓ USB HID keyboard/reader detected: {name}");
                    report.UsbHidKeyboardFound = true;
                    report.DeviceName ??= name;
                    report.DeviceId ??= deviceId;
                    report.DetectionMethod ??= "HID Keyboard (VID/PID)";
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RfidDiag] CheckHidKeyboards error: {ex.Message}");
        }
    }

    private static void CheckUsbCompositeDevices(RfidDetectionReport report)
    {
        Debug.WriteLine("[RfidDiag] Checking USB Composite Devices…");

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, Name FROM Win32_PnPEntity " +
                "WHERE Name LIKE '%USB Composite%' OR Description LIKE '%USB Composite%'");

            foreach (ManagementObject device in searcher.Get())
            {
                var deviceId = device["DeviceID"]?.ToString() ?? string.Empty;
                var name = device["Name"]?.ToString() ?? string.Empty;

                Debug.WriteLine($"[RfidDiag]   Composite: {name} | {deviceId}");

                if (deviceId.Contains("VID_", StringComparison.OrdinalIgnoreCase) &&
                    deviceId.Contains("PID_", StringComparison.OrdinalIgnoreCase))
                {
                    report.UsbHidKeyboardFound = true;
                    report.DeviceName ??= name;
                    report.DeviceId ??= deviceId;
                    report.DetectionMethod ??= "USB Composite Device";
                    Debug.WriteLine($"[RfidDiag]   ✓ USB device found: {name}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RfidDiag] CheckUsbCompositeDevices error: {ex.Message}");
        }
    }

    private static void PrintDiagnosticSummary(RfidDetectionReport report)
    {
        Debug.WriteLine("[RfidDiag] ── Summary ──");
        Debug.WriteLine($"[RfidDiag]   USB HID keyboard found : {report.UsbHidKeyboardFound}");
        Debug.WriteLine($"[RfidDiag]   Total keyboards seen   : {report.HidKeyboardCount}");
        Debug.WriteLine($"[RfidDiag]   Reader connected       : {report.IsReaderConnected}");

        if (report.IsReaderConnected)
        {
            Debug.WriteLine($"[RfidDiag]   Detection method       : {report.DetectionMethod}");
            Debug.WriteLine($"[RfidDiag]   Device name            : {report.DeviceName ?? "N/A"}");
            Debug.WriteLine($"[RfidDiag]   Device ID              : {report.DeviceId ?? "N/A"}");
        }
    }
}

/// <summary>
/// Result of an <see cref="RfidDetectionHelper.RunDiagnostics"/> scan.
/// </summary>
public class RfidDetectionReport
{
    /// <summary>True when at least one USB HID keyboard-class device was found (VID_/PID_ present).</summary>
    public bool UsbHidKeyboardFound { get; set; }

    /// <summary>Total number of keyboard entries enumerated by Win32_Keyboard.</summary>
    public int HidKeyboardCount { get; set; }

    /// <summary>Overall result: true when a USB HID RFID reader (or keyboard) was detected.</summary>
    public bool IsReaderConnected { get; set; }

    public string? DetectionMethod { get; set; }
    public string? DeviceName { get; set; }
    public string? DeviceId { get; set; }
    public string? ErrorMessage { get; set; }

    public override string ToString() =>
        $"Reader={IsReaderConnected}, Method={DetectionMethod}, HidKeyboards={HidKeyboardCount}";
}
