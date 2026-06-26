using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Covers the dead-connection auto-retry orchestration (CR-BUG-1, docs/ExoDeadConnectionRetry-Plan.md).
/// The pool hands out a runspace whose EXO session EXO tore down inside the idle window; the first
/// cmdlet throws "you must call Connect-ExchangeOnline". Eligible (read-only / single-write)
/// operations must discard that dead connection and run once more on a fresh borrow; multi-write
/// operations must NOT retry (a re-run would repeat an already-committed write).
///
/// Review #2 (2026-06-26) narrowed the RETRY trigger: retry fires only on the pre-cmdlet signature
/// (proves the cmdlet never ran), NOT on any connection error. A session that drops mid/after a
/// cmdlet still DISCARDS but must not replay a possibly-committed single write. So discard is gated
/// by the broad IsConnectionError and retry by the narrow IsRetriablePrecheckError.
///
/// These exercise <see cref="ExoConnectionPool.RunWithRetryCoreAsync{T}"/>, the pure orchestration
/// core, with fake borrow/return/discard delegates — the real pool needs a live EXO connection and
/// cannot be unit-hosted. The core owns the borrow↔return/discard pairing and the retry decision,
/// which is exactly the logic under test.
/// </summary>
public class ExoConnectionPoolRetryTests
{
    // The narrow pre-cmdlet signature: dead session detected BEFORE any cmdlet ran. Retriable.
    private static InvalidOperationException DeadSession() =>
        new("Exception calling \"GetCurrentConnectionContext\" with \"1\" argument(s): " +
            "\"You must call Connect-ExchangeOnline before calling any other cmdlet.\"");

