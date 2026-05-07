using RAMSOfficial.Models;
using Npgsql;
using Dapper;
using RAMSOfficial.Helpers;

namespace RAMSOfficial.Services;

/// <summary>
/// Handles all attendance validation logic with proper priority ordering:
/// 1. Holiday/Suspension (Highest Priority)
/// 2. Leave/Missed Log/Verification
/// 3. Substitutions
/// 4. Exam Schedules
/// 5. Class Schedules
/// 6. Regular Schedule (Lowest Priority)
/// 
/// Now uses centralized StatusCalculationHelper for precise status calculations
/// </summary>
public class AttendanceValidationService
{
    private readonly string _connectionString;
    private readonly AcademicTermFilterHelper _termFilterHelper;

    public AttendanceValidationService(string connectionString)
    {
        _connectionString = connectionString;
        _termFilterHelper = new AcademicTermFilterHelper(connectionString);
    }

    /// <summary>
    /// Helper method to format TimeSpan to 12-hour format with AM/PM
    /// </summary>
    private static string FormatTimeSpan(TimeSpan time)
    {
        var dateTime = DateTime.Today.Add(time);
        return dateTime.ToString("h:mm tt");
    }

    /// <summary>
    /// Helper method to format TimeSpan to detailed format with AM/PM for debugging
    /// </summary>
    private static string FormatTimeSpanDetailed(TimeSpan time)
    {
        var dateTime = DateTime.Today.Add(time);
        return dateTime.ToString("h:mm:ss tt");
    }

