namespace ExchangeAdminWeb.Services;

public sealed record UndoPreview(
    bool CanUndo,
    string? Error,
    string Module,
    string Action,
    string Target,
    string? OriginalOperationId,
    List<UndoPreviewChange>? Changes,
    bool HasConflict,
    string? ConflictDetail);

public sealed record UndoPreviewChange(
    string Field,
    string? CurrentValue,
    string? ExpectedValue,
    string? RevertToValue,
    bool IsConflict);

public sealed record UndoResult(bool Success, string? Error, string? ReversalOperationId);

public interface IUndoableModule
{
    string ModuleId { get; }
    bool CanUndo(Dictionary<string, object?> auditEvent);
    Task<UndoPreview> PreviewUndoAsync(Dictionary<string, object?> auditEvent);
    Task<UndoResult> ExecuteUndoAsync(Dictionary<string, object?> auditEvent, string performedBy, string ip, string ticket);
}
