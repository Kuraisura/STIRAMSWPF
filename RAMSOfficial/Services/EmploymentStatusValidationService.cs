using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Npgsql;
using Dapper;
using RAMSOfficial.Models;

namespace RAMSOfficial.Services;

/// <summary>
/// Comprehensive Attendance Validation Service
/// Implements all employment status rules and schedule validation
/// </summary>
public class EmploymentStatusValidationService
{
    private readonly string _connectionString;

    public EmploymentStatusValidationService(string connectionString)
    {
        _connectionString = connectionString;
    }

    /// <summary>
    /// Master validation method - checks all rules before allowing attendance
    /// </summary>
    public async Task<AttendanceValidationResponse> ValidateAttendanceRequestAsync(
        int employeeId,
        DateTime tapTime)
    {
        var response = new AttendanceValidationResponse
        {
            EmployeeId = employeeId,
            TapTime = tapTime,
            IsAllowed = false
        };

        try
        {
            

            // ???????????????????????????????????????????????????????
            // RULE 1: SUNDAY GLOBAL BLOCK (Highest Priority)
            // ???????????????????????????????????????????????????????
            if (tapTime.DayOfWeek == DayOfWeek.Sunday)
            {
                response.Status = "BLOCKED";
                response.Message = "Attendance logging is disabled on Sundays.";
                response.Reason = "SUNDAY_BLOCK";
                System.Diagnostics.Debug.WriteLine("? BLOCKED: Sunday attendance not allowed");
                return response;
            }

            // ???????????????????????????????????????????????????????
            // RULE 2: Get Employee Details
            // ???????????????????????????????????????????????????????
            var employee = await GetEmployeeDetailsAsync(employeeId);
            
            if (employee == null)
            {
                response.Status = "ERROR";
                response.Message = "Employee not found.";
                response.Reason = "EMPLOYEE_NOT_FOUND";
                System.Diagnostics.Debug.WriteLine($"? ERROR: Employee {employeeId} not found");
                return response;
            }

            if (!employee.IsActive)
            {
                response.Status = "BLOCKED";
                response.Message = "Your account is inactive. Please contact HR.";
                response.Reason = "ACCOUNT_INACTIVE";
                System.Diagnostics.Debug.WriteLine($"? BLOCKED: Employee {employee.FullName} is inactive");
                return response;
            }

            response.EmployeeName = employee.FullName;
            response.EmploymentStatus = employee.EmploymentStatus;
            response.EmployeeType = employee.EmployeeType;

            System.Diagnostics.Debug.WriteLine($"");
            System.Diagnostics.Debug.WriteLine($"?????????????????????????????????????????????????????????");
            System.Diagnostics.Debug.WriteLine($"?  ATTENDANCE VALIDATION                                ?");
            System.Diagnostics.Debug.WriteLine($"?  Employee: {employee.FullName,-42} ?");
            System.Diagnostics.Debug.WriteLine($"?  Status: {employee.EmploymentStatus,-44} ?");
            System.Diagnostics.Debug.WriteLine($"?  Type: {employee.EmployeeType,-46} ?");
            System.Diagnostics.Debug.WriteLine($"?  Date: {tapTime:yyyy-MM-dd} | Day: {tapTime.DayOfWeek,-24} ?");
            System.Diagnostics.Debug.WriteLine($"?????????????????????????????????????????????????????????");

            // ???????????????????????????????????????????????????????
            // RULE 3: Route to Employment-Specific Validation
            // ???????????????????????????????????????????????????????
            switch (employee.EmploymentStatus?.ToUpper())
            {
                case "PART TIME":
                case "PART-TIME":
                case "PT":
                    return await ValidatePartTimeAsync(employee, tapTime, response);

                case "PART TIME FULL LOAD":
                case "PART-TIME FULL LOAD":
                case "PT FULL LOAD":
                case "PTFL":
                    return await ValidatePartTimeFullLoadAsync(employee, tapTime, response);

                case "REGULAR":
                case "FULL TIME":
                case "FULL-TIME":
                case "FT":
                case "PROVISIONARY":  // ? ADDED: Provisionary uses same validation as Regular
                    return await ValidateRegularEmployeeAsync(employee, tapTime, response);

                default:
                    // Unknown employment status - default to regular rules
                    System.Diagnostics.Debug.WriteLine($"? Unknown employment status: {employee.EmploymentStatus}");
                    System.Diagnostics.Debug.WriteLine($"   Defaulting to Regular employee validation");
                    return await ValidateRegularEmployeeAsync(employee, tapTime, response);
            }
        }
        catch (Exception ex) when (
            ex is System.Net.Sockets.SocketException ||
            ex is NpgsqlException ||
            ex.InnerException is System.Net.Sockets.SocketException ||
            ex.InnerException is NpgsqlException)
        {
            // Connection error → allow attendance in offline mode
            System.Diagnostics.Debug.WriteLine($"  ⚠ Connection error — offline fallback: {ex.Message}");
            response.IsAllowed = true;
            response.Status = "pending";
            response.Mode = null;
            response.Message = "Offline mode — attendance recorded locally";
            response.Reason = "OFFLINE_ALLOW";
            return response;
        }
        catch (Exception ex)
        {
            response.Status = "ERROR";
            response.Message = $"Validation error: {ex.Message}";
            response.Reason = "SYSTEM_ERROR";
            System.Diagnostics.Debug.WriteLine($"? VALIDATION ERROR: {ex.Message}");
            return response;
        }
    }