    /// <summary>
    /// Master validation method - checks all conditions in priority order
    /// PRIORITY ORDER (as specified):
    /// 1. Holiday/Suspension (Highest Priority)
    /// 2. Leave/Missed Log/Verification
    /// 3. Substitutions
    /// 4. Exam Schedule
    /// 5. Class Schedule
    /// 6. Regular (Lowest Priority)
    /// </summary>
    public async Task<AttendanceValidationResult> ValidateAttendanceAsync(
        int employeeId, 
        DateTime tapTime, 
        string tapType)
    {
        var result = new AttendanceValidationResult
        {
            IsValid = true,
            TapTime = tapTime,
            TapType = tapType,
            EmployeeId = employeeId
        };

        // Pre-flight: if offline, return a basic valid result
        if (!DatabaseConnectionGuard.IsOnline())
        {
            System.Diagnostics.Debug.WriteLine($"  ⚠ OFFLINE — skipping comprehensive validation");
            result.IsValid = true;
            result.AttendanceStatus = "pending";
            result.Notes = "Validated offline — will re-validate on sync";
            return result;
        }

        try
        {

        System.Diagnostics.Debug.WriteLine($"");
        System.Diagnostics.Debug.WriteLine($"????????????????????????????????????????????");
        System.Diagnostics.Debug.WriteLine($"?? ATTENDANCE VALIDATION STARTED");
        System.Diagnostics.Debug.WriteLine($"   Employee ID: {employeeId}");
        System.Diagnostics.Debug.WriteLine($"   Tap Time: {tapTime:yyyy-MM-dd h:mm:ss tt}");
        System.Diagnostics.Debug.WriteLine($"   Tap Type: {tapType}");
        System.Diagnostics.Debug.WriteLine($"????????????????????????????????????????????");

        // SUB-PRIORITY 0: Get Active Academic Term (Filter schedules by term)
        System.Diagnostics.Debug.WriteLine($"");
        System.Diagnostics.Debug.WriteLine($"?? SUB-PRIORITY 0: Getting Active Academic Term...");
        var activeTerm = await _termFilterHelper.GetActiveAcademicTermAsync(tapTime.Date);
        
        // tama nga hinala ko
        if (activeTerm.ShouldBlockSystem)
        {
            System.Diagnostics.Debug.WriteLine($"");
            System.Diagnostics.Debug.WriteLine($"?????? SYSTEM BLOCKED ??????");
            System.Diagnostics.Debug.WriteLine($"? NO ACTIVE ACADEMIC TERM FOUND");
            System.Diagnostics.Debug.WriteLine($"?? All attendance operations are BLOCKED");
            System.Diagnostics.Debug.WriteLine($"?? Reason: {activeTerm.GetBlockReason()}");
            System.Diagnostics.Debug.WriteLine($"");
            System.Diagnostics.Debug.WriteLine($"? ACTION REQUIRED:");
            System.Diagnostics.Debug.WriteLine($"   1. Activate an academic term in the database");
            System.Diagnostics.Debug.WriteLine($"   2. UPDATE academic_terms SET is_active = true WHERE id = ?");
            System.Diagnostics.Debug.WriteLine($"????????????????????????????????????????????");
            
            result.IsValid = false;
            result.ScheduleType = "system_blocked";
            result.ScheduleDetails = "No Active Academic Term";
            result.AttendanceStatus = "blocked";
            result.Notes = activeTerm.GetBlockReason();
            result.IsBlocked = true;
            result.BlockReason = "No active academic term - system operations blocked";
            
            return result;
        }
        
        if (activeTerm.HasActiveTerm)
        {
            System.Diagnostics.Debug.WriteLine($"? Active Term Found: {activeTerm.TermName}");
            System.Diagnostics.Debug.WriteLine($"   Term ID: {activeTerm.TermId}");
            System.Diagnostics.Debug.WriteLine($"   Academic Year: {activeTerm.AcademicYear}");
            System.Diagnostics.Debug.WriteLine($"   ?? All schedule queries will be filtered by this term");
        }
        else
        {
            System.Diagnostics.Debug.WriteLine($"?? No Active Term - All schedules will be considered");
        }

        // Store term info in result for later use
        result.ActiveTermId = activeTerm.TermId;
        result.ActiveTermName = activeTerm.TermName;

        // SECURITY CHECK: Blocked substitution (always check first for security)
        var blockCheck = await CheckIfOriginalEmployeeIsSubstitutedAsync(employeeId, tapTime);
        if (blockCheck.IsBlocked)
        {
            result.IsValid = false;
            result.ScheduleType = "blocked_substitution";
            result.ScheduleDetails = $"Class Substituted: {blockCheck.SubjectName}";
            result.AttendanceStatus = "blocked";
            result.Notes = $"This class ({blockCheck.SubjectName} - {blockCheck.Section}) has been substituted by {blockCheck.SubstituteName}. You cannot tap in/out for this schedule.";
            result.IsBlocked = true;
            result.BlockReason = "Class substituted by another employee";
            
            System.Diagnostics.Debug.WriteLine($"? BLOCKED: Class substituted");
            System.Diagnostics.Debug.WriteLine($"????????????????????????????????????????????????????????");
            return result;
        }

        // PRIORITY 1: Holiday/Suspension (HIGHEST PRIORITY)
        System.Diagnostics.Debug.WriteLine($"");
        System.Diagnostics.Debug.WriteLine($"??? PRIORITY 1: Checking Holiday/Suspension...");
        
        var holiday = await GetHolidayAsync(tapTime.Date);
        
        if (holiday != null)
        {
            System.Diagnostics.Debug.WriteLine($"? HOLIDAY FOUND!");
            System.Diagnostics.Debug.WriteLine($"   Name: {holiday.Name}");
            System.Diagnostics.Debug.WriteLine($"   Type: {holiday.Type}");
            System.Diagnostics.Debug.WriteLine($"   Affects Attendance: {holiday.AffectsAttendance}");
            System.Diagnostics.Debug.WriteLine($"   Reporting Only: {holiday.ReportingOnly}");
            
            result.ScheduleType = "holiday";
            result.ScheduleDetails = $"{holiday.Type}: {holiday.Name}";
            result.Priority = 1;
            result.IsHoliday = true;
            result.HolidayType = holiday.Type;

            var employeeInfo = await GetEmployeeAsync(employeeId);
            var staffType = employeeInfo?.StaffType ?? "";
            var employmentStatus = employeeInfo?.EmploymentStatus ?? "";
            var isNonTeaching = staffType.Equals("Non-Teaching", StringComparison.OrdinalIgnoreCase);
            var isTeaching = staffType.Equals("Teaching", StringComparison.OrdinalIgnoreCase);

            System.Diagnostics.Debug.WriteLine($"");
            System.Diagnostics.Debug.WriteLine($"????????????????????????????????????????");
            System.Diagnostics.Debug.WriteLine($"??? HOLIDAY VALIDATION");
            System.Diagnostics.Debug.WriteLine($"   Holiday: {holiday.Name}");
            System.Diagnostics.Debug.WriteLine($"   Type: {holiday.Type}");
            System.Diagnostics.Debug.WriteLine($"   Employee: {employeeInfo?.FullName}");
            System.Diagnostics.Debug.WriteLine($"   Staff Type: {staffType}");
            System.Diagnostics.Debug.WriteLine($"   Employment Status: {employmentStatus}");
            System.Diagnostics.Debug.WriteLine($"   Is Non-Teaching: {isNonTeaching}");
            System.Diagnostics.Debug.WriteLine($"   Is Teaching: {isTeaching}");
            System.Diagnostics.Debug.WriteLine($"????????????????????????????????????????");

            // Set employee schedule times for calculations
            if (employeeInfo != null)
            {
                result.ExpectedTimeIn = employeeInfo.ScheduleTimeIn;
                result.ExpectedTimeOut = employeeInfo.ScheduleTimeOut;
            }

            // Use StatusCalculationHelper for holiday status calculation
            var holidayStatusResult = StatusCalculationHelper.CalculateHolidayStatus(
                holiday.Type ?? "",
                holiday.Name ?? "",
                isTeaching,
                isNonTeaching,
                employmentStatus,
                tapTime.TimeOfDay,
                result.ExpectedTimeIn,
                result.ExpectedTimeOut,
                tapType
            );

            result.AttendanceStatus = holidayStatusResult.Status;
            result.IsLate = holidayStatusResult.IsLate;
            result.IsEarlyOut = holidayStatusResult.IsEarlyOut;
            result.LateMinutes = holidayStatusResult.LateMinutes;
            result.UndertimeMinutes = holidayStatusResult.UndertimeMinutes;
            result.Notes = holidayStatusResult.Notes;

            // If holiday logic produced admin-time but the employee has an actual schedule
            // (exam/class/substitute) for today, use schedule-based calculation instead.
            if (string.Equals(result.AttendanceStatus, "admin-time", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var mergedScheduleForHoliday = await new EmployeeScheduleService(_connectionString)
                        .GetEmployeeScheduleAsync(employeeId, tapTime.Date);

                    if (mergedScheduleForHoliday.HasSchedule &&
                        mergedScheduleForHoliday.ScheduleTimeIn != TimeSpan.Zero &&
                        mergedScheduleForHoliday.ScheduleTimeOut != TimeSpan.Zero)
                    {
                        System.Diagnostics.Debug.WriteLine($"⚠ Holiday admin-time overridden by active schedule");
                        System.Diagnostics.Debug.WriteLine($"   Source: {mergedScheduleForHoliday.ScheduleSource}");
                        System.Diagnostics.Debug.WriteLine($"   Window: {FormatTimeSpan(mergedScheduleForHoliday.ScheduleTimeIn)} - {FormatTimeSpan(mergedScheduleForHoliday.ScheduleTimeOut)}");

                        result.ScheduleType = "combined_schedule";
                        result.ScheduleDetails = $"Combined Schedule: {mergedScheduleForHoliday.ScheduleSource}";
                        result.ExpectedTimeIn = mergedScheduleForHoliday.ScheduleTimeIn;
                        result.ExpectedTimeOut = mergedScheduleForHoliday.ScheduleTimeOut;
                        result.Priority = 3;

                        var scheduleStatus = string.Equals(tapType, "IN", StringComparison.OrdinalIgnoreCase)
                            ? StatusCalculationHelper.CalculateTimeInStatus(
                                tapTime.TimeOfDay,
                                mergedScheduleForHoliday.ScheduleTimeIn,
                                "Combined Schedule",
                                mergedScheduleForHoliday.ScheduleSource ?? "Exam/Class/Substitute")
                            : StatusCalculationHelper.CalculateTimeOutStatus(
                                tapTime.TimeOfDay,
                                mergedScheduleForHoliday.ScheduleTimeOut,
                                "Combined Schedule",
                                mergedScheduleForHoliday.ScheduleSource ?? "Exam/Class/Substitute");

                        result.AttendanceStatus = scheduleStatus.Status;
                        result.IsLate = scheduleStatus.IsLate;
                        result.IsEarlyOut = scheduleStatus.IsEarlyOut;
                        result.LateMinutes = scheduleStatus.LateMinutes;
                        result.UndertimeMinutes = scheduleStatus.UndertimeMinutes;
                        result.Notes = $"Holiday ({holiday.Type}: {holiday.Name}) with active schedule. {scheduleStatus.Notes}".Trim();
                    }
                }
                catch (Exception overrideEx)
                {
                    System.Diagnostics.Debug.WriteLine($"⚠ Schedule override check failed during holiday handling: {overrideEx.Message}");
                }
            }
            
            System.Diagnostics.Debug.WriteLine($"? PRIORITY 1 APPLIED - Returning with status: {result.AttendanceStatus}");
            System.Diagnostics.Debug.WriteLine($"????????????????????????????????????????");
            return result;
        }
        else
        {
            System.Diagnostics.Debug.WriteLine($"? NO HOLIDAY FOUND for date: {tapTime.Date:yyyy-MM-dd}");
            System.Diagnostics.Debug.WriteLine($"   Continuing to Priority 2...");
        }

        // PRIORITY 2: Leave/Readjusted Schedule/Verification
        System.Diagnostics.Debug.WriteLine($"");
        System.Diagnostics.Debug.WriteLine($"?? PRIORITY 2: Checking Leave/Readjusted Schedule...");

        var nonTeachingInfo = await GetEmployeeAsync(employeeId);
        var isNonTeachingForVerification = nonTeachingInfo?.StaffType?.Equals("Non-Teaching", StringComparison.OrdinalIgnoreCase) == true;
        if (isNonTeachingForVerification)
        {
            var readjustHelper = new NonTeachingReadjustedScheduleHelper(_connectionString);
            var readjustRequest = await readjustHelper.GetReadjustedScheduleRequestAsync(employeeId, tapTime.Date);

            if (readjustRequest != null && readjustRequest.TimeStart.HasValue && readjustRequest.TimeEnd.HasValue)
            {
                System.Diagnostics.Debug.WriteLine($"? NON-TEACHING READJUSTED REQUEST APPLIED");

                result.ScheduleType = "verification_readjusted";
                result.ScheduleDetails = string.IsNullOrWhiteSpace(readjustRequest.Reason)
                    ? "Readjusted Schedule"
                    : $"Readjusted Schedule: {readjustRequest.Reason}";
                result.Priority = 2;
                result.HasVerificationRequest = true;
                result.VerificationRequestId = readjustRequest.RequestId;
                result.ExpectedTimeIn = readjustRequest.TimeStart;
                result.ExpectedTimeOut = readjustRequest.TimeEnd;

                var scheduleStatus = string.Equals(tapType, "IN", StringComparison.OrdinalIgnoreCase)
                    ? StatusCalculationHelper.CalculateTimeInStatus(
                        tapTime.TimeOfDay,
                        readjustRequest.TimeStart.Value,
                        "Readjusted Schedule",
                        "Verification")
                    : StatusCalculationHelper.CalculateTimeOutStatus(
                        tapTime.TimeOfDay,
                        readjustRequest.TimeEnd.Value,
                        "Readjusted Schedule",
                        "Verification");

                result.AttendanceStatus = scheduleStatus.Status;
                result.IsLate = scheduleStatus.IsLate;
                result.IsEarlyOut = scheduleStatus.IsEarlyOut;
                result.LateMinutes = scheduleStatus.LateMinutes;
                result.UndertimeMinutes = scheduleStatus.UndertimeMinutes;
                result.Notes = scheduleStatus.Notes;

                System.Diagnostics.Debug.WriteLine($"? PRIORITY 2 APPLIED (NON-TEACHING READJUSTED) - Returning with status: {result.AttendanceStatus}");
                System.Diagnostics.Debug.WriteLine($"????????????????????????????????????????");
                return result;
            }
        }

        var verificationRequest = await GetScheduleVerificationRequestAsync(employeeId, tapTime.Date);
        if (verificationRequest != null)
        {
            System.Diagnostics.Debug.WriteLine($"? VERIFICATION REQUEST FOUND!");
            System.Diagnostics.Debug.WriteLine($"   Request Type: {verificationRequest.RequestType}");
            System.Diagnostics.Debug.WriteLine($"   Reason: {verificationRequest.Reason}");

            var requestType = (verificationRequest.RequestType ?? string.Empty).Trim();
            var requestTypeNormalized = requestType.ToLowerInvariant();
            var requestStatusNormalized = (verificationRequest.Status ?? string.Empty).Trim().ToLowerInvariant();
            var isLeaveRequest = requestTypeNormalized == "leave";
            var isReadjustedSchedule = !isLeaveRequest && (
                verificationRequest.AffectsSchedule == true ||
                verificationRequest.TimeStart.HasValue ||
                verificationRequest.TimeEnd.HasValue ||
                requestTypeNormalized.Contains("readjust") ||
                requestTypeNormalized.Contains("schedule"));

            if (isLeaveRequest && requestStatusNormalized == "approved")
            {
                result.ScheduleType = "verification_leave";
                result.ScheduleDetails = $"{requestType}: {verificationRequest.Reason}";
                result.Priority = 2;
                result.HasVerificationRequest = true;
                result.VerificationRequestId = verificationRequest.RequestId;

                // Use StatusCalculationHelper for leave/verification status
                var leaveStatusResult = StatusCalculationHelper.CalculateLeaveStatus(
                    verificationRequest.RequestType ?? "",
                    verificationRequest.Reason ?? ""
                );

                result.AttendanceStatus = leaveStatusResult.Status;
                result.Notes = leaveStatusResult.Notes;

                System.Diagnostics.Debug.WriteLine($"? PRIORITY 2 APPLIED (LEAVE) - Returning with status: {result.AttendanceStatus}");
                System.Diagnostics.Debug.WriteLine($"????????????????????????????????????????");
                return result;
            }

            if (isReadjustedSchedule && verificationRequest.TimeStart.HasValue && verificationRequest.TimeEnd.HasValue)
            {
                result.ScheduleType = "verification_readjusted";
                result.ScheduleDetails = string.IsNullOrWhiteSpace(verificationRequest.Reason)
                    ? "Readjusted Schedule"
                    : $"Readjusted Schedule: {verificationRequest.Reason}";
                result.Priority = 2;
                result.HasVerificationRequest = true;
                result.VerificationRequestId = verificationRequest.RequestId;
                result.ExpectedTimeIn = verificationRequest.TimeStart;
                result.ExpectedTimeOut = verificationRequest.TimeEnd;

                var scheduleStatus = string.Equals(tapType, "IN", StringComparison.OrdinalIgnoreCase)
                    ? StatusCalculationHelper.CalculateTimeInStatus(
                        tapTime.TimeOfDay,
                        verificationRequest.TimeStart.Value,
                        "Readjusted Schedule",
                        "Verification")
                    : StatusCalculationHelper.CalculateTimeOutStatus(
                        tapTime.TimeOfDay,
                        verificationRequest.TimeEnd.Value,
                        "Readjusted Schedule",
                        "Verification");

                result.AttendanceStatus = scheduleStatus.Status;
                result.IsLate = scheduleStatus.IsLate;
                result.IsEarlyOut = scheduleStatus.IsEarlyOut;
                result.LateMinutes = scheduleStatus.LateMinutes;
                result.UndertimeMinutes = scheduleStatus.UndertimeMinutes;
                result.Notes = scheduleStatus.Notes;

                System.Diagnostics.Debug.WriteLine($"? PRIORITY 2 APPLIED (READJUSTED) - Returning with status: {result.AttendanceStatus}");
                System.Diagnostics.Debug.WriteLine($"????????????????????????????????????????");
                return result;
            }

            if (isLeaveRequest)
            {
                System.Diagnostics.Debug.WriteLine($"? Leave request found but not approved yet. Continuing...");
            }
            else if (isReadjustedSchedule)
            {
                System.Diagnostics.Debug.WriteLine($"? Readjusted schedule request missing time window. Continuing...");
            }
        }
        else
        {
            System.Diagnostics.Debug.WriteLine($"? NO VERIFICATION REQUEST FOUND");
            System.Diagnostics.Debug.WriteLine($"   Continuing to Priority 3...");
        }

        var employeeInfoForSchedule = nonTeachingInfo ?? await GetEmployeeAsync(employeeId);
        var isNonTeachingStaff = employeeInfoForSchedule?.StaffType?.Equals("Non-Teaching", StringComparison.OrdinalIgnoreCase) == true;

        if (!isNonTeachingStaff)
        {
            // UNIFIED PRIORITY 3-5: Get Combined Employee Schedule (Exams + Classes + Subs)
            System.Diagnostics.Debug.WriteLine($"");
            System.Diagnostics.Debug.WriteLine($"?? UNIFIED PRIORITY 3-5: Getting Combined Employee Schedule...");

            var scheduleService = new EmployeeScheduleService(_connectionString);
            var combinedSchedule = await scheduleService.GetEmployeeScheduleAsync(employeeId, tapTime.Date);

            if (combinedSchedule.HasSchedule)
            {
                System.Diagnostics.Debug.WriteLine($"? COMBINED SCHEDULE FOUND!");
                System.Diagnostics.Debug.WriteLine($"   Expected Time In: {FormatTimeSpan(combinedSchedule.ScheduleTimeIn)}");
                System.Diagnostics.Debug.WriteLine($"   Expected Time Out: {FormatTimeSpan(combinedSchedule.ScheduleTimeOut)}");
                System.Diagnostics.Debug.WriteLine($"   Schedule Types Combined: {combinedSchedule.ScheduleSource}");

                var source = combinedSchedule.ScheduleSource ?? string.Empty;
                var hasExam = source.Contains("exam schedule", StringComparison.OrdinalIgnoreCase);
                var hasClass = source.Contains("class schedule", StringComparison.OrdinalIgnoreCase);
                var hasSubstitute = source.Contains("substitute duty", StringComparison.OrdinalIgnoreCase);

                if (hasExam && !hasClass && !hasSubstitute)
                {
                    result.ScheduleType = "exam";
                    result.Priority = 4;
                    result.ScheduleDetails = $"Exam Schedule: {source}";
                }
                else if (!hasExam && hasClass && !hasSubstitute)
                {
                    result.ScheduleType = "class";
                    result.Priority = 5;
                    result.ScheduleDetails = $"Class Schedule: {source}";
                }
                else if (!hasExam && !hasClass && hasSubstitute)
                {
                    result.ScheduleType = "substitution";
                    result.Priority = 3;
                    result.ScheduleDetails = $"Substitute Schedule: {source}";
                }
                else
                {
                    result.ScheduleType = "combined_schedule";
                    result.Priority = 3;
                    result.ScheduleDetails = $"Combined Schedule: {source}";
                }

                result.ScheduleId = null; // merged source has no stable single schedule_id
                result.ExpectedTimeIn = combinedSchedule.ScheduleTimeIn;
                result.ExpectedTimeOut = combinedSchedule.ScheduleTimeOut;

                var employeeName = employeeInfoForSchedule?.FullName ?? "Employee";

                // Determine status based on actual tap time vs the combined blocks
                StatusCalculationHelper.StatusResult statusResult;
                if (tapType == "IN")
                {
                    statusResult = StatusCalculationHelper.CalculateTimeInStatus(
                        tapTime.TimeOfDay,
                        combinedSchedule.ScheduleTimeIn,
                        "Combined Schedule",
                        combinedSchedule.ScheduleSource
                    );
                }
                else
                {
                    statusResult = StatusCalculationHelper.CalculateTimeOutStatus(
                        tapTime.TimeOfDay,
                        combinedSchedule.ScheduleTimeOut,
                        "Combined Schedule",
                        combinedSchedule.ScheduleSource
                    );
                }

                result.AttendanceStatus = statusResult.Status;
                result.IsLate = statusResult.IsLate;
                result.IsEarlyOut = statusResult.IsEarlyOut;
                result.LateMinutes = statusResult.LateMinutes;
                result.UndertimeMinutes = statusResult.UndertimeMinutes;
                result.Notes = statusResult.Notes;

            var adminLoadMinutes = await CalculateAdminTimeLoadMinutesAsync(
                employeeId,
                tapTime.Date,
                tapTime.TimeOfDay,
                tapType,
                activeTerm.TermId);

            if (adminLoadMinutes.HasValue && adminLoadMinutes.Value > 0)
            {
                var adminNote = $"Admin Time Load: {adminLoadMinutes.Value} minutes";
                result.Notes = string.IsNullOrWhiteSpace(result.Notes)
                    ? adminNote
                    : $"{result.Notes} | {adminNote}";
            }

                System.Diagnostics.Debug.WriteLine($"? UNIFIED PRIORITY APPLIED - Returning with status: {result.AttendanceStatus}");
                System.Diagnostics.Debug.WriteLine($"????????????????????????????????????????");
                return result;
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"? NO CLASSES/EXAMS FOUND FOR TODAY");
                System.Diagnostics.Debug.WriteLine($"   Continuing to Priority 6...");
            }
        }
        else
        {
            System.Diagnostics.Debug.WriteLine($"? Non-Teaching staff - skipping combined schedule check");
            System.Diagnostics.Debug.WriteLine($"   Continuing to Priority 6...");
        }

        // PRIORITY 6: Regular/Admin Time fallback
        System.Diagnostics.Debug.WriteLine($"");
        System.Diagnostics.Debug.WriteLine($"? PRIORITY 6: Using Regular Schedule (Fallback)...");
        
        result.ScheduleType = "regular";
        result.ScheduleDetails = "Regular work hours";
        result.Priority = 6;
        var emp = employeeInfoForSchedule ?? await GetEmployeeAsync(employeeId);
        if (emp != null)
        {
            // Part-Time Full Load with no class/exam/substitute schedule for the day
            // must be recorded as Admin Time (not Regular Schedule).
            if (IsPartTimeFullLoad(emp.EmploymentStatus))
            {
                result.ScheduleType = "admin_time";
                result.ScheduleDetails = "Admin Time: No class/exam schedule for this day";
                result.ExpectedTimeIn = null;
                result.ExpectedTimeOut = null;
                result.AttendanceStatus = "admin-time";
                result.IsLate = false;
                result.IsEarlyOut = false;
                result.LateMinutes = null;
                result.UndertimeMinutes = null;
                result.Notes = "Part Time Full Load with no class/exam schedule today - recorded as Admin Time";

                System.Diagnostics.Debug.WriteLine($"   ? PART-TIME FULL LOAD with no class/exam schedule");
                System.Diagnostics.Debug.WriteLine($"   ? Fallback switched to ADMIN TIME");
                System.Diagnostics.Debug.WriteLine($"? PRIORITY 6 APPLIED (PTFL ADMIN TIME) - Returning with status: {result.AttendanceStatus}");
                System.Diagnostics.Debug.WriteLine($"????????????????????????????????????????");
                return result;
            }

            result.ExpectedTimeIn = emp.ScheduleTimeIn;
            result.ExpectedTimeOut = emp.ScheduleTimeOut;
            
            System.Diagnostics.Debug.WriteLine($"   Employee Schedule: {FormatTimeSpan(emp.ScheduleTimeIn)} - {FormatTimeSpan(emp.ScheduleTimeOut)}");
            System.Diagnostics.Debug.WriteLine($"   Staff Type: {emp.StaffType}");
            System.Diagnostics.Debug.WriteLine($"   Employment Status: {emp.EmploymentStatus}");
            
            // Use StatusCalculationHelper for regular schedule status
            if (emp.ScheduleTimeIn != TimeSpan.Zero && emp.ScheduleTimeOut != TimeSpan.Zero)
            {
                System.Diagnostics.Debug.WriteLine($"   ? Valid schedule times found - calculating status");
                
                var regularStatusResult = StatusCalculationHelper.CalculateRegularStatus(
                    tapTime.TimeOfDay,
                    emp.ScheduleTimeIn,
                    emp.ScheduleTimeOut,
                    tapType,
                    emp.FullName ?? "Employee"
                );

                result.AttendanceStatus = regularStatusResult.Status;
                result.IsLate = regularStatusResult.IsLate;
                result.IsEarlyOut = regularStatusResult.IsEarlyOut;
                result.LateMinutes = regularStatusResult.LateMinutes;
                result.UndertimeMinutes = regularStatusResult.UndertimeMinutes;
                result.Notes = regularStatusResult.Notes;
                
                System.Diagnostics.Debug.WriteLine($"   ? Calculated Status: {result.AttendanceStatus}");
                System.Diagnostics.Debug.WriteLine($"   ? Is Late: {result.IsLate}");
                System.Diagnostics.Debug.WriteLine($"   ? Late Minutes: {result.LateMinutes}");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"   ?? WARNING: Employee has no schedule_time_in/out set!");
                System.Diagnostics.Debug.WriteLine($"   schedule_time_in: {FormatTimeSpan(emp.ScheduleTimeIn)}");
                System.Diagnostics.Debug.WriteLine($"   schedule_time_out: {FormatTimeSpan(emp.ScheduleTimeOut)}");
                System.Diagnostics.Debug.WriteLine($"   ?? Defaulting to on-time (cannot calculate without schedule)");
                result.AttendanceStatus = "on-time";
                result.Notes = "No schedule defined - cannot calculate late/undertime status";
            }
        }
        else
        {
            result.AttendanceStatus = "on-time";
            System.Diagnostics.Debug.WriteLine($"   ?? No employee found - defaulting to on-time");
        }
        
