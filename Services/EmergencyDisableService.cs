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

public class EmergencyDisableService
{
    private readonly ModuleCredentialService _moduleCredentials;
    private readonly ModuleConfigService _moduleConfig;
    private readonly ProtectedPrincipalService _protectedPrincipalService;
    private readonly OperationTraceService _operationTrace;
    private readonly AuditService _audit;
    private readonly EmailService _email;
    private readonly DelineaService _delineaService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IWebHostEnvironment _env;
    private readonly IConfiguration _config;
    private readonly ILogger<EmergencyDisableService> _logger;

    private static readonly SemaphoreSlim _adThrottle = new(2, 2);

    public EmergencyDisableService(
        ModuleCredentialService moduleCredentials,
        ModuleConfigService moduleConfig,
        ProtectedPrincipalService protectedPrincipalService,
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
        _operationTrace = operationTrace;
        _audit = audit;
        _email = email;
        _delineaService = delineaService;
        _httpClientFactory = httpClientFactory;
        _env = env;
        _config = config;
        _logger = logger;
    }

    public async Task<EmergencyDisableResult> DisableAsync(
        ResolvedDirectoryPrincipal target, string ticket, string performedBy, string ip)
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
        if (protectionResult.IsProtected)
        {
            var blockedMsg = $"Target is a protected principal: {protectionResult.Reason}";
            _operationTrace.Step("ProtectedPrincipalCheck", "Blocked", details: new Dictionary<string, object?> { ["matchedRules"] = protectionResult.MatchedRules });
            steps.Add(new DisableStepResult("ProtectedPrincipalCheck", "BLOCKED", blockedMsg));
            opScope.Complete(false, blockedMsg);
            return new EmergencyDisableResult(false, blockedMsg, null, steps);
        }
        steps.Add(new DisableStepResult("ProtectedPrincipalCheck", "OK", null));

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
            preEntraEnabled = await ReadEntraEnabledState(target.UserPrincipalName, graphClient);
        }
        catch (Exception ex)
        {
            var msg = $"Failed to read Entra pre-state: {ex.Message}";
            _operationTrace.Step("ReadEntraPreState", "Failed", backend: "MicrosoftGraph", exception: ex);
            steps.Add(new DisableStepResult("ReadEntraPreState", "FAILED", msg));
            opScope.Complete(false, msg);
            return new EmergencyDisableResult(false, msg, null, steps);
        }

        steps.Add(new DisableStepResult("ReadPreState", "OK", $"AD={preAdEnabled}, Entra={preEntraEnabled}"));

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
        if (!await _adThrottle.WaitAsync(TimeSpan.FromSeconds(30)))
        {
            disableAdResult = new DisableStepResult("DisableAD", "FAILED", "AD throttle timeout.");
            resetPwResult = new DisableStepResult("ResetPassword", "FAILED", "AD throttle timeout.");
            steps.Add(disableAdResult);
            steps.Add(resetPwResult);
        }
        else
        {
            try
            {
                // 6a/6b. Hold the AD slot across both AD mutations so another emergency op cannot interleave between disable and reset.
                disableAdResult = await ExecuteDisableAD(target, adCreds.Value, adSlotHeld: true);
                steps.Add(disableAdResult);

                resetPwResult = await ExecuteResetPassword(target, adCreds.Value, adSlotHeld: true);
                steps.Add(resetPwResult);
            }
            finally
            {
                _adThrottle.Release();
            }
        }

        // 6c. Revoke Entra sign-in sessions
        var revokeResult = await ExecuteRevokeEntraSessions(target.UserPrincipalName, graphClient);
        steps.Add(revokeResult);

        // 6d. Disable Entra account
        var disableEntraResult = await ExecuteDisableEntra(target.UserPrincipalName, graphClient);
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

        // Determine overall success (all mutation steps must succeed)
        var allMutationsSucceeded =
            disableAdResult.Status == "OK" &&
            resetPwResult.Status == "OK" &&
            revokeResult.Status == "OK" &&
            disableEntraResult.Status == "OK";

        var overallSuccess = allMutationsSucceeded;
        var overallError = allMutationsSucceeded ? null : "One or more steps failed. Review step details and escalate for manual follow-up.";

        // 8. Audit the operation
        LogAudit(target, performedBy, ip, ticket, overallSuccess, steps, overallError);

        // 9. Send security team notification
        await SendSecurityNotificationAsync(target, performedBy, ip, ticket, overallSuccess, steps);

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

    private async Task<bool> ReadEntraEnabledState(string upn, GraphTokenClient graphClient)
    {
        var escaped = Uri.EscapeDataString(upn);
        using var doc = await graphClient.GetAsync($"/users/{escaped}?$select=accountEnabled");
        if (doc == null)
            throw new InvalidOperationException("Graph API returned non-success reading user state.");

        if (doc.RootElement.TryGetProperty("accountEnabled", out var prop))
            return prop.GetBoolean();

        throw new InvalidOperationException("Graph API response missing accountEnabled property.");
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
            var success = await graphClient.PatchAsync($"/users/{escaped}", new { accountEnabled = false });
            if (!success)
            {
                _operationTrace.Step("DisableEntra", "Failed", backend: "MicrosoftGraph", command: "PATCH /users/{upn} accountEnabled=false");
                return new DisableStepResult("DisableEntra", "FAILED", "Graph API returned non-success for disable user.");
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
        var logRoot = _config["Audit:LogRoot"] ?? @"E:\WWWOutput";
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

    private void LogAudit(
        ResolvedDirectoryPrincipal target,
        string performedBy,
        string ip,
        string ticket,
        bool success,
        List<DisableStepResult> steps,
        string? errorDetail)
    {
        var stepSummary = steps
            .Where(s => s.Step is "DisableAD" or "ResetPassword" or "RevokeEntraSessions" or "DisableEntra")
            .Select(s => $"{s.Step}={s.Status}")
            .ToArray();

        _audit.LogModuleAction(
            performedBy,
            ip,
            "EmergencyDisable",
            "EmergencyDisable",
            target.UserPrincipalName,
            success,
            ticket,
            errorDetail ?? (success ? null : string.Join("; ", stepSummary)));
    }

    private async Task SendSecurityNotificationAsync(
        ResolvedDirectoryPrincipal target,
        string performedBy,
        string ip,
        string ticket,
        bool success,
        List<DisableStepResult> steps)
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
            };

            foreach (var step in steps.Where(s => s.Step is "DisableAD" or "ResetPassword" or "RevokeEntraSessions" or "DisableEntra"))
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
