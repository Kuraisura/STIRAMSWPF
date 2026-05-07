using System.Diagnostics;
using System.Globalization;

namespace RAMSOfficial.Helpers;

/// <summary>
/// Normalizes data values before pushing to database to prevent
/// CHECK constraint violations and timestamp format errors.
///
/// Source of truth — exact constraints from <c>pg_constraint</c>:
///
///   <b>attendance_logs_status_valid</b>
///     NULL, 'on-time', 'late', 'undertime', 'admin-time', 'absent'
///
///   <b>attendance_logs_log_type_valid</b>
///     NULL, 'IN', 'OUT'
///
///   <b>attendance_logs_admin_time_status_check</b>
///     NULL, 'admin_time', 'regular'
///
/// Diagnostic SQL (run in the database SQL Editor):
/// <code>
///   SELECT conname, pg_get_constraintdef(oid)
///   FROM pg_constraint
///   WHERE conrelid = 'attendance_logs'::regclass
///     AND contype = 'c';
/// </code>
/// </summary>
public static class DataSanitizer
{
    // ═══════════════════ attendance_status ═══════════════════

    /// <summary>
    /// The exact values accepted by the database
    /// <c>attendance_logs_status_valid</c> CHECK constraint.
    /// NULL is also valid but handled separately.
    /// </summary>
    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "on-time",
        "late",
        "undertime",
        "admin-time",
        "absent"
    };

    /// <summary>
    /// Normalizes an <c>attendance_status</c> value so it passes the
    /// database CHECK constraint.
    ///
    /// Mapping:
    ///   null / empty / whitespace      → "on-time"  (safe default)
    ///   "present"                      → "on-time"
    ///   "on time" / "ON-TIME"          → "on-time"
    ///   "pending"                      → "on-time"
    ///   "admin time" / "Admin-Time"    → "admin-time"
    ///   "early-out" / "early out"      → "undertime"
    ///   "absent"                       → "absent"   (valid per constraint)
    ///   Already valid (case-insensitive) → lowered exact match
    ///   Anything else                  → "on-time"  (safe fallback)
    /// </summary>
    public static string NormalizeAttendanceStatus(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "on-time";

        var trimmed = raw.Trim().ToLowerInvariant();

        // Direct match against the constraint's allowed set
        if (AllowedStatuses.Contains(trimmed))
            return trimmed;

        // Common aliases / legacy values
        var normalized = trimmed switch
        {
            "present"    => "on-time",
            "on time"    => "on-time",
            "ontime"     => "on-time",
            "on_time"    => "on-time",
            "pending"    => "on-time",
            "admin time" => "admin-time",
            "admin_time" => "admin-time",
            "admintime"  => "admin-time",
            "early-out"  => "undertime",
            "early out"  => "undertime",
            "earlyout"   => "undertime",
            _            => null
        };

        if (normalized != null)
        {
            Debug.WriteLine($"[DataSanitizer] Normalized '{raw}' → '{normalized}'");
            return normalized;
        }

        // Unknown value — log and fallback
        Debug.WriteLine($"[DataSanitizer] ⚠ UNKNOWN status '{raw}' → fallback 'on-time'");
        return "on-time";
    }

    // ═══════════════════ log_type ═══════════════════

    /// <summary>
    /// Normalizes a <c>log_type</c> value to match the
    /// <c>attendance_logs_log_type_valid</c> CHECK constraint.
    /// Allowed: NULL, 'IN', 'OUT'.
    /// </summary>
    public static string NormalizeLogType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "IN";

        var upper = raw.Trim().ToUpperInvariant();
        return upper switch
        {
            "IN"       => "IN",
            "OUT"      => "OUT",
            "TIME IN"  => "IN",
            "TIME OUT" => "OUT",
            "TIMEIN"   => "IN",
            "TIMEOUT"  => "OUT",
            _          => "IN"
        };
    }

    // ═══════════════════ admin_time_status ═══════════════════

    /// <summary>
    /// Normalizes an <c>admin_time_status</c> value to match the
    /// <c>attendance_logs_admin_time_status_check</c> CHECK constraint.
    /// Allowed: NULL, 'admin_time', 'regular'.
    /// </summary>
    public static string? NormalizeAdminTimeStatus(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var trimmed = raw.Trim().ToLowerInvariant();
        return trimmed switch
        {
            "admin_time"  => "admin_time",
            "admin-time"  => "admin_time",
            "admin time"  => "admin_time",
            "admintime"   => "admin_time",
            "regular"     => "regular",
            _             => null
        };
    }

    // ═══════════════════ Timestamp normalization ═══════════════════

    /// <summary>
    /// Common formats found in SQLite TEXT columns for date-times.
    /// </summary>
    private static readonly string[] TimestampFormats =
    [
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd HH:mm:ss.fff",
        "yyyy-MM-dd HH:mm:ss.fffffff",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss.fff",
        "yyyy-MM-ddTHH:mm:ss.fffffff",
        "yyyy-MM-dd",
        "MM/dd/yyyy HH:mm:ss",
        "M/d/yyyy h:mm:ss tt",
        "MM/dd/yyyy h:mm:ss tt",
        "M/d/yyyy HH:mm:ss"
    ];

    /// <summary>
    /// Safely parses a SQLite TEXT column into a <see cref="DateTime"/>
    /// suitable for PostgreSQL <c>timestamp</c>.
    ///
    /// Returns <c>null</c> when the value is null, empty, or unparseable
    /// — the caller decides whether to skip the row or substitute a fallback.
    /// </summary>
    public static DateTime? ParseTimestamp(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        if (DateTime.TryParseExact(raw.Trim(), TimestampFormats,
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
            return exact;

        // Fallback: general parse (handles culture-specific strings)
        if (DateTime.TryParse(raw.Trim(), CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var general))
            return general;

        Debug.WriteLine($"[DataSanitizer] ⚠ UNPARSEABLE timestamp '{raw}'");
        return null;
    }

    /// <summary>
    /// Safely parses a SQLite TEXT column into a <see cref="DateTime"/>
    /// suitable for PostgreSQL <c>date</c>.
    ///
    /// Returns <c>null</c> when the value is null, empty, or unparseable.
    /// </summary>
    public static DateTime? ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        if (DateTime.TryParseExact(raw.Trim(),
                ["yyyy-MM-dd", "MM/dd/yyyy", "M/d/yyyy"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
            return exact.Date;

        // Fallback: try full timestamp formats, take .Date
        var ts = ParseTimestamp(raw);
        return ts?.Date;
    }

    /// <summary>
    /// Formats a <see cref="DateTime"/> as an ISO 8601 timestamp string
    /// that PostgreSQL always accepts: <c>yyyy-MM-dd HH:mm:ss</c>.
    /// </summary>
    public static string FormatTimestamp(DateTime dt)
        => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>
    /// Formats a <see cref="DateTime"/> as an ISO 8601 date string
    /// that PostgreSQL always accepts: <c>yyyy-MM-dd</c>.
    /// </summary>
    public static string FormatDate(DateTime dt)
        => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
