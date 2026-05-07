// ================================================================================================
// QUICK REFERENCE: Substitution Helper Classes
// ================================================================================================
// Scenario: Employee has 3 schedules (8-11, 11-2, 2-4), middle one (11-2) is substituted
// Employee taps in at 8:00 AM and out at 4:00 PM
// Expected: Display "8:00-4:00" with "-3 hours (substitution)" note
// ================================================================================================

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace RAMSOfficial.Helpers;

/// <summary>
/// Quick reference examples for using the substitution helper classes
/// </summary>
public static class SubstitutionHelperQuickReference
{
    // ================================================================================================
    // BASIC USAGE
    // ================================================================================================

    public static void BasicUsageExample()
    {
        // Step 1: Create schedule objects
        var allSchedules = new List<ScheduleTimeSlot>
        {
            new() { StartTime = new TimeSpan(8, 0, 0), EndTime = new TimeSpan(11, 0, 0), SubjectName = "Math 101", Section = "A" },
            new() { StartTime = new TimeSpan(11, 0, 0), EndTime = new TimeSpan(14, 0, 0), SubjectName = "Physics 102", Section = "B" },
            new() { StartTime = new TimeSpan(14, 0, 0), EndTime = new TimeSpan(16, 0, 0), SubjectName = "Chemistry 103", Section = "C" }
        };

        var substitutedSchedule = allSchedules[1]; // The middle one: 11:00-14:00

        // Step 2: Check if this is a middle schedule (3+ schedules and middle position)
        var detector = new MiddleScheduleDetector();
        var isMiddle = detector.ShouldApplyMiddleScheduleLogic(allSchedules, substitutedSchedule);
        // Returns: true (employee has 3 schedules, substituted one is in the middle)

        // Step 3: Calculate time adjustment
        var calculator = new SubstitutionTimeCalculator();
        var timeResult = calculator.CalculateSubstitutionTime(allSchedules, substitutedSchedule);
        // timeResult.SubstitutedHours = 3.0
        // timeResult.EffectiveTimeIn = 08:00
        // timeResult.EffectiveTimeOut = 16:00

        // Step 4: Generate display
        var displayHelper = new AttendanceDisplayHelper();
        var displayInfo = displayHelper.GenerateAttendanceDisplay(
            new DateTime(2024, 1, 15, 8, 0, 0),   // Actual time in
            new DateTime(2024, 1, 15, 16, 0, 0),  // Actual time out
            allSchedules,
            new List<ScheduleTimeSlot> { substitutedSchedule },
            new List<ScheduleTimeSlot>() // No substitute duties
        );

        // Step 5: Display to user
        string display = displayHelper.GenerateQuickSummary(displayInfo);
        // Output: "08:00-16:00 (-3.0h: 11:00-14:00 substituted)"
    }

    // ================================================================================================
    // ADVANCED USAGE: Net Teaching Load
    // ================================================================================================

    public static void NetTeachingLoadExample()
    {
        // Scenario: Employee has own schedules, some substituted, plus picked up substitute duties
        var ownSchedules = new List<ScheduleTimeSlot>
        {
            new() { StartTime = new TimeSpan(8, 0, 0), EndTime = new TimeSpan(11, 0, 0) },
            new() { StartTime = new TimeSpan(11, 0, 0), EndTime = new TimeSpan(14, 0, 0) },
            new() { StartTime = new TimeSpan(14, 0, 0), EndTime = new TimeSpan(16, 0, 0) }
        };

        var substitutedSchedules = new List<ScheduleTimeSlot>
        {
            ownSchedules[1] // 11:00-14:00 substituted (-3 hours)
        };

        var substituteDuties = new List<ScheduleTimeSlot>
        {
            new() 
            { 
                StartTime = new TimeSpan(9, 0, 0), 
                EndTime = new TimeSpan(10, 0, 0),
                IsSubstituteDuty = true,
                OriginalEmployeeName = "John Doe"
                // +1 hour (covering for John)
            }
        };

        var calculator = new SubstitutionTimeCalculator();
        var netResult = calculator.CalculateNetTeachingLoad(ownSchedules, substitutedSchedules, substituteDuties);
        // netResult.TotalOwnHours = 8.0
        // netResult.SubstitutedHours = 3.0 (removed)
        // netResult.SubstituteDutyHours = 1.0 (added)
        // netResult.NetAdjustmentHours = -2.0 (1.0 - 3.0)
        // netResult.TotalActiveHours = 6.0 (8 - 3 + 1)
    }

    // ================================================================================================
    // DISPLAY EXAMPLES
    // ================================================================================================

