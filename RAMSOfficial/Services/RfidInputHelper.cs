using System.Diagnostics;
using System.Windows;

namespace RAMSOfficial.Services;

/// <summary>
/// Helper service for processing RFID card taps
/// Handles keyboard emulation from RFID readers
/// </summary>
public class RfidInputHelper
{
    private static DateTime _lastProcessTime = DateTime.MinValue;
    private const int DEBOUNCE_MS = 200; // Prevent duplicate rapid inputs

    /// <summary>
    /// Process RFID input from keyboard emulation reader
    /// Format: RFID code + ENTER key
    /// Example: "0524370791" + ENTER
    /// </summary>
    public static async Task<bool> ProcessRfidInputAsync(string rfidCode, Func<string, Task<bool>> processCallback)
    {
        if (string.IsNullOrWhiteSpace(rfidCode))
        {
            Debug.WriteLine("? RFID code is empty or null");
            return false;
        }

        // Debounce - prevent duplicate rapid inputs
        var timeSinceLastProcess = DateTime.Now - _lastProcessTime;
        if (timeSinceLastProcess.TotalMilliseconds < DEBOUNCE_MS)
        {
            Debug.WriteLine($"?? Debouncing - ignoring input (too fast: {timeSinceLastProcess.TotalMilliseconds}ms)");
            return false;
        }

        _lastProcessTime = DateTime.Now;

        try
        {
            Debug.WriteLine("???????????????????????????????????????????");
            Debug.WriteLine($"?? RFID CARD TAPPED");
            Debug.WriteLine($"   Code: {rfidCode}");
            Debug.WriteLine($"   Length: {rfidCode.Length} characters");
            Debug.WriteLine($"   Time: {DateTime.Now:HH:mm:ss.fff}");
            Debug.WriteLine("???????????????????????????????????????????");

            // Validate RFID code format
            if (!IsValidRfidCode(rfidCode))
            {
                Debug.WriteLine($"? Invalid RFID code format: {rfidCode}");
                ShowError($"Invalid RFID code format.\n\nReceived: {rfidCode}\n\nPlease contact administrator.");
                return false;
            }

            // Call the process callback
            var result = await processCallback(rfidCode);

            if (result)
            {
                Debug.WriteLine("? RFID processing successful");
            }
            else
            {
                Debug.WriteLine("?? RFID processing returned false");
            }

            return result;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? ERROR processing RFID: {ex.Message}");
            Debug.WriteLine($"   Stack: {ex.StackTrace}");
            ShowError($"Error processing RFID card:\n\n{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Validate RFID code format
    /// Typical formats: 10 digits (0524370791) or alphanumeric
    /// </summary>
    private static bool IsValidRfidCode(string rfidCode)
    {
        // Check minimum length
        if (rfidCode.Length < 1)
        {
            Debug.WriteLine($"? RFID code too short: {rfidCode.Length} characters");
            return false;
        }

        // Check maximum length (typical RFID codes are 8-14 characters)
        if (rfidCode.Length > 20)
        {
            Debug.WriteLine($"? RFID code too long: {rfidCode.Length} characters");
            return false;
        }

        // Check for valid characters (alphanumeric only)
        if (!rfidCode.All(c => char.IsLetterOrDigit(c)))
        {
            Debug.WriteLine($"? RFID code contains invalid characters");
            return false;
        }

        Debug.WriteLine($"? RFID code format valid");
        return true;
    }

    /// <summary>
    /// Show error message to user
    /// </summary>
    private static void ShowError(string message)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            MessageBox.Show(
                message,
                "RFID Reader Error",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        });
    }

    /// <summary>
    /// Log RFID attempt for debugging
    /// </summary>
    public static void LogRfidAttempt(string rfidCode, bool success, string? employeeName = null)
    {
        var status = success ? "? SUCCESS" : "? FAILED";
        var employee = employeeName ?? "Unknown";

        Debug.WriteLine("???????????????????????????????????????????");
        Debug.WriteLine($"RFID Attempt Log");
        Debug.WriteLine($"  Status: {status}");
        Debug.WriteLine($"  Code: {rfidCode}");
        Debug.WriteLine($"  Employee: {employee}");
        Debug.WriteLine($"  Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Debug.WriteLine("???????????????????????????????????????????");
    }

    /// <summary>
    /// Test if RFID reader is working
    /// Opens Notepad and waits for scan
    /// </summary>
    public static void TestRfidReader()
    {
        Debug.WriteLine("?? RFID Reader Test Mode");
        Debug.WriteLine("Instructions:");
        Debug.WriteLine("1. Notepad will open");
        Debug.WriteLine("2. Tap your RFID card");
        Debug.WriteLine("3. The code should appear in Notepad");
        Debug.WriteLine("4. Check if ENTER is automatically pressed");

        try
        {
            var notepad = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "notepad.exe",
                    UseShellExecute = true
                }
            };
            notepad.Start();

            Debug.WriteLine("? Notepad opened - ready for RFID test");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? Failed to open Notepad: {ex.Message}");
        }
    }
}
