using System.Collections;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Security.Cryptography;
using System.Text.Json;

namespace ExchangeAdminWeb.Services;

public sealed record DisableStepResult(string Step, string Status, string? Detail);

public sealed record DisableSnapshot
{
    public string OperationId { get; init; } = "";
    public DateTime Timestamp { get; init; }
    public string Actor { get; init; } = "";
    public string Ticket { get; init; } = "";
    public string TargetUpn { get; init; } = "";
    public string TargetDn { get; init; } = "";
    public string? TargetObjectGuid { get; init; }
    public bool PreState_AdEnabled { get; init; }
    public bool PreState_EntraEnabled { get; init; }
    public List<DisableStepResult> Steps { get; init; } = new();
}

public sealed record EmergencyDisableResult(
    bool Success,
    string? Error,
    DisableSnapshot? Snapshot,
    List<DisableStepResult> Steps);

/// <summary>
/// Outcome of the lockdown-OU move. Only <see cref="NotRequested"/>, <see cref="Moved"/> and
/// <see cref="AlreadyInPlace"/> are compatible with overall success; a requested move that did
/// not happen fails the operation (Known Failure Class 2 - no blanket success).
/// </summary>
public enum LockdownOutcome
{
    NotRequested,
    Moved,
    AlreadyInPlace,
    Skipped,
    Failed
}

/// <summary>
/// What the lockdown step should do, decided without touching the directory. When
/// <see cref="Proceed"/> is false the outcome and detail are final and no AD call is made.
/// </summary>
public sealed record LockdownDecision(bool Proceed, LockdownOutcome Outcome, string? Detail);

/// <summary>
/// Result of building the appended info-attribute value. <see cref="Value"/> is set only when
/// <see cref="Ok"/>; the builder refuses rather than truncating.
/// </summary>
public sealed record LockdownStampResult(bool Ok, string? Value, string? Error);

public class EmergencyDisableService
{
    private readonly ModuleCredentialService _moduleCredentials;
    private readonly ModuleConfigService _moduleConfig;
    private readonly ProtectedPrincipalService _protectedPrincipalService;
    private readonly ProtectedPrincipalServicerService _servicers;
    private readonly OperationTraceService _operationTrace;
    private readonly AuditService _audit;
    private readonly EmailService _email;
    private readonly DelineaService _delineaService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IWebHostEnvironment _env;
    private readonly IConfiguration _config;
    private readonly ILogger<EmergencyDisableService> _logger;

    private static readonly SemaphoreSlim _adThrottle = new(2, 2);

    /// <summary>Module id for the servicer grant. Must match the catalog descriptor.</summary>
    private const string ServicerModuleId = "EmergencyDisable";

    public EmergencyDisableService(
        ModuleCredentialService moduleCredentials,
        ModuleConfigService moduleConfig,
        ProtectedPrincipalService protectedPrincipalService,
        ProtectedPrincipalServicerService servicers,
        OperationTraceService operationTrace,
        AuditService audit,
        EmailService email,
        DelineaService delineaService,
        IHttpClientFactory httpClientFactory,
        IWebHostEnvironment env,
        IConfiguration config,
        ILogger<EmergencyDisableService> logger)
    {
        _moduleCredentials = moduleCredentials;
        _moduleConfig = moduleConfig;
        _protectedPrincipalService = protectedPrincipalService;
        _servicers = servicers;
        _operationTrace = operationTrace;
        _audit = audit;
        _email = email;
        _delineaService = delineaService;
        _httpClientFactory = httpClientFactory;
        _env = env;
        _config = config;
        _logger = logger;
    }

