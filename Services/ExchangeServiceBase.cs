using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Services;

public abstract class ExchangeServiceBase
{
    protected readonly ExoConnectionPool _exoPool;
    protected readonly DelineaService _delineaService;
    protected readonly ILogger _logger;
    protected readonly string _onPremServerUri;
    protected static readonly SemaphoreSlim _onPremThrottle = new(2, 2);

    protected ExchangeServiceBase(ExoConnectionPool exoPool, DelineaService delineaService, ILogger logger, string onPremServerUri)
    {
        _exoPool = exoPool;
        _delineaService = delineaService;
        _logger = logger;
        _onPremServerUri = onPremServerUri;
    }

    // -------------------------------------------------------------------------
    // Pool / Run helpers
    // -------------------------------------------------------------------------

    protected async Task<PermissionResult> RunAsync(Action<PowerShell> operation, Func<(string message, string? detail)>? successFormatter = null)
    {
        var pooled = await _exoPool.BorrowAsync();
        bool discard = false;
        try
        {
            var (result, hadConnectionError) = await Task.Run(() =>
            {
                ConnectionErrorFlag = false;
                var ps = pooled.PowerShell;
                try
                {
                    operation(ps);

                    if (successFormatter is not null)
                    {
                        var (message, detail) = successFormatter();
                        return (new PermissionResult { Success = true, Message = message, Detail = detail }, ConnectionErrorFlag);
                    }
                    return (PermissionResult.Ok(), ConnectionErrorFlag);
                }
                catch (Exception ex)
                {
                    var psErrors = ps.Streams.Error
                        .Select(e => e.Exception?.Message ?? e.ToString())
                        .Where(m => !string.IsNullOrWhiteSpace(m))
                        .ToList();

                    var primary = psErrors.FirstOrDefault() ?? ex.Message;
                    var detail = psErrors.Count > 1 ? string.Join(" | ", psErrors.Skip(1)) : null;

                    _logger.LogError(ex, "Exchange operation failed: {Message}", primary);
                    return (PermissionResult.Fail(primary, detail), IsConnectionError(ex) || ConnectionErrorFlag);
                }
            });

            discard = hadConnectionError;
            return result;
        }
        finally
        {
            if (discard)
                _exoPool.Discard(pooled);
            else
                _exoPool.Return(pooled);
        }
    }

    protected async Task<T> RunPooledQueryAsync<T>(Func<PowerShell, T> query)
    {
        var pooled = await _exoPool.BorrowAsync();
        bool discard = false;
        try
        {
            var (result, hadConnectionError) = await Task.Run(() =>
            {
                ConnectionErrorFlag = false;
                var r = query(pooled.PowerShell);
                return (r, ConnectionErrorFlag);
            });

            discard = hadConnectionError;
            return result;
        }
        catch (Exception ex) when (IsConnectionError(ex))
        {
            discard = true;
            throw;
        }
        finally
        {
            if (discard)
                _exoPool.Discard(pooled);
            else
                _exoPool.Return(pooled);
        }
    }

    protected static async Task<T> ThrottledAsync<T>(Func<Task<T>> operation, SemaphoreSlim? throttle = null)
    {
        if (throttle != null)
        {
            if (!await throttle.WaitAsync(TimeSpan.FromMinutes(2)))
                throw new InvalidOperationException("Exchange service is busy. Please try again shortly.");
            try { return await operation(); }
            finally { throttle.Release(); }
        }
        return await operation();
    }

    // -------------------------------------------------------------------------
    // Invoke helpers
    // -------------------------------------------------------------------------

    [ThreadStatic] protected static bool ConnectionErrorFlag;

    protected static Collection<PSObject> Invoke(PowerShell ps)
    {
        Collection<PSObject> result;
        try
        {
            result = ps.Invoke();
        }
        catch (RuntimeException ex)
        {
            if (IsConnectionError(ex))
                ConnectionErrorFlag = true;
            throw new InvalidOperationException(ex.Message, ex);
        }

        if (ps.HadErrors)
        {
            var err = ps.Streams.Error.FirstOrDefault();
            var msg = err?.Exception?.Message ?? err?.ToString() ?? "An unknown error occurred.";
            if (IsConnectionError(err?.Exception))
                ConnectionErrorFlag = true;
            throw new InvalidOperationException(msg);
        }

        ps.Commands.Clear();
        return result;
    }

    protected static Collection<PSObject> InvokeOptional(PowerShell ps)
    {
        var result = ps.Invoke();
        if (ps.Streams.Error.Any(e => IsConnectionError(e.Exception)))
            ConnectionErrorFlag = true;
        ps.Streams.Error.Clear();
        ps.Commands.Clear();
        return result;
    }

