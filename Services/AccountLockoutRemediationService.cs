using System.Collections.ObjectModel;
using System.Management.Automation;
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
    private readonly IAuthorizationService _authorization;
    private readonly AuditService _audit;
    private readonly OperationTraceService _operationTrace;
    private readonly ILogger<AccountLockoutRemediationService> _logger;

    public AccountLockoutRemediationService(
        ModuleCredentialService moduleCredentials,
        ModuleConfigService moduleConfig,
        ProtectedPrincipalService protectedPrincipals,
        IAuthorizationService authorization,
        AuditService audit,
        OperationTraceService operationTrace,
        ILogger<AccountLockoutRemediationService> logger)
    {
        _moduleCredentials = moduleCredentials;
        _moduleConfig = moduleConfig;
        _protectedPrincipals = protectedPrincipals;
        _authorization = authorization;
        _audit = audit;
        _operationTrace = operationTrace;
        _logger = logger;
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

            if (execute)
            {
                var guard = await GuardTargetUsersAsync(realMachines, context, ticketNumber, action);
                realMachines = guard.machineMap;
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

            _operationTrace.Step(execute ? "RemoteLogoff" : "RemoteSessionQuery", backend: "WinRM", command: execute ? "logoff.exe" : "quser.exe");
            var throttle = ClampThrottle(requestedThrottle <= 0 ? GetDefaultThrottleLimit() : requestedThrottle);
            var rows = await Task.Run(() => QueryAndMaybeLogoff(realMachines, execute, throttle, credentials.Value));
            var allRows = rows.ToList();

            foreach (var blank in includeBlankSourceRows)
            {
                allRows.Add(new ComputerSessionActionRow(
                    "(unknown)",
                    blank.UserRaw,
                    "",
                    "",
                    "blank-source",
                    false,
                    blank.Detail));
            }

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

    private async Task<(Dictionary<string, string[]> machineMap, List<ComputerSessionActionRow> rows)> GuardTargetUsersAsync(
        Dictionary<string, string[]> machineMap,
        AccountLockoutOperatorContext context,
        string ticketNumber,
        string action)
    {
        var allUsers = machineMap.Values.SelectMany(x => x).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var allowedUsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<ComputerSessionActionRow>();

        foreach (var user in allUsers)
        {
            var firstResolution = await _protectedPrincipals.ResolveWithStatusAsync(user);
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

            if (protection.IsProtected)
            {
                rows.Add(BlockedUserRow(user, "protected-principal", protection.Reason));
                AuditLogoff(context, action, user, false, ticketNumber, protection.Reason, new Dictionary<string, object?> { ["protectedPrincipal"] = true });
                continue;
            }

            var secondResolution = await _protectedPrincipals.ResolveWithStatusAsync(user);
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

            allowedUsers.Add(user);
        }

        var filtered = machineMap
            .Select(kvp => new KeyValuePair<string, string[]>(kvp.Key, kvp.Value.Where(allowedUsers.Contains).ToArray()))
            .Where(kvp => kvp.Value.Length > 0)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

        return (filtered, rows);
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

    private List<ComputerSessionActionRow> QueryAndMaybeLogoff(
        Dictionary<string, string[]> machineMap,
        bool execute,
        int throttleLimit,
        (string username, string password, string domain) credentials)
    {
        if (machineMap.Count == 0)
            return [];

        using var ps = PowerShell.Create();
        ps.AddCommand("Invoke-Command")
          .AddParameter("ComputerName", machineMap.Keys.ToArray())
          .AddParameter("Credential", CreateCredential(credentials))
          .AddParameter("ThrottleLimit", throttleLimit)
          .AddParameter("ScriptBlock", ScriptBlock.Create(RemoteSessionScript))
          .AddParameter("ArgumentList", new object[] { machineMap, execute })
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

    private static readonly string RemoteSessionScript = @"
param($machineUserMap, [bool]$doExecute)
$me = $env:COMPUTERNAME.ToLowerInvariant()
$targetUsers = @()
foreach ($key in $machineUserMap.Keys) {
    $short = ([string]$key -replace '\..*$', '').ToLowerInvariant()
    if ($short -eq $me -or ([string]$key).ToLowerInvariant() -eq $me) {
        $targetUsers = @($machineUserMap[$key])
        break
    }
}
$targetUsers = @($targetUsers | ForEach-Object { ([string]$_).ToLowerInvariant() } | Where-Object { $_ })
if (-not $targetUsers) {
    [pscustomobject]@{ User = ''; SessionId = ''; State = ''; Action = 'no-mapping'; Success = $false; Detail = ""No flagged users matched $me"" }
    return
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
    [pscustomobject]@{ User = ($targetUsers -join ','); SessionId = ''; State = ''; Action = 'not-logged-on'; Success = $true; Detail = 'No matching session present' }
    return
}

$found = $false
foreach ($line in ($raw | Select-Object -Skip 1)) {
    $l = $line -replace '^\s*>?\s*', ''
    $cols = $l -split '\s{2,}'
    if ($cols.Count -lt 5) { continue }

    $userName = $cols[0].Trim()
    if ($userName.ToLowerInvariant() -notin $targetUsers) { continue }

    $sessionId = ($cols[1..($cols.Count - 1)] | Where-Object { $_ -match '^\d+$' } | Select-Object -First 1)
    if (-not $sessionId) { continue }

    $state = if ($cols.Count -ge 6) { $cols[3].Trim() } else { $cols[2].Trim() }
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

    [pscustomobject]@{ User = $userName; SessionId = $sessionId; State = $state; Action = $action; Success = $success; Detail = $detail }
}

if (-not $found) {
    [pscustomobject]@{ User = ($targetUsers -join ','); SessionId = ''; State = ''; Action = 'not-logged-on'; Success = $true; Detail = 'Flagged user has no current session' }
}
";

    private static PowerShell CreateAdPowerShell()
    {
        var ps = PowerShell.Create();
        ps.AddCommand("Import-Module")
          .AddParameter("Name", "ActiveDirectory")
          .AddParameter("ErrorAction", "Stop");

        ps.Invoke();
        if (ps.HadErrors)
        {
            var message = ps.Streams.Error.FirstOrDefault()?.Exception.Message ?? "ActiveDirectory module import failed.";
            ps.Dispose();
            throw new InvalidOperationException(message);
        }

        ps.Commands.Clear();
        ps.Streams.Error.Clear();
        return ps;
    }

    private static Collection<PSObject> Invoke(PowerShell ps)
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

    private static AccountLogoffResult EmptyLogoffResult(string message, bool execute)
        => new(false, message, execute, 0, 0, 0, []);

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
