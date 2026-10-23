namespace ExchangeAdminWeb.Services;

/// <summary>
/// aud-a53. What the Mailbox Permissions and Calendar Permissions single-change handlers say when a step of the change throws:
/// the step, whether anything could have been written, and the cause in the failing command's own words.
/// </summary>
/// <remarks>
/// The handlers' catch said "Operation may have succeeded but audit logging failed: {ex.Message}" whatever had thrown - a
/// step before the write as well as one after it - with the raw exception text. Since aud-a28q the audit writers do not
/// throw, and since aud-a53 the record and each notification sit in their own guard, so a throw that reaches the catch is
/// one of the change's own steps. The handler names the step it is at in a local (<see cref="BeforeTheWrite"/>, then
/// <see cref="TheWrite"/>, then <see cref="AfterTheWrite"/>); the cause is <see cref="ExchangeServiceBase.ResolveFailure"/>'s
/// (aud-a23i) - a cmdlet's error as it wrote it, never the "running command stopped" wrapper.
/// </remarks>
internal static class PermissionChangeFailure
{
    /// <summary>Before the write is made: nothing can have been written.</summary>
    internal const string BeforeTheWrite = "a step before the write";

    /// <summary>The write itself: it may or may not have taken.</summary>
    internal const string TheWrite = "the write";

    /// <summary>After the write returned: it was made as it reported.</summary>
    internal const string AfterTheWrite = "a step after the write";

    /// <summary>The banner, and the record's detail, for a throw at <paramref name="step"/>.</summary>
    internal static string Banner(string step, Exception ex)
    {
        var cause = ExchangeServiceBase.ResolveFailure(ex).primary.Trim().TrimEnd('.');
        return step switch
        {
            BeforeTheWrite => $"The change was not made: {step} failed: {cause}. Nothing was written.",
            TheWrite => $"Whether the change was made could not be confirmed: {step} failed: {cause}. Check the permissions before trying again.",
            _ => $"The change was written, but {step} failed: {cause}. Check the permissions to confirm the result.",
        };
    }

    /// <summary>
    /// aud-a73. The result the record of a throw at <paramref name="step"/> is filed under: Undetermined at
    /// <see cref="TheWrite"/>, which may or may not have taken, as its banner says; Failed before the write, when nothing
    /// was written, and after it, when the written change has its own row and this one says a later step failed.
    /// </summary>
    internal static AuditResult AuditResultOf(string step) => step == TheWrite ? AuditResult.Undetermined : AuditResult.Failed;
}
