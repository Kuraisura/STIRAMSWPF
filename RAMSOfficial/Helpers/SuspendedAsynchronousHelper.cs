using System;
using System.Diagnostics;

namespace RAMSOfficial.Helpers;

/// <summary>
/// Helper for handling suspended_asynchronous (Class Suspension - Reporting) holiday logic
/// 
/// RULES:
/// - Teaching Staff (Part-Time Full Load + Regular): ADMIN TIME
/// - Non-Teaching Staff (All): CALCULATE NORMAL STATUS (Late/On Time/Undertime)
/// </summary>
public class SuspendedAsynchronousHelper
{
    /// <summary>
    /// Determines the attendance mode for an employee during a suspended_asynchronous holiday
    /// </summary>
    public static SuspendedAsyncResult DetermineAttendanceMode(
        string? staffType,
        string? employmentStatus)
    {
        var result = new SuspendedAsyncResult();

        Debug.WriteLine($"");
        Debug.WriteLine($"????????????????????????????????????????");
        Debug.WriteLine($"?? SUSPENDED_ASYNCHRONOUS HELPER");
        Debug.WriteLine($"   Staff Type: {staffType ?? "NULL"}");
        Debug.WriteLine($"   Employment Status: {employmentStatus ?? "NULL"}");
        Debug.WriteLine($"????????????????????????????????????????");

        // Validate inputs
        if (string.IsNullOrWhiteSpace(staffType))
        {
            Debug.WriteLine($"   ?? WARNING: Staff Type is NULL or empty");
            result.AttendanceMode = AttendanceMode.CalculateNormal;
            result.Reason = "Staff type unknown - defaulting to normal calculation";
            return result;
        }

        var isNonTeaching = staffType.Equals("Non-Teaching", StringComparison.OrdinalIgnoreCase);
        var isTeaching = staffType.Equals("Teaching", StringComparison.OrdinalIgnoreCase);

        Debug.WriteLine($"   Is Non-Teaching: {isNonTeaching}");
        Debug.WriteLine($"   Is Teaching: {isTeaching}");

        // ???????????????????????????????????????????????????????
        // RULE 1: NON-TEACHING STAFF (ALL)
        // ? CALCULATE NORMAL STATUS (Late/On Time/Undertime)
        // ???????????????????????????????????????????????????????
        if (isNonTeaching)
        {
            result.AttendanceMode = AttendanceMode.CalculateNormal;
            result.AttendanceStatus = null; // Will be calculated
            result.Reason = "NON_TEACHING_EXEMPT";
            result.Message = "Non-Teaching staff - work as normal";
            result.Notes = "Suspended/Asynchronous: Non-Teaching staff must follow regular schedule. Status will be On Time, Late, or Undertime.";
            result.IsExemptFromAdminTime = true;

            Debug.WriteLine($"   ? NON-TEACHING STAFF DETECTED");
            Debug.WriteLine($"   ? Attendance Mode: CALCULATE NORMAL STATUS");
            Debug.WriteLine($"   ? Status will be: On Time / Late / Undertime");
            Debug.WriteLine($"   ? NO ADMIN TIME for Non-Teaching staff");
            Debug.WriteLine($"????????????????????????????????????????");

            return result;
        }

        // ???????????????????????????????????????????????????????
        // RULE 2: TEACHING STAFF (ALL)
        // ? ADMIN TIME (Part-Time Full Load + Regular)
        // ???????????????????????????????????????????????????????
        if (isTeaching)
        {
            var isPartTimeFull = !string.IsNullOrWhiteSpace(employmentStatus) &&
                                employmentStatus.Contains("Part", StringComparison.OrdinalIgnoreCase) &&
                                employmentStatus.Contains("Full Load", StringComparison.OrdinalIgnoreCase);
            
            var isRegular = !string.IsNullOrWhiteSpace(employmentStatus) &&
                           (employmentStatus.Equals("Regular", StringComparison.OrdinalIgnoreCase) ||
                            employmentStatus.Contains("Full-Time", StringComparison.OrdinalIgnoreCase) ||
                            employmentStatus.Contains("Full Time", StringComparison.OrdinalIgnoreCase));

            Debug.WriteLine($"   Is Part-Time Full Load: {isPartTimeFull}");
            Debug.WriteLine($"   Is Regular: {isRegular}");

            // Both Part-Time Full Load and Regular teaching staff get Admin Time
            if (isPartTimeFull || isRegular)
            {
                result.AttendanceMode = AttendanceMode.AdminTime;
                result.AttendanceStatus = "admin-time";
                result.Reason = isPartTimeFull ? "PTFL_REPORTING_ADMIN_TIME" : "REGULAR_REPORTING_ADMIN_TIME";
                result.Message = "Teaching staff - reporting required";
                result.Notes = $"Suspended/Asynchronous: {employmentStatus} Teaching staff - recorded as Admin Time.";
                result.IsExemptFromAdminTime = false;

                Debug.WriteLine($"   ? TEACHING STAFF ({employmentStatus}) DETECTED");
                Debug.WriteLine($"   ? Attendance Mode: ADMIN TIME");
                Debug.WriteLine($"   ? Status: admin-time (FORCED)");
                Debug.WriteLine($"   ? Reporting staff get Admin Time");
                Debug.WriteLine($"????????????????????????????????????????");

                return result;
            }
            else
            {
                // Part-Time (not Full Load) teaching staff
                Debug.WriteLine($"   ?? PART-TIME (NOT FULL LOAD) TEACHING STAFF");
                Debug.WriteLine($"   ? Attendance Mode: CALCULATE NORMAL STATUS");
                Debug.WriteLine($"   ? Not eligible for Admin Time");

                result.AttendanceMode = AttendanceMode.CalculateNormal;
                result.AttendanceStatus = null;
                result.Reason = "PART_TIME_NOT_REPORTING";
                result.Message = "Part-Time teaching staff - no reporting required";
                result.Notes = $"Suspended/Asynchronous: {employmentStatus} - Status calculated normally.";
                result.IsExemptFromAdminTime = true;

                Debug.WriteLine($"????????????????????????????????????????");
                return result;
            }
        }

        // ???????????????????????????????????????????????????????
        // FALLBACK: Unknown Staff Type
        // ???????????????????????????????????????????????????????
        Debug.WriteLine($"   ?? UNKNOWN STAFF TYPE: {staffType}");
        Debug.WriteLine($"   ? Defaulting to: CALCULATE NORMAL STATUS");
        Debug.WriteLine($"????????????????????????????????????????");

        result.AttendanceMode = AttendanceMode.CalculateNormal;
        result.AttendanceStatus = null;
        result.Reason = "UNKNOWN_STAFF_TYPE";
        result.Message = "Unknown staff type - status calculated normally";
        result.Notes = $"Suspended/Asynchronous: Unknown staff type '{staffType}' - status calculated normally.";
        result.IsExemptFromAdminTime = true;

        return result;
    }
}

