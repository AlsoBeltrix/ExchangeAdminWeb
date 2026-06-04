using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
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

public sealed record TestAccountTemplate(
    string Id,
    string DisplayName,
    string Description,
    bool CreateOnPremAd,
    bool CreateEntra,
    bool ExchangeOnline,
    bool Teams,
    bool OnPremMailbox);

public sealed record TestAccountCreateRequest(
    string TemplateId,
    string NamePrefix,
    string DisplayNamePrefix,
    int Quantity,
    string Ticket,
    bool CreateOnPremAd,
    bool CreateEntra,
    bool ExchangeOnline,
    bool Teams,
    bool OnPremMailbox);

public sealed record TestAccountCreatePreview(
    bool Success,
    string Message,
    List<TestAccountCreatePreviewRow> Rows);

public sealed record TestAccountCreatePreviewRow(
    string SamAccountName,
    string UserPrincipalName,
    string DisplayName,
    string Source,
    string Services);

public sealed class TestAccountPoolService
{
    private const string ModuleId = "TestAccountPool";
    private const int MaxCreateQuantity = 25;
    private static readonly SemaphoreSlim AdThrottle = new(2, 2);
    private static readonly Regex SafeNameRegex = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{1,17}$", RegexOptions.Compiled);

    public static readonly TestAccountTemplate[] Templates =
    [
        new(
            "entra-exol-teams",
            "Entra + EXO + Teams",
            "Cloud-only Entra account, added to the Entra pool plus configured Exchange Online and Teams provisioning groups.",
            CreateOnPremAd: false,
            CreateEntra: true,
            ExchangeOnline: true,
            Teams: true,
            OnPremMailbox: false),
        new(
            "ad-exol-teams",
            "AD + EXO + Teams",
            "On-prem AD account, added to the AD pool plus configured synced Exchange Online and Teams provisioning groups.",
            CreateOnPremAd: true,
            CreateEntra: false,
            ExchangeOnline: true,
            Teams: true,
            OnPremMailbox: false),
        new(
            "ad-onprem-mailbox-teams",
            "AD + On-Prem Mailbox + Teams",
            "On-prem AD account, enabled for an on-prem Exchange mailbox, added to the AD pool and configured Teams group.",
            CreateOnPremAd: true,
            CreateEntra: false,
            ExchangeOnline: false,
            Teams: true,
            OnPremMailbox: true),
        new(
            "basic-ad",
            "Basic AD Account",
            "On-prem AD account only, added to the AD test account pool and left disabled until checkout.",
            CreateOnPremAd: true,
            CreateEntra: false,
            ExchangeOnline: false,
            Teams: false,
            OnPremMailbox: false),
        new(
            "custom",
            "Custom",
            "Choose account authority and provisioning options manually.",
            CreateOnPremAd: true,
            CreateEntra: false,
            ExchangeOnline: false,
            Teams: false,
            OnPremMailbox: false)
    ];

    private readonly ModuleConfigService _moduleConfig;
    private readonly ModuleCredentialService _moduleCredentials;
    private readonly DelineaService _delineaService;
    private readonly ProtectedPrincipalService _protectedPrincipalService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AuditService _audit;
    private readonly EmailService _email;
    private readonly OperationTraceService _operationTrace;
    private readonly ILogger<TestAccountPoolService> _logger;

