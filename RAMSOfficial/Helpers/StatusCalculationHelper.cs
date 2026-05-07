using System;
using System.Diagnostics;

namespace RAMSOfficial.Helpers;

/// <summary>
/// Centralized helper for calculating attendance status across all priority schedules.
/// Ensures precise and accurate status determination for:
/// - Class Schedules (Priority 5)
/// - Exam Schedules (Priority 4)
/// - Leave/Suspensions (Priority 2)
/// - Holidays (Priority 1)
/// - Substitutions (Priority 3)
/// - Regular Schedules (Priority 6)
/// </summary>
public class StatusCalculationHelper
{
    /// <summary>
    /// Result of status calculation with detailed information
    /// </summary>
    public class StatusResult
    {
        public string Status { get; set; } = "on-time";
        public bool IsLate { get; set; }
        public bool IsEarlyOut { get; set; }
        public int? LateMinutes { get; set; }
        public int? UndertimeMinutes { get; set; }
        public string Notes { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
    }

    /// <summary>
    /// Calculate status for a TIME IN tap
    /// NO GRACE PERIOD - Any lateness is marked as LATE
    /// </summary>
    public static StatusResult CalculateTimeInStatus(
        TimeSpan actualTime,
        TimeSpan expectedTime,
        string scheduleType,
        string scheduleDetails)
    {
        if (expectedTime == TimeSpan.Zero)
        {
            return new StatusResult
            {
                Status = "on-time",
                Notes = "Schedule start time not configured",
                Reason = $"{scheduleType}: Missing expected time"
            };
        }

        // Truncate seconds so that 11:59:59 is still the same minute as 11:59:00
        actualTime = new TimeSpan(actualTime.Hours, actualTime.Minutes, 0);
        expectedTime = new TimeSpan(expectedTime.Hours, expectedTime.Minutes, 0);

        var result = new StatusResult
        {
            Status = "on-time",
            Notes = "On time",
            Reason = $"{scheduleType}: Tapped at expected time"
        };

        Debug.WriteLine($"");
        Debug.WriteLine($"???????????????????????????????????????????");
        Debug.WriteLine($"?? STATUS CALCULATION - TIME IN");
        Debug.WriteLine($"   Schedule Type: {scheduleType}");
        Debug.WriteLine($"   Schedule: {scheduleDetails}");
        Debug.WriteLine($"   Expected IN: {FormatTimeSpan(expectedTime)}");
        Debug.WriteLine($"   Actual Tap: {FormatTimeSpan(actualTime)}");

        var difference = actualTime - expectedTime;
        Debug.WriteLine($"   Time Difference: {difference.TotalMinutes:F2} minutes");

        // Handle cross-day scenarios (tapping very late, e.g., 7:45 PM when expected 7:00 AM)
        if (difference.TotalHours > 12)
        {
            var lateTotalMinutes = (int)difference.TotalMinutes;
            result.IsLate = true;
            result.LateMinutes = lateTotalMinutes;
            result.Status = "late";
            result.Notes = $"Late by {result.LateMinutes} minutes";
            result.Reason = $"{scheduleType}: Cross-day late arrival";

            Debug.WriteLine($"   ?? CROSS-DAY LATE DETECTION");
            Debug.WriteLine($"   Late Minutes: {result.LateMinutes}");
            Debug.WriteLine($"   ? Result: LATE");
        }
        // NO GRACE PERIOD - Any amount late is marked as LATE
        else if (difference.TotalMinutes > 0)
        {
            result.IsLate = true;
            result.LateMinutes = (int)difference.TotalMinutes;
            result.Status = "late";
            result.Notes = $"Late by {result.LateMinutes} minutes";
            result.Reason = $"{scheduleType}: Tapped after expected time";

            Debug.WriteLine($"   ?? LATE (NO GRACE PERIOD)");
            Debug.WriteLine($"   Late Minutes: {result.LateMinutes}");
            Debug.WriteLine($"   ? Result: LATE");
        }
        else
        {
            // On time or early
            result.Status = "on-time";
            result.Notes = "On time";
            result.Reason = $"{scheduleType}: Tapped on time or early";

            Debug.WriteLine($"   ? ON TIME");
            if (difference.TotalMinutes < 0)
            {
                Debug.WriteLine($"   Early by {Math.Abs(difference.TotalMinutes):F2} minutes");
            }
        }

        Debug.WriteLine($"???????????????????????????????????????????");
        return result;
    }

