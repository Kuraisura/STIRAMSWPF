using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Npgsql;
using Dapper;
using RAMSOfficial;
using RAMSOfficial.Services;

namespace RAMSOfficial.Helpers;

/// <summary>
/// Handles dynamic schedule updates when a substitute duty is approved AFTER the employee has already tapped in.
/// Example: Brian taps in at 8:00 AM for his 8-11 class. Later, a substitution is approved for 12-3 PM.
/// Brian's expected schedule should update to 8:00-3:00 PM, and his next tap will be validated against this.
/// </summary>
public class DynamicScheduleUpdater
{
    private readonly string _connectionString;

    public DynamicScheduleUpdater(string connectionString)
    {
        _connectionString = connectionString;
    }

    /// <summary>
    /// Updates an employee's active attendance record when a new substitute duty is assigned.
    /// This recalculates their expected time out based on the new combined schedule.
    /// </summary>
    public async Task<DynamicScheduleUpdateResult> UpdateActiveAttendanceScheduleAsync(
        int employeeId,
        DateTime date,
        TimeSpan newSubstituteDutyStart,
        TimeSpan newSubstituteDutyEnd)
    {
        var result = new DynamicScheduleUpdateResult
        {
            EmployeeId = employeeId,
            Date = date,
            NewSubstituteDutyStart = newSubstituteDutyStart,
            NewSubstituteDutyEnd = newSubstituteDutyEnd
        };

        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            System.Diagnostics.Debug.WriteLine("");
            System.Diagnostics.Debug.WriteLine("???????????????????????????????????????????????????????");
            System.Diagnostics.Debug.WriteLine($"  \uE946 DYNAMIC SCHEDULE UPDATE");  // Info icon
            System.Diagnostics.Debug.WriteLine($"  Employee ID: {employeeId}");
            System.Diagnostics.Debug.WriteLine($"  Date: {date:yyyy-MM-dd}");
            System.Diagnostics.Debug.WriteLine($"  New Substitute Duty: {newSubstituteDutyStart:hh\\:mm} - {newSubstituteDutyEnd:hh\\:mm}");
            System.Diagnostics.Debug.WriteLine("???????????????????????????????????????????????????????");

            // STEP 1: Check if employee has an active Time IN (no Time OUT yet) for today
            var activeLog = await connection.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT 
                    log_id,
                    log_time,
                    log_type,
                    notes
                FROM attendance_logs
                WHERE employee_id = @EmployeeId
                    AND date = @Date
                    AND log_type = 'IN'
                    AND NOT EXISTS (
                        SELECT 1 FROM attendance_logs out_log
                        WHERE out_log.employee_id = @EmployeeId
                            AND out_log.date = @Date
                            AND out_log.log_type = 'OUT'
                    )
                ORDER BY log_time DESC
                LIMIT 1
            ", new { EmployeeId = employeeId, Date = date.Date });

            if (activeLog == null)
            {
                result.HasActiveTimeIn = false;
                result.Message = "No active Time IN found. Employee hasn't tapped in yet.";
                System.Diagnostics.Debug.WriteLine($"  \uE946 {result.Message}");  // Info icon
                return result;
            }

            result.HasActiveTimeIn = true;
            result.OriginalTimeIn = (DateTime)activeLog.log_time;
            result.ActiveLogId = activeLog.log_id;

            System.Diagnostics.Debug.WriteLine($"  \uE73E Active Time IN found: {result.OriginalTimeIn:HH:mm}");  // CheckMark

            // STEP 2: Get employee's original schedules for today
            var originalSchedules = await GetEmployeeSchedulesAsync(connection, employeeId, date);
            result.OriginalSchedules = originalSchedules;

            if (!originalSchedules.Any())
            {
                result.Message = "No original schedules found for employee.";
                System.Diagnostics.Debug.WriteLine($"  \uE7BA {result.Message}");  // Warning
                return result;
            }

            // STEP 3: Calculate original expected time range (before substitute duty)
            var originalTimeIn = originalSchedules.Min(s => s.StartTime);
            var originalTimeOut = originalSchedules.Max(s => s.EndTime);

            result.OriginalExpectedTimeIn = originalTimeIn;
            result.OriginalExpectedTimeOut = originalTimeOut;

            System.Diagnostics.Debug.WriteLine($"  Original Expected Schedule: {originalTimeIn:hh\\:mm} - {originalTimeOut:hh\\:mm}");

            // STEP 4: Add the new substitute duty to the schedule
            var newSubstituteDuty = new ScheduleTimeSlot
            {
                StartTime = newSubstituteDutyStart,
                EndTime = newSubstituteDutyEnd,
                IsSubstituteDuty = true,
                SubjectName = "Substitute Duty"
            };

            var combinedSchedules = originalSchedules.ToList();
            combinedSchedules.Add(newSubstituteDuty);

            // STEP 5: Calculate NEW expected time range (including substitute duty)
            var newExpectedTimeIn = combinedSchedules.Min(s => s.StartTime);
            var newExpectedTimeOut = combinedSchedules.Max(s => s.EndTime);

            result.NewExpectedTimeIn = newExpectedTimeIn;
            result.NewExpectedTimeOut = newExpectedTimeOut;
            result.CombinedSchedules = combinedSchedules;

            System.Diagnostics.Debug.WriteLine($"  \uE8F4 New Expected Schedule: {newExpectedTimeIn:hh\\:mm} - {newExpectedTimeOut:hh\\:mm}");  // Updated icon

            // STEP 6: Check if schedule has actually changed
            if (originalTimeOut == newExpectedTimeOut)
            {
                result.ScheduleChanged = false;
                result.Message = "Schedule not changed. Substitute duty doesn't extend the time range.";
                System.Diagnostics.Debug.WriteLine($"  \uE946 {result.Message}");  // Info
                return result;
            }

            result.ScheduleChanged = true;
            result.TimeExtensionMinutes = (int)(newExpectedTimeOut - originalTimeOut).TotalMinutes;

            System.Diagnostics.Debug.WriteLine($"  \uE73E Schedule Extended by {result.TimeExtensionMinutes} minutes!");  // CheckMark

            // STEP 7: Update the attendance log notes to reflect the new schedule
            var updatedNotes = BuildUpdatedNotes(activeLog.notes?.ToString(), newSubstituteDutyStart, newSubstituteDutyEnd, newExpectedTimeOut);
            
            await connection.ExecuteAsync(@"
                UPDATE attendance_logs
                SET notes = @Notes,
                    updated_at = NOW()
                WHERE log_id = @LogId
            ", new { Notes = updatedNotes, LogId = result.ActiveLogId });

            result.UpdatedNotes = updatedNotes;
            result.Success = true;
            result.Message = $"Schedule updated successfully. New expected time out: {newExpectedTimeOut:hh\\:mm}";

            System.Diagnostics.Debug.WriteLine($"  \uE73E {result.Message}");  // CheckMark
            System.Diagnostics.Debug.WriteLine("???????????????????????????????????????????????????????");

            return result;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"  \uE783 Error updating dynamic schedule: {ex.Message}");  // Error
            result.Success = false;
            result.Message = $"Error: {ex.Message}";
            return result;
        }
    }

    /// <summary>
    /// Gets all schedules for an employee on a specific date
    /// IMPORTANT: This gets the employee's OWN teaching schedules, NOT substitute duties
    /// </summary>
    private async Task<List<ScheduleTimeSlot>> GetEmployeeSchedulesAsync(
        NpgsqlConnection connection,
        int employeeId,
        DateTime date)
    {
        var schedules = new List<ScheduleTimeSlot>();
        var dayOfWeek = (int)date.DayOfWeek;
        var dayOfWeekName = date.DayOfWeek.ToString();
        var dayOfWeekNumberText = dayOfWeek.ToString();

        System.Diagnostics.Debug.WriteLine($"\uE946 GetEmployeeSchedulesAsync:");  // Info
        System.Diagnostics.Debug.WriteLine($"   Employee ID: {employeeId}");
        System.Diagnostics.Debug.WriteLine($"   Date: {date:yyyy-MM-dd} ({date.DayOfWeek})");
        System.Diagnostics.Debug.WriteLine($"   Day of Week (int): {dayOfWeek}");

        // Get substituted time slots (to exclude from schedule)
        var substitutedSlots = await connection.QueryAsync<dynamic>(@"
            SELECT 
                start_time as StartTime,
                end_time as EndTime
            FROM class_substitutions_v2
            WHERE original_employee_id = @EmployeeId
                AND substitution_date::date = @Date
                AND status = 'approved'
        ", new { EmployeeId = employeeId, Date = date.Date });

        var substitutedList = substitutedSlots.ToList();

        // ???????????????????????????????????????????????????????????????????????????????
        // Get exam schedules FIRST (Priority 1)
        // ???????????????????????????????????????????????????????????????????????????????
        var examSchedules = await connection.QueryAsync<dynamic>(@"
            SELECT 
                time_start,
                time_end,
                subject_name,
                section,
                room_code
            FROM exam_schedules
            WHERE employee_id = @EmployeeId
                AND (
                    exam_date::date = @Date
                    OR (
                        exam_date IS NULL
                        AND (
                            day_of_week::text = @DayOfWeekNumberText
                            OR LOWER(TRIM(day_of_week::text)) = LOWER(@DayOfWeekName)
                        )
                    )
                )
                AND (
                    status IS NULL
                    OR LOWER(status) IN ('active', 'scheduled', 'available', 'approved', 'published')
                )
            ORDER BY time_start
        ", new
        {
            EmployeeId = employeeId,
            Date = date.Date,
            DayOfWeekNumberText = dayOfWeekNumberText,
            DayOfWeekName = dayOfWeekName
        });

        System.Diagnostics.Debug.WriteLine($"   \uE946 Exam schedules query returned {((IEnumerable<dynamic>)examSchedules).Count()} results");

        foreach (var exam in examSchedules)
        {
            // Filter out if substituted
            bool isSubstituted = substitutedList.Any(sub => 
                ((TimeSpan)sub.StartTime) == ((TimeSpan)exam.time_start) &&
                ((TimeSpan)sub.EndTime) == ((TimeSpan)exam.time_end));
                
            if (isSubstituted) continue;

            System.Diagnostics.Debug.WriteLine($"   \uE73E Found exam schedule:");  // CheckMark
            System.Diagnostics.Debug.WriteLine($"      Time: {exam.time_start:hh\\:mm} - {exam.time_end:hh\\:mm}");
            System.Diagnostics.Debug.WriteLine($"      Subject: {exam.subject_name}");

            schedules.Add(new ScheduleTimeSlot
            {
                StartTime = (TimeSpan)exam.time_start,
                EndTime = (TimeSpan)exam.time_end,
                SubjectName = exam.subject_name,
                Section = exam.section,
                Room = exam.room_code,
                IsSubstituteDuty = false
            });
        }

        if (schedules.Any())
        {
            System.Diagnostics.Debug.WriteLine($"   \uE73E Exam schedules found: {schedules.Count}, skipping teaching schedules.");
            System.Diagnostics.Debug.WriteLine($"   \uE73E Total schedules found: {schedules.Count}");
            return schedules.OrderBy(s => s.StartTime).ToList();
        }

        // ???????????????????????????????????????????????????????????????????????????????
        // Get teaching schedules for this employee (Priority 2)
        // ???????????????????????????????????????????????????????????????????????????????
        var teachingSchedules = await connection.QueryAsync<dynamic>(@"
            SELECT 
                ts.schedule_id,
                ts.time_start,
                ts.time_end,
                ts.subject_name,
                ts.section,
                ts.room_id,
                ts.day_of_week,
                ts.specific_date,
                ts.is_recurring,
                ts.status
            FROM teaching_schedules ts
            WHERE ts.employee_id = @EmployeeId
                AND (
                    (ts.specific_date::date = @Date) OR
                    (
                        (
                            ts.day_of_week::text = @DayOfWeekNumberText
                            OR LOWER(TRIM(ts.day_of_week::text)) = LOWER(@DayOfWeekName)
                        )
                        AND (ts.is_recurring = true OR ts.is_recurring IS NULL)
                    )
                )
                AND (
                    ts.status IS NULL
                    OR LOWER(ts.status) IN ('active', 'available', 'scheduled', 'approved', 'published')
                )
            ORDER BY ts.time_start
        ", new
        {
            EmployeeId = employeeId,
            Date = date.Date,
            DayOfWeekNumberText = dayOfWeekNumberText,
            DayOfWeekName = dayOfWeekName
        });

        System.Diagnostics.Debug.WriteLine($"   \uE946 Teaching schedules query returned {((IEnumerable<dynamic>)teachingSchedules).Count()} results");

        foreach (var schedule in teachingSchedules)
        {
            // Filter out if substituted
            bool isSubstituted = substitutedList.Any(sub => 
                ((TimeSpan)sub.StartTime) == ((TimeSpan)schedule.time_start) &&
                ((TimeSpan)sub.EndTime) == ((TimeSpan)schedule.time_end));
                
            if (isSubstituted) continue;

            System.Diagnostics.Debug.WriteLine($"   \uE73E Found teaching schedule:");  // CheckMark
            System.Diagnostics.Debug.WriteLine($"      Schedule ID: {schedule.schedule_id}");
            System.Diagnostics.Debug.WriteLine($"      Time: {schedule.time_start:hh\\:mm} - {schedule.time_end:hh\\:mm}");
            System.Diagnostics.Debug.WriteLine($"      Subject: {schedule.subject_name}");
            System.Diagnostics.Debug.WriteLine($"      Section: {schedule.section}");
            System.Diagnostics.Debug.WriteLine($"      Day of Week: {schedule.day_of_week}");
            System.Diagnostics.Debug.WriteLine($"      Specific Date: {schedule.specific_date}");
            System.Diagnostics.Debug.WriteLine($"      Is Recurring: {schedule.is_recurring}");
            System.Diagnostics.Debug.WriteLine($"      Status: {schedule.status}");

            schedules.Add(new ScheduleTimeSlot
            {
                StartTime = (TimeSpan)schedule.time_start,
                EndTime = (TimeSpan)schedule.time_end,
                SubjectName = schedule.subject_name,
                Section = schedule.section,
                Room = schedule.room_id?.ToString(),
                IsSubstituteDuty = false  // These are OWN schedules, not substitute duties
            });
        }

        System.Diagnostics.Debug.WriteLine($"   \uE73E Total schedules found: {schedules.Count}");

        return schedules.OrderBy(s => s.StartTime).ToList();
    }

    /// <summary>
    /// Builds updated notes for the attendance log
    /// </summary>
    private string BuildUpdatedNotes(string? existingNotes, TimeSpan dutyStart, TimeSpan dutyEnd, TimeSpan newTimeOut)
    {
        var notes = string.IsNullOrWhiteSpace(existingNotes) ? "" : existingNotes + " | ";
        notes += $"Schedule updated: Substitute duty {dutyStart:hh\\:mm}-{dutyEnd:hh\\:mm} added. New expected time out: {newTimeOut:hh\\:mm}";
        return notes;
    }

    /// <summary>
    /// Gets the current expected schedule for an employee, including any dynamically added substitute duties
    /// </summary>
    public async Task<CurrentScheduleInfo> GetCurrentExpectedScheduleAsync(int employeeId, DateTime date)
    {
        var info = new CurrentScheduleInfo
        {
            EmployeeId = employeeId,
            Date = date
        };

        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            System.Diagnostics.Debug.WriteLine($"");
            System.Diagnostics.Debug.WriteLine($"\uE946 GetCurrentExpectedScheduleAsync:");  // Info
            System.Diagnostics.Debug.WriteLine($"   Employee ID: {employeeId}");
            System.Diagnostics.Debug.WriteLine($"   Date: {date:yyyy-MM-dd} ({date.DayOfWeek})");

            // Get original schedules
            var originalSchedules = await GetEmployeeSchedulesAsync(connection, employeeId, date);
            info.OriginalSchedules = originalSchedules;

            System.Diagnostics.Debug.WriteLine($"   \uE73E Original schedules count: {originalSchedules.Count}");

            // Get approved substitute duties for today
            var substituteDuties = await connection.QueryAsync<dynamic>(@"
                SELECT 
                    cs.start_time,
                    cs.end_time,
                    cs.original_employee_id,
                    oe.full_name as original_employee_name,
                    ts.subject_name,
                    ts.section,
                    ts.room_id
                FROM class_substitutions_v2 cs
                LEFT JOIN employees oe ON cs.original_employee_id = oe.employee_id
                LEFT JOIN teaching_schedules ts ON 
                    ts.employee_id = cs.original_employee_id
                    AND ts.time_start = cs.start_time
                    AND ts.time_end = cs.end_time
                WHERE cs.substitute_employee_id = @EmployeeId
                    AND cs.substitution_date::date = @Date
                    AND cs.status = 'approved'
                ORDER BY cs.start_time
            ", new { EmployeeId = employeeId, Date = date.Date });

            var substituteDutyList = new List<ScheduleTimeSlot>();
            foreach (var duty in substituteDuties)
            {
                System.Diagnostics.Debug.WriteLine($"   \uE73E Found substitute duty:");  // CheckMark
                System.Diagnostics.Debug.WriteLine($"      Time: {duty.start_time:hh\\:mm} - {duty.end_time:hh\\:mm}");
                System.Diagnostics.Debug.WriteLine($"      For: {duty.original_employee_name}");
                System.Diagnostics.Debug.WriteLine($"      Subject: {duty.subject_name} ({duty.section})");

                substituteDutyList.Add(new ScheduleTimeSlot
                {
                    StartTime = (TimeSpan)duty.start_time,
                    EndTime = (TimeSpan)duty.end_time,
                    IsSubstituteDuty = true,
                    OriginalEmployeeId = duty.original_employee_id,
                    OriginalEmployeeName = duty.original_employee_name,
                    SubjectName = duty.subject_name ?? "Substitute Duty",
                    Section = duty.section,
                    Room = duty.room_id?.ToString()  // Get room_id from teaching_schedules
                });
            }

            info.SubstituteDuties = substituteDutyList;

            System.Diagnostics.Debug.WriteLine($"   \uE73E Substitute duties count: {substituteDutyList.Count}");

            // Combine all schedules
            var allSchedules = originalSchedules.ToList();
            allSchedules.AddRange(substituteDutyList);

            if (allSchedules.Any())
            {
                info.HasSchedule = true;
                info.ExpectedTimeIn = allSchedules.Min(s => s.StartTime);
                info.ExpectedTimeOut = allSchedules.Max(s => s.EndTime);
                info.TotalScheduleCount = allSchedules.Count;
                info.CombinedSchedules = allSchedules.OrderBy(s => s.StartTime).ToList();

                System.Diagnostics.Debug.WriteLine($"   \uE73E COMBINED SCHEDULE:");  // CheckMark
                System.Diagnostics.Debug.WriteLine($"      Time IN:  {info.ExpectedTimeIn:hh\\:mm}");
                System.Diagnostics.Debug.WriteLine($"      Time OUT: {info.ExpectedTimeOut:hh\\:mm}");
                System.Diagnostics.Debug.WriteLine($"      Total: {info.TotalScheduleCount} schedules");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"   \uE7BA No schedules found (neither original nor substitute duties)");  // Warning
            }

            return info;
        }
        catch (Exception ex) when (
            ex is System.Net.Sockets.SocketException ||
            (ex is NpgsqlException && ex is not PostgresException) ||
            ex.InnerException is System.Net.Sockets.SocketException ||
            (ex.InnerException is NpgsqlException && ex.InnerException is not PostgresException))
        {
            // Database connecting error - Fallback to Local Cache Mode
            System.Diagnostics.Debug.WriteLine($"[LocalState] DB Error — switching to Local Cache Mode for schedule (emp={employeeId})");
            System.Diagnostics.Debug.WriteLine($"   Error: {ex.GetType().Name}: {ex.Message}");

            // Fallback 1: RAM cache (ScheduleCacheService — loaded at startup from rams_offline.db)
            try
            {
                var cachedSchedules = App.ScheduleCache?.GetSchedules(employeeId);
                if (cachedSchedules != null && cachedSchedules.Count > 0)
                {
                    info.HasSchedule = true;
                    info.ExpectedTimeIn = cachedSchedules.Min(s => s.StartTime);
                    info.ExpectedTimeOut = cachedSchedules.Max(s => s.EndTime);
                    info.TotalScheduleCount = cachedSchedules.Count;
                    info.OriginalSchedules = cachedSchedules.Select(s => new ScheduleTimeSlot
                    {
                        StartTime = s.StartTime,
                        EndTime = s.EndTime,
                        SubjectName = s.SubjectName,
                        Section = s.Section,
                        Room = s.Room,
                        IsSubstituteDuty = false
                    }).ToList();
                    info.CombinedSchedules = info.OriginalSchedules;

                    System.Diagnostics.Debug.WriteLine($"[LocalState] RAM cache fallback: {cachedSchedules.Count} schedules, {info.ExpectedTimeIn:hh\\:mm}-{info.ExpectedTimeOut:hh\\:mm}");
                    return info;
                }
            }
            catch (Exception cacheEx)
            {
                System.Diagnostics.Debug.WriteLine($"[LocalState] RAM cache fallback failed: {cacheEx.Message}");
            }

            // Fallback 2: Employee default schedule_time_in / schedule_time_out from RAM cache
            try
            {
                var emp = App.ScheduleCache?.GetEmployeeById(employeeId);
                if (emp != null && emp.ScheduleTimeIn != TimeSpan.Zero && emp.ScheduleTimeOut != TimeSpan.Zero)
                {
                    info.HasSchedule = true;
                    info.ExpectedTimeIn = emp.ScheduleTimeIn;
                    info.ExpectedTimeOut = emp.ScheduleTimeOut;
                    info.TotalScheduleCount = 1;
                    info.OriginalSchedules = new List<ScheduleTimeSlot>
                    {
                        new()
                        {
                            StartTime = emp.ScheduleTimeIn,
                            EndTime = emp.ScheduleTimeOut,
                            SubjectName = "Default Schedule",
                            IsSubstituteDuty = false
                        }
                    };
                    info.CombinedSchedules = info.OriginalSchedules;

                    System.Diagnostics.Debug.WriteLine($"[LocalState] Employee default schedule fallback: {emp.ScheduleTimeIn:hh\\:mm}-{emp.ScheduleTimeOut:hh\\:mm}");
                    return info;
                }
            }
            catch (Exception empEx)
            {
                System.Diagnostics.Debug.WriteLine($"[LocalState] Employee default schedule fallback failed: {empEx.Message}");
            }

            System.Diagnostics.Debug.WriteLine($"[LocalState] No local schedule fallback available for employee {employeeId}");
            info.ErrorMessage = $"Offline — no local schedule data available: {ex.Message}";
            return info;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"\uE783 Error getting current schedule: {ex.Message}");  // Error
            System.Diagnostics.Debug.WriteLine($"   Stack: {ex.StackTrace}");
            info.ErrorMessage = ex.Message;
            return info;
        }
    }
}