    public static void DisplayExamples()
    {
        var allSchedules = new List<ScheduleTimeSlot>
        {
            new() { StartTime = new TimeSpan(8, 0, 0), EndTime = new TimeSpan(11, 0, 0), SubjectName = "Math 101", Section = "A" },
            new() { StartTime = new TimeSpan(11, 0, 0), EndTime = new TimeSpan(14, 0, 0), SubjectName = "Physics 102", Section = "B" },
            new() { StartTime = new TimeSpan(14, 0, 0), EndTime = new TimeSpan(16, 0, 0), SubjectName = "Chemistry 103", Section = "C" }
        };

        var displayHelper = new AttendanceDisplayHelper();
        var displayInfo = displayHelper.GenerateAttendanceDisplay(
            new DateTime(2024, 1, 15, 8, 0, 0),
            new DateTime(2024, 1, 15, 16, 0, 0),
            allSchedules,
            new List<ScheduleTimeSlot> { allSchedules[1] },
            new List<ScheduleTimeSlot>()
        );

        // Quick one-line summary
        string quickSummary = displayHelper.GenerateQuickSummary(displayInfo);
        // "08:00-16:00 (-3.0h: 11:00-14:00 substituted)"

        // Primary display (time range)
        string primaryDisplay = displayInfo.GetPrimaryDisplay();
        // "08:00-16:00"

        // Adjustment note
        string adjustment = displayInfo.GetAdjustmentDisplay();
        // "-3.0 hours (substitution)"

        // Detailed breakdown
        string details = displayHelper.GenerateDetailedBreakdown(displayInfo);
        /*
        === ATTENDANCE DETAILS ===
        Time In:  08:00
        Time Out: 16:00

        SUBSTITUTED CLASSES:
          • 11:00-14:00 (-3.0h) Middle schedule (position 2 of 3)
            Physics 102 (B)
          Total Reduced: -3.0 hours

        NET ADJUSTMENT:
          -3.0 hours (substitution)
          Active Teaching Hours: 5.0h
        */
    }

    // ================================================================================================
    // PATTERN DETECTION
    // ================================================================================================

    public static void PatternDetectionExample()
    {
        var allSchedules = new List<ScheduleTimeSlot>
        {
            new() { StartTime = new TimeSpan(8, 0, 0), EndTime = new TimeSpan(11, 0, 0) },
            new() { StartTime = new TimeSpan(11, 0, 0), EndTime = new TimeSpan(14, 0, 0) },
            new() { StartTime = new TimeSpan(14, 0, 0), EndTime = new TimeSpan(16, 0, 0) }
        };

        var detector = new MiddleScheduleDetector();
        var pattern = detector.AnalyzeSchedulePattern(allSchedules);
        // pattern.Pattern = SchedulePattern.ThreeOrMoreSchedules
        // pattern.TotalSchedules = 3
        // pattern.EarliestStart = 08:00
        // pattern.LatestEnd = 16:00
        // pattern.IsContinuous = true

        string patternSummary = pattern.GetSummary();
        // "3 schedules: 08:00-16:00 (continuous)"
    }

    // ================================================================================================
    // INTEGRATION WITH EXISTING CODE
    // ================================================================================================

    // Example: In your attendance processing
    public static async Task ProcessAttendanceExample(int employeeId, DateTime date, string connectionString)
    {
        // Get schedules from existing service
        var scheduleService = new Services.EmployeeScheduleService(connectionString);
        var scheduleResult = await scheduleService.GetEmployeeScheduleAsync(employeeId, date);
        
        // Get actual tap times (placeholder - implement based on your needs)
        var timeIn = await GetTimeInAsync(employeeId, date);
        var timeOut = await GetTimeOutAsync(employeeId, date);
        
        // Get substitution details (placeholder - implement based on your needs)
        var allSchedules = await GetAllSchedulesAsync(employeeId, date);
        var substitutedSchedules = await GetSubstitutedSchedulesAsync(employeeId, date);
        var substituteDuties = await GetSubstituteDutiesAsync(employeeId, date);
        
        // Generate display
        var displayHelper = new AttendanceDisplayHelper();
        var displayInfo = displayHelper.GenerateAttendanceDisplay(
            timeIn,
            timeOut,
            allSchedules,
            substitutedSchedules,
            substituteDuties
        );
        
        // Update UI (example - adjust based on your UI framework)
        // lblTimeRange.Text = displayInfo.GetPrimaryDisplay();
        // lblAdjustment.Text = displayInfo.GetAdjustmentDisplay();
        // txtNotes.Text = string.Join("\n", displayInfo.Notes);
    }

    // ================================================================================================
    // COMMON USE CASES
    // ================================================================================================

    // Use Case 1: Check if middle schedule logic applies
    public static bool CheckMiddleScheduleLogic(List<ScheduleTimeSlot> allSchedules, ScheduleTimeSlot substitutedSchedule)
    {
        var detector = new MiddleScheduleDetector();
        bool shouldApplyLogic = detector.ShouldApplyMiddleScheduleLogic(allSchedules, substitutedSchedule);
        
        if (shouldApplyLogic)
        {
            // This employee has 3+ schedules and the substituted one is in the middle
            // Display full time range (8:00-4:00) with substitution note
            return true;
        }
        
        return false;
    }

    // Use Case 2: Calculate hours to transfer to substitute
    public static double CalculateHoursToTransfer(List<ScheduleTimeSlot> allSchedules, ScheduleTimeSlot substitutedSchedule)
    {
        var calculator = new SubstitutionTimeCalculator();
        var timeCalc = calculator.CalculateSubstitutionTime(allSchedules, substitutedSchedule);
        double hoursToTransfer = timeCalc.SubstitutedHours; // 3.0
        // Transfer these hours to the substitute employee's record
        return hoursToTransfer;
    }

