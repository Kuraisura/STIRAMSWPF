using System;
using System.Threading.Tasks;
using Npgsql;
using Dapper;
using RAMSOfficial.Models;

namespace RAMSOfficial.Services;

/// <summary>
/// Validates attendance based on approved leave requests
/// PRIORITY: HOLIDAY ? LEAVE ? EXAM ? CLASS
/// </summary>
public class LeaveValidationService
{
    private readonly string _connectionString;

    public LeaveValidationService(string connectionString)
    {
        _connectionString = connectionString;
    }

    /// <summary>
    /// Check if employee has approved leave for the given date
    /// This is PRIORITY 2 (after Holiday, before Exam/Class)
    /// </summary>
    public async Task<LeaveValidationResult> ValidateLeaveStatusAsync(
        int employeeId,
        DateTime checkTime)
    {
        var result = new LeaveValidationResult
        {
            IsAllowed = true,
            CheckDate = checkTime.Date,
            EmployeeId = employeeId
        };

        try
        {
            // Pre-flight: skip database if offline
            if (!DatabaseConnectionGuard.IsOnline())
            {
                System.Diagnostics.Debug.WriteLine($"  ⚠ OFFLINE — skipping leave validation (allowing attendance)");
                return result;
            }

            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            System.Diagnostics.Debug.WriteLine($"");
            System.Diagnostics.Debug.WriteLine($"???????????????????????????????????????????????");
            System.Diagnostics.Debug.WriteLine($"  ?? LEAVE VALIDATION CHECK (PRIORITY 2)");
            System.Diagnostics.Debug.WriteLine($"  Employee ID: {employeeId}");
            System.Diagnostics.Debug.WriteLine($"  Date: {checkTime:yyyy-MM-dd}");
            System.Diagnostics.Debug.WriteLine($"???????????????????????????????????????????????");

            // ???????????????????????????????????????????????
            // Query for APPROVED leave on this specific date
            // ???????????????????????????????????????????????
            var leaveRequest = await connection.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT 
                    request_id as RequestId,
                    employee_id as EmployeeId,
                    request_type as RequestType,
                    requested_time as RequestedTime,
                    reason as Reason,
                    status as Status,
                    schedule_date as ScheduleDate,
                    time_start as TimeStart,
                    time_end as TimeEnd,
                    supporting_documents as SupportingDocuments,
                    requested_at as RequestedAt,
                    reviewed_by as ReviewedBy,
                    reviewed_at as ReviewedAt
                FROM verification_requests
                WHERE employee_id = @EmployeeId
                    AND request_type = 'leave'
                    AND status = 'approved'
                    AND DATE(requested_time) = @CheckDate
                ORDER BY requested_at DESC
                LIMIT 1
            ", new { 
                EmployeeId = employeeId, 
                CheckDate = checkTime.Date 
            });

            if (leaveRequest != null)
            {
                // ? APPROVED LEAVE FOUND - Block attendance
                result.IsAllowed = false;
                result.HasApprovedLeave = true;
                result.LeaveRequestId = leaveRequest.RequestId;
                result.LeaveReason = leaveRequest.Reason;
                result.LeaveStartTime = leaveRequest.TimeStart;
                result.LeaveEndTime = leaveRequest.TimeEnd;
                result.RequestedTime = leaveRequest.RequestedTime;
                result.ReviewedBy = leaveRequest.ReviewedBy;
                result.ReviewedAt = leaveRequest.ReviewedAt;
                result.Reason = "APPROVED_LEAVE";
                result.Message = "Leave Request Approved";
                result.Notes = $"This employee has approved leave for {checkTime:yyyy-MM-dd}.";

                System.Diagnostics.Debug.WriteLine($"");
                System.Diagnostics.Debug.WriteLine($"  ? APPROVED LEAVE FOUND - ATTENDANCE BLOCKED");
                System.Diagnostics.Debug.WriteLine($"     Request ID: {result.LeaveRequestId}");
                System.Diagnostics.Debug.WriteLine($"     Leave Date: {checkTime:yyyy-MM-dd}");
                System.Diagnostics.Debug.WriteLine($"     Reason: {result.LeaveReason}");
                
                if (result.LeaveStartTime.HasValue && result.LeaveEndTime.HasValue)
                {
                    System.Diagnostics.Debug.WriteLine($"     Duration: {result.LeaveStartTime:hh\\:mm} - {result.LeaveEndTime:hh\\:mm}");
                }
                
                System.Diagnostics.Debug.WriteLine($"     Reviewed: {result.ReviewedAt:yyyy-MM-dd HH:mm:ss}");
                System.Diagnostics.Debug.WriteLine($"     ? No attendance will be recorded");

                return result;
            }

            // ? NO APPROVED LEAVE - Attendance allowed (continue to next validation)
            System.Diagnostics.Debug.WriteLine($"  ? No approved leave found - Continue validation");
            
            return result;
        }
        catch (Exception ex) when (
            ex is System.Net.Sockets.SocketException ||
            ex is NpgsqlException ||
            ex.InnerException is System.Net.Sockets.SocketException ||
            ex.InnerException is NpgsqlException)
        {
            System.Diagnostics.Debug.WriteLine($"  ⚠ Connection error (offline fallback): {ex.Message}");
            result.IsAllowed = true; // fail-safe: allow attendance
            return result;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"  ? ERROR checking leave status: {ex.Message}");

            // On error, allow attendance to continue (fail-safe)
            result.IsAllowed = true;
            return result;
        }
    }
}

/// <summary>
/// Result of leave validation
/// </summary>
public class LeaveValidationResult
{
    public bool IsAllowed { get; set; }
    public int EmployeeId { get; set; }
    public DateTime CheckDate { get; set; }
    public bool HasApprovedLeave { get; set; }
    
    // Leave Request Details
    public long? LeaveRequestId { get; set; }
    public string? LeaveReason { get; set; }
    public TimeSpan? LeaveStartTime { get; set; }
    public TimeSpan? LeaveEndTime { get; set; }
    public DateTime? RequestedTime { get; set; }
    public int? ReviewedBy { get; set; }
    public DateTime? ReviewedAt { get; set; }
    
    // Validation Results
    public string? Reason { get; set; }
    public string? Message { get; set; }
    public string? Notes { get; set; }
}
