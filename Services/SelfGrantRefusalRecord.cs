namespace ExchangeAdminWeb.Services;

/// <summary>
/// The record and the banner of a refused self-grant on the Mailbox Permissions and Calendar Permissions pages (aud-a44,
/// aud-a50): the refusal the validator gave, and "The audit row could not be written." when its record was not.
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

    /// <summary>
    /// Writes the refusal's record through <paramref name="write"/> and returns the banner: the refusal, and
    /// <see cref="AuditRowNotWritten"/> after it when the writer dropped the row or the write threw - a throw logged against
    /// <paramref name="target"/>, as <c>SubmitSingle</c>'s catch logs it.
    /// </summary>
    internal static string Banner(string refusal, Func<JsonlWriteOutcome> write, ILogger logger, string target)
    {
        try
        {
            // A dropped row is the writer's to log (aud-a28u); the banner only says so.
            return write() == JsonlWriteOutcome.Written ? refusal : refusal + " " + AuditRowNotWritten;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write audit for the self-grant refusal on {Target}", target);
            return refusal + " " + AuditRowNotWritten;
        }
    }
}
