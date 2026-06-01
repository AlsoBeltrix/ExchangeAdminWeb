using System.Management.Automation;
using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Services;

public class OutOfOfficeService : ExchangeServiceBase
{
    public OutOfOfficeService(ExoConnectionPool exoPool, DelineaService delineaService, ILogger logger, string onPremServerUri)
        : base(exoPool, delineaService, logger, onPremServerUri) { }

    public async Task<OutOfOfficeResult> GetOutOfOfficeAsync(string emailAddress)
    {
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
        });
    }

    public Task<PermissionResult> SetOutOfOfficeAsync(string emailAddress, string state, string? internalMessage, string? externalMessage, DateTime? startTime, DateTime? endTime)
    {
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
            : $"Auto-reply set to {state} for {emailAddress}.", (string?)null));
    }
}
