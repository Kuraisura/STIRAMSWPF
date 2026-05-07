using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using RAMSOfficial.Models;

namespace RAMSOfficial.Helpers;

public class NonTeachingReadjustedScheduleHelper
{
    private readonly string _connectionString;
    private readonly AcademicTermFilterHelper _termFilterHelper;

    public NonTeachingReadjustedScheduleHelper(string connectionString)
    {
        _connectionString = connectionString;
        _termFilterHelper = new AcademicTermFilterHelper(connectionString);
    }

    public async Task<VerificationRequest?> GetReadjustedScheduleRequestAsync(int employeeId, DateTime date)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var activeTerm = await _termFilterHelper.GetActiveAcademicTermAsync(date);
            AcademicTermFilterHelper.LogTermFilter(activeTerm, "Non-Teaching Readjusted Schedule");

            var request = await connection.QueryFirstOrDefaultAsync<VerificationRequest>(@"
                SELECT * FROM verification_requests
                WHERE employee_id = @EmployeeId
                    AND LOWER(request_type) = 'readjusting'
                    AND LOWER(status) IN ('pending', 'approved')
                    AND (
                        DATE(schedule_date) = @Date
                        OR DATE(requested_time) = @Date
                        OR DATE(original_time) = @Date
                        OR DATE(requested_at) = @Date
                    )
                    AND (@TermId IS NULL OR term_id = @TermId)
                ORDER BY requested_at DESC
                LIMIT 1
            ", new { EmployeeId = employeeId, Date = date.Date, TermId = activeTerm.TermId });

            if (request != null)
            {
                Debug.WriteLine($"");
                Debug.WriteLine($"?? NON-TEACHING READJUSTED SCHEDULE FOUND:");
                Debug.WriteLine($"   Request ID: {request.RequestId}");
                Debug.WriteLine($"   Status: {request.Status}");
                Debug.WriteLine($"   Time Start: {request.TimeStart}");
                Debug.WriteLine($"   Time End: {request.TimeEnd}");
            }

            return request;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"⚠ Non-teaching readjusted schedule lookup failed: {ex.Message}");
            return null;
        }
    }
}