        System.Diagnostics.Debug.WriteLine($"? PRIORITY 6 APPLIED (Fallback) - Returning with status: {result.AttendanceStatus}");
        System.Diagnostics.Debug.WriteLine($"????????????????????????????????????????");

        return result;
        }
        catch (Exception ex) when (
            ex is System.Net.Sockets.SocketException ||
            ex is NpgsqlException ||
            ex.InnerException is System.Net.Sockets.SocketException ||
            ex.InnerException is NpgsqlException)
        {
            System.Diagnostics.Debug.WriteLine($"  ⚠ Connection error — offline fallback: {ex.Message}");
            result.IsValid = true;
            result.AttendanceStatus = "pending";
            result.Notes = "Validated offline — will re-validate on sync";
            return result;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"  ✗ Validation error: {ex.Message}");
            result.IsValid = true;
            result.AttendanceStatus = "pending";
            result.Notes = $"Validation error: {ex.Message}";
            return result;
        }
    }

    private static bool IsPartTimeFullLoad(string? employmentStatus)
    {
        if (string.IsNullOrWhiteSpace(employmentStatus))
            return false;

        var normalized = employmentStatus
            .Trim()
            .ToLowerInvariant()
            .Replace("-", " ")
            .Replace("_", " ");

        return normalized.Contains("part time full load", StringComparison.Ordinal) ||
               normalized.Contains("pt full load", StringComparison.Ordinal);
    }

    private void CalculateAttendanceStatus(AttendanceValidationResult result, TimeSpan actualTime, string tapType)
    {
        System.Diagnostics.Debug.WriteLine($"");
        System.Diagnostics.Debug.WriteLine($"????????????????????????????????????????");
        System.Diagnostics.Debug.WriteLine($"?? CALCULATING ATTENDANCE STATUS");
        System.Diagnostics.Debug.WriteLine($"   Using StatusCalculationHelper");
        System.Diagnostics.Debug.WriteLine($"   Schedule Type: {result.ScheduleType}");
        System.Diagnostics.Debug.WriteLine($"   Priority: {result.Priority}");
        System.Diagnostics.Debug.WriteLine($"????????????????????????????????????????");

        // Ensure we ALWAYS set a valid status - default to on-time
        result.AttendanceStatus = "on-time";

        StatusCalculationHelper.StatusResult statusResult;

        // Use the appropriate helper method based on schedule type
        switch (result.ScheduleType?.ToLowerInvariant())
        {
            case "class":
                if (result.ExpectedTimeIn.HasValue && result.ExpectedTimeOut.HasValue)
                {
                    var subjectName = ExtractSubjectFromDetails(result.ScheduleDetails);
                    var section = ExtractSectionFromDetails(result.ScheduleDetails);
                    
                    statusResult = StatusCalculationHelper.CalculateClassStatus(
                        actualTime,
                        result.ExpectedTimeIn.Value,
                        result.ExpectedTimeOut.Value,
                        tapType,
                        subjectName,
                        section
                    );
                    ApplyStatusResult(result, statusResult);
                }
                else
                {
                    result.AttendanceStatus = "on-time";
                    result.Notes = "Class schedule - no time info available";
                }
                break;

            case "exam":
                if (result.ExpectedTimeIn.HasValue && result.ExpectedTimeOut.HasValue)
                {
                    var subjectName = ExtractSubjectFromDetails(result.ScheduleDetails);
                    var section = ExtractSectionFromDetails(result.ScheduleDetails);
                    
                    statusResult = StatusCalculationHelper.CalculateExamStatus(
                        actualTime,
                        result.ExpectedTimeIn.Value,
                        result.ExpectedTimeOut.Value,
                        tapType,
                        subjectName,
                        section
                    );
                    ApplyStatusResult(result, statusResult);
                }
                else
                {
                    result.AttendanceStatus = "on-time";
                    result.Notes = "Exam schedule - no time info available";
                }
                break;

            case "substitution":
                if (result.ExpectedTimeIn.HasValue && result.ExpectedTimeOut.HasValue)
                {
                    var subjectName = ExtractSubjectFromDetails(result.ScheduleDetails);
                    var originalName = ExtractOriginalEmployeeFromDetails(result.ScheduleDetails);
                    
                    statusResult = StatusCalculationHelper.CalculateSubstitutionStatus(
                        actualTime,
                        result.ExpectedTimeIn.Value,
                        result.ExpectedTimeOut.Value,
                        tapType,
                        subjectName,
                        originalName
                    );
                    ApplyStatusResult(result, statusResult);
                }
                else
                {
                    result.AttendanceStatus = "on-time";
                    result.Notes = "Substitution - no time info available";
                }
                break;

            case "regular":
                if (result.ExpectedTimeIn.HasValue && result.ExpectedTimeOut.HasValue)
                {
                    statusResult = StatusCalculationHelper.CalculateRegularStatus(
                        actualTime,
                        result.ExpectedTimeIn.Value,
                        result.ExpectedTimeOut.Value,
                        tapType,
                        "Employee"
                    );
                    ApplyStatusResult(result, statusResult);
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"   ?? WARNING: Employee has no schedule_time_in/out set!");
                    result.AttendanceStatus = "on-time";
                    result.Notes = "No schedule defined - cannot calculate late/undertime status";
                }
                break;

            default:
                // Fallback for unknown schedule types
                if (tapType == "IN" && result.ExpectedTimeIn.HasValue)
                {
                    statusResult = StatusCalculationHelper.CalculateTimeInStatus(
                        actualTime,
                        result.ExpectedTimeIn.Value,
                        result.ScheduleType ?? "Unknown",
                        result.ScheduleDetails ?? "Unknown schedule"
                    );
                    ApplyStatusResult(result, statusResult);
                }
                else if (tapType == "OUT" && result.ExpectedTimeOut.HasValue)
                {
                    statusResult = StatusCalculationHelper.CalculateTimeOutStatus(
                        actualTime,
                        result.ExpectedTimeOut.Value,
                        result.ScheduleType ?? "Unknown",
                        result.ScheduleDetails ?? "Unknown schedule"
                    );
                    ApplyStatusResult(result, statusResult);
                }
                else
                {
                    result.AttendanceStatus = "on-time";
                    result.Notes = result.Notes ?? "Attendance recorded";
                }
                break;
        }
        
        System.Diagnostics.Debug.WriteLine($"????????????????????????????????????????");
    }

    /// <summary>
    /// Apply status result from helper to the validation result
    /// </summary>
    private void ApplyStatusResult(AttendanceValidationResult result, StatusCalculationHelper.StatusResult statusResult)
    {
        result.AttendanceStatus = statusResult.Status;
        result.IsLate = statusResult.IsLate;
        result.IsEarlyOut = statusResult.IsEarlyOut;
        result.LateMinutes = statusResult.LateMinutes;
        result.UndertimeMinutes = statusResult.UndertimeMinutes;
        
        // Preserve existing notes if any, otherwise use status result notes
        if (string.IsNullOrWhiteSpace(result.Notes))
        {
            result.Notes = statusResult.Notes;
        }
    }

    /// <summary>
    /// Extract subject name from schedule details string
    /// </summary>
    private string ExtractSubjectFromDetails(string? details)
    {
        if (string.IsNullOrEmpty(details)) return "Unknown Subject";
        
        // Pattern: "Class: SubjectName - Section" or "Exam: SubjectName - Section"
        var parts = details.Split(':');
        if (parts.Length > 1)
        {
            var afterColon = parts[1].Trim();
            var dashIndex = afterColon.IndexOf(" - ");
            if (dashIndex > 0)
            {
                return afterColon.Substring(0, dashIndex).Trim();
            }
            return afterColon;
        }
        
        return details;
    }

    /// <summary>
    /// Extract section from schedule details string
    /// </summary>
    private string ExtractSectionFromDetails(string? details)
    {
        if (string.IsNullOrEmpty(details)) return "";
        
        var dashIndex = details.LastIndexOf(" - ");
        if (dashIndex > 0 && dashIndex < details.Length - 3)
        {
            return details.Substring(dashIndex + 3).Trim();
        }
        
        return "";
    }

    /// <summary>
    /// Extract original employee name from substitution details
    /// </summary>
    private string ExtractOriginalEmployeeFromDetails(string? details)
    {
        if (string.IsNullOrEmpty(details)) return "Unknown Employee";
        
        // Pattern: "Substitution: SubjectName - Section"
        // or from Notes: "Substituting for EmployeeName - SubjectName"
        if (details.Contains("for "))
        {
            var forIndex = details.IndexOf("for ");
            var dashIndex = details.IndexOf(" - ", forIndex);
            if (dashIndex > forIndex)
            {
                return details.Substring(forIndex + 4, dashIndex - forIndex - 4).Trim();
            }
        }
        
        return "Employee";
    }

    private static T? SelectBestScheduleForTap<T>(
        IEnumerable<T> schedules,
        DateTime tapTime,
        string tapType,
        Func<T, TimeSpan?> getStart,
        Func<T, TimeSpan?> getEnd)
    {
        var list = schedules.ToList();
        if (list.Count == 0) return default;

        var now = tapTime.TimeOfDay;

        // 1) Prefer an active window containing current time.
        var active = list
            .Where(s => getStart(s).HasValue && getEnd(s).HasValue)
            .Where(s => now >= getStart(s)!.Value && now <= getEnd(s)!.Value)
            .OrderBy(s => getStart(s)!.Value)
            .ToList();
        if (active.Count > 0)
            return active[0];

        var isOut = string.Equals(tapType, "OUT", StringComparison.OrdinalIgnoreCase);

        // 2) For TIME IN: prefer nearest upcoming start, otherwise latest past start.
        if (!isOut)
        {
            var upcoming = list
                .Where(s => getStart(s).HasValue)
                .Where(s => getStart(s)!.Value >= now)
                .OrderBy(s => getStart(s)!.Value)
                .ToList();
            if (upcoming.Count > 0) return upcoming[0];

            var latestPast = list
                .Where(s => getStart(s).HasValue)
                .OrderByDescending(s => getStart(s)!.Value)
                .ToList();
            if (latestPast.Count > 0) return latestPast[0];
        }
        // 3) For TIME OUT: prefer nearest upcoming end, otherwise latest past end.
        else
        {
            var upcomingEnd = list
                .Where(s => getEnd(s).HasValue)
                .Where(s => getEnd(s)!.Value >= now)
                .OrderBy(s => getEnd(s)!.Value)
                .ToList();
            if (upcomingEnd.Count > 0) return upcomingEnd[0];

            var latestPastEnd = list
                .Where(s => getEnd(s).HasValue)
                .OrderByDescending(s => getEnd(s)!.Value)
                .ToList();
            if (latestPastEnd.Count > 0) return latestPastEnd[0];
        }

        return list[0];
    }

    private async Task<ExamSchedule?> GetExamScheduleAsync(int employeeId, DateTime tapTime, string tapType, long? termId = null)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var dayOfWeek = tapTime.DayOfWeek.ToString();

            AcademicTermFilterHelper.LogTermFilter(
                termId.HasValue ? new AcademicTermFilterHelper.ActiveTermResult { TermId = termId, IsActive = true } : null,
                "Exam Schedules Query"
            );

            // Build term filter clause
            var termFilter = AcademicTermFilterHelper.BuildTermFilterClauseParameterized(termId);

            var query = $@"
                SELECT * FROM exam_schedules
                WHERE employee_id = @EmployeeId
                    AND (
                        (exam_date = @Date) OR
                        (day_of_week = @DayOfWeek AND exam_date IS NULL)
                    )
                    AND (status IS NULL OR status = 'active' OR status = 'scheduled')
                    {termFilter}
                ORDER BY exam_date DESC NULLS LAST, time_start ASC
            ";

            var parameters = new DynamicParameters();
            parameters.Add("EmployeeId", employeeId);
            parameters.Add("Date", tapTime.Date);
            parameters.Add("DayOfWeek", dayOfWeek);
            if (termId.HasValue)
            {
                parameters.Add("TermId", termId.Value);
            }

            var candidates = (await connection.QueryAsync<ExamSchedule>(query, parameters)).ToList();

            var exam = SelectBestScheduleForTap(
                candidates,
                tapTime,
                tapType,
                e => e.TimeStart,
                e => e.TimeEnd);

            if (exam != null)
            {
                // Validate term match
                if (!AcademicTermFilterHelper.IsScheduleInActiveTerm(exam.Term, 
                    termId.HasValue ? new AcademicTermFilterHelper.ActiveTermResult { TermId = termId, IsActive = true } : null))
                {
                    System.Diagnostics.Debug.WriteLine($"   ?? Exam schedule excluded due to term mismatch");
                    return null;
                }
            }

            return exam;
        }
        catch
        {
            return null;
        }
    }

    private async Task<TeachingSchedule?> GetClassScheduleAsync(int employeeId, DateTime tapTime, string tapType, long? termId = null)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var dayOfWeek = tapTime.DayOfWeek.ToString();
            var dayOfWeekNumber = (int)tapTime.DayOfWeek; // 0=Sunday, 1=Monday, etc.

            System.Diagnostics.Debug.WriteLine($"");
            System.Diagnostics.Debug.WriteLine($"   ?? GetClassScheduleAsync for attendance validation:");
            System.Diagnostics.Debug.WriteLine($"      Employee ID: {employeeId}");
            System.Diagnostics.Debug.WriteLine($"      Date: {tapTime.Date:yyyy-MM-dd}");
            System.Diagnostics.Debug.WriteLine($"      Day: {dayOfWeek} ({dayOfWeekNumber})");
            System.Diagnostics.Debug.WriteLine($"      Time: {FormatTimeSpan(tapTime.TimeOfDay)}");
            System.Diagnostics.Debug.WriteLine($"      Tap Type: {tapType}");

            AcademicTermFilterHelper.LogTermFilter(
                termId.HasValue ? new AcademicTermFilterHelper.ActiveTermResult { TermId = termId, IsActive = true } : null,
                "Teaching Schedules Query"
            );

            // Build term filter clause
            var termFilter = AcademicTermFilterHelper.BuildTermFilterClauseParameterized(termId);

            var query = $@"
                SELECT 
                    schedule_id as ScheduleId,
                    employee_id as EmployeeId,
                    subject_name as SubjectName,
                    section as Section,
                    time_start as TimeStart,
                    time_end as TimeEnd,
                    COALESCE(room_id::text, '') as RoomCode,
                    day_of_week::text as DayOfWeek,
                    specific_date as SpecificDate,
                    is_recurring as IsRecurring,
                    term as Term
                FROM teaching_schedules
                WHERE employee_id = @EmployeeId
                    AND (
                        (specific_date = @Date) OR
                        (
                            day_of_week = @DayOfWeekNumber
                            AND (is_recurring = true OR is_recurring IS NULL)
                        )
                    )
                    AND (status IS NULL OR status = 'active' OR status = 'available')
                    {termFilter}
                ORDER BY specific_date DESC NULLS LAST, time_start ASC
            ";

            var parameters = new DynamicParameters();
            parameters.Add("EmployeeId", employeeId);
            parameters.Add("Date", tapTime.Date);
            parameters.Add("DayOfWeekNumber", dayOfWeekNumber);
            if (termId.HasValue)
            {
                parameters.Add("TermId", termId.Value);
            }

            var candidates = (await connection.QueryAsync<TeachingSchedule>(query, parameters)).ToList();

            var schedule = SelectBestScheduleForTap(
                candidates,
                tapTime,
                tapType,
                s => s.TimeStart,
                s => s.TimeEnd);

            if (schedule != null)
            {
                System.Diagnostics.Debug.WriteLine($"      ? Found schedule: {schedule.SubjectName} - {schedule.Section}");
                System.Diagnostics.Debug.WriteLine($"         Time: {FormatTimeSpan(schedule.TimeStart ?? TimeSpan.Zero)} - {FormatTimeSpan(schedule.TimeEnd ?? TimeSpan.Zero)}");
                System.Diagnostics.Debug.WriteLine($"         Term: {schedule.Term?.ToString() ?? "Not assigned"}");

                // Validate term match
                if (!AcademicTermFilterHelper.IsScheduleInActiveTerm(schedule.Term, 
                    termId.HasValue ? new AcademicTermFilterHelper.ActiveTermResult { TermId = termId, IsActive = true } : null))
                {
                    System.Diagnostics.Debug.WriteLine($"      ?? Schedule excluded due to term mismatch");
                    return null;
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"      ? No schedule found at this time");
            }

            return schedule;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"      ? ERROR in GetClassScheduleAsync:");
            System.Diagnostics.Debug.WriteLine($"         Message: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"         Stack: {ex.StackTrace}");
            return null;
        }
    }

    private async Task<int?> CalculateAdminTimeLoadMinutesAsync(
        int employeeId,
        DateTime date,
        TimeSpan tapTime,
        string tapType,
        long? termId)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var dayOfWeekNumber = (int)date.DayOfWeek;
            var termFilter = AcademicTermFilterHelper.BuildTermFilterClauseParameterized(termId);

            var query = $@"
                SELECT time_start AS TimeStart, time_end AS TimeEnd
                FROM teaching_schedules
                WHERE employee_id = @EmployeeId
                    AND (
                        (specific_date = @Date) OR
                        (
                            day_of_week = @DayOfWeekNumber
                            AND (is_recurring = true OR is_recurring IS NULL)
                        )
                    )
                    AND (status IS NULL OR status = 'active' OR status = 'available')
                    {termFilter}
                ORDER BY time_start ASC
            ";

            var parameters = new DynamicParameters();
            parameters.Add("EmployeeId", employeeId);
            parameters.Add("Date", date);
            parameters.Add("DayOfWeekNumber", dayOfWeekNumber);
            if (termId.HasValue)
            {
                parameters.Add("TermId", termId.Value);
            }

            var schedules = (await connection.QueryAsync<TeachingSchedule>(query, parameters))
                .Where(s => s.TimeStart.HasValue && s.TimeEnd.HasValue)
                .Select(s => new
                {
                    Start = new TimeSpan(s.TimeStart!.Value.Hours, s.TimeStart.Value.Minutes, 0),
                    End = new TimeSpan(s.TimeEnd!.Value.Hours, s.TimeEnd.Value.Minutes, 0)
                })
                .Where(s => s.End > s.Start)
                .OrderBy(s => s.Start)
                .ToList();

            if (!schedules.Any())
                return null;

            var merged = new List<(TimeSpan Start, TimeSpan End)>();
            foreach (var schedule in schedules)
            {
                if (merged.Count == 0)
                {
                    merged.Add((schedule.Start, schedule.End));
                    continue;
                }

                var last = merged[^1];
                if (schedule.Start <= last.End)
                {
                    merged[^1] = (last.Start, schedule.End > last.End ? schedule.End : last.End);
                }
                else
                {
                    merged.Add((schedule.Start, schedule.End));
                }
            }

            var totalMinutes = 0;
            for (var i = 0; i < merged.Count - 1; i++)
            {
                var gap = merged[i + 1].Start - merged[i].End;
                if (gap.TotalMinutes > 0)
                {
                    totalMinutes += (int)Math.Round(gap.TotalMinutes);
                }
            }

            tapTime = new TimeSpan(tapTime.Hours, tapTime.Minutes, 0);
            if (string.Equals(tapType, "IN", StringComparison.OrdinalIgnoreCase))
            {
                var earlyGap = merged[0].Start - tapTime;
                if (earlyGap.TotalMinutes > 0)
                {
                    totalMinutes += (int)Math.Round(earlyGap.TotalMinutes);
                }
            }
            else if (string.Equals(tapType, "OUT", StringComparison.OrdinalIgnoreCase))
            {
                var lateGap = tapTime - merged[^1].End;
                if (lateGap.TotalMinutes > 0)
                {
                    totalMinutes += (int)Math.Round(lateGap.TotalMinutes);
                }
            }

            return totalMinutes > 0 ? totalMinutes : null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"⚠ Admin time load calculation failed: {ex.Message}");
            return null;
        }
    }

    private async Task<HolidayCalendar?> GetHolidayAsync(DateTime date)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            // ? Updated to support both date ranges and legacy single-date column
            var holiday = await connection.QueryFirstOrDefaultAsync<HolidayCalendar>(@"
                SELECT 
                    id as HolidayId,
                    name as Name,
                    type as Type,
                    description as Description,
                    start_date as StartDate,
                    end_date as EndDate,
                    date as Date,
                    affects_attendance as AffectsAttendance,
                    holiday_type as HolidayType,
                    reporting_only as ReportingOnly,
                    reporting_staff_only as ReportingStaffOnly,
                    created_by as CreatedBy,
                    created_at as CreatedAt,
                    updated_at as UpdatedAt
                FROM holiday_calendar
                WHERE @Date BETWEEN start_date AND end_date
                   OR date = @Date
                ORDER BY created_at DESC
                LIMIT 1
            ", new { Date = date });

            return holiday;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error getting holiday: {ex.Message}");
            return null;
        }
    }

    private async Task<VerificationRequest?> GetScheduleVerificationRequestAsync(int employeeId, DateTime date)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var requests = await connection.QueryAsync<VerificationRequest>(@"
                SELECT * FROM verification_requests
                WHERE employee_id = @EmployeeId
                    AND LOWER(status) IN ('pending', 'approved')
                    AND (
                        DATE(schedule_date) = @Date
                        OR DATE(requested_time) = @Date
                        OR DATE(original_time) = @Date
                    )
                ORDER BY requested_at DESC
            ", new { EmployeeId = employeeId, Date = date });

            var requestList = requests.ToList();
            if (!requestList.Any())
                return null;

            bool IsLeave(VerificationRequest r)
                => string.Equals(r.RequestType?.Trim(), "leave", StringComparison.OrdinalIgnoreCase);

            bool IsApproved(VerificationRequest r)
                => string.Equals(r.Status?.Trim(), "approved", StringComparison.OrdinalIgnoreCase);

            bool IsReadjusted(VerificationRequest r)
            {
                if (r.AffectsSchedule == true || r.TimeStart.HasValue || r.TimeEnd.HasValue)
                    return true;

                var type = r.RequestType?.Trim() ?? string.Empty;
                return type.Contains("readjust", StringComparison.OrdinalIgnoreCase) ||
                       type.Contains("schedule", StringComparison.OrdinalIgnoreCase);
            }

            var approvedLeave = requestList.FirstOrDefault(r => IsLeave(r) && IsApproved(r));
            if (approvedLeave != null)
                return approvedLeave;

            var readjusted = requestList
                .Where(IsReadjusted)
                .OrderByDescending(r => r.RequestedAt ?? DateTime.MinValue)
                .FirstOrDefault();

            return readjusted;
        }
        catch
        {
            return null;
        }
    }

    private async Task<Employee?> GetEmployeeAsync(int employeeId)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var employee = await connection.QueryFirstOrDefaultAsync<Employee>(@"
                SELECT 
                    employee_id as EmployeeId,
                    full_name as FullName,
                    staff_type as StaffType,
                    employment_status as EmploymentStatus,
                    schedule_time_in as ScheduleTimeIn,
                    schedule_time_out as ScheduleTimeOut,
                    is_reporting_staff as IsReportingStaff
                FROM employees
                WHERE employee_id = @EmployeeId
            ", new { EmployeeId = employeeId });

            if (employee != null)
            {
                System.Diagnostics.Debug.WriteLine($"");
                System.Diagnostics.Debug.WriteLine($"?? EMPLOYEE DETAILS RETRIEVED:");
                System.Diagnostics.Debug.WriteLine($"   Name: {employee.FullName}");
                System.Diagnostics.Debug.WriteLine($"   Staff Type: {employee.StaffType}");
                System.Diagnostics.Debug.WriteLine($"   Employment Status: {employee.EmploymentStatus}");
                System.Diagnostics.Debug.WriteLine($"   Schedule IN: {FormatTimeSpan(employee.ScheduleTimeIn)}");
                System.Diagnostics.Debug.WriteLine($"   Schedule OUT: {FormatTimeSpan(employee.ScheduleTimeOut)}");
                System.Diagnostics.Debug.WriteLine($"   Is Reporting Staff: {employee.IsReportingStaff}");
            }

            return employee;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"? Error getting employee: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Check if the original employee is trying to tap during a substituted time slot
    /// Returns blocking info if they should be blocked
    /// </summary>
    private async Task<SubstitutionBlockCheck> CheckIfOriginalEmployeeIsSubstitutedAsync(int employeeId, DateTime tapTime)
    {
        var blockCheck = new SubstitutionBlockCheck { IsBlocked = false };
        
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var currentTime = tapTime.TimeOfDay;
            var currentDate = tapTime.Date;

            System.Diagnostics.Debug.WriteLine($"");
            System.Diagnostics.Debug.WriteLine($"?? CHECKING IF EMPLOYEE IS SUBSTITUTED:");
            System.Diagnostics.Debug.WriteLine($"   Employee ID: {employeeId}");
            System.Diagnostics.Debug.WriteLine($"   Date: {currentDate:yyyy-MM-dd}");
            System.Diagnostics.Debug.WriteLine($"   Time: {FormatTimeSpanDetailed(currentTime)}");

            // ?? FIXED: Use ::date casting and BETWEEN for time range
            // Check if this employee is the original_employee_id in an approved substitution
            // for this date and time range
            var substitution = await connection.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT 
                    cs.id as SubstitutionId,
                    cs.original_employee_id as OriginalEmployeeId,
                    cs.substitute_employee_id as SubstituteEmployeeId,
                    cs.substitution_date as SubstitutionDate,
                    cs.start_time as StartTime,
                    cs.end_time as EndTime,
                    cs.reason as Reason,
                    se.full_name as SubstituteName,
                    ts.subject_name as SubjectName,
                    ts.section as Section,
                    COALESCE(ts.room_id::text, '') as Room
                FROM class_substitutions_v2 cs
                LEFT JOIN employees se ON cs.substitute_employee_id = se.employee_id
                LEFT JOIN teaching_schedules ts ON 
                    ts.employee_id = cs.original_employee_id
                    AND ts.time_start = cs.start_time
                    AND ts.time_end = cs.end_time
                    AND (
                        (ts.specific_date = cs.substitution_date::date) OR
                        (ts.day_of_week = EXTRACT(DOW FROM cs.substitution_date)::int AND (ts.is_recurring = true OR ts.is_recurring IS NULL))
                    )
                WHERE cs.original_employee_id = @EmployeeId
                    AND cs.substitution_date::date = @Date
                    AND @Time BETWEEN cs.start_time AND cs.end_time
                    AND cs.status = 'approved'
                ORDER BY cs.created_at DESC
                LIMIT 1
            ", new { 
                EmployeeId = employeeId, 
                Date = currentDate,
                Time = currentTime
            });

            if (substitution != null)
            {
                blockCheck.IsBlocked = true;
                blockCheck.SubstitutionId = substitution.SubstitutionId != null ? (long)substitution.SubstitutionId : 0;
                blockCheck.SubstituteEmployeeId = substitution.SubstituteEmployeeId ?? 0;
                blockCheck.SubstituteName = substitution.SubstituteName ?? "Unknown";
                blockCheck.SubjectName = substitution.SubjectName ?? "Class";
                blockCheck.Section = substitution.Section ?? "";
                blockCheck.StartTime = substitution.StartTime != null ? (TimeSpan)substitution.StartTime : TimeSpan.Zero;
                blockCheck.EndTime = substitution.EndTime != null ? (TimeSpan)substitution.EndTime : TimeSpan.Zero;
                blockCheck.Room = substitution.Room ?? "";
                blockCheck.Reason = substitution.Reason ?? "";

                System.Diagnostics.Debug.WriteLine($"");
                System.Diagnostics.Debug.WriteLine($"?? BLOCKING ORIGINAL EMPLOYEE!");
                System.Diagnostics.Debug.WriteLine($"   Employee ID: {employeeId} is being substituted");
                System.Diagnostics.Debug.WriteLine($"   Substitution ID: {blockCheck.SubstitutionId}");
                System.Diagnostics.Debug.WriteLine($"   Subject: {blockCheck.SubjectName} ({blockCheck.Section})");
                System.Diagnostics.Debug.WriteLine($"   Time Slot: {FormatTimeSpan(blockCheck.StartTime)} - {FormatTimeSpan(blockCheck.EndTime)}");
                System.Diagnostics.Debug.WriteLine($"   Substituted By: {blockCheck.SubstituteName} (ID: {blockCheck.SubstituteEmployeeId})");
                System.Diagnostics.Debug.WriteLine($"   Reason: {blockCheck.Reason}");
                System.Diagnostics.Debug.WriteLine($"   ? Attendance BLOCKED - Class is substituted");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"   ? No substitution blocking - Employee can proceed");
            }

            return blockCheck;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"");
            System.Diagnostics.Debug.WriteLine($"? ERROR in CheckIfOriginalEmployeeIsSubstitutedAsync:");
            System.Diagnostics.Debug.WriteLine($"   Message: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"   Stack: {ex.StackTrace}");
            return blockCheck; // Don't block on error
        }
    }

    /// <summary>
    /// Get transferred teaching schedule if this employee is substituting someone
    /// Returns the original employee's schedule details
    /// ?? FIXED: Checks for substitution on the ENTIRE DAY, not just current time
    /// This allows tapping at ANY time (e.g., 07:35) when substitution exists for 08:00-11:00
    /// </summary>
    private async Task<SubstituteScheduleTransfer?> GetSubstituteScheduleTransferAsync(int employeeId, DateTime tapTime)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var currentTime = tapTime.TimeOfDay;
            var currentDate = tapTime.Date;

            System.Diagnostics.Debug.WriteLine($"");
            System.Diagnostics.Debug.WriteLine($"?? CHECKING FOR SUBSTITUTE SCHEDULE TRANSFER:");
            System.Diagnostics.Debug.WriteLine($"   Employee ID: {employeeId}");
            System.Diagnostics.Debug.WriteLine($"   Date: {currentDate:yyyy-MM-dd}");
            System.Diagnostics.Debug.WriteLine($"   Time: {FormatTimeSpanDetailed(currentTime)}");

            // ?? CRITICAL FIX: Check for substitution on ENTIRE DAY, not just current time
            // This allows Rohan to tap at 07:35 even though substitution is 08:00-11:00
            var substitution = await connection.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT 
                    cs.id,
                    cs.original_employee_id,
                    cs.substitute_employee_id,
                    cs.substitution_date,
                    cs.start_time,
                    cs.end_time,
                    cs.reason,
                    oe.full_name as original_employee_name,
                    ts.subject_name,
                    ts.section,
                    COALESCE(ts.room_id::text, '') as room,
                    ts.schedule_id
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
                ORDER BY cs.start_time ASC
                LIMIT 1
            ", new { 
                EmployeeId = employeeId, 
                Date = currentDate
            });

            if (substitution != null)
            {
                // ?? FIXED: Access dynamic properties with lowercase names (Dapper convention)
                long substitutionId = 0;
                if (substitution.id != null)
                {
                    substitutionId = (long)substitution.id;
                }
                
                var transfer = new SubstituteScheduleTransfer
                {
                    SubstitutionId = substitutionId,
                    OriginalEmployeeId = substitution.original_employee_id ?? 0,
                    OriginalEmployeeName = substitution.original_employee_name ?? "Unknown",
                    SubjectName = substitution.subject_name ?? "Substituted Class",
                    Section = substitution.section ?? "",
                    StartTime = substitution.start_time != null ? (TimeSpan)substitution.start_time : TimeSpan.Zero,
                    EndTime = substitution.end_time != null ? (TimeSpan)substitution.end_time : TimeSpan.Zero,
                    Room = substitution.room ?? "",
                    Reason = substitution.reason ?? ""
                };

                System.Diagnostics.Debug.WriteLine($"");
                System.Diagnostics.Debug.WriteLine($"? SUBSTITUTION FOUND (DAY-BASED CHECK)!");
                System.Diagnostics.Debug.WriteLine($"   Substitution ID: {transfer.SubstitutionId}");
                System.Diagnostics.Debug.WriteLine($"   Original Employee: {transfer.OriginalEmployeeName} (ID: {transfer.OriginalEmployeeId})");
                System.Diagnostics.Debug.WriteLine($"   Subject: {transfer.SubjectName}");
                System.Diagnostics.Debug.WriteLine($"   Section: {transfer.Section}");
                System.Diagnostics.Debug.WriteLine($"   Time Slot: {FormatTimeSpan(transfer.StartTime)} - {FormatTimeSpan(transfer.EndTime)}");
                System.Diagnostics.Debug.WriteLine($"   Room: {transfer.Room}");
                System.Diagnostics.Debug.WriteLine($"   Reason: {transfer.Reason}");
                System.Diagnostics.Debug.WriteLine($"   ?? NOTE: Employee can tap at ANY time today");
                System.Diagnostics.Debug.WriteLine($"   Current tap time: {FormatTimeSpanDetailed(currentTime)}");
                System.Diagnostics.Debug.WriteLine($"   Actual duty: {FormatTimeSpan(transfer.StartTime)} - {FormatTimeSpan(transfer.EndTime)}");
                
                // ?? CRITICAL: Check if teaching schedule was found
                if (substitution.schedule_id != null)
                {
                    System.Diagnostics.Debug.WriteLine($"   Teaching Schedule ID: {substitution.schedule_id}");
                    System.Diagnostics.Debug.WriteLine($"   ? Teaching schedule details found in database");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"   ?? WARNING: No matching teaching_schedule found!");
                    System.Diagnostics.Debug.WriteLine($"   The substitution exists but the original employee doesn't have");
                    System.Diagnostics.Debug.WriteLine($"   a teaching_schedule with matching times.");
                    System.Diagnostics.Debug.WriteLine($"   Subject/Section will be shown as 'Substituted Class'");
                }
                
                System.Diagnostics.Debug.WriteLine($"   ? Transferring this schedule to substitute employee {employeeId}");

                return transfer;
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"   ? No approved substitution found for this employee on this date");
                return null;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"");
            System.Diagnostics.Debug.WriteLine($"? ERROR in GetSubstituteScheduleTransferAsync:");
            System.Diagnostics.Debug.WriteLine($"   Message: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"   Stack: {ex.StackTrace}");
            return null;
        }
    }
}

