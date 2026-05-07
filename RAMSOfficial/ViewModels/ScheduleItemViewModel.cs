using System;
using System.Collections.ObjectModel;
using System.Windows.Media;
using RAMSOfficial.Models;

namespace RAMSOfficial.ViewModels;

/// <summary>
/// View model for displaying schedule items in the UI
/// </summary>
public class ScheduleItemViewModel : ViewModelBase
{
    private string _scheduleType = string.Empty;
    private string _title = string.Empty;
    private string _subtitle = string.Empty;
    private string _timeRange = string.Empty;
    private string _location = string.Empty;
    private string _icon = string.Empty;
    private Brush _iconColor = Brushes.Blue;
    private Brush _backgroundColor = Brushes.LightBlue;
    private int _priority;

    public string ScheduleType
    {
        get => _scheduleType;
        set => SetProperty(ref _scheduleType, value);
    }

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public string Subtitle
    {
        get => _subtitle;
        set => SetProperty(ref _subtitle, value);
    }

    public string TimeRange
    {
        get => _timeRange;
        set => SetProperty(ref _timeRange, value);
    }

    public string Location
    {
        get => _location;
        set => SetProperty(ref _location, value);
    }

    public string Icon
    {
        get => _icon;
        set => SetProperty(ref _icon, value);
    }

    public Brush IconColor
    {
        get => _iconColor;
        set => SetProperty(ref _iconColor, value);
    }

    public Brush BackgroundColor
    {
        get => _backgroundColor;
        set => SetProperty(ref _backgroundColor, value);
    }

    public int Priority
    {
        get => _priority;
        set => SetProperty(ref _priority, value);
    }

    public static ScheduleItemViewModel FromExamSchedule(ExamSchedule exam)
    {
        return new ScheduleItemViewModel
        {
            ScheduleType = "exam",
            Title = $"?? {exam.SubjectName}",
            Subtitle = $"Section: {exam.Section}",
            TimeRange = FormatTimeRange(exam.TimeStart, exam.TimeEnd),
            Location = exam.RoomCode ?? "TBA",
            Icon = "??",
            IconColor = new SolidColorBrush(Color.FromRgb(211, 47, 47)), // Red
            BackgroundColor = new SolidColorBrush(Color.FromRgb(255, 235, 238)),
            Priority = 1
        };
    }

    public static ScheduleItemViewModel FromTeachingSchedule(TeachingSchedule schedule)
    {
        return new ScheduleItemViewModel
        {
            ScheduleType = "class",
            Title = $"?? {schedule.SubjectName}",
            Subtitle = $"Section: {schedule.Section}",
            TimeRange = FormatTimeRange(schedule.TimeStart, schedule.TimeEnd),
            Location = schedule.RoomId?.ToString() ?? "TBA",
            Icon = "??",
            IconColor = new SolidColorBrush(Color.FromRgb(25, 118, 210)), // Blue
            BackgroundColor = new SolidColorBrush(Color.FromRgb(227, 242, 253)),
            Priority = 2
        };
    }

    public static ScheduleItemViewModel FromSubstitution(ClassSubstitution substitution)
    {
        return new ScheduleItemViewModel
        {
            ScheduleType = "substitution",
            Title = $"?? Substitution Duty",
            Subtitle = substitution.Reason ?? "Class substitution",
            TimeRange = FormatTimeRange(substitution.StartTime, substitution.EndTime),
            Location = "As per schedule",
            Icon = "??",
            IconColor = new SolidColorBrush(Color.FromRgb(255, 152, 0)), // Orange
            BackgroundColor = new SolidColorBrush(Color.FromRgb(255, 243, 224)),
            Priority = 4
        };
    }

    public static ScheduleItemViewModel FromHoliday(HolidayCalendar holiday)
    {
        var (icon, color, bgColor) = holiday.Type.ToLowerInvariant() switch
        {
            "holiday" => ("??", Color.FromRgb(76, 175, 80), Color.FromRgb(232, 245, 233)),
            "suspension" => ("??", Color.FromRgb(255, 152, 0), Color.FromRgb(255, 243, 224)),
            "online" => ("??", Color.FromRgb(33, 150, 243), Color.FromRgb(227, 242, 253)),
            "reporting" => ("??", Color.FromRgb(156, 39, 176), Color.FromRgb(243, 229, 245)),
            _ => ("??", Color.FromRgb(96, 125, 139), Color.FromRgb(236, 239, 241))
        };

        return new ScheduleItemViewModel
        {
            ScheduleType = "holiday",
            Title = $"{icon} {holiday.Name}",
            Subtitle = holiday.Type,
            TimeRange = "All Day",
            Location = holiday.Description ?? string.Empty,
            Icon = icon,
            IconColor = new SolidColorBrush(color),
            BackgroundColor = new SolidColorBrush(bgColor),
            Priority = 3
        };
    }

    public static ScheduleItemViewModel FromVerificationRequest(VerificationRequest request)
    {
        var (icon, title) = request.RequestType.ToLowerInvariant() switch
        {
            "leave" => ("???", "Leave Request"),
            "missed_log" => ("?", "Missed Log"),
            "time_correction" => ("??", "Time Correction"),
            _ => ("??", "Verification Request")
        };

        return new ScheduleItemViewModel
        {
            ScheduleType = "verification",
            Title = $"{icon} {title}",
            Subtitle = request.Reason ?? "Pending verification",
            TimeRange = request.Status == "pending" ? "Pending" : request.Status,
            Location = string.Empty,
            Icon = icon,
            IconColor = new SolidColorBrush(Color.FromRgb(255, 193, 7)), // Amber
            BackgroundColor = new SolidColorBrush(Color.FromRgb(255, 248, 225)),
            Priority = 5
        };
    }

    private static string FormatTimeRange(TimeSpan? start, TimeSpan? end)
    {
        if (!start.HasValue || !end.HasValue)
            return "TBA";

        return $"{start.Value:hh\\:mm} - {end.Value:hh\\:mm}";
    }
}

/// <summary>
/// Extended Main View Model with schedule support
/// </summary>
public class ScheduleInfo
{
    public ObservableCollection<ScheduleItemViewModel> TodaySchedules { get; set; } = new();
    public string ScheduleSummary { get; set; } = string.Empty;
    public bool HasSchedules => TodaySchedules.Count > 0;
}
