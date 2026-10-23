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

            return AuthorizeServer(ps, creds.Value, dnsName, ipAddress);
        });
    }

    /// <remarks>
    /// Internal as a TEST SEAM (psl-3): import, discovery and the change on a caller-supplied
    /// PowerShell, so the sequence runs against local stand-in functions without a domain. The
    /// async method above keeps the configuration and credential gates and owns the runspace.
    /// </remarks>
    internal DhcpOperationResult AuthorizeServer(PowerShell ps, (string username, string password, string domain) creds, string dnsName, string ipAddress)
    {
        ps.AddCommand("Import-Module")
          .AddParameter("Name", "ActiveDirectory")
          .AddParameter("ErrorAction", "Stop");
        ps.Invoke();
        ps.Commands.Clear();

        var credential = CreateCredential(creds.username, creds.password, creds.domain);
        var dc = DiscoverDomainController(ps, credential);
        if (dc == null)
            return new DhcpOperationResult { Success = false, Message = "Could not discover a domain controller." };

        // psl-10. The import's error stream is redirected (2>$null): the script imports and changes in one Invoke-Command,
        // whose records come back in the one stream the HadErrors check below reads, so a record the import wrote past Stop
        // failed a change that had been made. No clear can come between them from here, and $Error.Clear() in the script
        // would not help - the record has already been written to the stream (measured). Under Stop the import's own failure
        // is still thrown, stopping the script before the change: the import's error is the one it throws.
        var script = ScriptBlock.Create("param($DnsName, $IPAddress) Import-Module DhcpServer -ErrorAction Stop 2>$null; Add-DhcpServerInDC -DnsName $DnsName -IPAddress $IPAddress -ErrorAction Stop");
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

            return DeauthorizeServer(ps, creds.Value, dnsName, ipAddress);
        });
    }

    /// <remarks>
    /// Internal as a TEST SEAM (psl-3); see <see cref="AuthorizeServer"/>.
    /// </remarks>
    internal DhcpOperationResult DeauthorizeServer(PowerShell ps, (string username, string password, string domain) creds, string dnsName, string ipAddress)
    {
        ps.AddCommand("Import-Module")
          .AddParameter("Name", "ActiveDirectory")
          .AddParameter("ErrorAction", "Stop");
        ps.Invoke();
        ps.Commands.Clear();

        var credential = CreateCredential(creds.username, creds.password, creds.domain);
        var dc = DiscoverDomainController(ps, credential);
        if (dc == null)
            return new DhcpOperationResult { Success = false, Message = "Could not discover a domain controller." };

        // psl-10. The import's error stream redirected, as in AuthorizeServer and for the same reason.
        var script = ScriptBlock.Create("param($DnsName, $IPAddress) Import-Module DhcpServer -ErrorAction Stop 2>$null; Remove-DhcpServerInDC -DnsName $DnsName -IPAddress $IPAddress -ErrorAction Stop");
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
        finally
        {
            // psl-3: Commands.Clear() leaves error records, and the change's HadErrors check is
            // true while ANY record remains, so a discovery record (or the import's, which runs
            // just before) failed a successful change and joined a failed change's report.
            ps.Streams.Error.Clear();
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
        finally
        {
            // psl-3: as above - the change after discovery must see only its own records.
            ps.Streams.Error.Clear();
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