    // A connection error that is NOT the pre-cmdlet signature: e.g. the session drops mid-flight.
    // Broad classifier discards it, but it must NOT trigger a replay.
    private static InvalidOperationException MidFlightConnectionDrop() =>
        new("The remote session was unexpectedly closed: the connection was aborted.");

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
            var pr = MakeRunspace(id);
            _live.Add(pr);
            return Task.FromResult(pr);
        }

        public void Return(PooledRunspace pr) { Returns++; ReturnedIds.Add(IdOf(pr)); }
        public void Discard(PooledRunspace pr) { Discards++; DiscardedIds.Add(IdOf(pr)); }

        private int IdOf(PooledRunspace pr) => _live.IndexOf(pr);

        private static PooledRunspace MakeRunspace(int gen)
        {
            var rs = System.Management.Automation.Runspaces.RunspaceFactory.CreateRunspace();
            var ps = System.Management.Automation.PowerShell.Create();
            return new PooledRunspace(rs, ps, gen);
        }
    }

    private static Task<T> Run<T>(
        FakePool pool,
        Func<PooledRunspace, Task<PooledOutcome<T>>> run,
        bool allowRetry,
        PoolFailurePolicy policy = PoolFailurePolicy.Return)
        => ExoConnectionPool.RunWithRetryCoreAsync(
            pool.Borrow, pool.Return, pool.Discard, run,
            ExoConnectionPool.IsConnectionError,
            ExoConnectionPool.IsRetriablePrecheckError,
            allowRetry, policy);

    [Fact]
    public async Task EligibleOp_PrecheckDeadSessionThenSuccess_RetriesOnFreshBorrow()
    {
        var pool = new FakePool();
        var attempts = 0;

        var result = await Run<string>(pool, _ =>
        {
            attempts++;
            if (attempts == 1) throw DeadSession();
            return Task.FromResult(new PooledOutcome<string>("ok", false));
        }, allowRetry: true);

        Assert.Equal("ok", result);
        Assert.Equal(2, attempts);
        Assert.Equal(2, pool.Borrows);
        Assert.Equal(1, pool.Discards);
        Assert.Equal(1, pool.Returns);
        Assert.Equal(0, pool.DiscardedIds[0]); // first (dead) connection discarded
        Assert.Equal(1, pool.ReturnedIds[0]);  // second (fresh) connection returned
        Assert.Equal(pool.Borrows, pool.Returns + pool.Discards); // slot conservation
    }

    [Fact]
    public async Task EligibleOp_PrecheckDeadSessionTwice_SurfacesFailure_NoThirdAttempt()
    {
        var pool = new FakePool();
        var attempts = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run<string>(pool, _ => { attempts++; throw DeadSession(); }, allowRetry: true));

        Assert.Equal(2, attempts);       // exactly one retry, no third
        Assert.Equal(2, pool.Borrows);
        Assert.Equal(2, pool.Discards);
        Assert.Equal(0, pool.Returns);
        Assert.Equal(pool.Borrows, pool.Returns + pool.Discards);
    }

    [Fact]
    public async Task EligibleOp_MidFlightConnectionDrop_DiscardsButDoesNotRetry()
    {
        // THE review-#2 case: a connection error that is NOT the pre-cmdlet signature. The single
        // write may have committed, so we must discard (don't reuse a dead session) but NOT replay.
        var pool = new FakePool();
        var attempts = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run<string>(pool, _ => { attempts++; throw MidFlightConnectionDrop(); }, allowRetry: true));

        Assert.Equal(1, attempts);       // NO retry despite being eligible — error isn't pre-check
        Assert.Equal(1, pool.Borrows);
        Assert.Equal(1, pool.Discards);  // still discarded (broad connection error)
        Assert.Equal(0, pool.Returns);
        Assert.Equal(pool.Borrows, pool.Returns + pool.Discards);
    }

    [Fact]
    public async Task NonEligibleOp_PrecheckDeadSession_DiscardsButDoesNotRetry()
    {
        var pool = new FakePool();
        var attempts = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run<string>(pool, _ => { attempts++; throw DeadSession(); }, allowRetry: false));

        Assert.Equal(1, attempts);       // multi-write op: not eligible, no retry
        Assert.Equal(1, pool.Borrows);
        Assert.Equal(1, pool.Discards);  // dead connection still discarded (self-heal for next time)
        Assert.Equal(0, pool.Returns);
    }

    [Fact]
    public async Task NonConnectionError_ThrowsWithoutRetry_ConnectionReturned()
    {
        var pool = new FakePool();
        var attempts = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run<string>(pool, _ => { attempts++; throw BusinessError(); }, allowRetry: true));

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
            Run<string>(pool, _ => throw BusinessError(), allowRetry: true, PoolFailurePolicy.Discard));

        Assert.Equal(1, pool.Borrows);
        Assert.Equal(1, pool.Discards);  // can't guarantee clean pipeline -> discard
        Assert.Equal(0, pool.Returns);
    }

    [Fact]
    public async Task EligibleOp_PrecheckFailureViaTracker_RetriesAndSucceeds()
    {
        // RunAsync swallows the throw and signals the dead session via the returned outcome's
        // flags instead of throwing. RetriablePrecheck=true means it was the pre-cmdlet signature.
        var pool = new FakePool();
        var attempts = 0;

        var result = await Run<string>(pool, _ =>
        {
            attempts++;
            return Task.FromResult(attempts == 1
                ? new PooledOutcome<string>("failed", ConnectionFailure: true, RetriablePrecheck: true)
                : new PooledOutcome<string>("ok", false));
        }, allowRetry: true);

        Assert.Equal("ok", result);
        Assert.Equal(2, attempts);
        Assert.Equal(1, pool.Discards);
        Assert.Equal(1, pool.Returns);
    }

    [Fact]
    public async Task EligibleOp_MidFlightFailureViaTracker_DiscardsNoRetry()
    {
        // Connection failure flagged via tracker, but NOT the pre-cmdlet signature: discard, no replay.
        var pool = new FakePool();
        var attempts = 0;

        var result = await Run<string>(pool, _ =>
        {
            attempts++;
            return Task.FromResult(new PooledOutcome<string>("failed", ConnectionFailure: true, RetriablePrecheck: false));
        }, allowRetry: true);

        Assert.Equal("failed", result);  // surfaced as-is
        Assert.Equal(1, attempts);       // NO retry
        Assert.Equal(1, pool.Discards);  // discarded
        Assert.Equal(0, pool.Returns);
    }

    [Fact]
    public async Task NonEligibleOp_PrecheckFailureViaTracker_ReturnsResultNoRetry()
    {
        var pool = new FakePool();
        var attempts = 0;

        var result = await Run<string>(pool, _ =>
        {
            attempts++;
            return Task.FromResult(new PooledOutcome<string>("failed", ConnectionFailure: true, RetriablePrecheck: true));
        }, allowRetry: false);

        Assert.Equal("failed", result);
        Assert.Equal(1, attempts);       // not eligible, no retry
        Assert.Equal(1, pool.Discards);
        Assert.Equal(0, pool.Returns);
    }

    [Fact]
    public async Task HealthyOp_SingleBorrowReturned_NoDiscard()
    {
        var pool = new FakePool();

        var result = await Run<string>(pool,
            _ => Task.FromResult(new PooledOutcome<string>("ok", false)), allowRetry: true);

        Assert.Equal("ok", result);
        Assert.Equal(1, pool.Borrows);
        Assert.Equal(1, pool.Returns);
        Assert.Equal(0, pool.Discards);
    }

    [Fact]
    public void Classifiers_DiscardIsBroad_RetryIsNarrow()
    {
        // Discard (broad): pre-check AND mid-flight AND generic connection errors.
        Assert.True(ExoConnectionPool.IsConnectionError(DeadSession()));
        Assert.True(ExoConnectionPool.IsConnectionError(MidFlightConnectionDrop()));
        Assert.True(ExoConnectionPool.IsConnectionError(
            new InvalidOperationException("The runspace is not available.")));
        Assert.False(ExoConnectionPool.IsConnectionError(BusinessError()));
        Assert.False(ExoConnectionPool.IsConnectionError(null));

        // Retry (narrow): ONLY the pre-cmdlet signature. Crucially NOT the mid-flight drop.
        Assert.True(ExoConnectionPool.IsRetriablePrecheckError(DeadSession()));
        Assert.False(ExoConnectionPool.IsRetriablePrecheckError(MidFlightConnectionDrop()));
        Assert.False(ExoConnectionPool.IsRetriablePrecheckError(
            new InvalidOperationException("The runspace is not available.")));
        Assert.False(ExoConnectionPool.IsRetriablePrecheckError(BusinessError()));
        Assert.False(ExoConnectionPool.IsRetriablePrecheckError(null));

        // Every retriable error is also a connection error (retry implies discard).
        Assert.True(ExoConnectionPool.IsConnectionError(DeadSession())
            && ExoConnectionPool.IsRetriablePrecheckError(DeadSession()));
    }
}
