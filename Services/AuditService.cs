namespace ExchangeAdminWeb.Services;

public class AuditService
{
    private readonly JsonlLogService _log;
    private readonly OperationTraceService _operationTrace;
    private readonly UsageTelemetryService? _usage;

    /// <summary>
    /// <paramref name="usage"/> is the anonymous usage-telemetry sink
    /// (docs/UsageTelemetry-Plan.md, AC5). It is optional and defaults to null so every
    /// existing construction - the test suites included - compiles and behaves exactly as
    /// before; a null sink is a no-op.
    /// </summary>
    public AuditService(JsonlLogService log, OperationTraceService operationTrace, UsageTelemetryService? usage = null)
    {
        _log = log;
        _operationTrace = operationTrace;
        _usage = usage;
    }

    /// <summary>
    /// Merges caller-supplied fields into an audit event, whatever the outcome.
    /// </summary>
    /// <remarks>
    /// The ONLY channel for a detail that must survive a SUCCESSFUL operation. Every method here
    /// writes <c>["error"] = success ? null : errorDetail</c>, so a detail passed as
    /// <c>errorDetail</c> on a success is silently discarded - which is how an authorised-servicer
    /// record was lost once already (blr-era finding).
    ///
    /// That matters most for protected-principal servicing: the record exists precisely because
    /// the action SUCCEEDED against a principal the app would normally refuse, so the one field
    /// explaining why it was allowed cannot live in the failure channel.
    ///
    /// Merged last, so a caller can override a computed field deliberately rather than being
    /// silently ignored.
    /// </remarks>
    private static void MergeExtra(Dictionary<string, object?> evt, Dictionary<string, object?>? extra)
    {
        if (extra == null)
            return;

        foreach (var kvp in extra)
            evt[kvp.Key] = kvp.Value;
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
        string? errorDetail = null,
        Dictionary<string, object?>? extra = null)
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

