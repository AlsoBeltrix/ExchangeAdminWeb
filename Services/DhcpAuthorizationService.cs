using System.Management.Automation;
using System.Management.Automation.Runspaces;

namespace ExchangeAdminWeb.Services;

public class DhcpAuthorizationService
{
    private readonly ILogger<DhcpAuthorizationService> _logger;
    private readonly ModuleCredentialService _moduleCredentials;
    private readonly ModuleConfigService _moduleConfig;

    public DhcpAuthorizationService(ILogger<DhcpAuthorizationService> logger, ModuleCredentialService moduleCredentials, ModuleConfigService moduleConfig)
    {
        _logger = logger;
        _moduleCredentials = moduleCredentials;
        _moduleConfig = moduleConfig;
    }

    private int? GetSecretId()
    {
        var val = _moduleConfig.GetValue("DhcpAuthorization", "DelineaSecretId");
        return int.TryParse(val, out var id) && id > 0 ? id : null;
    }

    public async Task<List<DhcpServerEntry>> GetAuthorizedServersAsync()
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
              .AddParameter("Name", "DhcpServer")
              .AddParameter("ErrorAction", "Stop");
            ps.Invoke();
            ps.Commands.Clear();

            ps.AddCommand("Get-DhcpServerInDC")
              .AddParameter("ErrorAction", "Stop");
            var results = ps.Invoke();
            ps.Commands.Clear();

            var servers = new List<DhcpServerEntry>();
            foreach (var r in results)
            {
                servers.Add(new DhcpServerEntry
                {
                    DnsName = r.Properties["DnsName"]?.Value?.ToString() ?? "",
                    IpAddress = r.Properties["IPAddress"]?.Value?.ToString() ?? ""
                });
            }

