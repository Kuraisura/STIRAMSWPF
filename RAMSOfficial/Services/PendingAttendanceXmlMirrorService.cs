using System.Xml.Linq;
using System.IO;

namespace RAMSOfficial.Services;

/// <summary>
/// Mirrors pending attendance queue into an XML snapshot file.
/// This is an audit/backup copy of offline logs while SQLite remains
/// the primary queue source of truth for retries and uploads.
/// </summary>
public static class PendingAttendanceXmlMirrorService
{
    public static async Task WriteSnapshotAsync(string xmlPath, IEnumerable<PendingAttendanceLog> logs)
    {
        var directory = Path.GetDirectoryName(xmlPath);
        if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        var now = DateTime.Now;
        var root = new XElement("pending_attendance_snapshot",
            new XAttribute("generated_at", now.ToString("yyyy-MM-dd HH:mm:ss")),
            logs.Select(l => new XElement("log",
                new XAttribute("local_id", l.LocalId),
                new XElement("employee_id", l.EmployeeId),
                new XElement("rfid_code", l.RfidCode ?? string.Empty),
                new XElement("log_time", l.LogTime ?? string.Empty),
                new XElement("log_type", l.LogType ?? string.Empty),
                new XElement("date", l.Date ?? string.Empty),
                new XElement("attendance_status", l.AttendanceStatus ?? string.Empty),
                new XElement("is_late", l.IsLate),
                new XElement("is_early_out", l.IsEarlyOut),
                new XElement("late_minutes", l.LateMinutes?.ToString() ?? string.Empty),
                new XElement("undertime_minutes", l.UndertimeMinutes?.ToString() ?? string.Empty),
                new XElement("notes", l.Notes ?? string.Empty),
                new XElement("schedule_id", l.ScheduleId?.ToString() ?? string.Empty),
                new XElement("term_id", l.TermId?.ToString() ?? string.Empty),
                new XElement("sync_status", l.SyncStatus),
                new XElement("sync_attempts", l.SyncAttempts),
                new XElement("sync_error", l.SyncError ?? string.Empty),
                new XElement("created_at", l.CreatedAt ?? string.Empty),
                new XElement("last_sync_attempt", l.LastSyncAttempt ?? string.Empty)
            )));

        var doc = new XDocument(new XDeclaration("1.0", "utf-8", "yes"), root);

        await using var stream = new FileStream(xmlPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        await doc.SaveAsync(stream, SaveOptions.None, CancellationToken.None);
    }
}
