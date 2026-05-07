using System.Windows.Media;

namespace RAMSOfficial.Helpers;

/// <summary>
/// Color codes for attendance statuses
/// Only 4 allowed statuses: on-time, late, undertime, admin-time
/// </summary>
public static class AttendanceStatusColors
{
    // Color Palette for Attendance Statuses
    
    /// <summary>
    /// ON-TIME: Green - Employee arrived on time
    /// </summary>
    public static readonly SolidColorBrush OnTime = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4CAF50")!); // Green
    
    /// <summary>
    /// LATE: Red - Employee arrived late
    /// </summary>
    public static readonly SolidColorBrush Late = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F44336")!); // Red
    
    /// <summary>
    /// UNDERTIME: Orange - Employee left early
    /// </summary>
    public static readonly SolidColorBrush Undertime = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF9800")!); // Orange
    
    /// <summary>
    /// ADMIN-TIME: Blue - Administrative time (PT Full Load, holidays, leaves, etc.)
    /// </summary>
    public static readonly SolidColorBrush AdminTime = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2196F3")!); // Blue

    /// <summary>
    /// ABSENT: Gray - Employee did not report to work
    /// </summary>
    public static readonly SolidColorBrush Absent = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9E9E9E")!); // Gray

    /// <summary>
    /// Get brush by status string
    /// </summary>
    public static SolidColorBrush GetColorByStatus(string status)
    {
        return status?.ToLower() switch
        {
            "on-time" => OnTime,
            "late" => Late,
            "undertime" => Undertime,
            "admin-time" => AdminTime,
            "absent" => Absent,
            _ => OnTime // Default to green
        };
    }

    /// <summary>
    /// Get hex color string by status
    /// </summary>
    public static string GetHexColorByStatus(string status)
    {
        return status?.ToLower() switch
        {
            "on-time" => "#4CAF50",    // Green
            "late" => "#F44336",       // Red
            "undertime" => "#FF9800",  // Orange
            "admin-time" => "#2196F3", // Blue
            "absent" => "#9E9E9E",     // Gray
            _ => "#4CAF50"             // Default green
        };
    }

    /// <summary>
    /// Get display icon by status using universal Unicode symbols.
    /// Uses symbols that render correctly in default WPF text fonts.
    /// </summary>
    public static string GetIconByStatus(string status)
    {
        return status?.ToLower() switch
        {
            "on-time" => "✓",
            "late" => "⏰",
            "undertime" => "⚠",
            "admin-time" => "🛠",
            "absent" => "✖",
            _ => "•"
        };
    }

    /// <summary>
    /// Get display text by status
    /// </summary>
    public static string GetDisplayText(string status)
    {
        return status?.ToLower() switch
        {
            "on-time" => "On Time",
            "late" => "Late",
            "undertime" => "Undertime",
            "admin-time" => "Admin Time", // Display with proper casing and space
            "absent" => "Absent",
            _ => "Unknown"
        };
    }
}