    /// <param name="actingUser">
    /// The operator, for the protected-principal servicer decision. REQUIRED and not defaulted: a
    /// null principal refuses, and a default would quietly make every caller that forgot it into a
    /// caller that cannot service - or, worse, invite an ambient lookup.
    /// </param>
    /// <param name="moveToLockdownOu">
    /// Whether the operator left the lockdown step armed. Required and NOT defaulted, for the
    /// same reason <paramref name="actingUser"/> is: a default quietly turns every caller that
    /// forgot it into a caller with different security behaviour.
    /// </param>
    public async Task<EmergencyDisableResult> DisableAsync(
        ResolvedDirectoryPrincipal target, string ticket, string performedBy, string ip,
        System.Security.Claims.ClaimsPrincipal? actingUser, bool moveToLockdownOu)
    {
        using var opScope = _operationTrace.BeginOperation(
            module: "EmergencyDisable",
            action: "DisableCompromisedAccount",
            actor: performedBy,
            ipAddress: ip,
            target: target.UserPrincipalName,
            ticket: ticket);

        var steps = new List<DisableStepResult>();

        if (string.IsNullOrWhiteSpace(ticket))
        {
            const string msg = "Ticket number is required for emergency disable operations.";
            _operationTrace.Step("TicketValidation", "Failed");
            steps.Add(new DisableStepResult("TicketValidation", "FAILED", msg));
            opScope.Complete(false, msg);
            return new EmergencyDisableResult(false, msg, null, steps);
        }

        ticket = ticket.Trim();
        steps.Add(new DisableStepResult("TicketValidation", "OK", null));

        // 1. Protected principal check (fail-closed)
        var protectionResult = await _protectedPrincipalService.CheckAsync(target);
        if (protectionResult.CheckFailed)
        {
            var failMsg = $"Protected principal check failed: {protectionResult.Reason}";
            _operationTrace.Step("ProtectedPrincipalCheck", "Failed", details: new Dictionary<string, object?> { ["reason"] = protectionResult.Reason });
            steps.Add(new DisableStepResult("ProtectedPrincipalCheck", "BLOCKED", failMsg));
            opScope.Complete(false, failMsg);
            return new EmergencyDisableResult(false, failMsg, null, steps);
        }
        string? servicedNote = null;
        if (protectionResult.IsProtected)
        {
            // An authorised servicer may proceed. Protection is evaluated first and never
            // weakened - this only decides whether THIS operator may act on a target already
            // known to be protected.
            servicedNote = ProtectedPrincipalServicing.NoteFor(
                _servicers, actingUser, ServicerModuleId, protectionResult.MatchedRules);

            if (servicedNote is null)
            {
                var blockedMsg = $"Target is a protected principal: {protectionResult.Reason}";
                _operationTrace.Step("ProtectedPrincipalCheck", "Blocked", details: new Dictionary<string, object?> { ["matchedRules"] = protectionResult.MatchedRules });
                steps.Add(new DisableStepResult("ProtectedPrincipalCheck", "BLOCKED", blockedMsg));
                opScope.Complete(false, blockedMsg);
                return new EmergencyDisableResult(false, blockedMsg, null, steps);
            }

            // A serviced override is a visible step rather than a silent OK. The step trail is
            // operator-facing and diagnostic, NOT the durable record - the note also goes to the
            // audit event via LogAudit below, which is the store that answers "who permitted
            // this?" later (pps-3: it was in the trail and missing from the audit).
            _operationTrace.Step("ProtectedPrincipalCheck", "Serviced", details: new Dictionary<string, object?> { ["matchedRules"] = protectionResult.MatchedRules });
            steps.Add(new DisableStepResult("ProtectedPrincipalCheck", "SERVICED", servicedNote));
        }
        else
        {
            steps.Add(new DisableStepResult("ProtectedPrincipalCheck", "OK", null));
        }

        // 2. Get AD credentials from Delinea
        var adCreds = await _moduleCredentials.GetCredentialsAsync("EmergencyDisable", "disable compromised account");
        if (adCreds == null)
        {
            const string msg = "AD credentials unavailable from Delinea. Configure EmergencyDisable module's DelineaSecretId.";
            _operationTrace.Step("GetADCredentials", "Failed", backend: "Delinea");
            steps.Add(new DisableStepResult("GetADCredentials", "FAILED", msg));
            opScope.Complete(false, msg);
            return new EmergencyDisableResult(false, msg, null, steps);
        }
        steps.Add(new DisableStepResult("GetADCredentials", "OK", null));

        // 3. Get Graph credentials from Delinea
        var graphClient = await GetGraphClientAsync();
        if (graphClient == null)
        {
            const string msg = "Graph API credentials unavailable from Delinea. Configure EmergencyDisable module's GraphDelineaSecretId.";
            _operationTrace.Step("GetGraphCredentials", "Failed", backend: "Delinea");
            steps.Add(new DisableStepResult("GetGraphCredentials", "FAILED", msg));
            opScope.Complete(false, msg);
            return new EmergencyDisableResult(false, msg, null, steps);
        }
        steps.Add(new DisableStepResult("GetGraphCredentials", "OK", null));

        // 4. Build pre-action snapshot
        bool preAdEnabled;
        bool preEntraEnabled;
        bool isSynced;

        try
        {
            preAdEnabled = await ReadAdEnabledState(target, adCreds.Value);
        }
        catch (Exception ex)
        {
            var msg = $"Failed to read AD pre-state: {ex.Message}";
            _operationTrace.Step("ReadADPreState", "Failed", backend: "ActiveDirectory", exception: ex);
            steps.Add(new DisableStepResult("ReadADPreState", "FAILED", msg));
            opScope.Complete(false, msg);
            return new EmergencyDisableResult(false, msg, null, steps);
        }

        try
        {
            (preEntraEnabled, isSynced) = await ReadEntraEnabledState(target.UserPrincipalName, graphClient);
        }
        catch (Exception ex)
        {
            var msg = $"Failed to read Entra pre-state: {ex.Message}";
            _operationTrace.Step("ReadEntraPreState", "Failed", backend: "MicrosoftGraph", exception: ex);
            steps.Add(new DisableStepResult("ReadEntraPreState", "FAILED", msg));
            opScope.Complete(false, msg);
            return new EmergencyDisableResult(false, msg, null, steps);
        }

        steps.Add(new DisableStepResult("ReadPreState", "OK", $"AD={preAdEnabled}, Entra={preEntraEnabled}, Synced={isSynced}"));

        var snapshot = new DisableSnapshot
        {
            OperationId = opScope.OperationId,
            Timestamp = DateTime.UtcNow,
            Actor = performedBy,
            Ticket = ticket,
            TargetUpn = target.UserPrincipalName,
            TargetDn = target.DistinguishedName ?? "",
            TargetObjectGuid = target.ObjectGuid,
            PreState_AdEnabled = preAdEnabled,
            PreState_EntraEnabled = preEntraEnabled,
            Steps = steps
        };

        // 5. Write snapshot to disk BEFORE mutations
        try
        {
            PersistSnapshot(snapshot);
        }
        catch (Exception ex)
        {
            var msg = $"Failed to persist pre-action snapshot: {ex.Message}";
            _operationTrace.Step("PersistSnapshot", "Failed", exception: ex);
            steps.Add(new DisableStepResult("PersistSnapshot", "FAILED", msg));
            opScope.Complete(false, msg);
            return new EmergencyDisableResult(false, msg, snapshot, steps);
        }
        steps.Add(new DisableStepResult("PersistSnapshot", "OK", null));

        // 6. Execute disable steps
        DisableStepResult disableAdResult;
        DisableStepResult resetPwResult;
        LockdownOutcome lockdownOutcome;
        var lockdownOuDn = _moduleConfig.GetValue("EmergencyDisable", "LockdownOuDn");

        if (!await _adThrottle.WaitAsync(TimeSpan.FromSeconds(30)))
        {
            disableAdResult = new DisableStepResult("DisableAD", "FAILED", "AD throttle timeout.");
            resetPwResult = new DisableStepResult("ResetPassword", "FAILED", "AD throttle timeout.");
            steps.Add(disableAdResult);
            steps.Add(resetPwResult);

            // The disable did not happen, so the move must not either - the same rule as any
            // other failed disable, reached by the same decision function.
            var throttledDecision = DecideLockdown(moveToLockdownOu, false, lockdownOuDn, target.DistinguishedName);
            lockdownOutcome = throttledDecision.Outcome;
            steps.Add(LockdownStep(throttledDecision));
        }
        else
        {
            try
            {
                // 6a/6b/6c. Hold the AD slot across every AD mutation so another emergency op cannot interleave between disable, reset and the lockdown move.
                disableAdResult = await ExecuteDisableAD(target, adCreds.Value, adSlotHeld: true);
                steps.Add(disableAdResult);

                resetPwResult = await ExecuteResetPassword(target, adCreds.Value, adSlotHeld: true);
                steps.Add(resetPwResult);

                // The move is gated on the AD disable only. A failed password reset or Entra
                // step does not make the move wrong, but moving a still-enabled account into a
                // lockdown OU produces an object that lies to whoever reads it next.
                var lockdownDecision = DecideLockdown(
                    moveToLockdownOu, disableAdResult.Status == "OK", lockdownOuDn, target.DistinguishedName);

                if (lockdownDecision.Proceed)
                {
                    var (lockdownStep, outcome) = await ExecuteMoveToLockdownOu(
                        target, adCreds.Value, lockdownOuDn!.Trim(), adSlotHeld: true);
                    steps.Add(lockdownStep);
                    lockdownOutcome = outcome;
                }
                else
                {
                    lockdownOutcome = lockdownDecision.Outcome;
                    steps.Add(LockdownStep(lockdownDecision));
                }
            }
            finally
            {
                _adThrottle.Release();
            }
        }

        // 6c. Revoke Entra sign-in sessions
        var revokeResult = await ExecuteRevokeEntraSessions(target.UserPrincipalName, graphClient);
        steps.Add(revokeResult);

        // 6d. Disable Entra account. For synced users accountEnabled is on-prem mastered and
        // Entra rejects a direct PATCH; the AD disable (6a) is the master and propagates on the
        // next sync, so skip the doomed write and record it as not-applicable rather than failed.
        DisableStepResult disableEntraResult;
        if (ShouldSkipEntraDisable(isSynced))
        {
            const string skipReason = "Skipped: user is synced from on-prem AD; accountEnabled is on-prem mastered. Disabled via AD, propagates to Entra on next directory sync.";
            _operationTrace.Step("DisableEntra", "Skipped", backend: "MicrosoftGraph", target: target.UserPrincipalName, details: new Dictionary<string, object?> { ["reason"] = "onPremisesSyncEnabled" });
            disableEntraResult = new DisableStepResult("DisableEntra", "SKIPPED", skipReason);
        }
        else
        {
            disableEntraResult = await ExecuteDisableEntra(target.UserPrincipalName, graphClient);
        }
        steps.Add(disableEntraResult);

        // 7. Update snapshot with step results
        var finalSnapshot = snapshot with { Steps = new List<DisableStepResult>(steps) };
        try
        {
            PersistSnapshot(finalSnapshot);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update snapshot file with final step results for operation {OpId}", opScope.OperationId);
        }

        // Determine overall success. AD disable / reset / revoke must each be OK; the Entra
        // disable may be OK or SKIPPED (skipped for synced users - see ShouldSkipEntraDisable).
        var allMutationsSucceeded = IsOverallSuccess(
            disableAdResult.Status, resetPwResult.Status, revokeResult.Status, disableEntraResult.Status, lockdownOutcome);

        var overallSuccess = allMutationsSucceeded;
        var overallError = allMutationsSucceeded ? null : "One or more steps failed. Review step details and escalate for manual follow-up.";

        // 8. Audit the operation
        LogAudit(target, performedBy, ip, ticket, overallSuccess, steps, overallError, servicedNote,
            moveToLockdownOu, lockdownOutcome);

        // 9. Send security team notification
        await SendSecurityNotificationAsync(target, performedBy, ip, ticket, overallSuccess, steps,
            moveToLockdownOu, lockdownOutcome);

        // 10. Return result
        opScope.Complete(overallSuccess, overallError);
        return new EmergencyDisableResult(overallSuccess, overallError, finalSnapshot, steps);
    }

