using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// The record and the banner of a refused self-grant on the Mailbox Permissions and Calendar Permissions pages (aud-a44,
/// aud-a50): the refusal the validator gave, and "The audit row could not be written." when its record was not. Since
/// aud-a59 also the protected-target denial's and the failed on-premises change's (<see cref="DenialRecord"/>,
/// <see cref="FailedChangeRecord"/>), and since aud-a71 the failed single change's (<see cref="FailedSingleChangeRecord"/>).
/// </summary>
/// <remarks>
/// <c>ExecuteOnPrem</c> writes its refusal through this, on one line: the pages are line-keyed by ClickGateRegistry, and the
/// one free line could not hold <c>SubmitSingle</c>'s try/catch, which says the same in place. The writer does not throw
/// (aud-a28q) and hands back what became of the row (aud-a28u); a write that throws all the same is logged here, and its
/// exception never reaches the banner.
/// </remarks>
internal static class SelfGrantRefusalRecord
{
    /// <summary>The sentence a banner gains when the refusal's audit row was not written.</summary>
    internal const string AuditRowNotWritten = "The audit row could not be written.";

    /// <summary>
    /// aud-a61. What the on-premises write's re-check adds to its refusal: the same check passed before the operator
    /// confirmed, so what it reads changed - or could no longer be read - in the pause.
    /// </summary>
    internal const string RefusedAtWrite =
        "The same check had passed before you confirmed; made again right before the write, it refused.";

    /// <summary>The write-time re-check's refusal (aud-a61): the validator's own, then <see cref="RefusedAtWrite"/>.</summary>
    internal static string AtWrite(string refusal) => refusal + " " + RefusedAtWrite;

    /// <summary>The record <see cref="Banner"/> names by default when its write throws: a refused self-grant.</summary>
    internal const string SelfGrantRecord = "the self-grant refusal";

    /// <summary>aud-a59. A protected-target denial's record - the words the pages' own denial catch logs.</summary>
    internal const string DenialRecord = "protected-target denial";

    /// <summary>aud-a59. The record of an on-premises change that failed.</summary>
    internal const string FailedChangeRecord = "the failed on-premises change";

    /// <summary>
    /// aud-a71. The record of a single change that failed (<c>SubmitSingle</c>): its write, or a step before or after it. Not
    /// <see cref="FailedChangeRecord"/>, whose words would misname a change Exchange Online was to write.
    /// </summary>
    internal const string FailedSingleChangeRecord = "the failed permission change";

    /// <summary>
    /// aud-a70 (5). The record of a change refused before its write: <c>SubmitSingle</c>'s refusals once the target is validated
    /// (the location unreadable or nowhere, on-premises not permitted) and <c>ExecuteOnPrem</c>'s authorization refusal.
    /// </summary>
    internal const string RefusedChangeRecord = "the refused permission change";

    /// <summary>aud-a70 (5). <c>ExecuteOnPrem</c>'s refusal when the operator may not change on-premises permissions.</summary>
    internal const string OnPremNotAuthorized = "Authorization denied for on-prem operations. Nothing was written.";

    /// <summary>aud-a70 (1). A single change's own record (<c>SubmitSingle</c>), in the words its guard has always logged.</summary>
    internal const string SingleChangeRecord = "the permission change";

    /// <summary>aud-a70 (1). An on-premises change's own record (<c>ExecuteOnPrem</c>): one row per right on Mailbox Permissions.</summary>
    internal const string OnPremChangeRecord = "the on-premises permission change";

    /// <summary>
    /// aud-a70 (1). Writes a change's own record through <paramref name="write"/>, in a guard of its own, and says whether its
    /// row was written. A throw is logged against <paramref name="target"/> as a failed write of <paramref name="record"/> and
    /// never reaches the caller, so the change's result stands - the Constitution's "audit failure must not make an
    /// already-completed operation appear to have failed" (aud-a53). A row the writer dropped is the writer's to log (aud-a28u).
    /// </summary>
    internal static bool Recorded(Func<JsonlWriteOutcome> write, ILogger logger, string target, string record)
    {
        try
        {
            return write() == JsonlWriteOutcome.Written;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write audit for {Record} on {Target}; the change stands", record, target);
            return false;
        }
    }

    /// <summary>
    /// aud-a70 (1). <paramref name="result"/> as its banner shows it: as it is when its record was <paramref name="written"/>,
    /// else saying the audit row could not be written (<see cref="AuditRowNotWritten"/>) - its outcome unchanged either way.
    /// </summary>
    internal static PermissionResult Said(PermissionResult result, bool written) =>
        written ? result : result.WithMessage(result.Message + " " + AuditRowNotWritten);

    /// <summary>
    /// Writes the refusal's record through <paramref name="write"/> and returns the banner: the refusal, and
    /// <see cref="AuditRowNotWritten"/> after it when the writer dropped the row or the write threw - a throw logged against
    /// <paramref name="target"/> as a failed write of <paramref name="record"/>, as <c>SubmitSingle</c>'s catch logs it.
    /// </summary>
    internal static string Banner(string refusal, Func<JsonlWriteOutcome> write, ILogger logger, string target,
        string record = SelfGrantRecord)
    {
        try
        {
            // A dropped row is the writer's to log (aud-a28u); the banner only says so.
            return write() == JsonlWriteOutcome.Written ? refusal : refusal + " " + AuditRowNotWritten;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write audit for {Record} on {Target}", record, target);
            return refusal + " " + AuditRowNotWritten;
        }
    }
}
