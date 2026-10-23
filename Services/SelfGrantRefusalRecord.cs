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
