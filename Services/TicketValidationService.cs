namespace ExchangeAdminWeb.Services;

/// <summary>How a ticket fared against a module's validation policy.</summary>
public enum TicketGateOutcome
{
    /// <summary>The ticket satisfies this module's policy; the operation may proceed.</summary>
    Accepted,

    /// <summary>The ticket itself is unacceptable (missing, or ServiceNow rejected it).</summary>
    Rejected,

    /// <summary>
    /// The policy could not be evaluated (unreadable config, an invalid switch
    /// value, or validation switched on while the ServiceNow integration is
    /// dormant). Consumers must refuse: fail closed, never fall through to a
    /// permissive default.
    /// </summary>
    Unavailable,
}

/// <summary>Result of a ticket-gate check. Named to avoid the existing
/// <see cref="TicketValidationResult"/> the ServiceNow client returns.</summary>
public sealed record TicketGateResult(TicketGateOutcome Outcome, string? Message)
{
    public bool Accepted => Outcome == TicketGateOutcome.Accepted;
}

/// <summary>Per-module ticket validation policy.</summary>
public interface ITicketValidator
{
    Task<TicketGateResult> ValidateAsync(string moduleId, string? ticketNumber);
}

/// <summary>
/// The per-module policy layer over the app's single ServiceNow client.
/// </summary>
/// <remarks>
/// Each module owns a <c>ValidateTickets</c> switch in its Module Config. Off
/// (the default): any non-blank ticket is accepted as plain audit metadata -
/// the repo's long-standing presence-only shape. On: the ticket must validate
/// through <see cref="ServiceNowService.ValidateTicketAsync"/>.
///
/// While ServiceNow is dormant (<see cref="ServiceNowService.Enabled"/> false),
/// On refuses rather than borrowing the dormant client's everything-passes
/// behavior: a switch an operator believes is validating must never silently
/// validate nothing (review finding btv-1; the idm-3 decorative-control class).
/// For the same reason a NON-EMPTY switch value that is not true/false refuses
/// instead of quietly meaning Off - a deliberate divergence from the
/// PreventSelfGrant unparseable-means-default convention, which guards a
/// behavior preference, not a control someone relies on.
/// </remarks>
public sealed class TicketValidationService : ITicketValidator
{
    internal const string ValidateTicketsKey = "ValidateTickets";

    private readonly ModuleConfigService _moduleConfig;
    private readonly ServiceNowService _serviceNow;

    public TicketValidationService(ModuleConfigService moduleConfig, ServiceNowService serviceNow)
    {
        _moduleConfig = moduleConfig;
        _serviceNow = serviceNow;
    }

    public async Task<TicketGateResult> ValidateAsync(string moduleId, string? ticketNumber)
    {
        // Fail closed on unreadable config before anything else: a module whose
        // policy cannot be read must not act as if its policy were Off.
        if (_moduleConfig.IsModuleCorrupt(moduleId))
        {
            return new(
                TicketGateOutcome.Unavailable,
                "Module configuration is unreadable, so the ticket policy cannot be evaluated. " +
                "Ask an administrator to check this module's config.");
        }

        // Presence is never waived, in either mode.
        if (string.IsNullOrWhiteSpace(ticketNumber))
        {
            return new(TicketGateOutcome.Rejected, "A ticket number is required.");
        }

        var configured = _moduleConfig.GetValue(moduleId, ValidateTicketsKey);

        // Unset is not a mistype: absent or blank means the switch was never
        // configured, and presence-only is the correct default.
        if (string.IsNullOrWhiteSpace(configured))
        {
            return new(TicketGateOutcome.Accepted, null);
        }

        if (!bool.TryParse(configured, out var validate))
        {
            return new(
                TicketGateOutcome.Unavailable,
                $"The {ValidateTicketsKey} setting for this module is '{configured.Trim()}', " +
                "which is not true or false. Fix it in Module Config.");
        }

        if (!validate)
        {
            return new(TicketGateOutcome.Accepted, null);
        }

        if (!_serviceNow.Enabled)
        {
            return new(
                TicketGateOutcome.Unavailable,
                "Ticket validation is switched on for this module, but the ServiceNow " +
                "integration is not enabled on this deployment.");
        }

        var result = await _serviceNow.ValidateTicketAsync(ticketNumber.Trim());
        return result.IsValid
            ? new(TicketGateOutcome.Accepted, null)
            : new(TicketGateOutcome.Rejected, result.Message);
    }
}
