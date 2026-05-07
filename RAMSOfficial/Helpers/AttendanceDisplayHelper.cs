using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RAMSOfficial.Helpers;

/// <summary>
/// Helper class to format attendance display when an employee has approved substitutions.
/// Handles the complex scenario where:
/// - Employee taps in at 8:00 AM and out at 4:00 PM
/// - Has schedules: 8:00-11:00, 11:00-2:00, 2:00-4:00
/// - Middle schedule (11:00-2:00) is substituted
/// Display should show: 8:00-4:00 with note about substitution and -3 hours adjustment
/// </summary>
public class AttendanceDisplayHelper
{
    private readonly SubstitutionTimeCalculator _timeCalculator;
    private readonly MiddleScheduleDetector _scheduleDetector;

    public AttendanceDisplayHelper()
    {
        _timeCalculator = new SubstitutionTimeCalculator();
        _scheduleDetector = new MiddleScheduleDetector();
    }

    /// <summary>
    /// Generates a comprehensive display for an employee's attendance with substitution information
    /// </summary>
    /// <param name="actualTimeIn">Actual time employee tapped in</param>
    /// <param name="actualTimeOut">Actual time employee tapped out</param>
    /// <param name="allSchedules">All class schedules for this day</param>
    /// <param name="substitutedSchedules">Schedules that were substituted</param>
    /// <param name="substituteDuties">Substitute duties employee picked up</param>
    /// <returns>Display information for attendance</returns>
    public AttendanceDisplayInfo GenerateAttendanceDisplay(
        DateTime? actualTimeIn,
        DateTime? actualTimeOut,
        List<ScheduleTimeSlot> allSchedules,
        List<ScheduleTimeSlot> substitutedSchedules,
        List<ScheduleTimeSlot> substituteDuties)
    {
        var displayInfo = new AttendanceDisplayInfo
        {
            ActualTimeIn = actualTimeIn,
            ActualTimeOut = actualTimeOut
        };

        if (allSchedules == null || !allSchedules.Any())
        {
            displayInfo.DisplayTimeIn = actualTimeIn?.ToString("HH:mm") ?? "--:--";
            displayInfo.DisplayTimeOut = actualTimeOut?.ToString("HH:mm") ?? "--:--";
            displayInfo.HasSubstitution = false;
            return displayInfo;
        }

        // Check if there are any substitutions
        bool hasSubstitutions = substitutedSchedules != null && substitutedSchedules.Any();
        bool hasSubstituteDuties = substituteDuties != null && substituteDuties.Any();

        displayInfo.HasSubstitution = hasSubstitutions || hasSubstituteDuties;

        if (!displayInfo.HasSubstitution)
        {
            // No substitutions - simple display
            displayInfo.DisplayTimeIn = actualTimeIn?.ToString("HH:mm") ?? "--:--";
            displayInfo.DisplayTimeOut = actualTimeOut?.ToString("HH:mm") ?? "--:--";
            return displayInfo;
        }

        // Calculate time adjustments
        var timeResult = _timeCalculator.CalculateNetTeachingLoad(
            allSchedules,
            substitutedSchedules ?? new List<ScheduleTimeSlot>(),
            substituteDuties ?? new List<ScheduleTimeSlot>());

        displayInfo.TimeCalculation = timeResult;

        // Generate primary display (actual tap times)
        displayInfo.DisplayTimeIn = actualTimeIn?.ToString("HH:mm") ?? "--:--";
        displayInfo.DisplayTimeOut = actualTimeOut?.ToString("HH:mm") ?? "--:--";

        // Build substitution details
        if (hasSubstitutions)
        {
            foreach (var sub in substitutedSchedules!)
            {
                // Check if this is a middle schedule
                var detection = _scheduleDetector.DetectMiddleSchedule(allSchedules, sub);
                
                displayInfo.SubstitutionDetails.Add(new SubstitutionDisplayDetail
                {
                    Schedule = sub,
                    IsMiddleSchedule = detection.IsMiddleSchedule,
                    PositionInfo = detection.PositionDescription ?? "Unknown position",
                    HoursReduced = sub.DurationHours
                });
            }

            displayInfo.TotalHoursReduced = substitutedSchedules.Sum(s => s.DurationHours);
        }

        // Build substitute duty details
        if (hasSubstituteDuties)
        {
            foreach (var duty in substituteDuties!)
            {
                displayInfo.SubstituteDutyDetails.Add(new SubstituteDutyDisplayDetail
                {
                    Schedule = duty,
                    HoursAdded = duty.DurationHours,
                    OriginalEmployeeName = duty.OriginalEmployeeName ?? "Unknown"
                });
            }

            displayInfo.TotalHoursAdded = substituteDuties.Sum(s => s.DurationHours);
        }

        // Generate notes/messages
        displayInfo.Notes = GenerateDisplayNotes(displayInfo, hasSubstitutions, hasSubstituteDuties);
        displayInfo.TimeAdjustmentNote = GenerateTimeAdjustmentNote(
            displayInfo.TotalHoursReduced, 
            displayInfo.TotalHoursAdded);

        return displayInfo;
    }

