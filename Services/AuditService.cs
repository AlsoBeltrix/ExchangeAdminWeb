using System.Globalization;
using System.Text;

namespace ExchangeAdminWeb.Services;

public class AuditService
{
    private readonly string _logFolder;
    private readonly string _rotationPeriod;
    private readonly object _lock = new();
    private static readonly string CsvHeader = "TimestampUtc,User,IPAddress,TicketNumber,Action,TargetMailbox,AffectedUser,PermissionType,AutoMapping,AccessRight,Result,Error";

    public AuditService(IConfiguration config)
    {
        var logRoot = config["Audit:LogRoot"] ?? @"E:\WWWOutput";
        _logFolder = Path.Combine(logRoot, "ExchangeAdminWeb");
        _rotationPeriod = config["Audit:RotationPeriod"]?.ToLowerInvariant() ?? "daily";

        Directory.CreateDirectory(_logFolder);
        MigrateOldLogFormatIfNeeded();
    }

    private void MigrateOldLogFormatIfNeeded()
    {
        try
        {
            // Check if current log file exists and has old format (no IPAddress column)
            var currentLogFile = Path.Combine(_logFolder, GetLogFilename());

            if (File.Exists(currentLogFile))
            {
                var firstLine = File.ReadLines(currentLogFile).FirstOrDefault();
                // Old format: "TimestampUtc,User,TicketNumber,Action..."
                // New format: "TimestampUtc,User,IPAddress,TicketNumber,Action..."
                if (firstLine != null && firstLine.StartsWith("TimestampUtc,User,TicketNumber"))
                {
                    // Old format detected - rename it
                    var backupName = currentLogFile.Replace(".csv", $"_old_format_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
                    File.Move(currentLogFile, backupName);

                    // Log will be recreated with new header on next write
                    Console.WriteLine($"[AuditService] Migrated old log format to: {Path.GetFileName(backupName)}");
                }
            }
        }
        catch
        {
            // Don't fail startup if log migration fails
        }
    }

    public void LogMailboxPermission(
        string performedBy,
        string ipAddress,
        string action,
        string targetMailbox,
        string affectedUser,
        string permissionType,
        bool success,
        string ticketNumber,
        bool? autoMapping = null,
        string? errorDetail = null)
    {
        var csvLine = BuildCsvLine(
            DateTime.UtcNow.ToString("O"),
            SamName(performedBy),
            ipAddress,
            ticketNumber,
            action,
            targetMailbox,
            affectedUser,
            permissionType,
            autoMapping?.ToString() ?? "",
            "",
            success ? "SUCCESS" : "FAILED",
            errorDetail ?? "");

        WriteLog(csvLine);
    }

    public void LogCalendarPermission(
        string performedBy,
        string ipAddress,
        string action,
        string targetMailbox,
        string affectedUser,
        string? accessRight,
        bool success,
        string ticketNumber,
        string? errorDetail = null)
    {
        var csvLine = BuildCsvLine(
            DateTime.UtcNow.ToString("O"),
            SamName(performedBy),
            ipAddress,
            ticketNumber,
            action,
            targetMailbox,
            affectedUser,
            "Calendar",
            "",
            accessRight ?? "",
            success ? "SUCCESS" : "FAILED",
            errorDetail ?? "");

        WriteLog(csvLine);
    }

    public void LogMigrationCheck(
        string performedBy,
        string ipAddress,
        string emailAddress,
        string status,
        string ticketNumber,
        string? reasons = null)
    {
        var csvLine = BuildCsvLine(
            DateTime.UtcNow.ToString("O"),
            SamName(performedBy),
            ipAddress,
            ticketNumber,
            "CheckMigrationEligibility",
            emailAddress,
            "N/A",
            "Migration",
            "",
            status,
            status == "Eligible" ? "SUCCESS" : "INELIGIBLE",
            reasons ?? "");

        WriteLog(csvLine);
    }

    public void LogMigrationBatch(
        string performedBy,
        string ipAddress,
        string batchName,
        string direction,
        int userCount,
        bool autoStart,
        bool autoComplete,
        string ticketNumber,
        bool success,
        string? errorDetail = null)
    {
        var options = $"Users:{userCount},AutoStart:{autoStart},AutoComplete:{autoComplete}";
        var csvLine = BuildCsvLine(
            DateTime.UtcNow.ToString("O"),
            SamName(performedBy),
            ipAddress,
            ticketNumber,
            $"CreateMigrationBatch_{direction}",
            batchName,
            "N/A",
            "Migration",
            "",
            options,
            success ? "SUCCESS" : "FAILED",
            errorDetail ?? "");

        WriteLog(csvLine);
    }

    public void LogMigrationAction(
        string performedBy,
        string ipAddress,
        string action,
        string target,
        bool success,
        string ticketNumber = "",
        string? errorDetail = null)
    {
        var csvLine = BuildCsvLine(
            DateTime.UtcNow.ToString("O"),
            SamName(performedBy),
            ipAddress,
            ticketNumber,
            action,
            target,
            "N/A",
            "Migration",
            "",
            "",
            success ? "SUCCESS" : "FAILED",
            errorDetail ?? "");

        WriteLog(csvLine);
    }

    private string BuildCsvLine(params string[] fields)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < fields.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(EscapeCsvField(fields[i]));
        }
        return sb.ToString();
    }

    private string EscapeCsvField(string field)
    {
        if (string.IsNullOrEmpty(field))
            return "";

        // If field contains comma, quote, or newline, wrap in quotes and escape internal quotes
        if (field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r'))
        {
            return $"\"{field.Replace("\"", "\"\"")}\"";
        }

        return field;
    }

    private void WriteLog(string csvLine)
    {
        var filename = GetLogFilename();
        var logPath = Path.Combine(_logFolder, filename);

        lock (_lock)
        {
            if (!File.Exists(logPath))
            {
                File.WriteAllText(logPath, CsvHeader + Environment.NewLine);
            }

            File.AppendAllText(logPath, csvLine + Environment.NewLine);
        }
    }

    private string GetLogFilename()
    {
        var now = DateTime.Now;
        var suffix = _rotationPeriod switch
        {
            "weekly" => $"{now.Year}W{ISOWeek.GetWeekOfYear(now):D2}",
            "monthly" => now.ToString("yyyyMM"),
            _ => now.ToString("yyyyMMdd")
        };
        return $"exchangeadmin_{suffix}.csv";
    }

    // Strips domain prefix: "ANALOG\mcoelho" → "mcoelho", "mcoelho" → "mcoelho"
    private static string SamName(string identity) =>
        identity.Contains('\\') ? identity.Split('\\')[1] : identity;
}
