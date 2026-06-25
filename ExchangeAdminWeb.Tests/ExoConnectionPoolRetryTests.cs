using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Covers the dead-connection auto-retry orchestration (CR-BUG-1, docs/ExoDeadConnectionRetry-Plan.md).
/// The pool hands out a runspace whose EXO session EXO tore down inside the idle window; the first
/// cmdlet throws "you must call Connect-ExchangeOnline". Eligible (read-only / single-write)
/// operations must discard that dead connection and run once more on a fresh borrow; multi-write
/// operations must NOT retry (a re-run would repeat an already-committed write).
///
/// These exercise <see cref="ExoConnectionPool.RunWithRetryCoreAsync{T}"/>, the pure orchestration
/// core, with fake borrow/return/discard delegates — the real pool needs a live EXO connection and
/// cannot be unit-hosted. The core owns the borrow↔return/discard pairing and the retry decision,
/// which is exactly the logic under test.
/// </summary>
public class ExoConnectionPoolRetryTests
{
    // A connection error matches ExoConnectionPool.IsConnectionError. This is the real observed text.
    private static InvalidOperationException DeadSession() =>
        new("Exception calling \"GetCurrentConnectionContext\" with \"1\" argument(s): " +
            "\"You must call Connect-ExchangeOnline before calling any other cmdlet.\"");

    private static InvalidOperationException BusinessError() =>
        new("The mailbox 'x' couldn't be found.");

    /// <summary>Tracks borrow/return/discard so tests can assert the connection lifecycle.</summary>
    private sealed class FakePool
    {
        public int Borrows;
        public int Returns;
        public int Discards;
        public readonly List<int> BorrowedIds = new();
        public readonly List<int> ReturnedIds = new();
        public readonly List<int> DiscardedIds = new();

        // Each borrow yields a distinct PooledRunspace so we can prove a *fresh* one was used on retry.
        private int _nextId;
        private readonly List<PooledRunspace> _live = new();

        public Task<PooledRunspace> Borrow()
        {
            Borrows++;
            var id = _nextId++;
            BorrowedIds.Add(id);
            // The runspace internals are irrelevant here; identity is tracked via list position.
            var pr = MakeRunspace(id);
            _live.Add(pr);
            return Task.FromResult(pr);
        }

        public void Return(PooledRunspace pr) { Returns++; ReturnedIds.Add(IdOf(pr)); }
        public void Discard(PooledRunspace pr) { Discards++; DiscardedIds.Add(IdOf(pr)); }

        private int IdOf(PooledRunspace pr) => _live.IndexOf(pr);

        // PooledRunspace needs a Runspace + PowerShell; we never invoke on them in these tests, so
        // a defaulted instance via the public ctor is enough. Build the cheapest valid instance.
        private static PooledRunspace MakeRunspace(int gen)
        {
            var rs = System.Management.Automation.Runspaces.RunspaceFactory.CreateRunspace();
            var ps = System.Management.Automation.PowerShell.Create();
            return new PooledRunspace(rs, ps, gen);
        }
    }

    [Fact]
    public async Task EligibleOp_DeadSessionThenSuccess_RetriesOnFreshBorrow()
    {
        var pool = new FakePool();
        var attempts = 0;

        var result = await ExoConnectionPool.RunWithRetryCoreAsync<string>(
            pool.Borrow, pool.Return, pool.Discard,
            _ =>
            {
                attempts++;
                if (attempts == 1) throw DeadSession();
                return Task.FromResult(new PooledOutcome<string>("ok", false));
            },
            ExoConnectionPool.IsConnectionError,
            allowRetry: true,
            PoolFailurePolicy.Return);

        Assert.Equal("ok", result);
        Assert.Equal(2, attempts);            // ran twice
        Assert.Equal(2, pool.Borrows);        // on two separate borrows
        Assert.Equal(1, pool.Discards);       // the dead one was discarded
        Assert.Equal(1, pool.Returns);        // the healthy one returned
        Assert.Equal(0, pool.DiscardedIds[0]); // first (dead) connection discarded
        Assert.Equal(1, pool.ReturnedIds[0]);  // second (fresh) connection returned
        // Slot conservation: every borrow paired with exactly one return-or-discard.
        Assert.Equal(pool.Borrows, pool.Returns + pool.Discards);
    }

    [Fact]
    public async Task EligibleOp_DeadSessionTwice_SurfacesFailure_NoThirdAttempt()
    {
        var pool = new FakePool();
        var attempts = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExoConnectionPool.RunWithRetryCoreAsync<string>(
                pool.Borrow, pool.Return, pool.Discard,
                _ => { attempts++; throw DeadSession(); },
                ExoConnectionPool.IsConnectionError,
                allowRetry: true,
                PoolFailurePolicy.Return));

