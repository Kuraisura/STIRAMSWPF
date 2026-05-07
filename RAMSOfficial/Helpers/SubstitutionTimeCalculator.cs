using System;
using System.Collections.Generic;
using System.Linq;

namespace RAMSOfficial.Helpers;

/// <summary>
/// Calculates time adjustments when an employee has an approved substitution.
/// Handles the scenario where an employee has multiple class schedules and one (typically the middle one) is substituted.
/// Example: Employee has 8:00-11:00, 11:00-2:00, 2:00-4:00 schedules
///          If 11:00-2:00 is substituted, this calculates -3 hours to subtract from their teaching load
/// </summary>
public class SubstitutionTimeCalculator
{
    /// <summary>
    /// Calculates the substitution time adjustment for an employee's schedule
    /// </summary>
    /// <param name="allSchedules">All class schedules for the employee on this day</param>
    /// <param name="substitutedSchedule">The specific schedule that was substituted</param>
    /// <returns>Time calculation result including hours to subtract</returns>
    public SubstitutionTimeResult CalculateSubstitutionTime(
        List<ScheduleTimeSlot> allSchedules,
        ScheduleTimeSlot substitutedSchedule)
    {
        var result = new SubstitutionTimeResult
        {
            SubstitutedSchedule = substitutedSchedule,
            AllSchedulesCount = allSchedules.Count
        };

        if (substitutedSchedule == null)
        {
            result.IsValid = false;
            result.ErrorMessage = "Substituted schedule cannot be null";
            return result;
        }

        if (allSchedules == null || !allSchedules.Any())
        {
            result.IsValid = false;
            result.ErrorMessage = "No schedules provided";
            return result;
        }

        // Calculate the duration of the substituted schedule
        var duration = substitutedSchedule.EndTime - substitutedSchedule.StartTime;
        result.SubstitutedHours = duration.TotalHours;
        result.SubstitutedDuration = duration;

        // Find remaining schedules (excluding the substituted one)
        result.RemainingSchedules = allSchedules
            .Where(s => !(s.StartTime == substitutedSchedule.StartTime && s.EndTime == substitutedSchedule.EndTime))
            .OrderBy(s => s.StartTime)
            .ToList();

        // Calculate total time across remaining schedules
        if (result.RemainingSchedules.Any())
        {
            var earliestStart = result.RemainingSchedules.Min(s => s.StartTime);
            var latestEnd = result.RemainingSchedules.Max(s => s.EndTime);
            
            result.EffectiveTimeIn = earliestStart;
            result.EffectiveTimeOut = latestEnd;
            result.TotalActiveHours = (latestEnd - earliestStart).TotalHours;
        }
        else
        {
            // All schedules are substituted
            result.EffectiveTimeIn = TimeSpan.Zero;
            result.EffectiveTimeOut = TimeSpan.Zero;
            result.TotalActiveHours = 0;
        }

        result.IsValid = true;
        return result;
    }