    // -------------------------------------------------------------------------
    // Validation helpers
    // -------------------------------------------------------------------------

    protected static string ValidateMailbox(PowerShell ps, string mailbox)
    {
        ps.AddCommand("Get-Mailbox")
          .AddParameter("Identity", mailbox)
          .AddParameter("ErrorAction", "Stop");
        var result = Invoke(ps);
        var mbx = result.FirstOrDefault();

        return mbx?.Properties["PrimarySmtpAddress"]?.Value?.ToString() ?? mailbox;
    }

    protected static void ValidateRecipient(PowerShell ps, string identity)
    {
        ps.AddCommand("Get-Recipient")
          .AddParameter("Identity", identity)
          .AddParameter("ErrorAction", "Stop");
        var result = Invoke(ps);
        if (result.Count == 0)
            throw new InvalidOperationException($"Recipient '{identity}' not found.");
    }

    // -------------------------------------------------------------------------
    // On-Prem connection
    // -------------------------------------------------------------------------

    protected void ConnectOnPrem(PowerShell ps, string username, string password, string domain)
    {
        var fullUsername = username.Contains('\\') || username.Contains('@')
            ? username
            : $"{domain}\\{username}";

        var securePassword = new System.Security.SecureString();
        foreach (var c in password)
            securePassword.AppendChar(c);
        securePassword.MakeReadOnly();

        var credential = new PSCredential(fullUsername, securePassword);

        ps.AddCommand("New-PSSession")
          .AddParameter("ConfigurationName", "Microsoft.Exchange")
          .AddParameter("ConnectionUri", _onPremServerUri)
          .AddParameter("Authentication", "Kerberos")
          .AddParameter("Credential", credential)
          .AddParameter("ErrorAction", "Stop");
        var sessions = Invoke(ps);
        var session = sessions.FirstOrDefault() ?? throw new InvalidOperationException("Failed to create on-prem Exchange session");

        ps.Runspace.SessionStateProxy.SetVariable("onpremSession", session.BaseObject);

        _logger.LogInformation("Connected to on-prem Exchange at {Uri}", _onPremServerUri);
    }

    // -------------------------------------------------------------------------
    // On-Prem queries (shared by multiple services)
    // -------------------------------------------------------------------------

    protected async Task<(double mailboxSizeGB, double archiveSizeGB)?> GetOnPremMailboxSizeAsync(string emailAddress)
    {
        if (string.IsNullOrEmpty(_onPremServerUri))
        {
            _logger.LogError("OnPremExchange:ServerUri is not configured — cannot check mailbox size");
            return null;
        }

        var creds = await _delineaService.GetExchangeCredentialsAsync();
        if (creds is null)
        {
            _logger.LogError("Cannot connect to on-prem Exchange: failed to retrieve credentials from Delinea");
            return null;
        }

        return await ThrottledAsync(() => Task.Run(() =>
        {
            var iss = InitialSessionState.CreateDefault();
            iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
            using var runspace = RunspaceFactory.CreateRunspace(iss);
            runspace.Open();
            using var ps = PowerShell.Create();
            ps.Runspace = runspace;

            try
            {
                ConnectOnPrem(ps, creds.Value.username, creds.Value.password, creds.Value.domain);

                var mbxScript = ScriptBlock.Create("param($Identity) Get-MailboxStatistics -Identity $Identity -ErrorAction Stop | Select-Object TotalItemSize");
                ps.AddCommand("Invoke-Command")
                  .AddParameter("Session", ps.Runspace.SessionStateProxy.GetVariable("onpremSession"))
                  .AddParameter("ScriptBlock", mbxScript)
                  .AddParameter("ArgumentList", new object[] { emailAddress });
                var mbxStats = Invoke(ps);
                var totalItemSize = mbxStats.FirstOrDefault()?.Properties["TotalItemSize"]?.Value?.ToString();
                var mailboxGB = ParseExchangeSize(totalItemSize);

                _logger.LogInformation("On-prem mailbox size for {Email}: {Size} GB", emailAddress, mailboxGB);

                double archiveGB = 0;
                try
                {
                    var archiveScript = ScriptBlock.Create("param($Identity) Get-MailboxStatistics -Identity $Identity -Archive -ErrorAction Stop | Select-Object TotalItemSize");
                    ps.AddCommand("Invoke-Command")
                      .AddParameter("Session", ps.Runspace.SessionStateProxy.GetVariable("onpremSession"))
                      .AddParameter("ScriptBlock", archiveScript)
                      .AddParameter("ArgumentList", new object[] { emailAddress });
                    var archiveStats = Invoke(ps);
                    var archiveSize = archiveStats.FirstOrDefault()?.Properties["TotalItemSize"]?.Value?.ToString();
                    archiveGB = ParseExchangeSize(archiveSize);
                    _logger.LogInformation("On-prem archive size for {Email}: {Size} GB", emailAddress, archiveGB);
                }
                catch
                {
                    _logger.LogInformation("No archive mailbox found for {Email}", emailAddress);
                }

                return ((double mailboxSizeGB, double archiveSizeGB)?)(mailboxGB, archiveGB);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get on-prem mailbox size for {Email}", emailAddress);
                return null;
            }
            finally
            {
                try
                {
                    ps.Commands.Clear();
                    var session = ps.Runspace.SessionStateProxy.GetVariable("onpremSession");
                    if (session != null)
                    {
                        ps.AddCommand("Remove-PSSession").AddParameter("Session", session);
                        ps.Invoke();
                    }
                }
                catch { }
            }
        }), _onPremThrottle);
    }

