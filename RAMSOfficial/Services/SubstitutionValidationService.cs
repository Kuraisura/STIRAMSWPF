using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Npgsql;
using Dapper;
using RAMSOfficial.Models;

namespace RAMSOfficial.Services;

/// <summary>
/// Validates attendance based on approved class substitutions
/// PRIORITY: HOLIDAY ? LEAVE ? SUBSTITUTION ? EXAM ? CLASS
/// Handles both original employee (being substituted) and substitute employee
/// </summary>
public class SubstitutionValidationService
{
    private readonly string _connectionString;

    public SubstitutionValidationService(string connectionString)
    {
        _connectionString = connectionString;
    }

    /// <summary>
    /// Check if employee is involved in an approved substitution for the given date
    /// This is PRIORITY 3 (after Holiday and Leave, before Exam/Class)
    /// </summary>
    public async Task<SubstitutionValidationResult> ValidateSubstitutionStatusAsync(
        int employeeId,
        DateTime checkTime)
    {
        var result = new SubstitutionValidationResult
        {
            EmployeeId = employeeId,
            CheckDate = checkTime.Date,
            IsAllowed = true
        };

        try
        {
            // Pre-flight: skip database if offline
            if (!DatabaseConnectionGuard.IsOnline())
            {
                System.Diagnostics.Debug.WriteLine($"  ⚠ OFFLINE — skipping substitution validation");
                return result;
            }

            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            System.Diagnostics.Debug.WriteLine($"");
            System.Diagnostics.Debug.WriteLine($"???????????????????????????????????????????????????????");
            System.Diagnostics.Debug.WriteLine($"  ?? SUBSTITUTION VALIDATION CHECK (PRIORITY 3)");
            System.Diagnostics.Debug.WriteLine($"  Employee ID: {employeeId}");
            System.Diagnostics.Debug.WriteLine($"  Date: {checkTime:yyyy-MM-dd}");
            System.Diagnostics.Debug.WriteLine($"  Time: {checkTime:HH:mm:ss}");
            System.Diagnostics.Debug.WriteLine($"???????????????????????????????????????????????????????");

            // ???????????????????????????????????????????????????????
            // CHECK 1: Is this employee BEING SUBSTITUTED? (Original Employee)
            // ???????????????????????????????????????????????????????
            System.Diagnostics.Debug.WriteLine($"");
            System.Diagnostics.Debug.WriteLine($"[CHECK 1] Checking if employee {employeeId} is BEING SUBSTITUTED...");
            
            var asOriginal = await connection.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT 
                    cs.id as SubstitutionId,
                    cs.original_employee_id as OriginalEmployeeId,
                    cs.substitute_employee_id as SubstituteEmployeeId,
                    cs.substitution_date as SubstitutionDate,
                    cs.start_time as StartTime,
                    cs.end_time as EndTime,
                    cs.reason as Reason,
                    cs.status as Status,
                    cs.approved_at as ApprovedAt,
                    se.full_name as SubstituteName
                FROM class_substitutions_v2 cs
                LEFT JOIN employees se ON cs.substitute_employee_id = se.employee_id
                WHERE cs.original_employee_id = @EmployeeId
                    AND cs.substitution_date::date = @CheckDate
                    AND cs.status = 'approved'
                ORDER BY cs.created_at DESC
                LIMIT 1
            ", new { 
                EmployeeId = employeeId, 
                CheckDate = checkTime.Date 
            });

