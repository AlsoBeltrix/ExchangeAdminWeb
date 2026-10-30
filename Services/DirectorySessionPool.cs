using System.Management.Automation;
using System.Management.Automation.Runspaces;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// kd-88. Warm directory sessions for the protection checks: runspaces with the ActiveDirectory module already imported,
/// kept open between checks and lent to one check at a time.
/// </summary>
/// <remarks>
/// <para>
/// Every protection check used to build its own runspace and import the ActiveDirectory module before its first read - a
/// cost of seconds, paid again by the next check (the resolution, the mailbox proof, the group check and the self-grant check
/// of one grant each paid it). The owner's requirement (decisions 2026-10-30) is a warm session. This keeps
/// <see cref="ADDirectorySearchService"/>'s rule for its cached runspace (<c>GetOrCreateRunspace</c>): a session is reused
/// while it stays open, and one a read failed in is thrown away and a fresh one built the next time - so a session a failure
/// may have broken is never lent again. The searches keep their own runspace: a protection check never waits behind the
/// operator's typing, nor the typing behind a check.
/// </para>
/// <para>
/// A session holds no identity. Every read run in it names its own credential (<c>-Credential</c>), or runs as the app
/// pool's identity exactly as it did in a session of its own; what a session keeps between checks is the imported module
/// and nothing a check reads.
/// </para>
/// <para>
/// At most <see cref="Size"/> sessions exist, each lent to one check at a time; a check that finds none free within the
/// wait it was given is told so (<see cref="TimeoutException"/>), and its caller fails closed.
/// </para>
/// </remarks>
internal sealed class DirectorySessionPool : IDisposable
{
    internal const int DefaultSize = 4;

    private readonly Func<Runspace> _createRunspace;
    private readonly SemaphoreSlim _slots;
    private readonly Stack<Runspace> _idle = new();
    private readonly object _gate = new();
    private int _opened;
    private bool _disposed;

    internal DirectorySessionPool(Func<Runspace> createRunspace, int size = DefaultSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        _createRunspace = createRunspace ?? throw new ArgumentNullException(nameof(createRunspace));
        Size = size;
        _slots = new SemaphoreSlim(size, size);
    }

    /// <summary>The most sessions this pool keeps.</summary>
    internal int Size { get; }

    /// <summary>How many sessions this pool has opened - each a runspace and a module import.</summary>
    internal int Opened => Volatile.Read(ref _opened);

    /// <summary>The production session: the default session state with the script execution policy bypassed, not yet open.</summary>
    internal static Runspace DefaultRunspace()
    {
        var iss = InitialSessionState.CreateDefault();
        iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
        return RunspaceFactory.CreateRunspace(iss);
    }

    /// <summary>
    /// A session for one check, warm when one is free: dispose it when the check's reads are done. Throws
    /// <see cref="TimeoutException"/> when none frees within <paramref name="wait"/>, and whatever opening a new one threw.
    /// </summary>
    internal DirectorySession Lease(TimeSpan wait)
    {
        if (!_slots.Wait(wait))
            throw new TimeoutException($"No directory session was free within {wait.TotalSeconds:0} seconds.");

        try
        {
            Runspace? runspace = null;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                while (_idle.Count > 0 && runspace is null)
                {
                    var candidate = _idle.Pop();
                    if (candidate.RunspaceStateInfo.State == RunspaceState.Opened)
                        runspace = candidate;
                    else
                        candidate.Dispose();
                }
            }

            return new DirectorySession(this, runspace ?? Open());
        }
        catch
        {
            _slots.Release();
            throw;
        }
    }

    private Runspace Open()
    {
        var runspace = _createRunspace();
        try
        {
            runspace.Open();
            using (var ps = PowerShell.Create())
            {
                ps.Runspace = runspace;
                ActiveDirectoryModuleImport.Import(ps);
            }
            Interlocked.Increment(ref _opened);
            return runspace;
        }
        catch
        {
            runspace.Dispose();
            throw;
        }
    }

    private void Return(Runspace runspace, bool keep)
    {
        try
        {
            lock (_gate)
            {
                if (keep && !_disposed && runspace.RunspaceStateInfo.State == RunspaceState.Opened)
                {
                    _idle.Push(runspace);
                    return;
                }
            }
            runspace.Dispose();
        }
        finally
        {
            _slots.Release();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            while (_idle.Count > 0)
                _idle.Pop().Dispose();
        }
    }

    /// <summary>One check's lease of a warm session.</summary>
    internal sealed class DirectorySession : IDisposable
    {
        private readonly DirectorySessionPool _pool;
        private readonly Runspace _runspace;
        private bool _failed;
        private int _returned;

        internal DirectorySession(DirectorySessionPool pool, Runspace runspace)
        {
            _pool = pool;
            _runspace = runspace;
            PowerShell = PowerShell.Create();
            PowerShell.Runspace = runspace;
        }

        /// <summary>The check's pipeline over the warm runspace: its commands and errors are its own.</summary>
        internal PowerShell PowerShell { get; }

        /// <summary>A read failed in this session: it is thrown away when the check is done, never lent again.</summary>
        internal void Fail() => _failed = true;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _returned, 1) != 0)
                return;
            PowerShell.Dispose();
            _pool.Return(_runspace, keep: !_failed);
        }
    }
}