    // Use Case 3: Generate UI display
    public static (string forUI, string forTooltip) GenerateUIDisplay(
        DateTime? timeIn, 
        DateTime? timeOut, 
        List<ScheduleTimeSlot> schedules, 
        List<ScheduleTimeSlot> substituted, 
        List<ScheduleTimeSlot> duties)
    {
        var displayHelper = new AttendanceDisplayHelper();
        var display = displayHelper.GenerateAttendanceDisplay(timeIn, timeOut, schedules, substituted, duties);
        string forUI = displayHelper.GenerateQuickSummary(display);
        string forTooltip = displayHelper.GenerateDetailedBreakdown(display);
        return (forUI, forTooltip);
    }

    // ================================================================================================
    // HELPER METHODS (Placeholder implementations)
    // ================================================================================================

    private static async Task<DateTime?> GetTimeInAsync(int employeeId, DateTime date)
    {
        // TODO: Implement actual database query
        await Task.CompletedTask;
        return null;
    }

    private static async Task<DateTime?> GetTimeOutAsync(int employeeId, DateTime date)
    {
        // TODO: Implement actual database query
        await Task.CompletedTask;
        return null;
    }

    private static async Task<List<ScheduleTimeSlot>> GetAllSchedulesAsync(int employeeId, DateTime date)
    {
        // TODO: Implement actual database query
        await Task.CompletedTask;
        return new List<ScheduleTimeSlot>();
    }

    private static async Task<List<ScheduleTimeSlot>> GetSubstitutedSchedulesAsync(int employeeId, DateTime date)
    {
        // TODO: Implement actual database query
        await Task.CompletedTask;
        return new List<ScheduleTimeSlot>();
    }

    private static async Task<List<ScheduleTimeSlot>> GetSubstituteDutiesAsync(int employeeId, DateTime date)
    {
        // TODO: Implement actual database query
        await Task.CompletedTask;
        return new List<ScheduleTimeSlot>();
    }

    // ================================================================================================
    // PROPERTY ACCESS EXAMPLES
    // ================================================================================================

    public static void PropertyAccessExamples()
    {
        // Example setup
        var allSchedules = new List<ScheduleTimeSlot>
        {
            new() { StartTime = new TimeSpan(8, 0, 0), EndTime = new TimeSpan(11, 0, 0) },
            new() { StartTime = new TimeSpan(11, 0, 0), EndTime = new TimeSpan(14, 0, 0) },
            new() { StartTime = new TimeSpan(14, 0, 0), EndTime = new TimeSpan(16, 0, 0) }
        };
        var substitutedSchedule = allSchedules[1];

        var calculator = new SubstitutionTimeCalculator();
        var detector = new MiddleScheduleDetector();
        var displayHelper = new AttendanceDisplayHelper();

        var timeResult = calculator.CalculateSubstitutionTime(allSchedules, substitutedSchedule);
        var detection = detector.DetectMiddleSchedule(allSchedules, substitutedSchedule);
        var displayInfo = displayHelper.GenerateAttendanceDisplay(
            new DateTime(2024, 1, 15, 8, 0, 0),
            new DateTime(2024, 1, 15, 16, 0, 0),
            allSchedules,
            new List<ScheduleTimeSlot> { substitutedSchedule },
            new List<ScheduleTimeSlot>()
        );

        // From SubstitutionTimeResult:
        var substitutedHours = timeResult.SubstitutedHours;        // Hours being substituted (e.g., 3.0)
        var effectiveTimeIn = timeResult.EffectiveTimeIn;         // Effective start time after substitution
        var effectiveTimeOut = timeResult.EffectiveTimeOut;        // Effective end time after substitution
        var totalActiveHours = timeResult.TotalActiveHours;        // Total working hours after adjustments
        var netAdjustmentHours = timeResult.NetAdjustmentHours;      // Net change in teaching load

        // From MiddleScheduleDetectionResult:
        var isMiddleSchedule = detection.IsMiddleSchedule;         // true if substituted schedule is in middle
        var hasThreeOrMore = detection.HasThreeOrMoreSchedules;  // true if employee has 3+ schedules
        var positionDesc = detection.PositionDescription;      // "Middle schedule (position 2 of 3)"
        var substitutedIndex = detection.SubstitutedIndex;         // 1 (0-based index)

        // From AttendanceDisplayInfo:
        var displayTimeIn = displayInfo.DisplayTimeIn;          // "08:00"
        var displayTimeOut = displayInfo.DisplayTimeOut;         // "16:00"
        var totalHoursReduced = displayInfo.TotalHoursReduced;      // 3.0
        var totalHoursAdded = displayInfo.TotalHoursAdded;        // 0.0 (or hours from substitute duties)
        var netAdjustment = displayInfo.NetAdjustment;          // -3.0
        var timeAdjustmentNote = displayInfo.TimeAdjustmentNote;     // "-3.0 hours (substitution)"
        var hasMiddleSub = displayInfo.HasMiddleScheduleSubstitution; // true
    }
}
