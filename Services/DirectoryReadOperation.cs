using System.Collections.Concurrent;
using System.Diagnostics;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// kd-88. One operation's directory-read credential - a single change, or every row of a bulk file: fetched from the secret
/// store at most once and handed to every protection check inside the operation - and the time each check took.
/// </summary>
/// <remarks>
/// <para>
/// The owner's requirement (decisions 2026-10-30): every authorization and protection check in a write path is near-instant,
/// with one credential fetch per operation, and measured and logged. The dev log of the grant that waited two minutes shows
/// the protected-principal directory-read secret fetched three times for one grant (4.1 s, 2.7 s, 2.7 s): the target's
/// resolution, the proof of its mailbox and the group check each fetched it for themselves, and the self-grant check fetches
/// it once more.
/// </para>
/// <para>
/// Ambient: carried by the async flow, so the reads that share it need no new parameter - the protected-principal service's
/// virtual methods keep the shapes the suite's stand-ins override. An operation is begun where the work begins (a page
/// handler, a bulk loop, a service gate) and everything awaited inside it joins it; one begun inside another joins the
/// outer one, and only the outermost ends it. A check reached outside any operation is its own.
/// </para>
/// <para>
/// What it shares is the secret store's ANSWER for one Secret ID - the secret, or the failure. A fetch that failed is that
/// failure for every later check in the operation and is not repeated inside it (aud-a61's rule for the self-grant run), so a
/// bulk file is refused row by row on one failed fetch rather than fetched once per row. A new operation fetches afresh:
/// nothing outlives the operation that fetched it, and nothing is kept between operations.
/// </para>
/// <para>
/// A check begun with <see cref="BeginCheck"/> is timed: when it ends it logs one Information line, "{Check} for {Target}
/// took {ElapsedMs} ms ({Reads} directory reads, {Fetches} credential fetches)" - the directory reads made and the
/// credential fetches started while it ran, its nested work included. A check that read a synced recipient's on-premises SID
/// (docs/ProtectedPrincipalIdentifiers-Plan.md E) adds ", {SidReads} SID source reads from {SidSource} in {SidMs} ms" inside the
/// parentheses: that read is not a directory read, so it is counted and timed on its own. A check that skipped a protected group the
/// directory confirmed is gone and an admin ignored as missing on Verify (docs/DirectoryObjectVerify-Plan.md B.4) adds
/// ", {Ignored} ignored missing groups" last inside the parentheses. A check begun inside one of the same name is part of
/// it and logs nothing of its own, so a protected-principal check that reaches the service's own check logs once.
/// </para>
/// </remarks>
public sealed class DirectoryReadOperation : IDisposable
{
    private static readonly AsyncLocal<DirectoryReadOperation?> Ambient = new();

    private readonly DirectoryReadOperation? _outer;
    private readonly ConcurrentDictionary<int, Lazy<Task<(string username, string password, string domain)?>>>? _secrets;
    private readonly bool _joined;
    private readonly ILogger? _logger;
    private readonly string? _check;
    private readonly string? _target;
    private readonly long _started;
    private int _reads;
    private int _fetches;
    private int _sidReads;
    private long _sidMs;
    private string? _sidSource;
    private int _ignored;
    private int _disposed;