            if (asOriginal != null)
            {
                System.Diagnostics.Debug.WriteLine($"[CHECK 1] ? Found substitution record (ID: {asOriginal.SubstitutionId})");
                System.Diagnostics.Debug.WriteLine($"          Substitute: {asOriginal.SubstituteName} (ID: {asOriginal.SubstituteEmployeeId})");
                System.Diagnostics.Debug.WriteLine($"          Date: {asOriginal.SubstitutionDate:yyyy-MM-dd}");
                System.Diagnostics.Debug.WriteLine($"          Status: {asOriginal.Status}");
                
                // ? This employee is BEING SUBSTITUTED
                result.IsOriginalEmployee = true;
                result.HasSubstitution = true;
                result.SubstitutionId = asOriginal.SubstitutionId;
                result.SubstituteEmployeeId = asOriginal.SubstituteEmployeeId;
                result.SubstituteName = asOriginal.SubstituteName ?? "Unknown";
                result.SubstitutionReason = asOriginal.Reason;
                
                // ?? FIXED: Safely parse TimeSpan values
                if (asOriginal.StartTime != null)
                {
                    if (asOriginal.StartTime is TimeSpan startTimeSpan)
                    {
                        result.StartTime = startTimeSpan;
                    }
                    else if (TimeSpan.TryParse(asOriginal.StartTime.ToString(), out TimeSpan parsedStart))
                    {
                        result.StartTime = parsedStart;
                    }
                }
                
                if (asOriginal.EndTime != null)
                {
                    if (asOriginal.EndTime is TimeSpan endTimeSpan)
                    {
                        result.EndTime = endTimeSpan;
                    }
                    else if (TimeSpan.TryParse(asOriginal.EndTime.ToString(), out TimeSpan parsedEnd))
                    {
                        result.EndTime = parsedEnd;
                    }
                }
                
                result.ApprovedAt = asOriginal.ApprovedAt;

                System.Diagnostics.Debug.WriteLine($"          Times: {(result.StartTime?.ToString(@"hh\:mm") ?? "NULL")} - {(result.EndTime?.ToString(@"hh\:mm") ?? "NULL")}");

                // Get the original teaching schedule being substituted
                if (result.StartTime.HasValue && result.EndTime.HasValue)
                {
                    System.Diagnostics.Debug.WriteLine($"          Fetching original teaching schedule...");
                    
                    var originalSchedule = await GetTeachingScheduleAsync(
                        connection, 
                        employeeId, 
                        checkTime,
                        result.StartTime.Value,
                        result.EndTime.Value
                    );

                    if (originalSchedule != null)
                    {
                        result.OriginalScheduleId = originalSchedule.ScheduleId;
                        result.OriginalSubjectName = originalSchedule.SubjectName;
                        result.OriginalSection = originalSchedule.Section;
                        result.OriginalRoom = originalSchedule.Room;
                        
                        System.Diagnostics.Debug.WriteLine($"          ? Found schedule: {result.OriginalSubjectName} ({result.OriginalSection})");
                        System.Diagnostics.Debug.WriteLine($"            Room: {result.OriginalRoom}");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"          ?? WARNING: No teaching schedule found for these times!");
                        System.Diagnostics.Debug.WriteLine($"            This might indicate a data inconsistency.");
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"          ?? WARNING: Substitution has NULL start/end times!");
                    System.Diagnostics.Debug.WriteLine($"            Cannot look up teaching schedule.");
                }

                // ? ALLOWED - Original employee should NOT attend this class
                result.IsAllowed = true;
                result.Mode = "ORIGINAL_EMPLOYEE_SUBSTITUTED";
                result.Message = "Class Substituted";
                result.Notes = $"Your class has been substituted by {result.SubstituteName}.";

                System.Diagnostics.Debug.WriteLine($"");
                System.Diagnostics.Debug.WriteLine($"  ? ORIGINAL EMPLOYEE - BEING SUBSTITUTED");
                System.Diagnostics.Debug.WriteLine($"     Substitution ID: {result.SubstitutionId}");
                System.Diagnostics.Debug.WriteLine($"     Substitute: {result.SubstituteName} (ID: {result.SubstituteEmployeeId})");
                System.Diagnostics.Debug.WriteLine($"     Time: {(result.StartTime?.ToString(@"hh\:mm") ?? "N/A")} - {(result.EndTime?.ToString(@"hh\:mm") ?? "N/A")}")
;
                System.Diagnostics.Debug.WriteLine($"     Reason: {result.SubstitutionReason ?? "N/A"}");
                