/// <summary>
/// Result of dynamic schedule update operation
/// </summary>
public class DynamicScheduleUpdateResult
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    
    // Employee info
    public int EmployeeId { get; set; }
    public DateTime Date { get; set; }
    
    // Active attendance log
    public bool HasActiveTimeIn { get; set; }
    public DateTime? OriginalTimeIn { get; set; }
    public int? ActiveLogId { get; set; }
    
    // New substitute duty
    public TimeSpan NewSubstituteDutyStart { get; set; }
    public TimeSpan NewSubstituteDutyEnd { get; set; }
    
    // Schedule changes
    public List<ScheduleTimeSlot> OriginalSchedules { get; set; } = new();
    public List<ScheduleTimeSlot> CombinedSchedules { get; set; } = new();
    
    public TimeSpan? OriginalExpectedTimeIn { get; set; }
    public TimeSpan? OriginalExpectedTimeOut { get; set; }
    public TimeSpan? NewExpectedTimeIn { get; set; }
    public TimeSpan? NewExpectedTimeOut { get; set; }
    
    public bool ScheduleChanged { get; set; }
    public int TimeExtensionMinutes { get; set; }
    
    public string? UpdatedNotes { get; set; }

    /// <summary>
    /// Gets a summary of the schedule update
    /// </summary>
    public string GetSummary()
    {
        if (!Success)
            return $"Failed: {Message}";

        if (!HasActiveTimeIn)
            return "No active Time IN found. No update needed.";

        if (!ScheduleChanged)
            return "Schedule not changed. Substitute duty doesn't extend time range.";

        return $"Schedule updated: {OriginalExpectedTimeOut:hh\\:mm} ? {NewExpectedTimeOut:hh\\:mm} (+{TimeExtensionMinutes} min)";
    }
}