    /// <summary>
    /// Calculate status for a TIME OUT tap
    /// NO GRACE PERIOD - Any early departure is marked as UNDERTIME
    /// </summary>
    public static StatusResult CalculateTimeOutStatus(
        TimeSpan actualTime,
        TimeSpan expectedTime,
        string scheduleType,
        string scheduleDetails)
    {
        if (expectedTime == TimeSpan.Zero)
        {
            return new StatusResult
            {
                Status = "on-time",
                Notes = "Schedule end time not configured",
                Reason = $"{scheduleType}: Missing expected time"
            };
        }

        // Truncate seconds so that 11:59:59 is still the same minute as 11:59:00
        actualTime = new TimeSpan(actualTime.Hours, actualTime.Minutes, 0);
        expectedTime = new TimeSpan(expectedTime.Hours, expectedTime.Minutes, 0);

        var result = new StatusResult
        {
            Status = "on-time",
            Notes = "Completed shift",
            Reason = $"{scheduleType}: Tapped at expected time"
        };

        Debug.WriteLine($"");
        Debug.WriteLine($"???????????????????????????????????????????");
        Debug.WriteLine($"?? STATUS CALCULATION - TIME OUT");
        Debug.WriteLine($"   Schedule Type: {scheduleType}");
        Debug.WriteLine($"   Schedule: {scheduleDetails}");
        Debug.WriteLine($"   Expected OUT: {FormatTimeSpan(expectedTime)}");
        Debug.WriteLine($"   Actual Tap: {FormatTimeSpan(actualTime)}");

        var difference = expectedTime - actualTime;
        Debug.WriteLine($"   Time Difference: {difference.TotalMinutes:F2} minutes");

        // Handle cross-day scenarios (tapping very late next day)
        if (difference.TotalHours < -12)
        {
            // This means they worked overtime, not undertime
            result.Status = "on-time";
            result.Notes = "Overtime - completed shift";
            result.Reason = $"{scheduleType}: Worked overtime";

            Debug.WriteLine($"   ?? CROSS-DAY OVERTIME DETECTION");
            Debug.WriteLine($"   ? Result: ON TIME (Overtime)");
        }
        // NO GRACE PERIOD - Any amount early is marked as UNDERTIME
        else if (difference.TotalMinutes > 0)
        {
            result.IsEarlyOut = true;
            result.UndertimeMinutes = (int)difference.TotalMinutes;
            result.Status = "undertime";
            result.Notes = $"Undertime by {result.UndertimeMinutes} minutes";
            result.Reason = $"{scheduleType}: Tapped before expected time";

            Debug.WriteLine($"   ?? UNDERTIME (NO GRACE PERIOD)");
            Debug.WriteLine($"   Undertime Minutes: {result.UndertimeMinutes}");
            Debug.WriteLine($"   ? Result: UNDERTIME");
        }
        else
        {
            // Tapped out at or after expected time
            result.Status = "on-time";
            result.Notes = "Completed shift";
            result.Reason = $"{scheduleType}: Completed shift on time";

            Debug.WriteLine($"   ? ON TIME (Completed Shift)");
            if (difference.TotalMinutes < 0)
            {
                Debug.WriteLine($"   Worked {Math.Abs(difference.TotalMinutes):F2} minutes extra");
            }
        }

        Debug.WriteLine($"???????????????????????????????????????????");
        return result;
    }

