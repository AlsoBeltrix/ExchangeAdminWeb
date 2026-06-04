using System.Text.Json;

namespace ExchangeAdminWeb.Services;

public class ADAttributeEditorUndoService : IUndoableModule
{
    private readonly ADAttributeEditorService _editorService;
    private readonly ProtectedPrincipalService _protectedPrincipalService;
    private readonly ModuleEnablementService _moduleEnablement;
    private readonly AuditService _audit;
    private readonly OperationTraceService _operationTrace;
    private readonly ILogger<ADAttributeEditorUndoService> _logger;

    public ADAttributeEditorUndoService(
        ADAttributeEditorService editorService,
        ProtectedPrincipalService protectedPrincipalService,
        ModuleEnablementService moduleEnablement,
        AuditService audit,
        OperationTraceService operationTrace,
        ILogger<ADAttributeEditorUndoService> logger)
    {
        _editorService = editorService;
        _protectedPrincipalService = protectedPrincipalService;
        _moduleEnablement = moduleEnablement;
        _audit = audit;
        _operationTrace = operationTrace;
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

        // Must have at least one old_/new_ prefixed attribute pair
        return auditEvent.Keys.Any(k => k.StartsWith("old_", StringComparison.OrdinalIgnoreCase))
            && auditEvent.Keys.Any(k => k.StartsWith("new_", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<UndoPreview> PreviewUndoAsync(Dictionary<string, object?> auditEvent)
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

        // Check protected principal
        if (lookupResult.Principal != null)
        {
            var protCheck = await _protectedPrincipalService.CheckAsync(lookupResult.Principal);
            if (protCheck.CheckFailed)
                return ErrorPreview($"Protected principal check failed: {protCheck.Reason}", action, operationId);
            if (protCheck.IsProtected)
                return ErrorPreview("Target is a protected principal and cannot be modified.", action, operationId);
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
        string ticket)
    {
        var target = GetString(auditEvent, "target");
        var originalOperationId = GetString(auditEvent, "operationId");

        if (string.IsNullOrWhiteSpace(target))
            return new UndoResult(false, "No target identity in audit event.", null);

        if (!_moduleEnablement.IsModuleEnabled("ADAttributeEditor"))
            return new UndoResult(false, "ADAttributeEditor module is not enabled.", null);

        var changedAttributes = GetStringArray(auditEvent, "changedAttributes");
        if (changedAttributes.Length == 0)
            return new UndoResult(false, "No changed attributes recorded in audit event.", null);

        using var scope = _operationTrace.BeginOperation(
            module: "ADAttributeEditor",
            action: "ADAttributeEditor_Undo",
            actor: performedBy,
            ipAddress: ip,
            target: target,
            ticket: ticket,
            details: new Dictionary<string, object?> { ["originalOperationId"] = originalOperationId });

        try
        {
            // Re-read current state
            var lookupResult = await _editorService.LookupAsync(target);
            if (!lookupResult.Success || lookupResult.CurrentValues == null || lookupResult.Principal == null)
            {
                var error = $"Cannot read current state of '{target}': {lookupResult.Error ?? "lookup failed"}";
                scope.Complete(false, error);
                return new UndoResult(false, error, null);
            }

            // Check protected principal
            var protCheck = await _protectedPrincipalService.CheckAsync(lookupResult.Principal);
            if (protCheck.CheckFailed)
            {
                scope.Complete(false, protCheck.Reason);
                return new UndoResult(false, $"Protected principal check failed: {protCheck.Reason}", null);
            }
            if (protCheck.IsProtected)
            {
                scope.Complete(false, "Target is a protected principal");
                return new UndoResult(false, "Target is a protected principal and cannot be modified.", null);
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

            var saveResult = await _editorService.SaveAsync(
                lookupResult.Principal,
                proposedValues,
                performedBy,
                ip,
                ticket,
                maxLevel);

            if (!saveResult.Success)
            {
                // The SaveAsync already audits failure internally, but we also need the undo-specific audit
                LogUndoAudit(performedBy, ip, target, changedAttributes, auditEvent, originalOperationId, ticket, false, saveResult.Error);
                scope.Complete(false, saveResult.Error);
                return new UndoResult(false, saveResult.Error, null);
            }

            // Log the undo-specific audit event (the SaveAsync already logged a standard ADAttributeEditor_Update)
            LogUndoAudit(performedBy, ip, target, changedAttributes, auditEvent, originalOperationId, ticket, true, null);

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
            scope.Complete(false, ex.Message);
            return new UndoResult(false, $"Unexpected error during undo: {ex.Message}", null);
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
        string? errorDetail)
    {
        var extra = new Dictionary<string, object?>
        {
            ["originalOperationId"] = originalOperationId,
            ["changedAttributes"] = changedAttributes
        };

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

    private static string? GetString(Dictionary<string, object?> evt, string key)
    {
        if (!evt.TryGetValue(key, out var value) || value == null)
            return null;

        if (value is string s)
            return s;

        if (value is JsonElement je)
        {
            return je.ValueKind == JsonValueKind.String ? je.GetString() : je.GetRawText();
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
