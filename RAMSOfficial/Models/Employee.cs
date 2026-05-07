namespace RAMSOfficial.Models;

public class Employee
{
    // Primary Key
    public int EmployeeId { get; set; }
    
    // RFID and Identification
    public string RfidCode { get; set; } = string.Empty;
    public string SchoolId { get; set; } = string.Empty;
    public string? UniqueEmployeeId { get; set; }
    
    // Personal Information
    public string FullName { get; set; } = string.Empty;
    
    // Contact Information
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    
    // Employment Information
    public string Department { get; set; } = string.Empty;
    public int? DepartmentId { get; set; }
    public string? EmploymentStatus { get; set; }
    public string? EmploymentType { get; set; }
    public string? EmploymentSubtype { get; set; }
    public DateTime HireDate { get; set; }
    public DateTime? StartDate { get; set; }
    
    // Role and Type
    public string? Role { get; set; }
    public string? EmployeeType { get; set; }
    public string? StaffType { get; set; }
    public bool? IsReportingStaff { get; set; }
    
    // Schedule
    public TimeSpan ScheduleTimeIn { get; set; }
    public TimeSpan ScheduleTimeOut { get; set; }
    
    // Academic Information (for students/faculty)
    public int? YearLevel { get; set; }
    public string? CurrentSection { get; set; }
    public int? CurrentSemester { get; set; }
    
    // Verification
    public string? VerificationStatus { get; set; }
    public long? VerifiedBy { get; set; }
    public DateTime? VerifiedAt { get; set; }
    
    // System Fields
    public string? PhotoPath { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    
    // Password (for authentication - you may want to remove this from display)
    public string? Password { get; set; }

    // Computed Properties
    public string DisplayName => FullName;
    
    public string Initial
    {
        get
        {
            if (string.IsNullOrEmpty(FullName)) return "?";
            var parts = FullName.Split(' ');
            return parts.Length > 0 ? parts[0].Substring(0, 1).ToUpper() : "?";
        }
    }
    
    // For backward compatibility
    public string FirstName
    {
        get
        {
            if (string.IsNullOrEmpty(FullName)) return "";
            var parts = FullName.Split(' ');
            return parts.Length > 0 ? parts[0] : "";
        }
    }
    
    public string LastName
    {
        get
        {
            if (string.IsNullOrEmpty(FullName)) return "";
            var parts = FullName.Split(' ');
            return parts.Length > 1 ? string.Join(" ", parts.Skip(1)) : "";
        }
    }
    
    // Alias for compatibility
    public int Id => EmployeeId;
}
