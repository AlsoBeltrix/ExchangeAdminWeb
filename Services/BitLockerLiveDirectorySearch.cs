using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Security;
using System.Text.RegularExpressions;

namespace ExchangeAdminWeb.Services;

public interface IBitLockerLiveDirectorySearch
{
    Task<BitLockerLiveDirectorySearchResult> SearchByComputerNameAsync(string computerName, int limit);

    Task<BitLockerLiveDirectorySearchResult> SearchByRecoveryIdentifierAsync(
        BitLockerRecoveryIdentifier identifier,
        int limit);
}

public sealed class PowerShellBitLockerLiveDirectorySearch : IBitLockerLiveDirectorySearch
{
    private const string ModuleId = "BitLockerRecovery";
    private const string SearchBaseKey = "ActiveDirectorySearchBase";
    private const string ServerKey = "ActiveDirectoryServer";
    private const string CredentialPurpose = "BitLocker recovery AD read";

    private static readonly Regex RecoveryCnPattern = new(
        @"\{([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\}",
        RegexOptions.Compiled);

    private static readonly Regex RecoveryCreatedPattern = new(
        @"^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:[+-]\d{2}:\d{2}|Z)?)",
        RegexOptions.Compiled);

    private readonly ModuleConfigService _moduleConfig;
    private readonly ModuleCredentialService _moduleCredentials;
    private readonly ILogger<PowerShellBitLockerLiveDirectorySearch> _logger;

    public PowerShellBitLockerLiveDirectorySearch(
        ModuleConfigService moduleConfig,
        ModuleCredentialService moduleCredentials,
        ILogger<PowerShellBitLockerLiveDirectorySearch> logger)
    {
        _moduleConfig = moduleConfig;
        _moduleCredentials = moduleCredentials;
        _logger = logger;
    }

    public async Task<BitLockerLiveDirectorySearchResult> SearchByComputerNameAsync(
        string computerName,
        int limit)
    {
        if (string.IsNullOrWhiteSpace(computerName))
        {
            return BitLockerLiveDirectorySearchResult.Ok([], truncated: false);
        }

        return await RunDirectoryQueryAsync((credential, runspace) =>
        {
            var queryLimit = limit + 1;
            var escaped = EscapeLdapFilterValue(computerName.Trim());
            using var ps = CreatePowerShell(runspace);
            ps.AddCommand("Get-ADComputer")
                .AddParameter("LDAPFilter", $"(name=*{escaped}*)")
                .AddParameter("ResultSetSize", queryLimit)
                .AddParameter("Credential", credential);
            AddAdScope(ps);

            var computers = Invoke(ps);
            var rows = new List<BitLockerRecoveryKey>();
            var recoveryObjectsRead = 0;
            var unreadableRecoveryObjects = 0;
            var truncatedByRecoveryQuery = false;

            foreach (var computer in computers)
            {
                var computerNameValue = GetStringProperty(computer, "Name");
                var computerDn = GetStringProperty(computer, "DistinguishedName");
                if (string.IsNullOrWhiteSpace(computerNameValue) ||
                    string.IsNullOrWhiteSpace(computerDn))
                {
                    continue;
                }

                using var recoveryPs = CreatePowerShell(runspace);
                recoveryPs.AddCommand("Get-ADObject")
                    .AddParameter("SearchBase", computerDn)
                    .AddParameter("LDAPFilter", "(objectClass=msFVE-RecoveryInformation)")
                    .AddParameter("Properties", new[] { "whenCreated", "msFVE-RecoveryPassword" })
                    .AddParameter("ResultSetSize", queryLimit)
                    .AddParameter("Credential", credential);
                AddAdServer(recoveryPs);

                var recoveries = Invoke(recoveryPs);
                truncatedByRecoveryQuery |= recoveries.Count > limit;

                foreach (var recovery in recoveries)
                {
                    recoveryObjectsRead++;
                    var row = ToRecoveryKey(recovery, computerNameValue);
                    if (row is null)
                    {
                        unreadableRecoveryObjects++;
                        continue;
                    }

                    if (rows.Count >= limit)
                    {
                        return BuildResult(
                            rows,
                            truncated: true,
                            recoveryObjectsRead,
                            unreadableRecoveryObjects);
                    }

                    rows.Add(row);
                }
            }

            return BuildResult(
                rows,
                computers.Count > limit || truncatedByRecoveryQuery,
                recoveryObjectsRead,
                unreadableRecoveryObjects);
        });
    }

