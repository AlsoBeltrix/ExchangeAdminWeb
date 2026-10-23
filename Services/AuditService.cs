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

    /// <param name="filedAs">
    /// aud-a73. The result the row is filed under when the caller knows more than <paramref name="success"/> says:
    /// Undetermined for a change whose outcome could not be confirmed. The row keeps <paramref name="errorDetail"/> unless
    /// it is filed Success. Null, the default, files Success or Failed from <paramref name="success"/>, as every row was.
    /// </param>
    /// <returns>
    /// What became of the row (aud-a50): the Mailbox Permissions page says when a refusal's row was not written. Every
    /// other caller may ignore it.
    /// </returns>
    public JsonlWriteOutcome LogMailboxPermission(
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
        Dictionary<string, object?>? extra = null,
        AuditResult? filedAs = null)
    {
        var result = ResultText(success, filedAs);
        var evt = new Dictionary<string, object?>
        {
            ["ts"] = DateTime.UtcNow.ToString("O"),
            ["user"] = SamName(performedBy),
            ["ip"] = ipAddress,
            ["action"] = action,
            ["category"] = "MailboxPermission",
            ["result"] = result,
            ["ticket"] = string.IsNullOrEmpty(ticketNumber) ? null : ticketNumber,
            ["target"] = targetMailbox,
            ["affectedUser"] = affectedUser,
            ["permissionType"] = permissionType,
            ["autoMapping"] = autoMapping,
            ["error"] = result == "Success" ? null : errorDetail
        };

        MergeExtra(evt, extra);
        WriteAuditEvent(evt, out var outcome);
        return outcome;
    }

    /// <param name="filedAs">aud-a73. As <see cref="LogMailboxPermission"/>'s.</param>
    /// <returns>What became of the row (aud-a50), as <see cref="LogMailboxPermission"/> hands it back.</returns>
    public JsonlWriteOutcome LogCalendarPermission(
        string performedBy,
        string ipAddress,
        string action,
        string targetMailbox,
        string affectedUser,
        string? accessRight,
        bool success,
        string ticketNumber,
        string? errorDetail = null,
        Dictionary<string, object?>? extra = null,
        AuditResult? filedAs = null)
    {
        var result = ResultText(success, filedAs);
        var evt = new Dictionary<string, object?>
        {
            ["ts"] = DateTime.UtcNow.ToString("O"),
            ["user"] = SamName(performedBy),
            ["ip"] = ipAddress,
            ["action"] = action,
            ["category"] = "CalendarPermission",
            ["result"] = result,
            ["ticket"] = string.IsNullOrEmpty(ticketNumber) ? null : ticketNumber,
            ["target"] = targetMailbox,
            ["affectedUser"] = affectedUser,
            ["accessRight"] = accessRight,
            ["error"] = result == "Success" ? null : errorDetail
        };

        MergeExtra(evt, extra);
        WriteAuditEvent(evt, out var outcome);
        return outcome;
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
            // aud-a28z part 5 (from aud-a23l). The result says what the check found. Every status but Eligible was filed
            // "Ineligible", so a check that could not decide (aud-a23h's Undetermined) or threw (the page's "Error") read
            // as a verdict against the mailbox. The status field keeps the caller's own word.
            ["result"] = status switch { "Eligible" => "Success", "Ineligible" => "Ineligible", _ => "Undetermined" },
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

    // The one method every audit method ends in. aud-a50: it hands back what became of the row - the writer's outcome
    // (aud-a28u) - through an out parameter rather than a return value, so the telemetry call stays its last statement
    // (UsageTelemetryServiceTests pins both) and the methods that ignore it are untouched (the overload below).
    private void WriteAuditEvent(Dictionary<string, object?> evt, out JsonlWriteOutcome outcome)
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

            // aud-a28u. The step says whether the row was written. It read Success whatever became of the row, so after
            // a drop the trace claimed an audit row the log did not have. A dropped row is a Failed step that says why;
            // the operation's own completion below stays the audit result's - an audit failure is logged separately
            // and never makes the operation appear to have failed (Constitution, Auditing And Tracing).
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
        // The writer never throws (aud-a28q) and says whether it wrote the row; the action is
        // counted only when it did (aud-a28u) - a usage count is a claim that an audited action
        // happened, and one whose row the audit log does not have is not a count it may claim.
        // RecordAction never throws.
        // Hooked here rather than in LogModuleAction because this is the ONE method every
        // public audit method ends in (review finding ute-2).
        if (outcome == JsonlWriteOutcome.Written)
            _usage?.RecordAction(
                GetString(evt, "category") ?? string.Empty,
                GetString(evt, "action") ?? string.Empty,
                IsSuccessfulAuditResult(GetString(evt, "result")));
    }

    // The write for every audit method that does not ask what became of its row.
    private void WriteAuditEvent(Dictionary<string, object?> evt) => WriteAuditEvent(evt, out _);

    // The "AuditWritten" step's details (aud-a28u): the audit's category, action and result, as before, and - when the row
    // was not written - why, in words, the writer's outcome being a category rather than a message.
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

    // aud-a28z. A field WriteAuditEvent reads - for the trace's operation, the step's details, the operation's result
    // and the usage count - is the caller's value as text. A caller's extra can put any object under any of them, and
    // its ToString ran unguarded, so one that threw escaped the audit call before the write or after it (aud-a28q's
    // second gap). A value whose ToString throws reads as "(unreadable <TypeName>)": its type's name, never the value.
    private static string? GetString(IReadOnlyDictionary<string, object?> evt, string key)
        => evt.TryGetValue(key, out var value) ? TextOf(value) : null;

    private static string? TextOf(object? value)
    {
        if (value is null or string)
            return (string?)value;

        try
        {
            return value.ToString();
        }
        catch (Exception)
        {
            return $"(unreadable {value.GetType().Name})";
        }
    }

    private static bool IsSuccessfulAuditResult(string? result)
        => !string.Equals(result, "Failed", StringComparison.OrdinalIgnoreCase);

    // aud-a73. A permission row's result: the caller's explicit one when it gives one, else Success or Failed from its bool,
    // as every row was filed before. A value outside the enum is no explicit result.
    private static string ResultText(bool success, AuditResult? filedAs) => filedAs switch
    {
        AuditResult.Success => "Success",
        AuditResult.Failed => "Failed",
        AuditResult.Undetermined => "Undetermined",
        _ => success ? "Success" : "Failed",
    };

    // aud-a28q. A null actor is "unknown", the name the trace gives an actor it does not have. Dereferenced, it threw
    // out of every public method here before the record was built.
    private static string SamName(string? identity) =>
        identity is null ? "unknown" : identity.Contains('\\') ? identity.Split('\\')[1] : identity;
}
