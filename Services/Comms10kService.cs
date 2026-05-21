using System.Management.Automation;
using System.Management.Automation.Runspaces;

namespace ExchangeAdminWeb.Services;

public class Comms10kService
{
    private readonly ILogger<Comms10kService> _logger;
    private readonly ModuleConfigService _moduleConfig;

    public Comms10kService(ILogger<Comms10kService> logger, ModuleConfigService moduleConfig)
    {
        _logger = logger;
        _moduleConfig = moduleConfig;
    }

    private string TargetGroup => _moduleConfig.GetValue("Comms10k", "TargetGroupName") ?? "Comms-10k";
    private string DomainName => _moduleConfig.GetValue("Comms10k", "DomainName") ?? "ANALOG";

    public async Task<Comms10kMemberList> GetMembersAsync(int? limit = null)
    {
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

            ps.AddCommand("Get-ADGroupMember")
              .AddParameter("Identity", TargetGroup)
              .AddParameter("ErrorAction", "Stop");
            var members = ps.Invoke();
            ps.Commands.Clear();

            var result = new Comms10kMemberList { GroupName = TargetGroup };

            foreach (var member in members)
            {
                var sam = member.Properties["SamAccountName"]?.Value?.ToString() ?? "";
                var name = member.Properties["Name"]?.Value?.ToString() ?? "";
                var dn = member.Properties["DistinguishedName"]?.Value?.ToString() ?? "";

                // Get email via Get-ADUser
                ps.AddCommand("Get-ADUser")
                  .AddParameter("Identity", sam)
                  .AddParameter("Properties", new[] { "EmailAddress", "DisplayName" })
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

    public async Task<Comms10kUpdateResult> ReplaceAllMembersAsync(List<string> emails, string performedBy)
    {
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

            // Get initial member count
            ps.AddCommand("Get-ADGroupMember")
              .AddParameter("Identity", TargetGroup)
              .AddParameter("ErrorAction", "Stop");
            var initialMembers = ps.Invoke();
            ps.Commands.Clear();
            var initialCount = initialMembers.Count;

            // Resolve emails to DNs
            var resolvedDns = new List<string>();
            var skipped = new List<string>();

            foreach (var email in emails)
            {
                ps.AddCommand("Get-ADUser")
                  .AddParameter("Filter", $"UserPrincipalName -eq '{email.Replace("'", "''")}'")
                  .AddParameter("ErrorAction", "SilentlyContinue");
                var userResults = ps.Invoke();
                ps.Commands.Clear();

                var user = userResults.FirstOrDefault();
                if (user != null)
                {
                    var dn = user.Properties["DistinguishedName"]?.Value?.ToString();
                    if (dn != null) resolvedDns.Add(dn);
                    else skipped.Add(email);
                }
                else
                {
                    skipped.Add(email);
                }
            }

            if (resolvedDns.Count == 0)
            {
                return new Comms10kUpdateResult
                {
                    Success = false,
                    Message = "No valid users found in the uploaded list.",
                    SkippedEmails = skipped
                };
            }

            // Atomic replacement
            try
            {
                ps.AddCommand("Set-ADGroup")
                  .AddParameter("Identity", TargetGroup)
                  .AddParameter("Replace", new System.Collections.Hashtable { { "member", resolvedDns.ToArray() } })
                  .AddParameter("ErrorAction", "Stop");
                ps.Invoke();
                ps.Commands.Clear();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to replace members of {Group}", TargetGroup);
                return new Comms10kUpdateResult
                {
                    Success = false,
                    Message = $"Failed to update group: {ex.Message}",
                    SkippedEmails = skipped
                };
            }

            var added = resolvedDns.Count - initialMembers.Where(m =>
                resolvedDns.Contains(m.Properties["DistinguishedName"]?.Value?.ToString() ?? "")).Count();
            var removed = initialCount - initialMembers.Where(m =>
                resolvedDns.Contains(m.Properties["DistinguishedName"]?.Value?.ToString() ?? "")).Count();

            _logger.LogInformation("Comms10k updated by {User}: {Initial} -> {Final} members ({Added} added, {Removed} removed)",
                performedBy, initialCount, resolvedDns.Count, added, removed);

            return new Comms10kUpdateResult
            {
                Success = true,
                Message = $"Successfully updated {TargetGroup}: {resolvedDns.Count} members (was {initialCount}).",
                InitialCount = initialCount,
                FinalCount = resolvedDns.Count,
                ResolvedCount = resolvedDns.Count,
                SkippedEmails = skipped
            };
        });
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

public class Comms10kUpdateResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public int InitialCount { get; set; }
    public int FinalCount { get; set; }
    public int ResolvedCount { get; set; }
    public List<string> SkippedEmails { get; set; } = new();
}