    public async Task<BitLockerLiveDirectorySearchResult> SearchByRecoveryIdentifierAsync(
        BitLockerRecoveryIdentifier identifier,
        int limit)
    {
        if (string.IsNullOrWhiteSpace(identifier.Value))
        {
            return BitLockerLiveDirectorySearchResult.Ok([], truncated: false);
        }

        return await RunDirectoryQueryAsync((credential, runspace) =>
        {
            var queryLimit = limit + 1;
            using var ps = CreatePowerShell(runspace);
            var escaped = EscapeLdapFilterValue(identifier.Value);
            var filter = identifier.Kind == BitLockerRecoveryIdentifierKind.RecoveryPassword
                ? $"(&(objectClass=msFVE-RecoveryInformation)(msFVE-RecoveryPassword={escaped}))"
                : $"(&(objectClass=msFVE-RecoveryInformation)(cn=*{{{escaped}*))";

            ps.AddCommand("Get-ADObject")
                .AddParameter("LDAPFilter", filter)
                .AddParameter("Properties", new[] { "whenCreated", "msFVE-RecoveryPassword" })
                .AddParameter("ResultSetSize", queryLimit)
                .AddParameter("Credential", credential);
            AddAdScope(ps);

            var found = Invoke(ps);
            var rows = new List<BitLockerRecoveryKey>();
            var recoveryObjectsRead = 0;
            var unreadableRecoveryObjects = 0;

            foreach (var recovery in found)
            {
                recoveryObjectsRead++;
                var computer = GetParentComputerName(GetStringProperty(recovery, "DistinguishedName"));
                if (string.IsNullOrWhiteSpace(computer))
                {
                    continue;
                }

                var row = ToRecoveryKey(recovery, computer);
                if (row is null)
                {
                    unreadableRecoveryObjects++;
                    continue;
                }

                if (rows.Count >= limit)
                {
                    return BuildResult(
                        rows,
                        truncated: true,
                        recoveryObjectsRead,
                        unreadableRecoveryObjects);
                }

                rows.Add(row);
            }

            return BuildResult(
                rows,
                found.Count > limit,
                recoveryObjectsRead,
                unreadableRecoveryObjects);
        });
    }

