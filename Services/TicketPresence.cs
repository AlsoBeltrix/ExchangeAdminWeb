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

    /// <summary>True when <paramref name="ticket"/> is a ticket at all; see the class remarks.</summary>
    public static bool IsPresent([NotNullWhen(true)] string? ticket)
    {
        if (string.IsNullOrEmpty(ticket))
            return false;

        var visible = false;
        var rest = ticket.AsSpan();
        while (!rest.IsEmpty)
        {
            // An unpaired surrogate is not text at all.
            if (Rune.DecodeFromUtf16(rest, out var rune, out var consumed) != OperationStatus.Done)
                return false;
            rest = rest[consumed..];

            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format)
                return false;

            if (!Rune.IsWhiteSpace(rune) && !DrawsNothingAlone(category))
                visible = true;
        }

        return visible;
    }

    private static bool DrawsNothingAlone(UnicodeCategory category) => category is
        UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark or
        UnicodeCategory.OtherNotAssigned or UnicodeCategory.PrivateUse;

    /// <summary>
    /// The refusal for a missing ticket, as a ServiceNow result, or null when one was given - for the
    /// pages whose check is <c>Refusal(ticket) ?? await ServiceNow.ValidateTicketAsync(ticket)</c>.
    /// </summary>
    public static TicketValidationResult? Refusal(string? ticket) =>
        IsPresent(ticket) ? null : new TicketValidationResult { IsValid = false, Message = RequiredMessage };
}
