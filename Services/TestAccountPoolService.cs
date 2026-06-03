using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Security.Cryptography;
using System.Text.Json;

namespace ExchangeAdminWeb.Services;

public sealed record TestAccountPoolEntry(
    string Key,
    string Source,
    string DisplayName,
    string UserPrincipalName,
    string? SamAccountName,
    string? Mail,
    string? DistinguishedName,
    string? ObjectGuid,
    string? EntraObjectId,
    bool Enabled,
    DateTime? ExpiresUtc,
    string Status,
    string Profile,
    bool SupportsCheckout,
    string? StatusDetail);

public sealed record TestAccountPoolOperationResult(
    bool Success,
    string Message,
    string? Password = null,
    DateTime? ExpiresUtc = null);

public sealed class TestAccountPoolService
{
    private const string ModuleId = "TestAccountPool";
    private static readonly SemaphoreSlim AdThrottle = new(2, 2);

    private readonly ModuleConfigService _moduleConfig;
    private readonly ModuleCredentialService _moduleCredentials;
    private readonly DelineaService _delineaService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AuditService _audit;
    private readonly EmailService _email;
    private readonly OperationTraceService _operationTrace;
    private readonly ILogger<TestAccountPoolService> _logger;

    public TestAccountPoolService(
        ModuleConfigService moduleConfig,
        ModuleCredentialService moduleCredentials,
        DelineaService delineaService,
        IHttpClientFactory httpClientFactory,
        AuditService audit,
        EmailService email,
        OperationTraceService operationTrace,
        ILogger<TestAccountPoolService> logger)
    {
        _moduleConfig = moduleConfig;
        _moduleCredentials = moduleCredentials;
        _delineaService = delineaService;
        _httpClientFactory = httpClientFactory;
        _audit = audit;
        _email = email;
        _operationTrace = operationTrace;
        _logger = logger;
    }

    public bool HasAnyPoolConfigured =>
        IsOnPremPoolConfigured || IsEntraPoolConfigured;

    public bool IsOnPremPoolConfigured =>
        !string.IsNullOrWhiteSpace(GetConfig("OnPremPoolGroup"))
        && !string.IsNullOrWhiteSpace(GetConfig("DelineaSecretId"));

    public bool IsEntraPoolConfigured =>
        !string.IsNullOrWhiteSpace(GetConfig("EntraPoolGroupId"))
        && !string.IsNullOrWhiteSpace(GetConfig("GraphDelineaSecretId"));

    public int DefaultCheckoutHours => ClampCheckoutHours(ParseInt(GetConfig("DefaultCheckoutHours"), 24));

    public int MaxCheckoutHours => Math.Clamp(ParseInt(GetConfig("MaxCheckoutHours"), 168), 1, 720);

    public async Task<List<TestAccountPoolEntry>> GetPoolAsync()
    {
        var results = new List<TestAccountPoolEntry>();

        if (IsOnPremPoolConfigured)
        {
            try
            {
                results.AddRange(await GetOnPremPoolAsync());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load on-prem test account pool");
                results.Add(new TestAccountPoolEntry(
                    Key: "onprem-load-error",
                    Source: "OnPremAD",
                    DisplayName: "On-prem pool unavailable",
                    UserPrincipalName: "",
                    SamAccountName: null,
                    Mail: null,
                    DistinguishedName: null,
                    ObjectGuid: null,
                    EntraObjectId: null,
                    Enabled: false,
                    ExpiresUtc: null,
                    Status: "Error",
                    Profile: "On-prem AD",
                    SupportsCheckout: false,
                    StatusDetail: ex.Message));
            }
        }

        if (IsEntraPoolConfigured)
        {
            try
            {
                results.AddRange(await GetEntraPoolAsync());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load Entra test account pool");
                results.Add(new TestAccountPoolEntry(
                    Key: "entra-load-error",
                    Source: "EntraID",
                    DisplayName: "Entra pool unavailable",
                    UserPrincipalName: "",
                    SamAccountName: null,
                    Mail: null,
                    DistinguishedName: null,
                    ObjectGuid: null,
                    EntraObjectId: null,
                    Enabled: false,
                    ExpiresUtc: null,
                    Status: "Error",
                    Profile: "Entra ID",
                    SupportsCheckout: false,
                    StatusDetail: ex.Message));
            }
        }

        return results
            .Where(r => r.Status != "Error" || !results.Any(x => x.Source == r.Source && x.Status != "Error"))
            .OrderBy(r => r.Source)
            .ThenBy(r => r.DisplayName)
            .ToList();
    }

