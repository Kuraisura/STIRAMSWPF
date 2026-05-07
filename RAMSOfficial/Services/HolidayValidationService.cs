using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Npgsql;
using Dapper;
using RAMSOfficial.Models;

namespace RAMSOfficial.Services;

/// <summary>
/// Validates attendance based on holiday calendar rules
/// PRIORITY: HOLIDAY ? EXAM ? CLASS
/// Supports date ranges (start_date to end_date)
/// </summary>
public class HolidayValidationService
{
    private readonly string _connectionString;

    public HolidayValidationService(string connectionString)
    {
        _connectionString = connectionString;
    }

    /// <summary>
    /// Validate if attendance is allowed based on holiday calendar
    /// This is the HIGHEST PRIORITY check - must be done before exam/class checks
    /// </summary>
    public async Task<HolidayValidationResult> ValidateHolidayAttendanceAsync(
        int employeeId,
        string? employmentStatus,
        string? employmentType,
        string? staffType,
        DateTime checkTime)
    {
        var result = new HolidayValidationResult
        {
            IsAllowed = true,
            CheckDate = checkTime.Date
        };

        // ???????????????????????????????????????????????
        // PRIORITY 1: Check holiday_calendar (HIGHEST PRIORITY)
        // ???????????????????????????????????????????????
        var holidays = await GetHolidaysInRangeAsync(checkTime.Date);
        
        if (!holidays.Any())
        {
            // No holiday - attendance allowed normally
            return result;
        }

        // ? Holiday/Special Day found - apply rules
        var holiday = holidays.First(); // Take first if multiple (shouldn't happen)
        
        result.IsHolidayWithAttendance = true;
        result.HolidayName = holiday.Name;
        result.HolidayType = holiday.Type;
        result.HolidayDescription = holiday.Description;
        result.StartDate = holiday.StartDate;
        result.EndDate = holiday.EndDate;

        System.Diagnostics.Debug.WriteLine($"");
        System.Diagnostics.Debug.WriteLine($"???????????????????????????????????????????????");
        System.Diagnostics.Debug.WriteLine($"  ??? HOLIDAY CALENDAR CHECK (HIGHEST PRIORITY)");
        System.Diagnostics.Debug.WriteLine($"  Date: {checkTime:yyyy-MM-dd}");
        System.Diagnostics.Debug.WriteLine($"  Holiday: {holiday.Name}");
        System.Diagnostics.Debug.WriteLine($"  Type: {holiday.Type}");
        System.Diagnostics.Debug.WriteLine($"  Range: {holiday.StartDate:yyyy-MM-dd} to {holiday.EndDate:yyyy-MM-dd}");
        System.Diagnostics.Debug.WriteLine($"  Employee: {employmentStatus} / {staffType}");
        System.Diagnostics.Debug.WriteLine($"???????????????????????????????????????????????");

        var holidayType = holiday.Type?.ToLowerInvariant();

        // ???????????????????????????????????????????????
        // TYPE 1: HOLIDAY - NO CLASSES, NO ATTENDANCE
        // ? BLOCKS EVERYONE (Teaching + Non-Teaching)
        // ???????????????????????????????????????????????
        if (holidayType == "holiday")
        {
            result.IsAllowed = false;
            result.Reason = "HOLIDAY";
            result.Message = $"?? {holiday.Name}";
            result.AttendanceMode = "BLOCKED";
            result.Notes = "No classes scheduled. No attendance required.";
            
            System.Diagnostics.Debug.WriteLine($"  ? BLOCKED: Holiday - No attendance needed");
            System.Diagnostics.Debug.WriteLine($"     Affects: ALL STAFF (Teaching + Non-Teaching)");
            System.Diagnostics.Debug.WriteLine($"     Message: {result.Notes}");
            return result;
        }

        // ???????????????????????????????????????????????????????
        // ? CRITICAL FIX: Check if Non-Teaching staff BEFORE other types
        // Non-Teaching staff should work normally during:
        // - online_class (teaching staff work online, non-teaching work normally)
        // - suspended_asynchronous (classes suspended, but office staff work)
        // ???????????????????????????????????????????????????????
        var isNonTeachingStaff = staffType?.Equals("Non-Teaching", StringComparison.OrdinalIgnoreCase) == true;
        var isTeachingStaff = staffType?.Equals("Teaching", StringComparison.OrdinalIgnoreCase) == true;
        var isPartTimeFull = employmentStatus?.Contains("Part", StringComparison.OrdinalIgnoreCase) == true &&
                             employmentStatus?.Contains("Full Load", StringComparison.OrdinalIgnoreCase) == true;
        var isRegularTeaching = isTeachingStaff && (employmentStatus?.Equals("Regular", StringComparison.OrdinalIgnoreCase) == true ||
                                employmentStatus?.Equals("Full Time", StringComparison.OrdinalIgnoreCase) == true ||
                                employmentStatus?.Equals("Full-Time", StringComparison.OrdinalIgnoreCase) == true);

        // Non-Teaching staff: always follow normal schedule, never admin-time
        if (isNonTeachingStaff && (holidayType == "online_class" || holidayType == "online class" || 
                                   holidayType == "suspended_asynchronous" || holidayType == "suspended asynchronous"))
        {
            result.IsAllowed = true;
            result.AttendanceMode = "NORMAL_SCHEDULE";
            result.Reason = "NON_TEACHING_EXEMPT";
            result.Message = $"?? {holiday.Name} (Teaching staff only)";
            result.AttendanceStatus = null; // Always null for non-teaching staff
            result.Notes = $"Non-Teaching staff: Work as normal. Holiday type '{holiday.Type}' affects teaching staff only. Status will be calculated as On Time, Late, or Undertime.";
            result.IsHolidayWithAttendance = false;
            if (holidayType == "suspended_asynchronous" || holidayType == "suspended asynchronous")
            {
                result.WarningDialog = "Class Suspension - Reporting";
            }
            return result;
        }

        // Teaching staff: online_class
        if (holidayType == "online_class" || holidayType == "online class")
        {
            if (!isTeachingStaff)
            {
                // Safety: non-teaching handled above
                result.IsAllowed = true;
                result.Reason = "NON_TEACHING_NORMAL";
                result.Message = $"?? Online Classes - {holiday.Name}";
                result.AttendanceMode = "NORMAL_SCHEDULE";
                result.Notes = "Non-teaching staff: Normal work schedule.";
                result.AttendanceStatus = null;
                return result;
            }
            var hasTeachingSchedule = await HasTeachingScheduleAsync(employeeId, checkTime);
            if (hasTeachingSchedule)
            {
                result.IsAllowed = true;
                result.AttendanceMode = "ONLINE_CLASS_WITH_SCHEDULE";
                result.Reason = "ONLINE_CLASS_SCHEDULED";
                result.Message = $"?? Online Class Day - {holiday.Name}";
                result.Notes = "Teaching staff: Time in/out during class schedule (online). Status will be calculated based on class times.";
                result.AttendanceStatus = null;
            }
            else if (isPartTimeFull)
            {
                result.IsAllowed = true;
                result.AttendanceMode = "ONLINE_CLASS_ADMIN_TIME";
                result.Reason = "ONLINE_CLASS_NO_SCHEDULE_PTFL";
                result.Message = $"?? Online Class Day - {holiday.Name}";
                result.AttendanceStatus = "admin-time";
                result.Notes = "Part-Time Full Load: No class schedule - recorded as Admin Time.";
            }
            else
            {
                result.IsAllowed = false;
                result.AttendanceMode = "BLOCKED";
                result.Reason = "ONLINE_CLASS_NO_SCHEDULE_PT";
                result.Message = $"?? Online Class Day - {holiday.Name}";
                result.Notes = "Part-Time staff: No class schedule - no attendance required.";
            }
            return result;
        }

        // Teaching staff: suspended_asynchronous
        if (holidayType == "suspended_asynchronous" || holidayType == "suspended asynchronous")
        {
            // Non-Teaching staff: always normal schedule, never admin-time
            if (isNonTeachingStaff)
            {
                result.IsAllowed = true;
                result.AttendanceMode = "NORMAL_SCHEDULE";
                result.Reason = "NON_TEACHING_EXEMPT_SUSPENDED";
                result.Message = $"?? Suspended/Asynchronous - {holiday.Name} (Teaching staff only)";
                result.AttendanceStatus = null; // Status will be On Time, Late, or Undertime
                result.Notes = "Non-Teaching staff: Work as normal. Status will be calculated as On Time, Late, or Undertime.";
                result.IsHolidayWithAttendance = false;
                result.WarningDialog = "Class Suspension - Reporting";
                return result;
            }

            // Teaching staff: Only Part Time Full Load gets admin-time
            if (isPartTimeFull)
            {
                result.IsAllowed = true;
                result.AttendanceMode = "SUSPENDED_REPORTING";
                result.Reason = "SUSPENDED_ASYNCHRONOUS_REPORTING_PTFL";
                result.Message = $"?? Suspended/Asynchronous - {holiday.Name}";
                result.AttendanceStatus = "admin-time";
                result.Notes = $"Part-Time Full Load Teaching Staff: Physical reporting required - recorded as Admin Time.";
                result.WarningDialog = "Class Suspension - Reporting";
                return result;
            }
            // Other teaching staff: no reporting
            else
            {
                result.IsAllowed = false;
                result.AttendanceMode = "BLOCKED";
                result.Reason = "SUSPENDED_ASYNCHRONOUS_NO_REPORTING";
                result.Message = $"?? Suspended/Asynchronous - {holiday.Name}";
                result.Notes = "No physical reporting required.";
                result.AttendanceStatus = null;
                result.WarningDialog = "Class Suspension - Reporting";
                return result;
            }
        }

        // ???????????????????????????????????????????????
        // TYPE 4: UNKNOWN/OTHER - Default handling
        // ???????????????????????????????????????????????
        System.Diagnostics.Debug.WriteLine($"  ?? UNKNOWN HOLIDAY TYPE: {holiday.Type}");
        System.Diagnostics.Debug.WriteLine($"     Defaulting to BLOCKED for safety");
        
        result.IsAllowed = false;
        result.AttendanceMode = "BLOCKED";
        result.Reason = "UNKNOWN_HOLIDAY_TYPE";
        result.Message = $"Special Day - {holiday.Name}";
        result.Notes = $"Unknown holiday type: {holiday.Type}. Please contact administrator.";
        
        return result;
    }

