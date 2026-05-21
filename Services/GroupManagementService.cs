using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text.Json;
using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Services;

public class GroupManagementService : ExchangeServiceBase
{
    private readonly ModuleConfigService _moduleConfig;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private static readonly SemaphoreSlim _adThrottle = new(2, 2);
    private GraphTokenClient? _graph;

    public GroupManagementService(
        ExoConnectionPool exoPool,
        DelineaService delineaService,
        ModuleConfigService moduleConfig,
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        ILogger<GroupManagementService> logger)
        : base(exoPool, delineaService, logger, config["OnPremExchange:ServerUri"] ?? "")
    {
        _moduleConfig = moduleConfig;
        _httpClientFactory = httpClientFactory;
        _config = config;
    }

    private GraphTokenClient GetGraphClient()
    {
        if (_graph != null && _graph.IsConfigured) return _graph;

        var tenantId = _moduleConfig.GetValue("GroupManagement", "GraphTenantId") ?? "";
        var clientId = _moduleConfig.GetValue("GroupManagement", "GraphClientId") ?? "";
        var credTarget = _moduleConfig.GetValue("GroupManagement", "GraphCredentialTarget") ?? "Graph_GroupManagement";

        _graph = new GraphTokenClient(tenantId, clientId, credTarget, _httpClientFactory.CreateClient("MicrosoftGraph"));
        return _graph;
    }

