using System.Diagnostics;
using System.IO;
using System.Timers;
using Microsoft.Data.Sqlite;
using Npgsql;
using Dapper;
using RAMSOfficial.Helpers;
using RAMSOfficial.Models;

namespace RAMSOfficial.Services;

/// <summary>
/// Hybrid (Offline/Online) Attendance Sync Service for RFID tap-in
/// at STI College Santa Rosa.
///
/// ═══════════════════════════════════════════════════════════════════
///
///   CONNECTION LOGIC
///     Uses <c>AppDomain.CurrentDomain.BaseDirectory + "Database/STIRAMS.db"</c>
///     so the path resolves correctly after the app is installed.
///
///   HYBRID 'SAVE' METHOD — <see cref="RecordAttendanceAsync"/>
///     Step 1 — Save to local SQLite immediately.
///     Step 2 — Attempt to push to database (PostgreSQL).
///     Step 3 — If database succeeds, update is_synced = true locally.
///
///   BACKGROUND SYNC ENGINE
///     A 30-second timer drains unsynced records (is_synced = false)
///     and pushes them to database, ensuring no tap is ever lost.
///
///   PRIMARY KEYS
///     Uses GUIDs (<c>TEXT</c> in SQLite, <c>UUID</c> in database) so
///     offline-created entries never collide with online ones.
///
///   NuGet packages required:
///     • Microsoft.Data.Sqlite  (8.0.0)
///     • Npgsql                 (8.0.5)
///     • Dapper                 (2.1.28)
///
/// ═══════════════════════════════════════════════════════════════════
/// </summary>
public sealed class HybridAttendanceSyncService : IDisposable
{
    // ── Connection strings ──
    private readonly string _localConnectionString;
    private readonly string _databaseConnectionString;

    // ── Background sync ──
    private readonly System.Timers.Timer _syncTimer;
    private volatile bool _isSyncing;
    private DateTime _lastMetadataPullUtc = DateTime.MinValue;

    private const int SyncIntervalMs = 1_000;    // 1 second
    private const int SyncBatchSize = 20;         // records per cycle
    private const int MaxSyncAttempts = 10;       // give up after 10 failures

    /// <summary>Fires when the pending (unsynced) count changes.</summary>
    public event EventHandler<int>? PendingCountChanged;

    /// <summary>Fires after each sync cycle with (synced, failed) counts.</summary>
    public event EventHandler<(int Synced, int Failed)>? SyncCycleCompleted;

    /// <summary>Fires when online/offline status changes.</summary>
    public event EventHandler<bool>? ConnectivityChanged;

    /// <summary>UTC timestamp of the last successful sync.</summary>
    public DateTime? LastSyncUtc { get; private set; }

    /// <summary>True when the system has any network connectivity.</summary>
    public bool IsOnline => DatabaseConnectionGuard.IsOnline();

    // ═══════════════════ Construction & Initialization ═══════════════════

    public HybridAttendanceSyncService()
    {
        // ── 1. STIRAMS.db: stored in AppData so it survives project rebuilds.
        //    Every build would overwrite the file in bin\Debug\... resetting all data.
        //    AppData is persistent, just like rams_offline.db (LocalDatabaseService).
        var appDataFolder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dbFolder = Path.Combine(appDataFolder, "STI_Attendance", "Database");
        Directory.CreateDirectory(dbFolder);

        var dbPath = Path.Combine(dbFolder, "STIRAMS.db");

        // One-time seed: if no AppData STIRAMS.db yet, copy the full-schema template
        // from the build-output directory (which carries the database schema export).
        var templatePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Database", "STIRAMS.db");
        if (!File.Exists(dbPath) && File.Exists(templatePath))
        {
            File.Copy(templatePath, dbPath, overwrite: false);
            Debug.WriteLine($"[HybridSync] ✓ Seeded STIRAMS.db from template → {dbPath}");
        }

        _localConnectionString = $"Data Source={dbPath}";

        // ── 2. database connection (reuse the existing DatabaseService config) ──
        _databaseConnectionString = new DatabaseService().GetConnectionString();

        // ── 3. Background sync timer ──
        _syncTimer = new System.Timers.Timer(SyncIntervalMs);
        _syncTimer.Elapsed += async (_, _) => await SyncPendingRecordsAsync();
        _syncTimer.AutoReset = true;

        Debug.WriteLine("");
        Debug.WriteLine("═══════════════════════════════════════════════════════");
        Debug.WriteLine("  HYBRID ATTENDANCE SYNC SERVICE");
        Debug.WriteLine($"  STIRAMS.db : {dbPath}");
        Debug.WriteLine($"  ↑ Open THIS path in DBeaver to see live attendance data");
        Debug.WriteLine($"  Sync       : every {SyncIntervalMs / 1000}s  |  batch {SyncBatchSize}");
        Debug.WriteLine("═══════════════════════════════════════════════════════");
    }