    /// <summary>
    /// Calculates the net teaching load adjustment when an employee has substituted classes
    /// but also picks up substitute duties
    /// </summary>
    public SubstitutionTimeResult CalculateNetTeachingLoad(
        List<ScheduleTimeSlot> ownSchedules,
        List<ScheduleTimeSlot> substitutedSchedules,
        List<ScheduleTimeSlot> substituteDuties)
    {
        var result = new SubstitutionTimeResult
        {
            AllSchedulesCount = ownSchedules.Count,
            IsValid = true
        };

        // Calculate total hours from own schedules
        double totalOwnHours = ownSchedules.Sum(s => (s.EndTime - s.StartTime).TotalHours);

        // Calculate total substituted hours (hours removed)
        double totalSubstitutedHours = substitutedSchedules.Sum(s => (s.EndTime - s.StartTime).TotalHours);

        // Calculate total substitute duty hours (hours added)
        double totalSubstituteDutyHours = substituteDuties.Sum(s => (s.EndTime - s.StartTime).TotalHours);

        result.TotalOwnHours = totalOwnHours;
        result.SubstitutedHours = totalSubstitutedHours;
        result.SubstituteDutyHours = totalSubstituteDutyHours;
        result.NetAdjustmentHours = totalSubstituteDutyHours - totalSubstitutedHours;
        result.TotalActiveHours = totalOwnHours - totalSubstitutedHours + totalSubstituteDutyHours;

        // Calculate effective time range
        var activeSchedules = new List<ScheduleTimeSlot>();
        activeSchedules.AddRange(ownSchedules.Where(s => 
            !substitutedSchedules.Any(sub => 
                sub.StartTime == s.StartTime && sub.EndTime == s.EndTime)));
        activeSchedules.AddRange(substituteDuties);

        if (activeSchedules.Any())
        {
            result.EffectiveTimeIn = activeSchedules.Min(s => s.StartTime);
            result.EffectiveTimeOut = activeSchedules.Max(s => s.EndTime);
        }

        result.RemainingSchedules = activeSchedules.OrderBy(s => s.StartTime).ToList();

        return result;
    }
}

/// <summary>
/// Represents a time slot for a class schedule
/// </summary>
public class ScheduleTimeSlot
{
    public TimeSpan StartTime { get; set; }
    public TimeSpan EndTime { get; set; }
    public string? SubjectName { get; set; }
    public string? Section { get; set; }
    public string? Room { get; set; }
    public bool IsSubstituteDuty { get; set; }
    public int? OriginalEmployeeId { get; set; }
    public string? OriginalEmployeeName { get; set; }

    public double DurationHours => (EndTime - StartTime).TotalHours;
    public TimeSpan Duration => EndTime - StartTime;

    public override string ToString()
    {
        var subjectInfo = !string.IsNullOrEmpty(SubjectName) ? $"{SubjectName} ({Section})" : "Unknown";
        var typeInfo = IsSubstituteDuty ? "[SUB DUTY]" : "";
        return $"{StartTime:hh\\:mm} - {EndTime:hh\\:mm} {subjectInfo} {typeInfo}".Trim();
    }
}

/// <summary>
/// Result of substitution time calculation
/// </summary>
public class SubstitutionTimeResult
{
    public bool IsValid { get; set; }
    public string? ErrorMessage { get; set; }

    // Input details
    public int AllSchedulesCount { get; set; }
    public ScheduleTimeSlot? SubstitutedSchedule { get; set; }
    public List<ScheduleTimeSlot> RemainingSchedules { get; set; } = new();

    // Time calculations
    public double SubstitutedHours { get; set; }
    public TimeSpan SubstitutedDuration { get; set; }
    public TimeSpan? EffectiveTimeIn { get; set; }
    public TimeSpan? EffectiveTimeOut { get; set; }
    public double TotalActiveHours { get; set; }

    // Advanced calculations (for net teaching load)
    public double TotalOwnHours { get; set; }
    public double SubstituteDutyHours { get; set; }
    public double NetAdjustmentHours { get; set; }

    /// <summary>
    /// Gets a display string showing the hours to subtract
    /// Example: "-3 hours (Substitution)"
    /// </summary>
    public string GetSubstitutionAdjustmentDisplay()
    {
        if (!IsValid || SubstitutedHours == 0)
            return string.Empty;

        return $"-{SubstitutedHours:F1} hours (Substitution)";
    }

    /// <summary>
    /// Gets a display string showing the net adjustment
    /// Example: "+2 hours (Net: 2 sub duties - 0 substituted)"
    /// </summary>
    public string GetNetAdjustmentDisplay()
    {
        if (!IsValid)
            return string.Empty;

        if (NetAdjustmentHours == 0)
            return "No net adjustment";

        var sign = NetAdjustmentHours > 0 ? "+" : "";
        return $"{sign}{NetAdjustmentHours:F1} hours (Net: {SubstituteDutyHours:F1} sub duties - {SubstitutedHours:F1} substituted)";
    }
}