        Assert.Equal(2, attempts);       // exactly one retry, no third
        Assert.Equal(2, pool.Borrows);
        Assert.Equal(2, pool.Discards);  // both dead connections discarded
        Assert.Equal(0, pool.Returns);
        Assert.Equal(pool.Borrows, pool.Returns + pool.Discards);
    }

    [Fact]
    public async Task NonEligibleOp_DeadSession_DiscardsButDoesNotRetry()
    {
        var pool = new FakePool();
        var attempts = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExoConnectionPool.RunWithRetryCoreAsync<string>(
                pool.Borrow, pool.Return, pool.Discard,
                _ => { attempts++; throw DeadSession(); },
                ExoConnectionPool.IsConnectionError,
                allowRetry: false,   // multi-write op: NOT eligible
                PoolFailurePolicy.Return));

        Assert.Equal(1, attempts);       // single attempt, no retry
        Assert.Equal(1, pool.Borrows);
        Assert.Equal(1, pool.Discards);  // dead connection still discarded (self-heal for next time)
        Assert.Equal(0, pool.Returns);
        Assert.Equal(pool.Borrows, pool.Returns + pool.Discards);
    }

    [Fact]
    public async Task NonConnectionError_ThrowsWithoutRetry_ConnectionReturned()
    {
        var pool = new FakePool();
        var attempts = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExoConnectionPool.RunWithRetryCoreAsync<string>(
                pool.Borrow, pool.Return, pool.Discard,
                _ => { attempts++; throw BusinessError(); },
                ExoConnectionPool.IsConnectionError,
                allowRetry: true,    // eligible, but the error is not a connection error
                PoolFailurePolicy.Return));

        Assert.Equal(1, attempts);       // no retry for a business error
        Assert.Equal(1, pool.Borrows);
        Assert.Equal(1, pool.Returns);   // Return policy: clean pipeline, connection reusable
        Assert.Equal(0, pool.Discards);
    }

    [Fact]
    public async Task NonConnectionError_DiscardPolicy_DiscardsConnection()
    {
        var pool = new FakePool();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExoConnectionPool.RunWithRetryCoreAsync<string>(
                pool.Borrow, pool.Return, pool.Discard,
                _ => throw BusinessError(),
                ExoConnectionPool.IsConnectionError,
                allowRetry: true,
                PoolFailurePolicy.Discard));  // PermissionValidator policy

        Assert.Equal(1, pool.Borrows);
        Assert.Equal(1, pool.Discards);  // can't guarantee clean pipeline -> discard
        Assert.Equal(0, pool.Returns);
    }

    [Fact]
    public async Task EligibleOp_ConnectionFailureViaTracker_RetriesAndSucceeds()
    {
        // RunAsync swallows the throw and signals the dead session via the returned outcome's
        // ConnectionFailure flag instead of throwing. The orchestrator must treat that identically.
        var pool = new FakePool();
        var attempts = 0;

        var result = await ExoConnectionPool.RunWithRetryCoreAsync<string>(
            pool.Borrow, pool.Return, pool.Discard,
            _ =>
            {
                attempts++;
                return Task.FromResult(attempts == 1
                    ? new PooledOutcome<string>("failed", true)   // flagged dead session, returned not thrown
                    : new PooledOutcome<string>("ok", false));
            },
            ExoConnectionPool.IsConnectionError,
            allowRetry: true,
            PoolFailurePolicy.Return);

        Assert.Equal("ok", result);
        Assert.Equal(2, attempts);
        Assert.Equal(1, pool.Discards);  // first (flagged) discarded
        Assert.Equal(1, pool.Returns);   // second returned
    }

    [Fact]
    public async Task NonEligibleOp_ConnectionFailureViaTracker_ReturnsResultNoRetry()
    {
        var pool = new FakePool();
        var attempts = 0;

        var result = await ExoConnectionPool.RunWithRetryCoreAsync<string>(
            pool.Borrow, pool.Return, pool.Discard,
            _ => { attempts++; return Task.FromResult(new PooledOutcome<string>("failed", true)); },
            ExoConnectionPool.IsConnectionError,
            allowRetry: false,
            PoolFailurePolicy.Return);

        Assert.Equal("failed", result);  // surfaced as-is
        Assert.Equal(1, attempts);       // no retry
        Assert.Equal(1, pool.Discards);  // dead connection discarded
        Assert.Equal(0, pool.Returns);
    }

    [Fact]
    public async Task HealthyOp_SingleBorrowReturned_NoDiscard()
    {
        var pool = new FakePool();

        var result = await ExoConnectionPool.RunWithRetryCoreAsync<string>(
            pool.Borrow, pool.Return, pool.Discard,
            _ => Task.FromResult(new PooledOutcome<string>("ok", false)),
            ExoConnectionPool.IsConnectionError,
            allowRetry: true,
            PoolFailurePolicy.Return);

        Assert.Equal("ok", result);
        Assert.Equal(1, pool.Borrows);
        Assert.Equal(1, pool.Returns);
        Assert.Equal(0, pool.Discards);
    }

    [Fact]
    public void IsConnectionError_RecognizesDeadSessionSignature()
    {
        Assert.True(ExoConnectionPool.IsConnectionError(DeadSession()));
        Assert.True(ExoConnectionPool.IsConnectionError(
            new InvalidOperationException("The runspace is not available.")));
        Assert.False(ExoConnectionPool.IsConnectionError(BusinessError()));
        Assert.False(ExoConnectionPool.IsConnectionError(null));
    }
}