                if (!string.IsNullOrEmpty(result.OriginalSubjectName))
                {
                    System.Diagnostics.Debug.WriteLine($"     Subject: {result.OriginalSubjectName} ({result.OriginalSection})");
                }
                
                System.Diagnostics.Debug.WriteLine($"     ? Original employee should follow OTHER schedules");
                
                return result;
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[CHECK 1] ? No substitution found where employee is ORIGINAL");
            }

            // ???????????????????????????????????????????????????????
            // CHECK 2: Is this employee SUBSTITUTING someone? (Substitute Employee)
            // ???????????????????????????????????????????????????????
            System.Diagnostics.Debug.WriteLine($"");
            System.Diagnostics.Debug.WriteLine($"[CHECK 2] Checking if employee {employeeId} is SUBSTITUTING someone...");
            
            var asSubstitute = await connection.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT 
                    cs.id as SubstitutionId,
                    cs.original_employee_id as OriginalEmployeeId,
                    cs.substitute_employee_id as SubstituteEmployeeId,
                    cs.substitution_date as SubstitutionDate,
                    cs.start_time as StartTime,
                    cs.end_time as EndTime,
                    cs.reason as Reason,
                    cs.status as Status,
                    cs.approved_at as ApprovedAt,
                    oe.full_name as OriginalEmployeeName
                FROM class_substitutions_v2 cs
                LEFT JOIN employees oe ON cs.original_employee_id = oe.employee_id
                WHERE cs.substitute_employee_id = @EmployeeId
                    AND cs.substitution_date::date = @CheckDate
                    AND cs.status = 'approved'
                ORDER BY cs.created_at DESC
                LIMIT 1
            ", new { 
                EmployeeId = employeeId, 
                CheckDate = checkTime.Date 
            });

            if (asSubstitute != null)
            {
                System.Diagnostics.Debug.WriteLine($"[CHECK 2] ? Found substitution record (ID: {asSubstitute.SubstitutionId})");
                System.Diagnostics.Debug.WriteLine($"          Original Employee: {asSubstitute.OriginalEmployeeName} (ID: {asSubstitute.OriginalEmployeeId})");
                System.Diagnostics.Debug.WriteLine($"          Date: {asSubstitute.SubstitutionDate:yyyy-MM-dd}");
                System.Diagnostics.Debug.WriteLine($"          Status: {asSubstitute.Status}");
                
                // ? This employee is SUBSTITUTING someone else
                result.IsSubstituteEmployee = true;
                result.HasSubstitution = true;
                result.SubstitutionId = asSubstitute.SubstitutionId;
                result.OriginalEmployeeId = asSubstitute.OriginalEmployeeId;
                result.OriginalEmployeeName = asSubstitute.OriginalEmployeeName ?? "Unknown";
                result.SubstitutionReason = asSubstitute.Reason;
                
                // ?? FIXED: Safely parse TimeSpan values
                if (asSubstitute.StartTime != null)
                {
                    if (asSubstitute.StartTime is TimeSpan startTimeSpan)
                    {
                        result.StartTime = startTimeSpan;
                    }
                    else if (TimeSpan.TryParse(asSubstitute.StartTime.ToString(), out TimeSpan parsedStart))
                    {
                        result.StartTime = parsedStart;
                    }
                }
                
                if (asSubstitute.EndTime != null)
                {
                    if (asSubstitute.EndTime is TimeSpan endTimeSpan)
                    {
                        result.EndTime = endTimeSpan;
                    }
                    else if (TimeSpan.TryParse(asSubstitute.EndTime.ToString(), out TimeSpan parsedEnd))
                    {
                        result.EndTime = parsedEnd;
                    }
                }
                
                result.ApprovedAt = asSubstitute.ApprovedAt;

                System.Diagnostics.Debug.WriteLine($"          Times: {(result.StartTime?.ToString(@"hh\:mm") ?? "NULL")} - {(result.EndTime?.ToString(@"hh\:mm") ?? "NULL")}");

                // Get the substitute teaching schedule (original employee's schedule)
                if (result.OriginalEmployeeId.HasValue && result.StartTime.HasValue && result.EndTime.HasValue)
                {
                    System.Diagnostics.Debug.WriteLine($"          Fetching original employee's teaching schedule...");
                    
                    var substituteSchedule = await GetTeachingScheduleAsync(
                        connection,
                        result.OriginalEmployeeId.Value,
                        checkTime,
                        result.StartTime.Value,
                        result.EndTime.Value
                    );

                    if (substituteSchedule != null)
                    {
                        result.SubstituteScheduleId = substituteSchedule.ScheduleId;
                        result.SubstituteSubjectName = substituteSchedule.SubjectName;
                        result.SubstituteSection = substituteSchedule.Section;
                        result.SubstituteRoom = substituteSchedule.Room;
                        
                        System.Diagnostics.Debug.WriteLine($"          ? Found schedule: {result.SubstituteSubjectName} ({result.SubstituteSection})");
                        System.Diagnostics.Debug.WriteLine($"            Room: {result.SubstituteRoom}");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"          ?? WARNING: No teaching schedule found for original employee!");
                        System.Diagnostics.Debug.WriteLine($"            Subject/Section will show as generic 'Substituted Class'");
                        
                        // Fallback values
                        result.SubstituteSubjectName = "Substituted Class";
                        result.SubstituteSection = "";
                        result.SubstituteRoom = "";
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"          ?? WARNING: Missing data for schedule lookup");
                    System.Diagnostics.Debug.WriteLine($"            Original Employee ID: {result.OriginalEmployeeId?.ToString() ?? "NULL"}");
                    System.Diagnostics.Debug.WriteLine($"            Start Time: {result.StartTime?.ToString() ?? "NULL"}");
                    System.Diagnostics.Debug.WriteLine($"            End Time: {result.EndTime?.ToString() ?? "NULL"}");
                    
                    // Still allow the substitution even without schedule details
                    result.SubstituteSubjectName = "Substituted Class";
                    result.SubstituteSection = "";
                    result.SubstituteRoom = "";
                }

                // ? ALLOWED - Substitute must attend this class
                result.IsAllowed = true;
                result.Mode = "SUBSTITUTE_EMPLOYEE";
                result.Message = "Substitution Duty";
                result.Notes = $"You are substituting for {result.OriginalEmployeeName}.";

                System.Diagnostics.Debug.WriteLine($"");
                System.Diagnostics.Debug.WriteLine($"  ? SUBSTITUTE EMPLOYEE - COVERING CLASS");
                System.Diagnostics.Debug.WriteLine($"     Substitution ID: {result.SubstitutionId}");
                System.Diagnostics.Debug.WriteLine($"     Original Employee: {result.OriginalEmployeeName} (ID: {result.OriginalEmployeeId})");
                System.Diagnostics.Debug.WriteLine($"     Time: {(result.StartTime?.ToString(@"hh\:mm") ?? "N/A")} - {(result.EndTime?.ToString(@"hh\:mm") ?? "N/A")}")