    public async Task<List<GroupInfo>> SearchGroupsAsync(string searchTerm)
    {
        var results = new List<GroupInfo>();

        // Search EXO distribution groups and mail-enabled security groups
        var exoResults = await RunPooledQueryAsync(ps =>
        {
            ps.AddCommand("Get-DistributionGroup")
              .AddParameter("Filter", $"Name -like '*{searchTerm.Replace("'", "''")}*' -or PrimarySmtpAddress -like '*{searchTerm.Replace("'", "''")}*'")
              .AddParameter("ResultSize", 25)
              .AddParameter("ErrorAction", "Stop");
            return Invoke(ps);
        });

        foreach (var group in exoResults)
        {
            var isDirSynced = group.Properties["IsDirSynced"]?.Value as bool? ?? false;
            var typeDetails = group.Properties["RecipientTypeDetails"]?.Value?.ToString() ?? "";

            results.Add(new GroupInfo
            {
                Name = group.Properties["DisplayName"]?.Value?.ToString() ?? "",
                Email = group.Properties["PrimarySmtpAddress"]?.Value?.ToString() ?? "",
                Identity = group.Properties["PrimarySmtpAddress"]?.Value?.ToString() ?? "",
                SamAccountName = group.Properties["Alias"]?.Value?.ToString() ?? "",
                GroupType = typeDetails.Contains("Security") ? "MailEnabledSecurity" : "Distribution",
                IsDirSynced = isDirSynced,
                Backend = isDirSynced ? "OnPremAD" : "ExchangeOnline"
            });
        }

        // Search Microsoft 365 Groups via Graph API
        if (GetGraphClient().IsConfigured)
        {
            try
            {
                var encoded = Uri.EscapeDataString(searchTerm);
                using var graphResult = await GetGraphClient().GetAsync($"/groups?$filter=groupTypes/any(g:g eq 'Unified') and (startsWith(displayName,'{encoded}') or startsWith(mail,'{encoded}'))&$top=25&$select=id,displayName,mail,groupTypes");
                if (graphResult != null)
                {
                    foreach (var g in graphResult.RootElement.GetProperty("value").EnumerateArray())
                    {
                        results.Add(new GroupInfo
                        {
                            Name = g.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "",
                            Email = g.TryGetProperty("mail", out var mail) ? mail.GetString() ?? "" : "",
                            GroupType = "Microsoft365",
                            IsDirSynced = false,
                            Backend = "Graph",
                            GraphId = g.TryGetProperty("id", out var id) ? id.GetString() ?? "" : ""
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Graph group search failed — M365 groups not included in results");
            }
        }

        return results;
    }

    public async Task<GroupMemberList> GetMembersAsync(string groupIdentity, string backend, string? graphId = null, string? samAccountName = null)
    {
        if (backend == "ExchangeOnline")
            return await GetExoMembersAsync(groupIdentity);
        if (backend == "OnPremAD")
            return await GetOnPremMembersAsync(samAccountName ?? groupIdentity);
        if (backend == "Graph" && !string.IsNullOrEmpty(graphId))
            return await GetGraphMembersAsync(graphId, groupIdentity);
        return new GroupMemberList { GroupName = groupIdentity, Error = $"Unknown backend: {backend}" };
    }

    public async Task<PermissionResult> AddMemberAsync(string groupIdentity, string member, string backend, string? graphId = null, string? samAccountName = null)
    {
        if (backend == "ExchangeOnline")
            return await AddExoMemberAsync(groupIdentity, member);
        if (backend == "OnPremAD")
            return await AddOnPremMemberAsync(samAccountName ?? groupIdentity, member);
        if (backend == "Graph" && !string.IsNullOrEmpty(graphId))
            return await AddGraphMemberAsync(graphId, groupIdentity, member);
        return PermissionResult.Fail($"Unknown backend: {backend}");
    }

    public async Task<PermissionResult> RemoveMemberAsync(string groupIdentity, string member, string backend, string? graphId = null, string? samAccountName = null)
    {
        if (backend == "ExchangeOnline")
            return await RemoveExoMemberAsync(groupIdentity, member);
        if (backend == "OnPremAD")
            return await RemoveOnPremMemberAsync(samAccountName ?? groupIdentity, member);
        if (backend == "Graph" && !string.IsNullOrEmpty(graphId))
            return await RemoveGraphMemberAsync(graphId, groupIdentity, member);
        return PermissionResult.Fail($"Unknown backend: {backend}");
    }

    // --- EXO operations ---

    private async Task<GroupMemberList> GetExoMembersAsync(string groupIdentity)
    {
        return await RunPooledQueryAsync(ps =>
        {
            var result = new GroupMemberList { GroupName = groupIdentity };
            ps.AddCommand("Get-DistributionGroupMember")
              .AddParameter("Identity", groupIdentity)
              .AddParameter("ResultSize", "Unlimited")
              .AddParameter("ErrorAction", "Stop");
            var members = Invoke(ps);

            foreach (var m in members)
            {
                result.Members.Add(new GroupMemberInfo
                {
                    DisplayName = m.Properties["DisplayName"]?.Value?.ToString() ?? "",
                    Email = m.Properties["PrimarySmtpAddress"]?.Value?.ToString() ?? "",
                    RecipientType = m.Properties["RecipientType"]?.Value?.ToString() ?? ""
                });
            }
            return result;
        });
    }

    private async Task<PermissionResult> AddExoMemberAsync(string groupIdentity, string member)
    {
        return await RunAsync(ps =>
        {
            ps.AddCommand("Add-DistributionGroupMember")
              .AddParameter("Identity", groupIdentity)
              .AddParameter("Member", member)
              .AddParameter("ErrorAction", "Stop");
            Invoke(ps);
        }, () => ($"{member} added to {groupIdentity}.", null));
    }

    private async Task<PermissionResult> RemoveExoMemberAsync(string groupIdentity, string member)
    {
        return await RunAsync(ps =>
        {
            ps.AddCommand("Remove-DistributionGroupMember")
              .AddParameter("Identity", groupIdentity)
              .AddParameter("Member", member)
              .AddParameter("Confirm", false)
              .AddParameter("ErrorAction", "Stop");
            Invoke(ps);
        }, () => ($"{member} removed from {groupIdentity}.", null));
    }

    // --- On-prem AD operations ---

    private async Task<GroupMemberList> GetOnPremMembersAsync(string groupIdentity)
    {
        var creds = await _delineaService.GetExchangeCredentialsAsync();
        if (creds is null)
            return new GroupMemberList { GroupName = groupIdentity, Error = "AD credentials unavailable." };

        return await ThrottledAsync(async () => await Task.Run(() =>
        {
            var result = new GroupMemberList { GroupName = groupIdentity };
            var iss = InitialSessionState.CreateDefault();
            iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
            using var runspace = RunspaceFactory.CreateRunspace(iss);
            runspace.Open();
            using var ps = PowerShell.Create();
            ps.Runspace = runspace;

            ps.AddCommand("Import-Module").AddParameter("Name", "ActiveDirectory").AddParameter("ErrorAction", "Stop");
            ps.Invoke();
            ps.Commands.Clear();

            var credential = CreateCredential(creds.Value.username, creds.Value.password, creds.Value.domain);

            ps.AddCommand("Get-ADGroupMember")
              .AddParameter("Identity", groupIdentity)
              .AddParameter("Credential", credential)
              .AddParameter("ErrorAction", "Stop");
            var members = ps.Invoke();
            ps.Commands.Clear();

            foreach (var m in members)
            {
                var sam = m.Properties["SamAccountName"]?.Value?.ToString() ?? "";
                ps.AddCommand("Get-ADUser")
                  .AddParameter("Identity", sam)
                  .AddParameter("Properties", new[] { "EmailAddress", "DisplayName" })
                  .AddParameter("Credential", credential)
                  .AddParameter("ErrorAction", "SilentlyContinue");
                var userResults = ps.Invoke();
                ps.Commands.Clear();

                var user = userResults.FirstOrDefault();
                result.Members.Add(new GroupMemberInfo
                {
                    DisplayName = user?.Properties["DisplayName"]?.Value?.ToString() ?? m.Properties["Name"]?.Value?.ToString() ?? "",
                    Email = user?.Properties["EmailAddress"]?.Value?.ToString() ?? "",
                    RecipientType = "ADUser"
                });
            }

            return result;
        }), _adThrottle);
    }

    private async Task<PermissionResult> AddOnPremMemberAsync(string groupIdentity, string member)
    {
        var creds = await _delineaService.GetExchangeCredentialsAsync();
        if (creds is null)
            return PermissionResult.Fail("AD credentials unavailable.");

        return await ThrottledAsync(async () => await Task.Run(() =>
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

            var credential = CreateCredential(creds.Value.username, creds.Value.password, creds.Value.domain);

            // Resolve member by UPN or email
            ps.AddCommand("Get-ADUser")
              .AddParameter("Filter", $"UserPrincipalName -eq '{member.Replace("'", "''")}' -or EmailAddress -eq '{member.Replace("'", "''")}'")
              .AddParameter("Credential", credential)
              .AddParameter("ErrorAction", "Stop");
            var users = ps.Invoke();
            ps.Commands.Clear();

            if (users.Count == 0)
                throw new InvalidOperationException($"User '{member}' not found in AD.");
            if (users.Count > 1)
                throw new InvalidOperationException($"Ambiguous: '{member}' matches {users.Count} AD users.");

            ps.AddCommand("Add-ADGroupMember")
              .AddParameter("Identity", groupIdentity)
              .AddParameter("Members", users[0].Properties["DistinguishedName"]?.Value?.ToString())
              .AddParameter("Credential", credential)
              .AddParameter("ErrorAction", "Stop");
            ps.Invoke();
            ps.Commands.Clear();

            return PermissionResult.Ok($"{member} added to {groupIdentity} (on-premises).");
        }), _adThrottle);
    }

    private async Task<PermissionResult> RemoveOnPremMemberAsync(string groupIdentity, string member)
    {
        var creds = await _delineaService.GetExchangeCredentialsAsync();
        if (creds is null)
            return PermissionResult.Fail("AD credentials unavailable.");

        return await ThrottledAsync(async () => await Task.Run(() =>
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

            var credential = CreateCredential(creds.Value.username, creds.Value.password, creds.Value.domain);

            ps.AddCommand("Get-ADUser")
              .AddParameter("Filter", $"UserPrincipalName -eq '{member.Replace("'", "''")}' -or EmailAddress -eq '{member.Replace("'", "''")}'")
              .AddParameter("Credential", credential)
              .AddParameter("ErrorAction", "Stop");
            var users = ps.Invoke();
            ps.Commands.Clear();

            if (users.Count == 0)
                throw new InvalidOperationException($"User '{member}' not found in AD.");
            if (users.Count > 1)
                throw new InvalidOperationException($"Ambiguous: '{member}' matches {users.Count} AD users.");

            ps.AddCommand("Remove-ADGroupMember")
              .AddParameter("Identity", groupIdentity)
              .AddParameter("Members", users[0].Properties["DistinguishedName"]?.Value?.ToString())
              .AddParameter("Credential", credential)
              .AddParameter("Confirm", false)
              .AddParameter("ErrorAction", "Stop");
            ps.Invoke();
            ps.Commands.Clear();

            return PermissionResult.Ok($"{member} removed from {groupIdentity} (on-premises).");
        }), _adThrottle);
    }

    // --- Graph API operations (M365 Groups) ---

    private async Task<GroupMemberList> GetGraphMembersAsync(string groupId, string displayName)
    {
        var result = new GroupMemberList { GroupName = displayName };
        using var doc = await GetGraphClient().GetAsync($"/groups/{groupId}/members?$select=displayName,mail,userPrincipalName&$top=999");
        if (doc == null)
        {
            result.Error = "Failed to retrieve M365 group members.";
            return result;
        }

        foreach (var m in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            result.Members.Add(new GroupMemberInfo
            {
                DisplayName = m.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "",
                Email = m.TryGetProperty("mail", out var mail) ? mail.GetString() ?? ""
                    : m.TryGetProperty("userPrincipalName", out var upn) ? upn.GetString() ?? "" : "",
                RecipientType = "M365Member"
            });
        }
        return result;
    }

    private async Task<PermissionResult> AddGraphMemberAsync(string groupId, string displayName, string member)
    {
        using var userDoc = await GetGraphClient().GetAsync($"/users/{Uri.EscapeDataString(member)}?$select=id");
        if (userDoc == null)
            return PermissionResult.Fail($"User '{member}' not found in Entra ID.");

        var userId = userDoc.RootElement.GetProperty("id").GetString();
        var body = new Dictionary<string, string>
        {
            ["@odata.id"] = $"https://graph.microsoft.com/v1.0/directoryObjects/{userId}"
        };
        var success = await GetGraphClient().PostNoContentAsync($"/groups/{groupId}/members/$ref", body);

        return success
            ? new PermissionResult { Success = true, Message = $"{member} added to {displayName} (M365 Group)." }
            : PermissionResult.Fail($"Failed to add {member} to {displayName}. Check Graph API permissions.");
    }

    private async Task<PermissionResult> RemoveGraphMemberAsync(string groupId, string displayName, string member)
    {
        using var userDoc = await GetGraphClient().GetAsync($"/users/{Uri.EscapeDataString(member)}?$select=id");
        if (userDoc == null)
            return PermissionResult.Fail($"User '{member}' not found in Entra ID.");

        var userId = userDoc.RootElement.GetProperty("id").GetString();
        var success = await GetGraphClient().DeleteAsync($"/groups/{groupId}/members/{userId}/$ref");

        return success
            ? new PermissionResult { Success = true, Message = $"{member} removed from {displayName} (M365 Group)." }
            : PermissionResult.Fail($"Failed to remove {member} from {displayName}.");
    }

    private static PSCredential CreateCredential(string username, string password, string domain)
    {
        var fullUsername = username.Contains('\\') || username.Contains('@')
            ? username : $"{domain}\\{username}";
        var securePassword = new System.Security.SecureString();
        foreach (var c in password) securePassword.AppendChar(c);
        return new PSCredential(fullUsername, securePassword);
    }
}

public class GroupInfo
{
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Identity { get; set; } = "";
    public string SamAccountName { get; set; } = "";
    public string GroupType { get; set; } = "";
    public bool IsDirSynced { get; set; }
    public string Backend { get; set; } = "";
    public string GraphId { get; set; } = "";
}

public class GroupMemberList
{
    public string GroupName { get; set; } = "";
    public string? Error { get; set; }
    public List<GroupMemberInfo> Members { get; set; } = new();
}

public class GroupMemberInfo
{
    public string DisplayName { get; set; } = "";
    public string Email { get; set; } = "";
    public string RecipientType { get; set; } = "";
}