/// <summary>
/// Result of attendance validation containing all relevant information
/// </summary>
public class AttendanceValidationResult
{
    public bool IsValid { get; set; }
    public int EmployeeId { get; set; }
    public DateTime TapTime { get; set; }
    public string TapType { get; set; } = string.Empty;
    
    // Schedule Information
    public string ScheduleType { get; set; } = string.Empty; // exam, class, holiday, substitution, verification, regular, admin_time, blocked_substitution
    public long? ScheduleId { get; set; }
    public string ScheduleDetails { get; set; } = string.Empty;
    public int Priority { get; set; } // 1=highest, 6=lowest

    // Time Information
    public TimeSpan? ExpectedTimeIn { get; set; }
    public TimeSpan? ExpectedTimeOut { get; set; }
    public string? Room { get; set; }
    
    // Attendance Status - ?? CRITICAL: Default to valid value
    public string AttendanceStatus { get; set; } = "on-time"; // ONLY: on-time, late, undertime, admin-time, blocked
    public bool IsLate { get; set; }
    public bool IsEarlyOut { get; set; }
    public int? LateMinutes { get; set; }
    public int? UndertimeMinutes { get; set; }
    
    // Special Flags
    public bool IsHoliday { get; set; }
    public string? HolidayType { get; set; }
    public bool IsSubstitution { get; set; }
    public bool HasVerificationRequest { get; set; }
    public long? VerificationRequestId { get; set; }
    public bool IsBlocked { get; set; }
    public string? BlockReason { get; set; }
    
