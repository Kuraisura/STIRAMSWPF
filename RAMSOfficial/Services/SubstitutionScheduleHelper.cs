using System;
using System.Threading.Tasks;
using Npgsql;
using Dapper;

namespace RAMSOfficial.Services;

/// <summary>
/// ?? DEDICATED HELPER: Retrieves substitution schedule times from class_substitutions_v2
/// This ensures the UI always gets the correct 08:00-11:00 times for substitutions
/// </summary>
public class SubstitutionScheduleHelper
{
    private readonly string _connectionString;

    public SubstitutionScheduleHelper(string connectionString)
    {
        _connectionString = connectionString;
    }

    /// <summary>
    /// Get substitution schedule times for a substitute employee on a specific date
    /// Returns the start_time and end_time directly from class_substitutions_v2
    /// </summary>
    public async Task<SubstitutionScheduleTimes?> GetSubstitutionTimesAsync(int substituteEmployeeId, DateTime date)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var currentDate = date.Date;

            System.Diagnostics.Debug.WriteLine($"");
            System.Diagnostics.Debug.WriteLine($"?? SUBSTITUTION SCHEDULE HELPER:");
            System.Diagnostics.Debug.WriteLine($"   Substitute Employee ID: {substituteEmployeeId}");
            System.Diagnostics.Debug.WriteLine($"   Date: {currentDate:yyyy-MM-dd}");

            // ?? DIRECT QUERY: Get start_time and end_time from class_substitutions_v2
            var substitution = await connection.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT 
                    cs.id as SubstitutionId,
                    cs.start_time,
                    cs.end_time,
                    cs.original_employee_id,
                    oe.full_name as OriginalEmployeeName,
                    ts.subject_name,
                    ts.section,
                    COALESCE(ts.room_id::text, '') as room
                FROM class_substitutions_v2 cs
                LEFT JOIN employees oe ON cs.original_employee_id = oe.employee_id
                LEFT JOIN teaching_schedules ts ON 
                    ts.employee_id = cs.original_employee_id
                    AND ts.time_start = cs.start_time
                    AND ts.time_end = cs.end_time
                    AND (
                        (ts.specific_date = cs.substitution_date::date) OR
                        (ts.day_of_week = EXTRACT(DOW FROM cs.substitution_date)::int AND (ts.is_recurring = true OR ts.is_recurring IS NULL))
                    )
                WHERE cs.substitute_employee_id = @EmployeeId
                    AND cs.substitution_date::date = @Date
                    AND cs.status = 'approved'
                    AND cs.start_time IS NOT NULL
                    AND cs.end_time IS NOT NULL
                ORDER BY cs.start_time ASC
                LIMIT 1
            ", new { 
                EmployeeId = substituteEmployeeId, 
                Date = currentDate
            });

            if (substitution != null)
            {
                // ?? Access dynamic properties with lowercase (PostgreSQL/Dapper convention)
                var times = new SubstitutionScheduleTimes
                {
                    SubstitutionId = substitution.id != null ? (long)substitution.id : 0,
                    StartTime = substitution.start_time != null ? (TimeSpan)substitution.start_time : TimeSpan.Zero,
                    EndTime = substitution.end_time != null ? (TimeSpan)substitution.end_time : TimeSpan.Zero,
                    OriginalEmployeeId = substitution.original_employee_id ?? 0,
                    OriginalEmployeeName = substitution.original_employee_name ?? "Unknown",
                    SubjectName = substitution.subject_name ?? "Substituted Class",
                    Section = substitution.section ?? "",
                    Room = substitution.room ?? ""
                };

                System.Diagnostics.Debug.WriteLine($"   ? SUBSTITUTION FOUND!");
                System.Diagnostics.Debug.WriteLine($"      Substitution ID: {times.SubstitutionId}");
                System.Diagnostics.Debug.WriteLine($"      Start Time: {times.StartTime:hh\\:mm} ? FROM DB");
                System.Diagnostics.Debug.WriteLine($"      End Time: {times.EndTime:hh\\:mm} ? FROM DB");
                System.Diagnostics.Debug.WriteLine($"      Original: {times.OriginalEmployeeName}");
                System.Diagnostics.Debug.WriteLine($"      Subject: {times.SubjectName} ({times.Section})");
                System.Diagnostics.Debug.WriteLine($"      Room: {times.Room}");

                return times;
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"   ? No substitution found");
                return null;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"");
            System.Diagnostics.Debug.WriteLine($"? ERROR in GetSubstitutionTimesAsync:");
            System.Diagnostics.Debug.WriteLine($"   Message: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"   Stack: {ex.StackTrace}");
            return null;
        }
    }
}

/// <summary>
/// Substitution schedule times returned by the helper
/// </summary>
public class SubstitutionScheduleTimes
{
    public long SubstitutionId { get; set; }
    public TimeSpan StartTime { get; set; }
    public TimeSpan EndTime { get; set; }
    public int OriginalEmployeeId { get; set; }
    public string OriginalEmployeeName { get; set; } = string.Empty;
    public string SubjectName { get; set; } = string.Empty;
    public string Section { get; set; } = string.Empty;
    public string Room { get; set; } = string.Empty;
}
