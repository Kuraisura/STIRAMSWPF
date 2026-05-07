using System;
using System.Diagnostics;
using Npgsql;
using Dapper;

namespace RAMSOfficial.Helpers;

/// <summary>
/// Helper for filtering schedules by active academic term.
/// This ensures only schedules from the currently active term are retrieved and compared.
/// 
/// Integration: This acts as a SUB-PRIORITY filter that runs BEFORE schedule comparisons.
/// It modifies SQL queries to include term filtering based on the active academic term.
/// </summary>
public class AcademicTermFilterHelper
{
    private readonly string _connectionString;

    public AcademicTermFilterHelper(string connectionString)
    {
        _connectionString = connectionString;
    }

    /// <summary>
    /// Result containing active academic term information
    /// </summary>
    public class ActiveTermResult
    {
        public long? TermId { get; set; }
        public string? TermName { get; set; }
        public string? AcademicYear { get; set; }
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public bool IsActive { get; set; }
        public bool HasActiveTerm => TermId.HasValue && IsActive;
        
        /// <summary>
        /// Indicates if the system should block all attendance operations
        /// True when NO active term exists
        /// </summary>
        public bool ShouldBlockSystem => !HasActiveTerm;
        
        /// <summary>
        /// Gets the reason why system is blocked (if applicable)
        /// </summary>
        public string GetBlockReason()
        {
            if (ShouldBlockSystem)
            {
                return "No active academic term found. The system cannot process attendance until an academic term is activated.";
            }
            return string.Empty;
        }
    }