    // ???????????????????????????????????????
    // PART-TIME VALIDATION
    // ???????????????????????????????????????
    private async Task<AttendanceValidationResponse> ValidatePartTimeAsync(
        EmployeeDetails employee,
        DateTime tapTime,
        AttendanceValidationResponse response)
    {
        System.Diagnostics.Debug.WriteLine($"");
        System.Diagnostics.Debug.WriteLine($"?? PART-TIME VALIDATION");
        System.Diagnostics.Debug.WriteLine($"   Rule: MUST have class schedule today");
        System.Diagnostics.Debug.WriteLine($"   Staff Type: {employee.StaffType ?? "Unknown"}");

        // Get class schedules for today
        var schedules = await GetClassSchedulesForDateAsync(employee.EmployeeId, tapTime);

        if (schedules == null || !schedules.Any())
        {
            // ?? NO SCHEDULE - BLOCKED (Part-Time MUST have schedule)
            response.Status = "BLOCKED";
            response.Message = "Part-Time employees cannot log attendance without a class schedule today.";
            response.Reason = "NO_SCHEDULE";
            response.Mode = null;
            
            System.Diagnostics.Debug.WriteLine($"?? BLOCKED: No class schedule found for {tapTime:yyyy-MM-dd} ({tapTime.DayOfWeek})");
            System.Diagnostics.Debug.WriteLine($"   Part-Time employees MUST have a teaching schedule to attend");
            return response;
        }

        // ? HAS SCHEDULE - ALLOWED
        var currentSchedule = schedules.First();

        response.Status = "ALLOWED";
        response.IsAllowed = true;
        response.Message = $"Part-Time: Class schedule found. Attendance allowed.";
        response.Reason = "CLASS_SCHEDULE";
        response.Mode = "ClassBased";
        response.ScheduleDetails = new ScheduleInfo
        {
            ScheduleId = currentSchedule.ScheduleId,
            SubjectName = currentSchedule.SubjectName,
            Section = currentSchedule.Section,
            TimeStart = currentSchedule.TimeStart,
            TimeEnd = currentSchedule.TimeEnd,
            Room = currentSchedule.Room,
            DayOfWeek = currentSchedule.DayOfWeek
        };

        System.Diagnostics.Debug.WriteLine($"? ALLOWED: Class schedule found");
        System.Diagnostics.Debug.WriteLine($"   Subject: {currentSchedule.SubjectName}");
        System.Diagnostics.Debug.WriteLine($"   Section: {currentSchedule.Section}");
        System.Diagnostics.Debug.WriteLine($"   Time: {currentSchedule.TimeStart:hh\\:mm} - {currentSchedule.TimeEnd:hh\\:mm}");
        
        return response;
    }

