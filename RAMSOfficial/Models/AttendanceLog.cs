namespace RAMSOfficial.Models;

public class AttendanceLog
{
    // Primary Key - YOUR DATABASE USES log_id!
    public int LogId { get; set; }
    public int Id { get => LogId; set => LogId = value; } // Alias for compatibility
    
    // Foreign Key
    public int? EmployeeId { get; set; }
    
    // RFID Code
    public string RfidCode { get; set; } = string.Empty;
    
    // Time Information - YOUR DATABASE USES log_time!
    public DateTime LogTime { get; set; }
    public DateTime TapTime { get => LogTime; set => LogTime = value; } // Alias
    
    // Date
    public DateTime Date { get; set; }
    
    // Log Type - YOUR DATABASE USES log_type!
    public string LogType { get; set; } = string.Empty; // "IN" or "OUT"
    public string TapType { get => LogType; set => LogType = value; } // Alias
    
    // Status Information
    public string? AttendanceStatus { get; set; }
    public bool? IsLate { get; set; }
    public bool? IsEarlyOut { get; set; }
    public int? LateMinutes { get; set; }
    public int? UndertimeMinutes { get; set; }
    
    // Special Flags
    public bool? IsAdminTime { get; set; }
    public string? AdminTimeReason { get; set; }
    public string? AdminTimeStatus { get; set; }
    public bool? IsHoliday { get; set; }
    public bool? IsSuspended { get; set; }
    public bool? IsOnlineClass { get; set; }
    
    // Verification
    public int? VerifiedBy { get; set; }
    public DateTime? VerifiedAt { get; set; }
    
    // Notes
    public string? Notes { get; set; }
    
    // References
    public long? ScheduleId { get; set; }
    public long? TermId { get; set; }
    
    // System Fields
    public DateTime? CreatedAt { get; set; }
    
    // Computed Properties
    public string DisplayTime => LogTime != default ? LogTime.ToString("HH:mm:ss") : "";
    public string DisplayType => !string.IsNullOrEmpty(LogType) ? LogType.ToUpper() : "";
}
