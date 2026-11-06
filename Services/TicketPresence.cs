using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// kd-24. The one rule for whether a ticket number was given at all, shared by the presence checks
/// in front of ticket-required writes and by <see cref="TicketValidationService"/>.
/// </summary>
/// <remarks>
/// <para>
/// Present means: at least one visible character, and nothing in it that is never part of a ticket -
/// no control character (kd-24), no Unicode format character such as a zero-width space or a bidi mark
/// (kd-48), and no unpaired surrogate. <see cref="string.IsNullOrWhiteSpace(string?)"/> answers false for
/// all of those, and <see cref="ServiceNowService.ValidateTicketAsync"/> passes every ticket while the
/// integration is dormant, so a ticket that looked blank went through every presence check and reached
/// the write with nothing a reader of the audit row could use. A format character anywhere is refused,
/// not just one standing alone: a bidi mark reorders the text around it, so "INC0042" and a ticket that
/// only displays as "INC0042" would be different records of the same click.
/// </para>
/// <para>
/// Visible means not white space, and not a character that draws nothing by itself: a combining mark,
/// an unassigned or private-use code point. White space inside a ticket stays allowed ("CHG 0042").
/// </para>
/// <para>
/// Deliberately not the ServiceNow shape (<see cref="ServiceNowService.TicketNumberPattern"/>): with
/// validation off, any ticket a person typed is plain audit metadata, punctuation and spaces included
/// (.agents/decisions.md 2026-08-31), and the pattern guards only what is put into a ServiceNow query.
/// Callers pass the ticket trimmed, as they record it.
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
    /// Sweep item 40. The words a ticket is refused with when it holds something that is never part of a ticket - a control
    /// or Unicode format character, or an unpaired surrogate - where it holds a visible character too or nothing but such
    /// characters. It used to be refused as <see cref="RequiredMessage"/>, so an operator who could see a ticket in the box
    /// (a pasted ticket carrying a zero-width space, byte-order mark, soft hyphen or bidi mark) was told none had been given.
    /// Nothing is stripped: the ticket the audit records is the one the operator typed, so the operator is asked to retype it.
    /// </summary>
    public const string InvisibleCharacterMessage =
        "Ticket number contains an invisible character (a zero-width space, byte-order mark, soft hyphen, bidi mark or control character, usually carried in by a paste). Retype the ticket number by hand";

    /// <summary>True when <paramref name="ticket"/> is a ticket at all; see the class remarks.</summary>
    public static bool IsPresent([NotNullWhen(true)] string? ticket) => Problem(ticket) is null;

    /// <summary>
    /// Sweep item 40. <see cref="IsPresent"/> for a ticket as the operator typed it, trimmed as every check in front of a
    /// write trims it before it judges and records it - what a submit button's enabled state is decided by, so the button
    /// and the check behind it agree: a button is enabled only for a ticket that check will take as present.
    /// </summary>
    public static bool IsPresentAsTyped(string? typed) => IsPresent(typed?.Trim());

    /// <summary>
    /// Sweep item 40. Whether a ticket as typed is refused for an invisible character (<see cref="InvisibleCharacterMessage"/>)
    /// - what a page shows beside its ticket box, so a button disabled for it says why.
    /// </summary>
    public static bool HasInvisibleCharacterAsTyped(string? typed) =>
        Problem(typed?.Trim()) is InvisibleCharacterMessage;

    /// <summary>
    /// Sweep item 40. Why <paramref name="ticket"/> is not a ticket: null when it is one, <see cref="RequiredMessage"/> when it
    /// holds nothing but white space - or nothing visible, with nothing in it that is never part of a ticket - and
    /// <see cref="InvisibleCharacterMessage"/> when it holds a control or format character or an unpaired surrogate.
    /// </summary>
    public static string? Problem(string? ticket)
    {
        if (string.IsNullOrWhiteSpace(ticket))
            return RequiredMessage;

        var visible = false;
        var rest = ticket.AsSpan();
        while (!rest.IsEmpty)
        {
            // An unpaired surrogate is not text at all.
            if (Rune.DecodeFromUtf16(rest, out var rune, out var consumed) != OperationStatus.Done)
                return InvisibleCharacterMessage;
            rest = rest[consumed..];

            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format)
                return InvisibleCharacterMessage;

            if (!Rune.IsWhiteSpace(rune) && !DrawsNothingAlone(category))
                visible = true;
        }

        return visible ? null : RequiredMessage;
    }

    /// <summary>
    /// Sweep item 40. <see cref="Problem"/> in a caller's own words for a missing ticket: null when present,
    /// <paramref name="whenMissing"/> when none was given, and <see cref="InvisibleCharacterMessage"/> with a full stop when
    /// it holds an invisible character - for the checks that refuse a missing ticket in words of their own.
    /// </summary>
    public static string? Describe(string? ticket, string whenMissing) => Problem(ticket) switch
    {
        null => null,
        InvisibleCharacterMessage => InvisibleCharacterMessage + ".",
        _ => whenMissing,
    };

    private static bool DrawsNothingAlone(UnicodeCategory category) => category is
        UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark or
        UnicodeCategory.OtherNotAssigned or UnicodeCategory.PrivateUse;

    /// <summary>
    /// The refusal for a missing ticket, as a ServiceNow result, or null when one was given - for the
    /// pages whose check is <c>Refusal(ticket) ?? await ServiceNow.ValidateTicketAsync(ticket)</c>. Sweep item 40: its
    /// message is <see cref="Problem"/>'s - an invisible character is said as that, not as a missing ticket.
    /// </summary>
    public static TicketValidationResult? Refusal(string? ticket) =>
        Problem(ticket) is { } problem ? new TicketValidationResult { IsValid = false, Message = problem } : null;
}
