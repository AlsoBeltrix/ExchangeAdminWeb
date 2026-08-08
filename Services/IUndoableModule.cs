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
    /// <param name="actingUser">
    /// The operator viewing the preview. REQUIRED, and it exists for the protected-principal
    /// servicer decision: a preview that refuses what execute would allow makes an authorised
    /// servicer's override unreachable, because they never get an Undo button to press (pps-2).
    /// Preview and execute must reach the SAME decision, so both take the principal. Null refuses,
    /// exactly as it does at execute.
    /// </param>
    Task<UndoPreview> PreviewUndoAsync(Dictionary<string, object?> auditEvent, System.Security.Claims.ClaimsPrincipal? actingUser);
    Task<UndoResult> ExecuteUndoAsync(Dictionary<string, object?> auditEvent, string performedBy, string ip, string ticket, System.Security.Claims.ClaimsPrincipal? actingUser);
}
