using System;
using System.Linq;
using System.Threading.Tasks;
using Npgsql;
using Dapper;

namespace RAMSOfficial.Services;

/// <summary>
/// ?? HELPER: Detects when an original employee's ONLY schedule has been substituted
/// Shows warning and marks them absent automatically
/// 
/// Example: Brian has ONLY 1 class (08:00-11:00)
///          Rohan substitutes that class
///          Brian taps ? Warning: "All schedules substituted, marking absent"
/// </summary>
public class AllSchedulesSubstitutedHelper
{
    private readonly string _connectionString;

    public AllSchedulesSubstitutedHelper(string connectionString)
    {
        _connectionString = connectionString;
    }

    /// <summary>
    /// Check if ALL schedules (teaching + exam) for this employee are substituted
    /// Returns warning info if yes, null if employee has remaining schedules
    /// </summary>
    public async Task<AllSubstitutedWarning?> CheckAllSchedulesSubstitutedAsync(int employeeId, DateTime date)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var currentDate = date.Date;
            var dayOfWeekNumber = (int)date.DayOfWeek; // 0=Sunday, 1=Monday

            System.Diagnostics.Debug.WriteLine($"");
            System.Diagnostics.Debug.WriteLine($"?? ALL SCHEDULES SUBSTITUTED CHECK:");
            System.Diagnostics.Debug.WriteLine($"   Employee ID: {employeeId}");
            System.Diagnostics.Debug.WriteLine($"   Date: {currentDate:yyyy-MM-dd} ({date.DayOfWeek})");