    public async Task<TestAccountPoolOperationResult> CheckoutAsync(
        TestAccountPoolEntry account,
        int requestedHours,
        string performedBy,
        string ip,
        string ticket)
    {
        if (account.Source != "OnPremAD" || string.IsNullOrWhiteSpace(account.ObjectGuid))
            return new(false, "Checkout is currently supported only for on-prem AD-backed pool accounts.");

        if (string.IsNullOrWhiteSpace(ticket))
            return new(false, "Ticket number or reason is required.");

        var hours = ClampCheckoutHours(requestedHours);
        var expiresUtc = DateTime.UtcNow.AddHours(hours);

        using var op = _operationTrace.BeginOperation(
            ModuleId,
            "Checkout",
            performedBy,
            ip,
            account.UserPrincipalName,
            ticket,
            new Dictionary<string, object?> { ["requestedHours"] = hours });

        try
        {
            var creds = await _moduleCredentials.GetCredentialsAsync(ModuleId, "test account checkout");
            if (creds == null)
                return Fail(op, account, performedBy, ip, ticket, "Checkout", "AD credentials unavailable.");

            var notifyEmail = await ResolveAdminEmailAsync(performedBy, creds.Value);
            if (string.IsNullOrWhiteSpace(notifyEmail))
                return Fail(op, account, performedBy, ip, ticket, "Checkout", "Could not resolve your email address from Active Directory. Checkout was not started.");

            var password = GeneratePassword(28);
            var result = await ExecuteAdMutationAsync(creds.Value, ps =>
            {
                if (!IsCurrentPoolMember(ps, account.ObjectGuid, creds.Value))
                    return new(false, "Account is no longer a member of the configured test account pool group.");

                var current = ReadAdUserByGuid(ps, account.ObjectGuid, creds.Value);
                if (current == null)
                    return new(false, "Account no longer exists in Active Directory.");

                var enabled = GetBoolProperty(current, "Enabled");
                if (enabled)
                    return new(false, "Account is already enabled and cannot be checked out.");

                ResetPassword(ps, account.ObjectGuid, password, creds.Value);
                SetAccountExpiration(ps, account.ObjectGuid, expiresUtc, creds.Value);
                EnableAccount(ps, account.ObjectGuid, creds.Value);

                return new(true, "Checked out.");
            });

            if (!result.Success)
                return Fail(op, account, performedBy, ip, ticket, "Checkout", result.Message);

            try
            {
                await _email.SendTestAccountPasswordAsync(notifyEmail, account.UserPrincipalName, password, expiresUtc, ticket);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Password email failed for test account checkout of {Account}", account.UserPrincipalName);
                var cleanup = await DisableAndResetAsync(account.ObjectGuid, creds.Value, skipPoolMemberCheck: true);
                var cleanupMessage = cleanup.Success
                    ? "The account was immediately disabled and reset."
                    : $"Immediate cleanup also failed: {cleanup.Message}";
                return Fail(op, account, performedBy, ip, ticket, "Checkout", $"Password email failed. {cleanupMessage}");
            }

            LogAudit(performedBy, ip, "TestAccountPool_Checkout", account, true, ticket, null, new Dictionary<string, object?>
            {
                ["expiresUtc"] = expiresUtc.ToString("O"),
                ["checkoutHours"] = hours,
                ["notified"] = notifyEmail
            });

            op.Complete(true);
            return new(true, $"Checked out {account.UserPrincipalName} until {expiresUtc:g} UTC. Password was emailed to {notifyEmail}.", null, expiresUtc);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Checkout failed for {Account}", account.UserPrincipalName);
            _operationTrace.Step("Checkout", "Failed", backend: "ActiveDirectory", exception: ex);
            return Fail(op, account, performedBy, ip, ticket, "Checkout", ex.Message);
        }
    }