    private DirectoryReadOperation(DirectoryReadOperation? outer, bool joined, bool ownsSecrets,
        ILogger? logger = null, string? check = null, string? target = null)
    {
        _outer = outer;
        _joined = joined;
        _secrets = ownsSecrets ? new() : null;
        _logger = logger;
        _check = check;
        _target = target;
        _started = Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// Begins an operation, or joins the one the current flow is already in. Dispose it where the operation ends: a joined
    /// handle's dispose does nothing, so only the outermost one ends the operation.
    /// </summary>
    public static DirectoryReadOperation Begin()
    {
        if (Ambient.Value is { } current)
            return new DirectoryReadOperation(current, joined: true, ownsSecrets: false);

        var operation = new DirectoryReadOperation(null, joined: false, ownsSecrets: true);
        Ambient.Value = operation;
        return operation;
    }

    /// <summary>
    /// Begins a timed check - inside the current operation, or as an operation of its own - that logs its line when disposed.
    /// Inside a check of the same name it joins that check and logs nothing.
    /// </summary>
    internal static DirectoryReadOperation BeginCheck(ILogger logger, string check, string? target)
    {
        ArgumentNullException.ThrowIfNull(logger);
        var current = Ambient.Value;
        for (var node = current; node is not null; node = node._outer)
        {
            if (string.Equals(node._check, check, StringComparison.Ordinal))
                return new DirectoryReadOperation(current, joined: true, ownsSecrets: false);
        }

        var timed = new DirectoryReadOperation(current, joined: false, ownsSecrets: current is null, logger, check,
            string.IsNullOrWhiteSpace(target) ? "(no target)" : target.Trim());
        Ambient.Value = timed;
        return timed;
    }

    /// <summary>Whether the current flow is inside an operation.</summary>
    internal static bool InProgress => Ambient.Value is not null;

    /// <summary>One directory read - one directory cmdlet run - counted for every check the flow is in.</summary>
    internal static void CountRead()
    {
        for (var node = Ambient.Value; node is not null; node = node._outer)
            Interlocked.Increment(ref node._reads);
    }

    /// <summary>
    /// docs/ProtectedPrincipalIdentifiers-Plan.md E. One read of a synced recipient's on-premises SID from <paramref name="source"/>
    /// (Graph), which is not a directory read, and how long it took - counted for every check the flow is in.
    /// </summary>
    internal static void CountSidSourceRead(string source, long elapsedMs)
    {
        for (var node = Ambient.Value; node is not null; node = node._outer)
        {
            Interlocked.Increment(ref node._sidReads);
            Interlocked.Add(ref node._sidMs, elapsedMs);
            node._sidSource = source;
        }
    }

    /// <summary>
    /// docs/DirectoryObjectVerify-Plan.md B.4. <paramref name="count"/> protected groups the directory confirmed are gone that
    /// an admin ignored as missing on Verify, skipped by the check rather than refusing it - counted for every check the flow
    /// is in. The lookup that confirmed each is a directory read and is counted as one already.
    /// </summary>
    internal static void CountIgnored(int count)
    {
        if (count <= 0)
            return;
        for (var node = Ambient.Value; node is not null; node = node._outer)
            Interlocked.Add(ref node._ignored, count);
    }

    private static void CountFetch()
    {
        for (var node = Ambient.Value; node is not null; node = node._outer)
            Interlocked.Increment(ref node._fetches);
    }

    /// <summary>
    /// The directory-read secret for <paramref name="secretId"/>: inside an operation, the operation's answer -
    /// <paramref name="fetch"/> run once for it, its secret or its failure kept for every later caller - and outside one, a
    /// fetch of its own.
    /// </summary>
    internal static Task<(string username, string password, string domain)?> DirectoryReadSecretAsync(
        int secretId, Func<int, Task<(string username, string password, string domain)?>> fetch)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        var secrets = Root(Ambient.Value)?._secrets;
        if (secrets is null)
        {
            CountFetch();
            return fetch(secretId);
        }

        return secrets.GetOrAdd(secretId, id => new Lazy<Task<(string username, string password, string domain)?>>(() =>
        {
            CountFetch();
            return fetch(id);
        })).Value;
    }

    /// <summary>
    /// rev-a2. Drops the current operation's answer for <paramref name="secretId"/> - the directory rejected the credential it
    /// held - so the next caller inside the operation fetches afresh. Nothing outside an operation is kept to drop.
    /// </summary>
    internal static void ForgetDirectoryReadSecret(int secretId) => Root(Ambient.Value)?._secrets?.TryRemove(secretId, out _);

    private static DirectoryReadOperation? Root(DirectoryReadOperation? node)
    {
        while (node is not null && node._secrets is null)
            node = node._outer;
        return node;
    }

    public void Dispose()
    {
        if (_joined || Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // The flow leaves this check or operation, and what an operation fetched goes with it.
        if (ReferenceEquals(Ambient.Value, this))
            Ambient.Value = _outer;
        _secrets?.Clear();

        if (_logger is not null && _check is not null)
        {
            var elapsedMs = (long)Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
            var sidReads = Volatile.Read(ref _sidReads);
            var ignored = Volatile.Read(ref _ignored);
            if (sidReads == 0 && ignored == 0)
            {
                _logger.LogInformation("{Check} for {Target} took {ElapsedMs} ms ({Reads} directory reads, {Fetches} credential fetches)",
                    _check, _target, elapsedMs, Volatile.Read(ref _reads), Volatile.Read(ref _fetches));
            }
            else if (sidReads == 0)
            {
                // docs/DirectoryObjectVerify-Plan.md B.4: an ignored missing group skipped is visible on the same line.
                _logger.LogInformation("{Check} for {Target} took {ElapsedMs} ms ({Reads} directory reads, {Fetches} credential fetches, "
                    + "{Ignored} ignored missing groups)",
                    _check, _target, elapsedMs, Volatile.Read(ref _reads), Volatile.Read(ref _fetches), ignored);
            }
            else if (ignored > 0)
            {
                _logger.LogInformation("{Check} for {Target} took {ElapsedMs} ms ({Reads} directory reads, {Fetches} credential fetches, "
                    + "{SidReads} SID source reads from {SidSource} in {SidMs} ms, {Ignored} ignored missing groups)",
                    _check, _target, elapsedMs, Volatile.Read(ref _reads), Volatile.Read(ref _fetches),
                    sidReads, Volatile.Read(ref _sidSource), Interlocked.Read(ref _sidMs), ignored);
            }
            else
            {
                // docs/ProtectedPrincipalIdentifiers-Plan.md E: the SID source's read is not a directory read, so it has its own
                // count and elapsed figure, on the same line, so a slow source is visible.
                _logger.LogInformation("{Check} for {Target} took {ElapsedMs} ms ({Reads} directory reads, {Fetches} credential fetches, "
                    + "{SidReads} SID source reads from {SidSource} in {SidMs} ms)",
                    _check, _target, elapsedMs, Volatile.Read(ref _reads), Volatile.Read(ref _fetches),
                    sidReads, Volatile.Read(ref _sidSource), Interlocked.Read(ref _sidMs));
            }
        }
    }
}
