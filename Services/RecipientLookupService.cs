using System.Management.Automation;
using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Services;

public class RecipientLookupService : ExchangeServiceBase
{
    public RecipientLookupService(ExoConnectionPool exoPool, DelineaService delineaService, ILogger<RecipientLookupService> logger, IConfiguration config, ModuleCredentialService moduleCredentials, OperationTraceService operationTrace)
        : base(exoPool, delineaService, logger, config["OnPremExchange:ServerUri"] ?? "", moduleCredentials, "RecipientLookup", operationTrace) { }

    public async Task<RecipientInfoResult> GetRecipientInfoAsync(string emailAddress)
    {
        // Read-only: safe to retry on a dead pooled session.
        var result = await RunPooledQueryAsync((ps, tracker) =>
        {
            var r = new RecipientInfoResult { EmailAddress = emailAddress };

            try
            {
                ps.AddCommand("Get-Recipient")
                  .AddParameter("Identity", emailAddress)
                  .AddParameter("ErrorAction", "Stop");
                var recipients = Invoke(ps, tracker);
                var recip = recipients.FirstOrDefault();

                if (recip == null)
                {
                    r.Error = $"Recipient '{emailAddress}' not found.";
                    return r;
                }

                r.DisplayName = recip.Properties["DisplayName"]?.Value?.ToString();
                r.RecipientType = recip.Properties["RecipientTypeDetails"]?.Value?.ToString();
                // aud-a23d. Both times through the one reader, not `as DateTime?`: any other type came
                // back as null and the page showed "N/A", as if none were set. Which types the Exchange
                // Online module hands back is an unverified assumption; one this app cannot read is
                // kept and named on the page.
                r.WhenCreated = ReportedTime.Read(recip.Properties["WhenCreated"]?.Value);

                var emailAddresses = recip.Properties["EmailAddresses"]?.Value;
                if (emailAddresses is System.Collections.IEnumerable addrs)
                {
                    foreach (var addr in addrs)
                    {
                        var s = addr?.ToString();
                        if (s != null) r.EmailAddresses.Add(s);
                    }
                }

                // aud-a54. Under Stop, through ReadUnlessNotFound - the base's own location read's shape. Exchange's not-found,
                // by structure, is still "not a mailbox"; any other failure is a warning naming the read, and both markers are
                // set so the page shows what could not be read as such. Under -ErrorAction Ignore every failure, a throw
                // included, came back as no rows: "not a mailbox", Forwarding "None", Archive "Disabled", and nothing said. A
                // cancellation is not taken (aud-a23g's rule).
                ps.AddCommand("Get-Mailbox")
                  .AddParameter("Identity", emailAddress)
                  .AddParameter("ErrorAction", "Stop");
                PSObject? mbx = null;
                try
                {
                    mbx = ReadUnlessNotFound(ps, tracker).FirstOrDefault();
                }
                catch (Exception ex) when (!IsCancellation(ex))
                {
                    r.MailboxUnreadable = r.ArchiveUnreadable = $"Get-Mailbox failed: {ResolveFailure(ex).primary.TrimEnd('.')}";
                    r.Warnings.Add($"The Exchange Online mailbox could not be read: {r.MailboxUnreadable}.");
                }

                if (mbx != null)
                {
                    r.ForwardingAddress = mbx.Properties["ForwardingSmtpAddress"]?.Value?.ToString()
                        ?? mbx.Properties["ForwardingAddress"]?.Value?.ToString();

                    try
                    {
                        ps.AddCommand("Get-MailboxStatistics")
                          .AddParameter("Identity", emailAddress)
                          .AddParameter("ErrorAction", "Stop");
                        var stats = Invoke(ps, tracker);
                        var stat = stats.FirstOrDefault();
                        if (stat != null)
                        {
                            r.ItemCount = ParseLong(stat.Properties["ItemCount"]?.Value);
                            r.DeletedItemCount = ParseLong(stat.Properties["DeletedItemCount"]?.Value);
                            r.LastLogonTime = ReportedTime.Read(stat.Properties["LastLogonTime"]?.Value);
                            r.MailboxSizeGB = ParseSizeToGB(stat.Properties["TotalItemSize"]?.Value?.ToString());
                            r.DeletedItemSizeGB = ParseSizeToGB(stat.Properties["TotalDeletedItemSize"]?.Value?.ToString());
                        }
                        else
                        {
                            // aud-a68. An answer with no row is not statistics: every figure read "N/A" and nothing said why.
                            r.Warnings.Add("Could not retrieve mailbox statistics: Exchange returned no mailbox statistics.");
                        }
                    }
                    catch (Exception ex) when (!IsCancellation(ex))
                    {
                        // aud-a68. The cmdlet's own words (aud-a23i's helper), not PowerShell's "The running command stopped
                        // because ..." prefix. A cancellation is not a read that failed (aud-a23g's rule): it goes to the lookup's
                        // own catch, as the archive read's does.
                        r.Warnings.Add($"Could not retrieve mailbox statistics: {ResolveFailure(ex).primary.TrimEnd('.')}.");
                    }

                    // aud-a54. Exchange's own not-found for the archive, by structure (IsNotFound), is no archive; any other
                    // failure, or an answer with no row, is why the archive could not be read - the marker both size readers give
                    // that answer - and a warning. A bare catch made every failure no archive. ASSUMPTION, unverified (no live
                    // call): Exchange Online reports a mailbox with no archive in a shape IsNotFound reads (aud-a23k's, the same
                    // read); if not, such a mailbox reads "Could not be read" with Exchange's own words - visible, never a guess.
                    try
                    {
                        ps.AddCommand("Get-MailboxStatistics")
                          .AddParameter("Identity", emailAddress)
                          .AddParameter("Archive", true)
                          .AddParameter("ErrorAction", "Stop");
                        var archiveStats = Invoke(ps, tracker);
                        var archiveStat = archiveStats.FirstOrDefault();
                        if (archiveStat == null)
                        {
                            r.ArchiveUnreadable = "Exchange returned no archive statistics";
                        }
                        else
                        {
                            r.ArchiveEnabled = true;
                            r.ArchiveItemCount = ParseLong(archiveStat.Properties["ItemCount"]?.Value);
                            r.ArchiveDeletedItemCount = ParseLong(archiveStat.Properties["DeletedItemCount"]?.Value);
                            r.ArchiveSizeGB = ParseSizeToGB(archiveStat.Properties["TotalItemSize"]?.Value?.ToString());
                            r.ArchiveDeletedItemSizeGB = ParseSizeToGB(archiveStat.Properties["TotalDeletedItemSize"]?.Value?.ToString());
                        }
                    }
                    catch (Exception ex) when (IsNotFound(ex))
                    {
                        _logger.LogInformation("No archive mailbox found for {Email}", emailAddress);
                    }
                    catch (Exception ex) when (!IsCancellation(ex))
                    {
                        r.ArchiveUnreadable = $"Get-MailboxStatistics -Archive failed: {ResolveFailure(ex).primary.TrimEnd('.')}";
                    }

                    if (r.ArchiveUnreadable is { } archiveUnreadable)
                        r.Warnings.Add($"The Exchange Online archive could not be read: {archiveUnreadable}.");
                }
            }
            catch (Exception ex)
            {
                r.Error = ex.Message;
                _logger.LogError(ex, "Error getting recipient info for {Email}", emailAddress);
            }

            return r;
        }, allowRetry: true);

        if (result.Error == null)
        {
            try
            {
                result.MailboxLocation = await GetMailboxLocationAsync(emailAddress) switch
                {
                    "OnPrem" => "On-Premises",
                    "Cloud" => "Cloud",
                    _ => "Unknown"
                };
            }
            catch (Exception ex)
            {
                result.MailboxLocation = "Unknown";
                result.Warnings.Add($"Could not determine mailbox location: {ex.Message}");
            }
        }

        if (result.Error == null && result.MailboxLocation == "On-Premises")
        {
            try
            {
                // aud-a23o. A size only when it was read; one that could not be is a warning naming why. An unparseable size
                // showed 0 GB, a failed archive read 0 GB, and any failure "unavailable (connection not configured)", a cause
                // it guessed.
                var onPremSize = await GetOnPremMailboxSizeAsync(emailAddress);
                if (onPremSize.MailboxUnreadable is null)
                    result.MailboxSizeGB = onPremSize.MailboxGB;
                // aud-a68. An on-premises mailbox's archive is the on-premises read's to say: found, it is shown with its size;
                // not read, "Could not be read". ArchiveEnabled was set by the Exchange Online read alone, so every on-premises
                // archive showed "Archive: Disabled" with its rows hidden, and one that could not be read "Disabled" beside its
                // warning.
                result.ArchiveEnabled = onPremSize.ArchiveFound;
                result.ArchiveUnreadable = onPremSize.ArchiveUnreadable;
                if (onPremSize.ArchiveFound && onPremSize.ArchiveUnreadable is null)
                    result.ArchiveSizeGB = onPremSize.ArchiveGB;
                if (onPremSize.UnreadableReason("on-premises") is { } unreadable)
                    result.Warnings.Add($"{char.ToUpperInvariant(unreadable[0])}{unreadable[1..]}.");
            }
            catch (Exception ex)
            {
                result.ArchiveUnreadable = ResolveFailure(ex).primary.TrimEnd('.'); // aud-a68: the archive was not read either, so it is not "Disabled"
                result.Warnings.Add($"Could not retrieve on-premises mailbox size: {ex.Message}");
            }
        }

        return result;
    }
}