    /// <summary>
    /// Returns a local tap summary for a specific employee/date from STIRAMS.db.
    /// Combines both <c>offline_attendance</c> and mirrored <c>attendance_logs</c>
    /// so offline toggle decisions stay correct after online→offline transitions.
    /// </summary>
    public async Task<(bool HasTimeIn, bool HasTimeOut, string LastTapType)> GetTapSummaryForDateAsync(int employeeId, DateTime date)
    {
        try
        {
            var dateStr = date.ToString("yyyy-MM-dd");
            using var conn = new SqliteConnection(_localConnectionString);
            await conn.OpenAsync();

            var logs = (await conn.QueryAsync<string>(@"
                SELECT log_type FROM (
                    SELECT log_type, log_time
                    FROM offline_attendance
                    WHERE employee_id = @EmpId
                      AND substr(date, 1, 10) = @Date
                      AND is_synced <> -1
                    UNION ALL
                    SELECT log_type, log_time
                    FROM attendance_logs
                    WHERE employee_id = @EmpId
                      AND substr(date, 1, 10) = @Date
                ) t
                ORDER BY log_time ASC
            ", new { EmpId = employeeId, Date = dateStr })).ToList();

            var hasIn = logs.Any(l => string.Equals(l, "IN", StringComparison.OrdinalIgnoreCase));
            var hasOut = logs.Any(l => string.Equals(l, "OUT", StringComparison.OrdinalIgnoreCase));
            var last = logs.LastOrDefault() ?? string.Empty;

            return (hasIn, hasOut, last);
        }
        catch (Exception ex)
        {
            await DbLog("WARN", "LOCAL_STATE", $"GetTapSummaryForDate failed: {ex.Message}");
            return (false, false, string.Empty);
        }
    }

    /// <summary>
    /// Full startup sequence:
    ///   1. Create schema (offline_attendance, employee_tap_status, local_holidays, debug_logs)
    ///   2. Repair empty/corrupt timestamps in STIRAMS.db (prevents PG 22007 errors)
    ///   3. Master Pull — warm ScheduleCache from HybridDatabase if available
    ///   4. Migrate stuck pending records from rams_offline.db → STIRAMS.db
    ///   5. Pull metadata (holidays, today's attendance) if online
    ///   6. Start the 1-second background sync timer
    /// </summary>
    public async Task InitializeAsync()
    {
        await EnsureSchemaAsync();

        // ── Repair any empty/corrupt timestamps in STIRAMS.db ──
        await RepairEmptyTimestampsAsync();

        // ── Quarantine stuck max-attempt records so they stop blocking the queue ──
        await QuarantineMaxAttemptRecordsAsync();

        // ── Master Pull: warm ScheduleCache from HybridDatabase ──
        try
        {
            if (App.HybridDatabase != null)
            {
                var dbPath = App.HybridDatabase.GetDatabasePath();
                if (!string.IsNullOrEmpty(dbPath))
                {
                    await App.ScheduleCache.RefreshFromLocalDbAsync($"Data Source={dbPath}");
                    await DbLog("INFO", "INIT", $"RAM cache warmed: {App.ScheduleCache.CachedRfidCount} RFIDs");
                }
            }
        }
        catch (Exception ex)
        {
            await DbLog("WARN", "INIT", $"RAM cache warm failed (non-fatal): {ex.Message}");
        }

        // ── Migrate stuck pending records from rams_offline.db ──
        await MigrateStuckPendingRecordsAsync();

        // ── Initial metadata pull (holidays, attendance) if online ──
        if (IsOnline)
        {
            try { await PullMetadataAsync(); }
            catch (Exception ex) { await DbLog("WARN", "INIT", $"Initial pull failed: {ex.Message}"); }
        }

        // ── Start background sync engine ──
        _syncTimer.Start();

        // ── Monitor connectivity changes ──
        if (App.HybridDatabase?.NetStatus != null)
        {
            App.HybridDatabase.NetStatus.StatusChanged += async (_, newState) =>
            {
                var online = newState != NetworkStatusService.NetState.Red;
                ConnectivityChanged?.Invoke(this, online);
                if (online)
                {
                    await DbLog("INFO", "NET", "Connectivity restored — triggering sync");
                    _ = ForceSyncAsync();
                }
            };
        }

        var pending = await GetPendingCountAsync();
        await DbLog("INFO", "INIT", $"Initialized — sync timer started, {pending} pending records");
    }

    /// <summary>
    /// One-time migration: copies any stuck rows from rams_offline.db → pending_attendance
    /// into STIRAMS.db → offline_attendance so they enter the working sync pipeline.
    /// </summary>
    private async Task MigrateStuckPendingRecordsAsync()
    {
        try
        {
            var localDb = new LocalDatabaseService();
            var legacyPath = Path.GetFullPath(localDb.GetDatabasePath());
            var stiramsPath = Path.GetFullPath(GetDatabasePath());

            // Single-DB mode: LocalDatabaseService now points to STIRAMS.db too.
            // In this case, legacy->sovereign migration must be skipped to avoid
            // re-inserting the same pending rows on every app startup.
            if (string.Equals(legacyPath, stiramsPath, StringComparison.OrdinalIgnoreCase))
            {
                await DbLog("INFO", "MIGRATE", "Skipped legacy pending migration (single STIRAMS.db mode)");
                return;
            }

            var legacyConnStr = $"Data Source={legacyPath}";

            using var legacyConn = new SqliteConnection(legacyConnStr);
            await legacyConn.OpenAsync();

            // Check if pending_attendance table exists
            var tableExists = await legacyConn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='pending_attendance'");
            if (tableExists == 0) return;

            var stuck = (await legacyConn.QueryAsync<dynamic>(@"
                SELECT employee_id, rfid_code, log_time, log_type, date,
                       COALESCE(attendance_status, 'present') as attendance_status,
                       is_late, is_early_out, late_minutes, undertime_minutes,
                       notes, schedule_id, created_at
                FROM pending_attendance
                WHERE sync_status = 1
                ORDER BY created_at ASC
            ")).ToList();

            if (stuck.Count == 0) return;

            await DbLog("INFO", "MIGRATE", $"Found {stuck.Count} stuck records in rams_offline.db");

            using var localConn = new SqliteConnection(_localConnectionString);
            await localConn.OpenAsync();

            int migrated = 0;
            foreach (var r in stuck)
            {
                var guid = Guid.NewGuid().ToString();
                try
                {
                    // Validate timestamps before migrating — empty/corrupt dates
                    // will cause 22007 errors when the sync engine pushes to database.
                    var rawLogTime = (string?)r.log_time;
                    var rawDate = (string?)r.date;
                    var rawCreatedAt = (string?)r.created_at;

                    var parsedLogTime = DataSanitizer.ParseTimestamp(rawLogTime);
                    var parsedDate = DataSanitizer.ParseDate(rawDate);

                    if (parsedLogTime is null || parsedDate is null)
                    {
                        await DbLog("WARN", "MIGRATE",
                            $"Skipping record with empty timestamp: log_time='{rawLogTime}', date='{rawDate}', emp={r.employee_id}");
                        continue;
                    }

                    // Ensure created_at has a value; fall back to log_time
                    var safeCreatedAt = DataSanitizer.ParseTimestamp(rawCreatedAt) ?? parsedLogTime.Value;

                    await localConn.ExecuteAsync(@"
                        INSERT OR IGNORE INTO offline_attendance (
                            id, employee_id, rfid_code, log_time, log_type, date,
                            attendance_status, is_late, is_early_out, late_minutes,
                            undertime_minutes, notes, schedule_id,
                            is_synced, created_at
                        ) VALUES (
                            @Id, @EmpId, @Rfid, @LogTime, @LogType, @Date,
                            @Status, @IsLate, @IsEarlyOut, @LateMins, @Undertime,
                            @Notes, @SchedId, 0, @Created
                        )
                    ", new
                    {
                        Id = guid,
                        EmpId = (int)(long)r.employee_id,
                        Rfid = (string?)r.rfid_code ?? "",
                        LogTime = DataSanitizer.FormatTimestamp(parsedLogTime.Value),
                        LogType = DataSanitizer.NormalizeLogType((string?)r.log_type),
                        Date = DataSanitizer.FormatDate(parsedDate.Value),
                        Status = DataSanitizer.NormalizeAttendanceStatus((string?)r.attendance_status),
                        IsLate = (int)(long)r.is_late,
                        IsEarlyOut = (int)(long)r.is_early_out,
                        LateMins = r.late_minutes != null ? (int?)(long)r.late_minutes : null,
                        Undertime = r.undertime_minutes != null ? (int?)(long)r.undertime_minutes : null,
                        Notes = (string?)r.notes,
                        SchedId = r.schedule_id != null ? (long?)r.schedule_id : null,
                        Created = DataSanitizer.FormatTimestamp(safeCreatedAt)
                    });
                    migrated++;
                }
                catch (Exception ex)
                {
                    await DbLog("WARN", "MIGRATE", $"Failed to migrate record: {ex.Message}");
                }
            }

            // Mark migrated records as synced in the legacy database to prevent re-migration
            if (migrated > 0)
            {
                await legacyConn.ExecuteAsync(@"
                    UPDATE pending_attendance SET sync_status = 0
                    WHERE sync_status = 1
                ");
            }

            await DbLog("INFO", "MIGRATE", $"Migrated {migrated}/{stuck.Count} stuck records to STIRAMS.db");
        }
        catch (Exception ex)
        {
            await DbLog("WARN", "MIGRATE", $"Migration scan failed (non-fatal): {ex.Message}");
        }
    }

    /// <summary>
    /// Startup data-hygiene pass: scans <c>offline_attendance</c> in STIRAMS.db
    /// for rows where <c>log_time</c>, <c>date</c>, or <c>created_at</c> is
    /// NULL or an empty string. These rows cause PostgreSQL error
    /// <c>22007: invalid input syntax for type timestamp: ""</c> when the
    /// sync engine tries to push them.
    ///
    /// For each bad row:
    ///   • If <c>created_at</c> is parseable, use it as the fallback time.
    ///   • Otherwise, use <c>DateTime.Now</c>.
    ///   • Updates the row in-place so it can sync normally.
    /// </summary>
    private async Task RepairEmptyTimestampsAsync()
    {
        try
        {
            using var conn = new SqliteConnection(_localConnectionString);
            await conn.OpenAsync();

            // Find rows with empty or null timestamps that are still pending sync
            var broken = (await conn.QueryAsync<dynamic>(@"
                SELECT id, log_time, date, created_at
                FROM offline_attendance
                WHERE is_synced = 0
                  AND (
                    log_time IS NULL OR log_time = '' OR
                    date IS NULL OR date = '' OR
                    created_at IS NULL OR created_at = ''
                  )
            ")).ToList();

            if (broken.Count == 0) return;

            await DbLog("WARN", "REPAIR", $"Found {broken.Count} row(s) with empty timestamps — repairing");

            var nowStr = DataSanitizer.FormatTimestamp(DateTime.Now);
            var todayStr = DataSanitizer.FormatDate(DateTime.Now);
            int repaired = 0;

            foreach (var row in broken)
            {
                var id = (string)row.id;
                var rawLogTime = (string?)row.log_time;
                var rawDate = (string?)row.date;
                var rawCreated = (string?)row.created_at;

                // Try to recover a valid time from whichever column has one
                var anyGoodTime = DataSanitizer.ParseTimestamp(rawLogTime)
                              ?? DataSanitizer.ParseTimestamp(rawCreated)
                              ?? DateTime.Now;

                var fixedLogTime = !string.IsNullOrWhiteSpace(rawLogTime) && DataSanitizer.ParseTimestamp(rawLogTime) != null
                    ? rawLogTime!
                    : DataSanitizer.FormatTimestamp(anyGoodTime);

                var fixedDate = !string.IsNullOrWhiteSpace(rawDate) && DataSanitizer.ParseDate(rawDate) != null
                    ? rawDate!
                    : DataSanitizer.FormatDate(anyGoodTime);

                var fixedCreated = !string.IsNullOrWhiteSpace(rawCreated) && DataSanitizer.ParseTimestamp(rawCreated) != null
                    ? rawCreated!
                    : DataSanitizer.FormatTimestamp(anyGoodTime);

                await conn.ExecuteAsync(@"
                    UPDATE offline_attendance
                    SET log_time   = @LogTime,
                        date       = @Date,
                        created_at = @Created,
                        updated_at = @Now
                    WHERE id = @Id
                ", new
                {
                    Id = id,
                    LogTime = fixedLogTime,
                    Date = fixedDate,
                    Created = fixedCreated,
                    Now = nowStr
                });

                repaired++;
                Debug.WriteLine($"[HybridSync] REPAIRED {id}: log_time='{rawLogTime}'→'{fixedLogTime}', date='{rawDate}'→'{fixedDate}'");
            }

            await DbLog("INFO", "REPAIR", $"Repaired {repaired}/{broken.Count} row(s) with empty timestamps");
        }
        catch (Exception ex)
        {
            await DbLog("WARN", "REPAIR", $"Timestamp repair failed (non-fatal): {ex.Message}");
        }
    }

    /// <summary>
    /// Startup pass: permanently quarantines any records that have exceeded
    /// <see cref="MaxSyncAttempts"/> but were never flagged as <c>is_synced = -1</c>.
    /// These are the "3 pending" ghost records that block the queue forever.
    /// Sets <c>is_synced = -1</c> so they are excluded from:
    ///   • The sync engine's pending query
    ///   • The pending count badge
    ///   • The toggle logic
    /// </summary>
    private async Task QuarantineMaxAttemptRecordsAsync()
    {
        try
        {
            using var conn = new SqliteConnection(_localConnectionString);
            await conn.OpenAsync();

            var quarantined = await conn.ExecuteAsync(@"
                UPDATE offline_attendance
                SET is_synced       = -1,
                    last_sync_error = COALESCE(last_sync_error, '') || ' | QUARANTINED at startup (max attempts reached)',
                    updated_at      = @Now
                WHERE is_synced = 0
                  AND sync_attempts >= @MaxAttempts
            ", new
            {
                Now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                MaxAttempts = MaxSyncAttempts
            });

            if (quarantined > 0)
            {
                Debug.WriteLine($"[HybridSync] QUARANTINED {quarantined} stuck record(s) at startup (exceeded {MaxSyncAttempts} attempts)");
                await DbLog("INFO", "INIT", $"Quarantined {quarantined} stuck record(s) that exceeded max sync attempts");
            }
        }
        catch (Exception ex)
        {
            await DbLog("WARN", "INIT", $"Startup quarantine failed (non-fatal): {ex.Message}");
        }
    }

    // ═══════════════════ SCHEMA ═══════════════════

    /// <summary>
    /// Creates all sovereign tables in STIRAMS.db:
    ///   • <c>attendance_logs</c>  — full database-schema replica (populated on every tap)
    ///   • <c>offline_attendance</c> — GUID-keyed sync-tracking table
    ///   • <c>employee_tap_status</c> — fast IN/OUT toggle tracking
    ///   • <c>local_holidays</c> — holiday metadata mirror
    ///   • <c>debug_logs</c> — structured diagnostic log table
    /// </summary>
    private async Task EnsureSchemaAsync()
    {
        using var conn = new SqliteConnection(_localConnectionString);
        await conn.OpenAsync();

        // Enable WAL mode so concurrent readers/writers don't block each other.
        // The background sync timer and tap saves can now run without SQLITE_BUSY errors.
        await conn.ExecuteAsync("PRAGMA journal_mode=WAL;");
        await conn.ExecuteAsync("PRAGMA synchronous=NORMAL;");

        // ── attendance_logs: full database-schema replica ──
        // Written on every tap so DBeaver / offline queries always have real data.
        await conn.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS attendance_logs (
                log_id            INTEGER PRIMARY KEY,
                employee_id       INTEGER NOT NULL,
                rfid_code         TEXT    NOT NULL,
                log_time          TEXT    NOT NULL,
                log_type          TEXT    NOT NULL,
                date              TEXT    NOT NULL,
                attendance_status TEXT,
                is_late           INTEGER DEFAULT 0,
                is_early_out      INTEGER DEFAULT 0,
                late_minutes      INTEGER DEFAULT 0,
                undertime_minutes INTEGER DEFAULT 0,
                notes             TEXT,
                verified_by       INTEGER,
                verified_at       TEXT,
                is_admin_time     INTEGER DEFAULT 0,
                admin_time_reason TEXT,
                admin_time_status TEXT,
                is_holiday        INTEGER DEFAULT 0,
                is_suspended      INTEGER DEFAULT 0,
                is_online_class   INTEGER DEFAULT 0,
                schedule_id       INTEGER,
                term_id           INTEGER,
                created_at        TEXT
            );
        ");

        // Unique guard: one row per employee per day per tap direction.
        // INSERT OR REPLACE uses this to swap the offline placeholder (auto-assigned
        // log_id) with the real database log_id transparently.
        try
        {
            await conn.ExecuteAsync(@"
                CREATE UNIQUE INDEX IF NOT EXISTS idx_att_emp_date_type
                    ON attendance_logs(employee_id, substr(date, 1, 10), log_type);
            ");
        }
        catch (SqliteException) { /* index already exists */ }

        await conn.ExecuteAsync(@"
            CREATE INDEX IF NOT EXISTS idx_att_date     ON attendance_logs(date);
            CREATE INDEX IF NOT EXISTS idx_att_employee ON attendance_logs(employee_id);
        ");

        await conn.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS offline_attendance (
                id              TEXT PRIMARY KEY,          -- GUID, generated client-side
                employee_id     INTEGER NOT NULL,
                rfid_code       TEXT    NOT NULL,
                log_time        TEXT    NOT NULL,
                log_type        TEXT    NOT NULL,          -- 'IN' or 'OUT'
                date            TEXT    NOT NULL,
                attendance_status TEXT,
                is_late         INTEGER DEFAULT 0,
                is_early_out    INTEGER DEFAULT 0,
                late_minutes    INTEGER DEFAULT 0,
                undertime_minutes INTEGER DEFAULT 0,
                notes           TEXT,
                schedule_id     INTEGER,
                term_id         INTEGER,

                -- Sync state
                is_synced       INTEGER DEFAULT 0,        -- 0 = pending, 1 = synced
                database_log_id INTEGER,                  -- filled after successful push
                sync_attempts   INTEGER DEFAULT 0,
                last_sync_error TEXT,
                synced_at       TEXT,

                -- Timestamps
                created_at      TEXT NOT NULL,
                updated_at      TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_offline_att_synced
                ON offline_attendance(is_synced);
            CREATE INDEX IF NOT EXISTS idx_offline_att_employee
                ON offline_attendance(employee_id);
            CREATE INDEX IF NOT EXISTS idx_offline_att_date
                ON offline_attendance(date);
        ");

        // Clean existing duplicates before creating uniqueness guard
        await conn.ExecuteAsync(@"
            DELETE FROM offline_attendance
            WHERE rowid NOT IN (
                SELECT MIN(rowid)
                FROM offline_attendance
                WHERE is_synced <> -1
                GROUP BY employee_id, substr(date, 1, 10), UPPER(log_type)
            )
              AND is_synced <> -1;
        ");

        try
        {
            await conn.ExecuteAsync(@"
                CREATE UNIQUE INDEX IF NOT EXISTS idx_offline_att_one_per_day_type
                    ON offline_attendance(employee_id, substr(date, 1, 10), UPPER(log_type))
                    WHERE is_synced <> -1;
            ");
        }
        catch (SqliteException ex)
        {
            Debug.WriteLine($"[HybridSync] WARN: unique offline_attendance index skipped: {ex.Message}");
        }

        // ── Employee tap status: fast O(1) toggle tracking ──
        await conn.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS employee_tap_status (
                employee_id   INTEGER PRIMARY KEY,
                last_tap_type TEXT    NOT NULL DEFAULT 'OUT',
                last_tap_time TEXT    NOT NULL,
                updated_at    TEXT    NOT NULL
            );
        ");

        // ── Local holidays: offline holiday validation ──
        await conn.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS local_holidays (
                holiday_id    INTEGER PRIMARY KEY,
                holiday_name  TEXT    NOT NULL,
                holiday_date  TEXT    NOT NULL,
                start_date    TEXT,
                end_date      TEXT,
                holiday_type  TEXT,
                description   TEXT,
                is_active     INTEGER DEFAULT 1,
                synced_at     TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_local_holidays_date
                ON local_holidays(holiday_date);
        ");

        // ── Debug logs: structured diagnostics in SQLite ──
        await conn.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS debug_logs (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp  TEXT    NOT NULL,
                level      TEXT    NOT NULL,
                tag        TEXT    NOT NULL,
                message    TEXT    NOT NULL,
                context    TEXT,
                created_at TEXT    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_debug_logs_ts
                ON debug_logs(timestamp);
        ");

        // ── Self-Healing: add missing columns to existing tables ──
        // SQLite doesn't support IF NOT EXISTS for ALTER TABLE, so we
        // catch the "duplicate column" error and move on.
        var migrations = new[]
        {
            "ALTER TABLE offline_attendance ADD COLUMN updated_at TEXT",
            "ALTER TABLE offline_attendance ADD COLUMN sync_attempts INTEGER DEFAULT 0",
            "ALTER TABLE offline_attendance ADD COLUMN last_sync_error TEXT",
            "ALTER TABLE offline_attendance ADD COLUMN database_log_id INTEGER",
            "ALTER TABLE offline_attendance ADD COLUMN synced_at TEXT",
            "ALTER TABLE offline_attendance ADD COLUMN term_id INTEGER"
        };

        foreach (var ddl in migrations)
        {
            try { await conn.ExecuteAsync(ddl); }
            catch (SqliteException) { /* column already exists — safe to ignore */ }
        }

        // ── Self-healing for attendance_logs ──────────────────────────────────
        // The database-exported STIRAMS.db may lack columns added by the app
        // (schedule_id, term_id, is_admin_time, etc.). ALTER TABLE ADD COLUMN is
        // idempotent here because we catch the duplicate-column SqliteException.
        var attendanceLogsMigrations = new[]
        {
            "ALTER TABLE attendance_logs ADD COLUMN schedule_id       INTEGER",
            "ALTER TABLE attendance_logs ADD COLUMN term_id           INTEGER",
            "ALTER TABLE attendance_logs ADD COLUMN is_admin_time     INTEGER DEFAULT 0",
            "ALTER TABLE attendance_logs ADD COLUMN admin_time_reason TEXT",
            "ALTER TABLE attendance_logs ADD COLUMN admin_time_status TEXT",
            "ALTER TABLE attendance_logs ADD COLUMN is_holiday        INTEGER DEFAULT 0",
            "ALTER TABLE attendance_logs ADD COLUMN is_suspended      INTEGER DEFAULT 0",
            "ALTER TABLE attendance_logs ADD COLUMN is_online_class   INTEGER DEFAULT 0",
            "ALTER TABLE attendance_logs ADD COLUMN undertime_minutes INTEGER DEFAULT 0",
            "ALTER TABLE attendance_logs ADD COLUMN late_minutes      INTEGER DEFAULT 0"
        };
        foreach (var ddl in attendanceLogsMigrations)
        {
            try { await conn.ExecuteAsync(ddl); }
            catch (SqliteException) { /* column already exists */ }
        }

        Debug.WriteLine("[HybridSync] Schema verified (all sovereign tables ready + self-healed).");
    }

    // ═══════════════════ CORE: RecordAttendance ═══════════════════

    /// <summary>
    /// Hybrid save — the single entry-point for recording an attendance tap.
    ///
    /// <list type="number">
    ///   <item><description>
    ///     Step 1 — Save to local SQLite immediately (never fails unless disk is full).
    ///   </description></item>
    ///   <item><description>
    ///     Step 2 — Attempt to push the record to database.
    ///   </description></item>
    ///   <item><description>
    ///     Step 3 — If database succeeds, update the local row: <c>is_synced = 1</c>,
    ///     store the returned <c>log_id</c>.
    ///   </description></item>
    /// </list>
    ///
    /// If Step 2 fails (timeout, no network, etc.) the record stays local
    /// with <c>is_synced = 0</c> and the background sync engine will retry.
    /// </summary>
    public async Task<AttendanceSaveResult> RecordAttendanceAsync(AttendanceLog log)
    {
        var guid = Guid.NewGuid().ToString();
        var now = DateTime.Now;
        var result = new AttendanceSaveResult { Id = guid };

        // ─── Timestamp Normalization: never save empty/default timestamps ───
        if (log.LogTime == default)
        {
            Debug.WriteLine($"[LocalState] WARNING: log.LogTime was default — normalizing to DateTime.Now");
            log.LogTime = now;
        }
        if (log.Date == default)
        {
            Debug.WriteLine($"[LocalState] WARNING: log.Date was default — normalizing to today");
            log.Date = now.Date;
        }
        Debug.WriteLine($"[LocalState] RecordAttendance: emp={log.EmployeeId}, logTime='{log.LogTime:yyyy-MM-dd HH:mm:ss}', date='{log.Date:yyyy-MM-dd}', type='{log.LogType}'");

        // ─── Pre-check: reject duplicate log_type for same employee+date ───
        try
        {
            using var checkConn = new SqliteConnection(_localConnectionString);
            await checkConn.OpenAsync();

            var dateStr = log.Date.ToString("yyyy-MM-dd");
            var logType = DataSanitizer.NormalizeLogType(log.LogType);

            var existingCount = await checkConn.ExecuteScalarAsync<int>(@"
                SELECT COUNT(*) FROM offline_attendance
                WHERE employee_id = @EmpId
                  AND substr(date, 1, 10) = @Today
                  AND log_type = @LogType
                  AND is_synced <> -1
            ", new { EmpId = log.EmployeeId, Today = dateStr, LogType = logType });

            if (existingCount > 0)
            {
                Debug.WriteLine($"[LocalState] DUPLICATE BLOCKED: {logType} already exists for emp {log.EmployeeId} on {dateStr}");
                result.ErrorMessage = $"Duplicate Tap Detected — a {(logType == "IN" ? "Time In" : "Time Out")} record already exists for {dateStr}.";
                return result;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HybridSync] Duplicate pre-check failed (non-fatal): {ex.Message}");
        }

        // ─── Step 1: Save to local SQLite ───
        try
        {
            using var localConn = new SqliteConnection(_localConnectionString);
            await localConn.OpenAsync();

            await localConn.ExecuteAsync(@"
                INSERT INTO offline_attendance (
                    id, employee_id, rfid_code, log_time, log_type, date,
                    attendance_status, is_late, is_early_out, late_minutes,
                    undertime_minutes, notes, schedule_id, term_id,
                    is_synced, created_at
                ) VALUES (
                    @Id, @EmployeeId, @RfidCode, @LogTime, @LogType, @Date,
                    @AttendanceStatus, @IsLate, @IsEarlyOut, @LateMinutes,
                    @UndertimeMinutes, @Notes, @ScheduleId, @TermId,
                    0, @CreatedAt
                )
            ", new
            {
                Id = guid,
                log.EmployeeId,
                log.RfidCode,
                LogTime = log.LogTime.ToString("yyyy-MM-dd HH:mm:ss"),
                LogType = DataSanitizer.NormalizeLogType(log.LogType),
                Date = log.Date.ToString("yyyy-MM-dd"),
                AttendanceStatus = DataSanitizer.NormalizeAttendanceStatus(log.AttendanceStatus),
                IsLate = (log.IsLate ?? false) ? 1 : 0,
                IsEarlyOut = (log.IsEarlyOut ?? false) ? 1 : 0,
                LateMinutes = log.LateMinutes ?? 0,
                UndertimeMinutes = log.UndertimeMinutes ?? 0,
                log.Notes,
                log.ScheduleId,
                log.TermId,
                CreatedAt = now.ToString("yyyy-MM-dd HH:mm:ss")
            });

            result.SavedLocally = true;
            Debug.WriteLine($"[HybridSync] Step 1 ✅ Local save OK  (guid={guid})");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HybridSync] Step 1 ❌ Local save FAILED: {ex.Message}");
            result.ErrorMessage = $"Local save failed: {ex.Message}";
            return result;
        }

        // ─── Step 2: Attempt database push (5s timeout to protect UI thread) ───
        try
        {
            if (!DatabaseConnectionGuard.IsOnline())
            {
                Debug.WriteLine("[HybridSync] Step 2 ⏭ OFFLINE — skipping database push.");
                await NotifyPendingCountAsync();
                return result;
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var supaConn = new NpgsqlConnection(_databaseConnectionString);
            await supaConn.OpenAsync(cts.Token);

            var logId = await supaConn.ExecuteScalarAsync<int>(new CommandDefinition(@"
                INSERT INTO attendance_logs (
                    employee_id, rfid_code, log_time, log_type, date,
                    attendance_status, is_late, is_early_out, late_minutes,
                    undertime_minutes, notes, schedule_id, created_at
                ) VALUES (
                    @EmployeeId, @RfidCode, @LogTime::timestamp, @LogType, @Date::date,
                    @AttendanceStatus, @IsLate, @IsEarlyOut, @LateMinutes,
                    @UndertimeMinutes, @Notes, @ScheduleId, NOW()
                )
                RETURNING log_id
            ", new
            {
                log.EmployeeId,
                log.RfidCode,
                LogTime = log.LogTime,
                LogType = DataSanitizer.NormalizeLogType(log.LogType),
                Date = log.Date,
                AttendanceStatus = DataSanitizer.NormalizeAttendanceStatus(log.AttendanceStatus),
                IsLate = log.IsLate ?? false,
                IsEarlyOut = log.IsEarlyOut ?? false,
                LateMinutes = log.LateMinutes ?? 0,
                UndertimeMinutes = log.UndertimeMinutes ?? 0,
                log.Notes,
                log.ScheduleId
            }, cancellationToken: cts.Token));

            result.SavedOnline = true;
            result.databaseLogId = logId;
            Debug.WriteLine($"[HybridSync] Step 2 ✅ database push OK  (log_id={logId})");

            // ─── Step 3: Mark local row as synced ───
            await MarkAsSyncedAsync(guid, logId);
            Debug.WriteLine($"[HybridSync] Step 3 ✅ Local row marked is_synced=1");
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine("[HybridSync] Step 2 ⏱ database push timed out (5s) — will retry via background sync.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HybridSync] Step 2 ❌ database push failed (will retry): {ex.Message}");
            // Record stays is_synced = 0 — the background engine will retry
        }

        await NotifyPendingCountAsync();
        return result;
    }

    // ═══════════════════ SOVEREIGN: ProcessTapAsync ═══════════════════

    /// <summary>
    /// Sovereign offline-first tap processing pipeline.
    ///
    /// <list type="number">
    ///   <item>Look up employee (RAM cache → local_employees in rams_offline.db → database)</item>
    ///   <item>Check holidays using <c>local_holidays</c> in STIRAMS.db (fail-open)</item>
    ///   <item>Determine IN/OUT from <c>employee_tap_status</c> + <c>offline_attendance</c></item>
    ///   <item>Atomic SQL transaction: INSERT attendance + UPDATE tap status</item>
    ///   <item>If online, push to database immediately</item>
    ///   <item>Log every step to <c>debug_logs</c></item>
    /// </list>
    ///
    /// This method applies the same business rules as the online version
    /// but reads ALL data from local SQLite, ensuring it works fully offline.
    /// </summary>
    public async Task<SovereignTapResult> ProcessTapAsync(string rfidCode)
    {
        var sw = Stopwatch.StartNew();
        var now = DateTime.Now;
        var dateStr = now.ToString("yyyy-MM-dd");
        var nowStr = now.ToString("yyyy-MM-dd HH:mm:ss");

        await DbLog("INFO", "TAP", $"ProcessTap START — RFID={rfidCode}");

        // ── 1. Employee lookup ──────────────────────────────────
        Employee? employee = null;

        // Priority 1: RAM cache (sub-1ms)
        if (App.ScheduleCache != null)
            employee = App.ScheduleCache.GetEmployeeByRfid(rfidCode);

        // Priority 2: LocalDatabaseService (rams_offline.db, sub-10ms)
        if (employee == null)
        {
            try
            {
                var localDb = new LocalDatabaseService();
                employee = await localDb.GetEmployeeByRfidAsync(rfidCode);
            }
            catch (Exception ex)
            {
                await DbLog("WARN", "TAP", $"LocalDB lookup failed: {ex.Message}");
            }
        }

        // Priority 3: database (only when online)
        if (employee == null && DatabaseConnectionGuard.IsOnline())
        {
            try
            {
                var onlineDb = new DatabaseService();
                employee = await onlineDb.GetEmployeeByRfidAsync(rfidCode);
                if (employee != null)
                    await DbLog("INFO", "TAP", $"Employee found via database: {employee.FullName}");
            }
            catch (Exception ex)
            {
                await DbLog("WARN", "TAP", $"database lookup failed: {ex.Message}");
            }
        }

        if (employee == null)
        {
            await DbLog("WARN", "TAP", $"Employee NOT FOUND for RFID={rfidCode}");
            return SovereignTapResult.Fail("Employee not found. This card is not registered.");
        }

        // If local/RAM entry has no photo, try enriching from PostgreSQL while online.
        // This keeps kiosk photos updated even when local cache is stale.
        if (DatabaseConnectionGuard.IsOnline() && string.IsNullOrWhiteSpace(employee.PhotoPath))
        {
            try
            {
                var onlineDb = new DatabaseService();
                var onlineEmployee = await onlineDb.GetEmployeeByRfidAsync(rfidCode);
                if (onlineEmployee != null && !string.IsNullOrWhiteSpace(onlineEmployee.PhotoPath))
                {
                    employee.PhotoPath = onlineEmployee.PhotoPath;

                    // Best-effort cache refresh so next taps resolve the photo offline too.
                    App.ScheduleCache?.CacheEmployee(employee);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var localDb = new LocalDatabaseService();
                            await localDb.UpsertEmployeeAsync(employee);
                        }
                        catch { /* non-critical */ }
                    });

                    await DbLog("INFO", "TAP", "Employee photo path enriched from PostgreSQL");
                }
            }
            catch (Exception ex)
            {
                await DbLog("WARN", "TAP", $"Photo-path enrichment failed: {ex.Message}");
            }
        }

        await DbLog("INFO", "TAP", $"Employee: {employee.FullName} (ID={employee.EmployeeId})");

        // ── 2. Holiday check (local_holidays in STIRAMS.db) ─────
        var holidayBlock = await CheckLocalHolidayAsync(employee, now);
        if (holidayBlock != null)
        {
            await DbLog("INFO", "TAP", $"BLOCKED by holiday: {holidayBlock}");
            return SovereignTapResult.Fail($"Holiday: {holidayBlock}");
        }

        // ── 3. Toggle logic (ALWAYS local) ──────────────────────
        var nextTapType = await GetNextTapTypeLocalAsync(employee.EmployeeId, dateStr);
        await DbLog("INFO", "TAP", $"Next tap type: {nextTapType}");

        // ── 3b. Block if attendance is already complete ─────
        if (nextTapType == "COMPLETED")
        {
            await DbLog("INFO", "TAP", $"BLOCKED — attendance already complete for {employee.FullName} on {dateStr}");
            return SovereignTapResult.Fail("Attendance already completed for today (Time In and Time Out recorded).");
        }

        // ── 3c. 5-Minute Cooldown (Anti-Accidental Tap) ─────
        // "Only applies to the employee that logs in already" (Next action would be OUT)
        if (nextTapType == "OUT")
        {
            try
            {
                using var checkConn = new SqliteConnection(_localConnectionString);
                await checkConn.OpenAsync();

                var lastLogTimeStr = await checkConn.ExecuteScalarAsync<string>(@"
                    SELECT log_time FROM offline_attendance
                    WHERE employee_id = @EmpId AND substr(date, 1, 10) = @Today AND log_type = 'IN'
                    ORDER BY log_time DESC LIMIT 1
                ", new { EmpId = employee.EmployeeId, Today = dateStr });

                var lastLogTime = DataSanitizer.ParseTimestamp(lastLogTimeStr);
                if (lastLogTime.HasValue)
                {
                    var timeSinceLastTap = now - lastLogTime.Value;
                    if (timeSinceLastTap.TotalMinutes < 5)
                    {
                        var remaining = TimeSpan.FromMinutes(5) - timeSinceLastTap;
                        var remainingStr = remaining.Minutes > 0 
                            ? $"{remaining.Minutes}m {remaining.Seconds}s" 
                            : $"{remaining.Seconds}s";
                        var msg = $"Please wait {remainingStr} before tapping out to prevent accidental double-scanning.";
                        await DbLog("INFO", "TAP", $"BLOCKED — 5-minute cooldown active for {employee.FullName}. {msg}");
                        return SovereignTapResult.Fail(msg);
                    }
                }
            }
            catch (Exception ex)
            {
                await DbLog("WARN", "TAP", $"Cooldown check failed (non-fatal): {ex.Message}");
            }
        }

        // ── 4. Duplicate Shield (auto-correcting) ────────────────────
        // If the toggle logic returned IN but an IN already exists in the
        // pending queue, auto-correct this tap to OUT instead of blocking.
        // This prevents two TIME INs for the same employee on the same day.
        try
        {
            using var guardConn = new SqliteConnection(_localConnectionString);
            await guardConn.OpenAsync();

            var existingCount = await guardConn.ExecuteScalarAsync<int>(@"
                SELECT COUNT(*) FROM offline_attendance
                WHERE employee_id = @EmpId
                  AND substr(date, 1, 10) = @Today
                  AND log_type = @LogType
                  AND is_synced <> -1
            ", new { EmpId = employee.EmployeeId, Today = dateStr, LogType = nextTapType });

            if (existingCount > 0)
            {
                if (nextTapType == "IN")
                {
                    // ── Auto-correct: IN already exists → force this tap to OUT ──
                    Debug.WriteLine($"[LocalState] Employee {employee.EmployeeId} — Duplicate Shield: IN already exists. Auto-correcting to OUT.");
                    await DbLog("INFO", "TAP",
                        $"DUPLICATE SHIELD — IN exists for emp {employee.EmployeeId} on {dateStr}. Forcing tap to OUT.");
                    nextTapType = "OUT";

                    // Check if OUT also exists (attendance already complete)
                    var existingOut = await guardConn.ExecuteScalarAsync<int>(@"
                        SELECT COUNT(*) FROM offline_attendance
                        WHERE employee_id = @EmpId
                          AND substr(date, 1, 10) = @Today
                          AND log_type = 'OUT'
                          AND is_synced <> -1
                    ", new { EmpId = employee.EmployeeId, Today = dateStr });

                    if (existingOut > 0)
                    {
                        await DbLog("INFO", "TAP",
                            $"BLOCKED — both IN and OUT already exist for emp {employee.EmployeeId} on {dateStr}");
                        return SovereignTapResult.Fail(
                            "Attendance already completed for today (Time In and Time Out recorded).");
                    }
                }
                else
                {
                    // OUT already exists — attendance complete
                    await DbLog("WARN", "TAP",
                        $"DUPLICATE BLOCKED — {nextTapType} already exists for emp {employee.EmployeeId} on {dateStr}");
                    return SovereignTapResult.Fail(
                        $"Duplicate Tap Detected — a {(nextTapType == "IN" ? "Time In" : "Time Out")} record already exists for today.");
                }
            }
        }
        catch (Exception ex)
        {
            // Non-fatal — the atomic INSERT below will still succeed
            await DbLog("WARN", "TAP", $"Duplicate guard check failed (non-fatal): {ex.Message}");
        }

        // ── 5. Atomic save to STIRAMS.db ────────────────────────
        var guid = Guid.NewGuid().ToString();
        var statusResult = nextTapType == "IN"
            ? StatusCalculationHelper.CalculateTimeInStatus(
                now.TimeOfDay,
                employee.ScheduleTimeIn,
                "Regular",
                "Regular work hours")
            : StatusCalculationHelper.CalculateTimeOutStatus(
                now.TimeOfDay,
                employee.ScheduleTimeOut,
                "Regular",
                "Regular work hours");

        var normalizedStatus = DataSanitizer.NormalizeAttendanceStatus(statusResult.Status);

        try
        {
            using var conn = new SqliteConnection(_localConnectionString);
            await conn.OpenAsync();

            using var txn = conn.BeginTransaction();
            try
            {
                // 4a. INSERT into offline_attendance
                var inserted = await conn.ExecuteAsync(@"
                    INSERT OR IGNORE INTO offline_attendance (
                        id, employee_id, rfid_code, log_time, log_type, date,
                        attendance_status, is_late, is_early_out, late_minutes,
                        undertime_minutes, notes, schedule_id, term_id,
                        is_synced, created_at
                    ) VALUES (
                        @Id, @EmpId, @Rfid, @LogTime, @LogType, @Date,
                        @Status, @IsLate, @IsEarlyOut, @LateMinutes,
                        @UndertimeMinutes, @Notes, @ScheduleId, @TermId,
                        0, @CreatedAt
                    )
                ", new
                {
                    Id = guid,
                    EmpId = employee.EmployeeId,
                    Rfid = employee.RfidCode,
                    LogTime = nowStr,
                    LogType = DataSanitizer.NormalizeLogType(nextTapType),
                    Date = dateStr,
                    Status = normalizedStatus,
                    IsLate = statusResult.IsLate ? 1 : 0,
                    IsEarlyOut = statusResult.IsEarlyOut ? 1 : 0,
                    LateMinutes = statusResult.LateMinutes ?? 0,
                    UndertimeMinutes = statusResult.UndertimeMinutes ?? 0,
                    Notes = statusResult.Notes,
                    ScheduleId = (long?)null,
                    TermId = (long?)null,
                    CreatedAt = nowStr
                }, txn);

                if (inserted == 0)
                {
                    txn.Rollback();
                    await DbLog("INFO", "TAP", $"Duplicate tap ignored for emp={employee.EmployeeId}, type={nextTapType}, date={dateStr}");
                    return SovereignTapResult.Fail("Duplicate tap detected for this schedule. Please wait.");
                }

                // 4b. UPDATE employee_tap_status (atomic toggle guarantee)
                await conn.ExecuteAsync(@"
                    INSERT OR REPLACE INTO employee_tap_status (
                        employee_id, last_tap_type, last_tap_time, updated_at
                    ) VALUES (
                        @EmpId, @TapType, @TapTime, @UpdatedAt
                    )
                ", new
                {
                    EmpId = employee.EmployeeId,
                    TapType = nextTapType,
                    TapTime = nowStr,
                    UpdatedAt = nowStr
                }, txn);

                txn.Commit();
                await DbLog("INFO", "TAP", $"✅ Local save OK (guid={guid}, type={nextTapType})");

                // Mirror to attendance_logs immediately so STIRAMS.db shows data in DBeaver
                // even before database sync (no log_id yet → INSERT OR IGNORE).
                _ = Task.Run(async () => await MirrorToAttendanceLogsAsync(
                    null, employee.EmployeeId, employee.RfidCode,
                    now, nextTapType, normalizedStatus,
                    statusResult.IsLate, statusResult.IsEarlyOut,
                    statusResult.LateMinutes, statusResult.UndertimeMinutes,
                    statusResult.Notes));
            }
            catch
            {
                txn.Rollback();
                throw;
            }
        }
        catch (Exception ex)
        {
            // Log the EXACT SQL error to the console — this is the persistence failure diagnostic
            Debug.WriteLine($"[HybridSync] ❌ CRITICAL: Local SQLite save FAILED for emp={employee.EmployeeId}");
            Debug.WriteLine($"[HybridSync]    Exception: {ex.GetType().Name}: {ex.Message}");
            if (ex is Microsoft.Data.Sqlite.SqliteException sqlEx)
                Debug.WriteLine($"[HybridSync]    SQLite Error Code: {sqlEx.SqliteErrorCode}, Extended: {sqlEx.SqliteExtendedErrorCode}");
            Debug.WriteLine($"[HybridSync]    Stack: {ex.StackTrace}");
            await DbLog("ERROR", "TAP", $"❌ CRITICAL — Local save FAILED: {ex.GetType().Name}: {ex.Message}",
                $"RFID={rfidCode}, EmpId={employee.EmployeeId}, guid={guid}");
            return SovereignTapResult.Fail($"Failed to save tap: {ex.Message}");
        }

        // ── 5. If online, push to database (best-effort, 5s timeout) ───────
        // Runs with a CancellationToken so a SocketException / network drop
        // cannot freeze the WPF UI thread beyond 5 seconds.
        bool savedOnline = false;
        if (DatabaseConnectionGuard.IsOnline())
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var supaConn = new NpgsqlConnection(_databaseConnectionString);
                await supaConn.OpenAsync(cts.Token);

                var logId = await supaConn.ExecuteScalarAsync<int>(new CommandDefinition(@"
                    INSERT INTO attendance_logs (
                        employee_id, rfid_code, log_time, log_type, date,
                        attendance_status, is_late, is_early_out, late_minutes,
                        undertime_minutes, notes, schedule_id, term_id, created_at
                    ) VALUES (
                        @EmpId, @Rfid, @LogTime::timestamp, @LogType, @Date::date,
                        @Status, false, false, 0, 0, null, null, null, NOW()
                    )
                    RETURNING log_id
                ", new
                {
                    EmpId = employee.EmployeeId,
                    Rfid = employee.RfidCode,
                    LogTime = now,
                    LogType = DataSanitizer.NormalizeLogType(nextTapType),
                    Date = now.Date,
                    Status = normalizedStatus,
                    IsLate = statusResult.IsLate,
                    IsEarlyOut = statusResult.IsEarlyOut,
                    LateMinutes = statusResult.LateMinutes ?? 0,
                    UndertimeMinutes = statusResult.UndertimeMinutes ?? 0
                }, cancellationToken: cts.Token));

                if (logId > 0)
                {
                    await MarkAsSyncedAsync(guid, logId);
                    // Replace the offline placeholder row with the real database log_id
                    await MirrorToAttendanceLogsAsync(
                        logId, employee.EmployeeId, employee.RfidCode,
                        now, nextTapType, normalizedStatus,
                        statusResult.IsLate, statusResult.IsEarlyOut,
                        statusResult.LateMinutes, statusResult.UndertimeMinutes,
                        statusResult.Notes);
                    savedOnline = true;
                    await DbLog("INFO", "TAP", $"✅ database push OK (log_id={logId})");
                }
            }
            catch (OperationCanceledException)
            {
                await DbLog("WARN", "TAP", "database push timed out (5s) — will retry via background sync");
            }
            catch (Exception ex)
            {
                await DbLog("WARN", "TAP", $"database push failed (will retry): {ex.Message}");
            }
        }

        await NotifyPendingCountAsync();

        sw.Stop();
        await DbLog("INFO", "TAP",
            $"ProcessTap END — {sw.ElapsedMilliseconds}ms, online={savedOnline}, type={nextTapType}");

        // ── 6. Query overview data for graceful UI transition ───────
        string? timeInTimestamp = null;
        string? timeOutTimestamp = null;
        try
        {
            using var overviewConn = new SqliteConnection(_localConnectionString);
            await overviewConn.OpenAsync();

            if (nextTapType == "OUT")
            {
                // Retrieve the IN record so the UI can show both times
                timeInTimestamp = await overviewConn.QueryFirstOrDefaultAsync<string>(@"
                    SELECT log_time FROM offline_attendance
                    WHERE employee_id = @EmpId AND substr(date, 1, 10) = @Today AND log_type = 'IN'
                    ORDER BY log_time ASC LIMIT 1
                ", new { EmpId = employee.EmployeeId, Today = dateStr });
                timeOutTimestamp = nowStr;
                Debug.WriteLine($"[LocalState] Overview data: TimeIn={timeInTimestamp}, TimeOut={timeOutTimestamp}");
            }
            else if (nextTapType == "IN")
            {
                timeInTimestamp = nowStr;
            }
        }
        catch { /* overview data is non-critical */ }

        return new SovereignTapResult
        {
            Success = true,
            EmployeeNumericId = employee.EmployeeId,
            EmployeeName = employee.FullName,
            EmployeeId = employee.SchoolId ?? employee.EmployeeId.ToString(),
            Department = employee.Department ?? "—",
            TapDirection = nextTapType,
            Timestamp = now,
            SavedOnline = savedOnline,
            SavedLocally = true,
            IsQueued = !savedOnline,
            PhotoPath = employee.PhotoPath,
            LocalGuid = guid,
            TimeInTimestamp = timeInTimestamp,
            TimeOutTimestamp = timeOutTimestamp
        };
    }

    // ═══════════════════ Atomic Last-Action Check (Public API) ═══════════════════

    /// <summary>
    /// Atomic 'Last Action' check by RFID code.
    /// Queries the local SQLite database (STIRAMS.db) for the most recent log
    /// for this RFID today. Returns the last action and what the next action should be.
    ///
    /// <list type="bullet">
    ///   <item>If no records today → NextAction = "IN"</item>
    ///   <item>If last log was TIME_IN → NextAction = "OUT"</item>
    ///   <item>If last log was TIME_OUT → NextAction = "COMPLETED" (show overview screen)</item>
    /// </list>
    /// </summary>
    public async Task<LocalStatusResult> GetLatestLocalStatusAsync(string rfid)
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var result = new LocalStatusResult { Rfid = rfid, Date = today };

        try
        {
            using var conn = new SqliteConnection(_localConnectionString);
            await conn.OpenAsync();

            var lastLog = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT log_type, log_time, employee_id
                FROM offline_attendance
                WHERE rfid_code = @Rfid AND substr(date, 1, 10) = @Today
                ORDER BY log_time DESC
                LIMIT 1
            ", new { Rfid = rfid, Today = today });

            if (lastLog == null)
            {
                result.LastAction = null;
                result.NextAction = "IN";
                Debug.WriteLine($"[LocalState] RFID {rfid} — no records today. Next action: TIME_IN.");
                return result;
            }

            var lastType = ((string?)lastLog.log_type)?.ToUpperInvariant();
            result.EmployeeId = (int)(long)lastLog.employee_id;
            result.LastAction = lastType;
            result.LastActionTime = (string?)lastLog.log_time;

            if (string.Equals(lastType, "IN", StringComparison.OrdinalIgnoreCase))
            {
                result.NextAction = "OUT";
                Debug.WriteLine($"[LocalState] Employee {result.EmployeeId} last status: TIME_IN. Next action: TIME_OUT.");
            }
            else if (string.Equals(lastType, "OUT", StringComparison.OrdinalIgnoreCase))
            {
                result.NextAction = "COMPLETED";
                result.IsCompleted = true;
                Debug.WriteLine($"[LocalState] Employee {result.EmployeeId} last status: TIME_OUT. Attendance COMPLETED.");
            }
            else
            {
                result.NextAction = "IN";
                Debug.WriteLine($"[LocalState] Employee {result.EmployeeId} last status: {lastType ?? "UNKNOWN"}. Next action: TIME_IN.");
            }

            return result;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LocalState] ERROR querying status for RFID {rfid}: {ex.Message}");
            await DbLog("ERROR", "LOCAL_STATE", $"GetLatestLocalStatus failed for RFID {rfid}: {ex.Message}");
            result.NextAction = "IN";
            result.Error = ex.Message;
            return result;
        }
    }

    // ═══════════════════ State Mirror (Local-First) ═══════════════════

    /// <summary>
    /// Local-first "State Mirror" for an employee's attendance status today.
    /// ALWAYS queries STIRAMS.db first — never touches the network.
    /// Provides both Time In and Time Out timestamps for UI overview.
    ///
    /// <code>
    /// SELECT log_type FROM offline_attendance
    /// WHERE employee_id = @id AND date = @today
    /// ORDER BY log_time DESC LIMIT 1
    /// </code>
    ///
    /// Rules:
    ///   • If last local record is IN → NextAction = "OUT" (forced regardless of internet)
    ///   • If last local record is OUT → NextAction = "COMPLETED"
    ///   • If no records → NextAction = "IN"
    /// </summary>
    public async Task<EffectiveStatusResult> GetEffectiveStatusAsync(int employeeId)
    {
        var today = DateTime.Now;
        var dateStr = today.ToString("yyyy-MM-dd");
        var result = new EffectiveStatusResult { EmployeeId = employeeId, Date = dateStr };

        try
        {
            using var conn = new SqliteConnection(_localConnectionString);
            await conn.OpenAsync();

            // Query ALL of today's records (both pending and synced)
            var todayRecords = (await conn.QueryAsync<dynamic>(@"
                SELECT log_type, log_time, id
                FROM offline_attendance
                WHERE employee_id = @EmpId AND substr(date, 1, 10) = @Today
                ORDER BY log_time ASC
            ", new { EmpId = employeeId, Today = dateStr })).ToList();

            if (todayRecords.Count == 0)
            {
                result.NextAction = "IN";
                Debug.WriteLine($"[LocalState] GetEffectiveStatus: Employee {employeeId} — no records today. Next action: TIME_IN.");
                return result;
            }

            // Extract the first IN and first OUT timestamps
            foreach (var record in todayRecords)
            {
                var logType = ((string?)record.log_type)?.ToUpperInvariant();
                var logTime = (string?)record.log_time;

                if (logType == "IN" && result.TimeInTimestamp == null)
                    result.TimeInTimestamp = logTime;
                else if (logType == "OUT" && result.TimeOutTimestamp == null)
                    result.TimeOutTimestamp = logTime;
            }

            // Determine effective status from the LAST record
            var lastRecord = todayRecords.Last();
            result.LastAction = ((string?)lastRecord.log_type)?.ToUpperInvariant();
            result.LastActionTime = (string?)lastRecord.log_time;

            if (result.TimeInTimestamp != null && result.TimeOutTimestamp != null)
            {
                result.NextAction = "COMPLETED";
                result.IsCompleted = true;
                Debug.WriteLine($"[LocalState] GetEffectiveStatus: Employee {employeeId} — COMPLETED. In={result.TimeInTimestamp}, Out={result.TimeOutTimestamp}.");
            }
            else if (result.LastAction == "IN")
            {
                result.NextAction = "OUT";
                Debug.WriteLine($"[LocalState] GetEffectiveStatus: Employee {employeeId} last status: TIME_IN ({result.TimeInTimestamp}). Next action: TIME_OUT.");
            }
            else
            {
                result.NextAction = "IN";
                Debug.WriteLine($"[LocalState] GetEffectiveStatus: Employee {employeeId} last status: {result.LastAction ?? "UNKNOWN"}. Next action: TIME_IN.");
            }

            return result;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LocalState] ERROR in GetEffectiveStatus for employee {employeeId}: {ex.Message}");
            await DbLog("ERROR", "LOCAL_STATE", $"GetEffectiveStatus failed: {ex.Message}");
            result.NextAction = "IN";
            result.Error = ex.Message;
            return result;
        }
    }

    // ═══════════════════ Toggle Logic (Sovereign) ═══════════════════

    /// <summary>
    /// Determines the next tap direction by querying STIRAMS.db ONLY.
    ///   1. <c>offline_attendance</c> — authoritative count of today's taps.
    ///   2. <c>employee_tap_status</c> — fast O(1) fallback.
    ///   3. Cross-DB check via <c>LocalDatabaseService</c>.
    ///   4. No record → "IN" (first tap of the day).
    ///
    /// Returns <c>"IN"</c>, <c>"OUT"</c>, or <c>"COMPLETED"</c>.
    /// The caller MUST check for <c>"COMPLETED"</c> and block the tap.
    /// </summary>
    private async Task<string> GetNextTapTypeLocalAsync(int employeeId, string todayStr)
    {
        try
        {
            using var conn = new SqliteConnection(_localConnectionString);
            await conn.OpenAsync();

            // ── Authoritative: count today's taps from offline_attendance ──
            // Exclude quarantined records (is_synced = -1) so poison messages
            // don't affect the IN/OUT toggle.
            var todayTaps = (await conn.QueryAsync<string>(@"
                SELECT log_type FROM offline_attendance
                WHERE employee_id = @EmpId AND substr(date, 1, 10) = @Today
                  AND is_synced <> -1
                ORDER BY log_time ASC
            ", new { EmpId = employeeId, Today = todayStr })).ToList();

            if (todayTaps.Count > 0)
            {
                var hasIn = todayTaps.Any(t => string.Equals(t, "IN", StringComparison.OrdinalIgnoreCase));
                var hasOut = todayTaps.Any(t => string.Equals(t, "OUT", StringComparison.OrdinalIgnoreCase));

                if (hasIn && hasOut)
                {
                    Debug.WriteLine($"[LocalState] Employee {employeeId} last status: TIME_OUT. Attendance COMPLETED for {todayStr}.");
                    return "COMPLETED";
                }

                if (hasIn && !hasOut)
                {
                    Debug.WriteLine($"[LocalState] Employee {employeeId} last status: TIME_IN. Next action: TIME_OUT.");
                    return "OUT";
                }

                Debug.WriteLine($"[LocalState] Employee {employeeId} — edge case: OUT without IN. Next action: TIME_IN.");
                return "IN";
            }

            // ── Fallback 1: dedicated status table ──
            var lastTap = await conn.QueryFirstOrDefaultAsync<string>(@"
                SELECT last_tap_type FROM employee_tap_status
                WHERE employee_id = @EmpId
                  AND substr(last_tap_time, 1, 10) = @Today
            ", new { EmpId = employeeId, Today = todayStr });

            if (lastTap != null)
            {
                if (string.Equals(lastTap, "OUT", StringComparison.OrdinalIgnoreCase))
                {
                    Debug.WriteLine($"[LocalState] Employee {employeeId} (tap_status fallback) last status: TIME_OUT. Attendance COMPLETED.");
                    return "COMPLETED";
                }
                Debug.WriteLine($"[LocalState] Employee {employeeId} (tap_status fallback) last status: TIME_IN. Next action: TIME_OUT.");
                return "OUT";
            }

            // ── Fallback 2: cross-database check (rams_offline.db) ──
            try
            {
                var localDb = new LocalDatabaseService();
                var crossDbLast = await localDb.GetLastTapTypeForDateAsync(employeeId, DateTime.Today);
                if (!string.IsNullOrEmpty(crossDbLast))
                {
                    if (string.Equals(crossDbLast, "OUT", StringComparison.OrdinalIgnoreCase))
                    {
                        Debug.WriteLine($"[LocalState] Employee {employeeId} (cross-DB fallback) last status: TIME_OUT. Attendance COMPLETED.");
                        return "COMPLETED";
                    }
                    Debug.WriteLine($"[LocalState] Employee {employeeId} (cross-DB fallback) last status: TIME_IN. Next action: TIME_OUT.");
                    return "OUT";
                }
            }
            catch { /* non-critical */ }

            Debug.WriteLine($"[LocalState] Employee {employeeId} — no records today. Next action: TIME_IN.");
            return "IN"; // first tap of the day
        }
        catch (Exception ex)
        {
            await DbLog("ERROR", "TOGGLE", $"Toggle query failed: {ex.Message}");

            // ── Emergency fallback: try one more isolated query on a fresh connection ──
            // This prevents defaulting to IN when a transient SQLite error occurred.
            try
            {
                Debug.WriteLine($"[LocalState] Employee {employeeId} — primary toggle failed, trying emergency fallback...");
                using var fallbackConn = new SqliteConnection(_localConnectionString);
                await fallbackConn.OpenAsync();

                var existingIn = await fallbackConn.ExecuteScalarAsync<int>(@"
                    SELECT COUNT(*) FROM offline_attendance
                    WHERE employee_id = @EmpId AND substr(date, 1, 10) = @Today AND log_type = 'IN'
                      AND is_synced <> -1
                ", new { EmpId = employeeId, Today = todayStr });

                if (existingIn > 0)
                {
                    var existingOut = await fallbackConn.ExecuteScalarAsync<int>(@"
                        SELECT COUNT(*) FROM offline_attendance
                        WHERE employee_id = @EmpId AND substr(date, 1, 10) = @Today AND log_type = 'OUT'
                          AND is_synced <> -1
                    ", new { EmpId = employeeId, Today = todayStr });

                    if (existingOut > 0)
                    {
                        Debug.WriteLine($"[LocalState] Employee {employeeId} (emergency fallback) — COMPLETED.");
                        return "COMPLETED";
                    }
                    Debug.WriteLine($"[LocalState] Employee {employeeId} (emergency fallback) — IN exists, forcing OUT.");
                    return "OUT";
                }
            }
            catch (Exception fallbackEx)
            {
                Debug.WriteLine($"[LocalState] Emergency fallback also failed: {fallbackEx.Message}");
            }

            // ── Last resort: check rams_offline.db via LocalDatabaseService ──
            // If ALL SQLite queries on STIRAMS.db failed, try the cross-database as a final check
            try
            {
                Debug.WriteLine($"[LocalState] Employee {employeeId} — trying cross-DB (rams_offline.db) as last resort...");
                var localDb = new LocalDatabaseService();
                var crossDbLast = await localDb.GetLastTapTypeForDateAsync(employeeId, DateTime.Today);
                if (!string.IsNullOrEmpty(crossDbLast))
                {
                    if (string.Equals(crossDbLast, "OUT", StringComparison.OrdinalIgnoreCase))
                    {
                        Debug.WriteLine($"[LocalState] Employee {employeeId} (cross-DB last resort) — COMPLETED.");
                        return "COMPLETED";
                    }
                    Debug.WriteLine($"[LocalState] Employee {employeeId} (cross-DB last resort) — IN exists, forcing OUT.");
                    return "OUT";
                }
            }
            catch { /* non-critical */ }

            Debug.WriteLine($"[LocalState] Employee {employeeId} — all toggle queries failed. Defaulting to TIME_IN.");
            return "IN";
        }
    }

    // ═══════════════════ Local Holiday Check ═══════════════════

    /// <summary>
    /// Checks <c>local_holidays</c> in STIRAMS.db for a blocking holiday.
    /// Returns the holiday name if attendance is blocked, null if allowed.
    /// Fail-open: if the table is empty or the query errors, attendance is allowed.
    /// </summary>
    private async Task<string?> CheckLocalHolidayAsync(Employee employee, DateTime checkTime)
    {
        try
        {
            using var conn = new SqliteConnection(_localConnectionString);
            await conn.OpenAsync();

            var dateStr = checkTime.ToString("yyyy-MM-dd");

            var holiday = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT holiday_name, holiday_type FROM local_holidays
                WHERE is_active = 1
                  AND (holiday_date = @Date
                       OR (@Date BETWEEN start_date AND end_date))
                LIMIT 1
            ", new { Date = dateStr });

            if (holiday == null) return null;

            var holidayType = ((string?)holiday.holiday_type)?.ToLowerInvariant();

            // Only hard-block on full holidays; other types (online_class, etc.)
            // are allowed through with a note
            if (holidayType == "holiday")
            {
                return (string)holiday.holiday_name;
            }

            // Non-blocking holiday types — log but allow
            await DbLog("INFO", "HOLIDAY",
                $"Non-blocking holiday: {holiday.holiday_name} (type={holiday.holiday_type})");
            return null;
        }
        catch (Exception ex)
        {
            // Fail-open: allow attendance if holiday check errors
            await DbLog("WARN", "HOLIDAY", $"Holiday check failed (fail-open): {ex.Message}");
            return null;
        }
    }

    // ═══════════════════ BACKGROUND SYNC ENGINE ═══════════════════

    /// <summary>
    /// Periodically scans for <c>is_synced = 0</c> rows and pushes them
    /// to database with exponential backoff. Records whose retry delay
    /// has not elapsed are skipped. After <see cref="MaxSyncAttempts"/>
    /// failures a record is abandoned (sync_attempts ≥ max).
    ///
    /// Pulls employee/schedule deltas every second (lightweight)
    /// and full metadata on a throttled cadence (every 30 seconds)
    /// while pending queue upload is attempted every second.
    /// </summary>
    private async Task SyncPendingRecordsAsync()
    {
        if (_isSyncing) return;
        if (!DatabaseConnectionGuard.IsOnline()) return;

        _isSyncing = true;
        int synced = 0, failed = 0;

        try
        {
            // ── Fast delta refresh: employees + schedules every sync cycle (1s) ──
            if (App.HybridDatabase != null)
            {
                try
                {
                    await App.HybridDatabase.RefreshEmployeesAndSchedulesAsync();
                }
                catch (Exception ex)
                {
                    await DbLog("WARN", "SYNC", $"Fast delta refresh failed (non-fatal): {ex.Message}");
                }
            }

            // ── Pull metadata every 30 seconds (throttled) ──
            if ((DateTime.UtcNow - _lastMetadataPullUtc).TotalSeconds >= 30)
            {
                await PullMetadataAsync();
                _lastMetadataPullUtc = DateTime.UtcNow;
            }

            using var localConn = new SqliteConnection(_localConnectionString);
            await localConn.OpenAsync();

            var pending = (await localConn.QueryAsync<OfflineAttendanceRecord>(@"
                SELECT * FROM offline_attendance
                WHERE is_synced = 0
                  AND sync_attempts < @MaxAttempts
                ORDER BY created_at ASC
                LIMIT @Batch
            ", new { MaxAttempts = MaxSyncAttempts, Batch = SyncBatchSize })).ToList();

            if (pending.Count == 0) return;

            // Apply exponential backoff filter in C#
            var now = DateTime.Now;
            var ready = pending.Where(r =>
            {
                if (r.sync_attempts == 0 || string.IsNullOrEmpty(r.updated_at))
                    return true;
                if (!DateTime.TryParse(r.updated_at, out var lastAttempt))
                    return true;
                var delaySec = Math.Min(Math.Pow(2, r.sync_attempts), 300); // cap 5 min
                return (now - lastAttempt).TotalSeconds >= delaySec;
            }).ToList();

            if (ready.Count == 0) return;

            await DbLog("INFO", "SYNC", $"Syncing {ready.Count} pending records ({pending.Count} total pending)…");

            foreach (var record in ready)
            {
                try
                {
                    // ── Validate timestamps BEFORE touching database ──
                    var parsedLogTime = DataSanitizer.ParseTimestamp(record.LogTime);
                    var parsedDate = DataSanitizer.ParseDate(record.Date);

                    // ── Auto-repair: try to recover from created_at if log_time/date are bad ──
                    if (parsedLogTime is null || parsedDate is null)
                    {
                        var fallbackTime = DataSanitizer.ParseTimestamp(record.created_at) ?? DateTime.Now;
                        parsedLogTime ??= fallbackTime;
                        parsedDate ??= fallbackTime.Date;

                        // Persist the fix so it doesn't re-trigger every cycle
                        await localConn.ExecuteAsync(@"
                            UPDATE offline_attendance
                            SET log_time   = @LogTime,
                                date       = @Date,
                                updated_at = @Now
                            WHERE id = @Id
                        ", new
                        {
                            Id = record.Id,
                            LogTime = DataSanitizer.FormatTimestamp(parsedLogTime.Value),
                            Date = DataSanitizer.FormatDate(parsedDate.Value),
                            Now = DataSanitizer.FormatTimestamp(DateTime.Now)
                        });

                        await DbLog("INFO", "SYNC",
                            $"Auto-repaired timestamps for {record.Id}: log_time='{record.LogTime}'→'{DataSanitizer.FormatTimestamp(parsedLogTime.Value)}'",
                            $"emp={record.EmployeeId}");
                    }

                    // Sanitize values BEFORE sending to database
                    var sanitizedStatus = DataSanitizer.NormalizeAttendanceStatus(record.AttendanceStatus);
                    var sanitizedLogType = DataSanitizer.NormalizeLogType(record.LogType);

                    Debug.WriteLine($"[HybridSync] Pushing {record.Id}: status='{record.AttendanceStatus}'→'{sanitizedStatus}', type='{record.LogType}'→'{sanitizedLogType}', logTime={parsedLogTime:yyyy-MM-dd HH:mm:ss}");

                    using var supaConn = new NpgsqlConnection(_databaseConnectionString);
                    await supaConn.OpenAsync();

                    var logId = await supaConn.ExecuteScalarAsync<int>(@"
                        INSERT INTO attendance_logs (
                            employee_id, rfid_code, log_time, log_type, date,
                            attendance_status, is_late, is_early_out, late_minutes,
                            undertime_minutes, notes, schedule_id, term_id, created_at
                        ) VALUES (
                            @EmployeeId, @RfidCode, @LogTime, @LogType, @Date,
                            @AttendanceStatus, @IsLate, @IsEarlyOut, @LateMinutes,
                            @UndertimeMinutes, @Notes, @ScheduleId, @TermId, NOW()
                        )
                        RETURNING log_id
                    ", new
                    {
                        record.EmployeeId,
                        record.RfidCode,
                        LogTime = parsedLogTime.Value,
                        LogType = sanitizedLogType,
                        Date = parsedDate.Value,
                        AttendanceStatus = sanitizedStatus,
                        IsLate = record.IsLate == 1,
                        IsEarlyOut = record.IsEarlyOut == 1,
                        record.LateMinutes,
                        record.UndertimeMinutes,
                        record.Notes,
                        record.ScheduleId,
                        record.TermId
                    });

                    if (logId > 0)
                    {
                        await MarkAsSyncedAsync(localConn, record.Id, logId);
                        // Replace the offline placeholder in attendance_logs with the real log_id
                        _ = Task.Run(async () => await MirrorToAttendanceLogsAsync(
                            logId, record.EmployeeId, record.RfidCode,
                            parsedLogTime.Value, sanitizedLogType, sanitizedStatus,
                            record.IsLate == 1, record.IsEarlyOut == 1,
                            record.LateMinutes > 0 ? record.LateMinutes : (int?)null,
                            record.UndertimeMinutes > 0 ? record.UndertimeMinutes : (int?)null,
                            record.Notes, scheduleId: record.ScheduleId, termId: record.TermId));
                        synced++;
                        Debug.WriteLine($"[HybridSync] \u2705 Synced {record.Id} \u2192 log_id {logId}");
                    }
                }
                catch (PostgresException pgEx) when (pgEx.SqlState == "23505")
                {
                    // ── 23505: Unique constraint violation (Poison Message: Duplicate) ──
                    // The record already exists in database. Mark it as synced so
                    // it stops retrying and unblocks the queue.
                    Debug.WriteLine($"[HybridSync] ⚠ DUPLICATE {record.Id}: already in database — marking synced");
                    await DbLog("INFO", "SYNC",
                        $"Poison message resolved (23505 duplicate) for {record.Id}: marking as synced",
                        $"emp={record.EmployeeId}, constraint={pgEx.ConstraintName}");

                    // Mark synced with log_id = -1 (sentinel: synced-via-duplicate-resolution)
                    await MarkAsSyncedAsync(localConn, record.Id, -1);
                    synced++;
                }
                catch (PostgresException pgEx) when (pgEx.SqlState == "22007")
                {
                    // ── 22007: Invalid input syntax for type timestamp ──
                    // Auto-fix the timestamp and retry once. If the retry fails,
                    // mark the record as 'corrupted' so it doesn't block the queue.
                    Debug.WriteLine($"[HybridSync] ⚠ 22007 (Invalid Timestamp) for {record.Id} — attempting auto-fix");
                    await DbLog("WARN", "SYNC",
                        $"22007 for {record.Id}: auto-fixing timestamp and retrying",
                        $"emp={record.EmployeeId}, original_log_time='{record.LogTime}', original_date='{record.Date}'");

                    var fixedTime = DateTime.Now;
                    var fixedTimeStr = fixedTime.ToString("yyyy-MM-dd HH:mm:ss");
                    var fixedDateStr = fixedTime.ToString("yyyy-MM-dd");

                    // Repair the local row
                    await localConn.ExecuteAsync(@"
                        UPDATE offline_attendance
                        SET log_time = @LogTime, date = @Date, updated_at = @Now
                        WHERE id = @Id
                    ", new { Id = record.Id, LogTime = fixedTimeStr, Date = fixedDateStr, Now = fixedTimeStr });

                    // Retry with the fixed timestamp
                    try
                    {
                        using var retryConn = new NpgsqlConnection(_databaseConnectionString);
                        await retryConn.OpenAsync();

                        var retryLogId = await retryConn.ExecuteScalarAsync<int>(@"
                            INSERT INTO attendance_logs (
                                employee_id, rfid_code, log_time, log_type, date,
                                attendance_status, is_late, is_early_out, late_minutes,
                                undertime_minutes, notes, schedule_id, term_id, created_at
                            ) VALUES (
                                @EmployeeId, @RfidCode, @LogTime, @LogType, @Date,
                                @AttendanceStatus, @IsLate, @IsEarlyOut, @LateMinutes,
                                @UndertimeMinutes, @Notes, @ScheduleId, @TermId, NOW()
                            )
                            RETURNING log_id
                        ", new
                        {
                            record.EmployeeId,
                            record.RfidCode,
                            LogTime = fixedTime,
                            LogType = DataSanitizer.NormalizeLogType(record.LogType),
                            Date = fixedTime.Date,
                            AttendanceStatus = DataSanitizer.NormalizeAttendanceStatus(record.AttendanceStatus),
                            IsLate = record.IsLate == 1,
                            IsEarlyOut = record.IsEarlyOut == 1,
                            record.LateMinutes,
                            record.UndertimeMinutes,
                            record.Notes,
                            record.ScheduleId,
                            record.TermId
                        });

                        if (retryLogId > 0)
                        {
                            await MarkAsSyncedAsync(localConn, record.Id, retryLogId);
                            synced++;
                            Debug.WriteLine($"[HybridSync] ✅ 22007 auto-fix SUCCEEDED for {record.Id} → log_id {retryLogId}");
                            await DbLog("INFO", "SYNC",
                                $"22007 auto-fix succeeded for {record.Id} → log_id {retryLogId}");
                        }
                    }
                    catch (Exception retryEx)
                    {
                        // ── Poison Message Quarantine (22007) ──
                        // Mark as permanently failed so it stops blocking the queue.
                        // sync_attempts = MaxSyncAttempts ensures it's never re-fetched.
                        failed++;
                        await localConn.ExecuteAsync(@"
                            UPDATE offline_attendance
                            SET sync_attempts   = @Max,
                                last_sync_error = @Error,
                                updated_at      = @Now,
                                is_synced        = -1
                            WHERE id = @Id
                        ", new
                        {
                            Id = record.Id,
                            Max = MaxSyncAttempts,
                            Error = $"QUARANTINED: 22007 invalid timestamp — auto-fix retry failed: {retryEx.Message}",
                            Now = fixedTimeStr
                        });
                        Debug.WriteLine($"[HybridSync] ❌ QUARANTINED {record.Id} — 22007 auto-fix retry failed (will never retry)");
                        await DbLog("ERROR", "SYNC",
                            $"QUARANTINED {record.Id}: 22007 auto-fix retry failed — permanently removed from queue",
                            retryEx.Message);
                    }
                }
                catch (PostgresException pgEx) when (
                    pgEx.SqlState is "23503" or "23514")
                {
                    // ── Poison Message Quarantine (FK violation / CHECK constraint) ──
                    // These are permanent data errors that will NEVER succeed on retry.
                    // Quarantine immediately so they don't block the queue.
                    failed++;
                    var detail = $"SqlState={pgEx.SqlState}, Constraint={pgEx.ConstraintName}, " +
                                 $"Detail={pgEx.Detail}, Column={pgEx.ColumnName}";
                    Debug.WriteLine($"[HybridSync] ❌ QUARANTINED {record.Id}: permanent constraint error — {pgEx.MessageText}");
                    await localConn.ExecuteAsync(@"
                        UPDATE offline_attendance
                        SET sync_attempts   = @Max,
                            last_sync_error = @Error,
                            updated_at      = @Now,
                            is_synced        = -1
                        WHERE id = @Id
                    ", new
                    {
                        Id = record.Id,
                        Max = MaxSyncAttempts,
                        Error = $"QUARANTINED: PG {pgEx.SqlState} — {pgEx.MessageText} | {detail}",
                        Now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                    });
                    await DbLog("ERROR", "SYNC",
                        $"QUARANTINED {record.Id}: PG {pgEx.SqlState} — {pgEx.MessageText}",
                        detail);
                }
                catch (PostgresException pgEx)
                {
                    // Log the full Postgres constraint violation details
                    failed++;
                    var detail = $"SqlState={pgEx.SqlState}, Constraint={pgEx.ConstraintName}, " +
                                 $"Detail={pgEx.Detail}, Hint={pgEx.Hint}, " +
                                 $"Column={pgEx.ColumnName}, Table={pgEx.TableName}";
                    Debug.WriteLine($"[HybridSync] ❌ POSTGRES ERROR {record.Id}: {pgEx.MessageText}");
                    Debug.WriteLine($"[HybridSync]    {detail}");
                    Debug.WriteLine($"[HybridSync]    Record: emp={record.EmployeeId}, status='{record.AttendanceStatus}', type='{record.LogType}'");
                    await IncrementSyncAttemptsAsync(localConn, record.Id,
                        $"PG {pgEx.SqlState}: {pgEx.MessageText} | {detail}");
                    await DbLog("ERROR", "SYNC",
                        $"Postgres constraint violation for {record.Id}: {pgEx.MessageText}",
                        detail);

                    // Don't break for constraint violations — they're per-record issues.
                    // Only break for connection-level failures.
                }
                catch (Exception ex)
                {
                    failed++;
                    await IncrementSyncAttemptsAsync(localConn, record.Id, ex.Message);
                    Debug.WriteLine($"[HybridSync] ❌ Failed {record.Id}: {ex.Message}");

                    // Connection-level failure → stop the batch
                    if (ex is NpgsqlException or TimeoutException)
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HybridSync] Background sync error: {ex.Message}");
        }
        finally
        {
            _isSyncing = false;

            if (synced > 0 || failed > 0)
            {
                await DbLog("INFO", "SYNC", $"Cycle complete: {synced} synced, {failed} failed.");
                Debug.WriteLine($"[HybridSync] Cycle complete: {synced} synced, {failed} failed.");
                SyncCycleCompleted?.Invoke(this, (synced, failed));
                if (synced > 0) LastSyncUtc = DateTime.UtcNow;
            }

            await NotifyPendingCountAsync();
        }
    }

    // ═══════════════════ Helpers ═══════════════════

    /// <summary>Mark a record as successfully synced (new connection).</summary>
    private async Task MarkAsSyncedAsync(string guid, int databaseLogId)
    {
        using var conn = new SqliteConnection(_localConnectionString);
        await conn.OpenAsync();
        await MarkAsSyncedAsync(conn, guid, databaseLogId);
    }

    // ═══════════════════ STIRAMS.db attendance_logs mirror ═══════════════════

    /// <summary>
    /// Writes one attendance row into STIRAMS.db's <c>attendance_logs</c> table,
    /// keeping it as a full replica of the database schema.
    ///
    /// Rules:
    ///   • <paramref name="databaseLogId"/> &gt; 0 → <c>INSERT OR REPLACE</c> using
    ///     the real PK so the offline placeholder row is atomically superseded.
    ///   • <paramref name="databaseLogId"/> null / 0 → <c>INSERT OR IGNORE</c>
    ///     (the UNIQUE INDEX on employee_id + date + log_type prevents duplicates).
    /// </summary>
    public async Task MirrorToAttendanceLogsAsync(
        int? databaseLogId,
        int employeeId, string rfidCode,
        DateTime logTime, string logType,
        string attendanceStatus,
        bool isLate = false, bool isEarlyOut = false,
        int? lateMinutes = null, int? undertimeMinutes = null,
        string? notes = null,
        bool isAdminTime = false, string? adminTimeReason = null, string? adminTimeStatus = null,
        bool isHoliday = false, bool isSuspended = false, bool isOnlineClass = false,
        long? scheduleId = null, long? termId = null)
    {
        try
        {
            using var conn = new SqliteConnection(_localConnectionString);
            await conn.OpenAsync();

            var logTimeStr = logTime.ToString("yyyy-MM-dd HH:mm:ss");
            var dateStr    = logTime.ToString("yyyy-MM-dd");
            var now        = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var safeStatus = DataSanitizer.NormalizeAttendanceStatus(attendanceStatus);
            var safeType   = DataSanitizer.NormalizeLogType(logType);

            if (databaseLogId is > 0)
            {
                // Real database log_id → INSERT OR REPLACE so the unique-index constraint
                // removes any existing offline placeholder for the same employee/date/type.
                await conn.ExecuteAsync(@"
                    INSERT OR REPLACE INTO attendance_logs (
                        log_id, employee_id, rfid_code, log_time, log_type, date,
                        attendance_status, is_late, is_early_out, late_minutes,
                        undertime_minutes, notes,
                        is_admin_time, admin_time_reason, admin_time_status,
                        is_holiday, is_suspended, is_online_class,
                        schedule_id, term_id, created_at
                    ) VALUES (
                        @LogId, @EmpId, @Rfid, @LogTime, @LogType, @Date,
                        @Status, @IsLate, @IsEarlyOut, @LateMins,
                        @Undertime, @Notes,
                        @IsAdminTime, @AdminReason, @AdminStatus,
                        @IsHoliday, @IsSuspended, @IsOnlineClass,
                        @SchedId, @TermId, @Now
                    )
                ", new
                {
                    LogId = databaseLogId.Value,
                    EmpId = employeeId, Rfid = rfidCode,
                    LogTime = logTimeStr, LogType = safeType, Date = dateStr,
                    Status = safeStatus,
                    IsLate = isLate ? 1 : 0, IsEarlyOut = isEarlyOut ? 1 : 0,
                    LateMins = lateMinutes, Undertime = undertimeMinutes,
                    Notes = notes,
                    IsAdminTime = isAdminTime ? 1 : 0, AdminReason = adminTimeReason, AdminStatus = adminTimeStatus,
                    IsHoliday = isHoliday ? 1 : 0, IsSuspended = isSuspended ? 1 : 0, IsOnlineClass = isOnlineClass ? 1 : 0,
                    SchedId = scheduleId, TermId = termId, Now = now
                });

                await DbLog("INFO", "MIRROR",
                    $"✓ attendance_logs ← log_id={databaseLogId} ({safeType}) emp={employeeId}");
            }
            else
            {
                // Offline — the existing STIRAMS.db schema uses log_id NUMERIC PRIMARY KEY
                // which does NOT auto-increment (unlike INTEGER PRIMARY KEY). Providing NULL
                // would violate NOT NULL. Use a unique negative epoch-based temp id that is
                // replaced by the real database log_id when this record syncs online.
                var tempLogId = (int)(-(DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                                       % 100_000_000) - employeeId);

                await conn.ExecuteAsync(@"
                    INSERT OR REPLACE INTO attendance_logs (
                        log_id, employee_id, rfid_code, log_time, log_type, date,
                        attendance_status, is_late, is_early_out, late_minutes,
                        undertime_minutes, notes,
                        is_admin_time, admin_time_reason, admin_time_status,
                        is_holiday, is_suspended, is_online_class,
                        schedule_id, term_id, created_at
                    ) VALUES (
                        @LogId, @EmpId, @Rfid, @LogTime, @LogType, @Date,
                        @Status, @IsLate, @IsEarlyOut, @LateMins,
                        @Undertime, @Notes,
                        @IsAdminTime, @AdminReason, @AdminStatus,
                        @IsHoliday, @IsSuspended, @IsOnlineClass,
                        @SchedId, @TermId, @Now
                    )
                ", new
                {
                    LogId = tempLogId,
                    EmpId = employeeId, Rfid = rfidCode,
                    LogTime = logTimeStr, LogType = safeType, Date = dateStr,
                    Status = safeStatus,
                    IsLate = isLate ? 1 : 0, IsEarlyOut = isEarlyOut ? 1 : 0,
                    LateMins = lateMinutes, Undertime = undertimeMinutes,
                    Notes = notes,
                    IsAdminTime = isAdminTime ? 1 : 0, AdminReason = adminTimeReason, AdminStatus = adminTimeStatus,
                    IsHoliday = isHoliday ? 1 : 0, IsSuspended = isSuspended ? 1 : 0, IsOnlineClass = isOnlineClass ? 1 : 0,
                    SchedId = scheduleId, TermId = termId, Now = now
                });

                await DbLog("INFO", "MIRROR",
                    $"✓ attendance_logs ← tmp_id={tempLogId} ({safeType}) emp={employeeId} (pending sync)");
            }
        }
        catch (Exception ex)
        {
            await DbLog("WARN", "MIRROR", $"MirrorToAttendanceLogsAsync failed: {ex.Message}");
        }
    }

    /// <summary>Mark a record as successfully synced (reuse connection).</summary>
    private static async Task MarkAsSyncedAsync(SqliteConnection conn, string guid, int databaseLogId)
    {
        await conn.ExecuteAsync(@"
            UPDATE offline_attendance
            SET is_synced       = 1,
                database_log_id = @LogId,
                synced_at       = @Now,
                updated_at      = @Now
            WHERE id = @Id
        ", new
        {
            Id = guid,
            LogId = databaseLogId,
            Now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        });
    }

    /// <summary>Bump the attempt counter and record the last error.</summary>
    private static async Task IncrementSyncAttemptsAsync(SqliteConnection conn, string guid, string error)
    {
        await conn.ExecuteAsync(@"
            UPDATE offline_attendance
            SET sync_attempts   = sync_attempts + 1,
                last_sync_error = @Error,
                updated_at      = @Now
            WHERE id = @Id
        ", new
        {
            Id = guid,
            Error = error,
            Now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        });
    }

    /// <summary>Get the count of unsynced records (excludes quarantined and max-attempt records).</summary>
    public async Task<int> GetPendingCountAsync()
    {
        try
        {
            using var conn = new SqliteConnection(_localConnectionString);
            await conn.OpenAsync();
            return await conn.ExecuteScalarAsync<int>(@"
                SELECT COUNT(*) FROM offline_attendance
                WHERE is_synced = 0
                  AND sync_attempts < @MaxAttempts
            ", new { MaxAttempts = MaxSyncAttempts });
        }
        catch { return 0; }
    }

    private async Task NotifyPendingCountAsync()
    {
        try
        {
            var count = await GetPendingCountAsync();
            PendingCountChanged?.Invoke(this, count);
        }
        catch { /* best-effort */ }
    }

    /// <summary>Force an immediate sync cycle (e.g. when connectivity is restored).</summary>
    public Task ForceSyncAsync() => SyncPendingRecordsAsync();

    // ═══════════════════ Metadata Pull (the "Pull" logic) ═══════════════════

    /// <summary>
    /// Pulls metadata from database into STIRAMS.db so offline business rules
    /// stay current. Called automatically at the start of each sync cycle.
    ///
    /// Pulls:
    ///   • Holidays → <c>local_holidays</c>
    ///   • Employee tap status (today's attendance) → <c>employee_tap_status</c>
    ///
    /// If a new holiday was added to database while offline, this ensures
    /// the local copy picks it up on the next sync, so the offline engine
    /// can correctly block attendance for new holidays.
    /// </summary>
    private async Task PullMetadataAsync()
    {
        try
        {
            // ── Always check database for new employees and schedule changes ──
            // This is the core of the "always live" requirement: periodic
            // sync cycle delta-pulls employees + class_schedules from database
            // into rams_offline.db and refreshes the in-memory ScheduleCache,
            // so a new employee or a newly-assigned class schedule is visible
            // to the kiosk without restarting the application.
            if (App.HybridDatabase != null)
            {
                try
                {
                    await App.HybridDatabase.RefreshEmployeesAndSchedulesAsync();
                }
                catch (Exception ex)
                {
                    await DbLog("WARN", "PULL", $"Employee/schedule refresh failed (non-fatal): {ex.Message}");
                }
            }

            using var supaConn = new NpgsqlConnection(_databaseConnectionString);
            await supaConn.OpenAsync();

            // ── Pull holidays ──
            // NOTE: The database holiday_calendar table uses columns:
            //   id (not holiday_id), name (not holiday_name), type (not holiday_type),
            //   start_date, end_date, description
            try
            {
                var holidays = (await supaConn.QueryAsync<dynamic>(@"
                    SELECT id, name, type, start_date, end_date,
                           description
                    FROM holiday_calendar
                ")).ToList();

                if (holidays.Count > 0)
                {
                    using var localConn = new SqliteConnection(_localConnectionString);
                    await localConn.OpenAsync();

                    using var txn = localConn.BeginTransaction();
                    await localConn.ExecuteAsync("DELETE FROM local_holidays", transaction: txn);

                    var syncedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    foreach (var h in holidays)
                    {
                        await localConn.ExecuteAsync(@"
                            INSERT INTO local_holidays (
                                holiday_id, holiday_name, holiday_date, start_date, end_date,
                                holiday_type, description, is_active, synced_at
                            ) VALUES (
                                @Id, @Name, @Date, @Start, @End,
                                @Type, @Desc, @Active, @Synced
                            )
                        ", new
                        {
                            Id = (int)h.id,
                            Name = (string?)h.name ?? "",
                            Date = h.start_date != null
                                ? ((DateTime)h.start_date).ToString("yyyy-MM-dd") : "",
                            Start = h.start_date != null
                                ? ((DateTime)h.start_date).ToString("yyyy-MM-dd") : (string?)null,
                            End = h.end_date != null
                                ? ((DateTime)h.end_date).ToString("yyyy-MM-dd") : (string?)null,
                            Type = (string?)h.type,
                            Desc = (string?)h.description,
                            Active = 1,
                            Synced = syncedAt
                        }, txn);
                    }
                    txn.Commit();
                    await DbLog("INFO", "PULL", $"Holidays synced: {holidays.Count} records");
                }
            }
            catch (PostgresException pgEx) when (pgEx.SqlState == "42P01")
            {
                await DbLog("WARN", "PULL", $"holiday_calendar table not found: {pgEx.MessageText}");
            }
            catch (Exception ex)
            {
                await DbLog("WARN", "PULL", $"Holiday pull failed: {ex.Message}");
            }

            // ── Pull today's attendance: rebuild tap status AND mirror to attendance_logs ──
            try
            {
                var todayStr = DateTime.Today.ToString("yyyy-MM-dd");

                var todayLogs = (await supaConn.QueryAsync<dynamic>(@"
                    SELECT employee_id, rfid_code, log_id, log_type, log_time,
                           attendance_status, is_late, is_early_out,
                           late_minutes, undertime_minutes, notes,
                           is_admin_time, admin_time_reason, admin_time_status,
                           is_holiday, is_suspended, is_online_class,
                           schedule_id, term_id
                    FROM attendance_logs
                    WHERE date = CURRENT_DATE
                    ORDER BY log_time DESC
                ")).ToList();

                using var localConn = new SqliteConnection(_localConnectionString);
                await localConn.OpenAsync();

                using var txn = localConn.BeginTransaction();

                // ALWAYS delete today's rows first so STIRAMS.db exactly mirrors
                // database — including when records were deleted from database or
                // there are genuinely no taps yet today. Without this, stale rows
                // stayed indefinitely because the old code was guarded by
                // `if (todayLogs.Count > 0)`.
                await localConn.ExecuteAsync(
                    "DELETE FROM attendance_logs WHERE substr(date, 1, 10) = @Today",
                    new { Today = todayStr }, txn);

                if (todayLogs.Count > 0)
                {
                    // Group by employee, take latest for tap-status
                    var latestPerEmployee = todayLogs
                        .GroupBy(l => (int)l.employee_id)
                        .Select(g => g.First())
                        .ToList();

                    var nowStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

                    foreach (var latest in latestPerEmployee)
                    {
                        var supaTime = ((DateTime)latest.log_time).ToString("yyyy-MM-dd HH:mm:ss");
                        var localLast = await localConn.QueryFirstOrDefaultAsync<string>(@"
                            SELECT log_time FROM offline_attendance
                            WHERE employee_id = @EmpId AND substr(date, 1, 10) = @Today
                            ORDER BY log_time DESC LIMIT 1
                        ", new
                        {
                            EmpId = (int)latest.employee_id,
                            Today = todayStr
                        }, txn);

                        if (localLast == null ||
                            string.Compare(supaTime, localLast, StringComparison.Ordinal) > 0)
                        {
                            await localConn.ExecuteAsync(@"
                                INSERT OR REPLACE INTO employee_tap_status (
                                    employee_id, last_tap_type, last_tap_time, updated_at
                                ) VALUES (@EmpId, @TapType, @TapTime, @Now)
                            ", new
                            {
                                EmpId = (int)latest.employee_id,
                                TapType = (string?)latest.log_type ?? "IN",
                                TapTime = supaTime,
                                Now = nowStr
                            }, txn);
                        }
                    }

                    // Re-insert every row from database into STIRAMS.db attendance_logs
                    foreach (var row in todayLogs)
                    {
                        var rowLogTime = ((DateTime)row.log_time).ToString("yyyy-MM-dd HH:mm:ss");
                        var rowDate    = ((DateTime)row.log_time).ToString("yyyy-MM-dd");
                        await localConn.ExecuteAsync(@"
                            INSERT OR REPLACE INTO attendance_logs (
                                log_id, employee_id, rfid_code, log_time, log_type, date,
                                attendance_status, is_late, is_early_out,
                                late_minutes, undertime_minutes, notes,
                                is_admin_time, admin_time_reason, admin_time_status,
                                is_holiday, is_suspended, is_online_class,
                                schedule_id, term_id, created_at
                            ) VALUES (
                                @LogId, @EmpId, @Rfid, @LogTime, @LogType, @Date,
                                @Status, @IsLate, @IsEarlyOut,
                                @LateMins, @Undertime, @Notes,
                                @IsAdminTime, @AdminReason, @AdminStatus,
                                @IsHoliday, @IsSuspended, @IsOnlineClass,
                                @SchedId, @TermId, @Now
                            )
                        ", new
                        {
                            LogId      = (int)row.log_id,
                            EmpId      = (int)row.employee_id,
                            Rfid       = (string?)row.rfid_code ?? "",
                            LogTime    = rowLogTime,
                            LogType    = (string?)row.log_type ?? "IN",
                            Date       = rowDate,
                            Status     = DataSanitizer.NormalizeAttendanceStatus((string?)row.attendance_status),
                            IsLate     = (row.is_late != null && (bool)row.is_late) ? 1 : 0,
                            IsEarlyOut = (row.is_early_out != null && (bool)row.is_early_out) ? 1 : 0,
                            LateMins   = (int?)row.late_minutes,
                            Undertime  = (int?)row.undertime_minutes,
                            Notes      = (string?)row.notes,
                            IsAdminTime   = (row.is_admin_time != null && (bool)row.is_admin_time) ? 1 : 0,
                            AdminReason   = (string?)row.admin_time_reason,
                            AdminStatus   = (string?)row.admin_time_status,
                            IsHoliday     = (row.is_holiday != null && (bool)row.is_holiday) ? 1 : 0,
                            IsSuspended   = (row.is_suspended != null && (bool)row.is_suspended) ? 1 : 0,
                            IsOnlineClass = (row.is_online_class != null && (bool)row.is_online_class) ? 1 : 0,
                            SchedId    = row.schedule_id != null ? (long?)row.schedule_id : null,
                            TermId     = row.term_id != null ? (long?)row.term_id : null,
                            Now        = nowStr
                        }, txn);
                    }

                    txn.Commit();
                    await DbLog("INFO", "PULL",
                        $"attendance_logs synced: {latestPerEmployee.Count} employees, {todayLogs.Count} logs");
                }
                else
                {
                    // Commit the DELETE so the cleared state is persisted.
                    txn.Commit();
                    await DbLog("INFO", "PULL",
                        "Today's attendance: 0 records in database — STIRAMS.db attendance_logs cleared for today");
                }
            }
            catch (Exception ex)
            {
                await DbLog("WARN", "PULL", $"Attendance pull failed: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            await DbLog("WARN", "PULL", $"PullMetadata connection failed: {ex.Message}");
        }
    }

    // ═══════════════════ Debug Logger (SQLite + Console) ═══════════════════

    /// <summary>
    /// Writes a structured log entry to both the <c>debug_logs</c> table
    /// in STIRAMS.db and <see cref="Debug.WriteLine"/>. This provides
    /// persistent diagnostics even after app restart — inspect the table
    /// to see exactly why INSERT statements failed.
    /// </summary>
    private async Task DbLog(string level, string tag, string message, string? context = null)
    {
        var now = DateTime.Now;
        var line = $"[{now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] [{tag}] {message}";
        Debug.WriteLine(line);

        try
        {
            using var conn = new SqliteConnection(_localConnectionString);
            await conn.OpenAsync();
            await conn.ExecuteAsync(@"
                INSERT INTO debug_logs (timestamp, level, tag, message, context, created_at)
                VALUES (@Ts, @Level, @Tag, @Msg, @Ctx, @Now)
            ", new
            {
                Ts = now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                Level = level,
                Tag = tag,
                Msg = message,
                Ctx = context,
                Now = now.ToString("yyyy-MM-dd HH:mm:ss")
            });
        }
        catch
        {
            // Logging must never throw — silent fallback to Debug output only
        }
    }

    /// <summary>Get the STIRAMS.db path (AppData) for diagnostics and DBeaver connections.</summary>
    public string GetDatabasePath()
    {
        var appDataFolder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appDataFolder, "STI_Attendance", "Database", "STIRAMS.db");
    }

    // ═══════════════════ Recent Attendance Feed ═══════════════════

    /// <summary>
    /// Get today's recent attendance records for the sidebar feed.
    ///
    /// Queries BOTH <c>offline_attendance</c> (local save table)
    /// and <c>attendance_logs</c> (the database mirror) so offline taps appear in the feed
    /// immediately — without waiting for the fire-and-forget <see cref="MirrorToAttendanceLogsAsync"/>
    /// to complete.
    ///
    /// Employee names are resolved directly from STIRAMS.db <c>employees</c> table.
    /// </summary>
    public async Task<List<RecentAttendanceItem>> GetRecentAttendanceAsync(int limit = 10)
    {
        var today = TimeChangerWindow.GetCurrentTime().ToString("yyyy-MM-dd");
        try
        {
            using var conn = new SqliteConnection(_localConnectionString);
            await conn.OpenAsync();

            // Primary source: attendance_logs (authoritative feed table).
            // Return ONE latest row per employee for today's date.
            try
            {
                var sql = @"
                    WITH ranked AS (
                        SELECT employee_id, log_type, log_time, attendance_status,
                               ROW_NUMBER() OVER (
                                   PARTITION BY employee_id
                                   ORDER BY log_time DESC
                               ) AS rn
                        FROM attendance_logs
                        WHERE employee_id IS NOT NULL
                          AND employee_id > 0
                          AND substr(COALESCE(NULLIF(log_time, ''), NULLIF(date, '')), 1, 10) = @Today
                    )
                    SELECT
                        r.employee_id                                         AS EmployeeId,
                        e.full_name                                           AS FullName,
                        COALESCE(e.department, '')                            AS Department,
                        SUBSTR(COALESCE(e.full_name, 'E'), 1, 1)             AS Initial,
                        r.log_type                                            AS LogType,
                        r.log_time                                            AS LogTime,
                        r.attendance_status                                   AS AttendanceStatus,
                        e.photo_path                                          AS PhotoPath
                    FROM ranked r
                    INNER JOIN employees e ON r.employee_id = e.employee_id AND COALESCE(e.is_active, 1) = 1
                    WHERE r.rn = 1
                    ORDER BY r.log_time DESC
                    LIMIT @Limit";
                var rows = (await conn.QueryAsync<RecentAttendanceItem>(sql, new { Today = today, Limit = limit })).ToList();

                if (rows.Count == 0)
                {
                    // Fallback: if attendance_logs is temporarily empty, include local pending/offline rows.
                    var offlineFallbackSql = @"
                        WITH ranked AS (
                            SELECT employee_id,
                                   CASE
                                       WHEN INSTR(UPPER(COALESCE(log_type, '')), 'OUT') > 0 THEN 'OUT'
                                       ELSE 'IN'
                                   END AS log_type,
                                   COALESCE(NULLIF(log_time, ''), NULLIF(date, '') || ' 00:00:00') AS log_time,
                                   COALESCE(attendance_status, 'on-time') AS attendance_status,
                                   ROW_NUMBER() OVER (
                                       PARTITION BY employee_id
                                       ORDER BY COALESCE(NULLIF(log_time, ''), NULLIF(date, '') || ' 00:00:00') DESC
                                   ) AS rn
                            FROM offline_attendance
                            WHERE employee_id IS NOT NULL
                              AND employee_id > 0
                              AND COALESCE(is_synced, 0) <> -1
                              AND substr(COALESCE(NULLIF(log_time, ''), NULLIF(date, '')), 1, 10) = @Today
                        )
                        SELECT
                            r.employee_id                                         AS EmployeeId,
                            e.full_name                                           AS FullName,
                            COALESCE(e.department, '')                            AS Department,
                            SUBSTR(COALESCE(e.full_name, 'E'), 1, 1)             AS Initial,
                            r.log_type                                            AS LogType,
                            r.log_time                                            AS LogTime,
                            r.attendance_status                                   AS AttendanceStatus,
                            e.photo_path                                          AS PhotoPath
                        FROM ranked r
                        INNER JOIN employees e ON r.employee_id = e.employee_id AND COALESCE(e.is_active, 1) = 1
                        WHERE r.rn = 1
                        ORDER BY r.log_time DESC
                        LIMIT @Limit";

                    rows = (await conn.QueryAsync<RecentAttendanceItem>(offlineFallbackSql, new { Today = today, Limit = limit })).ToList();
                }

                await DbLog("INFO", "FEED", $"Recent feed rows={rows.Count} for day={today}");
                foreach (var r in rows)
                {
                    await DbLog("INFO", "FEED", $"  emp={r.EmployeeId} name='{r.FullName}' type={r.LogType} time={r.LogTime:yyyy-MM-dd HH:mm:ss}");
                }

                return rows;
            }
            catch (SqliteException)
            {
                var fallbackSql = @"
                    WITH merged AS (
                        SELECT employee_id,
                               CASE
                                   WHEN INSTR(UPPER(COALESCE(log_type, '')), 'OUT') > 0 THEN 'OUT'
                                   ELSE 'IN'
                               END AS log_type,
                               COALESCE(NULLIF(log_time, ''), NULLIF(date, '') || ' 00:00:00') AS log_time,
                               COALESCE(attendance_status, 'on-time') AS attendance_status,
                               1 AS src_priority
                        FROM attendance_logs
                        WHERE employee_id IS NOT NULL
                          AND employee_id > 0
                          AND substr(COALESCE(NULLIF(log_time, ''), NULLIF(date, '')), 1, 10) = @Today

                        UNION ALL

                        SELECT employee_id,
                               CASE
                                   WHEN INSTR(UPPER(COALESCE(log_type, '')), 'OUT') > 0 THEN 'OUT'
                                   ELSE 'IN'
                               END AS log_type,
                               COALESCE(NULLIF(log_time, ''), NULLIF(date, '') || ' 00:00:00') AS log_time,
                               COALESCE(attendance_status, 'on-time') AS attendance_status,
                               0 AS src_priority
                        FROM offline_attendance
                        WHERE employee_id IS NOT NULL
                          AND employee_id > 0
                          AND substr(COALESCE(NULLIF(log_time, ''), NULLIF(date, '')), 1, 10) = @Today
                          AND COALESCE(is_synced, 0) <> -1
                    ), ranked AS (
                        SELECT employee_id, log_type, log_time, attendance_status,
                               ROW_NUMBER() OVER (
                                   PARTITION BY employee_id
                                   ORDER BY log_time DESC, src_priority DESC
                               ) AS rn
                        FROM merged
                    )
                    SELECT
                        r.employee_id                            AS EmployeeId,
                        e.full_name                              AS FullName,
                        COALESCE(e.department, '')               AS Department,
                        SUBSTR(COALESCE(e.full_name, 'E'), 1, 1) AS Initial,
                        r.log_type                               AS LogType,
                        r.log_time                               AS LogTime,
                        r.attendance_status                      AS AttendanceStatus,
                        e.photo_path                             AS PhotoPath
                    FROM ranked r
                    INNER JOIN employees e ON r.employee_id = e.employee_id AND COALESCE(e.is_active, 1) = 1
                    WHERE r.rn = 1
                    ORDER BY r.log_time DESC
                    LIMIT @Limit";

                return (await conn.QueryAsync<RecentAttendanceItem>(fallbackSql, new { Today = today, Limit = limit })).ToList();
            }
        }
        catch (Exception ex)
        {
            await DbLog("WARN", "FEED", $"Recent attendance query failed: {ex.Message}");
            return [];
        }
    }

    public void Dispose()
    {
        _syncTimer.Stop();
        _syncTimer.Dispose();
    }
}

// ═══════════════════════════════════════════════════════════════════
// Supporting types
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// Result returned by <see cref="HybridAttendanceSyncService.RecordAttendanceAsync"/>.
/// </summary>
public class AttendanceSaveResult
{
    /// <summary>Local GUID assigned to this record.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>True when the record was persisted to local SQLite.</summary>
    public bool SavedLocally { get; set; }

    /// <summary>True when the record was also pushed to database.</summary>
    public bool SavedOnline { get; set; }

    /// <summary>The database-assigned <c>log_id</c> (null if offline).</summary>
    public int? databaseLogId { get; set; }

    /// <summary>Error message if the local save itself failed.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>True when the record is saved locally but not yet synced.</summary>
    public bool IsQueued => SavedLocally && !SavedOnline;

    /// <summary>Overall success — at minimum the local save must succeed.</summary>
    public bool Success => SavedLocally;
}

/// <summary>
/// Internal Dapper mapping for the <c>offline_attendance</c> table.
/// Uses snake_case properties to match SQLite column names.
/// </summary>
internal class OfflineAttendanceRecord
{
    public string Id { get; set; } = string.Empty;
    // ReSharper disable InconsistentNaming — matches SQLite columns
    public int employee_id { get; set; }
    public string rfid_code { get; set; } = string.Empty;
    public string log_time { get; set; } = string.Empty;
    public string log_type { get; set; } = string.Empty;
    public string date { get; set; } = string.Empty;
    public string? attendance_status { get; set; }
    public int is_late { get; set; }
    public int is_early_out { get; set; }
    public int late_minutes { get; set; }
    public int undertime_minutes { get; set; }
    public string? notes { get; set; }
    public long? schedule_id { get; set; }
    public long? term_id { get; set; }
    public int is_synced { get; set; }
    public int? database_log_id { get; set; }
    public int sync_attempts { get; set; }
    public string? last_sync_error { get; set; }
    public string? synced_at { get; set; }
    public string created_at { get; set; } = string.Empty;
    public string? updated_at { get; set; }
    // ReSharper restore InconsistentNaming

    // ── Convenience accessors for the sync engine ──
    public int EmployeeId => employee_id;
    public string RfidCode => rfid_code;
    public string LogTime => log_time;
    public string LogType => log_type;
    public string Date => date;
    public string? AttendanceStatus => attendance_status;
    public int IsLate => is_late;
    public int IsEarlyOut => is_early_out;
    public int LateMinutes => late_minutes;
    public int UndertimeMinutes => undertime_minutes;
    public string? Notes => notes;
    public long? ScheduleId => schedule_id;
    public long? TermId => term_id;
}

/// <summary>
/// Result of processing a single RFID tap through the sovereign
/// <see cref="HybridAttendanceSyncService.ProcessTapAsync"/> pipeline.
/// </summary>
public class SovereignTapResult
{
    public bool Success { get; init; }
    public int EmployeeNumericId { get; init; }
    public string EmployeeName { get; init; } = string.Empty;
    public string EmployeeId { get; init; } = string.Empty;
    public string Department { get; init; } = string.Empty;
    public string TapDirection { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; }

    /// <summary>True when the tap was persisted to database.</summary>
    public bool SavedOnline { get; init; }

    /// <summary>True when the tap was persisted to local SQLite (STIRAMS.db).</summary>
    public bool SavedLocally { get; init; }

    /// <summary>True when the tap is queued offline and awaiting sync.</summary>
    public bool IsQueued { get; init; }

    /// <summary>File path to the employee's photo (may be null).</summary>
    public string? PhotoPath { get; init; }

    /// <summary>The GUID assigned to the local record.</summary>
    public string? LocalGuid { get; init; }

    /// <summary>Local Time In timestamp (from STIRAMS.db) — for the UI overview.</summary>
    public string? TimeInTimestamp { get; init; }

    /// <summary>Local Time Out timestamp (from STIRAMS.db) — for the UI overview.</summary>
    public string? TimeOutTimestamp { get; init; }

    public string? ErrorMessage { get; init; }

    public static SovereignTapResult Fail(string message) => new()
    {
        Success = false,
        ErrorMessage = message
    };
}

/// <summary>
/// Result of <see cref="HybridAttendanceSyncService.GetLatestLocalStatusAsync"/>.
/// Provides the employee's last tap action today and what the next action should be.
/// </summary>
public class LocalStatusResult
{
    /// <summary>The RFID code that was queried.</summary>
    public string Rfid { get; set; } = string.Empty;

    /// <summary>Today's date string (yyyy-MM-dd).</summary>
    public string Date { get; set; } = string.Empty;

    /// <summary>Employee ID resolved from the local record (null if no records).</summary>
    public int? EmployeeId { get; set; }

    /// <summary>The last recorded action: "IN", "OUT", or null (no records today).</summary>
    public string? LastAction { get; set; }

    /// <summary>Timestamp string of the last action (from SQLite TEXT column).</summary>
    public string? LastActionTime { get; set; }

    /// <summary>
    /// What the next tap should be:
    ///   "IN"        — first tap of the day
    ///   "OUT"       — last was IN, needs OUT
    ///   "COMPLETED" — both IN and OUT recorded, show overview screen
    /// </summary>
    public string NextAction { get; set; } = "IN";

    /// <summary>True when both IN and OUT have been recorded today.</summary>
    public bool IsCompleted { get; set; }

    /// <summary>Error message if the local query failed (null on success).</summary>
    public string? Error { get; set; }
}

/// <summary>
/// Result of <see cref="HybridAttendanceSyncService.GetEffectiveStatusAsync"/>.
/// Local-first state mirror for an employee's attendance status today.
/// Provides both Time In and Time Out timestamps for the UI overview.
/// </summary>
public class EffectiveStatusResult
{
    /// <summary>The employee ID that was queried.</summary>
    public int EmployeeId { get; set; }

    /// <summary>Today's date string (yyyy-MM-dd).</summary>
    public string Date { get; set; } = string.Empty;

    /// <summary>The last recorded action: "IN", "OUT", or null (no records today).</summary>
    public string? LastAction { get; set; }

    /// <summary>Timestamp string of the last action (from SQLite TEXT column).</summary>
    public string? LastActionTime { get; set; }

    /// <summary>
    /// What the next tap should be:
    ///   "IN"        — first tap of the day
    ///   "OUT"       — last was IN, must be forced to OUT
    ///   "COMPLETED" — both IN and OUT recorded, show overview
    /// </summary>
    public string NextAction { get; set; } = "IN";

    /// <summary>True when both IN and OUT have been recorded today.</summary>
    public bool IsCompleted { get; set; }

    /// <summary>Time In timestamp from STIRAMS.db (null if no IN record).</summary>
    public string? TimeInTimestamp { get; set; }

    /// <summary>Time Out timestamp from STIRAMS.db (null if no OUT record).</summary>
    public string? TimeOutTimestamp { get; set; }

    /// <summary>Error message if the local query failed (null on success).</summary>
    public string? Error { get; set; }
}
