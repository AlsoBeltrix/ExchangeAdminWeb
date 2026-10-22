using System.Text.Json;

namespace ExchangeAdminWeb.Services;

public class ADAttributeEditorUndoService : IUndoableModule
{
    private readonly ADAttributeEditorService _editorService;
    private readonly ProtectedPrincipalService _protectedPrincipalService;
    private readonly ProtectedPrincipalServicerService _servicers;

    /// <summary>Module id for the servicer grant. Must match the catalog descriptor.</summary>
    private const string ServicerModuleId = "ADAttributeEditor";
    private readonly ModuleEnablementService _moduleEnablement;
    private readonly AuditService _audit;
    private readonly OperationTraceService _operationTrace;

    // Audit finding A26. Undo wrote the audit record and sent nothing. Notification dispatch
    // for attribute edits lived in the forward-edit PAGE, and undo does not go through it, so
    // a reversal was the one attribute change administrators never heard about. Since aud-a26b
    // SaveAsync announces its own results; this is kept for the exception path only.
    private readonly EmailService _email;
    private readonly ILogger<ADAttributeEditorUndoService> _logger;

    public ADAttributeEditorUndoService(
        ADAttributeEditorService editorService,
        ProtectedPrincipalService protectedPrincipalService,
        ProtectedPrincipalServicerService servicers,
        ModuleEnablementService moduleEnablement,
        AuditService audit,
        OperationTraceService operationTrace,
        EmailService email,
        ILogger<ADAttributeEditorUndoService> logger)
    {
        _editorService = editorService;
        _protectedPrincipalService = protectedPrincipalService;
        _servicers = servicers;
        _moduleEnablement = moduleEnablement;
        _audit = audit;
        _operationTrace = operationTrace;
        _email = email;
        _logger = logger;
    }

    public string ModuleId => "ADAttributeEditor";

