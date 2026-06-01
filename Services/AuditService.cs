namespace ExchangeAdminWeb.Services;

public class AuditService
{
    private readonly JsonlLogService _log;
    private readonly OperationTraceService _operationTrace;

    public AuditService(JsonlLogService log, OperationTraceService operationTrace)
    {
        _log = log;
        _operationTrace = operationTrace;
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

        WriteAuditEvent(evt);
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

        WriteAuditEvent(evt);
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

        WriteAuditEvent(evt);
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

        WriteAuditEvent(evt);
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
            ["target"] = target,
            ["ticket"] = string.IsNullOrWhiteSpace(ticketNumber) ? null : ticketNumber.Trim(),
            ["error"] = success ? null : errorDetail
        };

        WriteAuditEvent(evt);
    }

    public void LogLookupAction(
        string performedBy,
        string ipAddress,
        string action,
        string target,
        bool success,
        string? errorDetail = null,
        string ticketNumber = "")
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
            ["ticket"] = string.IsNullOrWhiteSpace(ticketNumber) ? null : ticketNumber.Trim(),
            ["error"] = success ? null : errorDetail
        };

        WriteAuditEvent(evt);
    }

    public void LogADAttributeEdit(
        string performedBy,
        string ipAddress,
        string target,
        List<AttributeChange> changes,
        bool success,
        string ticketNumber,
        string? errorDetail = null)
    {
        var changedAttrs = changes.Select(c => c.Name).ToArray();
        var evt = new Dictionary<string, object?>
        {
            ["ts"] = DateTime.UtcNow.ToString("O"),
            ["user"] = SamName(performedBy),
            ["ip"] = ipAddress,
            ["action"] = "ADAttributeEditor_Update",
            ["category"] = "ADAttributeEditor",
            ["result"] = success ? "Success" : "Failed",
            ["ticket"] = string.IsNullOrWhiteSpace(ticketNumber) ? null : ticketNumber.Trim(),
            ["target"] = target,
            ["changedAttributes"] = changedAttrs,
            ["error"] = success ? null : errorDetail
        };

        foreach (var change in changes)
        {
            evt[$"old_{change.Name}"] = change.OldValue;
            evt[$"new_{change.Name}"] = change.NewValue;
        }

        WriteAuditEvent(evt);
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

        WriteAuditEvent(evt);
    }

    private void WriteAuditEvent(Dictionary<string, object?> evt)
    {
        OperationTraceService.OperationScope? implicitScope = null;
        if (!_operationTrace.HasCurrentOperation)
        {
            implicitScope = _operationTrace.BeginOperation(
                module: GetString(evt, "category") ?? "Audit",
                action: GetString(evt, "action") ?? "AuditEvent",
                actor: GetString(evt, "user") ?? "unknown",
                ipAddress: GetString(evt, "ip") ?? "unknown",
                target: GetAuditTarget(evt),
                ticket: GetString(evt, "ticket"),
                details: new Dictionary<string, object?> { ["source"] = "AuditService" });
        }

        try
        {
            evt["eventType"] = "audit";
            evt["operationId"] = _operationTrace.CurrentOperationId;
            _log.Write(evt);
            _operationTrace.Step(
                "AuditWritten",
                result: "Success",
                details: new Dictionary<string, object?>
                {
                    ["auditCategory"] = GetString(evt, "category"),
                    ["auditAction"] = GetString(evt, "action"),
                    ["auditResult"] = GetString(evt, "result")
                });

            var operationSucceeded = IsSuccessfulAuditResult(GetString(evt, "result"));
            var operationMessage = operationSucceeded ? null : "Audit event recorded failure";
            implicitScope?.Complete(operationSucceeded, operationMessage);
        }
        finally
        {
            implicitScope?.Dispose();
        }
    }

    private static string? GetAuditTarget(IReadOnlyDictionary<string, object?> evt)
        => GetString(evt, "target") ?? GetString(evt, "batchName") ?? GetString(evt, "section");

    private static string? GetString(IReadOnlyDictionary<string, object?> evt, string key)
        => evt.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static bool IsSuccessfulAuditResult(string? result)
        => !string.Equals(result, "Failed", StringComparison.OrdinalIgnoreCase);

    private static string SamName(string identity) =>
        identity.Contains('\\') ? identity.Split('\\')[1] : identity;
}
