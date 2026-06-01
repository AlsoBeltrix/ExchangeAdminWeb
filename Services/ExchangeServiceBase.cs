using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Services;

public abstract class ExchangeServiceBase
{
    protected readonly ExoConnectionPool _exoPool;
    protected readonly DelineaService _delineaService;
    protected readonly ModuleCredentialService? _moduleCredentials;
    protected readonly ILogger _logger;
    protected readonly string _onPremServerUri;
    protected readonly string _moduleId;
    protected static readonly SemaphoreSlim _onPremThrottle = new(2, 2);

    protected ExchangeServiceBase(
        ExoConnectionPool exoPool,
        DelineaService delineaService,
        ILogger logger,
        string onPremServerUri,
        ModuleCredentialService? moduleCredentials = null,
        string moduleId = "")
    {
        _exoPool = exoPool;
        _delineaService = delineaService;
        _moduleCredentials = moduleCredentials;
        _logger = logger;
        _onPremServerUri = onPremServerUri;
        _moduleId = moduleId;
    }

    // -------------------------------------------------------------------------
    // Pool / Run helpers
    // -------------------------------------------------------------------------

    protected async Task<PermissionResult> RunAsync(Action<PowerShell, ConnectionErrorTracker> operation, Func<(string message, string? detail)>? successFormatter = null)
    {
        var pooled = await _exoPool.BorrowAsync();
        bool discard = false;
        try
        {
            var tracker = new ConnectionErrorTracker();
            var result = await Task.Run(() =>
            {
                var ps = pooled.PowerShell;
                try
                {
                    operation(ps, tracker);

                    if (successFormatter is not null)
                    {
                        var (message, detail) = successFormatter();
                        return new PermissionResult { Success = true, Message = message, Detail = detail };
                    }
                    return PermissionResult.Ok();
                }
                catch (Exception ex)
                {
                    var psErrors = ps.Streams.Error
                        .Select(e => e.Exception?.Message ?? e.ToString())
                        .Where(m => !string.IsNullOrWhiteSpace(m))
                        .ToList();

                    var primary = psErrors.FirstOrDefault() ?? ex.Message;
                    var detail = psErrors.Count > 1 ? string.Join(" | ", psErrors.Skip(1)) : null;

                    if (IsConnectionError(ex))
                        tracker.HasConnectionError = true;

                    _logger.LogError(ex, "Exchange operation failed: {Message}", primary);
                    return PermissionResult.Fail(primary, detail);
                }
            });

            discard = tracker.HasConnectionError;
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

    protected async Task<T> RunPooledQueryAsync<T>(Func<PowerShell, ConnectionErrorTracker, T> query)
    {
        var pooled = await _exoPool.BorrowAsync();
        bool discard = false;
        try
        {
            var tracker = new ConnectionErrorTracker();
            var result = await Task.Run(() => query(pooled.PowerShell, tracker));

            discard = tracker.HasConnectionError;
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
    // Connection error tracking
    // -------------------------------------------------------------------------

    protected internal sealed class ConnectionErrorTracker
    {
        public bool HasConnectionError { get; set; }
    }

    // -------------------------------------------------------------------------
    // Invoke helpers
    // -------------------------------------------------------------------------

    protected static Collection<PSObject> Invoke(PowerShell ps, ConnectionErrorTracker tracker)
    {
        Collection<PSObject> result;
        try
        {
            result = ps.Invoke();
        }
        catch (RuntimeException ex)
        {
            if (IsConnectionError(ex))
                tracker.HasConnectionError = true;
            throw new InvalidOperationException(ex.Message, ex);
        }

        if (ps.HadErrors)
        {
            var err = ps.Streams.Error.FirstOrDefault();
            var msg = err?.Exception?.Message ?? err?.ToString() ?? "An unknown error occurred.";
            if (IsConnectionError(err?.Exception))
                tracker.HasConnectionError = true;
            throw new InvalidOperationException(msg);
        }

        ps.Commands.Clear();
        return result;
    }

    protected static Collection<PSObject> Invoke(PowerShell ps)
    {
        return Invoke(ps, new ConnectionErrorTracker());
    }

    protected static Collection<PSObject> InvokeOptional(PowerShell ps, ConnectionErrorTracker tracker)
    {
        var result = ps.Invoke();
        if (ps.Streams.Error.Any(e => IsConnectionError(e.Exception)))
            tracker.HasConnectionError = true;
        ps.Streams.Error.Clear();
        ps.Commands.Clear();
        return result;
    }

    protected static Collection<PSObject> InvokeOptional(PowerShell ps)
    {
        return InvokeOptional(ps, new ConnectionErrorTracker());
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

    protected void ConnectOnPrem(PowerShell ps, string username, string password, string domain, int maxRetries = 3)
    {
        var fullUsername = username.Contains('\\') || username.Contains('@')
            ? username
            : $"{domain}\\{username}";

        var securePassword = new System.Security.SecureString();
        foreach (var c in password)
            securePassword.AppendChar(c);
        securePassword.MakeReadOnly();

        var credential = new PSCredential(fullUsername, securePassword);

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                ps.Commands.Clear();
                ps.Streams.Error.Clear();

                ps.AddScript("New-PSSessionOption -OperationTimeout 15000 -OpenTimeout 10000");
                var optResult = ps.Invoke();
                ps.Commands.Clear();
                var sessionOpt = optResult.FirstOrDefault()?.BaseObject;

                ps.AddCommand("New-PSSession")
                  .AddParameter("ConfigurationName", "Microsoft.Exchange")
                  .AddParameter("ConnectionUri", _onPremServerUri)
                  .AddParameter("Authentication", "Kerberos")
                  .AddParameter("Credential", credential)
                  .AddParameter("ErrorAction", "Stop");
                if (sessionOpt != null)
                    ps.AddParameter("SessionOption", sessionOpt);
                var sessions = Invoke(ps);
                var session = sessions.FirstOrDefault()
                    ?? throw new InvalidOperationException("New-PSSession returned no session object");

                ps.Runspace.SessionStateProxy.SetVariable("onpremSession", session.BaseObject);
                _logger.LogInformation("Connected to on-prem Exchange at {Uri} (attempt {Attempt})", _onPremServerUri, attempt);
                return;
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                _logger.LogWarning(ex, "On-prem connection attempt {Attempt}/{Max} failed, retrying", attempt, maxRetries);
                Thread.Sleep(2000 * attempt);
            }
        }

        throw new InvalidOperationException($"Failed to connect to on-prem Exchange at {_onPremServerUri} after {maxRetries} attempts");
    }

    protected async Task<(string username, string password, string domain)?> GetModuleCredentialsAsync(string purpose)
    {
        if (_moduleCredentials is null || string.IsNullOrWhiteSpace(_moduleId))
        {
            _logger.LogError("Cannot retrieve credentials for {Purpose}: service has no module credential context", purpose);
            return null;
        }

        return await _moduleCredentials.GetCredentialsAsync(_moduleId, purpose);
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

        var creds = await GetModuleCredentialsAsync("on-prem mailbox size check");
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

    protected async Task<string> GetMailboxLocationAsync(string identity)
    {
        if (await HasCloudMailboxAsync(identity))
            return "Cloud";

        return await GetOnPremMailboxLocationAsync(identity) ?? "Unknown";
    }

    protected async Task<bool> HasCloudMailboxAsync(string identity)
    {
        return await RunPooledQueryAsync((ps, tracker) =>
        {
            ps.AddCommand("Get-Mailbox")
              .AddParameter("Identity", identity)
              .AddParameter("ErrorAction", "Ignore");
            return InvokeOptional(ps, tracker).Any();
        });
    }

    private async Task<string?> GetOnPremMailboxLocationAsync(string identity)
    {
        if (string.IsNullOrWhiteSpace(_onPremServerUri))
        {
            _logger.LogWarning("OnPremExchange:ServerUri is not configured; cannot determine on-prem mailbox location for {Identity}", identity);
            return null;
        }

        var creds = await GetModuleCredentialsAsync("on-prem mailbox location check");
        if (creds is null)
        {
            _logger.LogError("Cannot determine on-prem mailbox location for {Identity}: failed to retrieve credentials from Delinea", identity);
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
                var session = ps.Runspace.SessionStateProxy.GetVariable("onpremSession");

                if (OnPremRecipientExists(ps, session, "Get-Mailbox", identity))
                    return "OnPrem";

                if (OnPremRecipientExists(ps, session, "Get-RemoteMailbox", identity))
                    return "Cloud";

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to determine on-prem mailbox location for {Identity}", identity);
                return null;
            }
            finally
            {
                RemoveOnPremSession(ps);
            }
        }), _onPremThrottle);
    }

    protected static bool OnPremRecipientExists(PowerShell ps, object? session, string commandName, string identity)
    {
        if (session is null)
            return false;

        var script = ScriptBlock.Create($"param($Identity) {commandName} -Identity $Identity -ErrorAction SilentlyContinue | Select-Object -First 1 Identity");
        ps.AddCommand("Invoke-Command")
          .AddParameter("Session", session)
          .AddParameter("ScriptBlock", script)
          .AddParameter("ArgumentList", new object[] { identity });
        return InvokeOptional(ps).Any();
    }

    protected static void RemoveOnPremSession(PowerShell ps)
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
