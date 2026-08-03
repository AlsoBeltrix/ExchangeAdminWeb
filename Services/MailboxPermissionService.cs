using System.Globalization;
using System.Management.Automation;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using ExchangeAdminWeb.Models;
using System.Management.Automation.Runspaces;

namespace ExchangeAdminWeb.Services;

public class MailboxPermissionService : ExchangeServiceBase
{
    public MailboxPermissionService(ExoConnectionPool exoPool, DelineaService delineaService, ILogger<MailboxPermissionService> logger, IConfiguration config, ModuleCredentialService moduleCredentials, OperationTraceService operationTrace)
        : base(exoPool, delineaService, logger, config["OnPremExchange:ServerUri"] ?? "", moduleCredentials, "MailboxPermissions", operationTrace) { }

    public Task<PermissionResult> AddMailboxPermissionsAsync(string targetMailbox, string user, bool fullAccess, bool sendAs, bool autoMapping)
    {
        // Each right is granted in its own try/catch and the successes/failures are
        // aggregated, mirroring the on-prem methods below. The previous RunAsync shape
        // reported the whole operation as a single Fail if SendAs threw after FullAccess
        // had already been granted, hiding the applied grant from the bulk audit row and
        // CSV report (and inviting a "retry" that re-runs both). Invoke(ps, tracker) still
        // flags connection errors before throwing, so RunPooledQueryAsync discards a
        // poisoned pooled connection even though the per-right catch swallows the error.
        return RunPooledQueryAsync((ps, tracker) =>
        {
            try
            {
                ValidateMailbox(ps, targetMailbox);
                ValidateRecipient(ps, user);
            }
            catch (Exception ex)
            {
                return PermissionResult.Fail(ex.Message);
            }

            var outcomes = new List<RightOutcome>();

            if (fullAccess)
            {
                outcomes.Add(TryRight("FullAccess", () =>
                {
                    ps.AddCommand("Add-MailboxPermission")
                      .AddParameter("Identity", targetMailbox)
                      .AddParameter("User", user)
                      .AddParameter("AccessRights", "FullAccess")
                      .AddParameter("AutoMapping", autoMapping)
                      .AddParameter("Confirm", false);
                    Invoke(ps, tracker);
                }));
            }

            if (sendAs)
            {
                outcomes.Add(TryRight("SendAs", () =>
                {
                    ps.AddCommand("Add-RecipientPermission")
                      .AddParameter("Identity", targetMailbox)
                      .AddParameter("Trustee", user)
                      .AddParameter("AccessRights", "SendAs")
                      .AddParameter("Confirm", false);
                    Invoke(ps, tracker);
                }));
            }

            return MailboxPermissionOutcome.ForGrant(targetMailbox, user, outcomes);
        });
    }

    public Task<PermissionResult> RemoveMailboxPermissionsAsync(string targetMailbox, string user, bool fullAccess, bool sendAs)
    {
        // Per-right try/catch with aggregation - see AddMailboxPermissionsAsync for why
        // the previous single-Fail RunAsync shape lost partial-success information.
        return RunPooledQueryAsync((ps, tracker) =>
        {
            try
            {
                ValidateMailbox(ps, targetMailbox);
                ValidateRecipient(ps, user);
            }
            catch (Exception ex)
            {
                return PermissionResult.Fail(ex.Message);
            }

            var outcomes = new List<RightOutcome>();

            if (fullAccess)
            {
                outcomes.Add(TryRight("FullAccess", () =>
                {
                    ps.AddCommand("Remove-MailboxPermission")
                      .AddParameter("Identity", targetMailbox)
                      .AddParameter("User", user)
                      .AddParameter("AccessRights", "FullAccess")
                      .AddParameter("Confirm", false);
                    Invoke(ps, tracker);
                }));
            }

            if (sendAs)
            {
                outcomes.Add(TryRight("SendAs", () =>
                {
                    ps.AddCommand("Remove-RecipientPermission")
                      .AddParameter("Identity", targetMailbox)
                      .AddParameter("Trustee", user)
                      .AddParameter("AccessRights", "SendAs")
                      .AddParameter("Confirm", false);
                    Invoke(ps, tracker);
                }));
            }

            return MailboxPermissionOutcome.ForRevoke(targetMailbox, user, outcomes);
        });
    }