    // ???????????????????????????????????????
    // PART-TIME FULL LOAD VALIDATION
    // ???????????????????????????????????????
    private async Task<AttendanceValidationResponse> ValidatePartTimeFullLoadAsync(
        EmployeeDetails employee,
        DateTime tapTime,
        AttendanceValidationResponse response)
    {
        System.Diagnostics.Debug.WriteLine($"");
        System.Diagnostics.Debug.WriteLine($"?? PART-TIME FULL LOAD VALIDATION");
        System.Diagnostics.Debug.WriteLine($"   Rule: Class schedule OR Admin Time");
        System.Diagnostics.Debug.WriteLine($"   Staff Type: {employee.StaffType ?? "Unknown"}");

        // Get class schedules for today
        var schedules = await GetClassSchedulesForDateAsync(employee.EmployeeId, tapTime);

        if (schedules != null && schedules.Any())
        {
            // ? HAS SCHEDULE - Class-based attendance
            var currentSchedule = schedules.First();

            response.Status = "ALLOWED";
            response.IsAllowed = true;
            response.Message = $"PT Full Load: Class schedule found. Attendance allowed.";
            response.Reason = "CLASS_SCHEDULE";
            response.Mode = "ClassBased";
            response.ScheduleDetails = new ScheduleInfo
            {
                ScheduleId = currentSchedule.ScheduleId,
                SubjectName = currentSchedule.SubjectName,
                Section = currentSchedule.Section,
                TimeStart = currentSchedule.TimeStart,
                TimeEnd = currentSchedule.TimeEnd,
                Room = currentSchedule.Room,
                DayOfWeek = currentSchedule.DayOfWeek
            };

            System.Diagnostics.Debug.WriteLine($"? ALLOWED: Class schedule found");
            System.Diagnostics.Debug.WriteLine($"   Subject: {currentSchedule.SubjectName}");
            System.Diagnostics.Debug.WriteLine($"   Mode: ClassBased");
        }
        else
        {
            // ? NO SCHEDULE - Admin Time (Still allowed!)
            response.Status = "ALLOWED";
            response.IsAllowed = true;
            response.Message = "PT Full Load: No class today. Attendance recorded as Admin Time.";
            response.Reason = "ADMIN_TIME";
            response.Mode = "AdminTime";
            response.ScheduleDetails = null;

            System.Diagnostics.Debug.WriteLine($"? ALLOWED: No class schedule, using Admin Time");
            System.Diagnostics.Debug.WriteLine($"   Mode: AdminTime");
        }

        return response;
    }

    // ???????????????????????????????????????????????????????????????
    // REGULAR EMPLOYEE VALIDATION
    // ???????????????????????????????????????????????????????????????
    private async Task<AttendanceValidationResponse> ValidateRegularEmployeeAsync(
        EmployeeDetails employee,
        DateTime tapTime,
        AttendanceValidationResponse response)
    {
        System.Diagnostics.Debug.WriteLine($"");
        System.Diagnostics.Debug.WriteLine($"?? REGULAR EMPLOYEE VALIDATION");
        System.Diagnostics.Debug.WriteLine($"   Rule: Teaching staff use schedules, Non-teaching use fixed hours");
        System.Diagnostics.Debug.WriteLine($"   Staff Type: {employee.StaffType ?? "Unknown"}");

        // ???????????????????????????????????????????????
        // CHECK STAFF TYPE FIRST - Non-Teaching NEVER check teaching schedules
        // ???????????????????????????????????????????????
        if (employee.IsNonTeachingStaff)
        {
            System.Diagnostics.Debug.WriteLine($"   ? NON-TEACHING STAFF - Using fixed work schedule ONLY");
            System.Diagnostics.Debug.WriteLine($"   ? Skipping teaching schedule check");
            
            // For Non-Teaching staff, ALWAYS use their fixed schedule from employees table
            // Do NOT check teaching_schedules at all
            return await ValidateFixedWorkScheduleAsync(employee, tapTime, response);
        }

        // ???????????????????????????????????????????????
        // TEACHING STAFF - Check if they have teaching schedules
        // ???????????????????????????????????????????????
        System.Diagnostics.Debug.WriteLine($"   ? TEACHING STAFF - Checking for class schedules");
        var teachingSchedules = await GetClassSchedulesForDateAsync(employee.EmployeeId, tapTime);

        if (teachingSchedules != null && teachingSchedules.Any())
        {
            // Regular TEACHING staff with schedules
            var currentSchedule = teachingSchedules.First();

            response.Status = "ALLOWED";
            response.IsAllowed = true;
            response.Message = $"Regular Teaching Staff: Class schedule found.";
            response.Reason = "CLASS_SCHEDULE";
            response.Mode = "ClassBased";
            response.NextTapType = await DetermineNextTapTypeAsync(employee.EmployeeId, tapTime.Date);
            response.ScheduleDetails = new ScheduleInfo
            {
                ScheduleId = currentSchedule.ScheduleId,
                SubjectName = currentSchedule.SubjectName,
                Section = currentSchedule.Section,
                TimeStart = currentSchedule.TimeStart,
                TimeEnd = currentSchedule.TimeEnd,
                Room = currentSchedule.Room,
                DayOfWeek = currentSchedule.DayOfWeek
            };

            System.Diagnostics.Debug.WriteLine($"? ALLOWED: Teaching schedule found");
            System.Diagnostics.Debug.WriteLine($"   Subject: {currentSchedule.SubjectName}");
            System.Diagnostics.Debug.WriteLine($"   Section: {currentSchedule.Section}");
            System.Diagnostics.Debug.WriteLine($"   Time: {currentSchedule.TimeStart:hh\\:mm} - {currentSchedule.TimeEnd:hh\\:mm}");
            System.Diagnostics.Debug.WriteLine($"   Mode: ClassBased");
            
            return response;
        }

        // ???????????????????????????????????????????????
        // TEACHING STAFF with NO classes today - Use fixed work schedule
        // ???????????????????????????????????????????????
        System.Diagnostics.Debug.WriteLine($"   No teaching schedule found, using fixed work hours");
        return await ValidateFixedWorkScheduleAsync(employee, tapTime, response);
    }

