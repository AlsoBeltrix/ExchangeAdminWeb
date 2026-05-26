using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExchangeAdminWeb.Services;

public class AuditService
{
    private readonly string _logFolder;
    private readonly string _rotationPeriod;
    private readonly ILogger<AuditService> _logger;
    private readonly object _lock = new();
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public AuditService(IConfiguration config, ILogger<AuditService> logger)
    {
        _logger = logger;
        var logRoot = config["Audit:LogRoot"] ?? @"E:\WWWOutput";
        _logFolder = Path.Combine(logRoot, "ExchangeAdminWeb");
        _rotationPeriod = config["Audit:RotationPeriod"]?.ToLowerInvariant() ?? "daily";

        Directory.CreateDirectory(_logFolder);
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
        var evt = new Dictionary<string, object?>
        {
            ["ts"] = DateTime.UtcNow.ToString("O"),
            ["user"] = SamName(performedBy),
            ["ip"] = ipAddress,
            ["action"] = action,
            ["category"] = "MailboxPermission",
            ["result"] = success ? "Success" : "Failed",
            ["ticket"] = string.IsNullOrEmpty(ticketNumber) ? null : ticketNumber,
            ["target"] = targetMailbox,
            ["affectedUser"] = affectedUser,
            ["permissionType"] = permissionType,
            ["autoMapping"] = autoMapping,
            ["error"] = success ? null : errorDetail
        };

        WriteEvent(evt);
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
        var evt = new Dictionary<string, object?>
        {
            ["ts"] = DateTime.UtcNow.ToString("O"),
            ["user"] = SamName(performedBy),
            ["ip"] = ipAddress,
            ["action"] = action,
            ["category"] = "CalendarPermission",
            ["result"] = success ? "Success" : "Failed",
            ["ticket"] = string.IsNullOrEmpty(ticketNumber) ? null : ticketNumber,
            ["target"] = targetMailbox,
            ["affectedUser"] = affectedUser,
            ["accessRight"] = accessRight,
            ["error"] = success ? null : errorDetail
        };

        WriteEvent(evt);
    }

    public void LogMigrationCheck(
        string performedBy,
        string ipAddress,
        string emailAddress,
        string status,
        string ticketNumber,
        string? reasons = null)
    {
        var evt = new Dictionary<string, object?>
        {
            ["ts"] = DateTime.UtcNow.ToString("O"),
            ["user"] = SamName(performedBy),
            ["ip"] = ipAddress,
            ["action"] = "CheckMigrationEligibility",
            ["category"] = "MigrationCheck",
            ["result"] = status == "Eligible" ? "Success" : "Ineligible",
            ["ticket"] = string.IsNullOrEmpty(ticketNumber) ? null : ticketNumber,
            ["target"] = emailAddress,
            ["status"] = status,
            ["reasons"] = reasons
        };

        WriteEvent(evt);
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
        var evt = new Dictionary<string, object?>
        {
            ["ts"] = DateTime.UtcNow.ToString("O"),
            ["user"] = SamName(performedBy),
            ["ip"] = ipAddress,
            ["action"] = $"CreateMigrationBatch_{direction}",
            ["category"] = "MigrationBatch",
            ["result"] = success ? "Success" : "Failed",
            ["ticket"] = string.IsNullOrEmpty(ticketNumber) ? null : ticketNumber,
            ["batchName"] = batchName,
            ["direction"] = direction,
            ["userCount"] = userCount,
            ["autoStart"] = autoStart,
            ["autoComplete"] = autoComplete,
            ["error"] = success ? null : errorDetail
        };

        WriteEvent(evt);
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
        var evt = new Dictionary<string, object?>
        {
            ["ts"] = DateTime.UtcNow.ToString("O"),
            ["user"] = SamName(performedBy),
            ["ip"] = ipAddress,
            ["action"] = action,
            ["category"] = "MigrationAction",
            ["result"] = success ? "Success" : "Failed",
            ["ticket"] = string.IsNullOrEmpty(ticketNumber) ? null : ticketNumber,
            ["target"] = target,
            ["error"] = success ? null : errorDetail
        };

        WriteEvent(evt);
    }

    public void LogLookupAction(
        string performedBy,
        string ipAddress,
        string action,
        string target,
        bool success,
        string? errorDetail = null)
    {
        var evt = new Dictionary<string, object?>
        {
            ["ts"] = DateTime.UtcNow.ToString("O"),
            ["user"] = SamName(performedBy),
            ["ip"] = ipAddress,
            ["action"] = action,
            ["category"] = "Lookup",
            ["result"] = success ? "Success" : "Failed",
            ["target"] = target,
            ["error"] = success ? null : errorDetail
        };

        WriteEvent(evt);
    }

    public void LogSettingsChange(
        string performedBy,
        string ipAddress,
        string section,
        string[] previousGroups,
        string[] newGroups)
    {
        var removed = previousGroups.Except(newGroups, StringComparer.OrdinalIgnoreCase).ToArray();
        var added = newGroups.Except(previousGroups, StringComparer.OrdinalIgnoreCase).ToArray();

        var evt = new Dictionary<string, object?>
        {
            ["ts"] = DateTime.UtcNow.ToString("O"),
            ["user"] = SamName(performedBy),
            ["ip"] = ipAddress,
            ["action"] = "UpdateSectionAccess",
            ["category"] = "AdminSettings",
            ["result"] = "Success",
            ["section"] = section,
            ["added"] = added.Length > 0 ? added : null,
            ["removed"] = removed.Length > 0 ? removed : null
        };

        WriteEvent(evt);
    }

    private void WriteEvent(Dictionary<string, object?> evt)
    {
        var filtered = evt.Where(kv => kv.Value != null).ToDictionary(kv => kv.Key, kv => kv.Value);
        var json = JsonSerializer.Serialize(filtered, JsonOpts);
        var logPath = Path.Combine(_logFolder, GetLogFilename());

        try
        {
            lock (_lock)
            {
                File.AppendAllText(logPath, json + "\n");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write audit event to {Path}", logPath);
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
        return $"exchangeadmin_{suffix}.jsonl";
    }

    private static string SamName(string identity) =>
        identity.Contains('\\') ? identity.Split('\\')[1] : identity;
}
