using System.Management.Automation;
using System.Management.Automation.Runspaces;

namespace ExchangeAdminWeb.Services;

public class Comms10kService
{
    private readonly ILogger<Comms10kService> _logger;
    private readonly ModuleConfigService _moduleConfig;
    private readonly DelineaService _delineaService;
    private readonly IConfiguration _config;

    public Comms10kService(ILogger<Comms10kService> logger, ModuleConfigService moduleConfig, DelineaService delineaService, IConfiguration config)
    {
        _logger = logger;
        _moduleConfig = moduleConfig;
        _delineaService = delineaService;
        _config = config;
    }

    private string? TargetGroup
    {
        get
        {
            var val = _moduleConfig.GetValue("Comms10k", "TargetGroupName");
            if (_moduleConfig.IsCorrupt) return null;
            return val;
        }
    }


    public bool IsConfigured => !string.IsNullOrEmpty(TargetGroup);

    public async Task<Comms10kMemberList> GetMembersAsync(int? limit = null)
    {
        var group = TargetGroup;
        if (string.IsNullOrEmpty(group))
            throw new InvalidOperationException("Comms10k module is not configured. Set TargetGroupName in Admin Settings.");

        var creds = await _delineaService.GetExchangeCredentialsAsync();
        if (creds is null)
            throw new InvalidOperationException("Cannot connect to AD: credentials unavailable.");

        return await Task.Run(() =>
        {
            var iss = InitialSessionState.CreateDefault();
            iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
            using var runspace = RunspaceFactory.CreateRunspace(iss);
            runspace.Open();
            using var ps = PowerShell.Create();
            ps.Runspace = runspace;

            ps.AddCommand("Import-Module")
              .AddParameter("Name", "ActiveDirectory")
              .AddParameter("ErrorAction", "Stop");
            ps.Invoke();
            ps.Commands.Clear();

            var server = _config["OnPremExchange:ServerUri"]?.Replace("/PowerShell/", "").Replace("http://", "").Replace("https://", "");
            var credential = CreateCredential(creds.Value.username, creds.Value.password, creds.Value.domain);

            ps.AddCommand("Get-ADGroupMember")
              .AddParameter("Identity", group)
              .AddParameter("Credential", credential)
              .AddParameter("ErrorAction", "Stop");
            var members = ps.Invoke();
            ps.Commands.Clear();

            var result = new Comms10kMemberList { GroupName = group };

            foreach (var member in members)
            {
                var sam = member.Properties["SamAccountName"]?.Value?.ToString() ?? "";
                var name = member.Properties["Name"]?.Value?.ToString() ?? "";

                ps.AddCommand("Get-ADUser")
                  .AddParameter("Identity", sam)
                  .AddParameter("Properties", new[] { "EmailAddress", "DisplayName" })
                  .AddParameter("Credential", credential)
                  .AddParameter("ErrorAction", "SilentlyContinue");
                var userResults = ps.Invoke();
                ps.Commands.Clear();

                var user = userResults.FirstOrDefault();
                var email = user?.Properties["EmailAddress"]?.Value?.ToString() ?? "";
                var displayName = user?.Properties["DisplayName"]?.Value?.ToString() ?? name;

                result.Members.Add(new Comms10kMember
                {
                    Email = email,
                    SamAccountName = sam,
                    DisplayName = displayName
                });
            }

            result.TotalCount = result.Members.Count;
            if (limit.HasValue)
                result.Members = result.Members.Take(limit.Value).ToList();

            return result;
        });
    }

