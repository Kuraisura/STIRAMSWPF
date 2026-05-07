using System;
using System.Threading.Tasks;
using Npgsql;
using Dapper;

namespace RAMSOfficial.Services;

/// <summary>
/// Service for logging ALL RFID tap attempts (success and failures)
/// Separate from attendance logging to avoid constraint violations
/// </summary>
public class RfidAttemptLoggingService
{
    private readonly string _connectionString;

    public RfidAttemptLoggingService(string connectionString)
    {
        _connectionString = connectionString;
    }

    /// <summary>
    /// Log a successful RFID tap with attendance recorded
    /// </summary>
    public async Task LogSuccessAsync(string rfidCode, int employeeId, string employeeName)
    {
        await LogAttemptAsync(new RfidAttemptLog
        {
            RfidCode = rfidCode,
            AttemptStatus = "SUCCESS",
            EmployeeId = employeeId,
            EmployeeName = employeeName,
            ErrorMessage = null
        });

        System.Diagnostics.Debug.WriteLine("");
        System.Diagnostics.Debug.WriteLine("???????????????????????????????????????");
        System.Diagnostics.Debug.WriteLine("? RFID Attempt Log");
        System.Diagnostics.Debug.WriteLine($"  Status: SUCCESS");
        System.Diagnostics.Debug.WriteLine($"  Code: {rfidCode}");
        System.Diagnostics.Debug.WriteLine($"  Employee: {employeeName}");
        System.Diagnostics.Debug.WriteLine($"  Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        System.Diagnostics.Debug.WriteLine("???????????????????????????????????????");
    }

    /// <summary>
    /// Log an unknown RFID (not found in system)
    /// </summary>
    public async Task LogUnknownRfidAsync(string rfidCode)
    {
        await LogAttemptAsync(new RfidAttemptLog
        {
            RfidCode = rfidCode,
            AttemptStatus = "UNKNOWN_RFID",
            EmployeeId = null,
            EmployeeName = null,
            ErrorMessage = "RFID code not found in employee database"
        });

        System.Diagnostics.Debug.WriteLine("");
        System.Diagnostics.Debug.WriteLine("???????????????????????????????????????");
        System.Diagnostics.Debug.WriteLine("?? RFID Attempt Log");
        System.Diagnostics.Debug.WriteLine($"  Status: UNKNOWN_RFID");
        System.Diagnostics.Debug.WriteLine($"  Code: {rfidCode}");
        System.Diagnostics.Debug.WriteLine($"  Employee: (Not Found)");
        System.Diagnostics.Debug.WriteLine($"  Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        System.Diagnostics.Debug.WriteLine("???????????????????????????????????????");
    }

    /// <summary>
    /// Log an inactive employee attempt
    /// </summary>
    public async Task LogInactiveEmployeeAsync(string rfidCode, int employeeId, string employeeName)
    {
        await LogAttemptAsync(new RfidAttemptLog
        {
            RfidCode = rfidCode,
            AttemptStatus = "INACTIVE_EMPLOYEE",
            EmployeeId = employeeId,
            EmployeeName = employeeName,
            ErrorMessage = "Employee account is inactive"
        });

        System.Diagnostics.Debug.WriteLine("");
        System.Diagnostics.Debug.WriteLine("???????????????????????????????????????");
        System.Diagnostics.Debug.WriteLine("?? RFID Attempt Log");
        System.Diagnostics.Debug.WriteLine($"  Status: INACTIVE_EMPLOYEE");
        System.Diagnostics.Debug.WriteLine($"  Code: {rfidCode}");
        System.Diagnostics.Debug.WriteLine($"  Employee: {employeeName}");
        System.Diagnostics.Debug.WriteLine($"  Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        System.Diagnostics.Debug.WriteLine("???????????????????????????????????????");
    }

    /// <summary>
    /// Log a blocked attendance attempt (validation failed)
    /// </summary>
    public async Task LogBlockedAttemptAsync(string rfidCode, int employeeId, string employeeName, string blockReason)
    {
        await LogAttemptAsync(new RfidAttemptLog
        {
            RfidCode = rfidCode,
            AttemptStatus = "BLOCKED",
            EmployeeId = employeeId,
            EmployeeName = employeeName,
            BlockReason = blockReason,
            ErrorMessage = $"Attendance blocked: {blockReason}"
        });

        System.Diagnostics.Debug.WriteLine("");
        System.Diagnostics.Debug.WriteLine("???????????????????????????????????????");
        System.Diagnostics.Debug.WriteLine("?? RFID Attempt Log");
        System.Diagnostics.Debug.WriteLine($"  Status: BLOCKED");
        System.Diagnostics.Debug.WriteLine($"  Code: {rfidCode}");
        System.Diagnostics.Debug.WriteLine($"  Employee: {employeeName}");
        System.Diagnostics.Debug.WriteLine($"  Reason: {blockReason}");
        System.Diagnostics.Debug.WriteLine($"  Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        System.Diagnostics.Debug.WriteLine("???????????????????????????????????????");
    }

    /// <summary>
    /// Log a duplicate tap attempt
    /// </summary>
    public async Task LogDuplicateAttemptAsync(string rfidCode, int employeeId, string employeeName)
    {
        await LogAttemptAsync(new RfidAttemptLog
        {
            RfidCode = rfidCode,
            AttemptStatus = "DUPLICATE",
            EmployeeId = employeeId,
            EmployeeName = employeeName,
            ErrorMessage = "Duplicate tap detected within cooldown period"
        });

        System.Diagnostics.Debug.WriteLine("");
        System.Diagnostics.Debug.WriteLine("???????????????????????????????????????");
        System.Diagnostics.Debug.WriteLine("?? RFID Attempt Log");
        System.Diagnostics.Debug.WriteLine($"  Status: DUPLICATE");
        System.Diagnostics.Debug.WriteLine($"  Code: {rfidCode}");
        System.Diagnostics.Debug.WriteLine($"  Employee: {employeeName}");
        System.Diagnostics.Debug.WriteLine($"  Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        System.Diagnostics.Debug.WriteLine("???????????????????????????????????????");
    }

    /// <summary>
    /// Log a system error during RFID processing
    /// </summary>
    public async Task LogErrorAsync(string rfidCode, string errorMessage, int? employeeId = null, string? employeeName = null)
    {
        await LogAttemptAsync(new RfidAttemptLog
        {
            RfidCode = rfidCode,
            AttemptStatus = "ERROR",
            EmployeeId = employeeId,
            EmployeeName = employeeName,
            ErrorMessage = errorMessage
        });

        System.Diagnostics.Debug.WriteLine("");
        System.Diagnostics.Debug.WriteLine("???????????????????????????????????????");
        System.Diagnostics.Debug.WriteLine("? RFID Attempt Log");
        System.Diagnostics.Debug.WriteLine($"  Status: ERROR");
        System.Diagnostics.Debug.WriteLine($"  Code: {rfidCode}");
        System.Diagnostics.Debug.WriteLine($"  Employee: {employeeName ?? "(Unknown)"}");
        System.Diagnostics.Debug.WriteLine($"  Error: {errorMessage}");
        System.Diagnostics.Debug.WriteLine($"  Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        System.Diagnostics.Debug.WriteLine("???????????????????????????????????????");
    }

    /// <summary>
    /// Internal method to log attempt to database
    /// </summary>
    private async Task LogAttemptAsync(RfidAttemptLog log)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            await connection.ExecuteAsync(@"
                INSERT INTO rfid_attempt_logs (
                    rfid_code,
                    attempt_time,
                    attempt_date,
                    attempt_status,
                    employee_id,
                    employee_name,
                    error_message,
                    block_reason
                )
                VALUES (
                    @RfidCode,
                    @AttemptTime,
                    @AttemptDate,
                    @AttemptStatus,
                    @EmployeeId,
                    @EmployeeName,
                    @ErrorMessage,
                    @BlockReason
                )
            ", new
            {
                log.RfidCode,
                AttemptTime = DateTime.Now,
                AttemptDate = DateTime.Now.Date,
                log.AttemptStatus,
                log.EmployeeId,
                log.EmployeeName,
                log.ErrorMessage,
                log.BlockReason
            });
        }
        catch (Exception ex)
        {
            // Don't throw - logging failures shouldn't break the main flow
            System.Diagnostics.Debug.WriteLine($"?? Failed to log RFID attempt: {ex.Message}");
        }
    }

    /// <summary>
    /// Get recent RFID attempts (for monitoring/debugging)
    /// </summary>
    public async Task<List<RfidAttemptLog>> GetRecentAttemptsAsync(int limit = 50)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var logs = await connection.QueryAsync<RfidAttemptLog>(@"
                SELECT 
                    attempt_id as AttemptId,
                    rfid_code as RfidCode,
                    attempt_time as AttemptTime,
                    attempt_date as AttemptDate,
                    attempt_status as AttemptStatus,
                    employee_id as EmployeeId,
                    employee_name as EmployeeName,
                    error_message as ErrorMessage,
                    block_reason as BlockReason,
                    created_at as CreatedAt
                FROM rfid_attempt_logs
                ORDER BY attempt_time DESC
                LIMIT @Limit
            ", new { Limit = limit });

            return logs.ToList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"?? Failed to retrieve RFID attempts: {ex.Message}");
            return new List<RfidAttemptLog>();
        }
    }
}

/// <summary>
/// Model for RFID attempt log
/// </summary>
public class RfidAttemptLog
{
    public long AttemptId { get; set; }
    public string RfidCode { get; set; } = string.Empty;
    public DateTime AttemptTime { get; set; }
    public DateTime AttemptDate { get; set; }
    public string AttemptStatus { get; set; } = string.Empty; // SUCCESS, UNKNOWN_RFID, INACTIVE_EMPLOYEE, BLOCKED, DUPLICATE, ERROR
    public int? EmployeeId { get; set; }
    public string? EmployeeName { get; set; }
    public string? ErrorMessage { get; set; }
    public string? BlockReason { get; set; }
    public DateTime CreatedAt { get; set; }
}