    protected async Task<string[]?> ResolveDagDatabasesAsync(string onPremTargetDAG)
    {
        if (string.IsNullOrWhiteSpace(onPremTargetDAG))
        {
            _logger.LogError("Migration:OnPremTargetDAG is not configured — cannot resolve target databases");
            return null;
        }

        if (string.IsNullOrEmpty(_onPremServerUri))
        {
            _logger.LogError("OnPremExchange:ServerUri is not configured — cannot resolve DAG databases");
            return null;
        }

        var creds = await _delineaService.GetExchangeCredentialsAsync();
        if (creds is null)
        {
            _logger.LogError("Cannot connect to on-prem Exchange: failed to retrieve credentials from Delinea");
            return null;
        }

        return await ThrottledAsync(() => Task.Run(() =>
        {
            var iss = InitialSessionState.CreateDefault();
            iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
            using var runspace = RunspaceFactory.CreateRunspace(iss);
            runspace.Open();
            using var ps = PowerShell.Create();
            ps.Runspace = runspace;

            try
            {
                ConnectOnPrem(ps, creds.Value.username, creds.Value.password, creds.Value.domain);

                var script = ScriptBlock.Create(
                    "param($DagName) Get-MailboxDatabase | Where-Object { $_.MasterServerOrAvailabilityGroup -eq $DagName -and $_.Recovery -eq $false } | Select-Object Name");
                ps.AddCommand("Invoke-Command")
                  .AddParameter("Session", ps.Runspace.SessionStateProxy.GetVariable("onpremSession"))
                  .AddParameter("ScriptBlock", script)
                  .AddParameter("ArgumentList", new object[] { onPremTargetDAG });
                var results = Invoke(ps);

                var databases = results
                    .Select(r => r.Properties["Name"]?.Value?.ToString())
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Cast<string>()
                    .ToArray();

                if (databases.Length == 0)
                {
                    _logger.LogWarning("No databases found for DAG '{DagName}'", onPremTargetDAG);
                    return (string[]?)null;
                }

                _logger.LogInformation("Resolved {Count} databases from DAG '{DagName}': {Databases}",
                    databases.Length, onPremTargetDAG, string.Join(", ", databases));

                return databases;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to resolve databases for DAG '{DagName}'", onPremTargetDAG);
                return null;
            }
            finally
            {
                try
                {
                    ps.Commands.Clear();
                    var session = ps.Runspace.SessionStateProxy.GetVariable("onpremSession");
                    if (session != null)
                    {
                        ps.AddCommand("Remove-PSSession").AddParameter("Session", session);
                        ps.Invoke();
                    }
                }
                catch { }
            }
        }), _onPremThrottle);
    }

    protected async Task<string> GetMailboxLocationAsync(string identity)
    {
        return await RunPooledQueryAsync(ps =>
        {
            ps.AddCommand("Get-Recipient")
              .AddParameter("Identity", identity)
              .AddParameter("ErrorAction", "Stop");
            var results = Invoke(ps);
            var recip = results.FirstOrDefault()
                ?? throw new InvalidOperationException($"Recipient '{identity}' not found.");

            var type = recip.Properties["RecipientTypeDetails"]?.Value?.ToString();
            return MailboxLocationClassifier.ForOperationRouting(type);
        });
    }

    // -------------------------------------------------------------------------
    // Calendar helper
    // -------------------------------------------------------------------------

