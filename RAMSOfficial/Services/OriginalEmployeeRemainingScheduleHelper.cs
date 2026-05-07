using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Npgsql;
using Dapper;

namespace RAMSOfficial.Services;

/// <summary>
/// ?? HELPER: Gets the REMAINING schedule for the ORIGINAL employee
/// when their class has been substituted.
/// 
/// Example: Brian has classes at 08:00-11:00 and 11:00-14:00
///          Rohan substitutes the 08:00-11:00 class
///          Brian should see: 11:00-14:00 (his remaining schedule)
/// </summary>
public class OriginalEmployeeRemainingScheduleHelper
{
    private readonly string _connectionString;

    public OriginalEmployeeRemainingScheduleHelper(string connectionString)
    {
        _connectionString = connectionString;
    }

    /// <summary>
    /// Get the remaining schedule for an original employee whose class was substituted
    /// Returns the earliest start and latest end of NON-substituted classes
    /// </summary>
    public async Task<RemainingScheduleResult?> GetRemainingScheduleAsync(int originalEmployeeId, DateTime date)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var currentDate = date.Date;
            var dayOfWeekNumber = (int)date.DayOfWeek; // 0=Sunday, 1=Monday

            System.Diagnostics.Debug.WriteLine($"");
            System.Diagnostics.Debug.WriteLine($"?? ORIGINAL EMPLOYEE REMAINING SCHEDULE HELPER:");
            System.Diagnostics.Debug.WriteLine($"   Original Employee ID: {originalEmployeeId}");
            System.Diagnostics.Debug.WriteLine($"   Date: {currentDate:yyyy-MM-dd} ({date.DayOfWeek})");

