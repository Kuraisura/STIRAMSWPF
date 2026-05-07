using System;
using System.Threading.Tasks;
using RAMSOfficial.Helpers;

namespace RAMSOfficial.Examples;

/// <summary>
/// Demonstrates the dynamic schedule update scenario:
/// 1. Brian Matthew Alcala has a schedule 8:00-11:00 AM
/// 2. Brian taps in at 8:00 AM
/// 3. Mark Crysler Baddo requests a substitution for his 12:00-3:00 PM class
/// 4. Substitution is approved and assigned to Brian
/// 5. Brian's expected schedule dynamically updates to 8:00-3:00 PM
/// 6. When Brian taps out, it's validated against the new 3:00 PM time
/// </summary>
public static class DynamicScheduleUpdateExample
{
    public static async Task DemonstrateScenarioAsync(string connectionString)
    {
        Console.WriteLine("????????????????????????????????????????????????????????????????");
        Console.WriteLine("  DYNAMIC SCHEDULE UPDATE SCENARIO");
        Console.WriteLine("????????????????????????????????????????????????????????????????");
        Console.WriteLine();

        // Setup
        int brianEmployeeId = 123; // Brian Matthew Alcala
        int markEmployeeId = 456;  // Mark Crysler Baddo
        DateTime today = DateTime.Today;

        Console.WriteLine("INITIAL SCENARIO:");
        Console.WriteLine($"  Brian Matthew Alcala (ID: {brianEmployeeId})");
        Console.WriteLine($"    Original Schedule: 8:00 AM - 11:00 AM (Math 101)");
        Console.WriteLine($"    Status: ? Already tapped IN at 8:00 AM");
        Console.WriteLine();
        Console.WriteLine($"  Mark Crysler Baddo (ID: {markEmployeeId})");
        Console.WriteLine($"    Original Schedule: 12:00 PM - 3:00 PM (Physics 102)");
        Console.WriteLine($"    Status: Requesting substitution");
        Console.WriteLine();

        // Step 1: Check Brian's current schedule BEFORE substitution
        Console.WriteLine("????????????????????????????????????????????????????????????????");
        Console.WriteLine("  STEP 1: Brian's Current Schedule (Before Substitution)");
        Console.WriteLine("????????????????????????????????????????????????????????????????");
        
        var updater = new DynamicScheduleUpdater(connectionString);
        var currentSchedule = await updater.GetCurrentExpectedScheduleAsync(brianEmployeeId, today);

        if (currentSchedule.HasSchedule)
        {
            Console.WriteLine($"  Expected Time IN:  {currentSchedule.ExpectedTimeIn:hh\\:mm tt}");
            Console.WriteLine($"  Expected Time OUT: {currentSchedule.ExpectedTimeOut:hh\\:mm tt}");
            Console.WriteLine($"  Total Schedules: {currentSchedule.TotalScheduleCount}");
            Console.WriteLine();
            
            Console.WriteLine("  Schedule Details:");
            foreach (var schedule in currentSchedule.CombinedSchedules)
            {
                var type = schedule.IsSubstituteDuty ? "[SUB DUTY]" : "[OWN CLASS]";
                Console.WriteLine($"    {type} {schedule.StartTime:hh\\:mm} - {schedule.EndTime:hh\\:mm} {schedule.SubjectName}");
            }
        }
        else
        {
            Console.WriteLine("  ? No schedule found!");
        }
        Console.WriteLine();

        // Step 2: Mark's substitution is APPROVED and assigned to Brian
        Console.WriteLine("????????????????????????????????????????????????????????????????");
        Console.WriteLine("  STEP 2: Substitution Approved - Updating Brian's Schedule");
        Console.WriteLine("????????????????????????????????????????????????????????????????");
        Console.WriteLine($"  ? Mark's substitution request APPROVED");
        Console.WriteLine($"  ? Brian Matthew Alcala assigned as substitute");
        Console.WriteLine($"  ? Substitute Duty: 12:00 PM - 3:00 PM");
        Console.WriteLine();

        // Update Brian's active attendance record
        var updateResult = await updater.UpdateActiveAttendanceScheduleAsync(
            brianEmployeeId,
            today,
            new TimeSpan(12, 0, 0),  // 12:00 PM
            new TimeSpan(15, 0, 0)   // 3:00 PM
        );

        Console.WriteLine("  UPDATE RESULT:");
        Console.WriteLine($"    Success: {updateResult.Success}");
        Console.WriteLine($"    Message: {updateResult.Message}");
        Console.WriteLine();

        if (updateResult.Success && updateResult.ScheduleChanged)
        {
            Console.WriteLine("  SCHEDULE CHANGES:");
            Console.WriteLine($"    Original Expected: {updateResult.OriginalExpectedTimeIn:hh\\:mm} - {updateResult.OriginalExpectedTimeOut:hh\\:mm}");
            Console.WriteLine($"    New Expected:      {updateResult.NewExpectedTimeIn:hh\\:mm} - {updateResult.NewExpectedTimeOut:hh\\:mm}");
            Console.WriteLine($"    Time Extended By:  +{updateResult.TimeExtensionMinutes} minutes");
            Console.WriteLine();

            Console.WriteLine("  NEW COMBINED SCHEDULE:");
            foreach (var schedule in updateResult.CombinedSchedules)
            {
                var type = schedule.IsSubstituteDuty ? "[SUB DUTY]" : "[OWN CLASS]";
                Console.WriteLine($"    {type} {schedule.StartTime:hh\\:mm} - {schedule.EndTime:hh\\:mm} {schedule.SubjectName}");
            }
        }
        Console.WriteLine();

        // Step 3: Verify Brian's updated schedule
        Console.WriteLine("????????????????????????????????????????????????????????????????");
        Console.WriteLine("  STEP 3: Verify Brian's Updated Schedule");
        Console.WriteLine("????????????????????????????????????????????????????????????????");
        
        var updatedSchedule = await updater.GetCurrentExpectedScheduleAsync(brianEmployeeId, today);

        Console.WriteLine($"  Expected Time IN:  {updatedSchedule.ExpectedTimeIn:hh\\:mm tt}");
        Console.WriteLine($"  Expected Time OUT: {updatedSchedule.ExpectedTimeOut:hh\\:mm tt}");
        Console.WriteLine($"  Has Substitute Duties: {updatedSchedule.HasSubstituteDuties}");
        Console.WriteLine();

        Console.WriteLine("  COMPLETE SCHEDULE:");
        foreach (var schedule in updatedSchedule.CombinedSchedules)
        {
            var type = schedule.IsSubstituteDuty ? "[SUB DUTY]" : "[OWN CLASS]";
            var extra = schedule.IsSubstituteDuty ? $" (for {schedule.OriginalEmployeeName})" : "";
            Console.WriteLine($"    {type} {schedule.StartTime:hh\\:mm} - {schedule.EndTime:hh\\:mm} {schedule.SubjectName}{extra}");
        }
        Console.WriteLine();

        // Step 4: Simulate Brian's Time OUT scenarios
        Console.WriteLine("????????????????????????????????????????????????????????????????");
        Console.WriteLine("  STEP 4: Time OUT Validation Scenarios");
        Console.WriteLine("????????????????????????????????????????????????????????????????");
        Console.WriteLine();

        // Scenario A: Brian taps out at 2:55 PM (Undertime)
        Console.WriteLine("  SCENARIO A: Brian taps out at 2:55 PM");
        Console.WriteLine($"    Expected Time OUT: {updatedSchedule.ExpectedTimeOut:hh\\:mm tt} (3:00 PM)");
        Console.WriteLine($"    Actual Time OUT:   2:55 PM");
        Console.WriteLine($"    Result: ? UNDERTIME (5 minutes early)");
        Console.WriteLine();

        // Scenario B: Brian taps out at 3:00 PM (On Time)
        Console.WriteLine("  SCENARIO B: Brian taps out at 3:00 PM");
        Console.WriteLine($"    Expected Time OUT: {updatedSchedule.ExpectedTimeOut:hh\\:mm tt} (3:00 PM)");
        Console.WriteLine($"    Actual Time OUT:   3:00 PM");
        Console.WriteLine($"    Result: ? ON TIME");
        Console.WriteLine();

        // Scenario C: Brian taps out at 3:01 PM (On Time - within grace period)
        Console.WriteLine("  SCENARIO C: Brian taps out at 3:01 PM");
        Console.WriteLine($"    Expected Time OUT: {updatedSchedule.ExpectedTimeOut:hh\\:mm tt} (3:00 PM)");
        Console.WriteLine($"    Actual Time OUT:   3:01 PM");
        Console.WriteLine($"    Result: ? ON TIME (within grace period)");
        Console.WriteLine();

        // Scenario D: Brian taps out at 3:05 PM (On Time - after expected)
        Console.WriteLine("  SCENARIO D: Brian taps out at 3:05 PM");
        Console.WriteLine($"    Expected Time OUT: {updatedSchedule.ExpectedTimeOut:hh\\:mm tt} (3:00 PM)");
        Console.WriteLine($"    Actual Time OUT:   3:05 PM");
        Console.WriteLine($"    Result: ? ON TIME (stayed after expected time)");
        Console.WriteLine();

        // Summary
        Console.WriteLine("????????????????????????????????????????????????????????????????");
        Console.WriteLine("  SUMMARY");
        Console.WriteLine("????????????????????????????????????????????????????????????????");
        Console.WriteLine();
        Console.WriteLine("  KEY POINTS:");
        Console.WriteLine("  1. ? Brian's original schedule: 8:00 AM - 11:00 AM");
        Console.WriteLine("  2. ? Brian tapped IN at 8:00 AM");
        Console.WriteLine("  3. ? Substitution approved for 12:00 PM - 3:00 PM");
        Console.WriteLine("  4. ? Schedule DYNAMICALLY UPDATED to: 8:00 AM - 3:00 PM");
        Console.WriteLine("  5. ? Next tap (Time OUT) validated against NEW schedule (3:00 PM)");
        Console.WriteLine();
        Console.WriteLine("  VALIDATION RULES:");
        Console.WriteLine("  • Time OUT before 3:00 PM = UNDERTIME");
        Console.WriteLine("  • Time OUT at 3:00 PM or after = ON TIME");
        Console.WriteLine();
        Console.WriteLine("????????????????????????????????????????????????????????????????");
    }