    private async Task<GraphTokenClient?> GetGraphClientAsync()
    {
        var secretIdStr = _moduleConfig.GetValue("EmergencyDisable", "GraphDelineaSecretId");
        if (!int.TryParse(secretIdStr, out var secretId) || secretId <= 0)
            return null;

        var fields = await _delineaService.GetSecretFieldsAsync(secretId);
        if (fields == null) return null;

        var tenantId = fields.GetValueOrDefault("Tenant ID") ?? "";
        var clientId = fields.GetValueOrDefault("Application ID") ?? "";
        var clientSecret = fields.GetValueOrDefault("Client Secret") ?? "";

        if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
            return null;

        return new GraphTokenClient(tenantId, clientId, clientSecret, _httpClientFactory.CreateClient("MicrosoftGraph"));
    }

    private async Task<bool> ReadAdEnabledState(ResolvedDirectoryPrincipal target, (string username, string password, string domain) creds)
    {
        if (!await _adThrottle.WaitAsync(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("AD throttle timeout while reading pre-state.");

        try
        {
            return await Task.Run(() =>
            {
                var iss = InitialSessionState.CreateDefault();
                iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
                using var runspace = RunspaceFactory.CreateRunspace(iss);
                runspace.Open();
                using var ps = PowerShell.Create();
                ps.Runspace = runspace;

                ps.AddCommand("Import-Module").AddParameter("Name", "ActiveDirectory").AddParameter("ErrorAction", "Stop");
                ps.Invoke();
                ps.Commands.Clear();

                var credential = CreatePSCredential(creds.username, creds.password, creds.domain);

                ps.AddCommand("Get-ADUser")
                  .AddParameter("Identity", target.DistinguishedName)
                  .AddParameter("Properties", new[] { "Enabled" })
                  .AddParameter("Credential", credential)
                  .AddParameter("ErrorAction", "Stop");
                var result = ps.Invoke();
                ps.Commands.Clear();

                if (ps.HadErrors || result.Count == 0)
                {
                    var err = ps.Streams.Error.FirstOrDefault()?.Exception?.Message ?? "Get-ADUser returned no results.";
                    throw new InvalidOperationException(err);
                }

                return result[0].Properties["Enabled"]?.Value as bool? ?? false;
            });
        }
        finally
        {
            _adThrottle.Release();
        }
    }

    private async Task<(bool Enabled, bool IsSynced)> ReadEntraEnabledState(string upn, GraphTokenClient graphClient)
    {
        var escaped = Uri.EscapeDataString(upn);
        using var doc = await graphClient.GetAsync($"/users/{escaped}?$select=accountEnabled,onPremisesSyncEnabled");
        if (doc == null)
            throw new InvalidOperationException("Graph API returned non-success reading user state.");

        if (!doc.RootElement.TryGetProperty("accountEnabled", out var prop))
            throw new InvalidOperationException("Graph API response missing accountEnabled property.");

        // onPremisesSyncEnabled is true only for directory-synced users; absent or null means
        // cloud-only. accountEnabled is on-prem mastered for synced users, so a direct Graph
        // PATCH of it is rejected - see ShouldSkipEntraDisable.
        var isSynced = doc.RootElement.TryGetProperty("onPremisesSyncEnabled", out var syncProp)
            && syncProp.ValueKind == JsonValueKind.True;

        return (prop.GetBoolean(), isSynced);
    }

    /// <summary>
    /// For directory-synced users, <c>accountEnabled</c> is mastered on-premises and Entra
    /// rejects a direct Graph PATCH of it ("on-premises mastered Directory Synced objects").
    /// The on-prem AD disable already handles the master copy, which propagates on the next
    /// directory sync, so the direct Entra disable is skipped for synced users. Pure for testing.
    /// </summary>
    internal static bool ShouldSkipEntraDisable(bool isSynced) => isSynced;

    /// <summary>
    /// Overall success accounting. The three on-prem/cloud-action mutations must each be OK; the
    /// Entra disable step may be OK or SKIPPED (skipped for synced users, see
    /// <see cref="ShouldSkipEntraDisable"/>). Pure for testing.
    /// </summary>
    internal static bool IsOverallSuccess(string disableAdStatus, string resetPwStatus, string revokeStatus, string disableEntraStatus, LockdownOutcome lockdown) =>
        disableAdStatus == "OK" &&
        resetPwStatus == "OK" &&
        revokeStatus == "OK" &&
        (disableEntraStatus == "OK" || disableEntraStatus == "SKIPPED") &&
        lockdown is LockdownOutcome.NotRequested or LockdownOutcome.Moved or LockdownOutcome.AlreadyInPlace;

    /// <summary>
    /// The maximum length of the AD <c>info</c> attribute (schema rangeUpper). The stamp refuses
    /// rather than truncating when an append would exceed it.
    /// </summary>
    internal const int InfoAttributeMaxLength = 1024;

    /// <summary>
    /// Decides the lockdown step without touching the directory. Everything decidable from the
    /// request flag, the disable outcome and the two DNs lives here so it is testable. Pure.
    /// </summary>
    internal static LockdownDecision DecideLockdown(
        bool requested, bool disableAdSucceeded, string? configuredOuDn, string? targetDn)
    {
        if (!requested)
            return new LockdownDecision(false, LockdownOutcome.NotRequested, null);

        if (string.IsNullOrWhiteSpace(configuredOuDn))
            return new LockdownDecision(false, LockdownOutcome.Failed,
                "Lockdown was requested but no lockdown OU is configured for this module. The account was NOT moved.");

        if (!disableAdSucceeded)
            return new LockdownDecision(false, LockdownOutcome.Skipped,
                "AD disable did not succeed; the account was not moved.");

        if (string.IsNullOrWhiteSpace(targetDn))
            return new LockdownDecision(false, LockdownOutcome.Failed,
                "Lockdown was requested but the target has no distinguished name. The account was NOT moved.");

        var currentParent = ParentDnOf(targetDn);
        if (currentParent != null && string.Equals(currentParent, configuredOuDn.Trim(), StringComparison.OrdinalIgnoreCase))
            return new LockdownDecision(false, LockdownOutcome.AlreadyInPlace,
                "Account is already in the lockdown OU; no move was needed.");

        return new LockdownDecision(true, LockdownOutcome.Moved, null);
    }

    /// <summary>
    /// The parent container of a distinguished name: everything after the first comma that is
    /// not escaped by a backslash. RFC 4514 allows an escaped comma inside an RDN value
    /// (CN=Doe\, Jane), so a naive Split(',') picks the wrong parent for those objects. Returns
    /// null when the DN has no parent component. Pure.
    /// </summary>
    internal static string? ParentDnOf(string? dn)
    {
        if (string.IsNullOrWhiteSpace(dn)) return null;

        var backslashes = 0;
        for (var i = 0; i < dn.Length; i++)
        {
            if (dn[i] == '\\')
            {
                backslashes++;
                continue;
            }

            if (dn[i] == ',' && backslashes % 2 == 0)
            {
                var parent = dn[(i + 1)..].Trim();
                return parent.Length == 0 ? null : parent;
            }

            backslashes = 0;
        }

        return null;
    }

    /// <summary>
    /// Builds the new <c>info</c> value: the existing content, then the stamp on its own line.
    /// ADUC renders info as the multi-line Notes box, so the separator is CRLF, matching the
    /// source script (Set-AccountSecurityHold.ps1). A prior stamp is never removed - nothing in
    /// this app may parse the stamp (owner ruling 2026-10-05), so it cannot be recognised to be
    /// replaced. Refuses rather than truncating when the result would exceed the schema limit.
    /// Pure.
    /// </summary>
    internal static LockdownStampResult BuildLockdownStamp(
        string? existingInfo, string previousParentDn, DateTime timestamp)
    {
        var line = $"[EMERGENCY DISABLE {timestamp:yyyy-MM-dd}] previous OU: {previousParentDn}";

        var combined = string.IsNullOrEmpty(existingInfo)
            ? line
            : existingInfo + "\r\n" + line;

        if (combined.Length > InfoAttributeMaxLength)
            return new LockdownStampResult(false, null,
                $"Appending the lockdown stamp would exceed the {InfoAttributeMaxLength}-character limit of the info attribute " +
                $"({combined.Length} characters). Nothing was written; the previous OU is recorded in the operation snapshot and the audit entry.");

        return new LockdownStampResult(true, combined, null);
    }

    private async Task<DisableStepResult> ExecuteDisableAD(ResolvedDirectoryPrincipal target, (string username, string password, string domain) creds, bool adSlotHeld = false)
    {
        if (!adSlotHeld && !await _adThrottle.WaitAsync(TimeSpan.FromSeconds(30)))
            return new DisableStepResult("DisableAD", "FAILED", "AD throttle timeout.");

        try
        {
            return await Task.Run(() =>
            {
                var iss = InitialSessionState.CreateDefault();
                iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
                using var runspace = RunspaceFactory.CreateRunspace(iss);
                runspace.Open();
                using var ps = PowerShell.Create();
                ps.Runspace = runspace;

                ps.AddCommand("Import-Module").AddParameter("Name", "ActiveDirectory").AddParameter("ErrorAction", "Stop");
                ps.Invoke();
                ps.Commands.Clear();

                var credential = CreatePSCredential(creds.username, creds.password, creds.domain);

                var verifyError = VerifyBoundObject(ps, target, credential);
                if (verifyError != null)
                {
                    _operationTrace.Step("DisableAD", "Failed", backend: "ActiveDirectory", details: new Dictionary<string, object?> { ["reason"] = verifyError });
                    return new DisableStepResult("DisableAD", "FAILED", verifyError);
                }

                ps.AddCommand("Set-ADUser")
                  .AddParameter("Identity", target.DistinguishedName)
                  .AddParameter("Enabled", false)
                  .AddParameter("Credential", credential)
                  .AddParameter("ErrorAction", "Stop");
                ps.Invoke();
                ps.Commands.Clear();

                if (ps.HadErrors)
                {
                    var err = ps.Streams.Error.FirstOrDefault()?.Exception?.Message ?? "Set-ADUser -Enabled $false failed.";
                    _operationTrace.Step("DisableAD", "Failed", backend: "ActiveDirectory", command: "Set-ADUser -Enabled $false");
                    return new DisableStepResult("DisableAD", "FAILED", err);
                }

                _operationTrace.Step("DisableAD", "Success", backend: "ActiveDirectory", command: "Set-ADUser -Enabled $false", target: target.DistinguishedName);
                return new DisableStepResult("DisableAD", "OK", null);
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DisableAD step failed for {Target}", target.UserPrincipalName);
            _operationTrace.Step("DisableAD", "Failed", backend: "ActiveDirectory", exception: ex);
            return new DisableStepResult("DisableAD", "FAILED", ex.Message);
        }
        finally
        {
            if (!adSlotHeld)
                _adThrottle.Release();
        }
    }

    /// <summary>
    /// Moves the target into the configured lockdown OU and appends the previous parent OU to
    /// its info attribute. Returns the step result and the outcome the caller folds into overall
    /// success. The stamp is deliberately NOT allowed to fail the move: its information is held
    /// authoritatively in the snapshot and the audit, so a stamp failure is reported loudly in
    /// the step detail while the move itself still counts as done.
    /// </summary>
    private async Task<(DisableStepResult Step, LockdownOutcome Outcome)> ExecuteMoveToLockdownOu(
        ResolvedDirectoryPrincipal target,
        (string username, string password, string domain) creds,
        string lockdownOuDn,
        bool adSlotHeld = false)
    {
        if (!adSlotHeld && !await _adThrottle.WaitAsync(TimeSpan.FromSeconds(30)))
            return (new DisableStepResult("LockdownMove", "FAILED", "AD throttle timeout."), LockdownOutcome.Failed);

        try
        {
            return await Task.Run(() =>
            {
                var iss = InitialSessionState.CreateDefault();
                iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
                using var runspace = RunspaceFactory.CreateRunspace(iss);
                runspace.Open();
                using var ps = PowerShell.Create();
                ps.Runspace = runspace;

                ps.AddCommand("Import-Module").AddParameter("Name", "ActiveDirectory").AddParameter("ErrorAction", "Stop");
                ps.Invoke();
                ps.Commands.Clear();

                var credential = CreatePSCredential(creds.username, creds.password, creds.domain);

                var verifyError = VerifyBoundObject(ps, target, credential);
                if (verifyError != null)
                {
                    _operationTrace.Step("LockdownMove", "Failed", backend: "ActiveDirectory", details: new Dictionary<string, object?> { ["reason"] = verifyError });
                    return (new DisableStepResult("LockdownMove", "FAILED", verifyError), LockdownOutcome.Failed);
                }

                // Confirm the destination exists rather than letting Move-ADObject guess or
                // fail obscurely. A missing OU fails the step closed - it never picks another.
                ps.AddCommand("Get-ADOrganizationalUnit")
                  .AddParameter("Identity", lockdownOuDn)
                  .AddParameter("Credential", credential)
                  .AddParameter("ErrorAction", "Stop");
                var ouLookup = ps.Invoke();
                var ouLookupFailed = ps.HadErrors || ouLookup.Count == 0;
                var ouLookupError = ps.Streams.Error.FirstOrDefault()?.Exception?.Message;
                ps.Commands.Clear();
                ps.Streams.Error.Clear();

                if (ouLookupFailed)
                {
                    var err = $"Configured lockdown OU could not be read: {ouLookupError ?? "no such organizational unit."} The account was NOT moved.";
                    _operationTrace.Step("LockdownMove", "Failed", backend: "ActiveDirectory", command: "Get-ADOrganizationalUnit");
                    return (new DisableStepResult("LockdownMove", "FAILED", err), LockdownOutcome.Failed);
                }

                var previousParent = ParentDnOf(target.DistinguishedName) ?? "(unknown)";

                ps.AddCommand("Move-ADObject")
                  .AddParameter("Identity", target.DistinguishedName)
                  .AddParameter("TargetPath", lockdownOuDn)
                  .AddParameter("Credential", credential)
                  .AddParameter("ErrorAction", "Stop");
                ps.Invoke();
                var moveFailed = ps.HadErrors;
                var moveError = ps.Streams.Error.FirstOrDefault()?.Exception?.Message;
                ps.Commands.Clear();
                ps.Streams.Error.Clear();

                if (moveFailed)
                {
                    var err = moveError ?? "Move-ADObject failed.";
                    _operationTrace.Step("LockdownMove", "Failed", backend: "ActiveDirectory", command: "Move-ADObject");
                    return (new DisableStepResult("LockdownMove", "FAILED", err), LockdownOutcome.Failed);
                }

                _operationTrace.Step("LockdownMove", "Success", backend: "ActiveDirectory", command: "Move-ADObject", target: lockdownOuDn);

                // The DN changed with the move, so every write from here binds by ObjectGUID.
                var stampDetail = StampPreviousOu(ps, target, credential, previousParent);

                return (new DisableStepResult("LockdownMove", "OK", $"Moved to {lockdownOuDn}. {stampDetail}"), LockdownOutcome.Moved);
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LockdownMove step failed for {Target}", target.UserPrincipalName);
            _operationTrace.Step("LockdownMove", "Failed", backend: "ActiveDirectory", exception: ex);
            return (new DisableStepResult("LockdownMove", "FAILED", ex.Message), LockdownOutcome.Failed);
        }
        finally
        {
            if (!adSlotHeld)
                _adThrottle.Release();
        }
    }

    /// <summary>
    /// Appends the previous parent OU to the moved object's info attribute, bound by ObjectGUID
    /// because the DN changed when the object moved. Returns a human-readable detail string; it
    /// never throws out of the move step, because the stamp is a breadcrumb and not the record.
    /// </summary>
    private string StampPreviousOu(
        PowerShell ps, ResolvedDirectoryPrincipal target, PSCredential credential, string previousParentDn)
    {
        if (string.IsNullOrEmpty(target.ObjectGuid))
            return "Previous OU NOT stamped: the target has no recorded ObjectGUID to bind the write to.";

        try
        {
            ps.AddCommand("Get-ADUser")
              .AddParameter("Identity", target.ObjectGuid)
              .AddParameter("Properties", new[] { "info" })
              .AddParameter("Credential", credential)
              .AddParameter("ErrorAction", "Stop");
            var reRead = ps.Invoke();
            var readFailed = ps.HadErrors || reRead.Count == 0;
            var readError = ps.Streams.Error.FirstOrDefault()?.Exception?.Message;
            ps.Commands.Clear();
            ps.Streams.Error.Clear();

            if (readFailed)
                return $"Previous OU NOT stamped: could not re-read the moved object ({readError ?? "no object returned"}).";

            var existingInfo = reRead[0].Properties["info"]?.Value?.ToString();
            var stamp = BuildLockdownStamp(existingInfo, previousParentDn, DateTime.UtcNow);
            if (!stamp.Ok)
            {
                _operationTrace.Step("LockdownStamp", "Failed", backend: "ActiveDirectory", details: new Dictionary<string, object?> { ["reason"] = stamp.Error });
                return $"Previous OU NOT stamped: {stamp.Error}";
            }

            ps.AddCommand("Set-ADUser")
              .AddParameter("Identity", target.ObjectGuid)
              .AddParameter("Replace", new Hashtable { ["info"] = stamp.Value })
              .AddParameter("Credential", credential)
              .AddParameter("ErrorAction", "Stop");
            ps.Invoke();
            var writeFailed = ps.HadErrors;
            var writeError = ps.Streams.Error.FirstOrDefault()?.Exception?.Message;
            ps.Commands.Clear();
            ps.Streams.Error.Clear();

            if (writeFailed)
            {
                _operationTrace.Step("LockdownStamp", "Failed", backend: "ActiveDirectory", command: "Set-ADUser -Replace info");
                return $"Previous OU NOT stamped: {writeError ?? "Set-ADUser -Replace info failed."}";
            }

            _operationTrace.Step("LockdownStamp", "Success", backend: "ActiveDirectory", command: "Set-ADUser -Replace info");
            return $"Previous OU {previousParentDn} appended to the Notes (info) attribute.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lockdown stamp failed for {Target}", target.UserPrincipalName);
            _operationTrace.Step("LockdownStamp", "Failed", backend: "ActiveDirectory", exception: ex);
            return $"Previous OU NOT stamped: {ex.Message}";
        }
    }

    private async Task<DisableStepResult> ExecuteResetPassword(ResolvedDirectoryPrincipal target, (string username, string password, string domain) creds, bool adSlotHeld = false)
    {
        if (!adSlotHeld && !await _adThrottle.WaitAsync(TimeSpan.FromSeconds(30)))
            return new DisableStepResult("ResetPassword", "FAILED", "AD throttle timeout.");

        try
        {
            return await Task.Run(() =>
            {
                var iss = InitialSessionState.CreateDefault();
                iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
                using var runspace = RunspaceFactory.CreateRunspace(iss);
                runspace.Open();
                using var ps = PowerShell.Create();
                ps.Runspace = runspace;

                ps.AddCommand("Import-Module").AddParameter("Name", "ActiveDirectory").AddParameter("ErrorAction", "Stop");
                ps.Invoke();
                ps.Commands.Clear();

                var credential = CreatePSCredential(creds.username, creds.password, creds.domain);

                var verifyError = VerifyBoundObject(ps, target, credential);
                if (verifyError != null)
                {
                    _operationTrace.Step("ResetPassword", "Failed", backend: "ActiveDirectory", details: new Dictionary<string, object?> { ["reason"] = verifyError });
                    return new DisableStepResult("ResetPassword", "FAILED", verifyError);
                }

                var newPassword = GenerateRandomPassword(32);
                var secureNewPassword = new System.Security.SecureString();
                foreach (var c in newPassword) secureNewPassword.AppendChar(c);

                ps.AddCommand("Set-ADAccountPassword")
                  .AddParameter("Identity", target.DistinguishedName)
                  .AddParameter("NewPassword", secureNewPassword)
                  .AddParameter("Reset")
                  .AddParameter("Credential", credential)
                  .AddParameter("ErrorAction", "Stop");
                ps.Invoke();
                ps.Commands.Clear();

                if (ps.HadErrors)
                {
                    var err = ps.Streams.Error.FirstOrDefault()?.Exception?.Message ?? "Set-ADAccountPassword failed.";
                    _operationTrace.Step("ResetPassword", "Failed", backend: "ActiveDirectory", command: "Set-ADAccountPassword -Reset");
                    return new DisableStepResult("ResetPassword", "FAILED", err);
                }

                _operationTrace.Step("ResetPassword", "Success", backend: "ActiveDirectory", command: "Set-ADAccountPassword -Reset", target: target.DistinguishedName);
                return new DisableStepResult("ResetPassword", "OK", null);
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ResetPassword step failed for {Target}", target.UserPrincipalName);
            _operationTrace.Step("ResetPassword", "Failed", backend: "ActiveDirectory", exception: ex);
            return new DisableStepResult("ResetPassword", "FAILED", ex.Message);
        }
        finally
        {
            if (!adSlotHeld)
                _adThrottle.Release();
        }
    }

    private async Task<DisableStepResult> ExecuteRevokeEntraSessions(string upn, GraphTokenClient graphClient)
    {
        try
        {
            var escaped = Uri.EscapeDataString(upn);
            var success = await graphClient.PostNoContentAsync($"/users/{escaped}/revokeSignInSessions");
            if (!success)
            {
                _operationTrace.Step("RevokeEntraSessions", "Failed", backend: "MicrosoftGraph", command: "POST /users/{upn}/revokeSignInSessions");
                return new DisableStepResult("RevokeEntraSessions", "FAILED", "Graph API returned non-success for revokeSignInSessions.");
            }

            _operationTrace.Step("RevokeEntraSessions", "Success", backend: "MicrosoftGraph", command: "POST /users/{upn}/revokeSignInSessions", target: upn);
            return new DisableStepResult("RevokeEntraSessions", "OK", null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RevokeEntraSessions step failed for {Upn}", upn);
            _operationTrace.Step("RevokeEntraSessions", "Failed", backend: "MicrosoftGraph", exception: ex);
            return new DisableStepResult("RevokeEntraSessions", "FAILED", ex.Message);
        }
    }

    private async Task<DisableStepResult> ExecuteDisableEntra(string upn, GraphTokenClient graphClient)
    {
        try
        {
            var escaped = Uri.EscapeDataString(upn);
            var (success, status, safeError) = await graphClient.PatchWithStatusAsync($"/users/{escaped}", new { accountEnabled = false });
            if (!success)
            {
                var detail = $"Graph PATCH disable returned {(int)status} {status}"
                    + (string.IsNullOrWhiteSpace(safeError) ? "." : $": {safeError}");
                _operationTrace.Step("DisableEntra", "Failed", backend: "MicrosoftGraph", command: "PATCH /users/{upn} accountEnabled=false",
                    details: new Dictionary<string, object?> { ["status"] = (int)status, ["graphError"] = safeError });
                return new DisableStepResult("DisableEntra", "FAILED", detail);
            }

            _operationTrace.Step("DisableEntra", "Success", backend: "MicrosoftGraph", command: "PATCH /users/{upn} accountEnabled=false", target: upn);
            return new DisableStepResult("DisableEntra", "OK", null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DisableEntra step failed for {Upn}", upn);
            _operationTrace.Step("DisableEntra", "Failed", backend: "MicrosoftGraph", exception: ex);
            return new DisableStepResult("DisableEntra", "FAILED", ex.Message);
        }
    }

    private void PersistSnapshot(DisableSnapshot snapshot)
    {
        var logRoot = AuditLogRoot.Require(_config);
        var snapshotDir = Path.Combine(logRoot, "ExchangeAdminWeb", "snapshots");
        Directory.CreateDirectory(snapshotDir);

        var filePath = Path.Combine(snapshotDir, $"{snapshot.OperationId}.json");
        var tempPath = Path.Combine(snapshotDir, $"{snapshot.OperationId}.{Guid.NewGuid():N}.tmp");

        var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var json = JsonSerializer.Serialize(snapshot, options);

        try
        {
            File.WriteAllText(tempPath, json);
            if (File.Exists(filePath))
                File.Replace(tempPath, filePath, null);
            else
                File.Move(tempPath, filePath);
        }
        finally
        {
            if (File.Exists(tempPath))
                try { File.Delete(tempPath); } catch { }
        }

        _logger.LogInformation("Snapshot persisted to {Path} for operation {OpId}", filePath, snapshot.OperationId);
    }

    /// <summary>
    /// The step row for a lockdown outcome decided without touching AD. Every run gets a row,
    /// including a declined one: an unticked box is the operator declining the protection the
    /// step exists to provide, which is a decision and not an absence.
    /// </summary>
    private static DisableStepResult LockdownStep(LockdownDecision decision) =>
        new("LockdownMove", StepStatusFor(decision.Outcome),
            decision.Detail ?? "Lockdown move was not requested by the operator.");

    internal static string StepStatusFor(LockdownOutcome outcome) => outcome switch
    {
        LockdownOutcome.Moved => "OK",
        LockdownOutcome.AlreadyInPlace => "SKIPPED",
        LockdownOutcome.Skipped => "SKIPPED",
        LockdownOutcome.Failed => "FAILED",
        _ => "NOT REQUESTED"
    };

    /// <summary>
    /// Table row class for a step status in the result table (edl-1). Declining the lockdown is
    /// a decision the operator is entitled to make, so NOT REQUESTED is neutral and must never
    /// land in the danger bucket beside a genuine failure - the page previously painted every
    /// status other than OK and SKIPPED red, which made an authorised choice look like an error
    /// on every run. Lives here rather than inline in the page so it is testable. Pure.
    /// </summary>
    internal static string RowClassFor(string status) => status switch
    {
        "OK" => "table-success",
        "SKIPPED" or "NOT REQUESTED" => "table-secondary",
        _ => "table-danger"
    };

    private void LogAudit(
        ResolvedDirectoryPrincipal target,
        string performedBy,
        string ip,
        string ticket,
        bool success,
        List<DisableStepResult> steps,
        string? errorDetail,
        string? servicedNote,
        bool lockdownRequested,
        LockdownOutcome lockdownOutcome)
    {
        var stepSummary = steps
            .Where(s => s.Step is "DisableAD" or "ResetPassword" or "RevokeEntraSessions" or "DisableEntra" or "LockdownMove")
            .Select(s => $"{s.Step}={s.Status}")
            .ToArray();

        // "Disabled WITHOUT lockdown" is a materially different act from "disabled and locked
        // down" - the operator declined the protection, or it did not land - so the durable
        // record carries both the request and the outcome, on success as well as on failure.
        var extra = ProtectedPrincipalServicing.Extra(servicedNote) ?? new Dictionary<string, object?>();
        extra["lockdownRequested"] = lockdownRequested;
        extra["lockdownOutcome"] = lockdownOutcome.ToString();

        // The operation trace already records a "Serviced" step, but the trace is diagnostic and
        // the audit log is the durable record - different stores, different retention, different
        // readers. An override present only in the trace cannot answer "who permitted this?" from
        // the audit log, which is where that question gets asked (pps-3).
        //
        // It rides extra, never errorDetail: a serviced disable SUCCEEDS, and errorDetail is
        // written as null on success.
        _audit.LogModuleAction(
            performedBy,
            ip,
            "EmergencyDisable",
            "EmergencyDisable",
            target.UserPrincipalName,
            success,
            ticket,
            errorDetail ?? (success ? null : string.Join("; ", stepSummary)),
            extra);
    }

    private async Task SendSecurityNotificationAsync(
        ResolvedDirectoryPrincipal target,
        string performedBy,
        string ip,
        string ticket,
        bool success,
        List<DisableStepResult> steps,
        bool lockdownRequested,
        LockdownOutcome lockdownOutcome)
    {
        try
        {
            var notifyEmail = _moduleConfig.GetValue("EmergencyDisable", "NotifySecurityTeam");
            if (string.IsNullOrWhiteSpace(notifyEmail))
            {
                _logger.LogWarning("EmergencyDisable NotifySecurityTeam email not configured, skipping notification");
                return;
            }

            var stepDetails = new Dictionary<string, string>
            {
                ["Target UPN"] = target.UserPrincipalName,
                ["Target DN"] = target.DistinguishedName ?? "(unknown)",
                // Stated outright rather than left to be inferred from the step rows: the
                // reader of this mail needs to know whether the account can be re-enabled by
                // routine helpdesk rights.
                ["Lockdown"] = LockdownSummary(lockdownRequested, lockdownOutcome),
            };

            foreach (var step in steps.Where(s => s.Step is "DisableAD" or "ResetPassword" or "RevokeEntraSessions" or "DisableEntra" or "LockdownMove"))
            {
                stepDetails[step.Step] = step.Status + (step.Detail != null ? $" - {step.Detail}" : "");
            }

            await _email.SendAdminNotificationAsync(
                performedBy,
                ip,
                "EmergencyDisable",
                success,
                ticket,
                stepDetails,
                success ? null : "One or more steps failed. Manual follow-up required.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send security team notification for EmergencyDisable of {Target}", target.UserPrincipalName);
        }
    }

    /// <summary>
    /// One unambiguous line for the security team: was the account locked down or not. Pure.
    /// </summary>
    internal static string LockdownSummary(bool requested, LockdownOutcome outcome) => outcome switch
    {
        LockdownOutcome.Moved => "DISABLED AND LOCKED DOWN - the account was moved to the lockdown OU.",
        LockdownOutcome.AlreadyInPlace => "DISABLED AND LOCKED DOWN - the account was already in the lockdown OU.",
        LockdownOutcome.NotRequested => "DISABLED WITHOUT LOCKDOWN - the operator did not request the lockdown move.",
        _ => requested
            ? "DISABLED WITHOUT LOCKDOWN - the lockdown move was requested but did NOT happen. The account may still be re-enabled through routine delegation. Manual follow-up required."
            : "DISABLED WITHOUT LOCKDOWN - the operator did not request the lockdown move."
    };

    private static string? VerifyBoundObject(PowerShell ps, ResolvedDirectoryPrincipal target, PSCredential credential)
    {
        ps.AddCommand("Get-ADUser")
          .AddParameter("Identity", target.DistinguishedName)
          .AddParameter("Properties", new[] { "ObjectGUID" })
          .AddParameter("Credential", credential)
          .AddParameter("ErrorAction", "Stop");
        var reRead = ps.Invoke();
        ps.Commands.Clear();

        if (reRead.Count == 0)
            return "Target object no longer exists at the recorded DN.";

        if (!string.IsNullOrEmpty(target.ObjectGuid))
        {
            var freshGuid = reRead[0].Properties["ObjectGUID"]?.Value?.ToString();
            if (!string.Equals(freshGuid, target.ObjectGuid, StringComparison.OrdinalIgnoreCase))
                return $"Bound-object mismatch: expected GUID {target.ObjectGuid}, found {freshGuid}.";
        }

        return null;
    }

    private static string GenerateRandomPassword(int length)
    {
        const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!@#$%^&*()-_=+[]{}|;:,.<>?";
        var password = new char[length];
        for (int i = 0; i < length; i++)
            password[i] = chars[RandomNumberGenerator.GetInt32(chars.Length)];
        return new string(password);
    }

    private static PSCredential CreatePSCredential(string username, string password, string domain)
    {
        var fullUsername = username.Contains('\\') || username.Contains('@')
            ? username : $"{domain}\\{username}";
        var securePassword = new System.Security.SecureString();
        foreach (var c in password) securePassword.AppendChar(c);
        return new PSCredential(fullUsername, securePassword);
    }
}
