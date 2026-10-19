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

    // ---- the disconnect call, as a source scan ------------------------------------------------

    /// <summary>
    /// Comments blanked, string literals KEPT - the cmdlet names and parameter names being
    /// checked here are literals, so <c>ProgressScan.CodeView</c> would blank the very thing
    /// under test. The comments in this file name both cmdlets repeatedly and would otherwise
    /// satisfy every scan below on their own.
    /// </summary>
    private static string PoolCode() =>
        RazorSyntax.CommentsBlanked(File.ReadAllText(
            AuditCategoryFilingTests.FindRepoFile("Services", "ExoConnectionPool.cs")));

    [Fact]
    public void TheDisconnectAlwaysNamesTheConnectionItIsClosing()
    {
        // The unselected form is the defect. There must be no Disconnect-ExchangeOnline anywhere
        // in the pool that is not immediately followed by a ConnectionId parameter.
        var code = PoolCode();

        var at = code.IndexOf("Disconnect-ExchangeOnline", StringComparison.Ordinal);
        Assert.True(at > 0, "The pool no longer disconnects at all - this guard is stale.");

        while (at > 0)
        {
            // The AddParameter chain for one command, bounded by the Invoke that runs it.
            var invoke = code.IndexOf("Invoke()", at, StringComparison.Ordinal);
            Assert.True(invoke > at, "A Disconnect-ExchangeOnline with no Invoke after it.");

            Assert.Contains("\"ConnectionId\"", code[at..invoke], StringComparison.Ordinal);

            at = code.IndexOf("Disconnect-ExchangeOnline", at + 1, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ARunspaceWithNoKnownConnectionIdIsNeverDisconnected()
    {
        // Leaking one server session until it times out is recoverable. Disconnecting every
        // other operator is not, so the unknown case must bail out before the command is built.
        var code = PoolCode();

        var guard = code.IndexOf("if (string.IsNullOrWhiteSpace(pooled.ConnectionId))", StringComparison.Ordinal);
        Assert.True(guard > 0, "DestroyRunspace must refuse to disconnect without an id.");

        var disconnect = code.IndexOf("Disconnect-ExchangeOnline", guard, StringComparison.Ordinal);
        Assert.True(disconnect > guard, "The guard must precede the disconnect it protects.");
    }

    [Fact]
    public void ConnectionEstablishmentIsSerialised()
    {
        // The id is only identifiable as "the one that appeared", so two concurrent connects
        // make the comparison ambiguous and NewConnectionId would correctly refuse both - the
        // pool would then never be able to disconnect anything.
        var code = PoolCode();

        var lockAt = code.IndexOf("lock (_connectLock)", StringComparison.Ordinal);
        Assert.True(lockAt > 0, "Connection establishment must be serialised.");

        var connect = code.IndexOf("\"Connect-ExchangeOnline\"", lockAt, StringComparison.Ordinal);
        Assert.True(connect > lockAt, "Connect-ExchangeOnline must run inside that lock.");
    }
}
