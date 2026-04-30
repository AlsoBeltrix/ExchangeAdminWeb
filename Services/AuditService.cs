using System.Globalization;
using System.Text;

namespace ExchangeAdminWeb.Services;

public class AuditService
{
    private readonly string _logFolder;
    private readonly string _rotationPeriod;
    private readonly object _lock = new();
    private static readonly string CsvHeader = "TimestampUtc,User,TicketNumber,Action,TargetMailbox,AffectedUser,PermissionType,AutoMapping,AccessRight,Result,Error";

    public AuditService(IConfiguration config)
    {
        var logRoot = config["Audit:LogRoot"] ?? @"E:\WWWOutput";
        _logFolder = Path.Combine(logRoot, "ExchangeAdminWeb");
        _rotationPeriod = config["Audit:RotationPeriod"]?.ToLowerInvariant() ?? "daily";

        Directory.CreateDirectory(_logFolder);
    }

    public void LogMailboxPermission(
        string performedBy,
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
        try
        {
            var filename = GetLogFilename();
            var logPath = Path.Combine(_logFolder, filename);

            lock (_lock)
            {
                // Write header if file doesn't exist
                if (!File.Exists(logPath))
                {
                    File.WriteAllText(logPath, CsvHeader + Environment.NewLine);
                }

                File.AppendAllText(logPath, csvLine + Environment.NewLine);
            }
        }
        catch
        {
            // Log write failure must not surface to the user
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
