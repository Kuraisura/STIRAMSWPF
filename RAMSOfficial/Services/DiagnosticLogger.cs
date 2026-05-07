using System.Diagnostics;
using System.IO;
using System.Collections.Concurrent;

namespace RAMSOfficial.Services;

/// <summary>
/// STRUCTURED DIAGNOSTIC LOGGER
/// ═══════════════════════════════════════════════════════════════════
///
/// Writes timestamped, structured trace lines for every RFID scan
/// and sync event to both <see cref="Debug"/> output and a rolling
/// <c>debug_log.txt</c> file in the application data directory.
///
/// Trace format per RFID scan:
///   RFID [ID] Scanned at [Timestamp].
///   Current Network State: [Latency]ms / [Speed]kbps.
///   Searching SQLite... [Found/NotFound].
///   Searching database... [Result/Error Code].
///   Sync Status: [Success/Failure Reason].
///
/// Thread-safe: uses a <see cref="ConcurrentQueue{T}"/> and a
/// background flush task so log writes never block the UI thread.
/// ═══════════════════════════════════════════════════════════════════
/// </summary>
public sealed class DiagnosticLogger : IAsyncDisposable
{
    private static DiagnosticLogger? _instance;
    private static readonly object _lock = new();

    /// <summary>Singleton accessor. Call <see cref="Initialize"/> first.</summary>
    public static DiagnosticLogger Instance
    {
        get
        {
            if (_instance == null)
                Initialize();
            return _instance!;
        }
    }

    private readonly string _logFilePath;
    private readonly ConcurrentQueue<string> _queue = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _flushTask;
    private const int FlushIntervalMs = 500;
    private const long MaxFileSizeBytes = 5 * 1024 * 1024; // 5 MB rolling limit

    private DiagnosticLogger()
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "STI_Attendance");
        Directory.CreateDirectory(appData);
        _logFilePath = Path.Combine(appData, "debug_log.txt");

        _flushTask = FlushLoopAsync(_cts.Token);

        Log("DIAG", "DiagnosticLogger started");
        Log("DIAG", $"Log file: {_logFilePath}");
    }

    public static void Initialize()
    {
        lock (_lock)
        {
            _instance ??= new DiagnosticLogger();
        }
    }

    // ════════════════════════════════════════════════════════════
    //  PUBLIC API
    // ════════════════════════════════════════════════════════════

    /// <summary>Write a generic diagnostic line.</summary>
    public void Log(string tag, string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{tag}] {message}";
        Debug.WriteLine(line);
        _queue.Enqueue(line);
    }

    /// <summary>
    /// Log a complete RFID scan trace with structured fields.
    /// Call this from the RFID processing pipeline.
    /// </summary>
    public void LogRfidScan(RfidScanTrace trace)
    {
        var sep = new string('─', 60);
        Log("RFID", sep);
        Log("RFID", $"RFID [{trace.RfidCode}] Scanned at [{trace.Timestamp:yyyy-MM-dd HH:mm:ss.fff}].");
        Log("RFID", $"Current Network State: {trace.NetState} — {trace.LatencyMs}ms / {trace.SpeedKbps}kbps — Adapter: {trace.AdapterName}");
        Log("RFID", $"Searching RAM... [{(trace.RamHit ? "Found" : "NotFound")}].");
        Log("RFID", $"Searching SQLite... [{(trace.SqliteHit ? "Found" : "NotFound")}].");

        if (trace.databaseQueried)
        {
            if (trace.databaseHit)
                Log("RFID", $"Searching database... [Found → upserted to SQLite+RAM].");
            else if (trace.databaseError != null)
                Log("RFID", $"Searching database... [Error: {trace.databaseError}].");
            else if (trace.databaseTimedOut)
                Log("RFID", $"Searching database... [Timeout after {trace.databaseTimeoutMs}ms].");
            else
                Log("RFID", $"Searching database... [NotFound — RFID not registered].");
        }
        else
        {
            Log("RFID", $"Searching database... [Skipped — {trace.databaseSkipReason}].");
        }

        var result = trace.EmployeeName ?? "UNKNOWN";
        Log("RFID", $"Result: {result} — Total lookup: {trace.TotalMs}ms.");
        Log("RFID", sep);
    }

    /// <summary>Log a sync pulse event.</summary>
    public void LogSyncPulse(string phase, int employees, int schedules,
                              int ghosts, int pending, long durationMs, string? error = null)
    {
        if (error != null)
        {
            Log("SYNC", $"Sync {phase} FAILED after {durationMs}ms — {error}");
        }
        else
        {
            Log("SYNC", $"Sync {phase} OK — emp={employees} sched={schedules} " +
                         $"ghosts={ghosts} pending={pending} ({durationMs}ms)");
        }
    }

    /// <summary>Log a network state transition.</summary>
    public void LogNetworkTransition(string from, string to, long latencyMs,
                                      long speedKbps, string adapter, bool hotspot)
    {
        Log("NET", $"Network {from} → {to} — latency={latencyMs}ms speed={speedKbps}kbps " +
                    $"adapter={adapter} hotspot={hotspot}");
    }

    // ════════════════════════════════════════════════════════════
    //  BACKGROUND FLUSH
    // ════════════════════════════════════════════════════════════

    private async Task FlushLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(FlushIntervalMs, ct);
            }
            catch (OperationCanceledException) { break; }

            FlushQueue();
        }

        // Final flush on shutdown
        FlushQueue();
    }

    private void FlushQueue()
    {
        if (_queue.IsEmpty) return;

        try
        {
            // Rolling file size check
            if (File.Exists(_logFilePath))
            {
                var fi = new FileInfo(_logFilePath);
                if (fi.Length > MaxFileSizeBytes)
                {
                    var backup = _logFilePath + ".old";
                    if (File.Exists(backup)) File.Delete(backup);
                    File.Move(_logFilePath, backup);
                }
            }

            using var writer = new StreamWriter(_logFilePath, append: true);
            while (_queue.TryDequeue(out var line))
            {
                writer.WriteLine(line);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DiagnosticLogger] Flush error: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        await _flushTask.ConfigureAwait(false);
        FlushQueue();
        _cts.Dispose();
    }
}

/// <summary>
/// Structured trace data for a single RFID scan lookup.
/// Built incrementally as the lookup progresses through
/// RAM → SQLite → database tiers.
/// </summary>
public sealed class RfidScanTrace
{
    public string RfidCode { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.Now;

    // Network snapshot
    public string NetState { get; set; } = "Unknown";
    public long LatencyMs { get; set; } = -1;
    public long SpeedKbps { get; set; }
    public string AdapterName { get; set; } = "";

    // Lookup results
    public bool RamHit { get; set; }
    public bool SqliteHit { get; set; }
    public bool databaseQueried { get; set; }
    public bool databaseHit { get; set; }
    public bool databaseTimedOut { get; set; }
    public int databaseTimeoutMs { get; set; }
    public string? databaseError { get; set; }
    public string? databaseSkipReason { get; set; }

    // Outcome
    public string? EmployeeName { get; set; }
    public long TotalMs { get; set; }
}