    /// <summary>
    /// Applies one right and records whether it took. A throw is captured, never propagated, so a
    /// later right is still attempted and the caller can report a partial result.
    /// </summary>
    /// <remarks>
    /// <c>Invoke(ps, tracker)</c> still flags connection errors before throwing, so a poisoned
    /// pooled connection is discarded by <c>RunPooledQueryAsync</c> even though the failure is
    /// swallowed here.
    /// </remarks>
    private static RightOutcome TryRight(string right, Action apply)
    {
        try
        {
            apply();
            return RightOutcome.Ok(right);
        }
        catch (Exception ex)
        {
            return RightOutcome.Failed(right, ex.Message);
        }
    }

    /// <summary>
    /// <see cref="TryRight"/> for the on-premises paths, which additionally reset the shared
    /// PowerShell instance.
    /// </summary>
    /// <remarks>
    /// The clear is load-bearing, not tidiness: the on-prem paths reuse ONE PowerShell instance
    /// across both rights, so a failed command left in the pipeline (or its error stream) would be
    /// re-invoked with, and attributed to, the next right.
    /// </remarks>
    private static RightOutcome TryRightOnPrem(PowerShell ps, string right, Action apply)
    {
        try
        {
            apply();
            return RightOutcome.Ok(right);
        }
        catch (Exception ex)
        {
            ps.Commands.Clear();
            ps.Streams.Error.Clear();
            return RightOutcome.Failed(right, ex.Message);
        }
    }

