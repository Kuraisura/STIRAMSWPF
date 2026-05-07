using System;
using System.Collections.Generic;
using System.Linq;

namespace RAMSOfficial.Helpers;

/// <summary>
/// Detects if a substituted schedule is in the "middle" of an employee's daily schedule.
/// This logic applies when an employee has 3 or more class schedules and the substituted one 
/// is neither the first nor the last.
/// Example: 8:00-11:00, 11:00-2:00, 2:00-4:00 ? 11:00-2:00 is the middle schedule
/// </summary>
public class MiddleScheduleDetector
{
    /// <summary>
    /// Analyzes whether the substituted schedule is in the middle position
    /// </summary>
    /// <param name="allSchedules">All class schedules for the employee on this day (sorted by time)</param>
    /// <param name="substitutedSchedule">The schedule that was substituted</param>
    /// <returns>Detection result with position information</returns>
    public MiddleScheduleDetectionResult DetectMiddleSchedule(
        List<ScheduleTimeSlot> allSchedules,
        ScheduleTimeSlot substitutedSchedule)
    {
        var result = new MiddleScheduleDetectionResult
        {
            SubstitutedSchedule = substitutedSchedule,
            TotalScheduleCount = allSchedules?.Count ?? 0
        };

        if (allSchedules == null || !allSchedules.Any())
        {
            result.IsValid = false;
            result.ErrorMessage = "No schedules provided";
            return result;
        }

        if (substitutedSchedule == null)
        {
            result.IsValid = false;
            result.ErrorMessage = "Substituted schedule cannot be null";
            return result;
        }

        // Sort schedules by start time
        var sortedSchedules = allSchedules.OrderBy(s => s.StartTime).ToList();
        result.SortedSchedules = sortedSchedules;

        // Find the index of the substituted schedule
        int substitutedIndex = -1;
        for (int i = 0; i < sortedSchedules.Count; i++)
        {
            if (sortedSchedules[i].StartTime == substitutedSchedule.StartTime &&
                sortedSchedules[i].EndTime == substitutedSchedule.EndTime)
            {
                substitutedIndex = i;
                break;
            }
        }

        if (substitutedIndex == -1)
        {
            result.IsValid = false;
            result.ErrorMessage = "Substituted schedule not found in the list";
            return result;
        }

        result.SubstitutedIndex = substitutedIndex;
        result.IsValid = true;

        // Determine position
        if (sortedSchedules.Count < 3)
        {
            result.HasThreeOrMoreSchedules = false;
            result.IsMiddleSchedule = false;
            result.PositionDescription = sortedSchedules.Count == 1 ? "Only schedule" :
                (substitutedIndex == 0 ? "First of two" : "Last of two");
        }
        else
        {
            result.HasThreeOrMoreSchedules = true;
            bool isFirst = substitutedIndex == 0;
            bool isLast = substitutedIndex == sortedSchedules.Count - 1;
            
            result.IsMiddleSchedule = !isFirst && !isLast;
            
            if (isFirst)
            {
                result.PositionDescription = "First schedule";
            }
            else if (isLast)
            {
                result.PositionDescription = "Last schedule";
            }
            else
            {
                result.PositionDescription = $"Middle schedule (position {substitutedIndex + 1} of {sortedSchedules.Count})";
            }
        }

        // Get schedules before and after
        if (substitutedIndex > 0)
        {
            result.SchedulesBefore = sortedSchedules.Take(substitutedIndex).ToList();
        }

        if (substitutedIndex < sortedSchedules.Count - 1)
        {
            result.SchedulesAfter = sortedSchedules.Skip(substitutedIndex + 1).ToList();
        }

        return result;
    }

    /// <summary>
    /// Determines if the middle schedule logic should apply
    /// (employee has 3+ schedules and substituted one is in the middle)
    /// </summary>
    public bool ShouldApplyMiddleScheduleLogic(
        List<ScheduleTimeSlot> allSchedules,
        ScheduleTimeSlot substitutedSchedule)
    {
        var detection = DetectMiddleSchedule(allSchedules, substitutedSchedule);
        return detection.IsValid && detection.HasThreeOrMoreSchedules && detection.IsMiddleSchedule;
    }

