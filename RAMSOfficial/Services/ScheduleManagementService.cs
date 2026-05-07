using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Npgsql;
using Dapper;
using RAMSOfficial.Models;

namespace RAMSOfficial.Services;

/// <summary>
/// Comprehensive Schedule Management Service
/// Handles ALL schedule types: Exams, Classes, Holidays, Substitutions, Verification Requests
/// </summary>
public class ScheduleManagementService
{
    private readonly string _connectionString;

    public ScheduleManagementService(string connectionString)
    {
        _connectionString = connectionString;
    }

    #region Exam Schedules

    /// <summary>
    /// Get all exam schedules for an employee
    /// </summary>
    public async Task<List<ExamSchedule>> GetExamSchedulesAsync(int employeeId, DateTime? startDate = null, DateTime? endDate = null)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var query = @"
                SELECT * FROM exam_schedules
                WHERE employee_id = @EmployeeId
                    AND (status IS NULL OR status = 'active')";

            if (startDate.HasValue && endDate.HasValue)
            {
                query += " AND exam_date BETWEEN @StartDate AND @EndDate";
            }

            query += " ORDER BY exam_date DESC, time_start ASC";

            var schedules = await connection.QueryAsync<ExamSchedule>(query, new
            {
                EmployeeId = employeeId,
                StartDate = startDate,
                EndDate = endDate
            });

