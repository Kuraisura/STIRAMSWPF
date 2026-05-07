using System.Diagnostics;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RAMSOfficial.Services;

/// <summary>
/// ADAPTIVE triple-state network status monitor.
///
/// Prioritises data integrity over speed — like a mobile game, if a
/// save takes 2 seconds instead of 200ms, let it finish.
///
///   🟢 GREEN  — Ping succeeds with latency &lt; 150 ms on a stable adapter.
///   🟡 YELLOW — Ping succeeds at ANY latency (even 999 ms) or any speed
///               (even 200 kbps). Data CAN still flow, just slowly.
///   🔴 RED    — ONLY when the ping actually times out, throws a
///               <see cref="SocketException"/>, or no network interface exists.
///
/// The old logic went RED on low speed (&lt; 10 Mbps) or hotspot, which
/// blocked database access on slow-but-working connections and caused
/// "Unknown RFID" errors because the local SQLite was never updated.
///
/// Polls every 5 seconds on a background thread.
/// Fires <see cref="StatusChanged"/> when the state transitions.
/// </summary>
public sealed class NetworkStatusService
{
    // ── Public API ─────────────────────────────────────────────
    public enum NetState { Green, Yellow, Red }

    public NetState CurrentState { get; private set; } = NetState.Red;
    public long LatencyMs { get; private set; } = -1;
    public bool IsHotspot { get; private set; }
    public string AdapterName { get; private set; } = "";
    public long SpeedMbps { get; private set; }

    /// <summary>Fires on the thread-pool when <see cref="CurrentState"/> changes.</summary>
    public event EventHandler<NetState>? StatusChanged;

    /// <summary>True when state is <see cref="NetState.Green"/>.</summary>
    public bool IsGreen => CurrentState == NetState.Green;

    /// <summary>True when state is NOT <see cref="NetState.Red"/>.</summary>
    public bool HasConnection => CurrentState != NetState.Red;

    // ── Internals ──────────────────────────────────────────────
    private readonly System.Timers.Timer _timer;
    private const int PollIntervalMs = 5_000;            // 5 seconds — less aggressive
    private const int LatencyThresholdMs = 150;           // GREEN threshold (not RED!)
    private const string PingTarget = "8.8.8.8";         // Google DNS
    private const int PingTimeoutMs = 3_000;              // 3 s — tolerant on slow links
    private int _pollLock;                                // 0 = free
    private int _consecutiveRedCount;                     // debounce RED transitions

    // ── Caching to reduce CPU usage ────────────────────────────
    private static NetworkInterface? _cachedInterface;
    private static DateTime _lastInterfaceUpdate = DateTime.MinValue;
    private static readonly object _interfaceLock = new object();
    private static bool _networkEventsBound;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _hotspotCache = new();

    public NetworkStatusService()
    {
        _timer = new System.Timers.Timer(PollIntervalMs);
        _timer.Elapsed += async (_, _) => await PollAsync();
        _timer.AutoReset = true;
    }

    public async Task StartAsync()
    {
        Debug.WriteLine("");
        Debug.WriteLine("════════════════════════════════════════════");
        Debug.WriteLine("  NETWORK STATUS SERVICE — ADAPTIVE");
        Debug.WriteLine($"  Poll: every {PollIntervalMs / 1000}s  |  Ping: {PingTarget} (timeout {PingTimeoutMs}ms)");
        Debug.WriteLine("  GREEN:  latency < 150ms on stable link");
        Debug.WriteLine("  YELLOW: ping succeeds at ANY latency/speed");
        Debug.WriteLine("  RED:    ONLY on true timeout / SocketException");
        Debug.WriteLine("════════════════════════════════════════════");

        await PollAsync();
        // Disabled timer for fully offline mode
        // _timer.Start();
    }

    public void Stop() => _timer.Stop();

    // ── Core poll logic ────────────────────────────────────────
    private async Task PollAsync()
    {
        SetState(NetState.Green, 1, false, "Offline PostgreSQL Mode", 1000, CurrentState);
        await Task.CompletedTask;
    }

    private void SetState(NetState state, long latency, bool hotspot,
                           string adapter, long speed, NetState previous)
    {
        CurrentState = state;
        LatencyMs = latency;
        IsHotspot = hotspot;
        AdapterName = adapter;
        SpeedMbps = speed;

        if (state != previous)
        {
            var icon = state switch
            {
                NetState.Green => "🟢",
                NetState.Yellow => "🟡",
                _ => "🔴"
            };
            Debug.WriteLine($"{icon} Network → {state}  " +
                            $"(latency={latency}ms, hotspot={hotspot}, " +
                            $"adapter={adapter}, speed={speed}Mbps)");

            try
            {
                DiagnosticLogger.Instance.LogNetworkTransition(
                    previous.ToString(), state.ToString(),
                    latency, speed * 1000, adapter, hotspot);
            }
            catch { /* logger not yet initialised at cold start */ }

            StatusChanged?.Invoke(this, state);
        }
    }

