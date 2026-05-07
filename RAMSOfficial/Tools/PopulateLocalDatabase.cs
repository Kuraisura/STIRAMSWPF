using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using RAMSOfficial.Services;

namespace RAMSOfficial.Tools;

/// <summary>
/// Tool to manually populate local database from database
/// Run this when you need to force a full database clone
/// </summary>
public class PopulateLocalDatabase
{
    /// <summary>
    /// Populate local database with ALL data from database
    /// </summary>
    public static async Task PopulateAsync()
    {
        Debug.WriteLine("");
        Debug.WriteLine("????????????????????????????????????????????");
        Debug.WriteLine("?? MANUAL DATABASE POPULATION STARTED");
        Debug.WriteLine("????????????????????????????????????????????");
        
        try
        {
            var onlineDb = new DatabaseService();
            var localDb = new LocalDatabaseService();
            
            // Initialize local database schema
            Debug.WriteLine("?? Initializing local database schema...");
            await localDb.InitializeAsync();
            Debug.WriteLine("? Schema initialized");
            
            Debug.WriteLine("");
            Debug.WriteLine("?? Downloading data from database...");
            
            // 1. Get all employees
            Debug.WriteLine("   1?? Fetching employees...");
            var employees = await GetAllEmployeesFromdatabaseAsync(onlineDb);
            Debug.WriteLine($"      Found: {employees.Count} employees");
            
            if (employees.Count == 0)
            {
                Debug.WriteLine("");
                Debug.WriteLine("?? WARNING: No employees found in database!");
                Debug.WriteLine("   Please check:");
                Debug.WriteLine("   • database connection string is correct");
                Debug.WriteLine("   • employees table exists");
                Debug.WriteLine("   • employees table has data");
                return;
            }
            
            // 2. Save to local database
            Debug.WriteLine("");
            Debug.WriteLine("?? Saving to local database...");
            await localDb.OverwriteEmployeesAsync(employees);
            Debug.WriteLine($"? Saved {employees.Count} employees to local database");
            
            // 3. Get attendance logs (last 30 days)
            Debug.WriteLine("");
            Debug.WriteLine("   2?? Fetching recent attendance logs...");
            var logs = await GetRecentAttendanceLogsAsync(onlineDb, 30);
            Debug.WriteLine($"      Found: {logs.Count} attendance logs");
            
            if (logs.Count > 0)
            {
                await localDb.OverwriteAttendanceLogsAsync(logs);
                Debug.WriteLine($"? Saved {logs.Count} attendance logs");
            }
            
            // 4. Get schedules
            Debug.WriteLine("");
            Debug.WriteLine("   3?? Fetching schedules...");
            var schedules = await GetAllSchedulesAsync(onlineDb);
            Debug.WriteLine($"      Found: {schedules.Count} schedules");
            
            if (schedules.Count > 0)
            {
                await localDb.OverwriteSchedulesAsync(schedules);
                Debug.WriteLine($"? Saved {schedules.Count} schedules");
            }
            
            // 5. Get holidays
            Debug.WriteLine("");
            Debug.WriteLine("   4?? Fetching holidays...");
            var holidays = await GetAllHolidaysAsync(onlineDb);
            Debug.WriteLine($"      Found: {holidays.Count} holidays");
            
            if (holidays.Count > 0)
            {
                await localDb.OverwriteHolidaysAsync(holidays);
                Debug.WriteLine($"? Saved {holidays.Count} holidays");
            }
            
            // 6. Get substitutes
            Debug.WriteLine("");
            Debug.WriteLine("   5?? Fetching substitutes...");
            var substitutes = await GetAllSubstitutesAsync(onlineDb);
            Debug.WriteLine($"      Found: {substitutes.Count} substitutes");
            
            if (substitutes.Count > 0)
            {
                await localDb.OverwriteSubstitutesAsync(substitutes);
                Debug.WriteLine($"? Saved {substitutes.Count} substitutes");
            }
            
            // Summary
            Debug.WriteLine("");
            Debug.WriteLine("????????????????????????????????????????????");
            Debug.WriteLine("? POPULATION COMPLETE");
            Debug.WriteLine("????????????????????????????????????????????");
            Debug.WriteLine($"?? Summary:");
            Debug.WriteLine($"   Employees: {employees.Count}");
            Debug.WriteLine($"   Attendance Logs: {logs.Count}");
            Debug.WriteLine($"   Schedules: {schedules.Count}");
            Debug.WriteLine($"   Holidays: {holidays.Count}");
            Debug.WriteLine($"   Substitutes: {substitutes.Count}");
            Debug.WriteLine($"");
            Debug.WriteLine($"?? Database Location:");
            Debug.WriteLine($"   {localDb.GetDatabasePath()}");
            Debug.WriteLine("????????????????????????????????????????????");
            
            // Create backup
            Debug.WriteLine("");
            Debug.WriteLine("?? Creating backup...");
            var backupPath = await localDb.ExportToSqlBackupAsync();
            if (backupPath != null)
            {
                Debug.WriteLine($"? Backup created: {backupPath}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("");
            Debug.WriteLine("? ERROR during population:");
            Debug.WriteLine($"   {ex.Message}");
            Debug.WriteLine($"");
            Debug.WriteLine("Stack Trace:");
            Debug.WriteLine(ex.StackTrace);
            throw;
        }
    }
    
    private static async Task<List<RAMSOfficial.Models.Employee>> GetAllEmployeesFromdatabaseAsync(DatabaseService db)
    {
        try
        {
            using var connection = new Npgsql.NpgsqlConnection(db.GetConnectionString());
            await connection.OpenAsync();
            
            var employees = await connection.QueryAsync<RAMSOfficial.Models.Employee>(@"
                SELECT 
                    employee_id as EmployeeId,
                    rfid_code as RfidCode,
                    full_name as FullName,
                    school_id as SchoolId,
                    department as Department,
                    department_id as DepartmentId,
                    email as Email,
                    phone as Phone,
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
                WHERE is_active = true
                ORDER BY full_name
            ");
            
            return employees.ToList();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? Error getting employees: {ex.Message}");
            return new List<RAMSOfficial.Models.Employee>();
        }
    }
    
    private static async Task<List<RAMSOfficial.Models.AttendanceLog>> GetRecentAttendanceLogsAsync(DatabaseService db, int days)
    {
        try
        {
            using var connection = new Npgsql.NpgsqlConnection(db.GetConnectionString());
            await connection.OpenAsync();
            
            var cutoffDate = DateTime.Now.AddDays(-days).Date;
            
            var logs = await connection.QueryAsync<RAMSOfficial.Models.AttendanceLog>(@"
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
                    schedule_id as ScheduleId,
                    created_at as CreatedAt,
                    updated_at as UpdatedAt
                FROM attendance_logs
                WHERE date >= @CutoffDate
                ORDER BY log_time DESC
            ", new { CutoffDate = cutoffDate });
            
            return logs.ToList();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? Error getting attendance logs: {ex.Message}");
            return new List<RAMSOfficial.Models.AttendanceLog>();
        }
    }
    
    private static async Task<List<dynamic>> GetAllSchedulesAsync(DatabaseService db)
    {
        try
        {
            using var connection = new Npgsql.NpgsqlConnection(db.GetConnectionString());
            await connection.OpenAsync();

            var schedules = await connection.QueryAsync(@"
                SELECT schedule_id, employee_id, subject_name,
                    CAST(NULL AS TEXT)  AS subject_code,
                    section, day_of_week,
                    time_start          AS start_time,
                    time_end            AS end_time,
                    CAST(NULL AS TEXT)  AS room,
                    CAST(NULL AS INT)   AS semester,
                    CAST(NULL AS TEXT)  AS school_year,
                    CASE WHEN status = 'active' OR status IS NULL THEN true ELSE false END AS is_active,
                    created_at, updated_at
                FROM teaching_schedules
                WHERE status = 'active' OR status IS NULL
                ORDER BY employee_id, day_of_week, time_start
            ");

            return schedules.ToList();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? Error getting schedules: {ex.Message}");
            return new List<dynamic>();
        }
    }
    
    private static async Task<List<dynamic>> GetAllHolidaysAsync(DatabaseService db)
    {
        try
        {
            using var connection = new Npgsql.NpgsqlConnection(db.GetConnectionString());
            await connection.OpenAsync();
            
            var holidays = await connection.QueryAsync(@"
                SELECT * FROM holidays
                WHERE is_active = true
                ORDER BY holiday_date
            ");
            
            return holidays.ToList();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? Error getting holidays: {ex.Message}");
            return new List<dynamic>();
        }
    }
    
    private static async Task<List<dynamic>> GetAllSubstitutesAsync(DatabaseService db)
    {
        try
        {
            using var connection = new Npgsql.NpgsqlConnection(db.GetConnectionString());
            await connection.OpenAsync();
            
            var substitutes = await connection.QueryAsync(@"
                SELECT * FROM substitute_teacher
                WHERE is_active = true
                ORDER BY substitute_date DESC
            ");
            
            return substitutes.ToList();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"? Error getting substitutes: {ex.Message}");
            return new List<dynamic>();
        }
    }
}