    public async Task<TestAccountPoolOperationResult> CheckinAsync(
        TestAccountPoolEntry account,
        string performedBy,
        string ip,
        string ticket)
    {
        if (account.Source != "OnPremAD" || string.IsNullOrWhiteSpace(account.ObjectGuid))
            return new(false, "Check-in is currently supported only for on-prem AD-backed pool accounts.");

        if (string.IsNullOrWhiteSpace(ticket))
            return new(false, "Ticket number or reason is required.");

        using var op = _operationTrace.BeginOperation(ModuleId, "CheckIn", performedBy, ip, account.UserPrincipalName, ticket);

        try
        {
            var creds = await _moduleCredentials.GetCredentialsAsync(ModuleId, "test account check-in");
            if (creds == null)
                return Fail(op, account, performedBy, ip, ticket, "CheckIn", "AD credentials unavailable.");

            var result = await DisableAndResetAsync(account.ObjectGuid, creds.Value);
            if (!result.Success)
                return Fail(op, account, performedBy, ip, ticket, "CheckIn", result.Message);

            LogAudit(performedBy, ip, "TestAccountPool_CheckIn", account, true, ticket);
            op.Complete(true);
            return new(true, $"Checked in {account.UserPrincipalName}. The account is disabled and its password was reset.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Check-in failed for {Account}", account.UserPrincipalName);
            _operationTrace.Step("CheckIn", "Failed", backend: "ActiveDirectory", exception: ex);
            return Fail(op, account, performedBy, ip, ticket, "CheckIn", ex.Message);
        }
    }

    public async Task<TestAccountPoolOperationResult> CleanupExpiredAsync(string performedBy, string ip)
    {
        using var op = _operationTrace.BeginOperation(ModuleId, "CleanupExpired", performedBy, ip);

        if (!IsOnPremPoolConfigured)
            return new(false, "On-prem pool is not configured.");

        var accounts = (await GetOnPremPoolAsync()).Where(a => a.Status == "Expired").ToList();
        if (accounts.Count == 0)
        {
            op.Complete(true, "No expired accounts found.");
            return new(true, "No expired accounts found.");
        }

        var creds = await _moduleCredentials.GetCredentialsAsync(ModuleId, "test account expiry cleanup");
        if (creds == null)
            return new(false, "AD credentials unavailable.");

        var failed = 0;
        foreach (var account in accounts)
        {
            if (string.IsNullOrWhiteSpace(account.ObjectGuid))
                continue;

            var result = await DisableAndResetAsync(account.ObjectGuid, creds.Value);
            if (!result.Success)
            {
                failed++;
                LogAudit(performedBy, ip, "TestAccountPool_ExpireCleanup", account, false, "", result.Message);
            }
            else
            {
                LogAudit(performedBy, ip, "TestAccountPool_ExpireCleanup", account, true);
            }
        }

        var success = failed == 0;
        var message = success
            ? $"Cleaned up {accounts.Count} expired account(s)."
            : $"Cleaned up {accounts.Count - failed} expired account(s); {failed} failed.";
        op.Complete(success, message);
        return new(success, message);
    }

    internal static string DetermineStatus(bool enabled, DateTime? expiresUtc, DateTime nowUtc)
    {
        if (!enabled)
            return "Available";

        if (expiresUtc.HasValue && expiresUtc.Value <= nowUtc)
            return "Expired";

        return "CheckedOut";
    }

    internal int ClampCheckoutHours(int hours) => Math.Clamp(hours <= 0 ? DefaultCheckoutHours : hours, 1, MaxCheckoutHours);

    internal static string GeneratePassword(int length)
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@#$%^&*()-_=+";
        return string.Create(length, chars, static (span, alphabet) =>
        {
            for (var i = 0; i < span.Length; i++)
                span[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        });
    }

