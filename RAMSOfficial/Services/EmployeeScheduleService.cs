using System;
using System.Linq;
using System.Threading.Tasks;
using Npgsql;
using Dapper;

namespace RAMSOfficial.Services;

/// <summary>
/// Service to retrieve employee schedule information
/// Handles both Teaching (from teaching_schedules) and Non-Teaching (from employees table)
/// </summary>
public class EmployeeScheduleService
{
    private readonly string _connectionString;

    public EmployeeScheduleService(string connectionString)
    {
        _connectionString = connectionString;
    }

    /// <summary>
    /// Get employee's expected schedule for today
    /// Teaching staff: earliest time_start to latest time_end from all classes today
    /// Non-Teaching staff: schedule_time_in to schedule_time_out from employees table
    /// ? EXCLUDES substituted classes from calculation
    /// </summary>
    public async Task<EmployeeScheduleResult> GetEmployeeScheduleAsync(int employeeId, DateTime date)
    {
        var result = new EmployeeScheduleResult
        {
            EmployeeId = employeeId,
            Date = date
        };

        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            // Get employee details
            var employee = await connection.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT 
                    employee_id,
                    staff_type,
                    schedule_time_in::text AS schedule_time_in,
                    schedule_time_out::text AS schedule_time_out
                FROM employees
                WHERE employee_id = @EmployeeId
            ", new { EmployeeId = employeeId });

            if (employee == null)
            {
                result.IsSuccess = false;
                result.ErrorMessage = "Employee not found";
                return result;
            }

            result.StaffType = employee.staff_type;

            // ???????????????????????????????????????????????
            // CRITICAL: Check staff_type - Non-Teaching ALWAYS use fixed schedule
            // ???????????????????????????????????????????????
            if (employee.staff_type != null && 
                employee.staff_type.ToString().Equals("Non-Teaching", StringComparison.OrdinalIgnoreCase))
            {
                // ? NON-TEACHING STAFF - Use schedule from employees table ONLY
                result.IsSuccess = true;
                result.HasSchedule = true;

                if (employee.schedule_time_in != null)
                {
                    TimeSpan.TryParse(employee.schedule_time_in.ToString(), out TimeSpan parsedIn);
                    result.ScheduleTimeIn = parsedIn;
                }

                if (employee.schedule_time_out != null)
                {
                    TimeSpan.TryParse(employee.schedule_time_out.ToString(), out TimeSpan parsedOut);
                    result.ScheduleTimeOut = parsedOut;
                }

                result.ScheduleCount = 0;
                result.ScheduleSource = "employees_non_teaching";

                System.Diagnostics.Debug.WriteLine($"?? Non-Teaching Staff Schedule:");
                System.Diagnostics.Debug.WriteLine($"   Employee ID: {employeeId}");
                System.Diagnostics.Debug.WriteLine($"   Staff Type: Non-Teaching");
                System.Diagnostics.Debug.WriteLine($"   Time In: {result.ScheduleTimeIn:hh\\:mm}");
                System.Diagnostics.Debug.WriteLine($"   Time Out: {result.ScheduleTimeOut:hh\\:mm}");
                System.Diagnostics.Debug.WriteLine($"   Source: employees table (fixed schedule)");
                
                return result;
            }

            // ???????????????????????????????????????????????
            // TEACHING STAFF - Check exam_schedules first, then teaching_schedules
            // ? NEW: EXCLUDE SUBSTITUTED CLASSES + INCLUDE SUBSTITUTE DUTIES
            // ???????????????????????????????????????????????
            if (employee.staff_type != null && 
                employee.staff_type.ToString().Equals("Teaching", StringComparison.OrdinalIgnoreCase))
            {
                var dayOfWeekNumber = (int)date.DayOfWeek;
                var dayOfWeekName = date.DayOfWeek.ToString();
                var dayOfWeekNumberText = dayOfWeekNumber.ToString();

                System.Diagnostics.Debug.WriteLine($"?? Searching schedules for Teaching staff:");
                System.Diagnostics.Debug.WriteLine($"   Day: {dayOfWeekName} ({dayOfWeekNumber})");
                System.Diagnostics.Debug.WriteLine($"   Date: {date:yyyy-MM-dd}");

                // ???????????????????????????????????????????????
                // STEP 0: Check if employee is substituting someone (ADD to their schedule)
                // ???????????????????????????????????????????????
                var substituteDuties = await connection.QueryAsync<dynamic>(@"
                    SELECT 
                        cs.id as SubstitutionId,
                        cs.start_time::text as StartTime,
                        cs.end_time::text as EndTime,
                        cs.original_employee_id as OriginalEmployeeId,
                        oe.full_name as OriginalEmployeeName,
                        ts.subject_name as SubjectName,
                        ts.section as Section
                    FROM class_substitutions_v2 cs
                    LEFT JOIN employees oe ON cs.original_employee_id = oe.employee_id
                    LEFT JOIN teaching_schedules ts ON 
                        ts.employee_id = cs.original_employee_id
                        AND ts.time_start = cs.start_time
                        AND ts.time_end = cs.end_time
                    WHERE cs.substitute_employee_id = @EmployeeId
                        AND cs.substitution_date::date = @Date
                        AND cs.status = 'approved'
                        AND cs.start_time IS NOT NULL
                        AND cs.end_time IS NOT NULL
                    ORDER BY cs.start_time ASC
                ", new { EmployeeId = employeeId, Date = date.Date });

                var substituteDutiesList = substituteDuties.ToList();

                if (substituteDutiesList.Any())
                {
                    System.Diagnostics.Debug.WriteLine($"");
                    System.Diagnostics.Debug.WriteLine($"   ? SUBSTITUTE DUTIES FOUND: {substituteDutiesList.Count}");
                    foreach (var duty in substituteDutiesList)
                    {
                        // ?? CRITICAL FIX: Access dynamic properties with lowercase names (PostgreSQL/Dapper convention)
                        TimeSpan.TryParse(duty.starttime?.ToString(), out TimeSpan pStart);
                        TimeSpan.TryParse(duty.endtime?.ToString(), out TimeSpan pEnd);
                        var startTime = duty.starttime != null ? pStart.ToString(@"hh\:mm") : "N/A";
                        var endTime = duty.endtime != null ? pEnd.ToString(@"hh\:mm") : "N/A";
                        var originalName = duty.originalemployeename ?? "Unknown";
                        var subjectName = duty.subjectname ?? "Unknown Subject";
                        var section = duty.section ?? "";

                        System.Diagnostics.Debug.WriteLine($"      + Added: {startTime} - {endTime} (Substituting for {originalName})");
                        System.Diagnostics.Debug.WriteLine($"        Subject: {subjectName} ({section})");
                    }
                }

                // ???????????????????????????????????????????????
                // STEP 1: Get substituted time slots (to exclude from schedule)
                // ???????????????????????????????????????????????
                var substitutedSlots = await connection.QueryAsync<dynamic>(@"
                    SELECT 
                        start_time::text as StartTime,
                        end_time::text as EndTime
                    FROM class_substitutions_v2
                    WHERE original_employee_id = @EmployeeId
                        AND substitution_date::date = @Date
                        AND status = 'approved'
                ", new { EmployeeId = employeeId, Date = date.Date });

                var substitutedList = substitutedSlots.ToList();

                if (substitutedList.Any())
                {
                    System.Diagnostics.Debug.WriteLine($"");
                    System.Diagnostics.Debug.WriteLine($"   ?? SUBSTITUTED CLASSES FOUND: {substitutedList.Count}");
                    foreach (var sub in substitutedList)
                    {
                        TimeSpan.TryParse(sub.starttime?.ToString(), out TimeSpan pStart);
                        TimeSpan.TryParse(sub.endtime?.ToString(), out TimeSpan pEnd);
                        System.Diagnostics.Debug.WriteLine($"      - Excluded: {pStart:hh\\:mm} - {pEnd:hh\\:mm}");
                    }
                }

                // Collect ALL schedule blocks to merge
                var allTimeSlots = new List<RAMSOfficial.Helpers.ScheduleConflictHelper.TimeSlot>();

                // 1. Add Substitute Duties (Employee acts as a substitute)
                var validDuties = new List<dynamic>();
                foreach (var duty in substituteDutiesList)
                {
                    TimeSpan.TryParse(duty.starttime?.ToString(), out TimeSpan start);
                    TimeSpan.TryParse(duty.endtime?.ToString(), out TimeSpan end);
                    allTimeSlots.Add(new RAMSOfficial.Helpers.ScheduleConflictHelper.TimeSlot
                    {
                        StartTime = start,
                        EndTime = end,
                        Source = "substitute_duty",
                        SubjectName = duty.subjectname ?? "Unknown Subject"
                    });
                    validDuties.Add(duty);
                }

                // 2. Add Exam Schedules (Exclude those marked as substituted)
                var examSchedules = await connection.QueryAsync<dynamic>(@"
                    SELECT 
                        exam_schedule_id,
                        employee_id,
                        exam_date,
                        day_of_week,
                        time_start::text AS time_start,
                        time_end::text AS time_end,
                        subject_name,
                        section,
                        exam_type,
                        course_code,
                        room_code,
                        status
                    FROM exam_schedules
                    WHERE employee_id = @EmployeeId
                        AND (
                            exam_date::date = @Date
                            OR (
                                exam_date IS NULL
                                AND (
                                    day_of_week::text = @DayOfWeekNumberText
                                    OR LOWER(TRIM(day_of_week::text)) = LOWER(@DayOfWeekName)
                                )
                            )
                        )
                        AND (
                            status IS NULL
                            OR LOWER(status) IN ('active', 'scheduled', 'available', 'approved', 'published')
                        )
                ", new { 
                    EmployeeId = employeeId, 
                    Date = date.Date,
                    DayOfWeekNumberText = dayOfWeekNumberText,
                    DayOfWeekName = dayOfWeekName
                });

                var examList = examSchedules.ToList();
                var nonSubstitutedExams = examList.Where(exam => {
                    TimeSpan.TryParse(exam.time_start?.ToString(), out TimeSpan examStart);
                    TimeSpan.TryParse(exam.time_end?.ToString(), out TimeSpan examEnd);
                    return !substitutedList.Any(sub => {
                        TimeSpan.TryParse(sub.starttime?.ToString(), out TimeSpan subStart);
                        TimeSpan.TryParse(sub.endtime?.ToString(), out TimeSpan subEnd);
                        return subStart == examStart && subEnd == examEnd;
                    });
                }).ToList();

                foreach (var exam in nonSubstitutedExams)
                {
                    TimeSpan.TryParse(exam.time_start?.ToString(), out TimeSpan examStart);
                    TimeSpan.TryParse(exam.time_end?.ToString(), out TimeSpan examEnd);
                    allTimeSlots.Add(new RAMSOfficial.Helpers.ScheduleConflictHelper.TimeSlot
                    {
                        StartTime = examStart,
                        EndTime = examEnd,
                        Source = "exam_schedule",
                        SubjectName = exam.subject_name ?? "Exam"
                    });
                }

                var teachingList = new List<dynamic>();
                var nonSubstitutedClasses = new List<dynamic>();
                if (!nonSubstitutedExams.Any())
                {
                    // 3. Add Teaching Schedules (Exclude those marked as substituted)
                    var teachingSchedules = await connection.QueryAsync<dynamic>(@"
                        SELECT 
                            time_start::text AS time_start,
                            time_end::text AS time_end,
                            subject_name,
                            section,
                            room_id
                        FROM teaching_schedules
                        WHERE employee_id = @EmployeeId
                            AND (
                                (specific_date::date = @Date::date) OR
                                (
                                    (
                                        day_of_week::text = @DayOfWeekNumberText
                                        OR LOWER(TRIM(day_of_week::text)) = LOWER(@DayOfWeekName)
                                    )
                                    AND (is_recurring = true OR is_recurring IS NULL)
                                )
                            )
                            AND (
                                status IS NULL
                                OR LOWER(status) IN ('active', 'available', 'scheduled', 'approved', 'published')
                            )
                    ", new { 
                        EmployeeId = employeeId, 
                        Date = date.Date,
                        DayOfWeekNumberText = dayOfWeekNumberText,
                        DayOfWeekName = dayOfWeekName
                    });

                    teachingList = teachingSchedules.ToList();
                    nonSubstitutedClasses = teachingList.Where(cls => {
                        TimeSpan.TryParse(cls.time_start?.ToString(), out TimeSpan clsStart);
                        TimeSpan.TryParse(cls.time_end?.ToString(), out TimeSpan clsEnd);
                        return !substitutedList.Any(sub => {
                            TimeSpan.TryParse(sub.starttime?.ToString(), out TimeSpan subStart);
                            TimeSpan.TryParse(sub.endtime?.ToString(), out TimeSpan subEnd);
                            return subStart == clsStart && subEnd == clsEnd;
                        });
                    }).ToList();

                    foreach (var cls in nonSubstitutedClasses)
                    {
                        TimeSpan.TryParse(cls.time_start?.ToString(), out TimeSpan clsStart);
                        TimeSpan.TryParse(cls.time_end?.ToString(), out TimeSpan clsEnd);
                        allTimeSlots.Add(new RAMSOfficial.Helpers.ScheduleConflictHelper.TimeSlot
                        {
                            StartTime = clsStart,
                            EndTime = clsEnd,
                            Source = "teaching_schedule",
                            SubjectName = cls.subject_name ?? "Class"
                        });
                    }
                }

                // Process and merge ALL collected schedules using our new Conflict Helper
                if (allTimeSlots.Any())
                {
                    var merged = RAMSOfficial.Helpers.ScheduleConflictHelper.ProcessAndMergeSchedules(allTimeSlots);
                    
                    var totalActive = allTimeSlots.Count;
                    var substitutedCount = (examList.Count - nonSubstitutedExams.Count) + (teachingList.Count - nonSubstitutedClasses.Count);
                    var originalCount = examList.Count + teachingList.Count;

                    result.IsSuccess = true;
                    result.HasSchedule = true;
                    result.ScheduleTimeIn = merged.ExpectedTimeIn;
                    result.ScheduleTimeOut = merged.ExpectedTimeOut;
                    result.ScheduleCount = totalActive;
                    result.SubstitutedCount = substitutedCount;
                    result.TotalScheduleCount = originalCount;
                    result.SubstituteDutiesCount = substituteDutiesList.Count;
                    var scheduleSourceParts = new List<string>();
                    if (nonSubstitutedExams.Count > 0) scheduleSourceParts.Add("exam schedule");
                    if (nonSubstitutedClasses.Count > 0) scheduleSourceParts.Add("class schedule");
                    if (substituteDutiesList.Count > 0) scheduleSourceParts.Add("substitute duty");

                    result.ScheduleSource = scheduleSourceParts.Count > 0
                        ? $"{string.Join(" + ", scheduleSourceParts)} ({totalActive})"
                        : $"combined_schedules ({totalActive})";

                    System.Diagnostics.Debug.WriteLine($"? DISSECTED AND ORGANIZED SCHEDULES FOUND:");
                    System.Diagnostics.Debug.WriteLine($"   Exams: {nonSubstitutedExams.Count} | Classes: {nonSubstitutedClasses.Count} | Subs: {substituteDutiesList.Count}");
                    System.Diagnostics.Debug.WriteLine($"   Expected Time In: {merged.ExpectedTimeIn:hh\\:mm}");
                    System.Diagnostics.Debug.WriteLine($"   Expected Time Out: {merged.ExpectedTimeOut:hh\\:mm}");

                    return result;
                }
                else
                {
                    result.IsSuccess = true;
                    result.HasSchedule = false;
                    result.ScheduleTimeIn = TimeSpan.Zero;
                    result.ScheduleTimeOut = TimeSpan.Zero;
                    result.ScheduleCount = 0;
                    result.SubstitutedCount = teachingList.Count + examList.Count;
                    result.TotalScheduleCount = teachingList.Count + examList.Count;
                    result.ScheduleSource = "no_classes_after_substitutions";
                    return result;
                }
            }

            // Fallback: should never reach here
            result.IsSuccess = false;
            result.HasSchedule = false;
            result.ErrorMessage = "Unknown staff type or no data";
            return result;
        }
        catch (Exception ex)
        {
            result.IsSuccess = false;
            result.ErrorMessage = "Error retrieving schedule: " + ex.Message;
            return result;
        }
    }
}

/// <summary>
/// Result of employee schedule retrieval
/// </summary>
public class EmployeeScheduleResult
{
    public int EmployeeId { get; set; }
    public DateTime Date { get; set; }
    public bool IsSuccess { get; set; }
    public bool HasSchedule { get; set; }
    public string? StaffType { get; set; }
    public TimeSpan ScheduleTimeIn { get; set; }
    public TimeSpan ScheduleTimeOut { get; set; }
    public int ScheduleCount { get; set; }
    public int SubstitutedCount { get; set; }
    public int SubstituteDutiesCount { get; set; }
    public int TotalScheduleCount { get; set; }
    public string? ScheduleSource { get; set; }
    public string? ErrorMessage { get; set; }

    public int ActiveTeachingLoad => ScheduleCount;
    public int OwnClasses => ScheduleCount - SubstituteDutiesCount;
    public int ReducedBy => SubstitutedCount;
    public int AddedBy => SubstituteDutiesCount;
}
