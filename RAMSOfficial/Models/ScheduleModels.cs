namespace RAMSOfficial.Models;

public class TeachingSchedule
{
    public long ScheduleId { get; set; }
    public int EmployeeId { get; set; }
    public int? CourseId { get; set; }
    public int? RoomId { get; set; }
    public string? SubjectName { get; set; }
    public string? ClassType { get; set; }
    public string? DayOfWeek { get; set; }
    public TimeSpan? TimeStart { get; set; }
    public TimeSpan? TimeEnd { get; set; }
    public string? Section { get; set; }
    public DateTime? SubstitutionDate { get; set; }
    public string? Status { get; set; }
    public int? SubstituteEmployeeId { get; set; }
    public string? UnavailableReason { get; set; }
    public DateTime? SubstitutedAt { get; set; }
    public int? SubstitutedBy { get; set; }
    public long? Term { get; set; }
    public DateTime? SpecificDate { get; set; }
    public bool? IsRecurring { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class ExamSchedule
{
    public long ExamScheduleId { get; set; }
    public int EmployeeId { get; set; }
    public string? DayOfWeek { get; set; }
    public TimeSpan? TimeStart { get; set; }
    public TimeSpan? TimeEnd { get; set; }
    public string? CourseCode { get; set; }
    public string? SubjectName { get; set; }
    public string? Section { get; set; }
    public string? RoomCode { get; set; }
    public string? Status { get; set; }
    public int? SubstituteEmployeeId { get; set; }
    public string? UnavailableReason { get; set; }
    public DateTime? SubstitutedAt { get; set; }
    public int? SubstitutedBy { get; set; }
    public DateTime? ExamDate { get; set; }
    public DateTime? SubstitutionDate { get; set; }
    public long? Term { get; set; }
    public string? ExamType { get; set; }
    public string? ClassType { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class ClassSubstitution
{
    public long Id { get; set; }
    public int OriginalEmployeeId { get; set; }
    public int SubstituteEmployeeId { get; set; }
    public DateTime SubstitutionDate { get; set; }
    public TimeSpan? StartTime { get; set; }
    public TimeSpan? EndTime { get; set; }
    public string? Reason { get; set; }
    public string Status { get; set; } = "pending";
    public int? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime? RequestedAt { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class HolidayCalendar
{
    // Primary Key
    public long HolidayId { get; set; }  // Maps to 'id' column
    
    // Date Range
    public DateTime StartDate { get; set; }  // Required
    public DateTime EndDate { get; set; }    // Required
    public DateTime? Date { get; set; }      // Legacy column (if still in use)
    
    // Holiday Information
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty; // holiday, online_class, suspended_asynchronous
    public string? Description { get; set; }
    
    // Flags
    public bool AffectsAttendance { get; set; }  // If true, blocks attendance
    public bool? ReportingOnly { get; set; }     // If true, only reporting staff must attend
    public bool? ReportingStaffOnly { get; set; } // Alternative name for reporting_only
    
    // Metadata
    public string? HolidayType { get; set; }  // Legacy/alternative type field
    public int? CreatedBy { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    
    // Computed Properties
    public bool IsSingleDay => StartDate.Date == EndDate.Date;
    public int DurationDays => (EndDate.Date - StartDate.Date).Days + 1;
    
    // Helper property for backward compatibility
    public bool? IsActive => true; // Assume active if no is_active column exists
}

public class VerificationRequest
{
    public long RequestId { get; set; }
    public int EmployeeId { get; set; }
    public int? AttendanceLogId { get; set; }
    public string RequestType { get; set; } = string.Empty; // missed_log, time_correction, leave
    public DateTime? OriginalTime { get; set; }
    public DateTime? RequestedTime { get; set; }
    public string? Reason { get; set; }
    public string? SupportingDocuments { get; set; }
    public string Status { get; set; } = "pending"; // pending, approved, rejected
    public int? RequestedBy { get; set; }
    public int? ReviewedBy { get; set; }
    public string? ReviewNotes { get; set; }
    public DateTime? RequestedAt { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public long? ScheduleId { get; set; }
    public string? ScheduleType { get; set; }
    public DateTime? ScheduleDate { get; set; }
    public bool? AffectsSchedule { get; set; }
    public bool? SubstitutionCreated { get; set; }
    public TimeSpan? TimeStart { get; set; }
    public TimeSpan? TimeEnd { get; set; }
    public bool? SubstitutionApplied { get; set; }
    public string? LogType { get; set; }
}
