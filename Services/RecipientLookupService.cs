using System.Management.Automation;
using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Services;

public class RecipientLookupService : ExchangeServiceBase
{
    public RecipientLookupService(ExoConnectionPool exoPool, DelineaService delineaService, ILogger logger, string onPremServerUri)
        : base(exoPool, delineaService, logger, onPremServerUri) { }

    public async Task<RecipientInfoResult> GetRecipientInfoAsync(string emailAddress)
    {
        var result = await RunPooledQueryAsync(ps =>
        {
            var r = new RecipientInfoResult { EmailAddress = emailAddress };

            try
            {
                ps.AddCommand("Get-Recipient")
                  .AddParameter("Identity", emailAddress)
                  .AddParameter("ErrorAction", "Stop");
                var recipients = Invoke(ps);
                var recip = recipients.FirstOrDefault();

                if (recip == null)
                {
                    r.Error = $"Recipient '{emailAddress}' not found.";
                    return r;
                }

                r.DisplayName = recip.Properties["DisplayName"]?.Value?.ToString();
                r.RecipientType = recip.Properties["RecipientTypeDetails"]?.Value?.ToString();
                r.WhenCreated = recip.Properties["WhenCreated"]?.Value as DateTime?;

                var emailAddresses = recip.Properties["EmailAddresses"]?.Value;
                if (emailAddresses is System.Collections.IEnumerable addrs)
                {
                    foreach (var addr in addrs)
                    {
                        var s = addr?.ToString();
                        if (s != null) r.EmailAddresses.Add(s);
                    }
                }

                r.MailboxLocation = MailboxLocationClassifier.ForLookupDisplay(r.RecipientType);

                ps.AddCommand("Get-Mailbox")
                  .AddParameter("Identity", emailAddress)
                  .AddParameter("ErrorAction", "Ignore");
                var mbxResults = InvokeOptional(ps);
                var mbx = mbxResults.FirstOrDefault();

                if (mbx != null)
                {
                    r.ForwardingAddress = mbx.Properties["ForwardingSmtpAddress"]?.Value?.ToString()
                        ?? mbx.Properties["ForwardingAddress"]?.Value?.ToString();

                    try
                    {
                        ps.AddCommand("Get-MailboxStatistics")
                          .AddParameter("Identity", emailAddress)
                          .AddParameter("ErrorAction", "Stop");
                        var stats = Invoke(ps);
                        var stat = stats.FirstOrDefault();
                        if (stat != null)
                        {
                            r.ItemCount = ParseLong(stat.Properties["ItemCount"]?.Value);
                            r.DeletedItemCount = ParseLong(stat.Properties["DeletedItemCount"]?.Value);
                            r.LastLogonTime = stat.Properties["LastLogonTime"]?.Value as DateTime?;
                            r.MailboxSizeGB = ParseSizeToGB(stat.Properties["TotalItemSize"]?.Value?.ToString());
                            r.DeletedItemSizeGB = ParseSizeToGB(stat.Properties["TotalDeletedItemSize"]?.Value?.ToString());
                        }
                    }
                    catch (Exception ex)
                    {
                        r.Warnings.Add($"Could not retrieve mailbox statistics: {ex.Message}");
                    }

                    try
                    {
                        ps.AddCommand("Get-MailboxStatistics")
                          .AddParameter("Identity", emailAddress)
                          .AddParameter("Archive", true)
                          .AddParameter("ErrorAction", "Stop");
                        var archiveStats = Invoke(ps);
                        var archiveStat = archiveStats.FirstOrDefault();
                        if (archiveStat != null)
                        {
                            r.ArchiveEnabled = true;
                            r.ArchiveItemCount = ParseLong(archiveStat.Properties["ItemCount"]?.Value);
                            r.ArchiveDeletedItemCount = ParseLong(archiveStat.Properties["DeletedItemCount"]?.Value);
                            r.ArchiveSizeGB = ParseSizeToGB(archiveStat.Properties["TotalItemSize"]?.Value?.ToString());
                            r.ArchiveDeletedItemSizeGB = ParseSizeToGB(archiveStat.Properties["TotalDeletedItemSize"]?.Value?.ToString());
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch (Exception ex)
            {
                r.Error = ex.Message;
                _logger.LogError(ex, "Error getting recipient info for {Email}", emailAddress);
            }

            return r;
        });

        if (result.Error == null && result.MailboxLocation == "On-Premises")
        {
            try
            {
                var onPremSize = await GetOnPremMailboxSizeAsync(emailAddress);
                if (onPremSize != null)
                {
                    result.MailboxSizeGB = onPremSize.Value.mailboxSizeGB;
                    result.ArchiveSizeGB = onPremSize.Value.archiveSizeGB;
                }
                else
                {
                    result.Warnings.Add("On-premises mailbox size unavailable (connection not configured).");
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Could not retrieve on-premises mailbox size: {ex.Message}");
            }
        }

        return result;
    }
}
