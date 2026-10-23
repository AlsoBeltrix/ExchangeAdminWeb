using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Security.Claims;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Services;

public class CalendarPermissionService : ExchangeServiceBase
{
    /// <summary>
    /// Module id for the servicer grant. Must match the catalog descriptor AND the page's own
    /// constant: PermissionValidator serves three modules, so a borrowed id would let a grant in
    /// one authorise the others.
    /// </summary>
    private const string ServicerModuleId = "CalendarPermissions";

    public CalendarPermissionService(ExoConnectionPool exoPool, DelineaService delineaService, ILogger<CalendarPermissionService> logger, IConfiguration config, ModuleCredentialService moduleCredentials, OperationTraceService operationTrace)
        : base(exoPool, delineaService, logger, config["OnPremExchange:ServerUri"] ?? "", moduleCredentials, "CalendarPermissions", operationTrace) { }

    /// <param name="targetMailbox">The operator's input, for the message only.</param>
    /// <param name="mailbox">
    /// The mailbox the protected-principal check validated (aud-a2d). Its GUID is the mailbox part
    /// of the folder identity written; null refuses before a connection is borrowed.
    /// </param>
    public Task<PermissionResult> SetCalendarPermissionAsync(string targetMailbox, ValidatedMailbox? mailbox, string user, CalendarAccessRight accessRight)
    {
        if (mailbox is null)
            return Task.FromResult(PermissionResult.Fail(ValidatedMailbox.Refusal(targetMailbox)));

        string? calendarPath = null;
        string? resolvedMailbox = null;

        return RunAsync((ps, tracker) =>
        {
            resolvedMailbox = BindValidatedMailbox(ps, tracker, mailbox);
            ValidateRecipient(ps, user);

            // aud-a2d. The folder identity was built from the primary address the TYPED string
            // resolved to, and Exchange resolved that address again on write. It is built from the
            // validated mailbox's GUID now, and the folder name is read from that mailbox too - a
            // localized name ("Kalender") belongs to one mailbox, not to whatever a string names.
            calendarPath = GetCalendarFolderName(ps, mailbox.Identity);
            var level = accessRight.ToString();

            ps.AddCommand("Set-MailboxFolderPermission")
              .AddParameter("Identity", calendarPath)
              .AddParameter("User", user)
              .AddParameter("AccessRights", level)
              .AddParameter("ErrorAction", "Stop");
            try
            {
                Invoke(ps, tracker);
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
                Invoke(ps, tracker);
            }
        }, () => ($"{user} granted {accessRight} permission on the calendar of {resolvedMailbox} ({calendarPath})", null));
    }

    /// <param name="mailbox">As on <see cref="SetCalendarPermissionAsync"/>: null refuses.</param>
    public Task<PermissionResult> RemoveCalendarPermissionAsync(string targetMailbox, ValidatedMailbox? mailbox, string user)
    {
        if (mailbox is null)
            return Task.FromResult(PermissionResult.Fail(ValidatedMailbox.Refusal(targetMailbox)));

        string? calendarPath = null;
        string? resolvedMailbox = null;

        // Single-write (Remove-MailboxFolderPermission): safe to retry on a dead pooled session.
        // The preceding Get-* validations are reads. SetCalendarPermissionAsync is deliberately
        // NOT opted in: it is a Set-then-fallback-Add (two writes) per the retry-safety audit.
        return RunAsync((ps, tracker) =>
        {
            resolvedMailbox = BindValidatedMailbox(ps, tracker, mailbox);
            ValidateRecipient(ps, user);

            // The validated mailbox's folder, by its GUID - see SetCalendarPermissionAsync.
            calendarPath = GetCalendarFolderName(ps, mailbox.Identity);
            ps.AddCommand("Remove-MailboxFolderPermission")
              .AddParameter("Identity", calendarPath)
              .AddParameter("User", user)
              .AddParameter("Confirm", false);
            Invoke(ps, tracker);
        }, () => ($"Calendar permission removed for {user} on the calendar of {resolvedMailbox} ({calendarPath})", null), allowRetry: true);
    }

