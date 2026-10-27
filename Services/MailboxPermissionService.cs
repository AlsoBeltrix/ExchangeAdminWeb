using System.Globalization;
using System.Management.Automation;
using System.Security.Claims;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using ExchangeAdminWeb.Models;
using System.Management.Automation.Runspaces;

namespace ExchangeAdminWeb.Services;

public class MailboxPermissionService : ExchangeServiceBase
{
    /// <summary>
    /// Module id for the servicer grant. Must match the catalog descriptor AND the page's own
    /// constant: PermissionValidator serves three modules, so a borrowed id would let a grant in
    /// one authorise the others.
    /// </summary>
    private const string ServicerModuleId = "MailboxPermissions";

    /// <summary>
    /// The detail recorded for a bulk row whose User is empty or blank (aud-a50); the Calendar Permissions loop records the
    /// same.
    /// </summary>
    internal const string BulkRowNamesNoUser = "No user specified (the User column is empty)";

    /// <summary>aud-a59. The error a bulk row that names no target is refused with, in both permission loops.</summary>
    internal const string BulkRowNamesNoTarget = "No target specified (the Target column is empty)";

    public MailboxPermissionService(ExoConnectionPool exoPool, DelineaService delineaService, ILogger<MailboxPermissionService> logger, IConfiguration config, ModuleCredentialService moduleCredentials, OperationTraceService operationTrace)
        : base(exoPool, delineaService, logger, config["OnPremExchange:ServerUri"] ?? "", moduleCredentials, "MailboxPermissions", operationTrace) { }

    /// <param name="targetMailbox">The operator's input, for messages and the result only.</param>
    /// <param name="mailbox">
    /// The mailbox the protected-principal check validated (aud-a2d) - the only identity written.
    /// Null refuses before a connection is borrowed; the typed string is never written.
    /// </param>
    public Task<PermissionResult> AddMailboxPermissionsAsync(string targetMailbox, ValidatedMailbox? mailbox, string user, bool fullAccess, bool sendAs, bool autoMapping)
    {
        if (mailbox is null)
            return Task.FromResult(PermissionResult.Fail(ValidatedMailbox.Refusal(targetMailbox)));

        // Each right is granted in its own try/catch and the successes/failures are
        // aggregated, mirroring the on-prem methods below. The previous RunAsync shape
        // reported the whole operation as a single Fail if SendAs threw after FullAccess
        // had already been granted, hiding the applied grant from the bulk audit row and
        // CSV report (and inviting a "retry" that re-runs both). Invoke(ps, tracker) still
        // flags connection errors before throwing, so RunPooledQueryAsync discards a
        // poisoned pooled connection even though the per-right catch swallows the error.
        // aud-a76: so the one throw that leaves is the pool's - its borrow, before the mark below - and it leaves as a
        // PermissionWriteNotSubmittedException: nothing was sent.
        var submission = new PermissionWriteSubmission();
        return submission.TrackAsync(() => RunPooledQueryAsync((ps, tracker) =>
        {
            try
            {
                BindValidatedMailbox(ps, tracker, mailbox);
                ValidateRecipient(ps, user);
            }
            catch (Exception ex)
            {
                return PermissionResult.Fail(ex.Message);
            }

            var outcomes = new List<RightOutcome>();
            submission.Submitting(); // aud-a76: the first write command is invoked below

            if (fullAccess)
            {
                outcomes.Add(TryRight(tracker, "FullAccess", own =>
                {
                    ps.AddCommand("Add-MailboxPermission")
                      .AddParameter("Identity", mailbox.Identity)
                      .AddParameter("User", user)
                      .AddParameter("AccessRights", "FullAccess")
                      .AddParameter("AutoMapping", autoMapping)
                      .AddParameter("Confirm", false);
                    Invoke(ps, own);
                }));
            }

            if (sendAs)
            {
                outcomes.Add(TryRight(tracker, "SendAs", own =>
                {
                    ps.AddCommand("Add-RecipientPermission")
                      .AddParameter("Identity", mailbox.Identity)
                      .AddParameter("Trustee", user)
                      .AddParameter("AccessRights", "SendAs")
                      .AddParameter("Confirm", false);
                    Invoke(ps, own);
                }));
            }

            return MailboxPermissionOutcome.ForGrant(targetMailbox, user, outcomes);
        }));
    }

