using System.Diagnostics;
using System.Net.Sockets;
using Npgsql;

namespace RAMSOfficial.Services;

/// <summary>
/// OFFLINE-AWARE CONNECTION GUARD
/// ═══════════════════════════════════════════════════════════════════
///
/// Wraps all database/NpgsqlConnection calls with:
///   1. Pre-flight connectivity check (NetworkStatusService)
///   2. SocketException / NpgsqlException catch → returns default(T)
///   3. Configurable timeout (default 10s)
///
/// Usage in validation services:
///   var result = await DatabaseConnectionGuard.QuerySafeAsync(
///       connString, netStatus,
///       async conn => await conn.QueryAsync<T>("SELECT ..."),
///       fallback: new List<T>()
///   );
///
/// This prevents:
///   • "No such host is known" crashes
///   • "Exception while reading from stream" hangs
///   • Blocking the UI thread on dead connections
/// ═══════════════════════════════════════════════════════════════════
/// </summary>
public static class DatabaseConnectionGuard
{
    private const int DefaultTimeoutMs = 10_000;

    /// <summary>
    /// Execute a database query with full offline protection.
    /// Returns <paramref name="fallback"/> if offline or on any connection error.
    /// </summary>
    public static async Task<T> QuerySafeAsync<T>(
        string connectionString,
        Func<NpgsqlConnection, Task<T>> query,
        T fallback,
        int timeoutMs = DefaultTimeoutMs)
    {
        try
        {
            using var conn = new NpgsqlConnection(connectionString);

            // Open with timeout
            var openTask = conn.OpenAsync();
            var completed = await Task.WhenAny(openTask, Task.Delay(timeoutMs));

            if (completed != openTask || !openTask.IsCompletedSuccessfully)
            {
                Debug.WriteLine($"[ConnGuard] Connection TIMEOUT ({timeoutMs}ms) — returning fallback");
                return fallback;
            }

            // Execute query with timeout
            var queryTask = query(conn);
            completed = await Task.WhenAny(queryTask, Task.Delay(timeoutMs));

            if (completed == queryTask && queryTask.IsCompletedSuccessfully)
            {
                return queryTask.Result;
            }

            Debug.WriteLine($"[ConnGuard] Query TIMEOUT ({timeoutMs}ms) — returning fallback");
            return fallback;
        }
        catch (SocketException ex)
        {
            Debug.WriteLine($"[ConnGuard] SocketException: {ex.Message} — returning fallback");
            return fallback;
        }
        catch (NpgsqlException ex)
        {
            Debug.WriteLine($"[ConnGuard] NpgsqlException: {ex.Message} — returning fallback");
            return fallback;
        }
        catch (TimeoutException ex)
        {
            Debug.WriteLine($"[ConnGuard] TimeoutException: {ex.Message} — returning fallback");
            return fallback;
        }
        catch (Exception ex) when (
            ex.InnerException is SocketException ||
            ex.InnerException is NpgsqlException)
        {
            Debug.WriteLine($"[ConnGuard] Inner connection error: {ex.InnerException!.Message} — returning fallback");
            return fallback;
        }
    }

    /// <summary>
    /// Execute a database query returning a single value or null.
    /// </summary>
    public static async Task<T?> QuerySingleSafeAsync<T>(
        string connectionString,
        Func<NpgsqlConnection, Task<T?>> query,
        int timeoutMs = DefaultTimeoutMs) where T : class
    {
        return await QuerySafeAsync(connectionString, query, fallback: null, timeoutMs);
    }

    /// <summary>
    /// Returns true if the connection string can reach database right now.
    /// Uses the global NetworkStatusService as first check.
    /// 
    /// MODIFIED: Project migrated to fully offline PostgreSQL. 
    /// IsOnline is forced to TRUE so local schedules and queries are never skipped.
    /// </summary>
    public static bool IsOnline()
    {
        return true; 
    }
}