    /// <summary>
    /// Get the currently active academic term
    /// This determines which term's schedules should be retrieved
    /// </summary>
    public async Task<ActiveTermResult> GetActiveAcademicTermAsync(DateTime? forDate = null)
    {
        var result = new ActiveTermResult();

        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var checkDate = forDate ?? DateTime.Now.Date;

            Debug.WriteLine($"");
            Debug.WriteLine($"???????????????????????????????????????????");
            Debug.WriteLine($"?? ACADEMIC TERM FILTER");
            Debug.WriteLine($"   Checking for active term on: {checkDate:yyyy-MM-dd}");

            // Query for active term based on the date
            var activeTerm = await connection.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT 
                    id,
                    academic_year,
                    term_name,
                    start_date,
                    end_date,
                    is_active
                FROM academic_terms
                WHERE is_active = true
                    AND @CheckDate >= start_date
                    AND @CheckDate <= end_date
                ORDER BY start_date DESC
                LIMIT 1
            ", new { CheckDate = checkDate });

            if (activeTerm != null)
            {
                result.TermId = activeTerm.id;
                result.TermName = activeTerm.term_name;
                result.AcademicYear = activeTerm.academic_year;
                result.StartDate = activeTerm.start_date;
                result.EndDate = activeTerm.end_date;
                result.IsActive = activeTerm.is_active;

                Debug.WriteLine($"   ? ACTIVE TERM FOUND:");
                Debug.WriteLine($"      Term ID: {result.TermId}");
                Debug.WriteLine($"      Term Name: {result.TermName}");
                Debug.WriteLine($"      Academic Year: {result.AcademicYear}");
                Debug.WriteLine($"      Period: {result.StartDate:yyyy-MM-dd} to {result.EndDate:yyyy-MM-dd}");
                Debug.WriteLine($"   ?? FILTER ACTIVE: Only {result.TermName} schedules will be retrieved");
            }
            else
            {
                Debug.WriteLine($"   ? NO ACTIVE TERM FOUND for {checkDate:yyyy-MM-dd}");
                Debug.WriteLine($"   ?? SYSTEM WILL BE BLOCKED");
                Debug.WriteLine($"   ?? Reason: {result.GetBlockReason()}");
                Debug.WriteLine($"   ?? No attendance operations will be allowed");
            }

            Debug.WriteLine($"???????????????????????????????????????????");
            return result;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"");
            Debug.WriteLine($"? ERROR in GetActiveAcademicTermAsync:");
            Debug.WriteLine($"   Message: {ex.Message}");
            Debug.WriteLine($"   Stack: {ex.StackTrace}");
            Debug.WriteLine($"   Continuing without term filter...");
            Debug.WriteLine($"???????????????????????????????????????????");
            return result;
        }
    }

    /// <summary>
    /// Build WHERE clause fragment for term filtering
    /// </summary>
    public static string BuildTermFilterClause(long? termId, string tableAlias = "")
    {
        if (!termId.HasValue)
        {
            // No active term - don't filter by term
            return "";
        }

        var prefix = string.IsNullOrEmpty(tableAlias) ? "" : $"{tableAlias}.";
        return $"AND {prefix}term = {termId.Value}";
    }

    /// <summary>
    /// Build WHERE clause fragment for term filtering (parameterized version)
    /// </summary>
    public static string BuildTermFilterClauseParameterized(long? termId, string tableAlias = "")
    {
        if (!termId.HasValue)
        {
            // No active term - don't filter by term
            return "";
        }

        var prefix = string.IsNullOrEmpty(tableAlias) ? "" : $"{tableAlias}.";
        return $"AND {prefix}term = @TermId";
    }

    /// <summary>
    /// Log term filtering information for debugging
    /// </summary>
    public static void LogTermFilter(ActiveTermResult? termResult, string queryContext)
    {
        Debug.WriteLine($"");
        Debug.WriteLine($"?? TERM FILTER - {queryContext}");

        if (termResult?.HasActiveTerm == true)
        {
            Debug.WriteLine($"   ? Filtering by Term: {termResult.TermName} (ID: {termResult.TermId})");
            Debug.WriteLine($"   Academic Year: {termResult.AcademicYear}");
            Debug.WriteLine($"   Only schedules from this term will be included");
        }
        else
        {
            Debug.WriteLine($"   ?? No term filtering applied");
            Debug.WriteLine($"   All schedules regardless of term will be included");
        }
    }

    /// <summary>
    /// Validate if a schedule belongs to the active term
    /// Used for additional runtime validation
    /// </summary>
    public static bool IsScheduleInActiveTerm(long? scheduleTerm, ActiveTermResult? activeTerm)
    {
        // If no active term is set, allow all schedules
        if (activeTerm?.HasActiveTerm != true)
        {
            return true;
        }

        // If schedule has no term, it's a legacy schedule - allow it
        if (!scheduleTerm.HasValue)
        {
            Debug.WriteLine($"   ?? Schedule has no term assigned - allowing (legacy schedule)");
            return true;
        }

        if (!activeTerm.TermId.HasValue)
        {
            Debug.WriteLine($"   ?? Active term has no TermId - allowing schedule (safety fallback)");
            return true;
        }

        // Check if schedule term matches active term
        var matches = scheduleTerm.Value == activeTerm.TermId.Value;
        
        if (!matches)
        {
            Debug.WriteLine($"   ? Schedule term mismatch:");
            Debug.WriteLine($"      Schedule Term: {scheduleTerm}");
            Debug.WriteLine($"      Active Term: {activeTerm.TermId} ({activeTerm.TermName})");
            Debug.WriteLine($"      This schedule will be EXCLUDED");
        }

        return matches;
    }

    /// <summary>
    /// Get all academic terms (for admin/debugging purposes)
    /// </summary>
    public async Task<List<AcademicTerm>> GetAllAcademicTermsAsync()
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var terms = await connection.QueryAsync<AcademicTerm>(@"
                SELECT 
                    id as Id,
                    academic_year as AcademicYear,
                    term_name as TermName,
                    start_date as StartDate,
                    end_date as EndDate,
                    is_active as IsActive,
                    created_at as CreatedAt,
                    updated_at as UpdatedAt
                FROM academic_terms
                ORDER BY academic_year DESC, start_date DESC
            ");

            return terms.ToList();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error getting academic terms: {ex.Message}");
            return new List<AcademicTerm>();
        }
    }
}

/// <summary>
/// Model for academic_terms table
/// </summary>
public class AcademicTerm
{
    public long Id { get; set; }
    public string? AcademicYear { get; set; }
    public string? TermName { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public bool IsActive { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
