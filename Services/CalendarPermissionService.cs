using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Services;

public class CalendarPermissionService : ExchangeServiceBase
{
    public CalendarPermissionService(ExoConnectionPool exoPool, DelineaService delineaService, ILogger logger, string onPremServerUri)
        : base(exoPool, delineaService, logger, onPremServerUri) { }

    public Task<PermissionResult> SetCalendarPermissionAsync(string targetMailbox, string user, CalendarAccessRight accessRight)
    {
        string? calendarPath = null;

        return RunAsync(ps =>
        {
            var resolvedMailbox = ValidateMailbox(ps, targetMailbox);
            ValidateRecipient(ps, user);

            calendarPath = GetCalendarFolderName(ps, resolvedMailbox);
            var level = accessRight.ToString();

            ps.AddCommand("Set-MailboxFolderPermission")
              .AddParameter("Identity", calendarPath)
              .AddParameter("User", user)
              .AddParameter("AccessRights", level)
              .AddParameter("ErrorAction", "Stop");
            try
            {
                Invoke(ps);
            }
            catch
            {
                ps.Commands.Clear();
                ps.Streams.Error.Clear();
                ps.AddCommand("Add-MailboxFolderPermission")
                  .AddParameter("Identity", calendarPath)
                  .AddParameter("User", user)
                  .AddParameter("AccessRights", level)
                  .AddParameter("ErrorAction", "Stop");
                Invoke(ps);
            }
        }, () => ($"{user} granted {accessRight} permission to {calendarPath}", null));
    }

    public Task<PermissionResult> RemoveCalendarPermissionAsync(string targetMailbox, string user)
    {
        string? calendarPath = null;

        return RunAsync(ps =>
        {
            var resolvedMailbox = ValidateMailbox(ps, targetMailbox);
            ValidateRecipient(ps, user);

            calendarPath = GetCalendarFolderName(ps, resolvedMailbox);
            ps.AddCommand("Remove-MailboxFolderPermission")
              .AddParameter("Identity", calendarPath)
              .AddParameter("User", user)
              .AddParameter("Confirm", false);
            Invoke(ps);
        }, () => ($"Calendar permission removed for {user} on {calendarPath}", null));
    }

