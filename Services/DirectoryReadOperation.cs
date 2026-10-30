using System.Collections.Concurrent;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// kd-88. One operation's directory-read credential - a single change, or every row of a bulk file: fetched from the secret
/// store at most once and handed to every protection check inside the operation.
/// </summary>
/// <remarks>
/// <para>
/// The owner's requirement (decisions 2026-10-30): every authorization and protection check in a write path is near-instant,
/// with one credential fetch per operation. The dev log of the grant that waited two minutes shows the protected-principal
/// directory-read secret fetched three times for one grant (4.1 s, 2.7 s, 2.7 s): the target's resolution, the proof of its
/// mailbox and the group check each fetched it for themselves, and the self-grant check fetches it once more.
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
/// </remarks>
public sealed class DirectoryReadOperation : IDisposable
{
    private static readonly AsyncLocal<DirectoryReadOperation?> Ambient = new();

    private readonly DirectoryReadOperation? _outer;
    private readonly ConcurrentDictionary<int, Lazy<Task<(string username, string password, string domain)?>>>? _secrets;
    private readonly bool _joined;
    private int _disposed;

    private DirectoryReadOperation(DirectoryReadOperation? outer, bool joined, bool ownsSecrets)
    {
        _outer = outer;
        _joined = joined;
        _secrets = ownsSecrets ? new() : null;
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

    /// <summary>Whether the current flow is inside an operation.</summary>
    internal static bool InProgress => Ambient.Value is not null;

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
            return fetch(secretId);

        return secrets.GetOrAdd(secretId,
            id => new Lazy<Task<(string username, string password, string domain)?>>(() => fetch(id))).Value;
    }

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

        // The flow leaves this operation, and what it fetched goes with it.
        if (ReferenceEquals(Ambient.Value, this))
            Ambient.Value = _outer;
        _secrets?.Clear();
    }
}
