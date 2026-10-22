using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using ExchangeAdminWeb.Models.AccountLockoutRemediation;
using Microsoft.AspNetCore.Authorization;

namespace ExchangeAdminWeb.Services;

public sealed class AccountLockoutRemediationService
{
    private const string ModuleId = "AccountLockoutRemediation";
    private const string AccessPolicy = "AccountLockoutRemediation";
    private const string LogoffPolicy = "AccountLockoutRemediationLogoff";

    private readonly ModuleCredentialService _moduleCredentials;
    private readonly ModuleConfigService _moduleConfig;
    private readonly ProtectedPrincipalService _protectedPrincipals;
    private readonly ProtectedPrincipalServicerService _servicers;
    private readonly IAuthorizationService _authorization;
    private readonly AuditService _audit;
    private readonly EmailService _email;
    private readonly OperationTraceService _operationTrace;
    private readonly ILogger<AccountLockoutRemediationService> _logger;

    public AccountLockoutRemediationService(
        ModuleCredentialService moduleCredentials,
        ModuleConfigService moduleConfig,
        ProtectedPrincipalService protectedPrincipals,
        ProtectedPrincipalServicerService servicers,
        IAuthorizationService authorization,
        AuditService audit,
        EmailService email,
        OperationTraceService operationTrace,
        ILogger<AccountLockoutRemediationService> logger)
    {
        _moduleCredentials = moduleCredentials;
        _moduleConfig = moduleConfig;
        _protectedPrincipals = protectedPrincipals;
        _servicers = servicers;
        _authorization = authorization;
        _audit = audit;
        _email = email;
        _operationTrace = operationTrace;
        _logger = logger;
        SidReader = ReadObjectSid;
    }

    public async Task<AccountLockoutDiscoveryResult> DiscoverLockoutSourcesAsync(
        AccountLockoutSourceRequest request,
        AccountLockoutOperatorContext context)
    {
        using var scope = _operationTrace.BeginOperation(
            ModuleId,
            "DiscoverLockoutSources",
            context.DisplayName,
            context.IpAddress,
            details: new Dictionary<string, object?>
            {
                ["withinHours"] = request.WithinHours,
                ["userCount"] = request.Users.Length,
                ["domainControllerCount"] = request.DomainControllers.Length
            });

        try
        {
            var access = await _authorization.AuthorizeAsync(context.Principal, AccessPolicy);
            if (!access.Succeeded)
            {
                const string message = "You are not authorized to access account lockout remediation.";
                _operationTrace.Step("Authorize", "Failed", details: new Dictionary<string, object?> { ["policy"] = AccessPolicy });
                scope.Complete(false, message);
                return new AccountLockoutDiscoveryResult(false, message, [], [], []);
            }

            if (_moduleConfig.IsModuleCorrupt(ModuleId))
            {
                const string message = "Module configuration is corrupt. Account lockout remediation is unavailable.";
                _operationTrace.Step("ConfigCheck", "Failed");
                scope.Complete(false, message);
                return new AccountLockoutDiscoveryResult(false, message, [], [], []);
            }

            var credentials = await GetCredentialsAsync("read lockout events");
            if (credentials == null)
            {
                const string message = "Module credentials are not configured or unavailable.";
                _operationTrace.Step("CredentialLookup", "Failed", backend: "Delinea");
                scope.Complete(false, message);
                return new AccountLockoutDiscoveryResult(false, message, [], [], []);
            }

            var normalizedUsers = NormalizeUsers(request.Users);
            var dcs = request.DomainControllers.Length > 0
                ? NormalizeNames(request.DomainControllers)
                : await DiscoverPdcAsync(credentials.Value);

            if (dcs.Length == 0)
            {
                const string message = "No domain controller was supplied or discovered.";
                scope.Complete(false, message);
                return new AccountLockoutDiscoveryResult(false, message, [], [], []);
            }

            _operationTrace.Step("ReadLockoutEvents", backend: "ActiveDirectory", command: "Get-WinEvent");
            var read = await Task.Run(() => ReadLockoutEvents(dcs, normalizedUsers, request.WithinHours, credentials.Value));
            var actionable = read.events.Where(e => e.Actionable).ToArray();
            var machines = actionable.Select(e => e.SourceMachine).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray();

            var success = read.failures.Count == 0 || read.events.Count > 0;
            var summary = read.events.Count == 0
                ? "No matching lockout events were found."
                : $"Found {read.events.Count} matching lockout event(s), {actionable.Length} actionable.";

            _audit.LogLookupAction(
                context.DisplayName,
                context.IpAddress,
                "DiscoverLockoutSources",
                normalizedUsers.Length > 0 ? string.Join(",", normalizedUsers) : "(all locked accounts)",
                success,
                read.failures.Count == 0 ? null : $"{read.failures.Count} domain controller read failure(s).");

            scope.Complete(success, summary);
            return new AccountLockoutDiscoveryResult(success, summary, read.events, machines, read.failures);
        }
        catch (Exception ex)
        {
            const string message = "Lockout source discovery failed. Check operation trace for details.";
            _logger.LogWarning(ex, "Account lockout source discovery failed");
            _operationTrace.Step("DiscoverLockoutSources", "Failed", backend: "ActiveDirectory", exception: ex);
            scope.Complete(false, message, ex);
            return new AccountLockoutDiscoveryResult(false, message, [], [], [SafeError(ex)]);
        }
    }

    public async Task<AccountLogoffResult> LogoffLockoutSourcesAsync(
        AccountLockoutLogoffRequest request,
        AccountLockoutOperatorContext context)
    {
        var result = await LogoffLockoutSourcesCoreAsync(request, context);
        await NotifyLogoffAdminAsync("AccountLockout_LogoffSources", request, null, result, context);
        return result;
    }

    private async Task<AccountLogoffResult> LogoffLockoutSourcesCoreAsync(
        AccountLockoutLogoffRequest request,
        AccountLockoutOperatorContext context)
    {
        var discovery = await DiscoverLockoutSourcesAsync(
            new AccountLockoutSourceRequest(request.Users, request.WithinHours, request.DomainControllers, request.ThrottleLimit),
            context);

        if (!discovery.Success || discovery.Events.Count == 0)
            return new AccountLogoffResult(false, discovery.Message, request.Execute, 0, 0, discovery.ReadFailures.Count, []);

        var machineMap = discovery.Events
            .Where(e => e.Actionable)
            .GroupBy(e => e.SourceMachine, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Select(e => e.User).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                StringComparer.OrdinalIgnoreCase);

        foreach (var blank in discovery.Events.Where(e => !e.Actionable))
        {
            machineMap.TryAdd("(unknown)", []);
        }

        return await RunLogoffAsync(
            "LogoffFromLockoutSources",
            machineMap,
            request.Execute,
            request.TicketNumber,
            request.ThrottleLimit,
            context,
            includeBlankSourceRows: discovery.Events.Where(e => !e.Actionable).ToArray());
    }