    /// <param name="mailbox">As on <see cref="AddMailboxPermissionsAsync"/>: null refuses.</param>
    public Task<PermissionResult> RemoveMailboxPermissionsAsync(string targetMailbox, ValidatedMailbox? mailbox, string user, bool fullAccess, bool sendAs)
    {
        if (mailbox is null)
            return Task.FromResult(PermissionResult.Fail(ValidatedMailbox.Refusal(targetMailbox)));

        // Per-right try/catch with aggregation - see AddMailboxPermissionsAsync for why
        // the previous single-Fail RunAsync shape lost partial-success information, and for
        // the mark (aud-a76).
        var submission = new PermissionWriteSubmission();
        return submission.TrackAsync(() => RunPooledQueryAsync((ps, tracker) =>
        {
            try
            {
                BindValidatedMailbox(ps, tracker, mailbox);
                ValidateRecipient(ps, user);
            }
            catch (Exception ex)
            {
                return PermissionResult.Fail(ex.Message);
            }

            var outcomes = new List<RightOutcome>();
            submission.Submitting(); // aud-a76: the first write command is invoked below

            if (fullAccess)
            {
                outcomes.Add(TryRight(tracker, "FullAccess", own =>
                {
                    ps.AddCommand("Remove-MailboxPermission")
                      .AddParameter("Identity", mailbox.Identity)
                      .AddParameter("User", user)
                      .AddParameter("AccessRights", "FullAccess")
                      .AddParameter("Confirm", false);
                    Invoke(ps, own);
                }));
            }

            if (sendAs)
            {
                outcomes.Add(TryRight(tracker, "SendAs", own =>
                {
                    ps.AddCommand("Remove-RecipientPermission")
                      .AddParameter("Identity", mailbox.Identity)
                      .AddParameter("Trustee", user)
                      .AddParameter("AccessRights", "SendAs")
                      .AddParameter("Confirm", false);
                    Invoke(ps, own);
                }));
            }

            return MailboxPermissionOutcome.ForRevoke(targetMailbox, user, outcomes);
        }));
    }

