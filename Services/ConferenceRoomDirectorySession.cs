using System.Management.Automation;
using System.Management.Automation.Runspaces;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// Sweep item 28. One Room Finder row's Active Directory work: the module's AD credential looked up once, and one directory
/// runspace - opened, the ActiveDirectory module imported and the credential prepared once - for every AD step the row takes.
/// </summary>
/// <remarks>
/// <para>
/// A row's four AD paths - the City/State/Country preflight and write, the on-prem room-list preflight and add - each looked
/// the credential up and opened, imported and prepared a runspace of their own, so a synced-list row made up to four lookups
/// and four module imports (sweep item 28). The kd-88 rule, one credential fetch per operation, applied to this module's
/// own credential: the row is the operation. The first lookup's answer - the credential or why there is none - is the row's
/// answer for every later step; a lookup that threw (the operator's cancel) keeps nothing, so a later step asks again.
/// </para>
/// <para>
/// The steps run one after another, never at once. Each takes the pipeline with its commands and every stream cleared, so
/// what one step left - a command, an error record its own <c>HadErrors</c> check would take - is not the next one's. A
/// runspace that is no longer open is replaced, the module imported into it again. Nothing outlives the row: disposing the
/// session disposes the pipeline and the runspace. What each step reads and writes, and in which order, is unchanged: only
/// the setup they used to repeat is shared. Conference Rooms runs no protected-principal check (owner ruling 2026-11-03),
/// and this adds none.
/// </para>
/// </remarks>
internal sealed class ConferenceRoomDirectorySession : IDisposable
{
    private ModuleCredentialLookup? _lookup;
    private Runspace? _runspace;
    private PowerShell? _ps;
    private bool _imported;
    private PSCredential? _credential;
    private bool _disposed;

    /// <summary>How many lookups this session made: one per row, unless one threw.</summary>
    internal int Lookups { get; private set; }

    /// <summary>How many directory runspaces this session opened: one per row, unless one stopped being open.</summary>
    internal int RunspacesOpened { get; private set; }

    /// <summary>The row's credential lookup: <paramref name="lookup"/> the first time, its answer every time after.</summary>
    public async Task<ModuleCredentialLookup> LookupAsync(Func<Task<ModuleCredentialLookup>> lookup)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_lookup is { } answered)
            return answered;

        Lookups++;
        var answer = await lookup();
        _lookup = answer;
        return answer;
    }

    /// <summary>
    /// The row's pipeline on its directory runspace, cleared for the step about to use it: opened by <paramref name="open"/>
    /// the first time, and again only when the runspace is no longer open.
    /// </summary>
    public PowerShell Pipeline(Func<Runspace> open)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_ps is not null && _runspace?.RunspaceStateInfo.State == RunspaceState.Opened)
        {
            _ps.Commands.Clear();
            _ps.Streams.ClearStreams();
            return _ps;
        }

        DisposeRunspace();
        var runspace = open();
        RunspacesOpened++;
        var ps = PowerShell.Create();
        ps.Runspace = runspace;
        (_runspace, _ps) = (runspace, ps);
        return ps;
    }

    /// <summary>The ActiveDirectory module, imported by <paramref name="import"/> into the row's runspace once.</summary>
    public void EnsureImported(Action<PowerShell> import)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var ps = _ps ?? throw new InvalidOperationException("The directory runspace is not open.");
        if (_imported)
            return;
        import(ps);
        _imported = true;
    }

    /// <summary>The row's AD credential, prepared by <paramref name="create"/> once.</summary>
    public PSCredential Credential(Func<PSCredential> create) => _credential ??= create();

    private void DisposeRunspace()
    {
        _ps?.Dispose();
        _runspace?.Dispose();
        (_ps, _runspace, _imported) = (null, null, false);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        DisposeRunspace();
    }
}