    // Academic Term Information
    public long? ActiveTermId { get; set; }
    public string? ActiveTermName { get; set; }
    
    // Notes and Messages
    public string? Notes { get; set; }
    public List<string> Warnings { get; set; } = new();
    
    public string GetDisplayMessage()
    {
        if (IsBlocked)
        {
            return $"?? BLOCKED - {ScheduleDetails}";
        }

        if (string.Equals(ScheduleType, "admin_time", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ScheduleType, "admin-time", StringComparison.OrdinalIgnoreCase))
        {
            return $"[ADMIN TIME] {ScheduleDetails} - {AttendanceStatus.ToUpper()}";
        }
        
        var priorityName = Priority switch
        {
            1 => "HOLIDAY/SUSPENSION",
            2 => "LEAVE/MISSED LOG",
            3 => "SUBSTITUTION",
            4 => "EXAM SCHEDULE",
            5 => "CLASS SCHEDULE",
            _ => "REGULAR SCHEDULE"
        };

        return $"[{priorityName}] {ScheduleDetails} - {AttendanceStatus.ToUpper()}";
    }
}

/// <summary>
/// Result of checking if employee should be blocked due to substitution
/// </summary>
public class SubstitutionBlockCheck
{
    public bool IsBlocked { get; set; }
    public long SubstitutionId { get; set; }
    public int SubstituteEmployeeId { get; set; }
    public string? SubstituteName { get; set; }
    public string? SubjectName { get; set; }
    public string? Section { get; set; }
    public TimeSpan StartTime { get; set; }
    public TimeSpan EndTime { get; set; }
    public string? Room { get; set; }
    public string? Reason { get; set; }
}

/// <summary>
/// Information about transferred schedule for substitute employee
/// </summary>
public class SubstituteScheduleTransfer
{
    public long SubstitutionId { get; set; }
    public int OriginalEmployeeId { get; set; }
    public string? OriginalEmployeeName { get; set; }
    public string? SubjectName { get; set; }
    public string? Section { get; set; }
    public TimeSpan StartTime { get; set; }
    public TimeSpan EndTime { get; set; }
    public string? Room { get; set; }
    public string? Reason { get; set; }
}
