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
    /// Writes exactly the request the page captured before its first await (aud-a7b). The
    /// positional overload this replaced let a caller assemble the arguments from live form fields
    /// at the moment of the write; the page was its only caller.
    /// </summary>
    public Task<PermissionResult> SetOutOfOfficeAsync(OutOfOfficeRequest request)
    {
        var (emailAddress, state, internalMessage, externalMessage, startTime, endTime, _) = request;

        // Single-write (Set-MailboxAutoReplyConfiguration): safe to retry on a dead session.
        return RunAsync((ps, tracker) =>
        {
            ps.AddCommand("Set-MailboxAutoReplyConfiguration")
              .AddParameter("Identity", emailAddress)
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
