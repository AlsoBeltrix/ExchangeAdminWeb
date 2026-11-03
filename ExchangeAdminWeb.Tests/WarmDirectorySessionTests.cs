using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// kd-88: a warm directory session. Every protected-group check built a runspace and imported the ActiveDirectory module
/// before its first read, and the next check did it all again. The check now borrows a session from
/// <see cref="ADDirectorySearchService"/>'s warm sessions for checks - its cached-runspace rule, kept apart from the searches'
/// own runspace - and gives it back. The sessions here are the stand-in forest's; nothing reaches a directory.
/// </summary>
public sealed class WarmDirectorySessionTests : IDisposable
{
    private readonly ExchangeWriteHarness _h = new();
    private readonly SelfGrantDirectoryStandIn.Entry _tess;
    private readonly SelfGrantDirectoryStandIn.Entry _vip;

    public WarmDirectorySessionTests()
    {
        _tess = _h.Forest.AddUser("tess", upn: "tess@contoso.test", mail: "tess@contoso.test");
        _h.Forest.AddGroup("Team", members: [_tess]);
        _vip = _h.Forest.AddGroup("VIP Staff");
    }

    public void Dispose() => _h.Dispose();

    private ResolvedDirectoryPrincipal Tess => new(
        "Test", "Tess", "tess@contoso.test", "tess", "tess@contoso.test", _tess.Dn, _tess.Guid.ToString(), null);

    private (ProtectedPrincipalService Service, ADDirectorySearchService Search) Service()
    {
        var search = new ADDirectorySearchService(NullLogger<ADDirectorySearchService>.Instance) { CreateCheckRunspace = _h.Forest.Open };
        _h.ProtectedValidator(ExchangeWriteHarness.ExchangeKnows("nobody@contoso.test", "nobody@contoso.test", Guid.NewGuid()),
            new ProtectedPrincipalConfig { Groups = [_vip.Dn] }, search);
        return (_h.ProtectedPrincipals!, search);
    }

    private int ModuleImports => _h.Forest.Calls.Cast<string>().Count(call => call == "Import-Module ActiveDirectory");

    [Fact]
    public async Task TwoChecks_BorrowOneWarmSession_FromTheDirectorySearchService()
    {
        var (service, search) = Service();

        var first = await service.CheckAsync(Tess);
        var second = await service.CheckAsync(Tess);

        Assert.False(first.CheckFailed, first.Reason);
        Assert.False(second.CheckFailed, second.Reason);
        Assert.False(first.IsProtected);
        Assert.Equal(1, _h.Forest.RunspacesHandedOut);
        Assert.Equal(1, ModuleImports);
        Assert.Equal(1, search.CheckSessions.Opened);
        Assert.Empty(_h.Forest.Errors);
    }

    [Fact]
    public async Task ASessionAReadFailedIn_IsNotLentAgain()
    {
        // The GetOrCreateRunspace rule: a session a failure may have broken is thrown away, and the next check builds a new one.
        var (service, search) = Service();
        _h.Forest.Failures["Get-ADObject " + SelfGrantDirectoryStandIn.TokenGroups] = "The server is not operational.";

        Assert.True((await service.CheckAsync(Tess)).CheckFailed);
        _h.Forest.Failures.Clear();
        Assert.False((await service.CheckAsync(Tess)).CheckFailed);

        Assert.Equal(2, _h.Forest.RunspacesHandedOut);
        Assert.Equal(2, search.CheckSessions.Opened);
    }

