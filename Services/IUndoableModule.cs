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

    /// <summary>
    /// Records an undo of <paramref name="auditEvent"/> that the caller refused before calling
    /// <see cref="ExecuteUndoAsync"/>, as this module's own refused-undo record (aud-a28l). Never throws.
    /// </summary>
    /// <remarks>
    /// The Admin Event Log re-checks the operator's authorization before it hands an undo to its module,
    /// and that refusal used to leave no record: the module never saw the attempt, and the page holds the
    /// module only through this interface. It is a member here so the record is written in the module's
    /// own shape, beside the refusals ExecuteUndoAsync records, and so a module added later cannot leave
    /// the page's refusals unrecorded by omission. It must not throw: a failed audit write is logged, and
    /// the operator is still shown the refusal rather than whatever the page's catch would say.
    /// </remarks>
    void LogUndoRefused(Dictionary<string, object?> auditEvent, string performedBy, string ip, string ticket, string refusedAt, string reason);
}