    public async Task<BulkOperationResult> ProcessMailboxPermissionsCsvAsync(Stream csvStream, bool isAdd, PermissionValidator validator, string currentUser, AuditService audit, string ipAddress, string ticketNumber)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HeaderValidated = null,
            MissingFieldFound = null
        };

        using var reader = new StreamReader(csvStream, Encoding.UTF8);
        using var csv = new CsvReader(reader, config);

        var records = new List<MailboxPermissionCsvRow>();
        await foreach (var row in csv.GetRecordsAsync<MailboxPermissionCsvRow>())
        {
            records.Add(row);
            if (records.Count > 200)
                break;
        }
        if (records.Count > 200)
            return new BulkOperationResult { TotalRows = records.Count, FailedCount = records.Count, Errors = new() { "CSV exceeds 200 row limit. Please split into smaller files." } };
        var errors = new List<string>();
        var entries = new List<BulkOperationEntry>();
        var successCount = 0;

        foreach (var row in records)
        {
            try
            {
                var fullAccess = ParseBool(row.FullAccess, "FullAccess");
                var sendAs = ParseBool(row.SendAs, "SendAs");
                var autoMap = ParseBool(string.IsNullOrWhiteSpace(row.AutoMapping) ? "True" : row.AutoMapping, "AutoMapping");

                if (!fullAccess && !sendAs)
                {
                    audit.LogMailboxPermission(
                        currentUser, ipAddress, $"Bulk{(isAdd ? "Add" : "Remove")}_NoPermissions",
                        row.Target, row.User, "None", false, ticketNumber,
                        errorDetail: "No permissions specified (FullAccess and SendAs are both false)");
                    errors.Add($"{row.Target}/{row.User}: No permissions specified (FullAccess and SendAs are both false)");
                    entries.Add(new BulkOperationEntry
                    {
                        Target = row.Target,
                        User = row.User,
                        Permission = "None",
                        Status = "FAILED",
                        Message = "No permissions specified"
                    });
                    continue;
                }

                var permissions = new List<string>();
                if (fullAccess) permissions.Add("FullAccess");
                if (sendAs) permissions.Add("SendAs");
                var permType = string.Join("+", permissions);

                // Validate target is not excluded
                var validationError = await validator.ValidateTargetMailboxAsync(row.Target);
                if (validationError is not null)
                {
                    audit.LogMailboxPermission(
                        currentUser, ipAddress, $"Bulk{(isAdd ? "Add" : "Remove")}_ExcludedTarget",
                        row.Target, row.User, permType, false, ticketNumber,
                        errorDetail: validationError);
                    errors.Add($"{row.Target}/{row.User}: {validationError}");
                    entries.Add(new BulkOperationEntry
                    {
                        Target = row.Target,
                        User = row.User,
                        Permission = "Mailbox",
                        Status = "FAILED",
                        Message = validationError
                    });
                    continue;
                }

                if (isAdd)
                {
                    var selfGrantError = await validator.ValidateSelfGrantAsync(currentUser, row.User);
                    if (selfGrantError is not null)
                    {
                        // Log self-grant attempt to audit log
                        audit.LogMailboxPermission(
                            currentUser, ipAddress, $"BulkAdd{permType}_SelfGrantAttempt",
                            row.Target, row.User, permType, false, ticketNumber, autoMap,
                            errorDetail: "Self-grant blocked: User attempted to grant permissions to themselves");

                        errors.Add($"{row.Target}/{row.User}: {selfGrantError}");
                        entries.Add(new BulkOperationEntry
                        {
                            Target = row.Target,
                            User = row.User,
                            Permission = "Mailbox",
                            Status = "FAILED",
                            Message = selfGrantError
                        });
                        continue;
                    }
                }

                var result = isAdd
                    ? await AddMailboxPermissionsAsync(row.Target, row.User, fullAccess, sendAs, autoMap)
                    : await RemoveMailboxPermissionsAsync(row.Target, row.User, fullAccess, sendAs);

                var action = $"Bulk{(isAdd ? "Add" : "Remove")}_{permType}";
                audit.LogMailboxPermission(
                    currentUser, ipAddress, action, row.Target, row.User, permType,
                    result.Success, ticketNumber, isAdd && fullAccess ? autoMap : null,
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
                audit.LogMailboxPermission(
                    currentUser, ipAddress, $"Bulk{(isAdd ? "Add" : "Remove")}_Error",
                    row.Target, row.User, "Mailbox", false, ticketNumber,
                    errorDetail: ex.Message);
                errors.Add($"{row.Target}/{row.User}: {ex.Message}");
                entries.Add(new BulkOperationEntry
                {
                    Target = row.Target,
                    User = row.User,
                    Permission = "Mailbox",
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

    public async Task<PermissionResult> AddMailboxPermissionsOnPremAsync(string targetMailbox, string user, bool fullAccess, bool sendAs)
    {
        var creds = await GetModuleCredentialsAsync("on-prem mailbox permission operation");
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

                var outcomes = new List<RightOutcome>();

                if (fullAccess)
                {
                    outcomes.Add(TryRightOnPrem(ps, "FullAccess", () =>
                    {
                        var script = ScriptBlock.Create("param($Identity, $User) Add-MailboxPermission -Identity $Identity -User $User -AccessRights FullAccess -Confirm:$false -ErrorAction Stop");
                        ps.AddCommand("Invoke-Command")
                          .AddParameter("Session", session)
                          .AddParameter("ScriptBlock", script)
                          .AddParameter("ArgumentList", new object[] { targetMailbox, user });
                        Invoke(ps);
                    }));
                }

                if (sendAs)
                {
                    outcomes.Add(TryRightOnPrem(ps, "SendAs", () =>
                    {
                        var script = ScriptBlock.Create("param($Identity, $Trustee) Add-ADPermission -Identity $Identity -User $Trustee -ExtendedRights 'Send As' -Confirm:$false -ErrorAction Stop");
                        ps.AddCommand("Invoke-Command")
                          .AddParameter("Session", session)
                          .AddParameter("ScriptBlock", script)
                          .AddParameter("ArgumentList", new object[] { targetMailbox, user });
                        Invoke(ps);
                    }));
                }

                return MailboxPermissionOutcome.ForGrant(targetMailbox, user, outcomes, onPrem: true);
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

    public async Task<PermissionResult> RemoveMailboxPermissionsOnPremAsync(string targetMailbox, string user, bool fullAccess, bool sendAs)
    {
        var creds = await GetModuleCredentialsAsync("on-prem mailbox permission operation");
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

                var outcomes = new List<RightOutcome>();

                if (fullAccess)
                {
                    outcomes.Add(TryRightOnPrem(ps, "FullAccess", () =>
                    {
                        var script = ScriptBlock.Create("param($Identity, $User) Remove-MailboxPermission -Identity $Identity -User $User -AccessRights FullAccess -Confirm:$false -ErrorAction Stop");
                        ps.AddCommand("Invoke-Command")
                          .AddParameter("Session", session)
                          .AddParameter("ScriptBlock", script)
                          .AddParameter("ArgumentList", new object[] { targetMailbox, user });
                        Invoke(ps);
                    }));
                }

                if (sendAs)
                {
                    outcomes.Add(TryRightOnPrem(ps, "SendAs", () =>
                    {
                        var script = ScriptBlock.Create("param($Identity, $Trustee) Remove-ADPermission -Identity $Identity -User $Trustee -ExtendedRights 'Send As' -Confirm:$false -ErrorAction Stop");
                        ps.AddCommand("Invoke-Command")
                          .AddParameter("Session", session)
                          .AddParameter("ScriptBlock", script)
                          .AddParameter("ArgumentList", new object[] { targetMailbox, user });
                        Invoke(ps);
                    }));
                }

                return MailboxPermissionOutcome.ForRevoke(targetMailbox, user, outcomes, onPrem: true);
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