    // ???????????????????????????????????????????????
    // FIXED WORK SCHEDULE VALIDATION (for Non-Teaching and Teaching with no classes)
    // ???????????????????????????????????????????????
    private async Task<AttendanceValidationResponse> ValidateFixedWorkScheduleAsync(
        EmployeeDetails employee,
        DateTime tapTime,
        AttendanceValidationResponse response)
    {
        try
        {
            WorkSchedule workSchedule;
            
            // ???????????????????????????????????????????????
            // For NON-TEACHING staff: ALWAYS use employee's default schedule
            // Do NOT query employee_work_schedules table
            // ???????????????????????????????????????????????
            if (employee.IsNonTeachingStaff)
            {
                System.Diagnostics.Debug.WriteLine($"   ? NON-TEACHING STAFF - Using employee default schedule ONLY");
                System.Diagnostics.Debug.WriteLine($"   ? NOT querying employee_work_schedules table");
                
                workSchedule = new WorkSchedule
                {
                    TimeIn = employee.ScheduleTimeIn,
                    TimeOut = employee.ScheduleTimeOut,
                    IsActive = true
                };
                
                System.Diagnostics.Debug.WriteLine($"   Schedule: {employee.ScheduleTimeIn:hh\\:mm} - {employee.ScheduleTimeOut:hh\\:mm}");
            }
            else
            {
                // ???????????????????????????????????????????????
                // For TEACHING staff with no classes: Try employee_work_schedules first
                // ???????????????????????????????????????????????
                System.Diagnostics.Debug.WriteLine($"   Teaching staff with no classes - checking employee_work_schedules");
                
                var customSchedule = await GetWorkScheduleAsync(employee.EmployeeId);

                if (customSchedule == null || !customSchedule.IsActive)
                {
                    // No work schedule defined - use employee's default schedule
                    System.Diagnostics.Debug.WriteLine($"   No active work schedule, using employee default times");
                    
                    workSchedule = new WorkSchedule
                    {
                        TimeIn = employee.ScheduleTimeIn,
                        TimeOut = employee.ScheduleTimeOut,
                        IsActive = true
                    };

                    System.Diagnostics.Debug.WriteLine($"   Default Schedule: {employee.ScheduleTimeIn:hh\\:mm} - {employee.ScheduleTimeOut:hh\\:mm}");
                }
                else
                {
                    workSchedule = customSchedule;
                    System.Diagnostics.Debug.WriteLine($"   Using custom work schedule: {customSchedule.TimeIn:hh\\:mm} - {customSchedule.TimeOut:hh\\:mm}");
                }
            }

            if (!workSchedule.IsActive)
            {
                response.Status = "BLOCKED";
                response.Message = "Your work schedule is currently inactive. Please contact HR.";
                response.Reason = "SCHEDULE_INACTIVE";
                
                System.Diagnostics.Debug.WriteLine($"? BLOCKED: Work schedule is inactive");
                return response;
            }

            // ???????????????????????????????????????????????
            // ? NEW LOGIC: ALWAYS ALLOW attendance, just set schedule details
            // The actual late/on-time/undertime calculation happens in AttendanceValidationService
            // ???????????????????????????????????????????????
            var currentTime = tapTime.TimeOfDay;
            var scheduleTimeIn = workSchedule.TimeIn;
            var scheduleTimeOut = workSchedule.TimeOut;
            
            System.Diagnostics.Debug.WriteLine($"");
            System.Diagnostics.Debug.WriteLine($"   Schedule Details:");
            System.Diagnostics.Debug.WriteLine($"   Expected TIME IN: {scheduleTimeIn:hh\\:mm}");
            System.Diagnostics.Debug.WriteLine($"   Expected TIME OUT: {scheduleTimeOut:hh\\:mm}");
            System.Diagnostics.Debug.WriteLine($"   Current Time: {currentTime:hh\\:mm}");

            // Determine if tap is for IN or OUT
            var nextTapType = await DetermineNextTapTypeAsync(employee.EmployeeId, tapTime.Date);
            System.Diagnostics.Debug.WriteLine($"   Next Tap Type: {nextTapType}");

            // ???????????????????????????????????????????????
            // ? ALWAYS ALLOW - Status calculation happens during recording
            // ???????????????????????????????????????????????
            response.Status = "ALLOWED";
            response.IsAllowed = true;
            response.Message = employee.IsNonTeachingStaff 
                ? "Non-Teaching Staff: Attendance allowed." 
                : "Regular employee: Attendance allowed.";
            response.Reason = "WORK_SCHEDULE";
            response.Mode = "WorkSchedule";
            response.NextTapType = nextTapType;
            response.ScheduleDetails = new ScheduleInfo
            {
                TimeStart = scheduleTimeIn,
                TimeEnd = scheduleTimeOut,
                DayOfWeek = tapTime.DayOfWeek.ToString()
            };

            System.Diagnostics.Debug.WriteLine($"? ALLOWED: Attendance will be recorded");
            System.Diagnostics.Debug.WriteLine($"   Status calculation will happen during recording");
            System.Diagnostics.Debug.WriteLine($"   Expected: {(nextTapType == "IN" ? scheduleTimeIn : scheduleTimeOut):hh\\:mm}");
            System.Diagnostics.Debug.WriteLine($"   Actual: {currentTime:hh\\:mm}");
            
            return response;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"");
            System.Diagnostics.Debug.WriteLine($"??? EXCEPTION in ValidateFixedWorkScheduleAsync!");
            System.Diagnostics.Debug.WriteLine($"   Exception Type: {ex.GetType().Name}");
            System.Diagnostics.Debug.WriteLine($"   Message: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"   Stack Trace:");
            System.Diagnostics.Debug.WriteLine($"{ex.StackTrace}");
            
            // Re-throw the exception so it gets caught by the outer handler
            throw;
        }
    }