        MergeExtra(evt, extra);
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
        string? errorDetail = null,
        Dictionary<string, object?>? extra = null)
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

        MergeExtra(evt, extra);
        WriteAuditEvent(evt);
    }

    public void LogMigrationCheck(
        string performedBy,
        string ipAddress,
        string emailAddress,
        string status,
        string ticketNumber,
        string? reasons = null,
        Dictionary<string, object?>? extra = null)
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

        MergeExtra(evt, extra);
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
        string? errorDetail = null,
        Dictionary<string, object?>? extra = null)
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

        MergeExtra(evt, extra);
        WriteAuditEvent(evt);
    }

    public void LogMigrationAction(
        string performedBy,
        string ipAddress,
        string action,
        string target,
        bool success,
        string ticketNumber = "",
        string? errorDetail = null,
        Dictionary<string, object?>? extra = null)
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

        MergeExtra(evt, extra);
        WriteAuditEvent(evt);
    }

    public void LogModuleAction(
        string performedBy,
        string ipAddress,
        string action,
        string category,
        string target,
        bool success,
        string ticketNumber = "",
        string? errorDetail = null,
        Dictionary<string, object?>? extra = null)
    {
        var evt = new Dictionary<string, object?>
        {
            ["ts"] = DateTime.UtcNow.ToString("O"),
            ["user"] = SamName(performedBy),
            ["ip"] = ipAddress,
            ["action"] = action,
            ["category"] = category,
            ["result"] = success ? "Success" : "Failed",
            ["target"] = target,
            ["ticket"] = string.IsNullOrWhiteSpace(ticketNumber) ? null : ticketNumber.Trim(),
            ["error"] = success ? null : errorDetail
        };

        MergeExtra(evt, extra);
        WriteAuditEvent(evt);
    }

    public void LogLookupAction(
        string performedBy,
        string ipAddress,
        string action,
        string target,
        bool success,
        string? errorDetail = null,
        string ticketNumber = "",
        Dictionary<string, object?>? extra = null)
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

        MergeExtra(evt, extra);
        WriteAuditEvent(evt);
    }

    public void LogMfaResetAction(
        string performedBy,
        string ipAddress,
        string action,
        string target,
        bool success,
        string ticketNumber = "",
        string? errorDetail = null,
        int? removedCount = null,
        int? totalMethods = null,
        string[]? methodTypes = null,
        Dictionary<string, object?>? extra = null)
    {
        var evt = new Dictionary<string, object?>
        {
            ["ts"] = DateTime.UtcNow.ToString("O"),
            ["user"] = SamName(performedBy),
            ["ip"] = ipAddress,
            ["action"] = action,
            ["category"] = "MfaReset",
            ["result"] = success ? "Success" : "Failed",
            ["target"] = target,
            ["ticket"] = string.IsNullOrWhiteSpace(ticketNumber) ? null : ticketNumber.Trim(),
            ["error"] = success ? null : errorDetail,
            ["removedCount"] = removedCount,
            ["totalMethods"] = totalMethods,
            ["methodTypes"] = methodTypes is { Length: > 0 } ? methodTypes : null
        };

        MergeExtra(evt, extra);
        WriteAuditEvent(evt);
    }

    // virtual: test seam so the bulk job processor's per-row audit calls can be observed in tests
    // (no behavior change; mirrors the EmailService admin-notify seam from the notifications sweep).
    public virtual void LogConferenceRoomAction(
        string performedBy,
        string ipAddress,
        string action,
        string target,
        bool success,
        string ticketNumber = "",
        string? errorDetail = null,
        Dictionary<string, object?>? oldValues = null,
        Dictionary<string, object?>? newValues = null,
        Dictionary<string, object?>? extra = null)
    {
        var evt = new Dictionary<string, object?>
        {
            ["ts"] = DateTime.UtcNow.ToString("O"),
            ["user"] = SamName(performedBy),
            ["ip"] = ipAddress,
            ["action"] = action,
            ["category"] = "ConferenceRooms",
            ["result"] = success ? "Success" : "Failed",
            ["target"] = target,
            ["ticket"] = string.IsNullOrWhiteSpace(ticketNumber) ? null : ticketNumber.Trim(),
            ["error"] = success ? null : errorDetail
        };

        if (oldValues != null)
        {
            foreach (var kv in oldValues)
                evt[$"old_{kv.Key}"] = kv.Value;
        }
        if (newValues != null)
        {
            foreach (var kv in newValues)
                evt[$"new_{kv.Key}"] = kv.Value;
        }

        MergeExtra(evt, extra);
        WriteAuditEvent(evt);
    }

    public void LogADAttributeEdit(
        string performedBy,
        string ipAddress,
        string target,
        List<AttributeChange> changes,
        bool success,
        string ticketNumber,
        string? errorDetail = null,
        Dictionary<string, object?>? extra = null)
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

        MergeExtra(evt, extra);
        WriteAuditEvent(evt);
    }

    public void LogSettingsChange(
        string performedBy,
        string ipAddress,
        string section,
        string[] previousGroups,
        string[] newGroups,
        Dictionary<string, object?>? extra = null)
    {
        var removed = previousGroups.Except(newGroups, StringComparer.OrdinalIgnoreCase).ToArray();
        var added = newGroups.Except(previousGroups, StringComparer.OrdinalIgnoreCase).ToArray();

        var evt = new Dictionary<string, object?>
        {
            ["ts"] = DateTime.UtcNow.ToString("O"),
            ["user"] = SamName(performedBy),
            ["ip"] = ipAddress,
            ["action"] = SettingsChangeAction,
            ["category"] = SettingsChangeCategory,
            ["result"] = "Success",
            ["section"] = section,
            ["added"] = added.Length > 0 ? added : null,
            ["removed"] = removed.Length > 0 ? removed : null
        };

        MergeExtra(evt, extra);
        WriteAuditEvent(evt);
    }

    /// <summary>
    /// A settings save that was refused or failed, filed as <see cref="LogSettingsChange"/> files a
    /// successful one - same action, category and section - so an admin reading a section's changes
    /// sees the attempts that did not land beside them (aud-a28f).
    /// </summary>
    /// <remarks>
    /// LogSettingsChange can only say Success, so a settings save that ended any other way could not
    /// be recorded at all. <paramref name="errorDetail"/> is what the admin was shown. A refusal
    /// passes aud-a28's <c>refusedAt</c> and <c>mutationAttempted</c> through
    /// <paramref name="extra"/>; a failed write passes neither, because it may have been tried.
    /// </remarks>
    public void LogSettingsChangeFailed(
        string performedBy,
        string ipAddress,
        string section,
        string errorDetail,
        Dictionary<string, object?>? extra = null)
    {
        var evt = new Dictionary<string, object?>
        {
            ["ts"] = DateTime.UtcNow.ToString("O"),
            ["user"] = SamName(performedBy),
            ["ip"] = ipAddress,
            ["action"] = SettingsChangeAction,
            ["category"] = SettingsChangeCategory,
            ["result"] = "Failed",
            ["section"] = section,
            ["error"] = errorDetail
        };

        MergeExtra(evt, extra);
        WriteAuditEvent(evt);
    }

    /// <summary>
    /// A connection-pool drain that failed after the Exchange Online connection settings were saved
    /// (aud-a26k): result Failed, its own action, filed in the settings category beside the save it follows.
    /// </summary>
    /// <remarks>
    /// It used the settings-change action, so an administrator filtering the Admin Event Log by action could not
    /// tell a failed drain - the settings saved, old connections still open - from a failed settings change. The
    /// category stays the settings one so the drain sits beside the save it follows.
    /// </remarks>
    public void LogConnectionPoolDrainFailed(
        string performedBy,
        string ipAddress,
        string section,
        string errorDetail)
    {
        var evt = new Dictionary<string, object?>
        {
            ["ts"] = DateTime.UtcNow.ToString("O"),
            ["user"] = SamName(performedBy),
            ["ip"] = ipAddress,
            ["action"] = ConnectionPoolDrainAction,
            ["category"] = SettingsChangeCategory,
            ["result"] = "Failed",
            ["section"] = section,
            ["error"] = errorDetail
        };

        WriteAuditEvent(evt);
    }

    // Shared by LogSettingsChange and LogSettingsChangeFailed, so a refusal can never be filed apart
    // from the changes it would have made. "UpdateSectionAccess" is the historical action name for
    // every settings change, not only section access; renaming it would split the log in two.
    private const string SettingsChangeAction = "UpdateSectionAccess";
    private const string SettingsChangeCategory = "AdminSettings";
    private const string ConnectionPoolDrainAction = "DrainConnectionPool";

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

        JsonlWriteOutcome outcome;
        try
        {
            evt["eventType"] = "audit";
            evt["operationId"] = _operationTrace.CurrentOperationId;
            outcome = _log.Write(evt);
            _operationTrace.Step(
                "AuditWritten",
                result: outcome == JsonlWriteOutcome.Written ? "Success" : "Failed",
                details: AuditWrittenDetails(evt, outcome));

            var operationSucceeded = IsSuccessfulAuditResult(GetString(evt, "result"));
            var operationMessage = operationSucceeded ? null : "Audit event recorded failure";
            implicitScope?.Complete(operationSucceeded, operationMessage);
        }
        finally
        {
            implicitScope?.Dispose();
        }

        // Anonymous usage telemetry (docs/UsageTelemetry-Plan.md, AC5). Deliberately the LAST
        // statement and OUTSIDE the try/finally: the audit row has been handed to the writer and
        // the operation scope already closed, so a telemetry call cannot mask an audit failure.
        // The writer logs its own failure and never throws (aud-a28q), so the action is counted
        // whether or not its row could be written - it happened. RecordAction never throws.
        // Hooked here rather than in LogModuleAction because this is the ONE method every
        // public audit method ends in (review finding ute-2).
        if (outcome == JsonlWriteOutcome.Written)
            _usage?.RecordAction(
                GetString(evt, "category") ?? string.Empty,
                GetString(evt, "action") ?? string.Empty,
                IsSuccessfulAuditResult(GetString(evt, "result")));
    }

    private static Dictionary<string, object?> AuditWrittenDetails(IReadOnlyDictionary<string, object?> evt, JsonlWriteOutcome outcome)
    {
        var details = new Dictionary<string, object?>
        {
            ["auditCategory"] = GetString(evt, "category"),
            ["auditAction"] = GetString(evt, "action"),
            ["auditResult"] = GetString(evt, "result")
        };

        if (outcome != JsonlWriteOutcome.Written)
        {
            details["reason"] = outcome == JsonlWriteOutcome.NotSerializable
                ? "the audit row was not written: the record could not be serialised"
                : "the audit row was not written: the log file could not be written";
        }

        return details;
    }

    private static string? GetAuditTarget(IReadOnlyDictionary<string, object?> evt)
        => GetString(evt, "target") ?? GetString(evt, "batchName") ?? GetString(evt, "section");

    private static string? GetString(IReadOnlyDictionary<string, object?> evt, string key)
        => evt.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static bool IsSuccessfulAuditResult(string? result)
        => !string.Equals(result, "Failed", StringComparison.OrdinalIgnoreCase);

    // aud-a28q. A null actor is "unknown", the name the trace gives an actor it does not have. Dereferenced, it threw
    // out of every public method here before the record was built.
    private static string SamName(string? identity) =>
        identity is null ? "unknown" : identity.Contains('\\') ? identity.Split('\\')[1] : identity;
}
