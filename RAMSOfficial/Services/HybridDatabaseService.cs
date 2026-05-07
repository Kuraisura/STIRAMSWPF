using System.Diagnostics;
using RAMSOfficial.Helpers;
using RAMSOfficial.Models;
using Npgsql;
using Dapper;

namespace RAMSOfficial.Services;

/// <summary>
/// ENHANCED Hybrid database service with PURE OFFLINE MODE
/// When OFFLINE: Uses LocalDB exclusively (no database attempts)
/// When ONLINE: Syncs all locally-saved data to database automatically
/// </summary>
public class HybridDatabaseService
{
    private readonly DatabaseService _onlineDb;
    private readonly LocalDatabaseService _localDb;
    private readonly NetworkStatusService _netStatus;
    private readonly System.Timers.Timer _heartbeatTimer;   // 1s heartbeat for pending + delta checks
    private readonly System.Timers.Timer _fullSyncTimer;     // slower 5-min full refresh
    private readonly System.Timers.Timer _backupTimer;       // every-minute hidden .db snapshot backup
    private int _syncLock = 0; // 0 = free, 1 = locked (Interlocked-guarded)
    private DateTime _lastHeartbeatDeltaAt = DateTime.MinValue;
    private const int HeartbeatDeltaIntervalSeconds = 1;

    // ── Circuit breaker state ──
    private int _consecutiveOnlineFailures = 0;
    private const int CircuitBreakerThreshold = 3;
    private DateTime _circuitOpenedAt = DateTime.MinValue;
    private const int CircuitCooldownSeconds = 30;
    private const int OnlineQueryTimeoutMs = 10_000; // 10s — tolerant on slow connections

    public bool IsOnline => _netStatus.HasConnection;
    public string ConnectionStatus => _netStatus.GetStatusText();
    public string StatusIcon => _netStatus.GetStatusIcon();

    /// <summary>Triple-state network service (Green / Yellow / Red).</summary>
    public NetworkStatusService NetStatus => _netStatus;