    // ???????????????????????????????????????????????????????????????
    // HELPER: Determine next tap type
    // ???????????????????????????????????????????????????????????????
    private async Task<string> DetermineNextTapTypeAsync(int employeeId, DateTime date)
    {
        try
        {
            var lastTapType = await GetLastTapTypeAsync(employeeId, date);
            var nextType = lastTapType == "IN" ? "OUT" : "IN";
            
            System.Diagnostics.Debug.WriteLine($"   Last Tap Type: {lastTapType}");
            System.Diagnostics.Debug.WriteLine($"   Next Tap Type: {nextType}");
            
            return nextType;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"? ERROR in DetermineNextTapTypeAsync:");
            System.Diagnostics.Debug.WriteLine($"   Message: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"   Stack: {ex.StackTrace}");
            
            // Default to IN if error occurs
            return "IN";
        }
    }

    // ???????????????????????????????????????????????????????????????
    // DATABASE QUERIES
    // ???????????????????????????????????????????????????????????????

    private async Task<EmployeeDetails?> GetEmployeeDetailsAsync(int employeeId)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var employee = await connection.QueryFirstOrDefaultAsync<EmployeeDetails>(@"
                SELECT 
                    employee_id as EmployeeId,
                    full_name as FullName,
                    employment_status as EmploymentStatus,
                    employee_type as EmployeeType,
                    staff_type as StaffType,
                    schedule_time_in as ScheduleTimeIn,
                    schedule_time_out as ScheduleTimeOut,
                    is_active as IsActive
                FROM employees
                WHERE employee_id = @EmployeeId
            ", new { EmployeeId = employeeId });

            if (employee != null)
            {
                // ???????????????????????????????????????????????
                // ? DATA NORMALIZATION: Ensure employee_type matches staff_type
                // ???????????????????????????????????????????????
                if (employee.IsNonTeachingStaff && 
                    !string.Equals(employee.EmployeeType, "non-teaching", StringComparison.OrdinalIgnoreCase))
                {
                    System.Diagnostics.Debug.WriteLine($"");
                    System.Diagnostics.Debug.WriteLine($"?? DATA INCONSISTENCY DETECTED!");
                    System.Diagnostics.Debug.WriteLine($"   Staff Type: {employee.StaffType} (Non-Teaching)");
                    System.Diagnostics.Debug.WriteLine($"   Employee Type: {employee.EmployeeType} (Should be 'non-teaching')");
                    System.Diagnostics.Debug.WriteLine($"   ? Auto-correcting for this session...");
                    
                    // Normalize for this session
                    employee.EmployeeType = "non-teaching";
                }
                
                System.Diagnostics.Debug.WriteLine($"?? Employee Details Retrieved:");
                System.Diagnostics.Debug.WriteLine($"   ID: {employee.EmployeeId}");
                System.Diagnostics.Debug.WriteLine($"   Name: {employee.FullName}");
                System.Diagnostics.Debug.WriteLine($"   Employment Status: {employee.EmploymentStatus ?? "N/A"}");
                System.Diagnostics.Debug.WriteLine($"   Employee Type: {employee.EmployeeType ?? "N/A"}");
                System.Diagnostics.Debug.WriteLine($"   Staff Type: {employee.StaffType ?? "N/A"}");
                System.Diagnostics.Debug.WriteLine($"   Schedule: {employee.ScheduleTimeIn:hh\\:mm} - {employee.ScheduleTimeOut:hh\\:mm}");
                System.Diagnostics.Debug.WriteLine($"   Active: {employee.IsActive}");
            }

            return employee;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"? Error getting employee details: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"   Stack: {ex.StackTrace}");
            return null;
        }
    }

    private async Task<List<ClassSchedule>> GetClassSchedulesForDateAsync(int employeeId, DateTime date)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var dayOfWeek = date.DayOfWeek.ToString();
            var dayOfWeekNumber = (int)date.DayOfWeek; // 0=Sunday, 1=Monday, etc.

            System.Diagnostics.Debug.WriteLine($"");
            System.Diagnostics.Debug.WriteLine($"?? SEARCHING FOR SCHEDULES (Exam Priority):");
            System.Diagnostics.Debug.WriteLine($"   Employee ID: {employeeId}");
            System.Diagnostics.Debug.WriteLine($"   Date: {date:yyyy-MM-dd} ({dayOfWeek})");
            System.Diagnostics.Debug.WriteLine($"   Day Number: {dayOfWeekNumber}");

            // ???????????????????????????????????????????????
            // PRIORITY 1: Check exam_schedules FIRST
            // ???????????????????????????????????????????????
            System.Diagnostics.Debug.WriteLine($"");
            System.Diagnostics.Debug.WriteLine($"? PRIORITY 1: Checking exam_schedules...");

            var examSchedules = await connection.QueryAsync<ClassSchedule>(@"
                SELECT 
                    exam_schedule_id as ScheduleId,
                    subject_name as SubjectName,
                    section as Section,
                    time_start as TimeStart,
                    time_end as TimeEnd,
                    COALESCE(room_code, '') as Room,
                    exam_date as SpecificDate,
                    false as IsRecurring
                FROM exam_schedules
                WHERE employee_id = @EmployeeId
                    AND (
                        exam_date::date = @Date::date
                        OR (
                            exam_date IS NULL
                            AND (
                                day_of_week::text = @DayOfWeekNumberText
                                OR LOWER(TRIM(day_of_week::text)) = LOWER(@DayOfWeekName)
                            )
                        )
                    )
                    AND (status IS NULL OR LOWER(status) IN ('active', 'scheduled', 'available', 'approved', 'published'))
                ORDER BY time_start ASC
            ", new { 
                EmployeeId = employeeId, 
                Date = date.Date,
                DayOfWeekNumberText = dayOfWeekNumber.ToString(),
                DayOfWeekName = dayOfWeek
            });

            var examList = examSchedules.ToList();

            var allSchedules = new List<ClassSchedule>();

            if (examList.Any())
            {
                System.Diagnostics.Debug.WriteLine($"   ?? EXAM SCHEDULES FOUND: {examList.Count}");
                foreach (var exam in examList)
                {
                    System.Diagnostics.Debug.WriteLine($"   - EXAM: {exam.SubjectName} ({exam.Section})");
                    System.Diagnostics.Debug.WriteLine($"     Time: {exam.TimeStart:hh\\:mm} - {exam.TimeEnd:hh\\:mm}");
                }
                allSchedules.AddRange(examList);
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"   ? No exam schedules found for {date:yyyy-MM-dd}");
            }

            // ???????????????????????????????????????????????
            // PRIORITY 2: Fall back to teaching_schedules
            // ???????????????????????????????????????????????
            System.Diagnostics.Debug.WriteLine($"");
            System.Diagnostics.Debug.WriteLine($"? PRIORITY 2: Checking teaching_schedules...");

            var schedules = await connection.QueryAsync<ClassSchedule>(@"
                SELECT 
                    schedule_id as ScheduleId,
                    subject_name as SubjectName,
                    section as Section,
                    time_start as TimeStart,
                    time_end as TimeEnd,
                    COALESCE(room_id::text, '') as Room,
                    day_of_week::text as DayOfWeek,
                    specific_date as SpecificDate,
                    is_recurring as IsRecurring
                FROM teaching_schedules
                WHERE employee_id = @EmployeeId
                    AND (
                        (specific_date = @Date) OR
                        (
                            -- Handle numeric day_of_week (SMALLINT type)
                            day_of_week = @DayOfWeekNumber
                            AND (is_recurring = true OR is_recurring IS NULL)
                        )
                    )
                    AND (status IS NULL OR status = 'active' OR status = 'available')
                ORDER BY 
                    specific_date DESC NULLS LAST,
                    time_start ASC
            ", new { 
                EmployeeId = employeeId, 
                Date = date.Date, 
                DayOfWeekNumber = dayOfWeekNumber
            });

            var scheduleList = schedules.ToList();

            System.Diagnostics.Debug.WriteLine($"   Found {scheduleList.Count} teaching schedule(s)");

            if (scheduleList.Any())
            {
                foreach (var schedule in scheduleList)
                {
                    System.Diagnostics.Debug.WriteLine($"   ? Teaching Schedule Found:");
                    System.Diagnostics.Debug.WriteLine($"      Subject: {schedule.SubjectName}");
                    System.Diagnostics.Debug.WriteLine($"      Section: {schedule.Section}");
                    System.Diagnostics.Debug.WriteLine($"      Time: {schedule.TimeStart:hh\\:mm} - {schedule.TimeEnd:hh\\:mm}");
                    System.Diagnostics.Debug.WriteLine($"      Day: {schedule.DayOfWeek}");
                    System.Diagnostics.Debug.WriteLine($"      Specific Date: {schedule.SpecificDate?.ToString("yyyy-MM-dd") ?? "Recurring"}");
                }
                allSchedules.AddRange(scheduleList);
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"   ? No teaching schedules found either.");
            }

            // ???????????????????????????????????????????????
            // PRIORITY 3: Check Substitute Duties
            // ???????????????????????????????????????????????
            System.Diagnostics.Debug.WriteLine($"");
            System.Diagnostics.Debug.WriteLine($"? PRIORITY 3: Checking substitute duties...");

            var substituteDuties = await connection.QueryAsync<ClassSchedule>(@"
                SELECT 
                    cs.id as ScheduleId,
                    ts.subject_name as SubjectName,
                    ts.section as Section,
                    cs.start_time as TimeStart,
                    cs.end_time as TimeEnd,
                    '' as Room,
                    cs.substitution_date as SpecificDate,
                    false as IsRecurring
                FROM class_substitutions_v2 cs
                LEFT JOIN teaching_schedules ts ON 
                    ts.employee_id = cs.original_employee_id
                    AND ts.time_start = cs.start_time
                    AND ts.time_end = cs.end_time
                WHERE cs.substitute_employee_id = @EmployeeId
                    AND cs.substitution_date::date = @Date::date
                    AND cs.status = 'approved'
                ORDER BY cs.start_time ASC
            ", new { 
                EmployeeId = employeeId, 
                Date = date.ToString("yyyy-MM-dd")
            });

            var substituteList = substituteDuties.ToList();

            if (substituteList.Any())
            {
                System.Diagnostics.Debug.WriteLine($"   ?? SUBSTITUTE DUTIES FOUND (Fallback): {substituteList.Count}");
                allSchedules.AddRange(substituteList);
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"   ? No substitute duties found.");
            }

            return allSchedules.OrderBy(s => s.TimeStart).ToList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"? Error getting class schedules: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"   Stack: {ex.StackTrace}");
            return new List<ClassSchedule>();
        }
    }

    private async Task<WorkSchedule?> GetWorkScheduleAsync(int employeeId)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var schedule = await connection.QueryFirstOrDefaultAsync<WorkSchedule>(@"
                SELECT 
                    time_in as TimeIn,
                    time_out as TimeOut,
                    is_active as IsActive
                FROM employee_work_schedules
                WHERE employee_id = @EmployeeId
                    AND is_active = true
                ORDER BY created_at DESC
                LIMIT 1
            ", new { EmployeeId = employeeId });

            return schedule;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"?? Error getting work schedule: {ex.Message}");
            return null;
        }
    }

    private async Task<string> GetLastTapTypeAsync(int employeeId, DateTime date)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            System.Diagnostics.Debug.WriteLine($"");
            System.Diagnostics.Debug.WriteLine($"   Querying last tap type for employee {employeeId} on {date:yyyy-MM-dd}");

            var lastType = await connection.QueryFirstOrDefaultAsync<string>(@"
                SELECT log_type
                FROM attendance_logs
                WHERE employee_id = @EmployeeId
                    AND date = @Date
                ORDER BY log_time DESC
                LIMIT 1
            ", new { EmployeeId = employeeId, Date = date });

            var result = lastType?.ToUpper() ?? "OUT";
            System.Diagnostics.Debug.WriteLine($"   Last tap type result: {result} (raw value: {lastType ?? "NULL"})");
            
            return result;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"? ERROR in GetLastTapTypeAsync:");
            System.Diagnostics.Debug.WriteLine($"   Employee ID: {employeeId}");
            System.Diagnostics.Debug.WriteLine($"   Date: {date:yyyy-MM-dd}");
            System.Diagnostics.Debug.WriteLine($"   Message: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"   Stack: {ex.StackTrace}");
            
            // Default to OUT if error occurs
            return "OUT";
        }
    }
}

