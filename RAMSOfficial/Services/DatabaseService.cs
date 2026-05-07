using Npgsql;
using Dapper;
using RAMSOfficial.Helpers;
using RAMSOfficial.Models;
using RAMSOfficial.Services; // Added for AttendanceValidationService

namespace RAMSOfficial.Services;

public class DatabaseService
{
    private readonly string _connectionString;
    private ProfilePhotoService? _profilePhotoService;

    public DatabaseService()
    {
        _connectionString = "Host=localhost;Port=5432;Username=postgres;Password=121121212112@Aa;Database=stirams;CommandTimeout=10;Timeout=10";
        _profilePhotoService = new ProfilePhotoService(_connectionString);
    }

    /// <summary>
    /// Access the ProfilePhotoService for advanced photo operations.
    /// Includes batch loading, photo statistics, save/delete operations.
    /// </summary>
    public ProfilePhotoService ProfilePhotos => _profilePhotoService ?? new ProfilePhotoService(_connectionString);

    public async Task<byte[]?> GetEmployeePhotoBytesAsync(int employeeId)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);

            var imageBytes = await connection.QueryFirstOrDefaultAsync<byte[]>(@"
                SELECT image_data
                FROM profile_photos
                WHERE entity_type = 'employee'
                    AND entity_id = @EmployeeId
                    AND image_data IS NOT NULL
                LIMIT 1
            ", new { EmployeeId = employeeId });

            return imageBytes;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error getting employee photo bytes: {ex.Message}");
            return null;
        }
    }

    public string GetConnectionString()
    {
        return _connectionString;
    }

    public async Task<bool> InitializeDatabaseAsync()
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            // Check if tables exist
            var employeesExists = await connection.ExecuteScalarAsync<bool>(@"
                SELECT EXISTS (
                    SELECT FROM information_schema.tables 
                    WHERE table_schema = 'public' 
                    AND table_name = 'employees'
                )
            ");

            var attendanceExists = await connection.ExecuteScalarAsync<bool>(@"
                SELECT EXISTS (
                    SELECT FROM information_schema.tables 
                    WHERE table_schema = 'public' 
                    AND table_name = 'attendance_logs'
                )
            ");

            return employeesExists && attendanceExists;
        }
        catch (NpgsqlException)
        {
            // Connection / auth / query failure — NOT the same as "tables missing"
            // Return null-like via a separate method so caller can distinguish
            throw;
        }
        catch (TimeoutException)
        {
            throw;
        }
    }

    public async Task<Employee?> GetEmployeeByRfidAsync(string rfidCode)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            
            // NOW we can query directly from employees table since rfid_code IS there!
            var employee = await connection.QueryFirstOrDefaultAsync<Employee>(@"
                SELECT 
                    employee_id as EmployeeId,
                    rfid_code as RfidCode,
                    full_name as FullName,
                    school_id as SchoolId,
                    department as Department,
                    department_id as DepartmentId,
                    email as Email,
                    phone as Phone,
                    password as Password,
                    schedule_time_in as ScheduleTimeIn,
                    schedule_time_out as ScheduleTimeOut,
                    employment_status as EmploymentStatus,
                    employment_type as EmploymentType,
                    employment_subtype as EmploymentSubtype,
                    hire_date as HireDate,
                    start_date as StartDate,
                    role as Role,
                    employee_type as EmployeeType,
                    staff_type as StaffType,
                    is_reporting_staff as IsReportingStaff,
                    year_level as YearLevel,
                    current_section as CurrentSection,
                    current_semester as CurrentSemester,
                    unique_employee_id as UniqueEmployeeId,
                    verification_status as VerificationStatus,
                    verified_by as VerifiedBy,
                    verified_at as VerifiedAt,
                    photo_path as PhotoPath,
                    is_active as IsActive,
                    created_at as CreatedAt,
                    updated_at as UpdatedAt
                FROM employees 
                WHERE TRIM(rfid_code) = @RfidCode
            ", new { RfidCode = rfidCode });

            return employee;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error getting employee by RFID: {ex.Message}");
            throw;
        }
    }

    public async Task<Employee?> GetEmployeeByIdAsync(int employeeId)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            
            var employee = await connection.QueryFirstOrDefaultAsync<Employee>(@"
                SELECT 
                    employee_id as EmployeeId,
                    rfid_code as RfidCode,
                    full_name as FullName,
                    school_id as SchoolId,
                    department as Department,
                    department_id as DepartmentId,
                    email as Email,
                    phone as Phone,
                    password as Password,
                    schedule_time_in as ScheduleTimeIn,
                    schedule_time_out as ScheduleTimeOut,
                    employment_status as EmploymentStatus,
                    employment_type as EmploymentType,
                    employment_subtype as EmploymentSubtype,
                    hire_date as HireDate,
                    start_date as StartDate,
                    role as Role,
                    employee_type as EmployeeType,
                    staff_type as StaffType,
                    is_reporting_staff as IsReportingStaff,
                    year_level as YearLevel,
                    current_section as CurrentSection,
                    current_semester as CurrentSemester,
                    unique_employee_id as UniqueEmployeeId,
                    verification_status as VerificationStatus,
                    verified_by as VerifiedBy,
                    verified_at as VerifiedAt,
                    photo_path as PhotoPath,
                    is_active as IsActive,
                    created_at as CreatedAt,
                    updated_at as UpdatedAt
                FROM employees 
                WHERE employee_id = @EmployeeId
            ", new { EmployeeId = employeeId });

            return employee;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error getting employee by ID: {ex.Message}");
            throw;
        }
    }

    public async Task<string> GetLastTapTypeForDateAsync(int employeeId, DateTime date)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            
            var lastLogType = await connection.QueryFirstOrDefaultAsync<string>(@"
                SELECT log_type 
                FROM attendance_logs 
                WHERE employee_id = @EmployeeId
                    AND date = @Date
                ORDER BY log_time DESC 
                LIMIT 1
            ", new { EmployeeId = employeeId, Date = date.Date });

            System.Diagnostics.Debug.WriteLine($"? GetLastTapTypeForDateAsync: Employee {employeeId}, Date {date:yyyy-MM-dd}, Last Tap: {lastLogType ?? "NONE"}");
            
            return lastLogType?.ToUpper() ?? "OUT";
        }
        catch
        {
            return "OUT";
        }
    }

    public async Task<string> GetLastTapTypeAsync(int employeeId)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            
            var lastLogType = await connection.QueryFirstOrDefaultAsync<string>(@"
                SELECT log_type 
                FROM attendance_logs 
                WHERE employee_id = @EmployeeId 
                ORDER BY log_time DESC 
                LIMIT 1
            ", new { EmployeeId = employeeId });

            return lastLogType?.ToUpper() ?? "OUT";
        }
        catch
        {
            return "OUT";
        }
    }

    public async Task<AttendanceLog> RecordAttendanceAsync(int employeeId, string tapType, AttendanceValidationResponse? employmentValidation = null, MissedLogValidationResult? missedLogValidation = null)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            
            // ???????????????????????????????????????????????????????????????
            // ?? CRITICAL: Check for active academic term FIRST
            // Block ALL attendance operations if no active term exists
            // ???????????????????????????????????????????????????????????????
            System.Diagnostics.Debug.WriteLine($"");
            System.Diagnostics.Debug.WriteLine($"???????????????????????????????????????????");
            System.Diagnostics.Debug.WriteLine($"?? PRE-VALIDATION: Checking System Availability");
            
            var termFilterHelper = new Helpers.AcademicTermFilterHelper(_connectionString);
            var activeTerm = await termFilterHelper.GetActiveAcademicTermAsync(DateTime.Now.Date);
            
            if (activeTerm.ShouldBlockSystem)
            {
                System.Diagnostics.Debug.WriteLine($"");
                System.Diagnostics.Debug.WriteLine($"?????? SYSTEM BLOCKED - NO ATTENDANCE ALLOWED ??????");
                System.Diagnostics.Debug.WriteLine($"? Reason: {activeTerm.GetBlockReason()}");
                System.Diagnostics.Debug.WriteLine($"? Employee ID {employeeId} attempted to tap {tapType}");
                System.Diagnostics.Debug.WriteLine($"?? NO DATA WILL BE SAVED TO DATABASE");
                System.Diagnostics.Debug.WriteLine($"");
                System.Diagnostics.Debug.WriteLine($"?? ACTION REQUIRED BY ADMINISTRATOR:");
                System.Diagnostics.Debug.WriteLine($"   1. Activate an academic term in the database");
                System.Diagnostics.Debug.WriteLine($"   2. SQL: UPDATE academic_terms SET is_active = true WHERE id = ?");
                System.Diagnostics.Debug.WriteLine($"???????????????????????????????????????????");
                
                throw new InvalidOperationException(activeTerm.GetBlockReason() + " Please contact the system administrator to activate an academic term.");
            }
            
            System.Diagnostics.Debug.WriteLine($"? System Available - Active Term: {activeTerm.TermName}");
            System.Diagnostics.Debug.WriteLine($"   Term ID: {activeTerm.TermId}");
            System.Diagnostics.Debug.WriteLine($"   Academic Year: {activeTerm.AcademicYear}");
            System.Diagnostics.Debug.WriteLine($"???????????????????????????????????????????");
            
            // Get employee's RFID code
            var employee = await GetEmployeeByIdAsync(employeeId);
            if (employee == null || string.IsNullOrEmpty(employee.RfidCode))
            {
                throw new Exception("Employee not found or has no RFID code.");
            }

            // ???????????????????????????????????????????????????????
            // ? CRITICAL FIX: Use REQUESTED TIME from missed log if available
            // This ensures the database saves the SAME time shown in UI
            // ???????????????????????????????????????????????????????
            DateTime now;
            bool usedMissedLogTime = false;
            
            if (missedLogValidation != null && 
                missedLogValidation.HasApprovedMissedLog && 
                missedLogValidation.RequestedTime.HasValue)
            {
                // ? USE REQUESTED TIME from missed log request
                now = missedLogValidation.RequestedTime.Value;
                usedMissedLogTime = true;
                
                System.Diagnostics.Debug.WriteLine($"");
                System.Diagnostics.Debug.WriteLine($"???????????????????????????????????????");
                System.Diagnostics.Debug.WriteLine($"  ? USING MISSED LOG REQUESTED TIME");
                System.Diagnostics.Debug.WriteLine($"  Request ID: #{missedLogValidation.RequestId}");
                System.Diagnostics.Debug.WriteLine($"  Requested Time: {now:yyyy-MM-dd HH:mm:ss}");
                System.Diagnostics.Debug.WriteLine($"  This time will be SAVED in database");
                System.Diagnostics.Debug.WriteLine($"???????????????????????????????????????");
            }
            else
            {
                // ? USE CUSTOM TIME if Time Override is active, or real time
                now = TimeChangerWindow.GetCurrentTime();
            }
            
            var today = now.Date;
            
            System.Diagnostics.Debug.WriteLine($"");
            System.Diagnostics.Debug.WriteLine($"???????????????????????????????????????");
            System.Diagnostics.Debug.WriteLine($"  ?? RECORDING ATTENDANCE                              ");
            System.Diagnostics.Debug.WriteLine($"  Employee: {employee.FullName,-42} ");
            System.Diagnostics.Debug.WriteLine($"  Type: {tapType,-47} ");
            System.Diagnostics.Debug.WriteLine($"  Date: {today:yyyy-MM-dd}                                      ");
            System.Diagnostics.Debug.WriteLine($"  Time: {now:HH:mm}                                          ");
            
            if (usedMissedLogTime)
            {
                System.Diagnostics.Debug.WriteLine($"  Mode: ? MISSED LOG REQUESTED TIME");
                System.Diagnostics.Debug.WriteLine($"  Request ID: #{missedLogValidation!.RequestId}");
            }
            else if (TimeChangerWindow.IsTimeOverridden())
            {
                System.Diagnostics.Debug.WriteLine($"  Mode: ? CUSTOM OVERRIDE (Time Changer)");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"  Mode: ? Real Time");
            }
            
            if (employmentValidation != null)
            {
                System.Diagnostics.Debug.WriteLine($"  Employment Mode: {employmentValidation.Mode ?? "N/A",-37} ");
            }
            if (missedLogValidation != null && missedLogValidation.HasApprovedMissedLog)
            {
                System.Diagnostics.Debug.WriteLine($"  ?? MISSED LOG: Request #{missedLogValidation.RequestId}");
                System.Diagnostics.Debug.WriteLine($"  ? STATUS WILL BE FORCED TO 'ON-TIME'");
            }
            System.Diagnostics.Debug.WriteLine($"???????????????????????????????????????");
            
            // ? CHECK FOR EXISTING ATTENDANCE (Prevent Duplicate)
            var existingLog = await connection.QueryFirstOrDefaultAsync<AttendanceLog>(@"
                SELECT 
                    log_id as LogId,
                    employee_id as EmployeeId,
                    rfid_code as RfidCode,
                    log_time as LogTime,
                    log_type as LogType,
                    date as Date,
                    attendance_status as AttendanceStatus,
                    is_late as IsLate,
                    is_early_out as IsEarlyOut,
                    late_minutes as LateMinutes,
                    undertime_minutes as UndertimeMinutes,
                    notes as Notes,
                    is_admin_time as IsAdminTime,
                    is_holiday as IsHoliday,
                    is_suspended as IsSuspended,
                    is_online_class as IsOnlineClass,
                    schedule_id as ScheduleId,
                    term_id as TermId,
                    created_at as CreatedAt
                FROM attendance_logs 
                WHERE employee_id = @EmployeeId 
                    AND date = @Date
                    AND log_type = @LogType
                ORDER BY log_time DESC
                LIMIT 1
            ", new { EmployeeId = employeeId, Date = today, LogType = tapType });

            if (existingLog != null)
            {
                // ?? DUPLICATE FOUND - Return existing record instead of inserting
                System.Diagnostics.Debug.WriteLine($"");
                System.Diagnostics.Debug.WriteLine($"?? DUPLICATE ATTENDANCE DETECTED!");
                System.Diagnostics.Debug.WriteLine($"   Employee already has a {tapType} record for today");
                System.Diagnostics.Debug.WriteLine($"   Existing Log ID: {existingLog.LogId}");
                System.Diagnostics.Debug.WriteLine($"   Existing Time: {existingLog.LogTime:yyyy-MM-dd HH:mm:ss}");
                System.Diagnostics.Debug.WriteLine($"   Status: {existingLog.AttendanceStatus}");
                System.Diagnostics.Debug.WriteLine($"");
                System.Diagnostics.Debug.WriteLine($"? Returning existing record (no duplicate insert)");
                System.Diagnostics.Debug.WriteLine($"???????????????????????????????????????????????????????");
                
                return existingLog;
            }
            
            // ? NO DUPLICATE - Safe to insert
            System.Diagnostics.Debug.WriteLine($"? No duplicate found - proceeding with insert");
            
            // Validate attendance with comprehensive logic
            var validationService = new AttendanceValidationService(_connectionString);
            var validation = await validationService.ValidateAttendanceAsync(employeeId, now, tapType);
            
            System.Diagnostics.Debug.WriteLine($"?? Validation completed - building attendance record");
            System.Diagnostics.Debug.WriteLine($"   BEFORE processing:");
            System.Diagnostics.Debug.WriteLine($"     validation.AttendanceStatus = '{validation.AttendanceStatus}'");
            if (employmentValidation != null)
            {
                System.Diagnostics.Debug.WriteLine($"     employmentValidation.Mode = '{employmentValidation.Mode}'");
            }
            
            // Determine attendance status based on employment validation mode
            string attendanceStatus;
            
            // ???????????????????????????????????????????????????????
            // ? CRITICAL FIX: REMOVED MISSED LOG OVERRIDE
            // Missed logs should NOT force status to "on-time"
            // The status should be calculated based on actual tap time vs schedule
            // Missed logs only affect the TIME displayed, not the STATUS
            // ???????????????????????????????????????????????????????
            
            if (employmentValidation != null && employmentValidation.Mode == "AdminTime")
            {
                // ???????????????????????????????????????????????
                // ? CRITICAL FIX: Part-Time Full Load with NO class schedule
                // BUT - Check if AttendanceValidationService found an active schedule NOW
                // INCLUDES: class, exam, OR substitution
                // ???????????????????????????????????????????????
                
                // If validation found ANY active schedule (class, exam, or substitution), use that status instead
                var hasActiveScheduleNow =
                    (validation.ScheduleType == "class" && validation.Priority == 5) ||
                    (validation.ScheduleType == "exam" && validation.Priority == 4) ||
                    (validation.ScheduleType == "substitution" && validation.Priority == 3) ||
                    (validation.ScheduleType == "combined_schedule" && validation.Priority == 3) ||
                    (validation.ExpectedTimeIn.HasValue && validation.ExpectedTimeOut.HasValue &&
                     !string.Equals(validation.ScheduleType, "holiday", StringComparison.OrdinalIgnoreCase));

                if (hasActiveScheduleNow)
                {
                    // There IS an active schedule NOW - use normal status
                    var rawStatus = validation.AttendanceStatus;
                    attendanceStatus = !string.IsNullOrWhiteSpace(rawStatus) 
                        ? rawStatus.ToLowerInvariant() 
                        : "on-time";
                    
                    System.Diagnostics.Debug.WriteLine($"   ? Employment said AdminTime BUT active {validation.ScheduleType} found NOW");
                    System.Diagnostics.Debug.WriteLine($"   ? Using schedule-based status: {attendanceStatus}");
                    System.Diagnostics.Debug.WriteLine($"   ? Schedule Details: {validation.ScheduleDetails}");
                }
                else
                {
                    // No active schedule NOW - use admin-time
                    attendanceStatus = "admin-time";
                    validation.AttendanceStatus = "admin-time";
                    validation.Notes = "Part-Time Full Load - Admin Time (no class schedule active now)";
                    validation.IsLate = false;
                    validation.IsEarlyOut = false;
                    validation.LateMinutes = null;
                    validation.UndertimeMinutes = null;
                    
                    System.Diagnostics.Debug.WriteLine($"   ? Status FORCED to: admin-time (PT Full Load, no active class now)");
                    System.Diagnostics.Debug.WriteLine($"   ? All late/undertime flags cleared");
                }
            }
            else
            {
                // Use normal validation status - ensure it's lowercase and not null
                var rawStatus = validation.AttendanceStatus;
                System.Diagnostics.Debug.WriteLine($"     Raw validation status: '{rawStatus}'");
                
                attendanceStatus = !string.IsNullOrWhiteSpace(rawStatus) 
                    ? rawStatus.ToLowerInvariant() 
                    : "on-time";
                    
                System.Diagnostics.Debug.WriteLine($"   ? Status set to: {attendanceStatus}");
                
                // ?? Log missed log info if present (for debugging)
                if (missedLogValidation != null && missedLogValidation.HasApprovedMissedLog)
                {
                    System.Diagnostics.Debug.WriteLine($"   ?? MISSED LOG PRESENT: Request #{missedLogValidation.RequestId}");
                    System.Diagnostics.Debug.WriteLine($"   ?? Reason: {missedLogValidation.Reason}");
                    System.Diagnostics.Debug.WriteLine($"   ?? Status NOT overridden - using calculated: {attendanceStatus}");
                    
                    // Add missed log info to notes
                    validation.Notes = $"{validation.Notes ?? ""} | Missed Log Request #{missedLogValidation.RequestId}: {missedLogValidation.Reason}".Trim('|').Trim();
                }
            }

            // Always tag notes with schedule source for traceability in attendance logs.
            var scheduleTypeLabel = validation.ScheduleType switch
            {
                "exam" => "Exam Schedule",
                "class" => "Class Schedule",
                "substitution" => "Substitute Schedule",
                "combined_schedule" => "Combined Schedule",
                "holiday" => "Holiday",
                "verification" => "Verification",
                "regular" => "Regular Schedule",
                "admin_time" => "Admin Time",
                "admin-time" => "Admin Time",
                _ => string.IsNullOrWhiteSpace(validation.ScheduleType) ? "Unknown" : validation.ScheduleType
            };

            var scheduleContext = $"Source: {scheduleTypeLabel}";
            if (!string.IsNullOrWhiteSpace(validation.ScheduleDetails))
            {
                scheduleContext += $" ({validation.ScheduleDetails})";
            }

            validation.Notes = string.IsNullOrWhiteSpace(validation.Notes)
                ? scheduleContext
                : $"{validation.Notes} | {scheduleContext}";

            // ? SAFETY CHECK: Ensure status is one of the 5 allowed values
            // Source: attendance_logs_status_valid CHECK constraint in database
            var allowedStatuses = new[] { "on-time", "late", "undertime", "admin-time", "absent" };
            
            System.Diagnostics.Debug.WriteLine($"   ============================================");
            System.Diagnostics.Debug.WriteLine($"   PRE-INSERT VALIDATION:");
            System.Diagnostics.Debug.WriteLine($"   Current status value: '{attendanceStatus}'");
            System.Diagnostics.Debug.WriteLine($"   Length: {attendanceStatus?.Length ?? 0}");
            System.Diagnostics.Debug.WriteLine($"   Is in allowed list: {allowedStatuses.Contains(attendanceStatus)}");
            System.Diagnostics.Debug.WriteLine($"   Allowed values: {string.Join(", ", allowedStatuses.Select(s => $"'{s}'"))}");
            
            if (!allowedStatuses.Contains(attendanceStatus))
            {
                System.Diagnostics.Debug.WriteLine($"   ?????? INVALID STATUS DETECTED ??????");
                System.Diagnostics.Debug.WriteLine($"   REJECTED VALUE: '{attendanceStatus}'");
                System.Diagnostics.Debug.WriteLine($"   VALUE BYTES: {string.Join(" ", System.Text.Encoding.UTF8.GetBytes(attendanceStatus ?? "").Select(b => b.ToString("X2")))}");
                System.Diagnostics.Debug.WriteLine($"   ?? Full validation details:");
                System.Diagnostics.Debug.WriteLine($"      ScheduleType: {validation.ScheduleType}");
                System.Diagnostics.Debug.WriteLine($"      Priority: {validation.Priority}");
                System.Diagnostics.Debug.WriteLine($"      Notes: {validation.Notes}");
                System.Diagnostics.Debug.WriteLine($"      IsLate: {validation.IsLate}");
                System.Diagnostics.Debug.WriteLine($"      IsEarlyOut: {validation.IsEarlyOut}");
                System.Diagnostics.Debug.WriteLine($"      EmployeeId: {employeeId}");
                System.Diagnostics.Debug.WriteLine($"      TapType: {tapType}");
                if (employmentValidation != null)
                {
                    System.Diagnostics.Debug.WriteLine($"      EmploymentValidation.Mode: {employmentValidation.Mode}");
                    System.Diagnostics.Debug.WriteLine($"      EmploymentValidation.Status: {employmentValidation.Status}");
                }
                System.Diagnostics.Debug.WriteLine($"   ? FORCING TO: 'on-time'");
                attendanceStatus = "on-time"; // Fallback to safe default
                validation.AttendanceStatus = "on-time";
            }
            
            System.Diagnostics.Debug.WriteLine($"   FINAL STATUS FOR INSERT: '{attendanceStatus}'");
            System.Diagnostics.Debug.WriteLine($"   ============================================");
            
            // ?? LOG THE EXACT PARAMETERS BEFORE INSERT
            System.Diagnostics.Debug.WriteLine($"");
            System.Diagnostics.Debug.WriteLine($"   ?? PREPARING DATABASE INSERT:");
            System.Diagnostics.Debug.WriteLine($"      EmployeeId: {employeeId}");
            System.Diagnostics.Debug.WriteLine($"      RfidCode: {employee.RfidCode}");
            System.Diagnostics.Debug.WriteLine($"      LogTime: {now}");
            System.Diagnostics.Debug.WriteLine($"      LogType: {tapType}");
            System.Diagnostics.Debug.WriteLine($"      Date: {today}");
            System.Diagnostics.Debug.WriteLine($"      Status: '{attendanceStatus}' (Length: {attendanceStatus?.Length ?? 0})");
            System.Diagnostics.Debug.WriteLine($"      IsLate: {validation.IsLate}");
            System.Diagnostics.Debug.WriteLine($"      IsEarlyOut: {validation.IsEarlyOut}");
            System.Diagnostics.Debug.WriteLine($"      LateMinutes: {validation.LateMinutes}");
            System.Diagnostics.Debug.WriteLine($"      UndertimeMinutes: {validation.UndertimeMinutes}");
            System.Diagnostics.Debug.WriteLine($"      Notes: {validation.Notes}");
            System.Diagnostics.Debug.WriteLine($"      ScheduleId: {validation.ScheduleId}");
            
            // Insert attendance log with validation results
            int logId;
            try
            {
                logId = await connection.ExecuteScalarAsync<int>(@"
                    INSERT INTO attendance_logs (
                        employee_id, rfid_code, log_time, log_type, date,
                        attendance_status, is_late, is_early_out,
                        late_minutes, undertime_minutes, notes,
                        is_admin_time, admin_time_reason, admin_time_status,
                        is_holiday, is_suspended, is_online_class,
                        schedule_id, term_id
                    )
                    VALUES (
                        @EmployeeId, @RfidCode, @LogTime, @LogType, @Date,
                        @Status, @IsLate, @IsEarlyOut,
                        @LateMinutes, @UndertimeMinutes, @Notes,
                        @IsAdminTime, @AdminTimeReason, @AdminTimeStatus,
                        @IsHoliday, @IsSuspended, @IsOnlineClass,
                        @ScheduleId, @TermId
                    )
                    RETURNING log_id
                ", new {
                    EmployeeId = employeeId,
                    RfidCode = employee.RfidCode,
                    LogTime = now,
                    LogType = tapType,
                    Date = today,
                    Status = DataSanitizer.NormalizeAttendanceStatus(attendanceStatus),
                    IsLate = validation.IsLate,
                    IsEarlyOut = validation.IsEarlyOut,
                    LateMinutes = validation.LateMinutes,
                    UndertimeMinutes = validation.UndertimeMinutes,
                    Notes = validation.Notes,
                    IsAdminTime = attendanceStatus == "admin-time",
                    AdminTimeReason = attendanceStatus == "admin-time" ? validation.Notes : null,
                    AdminTimeStatus = attendanceStatus == "admin-time" ? "approved" : null,
                    IsHoliday = validation.IsHoliday,
                    IsSuspended = string.Equals(validation.HolidayType, "suspended_asynchronous", StringComparison.OrdinalIgnoreCase),
                    IsOnlineClass = string.Equals(validation.HolidayType, "online_class", StringComparison.OrdinalIgnoreCase),
                    ScheduleId = validation.ScheduleId,
                    TermId = activeTerm.TermId
                });
                
                System.Diagnostics.Debug.WriteLine($"   ? INSERT SUCCESSFUL! Log ID: {logId}");
            }
            catch (Npgsql.PostgresException pgEx)
            {
                System.Diagnostics.Debug.WriteLine($"");
                System.Diagnostics.Debug.WriteLine($"   ??? POSTGRESQL ERROR ???");
                System.Diagnostics.Debug.WriteLine($"   Error Code: {pgEx.SqlState}");
                System.Diagnostics.Debug.WriteLine($"   Error Message: {pgEx.Message}");
                System.Diagnostics.Debug.WriteLine($"   Constraint: {pgEx.ConstraintName}");
                System.Diagnostics.Debug.WriteLine($"   Detail: {pgEx.Detail}");
                System.Diagnostics.Debug.WriteLine($"   Hint: {pgEx.Hint}");
                System.Diagnostics.Debug.WriteLine($"   ");
                System.Diagnostics.Debug.WriteLine($"   THE VALUE THAT FAILED:");
                System.Diagnostics.Debug.WriteLine($"   attendance_status = '{attendanceStatus}'");
                var rawStatus = attendanceStatus ?? string.Empty;
                System.Diagnostics.Debug.WriteLine($"   Raw bytes: {string.Join(" ", System.Text.Encoding.UTF8.GetBytes(rawStatus).Select(b => $"0x{b:X2}"))}");
                System.Diagnostics.Debug.WriteLine($"   ");
                System.Diagnostics.Debug.WriteLine($"   EXPECTED VALUES:");
                System.Diagnostics.Debug.WriteLine($"   'on-time', 'late', 'undertime', 'admin-time'");
                System.Diagnostics.Debug.WriteLine($"   ???????????????");
                throw; // Re-throw to be caught by outer handler
            }
            
            // Fetch the created log
            var log = await connection.QueryFirstAsync<AttendanceLog>(@"
                SELECT 
                    log_id as LogId,
                    employee_id as EmployeeId,
                    rfid_code as RfidCode,
                    log_time as LogTime,
                    log_type as LogType,
                    date as Date,
                    attendance_status as AttendanceStatus,
                    is_late as IsLate,
                    is_early_out as IsEarlyOut,
                    late_minutes as LateMinutes,
                    undertime_minutes as UndertimeMinutes,
                    notes as Notes,
                    verified_by as VerifiedBy,
                    verified_at as VerifiedAt,
                    is_admin_time as IsAdminTime,
                    admin_time_reason as AdminTimeReason,
                    admin_time_status as AdminTimeStatus,
                    is_holiday as IsHoliday,
                    is_suspended as IsSuspended,
                    is_online_class as IsOnlineClass,
                    schedule_id as ScheduleId,
                    term_id as TermId,
                    created_at as CreatedAt
                FROM attendance_logs 
                WHERE log_id = @LogId
            ", new { LogId = logId });

            // Log validation details
            System.Diagnostics.Debug.WriteLine($"");
            System.Diagnostics.Debug.WriteLine($"? NEW Attendance Record Created:");
            System.Diagnostics.Debug.WriteLine($"   Log ID: {logId}");
            System.Diagnostics.Debug.WriteLine($"   Priority {validation.Priority}: {validation.ScheduleType}");
            System.Diagnostics.Debug.WriteLine($"   Status: {validation.AttendanceStatus}");
            System.Diagnostics.Debug.WriteLine($"   {validation.GetDisplayMessage()}");
            System.Diagnostics.Debug.WriteLine($"???????????????????????????????????????????????????????");

            return log;
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "23505")
        {
            // ? UNIQUE CONSTRAINT VIOLATION - This should rarely happen now due to pre-check
            System.Diagnostics.Debug.WriteLine($"");
            System.Diagnostics.Debug.WriteLine($"? DATABASE CONSTRAINT VIOLATION CAUGHT!");
            System.Diagnostics.Debug.WriteLine($"   SQL State: {ex.SqlState}");
            System.Diagnostics.Debug.WriteLine($"   Message: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"   This indicates a race condition - retrieving existing record...");
            
            // Fetch the existing record that caused the conflict
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            
            var today = DateTime.Now.Date;
            var existingLog = await connection.QueryFirstAsync<AttendanceLog>(@"
                SELECT 
                    log_id as LogId,
                    employee_id as EmployeeId,
                    rfid_code as RfidCode,
                    log_time as LogTime,
                    log_type as LogType,
                    date as Date,
                    attendance_status as AttendanceStatus,
                    is_late as IsLate,
                    is_early_out as IsEarlyOut,
                    late_minutes as LateMinutes,
                    undertime_minutes as UndertimeMinutes,
                    notes as Notes,
                    is_admin_time as IsAdminTime,
                    is_holiday as IsHoliday,
                    is_suspended as IsSuspended,
                    is_online_class as IsOnlineClass,
                    schedule_id as ScheduleId,
                    term_id as TermId,
                    created_at as CreatedAt
                FROM attendance_logs 
                WHERE employee_id = @EmployeeId 
                    AND date = @Date
                    AND log_type = @LogType
                ORDER BY log_time DESC
                LIMIT 1
            ", new { EmployeeId = employeeId, Date = today, LogType = tapType });
            
            System.Diagnostics.Debug.WriteLine($"? Retrieved existing record (Log ID: {existingLog.LogId})");
            System.Diagnostics.Debug.WriteLine($"???????????????????????????????????????????????????????");
            
            return existingLog;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"");
            System.Diagnostics.Debug.WriteLine($"? ERROR recording attendance:");
            System.Diagnostics.Debug.WriteLine($"   Type: {ex.GetType().Name}");
            System.Diagnostics.Debug.WriteLine($"   Message: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"   Stack: {ex.StackTrace}");
            System.Diagnostics.Debug.WriteLine($"???????????????????????????????????????????????????????");
            throw;
        }
    }

    public async Task<List<AttendanceLog>> GetTodayAttendanceAsync(int employeeId)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            
            // Use custom time date if set, otherwise use current date
            var targetDate = TimeChangerWindow.GetCurrentTime().Date;
            
            var logs = await connection.QueryAsync<AttendanceLog>(@"
                SELECT 
                    log_id as LogId,
                    employee_id as EmployeeId,
                    rfid_code as RfidCode,
                    log_time as LogTime,
                    log_type as LogType,
                    date as Date,
                    attendance_status as AttendanceStatus,
                    is_late as IsLate,
                    is_early_out as IsEarlyOut,
                    late_minutes as LateMinutes,
                    undertime_minutes as UndertimeMinutes,
                    notes as Notes,
                    created_at as CreatedAt
                FROM attendance_logs 
                WHERE employee_id = @EmployeeId 
                    AND date = @TargetDate
                ORDER BY log_time DESC
            ", new { EmployeeId = employeeId, TargetDate = targetDate });

            return logs.ToList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error getting today's attendance: {ex.Message}");
            return new List<AttendanceLog>();
        }
    }

    public async Task<IEnumerable<AttendanceLog>> GetTodayAttendanceForDateAsync(int employeeId, DateTime date)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            
            var logs = await connection.QueryAsync<AttendanceLog>(@"
                SELECT 
                    log_id as LogId,
                    employee_id as EmployeeId,
                    rfid_code as RfidCode,
                    log_time as LogTime,
                    log_type as LogType,
                    date as Date,
                    attendance_status as AttendanceStatus,
                    is_late as IsLate,
                    is_early_out as IsEarlyOut,
                    late_minutes as LateMinutes,
                    undertime_minutes as UndertimeMinutes,
                    notes as Notes,
                    created_at as CreatedAt
                FROM attendance_logs
                WHERE employee_id = @EmployeeId
                    AND date = @Date
                ORDER BY log_time ASC
            ", new { EmployeeId = employeeId, Date = date.Date });

            return logs;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error getting attendance for date: {ex.Message}");
            return Enumerable.Empty<AttendanceLog>();
        }
    }

    public async Task<List<AttendanceLog>> GetRecentAttendanceAsync(int limit = 10)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            
            var logs = await connection.QueryAsync<AttendanceLog>(@"
                SELECT 
                    log_id as LogId,
                    employee_id as EmployeeId,
                    rfid_code as RfidCode,
                    log_time as LogTime,
                    log_type as LogType,
                    date as Date,
                    attendance_status as AttendanceStatus,
                    is_late as IsLate,
                    is_early_out as IsEarlyOut,
                    notes as Notes,
                    created_at as CreatedAt
                FROM attendance_logs 
                ORDER BY log_time DESC 
                LIMIT @Limit
            ", new { Limit = limit });

            return logs.ToList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error getting recent attendance: {ex.Message}");
            return new List<AttendanceLog>();
        }
    }

    public async Task<List<RecentAttendanceItem>> GetRecentAttendanceItemsForDateAsync(DateTime date, int limit = 30)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);

            var items = await connection.QueryAsync<RecentAttendanceItem>(@"
                WITH day_logs AS (
                    SELECT
                        al.employee_id,
                        al.log_type,
                        al.log_time,
                        COALESCE(al.attendance_status, 'on-time') AS attendance_status
                    FROM attendance_logs al
                    WHERE al.date = @TargetDate
                      AND LOWER(COALESCE(al.attendance_status, 'on-time')) IN ('on-time', 'late', 'undertime')
                ),
                ranked AS (
                    SELECT
                        dl.employee_id,
                        dl.log_type,
                        dl.log_time,
                        dl.attendance_status,
                        ROW_NUMBER() OVER (
                            PARTITION BY dl.employee_id
                            ORDER BY dl.log_time DESC
                        ) AS rn
                    FROM day_logs dl
                ),
                in_out AS (
                    SELECT
                        dl.employee_id,
                        MIN(dl.log_time) FILTER (WHERE UPPER(dl.log_type) = 'IN') AS time_in,
                        MAX(dl.log_time) FILTER (WHERE UPPER(dl.log_type) = 'OUT') AS time_out
                    FROM day_logs dl
                    GROUP BY dl.employee_id
                )
                SELECT
                    r.employee_id                                         AS EmployeeId,
                    e.full_name                                           AS FullName,
                    COALESCE(e.department, '')                            AS Department,
                    SUBSTR(COALESCE(e.full_name, 'E'), 1, 1)             AS Initial,
                    r.log_type                                            AS LogType,
                    r.log_time                                            AS LogTime,
                    r.attendance_status                                   AS AttendanceStatus,
                    io.time_in                                            AS TimeIn,
                    io.time_out                                           AS TimeOut,
                    e.photo_path                                          AS PhotoPath
                FROM ranked r
                INNER JOIN employees e ON e.employee_id = r.employee_id AND e.is_active = true
                LEFT JOIN in_out io ON io.employee_id = r.employee_id
                WHERE r.rn = 1
                ORDER BY r.log_time DESC
                LIMIT @Limit
            ", new { TargetDate = date.Date, Limit = limit });

            return items.ToList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error getting recent attendance items for date: {ex.Message}");
            return new List<RecentAttendanceItem>();
        }
    }
}