    /// <summary>
    /// Applies one right and records whether it took. A throw is captured, never propagated, so a
    /// later right is still attempted and the caller can report a partial result.
    /// </summary>
    /// <remarks>
    /// <c>Invoke(ps, tracker)</c> still flags connection errors before throwing, so a poisoned
    /// pooled connection is discarded by <c>RunPooledQueryAsync</c> even though the failure is
    /// swallowed here: what the right's own invoke flags is carried to the operation's tracker.
    /// aud-a70 (14): and read for the right itself - a failure its session caused once the command was sent leaves the right
    /// unconfirmed (<see cref="ExchangeServiceBase.OutcomeUnknown"/>), where Exchange's own refusal is a failed right.
    /// </remarks>
    private static RightOutcome TryRight(ConnectionErrorTracker operation, string right, Action<ConnectionErrorTracker> apply)
    {
        var own = new ConnectionErrorTracker();
        try
        {
            apply(own);
            return RightOutcome.Ok(right);
        }
        catch (Exception ex)
        {
            return OutcomeUnknown(ex, own) ? RightOutcome.Unanswered(right, ex.Message) : RightOutcome.Failed(right, ex.Message);
        }
        finally
        {
            operation.HasConnectionError |= own.HasConnectionError;
            operation.HasRetriablePrecheckError |= own.HasRetriablePrecheckError;
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
    /// aud-a70 (14): the right's invoke classifies its own failure, so a session that broke once its Invoke-Command was sent
    /// leaves the right unconfirmed, where Exchange's own refusal is a failed right.
    /// </remarks>
    private static RightOutcome TryRightOnPrem(PowerShell ps, string right, Action<ConnectionErrorTracker> apply)
    {
        var sent = new ConnectionErrorTracker();
        try
        {
            apply(sent);
            return RightOutcome.Ok(right);
        }
        catch (Exception ex)
        {
            ps.Commands.Clear();
            ps.Streams.Error.Clear();
            return OutcomeUnknown(ex, sent) ? RightOutcome.Unanswered(right, ex.Message) : RightOutcome.Failed(right, ex.Message);
        }
    }

    /// <param name="actingUser">
    /// REQUIRED and not defaulted: the protected-principal servicer decision needs a real
    /// principal. Bulk is where a servicer grant is MOST useful and it was the path that could not
    /// use it - the single-target form serviced a protected mailbox while the same mailbox in a CSV
    /// was refused (pps-1). Null refuses, so any caller without a principal is safe.
    /// aud-a43: the self-grant check is handed it too, and finds the operator in the directory by it;
    /// null refuses every grant row there as a check that could not be completed.
    /// </param>
    public async Task<BulkOperationResult> ProcessMailboxPermissionsCsvAsync(Stream csvStream, bool isAdd, PermissionValidator validator, string currentUser, AuditService audit, EmailService email, string ipAddress, string ticketNumber, ClaimsPrincipal? actingUser)
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

        // aud-a61. One run of the self-grant check for the whole file: a group several rows name is read once - and a read
        // that failed is that failure for each of them.
        var selfGrantRun = new SelfGrantRun();

        foreach (var row in records)
        {
            try
            {
                // aud-a59. A row that names no target is an invalid row too, refused first: it went on to the protected-target
                // check with '' and was recorded under that check's action.
                if (string.IsNullOrWhiteSpace(row.Target))
                {
                    audit.LogMailboxPermission(
                        currentUser, ipAddress, $"Bulk{(isAdd ? "Add" : "Remove")}_NoTarget",
                        row.Target, row.User, "Mailbox", false, ticketNumber,
                        errorDetail: BulkRowNamesNoTarget);
                    errors.Add($"{row.Target}/{row.User}: {BulkRowNamesNoTarget}");
                    entries.Add(new BulkOperationEntry
                    {
                        Target = row.Target,
                        User = row.User,
                        Permission = "Mailbox",
                        Status = "FAILED",
                        Message = "No target specified"
                    });
                    continue;
                }

                // aud-a50. A row that names no user is an invalid row, refused before any check: the self-grant check put ''
                // to Exchange and the directory and recorded the row as a self-grant attempt, and a revocation went on to the
                // write. It is recorded as what it is, and the next row is processed.
                if (string.IsNullOrWhiteSpace(row.User))
                {
                    audit.LogMailboxPermission(
                        currentUser, ipAddress, $"Bulk{(isAdd ? "Add" : "Remove")}_NoUser",
                        row.Target, row.User, "Mailbox", false, ticketNumber,
                        errorDetail: BulkRowNamesNoUser);
                    errors.Add($"{row.Target}/{row.User}: {BulkRowNamesNoUser}");
                    entries.Add(new BulkOperationEntry
                    {
                        Target = row.Target,
                        User = row.User,
                        Permission = "Mailbox",
                        Status = "FAILED",
                        Message = "No user specified"
                    });
                    continue;
                }

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

                // Validate target is not excluded. The servicing overload with an explicit module
                // id: the back-compat overload cannot service by construction, which is what made
                // a servicer's CSV row refuse while their single-target submit succeeded (pps-1).
                var targetValidation = await validator.ValidateTargetMailboxAsync(
                    row.Target, actingUser, ServicerModuleId);
                var servicedNote = targetValidation.ServicedNote;
                var validationError = targetValidation.Error;
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
                    // aud-a43: the signed-in principal, never currentUser - the logon name is the record's actor only.
                    var selfGrantError = await validator.ValidateSelfGrantAsync(actingUser, row.User, selfGrantRun);
                    if (selfGrantError is not null)
                    {
                        // The record carries the validator's own refusal: a check that could not be completed is not a self-grant (aud-a28y).
                        audit.LogMailboxPermission(
                            currentUser, ipAddress, $"BulkAdd{permType}_SelfGrantAttempt",
                            row.Target, row.User, permType, false, ticketNumber, autoMap,
                            errorDetail: selfGrantError);

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

                // aud-a2d: this ROW's validated mailbox, never row.Target - the row is written to the
                // mailbox its own check resolved.
                var result = isAdd
                    ? await AddMailboxPermissionsAsync(row.Target, targetValidation.Mailbox, row.User, fullAccess, sendAs, autoMap)
                    : await RemoveMailboxPermissionsAsync(row.Target, targetValidation.Mailbox, row.User, fullAccess, sendAs);

                var action = $"Bulk{(isAdd ? "Add" : "Remove")}_{permType}";
                // Per row, because servicing is decided per row: a batch can mix serviced and
                // ordinary targets. The note rides extra - a serviced row SUCCEEDS, and errorDetail
                // is written as null on success, so the record of who permitted it would vanish.
                // The identity written rides beside it; `target` stays the row as typed.
                audit.LogMailboxPermission(
                    currentUser, ipAddress, action, row.Target, row.User, permType,
                    result.Success, ticketNumber, isAdd && fullAccess ? autoMap : null,
                    errorDetail: result.Success ? null : result.Message,
                    extra: ValidatedMailbox.WithWrittenIdentity(ProtectedPrincipalServicing.Extra(servicedNote), targetValidation.Mailbox),
                    filedAs: PermissionChangeFailure.FiledAs(result)); // aud-a70 (14): a right whose outcome is unknown files the row Undetermined

                // Audit finding A24. The row was audited and the operator's administrators got
                // a summary; the trustee and the mailbox owner got nothing. AppliedRights rather
                // than result.Success so a partial row notifies on the part that took (A23).
                await PermissionChangeNotifier.NotifyAffectedAsync(
                    email, result.AppliedRights ?? [], row.Target, row.Target, row.User,
                    currentUser, isAdd, _logger);

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

    /// <summary>
    /// The on-premises grant, bound to the validated mailbox's GUID (aud-a2d). Null refuses before a
    /// credential is requested.
    /// </summary>
    /// <remarks>
    /// Send As goes through <c>Add-ADPermission</c>, whose <c>-Identity</c> Microsoft documents as a
    /// DN or a unique name only - not a GUID. So the GUID is resolved by <c>Get-Mailbox</c> (a
    /// mailbox identity, which a GUID is) and the mailbox object is piped in, binding the
    /// permission to that object rather than to any string. One pipeline, so the remote endpoint's
    /// restricted language mode can still run it - the same shape <c>OnPremRecipientExists</c>
    /// uses. That the pipeline binds there is an assumption recorded in aud-a2d.
    /// </remarks>
    /// <param name="cancellationToken">
    /// kd-59. The page's activity token, for the credential fetch (kd-37's seam): a cancel during it leaves, as every throw
    /// before the mark does, as a <see cref="PermissionWriteNotSubmittedException"/> - nothing was sent - with the
    /// cancellation as its cause. Nothing after the fetch takes it: once the credential is in hand the write runs as before.
    /// </param>
    public async Task<PermissionResult> AddMailboxPermissionsOnPremAsync(string targetMailbox, ValidatedMailbox? mailbox, string user, bool fullAccess, bool sendAs,
        CancellationToken cancellationToken = default)
    {
        if (mailbox is null)
            return PermissionResult.Fail(ValidatedMailbox.Refusal(targetMailbox));

        // aud-a76: the credentials, the throttle, the runspace, the connect and the session read come before the mark, so a
        // throw from any of them leaves as a PermissionWriteNotSubmittedException: nothing was sent. Each right's own
        // failure is a result (TryRightOnPrem), as before.
        var submission = new PermissionWriteSubmission();
        var creds = await submission.TrackAsync(() => GetModuleCredentialsAsync("on-prem mailbox permission operation", cancellationToken)); // kd-59
        if (creds is null)
            return PermissionResult.Fail("Cannot connect to on-prem Exchange: credentials unavailable.");

        return await submission.TrackAsync(() => ThrottledAsync(async () => await Task.Run(() =>
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
                submission.Submitting(); // aud-a76: the first Invoke-Command is sent below

                var outcomes = new List<RightOutcome>();

                if (fullAccess)
                {
                    outcomes.Add(TryRightOnPrem(ps, "FullAccess", sent =>
                    {
                        var script = ScriptBlock.Create("param($Identity, $User) Add-MailboxPermission -Identity $Identity -User $User -AccessRights FullAccess -Confirm:$false -ErrorAction Stop");
                        ps.AddCommand("Invoke-Command")
                          .AddParameter("Session", session)
                          .AddParameter("ScriptBlock", script)
                          .AddParameter("ArgumentList", new object[] { mailbox.Identity, user });
                        Invoke(ps, sent);
                    }));
                }

                if (sendAs)
                {
                    outcomes.Add(TryRightOnPrem(ps, "SendAs", sent =>
                    {
                        var script = ScriptBlock.Create("param($Identity, $Trustee) Get-Mailbox -Identity $Identity -ErrorAction Stop | Add-ADPermission -User $Trustee -ExtendedRights 'Send As' -Confirm:$false -ErrorAction Stop");
                        ps.AddCommand("Invoke-Command")
                          .AddParameter("Session", session)
                          .AddParameter("ScriptBlock", script)
                          .AddParameter("ArgumentList", new object[] { mailbox.Identity, user });
                        Invoke(ps, sent);
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
        }), _onPremThrottle));
    }

    /// <summary>The on-premises revoke; bound exactly as <see cref="AddMailboxPermissionsOnPremAsync"/>, its token too (kd-59).</summary>
    public async Task<PermissionResult> RemoveMailboxPermissionsOnPremAsync(string targetMailbox, ValidatedMailbox? mailbox, string user, bool fullAccess, bool sendAs,
        CancellationToken cancellationToken = default)
    {
        if (mailbox is null)
            return PermissionResult.Fail(ValidatedMailbox.Refusal(targetMailbox));

        // aud-a76: marked as AddMailboxPermissionsOnPremAsync is.
        var submission = new PermissionWriteSubmission();
        var creds = await submission.TrackAsync(() => GetModuleCredentialsAsync("on-prem mailbox permission operation", cancellationToken)); // kd-59
        if (creds is null)
            return PermissionResult.Fail("Cannot connect to on-prem Exchange: credentials unavailable.");

        return await submission.TrackAsync(() => ThrottledAsync(async () => await Task.Run(() =>
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
                submission.Submitting(); // aud-a76: the first Invoke-Command is sent below

                var outcomes = new List<RightOutcome>();

                if (fullAccess)
                {
                    outcomes.Add(TryRightOnPrem(ps, "FullAccess", sent =>
                    {
                        var script = ScriptBlock.Create("param($Identity, $User) Remove-MailboxPermission -Identity $Identity -User $User -AccessRights FullAccess -Confirm:$false -ErrorAction Stop");
                        ps.AddCommand("Invoke-Command")
                          .AddParameter("Session", session)
                          .AddParameter("ScriptBlock", script)
                          .AddParameter("ArgumentList", new object[] { mailbox.Identity, user });
                        Invoke(ps, sent);
                    }));
                }

                if (sendAs)
                {
                    outcomes.Add(TryRightOnPrem(ps, "SendAs", sent =>
                    {
                        var script = ScriptBlock.Create("param($Identity, $Trustee) Get-Mailbox -Identity $Identity -ErrorAction Stop | Remove-ADPermission -User $Trustee -ExtendedRights 'Send As' -Confirm:$false -ErrorAction Stop");
                        ps.AddCommand("Invoke-Command")
                          .AddParameter("Session", session)
                          .AddParameter("ScriptBlock", script)
                          .AddParameter("ArgumentList", new object[] { mailbox.Identity, user });
                        Invoke(ps, sent);
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
        }), _onPremThrottle));
    }
}