    /// <summary>
    /// Quick demonstration with sample data (no database required)
    /// </summary>
    public static void QuickDemo()
    {
        Console.WriteLine("????????????????????????????????????????????????????????????????");
        Console.WriteLine("  QUICK DEMO: Dynamic Schedule Update Logic");
        Console.WriteLine("????????????????????????????????????????????????????????????????");
        Console.WriteLine();

        // Brian's original schedule
        var originalSchedule = new ScheduleTimeSlot
        {
            StartTime = new TimeSpan(8, 0, 0),   // 8:00 AM
            EndTime = new TimeSpan(11, 0, 0),    // 11:00 AM
            SubjectName = "Math 101",
            Section = "A"
        };

        // Mark's class (being substituted by Brian)
        var substituteDuty = new ScheduleTimeSlot
        {
            StartTime = new TimeSpan(12, 0, 0),  // 12:00 PM
            EndTime = new TimeSpan(15, 0, 0),    // 3:00 PM
            SubjectName = "Physics 102",
            Section = "B",
            IsSubstituteDuty = true,
            OriginalEmployeeName = "Mark Crysler Baddo"
        };

        Console.WriteLine("ORIGINAL SCHEDULE:");
        Console.WriteLine($"  {originalSchedule.StartTime:hh\\:mm} - {originalSchedule.EndTime:hh\\:mm} {originalSchedule.SubjectName}");
        Console.WriteLine();

        Console.WriteLine("SUBSTITUTE DUTY ADDED:");
        Console.WriteLine($"  {substituteDuty.StartTime:hh\\:mm} - {substituteDuty.EndTime:hh\\:mm} {substituteDuty.SubjectName} (for {substituteDuty.OriginalEmployeeName})");
        Console.WriteLine();

        // Calculate combined time range
        var combinedStart = new[] { originalSchedule.StartTime, substituteDuty.StartTime }.Min();
        var combinedEnd = new[] { originalSchedule.EndTime, substituteDuty.EndTime }.Max();

        Console.WriteLine("COMBINED SCHEDULE:");
        Console.WriteLine($"  Expected Time IN:  {combinedStart:hh\\:mm tt}");
        Console.WriteLine($"  Expected Time OUT: {combinedEnd:hh\\:mm tt}");
        Console.WriteLine();

        Console.WriteLine("VALIDATION:");
        Console.WriteLine($"  ? Time IN at 8:00 AM ? Validated against {combinedStart:hh\\:mm tt}");
        Console.WriteLine($"  ? Time OUT at 2:55 PM ? Undertime (before {combinedEnd:hh\\:mm tt})");
        Console.WriteLine($"  ? Time OUT at 3:00 PM ? On Time (matches {combinedEnd:hh\\:mm tt})");
        Console.WriteLine($"  ? Time OUT at 3:01 PM ? On Time (after {combinedEnd:hh\\:mm tt})");
        Console.WriteLine();
        Console.WriteLine("????????????????????????????????????????????????????????????????");
    }
}