    [Fact]
    public async Task ChecksRunAtOnce_EachHaveASessionOfTheirOwn_AndLeaveThemWarm()
    {
        var (service, search) = Service();

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => service.CheckAsync(Tess), TestContext.Current.CancellationToken)));

        Assert.All(results, result => Assert.False(result.CheckFailed, result.Reason));
        // At most as many sessions as ran at once - the service lets two through - and never more than the pool keeps.
        Assert.InRange(search.CheckSessions.Opened, 1, 2);
        Assert.Equal(search.CheckSessions.Opened, _h.Forest.RunspacesHandedOut);
    }

    [Fact]
    public async Task TwoResolutions_BorrowOneWarmSession_AndAFailedLookupThrowsItsSessionAway()
    {
        // The sweep, S1: the resolution's lookup opened a runspace and imported the module for every identity it resolved.
        var (service, search) = Service();

        Assert.Equal(ProtectedPrincipalService.ResolutionStatus.Resolved, (await service.ResolveWithStatusAsync("tess@contoso.test")).status);
        Assert.Equal(ProtectedPrincipalService.ResolutionStatus.Resolved, (await service.ResolveWithStatusAsync("tess")).status);
        Assert.Equal(1, _h.Forest.RunspacesHandedOut);
        Assert.Equal(1, ModuleImports);

        _h.Forest.Failures["Get-ADUser"] = "The server is not operational.";
        Assert.Equal(ProtectedPrincipalService.ResolutionStatus.Unavailable, (await service.ResolveWithStatusAsync("tess")).status);
        _h.Forest.Failures.Clear();
        Assert.Equal(ProtectedPrincipalService.ResolutionStatus.Resolved, (await service.ResolveWithStatusAsync("tess")).status);
        Assert.Equal(2, search.CheckSessions.Opened);
    }

    [Fact]
    public async Task TwoReadsBySid_BorrowOneWarmSession_AndDiscoverTheCatalogOnce()
    {
        // The sweep, S2, for the read that replaced the mailbox proof (docs/ProtectedPrincipalIdentifiers-Plan.md C.2 step 4):
        // the account by its SID at the global catalog, in a lent session, the catalog discovered once and kept.
        var sid = $"{SelfGrantDirectoryStandIn.DomainSid}-3996";
        _h.Forest.Add(new SelfGrantDirectoryStandIn.Entry
        {
            Dn = $"CN=room,OU=Staff,{SelfGrantDirectoryStandIn.DomainDn}",
            Class = "user",
            Sam = "room",
            Sid = sid,
        });
        var (service, search) = Service();

        Assert.Single((await service.FindObjectsBySidAsync(sid)).found);
        Assert.Single((await service.FindObjectsBySidAsync(sid)).found);
        Assert.Single(_h.Forest.Calls.Cast<string>(), call => call == "Get-ADForest");
        Assert.All(_h.Forest.Searches, read => Assert.StartsWith($"Get-ADObject {SelfGrantDirectoryStandIn.GlobalCatalogEndpoint} (objectSid=", read));

        Assert.Equal(1, _h.Forest.RunspacesHandedOut);
        Assert.Equal(1, ModuleImports);
        Assert.Equal(1, search.CheckSessions.Opened);
    }

    [Fact]
    public async Task TwoSelfGrantChecks_BorrowOneWarmSession()
    {
        // The sweep, S3: the self-grant check built a runspace, imported the module and found the catalog for every check.
        var validator = _h.NothingProtectedValidator(ExchangeWriteHarness.ExchangeKnows("nobody@contoso.test", "nobody@contoso.test", Guid.NewGuid()));
        var other = _h.Forest.AddUser("other", upn: "other@contoso.test", mail: "other@contoso.test", proxies: ["SMTP:other@contoso.test"]);

        Assert.Null(await validator.ValidateSelfGrantAsync(_h.Operator, other.Upn!));
        Assert.Null(await validator.ValidateSelfGrantAsync(_h.Operator, other.Upn!));

        Assert.Equal(1, _h.Forest.RunspacesHandedOut);
        Assert.Equal(1, ModuleImports);
        Assert.Empty(_h.Forest.Errors);
    }

    [Fact]
    public void AFullPool_RefusesAfterItsWait_ItNeverLendsOneSessionTwice()
    {
        using var pool = new DirectorySessionPool(_h.Forest.Open, size: 1);
        using var held = pool.Lease(TimeSpan.FromSeconds(5));

        Assert.Throws<TimeoutException>(() => pool.Lease(TimeSpan.FromMilliseconds(50)));
        held.Dispose();
        using var next = pool.Lease(TimeSpan.FromSeconds(5));
        Assert.Equal(1, pool.Opened);
    }
}