;
                System.Diagnostics.Debug.WriteLine($"     Reason: {result.SubstitutionReason ?? "N/A"}");
                
                if (!string.IsNullOrEmpty(result.SubstituteSubjectName))
                {
                    System.Diagnostics.Debug.WriteLine($"     Covering: {result.SubstituteSubjectName} ({result.SubstituteSection})");
                }
                
                System.Diagnostics.Debug.WriteLine($"     ? Substitute must time in/out during this class");
                
                return result;
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[CHECK 2] ? No substitution found where employee is SUBSTITUTE");
            }

            // ? NO SUBSTITUTION - Continue normal validation
            System.Diagnostics.Debug.WriteLine($"");
            System.Diagnostics.Debug.WriteLine($"  ? No substitution found - Continue normal validation");
            
            return result;
        }
        catch (Exception ex) when (
            ex is System.Net.Sockets.SocketException ||
            ex is NpgsqlException ||
            ex.InnerException is System.Net.Sockets.SocketException ||
            ex.InnerException is NpgsqlException)
        {
            System.Diagnostics.Debug.WriteLine($"  ⚠ Connection error — offline fallback: {ex.Message}");
            result.IsAllowed = true;
            return result;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"");
            System.Diagnostics.Debug.WriteLine($"  ? ERROR checking substitution status: {ex.Message}");

            // On error, allow attendance to continue (fail-safe)
            result.IsAllowed = true;
            return result;
        }
    }

    /// <summary>
    /// Get teaching schedule for specific time range
    /// Uses flexible time matching to handle edge cases
    /// </summary>
    private async Task<TeachingScheduleInfo?> GetTeachingScheduleAsync(
        NpgsqlConnection connection,
        int employeeId,
        DateTime date,
        TimeSpan startTime,
        TimeSpan endTime)
    {
        try
        {
            var dayOfWeekNumber = (int)date.DayOfWeek;

            System.Diagnostics.Debug.WriteLine($"          [Schedule Lookup] Employee {employeeId}, Date: {date:yyyy-MM-dd}, Day: {dayOfWeekNumber}");
            System.Diagnostics.Debug.WriteLine($"          [Schedule Lookup] Looking for times: {startTime:hh\\:mm} - {endTime:hh\\:mm}");

            // ?? FIXED: Use flexible time matching - check if schedule overlaps with substitution time
            var schedule = await connection.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT 
                    schedule_id as ScheduleId,
                    subject_name as SubjectName,
                    section as Section,
                    COALESCE(room_id::text, '') as Room,
                    time_start as TimeStart,
                    time_end as TimeEnd
                FROM teaching_schedules
                WHERE employee_id = @EmployeeId
                    AND (
                        (specific_date = @Date) OR
                        (
                            day_of_week = @DayOfWeekNumber
                            AND (is_recurring = true OR is_recurring IS NULL)
                        )
                    )
                    AND (
                        (time_start = @StartTime AND time_end = @EndTime) OR
                        (time_start <= @StartTime AND time_end >= @EndTime) OR
                        (time_start >= @StartTime AND time_end <= @EndTime) OR
                        (time_start < @EndTime AND time_end > @StartTime)
                    )
                    AND (status IS NULL OR status = 'active' OR status = 'available')
                ORDER BY 
                    specific_date DESC NULLS LAST,
                    CASE WHEN time_start = @StartTime AND time_end = @EndTime THEN 0 ELSE 1 END,
                    ABS(EXTRACT(EPOCH FROM (time_start - @StartTime)))
                LIMIT 1
            ", new { 
                EmployeeId = employeeId, 
                Date = date.Date,
                DayOfWeekNumber = dayOfWeekNumber,
                StartTime = startTime,
                EndTime = endTime
            });

            if (schedule != null)
            {
                System.Diagnostics.Debug.WriteLine($"          [Schedule Lookup] ? Found: {schedule.SubjectName} ({schedule.Section})");
                System.Diagnostics.Debug.WriteLine($"          [Schedule Lookup]   Times: {schedule.TimeStart:hh\\:mm} - {schedule.TimeEnd:hh\\:mm}");
                
                return new TeachingScheduleInfo
                {
                    ScheduleId = schedule.ScheduleId,
                    SubjectName = schedule.SubjectName,
                    Section = schedule.Section,
                    Room = schedule.Room,
                    TimeStart = schedule.TimeStart,
                    TimeEnd = schedule.TimeEnd
                };
            }

            System.Diagnostics.Debug.WriteLine($"          [Schedule Lookup] ? No teaching_schedule found");

            // Check exam_schedules as fallback
            var exam = await connection.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT 
                    exam_schedule_id as ScheduleId,
                    subject_name as SubjectName,
                    section as Section,
                    room_code as Room,
                    time_start as TimeStart,
                    time_end as TimeEnd
                FROM exam_schedules
                WHERE employee_id = @EmployeeId
                    AND exam_date = @Date
                    AND (
                        (time_start = @StartTime AND time_end = @EndTime) OR
                        (time_start <= @StartTime AND time_end >= @EndTime) OR
                        (time_start >= @StartTime AND time_end <= @EndTime) OR
                        (time_start < @EndTime AND time_end > @StartTime)
                    )
                    AND (status IS NULL OR status = 'active' OR status = 'scheduled')
                ORDER BY 
                    CASE WHEN time_start = @StartTime AND time_end = @EndTime THEN 0 ELSE 1 END,
                    ABS(EXTRACT(EPOCH FROM (time_start - @StartTime)))
                LIMIT 1
            ", new { 
                EmployeeId = employeeId, 
                Date = date.Date,
                StartTime = startTime,
                EndTime = endTime
            });

            if (exam != null)
            {
                System.Diagnostics.Debug.WriteLine($"          [Schedule Lookup] ? Found EXAM: {exam.SubjectName} ({exam.Section})");
                System.Diagnostics.Debug.WriteLine($"          [Schedule Lookup]   Times: {exam.TimeStart:hh\\:mm} - {exam.TimeEnd:hh\\:mm}");
                
                return new TeachingScheduleInfo
                {
                    ScheduleId = exam.ScheduleId,
                    SubjectName = exam.SubjectName,
                    Section = exam.Section,
                    Room = exam.Room,
                    TimeStart = exam.TimeStart,
                    TimeEnd = exam.TimeEnd
                };
            }

            System.Diagnostics.Debug.WriteLine($"          [Schedule Lookup] ? No exam_schedule found either");
            return null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"          [Schedule Lookup] ? ERROR: {ex.Message}");
            return null;
        }
    }
}

