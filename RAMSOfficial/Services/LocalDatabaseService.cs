using System.Diagnostics;
using System.IO;
using Microsoft.Data.Sqlite;
using Dapper;
using Npgsql;
using RAMSOfficial.Models;

namespace RAMSOfficial.Services;

/// <summary>
/// ENHANCED Local SQLite database - Complete database mirror stored in Documents folder
/// Syncs in real-time (every 1 second) when online
/// Works seamlessly offline with cached data
/// </summary>
public class LocalDatabaseService
{
    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly string _pendingLogsXmlPath;

    private sealed class PgColumnMeta
    {
        public string table_name { get; set; } = string.Empty;
        public string column_name { get; set; } = string.Empty;
        public string data_type { get; set; } = string.Empty;
        public string udt_name { get; set; } = string.Empty;
        public string is_nullable { get; set; } = "YES";
        public string? column_default { get; set; }
        public int ordinal_position { get; set; }
    }

    private sealed class PgPrimaryKeyMeta
    {
        public string table_name { get; set; } = string.Empty;
        public string column_name { get; set; } = string.Empty;
        public int ordinal_position { get; set; }
    }
    
    public LocalDatabaseService()
    {
        // Store database in LocalApplicationData for security (not user-accessible Documents)
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appFolder = Path.Combine(localAppData, "STI_Attendance", "Database");

        // Fallback: also check legacy Documents path and migrate if needed
        var legacyFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "RAMSOfficial", "Database");
        var legacyDbPath = Path.Combine(legacyFolder, "rams_offline.db");

        // Create folder if it doesn't exist
        if (!Directory.Exists(appFolder))
        {
            Directory.CreateDirectory(appFolder);
        }

        // Single offline database source of truth: STIRAMS.db
        _dbPath = Path.Combine(appFolder, "STIRAMS.db");
        _connectionString = $"Data Source={_dbPath};";
        _pendingLogsXmlPath = Path.Combine(appFolder, "pending_attendance_logs.xml");

        // Auto-migrate from legacy Documents path if the new path is empty
        if (!File.Exists(_dbPath) && File.Exists(legacyDbPath))
        {
            try
            {
                File.Copy(legacyDbPath, _dbPath, overwrite: false);
                Debug.WriteLine($"?? Migrated database from legacy path:");
                Debug.WriteLine($"   From: {legacyDbPath}");
                Debug.WriteLine($"   To:   {_dbPath}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"?? Legacy migration skipped: {ex.Message}");
            }
        }

        // Auto-migrate from STIRAMS.db (project Database folder) if local DB doesn't exist yet
        var stiraPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "Database", "STIRAMS.db");
        if (!File.Exists(_dbPath) && File.Exists(stiraPath))
        {
            try
            {
                File.Copy(stiraPath, _dbPath, overwrite: false);
                Debug.WriteLine($"?? Bootstrapped from STIRAMS.db:");
                Debug.WriteLine($"   From: {stiraPath}");
                Debug.WriteLine($"   To:   {_dbPath}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"?? STIRAMS.db bootstrap skipped: {ex.Message}");
            }
        }

        Debug.WriteLine($"");
        Debug.WriteLine($"????????????????????????????????????????????");
        Debug.WriteLine($"?? LOCAL DATABASE SERVICE (ENHANCED)");
        Debug.WriteLine($"   Database Path: {_dbPath}");
        Debug.WriteLine($"   Storage: LocalApplicationData (secured)");
        Debug.WriteLine($"   Mode: Complete database mirror");
        Debug.WriteLine($"????????????????????????????????????????????");
    }

    /// <summary>
    /// Replace <c>rams_offline.db</c> content using an existing SQLite database file
    /// (for example <c>STIRAMS.db</c>) via SQLite backup API.
    /// </summary>
    public async Task ReplaceWithDatabaseAsync(string sourceDbPath, bool ensureCompatibility = true)
    {
        if (string.IsNullOrWhiteSpace(sourceDbPath))
            throw new InvalidOperationException("Source database path is missing.");

        if (!File.Exists(sourceDbPath))
            throw new FileNotFoundException("Source database not found.", sourceDbPath);

        var sourceConnectionString = $"Data Source={sourceDbPath};";

        await using var source = new SqliteConnection(sourceConnectionString);
        await using var destination = new SqliteConnection(_connectionString);

        await source.OpenAsync();
        await destination.OpenAsync();

        await source.ExecuteAsync("PRAGMA busy_timeout=10000;");
        await destination.ExecuteAsync("PRAGMA busy_timeout=10000;");

        source.BackupDatabase(destination);

        if (ensureCompatibility)
        {
            // Ensure app-required local operational tables/columns still exist.
            await InitializeAsync();
        }
    }

