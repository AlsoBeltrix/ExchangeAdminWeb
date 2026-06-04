using System.Management.Automation;
using System.Management.Automation.Runspaces;
using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Services;

public class GroupManagementService
{
    private readonly ModuleConfigService _moduleConfig;
    private readonly ModuleCredentialService _moduleCredentials;
    private readonly ILogger<GroupManagementService> _logger;
    private static readonly SemaphoreSlim _adThrottle = new(2, 2);

    public GroupManagementService(
        ModuleConfigService moduleConfig,
        ModuleCredentialService moduleCredentials,
        ILogger<GroupManagementService> logger)
    {
        _moduleConfig = moduleConfig;
        _moduleCredentials = moduleCredentials;
        _logger = logger;
    }

    public async Task<List<GroupInfo>> SearchGroupsAsync(string searchTerm)
    {
        var creds = await GetCredentialsAsync("on-prem AD group search");
        if (creds is null)
            throw new InvalidOperationException("AD credentials unavailable. Check the DelineaSecretId configuration for GroupManagement.");

        return await ThrottledAdAsync(async () => await Task.Run(() =>
        {
            var results = new List<GroupInfo>();
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
            var escaped = searchTerm.Replace("'", "''");

            ps.AddCommand("Get-ADGroup")
              .AddParameter("Filter", $"Name -like '*{escaped}*' -or SamAccountName -like '*{escaped}*' -or Mail -like '*{escaped}*'")
              .AddParameter("Properties", new[] { "Mail", "GroupCategory", "GroupScope", "SamAccountName", "Description" })
              .AddParameter("Credential", credential)
              .AddParameter("ResultSetSize", 25)
              .AddParameter("ErrorAction", "Stop");
            var groups = ps.Invoke();
            ps.Commands.Clear();

            foreach (var group in groups)
            {
                var category = group.Properties["GroupCategory"]?.Value?.ToString() ?? "";
                var scope = group.Properties["GroupScope"]?.Value?.ToString() ?? "";
                var groupType = category == "Security" ? $"Security ({scope})" : $"Distribution ({scope})";

                results.Add(new GroupInfo
                {
                    Name = group.Properties["Name"]?.Value?.ToString() ?? "",
                    Email = group.Properties["Mail"]?.Value?.ToString() ?? "",
                    Identity = group.Properties["DistinguishedName"]?.Value?.ToString() ?? "",
                    SamAccountName = group.Properties["SamAccountName"]?.Value?.ToString() ?? "",
                    GroupType = groupType,
                    Backend = "OnPremAD"
                });
            }

            return results;
        }));
    }

    public async Task<GroupMemberList> GetMembersAsync(string groupIdentity, string? samAccountName = null)
    {
        var creds = await GetCredentialsAsync("on-prem AD group membership lookup");
        if (creds is null)
            return new GroupMemberList { GroupName = groupIdentity, Error = "AD credentials unavailable." };

        return await ThrottledAdAsync(async () => await Task.Run(() =>
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
            var resolvedDn = ResolveAdGroupIdentity(ps, samAccountName, groupIdentity, credential);

            ps.AddCommand("Get-ADGroupMember")
              .AddParameter("Identity", resolvedDn)
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
        }));
    }

    public async Task<PermissionResult> AddMemberAsync(string groupIdentity, string member, string? samAccountName = null)
    {
        var creds = await GetCredentialsAsync("on-prem AD group membership add");
        if (creds is null)
            return PermissionResult.Fail("AD credentials unavailable.");

        return await ThrottledAdAsync(async () => await Task.Run(() =>
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
            var resolvedGroupDn = ResolveAdGroupIdentity(ps, samAccountName, groupIdentity, credential);

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
              .AddParameter("Identity", resolvedGroupDn)
              .AddParameter("Members", users[0].Properties["DistinguishedName"]?.Value?.ToString())
              .AddParameter("Credential", credential)
              .AddParameter("ErrorAction", "Stop");
            ps.Invoke();
            ps.Commands.Clear();

            return PermissionResult.Ok($"{member} added to {groupIdentity} (on-premises).");
        }));
    }

    public async Task<PermissionResult> RemoveMemberAsync(string groupIdentity, string member, string? samAccountName = null)
    {
        var creds = await GetCredentialsAsync("on-prem AD group membership remove");
        if (creds is null)
            return PermissionResult.Fail("AD credentials unavailable.");

        return await ThrottledAdAsync(async () => await Task.Run(() =>
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
            var resolvedGroupDn = ResolveAdGroupIdentity(ps, samAccountName, groupIdentity, credential);

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
              .AddParameter("Identity", resolvedGroupDn)
              .AddParameter("Members", users[0].Properties["DistinguishedName"]?.Value?.ToString())
              .AddParameter("Credential", credential)
              .AddParameter("Confirm", false)
              .AddParameter("ErrorAction", "Stop");
            ps.Invoke();
            ps.Commands.Clear();

            return PermissionResult.Ok($"{member} removed from {groupIdentity} (on-premises).");
        }));
    }

    // --- Helpers ---

    private async Task<(string username, string password, string domain)?> GetCredentialsAsync(string purpose)
    {
        return await _moduleCredentials.GetCredentialsAsync("GroupManagement", purpose);
    }

    private async Task<T> ThrottledAdAsync<T>(Func<Task<T>> operation)
    {
        if (!await _adThrottle.WaitAsync(TimeSpan.FromMinutes(2)))
            throw new InvalidOperationException("AD group service is busy. Please try again shortly.");
        try { return await operation(); }
        finally { _adThrottle.Release(); }
    }

    private static string ResolveAdGroupIdentity(PowerShell ps, string? alias, string email, PSCredential credential)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(alias)) candidates.Add(alias);
        if (!string.IsNullOrEmpty(email)) candidates.Add(email);

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var escaped = candidate.Replace("'", "''");
            ps.AddCommand("Get-ADGroup")
              .AddParameter("Filter", $"SamAccountName -eq '{escaped}' -or Name -eq '{escaped}' -or Mail -eq '{escaped}'")
              .AddParameter("Credential", credential)
              .AddParameter("ErrorAction", "SilentlyContinue");
            var groups = ps.Invoke();
            ps.Commands.Clear();

            if (groups.Count == 1)
                return groups[0].Properties["DistinguishedName"]?.Value?.ToString()
                    ?? throw new InvalidOperationException($"Could not resolve DN for group '{candidate}'.");
        }

        var tried = string.Join(", ", candidates);
        throw new InvalidOperationException($"AD group not found. Tried: {tried}");
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
    public string Backend { get; set; } = "OnPremAD";
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
