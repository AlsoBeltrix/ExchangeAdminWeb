using System.Text.RegularExpressions;

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
/// (aud-a23i) - a cmdlet's error as it wrote it, never the "running command stopped" wrapper. aud-a76: a throw at the write
/// that the service marks as never sent - a <see cref="PermissionWriteNotSubmittedException"/> - is
/// <see cref="TheConnection"/> instead (<see cref="StageOf"/>): nothing was written.
/// </remarks>
internal static class PermissionChangeFailure
{
    /// <summary>Before the write is made: nothing can have been written.</summary>
    internal const string BeforeTheWrite = "a step before the write";

    /// <summary>The write itself: it may or may not have taken.</summary>
    internal const string TheWrite = "the write";

    /// <summary>
    /// aud-a76. The connection the write needed, refused or failed before the write's command was sent: nothing can have been
    /// written. The services say so with a <see cref="PermissionWriteNotSubmittedException"/>.
    /// </summary>
    internal const string TheConnection = "the connection to Exchange";

    /// <summary>After the write returned: it was made as it reported.</summary>
    internal const string AfterTheWrite = "a step after the write";

    /// <summary>The banner, and the record's detail, for a throw at <paramref name="step"/>.</summary>
    internal static string Banner(string step, Exception ex)
    {
        // aud-a76: a write never sent is told in its cause's words - the exception the service wrapped, not the wrapper.
        var cause = OneLine(ExchangeServiceBase.ResolveFailure(ex is PermissionWriteNotSubmittedException { InnerException: { } wrapped } ? wrapped : ex).primary).TrimEnd('.');
        return step switch
        {
            BeforeTheWrite => $"The change was not made: {step} failed: {cause}. Nothing was written.",
            TheConnection => $"Nothing was written: {step} failed: {cause}.",
            TheWrite => $"Whether the change was made could not be confirmed: {step} failed: {cause}. Check the permissions before trying again.",
            _ => $"The change was written, but {step} failed: {cause}. Check the permissions to confirm the result.",
        };
    }

    /// <summary>
    /// aud-a76. The stage a throw at <paramref name="step"/> came from, as the exception tells it: at <see cref="TheWrite"/>, a
    /// <see cref="PermissionWriteNotSubmittedException"/> - the service's word that the write's command was never sent - is
    /// <see cref="TheConnection"/>, where nothing can have been written; any other throw, and a throw at any other step, is
    /// the step's.
    /// </summary>
    internal static string StageOf(string step, Exception ex) =>
        step == TheWrite && ex is PermissionWriteNotSubmittedException ? TheConnection : step;

    /// <summary>
    /// aud-a73. The result the record of a throw at <paramref name="step"/> is filed under: Undetermined at
    /// <see cref="TheWrite"/>, which may or may not have taken, as its banner says; Failed before the write and at
    /// <see cref="TheConnection"/> (aud-a76), when nothing was written, and after it, when the written change has its own row
    /// and this one says a later step failed.
    /// </summary>
    internal static AuditResult AuditResultOf(string step) => step == TheWrite ? AuditResult.Undetermined : AuditResult.Failed;

    // aud-a70 (17). The banner is one line, and so is the record's error it becomes: a cause's line breaks and runs of white
    // space read as one space. The pool's "stopped" refusal is an ObjectDisposedException, whose message puts the object's
    // name on a line of its own.
    private static string OneLine(string text) => WhiteSpace.Replace(text, " ").Trim();

    private static readonly Regex WhiteSpace = new(@"\s+", RegexOptions.CultureInvariant);
}
