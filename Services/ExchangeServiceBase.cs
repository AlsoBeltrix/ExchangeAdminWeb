using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
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
                // Fall back to the live stream for any throw that didn't route through Invoke. aud-a23i: an error
                // stopped by -ErrorAction Stop is given in the cmdlet's own words (ResolveFailure).
                var (primary, detail) = ResolveFailure(ex);
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
            try
            {
                var result = query(pooled.PowerShell, tracker);
                return new PooledOutcome<T>(result, tracker.HasConnectionError, tracker.HasRetriablePrecheckError);
            }
            catch (Exception ex) when (tracker.HasConnectionError)
            {
                // psl-4: a helper classified this session from its error stream before the query
                // threw something unrelated. The pool reads only a thrown message, so it would
                // return the suspect connection and skip an eligible precheck retry. Hand it the
                // tracker's classification with the original exception instead; the throw's own
                // precheck signature still counts, as on the thrown path. With no tracker
                // evidence the exception propagates unchanged and its message is classified.
                return PooledOutcome<T>.ConnectionFault(ExceptionDispatchInfo.Capture(ex),
                    tracker.HasRetriablePrecheckError || ExoConnectionPool.IsRetriablePrecheckError(ex));
            }
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

    // Key under which invoke helpers stash captured PowerShell error messages on a thrown
    // exception, so callers (e.g. RunAsync) can recover primary + secondary
    // detail AFTER the error stream itself has been cleared. See ResolvePsErrors.
    internal const string PsErrorsDataKey = "PsErrors";

    // aud-a23i. What Invoke reports when PowerShell flags a call as having errors (HadErrors) and the error stream holds
    // none. It said "An unknown error occurred.", but the shape is known: an error suppressed by -ErrorAction
    // SilentlyContinue or Ignore - on a command the call ran, or on the call itself - sets HadErrors and writes no record
    // (probed on this host's PowerShell 7.6 SDK). Still a failure: a write PowerShell flagged is not confirmed. Worded with
    // none of the words IsConnectionError looks for, so it is not read as a dead session.
    internal const string ErrorNotSurfaced =
        "The command flagged an error but surfaced none: an error suppressed by -ErrorAction SilentlyContinue or Ignore, "
        + "on a command it ran or on itself, sets that flag without writing an error record.";

    // aud-a23o. Why an on-premises read could not be made at all, in the base's own words. Each was a null: the size read's
    // callers turned it into a cause they guessed ("on-prem connection unavailable", "connection not configured"), and the
    // location read into "Unknown", which the pages word "No cloud mailbox, on-prem mailbox, or on-prem remote mailbox was
    // found."
    internal const string OnPremNotConfigured = "on-premises Exchange is not configured (OnPremExchange:ServerUri is not set)";

    internal const string OnPremCredentialsUnavailable = "the module's on-premises Exchange credentials could not be retrieved";

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
    /// an invoke helper - the structured detail in
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

    /// <summary>
    /// aud-a23i. What a failed call says went wrong, for its failure message. An error a cmdlet writes under -ErrorAction
    /// Stop arrives as an ActionPreferenceStopException, whose message puts "The running command stopped because the
    /// preference variable ... is set to Stop:" in front of the error, and nothing reaches the error stream to read
    /// instead. Its ErrorRecord holds the error as written: that is the message, and any records the stream did capture
    /// are the detail. Anything else is <see cref="ResolvePsErrors"/>, as before.
    /// </summary>
    /// <remarks>
    /// One place, so every RunAsync caller reports the cmdlet's own words; aud-a23g did this for the Migration batch list
    /// alone. It looks through a wrapper only when the wrapper restates the exception it holds, as Invoke's does under
    /// Stop: a wrapper that says more - the step a caller names, the read it was making - keeps its own words.
    /// </remarks>
    internal static (string primary, string? detail) ResolveFailure(Exception ex)
    {
        var stop = ex as ActionPreferenceStopException
            ?? (ex.InnerException is ActionPreferenceStopException inner && inner.Message == ex.Message ? inner : null);
        var stopped = stop?.ErrorRecord?.ToString();
        if (string.IsNullOrWhiteSpace(stopped))
            return ResolvePsErrors(ex);

        var captured = ex.Data[PsErrorsDataKey] as string[];
        return (stopped, captured is { Length: > 0 } ? string.Join(" | ", captured) : null);
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

            var primary = psErrors.FirstOrDefault() ?? ErrorNotSurfaced;
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
        try
        {
            var result = ps.Invoke();
            ClassifyConnectionError(tracker, null, ps);
            return result;
        }
        catch (Exception ex)
        {
            // Optional non-terminating errors are consumed, but a terminating failure must
            // retain its diagnostics for RunAsync after this shared pipeline is cleaned.
            var psErrors = SnapshotErrorMessages(ps);
            ClassifyConnectionError(tracker, ex, ps);
            if (psErrors.Count > 0)
                ex.Data[PsErrorsDataKey] = psErrors.ToArray();
            throw;
        }
        finally
        {
            ps.Commands.Clear();
            ps.Streams.Error.Clear();
        }
    }

    protected static Collection<PSObject> InvokeOptional(PowerShell ps)
    {
        return InvokeOptional(ps, new ConnectionErrorTracker());
    }

    // aud-a23g. A cancelled or stopped read is neither a row that could not be read nor a read that failed, so a read's
    // catch lets it pass. The whole chain is searched because PowerShell's adapter wraps whatever a property getter throws,
    // a cancellation included, in a GetValueInvocationException. aud-a23o: moved here from MigrationService, with
    // ReadUnlessNotFound, IsNotFound and ReadSize, so the base's own reads decide by the same rules as Migration's.
    protected static bool IsCancellation(Exception ex)
    {
        for (Exception? cause = ex; cause != null; cause = cause.InnerException)
        {
            if (cause is OperationCanceledException or PipelineStoppedException)
                return true;
        }
        return false;
    }

    // aud-a23h. A read under -ErrorAction Stop through InvokeOptional (aud-a23g: it fails on an exception, not on
    // HadErrors alone, which an error a nested command suppresses sets with nothing thrown). Rows when Exchange found the
    // object; none when it said there is no such object; any other failure thrown to the caller.
    protected static Collection<PSObject> ReadUnlessNotFound(PowerShell ps, ConnectionErrorTracker tracker)
    {
        try
        {
            return InvokeOptional(ps, tracker);
        }
        catch (Exception ex) when (IsNotFound(ex))
        {
            return new Collection<PSObject>();
        }
    }

    /// <summary>
    /// aud-a23h. Whether a failed read was Exchange saying there is no such object - the one failure that is an answer.
    /// Read from the error record's structure, never its text: the category ObjectNotFound; a reason (the exception's
    /// type name) ending NotFoundException; or such a name among the identifiers of its FullyQualifiedErrorId, the Exchange
    /// Online module's form ("[FailureCategory=Cmdlet-ManagementObjectNotFoundException] 2B5E15D7,..."). The record is the
    /// exception's own or, one level in, the one inside the base class's Invoke wrapper.
    /// </summary>
    /// <remarks>
    /// ASSUMPTION, unverified (no live call): that the module reports "no such object" from Get-MigrationUser, Get-Mailbox
    /// and Get-MigrationBatch -Identity in one of those shapes. If it uses none, a check of a mailbox with no migration
    /// reads "could not be determined" and creation is blocked - visible and fail-closed, never "eligible".
    /// <para>
    /// Never CommandNotFoundException, which PowerShell also files under ObjectNotFound: a cmdlet this session does not
    /// have - a role not granted, a module not loaded - says nothing about the mailbox. And never the identifier of an
    /// error a throw statement raised, which PowerShell makes of the thrown text.
    /// </para>
    /// <para>
    /// aud-a23o. Also what the base's own reads take for "no such object": Exchange Online's Get-Mailbox for the location
    /// read, and on-premises Exchange's Get-Mailbox, Get-RemoteMailbox and Get-MailboxStatistics -Archive through
    /// Invoke-Command. ASSUMPTION, unverified (no live call): remoting keeps the record's category, reason and
    /// FullyQualifiedErrorId. If it does not, a location read of a mailbox not on that side is refused, and a move to Exchange
    /// Online of a mailbox with no archive is Undetermined - visible and fail-closed, never "no mailbox" or "no archive".
    /// </para>
    /// </remarks>
    internal static bool IsNotFound(Exception ex)
    {
        var record = (ex as IContainsErrorRecord ?? ex.InnerException as IContainsErrorRecord)?.ErrorRecord;
        if (record is null
            || ex is CommandNotFoundException
            || ex.InnerException is CommandNotFoundException
            || Identifiers(record.FullyQualifiedErrorId).Contains(nameof(CommandNotFoundException), StringComparer.Ordinal))
        {
            return false;
        }

        if (record.CategoryInfo.Category == ErrorCategory.ObjectNotFound || NamesNotFound(record.CategoryInfo.Reason))
            return true;

        var thrown = record.Exception as RuntimeException ?? ex as RuntimeException ?? ex.InnerException as RuntimeException;
        return thrown is not { WasThrownFromThrowStatement: true }
            && Identifiers(record.FullyQualifiedErrorId).Any(NamesNotFound);
    }

    private static bool NamesNotFound(string? identifier) =>
        identifier is not null
        && identifier.EndsWith("NotFoundException", StringComparison.Ordinal)
        && !identifier.EndsWith(nameof(CommandNotFoundException), StringComparison.Ordinal);

    private static IEnumerable<string> Identifiers(string? fullyQualifiedErrorId) =>
        IdentifierPart.Matches(fullyQualifiedErrorId ?? "").Select(match => match.Value);

    private static readonly Regex IdentifierPart = new(@"[A-Za-z0-9_.]+", RegexOptions.CultureInvariant);

    // -------------------------------------------------------------------------
    // Validation helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Reads the validated mailbox back by its GUID and refuses unless Exchange returns exactly
    /// that mailbox (aud-a2d). Returns its primary SMTP address, for messages only - never as an
    /// identity to write with.
    /// </summary>
    /// <remarks>
    /// The read the writes already made before writing, now checking what it gets. It used to take
    /// the operator's string and return whatever Exchange resolved it to, which the calendar paths
    /// then built their folder identity from. Exchange documents "GUID" as a mailbox identity
    /// without saying which GUID; comparing the ExchangeGuid it returns with the one the check
    /// proved means a GUID read some other way - or any other surprise in resolution - refuses
    /// before a write instead of landing on another mailbox.
    /// </remarks>
    protected static string BindValidatedMailbox(PowerShell ps, ConnectionErrorTracker tracker, ValidatedMailbox mailbox)
    {
        var found = ReadValidatedMailbox(ps, tracker, mailbox);
        var primary = found.Properties["PrimarySmtpAddress"]?.Value?.ToString();
        return string.IsNullOrWhiteSpace(primary) ? mailbox.Identity : primary;
    }

    /// <summary>
    /// <see cref="BindValidatedMailbox"/>'s read, returning the mailbox object itself for callers
    /// that need more of it than the address (the Conference Rooms room read). Same refusals.
    /// </summary>
    protected static PSObject ReadValidatedMailbox(PowerShell ps, ConnectionErrorTracker tracker, ValidatedMailbox mailbox)
    {
        ps.AddCommand("Get-Mailbox")
          .AddParameter("Identity", mailbox.Identity)
          .AddParameter("ErrorAction", "Stop");
        var found = Invoke(ps, tracker);

        if (found.Count != 1)
        {
            throw new InvalidOperationException(
                $"Exchange returned {found.Count} mailboxes for the validated mailbox {mailbox.Identity} - nothing was written.");
        }

        var exchangeGuid = ExchangeIdentityResolver.ReadExchangeGuid(found[0]);
        if (exchangeGuid != mailbox.ExchangeGuid)
        {
            throw new InvalidOperationException(
                $"Exchange resolved the validated mailbox {mailbox.Identity} to a different mailbox "
                + $"({exchangeGuid?.ToString("D") ?? "no mailbox GUID"}) - nothing was written.");
        }

        return found[0];
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

    // virtual: a test seam so the on-prem writes can be driven against an in-process runspace
    // (aud-a2d) - the override defines the remote session and the cmdlets locally. No behaviour change.
    protected virtual void ConnectOnPrem(PowerShell ps, string username, string password, string domain, int maxRetries = 3)
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

    // virtual: the same test seam as ConnectOnPrem (aud-a2d). No behaviour change.
    protected virtual async Task<(string username, string password, string domain)?> GetModuleCredentialsAsync(string purpose)
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

    // aud-a23k, aud-a23o. A mailbox's size and its archive's, each with why it could not be read when it could not; a size
    // with a reason is never shown as a number, summed, or held against a quota. Both size reads return it: Exchange Online's,
    // for a move back on-premises (MigrationService), and on-premises Exchange's, for the quota a move to Exchange Online is
    // checked against and for Recipient Lookup (GetOnPremMailboxSizeAsync).
    protected internal sealed record MailboxSizes(double MailboxGB, string? MailboxUnreadable, double ArchiveGB, string? ArchiveUnreadable)
    {
        // aud-a23o. Neither size could be read, for one reason: a read that could not be made at all.
        internal static MailboxSizes Unreadable(string reason) => new(0, reason, 0, reason);

        // aud-a23o. What could not be read and why, as one clause ("the on-premises mailbox size could not be read: ..."), or
        // null when both were read; one reason for both sizes is said once.
        internal string? UnreadableReason(string where) => (MailboxUnreadable, ArchiveUnreadable) switch
        {
            (null, null) => null,
            (string mailbox, string archive) when mailbox == archive => $"the {where} mailbox and archive sizes could not be read: {mailbox}",
            (string mailbox, null) => $"the {where} mailbox size could not be read: {mailbox}",
            (null, string archive) => $"the {where} archive size could not be read: {archive}",
            (string mailbox, string archive) => $"the {where} mailbox size could not be read: {mailbox}; the {where} archive size could not be read: {archive}",
        };
    }

    // aud-a23k. A size as reported, or why it is not one: no row, no TotalItemSize, or one ParseSizeToGB cannot read.
    // aud-a23o: moved here from MigrationService; both size reads use it.
    protected static (double gb, string? unreadable) ReadSize(Collection<PSObject> stats, string noRow)
    {
        var row = stats.FirstOrDefault();
        if (row is null)
            return (0, noRow);

        var reported = row.Properties["TotalItemSize"]?.Value?.ToString();
        if (string.IsNullOrWhiteSpace(reported))
            return (0, "Exchange reported no TotalItemSize");

        return ParseSizeToGB(reported) is { } gb
            ? (gb, null)
            : (0, $"the reported TotalItemSize '{reported}' is not a size this app can read");
    }

    // aud-a23o. The sizes a move to Exchange Online is checked against, each read or with why it was not - never 0. It read a
    // TotalItemSize it could not parse as 0 (ParseExchangeSize's "?? 0", now gone) and any failure of the archive read as no
    // archive, so the quota check could pass a mailbox whose size was never read; and it returned null for every other
    // failure, which its callers filed under a cause they guessed. Exchange's own not-found for the archive, read from the
    // error's structure (IsNotFound), is no archive; no row, or a size that does not parse, is a marker. Neither read's catch
    // takes a cancellation. ASSUMPTION, unverified (no live call): on-premises Exchange reports a mailbox with no archive,
    // through remoting, in a shape IsNotFound reads; if not, the check of such a mailbox is Undetermined - visible, never a
    // guess, and one dev check of that error record settles it.
    protected async Task<MailboxSizes> GetOnPremMailboxSizeAsync(string emailAddress)
    {
        if (string.IsNullOrEmpty(_onPremServerUri))
        {
            _logger.LogError("OnPremExchange:ServerUri is not configured - cannot check mailbox size");
            return MailboxSizes.Unreadable(OnPremNotConfigured);
        }

        _operationTrace?.Step("OnPremMailboxSizeRequested", backend: "OnPremExchange", target: emailAddress);
        var creds = await GetModuleCredentialsAsync("on-prem mailbox size check");
        if (creds is null)
        {
            _logger.LogError("Cannot connect to on-prem Exchange: failed to retrieve credentials from Delinea");
            _operationTrace?.Step("OnPremMailboxSizeRequested", "Failed", backend: "OnPremExchange", target: emailAddress, details: new Dictionary<string, object?> { ["reason"] = "Credentials unavailable" });
            return MailboxSizes.Unreadable(OnPremCredentialsUnavailable);
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

                double mailboxGB = 0;
                string? mailboxUnreadable;
                try
                {
                    AddOnPremRead(ps, session, "Get-MailboxStatistics -Identity $Identity -ErrorAction Stop | Select-Object TotalItemSize", emailAddress);
                    (mailboxGB, mailboxUnreadable) = ReadSize(Invoke(ps), "on-premises Exchange returned no mailbox statistics");
                }
                catch (Exception ex) when (!IsCancellation(ex))
                {
                    mailboxUnreadable = $"Get-MailboxStatistics failed: {ResolveFailure(ex).primary.TrimEnd('.')}";
                }

                _logger.LogInformation("On-prem mailbox size for {Email}: {Size}", emailAddress, mailboxUnreadable ?? $"{mailboxGB} GB");

                double archiveGB = 0;
                string? archiveUnreadable = null;
                try
                {
                    AddOnPremRead(ps, session, "Get-MailboxStatistics -Identity $Identity -Archive -ErrorAction Stop | Select-Object TotalItemSize", emailAddress);
                    (archiveGB, archiveUnreadable) = ReadSize(Invoke(ps), "on-premises Exchange returned no archive statistics");
                    _logger.LogInformation("On-prem archive size for {Email}: {Size}", emailAddress, archiveUnreadable ?? $"{archiveGB} GB");
                }
                catch (Exception ex) when (IsNotFound(ex))
                {
                    _logger.LogInformation("No archive mailbox found for {Email}", emailAddress);
                }
                catch (Exception ex) when (!IsCancellation(ex))
                {
                    archiveUnreadable = $"Get-MailboxStatistics -Archive failed: {ResolveFailure(ex).primary.TrimEnd('.')}";
                }

                return new MailboxSizes(mailboxGB, mailboxUnreadable, archiveGB, archiveUnreadable);
            }
            catch (Exception ex) when (!IsCancellation(ex))
            {
                _logger.LogError(ex, "Failed to get on-prem mailbox size for {Email}", emailAddress);
                return MailboxSizes.Unreadable(ResolveFailure(ex).primary.TrimEnd('.'));
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

    // aud-a23o. One read in the on-premises session: -ErrorAction Stop on the remote command and on Invoke-Command, so a
    // remote failure throws here with its record - category, reason, FullyQualifiedErrorId - for IsNotFound to read, where it
    // reached the local error stream as text, or, under SilentlyContinue, not at all.
    private static void AddOnPremRead(PowerShell ps, object? session, string command, string identity) =>
        ps.AddCommand("Invoke-Command")
          .AddParameter("Session", session)
          .AddParameter("ScriptBlock", ScriptBlock.Create($"param($Identity) {command}"))
          .AddParameter("ArgumentList", new object[] { identity })
          .AddParameter("ErrorAction", "Stop");

    // aud-a23o. Where a mailbox is, or why that could not be told: a cloud read that fails is reported as such, not as "no
    // cloud mailbox" - under Ignore it was, and on-premises was then asked about a mailbox Exchange Online had not answered
    // for. "Unknown" means both sides answered that they hold no such mailbox.
    public async Task<string> GetMailboxLocationAsync(string identity)
    {
        bool inTheCloud;
        try
        {
            inTheCloud = await HasCloudMailboxAsync(identity);
        }
        catch (Exception ex) when (!IsCancellation(ex))
        {
            throw new InvalidOperationException($"Exchange Online could not be asked: {ResolveFailure(ex).primary.TrimEnd('.')}", ex);
        }

        if (inTheCloud)
            return "Cloud";

        return await GetOnPremMailboxLocationAsync(identity) ?? "Unknown";
    }

    // aud-a23o. What a page refuses with when the location read fails, before any write. On the two permissions pages it
    // reached the handler's catch, "Operation may have succeeded but audit logging failed", which says of a read that a write
    // may have happened. aud-a70 (5): the pages record it, and it ends as their other refusals before a write end.
    internal static string LocationReadFailure(string target, Exception ex) =>
        $"Could not determine mailbox location for {target}: {ResolveFailure(ex).primary.TrimEnd('.')}. Nothing was written.";

    /// <summary>
    /// Where the validated mailbox lives, asked by its GUID so the location decides the path for
    /// the mailbox that will be written, not for whatever the typed string resolves to (aud-a2d).
    /// No mailbox is "Unknown", which every caller refuses.
    /// </summary>
    public Task<string> GetMailboxLocationAsync(ValidatedMailbox? mailbox) =>
        mailbox is null ? Task.FromResult("Unknown") : GetMailboxLocationAsync(mailbox.Identity);

    protected async Task<bool> HasCloudMailboxAsync(string identity)
    {
        // Read-only: safe to retry on a dead pooled session.
        return await RunPooledQueryAsync((ps, tracker) =>
        {
            // aud-a23o. Stop, not Ignore: under Ignore a read that failed - an error the cmdlet wrote, even a throw - was no
            // rows, "no cloud mailbox". ReadUnlessNotFound keeps Exchange's own "no such object" an answer and rethrows every
            // other failure as it was thrown, so the pool's discard and retry (psl-4) see what they saw before.
            ps.AddCommand("Get-Mailbox")
              .AddParameter("Identity", identity)
              .AddParameter("ErrorAction", "Stop");
            return ReadUnlessNotFound(ps, tracker).Count > 0;
        }, allowRetry: true);
    }

    // aud-a23o. A side that could not be asked - no server, no credentials, a session or a read that fails - throws, naming
    // why. Each was null, which GetMailboxLocationAsync made "Unknown". Null now only when both reads answered that there is
    // no such recipient.
    private async Task<string?> GetOnPremMailboxLocationAsync(string identity)
    {
        if (string.IsNullOrWhiteSpace(_onPremServerUri))
        {
            _logger.LogWarning("OnPremExchange:ServerUri is not configured; cannot determine on-prem mailbox location for {Identity}", identity);
            throw new InvalidOperationException($"on-premises Exchange could not be asked: {OnPremNotConfigured}");
        }

        var creds = await GetModuleCredentialsAsync("on-prem mailbox location check");
        if (creds is null)
        {
            _logger.LogError("Cannot determine on-prem mailbox location for {Identity}: failed to retrieve credentials from Delinea", identity);
            throw new InvalidOperationException($"on-premises Exchange could not be asked: {OnPremCredentialsUnavailable}");
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
            catch (Exception ex) when (!IsCancellation(ex))
            {
                _logger.LogError(ex, "Failed to determine on-prem mailbox location for {Identity}", identity);
                throw new InvalidOperationException($"on-premises Exchange could not be asked: {ResolveFailure(ex).primary.TrimEnd('.')}", ex);
            }
            finally
            {
                RemoveOnPremSession(ps);
            }
        }), _onPremThrottle);
    }

    // aud-a23o. Stop, not SilentlyContinue, which made a read that failed "no such recipient": Exchange's not-found, by
    // structure, is the answer, and any other failure is "<cmdlet> failed: <reason>". No session is no answer either; it was
    // "no recipient".
    protected static bool OnPremRecipientExists(PowerShell ps, object? session, string commandName, string identity)
    {
        if (session is null)
            throw new InvalidOperationException($"{commandName} failed: there is no on-premises Exchange session to run it in");

        AddOnPremRead(ps, session, $"{commandName} -Identity $Identity -ErrorAction Stop | Select-Object -First 1 Identity", identity);
        try
        {
            return ReadUnlessNotFound(ps, new ConnectionErrorTracker()).Count > 0;
        }
        catch (Exception ex) when (!IsCancellation(ex))
        {
            throw new InvalidOperationException($"{commandName} failed: {ResolveFailure(ex).primary.TrimEnd('.')}", ex);
        }
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

        // Composition lives in CalendarFolderIdentity so it is testable without Exchange: a wrong
        // identity does not fail, it targets a different folder.
        var fullPath = CalendarFolderIdentity.Build(mailbox, rawFolderPath);
        _logger.LogInformation("Constructed calendar identity: '{FullPath}'", fullPath);
        return fullPath;
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