    private async Task<BitLockerLiveDirectorySearchResult> RunDirectoryQueryAsync(
        Func<PSCredential, Runspace, BitLockerLiveDirectorySearchResult> query)
    {
        if (_moduleConfig.IsModuleCorrupt(ModuleId))
        {
            return BitLockerLiveDirectorySearchResult.Fail(
                "Module configuration is unreadable. Ask an administrator to check the BitLocker Recovery module config.");
        }

        var credential = await GetCredentialAsync();
        if (credential is null)
        {
            return BitLockerLiveDirectorySearchResult.Fail(
                "BitLocker Recovery AD credentials are not configured or unavailable.");
        }

        try
        {
            return await Task.Run(() =>
            {
                using var runspace = CreateRunspace();
                return query(credential, runspace);
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BitLocker live Active Directory recovery search failed.");
            return BitLockerLiveDirectorySearchResult.Fail(
                "Active Directory recovery keys could not be searched. An empty result would not mean no live key exists.");
        }
    }

    private async Task<PSCredential?> GetCredentialAsync()
    {
        var credentials = await _moduleCredentials.GetCredentialsAsync(ModuleId, CredentialPurpose);
        if (credentials is null)
        {
            return null;
        }

        var (username, password, domain) = credentials.Value;
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return null;
        }

        var credentialUser = BuildCredentialUserName(username, domain);
        var securePassword = new SecureString();
        foreach (var c in password)
        {
            securePassword.AppendChar(c);
        }

        securePassword.MakeReadOnly();
        return new PSCredential(credentialUser, securePassword);
    }

    private static Runspace CreateRunspace()
    {
        var sessionState = InitialSessionState.CreateDefault();
        sessionState.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
        sessionState.ImportPSModule(["ActiveDirectory"]);

        var runspace = RunspaceFactory.CreateRunspace(sessionState);
        try
        {
            runspace.Open();
        }
        catch
        {
            runspace.Dispose();
            throw;
        }

        return runspace;
    }

    private static PowerShell CreatePowerShell(Runspace runspace)
    {
        var ps = PowerShell.Create();
        ps.Runspace = runspace;
        return ps;
    }

    private void AddAdScope(PowerShell ps)
    {
        AddAdServer(ps);

        var searchBase = _moduleConfig.GetValue(ModuleId, SearchBaseKey);
        if (!string.IsNullOrWhiteSpace(searchBase))
        {
            ps.AddParameter("SearchBase", searchBase);
        }
    }

    private void AddAdServer(PowerShell ps)
    {
        var server = _moduleConfig.GetValue(ModuleId, ServerKey);
        if (!string.IsNullOrWhiteSpace(server))
        {
            ps.AddParameter("Server", server);
        }
    }

    private static Collection<PSObject> Invoke(PowerShell ps)
    {
        var output = ps.Invoke();
        if (ps.HadErrors)
        {
            throw new InvalidOperationException("Active Directory PowerShell command failed.");
        }

        return output;
    }

    private static BitLockerLiveDirectorySearchResult BuildResult(
        IReadOnlyList<BitLockerRecoveryKey> rows,
        bool truncated,
        int recoveryObjectsRead,
        int unreadableRecoveryObjects)
    {
        if (recoveryObjectsRead > 0 && rows.Count == 0)
        {
            return BitLockerLiveDirectorySearchResult.Fail(
                "Active Directory returned BitLocker recovery objects, but the configured account could not read recovery passwords.");
        }

        var warnings = unreadableRecoveryObjects > 0
            ? new[]
            {
                "Active Directory returned some BitLocker recovery objects without readable recovery passwords.",
            }
            : [];

        return BitLockerLiveDirectorySearchResult.Ok(rows, truncated, warnings);
    }

    private static BitLockerRecoveryKey? ToRecoveryKey(PSObject recovery, string computerName)
    {
        var password = GetStringProperty(recovery, "msFVE-RecoveryPassword");
        if (string.IsNullOrWhiteSpace(password))
        {
            return null;
        }

        var name = GetStringProperty(recovery, "Name") ?? string.Empty;
        var (keyId, createdUtc) = ParseRecoveryCn(name);
        createdUtc ??= ParseDateTimeProperty(recovery, "whenCreated");

        return new BitLockerRecoveryKey
        {
            RowId = 0,
            ComputerName = computerName,
            RecoveryPassword = password,
            KeyId = keyId,
            VolumeId = null,
            CreatedUtc = createdUtc,
            FirstSeenSource = "ActiveDirectory",
            LastSeenInAdUtc = DateTime.UtcNow,
            ResultSource = BitLockerRecoveryKeySource.ActiveDirectory,
        };
    }

    private static (string? KeyId, DateTime? CreatedUtc) ParseRecoveryCn(string cn)
    {
        string? keyId = null;
        DateTime? createdUtc = null;

        var keyMatch = RecoveryCnPattern.Match(cn);
        if (keyMatch.Success)
        {
            keyId = keyMatch.Groups[1].Value.ToLowerInvariant();
        }

        var createdMatch = RecoveryCreatedPattern.Match(cn);
        if (createdMatch.Success &&
            DateTime.TryParse(
                createdMatch.Groups[1].Value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal |
                System.Globalization.DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            createdUtc = parsed;
        }

        return (keyId, createdUtc);
    }

    private static DateTime? ParseDateTimeProperty(PSObject value, string propertyName)
    {
        var raw = value.Properties[propertyName]?.Value;
        return raw switch
        {
            DateTime date => date.ToUniversalTime(),
            string text when DateTime.TryParse(text, out var parsed) => parsed.ToUniversalTime(),
            _ => null,
        };
    }

    private static string? GetStringProperty(PSObject value, string propertyName) =>
        value.Properties[propertyName]?.Value?.ToString();

    private static string? GetParentComputerName(string? distinguishedName)
    {
        if (string.IsNullOrWhiteSpace(distinguishedName))
        {
            return null;
        }

        var parentDn = Regex.Replace(distinguishedName, @"^CN=(?:[^,\\]|\\.)*,", string.Empty);
        var match = Regex.Match(parentDn, @"^CN=((?:[^,\\]|\\.)*),");
        return match.Success
            ? Regex.Replace(match.Groups[1].Value, @"\\(.)", "$1")
            : null;
    }

    private static string BuildCredentialUserName(string username, string domain)
    {
        if (string.IsNullOrWhiteSpace(domain) ||
            username.Contains("\\", StringComparison.Ordinal) ||
            username.Contains("@", StringComparison.Ordinal))
        {
            return username;
        }

        return $@"{domain}\{username}";
    }

    private static string EscapeLdapFilterValue(string value)
    {
        return value
            .Replace(@"\", @"\5c", StringComparison.Ordinal)
            .Replace("*", @"\2a", StringComparison.Ordinal)
            .Replace("(", @"\28", StringComparison.Ordinal)
            .Replace(")", @"\29", StringComparison.Ordinal)
            .Replace("\0", @"\00", StringComparison.Ordinal);
    }
}

public sealed record BitLockerLiveDirectorySearchResult
{
    public required bool Success { get; init; }

    public string? Error { get; init; }

    public IReadOnlyList<BitLockerRecoveryKey> Keys { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public bool IsPartial => Warnings.Count > 0;

    public bool Truncated { get; init; }

    public static BitLockerLiveDirectorySearchResult Ok(
        IReadOnlyList<BitLockerRecoveryKey> keys,
        bool truncated,
        IReadOnlyList<string>? warnings = null) =>
        new()
        {
            Success = true,
            Keys = keys,
            Truncated = truncated,
            Warnings = warnings ?? [],
        };

    public static BitLockerLiveDirectorySearchResult Fail(string error) =>
        new() { Success = false, Error = error };
}
