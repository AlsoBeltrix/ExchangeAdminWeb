using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Audit finding A6B: <c>Disconnect-ExchangeOnline</c> with no selector routes to
/// <c>ConnectionContextFactory.RemoveAllConnectionContexts()</c>, and that map is a
/// <b>static</b> field - verified against the installed ExchangeOnlineManagement 3.9.2 by
/// metadata read. So retiring one pooled runspace tore down every other borrower's live
/// connection, mid-operation.
/// </summary>
/// <remarks>
/// Split deliberately. The id-attribution rule is a pure function and is tested as behaviour.
/// The disconnect call itself cannot be: it needs a real module, a real tenant and a real
/// second connection to observe the damage, none of which exist here - so that half is a
/// source scan and is labelled as one rather than dressed up.
/// </remarks>
public class ExoConnectionDisconnectScopeTests
{
    // ---- the attribution rule, as behaviour ---------------------------------------------------

    [Fact]
    public void TheIdThatAppearedIsTheNewConnectionsId()
    {
        Assert.Equal("c2", ExoConnectionPool.NewConnectionId(["c1"], ["c1", "c2"]));
    }

    [Fact]
    public void AFirstConnectionIsIdentifiedToo()
    {
        Assert.Equal("c1", ExoConnectionPool.NewConnectionId([], ["c1"]));
    }

    [Theory]
    [InlineData(new string[0], new string[0])]                       // connect produced nothing
    [InlineData(new[] { "c1" }, new[] { "c1" })]                     // nothing appeared
    [InlineData(new[] { "c1" }, new[] { "c1", "c2", "c3" })]         // two appeared: ambiguous
    [InlineData(new[] { "c1", "c2" }, new[] { "c2" })]               // one vanished, none added
    public void AnythingOtherThanExactlyOneNewIdIsRefused(string[] before, string[] after)
    {
        // Null is the caller's instruction never to disconnect this runspace. Guessing here
        // would mean disconnecting a connection someone else is using, or falling back to the
        // selector that disconnects everybody - which is the defect.
        Assert.Null(ExoConnectionPool.NewConnectionId(before, after));
    }

    [Fact]
    public void AnIdThatOnlyDiffersInCaseIsNotANewConnection()
    {
        Assert.Null(ExoConnectionPool.NewConnectionId(["C1"], ["c1"]));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void AnUnreadableSnapshotIsNeverTreatedAsAnEmptyOne(bool beforeFailed, bool afterFailed)
    {
        // Review of the first A6B fix. ReadConnectionIds used to return an empty list when it
        // could not look, so "I saw nothing" and "I could not see" were the same answer - and a
        // readable connection silently became an unattributable one. Null now means unreadable,
        // and no difference can be taken from a blind spot.
        var before = beforeFailed ? null : new List<string> { "c1" };
        var after = afterFailed ? null : new List<string> { "c1", "c2" };

        Assert.Null(ExoConnectionPool.NewConnectionId(before, after));
    }

    // ---- serialisation, as a source scan -----------------------------------------------------

    /// <summary>
    /// Comments blanked, string literals KEPT - the cmdlet name under test is a literal, so
    /// <c>ProgressScan.CodeView</c> would blank the very thing being checked, and the comments
    /// in that file name the cmdlet repeatedly.
    /// </summary>
    private static string PoolCode() =>
        RazorSyntax.CommentsBlanked(File.ReadAllText(
            AuditCategoryFilingTests.FindRepoFile("Services", "ExoConnectionPool.cs")));

    [Fact]
    public void EstablishmentIsSerialisedBehindABoundedCancellableGate()
    {
        // This one stays a scan: proving two connects do not overlap needs two real connects.
        // What it pins is the shape review found wrong the first time - a bare lock, which has
        // no timeout and no cancellation, so one stalled establishment could hold all five
        // slots shut with no way out.
        var code = PoolCode();

        Assert.DoesNotContain("lock (_connectLock)", code, StringComparison.Ordinal);

        var gate = code.IndexOf("_connectGate.Wait(ConnectGateTimeout, cancellationToken)", StringComparison.Ordinal);
        Assert.True(gate > 0, "Establishment must wait on a bounded, cancellable gate.");

        var connect = code.IndexOf("\"Connect-ExchangeOnline\"", gate, StringComparison.Ordinal);
        Assert.True(connect > gate, "Connect-ExchangeOnline must run after the gate is taken.");

        Assert.Contains("_connectGate.Release();", code, StringComparison.Ordinal);
    }
}
