using System.Management.Automation;
using System.Management.Automation.Runspaces;

namespace ExchangeAdminWeb.Services;

public class DhcpAuthorizationService
{
    private readonly ILogger<DhcpAuthorizationService> _logger;
    private readonly ModuleConfigService _moduleConfig;

    public DhcpAuthorizationService(ILogger<DhcpAuthorizationService> logger, ModuleConfigService moduleConfig)
    {
        _logger = logger;
        _moduleConfig = moduleConfig;
    }

    private string CredentialTarget
    {
        get
        {
            var val = _moduleConfig.GetValue("DhcpAuthorization", "CredentialTarget");
            return val ?? "DHCP_Admin";
        }
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

            if (ps.HadErrors)
            {
                var errors = string.Join("; ", ps.Streams.Error.Select(e => e.ToString()));
                _logger.LogError("Get-DhcpServerInDC failed: {Errors}", errors);
                throw new InvalidOperationException($"Failed to retrieve authorized DHCP servers: {errors}");
            }

            var servers = new List<DhcpServerEntry>();
            foreach (var item in results)
            {
                var dnsName = item.Properties["DnsName"]?.Value?.ToString() ?? "";
                var ipAddress = item.Properties["IPAddress"]?.Value?.ToString() ?? "";
                servers.Add(new DhcpServerEntry { DnsName = dnsName, IpAddress = ipAddress });
            }

            return servers;
        });
    }

    public async Task<DhcpOperationResult> AuthorizeServerAsync(string dnsName, string ipAddress)
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

            var credential = GetCredential();
            if (credential != null)
            {
                ps.AddScript($"$cred = $args[0]; Invoke-Command -ScriptBlock {{ Add-DhcpServerInDC -DnsName '{EscapeSingleQuote(dnsName)}' -IPAddress '{EscapeSingleQuote(ipAddress)}' -ErrorAction Stop }} -Credential $cred");
                ps.AddArgument(credential);
            }
            else
            {
                ps.AddCommand("Add-DhcpServerInDC")
                  .AddParameter("DnsName", dnsName)
                  .AddParameter("IPAddress", ipAddress)
                  .AddParameter("ErrorAction", "Stop");
            }

            try
            {
                ps.Invoke();

                if (ps.HadErrors)
                {
                    var errors = string.Join("; ", ps.Streams.Error.Select(e => e.ToString()));
                    _logger.LogError("Add-DhcpServerInDC failed for {DnsName}/{Ip}: {Errors}", dnsName, ipAddress, errors);
                    return new DhcpOperationResult { Success = false, Message = $"Failed to authorize: {errors}" };
                }

                _logger.LogInformation("DHCP server authorized: {DnsName} ({Ip})", dnsName, ipAddress);
                return new DhcpOperationResult { Success = true, Message = $"Successfully authorized DHCP server {dnsName} ({ipAddress})." };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Add-DhcpServerInDC exception for {DnsName}/{Ip}", dnsName, ipAddress);
                return new DhcpOperationResult { Success = false, Message = $"Failed to authorize: {ex.Message}" };
            }
        });
    }

    public async Task<DhcpOperationResult> DeauthorizeServerAsync(string dnsName, string ipAddress)
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

            var credential = GetCredential();
            if (credential != null)
            {
                ps.AddScript($"$cred = $args[0]; Invoke-Command -ScriptBlock {{ Remove-DhcpServerInDC -DnsName '{EscapeSingleQuote(dnsName)}' -IPAddress '{EscapeSingleQuote(ipAddress)}' -ErrorAction Stop }} -Credential $cred");
                ps.AddArgument(credential);
            }
            else
            {
                ps.AddCommand("Remove-DhcpServerInDC")
                  .AddParameter("DnsName", dnsName)
                  .AddParameter("IPAddress", ipAddress)
                  .AddParameter("ErrorAction", "Stop");
            }

            try
            {
                ps.Invoke();

                if (ps.HadErrors)
                {
                    var errors = string.Join("; ", ps.Streams.Error.Select(e => e.ToString()));
                    _logger.LogError("Remove-DhcpServerInDC failed for {DnsName}/{Ip}: {Errors}", dnsName, ipAddress, errors);
                    return new DhcpOperationResult { Success = false, Message = $"Failed to deauthorize: {errors}" };
                }

                _logger.LogInformation("DHCP server deauthorized: {DnsName} ({Ip})", dnsName, ipAddress);
                return new DhcpOperationResult { Success = true, Message = $"Successfully deauthorized DHCP server {dnsName} ({ipAddress})." };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Remove-DhcpServerInDC exception for {DnsName}/{Ip}", dnsName, ipAddress);
                return new DhcpOperationResult { Success = false, Message = $"Failed to deauthorize: {ex.Message}" };
            }
        });
    }

    private PSCredential? GetCredential()
    {
        var (username, password) = CredentialManagerService.ReadCredential(CredentialTarget);
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            _logger.LogWarning("No credential found in PasswordVault for target '{Target}'. Running DHCP commands as app pool identity.", CredentialTarget);
            return null;
        }

        var securePassword = new System.Security.SecureString();
        foreach (var c in password) securePassword.AppendChar(c);
        return new PSCredential(username, securePassword);
    }

    private static string EscapeSingleQuote(string value) => value.Replace("'", "''");
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