/// <summary>
/// Result of substitution validation
/// </summary>
public class SubstitutionValidationResult
{
    public bool IsAllowed { get; set; }
    public int EmployeeId { get; set; }
    public DateTime CheckDate { get; set; }
    public bool HasSubstitution { get; set; }
    
    // Employee Role in Substitution
    public bool IsOriginalEmployee { get; set; }  // Being substituted
    public bool IsSubstituteEmployee { get; set; } // Substituting someone
    
    // Substitution Details
    public long? SubstitutionId { get; set; }
    public int? OriginalEmployeeId { get; set; }
    public string? OriginalEmployeeName { get; set; }
    public int? SubstituteEmployeeId { get; set; }
    public string? SubstituteName { get; set; }
    public string? SubstitutionReason { get; set; }
    public TimeSpan? StartTime { get; set; }
    public TimeSpan? EndTime { get; set; }
    public DateTime? ApprovedAt { get; set; }
    
    // Schedule Details (Original)
    public long? OriginalScheduleId { get; set; }
    public string? OriginalSubjectName { get; set; }
    public string? OriginalSection { get; set; }
    public string? OriginalRoom { get; set; }
    
    // Schedule Details (Substitute)
    public long? SubstituteScheduleId { get; set; }
    public string? SubstituteSubjectName { get; set; }
    public string? SubstituteSection { get; set; }
    public string? SubstituteRoom { get; set; }
    
    // Validation Results
    public string? Mode { get; set; }      // ORIGINAL_EMPLOYEE_SUBSTITUTED, SUBSTITUTE_EMPLOYEE
    public string? Message { get; set; }
    public string? Notes { get; set; }
}

/// <summary>
/// Teaching schedule information
/// </summary>
public class TeachingScheduleInfo
{
    public long ScheduleId { get; set; }
    public string? SubjectName { get; set; }
    public string? Section { get; set; }
    public string? Room { get; set; }
    public TimeSpan TimeStart { get; set; }
    public TimeSpan TimeEnd { get; set; }
}
