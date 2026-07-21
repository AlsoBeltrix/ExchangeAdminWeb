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
    protected readonly OperationTraceService? _operationTrace;
    protected readonly string _onPremServerUri;
    protected readonly string _moduleId;
    protected static readonly SemaphoreSlim _onPremThrottle = new(2, 2);

    protected ExchangeServiceBase(
        ExoConnectionPool exoPool,
        DelineaService delineaService,
        ILogger logger,
        string onPremServerUri,
        ModuleCredentialService? moduleCredentials = null,
        string moduleId = "",
        OperationTraceService? operationTrace = null)
    {
        _exoPool = exoPool;
        _delineaService = delineaService;
        _moduleCredentials = moduleCredentials;
        _logger = logger;
        _operationTrace = operationTrace;
        _onPremServerUri = onPremServerUri;
        _moduleId = moduleId;
    }

    // -------------------------------------------------------------------------
    // Pool / Run helpers
    // -------------------------------------------------------------------------

    /// <param name="allowRetry">
    /// True only for read-only or single-write operations. When the borrowed connection is a
    /// dead/stale session, the pool discards it and runs <paramref name="operation"/> once more
    /// on a fresh connection. MUST be false for multi-write operations: re-running after an
    /// earlier write committed would repeat it. Defaults to false (the safe default).
    /// </param>
    protected Task<PermissionResult> RunAsync(Action<PowerShell, ConnectionErrorTracker> operation, Func<(string message, string? detail)>? successFormatter = null, bool allowRetry = false)
    {
        return _exoPool.RunWithRetryAsync(pooled => Task.Run(() =>
        {
            var tracker = new ConnectionErrorTracker();
            var ps = pooled.PowerShell;
            PermissionResult result;
            try
            {
                operation(ps, tracker);

                if (successFormatter is not null)
                {
                    var (message, detail) = successFormatter();
                    result = new PermissionResult { Success = true, Message = message, Detail = detail };
                }
                else
                {
                    result = PermissionResult.Ok();
                }
            }
            catch (Exception ex)
            {
                // Invoke clears ps.Streams.Error before throwing, so recover the captured
                // detail from the exception (ex.Data) rather than re-reading the stream.
                // Fall back to the live stream for any throw that didn't route through Invoke.
                var (primary, detail) = ResolvePsErrors(ex);
                if (ex.Data[PsErrorsDataKey] is null)
                {
                    var psErrors = SnapshotErrorMessages(ps);
                    if (psErrors.Count > 0)
                    {
                        primary = psErrors[0];
                        detail = psErrors.Count > 1 ? string.Join(" | ", psErrors.Skip(1)) : null;
                    }
                }

                // Invoke (when used) already classified via the error stream before clearing it;
                // also classify from the thrown message to cover throws that bypassed Invoke and
                // BuildPsException's recovered primary text. OR-ing is idempotent.
                if (IsConnectionError(ex))
                    tracker.HasConnectionError = true;
                if (ExoConnectionPool.IsRetriablePrecheckError(ex))
                {
                    tracker.HasConnectionError = true;
                    tracker.HasRetriablePrecheckError = true;
                }

                _logger.LogError(ex, "Exchange operation failed: {Message}", primary);
                result = PermissionResult.Fail(primary, detail);
            }

            // RunAsync never re-throws - a connection failure is reported via the tracker so the
            // pool can discard and (if eligible) retry. The base helper already cleared the
            // pipeline in Invoke, so a non-connection failure leaves the connection returnable.
            return new PooledOutcome<PermissionResult>(result, tracker.HasConnectionError, tracker.HasRetriablePrecheckError);
        }), allowRetry, PoolFailurePolicy.Return);
    }

    /// <param name="allowRetry">
    /// True only for read-only or single-write queries. See <see cref="RunAsync"/>. Defaults to
    /// false (the safe default).
    /// </param>
    protected Task<T> RunPooledQueryAsync<T>(Func<PowerShell, ConnectionErrorTracker, T> query, bool allowRetry = false)
    {
        return _exoPool.RunWithRetryAsync(pooled => Task.Run(() =>
        {
            var tracker = new ConnectionErrorTracker();
            var result = query(pooled.PowerShell, tracker);
            return new PooledOutcome<T>(result, tracker.HasConnectionError, tracker.HasRetriablePrecheckError);
        }), allowRetry, PoolFailurePolicy.Return);
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
        /// <summary>Any dead/suspect-session error was seen - gates DISCARD.</summary>
        public bool HasConnectionError { get; set; }

        /// <summary>
        /// The narrow "must call Connect-ExchangeOnline" pre-cmdlet error was seen - gates RETRY.
        /// Implies <see cref="HasConnectionError"/>. See
        /// <see cref="ExoConnectionPool.IsRetriablePrecheckError"/>.
        /// </summary>
        public bool HasRetriablePrecheckError { get; set; }
    }

    // -------------------------------------------------------------------------
    // Invoke helpers
    // -------------------------------------------------------------------------

    // Key under which Invoke stashes the captured PowerShell error messages on a thrown
    // InvalidOperationException, so callers (e.g. RunAsync) can recover primary + secondary
    // detail AFTER the error stream itself has been cleared. See ResolvePsErrors.
    internal const string PsErrorsDataKey = "PsErrors";

    private static List<string> SnapshotErrorMessages(PowerShell ps) =>
        ps.Streams.Error
          .Select(e => e.Exception?.Message ?? e.ToString())
          .Where(m => !string.IsNullOrWhiteSpace(m))
          .ToList();

    private static InvalidOperationException BuildPsException(string primary, IReadOnlyList<string> allErrors, Exception? inner = null)
    {
        var ex = inner is null
            ? new InvalidOperationException(primary)
            : new InvalidOperationException(primary, inner);
        if (allErrors.Count > 0)
            ex.Data[PsErrorsDataKey] = allErrors.ToArray();
        return ex;
    }

    /// <summary>
    /// Recover the captured PowerShell error messages from an exception thrown by
    /// <see cref="Invoke(PowerShell, ConnectionErrorTracker)"/> - the structured detail in
    /// <see cref="PsErrorsDataKey"/> if present, else the exception message.
    /// </summary>
    internal static (string primary, string? detail) ResolvePsErrors(Exception ex)
    {
        if (ex.Data[PsErrorsDataKey] is string[] errors && errors.Length > 0)
        {
            var primary = errors[0];
            var detail = errors.Length > 1 ? string.Join(" | ", errors.Skip(1)) : null;
            return (primary, detail);
        }
        return (ex.Message, null);
    }

    protected static Collection<PSObject> Invoke(PowerShell ps, ConnectionErrorTracker tracker)
    {
        Collection<PSObject> result;
        try
        {
            result = ps.Invoke();
        }
        catch (Exception ex)
        {
            // A terminating error leaves the failed command queued and errors on the stream.
            // Capture detail, then clear BOTH before throwing so the next step on this pooled
            // runspace starts clean (the pipeline is shared across steps in one operation).
            var psErrors = SnapshotErrorMessages(ps);
            ClassifyConnectionError(tracker, ex, ps);

            ps.Commands.Clear();
            ps.Streams.Error.Clear();

            var primary = psErrors.FirstOrDefault() ?? ex.Message;
            throw BuildPsException(primary, psErrors, ex);
        }

        if (ps.HadErrors)
        {
            var psErrors = SnapshotErrorMessages(ps);
            ClassifyConnectionError(tracker, null, ps);

            ps.Commands.Clear();
            ps.Streams.Error.Clear();

            var primary = psErrors.FirstOrDefault() ?? "An unknown error occurred.";
            throw BuildPsException(primary, psErrors);
        }

        ps.Commands.Clear();
        return result;
    }

    /// <summary>
    /// Best-effort invoke: never throws on a cmdlet error. Returns the pipeline results and
    /// outputs any error-stream messages, captured BEFORE the stream is cleared, so the
    /// caller can classify them (e.g. distinguish an on-prem-mastered write rejection from a
    /// real failure). Clears commands + error stream before returning.
    /// </summary>
    protected static Collection<PSObject> InvokeBestEffort(PowerShell ps, ConnectionErrorTracker tracker, out IReadOnlyList<string> errors)
    {
        Collection<PSObject> result;
        try
        {
            result = ps.Invoke();
        }
        catch (Exception ex)
        {
            var captured = SnapshotErrorMessages(ps);
            if (captured.Count == 0)
                captured.Add(ex.Message);
            ClassifyConnectionError(tracker, ex, ps);

            ps.Commands.Clear();
            ps.Streams.Error.Clear();
            errors = captured;
            return new Collection<PSObject>();
        }

        var streamErrors = SnapshotErrorMessages(ps);
        ClassifyConnectionError(tracker, null, ps);

        ps.Commands.Clear();
        ps.Streams.Error.Clear();
        errors = streamErrors;
        return result;
    }

    protected static Collection<PSObject> Invoke(PowerShell ps)
    {
        return Invoke(ps, new ConnectionErrorTracker());
    }

    protected static Collection<PSObject> InvokeOptional(PowerShell ps, ConnectionErrorTracker tracker)
    {
        var result = ps.Invoke();
        ClassifyConnectionError(tracker, null, ps);
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
                _operationTrace?.Step("OnPremConnected", backend: "OnPremExchange", details: new Dictionary<string, object?> { ["uri"] = _onPremServerUri, ["attempt"] = attempt });
                return;
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                _logger.LogWarning(ex, "On-prem connection attempt {Attempt}/{Max} failed, retrying", attempt, maxRetries);
                _operationTrace?.Step("OnPremConnectionRetry", "Warning", backend: "OnPremExchange", details: new Dictionary<string, object?> { ["attempt"] = attempt, ["maxRetries"] = maxRetries });
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
            _logger.LogError("OnPremExchange:ServerUri is not configured - cannot check mailbox size");
            return null;
        }

        _operationTrace?.Step("OnPremMailboxSizeRequested", backend: "OnPremExchange", target: emailAddress);
        var creds = await GetModuleCredentialsAsync("on-prem mailbox size check");
        if (creds is null)
        {
            _logger.LogError("Cannot connect to on-prem Exchange: failed to retrieve credentials from Delinea");
            _operationTrace?.Step("OnPremMailboxSizeRequested", "Failed", backend: "OnPremExchange", target: emailAddress, details: new Dictionary<string, object?> { ["reason"] = "Credentials unavailable" });
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

    public async Task<string> GetMailboxLocationAsync(string identity)
    {
        if (await HasCloudMailboxAsync(identity))
            return "Cloud";

        return await GetOnPremMailboxLocationAsync(identity) ?? "Unknown";
    }

    protected async Task<bool> HasCloudMailboxAsync(string identity)
    {
        // Read-only: safe to retry on a dead pooled session.
        return await RunPooledQueryAsync((ps, tracker) =>
        {
            ps.AddCommand("Get-Mailbox")
              .AddParameter("Identity", identity)
              .AddParameter("ErrorAction", "Ignore");
            return InvokeOptional(ps, tracker).Any();
        }, allowRetry: true);
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

    protected static bool ParseBool(string? value, string fieldName = "field")
    {
        var trimmed = value?.Trim();
        if (string.Equals(trimmed, "True", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(trimmed, "False", StringComparison.OrdinalIgnoreCase))
            return false;
        throw new FormatException($"Invalid value '{value}' for {fieldName}. Use True or False.");
    }

    // -------------------------------------------------------------------------
    // Connection error detection
    // -------------------------------------------------------------------------

    // Single source of truth lives on the pool so PermissionValidator (not a subclass) shares it.
    protected static bool IsConnectionError(Exception? ex) => ExoConnectionPool.IsConnectionError(ex);

    /// <summary>
    /// Set the tracker's connection/precheck flags from a thrown exception and/or the error
    /// stream. Discard is gated by the broad <see cref="IsConnectionError"/>; retry by the narrow
    /// <see cref="ExoConnectionPool.IsRetriablePrecheckError"/> (which implies a connection error).
    /// Call BEFORE clearing <c>ps.Streams.Error</c>.
    /// </summary>
    private static void ClassifyConnectionError(ConnectionErrorTracker tracker, Exception? thrown, PowerShell ps)
    {
        bool conn = (thrown != null && IsConnectionError(thrown))
            || ps.Streams.Error.Any(e => IsConnectionError(e.Exception));
        bool precheck = (thrown != null && ExoConnectionPool.IsRetriablePrecheckError(thrown))
            || ps.Streams.Error.Any(e => ExoConnectionPool.IsRetriablePrecheckError(e.Exception));

        if (conn || precheck) tracker.HasConnectionError = true;
        if (precheck) tracker.HasRetriablePrecheckError = true;
    }
}