    /// <summary>
    /// Calculate status for Holiday/Suspension scenarios (Priority 1)
    /// </summary>
    public static StatusResult CalculateHolidayStatus(
        string holidayType,
        string holidayName,
        bool isTeaching,
        bool isNonTeaching,
        string employmentStatus,
        TimeSpan? actualTime,
        TimeSpan? expectedTimeIn,
        TimeSpan? expectedTimeOut,
        string tapType)
    {
        Debug.WriteLine($"");
        Debug.WriteLine($"???????????????????????????????????????????");
        Debug.WriteLine($"??? HOLIDAY STATUS CALCULATION");
        Debug.WriteLine($"   Holiday: {holidayName}");
        Debug.WriteLine($"   Type: {holidayType}");
        Debug.WriteLine($"   Staff Type: {(isTeaching ? "Teaching" : isNonTeaching ? "Non-Teaching" : "Unknown")}");
        Debug.WriteLine($"   Employment: {employmentStatus}");
        Debug.WriteLine($"   Tap Type: {tapType}");

        var result = new StatusResult();

        // Handle suspended_asynchronous (Class Suspension - Reporting)
        if (holidayType?.ToLowerInvariant() == "suspended_asynchronous")
        {
            var suspendedResult = SuspendedAsynchronousHelper.DetermineAttendanceMode(
                isTeaching ? "Teaching" : "Non-Teaching",
                employmentStatus);

            if (suspendedResult.AttendanceMode == AttendanceMode.AdminTime)
            {
                result.Status = "admin-time";
                result.Notes = suspendedResult.Notes;
                result.Reason = suspendedResult.Reason;

                Debug.WriteLine($"   ? Result: ADMIN TIME");
                Debug.WriteLine($"   Reason: {suspendedResult.Reason}");
            }
            else
            {
                // Non-Teaching staff - calculate normal status
                if (actualTime.HasValue && expectedTimeIn.HasValue && expectedTimeOut.HasValue)
                {
                    if (tapType == "IN")
                    {
                        result = CalculateTimeInStatus(actualTime.Value, expectedTimeIn.Value, 
                            "Holiday (Suspended Async)", holidayName);
                    }
                    else
                    {
                        result = CalculateTimeOutStatus(actualTime.Value, expectedTimeOut.Value, 
                            "Holiday (Suspended Async)", holidayName);
                    }
                    result.Notes = $"{suspendedResult.Notes} {result.Notes}".Trim();
                }
                else
                {
                    result.Status = "on-time";
                    result.Notes = suspendedResult.Notes;
                    result.Reason = "No schedule available for calculation";
                }
            }
        }
        // Handle online_class
        else if (holidayType?.ToLowerInvariant() == "online_class")
        {
            var isPartTimeFull = employmentStatus?.Contains("Part", StringComparison.OrdinalIgnoreCase) == true &&
                                employmentStatus?.Contains("Full Load", StringComparison.OrdinalIgnoreCase) == true;

            if (isNonTeaching)
            {
                // Non-Teaching staff - calculate normal status
                if (actualTime.HasValue && expectedTimeIn.HasValue && expectedTimeOut.HasValue)
                {
                    if (tapType == "IN")
                    {
                        result = CalculateTimeInStatus(actualTime.Value, expectedTimeIn.Value, 
                            "Holiday (Online Class)", holidayName);
                    }
                    else
                    {
                        result = CalculateTimeOutStatus(actualTime.Value, expectedTimeOut.Value, 
                            "Holiday (Online Class)", holidayName);
                    }
                    result.Notes = $"Online class: {holidayName} - Non-Teaching staff. {result.Notes}".Trim();
                }
                else
                {
                    result.Status = "on-time";
                    result.Notes = $"Online class: {holidayName} - Non-Teaching staff";
                    result.Reason = "No schedule available for calculation";
                }
            }
            else if (isPartTimeFull)
            {
                result.Status = "admin-time";
                result.Notes = $"Online class: {holidayName} - Part-Time Full Load - Admin Time";
                result.Reason = "Part-Time Full Load during online class";

                Debug.WriteLine($"   ? Result: ADMIN TIME (Part-Time Full Load)");
            }
            else
            {
                // Regular Teaching - calculate normal status
                if (actualTime.HasValue && expectedTimeIn.HasValue && expectedTimeOut.HasValue)
                {
                    if (tapType == "IN")
                    {
                        result = CalculateTimeInStatus(actualTime.Value, expectedTimeIn.Value, 
                            "Holiday (Online Class)", holidayName);
                    }
                    else
                    {
                        result = CalculateTimeOutStatus(actualTime.Value, expectedTimeOut.Value, 
                            "Holiday (Online Class)", holidayName);
                    }
                    result.Notes = $"Online class: {holidayName}. {result.Notes}".Trim();
                }
                else
                {
                    result.Status = "on-time";
                    result.Notes = $"Online class: {holidayName}";
                    result.Reason = "No schedule available for calculation";
                }
            }
        }
        // Handle regular holidays with affects_attendance
        else
        {
            if (isNonTeaching)
            {
                // Non-Teaching staff - calculate normal status
                if (actualTime.HasValue && expectedTimeIn.HasValue && expectedTimeOut.HasValue)
                {
                    if (tapType == "IN")
                    {
                        result = CalculateTimeInStatus(actualTime.Value, expectedTimeIn.Value, 
                            "Holiday", holidayName);
                    }
                    else
                    {
                        result = CalculateTimeOutStatus(actualTime.Value, expectedTimeOut.Value, 
                            "Holiday", holidayName);
                    }
                    result.Notes = $"Holiday: {holidayName} - Non-Teaching staff. {result.Notes}".Trim();
                }
                else
                {
                    result.Status = "on-time";
                    result.Notes = $"Holiday: {holidayName} - Non-Teaching staff";
                    result.Reason = "No schedule available for calculation";
                }
            }
            else
            {
                result.Status = "admin-time";
                result.Notes = $"Holiday: {holidayName} - No attendance required";
                result.Reason = "Teaching staff on holiday";

                Debug.WriteLine($"   ? Result: ADMIN TIME (Teaching staff on holiday)");
            }
        }

        Debug.WriteLine($"   Final Status: {result.Status.ToUpper()}");
        Debug.WriteLine($"???????????????????????????????????????????");
        return result;
    }

