using System.Collections;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// sweep-c item 13. Makes Exchange's refusal of a mailbox-GUID identity legible: every Exchange write and pre-write read
/// names the mailbox by <see cref="ValidatedMailbox.Identity"/>, its ExchangeGuid (aud-a2d), an assumption no live Exchange
/// has confirmed. If Exchange does not take that form, every change is refused with "couldn't be found", naming a GUID the
/// operator never typed. This recognises that answer and says what it is.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Describe"/> is asked by <see cref="ExchangeServiceBase"/>'s <c>Invoke</c> for every command that fails, before
/// the pipeline is cleared. It answers only when the error is Exchange's not-found wording AND names a GUID the command passed
/// as the mailbox: its <c>-Identity</c> or <c>-Member</c> (a calendar folder's <c>"&lt;guid&gt;:\Calendar"</c> included), or
/// the first argument of an on-premises <c>Invoke-Command</c> - never a trustee. Then the operator message says the mailbox
/// GUID identity was refused, for which cmdlet, in Exchange's words, and the log carries the cmdlet and the GUID at Warning,
/// so the dev-deploy check recognises item 13 at once. Nothing is retried and no other identity is tried: aud-a2d stands.
/// </para>
/// <para>
/// <see cref="Logger"/> is set once at startup (<c>Program.cs</c>); the helpers that use it are static, as <c>Invoke</c> is.
/// </para>
/// </remarks>
internal static class MailboxGuidIdentityRefusal
{
    /// <summary>Where the Warning goes; set at startup, a no-op until then.</summary>
    internal static ILogger Logger { get; set; } = NullLogger.Instance;

    /// <summary>How every message this writes begins, so a page, an audit row or a test can tell it apart.</summary>
    internal const string Marker = "Exchange refused the mailbox GUID identity";

    private static readonly Regex GuidPattern = new(
        @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.CultureInvariant);

    private static readonly Regex NotFoundWording = new(
        @"(couldn't|could not|cannot|can't|wasn't|was not|isn't|is not) be found|not found|NotFoundException",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex CmdletName = new(@"\b[A-Z][a-z]+-[A-Za-z]+\b", RegexOptions.CultureInvariant);

    /// <summary>
    /// The operator message for a failure of the command queued on <paramref name="ps"/> when Exchange answered that the
    /// mailbox GUID it was given was not found; null for any other failure. Logs the Warning when it answers.
    /// </summary>
    internal static string? Describe(PowerShell ps, string primary, IReadOnlyList<string> errors)
    {
        if (string.IsNullOrEmpty(primary) || primary.Contains(Marker, StringComparison.Ordinal))
            return null;

        var said = errors.Count > 0 ? string.Join(" | ", errors.Prepend(primary)) : primary;
        if (!NotFoundWording.IsMatch(said))
            return null;

        foreach (var command in ps.Commands.Commands)
        {
            foreach (var value in MailboxValues(command))
            {
                foreach (Match guid in GuidPattern.Matches(value))
                {
                    if (!said.Contains(guid.Value, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var cmdlet = CmdletOf(command);
                    Logger.LogWarning(
                        "Exchange refused the mailbox GUID identity {MailboxGuid} for {Cmdlet} as not found (aud-a2d's identity form, sweep item 13): {Error}",
                        guid.Value, cmdlet, primary);
                    return Message(cmdlet, guid.Value, primary);
                }
            }
        }

        return null;
    }

    /// <summary>What the operator is told, and the audit row records.</summary>
    internal static string Message(string cmdlet, string guid, string exchangeSaid) =>
        $"{Marker} {guid} for {cmdlet}: Exchange answered \"{exchangeSaid.TrimEnd('.')}\". Every change names the mailbox "
        + "by its Exchange mailbox GUID, and this Exchange did not accept that form, so this step changed nothing. "
        + "Tell your administrator: it is the mailbox-GUID identity check of the dev deployment (sweep item 13).";

    /// <summary>
    /// The Get-Place answer of a room read came back empty for its GUID (it runs under SilentlyContinue, so Exchange's own
    /// words never arrive): the room's place fields - and an audit row's old values - read blank. Logged, so a deploy check
    /// that sees every room blank knows why.
    /// </summary>
    internal static void NoPlaceFor(string guid) =>
        Logger.LogWarning(
            "Get-Place returned no place for the mailbox GUID identity {MailboxGuid}: the room's city, building and capacity read blank. If every room reads so, Exchange is not accepting the GUID form for Get-Place (sweep item 13)",
            guid);

    // The values a command passes as the mailbox: -Identity, -Member, or an on-premises Invoke-Command's first argument.
    private static IEnumerable<string> MailboxValues(Command command)
    {
        foreach (var parameter in command.Parameters)
        {
            if (string.Equals(parameter.Name, "Identity", StringComparison.OrdinalIgnoreCase)
                || string.Equals(parameter.Name, "Member", StringComparison.OrdinalIgnoreCase))
            {
                if (Text(parameter.Value) is { } text)
                    yield return text;
            }
            else if (string.Equals(parameter.Name, "ArgumentList", StringComparison.OrdinalIgnoreCase)
                     && parameter.Value is IEnumerable arguments and not string)
            {
                foreach (var first in arguments)
                {
                    if (Text(first) is { } text)
                        yield return text;
                    break;
                }
            }
        }
    }

    private static string? Text(object? value) => (value is PSObject wrapped ? wrapped.BaseObject : value) as string;

    // The cmdlet's name; for Invoke-Command, the cmdlets its script block runs.
    private static string CmdletOf(Command command)
    {
        var name = command.CommandText;
        if (!string.Equals(name, "Invoke-Command", StringComparison.OrdinalIgnoreCase))
            return name;

        var script = command.Parameters.FirstOrDefault(p => string.Equals(p.Name, "ScriptBlock", StringComparison.OrdinalIgnoreCase))?.Value?.ToString();
        var inner = script is null
            ? []
            : CmdletName.Matches(script).Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return inner.Count == 0 ? name : $"{name} ({string.Join(", ", inner)})";
    }
}
