using System.Diagnostics;
using System.IO.Ports;
using System.Management;
using System.Text;

namespace RAMSOfficial.Services;

/// <summary>
/// Asynchronous, non-blocking RFID reader service that listens on a serial
/// (COM) port for incoming UIDs. Most RFID readers transmit the UID as ASCII
/// text terminated by CR/LF when a card is presented.
///
/// Usage (manual port):
///   var listener = new RfidListenerService();
///   listener.RfidUidReceived += (_, uid) => { /* process uid */ };
///   await listener.StartListeningAsync("COM3");
///
/// Usage (auto-detect port):
///   var port = await RfidListenerService.FindRfidPortAsync();
///   if (port != null) await listener.StartListeningAsync(port);
///
/// For USB-HID keyboard-emulation readers the <see cref="TapInWindow"/>
/// code-behind captures keystrokes directly; this service is the
/// alternative for true serial-port / virtual-COM hardware.
/// </summary>
public sealed class RfidListenerService : IDisposable
{
    private SerialPort? _serialPort;
    private CancellationTokenSource? _cts;
    private readonly StringBuilder _lineBuffer = new();

    /// <summary>Raised on the thread-pool when a complete RFID UID line is received.</summary>
    public event EventHandler<string>? RfidUidReceived;

    /// <summary>Raised when the reader encounters a communication error.</summary>
    public event EventHandler<string>? ErrorOccurred;

    public bool IsListening { get; private set; }

    // ─────────────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Open <paramref name="portName"/> and begin reading asynchronously.
    /// The method returns immediately; data arrives via <see cref="RfidUidReceived"/>.
    /// Raises <see cref="ErrorOccurred"/> (instead of throwing) when the port
    /// does not exist.
    /// </summary>
    public Task StartListeningAsync(string portName, int baudRate = 9600)
    {
        if (IsListening)
            return Task.CompletedTask;

        var availablePorts = SerialPort.GetPortNames();
        if (!availablePorts.Contains(portName, StringComparer.OrdinalIgnoreCase))
        {
            var msg = $"Port '{portName}' not found. Available: {string.Join(", ", availablePorts)}";
            Debug.WriteLine($"[RfidListener] {msg}");
            ErrorOccurred?.Invoke(this, msg);
            return Task.CompletedTask;
        }

        _cts = new CancellationTokenSource();

        _serialPort = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = SerialPort.InfiniteTimeout,
            Encoding = Encoding.ASCII
        };

        _serialPort.Open();
        IsListening = true;

        Debug.WriteLine($"[RfidListener] Listening on {portName} @ {baudRate} baud.");

        _ = Task.Run(() => ReadLoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Scans available COM ports via WMI and returns the port name whose
    /// description best matches an RFID reader or USB-serial adapter.
    /// Falls back to the first available port when no keyword match is found.
    /// Returns <c>null</c> when no COM ports are present on this machine.
    /// </summary>
    public static async Task<string?> FindRfidPortAsync()
    {
        var portNames = SerialPort.GetPortNames();
        if (portNames.Length == 0)
        {
            Debug.WriteLine("[RfidListener] No COM ports available.");
            return null;
        }

        Debug.WriteLine($"[RfidListener] Scanning {portNames.Length} COM port(s) for RFID reader...");

        // Keywords that commonly appear in RFID reader / USB-serial adapter descriptions.
        var rfidKeywords = new[]
        {
            "RFID", "ACR", "ACM", "USB Serial", "USB-Serial",
            "CP210", "CH340", "CH341", "FTDI", "Prolific", "CDC"
        };

        try
        {
            var portDescriptions = await Task.Run(GetComPortDescriptions);

            foreach (var kw in rfidKeywords)
            {
                foreach (var (port, description) in portDescriptions)
                {
                    if (description.Contains(kw, StringComparison.OrdinalIgnoreCase))
                    {
                        Debug.WriteLine($"[RfidListener] Matched '{kw}' on {port}: {description}");
                        return port;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RfidListener] WMI port scan error: {ex.Message}");
        }

        // No keyword match — return the first available port as best guess.
        var fallback = portNames[0];
        Debug.WriteLine($"[RfidListener] No RFID keyword match — falling back to {fallback}");
        return fallback;
    }

    /// <summary>Stop listening and close the serial port.</summary>
    public void StopListening()
    {
        if (!IsListening) return;

        IsListening = false;
        _cts?.Cancel();

        try
        {
            if (_serialPort?.IsOpen == true)
                _serialPort.Close();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RfidListener] Error closing port: {ex.Message}");
        }

        _serialPort?.Dispose();
        _serialPort = null;

        Debug.WriteLine("[RfidListener] Stopped.");
    }

    public void Dispose()
    {
        StopListening();
        _cts?.Dispose();
    }

    /// <summary>Returns all COM port names currently available on this machine.</summary>
    public static string[] GetAvailablePorts() => SerialPort.GetPortNames();

    // ─────────────────────────────────────────────────────────────────────────
    // Private helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Queries WMI for friendly names of COM ports so that RFID reader ports
    /// can be identified by description (e.g. "USB Serial Port (COM3)").
    /// </summary>
    private static List<(string Port, string Description)> GetComPortDescriptions()
    {
        var results = new List<(string, string)>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, Description FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");

            foreach (ManagementObject obj in searcher.Get())
            {
                var name = obj["Name"]?.ToString() ?? string.Empty;
                var desc = obj["Description"]?.ToString() ?? string.Empty;

                // Extract "COMx" from e.g. "USB Serial Port (COM3)"
                var start = name.LastIndexOf('(');
                var end   = name.LastIndexOf(')');
                if (start >= 0 && end > start)
                {
                    var portToken = name.Substring(start + 1, end - start - 1).Trim();
                    results.Add((portToken, $"{name} - {desc}"));
                    Debug.WriteLine($"[RfidListener]   {portToken}: {name}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RfidListener] GetComPortDescriptions error: {ex.Message}");
        }
        return results;
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[256];

        try
        {
            while (!ct.IsCancellationRequested && _serialPort?.IsOpen == true)
            {
                var stream = _serialPort.BaseStream;
                int bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);

                if (bytesRead <= 0)
                    continue;

                var chunk = Encoding.ASCII.GetString(buffer, 0, bytesRead);

                foreach (var ch in chunk)
                {
                    if (ch is '\r' or '\n')
                    {
                        var uid = _lineBuffer.ToString().Trim();
                        _lineBuffer.Clear();

                        if (!string.IsNullOrEmpty(uid))
                        {
                            Debug.WriteLine($"[RfidListener] UID received: {uid}");
                            RfidUidReceived?.Invoke(this, uid);
                        }
                    }
                    else
                    {
                        _lineBuffer.Append(ch);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation — not an error.
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RfidListener] Read error: {ex.Message}");
            ErrorOccurred?.Invoke(this, ex.Message);
        }
        finally
        {
            IsListening = false;
        }
    }
}
