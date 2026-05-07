using Dapper;
using Npgsql;
using System;
using System.Diagnostics;

namespace RAMSOfficial.Services;

/// <summary>
/// Service to validate and handle MISSED LOG verification requests
/// When an employee has an approved missed_log request, this service:
/// 1. Checks if there's an approved request for the current date
/// 2. Returns the requested_time from the verification request
/// 3. This time will OVERRIDE the actual tap time in attendance recording
/// </summary>
public class MissedLogValidationService
{
    private readonly string _connectionString;

    public MissedLogValidationService(string connectionString)
    {
        _connectionString = connectionString;
    }

    /// <summary>
    /// Check if employee has an approved missed_log request for the current date/time
    /// </summary>
    public async Task<MissedLogValidationResult> ValidateMissedLogAsync(
        int employeeId,
        DateTime currentTime,
        string tapType)
    {
        var result = new MissedLogValidationResult
        {
            EmployeeId = employeeId,
            TapType = tapType,
            ActualTapTime = currentTime,
            HasApprovedMissedLog = false,
            ShouldOverrideTime = false
        };

        try
        {
            // Pre-flight: skip database if offline
            if (!DatabaseConnectionGuard.IsOnline())
            {
                Debug.WriteLine("  ⚠ OFFLINE — skipping missed log check");
                return result;
            }

            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            Debug.WriteLine("");
            Debug.WriteLine("???????????????????????????????????????");
            Debug.WriteLine($"  ?? CHECKING MISSED LOG REQUESTS");
            Debug.WriteLine($"  Employee ID: {employeeId}");
            Debug.WriteLine($"  Current Time: {currentTime:yyyy-MM-dd HH:mm:ss}");
            Debug.WriteLine($"  Tap Type: {tapType}");
            Debug.WriteLine("???????????????????????????????????????");

            // Query for approved missed_log requests for this employee and date
            var missedLogRequest = await connection.QueryFirstOrDefaultAsync<MissedLogRequestDto>(@"
                SELECT 
                    request_id as RequestId,
                    employee_id as EmployeeId,
                    request_type as RequestType,
                    requested_time as RequestedTime,
                    log_type as LogType,
                    reason as Reason,
                    supporting_documents as SupportingDocuments,
                    status as Status,
                    reviewed_by as ReviewedBy,
                    reviewed_at as ReviewedAt,
                    review_notes as ReviewNotes
                FROM verification_requests
                WHERE employee_id = @EmployeeId
                    AND request_type = 'missed_log'
                    AND status = 'approved'
                    AND DATE(requested_time) = @RequestDate
                    AND log_type = @LogType
                ORDER BY requested_at DESC
                LIMIT 1
            ", new
            {
                EmployeeId = employeeId,
                RequestDate = currentTime.Date,
                LogType = tapType
            });

            if (missedLogRequest != null)
            {
                // ? APPROVED MISSED LOG FOUND!
                result.HasApprovedMissedLog = true;
                result.ShouldOverrideTime = true;
                result.RequestId = missedLogRequest.RequestId;
                result.RequestedTime = missedLogRequest.RequestedTime;
                result.Reason = missedLogRequest.Reason;
                result.ReviewedBy = missedLogRequest.ReviewedBy;
                result.ReviewedAt = missedLogRequest.ReviewedAt;
                result.ReviewNotes = missedLogRequest.ReviewNotes;

                Debug.WriteLine("");
                Debug.WriteLine("? APPROVED MISSED LOG FOUND!");
                Debug.WriteLine($"   Request ID: {result.RequestId}");
                Debug.WriteLine($"   Reason: {result.Reason}");
                Debug.WriteLine($"   Requested Time: {result.RequestedTime:yyyy-MM-dd HH:mm:ss}");
                Debug.WriteLine($"   Actual Tap Time: {currentTime:yyyy-MM-dd HH:mm:ss}");
                Debug.WriteLine($"   Reviewed At: {result.ReviewedAt:yyyy-MM-dd HH:mm:ss}");
                Debug.WriteLine("");
                Debug.WriteLine("? TIME OVERRIDE ENABLED:");
                Debug.WriteLine($"   Will record as: {result.RequestedTime:yyyy-MM-dd HH:mm:ss}");
                Debug.WriteLine($"   Instead of: {currentTime:yyyy-MM-dd HH:mm:ss}");
                Debug.WriteLine($"   Status will be calculated based on requested_time");
                Debug.WriteLine("???????????????????????????????????????");

                return result;
            }

            // ? NO APPROVED MISSED LOG FOUND
            Debug.WriteLine("? No approved missed log request found");
            Debug.WriteLine($"   Will use actual tap time: {currentTime:yyyy-MM-dd HH:mm:ss}");
            Debug.WriteLine("???????????????????????????????????????");

            return result;
        }
        catch (Exception ex) when (
            ex is System.Net.Sockets.SocketException ||
            ex is NpgsqlException ||
            ex.InnerException is System.Net.Sockets.SocketException ||
            ex.InnerException is NpgsqlException)
        {
            Debug.WriteLine($"⚠ OFFLINE fallback — skipping missed log: {ex.Message}");
            return result;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"?? ERROR checking missed log: {ex.Message}");
            Debug.WriteLine($"   Stack: {ex.StackTrace}");
            Debug.WriteLine("???????????????????????????????????????");

            // On error, don't override time (fail-safe)
            return result;
        }
    }
}

/// <summary>
/// DTO for missed log request data from database
/// </summary>
internal class MissedLogRequestDto
{
    public long RequestId { get; set; }
    public int EmployeeId { get; set; }
    public string RequestType { get; set; } = string.Empty;
    public DateTime? RequestedTime { get; set; }
    public string? LogType { get; set; }
    public string? Reason { get; set; }
    public string? SupportingDocuments { get; set; }
    public string Status { get; set; } = string.Empty;
    public int? ReviewedBy { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string? ReviewNotes { get; set; }
}

/// <summary>
/// Result of missed log validation
/// </summary>
public class MissedLogValidationResult
{
    public int EmployeeId { get; set; }
    public string TapType { get; set; } = string.Empty;
    public DateTime ActualTapTime { get; set; }
    
    // Missed Log Request Details
    public bool HasApprovedMissedLog { get; set; }
    public bool ShouldOverrideTime { get; set; }
    public long? RequestId { get; set; }
    public DateTime? RequestedTime { get; set; }
    public string? Reason { get; set; }
    public int? ReviewedBy { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string? ReviewNotes { get; set; }
}