// ???????????????????????????????????????????????????????????????
// MODELS
// ???????????????????????????????????????????????????????????????

public class AttendanceValidationResponse
{
    public int EmployeeId { get; set; }
    public string? EmployeeName { get; set; }
    public string? EmploymentStatus { get; set; }
    public string? EmployeeType { get; set; }
    public DateTime TapTime { get; set; }
    
    public string Status { get; set; } = "PENDING"; // ALLOWED, BLOCKED, ERROR
    public bool IsAllowed { get; set; } = false;
    public string Message { get; set; } = string.Empty;
    public string? Reason { get; set; } // SUNDAY_BLOCK, NO_SCHEDULE, CLASS_SCHEDULE, ADMIN_TIME, WORK_SCHEDULE, OUTSIDE_SCHEDULE
    public string? Mode { get; set; } // ClassBased, AdminTime, WorkSchedule
    public string? NextTapType { get; set; } // IN, OUT
    
    public ScheduleInfo? ScheduleDetails { get; set; }
}

public class ScheduleInfo
{
    public long? ScheduleId { get; set; }
    public string? SubjectName { get; set; }
    public string? Section { get; set; }
    public TimeSpan TimeStart { get; set; }
    public TimeSpan TimeEnd { get; set; }
    public string? Room { get; set; }
    public string? DayOfWeek { get; set; }
}

public class EmployeeDetails
{
    public int EmployeeId { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string? EmploymentStatus { get; set; }
    public string? EmployeeType { get; set; }
    public string? StaffType { get; set; }
    public TimeSpan ScheduleTimeIn { get; set; }
    public TimeSpan ScheduleTimeOut { get; set; }
    public bool IsActive { get; set; }
    
    public bool IsTeachingStaff => StaffType?.Equals("Teaching", StringComparison.OrdinalIgnoreCase) == true;
    public bool IsNonTeachingStaff => StaffType?.Equals("Non-Teaching", StringComparison.OrdinalIgnoreCase) == true;
}

public class ClassSchedule
{
    public long ScheduleId { get; set; }
    public string? SubjectName { get; set; }
    public string? Section { get; set; }
    public TimeSpan TimeStart { get; set; }
    public TimeSpan TimeEnd { get; set; }
    public string? Room { get; set; }
    public string? DayOfWeek { get; set; }
    public DateTime? SpecificDate { get; set; }
    public bool? IsRecurring { get; set; }
}

public class WorkSchedule
{
    public TimeSpan TimeIn { get; set; }
    public TimeSpan TimeOut { get; set; }
    public bool IsActive { get; set; }
}

