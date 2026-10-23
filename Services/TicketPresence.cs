using System.Diagnostics.CodeAnalysis;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// kd-24. The one rule for whether a ticket number was given at all, shared by the presence checks
/// in front of ticket-required writes and by <see cref="TicketValidationService"/>.
/// </summary>
/// <remarks>
/// <para>
/// Present means: not null, not empty or white space, and no control character anywhere in it. The
/// control-character half is the point. <see cref="string.IsNullOrWhiteSpace(string?)"/> answers false
/// for U+0000 or U+0001, and <see cref="ServiceNowService.ValidateTicketAsync"/> passes every ticket
/// while the integration is dormant, so a ticket made only of control characters went through every
/// presence check and reached the write with nothing a reader of the audit row could use.
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
    public static bool IsPresent([NotNullWhen(true)] string? ticket) =>
        !string.IsNullOrWhiteSpace(ticket) && !ticket.Any(char.IsControl);

    /// <summary>
    /// The refusal for a missing ticket, as a ServiceNow result, or null when one was given - for the
    /// pages whose check is <c>Refusal(ticket) ?? await ServiceNow.ValidateTicketAsync(ticket)</c>.
    /// </summary>
    public static TicketValidationResult? Refusal(string? ticket) =>
        IsPresent(ticket) ? null : new TicketValidationResult { IsValid = false, Message = RequiredMessage };
}