    public async Task<BulkOperationResult> ProcessCalendarPermissionsCsvAsync(Stream csvStream, bool isSet, PermissionValidator validator, string currentUser, AuditService audit, string ipAddress, string ticketNumber)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HeaderValidated = null,
            MissingFieldFound = null
        };

        using var reader = new StreamReader(csvStream, Encoding.UTF8);
        using var csv = new CsvReader(reader, config);

        var records = csv.GetRecords<CalendarPermissionCsvRow>().Take(201).ToList();
        if (records.Count > 200)
            return new BulkOperationResult { TotalRows = records.Count, FailedCount = records.Count, Errors = new() { "CSV exceeds 200 row limit. Please split into smaller files." } };
        var errors = new List<string>();
        var entries = new List<BulkOperationEntry>();
        var successCount = 0;

        foreach (var row in records)
        {
            try
            {
                var validationError = await validator.ValidateTargetMailboxAsync(row.Target);
                if (validationError is not null)
                {
                    audit.LogCalendarPermission(
                        currentUser, ipAddress, $"Bulk{(isSet ? "Set" : "Remove")}Calendar_ExcludedTarget",
                        row.Target, row.User, null, false, ticketNumber,
                        errorDetail: validationError);
                    errors.Add($"{row.Target}/{row.User}: {validationError}");
                    entries.Add(new BulkOperationEntry
                    {
                        Target = row.Target,
                        User = row.User,
                        Permission = "Calendar",
                        Status = "FAILED",
                        Message = validationError
                    });
                    continue;
                }

                if (isSet)
                {
                    var selfGrantError = await validator.ValidateSelfGrantAsync(currentUser, row.User);
                    if (selfGrantError is not null)
                    {
                        // Log self-grant attempt to audit log
                        audit.LogCalendarPermission(
                            currentUser, ipAddress, "BulkSetCalendar_SelfGrantAttempt",
                            row.Target, row.User, row.AccessRight, false, ticketNumber,
                            errorDetail: "Self-grant blocked: User attempted to grant permissions to themselves");

                        errors.Add($"{row.Target}/{row.User}: {selfGrantError}");
                        entries.Add(new BulkOperationEntry
                        {
                            Target = row.Target,
                            User = row.User,
                            Permission = "Calendar",
                            Status = "FAILED",
                            Message = selfGrantError
                        });
                        continue;
                    }
                }

                var permType = isSet ? row.AccessRight : "Remove";

                var result = isSet
                    ? await SetCalendarPermissionAsync(row.Target, row.User, Enum.Parse<CalendarAccessRight>(row.AccessRight))
                    : await RemoveCalendarPermissionAsync(row.Target, row.User);

                var action = $"Bulk{(isSet ? "Set" : "Remove")}Calendar";
                audit.LogCalendarPermission(
                    currentUser, ipAddress, action, row.Target, row.User,
                    isSet ? row.AccessRight : null, result.Success, ticketNumber,
                    errorDetail: result.Success ? null : result.Message);

                if (result.Success)
                {
                    successCount++;
                    entries.Add(new BulkOperationEntry
                    {
                        Target = row.Target,
                        User = row.User,
                        Permission = permType,
                        Status = "SUCCESS",
                        Message = result.Message
                    });
                }
                else
                {
                    errors.Add($"{row.Target}/{row.User}: {result.Message}");
                    entries.Add(new BulkOperationEntry
                    {
                        Target = row.Target,
                        User = row.User,
                        Permission = permType,
                        Status = "FAILED",
                        Message = result.Message
                    });
                }
            }
            catch (Exception ex)
            {
                audit.LogCalendarPermission(
                    currentUser, ipAddress, $"Bulk{(isSet ? "Set" : "Remove")}Calendar_Error",
                    row.Target, row.User, null, false, ticketNumber,
                    errorDetail: ex.Message);
                errors.Add($"{row.Target}/{row.User}: {ex.Message}");
                entries.Add(new BulkOperationEntry
                {
                    Target = row.Target,
                    User = row.User,
                    Permission = "Calendar",
                    Status = "ERROR",
                    Message = ex.Message
                });
            }
        }

        return new BulkOperationResult
        {
            TotalRows = records.Count,
            SuccessCount = successCount,
            FailedCount = errors.Count,
            Errors = errors,
            Entries = entries
        };
    }

    public async Task<PermissionResult> SetCalendarPermissionOnPremAsync(string targetMailbox, string user, string accessRight)
    {
        var creds = await _delineaService.GetExchangeCredentialsAsync();
        if (creds is null)
            return PermissionResult.Fail("Cannot connect to on-prem Exchange: credentials unavailable.");

        return await ThrottledAsync(async () => await Task.Run(() =>
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
                var session = ps.Runspace.SessionStateProxy.GetVariable("onpremSession");

                var script = ScriptBlock.Create(@"
                    param($Identity, $User, $AccessRights)
                    try {
                        Set-MailboxFolderPermission -Identity ""$Identity`:\Calendar"" -User $User -AccessRights $AccessRights -ErrorAction Stop
                    } catch {
                        Add-MailboxFolderPermission -Identity ""$Identity`:\Calendar"" -User $User -AccessRights $AccessRights -ErrorAction Stop
                    }");
                ps.AddCommand("Invoke-Command")
                  .AddParameter("Session", session)
                  .AddParameter("ScriptBlock", script)
                  .AddParameter("ArgumentList", new object[] { targetMailbox, user, accessRight });
                Invoke(ps);

                return new PermissionResult { Success = true, Message = $"{user} granted {accessRight} on {targetMailbox} calendar (on-premises)." };
            }
            finally
            {
                try
                {
                    ps.Commands.Clear();
                    var s = ps.Runspace.SessionStateProxy.GetVariable("onpremSession");
                    if (s != null) { ps.AddCommand("Remove-PSSession").AddParameter("Session", s); ps.Invoke(); }
                }
                catch { }
            }
        }), _onPremThrottle);
    }

    public async Task<PermissionResult> RemoveCalendarPermissionOnPremAsync(string targetMailbox, string user)
    {
        var creds = await _delineaService.GetExchangeCredentialsAsync();
        if (creds is null)
            return PermissionResult.Fail("Cannot connect to on-prem Exchange: credentials unavailable.");

        return await ThrottledAsync(async () => await Task.Run(() =>
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
                var session = ps.Runspace.SessionStateProxy.GetVariable("onpremSession");

                var script = ScriptBlock.Create(@"
                    param($Identity, $User)
                    Remove-MailboxFolderPermission -Identity ""$Identity`:\Calendar"" -User $User -Confirm:$false -ErrorAction Stop");
                ps.AddCommand("Invoke-Command")
                  .AddParameter("Session", session)
                  .AddParameter("ScriptBlock", script)
                  .AddParameter("ArgumentList", new object[] { targetMailbox, user });
                Invoke(ps);

                return new PermissionResult { Success = true, Message = $"Calendar permissions removed for {user} on {targetMailbox} (on-premises)." };
            }
            finally
            {
                try
                {
                    ps.Commands.Clear();
                    var s = ps.Runspace.SessionStateProxy.GetVariable("onpremSession");
                    if (s != null) { ps.AddCommand("Remove-PSSession").AddParameter("Session", s); ps.Invoke(); }
                }
                catch { }
            }
        }), _onPremThrottle);
    }
}
