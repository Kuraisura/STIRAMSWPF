using System;
using System.Collections.Generic;
using System.Linq;

namespace RAMSOfficial.Helpers;

public static class ScheduleConflictHelper
{
    public class TimeSlot
    {
        public TimeSpan StartTime { get; set; }
        public TimeSpan EndTime { get; set; }
        public string Source { get; set; } = "";
        public string SubjectName { get; set; } = "";
    }

    /// <summary>
    /// Organizes overlapping or adjacent time slots into a single continuous block.
    /// E.g., Exam (8:00-9:30) and Class (9:00-11:00) becomes one block (8:00-11:00).
    /// Prevents conflicts and provides a clean Expected Time In/Out.
    /// </summary>
    public static (TimeSpan ExpectedTimeIn, TimeSpan ExpectedTimeOut, int TotalBlocks) ProcessAndMergeSchedules(List<TimeSlot> slots)
    {
        if (slots == null || slots.Count == 0)
        {
            return (TimeSpan.Zero, TimeSpan.Zero, 0);
        }

        // Sort by start time
        var sortedSlots = slots.OrderBy(s => s.StartTime).ThenBy(s => s.EndTime).ToList();

        // Find the absolute minimum start time and maximum end time across all schedules for the day
        // This creates a single continuous shift for attendance calculation.
        // Even if there are gaps (e.g. 8-11 and 1-3), the expected shift is 8 to 3.
        var expectedTimeIn = sortedSlots.First().StartTime;
        var expectedTimeOut = sortedSlots.Max(s => s.EndTime);

        return (expectedTimeIn, expectedTimeOut, sortedSlots.Count);
    }
}
