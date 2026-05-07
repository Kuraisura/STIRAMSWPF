namespace RAMSOfficial.Helpers;

public static class ReasonTextFormatter
{
    public static string ToFormal(string? rawReason)
    {
        if (string.IsNullOrWhiteSpace(rawReason))
            return "Reason was not provided.";

        var reason = rawReason.Trim();

        var normalizedCode = reason
            .Replace('-', '_')
            .Replace(' ', '_')
            .ToUpperInvariant();

        var mapped = normalizedCode switch
        {
            "SUNDAY_BLOCK" => "Attendance logging is disabled on Sundays.",
            "HOLIDAY_BLOCK" => "Attendance is blocked due to an active holiday rule.",
            "NO_SCHEDULE" => "No active schedule is available for this employee at the selected time.",
            "NO_SCHEDULE_PT" => "No active schedule is available for this part-time employee at the selected time.",
            "OUTSIDE_SCHEDULE" => "The tap time is outside the allowed schedule window.",
            "APPROVED_LEAVE" => "Attendance is blocked because the employee has an approved leave request.",
            "ALL_SCHEDULES_SUBSTITUTED" => "All assigned schedules for this day are already substituted.",
            "SUBSTITUTION_SCHEDULE" => "Attendance is evaluated using the approved substitution schedule.",
            "VALIDATION_FAILED" => "Attendance validation did not pass.",
            "ADMIN_TIME" => "Attendance is recorded as Admin Time for this context.",
            "CLASS_SCHEDULE" => "Attendance is evaluated using the class schedule policy.",
            "WORK_SCHEDULE" => "Attendance is evaluated using the work schedule policy.",
            _ => null
        };

        if (!string.IsNullOrWhiteSpace(mapped))
            return mapped;

        var looksLikeCode = reason.All(c => char.IsUpper(c) || char.IsDigit(c) || c is '_' or '-');
        if (looksLikeCode)
        {
            var humanized = string.Join(" ", reason.Replace('-', '_').Split('_', StringSplitOptions.RemoveEmptyEntries))
                .ToLowerInvariant();

            if (!string.IsNullOrWhiteSpace(humanized))
            {
                humanized = char.ToUpper(humanized[0]) + humanized[1..];
                return EnsureSentencePunctuation(humanized);
            }
        }

        return EnsureSentencePunctuation(reason);
    }

    private static string EnsureSentencePunctuation(string text)
    {
        var t = text.Trim();
        if (string.IsNullOrEmpty(t)) return "Reason was not provided.";

        if (t.EndsWith('.') || t.EndsWith('!') || t.EndsWith('?'))
            return t;

        return t + ".";
    }
}