            return servers;
        });
    }

    public async Task<DhcpOperationResult> AuthorizeServerAsync(string dnsName, string ipAddress)
    {
        var secretId = GetSecretId();
        if (secretId is null)
            return new DhcpOperationResult { Success = false, Message = "DHCP module not configured. Set DelineaSecretId in Module Config." };
        var creds = await _moduleCredentials.GetCredentialsAsync("DhcpAuthorization", "DHCP authorization");
        if (creds is null)
            return new DhcpOperationResult { Success = false, Message = "Enterprise Admin credentials unavailable from Delinea." };

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

            var credential = CreateCredential(creds.Value.username, creds.Value.password, creds.Value.domain);
            var dc = DiscoverDomainController(ps, credential);
            if (dc == null)
                return new DhcpOperationResult { Success = false, Message = "Could not discover a domain controller." };

            ps.AddCommand("Import-Module")
              .AddParameter("Name", "DhcpServer")
              .AddParameter("ErrorAction", "Stop");
            ps.Invoke();
            ps.Commands.Clear();

            var script = ScriptBlock.Create("param($DnsName, $IPAddress) Add-DhcpServerInDC -DnsName $DnsName -IPAddress $IPAddress -ErrorAction Stop");
            ps.AddCommand("Invoke-Command")
              .AddParameter("ComputerName", dc)
              .AddParameter("Credential", credential)
              .AddParameter("ScriptBlock", script)
              .AddParameter("ArgumentList", new object[] { dnsName, ipAddress });

            try
            {
                ps.Invoke();
                if (ps.HadErrors)
                {
                    var errors = string.Join("; ", ps.Streams.Error.Select(e => e.ToString()));
                    return new DhcpOperationResult { Success = false, Message = $"Failed to authorize: {errors}" };
                }

                _logger.LogInformation("DHCP server authorized: {DnsName} ({Ip}) via DC {DC}", dnsName, ipAddress, dc);
                return new DhcpOperationResult { Success = true, Message = $"Successfully authorized DHCP server {dnsName} ({ipAddress})." };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Add-DhcpServerInDC exception for {DnsName}/{Ip} via DC {DC}", dnsName, ipAddress, dc);
                return new DhcpOperationResult { Success = false, Message = $"Failed to authorize: {ex.Message}" };
            }
        });
    }

    public async Task<DhcpOperationResult> DeauthorizeServerAsync(string dnsName, string ipAddress)
    {
        var secretId = GetSecretId();
        if (secretId is null)
            return new DhcpOperationResult { Success = false, Message = "DHCP module not configured. Set DelineaSecretId in Module Config." };
        var creds = await _moduleCredentials.GetCredentialsAsync("DhcpAuthorization", "DHCP deauthorization");
        if (creds is null)
            return new DhcpOperationResult { Success = false, Message = "Enterprise Admin credentials unavailable from Delinea." };

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

            var credential = CreateCredential(creds.Value.username, creds.Value.password, creds.Value.domain);
            var dc = DiscoverDomainController(ps, credential);
            if (dc == null)
                return new DhcpOperationResult { Success = false, Message = "Could not discover a domain controller." };

            ps.AddCommand("Import-Module")
              .AddParameter("Name", "DhcpServer")
              .AddParameter("ErrorAction", "Stop");
            ps.Invoke();
            ps.Commands.Clear();

            var script = ScriptBlock.Create("param($DnsName, $IPAddress) Remove-DhcpServerInDC -DnsName $DnsName -IPAddress $IPAddress -ErrorAction Stop");
            ps.AddCommand("Invoke-Command")
              .AddParameter("ComputerName", dc)
              .AddParameter("Credential", credential)
              .AddParameter("ScriptBlock", script)
              .AddParameter("ArgumentList", new object[] { dnsName, ipAddress });

            try
            {
                ps.Invoke();
                if (ps.HadErrors)
                {
                    var errors = string.Join("; ", ps.Streams.Error.Select(e => e.ToString()));
                    return new DhcpOperationResult { Success = false, Message = $"Failed to deauthorize: {errors}" };
                }

                _logger.LogInformation("DHCP server deauthorized: {DnsName} ({Ip}) via DC {DC}", dnsName, ipAddress, dc);
                return new DhcpOperationResult { Success = true, Message = $"Successfully deauthorized DHCP server {dnsName} ({ipAddress})." };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Remove-DhcpServerInDC exception for {DnsName}/{Ip} via DC {DC}", dnsName, ipAddress, dc);
                return new DhcpOperationResult { Success = false, Message = $"Failed to deauthorize: {ex.Message}" };
            }
        });
    }

    private string? DiscoverDomainController(PowerShell ps, PSCredential credential)
    {
        try
        {
            ps.AddCommand("Get-ADDomainController")
              .AddParameter("Discover", true)
              .AddParameter("Service", "ADWS")
              .AddParameter("Credential", credential)
              .AddParameter("ErrorAction", "Stop");
            var result = ps.Invoke();
            ps.Commands.Clear();

            var dc = result.FirstOrDefault()?.Properties["HostName"]?.Value?.ToString();
            if (!string.IsNullOrEmpty(dc))
            {
                _logger.LogInformation("Discovered domain controller: {DC}", dc);
                return dc;
            }
        }
        catch (Exception ex)
        {
            ps.Commands.Clear();
            _logger.LogWarning(ex, "Get-ADDomainController -Discover failed, falling back to Filter query");
        }

        try
        {
            ps.AddCommand("Get-ADDomainController")
              .AddParameter("Filter", "*")
              .AddParameter("Credential", credential)
              .AddParameter("ErrorAction", "Stop");
            var results = ps.Invoke();
            ps.Commands.Clear();

            var dc = results.FirstOrDefault()?.Properties["HostName"]?.Value?.ToString();
            if (!string.IsNullOrEmpty(dc))
            {
                _logger.LogInformation("Using domain controller from filter query: {DC}", dc);
                return dc;
            }
        }
        catch (Exception ex)
        {
            ps.Commands.Clear();
            _logger.LogError(ex, "Failed to discover any domain controller");
        }

        return null;
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

public class DhcpServerEntry
{
    public string DnsName { get; set; } = "";
    public string IpAddress { get; set; } = "";
}

public class DhcpOperationResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
}