    private async Task<IReadOnlyList<TestAccountPoolEntry>> GetOnPremPoolAsync()
    {
        var group = GetConfig("OnPremPoolGroup");
        if (string.IsNullOrWhiteSpace(group))
            return [];

        var creds = await _moduleCredentials.GetCredentialsAsync(ModuleId, "load on-prem test account pool");
        if (creds == null)
            throw new InvalidOperationException("AD credentials unavailable.");

        if (!await AdThrottle.WaitAsync(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("AD service is busy.");

        try
        {
            return await Task.Run(() => ReadOnPremPool(group, creds.Value));
        }
        finally
        {
            AdThrottle.Release();
        }
    }

    private IReadOnlyList<TestAccountPoolEntry> ReadOnPremPool(string group, (string username, string password, string domain) creds)
    {
        using var session = CreateAdPowerShell();
        var ps = session.PowerShell;
        ps.AddCommand("Get-ADGroupMember")
          .AddParameter("Identity", group)
          .AddParameter("Recursive")
          .AddParameter("Credential", CreateCredential(creds.username, creds.password, creds.domain))
          .AddParameter("ErrorAction", "Stop");
        var members = ps.Invoke();
        ThrowIfHadErrors(ps, "Get-ADGroupMember failed.");
        ps.Commands.Clear();

        var results = new List<TestAccountPoolEntry>();
        foreach (var member in members)
        {
            var objectClass = member.Properties["objectClass"]?.Value?.ToString();
            if (!string.Equals(objectClass, "user", StringComparison.OrdinalIgnoreCase))
                continue;

            var dn = member.Properties["distinguishedName"]?.Value?.ToString()
                ?? member.Properties["DistinguishedName"]?.Value?.ToString();
            if (string.IsNullOrWhiteSpace(dn))
                continue;

            ps.AddCommand("Get-ADUser")
              .AddParameter("Identity", dn)
              .AddParameter("Properties", new[] { "DisplayName", "UserPrincipalName", "SamAccountName", "mail", "Enabled", "AccountExpirationDate", "ObjectGUID", "DistinguishedName" })
              .AddParameter("Credential", CreateCredential(creds.username, creds.password, creds.domain))
              .AddParameter("ErrorAction", "Stop");
            var users = ps.Invoke();
            ThrowIfHadErrors(ps, "Get-ADUser failed.");
            ps.Commands.Clear();

            if (users.Count == 0)
                continue;

            results.Add(ToOnPremEntry(users[0]));
        }

        return results;
    }

    private static TestAccountPoolEntry ToOnPremEntry(PSObject user)
    {
        var upn = user.Properties["UserPrincipalName"]?.Value?.ToString() ?? "";
        var displayName = user.Properties["DisplayName"]?.Value?.ToString() ?? upn;
        var enabled = GetBoolProperty(user, "Enabled");
        var expiresUtc = ToUtc(user.Properties["AccountExpirationDate"]?.Value);
        var status = DetermineStatus(enabled, expiresUtc, DateTime.UtcNow);
        var mail = user.Properties["mail"]?.Value?.ToString();

        return new TestAccountPoolEntry(
            Key: user.Properties["ObjectGUID"]?.Value?.ToString() ?? upn,
            Source: "OnPremAD",
            DisplayName: displayName,
            UserPrincipalName: upn,
            SamAccountName: user.Properties["SamAccountName"]?.Value?.ToString(),
            Mail: mail,
            DistinguishedName: user.Properties["DistinguishedName"]?.Value?.ToString(),
            ObjectGuid: user.Properties["ObjectGUID"]?.Value?.ToString(),
            EntraObjectId: null,
            Enabled: enabled,
            ExpiresUtc: expiresUtc,
            Status: status,
            Profile: string.IsNullOrWhiteSpace(mail) ? "AD account, no mail" : "AD account with mail",
            SupportsCheckout: true,
            StatusDetail: status == "Expired" ? "Checkout expiry has passed. Run cleanup or check in." : null);
    }

    private async Task<IReadOnlyList<TestAccountPoolEntry>> GetEntraPoolAsync()
    {
        var groupId = GetConfig("EntraPoolGroupId");
        if (string.IsNullOrWhiteSpace(groupId))
            return [];

        var graph = await GetGraphClientAsync();
        if (graph == null)
            throw new InvalidOperationException("Graph credentials unavailable.");

        var results = new List<TestAccountPoolEntry>();
        var endpoint = $"/groups/{Uri.EscapeDataString(groupId)}/members?$select=id,displayName,userPrincipalName,mail,accountEnabled&$top=999";
        while (!string.IsNullOrWhiteSpace(endpoint))
        {
            using var doc = await graph.GetAsync(endpoint);
            if (doc == null)
                throw new InvalidOperationException("Graph group member query failed.");

            foreach (var item in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                if (!item.TryGetProperty("userPrincipalName", out var upnElement))
                    continue;

                var upn = upnElement.GetString() ?? "";
                var id = item.GetProperty("id").GetString() ?? upn;
                var enabled = item.TryGetProperty("accountEnabled", out var enabledElement)
                    && enabledElement.ValueKind == JsonValueKind.True;

                results.Add(new TestAccountPoolEntry(
                    Key: id,
                    Source: "EntraID",
                    DisplayName: item.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? upn : upn,
                    UserPrincipalName: upn,
                    SamAccountName: null,
                    Mail: item.TryGetProperty("mail", out var mail) ? mail.GetString() : null,
                    DistinguishedName: null,
                    ObjectGuid: null,
                    EntraObjectId: id,
                    Enabled: enabled,
                    ExpiresUtc: null,
                    Status: enabled ? "CheckedOut" : "Inventory",
                    Profile: "Entra account",
                    SupportsCheckout: false,
                    StatusDetail: "Inventory only. Cloud-only accounts cannot be checked out in this version."));
            }

            endpoint = TryGetNextLink(doc.RootElement);
        }

        return results;
    }

    private async Task<GraphTokenClient?> GetGraphClientAsync()
    {
        var secretIdStr = GetConfig("GraphDelineaSecretId");
        if (!int.TryParse(secretIdStr, out var secretId) || secretId <= 0)
            return null;

        var fields = await _delineaService.GetSecretFieldsAsync(secretId);
        if (fields == null) return null;

        var tenantId = fields.GetValueOrDefault("Tenant ID") ?? "";
        var clientId = fields.GetValueOrDefault("Application ID") ?? "";
        var clientSecret = fields.GetValueOrDefault("Client Secret") ?? "";

        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            return null;

        return new GraphTokenClient(tenantId, clientId, clientSecret, _httpClientFactory.CreateClient("MicrosoftGraph"));
    }

    private async Task<TestAccountPoolOperationResult> DisableAndResetAsync(string objectGuid, (string username, string password, string domain) creds, bool skipPoolMemberCheck = false)
    {
        var newPassword = GeneratePassword(28);
        return await ExecuteAdMutationAsync(creds, ps =>
        {
            if (!skipPoolMemberCheck && !IsCurrentPoolMember(ps, objectGuid, creds))
                return new(false, "Account is no longer a member of the configured test account pool group.");

            var current = ReadAdUserByGuid(ps, objectGuid, creds);
            if (current == null)
                return new(false, "Account no longer exists in Active Directory.");

            DisableAccount(ps, objectGuid, creds);
            ResetPassword(ps, objectGuid, newPassword, creds);
            ClearAccountExpiration(ps, objectGuid, creds);
            return new(true, "Checked in.");
        });
    }

    private async Task<string?> ResolveAdminEmailAsync(string performedBy, (string username, string password, string domain) creds)
    {
        if (!string.IsNullOrWhiteSpace(performedBy) && performedBy.Contains('@'))
            return performedBy;

        if (!await AdThrottle.WaitAsync(TimeSpan.FromSeconds(30)))
            return null;

        try
        {
            return await Task.Run(() =>
            {
                using var session = CreateAdPowerShell();
                var ps = session.PowerShell;
                var identity = performedBy.Contains('\\') ? performedBy.Split('\\')[1] : performedBy;
                if (string.IsNullOrWhiteSpace(identity))
                    return null;

                ps.AddCommand("Get-ADUser")
                  .AddParameter("Identity", identity)
                  .AddParameter("Properties", new[] { "mail", "UserPrincipalName" })
                  .AddParameter("Credential", CreateCredential(creds.username, creds.password, creds.domain))
                  .AddParameter("ErrorAction", "Stop");
                var users = ps.Invoke();
                ThrowIfHadErrors(ps, "Get-ADUser failed.");
                ps.Commands.Clear();

                if (users.Count == 0)
                    return null;

                var mail = users[0].Properties["mail"]?.Value?.ToString();
                if (!string.IsNullOrWhiteSpace(mail) && mail.Contains('@'))
                    return mail;

                var upn = users[0].Properties["UserPrincipalName"]?.Value?.ToString();
                return !string.IsNullOrWhiteSpace(upn) && upn.Contains('@') ? upn : null;
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve checkout notification email for {User}", performedBy);
            return null;
        }
        finally
        {
            AdThrottle.Release();
        }
    }

    private async Task<TestAccountPoolOperationResult> ExecuteAdMutationAsync(
        (string username, string password, string domain) creds,
        Func<PowerShell, TestAccountPoolOperationResult> action)
    {
        if (!await AdThrottle.WaitAsync(TimeSpan.FromSeconds(30)))
            return new(false, "AD service is busy.");

        try
        {
            return await Task.Run(() =>
            {
                using var session = CreateAdPowerShell();
                var ps = session.PowerShell;
                return action(ps);
            });
        }
        finally
        {
            AdThrottle.Release();
        }
    }

    private static AdPowerShellSession CreateAdPowerShell()
    {
        var iss = InitialSessionState.CreateDefault();
        iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
        var runspace = RunspaceFactory.CreateRunspace(iss);
        runspace.Open();
        var ps = PowerShell.Create();
        ps.Runspace = runspace;

        ps.AddCommand("Import-Module").AddParameter("Name", "ActiveDirectory").AddParameter("ErrorAction", "Stop");
        ps.Invoke();
        ThrowIfHadErrors(ps, "Import-Module ActiveDirectory failed.");
        ps.Commands.Clear();

        return new AdPowerShellSession(runspace, ps);
    }

    private static PSObject? ReadAdUserByGuid(PowerShell ps, string objectGuid, (string username, string password, string domain) creds)
    {
        ps.AddCommand("Get-ADUser")
          .AddParameter("Identity", objectGuid)
          .AddParameter("Properties", new[] { "Enabled", "ObjectGUID", "UserPrincipalName" })
          .AddParameter("Credential", CreateCredential(creds.username, creds.password, creds.domain))
          .AddParameter("ErrorAction", "Stop");
        var users = ps.Invoke();
        ThrowIfHadErrors(ps, "Get-ADUser failed.");
        ps.Commands.Clear();
        return users.Count == 0 ? null : users[0];
    }

    private bool IsCurrentPoolMember(PowerShell ps, string objectGuid, (string username, string password, string domain) creds)
    {
        var group = GetConfig("OnPremPoolGroup");
        if (string.IsNullOrWhiteSpace(group))
            return false;

        ps.AddCommand("Get-ADGroupMember")
          .AddParameter("Identity", group)
          .AddParameter("Recursive")
          .AddParameter("Credential", CreateCredential(creds.username, creds.password, creds.domain))
          .AddParameter("ErrorAction", "Stop");
        var members = ps.Invoke();
        ThrowIfHadErrors(ps, "Get-ADGroupMember failed.");
        ps.Commands.Clear();

        return members.Any(m =>
            string.Equals(m.Properties["objectGUID"]?.Value?.ToString(), objectGuid, StringComparison.OrdinalIgnoreCase)
            || string.Equals(m.Properties["ObjectGUID"]?.Value?.ToString(), objectGuid, StringComparison.OrdinalIgnoreCase));
    }

    private static void ResetPassword(PowerShell ps, string objectGuid, string password, (string username, string password, string domain) creds)
    {
        var secure = new System.Security.SecureString();
        foreach (var c in password)
            secure.AppendChar(c);

        ps.AddCommand("Set-ADAccountPassword")
          .AddParameter("Identity", objectGuid)
          .AddParameter("NewPassword", secure)
          .AddParameter("Reset")
          .AddParameter("Credential", CreateCredential(creds.username, creds.password, creds.domain))
          .AddParameter("ErrorAction", "Stop");
        ps.Invoke();
        ThrowIfHadErrors(ps, "Set-ADAccountPassword failed.");
        ps.Commands.Clear();
    }

    private static void EnableAccount(PowerShell ps, string objectGuid, (string username, string password, string domain) creds)
    {
        ps.AddCommand("Enable-ADAccount")
          .AddParameter("Identity", objectGuid)
          .AddParameter("Credential", CreateCredential(creds.username, creds.password, creds.domain))
          .AddParameter("ErrorAction", "Stop");
        ps.Invoke();
        ThrowIfHadErrors(ps, "Enable-ADAccount failed.");
        ps.Commands.Clear();
    }

    private static void DisableAccount(PowerShell ps, string objectGuid, (string username, string password, string domain) creds)
    {
        ps.AddCommand("Disable-ADAccount")
          .AddParameter("Identity", objectGuid)
          .AddParameter("Credential", CreateCredential(creds.username, creds.password, creds.domain))
          .AddParameter("ErrorAction", "Stop");
        ps.Invoke();
        ThrowIfHadErrors(ps, "Disable-ADAccount failed.");
        ps.Commands.Clear();
    }

    private static void SetAccountExpiration(PowerShell ps, string objectGuid, DateTime expiresUtc, (string username, string password, string domain) creds)
    {
        ps.AddCommand("Set-ADAccountExpiration")
          .AddParameter("Identity", objectGuid)
          .AddParameter("DateTime", expiresUtc.ToLocalTime())
          .AddParameter("Credential", CreateCredential(creds.username, creds.password, creds.domain))
          .AddParameter("ErrorAction", "Stop");
        ps.Invoke();
        ThrowIfHadErrors(ps, "Set-ADAccountExpiration failed.");
        ps.Commands.Clear();
    }

    private static void ClearAccountExpiration(PowerShell ps, string objectGuid, (string username, string password, string domain) creds)
    {
        ps.AddCommand("Clear-ADAccountExpiration")
          .AddParameter("Identity", objectGuid)
          .AddParameter("Credential", CreateCredential(creds.username, creds.password, creds.domain))
          .AddParameter("ErrorAction", "Stop");
        ps.Invoke();
        ThrowIfHadErrors(ps, "Clear-ADAccountExpiration failed.");
        ps.Commands.Clear();
    }

    private TestAccountPoolOperationResult Fail(
        OperationTraceService.OperationScope op,
        TestAccountPoolEntry account,
        string performedBy,
        string ip,
        string ticket,
        string action,
        string message)
    {
        LogAudit(performedBy, ip, $"TestAccountPool_{action}", account, false, ticket, message);
        op.Complete(false, message);
        return new(false, message);
    }

    private void LogAudit(
        string performedBy,
        string ip,
        string action,
        TestAccountPoolEntry account,
        bool success,
        string ticket = "",
        string? error = null,
        Dictionary<string, object?>? extra = null)
    {
        try
        {
            var details = extra ?? new Dictionary<string, object?>();
            details["source"] = account.Source;
            details["status"] = account.Status;
            details["objectGuid"] = account.ObjectGuid;
            details["entraObjectId"] = account.EntraObjectId;

            _audit.LogModuleAction(
                performedBy,
                ip,
                action,
                ModuleId,
                account.UserPrincipalName,
                success,
                ticket,
                error,
                details);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write TestAccountPool audit entry for {Action} {Target}", action, account.UserPrincipalName);
        }
    }

    private string? GetConfig(string key) => _moduleConfig.GetValue(ModuleId, key);

    private static int ParseInt(string? value, int fallback) =>
        int.TryParse(value, out var parsed) ? parsed : fallback;

    private static bool GetBoolProperty(PSObject obj, string name) =>
        obj.Properties[name]?.Value is bool b && b;

    private static DateTime? ToUtc(object? value)
    {
        if (value is not DateTime dt)
            return null;
        return dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
    }

    private static string? TryGetNextLink(JsonElement root)
    {
        if (!root.TryGetProperty("@odata.nextLink", out var prop))
            return null;

        var next = prop.GetString();
        if (string.IsNullOrWhiteSpace(next))
            return null;

        var marker = "/v1.0";
        var idx = next.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? next[(idx + marker.Length)..] : next;
    }

    private static PSCredential CreateCredential(string username, string password, string domain)
    {
        var fullUsername = username.Contains('\\') || username.Contains('@')
            ? username
            : $"{domain}\\{username}";
        var securePassword = new System.Security.SecureString();
        foreach (var c in password)
            securePassword.AppendChar(c);
        return new PSCredential(fullUsername, securePassword);
    }

    private static void ThrowIfHadErrors(PowerShell ps, string fallback)
    {
        if (!ps.HadErrors)
            return;

        var err = ps.Streams.Error.FirstOrDefault()?.Exception?.Message ?? fallback;
        ps.Streams.Error.Clear();
        throw new InvalidOperationException(err);
    }

    private sealed class AdPowerShellSession : IDisposable
    {
        private readonly Runspace _runspace;

        public AdPowerShellSession(Runspace runspace, PowerShell powerShell)
        {
            _runspace = runspace;
            PowerShell = powerShell;
        }

        public PowerShell PowerShell { get; }

        public void Dispose()
        {
            PowerShell.Dispose();
            _runspace.Dispose();
        }
    }
}