    /// <param name="actingUser">
    /// REQUIRED and not defaulted: the protected-principal servicer decision needs a real
    /// principal. Bulk is where a servicer grant is MOST useful and it was the path that could not
    /// use it - the single-target form serviced a protected mailbox while the same mailbox in a CSV
    /// was refused (pps-1). Null refuses, so any caller without a principal is safe.
    /// </param>
    public async Task<BulkOperationResult> ProcessCalendarPermissionsCsvAsync(Stream csvStream, bool isSet, PermissionValidator validator, string currentUser, AuditService audit, EmailService email, string ipAddress, string ticketNumber, ClaimsPrincipal? actingUser)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HeaderValidated = null,
            MissingFieldFound = null
        };

        using var reader = new StreamReader(csvStream, Encoding.UTF8);
        using var csv = new CsvReader(reader, config);

        var records = new List<CalendarPermissionCsvRow>();
        await foreach (var row in csv.GetRecordsAsync<CalendarPermissionCsvRow>())
        {
            records.Add(row);
            // Stop one row past the cap: enough to know the file is oversized, without reading an
            // unbounded file into memory.
            if (BulkCsvRowLimit.ShouldStopReading(records.Count))
                break;
        }
        if (BulkCsvRowLimit.Exceeds(records.Count))
            return BulkCsvRowLimit.Rejected(records.Count);
        var errors = new List<string>();
        var entries = new List<BulkOperationEntry>();
        var successCount = 0;

        foreach (var row in records)
        {
            try
            {
                // The servicing overload with an explicit module id: the back-compat overload
                // cannot service by construction, and a borrowed id would let a Mailbox grant
                // authorise Calendar - the validator serves three modules (pps-1).
                var targetValidation = await validator.ValidateTargetMailboxAsync(
                    row.Target, actingUser, ServicerModuleId);
                var servicedNote = targetValidation.ServicedNote;
                var validationError = targetValidation.Error;
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
                    var selfGrantError = await validator.ValidateSelfGrantAsync(actingUser, row.User);
                    if (selfGrantError is not null)
                    {
                        // The record carries the validator's own refusal: a check that could not be completed is not a self-grant (aud-a28y).
                        audit.LogCalendarPermission(
                            currentUser, ipAddress, "BulkSetCalendar_SelfGrantAttempt",
                            row.Target, row.User, row.AccessRight, false, ticketNumber,
                            errorDetail: selfGrantError);

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

                // aud-a2d: this ROW's validated mailbox, never row.Target.
                var result = isSet
                    ? await SetCalendarPermissionAsync(row.Target, targetValidation.Mailbox, row.User, Enum.Parse<CalendarAccessRight>(row.AccessRight))
                    : await RemoveCalendarPermissionAsync(row.Target, targetValidation.Mailbox, row.User);

                var action = $"Bulk{(isSet ? "Set" : "Remove")}Calendar";
                // Per row, because servicing is decided per row: a batch can mix serviced and
                // ordinary targets. The note rides extra - a serviced row SUCCEEDS, and errorDetail
                // is written as null on success, so the record of who permitted it would vanish.
                // The identity written rides beside it; `target` stays the row as typed.
                audit.LogCalendarPermission(
                    currentUser, ipAddress, action, row.Target, row.User,
                    isSet ? row.AccessRight : null, result.Success, ticketNumber,
                    extra: ValidatedMailbox.WithWrittenIdentity(ProtectedPrincipalServicing.Extra(servicedNote), targetValidation.Mailbox),
                    errorDetail: result.Success ? null : result.Message);

                // Audit finding A24, and the trustee label matches the single-target path: a
                // calendar notice names the target as "<address> (Calendar)". A calendar row
                // sets one right, so success IS the applied set - there is no partial here.
                //
                // Review of A24: permType is the OPERATOR's word, and it feeds the audit row
                // and the bulk outcome table - "Reviewer", or "Remove" for a revoke. Sending
                // that to the affected person told them 'Permission: Remove', naming an
                // action rather than the access that changed. The single-target paths say
                // "Calendar <level>" and "Calendar Access"; this now says the same, and
                // permType is left alone so the audit and the table are unchanged.
                var notifyPermType = isSet ? $"Calendar {row.AccessRight}" : "Calendar Access";
                await PermissionChangeNotifier.NotifyAffectedAsync(
                    email, result.Success ? [notifyPermType] : [], $"{row.Target} (Calendar)",
                    row.Target, row.User, currentUser, isSet, _logger);

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

    /// <summary>
    /// The on-premises grant, on the validated mailbox's calendar (aud-a2d): the remote script
    /// builds <c>&lt;GUID&gt;:\Calendar</c> from the argument it is sent. Null refuses before a
    /// credential is requested.
    /// </summary>
    public async Task<PermissionResult> SetCalendarPermissionOnPremAsync(string targetMailbox, ValidatedMailbox? mailbox, string user, string accessRight)
    {
        if (mailbox is null)
            return PermissionResult.Fail(ValidatedMailbox.Refusal(targetMailbox));

        var creds = await GetModuleCredentialsAsync("on-prem calendar permission operation");
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
                  .AddParameter("ArgumentList", new object[] { mailbox.Identity, user, accessRight });
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

    /// <summary>The on-premises revoke; bound exactly as <see cref="SetCalendarPermissionOnPremAsync"/>.</summary>
    public async Task<PermissionResult> RemoveCalendarPermissionOnPremAsync(string targetMailbox, ValidatedMailbox? mailbox, string user)
    {
        if (mailbox is null)
            return PermissionResult.Fail(ValidatedMailbox.Refusal(targetMailbox));

        var creds = await GetModuleCredentialsAsync("on-prem calendar permission operation");
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
                  .AddParameter("ArgumentList", new object[] { mailbox.Identity, user });
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
