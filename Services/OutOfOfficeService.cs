using System.Management.Automation;
using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Services;

public class OutOfOfficeService : ExchangeServiceBase
{
    public OutOfOfficeService(ExoConnectionPool exoPool, DelineaService delineaService, ILogger<OutOfOfficeService> logger, IConfiguration config)
        : base(exoPool, delineaService, logger, config["OnPremExchange:ServerUri"] ?? "") { }

    public async Task<OutOfOfficeResult> GetOutOfOfficeAsync(string emailAddress)
    {
        // Read-only: safe to retry on a dead pooled session.
        return await RunPooledQueryAsync((ps, tracker) =>
        {
            var result = new OutOfOfficeResult { EmailAddress = emailAddress, State = "Unknown" };

            try
            {
                ps.AddCommand("Get-MailboxAutoReplyConfiguration")
                  .AddParameter("Identity", emailAddress)
                  .AddParameter("ErrorAction", "Stop");
                var results = Invoke(ps, tracker);
                var config = results.FirstOrDefault();

                if (config == null)
                {
                    result.Error = $"Could not retrieve auto-reply configuration for '{emailAddress}'.";
                    return result;
                }

                result.State = config.Properties["AutoReplyState"]?.Value?.ToString() ?? "Disabled";
                result.InternalMessage = config.Properties["InternalMessage"]?.Value?.ToString();
                result.ExternalMessage = config.Properties["ExternalMessage"]?.Value?.ToString();
                result.StartTime = config.Properties["StartTime"]?.Value as DateTime?;
                result.EndTime = config.Properties["EndTime"]?.Value as DateTime?;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                _logger.LogError(ex, "Error getting OOF status for {Email}", emailAddress);
            }

            return result;
        }, allowRetry: true);
    }

    /// <summary>
    /// The current auto-reply of the mailbox the write will be bound to, for the audit's previous
    /// state (aud-a2d): read by its GUID, so the state recorded is the written mailbox's and not
    /// whatever the typed string resolves to. Null when there is no mailbox - the write then
    /// refuses as well.
    /// </summary>
    public async Task<OutOfOfficeResult?> GetOutOfOfficeAsync(ValidatedMailbox? mailbox) =>
        mailbox is null ? null : await GetOutOfOfficeAsync(mailbox.Identity);

    /// <summary>
    /// Writes exactly the request the page captured before its first await (aud-a7b), to the mailbox
    /// the protected-principal check validated (aud-a2d). The positional overload this replaced let
    /// a caller assemble the arguments from live form fields at the moment of the write; the page
    /// was its only caller.
    /// </summary>
    /// <param name="request">What to write. Its <c>Mailbox</c> is the operator's input, used for the message only.</param>
    /// <param name="mailbox">
    /// The only identity written. A second parameter rather than a field of the request: the request
    /// is what the operator asked for, captured before any await; this is what the check found, and
    /// exists only after it. Null refuses before a connection is borrowed.
    /// </param>
    public Task<PermissionResult> SetOutOfOfficeAsync(OutOfOfficeRequest request, ValidatedMailbox? mailbox)
    {
        var (emailAddress, state, internalMessage, externalMessage, startTime, endTime, _) = request;

        if (mailbox is null)
            return Task.FromResult(PermissionResult.Fail(ValidatedMailbox.Refusal(emailAddress)));

        // Single-write (Set-MailboxAutoReplyConfiguration): safe to retry on a dead session.
        return RunAsync((ps, tracker) =>
        {
            ps.AddCommand("Set-MailboxAutoReplyConfiguration")
              .AddParameter("Identity", mailbox.Identity)
              .AddParameter("AutoReplyState", state)
              .AddParameter("ErrorAction", "Stop");

            if (state != "Disabled")
            {
                if (!string.IsNullOrWhiteSpace(internalMessage))
                    ps.AddParameter("InternalMessage", internalMessage);
                if (!string.IsNullOrWhiteSpace(externalMessage))
                    ps.AddParameter("ExternalMessage", externalMessage);
            }

            if (state == "Scheduled")
            {
                if (startTime.HasValue)
                    ps.AddParameter("StartTime", startTime.Value);
                if (endTime.HasValue)
                    ps.AddParameter("EndTime", endTime.Value);
            }

            Invoke(ps, tracker);
        }, () => (state == "Disabled"
            ? $"Auto-reply disabled for {emailAddress}."
            : $"Auto-reply set to {state} for {emailAddress}.", (string?)null), allowRetry: true);
    }
}