    /// <summary>
    /// Calculate status for Leave/Missed Log scenarios (Priority 2)
    /// </summary>
    public static StatusResult CalculateLeaveStatus(
        string requestType,
        string reason)
    {
        Debug.WriteLine($"");
        Debug.WriteLine($"???????????????????????????????????????????");
        Debug.WriteLine($"?? LEAVE/MISSED LOG STATUS");
        Debug.WriteLine($"   Request Type: {requestType}");
        Debug.WriteLine($"   Reason: {reason}");

        var result = new StatusResult();

        switch (requestType?.ToLowerInvariant())
        {
            case "leave":
                result.Status = "admin-time";
                result.Notes = $"On leave: {reason} - Recorded as admin time";
                result.Reason = "Approved leave request";
                Debug.WriteLine($"   ? Result: ADMIN TIME (Leave)");
                break;

            case "missed_log":
                result.Status = "on-time";
                result.Notes = $"Missed log verified: {reason}";
                result.Reason = "Approved missed log request";
                Debug.WriteLine($"   ? Result: ON TIME (Missed log verified)");
                break;

            case "time_correction":
                result.Status = "on-time";
                result.Notes = "Time correction applied";
                result.Reason = "Approved time correction";
                Debug.WriteLine($"   ? Result: ON TIME (Time correction)");
                break;

            default:
                result.Status = "admin-time";
                result.Notes = $"Verification request: {requestType}";
                result.Reason = "Other verification request";
                Debug.WriteLine($"   ? Result: ADMIN TIME (Other verification)");
                break;
        }

        Debug.WriteLine($"???????????????????????????????????????????");
        return result;
    }

    /// <summary>
    /// Calculate status for Substitution scenarios (Priority 3)
    /// </summary>
    public static StatusResult CalculateSubstitutionStatus(
        TimeSpan actualTime,
        TimeSpan startTime,
        TimeSpan endTime,
        string tapType,
        string subjectName,
        string originalEmployeeName)
    {
        Debug.WriteLine($"");
        Debug.WriteLine($"???????????????????????????????????????????");
        Debug.WriteLine($"?? SUBSTITUTION STATUS");
        Debug.WriteLine($"   Substituting for: {originalEmployeeName}");
        Debug.WriteLine($"   Subject: {subjectName}");
        Debug.WriteLine($"   Schedule: {FormatTimeSpan(startTime)} - {FormatTimeSpan(endTime)}");
        Debug.WriteLine($"   Tap Type: {tapType}");
        Debug.WriteLine($"   Actual Time: {FormatTimeSpan(actualTime)}");

        var result = new StatusResult();

        if (tapType == "IN")
        {
            result = CalculateTimeInStatus(actualTime, startTime, 
                "Substitution", $"{subjectName} for {originalEmployeeName}");
        }
        else
        {
            result = CalculateTimeOutStatus(actualTime, endTime, 
                "Substitution", $"{subjectName} for {originalEmployeeName}");
        }

        result.Notes = $"Substituting for {originalEmployeeName} - {subjectName}. {result.Notes}".Trim();

        Debug.WriteLine($"   Final Status: {result.Status.ToUpper()}");
        Debug.WriteLine($"???????????????????????????????????????????");
        return result;
    }