    /// <summary>
    /// Get holidays that fall within a date range (supports start_date and end_date)
    /// </summary>
    private async Task<List<HolidayCalendar>> GetHolidaysInRangeAsync(DateTime checkDate)
    {
        return await DatabaseConnectionGuard.QuerySafeAsync(
            _connectionString,
            async conn =>
            {
                var holidays = await conn.QueryAsync<HolidayCalendar>(@"
                    SELECT 
                        id as HolidayId,
                        name as Name,
                        type as Type,
                        description as Description,
                        start_date as StartDate,
                        end_date as EndDate,
                        affects_attendance as AffectsAttendance,
                        holiday_type as HolidayType,
                        reporting_only as ReportingOnly,
                        reporting_staff_only as ReportingStaffOnly,
                        created_by as CreatedBy,
                        created_at as CreatedAt,
                        updated_at as UpdatedAt
                    FROM holiday_calendar
                    WHERE @CheckDate BETWEEN start_date AND end_date
                    ORDER BY created_at DESC
                ", new { CheckDate = checkDate });

                var list = holidays.ToList();

                if (list.Any())
                {
                    System.Diagnostics.Debug.WriteLine($"");
                    System.Diagnostics.Debug.WriteLine($"  ?? Found {list.Count} holiday(s) for {checkDate:yyyy-MM-dd}:");
                    foreach (var h in list)
                    {
                        var duration = h.StartDate == h.EndDate ? "Single day" : $"{h.StartDate:MM/dd} - {h.EndDate:MM/dd}";
                        System.Diagnostics.Debug.WriteLine($"     - {h.Name} ({h.Type}) [{duration}]");
                        System.Diagnostics.Debug.WriteLine($"       affects_attendance: {h.AffectsAttendance}, reporting_only: {h.ReportingOnly}");
                    }
                }

                return list;
            },
            fallback: new List<HolidayCalendar>()
        );
    }

    /// <summary>
    /// Check if employee has teaching schedule on this date
    /// </summary>
    private async Task<bool> HasTeachingScheduleAsync(int employeeId, DateTime checkTime)
    {
        return await DatabaseConnectionGuard.QuerySafeAsync(
            _connectionString,
            async conn =>
            {
                var dayOfWeekNumber = (int)checkTime.DayOfWeek;

                var hasExam = await conn.ExecuteScalarAsync<bool>(@"
                    SELECT EXISTS (
                        SELECT 1 FROM exam_schedules
                        WHERE employee_id = @EmployeeId
                            AND exam_date = @Date
                            AND (status IS NULL OR status = 'active' OR status = 'scheduled')
                    )
                ", new { EmployeeId = employeeId, Date = checkTime.Date });

                if (hasExam)
                {
                    System.Diagnostics.Debug.WriteLine($"     ? Has EXAM schedule on {checkTime:yyyy-MM-dd}");
                    return true;
                }

                var hasClass = await conn.ExecuteScalarAsync<bool>(@"
                    SELECT EXISTS (
                        SELECT 1 FROM teaching_schedules
                        WHERE employee_id = @EmployeeId
                            AND (
                                (specific_date = @Date) OR
                                (
                                    day_of_week = @DayOfWeekNumber
                                    AND (is_recurring = true OR is_recurring IS NULL)
                                )
                            )
                            AND (status IS NULL OR status = 'active' OR status = 'available')
                    )
                ", new { 
                    EmployeeId = employeeId, 
                    Date = checkTime.Date,
                    DayOfWeekNumber = dayOfWeekNumber
                });

                if (hasClass)
                    System.Diagnostics.Debug.WriteLine($"     ? Has CLASS schedule on {checkTime:yyyy-MM-dd}");
                else
                    System.Diagnostics.Debug.WriteLine($"     ? No teaching schedule on {checkTime:yyyy-MM-dd}");

                return hasClass;
            },
            fallback: false
        );
    }
}

/// <summary>
/// Result of holiday validation
/// </summary>
public class HolidayValidationResult
{
    public bool IsAllowed { get; set; }
    public DateTime CheckDate { get; set; }
    public bool IsHolidayWithAttendance { get; set; }
    // Holiday Info
    public string? HolidayName { get; set; }
    public string? HolidayType { get; set; }
    public string? HolidayDescription { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    // Validation Results
    public string? AttendanceMode { get; set; } // BLOCKED, ONLINE_CLASS_WITH_SCHEDULE, ONLINE_CLASS_ADMIN_TIME, SUSPENDED_REPORTING
    public string? AttendanceStatus { get; set; } // on-time, admin-time (or null for schedule-based)
    public string? Reason { get; set; }
    public string? Message { get; set; }
    public string? Notes { get; set; }
    public string? WarningDialog { get; set; } // For UI warning dialogs
}
