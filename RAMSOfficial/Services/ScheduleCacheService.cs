using System.Collections.Concurrent;
using System.Diagnostics;
using Dapper;
using Microsoft.Data.Sqlite;
using RAMSOfficial.Models;

namespace RAMSOfficial.Services;

/// <summary>
/// In-memory RAM cache for today's expected class schedules.
/// Loaded at startup and refreshed after each periodic sync.
/// Enables sub-1ms schedule lookups during RFID tap validation,
/// so the kiosk never blocks on disk or network I/O for schedule checks.
/// </summary>
public sealed class ScheduleCacheService
{
    /// <summary>
    /// Lightweight schedule entry kept in RAM.
    /// </summary>
    public sealed record CachedSchedule(
        int ScheduleId,
        int EmployeeId,
        string DayOfWeek,
        TimeSpan StartTime,
        TimeSpan EndTime,
        string? SubjectName,
        string? Section,
        string? Room);

    /// <summary>
    /// Raw row returned by SQLite before TimeSpan conversion.
    /// Dapper cannot auto-map TEXT → TimeSpan for SQLite, so we read
    /// as string first and convert manually.
    /// </summary>
    private sealed class ScheduleRow
    {
        public int ScheduleId { get; set; }
        public int EmployeeId { get; set; }
        public string DayOfWeek { get; set; } = "";
        public string? StartTime { get; set; }
        public string? EndTime { get; set; }
        public string? SubjectName { get; set; }
        public string? Section { get; set; }
        public string? Room { get; set; }
    }

    /// <summary>
    /// Result of the in-memory schedule pre-check.
    /// </summary>
    public sealed class SchedulePreCheckResult
    {
        public bool HasSchedule { get; init; }
        public bool IsPartTime { get; init; }
        /// <summary>True when a Part-Time employee has NO schedule today → triggers Warning.</summary>
        public bool ShouldWarn => IsPartTime && !HasSchedule;
        public string? WarningReason { get; init; }
        public TimeSpan? EarliestStart { get; init; }
        public TimeSpan? LatestEnd { get; init; }
        public int ScheduleCount { get; init; }
    }

    // Key = EmployeeId → List of today's schedules
    private readonly ConcurrentDictionary<int, List<CachedSchedule>> _cache = new();
    // Key = RFID code → Employee — zero-disk-IO lookups during tap
    private readonly ConcurrentDictionary<string, Employee> _employeeByRfid = new();
    private DateTime _cacheDate = DateTime.MinValue;
    private volatile bool _isLoaded;

    public bool IsLoaded => _isLoaded;
    public int CachedEmployeeCount => _cache.Count;
    public int CachedRfidCount => _employeeByRfid.Count;
    public int TotalScheduleCount => _cache.Values.Sum(v => v.Count);

