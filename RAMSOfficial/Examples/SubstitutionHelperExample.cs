using System;
using System.Collections.Generic;
using RAMSOfficial.Helpers;

namespace RAMSOfficial.Examples;

/// <summary>
/// Example demonstrating how to use the substitution helper classes
/// Scenario: Brian Matthew Alcala has three class schedules
/// - 8:00-11:00
/// - 11:00-2:00 (SUBSTITUTED)
/// - 2:00-4:00
/// He taps in at 8:00 AM and out at 4:00 PM
/// </summary>
public class SubstitutionHelperExample
{
    public static void DemonstrateUsage()
    {
        Console.WriteLine("========================================");
        Console.WriteLine("SUBSTITUTION HELPER DEMONSTRATION");
        Console.WriteLine("========================================\n");

        // STEP 1: Set up the scenario
        var allSchedules = new List<ScheduleTimeSlot>
        {
            new ScheduleTimeSlot
            {
                StartTime = new TimeSpan(8, 0, 0),
                EndTime = new TimeSpan(11, 0, 0),
                SubjectName = "Mathematics 101",
                Section = "A",
                Room = "Room 201"
            },
            new ScheduleTimeSlot
            {
                StartTime = new TimeSpan(11, 0, 0),
                EndTime = new TimeSpan(14, 0, 0), // 2:00 PM
                SubjectName = "Physics 102",
                Section = "B",
                Room = "Room 305"
            },
            new ScheduleTimeSlot
            {
                StartTime = new TimeSpan(14, 0, 0), // 2:00 PM
                EndTime = new TimeSpan(16, 0, 0),   // 4:00 PM
                SubjectName = "Chemistry 103",
                Section = "C",
                Room = "Room 401"
            }
        };

        var substitutedSchedule = allSchedules[1]; // Middle one: 11:00-2:00

        var substitutedSchedules = new List<ScheduleTimeSlot> { substitutedSchedule };
        var substituteDuties = new List<ScheduleTimeSlot>(); // No substitute duties for this example

        var actualTimeIn = new DateTime(2024, 1, 15, 8, 0, 0);
        var actualTimeOut = new DateTime(2024, 1, 15, 16, 0, 0); // 4:00 PM

        Console.WriteLine("SCENARIO:");
        Console.WriteLine($"Employee: Brian Matthew Alcala");
        Console.WriteLine($"Date: {actualTimeIn:yyyy-MM-dd}");
        Console.WriteLine($"Actual Time In: {actualTimeIn:HH:mm}");
        Console.WriteLine($"Actual Time Out: {actualTimeOut:HH:mm}\n");

        Console.WriteLine("CLASS SCHEDULES:");
        for (int i = 0; i < allSchedules.Count; i++)
        {
            var schedule = allSchedules[i];
            string subNote = schedule == substitutedSchedule ? " [SUBSTITUTED]" : "";
            Console.WriteLine($"  {i + 1}. {schedule.StartTime:hh\\:mm}-{schedule.EndTime:hh\\:mm} " +
                            $"{schedule.SubjectName} ({schedule.Section}){subNote}");
        }
        Console.WriteLine();

        // STEP 2: Use MiddleScheduleDetector
        Console.WriteLine("========================================");
        Console.WriteLine("CLASS 1: MiddleScheduleDetector");
        Console.WriteLine("========================================\n");

        var detector = new MiddleScheduleDetector();
        var detection = detector.DetectMiddleSchedule(allSchedules, substitutedSchedule);

        Console.WriteLine($"Is Valid: {detection.IsValid}");
        Console.WriteLine($"Has 3+ Schedules: {detection.HasThreeOrMoreSchedules}");
        Console.WriteLine($"Is Middle Schedule: {detection.IsMiddleSchedule}");
        Console.WriteLine($"Position: {detection.PositionDescription}");
        Console.WriteLine($"Index: {detection.SubstitutedIndex} (0-based)");
        Console.WriteLine($"\nDescription: {detection.GetDescription()}\n");

        var pattern = detector.AnalyzeSchedulePattern(allSchedules);
        Console.WriteLine($"Schedule Pattern: {pattern.GetSummary()}\n");

        // STEP 3: Use SubstitutionTimeCalculator
        Console.WriteLine("========================================");
        Console.WriteLine("CLASS 2: SubstitutionTimeCalculator");
        Console.WriteLine("========================================\n");

        var calculator = new SubstitutionTimeCalculator();
        var timeResult = calculator.CalculateSubstitutionTime(allSchedules, substitutedSchedule);

        Console.WriteLine($"Is Valid: {timeResult.IsValid}");
        Console.WriteLine($"Substituted Hours: {timeResult.SubstitutedHours:F1} hours");
        Console.WriteLine($"Substituted Duration: {timeResult.SubstitutedDuration}");
        Console.WriteLine($"Effective Time In: {timeResult.EffectiveTimeIn:hh\\:mm}");
        Console.WriteLine($"Effective Time Out: {timeResult.EffectiveTimeOut:hh\\:mm}");
        Console.WriteLine($"Total Active Hours: {timeResult.TotalActiveHours:F1} hours");
        Console.WriteLine($"\nAdjustment Display: {timeResult.GetSubstitutionAdjustmentDisplay()}");
        Console.WriteLine($"\nRemaining Schedules:");
        foreach (var schedule in timeResult.RemainingSchedules)
        {
            Console.WriteLine($"  • {schedule}");
        }
        Console.WriteLine();

        // Calculate net teaching load
        var netResult = calculator.CalculateNetTeachingLoad(
            allSchedules, 
            substitutedSchedules, 
            substituteDuties);

        Console.WriteLine("NET TEACHING LOAD:");
        Console.WriteLine($"  Own Hours: {netResult.TotalOwnHours:F1}");
        Console.WriteLine($"  Substituted Hours: -{netResult.SubstitutedHours:F1}");
        Console.WriteLine($"  Substitute Duty Hours: +{netResult.SubstituteDutyHours:F1}");
        Console.WriteLine($"  Net Adjustment: {netResult.NetAdjustmentHours:F1}");
        Console.WriteLine($"  Total Active Hours: {netResult.TotalActiveHours:F1}\n");

        // STEP 4: Use AttendanceDisplayHelper
        Console.WriteLine("========================================");
        Console.WriteLine("CLASS 3: AttendanceDisplayHelper");
        Console.WriteLine("========================================\n");

        var displayHelper = new AttendanceDisplayHelper();
        var displayInfo = displayHelper.GenerateAttendanceDisplay(
            actualTimeIn,
            actualTimeOut,
            allSchedules,
            substitutedSchedules,
            substituteDuties);

        Console.WriteLine("DISPLAY INFORMATION:");
        Console.WriteLine($"Primary Display: {displayInfo.GetPrimaryDisplay()}");
        Console.WriteLine($"Adjustment: {displayInfo.GetAdjustmentDisplay()}");
        Console.WriteLine($"Has Substitution: {displayInfo.HasSubstitution}");
        Console.WriteLine($"Has Middle Schedule Substitution: {displayInfo.HasMiddleScheduleSubstitution}");
        Console.WriteLine($"Total Hours Reduced: -{displayInfo.TotalHoursReduced:F1}");
        Console.WriteLine($"Total Hours Added: +{displayInfo.TotalHoursAdded:F1}");
        Console.WriteLine($"Net Adjustment: {displayInfo.NetAdjustment:F1}\n");

        Console.WriteLine("QUICK SUMMARY:");
        Console.WriteLine($"  {displayHelper.GenerateQuickSummary(displayInfo)}\n");

        Console.WriteLine("NOTES:");
        foreach (var note in displayInfo.Notes)
        {
            Console.WriteLine($"  {note}");
        }
        Console.WriteLine();

        Console.WriteLine("DETAILED BREAKDOWN:");
        Console.WriteLine(displayHelper.GenerateDetailedBreakdown(displayInfo));

        // BONUS: Demonstrate with substitute duties
        Console.WriteLine("\n========================================");
        Console.WriteLine("BONUS: WITH SUBSTITUTE DUTIES");
        Console.WriteLine("========================================\n");

        var substituteDutiesBonus = new List<ScheduleTimeSlot>
        {
            new ScheduleTimeSlot
            {
                StartTime = new TimeSpan(9, 0, 0),
                EndTime = new TimeSpan(10, 0, 0),
                SubjectName = "English 101",
                Section = "D",
                Room = "Room 105",
                IsSubstituteDuty = true,
                OriginalEmployeeId = 999,
                OriginalEmployeeName = "John Doe"
            }
        };

        var displayInfo2 = displayHelper.GenerateAttendanceDisplay(
            actualTimeIn,
            actualTimeOut,
            allSchedules,
            substitutedSchedules,
            substituteDutiesBonus);

        Console.WriteLine($"Quick Summary: {displayHelper.GenerateQuickSummary(displayInfo2)}\n");
        Console.WriteLine($"Adjustment: {displayInfo2.GetAdjustmentDisplay()}\n");
        Console.WriteLine(displayHelper.GenerateDetailedBreakdown(displayInfo2));

        Console.WriteLine("========================================");
        Console.WriteLine("DEMONSTRATION COMPLETE");
        Console.WriteLine("========================================");
    }
}