/// <summary>
/// Attendance mode for suspended_asynchronous holidays
/// </summary>
public enum AttendanceMode
{
    /// <summary>
    /// Calculate normal status (On Time/Late/Undertime) based on schedule
    /// Used for: Non-Teaching staff, Part-Time (not Full Load) teaching staff
    /// </summary>
    CalculateNormal,

    /// <summary>
    /// Force Admin Time status (no late/undertime)
    /// Used for: Part-Time Full Load teaching staff, Regular teaching staff
    /// </summary>
    AdminTime
}

/// <summary>
/// Result of suspended_asynchronous attendance determination
/// </summary>
public class SuspendedAsyncResult
{
    /// <summary>
    /// The attendance mode to use
    /// </summary>
    public AttendanceMode AttendanceMode { get; set; }

    /// <summary>
    /// The forced attendance status (admin-time) or null to calculate
    /// </summary>
    public string? AttendanceStatus { get; set; }

    /// <summary>
    /// Reason code for the decision
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// User-friendly message
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Detailed notes for the attendance log
    /// </summary>
    public string Notes { get; set; } = string.Empty;

    /// <summary>
    /// True if employee is exempt from Admin Time (Non-Teaching, Part-Time)
    /// </summary>
    public bool IsExemptFromAdminTime { get; set; }

    /// <summary>
    /// Gets a summary string for debugging
    /// </summary>
    public string GetSummary()
    {
        return AttendanceMode == AttendanceMode.AdminTime
            ? $"Admin Time - {Message}"
            : $"Calculate Normal - {Message}";
    }
}