    protected string GetCalendarFolderName(PowerShell ps, string mailbox)
    {
        ps.AddCommand("Get-MailboxFolderStatistics")
          .AddParameter("Identity", mailbox)
          .AddParameter("FolderScope", "Calendar")
          .AddParameter("ErrorAction", "Stop");

        var result = Invoke(ps);
        var folder = result.FirstOrDefault();

        if (folder is null)
            throw new InvalidOperationException($"No calendar folder found for {mailbox}");

        var rawFolderPath = folder.Properties["FolderPath"]?.Value?.ToString();
        _logger.LogInformation("Calendar folder lookup for {Mailbox}: raw FolderPath = '{RawPath}'", mailbox, rawFolderPath ?? "<null>");

        var folderPath = rawFolderPath ?? @"\Calendar";
        // Exchange Online may return forward slashes, but cmdlets require backslashes
        folderPath = folderPath.Replace("/", @"\");

        var fullPath = $"{mailbox}:{folderPath}";
        _logger.LogInformation("Constructed calendar identity: '{FullPath}'", fullPath);
        return fullPath;
    }

    // -------------------------------------------------------------------------
    // AD Group check
    // -------------------------------------------------------------------------

    protected void CheckAdGroupMembership(MigrationEligibilityResult result, string[] excludedADGroups)
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

        ps.AddCommand("Get-ADUser")
          .AddParameter("Filter", $"UserPrincipalName -eq '{result.EmailAddress.Replace("'", "''")}'")

          .AddParameter("Properties", "memberOf")
          .AddParameter("ErrorAction", "Stop");
        var adUser = ps.Invoke();
        ps.Commands.Clear();

        var memberOf = adUser.FirstOrDefault()?.Properties["memberOf"]?.Value;
        if (memberOf == null) return;

        var groups = new List<string>();
        if (memberOf is System.Collections.IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                if (item != null)
                    groups.Add(item.ToString() ?? string.Empty);
            }
        }

        foreach (var excludedGroup in excludedADGroups)
        {
            if (groups.Any(g => g.Contains(excludedGroup, StringComparison.OrdinalIgnoreCase)))
            {
                result.Status = MigrationStatus.Ineligible;
                result.IneligibilityReasons.Add($"Member of excluded group: {excludedGroup}");
                _logger.LogWarning("User {Email} is ineligible for cloud migration - member of {Group}", result.EmailAddress, excludedGroup);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Parse helpers
    // -------------------------------------------------------------------------

    public static double? ParseSizeToGB(string? sizeString)
    {
        if (string.IsNullOrWhiteSpace(sizeString)) return null;

        // Exchange returns sizes like "1.234 GB (1,234,567,890 bytes)" or "500.1 MB (524,396,544 bytes)"
        // Try parenthesized bytes first (most reliable)
        var parenMatch = System.Text.RegularExpressions.Regex.Match(sizeString, @"\(([\d,]+)\s+bytes\)");
        if (parenMatch.Success && long.TryParse(parenMatch.Groups[1].Value.Replace(",", ""), out var parenBytes))
            return Math.Round(parenBytes / 1073741824.0, 2);

        // Bare bytes format: "1,234,567 bytes"
        var bareMatch = System.Text.RegularExpressions.Regex.Match(sizeString, @"^([\d,]+)\s*bytes$");
        if (bareMatch.Success && long.TryParse(bareMatch.Groups[1].Value.Replace(",", ""), out var bareBytes))
            return Math.Round(bareBytes / 1073741824.0, 2);

        // Human-readable GB: "1.234 GB"
        var gbMatch = System.Text.RegularExpressions.Regex.Match(sizeString, @"([\d.]+)\s*GB", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (gbMatch.Success && double.TryParse(gbMatch.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var gb))
            return Math.Round(gb, 2);

        // Human-readable MB: "500.1 MB"
        var mbMatch = System.Text.RegularExpressions.Regex.Match(sizeString, @"([\d.]+)\s*MB", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (mbMatch.Success && double.TryParse(mbMatch.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mb))
            return Math.Round(mb / 1024.0, 2);

        // Human-readable KB: "512 KB"
        var kbMatch = System.Text.RegularExpressions.Regex.Match(sizeString, @"([\d.]+)\s*KB", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (kbMatch.Success && double.TryParse(kbMatch.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var kb))
            return Math.Round(kb / 1048576.0, 4);

        return null;
    }

    protected static long? ParseLong(object? value)
    {
        if (value is long l) return l;
        if (value is int i) return i;
        if (value != null && long.TryParse(value.ToString(), out var parsed)) return parsed;
        return null;
    }

    protected static double ParseExchangeSize(string? sizeString)
    {
        return ParseSizeToGB(sizeString) ?? 0;
    }

    protected static bool ParseBool(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "yes" or "true" or "1" or "x" => true,
            _ => false
        };

    // -------------------------------------------------------------------------
    // Connection error detection
    // -------------------------------------------------------------------------

    protected static bool IsConnectionError(Exception? ex) =>
        ex != null && (
            ex.Message.Contains("connection", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("session", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("runspace", StringComparison.OrdinalIgnoreCase));
}
