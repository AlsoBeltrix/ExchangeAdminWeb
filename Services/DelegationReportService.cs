using System.Management.Automation;
using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Services;

public class DelegationReportService : ExchangeServiceBase
{
    public DelegationReportService(ExoConnectionPool exoPool, DelineaService delineaService, ILogger logger, string onPremServerUri)
        : base(exoPool, delineaService, logger, onPremServerUri) { }

    public async Task<DelegationReportResult> GetMailboxDelegationAsync(string emailAddress)
    {
        return await RunPooledQueryAsync(ps =>
        {
            var result = new DelegationReportResult { EmailAddress = emailAddress };

            try
            {
                // Full Access
                ps.AddCommand("Get-MailboxPermission")
                  .AddParameter("Identity", emailAddress)
                  .AddParameter("ErrorAction", "Stop");
                var perms = Invoke(ps);
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
                var recipPerms = Invoke(ps);
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
                    var calPerms = Invoke(ps);
                    foreach (var perm in calPerms)
                    {
                        var user = perm.Properties["User"]?.Value?.ToString();
                        var rights = perm.Properties["AccessRights"]?.Value?.ToString();
                        if (user != null && rights != null
                            && !user.Equals("Default", StringComparison.OrdinalIgnoreCase)
                            && !user.Equals("Anonymous", StringComparison.OrdinalIgnoreCase))
                        {
                            result.Calendar.Add(new CalendarDelegationEntry { User = user, AccessRights = rights });
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not retrieve calendar permissions for {Email}", emailAddress);
                }
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                _logger.LogError(ex, "Error getting delegation report for {Email}", emailAddress);
            }

            return result;
        });
    }
}
