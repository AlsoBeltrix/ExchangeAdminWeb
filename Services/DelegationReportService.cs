using System.Management.Automation;
using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Services;

public class DelegationReportService : ExchangeServiceBase
{
    public DelegationReportService(ExoConnectionPool exoPool, DelineaService delineaService, ILogger<DelegationReportService> logger, IConfiguration config)
        : base(exoPool, delineaService, logger, config["OnPremExchange:ServerUri"] ?? "") { }

    public async Task<DelegationReportResult> GetMailboxDelegationAsync(string emailAddress)
    {
        // Read-only: safe to retry on a dead pooled session.
        return await RunPooledQueryAsync((ps, tracker) =>
        {
            var result = new DelegationReportResult { EmailAddress = emailAddress };

            try
            {
                // Full Access
                ps.AddCommand("Get-MailboxPermission")
                  .AddParameter("Identity", emailAddress)
                  .AddParameter("ErrorAction", "Stop");
                var perms = Invoke(ps, tracker);
                foreach (var perm in perms)
                {
                    var user = perm.Properties["User"]?.Value?.ToString();
                    var accessRights = perm.Properties["AccessRights"]?.Value?.ToString();
                    var isInherited = perm.Properties["IsInherited"]?.Value as bool? ?? true;
                    if (user != null && !isInherited && accessRights?.Contains("FullAccess") == true
                        && !user.Equals("NT AUTHORITY\\SELF", StringComparison.OrdinalIgnoreCase))
                    {
                        result.FullAccess.Add(new DelegationEntry { User = user });
                    }
                }

                // Send As
                ps.AddCommand("Get-RecipientPermission")
                  .AddParameter("Identity", emailAddress)
                  .AddParameter("ErrorAction", "Stop");
                var recipPerms = Invoke(ps, tracker);
                foreach (var perm in recipPerms)
                {
                    var trustee = perm.Properties["Trustee"]?.Value?.ToString();
                    if (trustee != null && !trustee.Equals("NT AUTHORITY\\SELF", StringComparison.OrdinalIgnoreCase))
                    {
                        result.SendAs.Add(new DelegationEntry { User = trustee });
                    }
                }

                // Calendar
                try
                {
                    var calendarPath = GetCalendarFolderName(ps, emailAddress);
                    ps.AddCommand("Get-MailboxFolderPermission")
                      .AddParameter("Identity", calendarPath)
                      .AddParameter("ErrorAction", "Stop");
                    var calPerms = Invoke(ps, tracker);
                    foreach (var perm in calPerms)
                    {
                        var user = perm.Properties["User"]?.Value?.ToString();
                        var rights = perm.Properties["AccessRights"]?.Value?.ToString();

                        // Review of A18. A row missing either field used to be dropped in
                        // silence, so the section could be short while CalendarError stayed
                        // null - the same lie this finding is about, one row at a time.
                        if (user == null || rights == null)
                        {
                            result.CalendarError ??=
                                "At least one calendar permission could not be read and is missing from this list.";
                            _logger.LogWarning(
                                "Calendar permission row for {Email} had no User or AccessRights and was skipped", emailAddress);
                            continue;
                        }

                        if (!user.Equals("Default", StringComparison.OrdinalIgnoreCase)
                            && !user.Equals("Anonymous", StringComparison.OrdinalIgnoreCase))
                        {
                            result.Calendar.Add(new CalendarDelegationEntry { User = user, AccessRights = rights });
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Audit finding A18. This catch is deliberate - a mailbox with no calendar
                    // folder must not fail the whole report - but it used to swallow the reason
                    // entirely, leaving an empty list that renders as "No calendar delegates"
                    // and an audit row that says the report succeeded. An operator reading
                    // "none" cannot tell it from "we could not look".
                    result.CalendarError = ex.Message.Split(Environment.NewLine)[0];
                    _logger.LogWarning(ex, "Could not retrieve calendar permissions for {Email}", emailAddress);
                }
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                _logger.LogError(ex, "Error getting delegation report for {Email}", emailAddress);
            }

            return result;
        }, allowRetry: true);
    }
}