    public async Task<Comms10kResolveResult> ResolveEmailsAsync(List<string> emails)
    {
        var group = TargetGroup;
        if (string.IsNullOrEmpty(group))
            return new Comms10kResolveResult { Success = false, Message = "Module not configured." };

        var creds = await _delineaService.GetExchangeCredentialsAsync();
        if (creds is null)
            return new Comms10kResolveResult { Success = false, Message = "AD credentials unavailable." };

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

            var credential = CreateCredential(creds.Value.username, creds.Value.password, creds.Value.domain);
            var resolved = new List<string>();
            var skipped = new List<string>();

            foreach (var email in emails)
            {
                var escaped = email.Replace("'", "''");
                ps.AddCommand("Get-ADUser")
                  .AddParameter("Filter", $"UserPrincipalName -eq '{escaped}' -or EmailAddress -eq '{escaped}'")
                  .AddParameter("Credential", credential)
                  .AddParameter("ErrorAction", "SilentlyContinue");
                var userResults = ps.Invoke();
                ps.Commands.Clear();

                if (userResults.Count > 1)
                {
                    skipped.Add($"{email} (ambiguous: {userResults.Count} matches)");
                }
                else if (userResults.Count == 1)
                {
                    var dn = userResults[0].Properties["DistinguishedName"]?.Value?.ToString();
                    if (dn != null) resolved.Add(dn);
                    else skipped.Add(email);
                }
                else
                {
                    skipped.Add(email);
                }
            }

            return new Comms10kResolveResult
            {
                Success = true,
                ResolvedDns = resolved,
                SkippedEmails = skipped,
                Message = $"{resolved.Count} resolved, {skipped.Count} not found."
            };
        });
    }

    public async Task<Comms10kUpdateResult> ExecuteReplaceAsync(List<string> resolvedDns, string performedBy)
    {
        if (resolvedDns.Count == 0)
            return new Comms10kUpdateResult { Success = false, Message = "Cannot replace with an empty member list." };

        var group = TargetGroup;
        if (string.IsNullOrEmpty(group))
            return new Comms10kUpdateResult { Success = false, Message = "Module not configured." };

        var creds = await _delineaService.GetExchangeCredentialsAsync();
        if (creds is null)
            return new Comms10kUpdateResult { Success = false, Message = "AD credentials unavailable." };

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

            var credential = CreateCredential(creds.Value.username, creds.Value.password, creds.Value.domain);

            // Get initial count
            ps.AddCommand("Get-ADGroupMember")
              .AddParameter("Identity", group)
              .AddParameter("Credential", credential)
              .AddParameter("ErrorAction", "Stop");
            var initialMembers = ps.Invoke();
            ps.Commands.Clear();
            var initialCount = initialMembers.Count;

            try
            {
                ps.AddCommand("Set-ADGroup")
                  .AddParameter("Identity", group)
                  .AddParameter("Replace", new System.Collections.Hashtable { { "member", resolvedDns.ToArray() } })
                  .AddParameter("Credential", credential)
                  .AddParameter("ErrorAction", "Stop");
                ps.Invoke();
                ps.Commands.Clear();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to replace members of {Group}", group);
                return new Comms10kUpdateResult { Success = false, Message = $"Failed to update group: {ex.Message}" };
            }

            _logger.LogInformation("Comms10k updated by {User}: {Initial} -> {Final} members",
                performedBy, initialCount, resolvedDns.Count);

            return new Comms10kUpdateResult
            {
                Success = true,
                Message = $"Successfully updated {group}: {resolvedDns.Count} members (was {initialCount}).",
                InitialCount = initialCount,
                FinalCount = resolvedDns.Count
            };
        });
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

public class Comms10kMember
{
    public string Email { get; set; } = "";
    public string SamAccountName { get; set; } = "";
    public string DisplayName { get; set; } = "";
}

public class Comms10kMemberList
{
    public string GroupName { get; set; } = "";
    public int TotalCount { get; set; }
    public List<Comms10kMember> Members { get; set; } = new();
}

public class Comms10kResolveResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public List<string> ResolvedDns { get; set; } = new();
    public List<string> SkippedEmails { get; set; } = new();
}

public class Comms10kUpdateResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public int InitialCount { get; set; }
    public int FinalCount { get; set; }
}