    public TestAccountPoolService(
        ModuleConfigService moduleConfig,
        ModuleCredentialService moduleCredentials,
        DelineaService delineaService,
        ProtectedPrincipalService protectedPrincipalService,
        IHttpClientFactory httpClientFactory,
        AuditService audit,
        EmailService email,
        OperationTraceService operationTrace,
        ILogger<TestAccountPoolService> logger)
    {
        _moduleConfig = moduleConfig;
        _moduleCredentials = moduleCredentials;
        _delineaService = delineaService;
        _protectedPrincipalService = protectedPrincipalService;
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

    private async Task<TestAccountPoolOperationResult?> CheckProtectedPrincipalAsync(TestAccountPoolEntry account)
    {
        var principal = new ResolvedDirectoryPrincipal(
            Source: account.Source,
            DisplayName: account.DisplayName,
            UserPrincipalName: account.UserPrincipalName,
            SamAccountName: account.SamAccountName,
            PrimarySmtpAddress: account.Mail,
            DistinguishedName: account.DistinguishedName,
            ObjectGuid: account.ObjectGuid,
            EntraObjectId: account.EntraObjectId);

        var result = await _protectedPrincipalService.CheckAsync(principal);
        if (result.CheckFailed)
            return new(false, $"Protection check failed: {result.Reason}");
        if (result.IsProtected)
            return new(false, $"Account {account.UserPrincipalName} is a protected principal and cannot be modified through the test account pool.");
        return null;
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

        var protectionCheck = await CheckProtectedPrincipalAsync(account);
        if (protectionCheck != null) return protectionCheck;

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

        var protectionCheck = await CheckProtectedPrincipalAsync(account);
        if (protectionCheck != null) return protectionCheck;

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

            var protectionCheck = await CheckProtectedPrincipalAsync(account);
            if (protectionCheck != null)
            {
                failed++;
                _logger.LogWarning("Skipping cleanup of protected account {Account}", account.UserPrincipalName);
                LogAudit(performedBy, ip, "TestAccountPool_ExpireCleanup", account, false, "", $"Protected principal: {protectionCheck.Message}");
                continue;
            }

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

    public static TestAccountCreateRequest BuildTemplateRequest(string templateId, string namePrefix, string displayNamePrefix, int quantity, string ticket)
    {
        var template = Templates.FirstOrDefault(t => t.Id.Equals(templateId, StringComparison.OrdinalIgnoreCase))
            ?? Templates.First(t => t.Id == "basic-ad");

        return new TestAccountCreateRequest(
            template.Id,
            namePrefix,
            displayNamePrefix,
            quantity,
            ticket,
            template.CreateOnPremAd,
            template.CreateEntra,
            template.ExchangeOnline,
            template.Teams,
            template.OnPremMailbox);
    }

    public TestAccountCreatePreview PreviewCreate(TestAccountCreateRequest request)
    {
        var validation = ValidateCreateRequest(request);
        if (!validation.Success)
            return new(false, validation.Message, []);

        return new(true, "Preview ready.", BuildCreateRows(request));
    }

    public async Task<TestAccountPoolOperationResult> CreateAccountsAsync(
        TestAccountCreateRequest request,
        string performedBy,
        string ip)
    {
        var validation = ValidateCreateRequest(request);
        if (!validation.Success)
            return new(false, validation.Message);

        var rows = BuildCreateRows(request);
        using var op = _operationTrace.BeginOperation(
            ModuleId,
            "CreateAccounts",
            performedBy,
            ip,
            string.Join(", ", rows.Select(r => r.UserPrincipalName)),
            request.Ticket,
            new Dictionary<string, object?>
            {
                ["template"] = request.TemplateId,
                ["quantity"] = request.Quantity,
                ["createOnPremAd"] = request.CreateOnPremAd,
                ["createEntra"] = request.CreateEntra,
                ["exchangeOnline"] = request.ExchangeOnline,
                ["teams"] = request.Teams,
                ["onPremMailbox"] = request.OnPremMailbox
            });

        try
        {
            (string username, string password, string domain)? adCreds = null;
            GraphTokenClient? graph = null;
            (string username, string password, string domain)? exchangeCreds = null;

            if (request.CreateOnPremAd)
            {
                adCreds = await _moduleCredentials.GetCredentialsAsync(ModuleId, "test account creation");
                if (adCreds == null)
                    return FailCreate(op, rows, performedBy, ip, request.Ticket, "AD credentials unavailable.");
            }

            if (request.CreateEntra)
            {
                graph = await GetGraphClientAsync();
                if (graph == null)
                    return FailCreate(op, rows, performedBy, ip, request.Ticket, "Graph credentials unavailable.");
            }

            if (request.OnPremMailbox)
            {
                exchangeCreds = await GetOnPremExchangeCredentialsAsync();
                if (exchangeCreds == null)
                    return FailCreate(op, rows, performedBy, ip, request.Ticket, "On-prem Exchange credentials unavailable.");

                if (string.IsNullOrWhiteSpace(GetConfig("OnPremExchangeServerUri")))
                    return FailCreate(op, rows, performedBy, ip, request.Ticket, "On-prem Exchange server URI is not configured.");
            }

            var created = new List<string>();
            var failed = new List<string>();
            foreach (var row in rows)
            {
                string? createdAdGuid = null;
                string? createdEntraId = null;

                try
                {
                    if (request.CreateOnPremAd)
                    {
                        var (dn, guid) = await CreateOnPremAdUserAsync(row, request, adCreds!.Value);
                        createdAdGuid = guid;
                        await ProvisionOnPremGroupsAsync(dn, request, adCreds!.Value);
                        if (request.OnPremMailbox)
                            await EnableOnPremMailboxAsync(dn, row.UserPrincipalName, exchangeCreds!.Value);
                    }

                    if (request.CreateEntra)
                    {
                        createdEntraId = await CreateEntraUserAsync(row, request, graph!);
                        await ProvisionEntraGroupsAsync(createdEntraId, row.UserPrincipalName, request, graph!);
                    }

                    created.Add(row.UserPrincipalName);
                    LogAudit(performedBy, ip, "TestAccountPool_Create", ToAuditEntry(row), true, request.Ticket, null, new Dictionary<string, object?>
                    {
                        ["template"] = request.TemplateId,
                        ["services"] = row.Services
                    });
                }
                catch (Exception rowEx)
                {
                    failed.Add($"{row.UserPrincipalName}: {rowEx.Message}");
                    _logger.LogError(rowEx, "Test account creation failed for {Account}, attempting cleanup of this-operation objects only", row.UserPrincipalName);

                    try
                    {
                        if (createdAdGuid != null && adCreds != null)
                            await TryDeleteAdAccountByGuidAsync(createdAdGuid, adCreds.Value);
                        if (createdEntraId != null && graph != null)
                            await TryDeleteEntraAccountByIdAsync(createdEntraId, graph);
                    }
                    catch (Exception cleanupEx)
                    {
                        _logger.LogWarning(cleanupEx, "Cleanup of partially-created account {Account} failed", row.UserPrincipalName);
                    }

                    LogAudit(performedBy, ip, "TestAccountPool_Create", ToAuditEntry(row), false, request.Ticket, rowEx.Message, new Dictionary<string, object?>
                    {
                        ["template"] = request.TemplateId,
                        ["services"] = row.Services
                    });
                }
            }

            var success = failed.Count == 0;
            var message = success
                ? $"Created {created.Count} disabled test account(s): {string.Join(", ", created)}. Refresh the pool to check them out."
                : $"Created {created.Count} account(s); {failed.Count} failed. {string.Join(" | ", failed)}";
            op.Complete(success, message);
            return new(success, message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Test account creation failed for prefix {Prefix}", request.NamePrefix);
            _operationTrace.Step("CreateAccounts", "Failed", backend: "ActiveDirectory/Graph", exception: ex);
            return FailCreate(op, rows, performedBy, ip, request.Ticket, ex.Message);
        }
    }

    private TestAccountPoolOperationResult ValidateCreateRequest(TestAccountCreateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Ticket))
            return new(false, "Ticket number or reason is required.");

        if (request.Quantity is < 1 or > MaxCreateQuantity)
            return new(false, $"Quantity must be between 1 and {MaxCreateQuantity}.");

        var prefix = request.NamePrefix.Trim();
        if (!SafeNameRegex.IsMatch(prefix))
            return new(false, "Account name prefix must be 2-18 characters and contain only letters, numbers, dot, underscore, or hyphen.");

        var maxSuffix = request.Quantity.ToString("D2").Length;
        if (prefix.Length + maxSuffix > 20)
            return new(false, "Generated sAMAccountName would exceed 20 characters. Use a shorter account name prefix.");

        if (!request.CreateOnPremAd && !request.CreateEntra)
            return new(false, "Choose at least one account authority: on-prem AD or Entra ID.");

        if (request.CreateOnPremAd)
        {
            if (string.IsNullOrWhiteSpace(GetConfig("OnPremCreateOU")))
                return new(false, "On-Prem Create OU is required for AD account creation.");
            if (string.IsNullOrWhiteSpace(GetConfig("OnPremUPNSuffix")))
                return new(false, "On-Prem UPN Suffix is required for AD account creation.");
            if (string.IsNullOrWhiteSpace(GetConfig("OnPremPoolGroup")))
                return new(false, "On-Prem Test Account Group is required so created AD accounts can be retrieved.");
            if (string.IsNullOrWhiteSpace(GetConfig("DelineaSecretId")))
                return new(false, "AD Delinea Secret ID is required for AD account creation.");
        }

        if (request.CreateEntra)
        {
            if (string.IsNullOrWhiteSpace(GetConfig("EntraDomain")))
                return new(false, "Entra Domain is required for Entra account creation.");
            if (string.IsNullOrWhiteSpace(GetConfig("EntraPoolGroupId")))
                return new(false, "Entra Test Account Group ID is required so created cloud accounts can be retrieved.");
            if (string.IsNullOrWhiteSpace(GetConfig("GraphDelineaSecretId")))
                return new(false, "Graph Delinea Secret ID is required for Entra account creation.");
        }

        if (request.ExchangeOnline)
        {
            if (request.CreateOnPremAd && string.IsNullOrWhiteSpace(GetConfig("OnPremExchangeOnlineGroup")))
                return new(false, "On-Prem Exchange Online Provisioning Group is required for AD-backed EXO test accounts.");
            if (request.CreateEntra && string.IsNullOrWhiteSpace(GetConfig("EntraExchangeOnlineGroupId")))
                return new(false, "Entra Exchange Online Group ID is required for cloud-only EXO test accounts.");
        }

        if (request.Teams)
        {
            if (request.CreateOnPremAd && string.IsNullOrWhiteSpace(GetConfig("OnPremTeamsGroup")))
                return new(false, "On-Prem Teams Provisioning Group is required for AD-backed Teams test accounts.");
            if (request.CreateEntra && string.IsNullOrWhiteSpace(GetConfig("EntraTeamsGroupId")))
                return new(false, "Entra Teams Group ID is required for cloud-only Teams test accounts.");
        }

        if (request.OnPremMailbox)
        {
            if (!request.CreateOnPremAd)
                return new(false, "On-prem mailbox creation requires an on-prem AD account.");
            if (string.IsNullOrWhiteSpace(GetConfig("OnPremExchangeDelineaSecretId")))
                return new(false, "On-Prem Exchange Delinea Secret ID is required for on-prem mailbox creation.");
            if (string.IsNullOrWhiteSpace(GetConfig("OnPremExchangeServerUri")))
                return new(false, "On-Prem Exchange Server URI is required for on-prem mailbox creation.");
        }

        return new(true, "Valid.");
    }

    private List<TestAccountCreatePreviewRow> BuildCreateRows(TestAccountCreateRequest request)
    {
        var prefix = Slug(request.NamePrefix);
        var displayPrefix = string.IsNullOrWhiteSpace(request.DisplayNamePrefix)
            ? "Test Account"
            : request.DisplayNamePrefix.Trim();
        var suffix = request.CreateEntra && !request.CreateOnPremAd
            ? GetConfig("EntraDomain")
            : GetConfig("OnPremUPNSuffix");

        var source = request.CreateOnPremAd && request.CreateEntra ? "AD + Entra"
            : request.CreateOnPremAd ? "OnPremAD"
            : "EntraID";
        var services = DescribeServices(request);

        var rows = new List<TestAccountCreatePreviewRow>();
        for (var i = 1; i <= request.Quantity; i++)
        {
            var accountName = request.Quantity == 1
                ? prefix
                : $"{prefix}{i:D2}";
            rows.Add(new TestAccountCreatePreviewRow(
                SamAccountName: accountName,
                UserPrincipalName: string.IsNullOrWhiteSpace(suffix) ? accountName : $"{accountName}@{suffix}",
                DisplayName: request.Quantity == 1 ? displayPrefix : $"{displayPrefix} {i:D2}",
                Source: source,
                Services: services));
        }

        return rows;
    }

    private static string DescribeServices(TestAccountCreateRequest request)
    {
        var services = new List<string>();
        if (request.ExchangeOnline) services.Add("Exchange Online");
        if (request.OnPremMailbox) services.Add("On-prem mailbox");
        if (request.Teams) services.Add("Teams");
        return services.Count == 0 ? "None" : string.Join(", ", services);
    }

    private async Task<(string dn, string guid)> CreateOnPremAdUserAsync(
        TestAccountCreatePreviewRow row,
        TestAccountCreateRequest request,
        (string username, string password, string domain) creds)
    {
        var password = GeneratePassword(28);
        var ou = GetConfig("OnPremCreateOU")!;

        var mutation = await ExecuteAdMutationAsync(creds, ps =>
        {
            var credential = CreateCredential(creds.username, creds.password, creds.domain);
            if (AdUserExists(ps, row.SamAccountName, row.UserPrincipalName, credential))
                throw new InvalidOperationException($"AD account already exists: {row.UserPrincipalName}");

            var secure = new System.Security.SecureString();
            foreach (var c in password)
                secure.AppendChar(c);

            ps.AddCommand("New-ADUser")
              .AddParameter("Name", row.DisplayName)
              .AddParameter("DisplayName", row.DisplayName)
              .AddParameter("SamAccountName", row.SamAccountName)
              .AddParameter("UserPrincipalName", row.UserPrincipalName)
              .AddParameter("Path", ou)
              .AddParameter("AccountPassword", secure)
              .AddParameter("Enabled", false)
              .AddParameter("OtherAttributes", new Dictionary<string, object> { ["extensionAttribute15"] = "TESTACCOUNT" })
              .AddParameter("Credential", credential)
              .AddParameter("ErrorAction", "Stop");
            ps.Invoke();
            ThrowIfHadErrors(ps, "New-ADUser failed.");
            ps.Commands.Clear();

            var user = ReadAdUserBySam(ps, row.SamAccountName, credential)
                ?? throw new InvalidOperationException($"Created AD account could not be read: {row.SamAccountName}");
            var dn = user.Properties["DistinguishedName"]?.Value?.ToString()
                ?? throw new InvalidOperationException($"Created AD account has no distinguishedName: {row.SamAccountName}");
            var guid = user.Properties["ObjectGUID"]?.Value?.ToString() ?? "";

            return new(true, $"{dn}|{guid}");
        });

        if (!mutation.Success)
            throw new InvalidOperationException(mutation.Message);
        var parts = mutation.Message.Split('|', 2);
        return (parts[0], parts.Length > 1 ? parts[1] : "");
    }

    private async Task ProvisionOnPremGroupsAsync(string dn, TestAccountCreateRequest request, (string username, string password, string domain) creds)
    {
        var poolGroup = GetConfig("OnPremPoolGroup")!;
        var exoGroup = request.ExchangeOnline ? GetConfig("OnPremExchangeOnlineGroup") : null;
        var teamsGroup = request.Teams ? GetConfig("OnPremTeamsGroup") : null;

        await ExecuteAdMutationAsync(creds, ps =>
        {
            var credential = CreateCredential(creds.username, creds.password, creds.domain);
            AddAdGroupMember(ps, poolGroup, dn, credential);
            if (!string.IsNullOrWhiteSpace(exoGroup))
                AddAdGroupMember(ps, exoGroup, dn, credential);
            if (!string.IsNullOrWhiteSpace(teamsGroup))
                AddAdGroupMember(ps, teamsGroup, dn, credential);
            return new(true, "Groups provisioned.");
        });
    }

    private async Task<string> CreateEntraUserAsync(
        TestAccountCreatePreviewRow row,
        TestAccountCreateRequest request,
        GraphTokenClient graph)
    {
        var password = GeneratePassword(28);
        var usageLocation = GetConfig("EntraUsageLocation");
        var body = new Dictionary<string, object?>
        {
            ["accountEnabled"] = false,
            ["displayName"] = row.DisplayName,
            ["mailNickname"] = row.SamAccountName.Replace(".", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal),
            ["userPrincipalName"] = row.UserPrincipalName,
            ["passwordProfile"] = new Dictionary<string, object?>
            {
                ["forceChangePasswordNextSignIn"] = true,
                ["password"] = password
            }
        };
        if (!string.IsNullOrWhiteSpace(usageLocation))
            body["usageLocation"] = usageLocation;

        using var created = await graph.PostAsync("/users", body);
        if (created == null)
            throw new InvalidOperationException($"Failed to create Entra account {row.UserPrincipalName}. Check Graph permissions and uniqueness.");

        var id = created.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException($"Created Entra account has no id: {row.UserPrincipalName}");

        return id;
    }

    private async Task ProvisionEntraGroupsAsync(string entraId, string upn, TestAccountCreateRequest request, GraphTokenClient graph)
    {
        await AddGraphGroupMemberAsync(graph, GetConfig("EntraPoolGroupId")!, entraId, upn, "Entra pool");
        if (request.ExchangeOnline)
            await AddGraphGroupMemberAsync(graph, GetConfig("EntraExchangeOnlineGroupId")!, entraId, upn, "Exchange Online provisioning");
        if (request.Teams)
            await AddGraphGroupMemberAsync(graph, GetConfig("EntraTeamsGroupId")!, entraId, upn, "Teams provisioning");
    }

    private async Task AddGraphGroupMemberAsync(GraphTokenClient graph, string groupId, string userId, string upn, string groupPurpose)
    {
        var body = new Dictionary<string, string>
        {
            ["@odata.id"] = $"https://graph.microsoft.com/v1.0/directoryObjects/{userId}"
        };
        var success = await graph.PostNoContentAsync($"/groups/{Uri.EscapeDataString(groupId)}/members/$ref", body);
        if (!success)
            throw new InvalidOperationException($"Created {upn}, but failed to add it to the {groupPurpose} group.");
    }

    private async Task<(string username, string password, string domain)?> GetOnPremExchangeCredentialsAsync()
    {
        var secretIdStr = GetConfig("OnPremExchangeDelineaSecretId");
        if (!int.TryParse(secretIdStr, out var secretId) || secretId <= 0)
            return null;

        return await _delineaService.GetCredentialsBySecretIdAsync(secretId);
    }

    private async Task EnableOnPremMailboxAsync(
        string distinguishedName,
        string upn,
        (string username, string password, string domain) creds)
    {
        var serverUri = GetConfig("OnPremExchangeServerUri")!;
        var database = GetConfig("OnPremMailboxDatabase");

        if (!await AdThrottle.WaitAsync(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("AD/Exchange service is busy.");

        try
        {
            await Task.Run(() =>
            {
                using var session = CreateAdPowerShell();
                var ps = session.PowerShell;
                ConnectOnPremExchange(ps, serverUri, creds);
                var remote = ps.Runspace.SessionStateProxy.GetVariable("onpremSession");

                ps.AddCommand("Invoke-Command")
                  .AddParameter("Session", remote)
                  .AddParameter("ScriptBlock", ScriptBlock.Create("param($Identity, $Database) if ([string]::IsNullOrWhiteSpace($Database)) { Enable-Mailbox -Identity $Identity -ErrorAction Stop } else { Enable-Mailbox -Identity $Identity -Database $Database -ErrorAction Stop }"))
                  .AddParameter("ArgumentList", new object?[] { distinguishedName, database ?? "" })
                  .AddParameter("ErrorAction", "Stop");
                ps.Invoke();
                ThrowIfHadErrors(ps, $"Enable-Mailbox failed for {upn}.");
                ps.Commands.Clear();

                RemoveOnPremExchangeSession(ps);
            });
        }
        finally
        {
            AdThrottle.Release();
        }
    }

    private async Task TryDeleteAdAccountByGuidAsync(string objectGuid, (string username, string password, string domain) creds)
    {
        await ExecuteAdMutationAsync(creds, ps =>
        {
            var credential = CreateCredential(creds.username, creds.password, creds.domain);

            ps.AddCommand("Get-ADUser")
              .AddParameter("Identity", objectGuid)
              .AddParameter("Credential", credential)
              .AddParameter("ErrorAction", "SilentlyContinue");
            var result = ps.Invoke();
            ps.Commands.Clear();
            if (result.Count == 0)
                return new(true, "Account not found by GUID — nothing to clean up.");

            ps.AddCommand("Remove-ADUser")
              .AddParameter("Identity", objectGuid)
              .AddParameter("Credential", credential)
              .AddParameter("Confirm", false)
              .AddParameter("ErrorAction", "Stop");
            ps.Invoke();
            ThrowIfHadErrors(ps, "Remove-ADUser failed during cleanup.");
            ps.Commands.Clear();

            _logger.LogInformation("Cleaned up partially-created AD account {Guid}", objectGuid);
            return new(true, "Cleaned up.");
        });
    }

    private async Task TryDeleteEntraAccountByIdAsync(string entraId, GraphTokenClient graph)
    {
        await graph.DeleteAsync($"/users/{entraId}");
        _logger.LogInformation("Cleaned up partially-created Entra account {Id}", entraId);
    }

    private TestAccountPoolOperationResult FailCreate(
        OperationTraceService.OperationScope op,
        List<TestAccountCreatePreviewRow> rows,
        string performedBy,
        string ip,
        string ticket,
        string message)
    {
        foreach (var row in rows)
            LogAudit(performedBy, ip, "TestAccountPool_Create", ToAuditEntry(row), false, ticket, message);
        op.Complete(false, message);
        return new(false, message);
    }

    private static TestAccountPoolEntry ToAuditEntry(TestAccountCreatePreviewRow row) =>
        new(
            Key: row.UserPrincipalName,
            Source: row.Source,
            DisplayName: row.DisplayName,
            UserPrincipalName: row.UserPrincipalName,
            SamAccountName: row.SamAccountName,
            Mail: null,
            DistinguishedName: null,
            ObjectGuid: null,
            EntraObjectId: null,
            Enabled: false,
            ExpiresUtc: null,
            Status: "Created",
            Profile: row.Services,
            SupportsCheckout: row.Source != "EntraID",
            StatusDetail: null);

    private static string Slug(string value)
    {
        var chars = value.Trim()
            .Where(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-')
            .Select(c => char.ToLowerInvariant(c))
            .ToArray();
        return new string(chars);
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

    private static PSObject? ReadAdUserBySam(PowerShell ps, string samAccountName, PSCredential credential)
    {
        ps.AddCommand("Get-ADUser")
          .AddParameter("Identity", samAccountName)
          .AddParameter("Properties", new[] { "DistinguishedName", "ObjectGUID", "UserPrincipalName" })
          .AddParameter("Credential", credential)
          .AddParameter("ErrorAction", "Stop");
        var users = ps.Invoke();
        ThrowIfHadErrors(ps, "Get-ADUser failed.");
        ps.Commands.Clear();
        return users.Count == 0 ? null : users[0];
    }

    private static bool AdUserExists(PowerShell ps, string samAccountName, string userPrincipalName, PSCredential credential)
    {
        var escapedSam = samAccountName.Replace("'", "''");
        var escapedUpn = userPrincipalName.Replace("'", "''");
        ps.AddCommand("Get-ADUser")
          .AddParameter("Filter", $"SamAccountName -eq '{escapedSam}' -or UserPrincipalName -eq '{escapedUpn}'")
          .AddParameter("Credential", credential)
          .AddParameter("ErrorAction", "Stop");
        var users = ps.Invoke();
        ThrowIfHadErrors(ps, "Get-ADUser failed.");
        ps.Commands.Clear();
        return users.Count > 0;
    }

    private static void AddAdGroupMember(PowerShell ps, string groupIdentity, string memberDn, PSCredential credential)
    {
        ps.AddCommand("Add-ADGroupMember")
          .AddParameter("Identity", groupIdentity)
          .AddParameter("Members", memberDn)
          .AddParameter("Credential", credential)
          .AddParameter("ErrorAction", "Stop");
        ps.Invoke();
        ThrowIfHadErrors(ps, $"Add-ADGroupMember failed for {groupIdentity}.");
        ps.Commands.Clear();
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

    private static void ConnectOnPremExchange(PowerShell ps, string serverUri, (string username, string password, string domain) creds)
    {
        var credential = CreateCredential(creds.username, creds.password, creds.domain);

        ps.Commands.Clear();
        ps.Streams.Error.Clear();
        ps.AddScript("New-PSSessionOption -OperationTimeout 15000 -OpenTimeout 10000");
        var optResult = ps.Invoke();
        ThrowIfHadErrors(ps, "New-PSSessionOption failed.");
        ps.Commands.Clear();
        var sessionOpt = optResult.FirstOrDefault()?.BaseObject;

        ps.AddCommand("New-PSSession")
          .AddParameter("ConfigurationName", "Microsoft.Exchange")
          .AddParameter("ConnectionUri", serverUri)
          .AddParameter("Authentication", "Kerberos")
          .AddParameter("Credential", credential)
          .AddParameter("ErrorAction", "Stop");
        if (sessionOpt != null)
            ps.AddParameter("SessionOption", sessionOpt);
        var sessions = ps.Invoke();
        ThrowIfHadErrors(ps, "New-PSSession failed.");
        ps.Commands.Clear();

        var session = sessions.FirstOrDefault()
            ?? throw new InvalidOperationException("New-PSSession returned no session object.");
        ps.Runspace.SessionStateProxy.SetVariable("onpremSession", session.BaseObject);
    }

    private static void RemoveOnPremExchangeSession(PowerShell ps)
    {
        try
        {
            var session = ps.Runspace.SessionStateProxy.GetVariable("onpremSession");
            if (session == null)
                return;

            ps.Commands.Clear();
            ps.AddCommand("Remove-PSSession").AddParameter("Session", session);
            ps.Invoke();
            ps.Commands.Clear();
        }
        catch
        {
        }
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