    /// <summary>
    /// Calculate status for Exam schedules (Priority 4)
    /// </summary>
    public static StatusResult CalculateExamStatus(
        TimeSpan actualTime,
        TimeSpan startTime,
        TimeSpan endTime,
        string tapType,
        string subjectName,
        string section)
    {
        Debug.WriteLine($"");
        Debug.WriteLine($"???????????????????????????????????????????");
        Debug.WriteLine($"?? EXAM STATUS");
        Debug.WriteLine($"   Exam: {subjectName} - {section}");
        Debug.WriteLine($"   Schedule: {FormatTimeSpan(startTime)} - {FormatTimeSpan(endTime)}");
        Debug.WriteLine($"   Tap Type: {tapType}");
        Debug.WriteLine($"   Actual Time: {FormatTimeSpan(actualTime)}");

        var result = new StatusResult();

        if (tapType == "IN")
        {
            result = CalculateTimeInStatus(actualTime, startTime, 
                "Exam", $"{subjectName} - {section}");
        }
        else
        {
            result = CalculateTimeOutStatus(actualTime, endTime, 
                "Exam", $"{subjectName} - {section}");
        }

        Debug.WriteLine($"   Final Status: {result.Status.ToUpper()}");
        Debug.WriteLine($"???????????????????????????????????????????");
        return result;
    }

    /// <summary>
    /// Calculate status for Class schedules (Priority 5)
    /// </summary>
    public static StatusResult CalculateClassStatus(
        TimeSpan actualTime,
        TimeSpan startTime,
        TimeSpan endTime,
        string tapType,
        string subjectName,
        string section)
    {
        Debug.WriteLine($"");
        Debug.WriteLine($"???????????????????????????????????????????");
        Debug.WriteLine($"?? CLASS STATUS");
        Debug.WriteLine($"   Class: {subjectName} - {section}");
        Debug.WriteLine($"   Schedule: {FormatTimeSpan(startTime)} - {FormatTimeSpan(endTime)}");
        Debug.WriteLine($"   Tap Type: {tapType}");
        Debug.WriteLine($"   Actual Time: {FormatTimeSpan(actualTime)}");

        var result = new StatusResult();

        if (tapType == "IN")
        {
            result = CalculateTimeInStatus(actualTime, startTime, 
                "Class", $"{subjectName} - {section}");
        }
        else
        {
            result = CalculateTimeOutStatus(actualTime, endTime, 
                "Class", $"{subjectName} - {section}");
        }

        Debug.WriteLine($"   Final Status: {result.Status.ToUpper()}");
        Debug.WriteLine($"???????????????????????????????????????????");
        return result;
    }

    /// <summary>
    /// Calculate status for Regular schedules (Priority 6 - Fallback)
    /// </summary>
    public static StatusResult CalculateRegularStatus(
        TimeSpan actualTime,
        TimeSpan scheduleTimeIn,
        TimeSpan scheduleTimeOut,
        string tapType,
        string employeeName)
    {
        Debug.WriteLine($"");
        Debug.WriteLine($"???????????????????????????????????????????");
        Debug.WriteLine($"? REGULAR STATUS");
        Debug.WriteLine($"   Employee: {employeeName}");
        Debug.WriteLine($"   Regular Schedule: {FormatTimeSpan(scheduleTimeIn)} - {FormatTimeSpan(scheduleTimeOut)}");
        Debug.WriteLine($"   Tap Type: {tapType}");
        Debug.WriteLine($"   Actual Time: {FormatTimeSpan(actualTime)}");

        var result = new StatusResult();

        // Validate schedule times
        if (scheduleTimeIn == TimeSpan.Zero && scheduleTimeOut == TimeSpan.Zero)
        {
            Debug.WriteLine($"   ?? WARNING: No schedule defined");
            result.Status = "on-time";
            result.Notes = "No schedule defined - cannot calculate late/undertime status";
            result.Reason = "Missing schedule configuration";
            Debug.WriteLine($"   ? Defaulting to: ON TIME");
        }
        else
        {
            if (tapType == "IN")
            {
                result = CalculateTimeInStatus(actualTime, scheduleTimeIn, 
                    "Regular", "Regular work hours");
            }
            else
            {
                result = CalculateTimeOutStatus(actualTime, scheduleTimeOut, 
                    "Regular", "Regular work hours");
            }
        }

        Debug.WriteLine($"   Final Status: {result.Status.ToUpper()}");
        Debug.WriteLine($"???????????????????????????????????????????");
        return result;
    }

    /// <summary>
    /// Helper to format TimeSpan for display
    /// </summary>
    private static string FormatTimeSpan(TimeSpan time)
    {
        var dateTime = DateTime.Today.Add(time);
        return dateTime.ToString("h:mm tt");
    }
}
