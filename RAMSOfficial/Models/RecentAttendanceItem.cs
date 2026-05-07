using System.Windows.Media.Imaging;
using RAMSOfficial.Helpers;

namespace RAMSOfficial.Models;

public class RecentAttendanceItem
{
    public int EmployeeId { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public string Initial { get; set; } = string.Empty;

    // Log Details
    public string LogType { get; set; } = string.Empty; // IN or OUT
    public DateTime LogTime { get; set; }
    public DateTime? TimeIn { get; set; }
    public DateTime? TimeOut { get; set; }
    public string AttendanceStatus { get; set; } = string.Empty; // on-time, late, undertime, admin-time

    // Photo
    public string? PhotoPath { get; set; }
    public BitmapImage? Photo { get; set; }
    public bool HasPhoto => Photo != null;

    // Display Properties
    public string LogTypeIcon => LogType == "IN" ? "➡" : "⬅";
    public string LogTypeText => LogType == "IN" ? "TIME IN" : "TIME OUT";
    public string LogTimeFormatted => LogTime.ToString("hh:mm tt");
    public string TimeInFormatted => TimeIn?.ToString("hh:mm tt") ?? string.Empty;
    public string TimeOutFormatted => TimeOut?.ToString("hh:mm tt") ?? string.Empty;
    public string TimeRangeFormatted => TimeIn.HasValue && TimeOut.HasValue
        ? $"{TimeInFormatted} - {TimeOutFormatted}"
        : TimeInFormatted + TimeOutFormatted;

    // Use color helper with only 4 allowed statuses
    public string StatusColor => AttendanceStatusColors.GetHexColorByStatus(AttendanceStatus);
    public System.Windows.Media.SolidColorBrush StatusBrush => AttendanceStatusColors.GetColorByStatus(AttendanceStatus);
    public string StatusIcon => AttendanceStatusColors.GetIconByStatus(AttendanceStatus);
    public string StatusText => AttendanceStatusColors.GetDisplayText(AttendanceStatus);
}