    public async Task<AccountLogoffResult> SweepScopedComputersAsync(
        AccountScopedLogoffRequest request,
        AccountLockoutOperatorContext context)
    {
        var result = await SweepScopedComputersCoreAsync(request, context);
        await NotifyLogoffAdminAsync("AccountLockout_SweepScoped", null, request, result, context);
        return result;
    }

    private async Task<AccountLogoffResult> SweepScopedComputersCoreAsync(
        AccountScopedLogoffRequest request,
        AccountLockoutOperatorContext context)
    {
        using var scope = _operationTrace.BeginOperation(
            ModuleId,
            "SweepScopedComputers",
            context.DisplayName,
            context.IpAddress,
            ticket: request.TicketNumber,
            details: new Dictionary<string, object?>
            {
                ["execute"] = request.Execute,
                ["userCount"] = request.Users.Length,
                ["hasSearchBase"] = !string.IsNullOrWhiteSpace(request.SearchBase),
                ["extraComputerCount"] = request.ExtraComputers.Length
            });

        try
        {
            var users = NormalizeUsers(request.Users);
            if (users.Length == 0)
            {
                const string message = "At least one target user is required.";
                scope.Complete(false, message);
                return EmptyLogoffResult(message, request.Execute);
            }

            if (string.IsNullOrWhiteSpace(request.SearchBase) && request.ExtraComputers.Length == 0)
            {
                const string message = "Supply a search base, extra computer list, or both.";
                scope.Complete(false, message);
                return EmptyLogoffResult(message, request.Execute);
            }

            var access = await _authorization.AuthorizeAsync(context.Principal, AccessPolicy);
            if (!access.Succeeded)
            {
                const string message = "You are not authorized to access account lockout remediation.";
                scope.Complete(false, message);
                return EmptyLogoffResult(message, request.Execute);
            }

            if (_moduleConfig.IsModuleCorrupt(ModuleId))
            {
                const string message = "Module configuration is corrupt. Account lockout remediation is unavailable.";
                scope.Complete(false, message);
                return EmptyLogoffResult(message, request.Execute);
            }

            var credentials = await GetCredentialsAsync("enumerate scoped computers");
            if (credentials == null)
            {
                const string message = "Module credentials are not configured or unavailable.";
                _operationTrace.Step("CredentialLookup", "Failed", backend: "Delinea");
                scope.Complete(false, message);
                return EmptyLogoffResult(message, request.Execute);
            }

            _operationTrace.Step("ResolveScopedComputers", backend: "ActiveDirectory", command: "Get-ADComputer");
            var computers = await Task.Run(() => ResolveComputers(request.SearchBase, request.ExtraComputers, credentials.Value));
            var maxTargets = GetMaxSweepTargets();
            if (maxTargets > 0 && computers.Length > maxTargets)
            {
                var message = $"Scoped sweep resolved {computers.Length} computers, above the configured limit of {maxTargets}.";
                scope.Complete(false, message);
                return EmptyLogoffResult(message, request.Execute);
            }

            var machineMap = computers.ToDictionary(c => c, _ => users, StringComparer.OrdinalIgnoreCase);
            scope.Complete(true, $"Resolved {computers.Length} computer(s).");

            return await RunLogoffAsync(
                "SweepScopedComputers",
                machineMap,
                request.Execute,
                request.TicketNumber,
                request.ThrottleLimit,
                context,
                includeBlankSourceRows: []);
        }
        catch (Exception ex)
        {
            const string message = "Scoped logoff sweep failed. Check operation trace for details.";
            _logger.LogWarning(ex, "Scoped account logoff sweep failed");
            _operationTrace.Step("SweepScopedComputers", "Failed", backend: "ActiveDirectory", exception: ex);
            scope.Complete(false, message, ex);
            return new AccountLogoffResult(false, message, request.Execute, 0, 0, 1, [
                new ComputerSessionActionRow("", "", "", "", "failed", false, SafeError(ex))
            ]);
        }
    }

    public int GetDefaultThrottleLimit() => ClampThrottle(ReadIntConfig("DefaultThrottleLimit", 32));