    /// <summary>
    /// Gets a comprehensive analysis of the schedule pattern
    /// </summary>
    public SchedulePatternAnalysis AnalyzeSchedulePattern(List<ScheduleTimeSlot> allSchedules)
    {
        var analysis = new SchedulePatternAnalysis
        {
            TotalSchedules = allSchedules?.Count ?? 0
        };

        if (allSchedules == null || !allSchedules.Any())
        {
            analysis.Pattern = SchedulePattern.NoSchedules;
            return analysis;
        }

        var sorted = allSchedules.OrderBy(s => s.StartTime).ToList();
        analysis.EarliestStart = sorted.First().StartTime;
        analysis.LatestEnd = sorted.Last().EndTime;
        analysis.TotalSpan = analysis.LatestEnd - analysis.EarliestStart;

        if (sorted.Count == 1)
        {
            analysis.Pattern = SchedulePattern.SingleSchedule;
        }
        else if (sorted.Count == 2)
        {
            analysis.Pattern = SchedulePattern.TwoSchedules;
        }
        else if (sorted.Count >= 3)
        {
            analysis.Pattern = SchedulePattern.ThreeOrMoreSchedules;
            
            // Check if schedules are continuous (back-to-back)
            bool isContinuous = true;
            for (int i = 0; i < sorted.Count - 1; i++)
            {
                if (sorted[i].EndTime != sorted[i + 1].StartTime)
                {
                    isContinuous = false;
                    break;
                }
            }
            analysis.IsContinuous = isContinuous;
        }

        // Calculate gaps between schedules
        for (int i = 0; i < sorted.Count - 1; i++)
        {
            var gap = sorted[i + 1].StartTime - sorted[i].EndTime;
            if (gap > TimeSpan.Zero)
            {
                analysis.Gaps.Add(new ScheduleGap
                {
                    AfterSchedule = sorted[i],
                    BeforeSchedule = sorted[i + 1],
                    Duration = gap
                });
            }
        }

        return analysis;
    }
}

/// <summary>
/// Result of middle schedule detection
/// </summary>
public class MiddleScheduleDetectionResult
{
    public bool IsValid { get; set; }
    public string? ErrorMessage { get; set; }

    // Input details
    public int TotalScheduleCount { get; set; }
    public ScheduleTimeSlot? SubstitutedSchedule { get; set; }
    public List<ScheduleTimeSlot> SortedSchedules { get; set; } = new();

    // Detection results
    public bool HasThreeOrMoreSchedules { get; set; }
    public bool IsMiddleSchedule { get; set; }
    public int SubstitutedIndex { get; set; } = -1;
    public string? PositionDescription { get; set; }

    // Surrounding schedules
    public List<ScheduleTimeSlot> SchedulesBefore { get; set; } = new();
    public List<ScheduleTimeSlot> SchedulesAfter { get; set; } = new();

    /// <summary>
    /// Gets a user-friendly description of the schedule situation
    /// </summary>
    public string GetDescription()
    {
        if (!IsValid)
            return ErrorMessage ?? "Invalid detection";

        if (!HasThreeOrMoreSchedules)
        {
            return $"Employee has {TotalScheduleCount} schedule(s). Middle schedule logic does not apply.";
        }

        if (IsMiddleSchedule)
        {
            return $"Employee has {TotalScheduleCount} schedules. The substituted schedule at {SubstitutedSchedule?.StartTime:hh\\:mm}-{SubstitutedSchedule?.EndTime:hh\\:mm} is in the middle position. " +
                   $"There are {SchedulesBefore.Count} schedule(s) before and {SchedulesAfter.Count} schedule(s) after.";
        }

        return $"Employee has {TotalScheduleCount} schedules, but the substituted schedule is {PositionDescription?.ToLower()}.";
    }
}

/// <summary>
/// Analysis of the overall schedule pattern for a day
/// </summary>
public class SchedulePatternAnalysis
{
    public int TotalSchedules { get; set; }
    public SchedulePattern Pattern { get; set; }
    public TimeSpan EarliestStart { get; set; }
    public TimeSpan LatestEnd { get; set; }
    public TimeSpan TotalSpan { get; set; }
    public bool IsContinuous { get; set; }
    public List<ScheduleGap> Gaps { get; set; } = new();

    public string GetSummary()
    {
        return Pattern switch
        {
            SchedulePattern.NoSchedules => "No schedules",
            SchedulePattern.SingleSchedule => $"Single schedule: {EarliestStart:hh\\:mm}-{LatestEnd:hh\\:mm}",
            SchedulePattern.TwoSchedules => $"Two schedules: {EarliestStart:hh\\:mm}-{LatestEnd:hh\\:mm} (Total span: {TotalSpan.TotalHours:F1}h)",
            SchedulePattern.ThreeOrMoreSchedules => 
                $"{TotalSchedules} schedules: {EarliestStart:hh\\:mm}-{LatestEnd:hh\\:mm} " +
                $"({(IsContinuous ? "continuous" : $"{Gaps.Count} gap(s)")})",
            _ => "Unknown pattern"
        };
    }
}

/// <summary>
/// Represents a gap between two consecutive schedules
/// </summary>
public class ScheduleGap
{
    public ScheduleTimeSlot? AfterSchedule { get; set; }
    public ScheduleTimeSlot? BeforeSchedule { get; set; }
    public TimeSpan Duration { get; set; }

    public override string ToString()
    {
        return $"Gap: {AfterSchedule?.EndTime:hh\\:mm} to {BeforeSchedule?.StartTime:hh\\:mm} ({Duration.TotalMinutes} minutes)";
    }
}

/// <summary>
/// Enumeration of possible schedule patterns
/// </summary>
public enum SchedulePattern
{
    NoSchedules,
    SingleSchedule,
    TwoSchedules,
    ThreeOrMoreSchedules
}