    /// <summary>
    /// True when the circuit breaker is open (online queries are being skipped).
    /// </summary>
    public bool IsCircuitOpen
    {
        get
        {
            if (_consecutiveOnlineFailures < CircuitBreakerThreshold)
                return false;
            // Allow a retry after cooldown
            if ((DateTime.UtcNow - _circuitOpenedAt).TotalSeconds >= CircuitCooldownSeconds)
            {
                Interlocked.Exchange(ref _consecutiveOnlineFailures, 0);
                Debug.WriteLine("⚡ Circuit breaker HALF-OPEN — allowing retry");
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Returns true when we have any connection (GREEN or YELLOW) AND circuit is closed.
    /// Uses <see cref="NetworkStatusService.HasConnection"/> which allows slow connections.
    /// </summary>
    private bool CanQueryOnline => _netStatus.HasConnection && !IsCircuitOpen;

    /// <summary>
    /// Record a successful online call — resets the circuit breaker.
    /// </summary>
    private void RecordOnlineSuccess()
    {
        if (_consecutiveOnlineFailures > 0)
        {
            Debug.WriteLine($"⚡ Circuit breaker RESET after success (was at {_consecutiveOnlineFailures} failures)");
            Interlocked.Exchange(ref _consecutiveOnlineFailures, 0);
        }
    }

    /// <summary>
    /// Record a failed online call — may trip the circuit breaker.
    /// </summary>
    private void RecordOnlineFailure(string context)
    {
        var count = Interlocked.Increment(ref _consecutiveOnlineFailures);
        if (count >= CircuitBreakerThreshold &&
            (_circuitOpenedAt == DateTime.MinValue || (DateTime.UtcNow - _circuitOpenedAt).TotalSeconds >= CircuitCooldownSeconds))
        {
            _circuitOpenedAt = DateTime.UtcNow;
            Debug.WriteLine($"⚡ Circuit breaker OPEN after {count} consecutive failures in {context}");
            Debug.WriteLine($"   All online queries will fail-fast to LocalDB for {CircuitCooldownSeconds}s");
        }
    }

    public event EventHandler<bool>? ConnectionStatusChanged;
    public event EventHandler<SyncCompletedEventArgs>? SyncCompleted;
    
    public HybridDatabaseService()
    {
        _onlineDb = new DatabaseService();
        _localDb = new LocalDatabaseService();
        _netStatus = new NetworkStatusService();

        // Heartbeat: every 1 second for pending upload responsiveness.
        // Delta employee/schedule fetch is also 1 second for near-real-time cache updates.
        _heartbeatTimer = new System.Timers.Timer(1_000);
        _heartbeatTimer.Elapsed += async (s, e) => await HeartbeatSyncAsync();
        _heartbeatTimer.AutoReset = true;

        // Full sync: every 5 minutes — complete mirror refresh
        _fullSyncTimer = new System.Timers.Timer(300_000);
        _fullSyncTimer.Elapsed += async (s, e) => await FullDatabaseSyncAsync();
        _fullSyncTimer.AutoReset = true;

        // Hidden SQLite snapshot backup: every 1 minute
        _backupTimer = new System.Timers.Timer(60_000);
        _backupTimer.Elapsed += async (s, e) =>
        {
            var path = await _localDb.CreateHiddenDatabaseBackupAsync();
            if (path != null)
                Debug.WriteLine($"   💾 Minute backup snapshot: {path}");
        };
        _backupTimer.AutoReset = true;

        // Subscribe to triple-state transitions for sanity check
        _netStatus.StatusChanged += OnNetStatusChanged;

        Debug.WriteLine($"");
        Debug.WriteLine($"????????????????????????????????????????????");
        Debug.WriteLine($"?? ENHANCED HYBRID DATABASE SERVICE");
        Debug.WriteLine($"   Online DB: database (PostgreSQL)");
        Debug.WriteLine($"   Offline DB: SQLite (Local Mirror)");
        Debug.WriteLine($"   Heartbeat: Every 1 second (pending uploads + delta pull)");
        Debug.WriteLine($"   Full Sync: Every 5 minutes (all tables)");
        Debug.WriteLine($"   Backup: Every 1 minute (hidden .db snapshot)");
        Debug.WriteLine($"   Strategy: Local-First with Delta Sync");
        Debug.WriteLine($"????????????????????????????????????????????");
    }
    
    /// <summary>
    /// Initialize hybrid database system
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            Debug.WriteLine($"");
            Debug.WriteLine($"?? Initializing Enhanced Hybrid Database System...");
            
            // Initialize local database schema
            await _localDb.InitializeAsync();

            // Start local minute snapshot backup immediately (works online/offline).
            _backupTimer.Start();

            // Create an immediate hidden .db snapshot at startup.
            _ = await _localDb.CreateHiddenDatabaseBackupAsync();
            
            // Check if we have cached data locally
            var hasCachedData = await _localDb.HasCachedDataAsync();
            
            if (hasCachedData)
            {
                Debug.WriteLine($"");
                Debug.WriteLine($"? Local database has cached employee data");
                Debug.WriteLine($"   ?? System can operate FULLY OFFLINE");
                Debug.WriteLine($"   ?? Will sync with database when connection available");
                
                // Show database stats
                var stats = await _localDb.GetDatabaseStatsAsync();
                Debug.WriteLine($"");
                Debug.WriteLine($"?? Local Database Statistics:");
                Debug.WriteLine($"   Employees: {stats.EmployeeCount}");
                Debug.WriteLine($"   Attendance Logs: {stats.AttendanceLogCount}");
                Debug.WriteLine($"   Pending Sync: {stats.PendingCount}");
                Debug.WriteLine($"   Schedules: {stats.ScheduleCount}");
                Debug.WriteLine($"   Database Size: {stats.GetDatabaseSizeFormatted()}");
                Debug.WriteLine($"   Last Sync: {stats.LastSyncTime ?? "Never"}");
            }
            else
            {
                Debug.WriteLine($"");
                Debug.WriteLine($"?? No cached data found in local database");
                Debug.WriteLine($"   ?? System REQUIRES online connection for initial setup");
            }
            
            // Start network monitoring
            await _netStatus.StartAsync();
            
            // Fully Offline PostgreSQL Mode: Disable Hybrid Syncing
            /*
            if (IsOnline)
            {
                Debug.WriteLine($"");
                Debug.WriteLine($"?? System is ONLINE - performing initial full database sync...");
                await FullDatabaseSyncAsync();

                // Create hidden .db backup snapshot after successful sync
                var backupPath = await _localDb.CreateHiddenDatabaseBackupAsync();
                if (backupPath != null)
                {
                    Debug.WriteLine($"");
                    Debug.WriteLine($"💾 Hidden .db Backup Created:");
                    Debug.WriteLine($"   Location: {backupPath}");
                }

                _heartbeatTimer.Start();
                _fullSyncTimer.Start();
                Debug.WriteLine($"? Heartbeat started (every 1s pending + delta) + Full sync (every 5min) + Backup (every 1min)");
            }
            else
            */
            {
                if (hasCachedData)
                {
                    Debug.WriteLine($"");
                    Debug.WriteLine($"? Starting in OFFLINE mode with cached data");
                    Debug.WriteLine($"   ?? Using local database");
                    Debug.WriteLine($"   ?? Will sync when connection is restored");
                }
                else
                {
                    Debug.WriteLine($"");
                    Debug.WriteLine($"? CRITICAL: No cached data and no internet connection");
                    Debug.WriteLine($"   ?? System cannot operate without initial data");
                    Debug.WriteLine($"   ?? Please connect to internet for first-time setup");
                }
            }
            
            Debug.WriteLine($"");
            Debug.WriteLine($"? Enhanced Hybrid Database System initialized successfully");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? Error initializing hybrid database:");
            Debug.WriteLine($"   Message: {ex.Message}");
            throw;
        }
    }
    
    /// <summary>
    /// FULL DATABASE SYNC — Safe upsert of all database tables into SQLite.
    /// Uses INSERT OR REPLACE (never DELETE+INSERT) so partial failures
    /// cannot wipe the local employee cache.
    /// </summary>
    private async Task FullDatabaseSyncAsync()
    {
        if (System.Threading.Interlocked.CompareExchange(ref _syncLock, 1, 0) != 0) return;
        if (!_netStatus.HasConnection && !IsOnline) { System.Threading.Interlocked.Exchange(ref _syncLock, 0); return; }

        int totalEmployees = 0, totalSchedules = 0, totalExams = 0, totalHolidays = 0, totalSubs = 0;

        try
        {
            var syncStart = DateTime.Now;

            Debug.WriteLine($"");
            Debug.WriteLine($"?? FULL DATABASE SYNC STARTED");
            Debug.WriteLine($"   Time: {syncStart:HH:mm:ss.fff}");

            // PRIORITY 1: Upload any pending offline data FIRST
            await UploadPendingAttendanceAsync();

            // PRIORITY 2: Download fresh data from database using SAFE UPSERT
            // 1?? Sync Employees table
            var employees = await GetAllEmployeesFromdatabaseAsync();
            if (employees.Count > 0)
            {
                totalEmployees = await _localDb.SafeUpsertEmployeesAsync(employees, isFullSync: true);
                await _localDb.SetLastSyncTimeAsync("employees", syncStart, totalEmployees, isFull: true);
                Debug.WriteLine($"   ? Employees: {totalEmployees} records upserted");

                // Ghost purge: hard-delete local employees that no longer exist in database
                var cloudIds = new HashSet<int>(employees.Select(e => e.EmployeeId));
                await _localDb.PurgeGhostEmployeesAsync(cloudIds);
            }

            // 2?? Sync Attendance Logs table (last 7 days for performance)
            // Online deletions are mirrored locally through the ghost-purge step below.
            // Local deletions never push to database because uploads are INSERT-only.
            var attendanceLogs = await GetRecentAttendanceLogsFromdatabaseAsync();
            if (attendanceLogs.Any())
            {
                await _localDb.OverwriteAttendanceLogsAsync(attendanceLogs);
                Debug.WriteLine($"   ? Attendance Logs: {attendanceLogs.Count} records synced");

                // Ghost purge: remove local logs that were deleted from database
                var cloudLogIds = new HashSet<int>(attendanceLogs.Select(l => l.LogId));
                await _localDb.PurgeGhostAttendanceLogsAsync(cloudLogIds);
            }

            // 3?? Sync Class Schedules table
            var schedules = await GetAllSchedulesFromdatabaseAsync();
            if (schedules.Count > 0)
            {
                totalSchedules = await _localDb.SafeUpsertSchedulesAsync(schedules, isFullSync: true);
                await _localDb.SetLastSyncTimeAsync("class_schedules", syncStart, totalSchedules, isFull: true);
                Debug.WriteLine($"   ? Schedules: {totalSchedules} records upserted");
            }

            // 4?? Sync Exam Schedules table
            var exams = await GetAllExamSchedulesFromdatabaseAsync();
            if (exams.Count > 0)
            {
                totalExams = await _localDb.SafeUpsertExamSchedulesAsync(exams, isFullSync: true);
                await _localDb.SetLastSyncTimeAsync("exam_schedules", syncStart, totalExams, isFull: true);
                Debug.WriteLine($"   ? Exam Schedules: {totalExams} records upserted");
            }

            // 5?? Sync Holidays table
            var holidays = await GetAllHolidaysFromdatabaseAsync();
            if (holidays.Count > 0)
            {
                totalHolidays = await _localDb.SafeUpsertHolidaysAsync(holidays, isFullSync: true);
                await _localDb.SetLastSyncTimeAsync("holidays", syncStart, totalHolidays, isFull: true);
                Debug.WriteLine($"   ? Holidays: {totalHolidays} records upserted");
            }

            // 6?? Sync Substitute Teacher table
            var substitutes = await GetAllSubstitutesFromdatabaseAsync();
            if (substitutes.Count > 0)
            {
                totalSubs = await _localDb.SafeUpsertSubstitutesAsync(substitutes, isFullSync: true);
                await _localDb.SetLastSyncTimeAsync("substitute_teacher", syncStart, totalSubs, isFull: true);
                Debug.WriteLine($"   ? Substitutes: {totalSubs} records upserted");
            }

            var syncDuration = (DateTime.Now - syncStart).TotalMilliseconds;
            Debug.WriteLine($"   ?? Sync completed in {syncDuration:F0}ms");
            Debug.WriteLine($"");

            // Notify sync completed with actual counts
            SyncCompleted?.Invoke(this, new SyncCompletedEventArgs(totalEmployees, attendanceLogs.Count));

            // Refresh RAM cache so every new employee and schedule change from this
            // full sync is visible to the kiosk before the next RFID tap.
            var dbPathForCache = _localDb.GetDatabasePath();
            _ = Task.Run(async () =>
            {
                try { await App.ScheduleCache.RefreshFromLocalDbAsync($"Data Source={dbPathForCache}"); }
                catch (Exception cex) { Debug.WriteLine($"?? Full-sync cache refresh failed: {cex.Message}"); }
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? Error in full database sync: {ex.Message}");
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _syncLock, 0);
        }
    }

    /// <summary>
    /// HEARTBEAT SYNC — Lightweight 1-second master-data delta sync.
    /// Uses delta sync (updated_at > last_sync) to minimize bandwidth.
    /// Also drains the pending attendance queue.
    /// </summary>
    private async Task HeartbeatSyncAsync()
    {
        if (System.Threading.Interlocked.CompareExchange(ref _syncLock, 1, 0) != 0) return;
        if (!_netStatus.HasConnection && !IsOnline) { System.Threading.Interlocked.Exchange(ref _syncLock, 0); return; }

        try
        {
            // Drain pending queue on every heartbeat
            await UploadPendingAttendanceAsync();

            // Delta pulls are throttled to every second.
            if ((DateTime.UtcNow - _lastHeartbeatDeltaAt).TotalSeconds < HeartbeatDeltaIntervalSeconds)
            {
                return;
            }

            _lastHeartbeatDeltaAt = DateTime.UtcNow;

            // Delta: employees
            var lastEmployeeSync = await _localDb.GetLastSyncTimeAsync("employees")
                                   ?? DateTime.Now.AddMinutes(-10);
            var deltaEmployees = await GetDeltaEmployeesFromdatabaseAsync(lastEmployeeSync);
            bool heartbeatChanges = false;
            if (deltaEmployees.Count > 0)
            {
                var count = await _localDb.SafeUpsertEmployeesAsync(deltaEmployees, isFullSync: false);
                await _localDb.SetLastSyncTimeAsync("employees", DateTime.Now, count, isFull: false);
                Debug.WriteLine($"   ?? Heartbeat: {count} employee(s) updated via delta sync");
                SyncCompleted?.Invoke(this, new SyncCompletedEventArgs(count, 0));
                heartbeatChanges = true;
            }

            // Also delta-sync schedules (catches Dean adding a new schedule from the web dashboard)
            var lastScheduleSync = await _localDb.GetLastSyncTimeAsync("class_schedules")
                                   ?? DateTime.Now.AddMinutes(-10);
            var deltaSchedules = await GetDeltaSchedulesFromdatabaseAsync(lastScheduleSync);
            if (deltaSchedules.Count > 0)
            {
                var sCount = await _localDb.SafeUpsertSchedulesAsync(deltaSchedules, isFullSync: false);
                await _localDb.SetLastSyncTimeAsync("class_schedules", DateTime.Now, sCount, isFull: false);
                Debug.WriteLine($"   ?? Heartbeat: {sCount} schedule(s) updated via delta sync");
                SyncCompleted?.Invoke(this, new SyncCompletedEventArgs(0, 0));
                heartbeatChanges = true;
            }

            // Delta: holidays (critical for offline policy rules)
            var lastHolidaySync = await _localDb.GetLastSyncTimeAsync("holidays")
                                  ?? DateTime.Now.AddMinutes(-10);
            var deltaHolidays = await GetDeltaHolidaysFromdatabaseAsync(lastHolidaySync);
            if (deltaHolidays.Count > 0)
            {
                var hCount = await _localDb.SafeUpsertHolidaysAsync(deltaHolidays, isFullSync: false);
                await _localDb.SetLastSyncTimeAsync("holidays", DateTime.Now, hCount, isFull: false);
                Debug.WriteLine($"   ?? Heartbeat: {hCount} holiday record(s) updated via delta sync");
            }

            // Delta: substitute assignments
            var lastSubSync = await _localDb.GetLastSyncTimeAsync("substitute_teacher")
                              ?? DateTime.Now.AddMinutes(-10);
            var deltaSubs = await GetDeltaSubstitutesFromdatabaseAsync(lastSubSync);
            if (deltaSubs.Count > 0)
            {
                var subCount = await _localDb.SafeUpsertSubstitutesAsync(deltaSubs, isFullSync: false);
                await _localDb.SetLastSyncTimeAsync("substitute_teacher", DateTime.Now, subCount, isFull: false);
                Debug.WriteLine($"   ?? Heartbeat: {subCount} substitute record(s) updated via delta sync");
            }

            // Delta: exam schedules
            var lastExamSync = await _localDb.GetLastSyncTimeAsync("exam_schedules")
                               ?? DateTime.Now.AddMinutes(-10);
            var deltaExams = await GetDeltaExamSchedulesFromdatabaseAsync(lastExamSync);
            if (deltaExams.Count > 0)
            {
                var eCount = await _localDb.SafeUpsertExamSchedulesAsync(deltaExams, isFullSync: false);
                await _localDb.SetLastSyncTimeAsync("exam_schedules", DateTime.Now, eCount, isFull: false);
                Debug.WriteLine($"   ?? Heartbeat: {eCount} exam schedule record(s) updated via delta sync");
                heartbeatChanges = true;
            }

            // Refresh the in-memory ScheduleCache whenever employees or schedules changed
            // so new employees / new class schedules take effect for the very next RFID tap.
            if (heartbeatChanges)
            {
                var dbPath = _localDb.GetDatabasePath();
                _ = Task.Run(async () =>
                {
                    try { await App.ScheduleCache.RefreshFromLocalDbAsync($"Data Source={dbPath}"); }
                    catch (Exception cex) { Debug.WriteLine($"?? Heartbeat cache refresh failed: {cex.Message}"); }
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"?? Heartbeat sync error: {ex.Message}");
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _syncLock, 0);
        }
    }
    
    /// <summary>
    /// Handle triple-state network transitions.
    /// When transitioning TO Green, perform a "Sanity Check" that
    /// compares local vs cloud record counts and triggers a full sync
    /// + hidden .db backup if they diverge.
    /// </summary>
    private async void OnNetStatusChanged(object? sender, NetworkStatusService.NetState newState)
    {
        Debug.WriteLine($"");
        Debug.WriteLine($"?? Triple-state transition ? {newState}");

        var isOnline = newState != NetworkStatusService.NetState.Red;
        ConnectionStatusChanged?.Invoke(this, isOnline);

        if (!isOnline)
        {
            Debug.WriteLine($"?? Connection lost - switching to OFFLINE mode");
            Debug.WriteLine($"   ?? Using cached local database");
            Debug.WriteLine($"   ?? All data will be saved locally until online");
            _heartbeatTimer.Stop();
            _fullSyncTimer.Stop();
            return;
        }

        _heartbeatTimer.Start();
        _fullSyncTimer.Start();

        if (newState == NetworkStatusService.NetState.Green)
        {
            Debug.WriteLine($"   ?? Performing sanity check (local vs cloud counts)...");
            try
            {
                // Compare employee count
                using var conn = new NpgsqlConnection(_onlineDb.GetConnectionString());
                await conn.OpenAsync();
                var cloudCount = await conn.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM employees WHERE is_active = true");
                var localStats = await _localDb.GetDatabaseStatsAsync();

                Debug.WriteLine($"   Cloud employees:  {cloudCount}");
                Debug.WriteLine($"   Local employees:  {localStats.EmployeeCount}");

                if (cloudCount != localStats.EmployeeCount)
                {
                    Debug.WriteLine($"   ?? MISMATCH — triggering full resync + backup");
                    await FullDatabaseSyncAsync();
                    await _localDb.CreateHiddenDatabaseBackupAsync();
                }
                else
                {
                    Debug.WriteLine($"   ? Counts match — local mirror is consistent");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"   ? Sanity check failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Upload pending attendance logs created while offline
    /// </summary>
    private async Task UploadPendingAttendanceAsync()
    {
        try
        {
            var pending = await _localDb.GetPendingAttendanceAsync();
            
            if (!pending.Any())
            {
                return;
            }
            
            Debug.WriteLine($"   ?? Uploading {pending.Count} pending attendance logs...");
            
            var synced = 0;
            var failed = 0;
            
            foreach (var log in pending)
            {
                try
                {
                    var databaseLogId = await UploadAttendanceTodatabaseAsync(log);
                    
                    if (databaseLogId > 0)
                    {
                        await _localDb.MarkAsSyncedAsync(log.LocalId, databaseLogId);
                        synced++;
                        Debug.WriteLine($"      ✅ Synced local_id {log.LocalId} → database ID {databaseLogId}");

                        // Replace the offline placeholder in STIRAMS.db with the real log_id
                        if (App.AttendanceSyncService != null)
                        {
                            var lt = DateTime.TryParse(log.LogTime, out var t) ? t : DateTime.Now;
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await App.AttendanceSyncService.MirrorToAttendanceLogsAsync(
                                        databaseLogId,
                                        log.EmployeeId, log.RfidCode, lt,
                                        DataSanitizer.NormalizeLogType(log.LogType),
                                        DataSanitizer.NormalizeAttendanceStatus(log.AttendanceStatus),
                                        log.IsLate == 1, log.IsEarlyOut == 1,
                                        log.LateMinutes, log.UndertimeMinutes,
                                        log.Notes,
                                        log.IsAdminTime == 1, log.AdminTimeReason, log.AdminTimeStatus,
                                        log.IsHoliday == 1, log.IsSuspended == 1, log.IsOnlineClass == 1,
                                        log.ScheduleId, log.TermId);
                                }
                                catch (Exception mx)
                                {
                                    Debug.WriteLine($"      ⚠ STIRAMS mirror failed for local_id {log.LocalId}: {mx.Message}");
                                }
                            });
                        }
                    }
                    else
                    {
                        failed++;
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    Debug.WriteLine($"      ? Failed to sync local_id {log.LocalId}: {ex.Message}");
                }
            }
            
            Debug.WriteLine($"   ?? Pending Uploads: {synced} synced, {failed} failed");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"   ? Error uploading pending attendance: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Upload a single attendance log to database
    /// </summary>
    private async Task<int> UploadAttendanceTodatabaseAsync(PendingAttendanceLog log)
    {
        using var connection = new NpgsqlConnection(_onlineDb.GetConnectionString());
        await connection.OpenAsync();
        
        var logId = await connection.ExecuteScalarAsync<int>(@"
            INSERT INTO attendance_logs (
                employee_id, rfid_code, log_time, log_type, date,
                attendance_status, is_late, is_early_out, late_minutes,
                undertime_minutes, notes, is_admin_time, admin_time_reason,
                admin_time_status, is_holiday, is_suspended, is_online_class,
                schedule_id, term_id
            ) VALUES (
                @EmployeeId, @RfidCode, @LogTime::timestamp, @LogType, @Date::date,
                @AttendanceStatus, @IsLate, @IsEarlyOut, @LateMinutes,
                @UndertimeMinutes, @Notes, @IsAdminTime, @AdminTimeReason,
                @AdminTimeStatus, @IsHoliday, @IsSuspended, @IsOnlineClass,
                @ScheduleId, @TermId
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
            IsLate = log.IsLate == 1,
            IsEarlyOut = log.IsEarlyOut == 1,
            log.LateMinutes,
            log.UndertimeMinutes,
            log.Notes,
            IsAdminTime = log.IsAdminTime == 1,
            log.AdminTimeReason,
            log.AdminTimeStatus,
            IsHoliday = log.IsHoliday == 1,
            IsSuspended = log.IsSuspended == 1,
            IsOnlineClass = log.IsOnlineClass == 1,
            log.ScheduleId,
            log.TermId
        });
        
        return logId;
    }
    
    /// <summary>
    /// Get all employees from database.
    /// CRITICAL: Uses explicit column aliases so Dapper can map
    /// snake_case PostgreSQL columns to PascalCase C# properties.
    /// </summary>
    private async Task<List<Employee>> GetAllEmployeesFromdatabaseAsync()
    {
        try
        {
            using var connection = new NpgsqlConnection(_onlineDb.GetConnectionString());
            await connection.OpenAsync();

            var employees = await connection.QueryAsync<Employee>(@"
                SELECT
                    employee_id       AS EmployeeId,
                    rfid_code         AS RfidCode,
                    full_name         AS FullName,
                    school_id         AS SchoolId,
                    department        AS Department,
                    department_id     AS DepartmentId,
                    email             AS Email,
                    phone             AS Phone,
                    password          AS Password,
                    schedule_time_in::text  AS ScheduleTimeIn,
                    schedule_time_out::text AS ScheduleTimeOut,
                    employment_status AS EmploymentStatus,
                    employment_type   AS EmploymentType,
                    employment_subtype AS EmploymentSubtype,
                    hire_date         AS HireDate,
                    start_date        AS StartDate,
                    role              AS Role,
                    employee_type     AS EmployeeType,
                    staff_type        AS StaffType,
                    is_reporting_staff AS IsReportingStaff,
                    year_level        AS YearLevel,
                    current_section   AS CurrentSection,
                    current_semester  AS CurrentSemester,
                    unique_employee_id AS UniqueEmployeeId,
                    verification_status AS VerificationStatus,
                    verified_by       AS VerifiedBy,
                    verified_at       AS VerifiedAt,
                    photo_path        AS PhotoPath,
                    is_active         AS IsActive,
                    created_at        AS CreatedAt,
                    updated_at        AS UpdatedAt
                FROM employees WHERE is_active = true
            ");

            return employees.ToList();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error getting employees from database: {ex.Message}");
            return new List<Employee>();
        }
    }
    
    /// <summary>
    /// Get recent attendance logs from database (last 7 days)
    /// Uses properly-aliased SELECT to prevent Dapper mapping issues.
    /// </summary>
    private async Task<List<AttendanceLog>> GetRecentAttendanceLogsFromdatabaseAsync()
    {
        try
        {
            using var connection = new NpgsqlConnection(_onlineDb.GetConnectionString());
            await connection.OpenAsync();

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
                WHERE date >= CURRENT_DATE - INTERVAL '7 days'
                ORDER BY log_time DESC
            ");

            return logs.ToList();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error getting attendance logs: {ex.Message}");
            return new List<AttendanceLog>();
        }
    }
    
    /// <summary>
    /// Get all schedules from database (teaching_schedules table)
    /// </summary>
    private async Task<List<dynamic>> GetAllSchedulesFromdatabaseAsync()
    {
        try
        {
            using var connection = new NpgsqlConnection(_onlineDb.GetConnectionString());
            await connection.OpenAsync();

            var schedules = await connection.QueryAsync(@"
                SELECT schedule_id, employee_id, subject_name,
                    CAST(NULL AS TEXT)  AS subject_code,
                    section,
                    CASE day_of_week::text
                        WHEN '0' THEN 'Sunday'
                        WHEN '1' THEN 'Monday'
                        WHEN '2' THEN 'Tuesday'
                        WHEN '3' THEN 'Wednesday'
                        WHEN '4' THEN 'Thursday'
                        WHEN '5' THEN 'Friday'
                        WHEN '6' THEN 'Saturday'
                        ELSE day_of_week::text
                    END AS day_of_week,
                    time_start::text    AS start_time,
                    time_end::text      AS end_time,
                    CAST(NULL AS TEXT)  AS room,
                    CAST(NULL AS INT)   AS semester,
                    CAST(NULL AS TEXT)  AS school_year,
                    CASE WHEN status = 'active' OR status IS NULL THEN true ELSE false END AS is_active,
                    created_at, updated_at
                FROM teaching_schedules
                WHERE status = 'active' OR status IS NULL
            ");

            return schedules.ToList();
        }
        catch (Npgsql.PostgresException pgEx) when (pgEx.SqlState == "42P01")
        {
            Debug.WriteLine($"Schedule table not found in database: {pgEx.MessageText}");
            return new List<dynamic>();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error getting schedules: {ex.Message}");
            return new List<dynamic>();
        }
    }

    /// <summary>
    /// Get all active/scheduled exam schedules from database.
    /// </summary>
    private async Task<List<dynamic>> GetAllExamSchedulesFromdatabaseAsync()
    {
        try
        {
            using var connection = new NpgsqlConnection(_onlineDb.GetConnectionString());
            await connection.OpenAsync();

            var exams = await connection.QueryAsync(@"
                SELECT
                    exam_schedule_id,
                    employee_id,
                    subject_name,
                    section,
                    time_start::text AS time_start,
                    time_end::text AS time_end,
                    room_code,
                    exam_date,
                    day_of_week,
                    COALESCE(exam_type, 'regular') AS exam_type,
                    COALESCE(status, 'active') AS status,
                    CASE WHEN status IS NULL OR status IN ('active', 'scheduled') THEN 1 ELSE 0 END AS is_active,
                    created_at,
                    updated_at
                FROM exam_schedules
                WHERE status IS NULL OR status IN ('active', 'scheduled')
            ");

            return exams.ToList();
        }
        catch (Npgsql.PostgresException pgEx) when (pgEx.SqlState == "42P01")
        {
            Debug.WriteLine($"Exam schedules table not found in database: {pgEx.MessageText}");
            return new List<dynamic>();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error getting exam schedules: {ex.Message}");
            return new List<dynamic>();
        }
    }
    
    /// <summary>
    /// Get all holidays from database
    /// </summary>
    private async Task<List<dynamic>> GetAllHolidaysFromdatabaseAsync()
    {
        try
        {
            using var connection = new NpgsqlConnection(_onlineDb.GetConnectionString());
            await connection.OpenAsync();
            
            var holidays = await connection.QueryAsync(@"
                SELECT * FROM holidays WHERE is_active = true
            ");
            
            return holidays.ToList();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error getting holidays: {ex.Message}");
            return new List<dynamic>();
        }
    }
    
    /// <summary>
    /// Get all substitute teachers from database
    /// </summary>
    private async Task<List<dynamic>> GetAllSubstitutesFromdatabaseAsync()
    {
        try
        {
            using var connection = new NpgsqlConnection(_onlineDb.GetConnectionString());
            await connection.OpenAsync();

            var substitutes = await connection.QueryAsync(@"
                SELECT 
                    substitute_id,
                    original_employee_id,
                    substitute_employee_id,
                    schedule_id,
                    substitute_date,
                    start_time::text AS start_time,
                    end_time::text AS end_time,
                    reason,
                    status,
                    is_active,
                    created_at,
                    updated_at
                FROM substitute_teacher WHERE is_active = true
            ");

            return substitutes.ToList();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error getting substitutes: {ex.Message}");
            return new List<dynamic>();
        }
    }

    // ═══════════════════════════════════════════════════════════
    // DELTA QUERIES — fetch only records updated since last sync
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// Fetch employees updated since <paramref name="since"/> from database.
    /// Uses aliased SELECT to fix Dapper snake_case mapping.
    /// </summary>
    private async Task<List<Employee>> GetDeltaEmployeesFromdatabaseAsync(DateTime since)
    {
        try
        {
            using var connection = new NpgsqlConnection(_onlineDb.GetConnectionString());
            await connection.OpenAsync();

            var employees = await connection.QueryAsync<Employee>(@"
                SELECT
                    employee_id       AS EmployeeId,
                    rfid_code         AS RfidCode,
                    full_name         AS FullName,
                    school_id         AS SchoolId,
                    department        AS Department,
                    department_id     AS DepartmentId,
                    email             AS Email,
                    phone             AS Phone,
                    password          AS Password,
                    schedule_time_in::text  AS ScheduleTimeIn,
                    schedule_time_out::text AS ScheduleTimeOut,
                    employment_status AS EmploymentStatus,
                    employment_type   AS EmploymentType,
                    employment_subtype AS EmploymentSubtype,
                    hire_date         AS HireDate,
                    start_date        AS StartDate,
                    role              AS Role,
                    employee_type     AS EmployeeType,
                    staff_type        AS StaffType,
                    is_reporting_staff AS IsReportingStaff,
                    year_level        AS YearLevel,
                    current_section   AS CurrentSection,
                    current_semester  AS CurrentSemester,
                    unique_employee_id AS UniqueEmployeeId,
                    verification_status AS VerificationStatus,
                    verified_by       AS VerifiedBy,
                    verified_at       AS VerifiedAt,
                    photo_path        AS PhotoPath,
                    is_active         AS IsActive,
                    created_at        AS CreatedAt,
                    updated_at        AS UpdatedAt
                FROM employees
                WHERE is_active = true
                  AND (updated_at > @Since OR created_at > @Since)
            ", new { Since = since });

            return employees.ToList();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Delta employee fetch error: {ex.Message}");
            return new List<Employee>();
        }
    }

    /// <summary>
    /// Fetch schedules updated since <paramref name="since"/> from database.
    /// </summary>
    private async Task<List<dynamic>> GetDeltaSchedulesFromdatabaseAsync(DateTime since)
    {
        try
        {
            using var connection = new NpgsqlConnection(_onlineDb.GetConnectionString());
            await connection.OpenAsync();

            var schedules = await connection.QueryAsync(@"
                SELECT schedule_id, employee_id, subject_name,
                    CAST(NULL AS TEXT)  AS subject_code,
                    section,
                    CASE day_of_week::text
                        WHEN '0' THEN 'Sunday'
                        WHEN '1' THEN 'Monday'
                        WHEN '2' THEN 'Tuesday'
                        WHEN '3' THEN 'Wednesday'
                        WHEN '4' THEN 'Thursday'
                        WHEN '5' THEN 'Friday'
                        WHEN '6' THEN 'Saturday'
                        ELSE day_of_week::text
                    END AS day_of_week,
                    time_start::text    AS start_time,
                    time_end::text      AS end_time,
                    CAST(NULL AS TEXT)  AS room,
                    CAST(NULL AS INT)   AS semester,
                    CAST(NULL AS TEXT)  AS school_year,
                    CASE WHEN status = 'active' OR status IS NULL THEN true ELSE false END AS is_active,
                    created_at, updated_at
                FROM teaching_schedules
                WHERE (status = 'active' OR status IS NULL)
                  AND (updated_at > @Since OR created_at > @Since)
            ", new { Since = since });

            return schedules.ToList();
        }
        catch (Npgsql.PostgresException pgEx) when (pgEx.SqlState == "42P01")
        {
            Debug.WriteLine($"Delta schedule table not found: {pgEx.MessageText}");
            return new List<dynamic>();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Delta schedule fetch error: {ex.Message}");
            return new List<dynamic>();
        }
    }

    /// <summary>
    /// Fetch holidays updated since <paramref name="since"/> from database.
    /// </summary>
    private async Task<List<dynamic>> GetDeltaHolidaysFromdatabaseAsync(DateTime since)
    {
        try
        {
            using var connection = new NpgsqlConnection(_onlineDb.GetConnectionString());
            await connection.OpenAsync();

            var holidays = await connection.QueryAsync(@"
                SELECT *
                FROM holidays
                WHERE is_active = true
                  AND (updated_at > @Since OR created_at > @Since)
            ", new { Since = since });

            return holidays.ToList();
        }
        catch (Npgsql.PostgresException pgEx) when (pgEx.SqlState == "42P01")
        {
            Debug.WriteLine($"Delta holiday table not found: {pgEx.MessageText}");
            return new List<dynamic>();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Delta holiday fetch error: {ex.Message}");
            return new List<dynamic>();
        }
    }

    /// <summary>
    /// Fetch substitute assignments updated since <paramref name="since"/> from database.
    /// </summary>
    private async Task<List<dynamic>> GetDeltaSubstitutesFromdatabaseAsync(DateTime since)
    {
        try
        {
            using var connection = new NpgsqlConnection(_onlineDb.GetConnectionString());
            await connection.OpenAsync();

            var substitutes = await connection.QueryAsync(@"
                SELECT 
                    substitute_id,
                    original_employee_id,
                    substitute_employee_id,
                    schedule_id,
                    substitute_date,
                    start_time::text AS start_time,
                    end_time::text AS end_time,
                    reason,
                    status,
                    is_active,
                    created_at,
                    updated_at
                FROM substitute_teacher
                WHERE is_active = true
                  AND (updated_at > @Since OR created_at > @Since)
            ", new { Since = since });

            return substitutes.ToList();
        }
        catch (Npgsql.PostgresException pgEx) when (pgEx.SqlState == "42P01")
        {
            Debug.WriteLine($"Delta substitute table not found: {pgEx.MessageText}");
            return new List<dynamic>();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Delta substitute fetch error: {ex.Message}");
            return new List<dynamic>();
        }
    }

    /// <summary>
    /// Fetch exam schedules updated since <paramref name="since"/> from database.
    /// </summary>
    private async Task<List<dynamic>> GetDeltaExamSchedulesFromdatabaseAsync(DateTime since)
    {
        try
        {
            using var connection = new NpgsqlConnection(_onlineDb.GetConnectionString());
            await connection.OpenAsync();

            var exams = await connection.QueryAsync(@"
                SELECT
                    exam_schedule_id,
                    employee_id,
                    subject_name,
                    section,
                    time_start::text AS time_start,
                    time_end::text AS time_end,
                    room_code,
                    exam_date,
                    day_of_week,
                    COALESCE(exam_type, 'regular') AS exam_type,
                    COALESCE(status, 'active') AS status,
                    CASE WHEN status IS NULL OR status IN ('active', 'scheduled') THEN 1 ELSE 0 END AS is_active,
                    created_at,
                    updated_at
                FROM exam_schedules
                WHERE (status IS NULL OR status IN ('active', 'scheduled'))
                  AND (updated_at > @Since OR created_at > @Since)
            ", new { Since = since });

            return exams.ToList();
        }
        catch (Npgsql.PostgresException pgEx) when (pgEx.SqlState == "42P01")
        {
            Debug.WriteLine($"Delta exam schedule table not found: {pgEx.MessageText}");
            return new List<dynamic>();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Delta exam schedule fetch error: {ex.Message}");
            return new List<dynamic>();
        }
    }
    
    /// <summary>
    /// Get employee by RFID — uses the 3-tier lookup:
    ///   RAM (sub-1ms) → SQLite (sub-10ms) → database (if online).
    /// </summary>
    public async Task<Employee?> GetEmployeeByRfidAsync(string rfidCode)
    {
        // Priority 1: RAM cache
        var ramHit = App.ScheduleCache?.GetEmployeeByRfid(rfidCode);
        if (ramHit != null) return ramHit;

        // Priority 2: Local SQLite
        try
        {
            var localHit = await _localDb.GetEmployeeByRfidAsync(rfidCode);
            if (localHit != null)
            {
                App.ScheduleCache?.CacheEmployee(localHit);
                return localHit;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HybridDB] Local fallback error: {ex.Message}");
        }

        // Priority 3: database (if online)
        if (CanQueryOnline)
        {
            try
            {
                var onlineHit = await _onlineDb.GetEmployeeByRfidAsync(rfidCode);
                if (onlineHit != null)
                {
                    RecordOnlineSuccess();
                    App.ScheduleCache?.CacheEmployee(onlineHit);
                    _ = Task.Run(async () =>
                    {
                        try { await _localDb.UpsertEmployeeAsync(onlineHit); }
                        catch { /* non-critical */ }
                    });
                    return onlineHit;
                }
            }
            catch (Exception ex)
            {
                RecordOnlineFailure($"GetEmployeeByRfid: {ex.Message}");
            }
        }

        return null;
    }
    
    /// <summary>
    /// Get employee by ID
    /// </summary>
    public async Task<Employee?> GetEmployeeByIdAsync(int employeeId)
    {
        try
        {
            if (CanQueryOnline)
            {
                return await _onlineDb.GetEmployeeByIdAsync(employeeId);
            }
            else
            {
                Debug.WriteLine($"?? OFFLINE/CB MODE: Getting employee {employeeId} from local cache");
                return await _localDb.GetEmployeeByIdAsync(employeeId);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"?? Error getting employee by ID: {ex.Message}");
            // Fallback to local DB
            try
            {
                return await _localDb.GetEmployeeByIdAsync(employeeId);
            }
            catch
            {
                return null;
            }
        }
    }
    
    /// <summary>
    /// Get last tap type for a specific date
    /// </summary>
    public async Task<string> GetLastTapTypeForDateAsync(int employeeId, DateTime date)
    {
        try
        {
            if (CanQueryOnline)
            {
                return await _onlineDb.GetLastTapTypeForDateAsync(employeeId, date);
            }
            else
            {
                return await _localDb.GetLastTapTypeForDateAsync(employeeId, date);
            }
        }
        catch
        {
            return "";
        }
    }
    
    /// <summary>
    /// Get today's attendance logs for a specific date
    /// </summary>
    public async Task<IEnumerable<AttendanceLog>> GetTodayAttendanceForDateAsync(int employeeId, DateTime date)
    {
        try
        {
            if (CanQueryOnline)
            {
                return await _onlineDb.GetTodayAttendanceForDateAsync(employeeId, date);
            }
            else
            {
                return await _localDb.GetTodayAttendanceForDateAsync(employeeId, date);
            }
        }
        catch
        {
            return Enumerable.Empty<AttendanceLog>();
        }
    }
    
    /// <summary>
    /// Record attendance using triple-state priority:
    ///   GREEN  → database first, then mirror to SQLite as synced.
    ///   YELLOW/RED (or database failure) → SQLite pending queue (NeedsSync = true).
    ///   The SyncWorker will push pending records when GREEN returns.
    /// </summary>
    public async Task<AttendanceLog> RecordAttendanceAsync(
        int employeeId,
        string nextTapType,
        AttendanceValidationResponse validationResult,
        MissedLogValidationResult? missedLogValidation = null)
    {
        try
        {
            var logTime = missedLogValidation?.RequestedTime ?? TimeChangerWindow.GetCurrentTime();

            // ── Condition A: GREEN or YELLOW + circuit closed → database first ──
            if (_netStatus.HasConnection && !IsCircuitOpen)
            {
                try
                {
                    var onlineLog = await _onlineDb.RecordAttendanceAsync(
                        employeeId, nextTapType, (dynamic)validationResult, missedLogValidation);
                    RecordOnlineSuccess();

                    // Mirror the synced record into rams_offline.db for offline access
                    _ = Task.Run(async () =>
                    {
                        try { await _localDb.InsertSyncedAttendanceLogAsync(onlineLog); }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"⚠ SQLite mirror after database write failed: {ex.Message}");
                        }
                    });

                    // Mirror into STIRAMS.db attendance_logs immediately (awaited).
                    // STIRAMS.db is the single source of truth for the Recent Attendance
                    // panel — it must be updated before LoadRecentEmployeesAsync fires.
                    if (App.AttendanceSyncService != null)
                    {
                        try
                        {
                            await App.AttendanceSyncService.MirrorToAttendanceLogsAsync(
                                onlineLog.LogId,
                                onlineLog.EmployeeId ?? employeeId,
                                onlineLog.RfidCode,
                                onlineLog.LogTime,
                                onlineLog.LogType,
                                onlineLog.AttendanceStatus ?? "on-time",
                                onlineLog.IsLate ?? false,
                                onlineLog.IsEarlyOut ?? false,
                                onlineLog.LateMinutes,
                                onlineLog.UndertimeMinutes,
                                onlineLog.Notes,
                                onlineLog.IsAdminTime ?? false,
                                onlineLog.AdminTimeReason,
                                onlineLog.AdminTimeStatus,
                                onlineLog.IsHoliday ?? false,
                                onlineLog.IsSuspended ?? false,
                                onlineLog.IsOnlineClass ?? false,
                                onlineLog.ScheduleId,
                                onlineLog.TermId);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"⚠ STIRAMS.db mirror failed: {ex.Message}");
                        }
                    }

                    Debug.WriteLine($"✓ Attendance recorded ONLINE → database (LogId={onlineLog.LogId})");
                    return onlineLog;
                }
                catch (Exception ex)
                {
                    RecordOnlineFailure($"RecordAttendance: {ex.Message}");
                    Debug.WriteLine($"⚠ database write failed → falling to local queue: {ex.Message}");
                    // Fall through to Condition B
                }
            }

            // ── Condition B: RED / database failure → SQLite pending queue ──
            Debug.WriteLine($"📦 Recording to SQLite pending queue (Net={_netStatus.CurrentState})");

            // Try to get RFID code: SQLite first, then RAM cache fallback
            string resolvedRfid = "";
            var employee = await _localDb.GetEmployeeByIdAsync(employeeId);
            if (employee != null)
            {
                resolvedRfid = employee.RfidCode;
            }
            else
            {
                // Employee not in local DB — try RAM cache
                Debug.WriteLine($"⚠ Employee ID {employeeId} not in local SQLite — checking RAM cache");
                var cached = App.ScheduleCache.GetEmployeeById(employeeId);
                if (cached != null)
                {
                    resolvedRfid = cached.RfidCode;
                    Debug.WriteLine($"✓ Found in RAM cache: {cached.FullName} (RFID={cached.RfidCode})");
                }
                else
                {
                    Debug.WriteLine($"⚠ Employee ID {employeeId} not in RAM either — using empty RFID for pending log");
                }
            }

            // Resolve and validate offline tap direction using local state mirrors.
            var resolvedTapType = await ResolveOfflineTapTypeAsync(employeeId, logTime.Date, nextTapType);
            if (!string.Equals(resolvedTapType, nextTapType, StringComparison.OrdinalIgnoreCase))
            {
                Debug.WriteLine($"🔄 Offline tap direction corrected: {nextTapType} → {resolvedTapType} (existing local state detected)");
                nextTapType = resolvedTapType;
            }

            // Do not queue duplicate pending records if the same tap direction already exists.
            var shouldQueue = await ShouldQueueOfflineLogAsync(employeeId, logTime.Date, nextTapType);
            if (!shouldQueue)
            {
                var offlineStatus = BuildOfflineStatusFromValidation(validationResult, logTime, nextTapType);
                Debug.WriteLine($"⏭️ Skip pending save: {nextTapType} already exists for employee {employeeId} on {logTime:yyyy-MM-dd}");
                return new AttendanceLog
                {
                    LogId = 0,
                    EmployeeId = employeeId,
                    RfidCode = resolvedRfid,
                    LogTime = logTime,
                    LogType = nextTapType,
                    Date = logTime.Date,
                    AttendanceStatus = offlineStatus.Status,
                    IsLate = offlineStatus.IsLate,
                    IsEarlyOut = offlineStatus.IsEarlyOut,
                    LateMinutes = offlineStatus.LateMinutes,
                    UndertimeMinutes = offlineStatus.UndertimeMinutes,
                    Notes = offlineStatus.Notes
                };
            }

            var pendingStatus = BuildOfflineStatusFromValidation(validationResult, logTime, nextTapType);

            await _localDb.SavePendingAttendanceAsync(
                employeeId,
                resolvedRfid,
                logTime,
                nextTapType,
                pendingStatus.Status,
                pendingStatus.IsLate,
                pendingStatus.IsEarlyOut,
                pendingStatus.LateMinutes,
                pendingStatus.UndertimeMinutes,
                pendingStatus.Notes,
                validationResult.ScheduleDetails?.ScheduleId);

            // Mirror offline tap into STIRAMS.db attendance_logs immediately
            // Awaited (not fire-and-forget) so the row exists before the UI reloads.
            if (App.AttendanceSyncService != null)
            {
                try
                {
                    await App.AttendanceSyncService.MirrorToAttendanceLogsAsync(
                        null,
                        employeeId,
                        resolvedRfid,
                        logTime,
                        nextTapType,
                        pendingStatus.Status,
                        pendingStatus.IsLate,
                        pendingStatus.IsEarlyOut,
                        pendingStatus.LateMinutes,
                        pendingStatus.UndertimeMinutes,
                        pendingStatus.Notes,
                        scheduleId: validationResult.ScheduleDetails?.ScheduleId);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"⚠ STIRAMS.db offline mirror failed: {ex.Message}");
                }
            }
            return new AttendanceLog
            {
                LogId = 0,
                EmployeeId = employeeId,
                RfidCode = resolvedRfid,
                LogTime = logTime,
                LogType = nextTapType,
                Date = logTime.Date,
                AttendanceStatus = pendingStatus.Status,
                IsLate = pendingStatus.IsLate,
                IsEarlyOut = pendingStatus.IsEarlyOut,
                LateMinutes = pendingStatus.LateMinutes,
                UndertimeMinutes = pendingStatus.UndertimeMinutes,
                Notes = pendingStatus.Notes
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"✗ Error recording attendance: {ex.Message}");
            throw;
        }
    }

    private static (string Status, bool IsLate, bool IsEarlyOut, int? LateMinutes, int? UndertimeMinutes, string? Notes)
        BuildOfflineStatusFromValidation(AttendanceValidationResponse validation, DateTime tapTime, string tapType)
    {
        var normalizedTapType = DataSanitizer.NormalizeLogType(tapType);

        if (string.Equals(validation.Mode, "AdminTime", StringComparison.OrdinalIgnoreCase))
        {
            return (
                DataSanitizer.NormalizeAttendanceStatus("admin-time"),
                false,
                false,
                null,
                null,
                string.IsNullOrWhiteSpace(validation.Message) ? "Admin time" : validation.Message);
        }

        var expectedIn = validation.ScheduleDetails?.TimeStart;
        var expectedOut = validation.ScheduleDetails?.TimeEnd;
        var hasExpected = expectedIn.HasValue && expectedOut.HasValue;

        if (!hasExpected)
        {
            return (
                DataSanitizer.NormalizeAttendanceStatus("on-time"),
                false,
                false,
                null,
                null,
                "Offline fallback: no schedule window available");
        }

        var statusResult = normalizedTapType == "OUT"
            ? RAMSOfficial.Helpers.StatusCalculationHelper.CalculateTimeOutStatus(
                tapTime.TimeOfDay,
                expectedOut!.Value,
                "OfflineMirror",
                "Local schedule window")
            : RAMSOfficial.Helpers.StatusCalculationHelper.CalculateTimeInStatus(
                tapTime.TimeOfDay,
                expectedIn!.Value,
                "OfflineMirror",
                "Local schedule window");

        return (
            DataSanitizer.NormalizeAttendanceStatus(statusResult.Status),
            statusResult.IsLate,
            statusResult.IsEarlyOut,
            statusResult.LateMinutes,
            statusResult.UndertimeMinutes,
            statusResult.Notes);
    }

    private async Task<string> ResolveOfflineTapTypeAsync(int employeeId, DateTime date, string requestedTapType)
    {
        var requested = string.Equals(requestedTapType, "OUT", StringComparison.OrdinalIgnoreCase) ? "OUT" : "IN";

        bool hasIn = false;
        bool hasOut = false;

        try
        {
            var localLogs = await _localDb.GetTodayAttendanceForDateAsync(employeeId, date);
            hasIn = localLogs.Any(l => string.Equals(l.LogType, "IN", StringComparison.OrdinalIgnoreCase));
            hasOut = localLogs.Any(l => string.Equals(l.LogType, "OUT", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"⚠ ResolveOfflineTapType local read failed: {ex.Message}");
        }

        if (App.AttendanceSyncService != null)
        {
            try
            {
                var stiraSummary = await App.AttendanceSyncService.GetTapSummaryForDateAsync(employeeId, date);
                hasIn = hasIn || stiraSummary.HasTimeIn;
                hasOut = hasOut || stiraSummary.HasTimeOut;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"⚠ ResolveOfflineTapType STIRAMS summary failed: {ex.Message}");
            }
        }

        if (hasIn && !hasOut) return "OUT";
        if (!hasIn && hasOut) return "IN";
        return requested;
    }

    private async Task<bool> ShouldQueueOfflineLogAsync(int employeeId, DateTime date, string tapType)
    {
        var normalizedType = string.Equals(tapType, "OUT", StringComparison.OrdinalIgnoreCase) ? "OUT" : "IN";

        try
        {
            var localLogs = await _localDb.GetTodayAttendanceForDateAsync(employeeId, date);
            if (localLogs.Any(l => string.Equals(l.LogType, normalizedType, StringComparison.OrdinalIgnoreCase)))
                return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"⚠ ShouldQueueOfflineLog local read failed: {ex.Message}");
        }

        if (App.AttendanceSyncService != null)
        {
            try
            {
                var summary = await App.AttendanceSyncService.GetTapSummaryForDateAsync(employeeId, date);
                if (normalizedType == "IN" && summary.HasTimeIn) return false;
                if (normalizedType == "OUT" && summary.HasTimeOut) return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"⚠ ShouldQueueOfflineLog STIRAMS summary failed: {ex.Message}");
            }
        }

        return true;
    }
    
    /// <summary>
    /// Get pending attendance count
    /// </summary>
    public async Task<int> GetPendingCountAsync()
    {
        return await _localDb.GetPendingCountAsync();
    }

    /// <summary>
    /// Force immediate upload of pending offline attendance to database.
    /// Also nudges STIRAMS sync worker when available.
    /// </summary>
    public async Task ForceUploadPendingTodatabaseAsync()
    {
        if (!CanQueryOnline)
            return;

        await UploadPendingAttendanceAsync();

        if (App.AttendanceSyncService != null)
        {
            try { await App.AttendanceSyncService.ForceSyncAsync(); }
            catch (Exception ex)
            {
                Debug.WriteLine($"⚠ Force STIRAMS sync failed: {ex.Message}");
            }
        }
    }
    
    /// <summary>
    /// Get today's attendance logs
    /// </summary>
    public async Task<IEnumerable<AttendanceLog>> GetTodayAttendanceAsync(int employeeId)
    {
        try
        {
            if (CanQueryOnline)
            {
                return await _onlineDb.GetTodayAttendanceAsync(employeeId);
            }
            else
            {
                return await _localDb.GetTodayAttendanceAsync(employeeId);
            }
        }
        catch
        {
            return Enumerable.Empty<AttendanceLog>();
        }
    }
    
    /// <summary>
    /// Get database statistics
    /// </summary>
    public async Task<LocalDatabaseStats> GetDatabaseStatsAsync()
    {
        return await _localDb.GetDatabaseStatsAsync();
    }
    
    /// <summary>
    /// Lightweight delta sync of master data from database into
    /// <c>rams_offline.db</c>, then refreshes the in-memory
    /// <see cref="ScheduleCacheService"/> so new employees and class schedule
    /// changes are available for the very next RFID tap.
    ///
    /// Safe to call concurrently — uses a non-blocking lock so simultaneous
    /// calls from <see cref="HeartbeatSyncAsync"/> and
    /// <see cref="HybridAttendanceSyncService"/> are silently skipped.
    /// </summary>
    public async Task RefreshEmployeesAndSchedulesAsync()
    {
        if (!CanQueryOnline) return;
        if (System.Threading.Interlocked.CompareExchange(ref _syncLock, 1, 0) != 0) return;

        try
        {
            bool anyChanges = false;

            // ── Employee delta sync ──
            var lastEmployeeSync = await _localDb.GetLastSyncTimeAsync("employees")
                                   ?? DateTime.Now.AddMinutes(-10);
            var deltaEmployees = await GetDeltaEmployeesFromdatabaseAsync(lastEmployeeSync);
            if (deltaEmployees.Count > 0)
            {
                await _localDb.SafeUpsertEmployeesAsync(deltaEmployees, isFullSync: false);
                await _localDb.SetLastSyncTimeAsync("employees", DateTime.Now, deltaEmployees.Count, isFull: false);
                Debug.WriteLine($"[DataRefresh] {deltaEmployees.Count} employee(s) pulled from database");
                RecordOnlineSuccess();
                anyChanges = true;
            }

            // ── Schedule delta sync ──
            var lastScheduleSync = await _localDb.GetLastSyncTimeAsync("class_schedules")
                                   ?? DateTime.Now.AddMinutes(-10);
            var deltaSchedules = await GetDeltaSchedulesFromdatabaseAsync(lastScheduleSync);
            if (deltaSchedules.Count > 0)
            {
                await _localDb.SafeUpsertSchedulesAsync(deltaSchedules, isFullSync: false);
                await _localDb.SetLastSyncTimeAsync("class_schedules", DateTime.Now, deltaSchedules.Count, isFull: false);
                Debug.WriteLine($"[DataRefresh] {deltaSchedules.Count} schedule(s) pulled from database");
                anyChanges = true;
            }

            // ── Holiday delta sync ──
            var lastHolidaySync = await _localDb.GetLastSyncTimeAsync("holidays")
                                  ?? DateTime.Now.AddMinutes(-10);
            var deltaHolidays = await GetDeltaHolidaysFromdatabaseAsync(lastHolidaySync);
            if (deltaHolidays.Count > 0)
            {
                await _localDb.SafeUpsertHolidaysAsync(deltaHolidays, isFullSync: false);
                await _localDb.SetLastSyncTimeAsync("holidays", DateTime.Now, deltaHolidays.Count, isFull: false);
                Debug.WriteLine($"[DataRefresh] {deltaHolidays.Count} holiday record(s) pulled from database");
            }

            // ── Substitution delta sync ──
            var lastSubSync = await _localDb.GetLastSyncTimeAsync("substitute_teacher")
                              ?? DateTime.Now.AddMinutes(-10);
            var deltaSubs = await GetDeltaSubstitutesFromdatabaseAsync(lastSubSync);
            if (deltaSubs.Count > 0)
            {
                await _localDb.SafeUpsertSubstitutesAsync(deltaSubs, isFullSync: false);
                await _localDb.SetLastSyncTimeAsync("substitute_teacher", DateTime.Now, deltaSubs.Count, isFull: false);
                Debug.WriteLine($"[DataRefresh] {deltaSubs.Count} substitute record(s) pulled from database");
            }

            // ── Exam schedule delta sync ──
            var lastExamSync = await _localDb.GetLastSyncTimeAsync("exam_schedules")
                               ?? DateTime.Now.AddMinutes(-10);
            var deltaExams = await GetDeltaExamSchedulesFromdatabaseAsync(lastExamSync);
            if (deltaExams.Count > 0)
            {
                await _localDb.SafeUpsertExamSchedulesAsync(deltaExams, isFullSync: false);
                await _localDb.SetLastSyncTimeAsync("exam_schedules", DateTime.Now, deltaExams.Count, isFull: false);
                Debug.WriteLine($"[DataRefresh] {deltaExams.Count} exam schedule record(s) pulled from database");
            }

            if (anyChanges)
            {
                await App.ScheduleCache.RefreshFromLocalDbAsync($"Data Source={_localDb.GetDatabasePath()}");
                Debug.WriteLine($"[DataRefresh] ScheduleCache refreshed — {App.ScheduleCache.CachedRfidCount} RFIDs, {App.ScheduleCache.TotalScheduleCount} schedules");
                SyncCompleted?.Invoke(this, new SyncCompletedEventArgs(deltaEmployees.Count, 0));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DataRefresh] RefreshEmployeesAndSchedulesAsync failed: {ex.Message}");
            RecordOnlineFailure($"RefreshEmployeesAndSchedules: {ex.Message}");
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _syncLock, 0);
        }
    }

    /// <summary>
    /// Get local database path
    /// </summary>
    public string GetDatabasePath()
    {
        return _localDb.GetDatabasePath();
    }
    
    /// <summary>
    /// Check if local database has cached data
    /// </summary>
    public async Task<bool> HasCachedDataAsync()
    {
        return await _localDb.HasCachedDataAsync();
    }
}

/// <summary>
/// Event args for sync completed event
/// </summary>
public class SyncCompletedEventArgs : EventArgs
{
    public int EmployeesSynced { get; }
    public int LogsSynced { get; }
    
    public SyncCompletedEventArgs(int employeesSynced, int logsSynced)
    {
        EmployeesSynced = employeesSynced;
        LogsSynced = logsSynced;
    }
}