    public bool CanUndo(Dictionary<string, object?> auditEvent)
    {
        var category = GetString(auditEvent, "category");
        var action = GetString(auditEvent, "action");
        var result = GetString(auditEvent, "result");

        if (!string.Equals(category, "ADAttributeEditor", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.Equals(action, "ADAttributeEditor_Update", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.Equals(result, "Success", StringComparison.OrdinalIgnoreCase))
            return false;

        // aud-a26m. Each changed attribute needs one recorded side, not both. The JSONL writer drops a null field,
        // so the record of a clear has no new_<attr> and the record of setting an attribute that had no value has
        // no old_<attr>; asking for an old_ key and a new_ key left every such edit without its Undo, though the
        // preview and the execute paths already read a missing side as "not set". An attribute with neither side
        // has nothing to restore from and nothing to check the directory against.
        var changedAttributes = GetStringArray(auditEvent, "changedAttributes");
        return changedAttributes.Length > 0
            && changedAttributes.All(attr => HasSide(auditEvent, $"old_{attr}") || HasSide(auditEvent, $"new_{attr}"));
    }

    // A side is a key present with a value. A null, or a JSON null, is absent: the writer would have dropped it.
    private static bool HasSide(Dictionary<string, object?> auditEvent, string key) =>
        auditEvent.TryGetValue(key, out var value)
        && value is not null
        && value is not JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined };

    public async Task<UndoPreview> PreviewUndoAsync(Dictionary<string, object?> auditEvent, System.Security.Claims.ClaimsPrincipal? actingUser)
    {
        var target = GetString(auditEvent, "target");
        var operationId = GetString(auditEvent, "operationId");
        var action = GetString(auditEvent, "action") ?? "ADAttributeEditor_Update";

        if (string.IsNullOrWhiteSpace(target))
            return ErrorPreview("Cannot undo: no target identity in audit event.", action, operationId);

        if (!_moduleEnablement.IsModuleEnabled("ADAttributeEditor"))
            return ErrorPreview("ADAttributeEditor module is not enabled.", action, operationId);

        var changedAttributes = GetStringArray(auditEvent, "changedAttributes");
        if (changedAttributes.Length == 0)
            return ErrorPreview("Cannot undo: no changed attributes recorded in audit event.", action, operationId);

        // Look up current state of the target
        var lookupResult = await _editorService.LookupAsync(target);
        if (!lookupResult.Success || lookupResult.CurrentValues == null)
            return ErrorPreview($"Cannot read current state of '{target}': {lookupResult.Error ?? "lookup failed"}", action, operationId);

        var identityProblem = IdentityMismatch(auditEvent, lookupResult.Principal, target);
        if (identityProblem != null)
            return ErrorPreview(identityProblem, action, operationId);

        // Check protected principal
        if (lookupResult.Principal != null)
        {
            var protCheck = await _protectedPrincipalService.CheckAsync(lookupResult.Principal);
            if (protCheck.CheckFailed)
                return ErrorPreview($"Protected principal check failed: {protCheck.Reason}", action, operationId);

            // Must reach the same decision as ExecuteUndoAsync below. A preview that refuses what
            // execute would allow denies the servicer an Undo button, so the grant is unreachable
            // even though the execute gate honours it (pps-2).
            //
            // Discarding the note is correct HERE and only here: a preview performs no write and
            // emits no audit event, so there is no record for it to belong to. Every other caller
            // must keep it - see pps-3, where exactly this call shape on a WRITE path allowed the
            // operation and threw away the record of who permitted it.
            if (protCheck.IsProtected
                && ProtectedPrincipalServicing.NoteFor(
                    _servicers, actingUser, ServicerModuleId, protCheck.MatchedRules) is null)
            {
                return ErrorPreview("Target is a protected principal and cannot be modified.", action, operationId);
            }
        }

        // Build change list with conflict detection
        var changes = new List<UndoPreviewChange>();
        var hasConflict = false;
        string? conflictDetail = null;

        foreach (var attr in changedAttributes)
        {
            var oldValue = GetString(auditEvent, $"old_{attr}");
            var newValue = GetString(auditEvent, $"new_{attr}");
            lookupResult.CurrentValues.TryGetValue(attr, out var currentValue);

            // Normalize empty to null for comparison
            var currentNorm = string.IsNullOrEmpty(currentValue) ? null : currentValue;
            var newNorm = string.IsNullOrEmpty(newValue) ? null : newValue;

            var isConflict = !string.Equals(currentNorm, newNorm, StringComparison.Ordinal);
            if (isConflict)
            {
                hasConflict = true;
                conflictDetail ??= $"Attribute '{attr}' has been modified since the original operation. " +
                    $"Expected '{newNorm ?? "(empty)"}' but found '{currentNorm ?? "(empty)"}'.";
            }

            changes.Add(new UndoPreviewChange(
                Field: attr,
                CurrentValue: currentNorm,
                ExpectedValue: newNorm,
                RevertToValue: oldValue,
                IsConflict: isConflict));
        }

        return new UndoPreview(
            CanUndo: !hasConflict,
            Error: hasConflict ? "One or more attributes have been modified since the original operation." : null,
            Module: "ADAttributeEditor",
            Action: action,
            Target: target,
            OriginalOperationId: operationId,
            Changes: changes,
            HasConflict: hasConflict,
            ConflictDetail: conflictDetail);
    }

    public async Task<UndoResult> ExecuteUndoAsync(
        Dictionary<string, object?> auditEvent,
        string performedBy,
        string ip,
        string ticket,
        // REQUIRED: undo reverses a change to a principal that may since have become protected,
        // so it needs the same servicer decision as a forward edit. Null refuses.
        System.Security.Claims.ClaimsPrincipal? actingUser)
    {
        var target = GetString(auditEvent, "target");
        var originalOperationId = GetString(auditEvent, "operationId");

        // aud-a28d: every refusal below writes the undo record before it returns, marked with where
        // it was refused and that nothing was sent to the directory. They used to complete only
        // the diagnostic trace, and the event-log page that calls this audited nothing; the one
        // refusal the page makes itself, its authorization re-check, is recorded through
        // LogUndoRefused (aud-a28l).
        if (string.IsNullOrWhiteSpace(target))
        {
            const string noTarget = "No target identity in audit event.";
            LogUndoAudit(performedBy, ip, "(none)", [], auditEvent, originalOperationId, ticket, false, noTarget, servicedNote: null, refusedAt: "AuditRecord");
            return new UndoResult(false, noTarget, null);
        }

        if (!_moduleEnablement.IsModuleEnabled("ADAttributeEditor"))
        {
            const string disabled = "ADAttributeEditor module is not enabled.";
            LogUndoAudit(performedBy, ip, target, [], auditEvent, originalOperationId, ticket, false, disabled, servicedNote: null, refusedAt: "ModuleEnablement");
            return new UndoResult(false, disabled, null);
        }

        var changedAttributes = GetStringArray(auditEvent, "changedAttributes");
        if (changedAttributes.Length == 0)
        {
            const string noChanges = "No changed attributes recorded in audit event.";
            LogUndoAudit(performedBy, ip, target, changedAttributes, auditEvent, originalOperationId, ticket, false, noChanges, servicedNote: null, refusedAt: "AuditRecord");
            return new UndoResult(false, noChanges, null);
        }

        // aud-a28o. The operation is begun inside the try, so a throw there reaches the catch below and is the
        // undo's recorded, announced failure rather than an exception out of the module with no record. The
        // finally disposes it, as the using did.
        OperationTraceService.OperationScope? scope = null;
        try
        {
            scope = BeginUndoOperation(performedBy, ip, target, ticket, originalOperationId);

            // Re-read current state
            var lookupResult = await _editorService.LookupAsync(target);
            if (!lookupResult.Success || lookupResult.CurrentValues == null || lookupResult.Principal == null)
            {
                var error = $"Cannot read current state of '{target}': {lookupResult.Error ?? "lookup failed"}";
                LogUndoAudit(performedBy, ip, target, changedAttributes, auditEvent, originalOperationId, ticket, false, error, servicedNote: null, refusedAt: "Lookup");
                scope.Complete(false, error);
                return new UndoResult(false, error, null);
            }

            var identityProblem = IdentityMismatch(auditEvent, lookupResult.Principal, target);
            if (identityProblem != null)
            {
                LogUndoAudit(performedBy, ip, target, changedAttributes, auditEvent, originalOperationId, ticket, false, identityProblem, servicedNote: null, refusedAt: "IdentityCheck");
                scope.Complete(false, identityProblem);
                return new UndoResult(false, identityProblem, null);
            }

            // Check protected principal
            var protCheck = await _protectedPrincipalService.CheckAsync(lookupResult.Principal);
            if (protCheck.CheckFailed)
            {
                var checkFailed = $"Protected principal check failed: {protCheck.Reason}";
                LogUndoAudit(performedBy, ip, target, changedAttributes, auditEvent, originalOperationId, ticket, false, checkFailed, servicedNote: null, refusedAt: "ProtectedPrincipalCheck");
                scope.Complete(false, protCheck.Reason);
                return new UndoResult(false, checkFailed, null);
            }
            // The note is BOUND, not tested and dropped. This is a write path: allowing the undo
            // while discarding the note would leave a successful modification of a protected
            // principal with nothing in the audit record naming who permitted it (pps-3). The
            // helper returns a nullable note precisely so the permission and the record cannot be
            // separated, and `NoteFor(...) is null` as a bare condition defeats that by design.
            string? servicedNote = null;
            if (protCheck.IsProtected)
            {
                servicedNote = ProtectedPrincipalServicing.NoteFor(
                    _servicers, actingUser, ServicerModuleId, protCheck.MatchedRules);

                if (servicedNote is null)
                {
                    const string protectedTarget = "Target is a protected principal and cannot be modified.";
                    LogUndoAudit(performedBy, ip, target, changedAttributes, auditEvent, originalOperationId, ticket, false, protectedTarget, servicedNote: null, refusedAt: "ProtectedPrincipalCheck");
                    scope.Complete(false, "Target is a protected principal");
                    return new UndoResult(false, protectedTarget, null);
                }
            }

            // Verify no conflicts before executing
            foreach (var attr in changedAttributes)
            {
                var newValue = GetString(auditEvent, $"new_{attr}");
                lookupResult.CurrentValues.TryGetValue(attr, out var currentValue);

                var currentNorm = string.IsNullOrEmpty(currentValue) ? null : currentValue;
                var newNorm = string.IsNullOrEmpty(newValue) ? null : newValue;

                if (!string.Equals(currentNorm, newNorm, StringComparison.Ordinal))
                {
                    var error = $"Conflict on attribute '{attr}': expected '{newNorm ?? "(empty)"}' but found '{currentNorm ?? "(empty)"}'. " +
                        "The value has been modified since the original operation.";
                    _operationTrace.Step("ConflictDetected", "Failed",
                        details: new Dictionary<string, object?>
                        {
                            ["attribute"] = attr,
                            ["expected"] = newNorm,
                            ["actual"] = currentNorm
                        });
                    LogUndoAudit(performedBy, ip, target, changedAttributes, auditEvent, originalOperationId, ticket, false, error, servicedNote: null, refusedAt: "ConflictCheck");
                    scope.Complete(false, error);
                    return new UndoResult(false, error, null);
                }
            }

            // Build proposed values (revert to old values)
            var proposedValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var attr in changedAttributes)
            {
                var oldValue = GetString(auditEvent, $"old_{attr}");
                proposedValues[attr] = oldValue;
            }

            var maxLevel = 0;
            if (auditEvent.TryGetValue("_undoMaxLevel", out var lvlObj))
            {
                if (lvlObj is int lvlInt) maxLevel = lvlInt;
                else if (lvlObj is long lvlLong) maxLevel = (int)lvlLong;
                else int.TryParse(lvlObj?.ToString(), out maxLevel);
            }

            // aud-a26b: SaveAsync announces this reversal itself, for whatever result it returns,
            // and the reversal record is what makes it read as ADAttributeEditor_Undo rather than
            // another update. So neither branch below dispatches; only the catch does, for an
            // exception SaveAsync never returns.
            var saveResult = await _editorService.SaveAsync(
                lookupResult.Principal,
                proposedValues,
                performedBy,
                ip,
                ticket,
                actingUser,
                maxLevel,
                reversal: new AttributeEditReversal(target, changedAttributes, originalOperationId));

            if (!saveResult.Success)
            {
                // The SaveAsync already audits failure internally, but we also need the undo-specific audit
                LogUndoAudit(performedBy, ip, target, changedAttributes, auditEvent, originalOperationId, ticket, false, saveResult.Error, servicedNote);
                scope.Complete(false, saveResult.Error);
                return new UndoResult(false, saveResult.Error, null);
            }

            // Log the undo-specific audit event (the SaveAsync already logged a standard ADAttributeEditor_Update)
            LogUndoAudit(performedBy, ip, target, changedAttributes, auditEvent, originalOperationId, ticket, true, null, servicedNote);

            var reversalOperationId = _operationTrace.CurrentOperationId;
            scope.Complete(true);

            _logger.LogInformation(
                "Undo completed for ADAttributeEditor operation {OriginalOperationId} on {Target} by {User}",
                originalOperationId, target, performedBy);

            return new UndoResult(true, null, reversalOperationId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Undo failed for ADAttributeEditor operation {OriginalOperationId}", originalOperationId);

            // Review of A26: an exception out of SaveAsync - or anywhere else in this
            // method - left NO audit record and NO notification. The forward path audits
            // every outcome, and an attempted reversal that threw is exactly the event
            // someone later asks about: it may have written to the directory before it
            // failed. The operation trace saw it; the trace is diagnostic, with different
            // retention and different readers.
            LogUndoAudit(performedBy, ip, target, changedAttributes, auditEvent, originalOperationId, ticket, false, ex.Message, servicedNote: null);
            await NotifyAdministratorsAsync(
                performedBy, ip, ticket, target, changedAttributes, originalOperationId,
                success: false, error: ex.Message);

            scope?.Complete(false, ex.Message);
            return new UndoResult(false, $"Unexpected error during undo: {ex.Message}", null);
        }
        finally
        {
            scope?.Dispose();
        }
    }

    /// <summary>The undo's trace operation (aud-a28o).</summary>
    /// <remarks>
    /// Internal virtual as a TEST SEAM, the shape of <c>ADAttributeEditorService.CreateDirectoryRunspace</c>: a
    /// test makes it throw, which the trace service is not known to do - its writes go through the JSONL writer,
    /// which logs and swallows its own IO errors. Production begins exactly the operation it always did.
    /// </remarks>
    internal virtual OperationTraceService.OperationScope BeginUndoOperation(
        string performedBy, string ip, string target, string ticket, string? originalOperationId) =>
        _operationTrace.BeginOperation(
            module: "ADAttributeEditor",
            action: "ADAttributeEditor_Undo",
            actor: performedBy,
            ipAddress: ip,
            target: target,
            ticket: ticket,
            details: new Dictionary<string, object?> { ["originalOperationId"] = originalOperationId });

    /// <summary>
    /// Records an undo the event log refused before calling <see cref="ExecuteUndoAsync"/> (aud-a28l).
    /// </summary>
    /// <remarks>
    /// The record the refusals inside ExecuteUndoAsync write (aud-a28d): the undo action, failed, with
    /// the reason the operator was shown, the operation it would have reversed and the values it would
    /// have restored, <c>refusedAt</c> and <c>mutationAttempted</c> false. Failed, so it never marks the
    /// original edit reversed (aud-a26c) and the edit keeps its Undo button. Not announced: the page
    /// never announced this refusal. An audit failure is logged and swallowed; it must not turn the
    /// refusal into an exception the page would report instead.
    /// </remarks>
    public void LogUndoRefused(
        Dictionary<string, object?> auditEvent, string performedBy, string ip, string ticket, string refusedAt, string reason)
    {
        try
        {
            var target = GetString(auditEvent, "target");
            LogUndoAudit(
                performedBy, ip, string.IsNullOrWhiteSpace(target) ? "(none)" : target,
                GetStringArray(auditEvent, "changedAttributes"), auditEvent, GetString(auditEvent, "operationId"),
                ticket, false, reason, servicedNote: null, refusedAt: refusedAt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audit failed for an undo refused before it ran");
        }
    }

    /// <summary>
    /// Tells administrators that an attempt to REVERSE an attribute change threw.
    /// </summary>
    /// <remarks>
    /// Audit finding A26: a reversal was silent, because dispatch lived in the forward-edit page
    /// and undo never goes through it. Since aud-a26b SaveAsync announces every result it returns,
    /// told by the <see cref="AttributeEditReversal"/> it is given that this is an undo. What is
    /// left here is the one case SaveAsync cannot announce: an exception, out of SaveAsync or
    /// anywhere else in ExecuteUndoAsync.
    ///
    /// The action and rows come from <see cref="AttributeEditReversal"/>, the same builder
    /// SaveAsync uses, so the two notices cannot drift. The action is <c>ADAttributeEditor_Undo</c>,
    /// not the forward action name: a reader of the mailbox needs to tell a reversal from another
    /// edit.
    ///
    /// Failures are logged and swallowed, matching the forward path. The notification is not
    /// permitted to change the reported outcome - the audit record already exists.
    /// </remarks>
    internal async Task NotifyAdministratorsAsync(
        string performedBy,
        string ip,
        string ticket,
        string target,
        string[] changedAttributes,
        string? originalOperationId,
        bool success,
        string? error)
    {
        try
        {
            var reversal = new AttributeEditReversal(target, changedAttributes, originalOperationId);

            await _email.SendAdminNotificationAsync(
                performedBy, ip, AttributeEditReversal.NoticeAction, success, ticket,
                reversal.NoticeDetails(success, error), error);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Email notification failed for AD attribute undo of {Target}", target);
        }
    }

    private void LogUndoAudit(
        string performedBy,
        string ip,
        string target,
        string[] changedAttributes,
        Dictionary<string, object?> originalEvent,
        string? originalOperationId,
        string ticket,
        bool success,
        string? errorDetail,
        string? servicedNote,
        // Set only for a refusal before the save (aud-a28d): nothing reached the directory.
        string? refusedAt = null)
    {
        var extra = new Dictionary<string, object?>
        {
            ["originalOperationId"] = originalOperationId,
            ["changedAttributes"] = changedAttributes
        };

        // A serviced override SUCCEEDS, so it cannot ride errorDetail: that field is written as
        // null on success and the record of who permitted it would be silently discarded.
        if (!string.IsNullOrWhiteSpace(servicedNote))
            extra[ProtectedPrincipalServicing.AuditKey] = servicedNote;

        // aud-a28's two fields. Without them a refused reversal could not be told from one that
        // reached the directory and failed - materially different events.
        if (refusedAt is not null)
        {
            extra["refusedAt"] = refusedAt;
            extra["mutationAttempted"] = false;
        }

        foreach (var attr in changedAttributes)
        {
            extra[$"old_{attr}"] = GetString(originalEvent, $"new_{attr}");
            extra[$"new_{attr}"] = GetString(originalEvent, $"old_{attr}");
        }

        _audit.LogModuleAction(
            performedBy: performedBy,
            ipAddress: ip,
            action: "ADAttributeEditor_Undo",
            category: "ADAttributeEditor",
            target: target,
            success: success,
            ticketNumber: ticket,
            errorDetail: errorDetail,
            extra: extra);
    }
    /// <summary>
    /// Audit finding A9. The audit event names its target by UPN, and a UPN can be reassigned
    /// to a different person. Undo resolves that name back to an object and writes to it, so
    /// without an immutable id it can restore one person's old values onto another's account.
    /// </summary>
    /// <remarks>
    /// Returns the refusal reason, or null when the identity is proven.
    ///
    /// A record written before the GUID was captured is REFUSED, not waved through. That is a
    /// deliberate behaviour change: undo stops working for older audit rows. It is the right
    /// way round - an undo that cannot prove its target is the failure this finding describes,
    /// and the operator can still re-apply the values by hand, having read them in the record.
    /// </remarks>
    internal static string? IdentityMismatch(
        Dictionary<string, object?> auditEvent, ResolvedDirectoryPrincipal? principal, string target)
    {
        var recorded = GetString(auditEvent, "targetObjectGuid");

        if (string.IsNullOrWhiteSpace(recorded))
            return $"Cannot undo: this audit record predates object-identity capture, so there is "
                 + $"no way to prove '{target}' is still the same account that was changed. "
                 + "Re-apply the recorded values manually if the change should be reversed.";

        if (string.IsNullOrWhiteSpace(principal?.ObjectGuid))
            return $"Cannot undo: the directory did not return an object id for '{target}', so it "
                 + "cannot be matched against the account that was changed.";

        if (!string.Equals(recorded, principal.ObjectGuid, StringComparison.OrdinalIgnoreCase))
            return $"Cannot undo: '{target}' now resolves to a different directory object than the "
                 + "one that was changed. The name has been reassigned; undoing would modify the "
                 + "wrong account.";

        return null;
    }


    private static string? GetString(Dictionary<string, object?> evt, string key)
    {
        if (!evt.TryGetValue(key, out var value) || value == null)
            return null;

        if (value is string s)
            return s;

        if (value is JsonElement je)
        {
            // aud-a26o: a JSON null is not set. Its raw text, "null", was taken for the value - shown by the preview
            // and written by the undo. The writer drops a null side, so only a hand-made record carries one.
            return je.ValueKind switch
            {
                JsonValueKind.String => je.GetString(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => je.GetRawText(),
            };
        }

        return value.ToString();
    }

    private static string[] GetStringArray(Dictionary<string, object?> evt, string key)
    {
        if (!evt.TryGetValue(key, out var value) || value == null)
            return [];

        if (value is string[] arr)
            return arr;

        if (value is JsonElement je && je.ValueKind == JsonValueKind.Array)
        {
            return je.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .ToArray();
        }

        if (value is IEnumerable<object> enumerable)
            return enumerable.Select(o => o?.ToString() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToArray();

        return [];
    }

    private static UndoPreview ErrorPreview(string error, string action, string? operationId)
    {
        return new UndoPreview(
            CanUndo: false,
            Error: error,
            Module: "ADAttributeEditor",
            Action: action,
            Target: "",
            OriginalOperationId: operationId,
            Changes: null,
            HasConflict: false,
            ConflictDetail: null);
    }
}
