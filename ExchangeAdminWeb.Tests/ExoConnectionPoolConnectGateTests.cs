using System.Collections;
using System.Management.Automation.Runspaces;
using System.Text.Json;
using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Finding kd-91: a borrow queued at the connect gate looks in the pool again once it holds the gate, so a connection
/// returned while it waited is lent instead of a new one being made. On dev, 2026-10-30, four overlapping autocomplete
/// borrows found the pool empty but for one; the first took it and the other three connected one after another, 9.8, 16.7
/// and 25.5 s after asking, while connections came back - the k-th of k cold borrowers waited for k connects.
/// </summary>
/// <remarks>
/// The pool's own connect step over a session of stand-in Import-Module, Get-ConnectionInformation, Connect-ExchangeOnline
/// - which says it has started and then waits on the test - and Disconnect-ExchangeOnline: the psl-9 seams, as
/// <see cref="ExoConnectionPoolCancellationTests"/> uses them. The certificate is read just before the gate, so a third
/// thumbprint read means the third borrow has looked in the pool, found it empty, and queued. Every wait is bounded. No
/// Exchange module, certificate or tenant.
/// </remarks>
public sealed class ExoConnectionPoolConnectGateTests : IDisposable
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    private readonly ExchangeWriteHarness _h = new();
    private readonly ArrayList _connectCalls = ArrayList.Synchronized(new ArrayList());
    private readonly ArrayList _connectIds = ArrayList.Synchronized(new ArrayList());
    private readonly ManualResetEventSlim _connectEntered = new(false);
    private readonly ManualResetEventSlim _connectRelease = new(true);
    private readonly List<ExoConnectionPool> _pools = [];
    private int _thumbprints;

    public void Dispose()
    {
        _connectRelease.Set();
        foreach (var pool in _pools)
            pool.Dispose();
        _h.Dispose();
        _connectEntered.Dispose();
        _connectRelease.Dispose();
    }

    private int Connects => _connectCalls.Cast<string>().Count(call => call == "Connect-ExchangeOnline");

    // A holds the one connection; B is connecting, its Connect-ExchangeOnline held by the test; C has found the pool empty and
    // queued behind B. A comes back while C waits, and B's connect is let finish.
    [Fact]
    public async Task ABorrowQueuedAtTheConnectGate_IsLentAConnectionReturnedWhileItWaited_AndConnectsNothing()
    {
        var pool = ConnectStepPool();
        var (a, b, c) = await ThreeColdBorrows(pool);

        pool.Return(a);
        Assert.Equal(1, pool.AvailableCount);
        _connectRelease.Set();
        var connected = await b.WaitAsync(Bound, TestContext.Current.CancellationToken);
        var lent = await c.WaitAsync(Bound, TestContext.Current.CancellationToken);

        Assert.Equal("c-1", connected.ConnectionId);
        Assert.Same(a, lent); // A's connection, lent to C - not a third connect
        Assert.Equal("c-0", lent.ConnectionId);
        Assert.Equal(2, Connects);
        Assert.Equal(0, pool.AvailableCount);
        Assert.Equal(["Pool after the connect gate"], BorrowedFromThePool());
        Assert.DoesNotContain(_connectCalls.Cast<string>(), call => call.StartsWith("Disconnect-ExchangeOnline", StringComparison.Ordinal));

        pool.Return(connected);
        pool.Return(lent);
        Assert.Equal(5, pool.FreeSlots);
        Assert.Equal(2, pool.AvailableCount);
    }

    // The same, but the connection that came back has sat idle past the pool's timeout: the second look takes it, cannot lend
    // it, and destroys it - by its own id - and C connects. Nothing stale is left in the pool, and nothing stale is lent.
    [Fact]
    public async Task AConnectionTooOldToLend_FoundAtTheConnectGate_IsDisconnectedByItsOwnId_AndTheBorrowConnects()
    {
        var pool = ConnectStepPool();
        var (a, b, c) = await ThreeColdBorrows(pool);

        pool.Return(a);
        a.LastUsed = DateTime.UtcNow - TimeSpan.FromHours(1);
        _connectRelease.Set();
        var connected = await b.WaitAsync(Bound, TestContext.Current.CancellationToken);
        var made = await c.WaitAsync(Bound, TestContext.Current.CancellationToken);

        Assert.NotSame(a, made);
        Assert.Equal("c-2", made.ConnectionId);
        Assert.Equal(3, Connects);
        Assert.Contains("Disconnect-ExchangeOnline c-0", _connectCalls.Cast<string>());
        Assert.Equal(0, pool.AvailableCount);
        Assert.Empty(BorrowedFromThePool());

        pool.Return(connected);
        pool.Return(made);
        Assert.Equal(5, pool.FreeSlots);
        Assert.Equal(2, pool.AvailableCount);
    }

    // A: a cold borrow, connected (c-0) and held. B: the next, inside its Connect-ExchangeOnline, holding the gate. C: the
    // third, past its look in the pool and queued at the gate.
    private async Task<(PooledRunspace A, Task<PooledRunspace> B, Task<PooledRunspace> C)> ThreeColdBorrows(ExoConnectionPool pool)
    {
        var ct = TestContext.Current.CancellationToken;
        var a = await pool.BorrowAsync(ct);
        Assert.Equal("c-0", a.ConnectionId);

        _connectEntered.Reset();
        _connectRelease.Reset();
        var b = pool.BorrowAsync(ct);
        Assert.True(_connectEntered.Wait(Bound, ct), "B never reached Connect-ExchangeOnline.");

        var c = pool.BorrowAsync(ct);
        await WaitUntil(() => Volatile.Read(ref _thumbprints) == 3, "C reaches the connect gate");
        Assert.False(c.IsCompleted, "C must be waiting at the gate behind B's connect.");
        Assert.Equal(0, pool.AvailableCount);
        return (a, b, c);
    }

    private ExoConnectionPool ConnectStepPool()
    {
        var pool = new ExoConnectionPool(_h.Config, _h.ModuleConfig, _h.Enablement, NullLogger<ExoConnectionPool>.Instance, _h.Trace)
        {
            ConnectRunspace = ConnectSession,
            CertificateThumbprint = _ =>
            {
                Interlocked.Increment(ref _thumbprints);
                return "0123456789ABCDEF";
            },
        };
        _pools.Add(pool);
        return pool;
    }

    private Runspace ConnectSession()
    {
        var state = InitialSessionState.CreateDefault2();
        state.Variables.Add(new SessionStateVariableEntry("ConnectCalls", _connectCalls, null));
        state.Variables.Add(new SessionStateVariableEntry("ConnectIds", _connectIds, null));
        state.Variables.Add(new SessionStateVariableEntry("ConnectEntered", _connectEntered, null));
        state.Variables.Add(new SessionStateVariableEntry("ConnectRelease", _connectRelease, null));
        state.Commands.Add(new SessionStateFunctionEntry("Import-Module", """
            [CmdletBinding()] param([string] $Name)
            [void]$ConnectCalls.Add("Import-Module $Name")
            """));
        state.Commands.Add(new SessionStateFunctionEntry("Get-ConnectionInformation", """
            [CmdletBinding()] param()
            foreach ($id in @($ConnectIds.ToArray())) { [pscustomobject]@{ ConnectionId = $id } }
            """));
        state.Commands.Add(new SessionStateFunctionEntry("Connect-ExchangeOnline", """
            [CmdletBinding()] param($AppId, $CertificateThumbprint, $Organization, $ShowBanner)
            [void]$ConnectCalls.Add('Connect-ExchangeOnline')
            [void]$ConnectEntered.Set()
            [void]$ConnectRelease.Wait(60000)
            [void]$ConnectIds.Add('c-' + $ConnectIds.Count)
            """));
        state.Commands.Add(new SessionStateFunctionEntry("Disconnect-ExchangeOnline", """
            [CmdletBinding()] param($ConnectionId, $Confirm)
            [void]$ConnectCalls.Add("Disconnect-ExchangeOnline $ConnectionId")
            """));
        return RunspaceFactory.CreateRunspace(state);
    }

    /// <summary>
    /// Every borrow the pool's trace records as lent from the pool, by where it looked: "Pool" before the connect gate, "Pool
    /// after the connect gate" for the second look. A line still being written is skipped.
    /// </summary>
    private List<string> BorrowedFromThePool()
    {
        var sources = new List<string>();
        foreach (var path in _h.Jsonl.GetTraceLogPaths(DateTime.Now).Where(File.Exists))
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                try
                {
                    using var step = JsonDocument.Parse(line);
                    var root = step.RootElement;
                    if (root.TryGetProperty("stage", out var stage) && stage.GetString() == "ExoConnectionBorrowed"
                        && root.TryGetProperty("details", out var details)
                        && details.TryGetProperty("source", out var source))
                    {
                        sources.Add(source.GetString() switch
                        {
                            "PoolAfterConnectGate" => "Pool after the connect gate",
                            var other => other ?? "",
                        });
                    }
                }
                catch (JsonException)
                {
                    continue;
                }
            }
        }
        return sources;
    }

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Bound;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting until {what}.");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }
}