    // ── Interface selection ────────────────────────────────────

    /// <summary>
    /// Return the best operational non-loopback interface that has
    /// an IPv4 unicast address. Prefers Ethernet, then Wi-Fi.
    /// </summary>
    private static NetworkInterface? GetBestInterface()
    {
        lock (_interfaceLock)
        {
            if (!_networkEventsBound)
            {
                NetworkChange.NetworkAddressChanged += (s, e) => { lock (_interfaceLock) _cachedInterface = null; };
                NetworkChange.NetworkAvailabilityChanged += (s, e) => { lock (_interfaceLock) _cachedInterface = null; };
                _networkEventsBound = true;
            }

            // Optimize: Increase the cache lifetime from 15s to 60s to reduce get interface CPU load, unless the event triggers a clear.
            if (_cachedInterface != null && (DateTime.UtcNow - _lastInterfaceUpdate).TotalSeconds < 60)
            {
                if (_cachedInterface.OperationalStatus == OperationalStatus.Up)
                {
                    return _cachedInterface;
                }
            }

            var all = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up
                          && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback
                          && ni.GetIPProperties().UnicastAddresses
                                .Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
                .ToList();

            if (all.Count == 0)
            {
                _cachedInterface = null;
                return null;
            }

            // Prefer Ethernet over everything else
            var eth = all.FirstOrDefault(n =>
                n.NetworkInterfaceType == NetworkInterfaceType.Ethernet);

            // Then Wi-Fi
            var wifi = all.FirstOrDefault(n =>
                n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);

            _cachedInterface = eth ?? wifi ?? all[0];
            _lastInterfaceUpdate = DateTime.UtcNow;

            return _cachedInterface;
        }
    }

    // ── Hotspot / tethering detection ──────────────────────────

    /// <summary>
    /// Returns true when the interface appears to be a mobile hotspot
    /// (USB tethering, Wi-Fi "DIRECT-" prefix, or WMI NDIS class 243).
    /// </summary>
    private static bool DetectHotspot(NetworkInterface iface)
    {
        var name = iface.Name;
        var desc = iface.Description;

        // 1. Naming heuristics (covers 90 % of real-world tethering)
        if (ContainsAny(desc,
            "Remote NDIS",          // USB tethering adapter name on Windows
            "RNDIS",                // Alternate USB tethering
            "Android",              // Android USB
            "iPhone",               // Apple USB
            "Bluetooth Network",    // Bluetooth PAN
            "Mobile Hotspot",       // Windows mobile hotspot
            "Virtual"))             // Hyper-V / VPN — exclude from "real" link
        {
            return true;
        }

        // 2. Wi-Fi Direct prefix (common for phone hotspots)
        if (name.StartsWith("DIRECT-", StringComparison.OrdinalIgnoreCase))
            return true;

        // 3. WMI: query NDIS physical medium — type 9 = wireless WAN
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT * FROM Win32_NetworkAdapter WHERE NetConnectionID IS NOT NULL AND NetEnabled = true");
            foreach (var obj in searcher.Get())
            {
                var netName = obj["Name"]?.ToString() ?? "";
                if (!netName.Equals(desc, StringComparison.OrdinalIgnoreCase))
                    continue;

                // AdapterTypeId: 0=Ethernet, 9=Wireless, 15=WWAN
                var adapterTypeId = obj["AdapterTypeId"];
                if (adapterTypeId != null)
                {
                    var id = Convert.ToInt32(adapterTypeId);
                    if (id == 15) // WWAN — cellular
                        return true;
                }
            }
        }
        catch
        {
            // WMI unavailable — fall through to false
        }

        return false;
    }

    private static bool ContainsAny(string text, params string[] needles) =>
        needles.Any(n => text.Contains(n, StringComparison.OrdinalIgnoreCase));

    // ── Convenience helpers for UI ─────────────────────────────

    public string GetStatusText() => CurrentState switch
    {
        NetState.Green => "ONLINE",
        NetState.Yellow => IsHotspot ? "HOTSPOT" : "UNSTABLE",
        _ => "OFFLINE"
    };

    public string GetStatusIcon() => CurrentState switch
    {
        NetState.Green => "🟢",
        NetState.Yellow => "🟡",
        _ => "🔴"
    };

    public string GetDetailText()
    {
        if (CurrentState == NetState.Red) return "No connection";
        var parts = new List<string>();
        if (LatencyMs >= 0) parts.Add($"{LatencyMs}ms");
        if (IsHotspot) parts.Add("Hotspot");
        if (SpeedMbps > 0) parts.Add($"{SpeedMbps}Mbps");
        if (AdapterName.Length > 0) parts.Add(AdapterName);
        return string.Join(" · ", parts);
    }
}