            return schedules.ToList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error getting exam schedules: {ex.Message}");
            return new List<ExamSchedule>();
        }
    }

    /// <summary>
    /// Get active exam schedule for specific date/time
    /// </summary>
    public async Task<ExamSchedule?> GetActiveExamScheduleAsync(int employeeId, DateTime dateTime)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var dayOfWeek = dateTime.DayOfWeek.ToString();
            var currentTime = dateTime.TimeOfDay;

            var exam = await connection.QueryFirstOrDefaultAsync<ExamSchedule>(@"
                SELECT * FROM exam_schedules
                WHERE employee_id = @EmployeeId
                    AND (
                        (exam_date = @Date) OR
                        (day_of_week = @DayOfWeek AND exam_date IS NULL)
                    )
                    AND time_start <= @Time
                    AND time_end >= @Time
                    AND (status IS NULL OR status = 'active')
                ORDER BY exam_date DESC NULLS LAST
                LIMIT 1
            ", new { EmployeeId = employeeId, Date = dateTime.Date, DayOfWeek = dayOfWeek, Time = currentTime });

            return exam;
        }
        catch
        {
            return null;
        }
    }

    #endregion

    #region Teaching Schedules

    /// <summary>
    /// Get all teaching schedules for an employee
    /// </summary>
    public async Task<List<TeachingSchedule>> GetTeachingSchedulesAsync(int employeeId, DateTime? startDate = null, DateTime? endDate = null)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var query = @"
                SELECT * FROM teaching_schedules
                WHERE employee_id = @EmployeeId
                    AND (status IS NULL OR status = 'active')";

            if (startDate.HasValue && endDate.HasValue)
            {
                query += @" AND (
                    (specific_date BETWEEN @StartDate AND @EndDate) OR
                    (is_recurring = true)
                )";
            }

            query += " ORDER BY specific_date DESC NULLS LAST, day_of_week, time_start ASC";

            var schedules = await connection.QueryAsync<TeachingSchedule>(query, new
            {
                EmployeeId = employeeId,
                StartDate = startDate,
                EndDate = endDate
            });

            return schedules.ToList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error getting teaching schedules: {ex.Message}");
            return new List<TeachingSchedule>();
        }
    }

    /// <summary>
    /// Get active teaching schedule for specific date/time
    /// </summary>
    public async Task<TeachingSchedule?> GetActiveTeachingScheduleAsync(int employeeId, DateTime dateTime)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var dayOfWeek = dateTime.DayOfWeek.ToString();
            var dayOfWeekNumber = (int)dateTime.DayOfWeek; // 0=Sunday, 1=Monday, etc.
            var currentTime = dateTime.TimeOfDay;

            var schedule = await connection.QueryFirstOrDefaultAsync<TeachingSchedule>(@"
                SELECT * FROM teaching_schedules
                WHERE employee_id = @EmployeeId
                    AND (
                        (specific_date = @Date) OR
                        (
                            -- Handle numeric day_of_week (SMALLINT type)
                            day_of_week = @DayOfWeekNumber
                            AND (is_recurring = true OR is_recurring IS NULL)
                        )
                    )
                    AND time_start <= @Time
                    AND time_end >= @Time
                    AND (status IS NULL OR status = 'active' OR status = 'available')
                ORDER BY specific_date DESC NULLS LAST
                LIMIT 1
            ", new { 
                EmployeeId = employeeId, 
                Date = dateTime.Date, 
                DayOfWeekNumber = dayOfWeekNumber,
                Time = currentTime 
            });

            return schedule;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Get today's teaching schedules
    /// </summary>
    public async Task<List<TeachingSchedule>> GetTodaySchedulesAsync(int employeeId)
    {
        var today = DateTime.Today;
        var dayOfWeek = today.DayOfWeek.ToString();
        var dayOfWeekNumber = (int)today.DayOfWeek;

        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var schedules = await connection.QueryAsync<TeachingSchedule>(@"
                SELECT * FROM teaching_schedules
                WHERE employee_id = @EmployeeId
                    AND (
                        (specific_date = @Today) OR
                        (
                            -- Handle numeric day_of_week (SMALLINT type)
                            day_of_week = @DayOfWeekNumber
                            AND (is_recurring = true OR is_recurring IS NULL)
                        )
                    )
                    AND (status IS NULL OR status = 'active' OR status = 'available')
                ORDER BY time_start ASC
            ", new { 
                EmployeeId = employeeId, 
                Today = today, 
                DayOfWeekNumber = dayOfWeekNumber
            });

            return schedules.ToList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error getting today's schedules: {ex.Message}");
            return new List<TeachingSchedule>();
        }
    }

    #endregion

    #region Holidays

    /// <summary>
    /// Get all holidays within date range
    /// </summary>
    public async Task<List<HolidayCalendar>> GetHolidaysAsync(DateTime? startDate = null, DateTime? endDate = null)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var query = "SELECT * FROM holiday_calendar WHERE 1=1";

            if (startDate.HasValue && endDate.HasValue)
            {
                query += " AND date BETWEEN @StartDate AND @EndDate";
            }

            query += " ORDER BY date ASC";

            var holidays = await connection.QueryAsync<HolidayCalendar>(query, new
            {
                StartDate = startDate,
                EndDate = endDate
            });

            return holidays.ToList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error getting holidays: {ex.Message}");
            return new List<HolidayCalendar>();
        }
    }

    /// <summary>
    /// Check if a specific date is a holiday
    /// </summary>
    public async Task<HolidayCalendar?> GetHolidayAsync(DateTime date)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var holiday = await connection.QueryFirstOrDefaultAsync<HolidayCalendar>(@"
                SELECT * FROM holiday_calendar
                WHERE date = @Date
                LIMIT 1
            ", new { Date = date });

            return holiday;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Get upcoming holidays (next 30 days)
    /// </summary>
    public async Task<List<HolidayCalendar>> GetUpcomingHolidaysAsync(int days = 30)
    {
        var startDate = DateTime.Today;
        var endDate = DateTime.Today.AddDays(days);

        return await GetHolidaysAsync(startDate, endDate);
    }

    #endregion

    #region Class Substitutions

    /// <summary>
    /// Get substitutions where employee is the substitute
    /// </summary>
    public async Task<List<ClassSubstitution>> GetSubstitutionDutiesAsync(int substituteEmployeeId, DateTime? startDate = null, DateTime? endDate = null)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var query = @"
                SELECT * FROM class_substitutions_v2
                WHERE substitute_employee_id = @EmployeeId
                    AND status = 'approved'";

            if (startDate.HasValue && endDate.HasValue)
            {
                query += " AND substitution_date BETWEEN @StartDate AND @EndDate";
            }

            query += " ORDER BY substitution_date DESC, start_time ASC";

            var substitutions = await connection.QueryAsync<ClassSubstitution>(query, new
            {
                EmployeeId = substituteEmployeeId,
                StartDate = startDate,
                EndDate = endDate
            });

            return substitutions.ToList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error getting substitution duties: {ex.Message}");
            return new List<ClassSubstitution>();
        }
    }

    /// <summary>
    /// Get substitutions where employee needs a substitute
    /// </summary>
    public async Task<List<ClassSubstitution>> GetOwnSubstitutionsAsync(int originalEmployeeId, DateTime? startDate = null, DateTime? endDate = null)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var query = @"
                SELECT * FROM class_substitutions_v2
                WHERE original_employee_id = @EmployeeId";

            if (startDate.HasValue && endDate.HasValue)
            {
                query += " AND substitution_date BETWEEN @StartDate AND @EndDate";
            }

            query += " ORDER BY substitution_date DESC, start_time ASC";

            var substitutions = await connection.QueryAsync<ClassSubstitution>(query, new
            {
                EmployeeId = originalEmployeeId,
                StartDate = startDate,
                EndDate = endDate
            });

            return substitutions.ToList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error getting own substitutions: {ex.Message}");
            return new List<ClassSubstitution>();
        }
    }

    /// <summary>
    /// Get active substitution for specific date/time
    /// </summary>
    public async Task<ClassSubstitution?> GetActiveSubstitutionAsync(int substituteEmployeeId, DateTime dateTime)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var currentTime = dateTime.TimeOfDay;

            var substitution = await connection.QueryFirstOrDefaultAsync<ClassSubstitution>(@"
                SELECT * FROM class_substitutions_v2
                WHERE substitute_employee_id = @EmployeeId
                    AND substitution_date = @Date
                    AND start_time <= @Time
                    AND end_time >= @Time
                    AND status = 'approved'
                LIMIT 1
            ", new { EmployeeId = substituteEmployeeId, Date = dateTime.Date, Time = currentTime });

            return substitution;
        }
        catch
        {
            return null;
        }
    }

    #endregion

    #region Verification Requests

    /// <summary>
    /// Get all verification requests for an employee
    /// </summary>
    public async Task<List<VerificationRequest>> GetVerificationRequestsAsync(int employeeId, string? status = null)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var query = "SELECT * FROM verification_requests WHERE employee_id = @EmployeeId";

            if (!string.IsNullOrEmpty(status))
            {
                query += " AND status = @Status";
            }

            query += " ORDER BY requested_at DESC";

            var requests = await connection.QueryAsync<VerificationRequest>(query, new
            {
                EmployeeId = employeeId,
                Status = status
            });

            return requests.ToList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error getting verification requests: {ex.Message}");
            return new List<VerificationRequest>();
        }
    }

    /// <summary>
    /// Get pending verification request for a specific date
    /// </summary>
    public async Task<VerificationRequest?> GetPendingVerificationRequestAsync(int employeeId, DateTime date)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var request = await connection.QueryFirstOrDefaultAsync<VerificationRequest>(@"
                SELECT * FROM verification_requests
                WHERE employee_id = @EmployeeId
                    AND DATE(schedule_date) = @Date
                    AND status = 'pending'
                ORDER BY requested_at DESC
                LIMIT 1
            ", new { EmployeeId = employeeId, Date = date });

            return request;
        }
        catch
        {
            return null;
        }
    }

    #endregion

    #region Schedule Summary

    /// <summary>
    /// Get comprehensive schedule summary for a date
    /// </summary>
    public async Task<ScheduleSummary> GetScheduleSummaryAsync(int employeeId, DateTime date)
    {
        var summary = new ScheduleSummary
        {
            Date = date,
            EmployeeId = employeeId
        };

        // Check if it's a holiday
        summary.Holiday = await GetHolidayAsync(date);

        // Get today's teaching schedules
        summary.TeachingSchedules = await GetTodaySchedulesAsync(employeeId);

        // Get exams for the day
        summary.ExamSchedules = await GetExamSchedulesAsync(employeeId, date, date);

        // Get substitution duties
        summary.SubstitutionDuties = await GetSubstitutionDutiesAsync(employeeId, date, date);

        // Get verification requests
        summary.VerificationRequest = await GetPendingVerificationRequestAsync(employeeId, date);

        return summary;
    }

    /// <summary>
    /// Get weekly schedule summary
    /// </summary>
    public async Task<List<ScheduleSummary>> GetWeeklyScheduleSummaryAsync(int employeeId, DateTime weekStart)
    {
        var summaries = new List<ScheduleSummary>();

        for (int i = 0; i < 7; i++)
        {
            var date = weekStart.AddDays(i);
            var summary = await GetScheduleSummaryAsync(employeeId, date);
            summaries.Add(summary);
        }

        return summaries;
    }

    #endregion
}