            // STEP 1: Count total teaching schedules
            var teachingScheduleCount = await connection.ExecuteScalarAsync<int>(@"
                SELECT COUNT(*)
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
            ", new { EmployeeId = employeeId, Date = currentDate, DayOfWeekNumber = dayOfWeekNumber });

            // STEP 2: Count total exam schedules
            var examScheduleCount = await connection.ExecuteScalarAsync<int>(@"
                SELECT COUNT(*)
                FROM exam_schedules es
                WHERE es.employee_id = @EmployeeId
                    AND (
                        (es.exam_date = @Date) OR
                        (es.day_of_week = @DayOfWeekNumber AND es.exam_date IS NULL)
                    )
                    AND (es.status IS NULL OR es.status = 'active')
            ", new { EmployeeId = employeeId, Date = currentDate, DayOfWeekNumber = dayOfWeekNumber });

            var totalScheduleCount = teachingScheduleCount + examScheduleCount;

            System.Diagnostics.Debug.WriteLine($"   ?? Schedule Counts:");
            System.Diagnostics.Debug.WriteLine($"      Teaching Schedules: {teachingScheduleCount}");
            System.Diagnostics.Debug.WriteLine($"      Exam Schedules: {examScheduleCount}");
            System.Diagnostics.Debug.WriteLine($"      TOTAL: {totalScheduleCount}");

            if (totalScheduleCount == 0)
            {
                System.Diagnostics.Debug.WriteLine($"   ?? No schedules found - not applicable");
                return null;
            }

            // STEP 3: Count substituted schedules
            var substitutedSchedules = await connection.QueryAsync<dynamic>(@"
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
            ", new { EmployeeId = employeeId, Date = currentDate });

            var substitutedList = substitutedSchedules.ToList();
            var substitutedCount = substitutedList.Count;

            System.Diagnostics.Debug.WriteLine($"   ?? Substituted Schedules: {substitutedCount}");

            if (substitutedCount == 0)
            {
                System.Diagnostics.Debug.WriteLine($"   ? No substitutions - employee can proceed normally");
                return null;
            }

            // STEP 4: Check if ALL schedules are substituted
            if (substitutedCount == totalScheduleCount && totalScheduleCount == 1)
            {
                // ?? CRITICAL: Employee has ONLY 1 schedule and it's substituted!
                var substitution = substitutedList.First();
                var substituteName = substitution.substitute_name ?? "Unknown";
                var startTime = substitution.start_time != null ? (TimeSpan)substitution.start_time : TimeSpan.Zero;
                var endTime = substitution.end_time != null ? (TimeSpan)substitution.end_time : TimeSpan.Zero;

                System.Diagnostics.Debug.WriteLine($"");
                System.Diagnostics.Debug.WriteLine($"   ?? ALL SCHEDULES SUBSTITUTED!");
                System.Diagnostics.Debug.WriteLine($"      Total Schedules: 1");
                System.Diagnostics.Debug.WriteLine($"      Substituted: 1");
                System.Diagnostics.Debug.WriteLine($"      Substitute: {substituteName}");
                System.Diagnostics.Debug.WriteLine($"      Time: {startTime:hh\\:mm} - {endTime:hh\\:mm}");
                System.Diagnostics.Debug.WriteLine($"      ?? Will mark employee as ABSENT!");

                return new AllSubstitutedWarning
                {
                    AllSchedulesSubstituted = true,
                    TotalScheduleCount = totalScheduleCount,
                    SubstitutedCount = substitutedCount,
                    SubstituteName = substituteName,
                    StartTime = startTime,
                    EndTime = endTime,
                    WarningMessage = $"This schedule has been substituted to {substituteName}, automatically marking you absent.",
                    ShouldMarkAbsent = true
                };
            }
            else if (substitutedCount == totalScheduleCount && totalScheduleCount > 1)
            {
                // All schedules substituted but employee has multiple schedules
                var substituteNames = substitutedList
                    .Select(s => s.substitute_name?.ToString() ?? "Unknown")
                    .Distinct()
                    .ToList();
                var substituteNamesStr = string.Join(", ", substituteNames);

                System.Diagnostics.Debug.WriteLine($"");
                System.Diagnostics.Debug.WriteLine($"   ?? ALL SCHEDULES SUBSTITUTED!");
                System.Diagnostics.Debug.WriteLine($"      Total Schedules: {totalScheduleCount}");
                System.Diagnostics.Debug.WriteLine($"      Substituted: {substitutedCount}");
                System.Diagnostics.Debug.WriteLine($"      Substitutes: {substituteNamesStr}");
                System.Diagnostics.Debug.WriteLine($"      ?? Will mark employee as ABSENT!");

                var firstSub = substitutedList.First();
                var firstSubName = firstSub.substitute_name ?? "Unknown";

                return new AllSubstitutedWarning
                {
                    AllSchedulesSubstituted = true,
                    TotalScheduleCount = totalScheduleCount,
                    SubstitutedCount = substitutedCount,
                    SubstituteName = substituteNamesStr,
                    StartTime = firstSub.start_time != null ? (TimeSpan)firstSub.start_time : TimeSpan.Zero,
                    EndTime = firstSub.end_time != null ? (TimeSpan)firstSub.end_time : TimeSpan.Zero,
                    WarningMessage = $"All {totalScheduleCount} schedule(s) have been substituted, automatically marking you absent.",
                    ShouldMarkAbsent = true
                };
            }
            else
            {
                // Some schedules substituted but not all
                System.Diagnostics.Debug.WriteLine($"   ? Partial substitution - employee has remaining schedules");
                System.Diagnostics.Debug.WriteLine($"      Remaining: {totalScheduleCount - substitutedCount} schedule(s)");
                return null;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"");
            System.Diagnostics.Debug.WriteLine($"? ERROR in CheckAllSchedulesSubstitutedAsync:");
            System.Diagnostics.Debug.WriteLine($"   Message: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"   Stack: {ex.StackTrace}");
            return null;
        }
    }
}

/// <summary>
/// Warning information when all schedules are substituted
/// </summary>
public class AllSubstitutedWarning
{
    public bool AllSchedulesSubstituted { get; set; }
    public int TotalScheduleCount { get; set; }
    public int SubstitutedCount { get; set; }
    public string SubstituteName { get; set; } = string.Empty;
    public TimeSpan StartTime { get; set; }
    public TimeSpan EndTime { get; set; }
    public string WarningMessage { get; set; } = string.Empty;
    public bool ShouldMarkAbsent { get; set; }
}
