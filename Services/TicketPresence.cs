using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// kd-24. The one rule for whether a ticket number was given at all, shared by the presence checks
/// in front of ticket-required writes and by <see cref="TicketValidationService"/>, and the one form
/// a ticket is recorded in (<see cref="Sanitize"/>).
/// </summary>
/// <remarks>
/// <para>
/// A ticket is recorded as typed with every character that is never part of a ticket removed - control
/// characters (kd-24), Unicode format characters such as a zero-width space or a bidi mark (kd-48), and
/// unpaired surrogates - then trimmed (.agents/decisions.md 2026-11-09, "A ticket is sanitized, not refused,
/// for invisible characters"). A pasted "INC0042" carrying a zero-width space is recorded as "INC0042"; it is
/// not refused. Removing every format character, not just one standing alone, also keeps a bidi mark from
/// making a ticket that only displays as "INC0042" a different record of the same click.
/// </para>
/// <para>
/// Present means: after sanitizing, at least one visible character remains. Visible means not white space,
/// and not a character that draws nothing by itself: a combining mark, an unassigned or private-use code
/// point. White space inside a ticket stays ("CHG 0042"). <see cref="ServiceNowService.ValidateTicketAsync"/>
/// passes every ticket while the integration is dormant, so this is the only refusal a ticket meets today.
/// </para>
/// <para>
/// Deliberately not the ServiceNow shape (<see cref="ServiceNowService.TicketNumberPattern"/>): with
/// validation off, any ticket a person typed is plain audit metadata, punctuation and spaces included
/// (.agents/decisions.md 2026-08-31), and the pattern guards only what is put into a ServiceNow query.
/// Callers judge and record the same <see cref="Sanitize"/>d string, so the check and the record agree.
/// </para>
/// </remarks>
public static class TicketPresence
{
    /// <summary>
    /// The words a missing ticket is refused with where the refusal is shaped as ServiceNow's: the
    /// ones <see cref="ServiceNowService"/> gives a blank ticket when it is enabled, so the operator
    /// reads the same refusal whether or not it is.
    /// </summary>
    public const string RequiredMessage = "Ticket number is required";

    /// <summary>
    /// The ticket as it is judged and recorded: <paramref name="typed"/> with every control character, Unicode
    /// format character and unpaired surrogate removed, then trimmed. Everything else is kept - white space inside
    /// the ticket, punctuation, and a combining mark on its base. "" for null.
    /// </summary>
    public static string Sanitize(string? typed)
    {
        if (string.IsNullOrEmpty(typed))
            return "";

        StringBuilder? kept = null;
        var rest = typed.AsSpan();
        var position = 0;
        while (!rest.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(rest, out var rune, out var consumed);
            var drop = status != OperationStatus.Done // an unpaired surrogate is not text at all
                || Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format;
            if (drop)
                kept ??= new StringBuilder(typed, 0, position, typed.Length);
            else
                kept?.Append(rest[..consumed]);
            rest = rest[consumed..];
            position += consumed;
        }

        return (kept?.ToString() ?? typed).Trim();
    }

    /// <summary>True when <paramref name="ticket"/>, sanitized, is a ticket at all; see the class remarks.</summary>
    public static bool IsPresent([NotNullWhen(true)] string? ticket) => Problem(ticket) is null;

    /// <summary>
    /// Sweep item 40. <see cref="IsPresent"/> for a ticket as the operator typed it, sanitized as every check in front of a
    /// write sanitizes it before it judges and records it - what a submit button's enabled state is decided by, so the button
    /// and the check behind it agree: a button is enabled only for a ticket that check will take as present.
    /// </summary>
    public static bool IsPresentAsTyped(string? typed) => IsPresent(Sanitize(typed));

    /// <summary>
    /// Why <paramref name="ticket"/> is not a ticket: null when, sanitized, it is one, and <see cref="RequiredMessage"/>
    /// when nothing visible remains.
    /// </summary>
    public static string? Problem(string? ticket)
    {
        var rest = Sanitize(ticket).AsSpan();
        while (!rest.IsEmpty)
        {
            Rune.DecodeFromUtf16(rest, out var rune, out var consumed); // sanitized: every rune decodes
            rest = rest[consumed..];

            if (!Rune.IsWhiteSpace(rune) && !DrawsNothingAlone(Rune.GetUnicodeCategory(rune)))
                return null;
        }

        return RequiredMessage;
    }

    /// <summary>
    /// <see cref="Problem"/> in a caller's own words: null when present, <paramref name="whenMissing"/> when no visible
    /// ticket was given - for the checks that refuse a missing ticket in words of their own.
    /// </summary>
    public static string? Describe(string? ticket, string whenMissing) => Problem(ticket) is null ? null : whenMissing;

    private static bool DrawsNothingAlone(UnicodeCategory category) => category is
        UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark or
        UnicodeCategory.OtherNotAssigned or UnicodeCategory.PrivateUse;

    /// <summary>
    /// The refusal for a missing ticket, as a ServiceNow result, or null when one was given - for the
    /// pages whose check is <c>Refusal(ticket) ?? await ServiceNow.ValidateTicketAsync(ticket)</c>, with
    /// <c>ticket</c> already <see cref="Sanitize"/>d.
    /// </summary>
    public static TicketValidationResult? Refusal(string? ticket) =>
        Problem(ticket) is { } problem ? new TicketValidationResult { IsValid = false, Message = problem } : null;
}