/// <summary>
/// Schedule summary for a specific date
/// </summary>
public class ScheduleSummary
{
    public DateTime Date { get; set; }
    public int EmployeeId { get; set; }
    public HolidayCalendar? Holiday { get; set; }
    public List<TeachingSchedule> TeachingSchedules { get; set; } = new();
    public List<ExamSchedule> ExamSchedules { get; set; } = new();
    public List<ClassSubstitution> SubstitutionDuties { get; set; } = new();
    public VerificationRequest? VerificationRequest { get; set; }

    public bool HasSchedules => 
        TeachingSchedules.Any() || 
        ExamSchedules.Any() || 
        SubstitutionDuties.Any();

    public bool IsHoliday => Holiday != null;

    public string GetSummaryText()
    {
        if (IsHoliday)
        {
            return $"Holiday: {Holiday!.Name}";
        }

        var scheduleCount = TeachingSchedules.Count + ExamSchedules.Count + SubstitutionDuties.Count;
        
        if (scheduleCount == 0)
        {
            return "No scheduled classes";
        }

        var parts = new List<string>();
        
        if (ExamSchedules.Any())
            parts.Add($"{ExamSchedules.Count} exam(s)");
        
        if (TeachingSchedules.Any())
            parts.Add($"{TeachingSchedules.Count} class(es)");
        
        if (SubstitutionDuties.Any())
            parts.Add($"{SubstitutionDuties.Count} substitution(s)");

        return string.Join(", ", parts);
    }
}
