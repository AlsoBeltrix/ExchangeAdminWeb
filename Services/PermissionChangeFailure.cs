using System.Text.RegularExpressions;
using ExchangeAdminWeb.Models;

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
    /// kd-59. What an on-premises change says, banner and record, when the operator's cancel stopped it before its command was
    /// sent: the app's phrase for a cancel (kd-57), and that nothing was written. kd-81: a single Exchange Online change's too.
    /// </summary>
    internal const string CancelledBeforeTheWrite = AuditService.CancelledByTheOperator + " before the change was sent. Nothing was written.";

    /// <summary>kd-59. The record <see cref="CancelledBeforeTheWrite"/> is written as, for the log line if that write throws.</summary>
    internal const string CancelledChangeRecord = "the cancelled on-premises change";

    /// <summary>
    /// kd-81. The record of a single change (<c>SubmitSingle</c>) the operator's cancel stopped before anything was sent. Not
    /// <see cref="CancelledChangeRecord"/>, whose words would misname a change Exchange Online was to write.
    /// </summary>
    internal const string CancelledSingleChangeRecord = "the cancelled permission change";

    /// <summary>
    /// kd-59. Whether <paramref name="ex"/> is the operator's cancel of an on-premises change: a write the service says was
    /// never sent (aud-a76), stopped by a cancellation - the credential fetch is the step that takes the activity's token -
    /// with that token, <paramref name="activity"/>, cancelled. Any other throw, a cancellation the operator did not ask for
    /// included, is a failure in its own words. kd-81: a single Exchange Online change's own looks at the token - as it
    /// starts, and inside its pooled call before its first write command - leave the same way.
    /// </summary>
    internal static bool CancelledBeforeItWasSent(Exception ex, CancellationToken activity) =>
        ex is PermissionWriteNotSubmittedException { InnerException: OperationCanceledException } && activity.IsCancellationRequested;

    /// <summary>
    /// kd-81. <see cref="CancelledBeforeItWasSent(Exception, CancellationToken)"/> for a single change thrown at
    /// <paramref name="step"/>: also a cancellation at <see cref="BeforeTheWrite"/> - the location read's, whose on-premises
    /// half takes the activity's token (kd-37's seam) - with that token cancelled. Nothing can have been written at that step,
    /// so it is the operator's cancel before anything was sent. Any other throw is a failure in its own words.
    /// </summary>
    internal static bool CancelledBeforeItWasSent(Exception ex, string step, CancellationToken activity) =>
        CancelledBeforeItWasSent(ex, activity) || (step == BeforeTheWrite && ex is OperationCanceledException && activity.IsCancellationRequested);

    /// <summary>
    /// aud-a73. The result the record of a throw at <paramref name="step"/> is filed under: Undetermined at
    /// <see cref="TheWrite"/>, which may or may not have taken, as its banner says; Failed before the write and at
    /// <see cref="TheConnection"/> (aud-a76), when nothing was written, and after it, when the written change has its own row
    /// and this one says a later step failed.
    /// </summary>
    internal static AuditResult AuditResultOf(string step) => step == TheWrite ? AuditResult.Undetermined : AuditResult.Failed;

    /// <summary>
    /// aud-a70 (14). What a change's own record is filed under when its result says more than its success: Undetermined when a
    /// right's outcome is unknown (<see cref="PermissionResult.UnconfirmedRights"/>) - <paramref name="right"/>'s when one is
    /// named, any right's otherwise. Else null: Success or Failed from the success the record is given, as always.
    /// </summary>
    internal static AuditResult? FiledAs(PermissionResult result, string? right = null) =>
        (right is null ? result.Unconfirmed : result.UnconfirmedRights?.Contains(right) == true) ? AuditResult.Undetermined : null;

    // aud-a70 (17). The banner is one line, and so is the record's error it becomes: a cause's line breaks and runs of white
    // space read as one space. The pool's "stopped" refusal is an ObjectDisposedException, whose message puts the object's
    // name on a line of its own.
    private static string OneLine(string text) => WhiteSpace.Replace(text, " ").Trim();

    private static readonly Regex WhiteSpace = new(@"\s+", RegexOptions.CultureInvariant);
}