/// <summary>
/// Current schedule information including dynamically added substitute duties
/// </summary>
public class CurrentScheduleInfo
{
    public int EmployeeId { get; set; }
    public DateTime Date { get; set; }
    
    public bool HasSchedule { get; set; }
    public TimeSpan? ExpectedTimeIn { get; set; }
    public TimeSpan? ExpectedTimeOut { get; set; }
    
    public List<ScheduleTimeSlot> OriginalSchedules { get; set; } = new();
    public List<ScheduleTimeSlot> SubstituteDuties { get; set; } = new();
    public List<ScheduleTimeSlot> CombinedSchedules { get; set; } = new();
    
    public int TotalScheduleCount { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Checks if the employee's schedule includes substitute duties
    /// </summary>
    public bool HasSubstituteDuties => SubstituteDuties.Any();

    /// <summary>
    /// Gets the display string for the schedule
    /// </summary>
    public string GetScheduleDisplay()
    {
        if (!HasSchedule)
            return "No schedule";

        var display = $"{ExpectedTimeIn:hh\\:mm} - {ExpectedTimeOut:hh\\:mm}";
        
        if (HasSubstituteDuties)
        {
            display += $" (includes {SubstituteDuties.Count} substitute {(SubstituteDuties.Count == 1 ? "duty" : "duties")})";
        }

        return display;
    }
}
