using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Services;

public class RecipientLookupService : ExchangeServiceBase
{
    public RecipientLookupService(ExoConnectionPool exoPool, DelineaService delineaService, ILogger<RecipientLookupService> logger, IConfiguration config, ModuleCredentialService moduleCredentials, OperationTraceService operationTrace)
        : base(exoPool, delineaService, logger, config["OnPremExchange:ServerUri"] ?? "", moduleCredentials, "RecipientLookup", operationTrace) { }

    // kd-37. The page's activity token, for the two on-premises reads' credentials: a cancel during either leaves the lookup as a
    // cancellation (neither catch takes one), where it finished with a warning. The Exchange Online reads take none.
    // RecipientLookupAccuracy S1. What the recipient is, and where its mailbox is, from Exchange's own answers on the side that
    // holds it (RecipientClassification, docs/RecipientLookupAccuracy-Plan.md 3.1-3.4). Exchange Online's Get-Mailbox is asked
    // only for a type it can return or one the tables do not hold; on-premises Exchange only about a mail user, or an unnamed
    // type with no Exchange Online mailbox, that Exchange Online does not report as cloud-only - by this module's own read,
    // below, not the shared location read, which repeated Exchange Online's Get-Mailbox and asked on-premises about every
    // recipient without an Exchange Online mailbox. The shared read stays, unchanged, for the two permission pages.
    public async Task<RecipientInfoResult> GetRecipientInfoAsync(string emailAddress, CancellationToken cancellationToken = default)
    {
        // Read-only: safe to retry on a dead pooled session. Each attempt starts from nothing, so a retry's answers are its own.
        var (result, mailboxAnswer, mailboxType) = await RunPooledQueryAsync((ps, tracker) =>
        {
            var r = new RecipientInfoResult { EmailAddress = emailAddress };
            var answer = CloudMailboxAnswer.NotAsked;
            string? ownType = null;

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
                    return (r, answer, ownType);
                }

                r.DisplayName = recip.Properties["DisplayName"]?.Value?.ToString();
                r.RecipientType = recip.Properties["RecipientTypeDetails"]?.Value?.ToString();
                // S1. The rest of the row the classification and the later reads need: no read is added.
                r.RecipientTypeCategory = recip.Properties["RecipientType"]?.Value?.ToString();
                var primary = recip.Properties["PrimarySmtpAddress"]?.Value?.ToString();
                r.PrimarySmtpAddress = string.IsNullOrWhiteSpace(primary) ? null : primary.Trim();
                r.IsDirSynced = RecipientClassification.ReadIsDirSynced(recip.Properties["IsDirSynced"]?.Value);
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
                // cancellation is not taken (aud-a23g's rule). RecipientLookupAccuracy S1: asked only when Exchange Online's type
                // leaves the answer open (RecipientClassification.AsksExchangeOnlineMailbox); for a list, contact, guest, mail
                // user or Microsoft 365 group it is known before it is made.
                PSObject? mbx = null;
                if (RecipientClassification.AsksExchangeOnlineMailbox(r.RecipientType))
                {
                    ps.AddCommand("Get-Mailbox")
                      .AddParameter("Identity", emailAddress)
                      .AddParameter("ErrorAction", "Stop");
                    try
                    {
                        mbx = ReadUnlessNotFound(ps, tracker).FirstOrDefault();
                        answer = mbx is null ? CloudMailboxAnswer.NotFound : CloudMailboxAnswer.Found;
                    }
                    catch (Exception ex) when (!IsCancellation(ex))
                    {
                        answer = CloudMailboxAnswer.Failed;
                        r.MailboxUnreadable = r.ArchiveUnreadable = $"Get-Mailbox failed: {ResolveFailure(ex).primary.TrimEnd('.')}";
                        r.Warnings.Add($"The Exchange Online mailbox could not be read: {r.MailboxUnreadable}.");
                    }
                }

                if (mbx != null)
                {
                    ownType = mbx.Properties["RecipientTypeDetails"]?.Value?.ToString();
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
                            // RecipientLookupAccuracy S2: and the figures say "Could not be read", not "N/A".
                            r.StatisticsUnreadable = "Exchange returned no mailbox statistics";
                            r.Warnings.Add("Could not retrieve mailbox statistics: Exchange returned no mailbox statistics.");
                        }
                    }
                    catch (Exception ex) when (!IsCancellation(ex))
                    {
                        // aud-a68. The cmdlet's own words (aud-a23i's helper), not PowerShell's "The running command stopped
                        // because ..." prefix. A cancellation is not a read that failed (aud-a23g's rule): it goes to the lookup's
                        // own catch, as the archive read's does.
                        r.StatisticsUnreadable = ResolveFailure(ex).primary.TrimEnd('.');
                        r.Warnings.Add($"Could not retrieve mailbox statistics: {r.StatisticsUnreadable}.");
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
                // aud-a68. Exchange's own "no such recipient", read from the error's structure, is the lookup's own words, which
                // only an empty answer reached; anything else is the cmdlet's own words (aud-a23i's helper), not PowerShell's
                // "The running command stopped because ..." prefix. Every later read here takes its own not-found, so the one
                // that gets this far is Get-Recipient's.
                r.Error = IsNotFound(ex) ? $"Recipient '{emailAddress}' not found." : ResolveFailure(ex).primary;
                _logger.LogError(ex, "Error getting recipient info for {Email}", emailAddress);
            }

            return (r, answer, ownType);
        }, allowRetry: true);

        if (result.Error != null)
            return result;

        // S1. On-premises is asked about the primary address Exchange Online returned (the typed text when it reported none),
        // so its answer is about the object Exchange Online found.
        var address = result.PrimarySmtpAddress ?? emailAddress;
        var onPrem = OnPremRecipientAnswer.NotAsked;
        string? onPremType = null;
        if (RecipientClassification.AsksOnPremises(result.RecipientType, result.IsDirSynced, mailboxAnswer))
        {
            try
            {
                (onPrem, onPremType) = await ReadOnPremRecipientAsync(address, cancellationToken);
            }
            catch (Exception ex) when (!IsCancellation(ex))
            {
                // kd-14. As the statistics read's catch (aud-a68 (2)): a cancellation is not a read that failed, so it leaves the
                // lookup (aud-a23g's rule); anything else in the error's own words (aud-a23i's helper) - today's warning (U1).
                onPrem = OnPremRecipientAnswer.CouldNotBeAsked;
                result.Warnings.Add($"Could not determine mailbox location: {ResolveFailure(ex).primary}");
            }
        }

        var classified = RecipientClassification.Classify(
            new RecipientFacts(address, result.RecipientType, result.IsDirSynced, mailboxAnswer, mailboxType, onPrem, onPremType));
        result.OnPremRecipientType = onPremType;
        result.RecipientTypeLabel = classified.Label;
        result.RecipientTypeSmallPrint = classified.SmallPrint;
        result.LocationClass = classified.Location;
        result.MailboxLocation = RecipientClassification.LocationWord(classified.Location);
        result.LocationNote = classified.LocationNote;
        if (classified.Warning is { } unsettled)
            result.Warnings.Add(unsettled);

        if (result.LocationClass == RecipientLocationClass.OnPremises)
        {
            try
            {
                // aud-a23o. A size only when it was read; one that could not be is a warning naming why. An unparseable size
                // showed 0 GB, a failed archive read 0 GB, and any failure "unavailable (connection not configured)", a cause
                // it guessed.
                var onPremSize = await GetOnPremMailboxSizeAsync(emailAddress, cancellationToken);
                if (onPremSize.MailboxUnreadable is null)
                    result.MailboxSizeGB = onPremSize.MailboxGB;
                else
                    result.StatisticsUnreadable = onPremSize.MailboxUnreadable; // S2: its size reads "Could not be read", not "N/A"
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
            catch (Exception ex) when (!IsCancellation(ex)) // kd-14: the same
            {
                result.ArchiveUnreadable = ResolveFailure(ex).primary.TrimEnd('.'); // aud-a68: the archive was not read either, so it is not "Disabled"
                result.StatisticsUnreadable = result.ArchiveUnreadable; // S2: nor the size, so it is not "N/A"
                result.Warnings.Add($"Could not retrieve on-premises mailbox size: {ResolveFailure(ex).primary}");
            }
        }

        return result;
    }

    // S1 (the plan's N4). What on-premises Exchange holds at the address - its RecipientTypeDetails, Exchange's not-found, or
    // more than one recipient - in one on-premises session, built from the base's protected surface as the base's own location
    // read is, so no shared file changes. The credential under the location read's purpose, so the vault log and the tests keep
    // their keys, with the page's token (kd-37: it reaches the credential read; the throttle, session and read take none, as
    // before). The address goes as an -ArgumentList value, never into the script text: it began as operator input, and the
    // session runs under a privileged credential. Exchange's not-found, by structure (IsNotFound), is an answer; every other
    // failure, a cmdlet the session lacks included, throws "on-premises Exchange could not be asked: ...", the base's words,
    // for the caller's catch. A cancellation is let through (aud-a23g's rule). ASSUMPTION, unverified (no live call; the
    // plan's A3, A4): the module's account may run Get-Recipient, and remoting keeps its not-found record's shape; if not, the
    // lookup reads "Could not be determined" in the cmdlet's own words - visible, never a guess.
    private async Task<(OnPremRecipientAnswer answer, string? type)> ReadOnPremRecipientAsync(string address, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_onPremServerUri))
        {
            _logger.LogWarning("OnPremExchange:ServerUri is not configured; cannot read the on-premises recipient type of {Identity}", address);
            throw new InvalidOperationException($"on-premises Exchange could not be asked: {OnPremNotConfigured}");
        }

        _operationTrace?.Step("OnPremRecipientTypeRequested", backend: "OnPremExchange", target: address);
        var creds = await GetModuleCredentialsAsync("on-prem mailbox location check", cancellationToken);
        if (creds is null)
        {
            _logger.LogError("Cannot read the on-premises recipient type of {Identity}: failed to retrieve credentials from Delinea", address);
            _operationTrace?.Step("OnPremRecipientTypeRequested", "Failed", backend: "OnPremExchange", target: address,
                details: new Dictionary<string, object?> { ["reason"] = "Credentials unavailable" });
            throw new InvalidOperationException($"on-premises Exchange could not be asked: {OnPremCredentialsUnavailable}");
        }

        return await ThrottledAsync(() => Task.Run(() =>
        {
            var iss = InitialSessionState.CreateDefault();
            iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
            using var runspace = RunspaceFactory.CreateRunspace(iss);
            runspace.Open();
            using var ps = PowerShell.Create();
            ps.Runspace = runspace;

            try
            {
                ConnectOnPrem(ps, creds.Value.username, creds.Value.password, creds.Value.domain);
                var session = ps.Runspace.SessionStateProxy.GetVariable("onpremSession")
                    ?? throw new InvalidOperationException("Get-Recipient failed: there is no on-premises Exchange session to run it in");

                // -ErrorAction Stop on the remote command and on Invoke-Command, as the base's AddOnPremRead: a remote failure
                // throws here with its record for IsNotFound to read.
                ps.AddCommand("Invoke-Command")
                  .AddParameter("Session", session)
                  .AddParameter("ScriptBlock", ScriptBlock.Create(OnPremRecipientRead))
                  .AddParameter("ArgumentList", new object[] { address })
                  .AddParameter("ErrorAction", "Stop");
                Collection<PSObject> rows;
                try
                {
                    rows = ReadUnlessNotFound(ps, new ConnectionErrorTracker());
                }
                catch (Exception ex) when (!IsCancellation(ex))
                {
                    throw new InvalidOperationException($"Get-Recipient failed: {ResolveFailure(ex).primary.TrimEnd('.')}", ex);
                }

                return rows.Count switch
                {
                    0 => (OnPremRecipientAnswer.NotFound, (string?)null),
                    1 => (OnPremRecipientAnswer.Found, rows[0].Properties["RecipientTypeDetails"]?.Value?.ToString()),
                    _ => (OnPremRecipientAnswer.MoreThanOne, null),
                };
            }
            catch (Exception ex) when (!IsCancellation(ex))
            {
                _logger.LogError(ex, "Failed to read the on-premises recipient type of {Identity}", address);
                throw new InvalidOperationException($"on-premises Exchange could not be asked: {ResolveFailure(ex).primary.TrimEnd('.')}", ex);
            }
            finally
            {
                RemoveOnPremSession(ps);
            }
        }), _onPremThrottle);
    }

    // Two rows are enough to tell one recipient from more than one (U2).
    private const string OnPremRecipientRead =
        "param($Identity) Get-Recipient -Identity $Identity -ErrorAction Stop | Select-Object -First 2 RecipientType, RecipientTypeDetails";
}