    /// <summary>
    /// Writes a full XML snapshot of pending_attendance for audit/backup.
    /// SQLite remains the primary queue source of truth.
    /// </summary>
    private async Task MirrorPendingLogsToXmlAsync()
    {
        try
        {
            var pending = await GetPendingAttendanceAsync();
            await PendingAttendanceXmlMirrorService.WriteSnapshotAsync(_pendingLogsXmlPath, pending);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"⚠ Pending XML mirror failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Rebuilds <c>rams_offline.db</c> from the current live database PUBLIC schema.
    /// This overwrites the existing database file, then restores app-required
    /// operational tables via <see cref="InitializeAsync"/>.
    /// </summary>
    public async Task RebuildFromdatabaseSchemaAsync(string databaseConnectionString)
    {
        if (string.IsNullOrWhiteSpace(databaseConnectionString))
            throw new InvalidOperationException("database connection string is missing.");

        Debug.WriteLine("\n[LocalDB] RebuildFromdatabaseSchemaAsync started");

        var backupPath = $"{_dbPath}.bak_{DateTime.Now:yyyyMMdd_HHmmss}";

        // 1) Pull live schema metadata from database
        List<string> tableNames;
        List<PgColumnMeta> columns;
        List<PgPrimaryKeyMeta> primaryKeys;

        await using (var pg = new NpgsqlConnection(databaseConnectionString))
        {
            await pg.OpenAsync();

            tableNames = (await pg.QueryAsync<string>(@"
                SELECT table_name
                FROM information_schema.tables
                WHERE table_schema = 'public'
                  AND table_type = 'BASE TABLE'
                ORDER BY table_name;
            ")).ToList();

            columns = (await pg.QueryAsync<PgColumnMeta>(@"
                SELECT table_name, column_name, data_type, udt_name,
                       is_nullable, column_default, ordinal_position
                FROM information_schema.columns
                WHERE table_schema = 'public'
                ORDER BY table_name, ordinal_position;
            ")).ToList();

            primaryKeys = (await pg.QueryAsync<PgPrimaryKeyMeta>(@"
                SELECT tc.table_name, kcu.column_name, kcu.ordinal_position
                FROM information_schema.table_constraints tc
                JOIN information_schema.key_column_usage kcu
                  ON tc.constraint_name = kcu.constraint_name
                 AND tc.table_schema = kcu.table_schema
                WHERE tc.table_schema = 'public'
                  AND tc.constraint_type = 'PRIMARY KEY'
                ORDER BY tc.table_name, kcu.ordinal_position;
            ")).ToList();
        }

        if (tableNames.Count == 0)
            throw new InvalidOperationException("No tables found in database public schema.");

        // 2) In-place reset of existing DB (avoids file-delete lock issues)
        // 3) Create fresh sqlite schema from database metadata
        using (var sqlite = new SqliteConnection(_connectionString))
        {
            await sqlite.OpenAsync();
            await sqlite.ExecuteAsync("PRAGMA journal_mode=WAL;");
            await sqlite.ExecuteAsync("PRAGMA synchronous=NORMAL;");

            await sqlite.ExecuteAsync("PRAGMA busy_timeout=10000;");

            // Best-effort backup before destructive reset.
            try
            {
                var safeBackupPath = backupPath.Replace("'", "''");
                await sqlite.ExecuteAsync($"VACUUM INTO '{safeBackupPath}';");
                Debug.WriteLine($"[LocalDB] Backup created: {backupPath}");
            }
            catch (Exception bx)
            {
                Debug.WriteLine($"[LocalDB] Backup skipped: {bx.Message}");
            }

            // Drop all existing user tables with retry, then recreate schema.
            const int dropRetries = 5;
            var dropped = false;
            Exception? lastDropError = null;

            for (var attempt = 1; attempt <= dropRetries; attempt++)
            {
                try
                {
                    var existingTables = (await sqlite.QueryAsync<string>(@"
                        SELECT name
                        FROM sqlite_master
                        WHERE type = 'table'
                          AND name NOT LIKE 'sqlite_%';
                    ")).ToList();

                    await sqlite.ExecuteAsync("PRAGMA foreign_keys=OFF;");
                    await sqlite.ExecuteAsync("BEGIN IMMEDIATE;");

                    foreach (var tableToDrop in existingTables)
                        await sqlite.ExecuteAsync($"DROP TABLE IF EXISTS [{tableToDrop}];");

                    await sqlite.ExecuteAsync("COMMIT;");
                    await sqlite.ExecuteAsync("PRAGMA foreign_keys=ON;");
                    dropped = true;
                    break;
                }
                catch (Exception ex)
                {
                    lastDropError = ex;
                    try { await sqlite.ExecuteAsync("ROLLBACK;"); } catch { }
                    if (attempt < dropRetries)
                        await Task.Delay(500 * attempt);
                }
            }

            if (!dropped)
                throw new InvalidOperationException($"Failed to reset existing sqlite schema: {lastDropError?.Message}");

            foreach (var table in tableNames)
            {
                var tableColumns = columns
                    .Where(c => c.table_name == table)
                    .OrderBy(c => c.ordinal_position)
                    .ToList();

                if (tableColumns.Count == 0)
                    continue;

                var pkCols = primaryKeys
                    .Where(p => p.table_name == table)
                    .OrderBy(p => p.ordinal_position)
                    .Select(p => p.column_name)
                    .ToList();

                var columnDefs = new List<string>();

                foreach (var col in tableColumns)
                {
                    var sqliteType = MapPgTypeToSqlite(col.data_type, col.udt_name);
                    var isNotNull = string.Equals(col.is_nullable, "NO", StringComparison.OrdinalIgnoreCase);
                    var isSinglePk = pkCols.Count == 1 && string.Equals(pkCols[0], col.column_name, StringComparison.OrdinalIgnoreCase);

                    var def = $"[{col.column_name}] {sqliteType}";
                    if (isSinglePk)
                        def += " PRIMARY KEY";
                    if (isNotNull && !isSinglePk)
                        def += " NOT NULL";

                    columnDefs.Add(def);
                }

                if (pkCols.Count > 1)
                {
                    var compositePk = string.Join(", ", pkCols.Select(c => $"[{c}]"));
                    columnDefs.Add($"PRIMARY KEY ({compositePk})");
                }

                var ddl = $"CREATE TABLE IF NOT EXISTS [{table}] ({string.Join(", ", columnDefs)});";
                await sqlite.ExecuteAsync(ddl);
            }
        }

        // 4) Re-create app-required operational tables and compatibility columns
        await InitializeAsync();

        Debug.WriteLine($"[LocalDB] Rebuild complete. Tables cloned from database: {tableNames.Count}");
    }

    private static string MapPgTypeToSqlite(string dataType, string udtName)
    {
        var t = (udtName ?? dataType ?? string.Empty).ToLowerInvariant();

        return t switch
        {
            "int2" or "int4" or "int8" or "serial" or "bigserial"
                or "integer" or "smallint" or "bigint" => "INTEGER",
            "float4" or "float8" or "numeric" or "decimal" or "real" or "double precision" => "REAL",
            "bool" or "boolean" => "INTEGER",
            "date" or "timestamp" or "timestamptz" or "time" or "timetz" => "TEXT",
            "uuid" or "json" or "jsonb" or "bytea" => "TEXT",
            _ => "TEXT"
        };
    }
    
    // ═══════════════════ SQLite Int64 → .NET int safe casts ═══════════════════
    // SQLite stores ALL integers as Int64 (long). Dapper returns dynamic objects
    // with long values. Assigning a long to an int via dynamic binding throws
    // RuntimeBinderException. These helpers prevent that crash.

    private static int SafeInt(object? val) => val != null ? (int)(long)val : 0;
    private static int? SafeNullableInt(object? val) => val != null ? (int?)(long)val : null;
    private static bool SafeBool(object? val) => val != null && (long)val == 1;
    private static long? SafeNullableLong(object? val) => val != null ? (long?)val : null;

    /// <summary>
    /// Initialize local database schema - creates ALL tables from database
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            Debug.WriteLine($"?? Initializing local database schema...");

            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            // Enable WAL mode so the background sync timer and tap saves
            // can run concurrently without SQLITE_BUSY errors.
            await connection.ExecuteAsync("PRAGMA journal_mode=WAL;");
            await connection.ExecuteAsync("PRAGMA synchronous=NORMAL;");

            // 1?? Employees table (complete mirror)
            await connection.ExecuteAsync(@"
                CREATE TABLE IF NOT EXISTS employees (
                    employee_id INTEGER PRIMARY KEY,
                    rfid_code TEXT NOT NULL,
                    full_name TEXT NOT NULL,
                    school_id TEXT,
                    department TEXT,
                    department_id INTEGER,
                    email TEXT,
                    phone TEXT,
                    password TEXT,
                    schedule_time_in TEXT,
                    schedule_time_out TEXT,
                    employment_status TEXT,
                    employment_type TEXT,
                    employment_subtype TEXT,
                    hire_date TEXT,
                    start_date TEXT,
                    role TEXT,
                    employee_type TEXT,
                    staff_type TEXT,
                    is_reporting_staff INTEGER,
                    year_level INTEGER,
                    current_section TEXT,
                    current_semester INTEGER,
                    unique_employee_id TEXT,
                    verification_status TEXT,
                    verified_by INTEGER,
                    verified_at TEXT,
                    photo_path TEXT,
                    is_active INTEGER DEFAULT 1,
                    created_at TEXT,
                    updated_at TEXT,
                    synced_at TEXT
                );
            ");

            // Ensure legacy copied schemas include is_active before index creation.
            try { await connection.ExecuteAsync("ALTER TABLE employees ADD COLUMN is_active INTEGER DEFAULT 1"); }
            catch (SqliteException) { }

            await connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_employees_rfid ON employees(rfid_code)");
            await connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_employees_active ON employees(is_active)");

            // Migration: add password column if it doesn't exist (existing databases)
            try { await connection.ExecuteAsync("ALTER TABLE employees ADD COLUMN password TEXT"); }
            catch (SqliteException) { /* column already exists */ }
            try { await connection.ExecuteAsync("ALTER TABLE employees ADD COLUMN synced_at TEXT"); }
            catch (SqliteException) { /* column already exists */ }
            
            // 2?? Attendance Logs table (complete mirror)
            await connection.ExecuteAsync(@"
                CREATE TABLE IF NOT EXISTS attendance_logs (
                    log_id INTEGER PRIMARY KEY,
                    employee_id INTEGER NOT NULL,
                    rfid_code TEXT NOT NULL,
                    log_time TEXT NOT NULL,
                    log_type TEXT NOT NULL,
                    date TEXT NOT NULL,
                    attendance_status TEXT,
                    is_late INTEGER DEFAULT 0,
                    is_early_out INTEGER DEFAULT 0,
                    late_minutes INTEGER DEFAULT 0,
                    undertime_minutes INTEGER DEFAULT 0,
                    notes TEXT,
                    verified_by INTEGER,
                    verified_at TEXT,
                    is_admin_time INTEGER DEFAULT 0,
                    admin_time_reason TEXT,
                    admin_time_status TEXT,
                    is_holiday INTEGER DEFAULT 0,
                    is_suspended INTEGER DEFAULT 0,
                    is_online_class INTEGER DEFAULT 0,
                    schedule_id INTEGER,
                    term_id INTEGER,
                    created_at TEXT,
                    synced_at TEXT
                );

                CREATE INDEX IF NOT EXISTS idx_attendance_date ON attendance_logs(date);
                CREATE INDEX IF NOT EXISTS idx_attendance_employee ON attendance_logs(employee_id);
            ");

            // Migration: add missing attendance_logs columns for existing databases
            var attColMigrations = new[]
            {
                "ALTER TABLE attendance_logs ADD COLUMN verified_by INTEGER",
                "ALTER TABLE attendance_logs ADD COLUMN verified_at TEXT",
                "ALTER TABLE attendance_logs ADD COLUMN is_admin_time INTEGER DEFAULT 0",
                "ALTER TABLE attendance_logs ADD COLUMN admin_time_reason TEXT",
                "ALTER TABLE attendance_logs ADD COLUMN admin_time_status TEXT",
                "ALTER TABLE attendance_logs ADD COLUMN is_holiday INTEGER DEFAULT 0",
                "ALTER TABLE attendance_logs ADD COLUMN is_suspended INTEGER DEFAULT 0",
                "ALTER TABLE attendance_logs ADD COLUMN is_online_class INTEGER DEFAULT 0",
                "ALTER TABLE attendance_logs ADD COLUMN term_id INTEGER",
                "ALTER TABLE attendance_logs ADD COLUMN synced_at TEXT"
            };
            foreach (var sql in attColMigrations)
            {
                try { await connection.ExecuteAsync(sql); }
                catch (SqliteException) { /* column already exists */ }
            }
            
            // 3?? Class Schedules table
            await connection.ExecuteAsync(@"
                CREATE TABLE IF NOT EXISTS class_schedules (
                    schedule_id INTEGER PRIMARY KEY,
                    employee_id INTEGER NOT NULL,
                    subject_name TEXT,
                    subject_code TEXT,
                    section TEXT,
                    day_of_week TEXT,
                    start_time TEXT,
                    end_time TEXT,
                    room TEXT,
                    semester INTEGER,
                    school_year TEXT,
                    is_active INTEGER DEFAULT 1,
                    created_at TEXT,
                    updated_at TEXT,
                    synced_at TEXT
                );
            ");

            try { await connection.ExecuteAsync("ALTER TABLE class_schedules ADD COLUMN is_active INTEGER DEFAULT 1"); }
            catch (SqliteException) { }
            try { await connection.ExecuteAsync("ALTER TABLE class_schedules ADD COLUMN synced_at TEXT"); }
            catch (SqliteException) { }

            await connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_schedules_employee ON class_schedules(employee_id)");
            await connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_schedules_active ON class_schedules(is_active)");

            // 4?? Exam Schedules table
            await connection.ExecuteAsync(@"
                CREATE TABLE IF NOT EXISTS exam_schedules (
                    exam_schedule_id INTEGER PRIMARY KEY,
                    employee_id INTEGER NOT NULL,
                    subject_name TEXT,
                    section TEXT,
                    time_start TEXT,
                    time_end TEXT,
                    room_code TEXT,
                    exam_date TEXT,
                    day_of_week TEXT,
                    exam_type TEXT,
                    status TEXT,
                    is_active INTEGER DEFAULT 1,
                    created_at TEXT,
                    updated_at TEXT,
                    synced_at TEXT
                );
            ");
            var examColMigrations = new[]
            {
                "ALTER TABLE exam_schedules ADD COLUMN exam_type TEXT",
                "ALTER TABLE exam_schedules ADD COLUMN day_of_week TEXT",
                "ALTER TABLE exam_schedules ADD COLUMN status TEXT",
                "ALTER TABLE exam_schedules ADD COLUMN is_active INTEGER DEFAULT 1",
                "ALTER TABLE exam_schedules ADD COLUMN synced_at TEXT"
            };
            foreach (var sql in examColMigrations)
            {
                try { await connection.ExecuteAsync(sql); }
                catch (SqliteException) { /* column already exists */ }
            }

            await connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_exam_sched_employee ON exam_schedules(employee_id)");
            await connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_exam_sched_date ON exam_schedules(exam_date)");
            await connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_exam_sched_active ON exam_schedules(is_active)");
            
            // 5?? Holidays table
            await connection.ExecuteAsync(@"
                CREATE TABLE IF NOT EXISTS holidays (
                    holiday_id INTEGER PRIMARY KEY,
                    holiday_name TEXT NOT NULL,
                    holiday_date TEXT NOT NULL,
                    holiday_type TEXT,
                    is_reporting_day INTEGER DEFAULT 0,
                    affects_teaching_staff INTEGER DEFAULT 1,
                    affects_non_teaching_staff INTEGER DEFAULT 1,
                    is_active INTEGER DEFAULT 1,
                    created_at TEXT,
                    updated_at TEXT,
                    synced_at TEXT
                );
            ");

            try { await connection.ExecuteAsync("ALTER TABLE holidays ADD COLUMN is_active INTEGER DEFAULT 1"); }
            catch (SqliteException) { }
            try { await connection.ExecuteAsync("ALTER TABLE holidays ADD COLUMN synced_at TEXT"); }
            catch (SqliteException) { }

            await connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_holidays_date ON holidays(holiday_date)");
            await connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_holidays_active ON holidays(is_active)");
            
            // 6?? Substitute Teacher table
            await connection.ExecuteAsync(@"
                CREATE TABLE IF NOT EXISTS substitute_teacher (
                    substitute_id INTEGER PRIMARY KEY,
                    original_employee_id INTEGER NOT NULL,
                    substitute_employee_id INTEGER NOT NULL,
                    schedule_id INTEGER NOT NULL,
                    substitute_date TEXT NOT NULL,
                    start_time TEXT,
                    end_time TEXT,
                    reason TEXT,
                    status TEXT DEFAULT 'active',
                    is_active INTEGER DEFAULT 1,
                    created_at TEXT,
                    updated_at TEXT,
                    synced_at TEXT
                );
            ");

            try { await connection.ExecuteAsync("ALTER TABLE substitute_teacher ADD COLUMN is_active INTEGER DEFAULT 1"); }
            catch (SqliteException) { }
            try { await connection.ExecuteAsync("ALTER TABLE substitute_teacher ADD COLUMN synced_at TEXT"); }
            catch (SqliteException) { }

            await connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_substitute_date ON substitute_teacher(substitute_date)");
            await connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_substitute_employee ON substitute_teacher(substitute_employee_id)");
            
            // 7?? Pending Attendance table (offline logs waiting for upload)
            await connection.ExecuteAsync(@"
                CREATE TABLE IF NOT EXISTS pending_attendance (
                    local_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    employee_id INTEGER NOT NULL,
                    rfid_code TEXT NOT NULL,
                    log_time TEXT NOT NULL,
                    log_type TEXT NOT NULL,
                    date TEXT NOT NULL,
                    attendance_status TEXT,
                    is_late INTEGER DEFAULT 0,
                    is_early_out INTEGER DEFAULT 0,
                    late_minutes INTEGER DEFAULT 0,
                    undertime_minutes INTEGER DEFAULT 0,
                    notes TEXT,
                    verified_by INTEGER,
                    verified_at TEXT,
                    is_admin_time INTEGER DEFAULT 0,
                    admin_time_reason TEXT,
                    admin_time_status TEXT,
                    is_holiday INTEGER DEFAULT 0,
                    is_suspended INTEGER DEFAULT 0,
                    is_online_class INTEGER DEFAULT 0,
                    schedule_id INTEGER,
                    term_id INTEGER,
                    sync_status TEXT DEFAULT 'pending',
                    sync_attempts INTEGER DEFAULT 0,
                    last_sync_attempt TEXT,
                    sync_error TEXT,
                    created_at TEXT
                );
            ");

            // Migration: add missing pending_attendance columns for existing databases
            var pendColMigrations = new[]
            {
                "ALTER TABLE pending_attendance ADD COLUMN verified_by INTEGER",
                "ALTER TABLE pending_attendance ADD COLUMN verified_at TEXT",
                "ALTER TABLE pending_attendance ADD COLUMN is_admin_time INTEGER DEFAULT 0",
                "ALTER TABLE pending_attendance ADD COLUMN admin_time_reason TEXT",
                "ALTER TABLE pending_attendance ADD COLUMN admin_time_status TEXT",
                "ALTER TABLE pending_attendance ADD COLUMN is_holiday INTEGER DEFAULT 0",
                "ALTER TABLE pending_attendance ADD COLUMN is_suspended INTEGER DEFAULT 0",
                "ALTER TABLE pending_attendance ADD COLUMN is_online_class INTEGER DEFAULT 0",
                "ALTER TABLE pending_attendance ADD COLUMN term_id INTEGER"
            };
            foreach (var sql in pendColMigrations)
            {
                try { await connection.ExecuteAsync(sql); }
                catch (SqliteException) { /* column already exists */ }
            }

            // 8?? Sync metadata table — tracks per-table sync timestamps for delta sync
            await connection.ExecuteAsync(@"
                CREATE TABLE IF NOT EXISTS sync_metadata (
                    table_name TEXT PRIMARY KEY,
                    last_full_sync TEXT,
                    last_delta_sync TEXT,
                    record_count INTEGER DEFAULT 0
                );
            ");

            // 9?? Employee current status — fast toggle tracking for IN/OUT
            await connection.ExecuteAsync(@"
                CREATE TABLE IF NOT EXISTS employee_current_status (
                    employee_id INTEGER PRIMARY KEY,
                    last_tap_type TEXT NOT NULL DEFAULT 'OUT',
                    last_tap_time TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
            ");

            // 10?? Migration: convert legacy TEXT sync_status → INTEGER
            //    0 = synced, 1 = pending, 2 = error (see SyncStatusCode)
            await connection.ExecuteAsync(@"
                UPDATE pending_attendance SET sync_status = 0 WHERE sync_status = 'synced';
                UPDATE pending_attendance SET sync_status = 1 WHERE sync_status = 'pending';
                UPDATE pending_attendance SET sync_status = 1 WHERE sync_status = 'syncing';
                UPDATE pending_attendance SET sync_status = 2 WHERE sync_status = 'failed';
            ");

            Debug.WriteLine($"Local database schema initialized");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error initializing local database:");
            Debug.WriteLine($"   Message: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Get recent attendance logs for today, joining with employees for display data.
    /// Includes both synced logs and pending (offline) logs.
    /// </summary>
    public async Task<List<RecentAttendanceItem>> GetRecentAttendanceAsync(int limit = 10)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var today = TimeChangerWindow.GetCurrentTime().ToString("yyyy-MM-dd");

            var items = await connection.QueryAsync<dynamic>(@"
                SELECT 
                    a.employee_id,
                    e.full_name,
                    e.department,
                    a.log_type,
                    a.log_time,
                    a.attendance_status,
                    e.photo_path
                FROM (
                    SELECT employee_id, log_type, log_time, attendance_status
                    FROM attendance_logs WHERE date = @Today
                    UNION ALL
                    SELECT employee_id, log_type, log_time, attendance_status
                    FROM pending_attendance WHERE date = @Today
                      AND sync_status NOT IN (@Error, @FailedValidation)
                ) a
                JOIN employees e ON a.employee_id = e.employee_id AND e.is_active = 1
                ORDER BY a.log_time DESC
                LIMIT @Limit
            ", new { Today = today, Limit = limit, Error = SyncStatusCode.Error, FailedValidation = SyncStatusCode.FailedValidation });

            return items.Select(r =>
            {
                DateTime logTime = DateTime.Now;
                if (r.log_time != null)
                    DateTime.TryParse(r.log_time.ToString(), out logTime);

                return new RecentAttendanceItem
                {
                    EmployeeId = (int)(long)r.employee_id,
                    FullName = (string?)r.full_name ?? "",
                    Department = (string?)r.department ?? "",
                    Initial = string.IsNullOrEmpty((string?)r.full_name)
                        ? "?"
                        : ((string)r.full_name)[..1].ToUpper(),
                    LogType = (string?)r.log_type ?? "",
                    LogTime = logTime,
                    AttendanceStatus = (string?)r.attendance_status ?? "present",
                    PhotoPath = (string?)r.photo_path
                };
            }).ToList();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error getting recent attendance from local DB: {ex.Message}");
            return new List<RecentAttendanceItem>();
        }
    }

    /// <summary>
    /// Overwrite employees table with fresh data from database
    /// </summary>
    public async Task OverwriteEmployeesAsync(List<Employee> employees)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            
            using var transaction = connection.BeginTransaction();
            
            // Delete all existing records
            await connection.ExecuteAsync("DELETE FROM employees", transaction: transaction);
            
            // Insert fresh data (INSERT OR REPLACE handles any duplicates in the source data)
            foreach (var emp in employees)
            {
                await connection.ExecuteAsync(@"
                    INSERT OR REPLACE INTO employees (
                        employee_id, rfid_code, full_name, school_id, department,
                        department_id, email, phone, password, schedule_time_in, schedule_time_out,
                        employment_status, employment_type, employment_subtype, hire_date, start_date,
                        role, employee_type, staff_type, is_reporting_staff, year_level,
                        current_section, current_semester, unique_employee_id,
                        verification_status, verified_by, verified_at, photo_path,
                        is_active, created_at, updated_at, synced_at
                    ) VALUES (
                        @EmployeeId, @RfidCode, @FullName, @SchoolId, @Department,
                        @DepartmentId, @Email, @Phone, @Password, @ScheduleTimeIn, @ScheduleTimeOut,
                        @EmploymentStatus, @EmploymentType, @EmploymentSubtype, @HireDate, @StartDate,
                        @Role, @EmployeeType, @StaffType, @IsReportingStaff, @YearLevel,
                        @CurrentSection, @CurrentSemester, @UniqueEmployeeId,
                        @VerificationStatus, @VerifiedBy, @VerifiedAt, @PhotoPath,
                        @IsActive, @CreatedAt, @UpdatedAt, @SyncedAt
                    )
                 ", new
                {
                    emp.EmployeeId,
                    emp.RfidCode,
                    emp.FullName,
                    emp.SchoolId,
                    emp.Department,
                    emp.DepartmentId,
                    emp.Email,
                    emp.Phone,
                    emp.Password,
                    ScheduleTimeIn = emp.ScheduleTimeIn.ToString(@"hh\:mm\:ss"),
                    ScheduleTimeOut = emp.ScheduleTimeOut.ToString(@"hh\:mm\:ss"),
                    emp.EmploymentStatus,
                    emp.EmploymentType,
                    emp.EmploymentSubtype,
                    HireDate = emp.HireDate.ToString("yyyy-MM-dd"),
                    StartDate = emp.StartDate?.ToString("yyyy-MM-dd"),
                    emp.Role,
                    emp.EmployeeType,
                    emp.StaffType,
                    IsReportingStaff = (emp.IsReportingStaff ?? false) ? 1 : 0,
                    emp.YearLevel,
                    emp.CurrentSection,
                    emp.CurrentSemester,
                    emp.UniqueEmployeeId,
                    emp.VerificationStatus,
                    emp.VerifiedBy,
                    VerifiedAt = emp.VerifiedAt?.ToString("yyyy-MM-dd HH:mm:ss"),
                    emp.PhotoPath,
                    IsActive = emp.IsActive ? 1 : 0,
                    CreatedAt = emp.CreatedAt?.ToString("yyyy-MM-dd HH:mm:ss"),
                    UpdatedAt = emp.UpdatedAt?.ToString("yyyy-MM-dd HH:mm:ss"),
                    SyncedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                }, transaction: transaction);
            }
            
            transaction.Commit();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? Error overwriting employees: {ex.Message}");
            throw;
        }
    }
    
    /// <summary>
    /// Overwrite attendance logs table with recent data from database
    /// </summary>
    public async Task OverwriteAttendanceLogsAsync(List<AttendanceLog> logs)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            using var transaction = connection.BeginTransaction();

            // Delete all existing records
            await connection.ExecuteAsync("DELETE FROM attendance_logs", transaction: transaction);

            // Get set of valid employee IDs from local employees table
            var validEmployeeIds = (await connection.QueryAsync<int>(
                "SELECT employee_id FROM employees WHERE is_active = 1",
                transaction: transaction)).ToHashSet();

            int inserted = 0, skipped = 0;

            // Insert fresh data — skip rows with invalid employee_id
            foreach (var log in logs)
            {
                // Validate: employee_id must be non-null, non-zero, and exist locally
                if (log.EmployeeId is null or 0)
                {
                    skipped++;
                    continue;
                }

                if (!validEmployeeIds.Contains(log.EmployeeId.Value))
                {
                    skipped++;
                    continue;
                }

                try
                {
                    await connection.ExecuteAsync(@"
                        INSERT OR REPLACE INTO attendance_logs (
                            log_id, employee_id, rfid_code, log_time, log_type, date,
                            attendance_status, is_late, is_early_out, late_minutes,
                            undertime_minutes, notes, verified_by, verified_at,
                            is_admin_time, admin_time_reason, admin_time_status,
                            is_holiday, is_suspended, is_online_class,
                            schedule_id, term_id, created_at, synced_at
                        ) VALUES (
                            @LogId, @EmployeeId, @RfidCode, @LogTime, @LogType, @Date,
                            @AttendanceStatus, @IsLate, @IsEarlyOut, @LateMinutes,
                            @UndertimeMinutes, @Notes, @VerifiedBy, @VerifiedAt,
                            @IsAdminTime, @AdminTimeReason, @AdminTimeStatus,
                            @IsHoliday, @IsSuspended, @IsOnlineClass,
                            @ScheduleId, @TermId, @CreatedAt, @SyncedAt
                        )
                    ", new
                    {
                        log.LogId,
                        log.EmployeeId,
                        RfidCode = log.RfidCode ?? "",
                        LogTime = log.LogTime.ToString("yyyy-MM-dd HH:mm:ss"),
                        LogType = log.LogType ?? "IN",
                        Date = log.Date.ToString("yyyy-MM-dd"),
                        log.AttendanceStatus,
                        IsLate = (log.IsLate ?? false) ? 1 : 0,
                        IsEarlyOut = (log.IsEarlyOut ?? false) ? 1 : 0,
                        log.LateMinutes,
                        log.UndertimeMinutes,
                        log.Notes,
                        log.VerifiedBy,
                        VerifiedAt = log.VerifiedAt?.ToString("yyyy-MM-dd HH:mm:ss"),
                        IsAdminTime = (log.IsAdminTime ?? false) ? 1 : 0,
                        log.AdminTimeReason,
                        log.AdminTimeStatus,
                        IsHoliday = (log.IsHoliday ?? false) ? 1 : 0,
                        IsSuspended = (log.IsSuspended ?? false) ? 1 : 0,
                        IsOnlineClass = (log.IsOnlineClass ?? false) ? 1 : 0,
                        log.ScheduleId,
                        log.TermId,
                        CreatedAt = log.CreatedAt?.ToString("yyyy-MM-dd HH:mm:ss"),
                        SyncedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                    }, transaction: transaction);

                    inserted++;
                }
                catch (Microsoft.Data.Sqlite.SqliteException sqlEx)
                {
                    Debug.WriteLine($"⚠ Skipping log {log.LogId}: {sqlEx.Message}");
                    skipped++;
                }
            }

            transaction.Commit();

            if (skipped > 0)
                Debug.WriteLine($"⚠ Attendance sync: {inserted} inserted, {skipped} skipped (invalid employee_id or constraint)");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"✗ Error overwriting attendance logs: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Insert a single already-synced attendance log into the local mirror.
    /// Called by <see cref="DataResolver"/> after a successful database write
    /// so the record is immediately available for offline queries.
    /// Uses INSERT OR REPLACE to avoid duplicates.
    /// </summary>
    public async Task InsertSyncedAttendanceLogAsync(AttendanceLog log)
    {
        try
        {
            // Guard: skip if employee_id is invalid
            if (log.EmployeeId is null or 0)
            {
                Debug.WriteLine($"⚠ InsertSyncedAttendanceLog skipped: EmployeeId is {log.EmployeeId}");
                return;
            }

            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            await connection.ExecuteAsync(@"
                INSERT OR REPLACE INTO attendance_logs (
                    log_id, employee_id, rfid_code, log_time, log_type, date,
                    attendance_status, is_late, is_early_out, late_minutes,
                    undertime_minutes, notes, verified_by, verified_at,
                    is_admin_time, admin_time_reason, admin_time_status,
                    is_holiday, is_suspended, is_online_class,
                    schedule_id, term_id, created_at, synced_at
                ) VALUES (
                    @LogId, @EmployeeId, @RfidCode, @LogTime, @LogType, @Date,
                    @AttendanceStatus, @IsLate, @IsEarlyOut, @LateMinutes,
                    @UndertimeMinutes, @Notes, @VerifiedBy, @VerifiedAt,
                    @IsAdminTime, @AdminTimeReason, @AdminTimeStatus,
                    @IsHoliday, @IsSuspended, @IsOnlineClass,
                    @ScheduleId, @TermId, @CreatedAt, @SyncedAt
                )
            ", new
            {
                log.LogId,
                log.EmployeeId,
                log.RfidCode,
                LogTime = log.LogTime.ToString("yyyy-MM-dd HH:mm:ss"),
                log.LogType,
                Date = log.Date.ToString("yyyy-MM-dd"),
                log.AttendanceStatus,
                IsLate = (log.IsLate ?? false) ? 1 : 0,
                IsEarlyOut = (log.IsEarlyOut ?? false) ? 1 : 0,
                log.LateMinutes,
                log.UndertimeMinutes,
                log.Notes,
                log.VerifiedBy,
                VerifiedAt = log.VerifiedAt?.ToString("yyyy-MM-dd HH:mm:ss"),
                IsAdminTime = (log.IsAdminTime ?? false) ? 1 : 0,
                log.AdminTimeReason,
                log.AdminTimeStatus,
                IsHoliday = (log.IsHoliday ?? false) ? 1 : 0,
                IsSuspended = (log.IsSuspended ?? false) ? 1 : 0,
                IsOnlineClass = (log.IsOnlineClass ?? false) ? 1 : 0,
                log.ScheduleId,
                log.TermId,
                CreatedAt = log.CreatedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? now,
                SyncedAt = now
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? InsertSyncedAttendanceLogAsync error: {ex.Message}");
        }
    }

    /// <summary>
    /// Overwrite schedules table
    /// </summary>
    public async Task OverwriteSchedulesAsync(List<dynamic> schedules)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            using var transaction = connection.BeginTransaction();
            connection.Execute("DELETE FROM class_schedules", transaction: transaction);

            var syncedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            foreach (IDictionary<string, object> schedule in schedules)
            {
                // Inject synced_at since database rows don't have it
                schedule["synced_at"] = syncedAt;

                connection.Execute(@"
                    INSERT OR REPLACE INTO class_schedules (
                        schedule_id, employee_id, subject_name, subject_code, section,
                        day_of_week, start_time, end_time, room, semester, school_year,
                        is_active, created_at, updated_at, synced_at
                    ) VALUES (
                        @schedule_id, @employee_id, @subject_name, @subject_code, @section,
                        @day_of_week, @start_time, @end_time, @room, @semester, @school_year,
                        @is_active, @created_at, @updated_at, @synced_at
                    )
                ", schedule, transaction: transaction);
            }

            transaction.Commit();

            Debug.WriteLine($"   Schedules overwritten: {schedules.Count} records");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? Error overwriting schedules: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Overwrite holidays table
    /// </summary>
    public async Task OverwriteHolidaysAsync(List<dynamic> holidays)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            using var transaction = connection.BeginTransaction();
            connection.Execute("DELETE FROM holidays", transaction: transaction);

            var syncedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            foreach (IDictionary<string, object> holiday in holidays)
            {
                holiday["synced_at"] = syncedAt;

                connection.Execute(@"
                    INSERT OR REPLACE INTO holidays (
                        holiday_id, holiday_name, holiday_date, holiday_type,
                        is_reporting_day, affects_teaching_staff, affects_non_teaching_staff,
                        is_active, created_at, updated_at, synced_at
                    ) VALUES (
                        @holiday_id, @holiday_name, @holiday_date, @holiday_type,
                        @is_reporting_day, @affects_teaching_staff, @affects_non_teaching_staff,
                        @is_active, @created_at, @updated_at, @synced_at
                    )
                ", holiday, transaction: transaction);
            }

            transaction.Commit();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? Error overwriting holidays: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Overwrite substitute teacher table
    /// </summary>
    public async Task OverwriteSubstitutesAsync(List<dynamic> substitutes)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            using var transaction = connection.BeginTransaction();
            connection.Execute("DELETE FROM substitute_teacher", transaction: transaction);

            var syncedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            foreach (IDictionary<string, object> sub in substitutes)
            {
                sub["synced_at"] = syncedAt;

                connection.Execute(@"
                    INSERT OR REPLACE INTO substitute_teacher (
                        substitute_id, original_employee_id, substitute_employee_id, schedule_id,
                        substitute_date, start_time, end_time, reason, status, is_active,
                        created_at, updated_at, synced_at
                    ) VALUES (
                        @substitute_id, @original_employee_id, @substitute_employee_id, @schedule_id,
                        @substitute_date, @start_time, @end_time, @reason, @status, @is_active,
                        @created_at, @updated_at, @synced_at
                    )
                ", sub, transaction: transaction);
            }

            transaction.Commit();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? Error overwriting substitutes: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Cache employee data from database to local database
    /// Called when online to sync employee information
    /// </summary>
    public async Task<int> CacheEmployeesAsync(List<Employee> employees)
    {
        try
        {
            Debug.WriteLine($"");
            Debug.WriteLine($"?? Caching {employees.Count} employees to local database...");

            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var syncedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var count = 0;

            using var transaction = connection.BeginTransaction();

            foreach (var emp in employees)
            {
                await UpsertEmployeeCoreAsync(connection, emp, syncedAt, transaction);
                count++;
            }

            transaction.Commit();

            Debug.WriteLine($"? Successfully cached {count} employees");
            return count;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? Error caching employees:");
            Debug.WriteLine($"   Message: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Immediately upsert a single employee into the local SQLite cache.
    /// Called when an online lookup finds an employee who may be missing locally —
    /// prevents the "Unknown RFID Card" error on the next offline tap.
    /// </summary>
    public async Task UpsertEmployeeAsync(Employee emp)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var syncedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            await UpsertEmployeeCoreAsync(connection, emp, syncedAt);

            Debug.WriteLine($"? Upserted employee {emp.FullName} (ID: {emp.EmployeeId}) into local cache");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? Error upserting employee to local DB: {ex.Message}");
        }
    }

    /// <summary>
    /// Core INSERT OR REPLACE for a single employee, reused by both bulk and single-upsert paths.
    /// </summary>
    private static async Task UpsertEmployeeCoreAsync(
        SqliteConnection connection, Employee emp, string syncedAt,
        SqliteTransaction? transaction = null)
    {
        await connection.ExecuteAsync(@"
            INSERT OR REPLACE INTO employees (
                employee_id, rfid_code, full_name, school_id, department, department_id,
                email, phone, password, schedule_time_in, schedule_time_out, employment_status,
                employment_type, employment_subtype, hire_date, start_date, role,
                employee_type, staff_type, is_reporting_staff, year_level, current_section,
                current_semester, unique_employee_id, verification_status, verified_by,
                verified_at, photo_path, is_active, created_at, updated_at, synced_at
            ) VALUES (
                @EmployeeId, @RfidCode, @FullName, @SchoolId, @Department, @DepartmentId,
                @Email, @Phone, @Password, @ScheduleTimeIn, @ScheduleTimeOut, @EmploymentStatus,
                @EmploymentType, @EmploymentSubtype, @HireDate, @StartDate, @Role,
                @EmployeeType, @StaffType, @IsReportingStaff, @YearLevel, @CurrentSection,
                @CurrentSemester, @UniqueEmployeeId, @VerificationStatus, @VerifiedBy,
                @VerifiedAt, @PhotoPath, @IsActive, @CreatedAt, @UpdatedAt, @SyncedAt
            )
        ", new
        {
            emp.EmployeeId,
            emp.RfidCode,
            emp.FullName,
            emp.SchoolId,
            emp.Department,
            emp.DepartmentId,
            emp.Email,
            emp.Phone,
            emp.Password,
            ScheduleTimeIn = emp.ScheduleTimeIn.ToString(),
            ScheduleTimeOut = emp.ScheduleTimeOut.ToString(),
            emp.EmploymentStatus,
            emp.EmploymentType,
            emp.EmploymentSubtype,
            HireDate = emp.HireDate.ToString("yyyy-MM-dd"),
            StartDate = emp.StartDate?.ToString("yyyy-MM-dd"),
            emp.Role,
            emp.EmployeeType,
            emp.StaffType,
            IsReportingStaff = (emp.IsReportingStaff ?? false) ? 1 : 0,
            emp.YearLevel,
            emp.CurrentSection,
            emp.CurrentSemester,
            emp.UniqueEmployeeId,
            emp.VerificationStatus,
            emp.VerifiedBy,
            VerifiedAt = emp.VerifiedAt?.ToString("yyyy-MM-dd HH:mm:ss"),
            emp.PhotoPath,
            IsActive = emp.IsActive ? 1 : 0,
            CreatedAt = emp.CreatedAt?.ToString("yyyy-MM-dd HH:mm:ss"),
            UpdatedAt = emp.UpdatedAt?.ToString("yyyy-MM-dd HH:mm:ss"),
            SyncedAt = syncedAt
        }, transaction);
    }

    // ═══════════════════════════════════════════════════════════
    // SYNC METADATA — tracks per-table sync timestamps for delta
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// Get the last successful sync timestamp for a table.
    /// Returns null if the table has never been synced.
    /// </summary>
    public async Task<DateTime?> GetLastSyncTimeAsync(string tableName)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            var ts = await connection.ExecuteScalarAsync<string?>(
                "SELECT last_delta_sync FROM sync_metadata WHERE table_name = @T",
                new { T = tableName });
            return ts != null ? DateTime.Parse(ts) : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Record a completed sync with a timestamp and row count.
    /// </summary>
    public async Task SetLastSyncTimeAsync(string tableName, DateTime timestamp, int count, bool isFull = false)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            var now = timestamp.ToString("yyyy-MM-dd HH:mm:ss");
            await connection.ExecuteAsync(@"
                INSERT INTO sync_metadata (table_name, last_full_sync, last_delta_sync, record_count)
                VALUES (@T, @Full, @Delta, @C)
                ON CONFLICT(table_name) DO UPDATE SET
                    last_delta_sync = @Delta,
                    last_full_sync  = CASE WHEN @IsFull = 1 THEN @Full ELSE last_full_sync END,
                    record_count    = @C
            ", new { T = tableName, Full = now, Delta = now, C = count, IsFull = isFull ? 1 : 0 });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? Error saving sync metadata for {tableName}: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════════════════════════
    // SAFE UPSERT — replaces destructive DELETE+INSERT methods
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// Safe employee upsert that never deletes existing records.
    /// Uses INSERT OR REPLACE so locally-cached employees survive
    /// even if a database fetch returns a partial result.
    /// <para>
    /// If <paramref name="isFullSync"/> is true, any local employees
    /// whose IDs are NOT in the incoming set are soft-deactivated
    /// (is_active = 0) so stale records don't produce false positives.
    /// </para>
    /// </summary>
    public async Task<int> SafeUpsertEmployeesAsync(List<Employee> employees, bool isFullSync = false)
    {
        if (employees.Count == 0) return 0;

        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var syncedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var count = 0;

            using var transaction = connection.BeginTransaction();

            foreach (var emp in employees)
            {
                await UpsertEmployeeCoreAsync(connection, emp, syncedAt, transaction);
                count++;
            }

            // Reconcile: soft-deactivate employees removed from database
            if (isFullSync && employees.Count >= 1)
            {
                var activeIds = employees.Select(e => e.EmployeeId).ToList();
                var idParams = string.Join(",", activeIds);
                var deactivated = await connection.ExecuteAsync($@"
                    UPDATE employees SET is_active = 0, synced_at = @SyncedAt
                    WHERE is_active = 1 AND employee_id NOT IN ({idParams})
                ", new { SyncedAt = syncedAt }, transaction);

                if (deactivated > 0)
                    Debug.WriteLine($"   Reconciled: {deactivated} employees deactivated (removed from database)");
            }

            transaction.Commit();
            return count;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? Error in SafeUpsertEmployeesAsync: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Safe schedule upsert. For a full sync, deletes stale schedules
    /// not present in the incoming batch.
    /// </summary>
    public async Task<int> SafeUpsertSchedulesAsync(List<dynamic> schedules, bool isFullSync = false)
    {
        if (schedules.Count == 0) return 0;

        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            using var transaction = connection.BeginTransaction();

            var syncedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var count = 0;
            var incomingIds = new List<int>();

            foreach (IDictionary<string, object> schedule in schedules)
            {
                schedule["synced_at"] = syncedAt;

                connection.Execute(@"
                    INSERT OR REPLACE INTO class_schedules (
                        schedule_id, employee_id, subject_name, subject_code, section,
                        day_of_week, start_time, end_time, room, semester, school_year,
                        is_active, created_at, updated_at, synced_at
                    ) VALUES (
                        @schedule_id, @employee_id, @subject_name, @subject_code, @section,
                        @day_of_week, @start_time, @end_time, @room, @semester, @school_year,
                        @is_active, @created_at, @updated_at, @synced_at
                    )
                ", schedule, transaction: transaction);

                if (schedule.TryGetValue("schedule_id", out var sid) && sid != null)
                    incomingIds.Add(Convert.ToInt32(sid));
                count++;
            }

            if (isFullSync && incomingIds.Count >= 1)
            {
                var idParams = string.Join(",", incomingIds);
                connection.Execute($@"
                    UPDATE class_schedules SET is_active = 0
                    WHERE is_active = 1 AND schedule_id NOT IN ({idParams})
                ", transaction: transaction);
            }

            transaction.Commit();
            return count;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? Error in SafeUpsertSchedulesAsync: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// Safe holiday upsert.
    /// </summary>
    public async Task<int> SafeUpsertHolidaysAsync(List<dynamic> holidays, bool isFullSync = false)
    {
        if (holidays.Count == 0) return 0;

        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            using var transaction = connection.BeginTransaction();

            var syncedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var count = 0;
            var incomingIds = new List<int>();

            foreach (IDictionary<string, object> holiday in holidays)
            {
                holiday["synced_at"] = syncedAt;

                connection.Execute(@"
                    INSERT OR REPLACE INTO holidays (
                        holiday_id, holiday_name, holiday_date, holiday_type,
                        is_reporting_day, affects_teaching_staff, affects_non_teaching_staff,
                        is_active, created_at, updated_at, synced_at
                    ) VALUES (
                        @holiday_id, @holiday_name, @holiday_date, @holiday_type,
                        @is_reporting_day, @affects_teaching_staff, @affects_non_teaching_staff,
                        @is_active, @created_at, @updated_at, @synced_at
                    )
                ", holiday, transaction: transaction);

                if (holiday.TryGetValue("holiday_id", out var hid) && hid != null)
                    incomingIds.Add(Convert.ToInt32(hid));
                count++;
            }

            if (isFullSync && incomingIds.Count >= 1)
            {
                var idParams = string.Join(",", incomingIds);
                connection.Execute($@"
                    UPDATE holidays SET is_active = 0
                    WHERE is_active = 1 AND holiday_id NOT IN ({idParams})
                ", transaction: transaction);
            }

            transaction.Commit();
            return count;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? Error in SafeUpsertHolidaysAsync: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// Safe substitute teacher upsert.
    /// </summary>
    public async Task<int> SafeUpsertSubstitutesAsync(List<dynamic> substitutes, bool isFullSync = false)
    {
        if (substitutes.Count == 0) return 0;

        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            using var transaction = connection.BeginTransaction();

            var syncedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var count = 0;
            var incomingIds = new List<int>();

            foreach (IDictionary<string, object> sub in substitutes)
            {
                sub["synced_at"] = syncedAt;

                connection.Execute(@"
                    INSERT OR REPLACE INTO substitute_teacher (
                        substitute_id, original_employee_id, substitute_employee_id, schedule_id,
                        substitute_date, start_time, end_time, reason, status, is_active,
                        created_at, updated_at, synced_at
                    ) VALUES (
                        @substitute_id, @original_employee_id, @substitute_employee_id, @schedule_id,
                        @substitute_date, @start_time, @end_time, @reason, @status, @is_active,
                        @created_at, @updated_at, @synced_at
                    )
                ", sub, transaction: transaction);

                if (sub.TryGetValue("substitute_id", out var sid) && sid != null)
                    incomingIds.Add(Convert.ToInt32(sid));
                count++;
            }

            if (isFullSync && incomingIds.Count >= 1)
            {
                var idParams = string.Join(",", incomingIds);
                connection.Execute($@"
                    UPDATE substitute_teacher SET is_active = 0
                    WHERE is_active = 1 AND substitute_id NOT IN ({idParams})
                ", transaction: transaction);
            }

            transaction.Commit();
            return count;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? Error in SafeUpsertSubstitutesAsync: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// Safe exam schedule upsert.
    /// </summary>
    public async Task<int> SafeUpsertExamSchedulesAsync(List<dynamic> examSchedules, bool isFullSync = false)
    {
        if (examSchedules.Count == 0) return 0;

        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            using var transaction = connection.BeginTransaction();

            var syncedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var count = 0;
            var incomingIds = new List<int>();

            foreach (IDictionary<string, object> exam in examSchedules)
            {
                exam["synced_at"] = syncedAt;

                connection.Execute(@"
                    INSERT OR REPLACE INTO exam_schedules (
                        exam_schedule_id, employee_id, subject_name, section,
                        time_start, time_end, room_code, exam_date,
                        day_of_week, exam_type, status, is_active,
                        created_at, updated_at, synced_at
                    ) VALUES (
                        @exam_schedule_id, @employee_id, @subject_name, @section,
                        @time_start, @time_end, @room_code, @exam_date,
                        @day_of_week, @exam_type, @status, @is_active,
                        @created_at, @updated_at, @synced_at
                    )
                ", exam, transaction: transaction);

                if (exam.TryGetValue("exam_schedule_id", out var eid) && eid != null)
                    incomingIds.Add(Convert.ToInt32(eid));
                count++;
            }

            if (isFullSync && incomingIds.Count >= 1)
            {
                var idParams = string.Join(",", incomingIds);
                connection.Execute($@"
                    UPDATE exam_schedules
                    SET is_active = 0,
                        status = 'inactive',
                        synced_at = @SyncedAt
                    WHERE is_active = 1
                      AND exam_schedule_id NOT IN ({idParams})
                ", new { SyncedAt = syncedAt }, transaction: transaction);
            }

            transaction.Commit();
            return count;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? Error in SafeUpsertExamSchedulesAsync: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// Get employee from local cache by RFID
    /// Used when offline
    /// </summary>
    public async Task<Employee?> GetEmployeeByRfidAsync(string rfidCode)
    {
        Debug.WriteLine("");
        Debug.WriteLine("???????????????????????????????????????????????????????????");
        Debug.WriteLine("?? OFFLINE MODE - GetEmployeeByRfidAsync DEBUG");
        Debug.WriteLine("???????????????????????????????????????????????????????????");
        Debug.WriteLine($"?? Input RFID Code: '{rfidCode}'");
        Debug.WriteLine($"?? RFID Length: {rfidCode?.Length ?? 0}");
        Debug.WriteLine($"?? Database Path: {_dbPath}");
        
        try
        {
            // Check if database file exists
            if (!File.Exists(_dbPath))
            {
                Debug.WriteLine($"? ERROR: Database file does not exist!");
                Debug.WriteLine($"   Expected path: {_dbPath}");
                return null;
            }
            
            var fileInfo = new FileInfo(_dbPath);
            Debug.WriteLine($"? Database file exists");
            Debug.WriteLine($"   Size: {fileInfo.Length / 1024.0:F2} KB");
            Debug.WriteLine($"   Last Modified: {fileInfo.LastWriteTime}");
            
            using var connection = new SqliteConnection(_connectionString);
            Debug.WriteLine($"");
            Debug.WriteLine($"?? Opening database connection...");
            await connection.OpenAsync();
            Debug.WriteLine($"? Connection opened successfully");
            
            // First, check total employee count
            Debug.WriteLine($"");
            Debug.WriteLine($"?? Checking total employees in database...");
            var totalCount = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employees");
            Debug.WriteLine($"   Total employees in table: {totalCount}");
            
            var activeCount = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employees WHERE is_active = 1");
            Debug.WriteLine($"   Active employees: {activeCount}");
            
            // Check if this specific RFID exists
            Debug.WriteLine($"");
            Debug.WriteLine($"?? Searching for RFID: '{rfidCode}'");
            var rfidExists = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM employees WHERE TRIM(rfid_code) = @RfidCode",
                new { RfidCode = rfidCode }
            );
            Debug.WriteLine($"   RFID match count: {rfidExists}");
            
            if (rfidExists == 0)
            {
                Debug.WriteLine($"? RFID NOT FOUND in database!");
                Debug.WriteLine($"");
                Debug.WriteLine($"?? Showing all RFIDs in database:");
                var allRfids = await connection.QueryAsync<dynamic>("SELECT employee_id, rfid_code, full_name FROM employees LIMIT 10");
                foreach (var emp in allRfids)
                {
                    Debug.WriteLine($"   ID: {emp.employee_id}, RFID: '{emp.rfid_code}', Name: {emp.full_name}");
                }
                Debug.WriteLine($"???????????????????????????????????????????????????????????");
                return null;
            }
            
            Debug.WriteLine($"? RFID found in database! Proceeding to fetch full employee data...");
            
            // Query without parsing TimeSpan - return dynamic first
            Debug.WriteLine($"");
            Debug.WriteLine($"?? Executing SELECT query...");
            var result = await connection.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT 
                    employee_id,
                    rfid_code,
                    full_name,
                    school_id,
                    department,
                    department_id,
                    email,
                    phone,
                    schedule_time_in,
                    schedule_time_out,
                    employment_status,
                    employment_type,
                    employment_subtype,
                    hire_date,
                    start_date,
                    role,
                    employee_type,
                    staff_type,
                    is_reporting_staff,
                    year_level,
                    current_section,
                    current_semester,
                    unique_employee_id,
                    verification_status,
                    verified_by,
                    verified_at,
                    photo_path,
                    is_active,
                    created_at,
                    updated_at
                FROM employees 
                WHERE TRIM(rfid_code) = @RfidCode
            ", new { RfidCode = rfidCode });
            
            if (result == null)
            {
                Debug.WriteLine($"? Query returned NULL (employee might be inactive)");
                Debug.WriteLine($"???????????????????????????????????????????????????????????");
                return null;
            }
            
            Debug.WriteLine($"? Query returned data!");
            Debug.WriteLine($"");
            Debug.WriteLine($"?? Raw Data Retrieved:");
            Debug.WriteLine($"   employee_id: {result.employee_id}");
            Debug.WriteLine($"   rfid_code: '{result.rfid_code}'");
            Debug.WriteLine($"   full_name: '{result.full_name}'");
            Debug.WriteLine($"   department: '{result.department}'");
            Debug.WriteLine($"   schedule_time_in: '{result.schedule_time_in}'");
            Debug.WriteLine($"   schedule_time_out: '{result.schedule_time_out}'");
            Debug.WriteLine($"   employment_status: '{result.employment_status}'");
            Debug.WriteLine($"   is_active: {result.is_active}");
            
            // Manually map to Employee object
            // CRITICAL: SQLite returns Int64 for ALL integer columns.
            // Use SafeInt/SafeNullableInt/SafeBool to prevent RuntimeBinderException.
            Debug.WriteLine($"");
            Debug.WriteLine($"?? Mapping to Employee object (safe Int64 casts)...");
            var employee = new Employee
            {
                EmployeeId = SafeInt(result.employee_id),
                RfidCode = (string?)result.rfid_code ?? string.Empty,
                FullName = (string?)result.full_name ?? string.Empty,
                SchoolId = (string?)result.school_id ?? string.Empty,
                Department = (string?)result.department ?? string.Empty,
                DepartmentId = SafeNullableInt(result.department_id),
                Email = (string?)result.email ?? string.Empty,
                Phone = (string?)result.phone ?? string.Empty,
                EmploymentStatus = (string?)result.employment_status,
                EmploymentType = (string?)result.employment_type,
                EmploymentSubtype = (string?)result.employment_subtype,
                Role = (string?)result.role,
                EmployeeType = (string?)result.employee_type,
                StaffType = (string?)result.staff_type,
                IsReportingStaff = SafeBool(result.is_reporting_staff),
                YearLevel = SafeNullableInt(result.year_level),
                CurrentSection = (string?)result.current_section,
                CurrentSemester = SafeNullableInt(result.current_semester),
                UniqueEmployeeId = (string?)result.unique_employee_id,
                VerificationStatus = (string?)result.verification_status,
                VerifiedBy = SafeNullableLong(result.verified_by),
                PhotoPath = (string?)result.photo_path,
                IsActive = SafeBool(result.is_active)
            };
            Debug.WriteLine($"? Basic fields mapped successfully (Int64-safe)");
            
            // Parse TimeSpan manually
            Debug.WriteLine($"");
            Debug.WriteLine($"? Parsing time fields...");
            if (result.schedule_time_in != null)
            {
                Debug.WriteLine($"   Parsing schedule_time_in: '{result.schedule_time_in}'");
                if (TimeSpan.TryParse(result.schedule_time_in.ToString(), out TimeSpan timeIn))
                {
                    employee.ScheduleTimeIn = timeIn;
                    Debug.WriteLine($"   ? Parsed as: {timeIn}");
                }
                else
                {
                    Debug.WriteLine($"   ?? Failed to parse schedule_time_in");
                }
            }
            
            if (result.schedule_time_out != null)
            {
                Debug.WriteLine($"   Parsing schedule_time_out: '{result.schedule_time_out}'");
                if (TimeSpan.TryParse(result.schedule_time_out.ToString(), out TimeSpan timeOut))
                {
                    employee.ScheduleTimeOut = timeOut;
                    Debug.WriteLine($"   ? Parsed as: {timeOut}");
                }
                else
                {
                    Debug.WriteLine($"   ?? Failed to parse schedule_time_out");
                }
            }
            
            // Parse dates manually
            Debug.WriteLine($"");
            Debug.WriteLine($"?? Parsing date fields...");
            if (result.hire_date != null)
            {
                if (DateTime.TryParse(result.hire_date.ToString(), out DateTime hireDate))
                {
                    employee.HireDate = hireDate;
                    Debug.WriteLine($"   ? hire_date: {hireDate:yyyy-MM-dd}");
                }
            }
            
            if (result.start_date != null)
            {
                if (DateTime.TryParse(result.start_date.ToString(), out DateTime startDate))
                {
                    employee.StartDate = startDate;
                    Debug.WriteLine($"   ? start_date: {startDate:yyyy-MM-dd}");
                }
            }
            
            if (result.verified_at != null)
            {
                if (DateTime.TryParse(result.verified_at.ToString(), out DateTime verifiedAt))
                {
                    employee.VerifiedAt = verifiedAt;
                }
            }
            
            if (result.created_at != null)
            {
                if (DateTime.TryParse(result.created_at.ToString(), out DateTime createdAt))
                {
                    employee.CreatedAt = createdAt;
                }
            }
            
            if (result.updated_at != null)
            {
                if (DateTime.TryParse(result.updated_at.ToString(), out DateTime updatedAt))
                {
                    employee.UpdatedAt = updatedAt;
                }
            }
            
            Debug.WriteLine($"");
            Debug.WriteLine($"???????????????????????????????????????????????????????????");
            Debug.WriteLine($"? EMPLOYEE FOUND IN LOCAL DATABASE (OFFLINE MODE)");
            Debug.WriteLine($"???????????????????????????????????????????????????????????");
            Debug.WriteLine($"   ID: {employee.EmployeeId}");
            Debug.WriteLine($"   Name: {employee.FullName}");
            Debug.WriteLine($"   RFID: {employee.RfidCode}");
            Debug.WriteLine($"   Department: {employee.Department}");
            Debug.WriteLine($"   Employment Status: {employee.EmploymentStatus}");
            Debug.WriteLine($"   Schedule: {employee.ScheduleTimeIn} - {employee.ScheduleTimeOut}");
            Debug.WriteLine($"???????????????????????????????????????????????????????????");
            Debug.WriteLine($"");
            
            return employee;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"");
            Debug.WriteLine($"???????????????????????????????????????????????????????????");
            Debug.WriteLine($"? EXCEPTION IN OFFLINE MODE - GetEmployeeByRfidAsync");
            Debug.WriteLine($"???????????????????????????????????????????????????????????");
            Debug.WriteLine($"Error Type: {ex.GetType().Name}");
            Debug.WriteLine($"Error Message: {ex.Message}");
            Debug.WriteLine($"");
            Debug.WriteLine($"Stack Trace:");
            Debug.WriteLine(ex.StackTrace);
            Debug.WriteLine($"???????????????????????????????????????????????????????????");
            Debug.WriteLine($"");
            return null;
        }
    }
    
    /// <summary>
    /// Get employee from local cache by employee ID
    /// Used when offline
    /// </summary>
    public async Task<Employee?> GetEmployeeByIdAsync(int employeeId)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            
            // Query without parsing TimeSpan - return dynamic first
            var result = await connection.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT 
                    employee_id,
                    rfid_code,
                    full_name,
                    school_id,
                    department,
                    department_id,
                    email,
                    phone,
                    schedule_time_in,
                    schedule_time_out,
                    employment_status,
                    employment_type,
                    employment_subtype,
                    hire_date,
                    start_date,
                    role,
                    employee_type,
                    staff_type,
                    is_reporting_staff,
                    year_level,
                    current_section,
                    current_semester,
                    unique_employee_id,
                    verification_status,
                    verified_by,
                    verified_at,
                    photo_path,
                    is_active,
                    created_at,
                    updated_at
                FROM employees 
                WHERE employee_id = @EmployeeId AND is_active = 1
            ", new { EmployeeId = employeeId });
            
            if (result == null)
            {
                Debug.WriteLine($"? No employee found with ID: {employeeId}");
                return null;
            }
            
            // Manually map to Employee object
            // CRITICAL: SQLite returns Int64 for ALL integer columns.
            // Use SafeInt/SafeNullableInt/SafeBool to prevent RuntimeBinderException.
            var employee = new Employee
            {
                EmployeeId = SafeInt(result.employee_id),
                RfidCode = (string?)result.rfid_code ?? string.Empty,
                FullName = (string?)result.full_name ?? string.Empty,
                SchoolId = (string?)result.school_id ?? string.Empty,
                Department = (string?)result.department ?? string.Empty,
                DepartmentId = SafeNullableInt(result.department_id),
                Email = (string?)result.email ?? string.Empty,
                Phone = (string?)result.phone ?? string.Empty,
                EmploymentStatus = (string?)result.employment_status,
                EmploymentType = (string?)result.employment_type,
                EmploymentSubtype = (string?)result.employment_subtype,
                Role = (string?)result.role,
                EmployeeType = (string?)result.employee_type,
                StaffType = (string?)result.staff_type,
                IsReportingStaff = SafeBool(result.is_reporting_staff),
                YearLevel = SafeNullableInt(result.year_level),
                CurrentSection = (string?)result.current_section,
                CurrentSemester = SafeNullableInt(result.current_semester),
                UniqueEmployeeId = (string?)result.unique_employee_id,
                VerificationStatus = (string?)result.verification_status,
                VerifiedBy = SafeNullableLong(result.verified_by),
                PhotoPath = (string?)result.photo_path,
                IsActive = SafeBool(result.is_active)
            };
            
            // Parse TimeSpan manually
            if (result.schedule_time_in != null)
            {
                if (TimeSpan.TryParse(result.schedule_time_in.ToString(), out TimeSpan timeIn))
                {
                    employee.ScheduleTimeIn = timeIn;
                }
            }
            
            if (result.schedule_time_out != null)
            {
                if (TimeSpan.TryParse(result.schedule_time_out.ToString(), out TimeSpan timeOut))
                {
                    employee.ScheduleTimeOut = timeOut;
                }
            }
            
            // Parse dates manually
            if (result.hire_date != null)
            {
                if (DateTime.TryParse(result.hire_date.ToString(), out DateTime hireDate))
                {
                    employee.HireDate = hireDate;
                }
            }
            
            if (result.start_date != null)
            {
                if (DateTime.TryParse(result.start_date.ToString(), out DateTime startDate))
                {
                    employee.StartDate = startDate;
                }
            }
            
            if (result.verified_at != null)
            {
                if (DateTime.TryParse(result.verified_at.ToString(), out DateTime verifiedAt))
                {
                    employee.VerifiedAt = verifiedAt;
                }
            }
            
            if (result.created_at != null)
            {
                if (DateTime.TryParse(result.created_at.ToString(), out DateTime createdAt))
                {
                    employee.CreatedAt = createdAt;
                }
            }
            
            if (result.updated_at != null)
            {
                if (DateTime.TryParse(result.updated_at.ToString(), out DateTime updatedAt))
                {
                    employee.UpdatedAt = updatedAt;
                }
            }
            
            Debug.WriteLine($"? Employee found in LocalDB by ID:");
            Debug.WriteLine($"   ID: {employee.EmployeeId}");
            Debug.WriteLine($"   Name: {employee.FullName}");
            Debug.WriteLine($"   RFID: {employee.RfidCode}");
            Debug.WriteLine($"   Department: {employee.Department}");
            
            return employee;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? Error getting employee by ID from local DB: {ex.Message}");
            Debug.WriteLine($"   Stack: {ex.StackTrace}");
            return null;
        }
    }

    /// <summary>
    /// Get the last tap type (IN or OUT) for an employee on a specific date.
    /// Checks both synced attendance_logs and unsent pending_attendance.
    /// </summary>
    public async Task<string> GetLastTapTypeForDateAsync(int employeeId, DateTime date)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var dateStr = date.ToString("yyyy-MM-dd");

            var result = await connection.QueryFirstOrDefaultAsync<string>(@"
                SELECT log_type FROM (
                    SELECT log_type, log_time FROM attendance_logs
                    WHERE employee_id = @EmpId AND date = @DateStr
                    UNION ALL
                    SELECT log_type, log_time FROM pending_attendance
                    WHERE employee_id = @EmpId AND date = @DateStr
                      AND sync_status NOT IN (@Error, @FailedValidation)
                ) combined
                ORDER BY log_time DESC
                LIMIT 1
            ", new { EmpId = employeeId, DateStr = dateStr, Error = SyncStatusCode.Error, FailedValidation = SyncStatusCode.FailedValidation });

            return result ?? "";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? Error getting last tap type from local DB: {ex.Message}");
            return "";
        }
    }

    /// <summary>
    /// Returns true when the employee has at least one active class OR exam schedule
    /// for the given date in the local mirror.
    /// </summary>
    public async Task<bool> HasAnyScheduleForDateAsync(int employeeId, DateTime date)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var dateStr = date.ToString("yyyy-MM-dd");
            var dayName = date.DayOfWeek.ToString();
            var dayNumber = ((int)date.DayOfWeek).ToString();

            var classCount = await connection.ExecuteScalarAsync<int>(@"
                SELECT COUNT(*)
                FROM class_schedules
                WHERE employee_id = @EmpId
                  AND is_active = 1
                  AND (
                    LOWER(COALESCE(day_of_week, '')) = LOWER(@DayName)
                    OR CAST(day_of_week AS TEXT) = @DayNumber
                  )
            ", new { EmpId = employeeId, DayName = dayName, DayNumber = dayNumber });

            if (classCount > 0) return true;

            var examCount = await connection.ExecuteScalarAsync<int>(@"
                SELECT COUNT(*)
                FROM exam_schedules
                WHERE employee_id = @EmpId
                  AND is_active = 1
                  AND (status IS NULL OR LOWER(status) IN ('active', 'scheduled', 'available', 'approved', 'published'))
                  AND (
                    date(exam_date) = date(@Date)
                    OR (
                        exam_date IS NULL
                        AND (
                            LOWER(COALESCE(day_of_week, '')) = LOWER(@DayName)
                            OR CAST(day_of_week AS TEXT) = @DayNumber
                        )
                    )
                  )
            ", new { EmpId = employeeId, Date = dateStr, DayName = dayName, DayNumber = dayNumber });

            return examCount > 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"⚠ HasAnyScheduleForDateAsync failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Get all attendance logs for an employee on a specific date from the local database.
    /// Includes both synced logs and pending logs.
    /// </summary>
    public async Task<IEnumerable<AttendanceLog>> GetTodayAttendanceForDateAsync(int employeeId, DateTime date)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var dateStr = date.ToString("yyyy-MM-dd");

            // Union synced logs + pending (offline) logs so that:
            //   1. The UI shows records immediately after an offline tap.
            //   2. The IN/OUT toggle logic sees the pending TIME-IN and won't
            //      block a legitimate TIME-OUT while offline.
            var logs = await connection.QueryAsync<AttendanceLog>(@"
                SELECT
                    log_id            AS LogId,
                    employee_id       AS EmployeeId,
                    rfid_code         AS RfidCode,
                    log_time          AS LogTime,
                    log_type          AS LogType,
                    date              AS Date,
                    attendance_status AS AttendanceStatus,
                    is_late           AS IsLate,
                    is_early_out      AS IsEarlyOut,
                    late_minutes      AS LateMinutes,
                    undertime_minutes AS UndertimeMinutes,
                    notes             AS Notes,
                    verified_by       AS VerifiedBy,
                    verified_at       AS VerifiedAt,
                    is_admin_time     AS IsAdminTime,
                    admin_time_reason AS AdminTimeReason,
                    admin_time_status AS AdminTimeStatus,
                    is_holiday        AS IsHoliday,
                    is_suspended      AS IsSuspended,
                    is_online_class   AS IsOnlineClass,
                    schedule_id       AS ScheduleId,
                    term_id           AS TermId,
                    created_at        AS CreatedAt
                FROM attendance_logs
                WHERE employee_id = @EmpId AND date = @DateStr
                UNION ALL
                SELECT
                    0,
                    employee_id, rfid_code,
                    log_time, log_type, date,
                    attendance_status, is_late,
                    is_early_out, late_minutes,
                    undertime_minutes, notes,
                    verified_by, verified_at,
                    is_admin_time, admin_time_reason,
                    admin_time_status, is_holiday,
                    is_suspended, is_online_class,
                    schedule_id, term_id, created_at
                FROM pending_attendance
                WHERE employee_id = @EmpId AND date = @DateStr
                  AND sync_status = @Pending
                ORDER BY log_time DESC
            ", new { EmpId = employeeId, DateStr = dateStr,
                     Error = SyncStatusCode.Error, FailedValidation = SyncStatusCode.FailedValidation,
                     Pending = SyncStatusCode.Pending });

            return logs;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? Error getting today attendance from local DB: {ex.Message}");
            return Enumerable.Empty<AttendanceLog>();
        }
    }

    /// <summary>
    /// Alias – returns the same result as <see cref="GetTodayAttendanceForDateAsync"/>
    /// using today's date.
    /// </summary>
    public Task<IEnumerable<AttendanceLog>> GetTodayAttendanceAsync(int employeeId)
        => GetTodayAttendanceForDateAsync(employeeId, DateTime.Today);

    /// <summary>
    /// Save attendance log to pending queue (offline mode)
    /// </summary>
    public async Task<long> SavePendingAttendanceAsync(
        int employeeId,
        string rfidCode,
        DateTime logTime,
        string logType,
        string attendanceStatus,
        bool isLate,
        bool isEarlyOut,
        int? lateMinutes,
        int? undertimeMinutes,
        string? notes,
        long? scheduleId,
        long? termId = null,
        bool isAdminTime = false,
        string? adminTimeReason = null,
        string? adminTimeStatus = null,
        bool isHoliday = false,
        bool isSuspended = false,
        bool isOnlineClass = false)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var localId = await connection.ExecuteScalarAsync<long>(@"
                INSERT INTO pending_attendance (
                    employee_id, rfid_code, log_time, log_type, date,
                    attendance_status, is_late, is_early_out, late_minutes,
                    undertime_minutes, notes, verified_by, verified_at,
                    is_admin_time, admin_time_reason, admin_time_status,
                    is_holiday, is_suspended, is_online_class,
                    schedule_id, term_id, sync_status, created_at
                ) VALUES (
                    @EmployeeId, @RfidCode, @LogTime, @LogType, @Date,
                    @AttendanceStatus, @IsLate, @IsEarlyOut, @LateMinutes,
                    @UndertimeMinutes, @Notes, NULL, NULL,
                    @IsAdminTime, @AdminTimeReason, @AdminTimeStatus,
                    @IsHoliday, @IsSuspended, @IsOnlineClass,
                    @ScheduleId, @TermId, @SyncStatus, @CreatedAt
                );
                SELECT last_insert_rowid();
            ", new
            {
                EmployeeId = employeeId,
                RfidCode = rfidCode,
                LogTime = logTime.ToString("yyyy-MM-dd HH:mm:ss"),
                LogType = logType,
                Date = logTime.ToString("yyyy-MM-dd"),
                AttendanceStatus = attendanceStatus,
                IsLate = isLate ? 1 : 0,
                IsEarlyOut = isEarlyOut ? 1 : 0,
                LateMinutes = lateMinutes,
                UndertimeMinutes = undertimeMinutes,
                Notes = notes,
                IsAdminTime = isAdminTime ? 1 : 0,
                AdminTimeReason = adminTimeReason,
                AdminTimeStatus = adminTimeStatus,
                IsHoliday = isHoliday ? 1 : 0,
                IsSuspended = isSuspended ? 1 : 0,
                IsOnlineClass = isOnlineClass ? 1 : 0,
                ScheduleId = scheduleId,
                TermId = termId,
                SyncStatus = SyncStatusCode.Pending,
                CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            });

            Debug.WriteLine($"?? Saved to pending queue: Local ID {localId}");

            await MirrorPendingLogsToXmlAsync();
            return localId;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? Error saving pending attendance: {ex.Message}");
            throw;
        }
    }
    
    /// <summary>
    /// Get all pending attendance logs (not yet synced to database)
    /// </summary>
    public async Task<List<PendingAttendanceLog>> GetPendingAttendanceAsync()
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            
            var pending = await connection.QueryAsync<PendingAttendanceLog>(@"
                SELECT
                    local_id           AS LocalId,
                    employee_id        AS EmployeeId,
                    rfid_code          AS RfidCode,
                    log_time           AS LogTime,
                    log_type           AS LogType,
                    date               AS Date,
                    attendance_status  AS AttendanceStatus,
                    is_late            AS IsLate,
                    is_early_out       AS IsEarlyOut,
                    late_minutes       AS LateMinutes,
                    undertime_minutes  AS UndertimeMinutes,
                    notes              AS Notes,
                    verified_by        AS VerifiedBy,
                    verified_at        AS VerifiedAt,
                    is_admin_time      AS IsAdminTime,
                    admin_time_reason  AS AdminTimeReason,
                    admin_time_status  AS AdminTimeStatus,
                    is_holiday         AS IsHoliday,
                    is_suspended       AS IsSuspended,
                    is_online_class    AS IsOnlineClass,
                    schedule_id        AS ScheduleId,
                    term_id            AS TermId,
                    created_at         AS CreatedAt,
                    sync_status        AS SyncStatus,
                    sync_attempts      AS SyncAttempts,
                    last_sync_attempt  AS LastSyncAttempt,
                    sync_error         AS SyncError
                FROM pending_attendance
                WHERE sync_status = @Pending
                ORDER BY created_at ASC
            ", new { Pending = SyncStatusCode.Pending });
            
            return pending.ToList();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? Error getting pending attendance: {ex.Message}");
            return new List<PendingAttendanceLog>();
        }
    }
    
    /// <summary>
    /// Mark a pending attendance log as synced
    /// </summary>
    public async Task MarkAsSyncedAsync(long localId, int databaseLogId)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            
            await connection.ExecuteAsync(@"
                UPDATE pending_attendance
                SET sync_status = @Synced,
                    last_sync_attempt = @SyncTime
                WHERE local_id = @LocalId
            ", new
            {
                LocalId = localId,
                Synced = SyncStatusCode.Synced,
                SyncTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            });

            Debug.WriteLine($"? Marked local_id {localId} as synced (database ID: {databaseLogId})");
            await MirrorPendingLogsToXmlAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? Error marking as synced: {ex.Message}");
        }
    }

    /// <summary>
    /// Get pending attendance logs with an optional batch limit.
    /// Used by <see cref="OutboundQueueProcessor"/> for drip-feed uploads.
    /// </summary>
    public async Task<List<PendingAttendanceLog>> GetPendingAttendanceAsync(int limit)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var pending = await connection.QueryAsync<PendingAttendanceLog>(@"
                SELECT
                    local_id           AS LocalId,
                    employee_id        AS EmployeeId,
                    rfid_code          AS RfidCode,
                    log_time           AS LogTime,
                    log_type           AS LogType,
                    date               AS Date,
                    attendance_status  AS AttendanceStatus,
                    is_late            AS IsLate,
                    is_early_out       AS IsEarlyOut,
                    late_minutes       AS LateMinutes,
                    undertime_minutes  AS UndertimeMinutes,
                    notes              AS Notes,
                    verified_by        AS VerifiedBy,
                    verified_at        AS VerifiedAt,
                    is_admin_time      AS IsAdminTime,
                    admin_time_reason  AS AdminTimeReason,
                    admin_time_status  AS AdminTimeStatus,
                    is_holiday         AS IsHoliday,
                    is_suspended       AS IsSuspended,
                    is_online_class    AS IsOnlineClass,
                    schedule_id        AS ScheduleId,
                    term_id            AS TermId,
                    created_at         AS CreatedAt,
                    sync_status        AS SyncStatus,
                    sync_attempts      AS SyncAttempts,
                    last_sync_attempt  AS LastSyncAttempt,
                    sync_error         AS SyncError
                FROM pending_attendance
                WHERE sync_status = @Pending
                ORDER BY created_at ASC
                LIMIT @Limit
            ", new { Limit = limit, Pending = SyncStatusCode.Pending });

            return pending.ToList();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? Error getting pending attendance (limit {limit}): {ex.Message}");
            return new List<PendingAttendanceLog>();
        }
    }

    /// <summary>
    /// Update the retry status of a pending log after a failed upload attempt.
    /// </summary>
    public async Task UpdatePendingSyncStatusAsync(
        long localId, int status, int attempts, string error)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            await connection.ExecuteAsync(@"
                UPDATE pending_attendance
                SET sync_status = @Status,
                    sync_attempts = @Attempts,
                    last_sync_attempt = @SyncTime,
                    sync_error = @Error
                WHERE local_id = @LocalId
            ", new
            {
                LocalId = localId,
                Status = status,
                Attempts = attempts,
                SyncTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Error = error
            });

            await MirrorPendingLogsToXmlAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? Error updating pending sync status: {ex.Message}");
        }
    }

    /// <summary>
    /// Hard-delete local records whose primary keys are NOT in the cloud set.
    /// Called during full sync to remove "ghost" records that were manually
    /// deleted from the database dashboard.
    /// </summary>
    public async Task<int> PurgeGhostEmployeesAsync(HashSet<int> cloudIds)
    {
        if (cloudIds.Count == 0) return 0;
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var idParams = string.Join(",", cloudIds);
            var deleted = await connection.ExecuteAsync($@"
                DELETE FROM employees
                WHERE employee_id NOT IN ({idParams})
            ");

            if (deleted > 0)
                Debug.WriteLine($"   ?? Purged {deleted} ghost employee(s) (hard-deleted from database)");

            return deleted;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? Error purging ghost employees: {ex.Message}");
            return 0;
        }
    }

    // ═══════════════════════════════════════════════════════════
    // TRUNCATE + BULK INSERT — "Cold Boot" hard overwrite
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// Delete ALL local employees and re-insert from the database master list.
    /// Guarantees SQLite is a 100% accurate mirror.
    /// Runs inside a single transaction — if the insert fails halfway,
    /// the old data is kept intact (atomic rollback).
    /// </summary>
    public async Task<int> TruncateAndBulkInsertEmployeesAsync(List<Employee> employees)
    {
        if (employees.Count == 0) return 0;

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        using var transaction = connection.BeginTransaction();

        try
        {
            await connection.ExecuteAsync("DELETE FROM employees", transaction: transaction);

            var syncedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            foreach (var emp in employees)
                await UpsertEmployeeCoreAsync(connection, emp, syncedAt, transaction);

            transaction.Commit();
            Debug.WriteLine($"[LocalDB] Truncate+BulkInsert employees: {employees.Count} rows");
            return employees.Count;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>
    /// Delete ALL local schedules and re-insert from the database master list.
    /// Atomic transaction — old data survives if the insert fails.
    /// </summary>
    public async Task<int> TruncateAndBulkInsertSchedulesAsync(List<dynamic> schedules)
    {
        if (schedules.Count == 0) return 0;

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        using var transaction = connection.BeginTransaction();

        try
        {
            await connection.ExecuteAsync("DELETE FROM class_schedules", transaction: transaction);

            var syncedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            foreach (IDictionary<string, object> schedule in schedules)
            {
                schedule["synced_at"] = syncedAt;
                connection.Execute(@"
                    INSERT OR REPLACE INTO class_schedules (
                        schedule_id, employee_id, subject_name, subject_code, section,
                        day_of_week, start_time, end_time, room, semester, school_year,
                        is_active, created_at, updated_at, synced_at
                    ) VALUES (
                        @schedule_id, @employee_id, @subject_name, @subject_code, @section,
                        @day_of_week, @start_time, @end_time, @room, @semester, @school_year,
                        @is_active, @created_at, @updated_at, @synced_at
                    )
                ", schedule, transaction: transaction);
            }

            transaction.Commit();
            Debug.WriteLine($"[LocalDB] Truncate+BulkInsert schedules: {schedules.Count} rows");
            return schedules.Count;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>
    /// Hard-delete local attendance logs whose IDs are NOT in the cloud set.
    /// </summary>
    public async Task<int> PurgeGhostAttendanceLogsAsync(HashSet<int> cloudLogIds)
    {
        if (cloudLogIds.Count == 0) return 0;
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var idParams = string.Join(",", cloudLogIds);
            var deleted = await connection.ExecuteAsync($@"
                DELETE FROM attendance_logs
                WHERE log_id NOT IN ({idParams})
            ");

            if (deleted > 0)
                Debug.WriteLine($"   ?? Purged {deleted} ghost attendance log(s)");

            return deleted;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? Error purging ghost logs: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// Create a DPAPI-encrypted binary backup of the SQLite database file.
    /// Stored in <c>%ProgramData%\STI_Attendance\EncryptedBackups</c>.
    /// </summary>
    public async Task<string?> CreateEncryptedBackupAsync()
    {
        try
        {
            if (!File.Exists(_dbPath)) return null;

            var backupFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "STI_Attendance", "EncryptedBackups");

            if (!Directory.Exists(backupFolder))
                Directory.CreateDirectory(backupFolder);

            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var bakPath = Path.Combine(backupFolder, $"rams_offline_{timestamp}.bak");

            // Read the raw SQLite file
            var rawBytes = await File.ReadAllBytesAsync(_dbPath);

            // Encrypt with DPAPI (machine-scoped — decryptable on this machine only)
            var encrypted = System.Security.Cryptography.ProtectedData.Protect(
                rawBytes, null,
                System.Security.Cryptography.DataProtectionScope.LocalMachine);

            await File.WriteAllBytesAsync(bakPath, encrypted);

            // Cleanup old backups (keep last 24)
            var backups = Directory.GetFiles(backupFolder, "*.bak")
                .OrderByDescending(f => f)
                .Skip(24)
                .ToList();
            foreach (var old in backups)
            {
                try { File.Delete(old); } catch { }
            }

            Debug.WriteLine($"   ?? Encrypted backup: {bakPath} ({encrypted.Length / 1024} KB)");
            return bakPath;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? Encrypted backup failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Create a hidden SQLite snapshot backup (.db) of the local database.
    /// Uses VACUUM INTO to produce a consistent backup file while the app is running.
    /// Stored under LocalAppData in a hidden folder.
    /// </summary>
    public async Task<string?> CreateHiddenDatabaseBackupAsync()
    {
        try
        {
            if (!File.Exists(_dbPath)) return null;

            var backupFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "STI_Attendance", "SystemCache", "Backups");

            if (!Directory.Exists(backupFolder))
                Directory.CreateDirectory(backupFolder);

            // Keep backup folder hidden from normal user browsing.
            try
            {
                var dirInfo = new DirectoryInfo(backupFolder);
                dirInfo.Attributes |= FileAttributes.Hidden;
            }
            catch { }

            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var backupPath = Path.Combine(backupFolder, $"rams_offline_{timestamp}.db");

            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            // Flush WAL pages first, then create a compact snapshot database file.
            await connection.ExecuteAsync("PRAGMA wal_checkpoint(FULL);");
            var safeBackupPath = backupPath.Replace("'", "''");
            await connection.ExecuteAsync($"VACUUM INTO '{safeBackupPath}';");

            try
            {
                var fileInfo = new FileInfo(backupPath);
                fileInfo.Attributes |= FileAttributes.Hidden;
            }
            catch { }

            // Cleanup old snapshot backups (keep latest 1440 = roughly 24h at 1/min).
            var oldBackups = Directory.GetFiles(backupFolder, "*.db")
                .OrderByDescending(f => f)
                .Skip(1440)
                .ToList();

            foreach (var old in oldBackups)
            {
                try { File.Delete(old); } catch { }
            }

            Debug.WriteLine($"   💾 Hidden .db backup created: {backupPath}");
            return backupPath;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"❌ Hidden .db backup failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Get count of pending attendance logs
    /// </summary>
    public async Task<int> GetPendingCountAsync()
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            
            var count = await connection.ExecuteScalarAsync<int>(@"
                SELECT COUNT(*) FROM pending_attendance
                WHERE sync_status = @Pending
            ", new { Pending = SyncStatusCode.Pending });

            return count;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? Error getting pending count: {ex.Message}");
            return 0;
        }
    }
    
    /// <summary>
    /// Get database file info for diagnostics
    /// </summary>
    public (string Path, long SizeInBytes, DateTime LastModified) GetDatabaseInfo()
    {
        try
        {
            var fileInfo = new FileInfo(_dbPath);
            return (
                fileInfo.FullName,
                fileInfo.Exists ? fileInfo.Length : 0,
                fileInfo.Exists ? fileInfo.LastWriteTime : DateTime.MinValue
            );
        }
        catch
        {
            return (_dbPath, 0, DateTime.MinValue);
        }
    }
    
    /// <summary>
    /// Check if local database has employee data cached
    /// </summary>
    public async Task<bool> HasCachedDataAsync()
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            
            var employeeCount = await connection.ExecuteScalarAsync<int>(@"
                SELECT COUNT(*) FROM employees WHERE is_active = 1
            ");
            
            Debug.WriteLine($"?? Local database has {employeeCount} cached employees");
            return employeeCount > 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? Error checking cached data: {ex.Message}");
            return false;
        }
    }
    
    /// <summary>
    /// Export local database to SQL backup file
    /// </summary>
    public async Task<string?> ExportToSqlBackupAsync()
    {
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var backupFolder = Path.Combine(localAppData, "STI_Attendance", "Backups");
            
            // Create backup folder if it doesn't exist
            if (!Directory.Exists(backupFolder))
            {
                Directory.CreateDirectory(backupFolder);
            }
            
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var backupPath = Path.Combine(backupFolder, $"rams_backup_{timestamp}.sql");
            
            Debug.WriteLine($"");
            Debug.WriteLine($"?? Creating SQL backup...");
            Debug.WriteLine($"   Location: {backupPath}");
            
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            
            using var writer = new StreamWriter(backupPath);
            
            // Write header
            await writer.WriteLineAsync("-- RAMS Offline Database Backup");
            await writer.WriteLineAsync($"-- Created: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            await writer.WriteLineAsync($"-- Source: {_dbPath}");
            await writer.WriteLineAsync();
            
            // Export employees table
            await writer.WriteLineAsync("-- ========================================");
            await writer.WriteLineAsync("-- EMPLOYEES TABLE");
            await writer.WriteLineAsync("-- ========================================");
            
            var employeeCount = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM employees");
            await writer.WriteLineAsync($"-- Total Employees: {employeeCount}");
            await writer.WriteLineAsync();
            
            // Export pending attendance
            await writer.WriteLineAsync("-- ========================================");
            await writer.WriteLineAsync("-- PENDING ATTENDANCE");
            await writer.WriteLineAsync("-- ========================================");
            
            var pendingCount = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM pending_attendance WHERE sync_status = @Pending",
                new { Pending = SyncStatusCode.Pending }
            );
            await writer.WriteLineAsync($"-- Pending Logs: {pendingCount}");
            
            if (pendingCount > 0)
            {
                var pendingLogs = await connection.QueryAsync<PendingAttendanceLog>(@"
                    SELECT * FROM pending_attendance WHERE sync_status = @Pending
                ", new { Pending = SyncStatusCode.Pending });
                
                foreach (var log in pendingLogs)
                {
                    await writer.WriteLineAsync($"-- Employee ID: {log.EmployeeId}, Type: {log.LogType}, Time: {log.LogTime}");
                }
            }
            
            await writer.WriteLineAsync();
            await writer.WriteLineAsync("-- Backup completed successfully");
            
            Debug.WriteLine($"? SQL backup created successfully");
            Debug.WriteLine($"   File: {backupPath}");
            Debug.WriteLine($"   Employees: {employeeCount}");
            Debug.WriteLine($"   Pending Logs: {pendingCount}");
            
            return backupPath;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? Error creating SQL backup: {ex.Message}");
            return null;
        }
    }
    
    /// <summary>
    /// Get the database path for external access
    /// </summary>
    public string GetDatabasePath() => _dbPath;
    
    /// <summary>
    /// Get statistics about local database
    /// </summary>
    public async Task<LocalDatabaseStats> GetDatabaseStatsAsync()
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            
            var stats = new LocalDatabaseStats
            {
                EmployeeCount = await connection.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM employees WHERE is_active = 1"
                ),
                AttendanceLogCount = await connection.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM attendance_logs"
                ),
                PendingCount = await connection.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM pending_attendance WHERE sync_status = @Pending",
                    new { Pending = SyncStatusCode.Pending }
                ),
                ScheduleCount = await connection.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM class_schedules WHERE is_active = 1"
                ),
                HolidayCount = await connection.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM holidays WHERE is_active = 1"
                ),
                SubstituteCount = await connection.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM substitute_teacher WHERE is_active = 1"
                ),
                LastSyncTime = await connection.ExecuteScalarAsync<string>(
                    "SELECT MAX(synced_at) FROM employees"
                )
            };
            
            var fileInfo = new FileInfo(_dbPath);
            if (fileInfo.Exists)
            {
                stats.DatabaseSizeBytes = fileInfo.Length;
                stats.LastModified = fileInfo.LastWriteTime;
            }
            
            return stats;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? Error getting database stats: {ex.Message}");
            return new LocalDatabaseStats();
        }
    }
}

/// <summary>
/// Model for pending attendance logs in local database
/// </summary>
public class PendingAttendanceLog
{
    public long LocalId { get; set; }
    public int EmployeeId { get; set; }
    public string RfidCode { get; set; } = string.Empty;
    public string LogTime { get; set; } = string.Empty;
    public string LogType { get; set; } = string.Empty;
    public string Date { get; set; } = string.Empty;
    public string? AttendanceStatus { get; set; }
    public int IsLate { get; set; }
    public int IsEarlyOut { get; set; }
    public int? LateMinutes { get; set; }
    public int? UndertimeMinutes { get; set; }
    public string? Notes { get; set; }
    public int? VerifiedBy { get; set; }
    public string? VerifiedAt { get; set; }
    public int IsAdminTime { get; set; }
    public string? AdminTimeReason { get; set; }
    public string? AdminTimeStatus { get; set; }
    public int IsHoliday { get; set; }
    public int IsSuspended { get; set; }
    public int IsOnlineClass { get; set; }
    public long? ScheduleId { get; set; }
    public long? TermId { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
    public int SyncStatus { get; set; } = SyncStatusCode.Pending;
    public int SyncAttempts { get; set; }
    public string? LastSyncAttempt { get; set; }
    public string? SyncError { get; set; }
}

/// <summary>
/// Integer constants for pending_attendance.sync_status.
/// </summary>
public static class SyncStatusCode
{
    /// <summary>Record has been successfully pushed to database.</summary>
    public const int Synced = 0;
    /// <summary>Record is waiting to be pushed.</summary>
    public const int Pending = 1;
    /// <summary>Record exceeded max retries and is in error state.</summary>
    public const int Error = 2;
    /// <summary>Record has a permanent validation error (22007/23505) and must not be retried.</summary>
    public const int FailedValidation = 3;
}

/// <summary>
/// Statistics about local database
/// </summary>
public class LocalDatabaseStats
{
    public int EmployeeCount { get; set; }
    public int AttendanceLogCount { get; set; }
    public int PendingCount { get; set; }
    public int ScheduleCount { get; set; }
    public int HolidayCount { get; set; }
    public int SubstituteCount { get; set; }
    public long DatabaseSizeBytes { get; set; }
    public DateTime LastModified { get; set; }
    public string? LastSyncTime { get; set; }
    
    public string GetDatabaseSizeFormatted()
    {
        if (DatabaseSizeBytes < 1024)
            return $"{DatabaseSizeBytes} B";
        else if (DatabaseSizeBytes < 1024 * 1024)
            return $"{DatabaseSizeBytes / 1024.0:F2} KB";
        else
            return $"{DatabaseSizeBytes / (1024.0 * 1024.0):F2} MB";
    }
}