            // STEP 1: Get ALL teaching schedules for this employee on this day
            var allSchedules = await connection.QueryAsync<dynamic>(@"
                SELECT 
                    ts.schedule_id,
                    ts.time_start,
                    ts.time_end,
                    ts.subject_name,
                    ts.section,
                    COALESCE(ts.room_id::text, '') as room
                FROM teaching_schedules ts
                WHERE ts.employee_id = @EmployeeId
                    AND (
                        (ts.specific_date = @Date) OR
                        (
                            ts.day_of_week = @DayOfWeekNumber
                            AND (ts.is_recurring = true OR ts.is_recurring IS NULL)
                        )
                    )
                    AND (ts.status IS NULL OR ts.status = 'active' OR ts.status = 'available')
                ORDER BY ts.time_start ASC
            ", new { 
                EmployeeId = originalEmployeeId, 
                Date = currentDate,
                DayOfWeekNumber = dayOfWeekNumber
            });

            var allSchedulesList = allSchedules.ToList();

            if (!allSchedulesList.Any())
            {
                System.Diagnostics.Debug.WriteLine($"   ? No schedules found for this employee");
                return null;
            }

            System.Diagnostics.Debug.WriteLine($"   ? Found {allSchedulesList.Count} total schedule(s):");
            foreach (var sched in allSchedulesList)
            {
                var start = sched.time_start != null ? (TimeSpan)sched.time_start : TimeSpan.Zero;
                var end = sched.time_end != null ? (TimeSpan)sched.time_end : TimeSpan.Zero;
                var subject = sched.subject_name ?? "Unknown";
                var section = sched.section ?? "";
                System.Diagnostics.Debug.WriteLine($"      - {start:hh\\:mm}-{end:hh\\:mm}: {subject} ({section})");
            }

            // STEP 2: Get SUBSTITUTED time slots for this employee
            var substitutedSlots = await connection.QueryAsync<dynamic>(@"
                SELECT 
                    cs.start_time,
                    cs.end_time,
                    se.full_name as substitute_name
                FROM class_substitutions_v2 cs
                LEFT JOIN employees se ON cs.substitute_employee_id = se.employee_id
                WHERE cs.original_employee_id = @EmployeeId
                    AND cs.substitution_date::date = @Date
                    AND cs.status = 'approved'
                ORDER BY cs.start_time ASC
            ", new { 
                EmployeeId = originalEmployeeId, 
                Date = currentDate
            });

            var substitutedSlotsList = substitutedSlots.ToList();

            if (!substitutedSlotsList.Any())
            {
                System.Diagnostics.Debug.WriteLine($"   ?? No substitutions found - returning full schedule");
                
                // No substitutions, return all schedules
                var firstSchedule = allSchedulesList.First();
                var lastSchedule = allSchedulesList.Last();
                
                return new RemainingScheduleResult
                {
                    HasRemainingSchedule = true,
                    EarliestStartTime = (TimeSpan)firstSchedule.time_start,
                    LatestEndTime = (TimeSpan)lastSchedule.time_end,
                    RemainingClassCount = allSchedulesList.Count,
                    SubstitutedClassCount = 0,
                    TotalClassCount = allSchedulesList.Count
                };
            }

            System.Diagnostics.Debug.WriteLine($"   ?? Found {substitutedSlotsList.Count} substitution(s):");
            foreach (var sub in substitutedSlotsList)
            {
                var start = sub.start_time != null ? (TimeSpan)sub.start_time : TimeSpan.Zero;
                var end = sub.end_time != null ? (TimeSpan)sub.end_time : TimeSpan.Zero;
                var substituteName = sub.substitute_name ?? "Unknown";
                System.Diagnostics.Debug.WriteLine($"      - {start:hh\\:mm}-{end:hh\\:mm}: Substituted by {substituteName}");
            }

            // STEP 3: Filter out substituted schedules
            var remainingSchedules = new List<dynamic>();
            foreach (var schedule in allSchedulesList)
            {
                var schedStart = (TimeSpan)schedule.time_start;
                var schedEnd = (TimeSpan)schedule.time_end;
                
                // Check if this schedule is substituted
                bool isSubstituted = substitutedSlotsList.Any(sub => 
                    (TimeSpan)sub.start_time == schedStart && 
                    (TimeSpan)sub.end_time == schedEnd
                );
                
                if (!isSubstituted)
                {
                    remainingSchedules.Add(schedule);
                }
            }

            if (!remainingSchedules.Any())
            {
                System.Diagnostics.Debug.WriteLine($"   ? All classes are substituted - NO remaining schedule");
                
                return new RemainingScheduleResult
                {
                    HasRemainingSchedule = false,
                    EarliestStartTime = TimeSpan.Zero,
                    LatestEndTime = TimeSpan.Zero,
                    RemainingClassCount = 0,
                    SubstitutedClassCount = substitutedSlotsList.Count,
                    TotalClassCount = allSchedulesList.Count
                };
            }

            // STEP 4: Calculate earliest start and latest end from remaining schedules
            var earliestStart = remainingSchedules.Min(s => (TimeSpan)s.time_start);
            var latestEnd = remainingSchedules.Max(s => (TimeSpan)s.time_end);

            System.Diagnostics.Debug.WriteLine($"");
            System.Diagnostics.Debug.WriteLine($"   ? REMAINING SCHEDULE CALCULATED:");
            System.Diagnostics.Debug.WriteLine($"      Earliest Start: {earliestStart:hh\\:mm}");
            System.Diagnostics.Debug.WriteLine($"      Latest End: {latestEnd:hh\\:mm}");
            System.Diagnostics.Debug.WriteLine($"      Remaining Classes: {remainingSchedules.Count}");
            System.Diagnostics.Debug.WriteLine($"      Substituted Classes: {substitutedSlotsList.Count}");
            System.Diagnostics.Debug.WriteLine($"      Total Classes: {allSchedulesList.Count}");

            return new RemainingScheduleResult
            {
                HasRemainingSchedule = true,
                EarliestStartTime = earliestStart,
                LatestEndTime = latestEnd,
                RemainingClassCount = remainingSchedules.Count,
                SubstitutedClassCount = substitutedSlotsList.Count,
                TotalClassCount = allSchedulesList.Count
            };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"");
            System.Diagnostics.Debug.WriteLine($"? ERROR in GetRemainingScheduleAsync:");
            System.Diagnostics.Debug.WriteLine($"   Message: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"   Stack: {ex.StackTrace}");
            return null;
        }
    }
}

/// <summary>
/// Result containing the remaining schedule for the original employee
/// </summary>
public class RemainingScheduleResult
{
    public bool HasRemainingSchedule { get; set; }
    public TimeSpan EarliestStartTime { get; set; }
    public TimeSpan LatestEndTime { get; set; }
    public int RemainingClassCount { get; set; }
    public int SubstitutedClassCount { get; set; }
    public int TotalClassCount { get; set; }
}