    /// <summary>
    /// Populate cache from the local SQLite database (fast, no network).
    /// Call at startup and after each periodic sync completes.
    /// </summary>
    public async Task RefreshFromLocalDbAsync(string sqliteConnectionString)
    {
        var today = DateTime.Today;
        var dayName = today.DayOfWeek.ToString(); // "Monday", "Tuesday", etc.
        // day_of_week may be stored as an integer (0-6) from older syncs or as
        // a full name ('Thursday') from the fixed sync. Both formats are handled.
        var dayNumber = ((int)today.DayOfWeek).ToString(); // "4" for Thursday

        try
        {
            using var connection = new SqliteConnection(sqliteConnectionString);
            await connection.OpenAsync();

            // Query Class Schedules
            var rows = await connection.QueryAsync<ScheduleRow>(@"
                SELECT schedule_id AS ScheduleId,
                       employee_id AS EmployeeId,
                       day_of_week AS DayOfWeek,
                       start_time  AS StartTime,
                       end_time    AS EndTime,
                       subject_name AS SubjectName,
                       section     AS Section,
                       room        AS Room
                FROM class_schedules
                WHERE is_active = 1
                  AND (
                      LOWER(day_of_week) = LOWER(@DayName)
                      OR CAST(day_of_week AS TEXT) = @DayNumber
                  )
            ", new { DayName = dayName, DayNumber = dayNumber });

            var schedules = new List<CachedSchedule>();
            foreach (var r in rows)
            {
                if (!TimeSpan.TryParse(r.StartTime, out var start))
                    start = TimeSpan.Zero;
                if (!TimeSpan.TryParse(r.EndTime, out var end))
                    end = TimeSpan.Zero;

                schedules.Add(new CachedSchedule(
                    r.ScheduleId, r.EmployeeId, r.DayOfWeek,
                    start, end,
                    r.SubjectName, r.Section, r.Room));
            }

            // Query Exam Schedules
            var todayString = today.ToString("yyyy-MM-dd");
            var examRows = await connection.QueryAsync<ScheduleRow>(@"
                SELECT exam_schedule_id AS ScheduleId,
                       employee_id AS EmployeeId,
                       day_of_week AS DayOfWeek,
                       time_start  AS StartTime,
                       time_end    AS EndTime,
                       subject_name AS SubjectName,
                       section     AS Section,
                       room_code   AS Room
                FROM exam_schedules
                WHERE is_active = 1
                  AND (status IS NULL OR LOWER(status) IN ('active', 'scheduled', 'available', 'approved', 'published'))
                  AND (
                      date(exam_date) = date(@TodayDate)
                      OR (exam_date IS NULL AND (LOWER(day_of_week) = LOWER(@DayName) OR CAST(day_of_week AS TEXT) = @DayNumber))
                  )
            ", new { DayName = dayName, DayNumber = dayNumber, TodayDate = todayString });

            foreach (var r in examRows)
            {
                if (!TimeSpan.TryParse(r.StartTime, out var start))
                    start = TimeSpan.Zero;
                if (!TimeSpan.TryParse(r.EndTime, out var end))
                    end = TimeSpan.Zero;

                schedules.Add(new CachedSchedule(
                    r.ScheduleId, r.EmployeeId, r.DayOfWeek,
                    start, end,
                    r.SubjectName, r.Section, r.Room));
            }

            var grouped = schedules.GroupBy(s => s.EmployeeId)
                                   .ToDictionary(g => g.Key, g => g.ToList());

            _cache.Clear();
            foreach (var kvp in grouped)
                _cache[kvp.Key] = kvp.Value;

            _cacheDate = today;
            _isLoaded = true;

            // Also load all active employees into the RFID → Employee RAM cache
            await RefreshEmployeeCacheAsync(connection);

            Debug.WriteLine($"");
            Debug.WriteLine($"?? Schedule Cache Refreshed:");
            Debug.WriteLine($"   Date: {today:yyyy-MM-dd} ({dayName})");
            Debug.WriteLine($"   Employees with schedules: {_cache.Count}");
            Debug.WriteLine($"   Total schedule entries: {TotalScheduleCount}");
            Debug.WriteLine($"   Employees in RFID cache: {_employeeByRfid.Count}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? Schedule cache refresh failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Load all active employees into the RFID → Employee RAM dictionary.
    /// Called during <see cref="RefreshFromLocalDbAsync"/> so the kiosk
    /// can resolve RFID → Employee in ~0ms without touching disk.
    /// </summary>
    private async Task RefreshEmployeeCacheAsync(SqliteConnection connection)
    {
        try
        {
            var rows = await connection.QueryAsync<dynamic>(@"
                SELECT employee_id, rfid_code, full_name, school_id, department,
                       department_id, email, phone, schedule_time_in, schedule_time_out,
                       employment_status, employment_type, employment_subtype,
                       hire_date, start_date, role, employee_type, staff_type,
                       is_reporting_staff, year_level, current_section, current_semester,
                       unique_employee_id, verification_status, verified_by, verified_at,
                       photo_path, is_active
                FROM employees WHERE is_active = 1
            ");

            _employeeByRfid.Clear();
            foreach (var r in rows)
            {
                string? rfid = r.rfid_code?.ToString();
                if (string.IsNullOrEmpty(rfid)) continue;

                var emp = new Employee
                {
                    EmployeeId = (int)(long)r.employee_id,
                    RfidCode = rfid,
                    FullName = (string?)r.full_name ?? "",
                    SchoolId = (string?)r.school_id ?? "",
                    Department = (string?)r.department ?? "",
                    DepartmentId = r.department_id != null ? (int?)(long)r.department_id : null,
                    Email = (string?)r.email ?? "",
                    Phone = (string?)r.phone ?? "",
                    EmploymentStatus = (string?)r.employment_status,
                    EmploymentType = (string?)r.employment_type,
                    EmploymentSubtype = (string?)r.employment_subtype,
                    Role = (string?)r.role,
                    EmployeeType = (string?)r.employee_type,
                    StaffType = (string?)r.staff_type,
                    IsReportingStaff = r.is_reporting_staff != null && (long)r.is_reporting_staff == 1,
                    YearLevel = r.year_level != null ? (int?)(long)r.year_level : null,
                    CurrentSection = (string?)r.current_section,
                    CurrentSemester = r.current_semester != null ? (int?)(long)r.current_semester : null,
                    UniqueEmployeeId = (string?)r.unique_employee_id,
                    VerificationStatus = (string?)r.verification_status,
                    VerifiedBy = r.verified_by != null ? (int?)(long)r.verified_by : null,
                    PhotoPath = (string?)r.photo_path,
                    IsActive = r.is_active != null && (long)r.is_active == 1
                };

                // Parse TimeSpan fields
                if (r.schedule_time_in != null)
                {
                    if (TimeSpan.TryParse(r.schedule_time_in.ToString(), out TimeSpan ti))
                        emp.ScheduleTimeIn = ti;
                }
                if (r.schedule_time_out != null)
                {
                    if (TimeSpan.TryParse(r.schedule_time_out.ToString(), out TimeSpan to))
                        emp.ScheduleTimeOut = to;
                }
                // Parse dates
                if (r.hire_date != null)
                {
                    if (DateTime.TryParse(r.hire_date.ToString(), out DateTime hd))
                        emp.HireDate = hd;
                }
                if (r.start_date != null)
                {
                    if (DateTime.TryParse(r.start_date.ToString(), out DateTime sd))
                        emp.StartDate = sd;
                }
                if (r.verified_at != null)
                {
                    if (DateTime.TryParse(r.verified_at.ToString(), out DateTime va))
                        emp.VerifiedAt = va;
                }

                _employeeByRfid[rfid] = emp;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? Employee RFID cache refresh failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Instant RAM lookup of an employee by RFID code (O(1), ~0ms).
    /// Returns null if the RFID is not in the cache.
    /// Callers should fall back to SQLite/database if null is returned
    /// (e.g. employee was just registered and the cache hasn't refreshed yet).
    /// </summary>
    public Employee? GetEmployeeByRfid(string rfidCode)
    {
        return _employeeByRfid.TryGetValue(rfidCode, out var emp) ? emp : null;
    }

    /// <summary>
    /// Hot-insert a single employee into the RAM cache.
    /// Called after an online lookup succeeds so subsequent taps
    /// resolve instantly even before the next cache refresh.
    /// </summary>
    public void CacheEmployee(Employee emp)
    {
        if (!string.IsNullOrEmpty(emp.RfidCode))
            _employeeByRfid[emp.RfidCode] = emp;
    }

    /// <summary>
    /// Look up an employee by ID from the RAM cache (O(n) scan).
    /// Returns null if not found. Used as a fallback when SQLite
    /// is missing the employee (ghost data scenario).
    /// </summary>
    public Employee? GetEmployeeById(int employeeId)
    {
        return _employeeByRfid.Values.FirstOrDefault(e => e.EmployeeId == employeeId);
    }

    /// <summary>
    /// Get schedules for an employee from RAM (sub-1ms).
    /// Returns empty list if employee has no classes today.
    /// Automatically invalidates stale cache from a previous day.
    /// </summary>
    public List<CachedSchedule> GetSchedules(int employeeId)
    {
        // Invalidate if we rolled past midnight
        if (_cacheDate != DateTime.Today)
        {
            _isLoaded = false;
            _cache.Clear();
            return [];
        }

        return _cache.TryGetValue(employeeId, out var schedules)
            ? schedules
            : [];
    }

    /// <summary>
    /// Check whether an employee has ANY schedule today (RAM lookup, O(1)).
    /// </summary>
    public bool HasScheduleToday(int employeeId)
    {
        if (_cacheDate != DateTime.Today) return false;
        return _cache.ContainsKey(employeeId);
    }

    /// <summary>
    /// Get earliest start and latest end time for an employee today (RAM lookup).
    /// Returns null if no schedule found.
    /// </summary>
    public (TimeSpan Start, TimeSpan End)? GetExpectedWindow(int employeeId)
    {
        var schedules = GetSchedules(employeeId);
        if (schedules.Count == 0) return null;

        var earliest = schedules.Min(s => s.StartTime);
        var latest = schedules.Max(s => s.EndTime);
        return (earliest, latest);
    }

    /// <summary>
    /// Instant RAM-based attendance pre-check.
    /// If <paramref name="emp"/> is Part-Time and has NO schedule today,
    /// returns <see cref="SchedulePreCheckResult.ShouldWarn"/> = true
    /// so the UI can immediately transition to the Warning overlay
    /// without waiting for any DB round-trip.
    /// </summary>
    public SchedulePreCheckResult ValidateAttendance(Employee emp)
    {
        var schedules = GetSchedules(emp.EmployeeId);
        var window = schedules.Count > 0
            ? (GetExpectedWindow(emp.EmployeeId))
            : null;

        var isPartTime = emp.EmploymentStatus != null &&
                         emp.EmploymentStatus.Contains("Part", StringComparison.OrdinalIgnoreCase) &&
                         !emp.EmploymentStatus.Contains("Full Load", StringComparison.OrdinalIgnoreCase);

        string? reason = null;
        if (isPartTime && schedules.Count == 0)
        {
            reason = $"NO_SCHEDULE: Part-Time employee {emp.FullName} has no classes on {DateTime.Today:dddd}.";
            Debug.WriteLine($"\u26A0\uFE0F Schedule pre-check: {reason}");
        }
        else
        {
            Debug.WriteLine($"\u2705 Schedule pre-check: {emp.FullName} has {schedules.Count} class(es) today");
        }

        return new SchedulePreCheckResult
        {
            HasSchedule = schedules.Count > 0,
            IsPartTime = isPartTime,
            WarningReason = reason,
            EarliestStart = window?.Start,
            LatestEnd = window?.End,
            ScheduleCount = schedules.Count
        };
    }
}