    private async Task<AccountLogoffResult> RunLogoffAsync(
        string action,
        Dictionary<string, string[]> machineMap,
        bool execute,
        string ticketNumber,
        int requestedThrottle,
        AccountLockoutOperatorContext context,
        IReadOnlyList<AccountLockoutEventRow> includeBlankSourceRows)
    {
        using var scope = _operationTrace.BeginOperation(
            ModuleId,
            action,
            context.DisplayName,
            context.IpAddress,
            ticket: ticketNumber,
            details: new Dictionary<string, object?>
            {
                ["execute"] = execute,
                ["machineCount"] = machineMap.Count,
                ["blankSourceRows"] = includeBlankSourceRows.Count
            });

        try
        {
            var realMachines = machineMap.Where(kvp => kvp.Key != "(unknown)").ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);
            if (realMachines.Count == 0 && includeBlankSourceRows.Count == 0)
            {
                const string message = "No actionable machines were found.";
                scope.Complete(true, message);
                return new AccountLogoffResult(true, message, execute, 0, 0, 0, []);
            }

            if (execute && string.IsNullOrWhiteSpace(ticketNumber))
            {
                const string message = "Ticket Number is required for logoff execution.";
                scope.Complete(false, message);
                return EmptyLogoffResult(message, execute);
            }

            if (execute)
            {
                var auth = await _authorization.AuthorizeAsync(context.Principal, LogoffPolicy);
                if (!auth.Succeeded)
                {
                    const string message = "You are not authorized to log users off.";
                    AuditLogoff(context, action, "(multiple)", false, ticketNumber, "Authorization denied.", new Dictionary<string, object?> { ["execute"] = true });
                    _operationTrace.Step("AuthorizeMutation", "Failed", details: new Dictionary<string, object?> { ["policy"] = LogoffPolicy });
                    scope.Complete(false, message);
                    return EmptyLogoffResult(message, execute);
                }
            }

            var credentials = await GetCredentialsAsync(execute ? "log off computer sessions" : "query computer sessions");
            if (credentials == null)
            {
                const string message = "Module credentials are not configured or unavailable.";
                _operationTrace.Step("CredentialLookup", "Failed", backend: "Delinea");
                scope.Complete(false, message);
                return EmptyLogoffResult(message, execute);
            }

            // Null on the dry-run path, which never runs the guard. The SID step then has no
            // approved object to compare against, and says so on the finding rather than
            // pretending the cross-check happened.
            Dictionary<string, ResolvedDirectoryPrincipal>? approvedPrincipals = null;

            if (execute)
            {
                var guard = await GuardTargetUsersAsync(realMachines, context, ticketNumber, action);
                realMachines = guard.machineMap;
                approvedPrincipals = guard.approved;
                if (guard.rows.Count > 0 && realMachines.Count == 0)
                {
                    var blocked = guard.rows.Count(r => !r.Success);
                    var message = $"All target users were blocked before logoff. {blocked} blocked row(s).";
                    scope.Complete(false, message);
                    return new AccountLogoffResult(false, message, true, 0, 0, blocked, guard.rows);
                }

                var writeAuth = await _authorization.AuthorizeAsync(context.Principal, LogoffPolicy);
                if (!writeAuth.Succeeded)
                {
                    const string message = "You are not authorized to log users off.";
                    AuditLogoff(context, action, "(multiple)", false, ticketNumber, "Authorization denied before remote logoff.", new Dictionary<string, object?> { ["execute"] = true });
                    _operationTrace.Step("AuthorizeBeforeRemoteLogoff", "Failed", details: new Dictionary<string, object?> { ["policy"] = LogoffPolicy });
                    scope.Complete(false, message);
                    return EmptyLogoffResult(message, execute);
                }
            }

            // Audit finding A5. Both paths, not just the destructive one: a dry run that named
            // the wrong person's session is what an operator reads before deciding to execute.
            _operationTrace.Step("ResolveTargetSids", backend: "ActiveDirectory", command: "Get-ADUser");
            var sidMapping = await MapTargetsToSidsAsync(
                realMachines, context, ticketNumber, action, credentials.Value, execute, approvedPrincipals);
            realMachines = sidMapping.machineMap;

            if (realMachines.Count == 0)
            {
                // Review of aud-a5, finding 5. This return used to hand back sidMapping.rows
                // alone, which skipped the blank-source explanations appended below - so a
                // discovery run that found only events with no caller computer reported
                // nothing at all to explain itself. The rows are assembled the same way the
                // main path assembles them.
                var earlyRows = sidMapping.rows.ToList();
                earlyRows.AddRange(includeBlankSourceRows.Select(BlankSourceRow));
                var blocked = earlyRows.Count(r => !r.Success);
                var message = $"No target user could be matched to a directory SID. {blocked} blocked row(s).";
                scope.Complete(false, message);
                return new AccountLogoffResult(false, message, execute, 0, 0, blocked, earlyRows);
            }

            _operationTrace.Step(execute ? "RemoteLogoff" : "RemoteSessionQuery", backend: "WinRM", command: execute ? "logoff.exe" : "quser.exe");
            var throttle = ClampThrottle(requestedThrottle <= 0 ? GetDefaultThrottleLimit() : requestedThrottle);
            var rows = await Task.Run(() => QueryAndMaybeLogoff(realMachines, execute, throttle, credentials.Value, sidMapping.sidNames));
            var allRows = rows.ToList();
            allRows.AddRange(sidMapping.rows);

            allRows.AddRange(includeBlankSourceRows.Select(BlankSourceRow));

            var hits = allRows.Count(r => r.Action is "logged-off" or "would-log-off");
            var failures = allRows.Count(r => !r.Success);
            var success = failures == 0 || hits > 0;
            var summary = execute
                ? $"Logoff completed with {hits} session(s) logged off and {failures} failure row(s)."
                : $"Dry run found {hits} session(s) to log off and {failures} failure row(s).";

            AuditLogoff(context, action, "(multiple)", success, ticketNumber, success ? null : summary, new Dictionary<string, object?>
            {
                ["execute"] = execute,
                ["machineCount"] = realMachines.Count,
                ["sessionHits"] = hits,
                ["failureRows"] = failures
            });

            scope.Complete(success, summary);
            return new AccountLogoffResult(success, summary, execute, realMachines.Count, hits, failures, allRows);
        }
        catch (Exception ex)
        {
            const string message = "Session query/logoff failed. Check operation trace for details.";
            _logger.LogWarning(ex, "Account logoff operation failed");
            _operationTrace.Step(action, "Failed", backend: "WinRM", exception: ex);
            AuditLogoff(context, action, "(multiple)", false, ticketNumber, SafeError(ex), new Dictionary<string, object?> { ["execute"] = execute });
            scope.Complete(false, message, ex);
            return new AccountLogoffResult(false, message, execute, machineMap.Count, 0, 1, [
                new ComputerSessionActionRow("", "", "", "", "failed", false, SafeError(ex))
            ]);
        }
    }

    /// <remarks>
    /// Internal rather than private as a TEST SEAM (the project already exposes internals to
    /// ExchangeAdminWeb.Tests). RunLogoffAsync fetches an AD credential and reaches a live
    /// directory before this guard runs, so no test can drive the servicer path through the public
    /// methods. Exposing the guard is what makes the decision testable; it stays a filter plus its
    /// audit rows, with no directory write of its own.
    /// </remarks>
    internal async Task<(Dictionary<string, string[]> machineMap, List<ComputerSessionActionRow> rows, Dictionary<string, ResolvedDirectoryPrincipal> approved)> GuardTargetUsersAsync(
        Dictionary<string, string[]> machineMap,
        AccountLockoutOperatorContext context,
        string ticketNumber,
        string action)
    {
        var allUsers = machineMap.Values.SelectMany(x => x).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var allowedUsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Review of aud-a5, finding 3: the object that passed the check travels on with the
        // name, so the SID step can prove it is still acting on the same object.
        var approved = new Dictionary<string, ResolvedDirectoryPrincipal>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<ComputerSessionActionRow>();

        foreach (var user in allUsers)
        {
            // Exchange fallback, not AD-only. NOTE: unlike the other GAP B sites this one was
            // already fail-CLOSED - an unresolved identity is blocked at the next line, not allowed
            // - so this is not closing a bypass. It stops cloud-only users being refused as
            // "could not be resolved" when they are perfectly resolvable through Exchange.
            // docs/ProtectedPrincipalGapFix-Plan.md GAP B.
            var firstResolution = await _protectedPrincipals.ResolveWithExchangeFallbackAsync(user);
            if (firstResolution.status != ProtectedPrincipalService.ResolutionStatus.Resolved || firstResolution.principal == null)
            {
                var detail = firstResolution.status == ProtectedPrincipalService.ResolutionStatus.Ambiguous
                    ? "Target identity is ambiguous."
                    : "Target identity could not be resolved for protected-principal enforcement.";
                rows.Add(BlockedUserRow(user, "identity-blocked", detail));
                AuditLogoff(context, action, user, false, ticketNumber, detail, new Dictionary<string, object?> { ["blockedBeforeWrite"] = true });
                continue;
            }

            var protection = await _protectedPrincipals.CheckAsync(firstResolution.principal);
            if (protection.CheckFailed)
            {
                rows.Add(BlockedUserRow(user, "protection-check-failed", protection.Reason));
                AuditLogoff(context, action, user, false, ticketNumber, protection.Reason, new Dictionary<string, object?> { ["protectedCheckFailed"] = true });
                continue;
            }

            // An authorised servicer may proceed. Protection is evaluated first and never weakened -
            // this only decides whether THIS operator may act on a user already known to be
            // protected. The blocked paths above stay blocked: an unresolved, ambiguous or failed
            // check does not know whether the user is protected, so there is no refusal to override.
            //
            // context.Principal is the operator on the circuit, the same identity the module's own
            // authorization checks use, so a null principal cannot reach here to be serviced.
            string? servicedNote = null;
            if (protection.IsProtected)
            {
                servicedNote = ProtectedPrincipalServicing.NoteFor(
                    _servicers, context.Principal, ModuleId, protection.MatchedRules, qualifier: user);

                if (servicedNote is null)
                {
                    rows.Add(BlockedUserRow(user, "protected-principal", protection.Reason));
                    AuditLogoff(context, action, user, false, ticketNumber, protection.Reason, new Dictionary<string, object?> { ["protectedPrincipal"] = true });
                    continue;
                }
            }

            // Same resolver as the first read: the re-read compares immutable identifiers against
            // it, and two different resolvers could disagree for reasons that are not a real
            // identity change.
            var secondResolution = await _protectedPrincipals.ResolveWithExchangeFallbackAsync(user);
            if (secondResolution.status != ProtectedPrincipalService.ResolutionStatus.Resolved || secondResolution.principal == null)
            {
                const string detail = "Target identity could not be re-read immediately before logoff.";
                rows.Add(BlockedUserRow(user, "identity-reread-failed", detail));
                AuditLogoff(context, action, user, false, ticketNumber, detail, new Dictionary<string, object?> { ["rereadFailed"] = true });
                continue;
            }

            if (!ImmutableIdentityMatches(firstResolution.principal, secondResolution.principal))
            {
                const string detail = "Target identity changed between lookup and execution.";
                rows.Add(BlockedUserRow(user, "identity-mismatch", detail));
                AuditLogoff(context, action, user, false, ticketNumber, detail, new Dictionary<string, object?> { ["immutableMismatch"] = true });
                continue;
            }

            // A serviced override succeeds, so it cannot ride errorDetail - that field is written as
            // null on success and the record of who permitted it would vanish. Audited here, per
            // user, because this method is where the override decision was actually made.
            if (servicedNote is not null)
            {
                AuditLogoff(context, action, user, true, ticketNumber, null,
                    ProtectedPrincipalServicing.Extra(servicedNote));
            }

            allowedUsers.Add(user);
            approved[user] = secondResolution.principal;
        }

        var filtered = machineMap
            .Select(kvp => new KeyValuePair<string, string[]>(kvp.Key, kvp.Value.Where(allowedUsers.Contains).ToArray()))
            .Where(kvp => kvp.Value.Length > 0)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

        return (filtered, rows, approved);
    }

    private async Task<(string username, string password, string domain)?> GetCredentialsAsync(string purpose)
        => await _moduleCredentials.GetCredentialsAsync(ModuleId, purpose);

    private async Task<string[]> DiscoverPdcAsync((string username, string password, string domain) credentials)
    {
        _operationTrace.Step("DiscoverPdc", backend: "ActiveDirectory", command: "Get-ADDomain");
        return await Task.Run(() =>
        {
            using var ps = CreateAdPowerShell();
            var credential = CreateCredential(credentials);
            ps.AddCommand("Get-ADDomain")
              .AddParameter("Credential", credential)
              .AddParameter("ErrorAction", "Stop");

            var domains = Invoke(ps);
            var pdce = domains.FirstOrDefault()?.Properties["PDCEmulator"]?.Value?.ToString();
            return string.IsNullOrWhiteSpace(pdce) ? Array.Empty<string>() : [pdce];
        });
    }

    private (List<AccountLockoutEventRow> events, List<string> failures) ReadLockoutEvents(
        string[] domainControllers,
        string[] users,
        int withinHours,
        (string username, string password, string domain) credentials)
    {
        var rows = new List<AccountLockoutEventRow>();
        var failures = new List<string>();
        var credential = CreateCredential(credentials);
        var start = DateTime.UtcNow.AddHours(-Math.Max(1, withinHours));
        var userSet = new HashSet<string>(users, StringComparer.OrdinalIgnoreCase);

        foreach (var dc in domainControllers)
        {
            using var ps = PowerShell.Create();
            ps.AddCommand("Invoke-Command")
              .AddParameter("ComputerName", dc)
              .AddParameter("Credential", credential)
              .AddParameter("ScriptBlock", ScriptBlock.Create(@"
param([datetime]$StartTime)
$events = Get-WinEvent -FilterHashtable @{ LogName = 'Security'; Id = 4740; StartTime = $StartTime } -ErrorAction Stop
foreach ($event in $events) {
    [pscustomobject]@{
        TimeCreated = $event.TimeCreated
        TargetUser = if ($event.Properties.Count -ge 1) { [string]$event.Properties[0].Value } else { '' }
        CallerComputer = if ($event.Properties.Count -ge 2) { [string]$event.Properties[1].Value } else { '' }
    }
}
"))
              .AddParameter("ArgumentList", new object[] { start })
              .AddParameter("ErrorAction", "Stop");

            Collection<PSObject> events;
            try
            {
                events = Invoke(ps);
            }
            catch (Exception ex)
            {
                var message = SafeError(ex);
                if (message.Contains("No events were found", StringComparison.OrdinalIgnoreCase))
                    continue;

                failures.Add($"{dc}: {message}");
                continue;
            }

            foreach (var item in events)
            {
                var rawUser = GetProperty(item, "TargetUser");
                var user = NormalizeUser(rawUser);
                if (string.IsNullOrWhiteSpace(user))
                    continue;
                if (userSet.Count > 0 && !userSet.Contains(user))
                    continue;

                var source = GetProperty(item, "CallerComputer").Trim().TrimEnd('$');
                var actionable = !string.IsNullOrWhiteSpace(source) && source != "-";
                DateTime? time = item.Properties["TimeCreated"]?.Value is DateTime dt ? dt : null;
                rows.Add(new AccountLockoutEventRow(
                    user,
                    rawUser,
                    source,
                    dc,
                    time,
                    actionable,
                    actionable ? "" : "4740 event had no caller computer; logoff cannot remediate this source."));
            }
        }

        return (rows, failures);
    }

    private string[] ResolveComputers(string searchBase, string[] extraComputers, (string username, string password, string domain) credentials)
    {
        var computers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(searchBase))
        {
            using var ps = CreateAdPowerShell();
            ps.AddCommand("Get-ADComputer")
              .AddParameter("Filter", "Enabled -eq $true")
              .AddParameter("Properties", new[] { "DNSHostName" })
              .AddParameter("SearchBase", searchBase.Trim())
              .AddParameter("Credential", CreateCredential(credentials))
              .AddParameter("ErrorAction", "Stop");

            foreach (var item in Invoke(ps))
            {
                var dns = item.Properties["DNSHostName"]?.Value?.ToString();
                var name = item.Properties["Name"]?.Value?.ToString();
                var computer = string.IsNullOrWhiteSpace(dns) ? name : dns;
                if (!string.IsNullOrWhiteSpace(computer))
                    computers.Add(computer.Trim());
            }
        }

        foreach (var computer in NormalizeNames(extraComputers))
            computers.Add(computer);

        return computers.OrderBy(x => x).ToArray();
    }

    /// <summary>
    /// Audit finding A5. Replaces each target username in the machine map with the SID of the
    /// directory object that username resolves to, so the remote script matches a logon session
    /// by SID rather than by a bare name that collides across domains.
    /// </summary>
    /// <remarks>
    /// Internal as a TEST SEAM, for the same reason <see cref="GuardTargetUsersAsync"/> is:
    /// RunLogoffAsync reaches a live directory before this point, so no test can drive it
    /// through the public methods.
    ///
    /// Fail closed throughout. A user whose SID cannot be established is removed from the map
    /// and reported - never passed through under their name, and never guessed at. The owner's
    /// standing ruling is that inaccurate input is the user's to resolve, not ours to work
    /// around.
    /// </remarks>
    internal async Task<(Dictionary<string, string[]> machineMap, Dictionary<string, string> sidNames, List<ComputerSessionActionRow> rows)> MapTargetsToSidsAsync(
        Dictionary<string, string[]> machineMap,
        AccountLockoutOperatorContext context,
        string ticketNumber,
        string action,
        (string username, string password, string domain) credentials,
        bool execute,
        IReadOnlyDictionary<string, ResolvedDirectoryPrincipal>? guarded = null)
    {
        var rows = new List<ComputerSessionActionRow>();
        var sidByUser = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sidNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Block(string user, string blockAction, string detail)
        {
            rows.Add(BlockedUserRow(user, blockAction, detail));
            AuditLogoff(context, action, user, false, ticketNumber, detail,
                new Dictionary<string, object?> { ["sidResolutionFailed"] = true, ["execute"] = execute });
        }

        foreach (var user in machineMap.Values.SelectMany(x => x).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var resolution = await _protectedPrincipals.ResolveWithExchangeFallbackAsync(user);
            if (resolution.status != ProtectedPrincipalService.ResolutionStatus.Resolved || resolution.principal == null)
            {
                Block(user, "sid-unresolved", resolution.status == ProtectedPrincipalService.ResolutionStatus.Ambiguous
                    ? "Target identity is ambiguous, so no single SID can be matched to it."
                    : "Target identity could not be resolved, so its SID is unknown.");
                continue;
            }

            // Review of aud-a5, finding 3. The guard resolved this name, re-read it, compared
            // immutable identity and approved THAT object - then handed on only the name, and
            // this method resolved the name again. A rename or an alias moving between the two
            // meant the object that passed protection and the object whose SID became the
            // destructive target were different objects. That is the exact defect class this
            // whole wave is about, sitting inside its own fix.
            if (guarded is not null && guarded.TryGetValue(user, out var approved)
                && !ImmutableIdentityMatches(approved, resolution.principal))
            {
                Block(user, "identity-changed",
                    "The target resolved to a different directory object than the one that passed the protected-principal check.");
                continue;
            }

            var dn = resolution.principal.DistinguishedName;
            if (string.IsNullOrWhiteSpace(dn))
            {
                Block(user, "sid-unresolved", "Target has no on-premises directory object, so no Windows logon session can belong to it.");
                continue;
            }

            // The same rule aud-a3 established: read from the domain that owns the object, not
            // from whichever domain this host happens to sit in.
            var server = GroupManagementService.ServerFromDn(dn);
            if (server is null)
            {
                Block(user, "sid-unresolved", "Target's distinguished name names no domain, so its SID cannot be read from the owning domain.");
                continue;
            }

            (string? sid, string? guid) read;
            try
            {
                read = await Task.Run(() => SidReader(dn, server, credentials));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read objectSid for a lockout remediation target");
                Block(user, "sid-lookup-failed", $"Reading the target's SID from {server} failed: {SafeError(ex)}");
                continue;
            }

            if (string.IsNullOrWhiteSpace(read.sid) || !read.sid.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
            {
                Block(user, "sid-unresolved", "The directory returned no usable objectSid for the target.");
                continue;
            }

            // Binding the SID read to the object that was approved, not just to its name. A DN
            // can be reused by a replacement object between the resolve and this read, so the
            // GUID that came back has to be the GUID that passed the check.
            var expectedGuid = resolution.principal.ObjectGuid;
            if (!string.IsNullOrWhiteSpace(expectedGuid) && !string.IsNullOrWhiteSpace(read.guid)
                && !string.Equals(expectedGuid, read.guid, StringComparison.OrdinalIgnoreCase))
            {
                Block(user, "identity-changed",
                    "The object holding the target's distinguished name changed between resolution and the SID read.");
                continue;
            }

            sidByUser[user] = read.sid;
            sidNames[read.sid] = string.IsNullOrWhiteSpace(resolution.principal.SamAccountName)
                ? user
                : resolution.principal.SamAccountName!;
        }

        var mapped = machineMap
            .Select(kvp => new KeyValuePair<string, string[]>(
                kvp.Key,
                kvp.Value
                    .Where(u => sidByUser.ContainsKey(u))
                    .Select(u => sidByUser[u])
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()))
            .Where(kvp => kvp.Value.Length > 0)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

        return (mapped, sidNames, rows);
    }

    /// <summary>
    /// TEST SEAM for <see cref="MapTargetsToSidsAsync"/>, in the same spirit as that method being
    /// internal: reading objectSid reaches a live directory, so nothing could otherwise exercise
    /// the fail-closed branches. Defaults to the real read; only tests replace it.
    /// </summary>
    internal Func<string, string, (string username, string password, string domain), (string? sid, string? guid)> SidReader { get; set; } = null!;

    /// <remarks>
    /// Reads the GUID alongside the SID so the caller can prove the object it just read is the
    /// object that passed the protected-principal check - review of aud-a5, finding 3. A DN can
    /// be reused by a replacement object, so the DN alone is not identity.
    /// </remarks>
    private (string? sid, string? guid) ReadObjectSid(string dn, string server, (string username, string password, string domain) credentials)
    {
        using var ps = CreateAdPowerShell();
        ps.AddCommand("Get-ADUser")
          .AddParameter("Identity", dn)
          .AddParameter("Properties", new[] { "objectSid", "objectGUID" })
          .AddParameter("Server", server)
          .AddParameter("Credential", CreateCredential(credentials))
          .AddParameter("ErrorAction", "Stop");

        var found = Invoke(ps).FirstOrDefault();
        return (SidText(found?.Properties["objectSid"]?.Value),
                found?.Properties["ObjectGUID"]?.Value?.ToString());
    }

    /// <summary>
    /// objectSid arrives as a SecurityIdentifier in-process and as a deserialized PSObject
    /// carrying a Value property over remoting; both must yield the S-1-... form.
    /// </summary>
    internal static string? SidText(object? value) => value switch
    {
        null => null,
        System.Security.Principal.SecurityIdentifier sid => sid.Value,
        PSObject pso => pso.Properties["Value"]?.Value?.ToString() ?? pso.BaseObject as string ?? pso.ToString(),
        string text => text,
        _ => value.ToString(),
    };

    private List<ComputerSessionActionRow> QueryAndMaybeLogoff(
        Dictionary<string, string[]> machineMap,
        bool execute,
        int throttleLimit,
        (string username, string password, string domain) credentials,
        Dictionary<string, string> sidDisplayNames)
    {
        if (machineMap.Count == 0)
            return [];

        using var ps = PowerShell.Create();
        ps.AddCommand("Invoke-Command")
          .AddParameter("ComputerName", machineMap.Keys.ToArray())
          .AddParameter("Credential", CreateCredential(credentials))
          .AddParameter("ThrottleLimit", throttleLimit)
          .AddParameter("ScriptBlock", ScriptBlock.Create(RemoteSessionScript))
          .AddParameter("ArgumentList", new object[] { machineMap, execute, sidDisplayNames })
          .AddParameter("ErrorAction", "Continue");

        var output = ps.Invoke();
        var rows = new List<ComputerSessionActionRow>();

        foreach (var item in output)
        {
            rows.Add(new ComputerSessionActionRow(
                GetRemoteComputer(item),
                GetProperty(item, "User"),
                GetProperty(item, "SessionId"),
                GetProperty(item, "State"),
                GetProperty(item, "Action"),
                GetBoolProperty(item, "Success"),
                GetProperty(item, "Detail")));
        }

        foreach (var err in ps.Streams.Error)
        {
            rows.Add(new ComputerSessionActionRow(
                err.TargetObject?.ToString() ?? "",
                "",
                "",
                "",
                "unreachable",
                false,
                err.Exception.Message));
        }

        return rows.OrderBy(r => r.Computer, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.User, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <remarks>
    /// Audit finding A5. The map now carries target SIDs, not usernames, and a session is only
    /// acted on when the SID of its owner - derived on the remote host - is in that set. The
    /// bare USERNAME column `quser` prints has no domain in it, so matching on it logged off
    /// `B\alex` or a local `alex` when the account checked was `A\alex`.
    ///
    /// A session whose owner cannot be established is REFUSED and reported, never logged off:
    /// an unidentified session may well be the target, and refusing to act on it is the only
    /// safe answer. `$sidDisplayNames` exists purely so those rows can name a person instead of
    /// a SID.
    /// </remarks>
    private static readonly string RemoteSessionScript = @"
param($machineUserMap, [bool]$doExecute, $sidDisplayNames)
$me = $env:COMPUTERNAME.ToLowerInvariant()
$targetSids = @()
foreach ($key in $machineUserMap.Keys) {
    $short = ([string]$key -replace '\..*$', '').ToLowerInvariant()
    if ($short -eq $me -or ([string]$key).ToLowerInvariant() -eq $me) {
        $targetSids = @($machineUserMap[$key])
        break
    }
}
$targetSids = @($targetSids | ForEach-Object { ([string]$_).Trim().ToUpperInvariant() } | Where-Object { $_ })
if (-not $targetSids) {
    [pscustomobject]@{ User = ''; SessionId = ''; State = ''; Action = 'no-mapping'; Success = $false; Detail = ""No flagged users matched $me"" }
    return
}

# Indexing, not .Contains or .ContainsKey: this map arrives as a Hashtable over remoting and as
# a Dictionary[string,string] when the script is run in-process, and those two do not share a
# single-argument lookup method. Indexing works on both and yields $null for a missing key.
function Get-TargetLabel([string]$sid) {
    if ($sidDisplayNames) {
        $label = $null
        try { $label = [string]$sidDisplayNames[$sid] } catch { $label = $null }
        if ($label) { return $label }
    }
    return $sid
}
$targetLabel = (@($targetSids | ForEach-Object { Get-TargetLabel $_ }) -join ',')

# One enumeration for the whole machine, then grouped by terminal-services session. Asking per
# session would be the same data fetched once per row.
#
# Review of aud-a5, finding 2: this runs AFTER quser, not before it. Collecting ownership
# first and joining a later quser row to it by session number alone meant that if a session
# ended and Windows reused its id - which it documents doing - the new occupant's row could
# match the previous occupant's cached SID. The snapshot is still not atomic with the logoff,
# but it is no longer taken before the session list it is joined to.
function Get-SessionOwners {
    $map = @{}
    foreach ($proc in @(Get-CimInstance -ClassName Win32_Process -ErrorAction Stop)) {
        $key = [string]$proc.SessionId
        if (-not $map.ContainsKey($key)) {
            $map[$key] = [pscustomobject]@{ Sids = (New-Object 'System.Collections.Generic.HashSet[string]'); Unreadable = 0 }
        }

        $owner = $null
        # Review of aud-a5, finding 1: an owner that cannot be read is COUNTED, not skipped.
        # GetOwnerSid is documented to return access-denied, and Windows lets a user run a
        # process as another account inside their own desktop. Dropping the unreadable ones
        # silently could leave a single readable secondary-account process standing as proof
        # that the session belongs to that account - and log the real occupant off.
        try { $owner = Invoke-CimMethod -InputObject $proc -MethodName GetOwnerSid -ErrorAction Stop } catch { $map[$key].Unreadable++; continue }
        if (-not $owner -or $owner.ReturnValue -ne 0) { $map[$key].Unreadable++; continue }
        $sid = ([string]$owner.Sid).Trim().ToUpperInvariant()
        if (-not $sid) { $map[$key].Unreadable++; continue }

        # Machine identities, service accounts and virtual accounts are never a logged-on
        # person, and every interactive session has some of them.
        if ($sid -eq 'S-1-5-18' -or $sid -eq 'S-1-5-19' -or $sid -eq 'S-1-5-20') { continue }
        if ($sid -like 'S-1-5-80-*' -or $sid -like 'S-1-5-82-*' -or $sid -like 'S-1-5-83-*' -or $sid -like 'S-1-5-90-*' -or $sid -like 'S-1-5-96-*') { continue }

        [void]$map[$key].Sids.Add($sid)
    }
    return $map
}

$out = quser.exe 2>&1
$code = $LASTEXITCODE
$err = @($out | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] } | ForEach-Object { $_.ToString() })
$raw = @($out | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] })
$combined = (@($raw) + @($err)) -join ""`n""
$noUsers = $combined -match 'No User exists for'

if (($code -ne 0 -or $err.Count) -and -not $noUsers) {
    [pscustomobject]@{ User = ''; SessionId = ''; State = ''; Action = 'query-failed'; Success = $false; Detail = (($err -join '; ').Trim() + "" (quser exit $code)"") }
    return
}

if ($noUsers -or -not $raw -or @($raw).Count -lt 2) {
    [pscustomobject]@{ User = $targetLabel; SessionId = ''; State = ''; Action = 'not-logged-on'; Success = $true; Detail = 'No matching session present' }
    return
}

# Ownership is read only now that there is a session list to join it to.
try {
    $ownersBySession = Get-SessionOwners
} catch {
    [pscustomobject]@{ User = $targetLabel; SessionId = ''; State = ''; Action = 'owner-query-failed'; Success = $false; Detail = ""Could not read session owners, so no session could be identified: $($_.Exception.Message)"" }
    return
}

$found = $false
foreach ($line in ($raw | Select-Object -Skip 1)) {
    $l = $line -replace '^\s*>?\s*', ''
    $cols = $l -split '\s{2,}'
    if ($cols.Count -lt 5) { continue }

    $quserName = $cols[0].Trim()

    $sessionId = ($cols[1..($cols.Count - 1)] | Where-Object { $_ -match '^\d+$' } | Select-Object -First 1)
    if (-not $sessionId) { continue }

    $state = if ($cols.Count -ge 6) { $cols[3].Trim() } else { $cols[2].Trim() }

    # The identity decision, and the only one. $quserName is a display string from here on; it
    # never selects a session.
    $entry = $ownersBySession[[string]$sessionId]
    $owners = if ($entry) { $entry.Sids } else { $null }
    $unreadable = if ($entry) { $entry.Unreadable } else { 0 }

    if (-not $owners -or $owners.Count -eq 0) {
        [pscustomobject]@{ User = $quserName; SessionId = $sessionId; State = $state; Action = 'owner-unverified'; Success = $false; Detail = 'Session owner could not be identified; refusing to act on it' }
        continue
    }
    if ($owners.Count -gt 1) {
        [pscustomobject]@{ User = $quserName; SessionId = $sessionId; State = $state; Action = 'owner-unverified'; Success = $false; Detail = ""Session has $($owners.Count) distinct process owners; refusing to act on it"" }
        continue
    }
    # One readable owner is only proof of ownership if there was nothing we could NOT read.
    if ($unreadable -gt 0) {
        [pscustomobject]@{ User = $quserName; SessionId = $sessionId; State = $state; Action = 'owner-unverified'; Success = $false; Detail = ""$unreadable process owner(s) in this session could not be read, so one readable owner does not identify it; refusing to act on it"" }
        continue
    }

    $ownerSid = @($owners)[0]
    if ($ownerSid -notin $targetSids) { continue }

    $found = $true
    $action = if ($doExecute) { 'logged-off' } else { 'would-log-off' }
    $success = $true
    $detail = ''

    if ($doExecute) {
        logoff.exe $sessionId 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) {
            $action = 'logoff-failed'
            $success = $false
            $detail = ""logoff.exe exit $LASTEXITCODE""
        }
    }

    [pscustomobject]@{ User = (Get-TargetLabel $ownerSid); SessionId = $sessionId; State = $state; Action = $action; Success = $success; Detail = $detail }
}

if (-not $found) {
    [pscustomobject]@{ User = $targetLabel; SessionId = ''; State = ''; Action = 'not-logged-on'; Success = $true; Detail = 'Flagged user has no current session' }
}
";

    // psl-7. The import through the shared helper, whose throw is its error. The HadErrors check that followed it was
    // dead for the import's own error under Stop (thrown, no record) and live only for a record written past the
    // preference, which it threw as the import's failure though the module had loaded. The dispose it held was the
    // only one on a failure path, so a failing import - the throw - leaked the PowerShell; the catch disposes it.
    // The runspace is a TEST SEAM: production passes none, and Invoke opens the PowerShell's own, as before.
    internal static PowerShell CreateAdPowerShell(Runspace? runspace = null)
    {
        var ps = PowerShell.Create();
        if (runspace is not null)
            ps.Runspace = runspace;

        try
        {
            ActiveDirectoryModuleImport.Import(ps);
        }
        catch
        {
            ps.Dispose();
            throw;
        }

        return ps;
    }

    internal static Collection<PSObject> Invoke(PowerShell ps)
    {
        var result = ps.Invoke();
        if (ps.HadErrors)
        {
            var message = ps.Streams.Error.FirstOrDefault()?.Exception.Message ?? "PowerShell command failed.";
            throw new InvalidOperationException(message);
        }

        return result;
    }

    private static PSCredential CreateCredential((string username, string password, string domain) credentials)
    {
        var fullUsername = credentials.username.Contains('\\') || credentials.username.Contains('@')
            ? credentials.username
            : $"{credentials.domain}\\{credentials.username}";
        var secure = new System.Security.SecureString();
        foreach (var c in credentials.password)
            secure.AppendChar(c);
        secure.MakeReadOnly();
        return new PSCredential(fullUsername, secure);
    }

    private static string[] NormalizeUsers(IEnumerable<string> users)
        => users.Select(NormalizeUser)
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(u => u)
            .ToArray();

    private static string NormalizeUser(string user)
    {
        if (string.IsNullOrWhiteSpace(user))
            return "";
        var trimmed = user.Trim();
        var slash = trimmed.LastIndexOf('\\');
        if (slash >= 0 && slash < trimmed.Length - 1)
            trimmed = trimmed[(slash + 1)..];
        return trimmed.Trim().ToLowerInvariant();
    }

    private static string[] NormalizeNames(IEnumerable<string> names)
        => names.Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x)
            .ToArray();

    private int GetMaxSweepTargets() => ReadIntConfig("MaxSweepTargets", 10000);

    private int ReadIntConfig(string key, int defaultValue)
        => int.TryParse(_moduleConfig.GetValue(ModuleId, key), out var value) ? value : defaultValue;

    private static int ClampThrottle(int throttle) => Math.Clamp(throttle, 1, 256);

    private static bool ImmutableIdentityMatches(ResolvedDirectoryPrincipal first, ResolvedDirectoryPrincipal second)
    {
        if (!string.IsNullOrWhiteSpace(first.ObjectGuid) && !string.IsNullOrWhiteSpace(second.ObjectGuid))
            return string.Equals(first.ObjectGuid, second.ObjectGuid, StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(first.DistinguishedName) && !string.IsNullOrWhiteSpace(second.DistinguishedName))
            return string.Equals(first.DistinguishedName, second.DistinguishedName, StringComparison.OrdinalIgnoreCase);
        return string.Equals(first.SamAccountName, second.SamAccountName, StringComparison.OrdinalIgnoreCase);
    }

    private static ComputerSessionActionRow BlockedUserRow(string user, string action, string detail)
        => new("", user, "", "", action, false, detail);

    /// <summary>
    /// A 4740 event whose caller computer was blank: nothing to log off, but the operator has to
    /// be told why that user is unaccounted for. Extracted so every return path can append them -
    /// review of aud-a5 found one that did not.
    /// </summary>
    private static ComputerSessionActionRow BlankSourceRow(AccountLockoutEventRow blank)
        => new("(unknown)", blank.UserRaw, "", "", "blank-source", false, blank.Detail);

    private static AccountLogoffResult EmptyLogoffResult(string message, bool execute)
        => new(false, message, execute, 0, 0, 0, []);

    // Mandatory admin notification on every executed logoff operation (Constitution
    // Section Notifications) - session-state change. One summary notification per operation
    // (counts), not one per session row. Dry-runs (Executed == false) do not notify.
    // Fail-safe: a send failure logs but never changes the returned result.
    private async Task NotifyLogoffAdminAsync(
        string action,
        AccountLockoutLogoffRequest? sourceRequest,
        AccountScopedLogoffRequest? scopedRequest,
        AccountLogoffResult result,
        AccountLockoutOperatorContext context)
    {
        if (!result.Executed)
            return;

        var users = sourceRequest?.Users ?? scopedRequest?.Users ?? [];
        var ticket = sourceRequest?.TicketNumber ?? scopedRequest?.TicketNumber ?? "";
        var details = new Dictionary<string, string>
        {
            ["Target Users"] = users.Length > 0 ? string.Join(", ", users) : "(none)",
            ["Machines"] = result.MachineCount.ToString(),
            ["Sessions Logged Off"] = result.SessionHitCount.ToString(),
            ["Failures"] = result.FailureCount.ToString()
        };

        try
        {
            await _email.SendAdminNotificationAsync(
                context.DisplayName,
                context.IpAddress,
                action,
                result.Success,
                ticket,
                details,
                result.Success ? null : result.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send AccountLockoutRemediation admin notification for {Action}", action);
        }
    }

    private void AuditLogoff(
        AccountLockoutOperatorContext context,
        string action,
        string target,
        bool success,
        string ticketNumber,
        string? error,
        Dictionary<string, object?> extra)
    {
        try
        {
            _audit.LogModuleAction(context.DisplayName, context.IpAddress, action, ModuleId, target, success, ticketNumber, error, extra);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write AccountLockoutRemediation audit event");
        }
    }

    private static string GetProperty(PSObject obj, string name)
        => obj.Properties[name]?.Value?.ToString() ?? "";

    private static bool GetBoolProperty(PSObject obj, string name)
        => obj.Properties[name]?.Value is bool b && b;

    private static string GetRemoteComputer(PSObject obj)
        => obj.Properties["PSComputerName"]?.Value?.ToString() ?? obj.Properties["Computer"]?.Value?.ToString() ?? "";

    private static string SafeError(Exception ex) => ex.Message.Split(Environment.NewLine)[0];
}