    /// <summary>
    /// Generates a single-line summary for quick display
    /// Example: "8:00-4:00 (-3h: 11:00-2:00 substituted)"
    /// </summary>
    public string GenerateQuickSummary(AttendanceDisplayInfo displayInfo)
    {
        if (!displayInfo.HasSubstitution)
        {
            return $"{displayInfo.DisplayTimeIn}-{displayInfo.DisplayTimeOut}";
        }

        var sb = new StringBuilder();
        sb.Append($"{displayInfo.DisplayTimeIn}-{displayInfo.DisplayTimeOut}");

        double netAdjustment = displayInfo.TotalHoursAdded - displayInfo.TotalHoursReduced;
        
        if (netAdjustment != 0)
        {
            string sign = netAdjustment > 0 ? "+" : "";
            sb.Append($" ({sign}{netAdjustment:F1}h");

            if (displayInfo.SubstitutionDetails.Any() && displayInfo.SubstitutionDetails.Count == 1)
            {
                var sub = displayInfo.SubstitutionDetails.First();
                sb.Append($": {sub.Schedule.StartTime:hh\\:mm}-{sub.Schedule.EndTime:hh\\:mm} substituted");
            }
            else if (displayInfo.SubstitutionDetails.Any())
            {
                sb.Append($": {displayInfo.SubstitutionDetails.Count} classes substituted");
            }

            if (displayInfo.SubstituteDutyDetails.Any())
            {
                sb.Append($", {displayInfo.SubstituteDutyDetails.Count} sub duties");
            }

            sb.Append(")");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Generates detailed notes for display
    /// </summary>
    private List<string> GenerateDisplayNotes(
        AttendanceDisplayInfo displayInfo,
        bool hasSubstitutions,
        bool hasSubstituteDuties)
    {
        var notes = new List<string>();

        if (hasSubstitutions)
        {
            if (displayInfo.SubstitutionDetails.Count == 1)
            {
                var sub = displayInfo.SubstitutionDetails.First();
                string middleNote = sub.IsMiddleSchedule ? " (middle schedule)" : "";
                notes.Add($"Class {sub.Schedule.StartTime:hh\\:mm}-{sub.Schedule.EndTime:hh\\:mm} was substituted{middleNote}");
                
                if (!string.IsNullOrEmpty(sub.Schedule.SubjectName))
                {
                    notes.Add($"  Subject: {sub.Schedule.SubjectName} ({sub.Schedule.Section})");
                }
            }
            else
            {
                notes.Add($"{displayInfo.SubstitutionDetails.Count} classes were substituted:");
                foreach (var sub in displayInfo.SubstitutionDetails)
                {
                    string middleNote = sub.IsMiddleSchedule ? " (middle)" : "";
                    notes.Add($"  • {sub.Schedule.StartTime:hh\\:mm}-{sub.Schedule.EndTime:hh\\:mm}{middleNote}");
                }
            }
        }

        if (hasSubstituteDuties)
        {
            if (displayInfo.SubstituteDutyDetails.Count == 1)
            {
                var duty = displayInfo.SubstituteDutyDetails.First();
                notes.Add($"Substituted for {duty.OriginalEmployeeName}: {duty.Schedule.StartTime:hh\\:mm}-{duty.Schedule.EndTime:hh\\:mm}");
                
                if (!string.IsNullOrEmpty(duty.Schedule.SubjectName))
                {
                    notes.Add($"  Subject: {duty.Schedule.SubjectName} ({duty.Schedule.Section})");
                }
            }
            else
            {
                notes.Add($"Covering {displayInfo.SubstituteDutyDetails.Count} substitute duties:");
                foreach (var duty in displayInfo.SubstituteDutyDetails)
                {
                    notes.Add($"  • {duty.Schedule.StartTime:hh\\:mm}-{duty.Schedule.EndTime:hh\\:mm} for {duty.OriginalEmployeeName}");
                }
            }
        }

        return notes;
    }

    /// <summary>
    /// Generates time adjustment note
    /// Examples:
    /// - "-3.0 hours due to substitution"
    /// - "+2.0 hours (2 sub duties - 0 substituted)"
    /// - "No net adjustment (3 sub duties - 3 substituted)"
    /// </summary>
    private string GenerateTimeAdjustmentNote(double hoursReduced, double hoursAdded)
    {
        double netAdjustment = hoursAdded - hoursReduced;

        if (netAdjustment == 0)
        {
            if (hoursReduced == 0 && hoursAdded == 0)
            {
                return string.Empty;
            }
            return $"No net adjustment ({hoursAdded:F1}h duties - {hoursReduced:F1}h substituted)";
        }

        if (hoursReduced == 0)
        {
            return $"+{hoursAdded:F1} hours (substitute duties)";
        }

        if (hoursAdded == 0)
        {
            return $"-{hoursReduced:F1} hours (substitution)";
        }

        string sign = netAdjustment > 0 ? "+" : "";
        return $"{sign}{netAdjustment:F1} hours ({hoursAdded:F1}h duties - {hoursReduced:F1}h substituted)";
    }

    /// <summary>
    /// Generates a detailed breakdown for display in a tooltip or expanded view
    /// </summary>
    public string GenerateDetailedBreakdown(AttendanceDisplayInfo displayInfo)
    {
        var sb = new StringBuilder();
        
        sb.AppendLine("=== ATTENDANCE DETAILS ===");
        sb.AppendLine($"Time In:  {displayInfo.DisplayTimeIn}");
        sb.AppendLine($"Time Out: {displayInfo.DisplayTimeOut}");
        sb.AppendLine();

        if (!displayInfo.HasSubstitution)
        {
            sb.AppendLine("No substitutions or substitute duties.");
            return sb.ToString();
        }

        if (displayInfo.SubstitutionDetails.Any())
        {
            sb.AppendLine("SUBSTITUTED CLASSES:");
            foreach (var sub in displayInfo.SubstitutionDetails)
            {
                sb.AppendLine($"  • {sub.Schedule.StartTime:hh\\:mm}-{sub.Schedule.EndTime:hh\\:mm} " +
                              $"(-{sub.HoursReduced:F1}h) {sub.PositionInfo}");
                if (!string.IsNullOrEmpty(sub.Schedule.SubjectName))
                {
                    sb.AppendLine($"    {sub.Schedule.SubjectName} ({sub.Schedule.Section})");
                }
            }
            sb.AppendLine($"  Total Reduced: -{displayInfo.TotalHoursReduced:F1} hours");
            sb.AppendLine();
        }

        if (displayInfo.SubstituteDutyDetails.Any())
        {
            sb.AppendLine("SUBSTITUTE DUTIES:");
            foreach (var duty in displayInfo.SubstituteDutyDetails)
            {
                sb.AppendLine($"  • {duty.Schedule.StartTime:hh\\:mm}-{duty.Schedule.EndTime:hh\\:mm} " +
                              $"(+{duty.HoursAdded:F1}h) for {duty.OriginalEmployeeName}");
                if (!string.IsNullOrEmpty(duty.Schedule.SubjectName))
                {
                    sb.AppendLine($"    {duty.Schedule.SubjectName} ({duty.Schedule.Section})");
                }
            }
            sb.AppendLine($"  Total Added: +{displayInfo.TotalHoursAdded:F1} hours");
            sb.AppendLine();
        }

        sb.AppendLine("NET ADJUSTMENT:");
        sb.AppendLine($"  {displayInfo.TimeAdjustmentNote}");

        if (displayInfo.TimeCalculation != null)
        {
            sb.AppendLine($"  Active Teaching Hours: {displayInfo.TimeCalculation.TotalActiveHours:F1}h");
        }

        return sb.ToString();
    }
}

/// <summary>
/// Complete display information for attendance with substitutions
/// </summary>
public class AttendanceDisplayInfo
{
    // Actual tap times
    public DateTime? ActualTimeIn { get; set; }
    public DateTime? ActualTimeOut { get; set; }

    // Display strings
    public string DisplayTimeIn { get; set; } = "--:--";
    public string DisplayTimeOut { get; set; } = "--:--";

    // Substitution information
    public bool HasSubstitution { get; set; }
    public List<SubstitutionDisplayDetail> SubstitutionDetails { get; set; } = new();
    public List<SubstituteDutyDisplayDetail> SubstituteDutyDetails { get; set; } = new();

    // Time adjustments
    public double TotalHoursReduced { get; set; }
    public double TotalHoursAdded { get; set; }
    public double NetAdjustment => TotalHoursAdded - TotalHoursReduced;

    // Notes and messages
    public List<string> Notes { get; set; } = new();
    public string TimeAdjustmentNote { get; set; } = string.Empty;

    // Calculation details
    public SubstitutionTimeResult? TimeCalculation { get; set; }

    /// <summary>
    /// Gets the primary display string
    /// Example: "8:00-4:00"
    /// </summary>
    public string GetPrimaryDisplay() => $"{DisplayTimeIn}-{DisplayTimeOut}";

    /// <summary>
    /// Gets the adjustment display
    /// Example: "-3.0 hours (substitution)"
    /// </summary>
    public string GetAdjustmentDisplay() => TimeAdjustmentNote;

    /// <summary>
    /// Checks if the employee has any middle schedule substitutions
    /// </summary>
    public bool HasMiddleScheduleSubstitution => 
        SubstitutionDetails.Any(s => s.IsMiddleSchedule);
}

/// <summary>
/// Details about a substituted class for display
/// </summary>
public class SubstitutionDisplayDetail
{
    public ScheduleTimeSlot Schedule { get; set; } = new();
    public bool IsMiddleSchedule { get; set; }
    public string PositionInfo { get; set; } = string.Empty;
    public double HoursReduced { get; set; }

    public string GetDisplayString()
    {
        string middleTag = IsMiddleSchedule ? " [MIDDLE]" : "";
        return $"{Schedule.StartTime:hh\\:mm}-{Schedule.EndTime:hh\\:mm} " +
               $"(-{HoursReduced:F1}h){middleTag} {Schedule.SubjectName}";
    }
}

/// <summary>
/// Details about a substitute duty for display
/// </summary>
public class SubstituteDutyDisplayDetail
{
    public ScheduleTimeSlot Schedule { get; set; } = new();
    public string OriginalEmployeeName { get; set; } = string.Empty;
    public double HoursAdded { get; set; }

    public string GetDisplayString()
    {
        return $"{Schedule.StartTime:hh\\:mm}-{Schedule.EndTime:hh\\:mm} " +
               $"(+{HoursAdded:F1}h) for {OriginalEmployeeName} - {Schedule.SubjectName}";
    }
}
