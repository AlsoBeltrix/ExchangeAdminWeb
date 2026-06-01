using System.Management.Automation;

namespace ExchangeAdminWeb.Services;

public class ExchangeIdentityResolver : ExchangeServiceBase, IIdentityResolver
{
    public ExchangeIdentityResolver(ExoConnectionPool exoPool, DelineaService delineaService, ILogger<ExchangeIdentityResolver> logger, IConfiguration config)
        : base(exoPool, delineaService, logger, config["OnPremExchange:ServerUri"] ?? "") { }

    public async Task<string?> ResolveToObjectIdAsync(string identity)
    {
        try
        {
            return await RunPooledQueryAsync((ps, tracker) =>
            {
                ps.AddCommand("Get-Recipient")
                  .AddParameter("Identity", identity)
                  .AddParameter("ErrorAction", "Stop");

                var results = Invoke(ps, tracker);
                var recipient = results.FirstOrDefault();
                return recipient?.Properties["ExternalDirectoryObjectId"]?.Value?.ToString();
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve identity for {Identity}", identity);
            return null;
        }
    }
}
