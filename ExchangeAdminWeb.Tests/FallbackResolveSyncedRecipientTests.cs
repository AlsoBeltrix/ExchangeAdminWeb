using ExchangeAdminWeb.Services;
using NSubstitute;
using static ExchangeAdminWeb.Services.ProtectedPrincipalService;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// <see cref="ProtectedPrincipalService.ResolveWithExchangeFallbackAsync"/> for a directory-synced recipient, end to end
/// over the real protection service, the stand-in forest and a stand-in Graph. The recipient's account lives in the
/// other domain of the forest, so Active Directory's lookup by address - which reads one domain - holds nothing under
/// any of its addresses; only the read of its on-premises SID from Graph, bound at the global catalog, finds the
/// account its protection rules name. Which of its addresses was typed must not decide whether it is found.
/// </summary>
public sealed class FallbackResolveSyncedRecipientTests : IDisposable
{
    private const string Primary = "far@fabrikam.test";
    private const string Alias = "far.alias@fabrikam.test";
    private const string ObjectId = "6a1d0c4e-2b3f-4a5b-8c6d-7e8f90a1b2c3";
    private const string FabrikamSid = "S-1-5-21-444444444-555555555-666666666";
    private const string StaffOuDn = "OU=Staff," + SelfGrantDirectoryStandIn.OtherDomainDn;
    private static readonly Guid MailboxGuid = Guid.Parse("0b7c5d1e-9a2f-4c3b-8d6e-1f2a3b4c5d6e");
    private static readonly Guid StaffOuGuid = Guid.Parse("3c2b1a09-8f7e-4d6c-9b5a-4e3d2c1b0a99");

    private readonly ExchangeWriteHarness _h = new();
    private readonly SelfGrantDirectoryStandIn.Entry _far;
    private readonly IIdentityResolver _exchange = Substitute.For<IIdentityResolver>();

    public FallbackResolveSyncedRecipientTests()
    {
        _h.Forest.AddGroup("Fabrikam Domain Users", domainDn: SelfGrantDirectoryStandIn.OtherDomainDn, domainSid: FabrikamSid,
            rid: SelfGrantDirectoryStandIn.DomainUsersRid);
        _h.Forest.Add(new SelfGrantDirectoryStandIn.Entry { Dn = StaffOuDn, Class = "organizationalUnit", Guid = StaffOuGuid });
        _far = _h.Forest.AddUser("far", upn: Primary, mail: Primary, domainDn: SelfGrantDirectoryStandIn.OtherDomainDn,
            domainSid: FabrikamSid);
        _h.GraphSids[ObjectId] = _far.Sid;

        // Exchange knows the synced mailbox under its primary address and its alias, and nobody else.
        var synced = new ResolvedRecipient(Primary, ObjectId, "UserMailbox", ExistsOnPrem: true, ExchangeGuid: MailboxGuid);
        _exchange.ResolveRecipientAsync(Arg.Any<string>()).Returns((ResolvedRecipient?)null);
        _exchange.ResolveRecipientAsync(Primary).Returns(synced);
        _exchange.ResolveRecipientAsync(Alias).Returns(synced);
    }

    public void Dispose() => _h.Dispose();

    private ProtectedPrincipalService Protection(ProtectedPrincipalConfig? rules = null)
    {
        _h.ProtectedValidator(_exchange, rules ?? SomeoneElseProtected());
        return _h.ProtectedPrincipals!;
    }

    // A rule set that protects one account the tests never resolve.
    private static ProtectedPrincipalConfig SomeoneElseProtected() => new()
    {
        Users = ["someone@contoso.test"],
        UserIds = [$"{SelfGrantDirectoryStandIn.DomainSid}-9001|someone@contoso.test"],
    };

    private bool ReadTheCatalogBySid() =>
        _h.Forest.Searches.Any(read => read.Contains("(objectSid=", StringComparison.Ordinal));

    // ----- typed by its primary address: bound to the account holding its SID -----

    [Fact]
    public async Task TypedByItsPrimaryAddress_ResolvesToTheAccountHoldingItsSid_InTheOtherDomain()
    {
        var protection = Protection();

        var (principal, status) = await protection.ResolveWithExchangeFallbackAsync(Primary);

        Assert.Equal(ResolutionStatus.Resolved, status);
        Assert.Equal(_far.Sid, principal!.ObjectSid);
        Assert.Equal(_far.Dn, principal.DistinguishedName);
        Assert.Equal(Primary, principal.PrimarySmtpAddress);
        // The mailbox GUID is carried only by the resolve for writes bound to a mailbox.
        Assert.Null(principal.ExchangeGuid);
        Assert.Contains(_h.Forest.Searches,
            read => read == $"Get-ADObject {SelfGrantDirectoryStandIn.GlobalCatalogEndpoint} (objectSid={_far.Sid})");
        Assert.Equal(1, _h.DirectoryReadFetches);
    }

    [Fact]
    public async Task TypedByItsPrimaryAddress_AProtectedUserRowOnItsAccount_Protects()
    {
        var protection = Protection(new ProtectedPrincipalConfig { Users = [Primary], UserIds = [$"{_far.Sid}|{Primary}"] });

        var (principal, status) = await protection.ResolveWithExchangeFallbackAsync(Primary);
        Assert.Equal(ResolutionStatus.Resolved, status);
        var verdict = await protection.CheckAsync(principal!);

        Assert.True(verdict.IsProtected);
        Assert.Equal([$"User:{Primary}"], verdict.MatchedRules);
    }

    [Fact]
    public async Task TypedByItsPrimaryAddress_AProtectedGroupOfTheOtherDomainHoldingItsAccount_Protects()
    {
        var vips = _h.Forest.AddGroup("Fabrikam VIPs", domainDn: SelfGrantDirectoryStandIn.OtherDomainDn, domainSid: FabrikamSid,
            members: [_far]);
        var protection = Protection(new ProtectedPrincipalConfig { Groups = [vips.Sid!] });

        var (principal, status) = await protection.ResolveWithExchangeFallbackAsync(Primary);
        Assert.Equal(ResolutionStatus.Resolved, status);
        var verdict = await protection.CheckAsync(principal!);

        Assert.True(verdict.IsProtected);
        Assert.Contains($"Group:{vips.Sid}", verdict.MatchedRules);
    }

    [Fact]
    public async Task TypedByItsPrimaryAddress_AProtectedOuHoldingItsAccount_Protects()
    {
        var protection = Protection(new ProtectedPrincipalConfig
        {
            OrganizationalUnits = [StaffOuDn],
            OuIds = [ProtectedPrincipalIdEntry.Format(StaffOuGuid.ToString(), StaffOuDn)],
        });

        var (principal, status) = await protection.ResolveWithExchangeFallbackAsync(Primary);
        Assert.Equal(ResolutionStatus.Resolved, status);
        var verdict = await protection.CheckAsync(principal!);

        Assert.True(verdict.IsProtected);
        Assert.Equal([$"OU:{StaffOuDn}"], verdict.MatchedRules);
    }

    [Fact]
    public async Task TypedByItsPrimaryAddress_AnAccountNoRuleNames_IsNotProtected()
    {
        var protection = Protection();

        var (principal, status) = await protection.ResolveWithExchangeFallbackAsync(Primary);
        Assert.Equal(ResolutionStatus.Resolved, status);
        var verdict = await protection.CheckAsync(principal!);

        Assert.False(verdict.IsProtected);
        Assert.False(verdict.CheckFailed, verdict.Reason);
    }

    // ----- typed by an alias: the same account -----

    [Fact]
    public async Task TypedByAnAlias_ResolvesToTheSameAccount()
    {
        var protection = Protection(new ProtectedPrincipalConfig { Users = [Primary], UserIds = [$"{_far.Sid}|{Primary}"] });

        var (principal, status) = await protection.ResolveWithExchangeFallbackAsync(Alias);
        Assert.Equal(ResolutionStatus.Resolved, status);
        var verdict = await protection.CheckAsync(principal!);

        Assert.Equal(_far.Sid, principal!.ObjectSid);
        Assert.Equal(Primary, principal.PrimarySmtpAddress);
        Assert.Null(principal.ExchangeGuid);
        Assert.True(verdict.IsProtected);
    }

    // ----- a bind that cannot be made still refuses, whichever address was typed -----

    [Theory]
    [InlineData(Primary)]
    [InlineData(Alias)]
    public async Task GraphNotAnswered_IsUnavailable_WithNoReadBySid(string typed)
    {
        _h.GraphSids.Remove(ObjectId);
        var protection = Protection();

        var (principal, status) = await protection.ResolveWithExchangeFallbackAsync(typed);

        Assert.Equal(ResolutionStatus.Unavailable, status);
        Assert.Null(principal);
        Assert.False(ReadTheCatalogBySid());
    }

    [Theory]
    [InlineData(Primary)]
    [InlineData(Alias)]
    public async Task GraphReportingNoSid_IsUnavailable_WhenActiveDirectoryHoldsNothingUnderTheAddress(string typed)
    {
        _h.GraphSids[ObjectId] = null;
        var protection = Protection();

        var (principal, status) = await protection.ResolveWithExchangeFallbackAsync(typed);

        Assert.Equal(ResolutionStatus.Unavailable, status);
        Assert.Null(principal);
        Assert.False(ReadTheCatalogBySid());
    }

    [Theory]
    [InlineData(Primary)]
    [InlineData(Alias)]
    public async Task TheCatalogReadFailing_IsUnavailable(string typed)
    {
        _h.Forest.Failures[$"Get-ADObject {SelfGrantDirectoryStandIn.GlobalCatalogEndpoint}"] = SelfGrantDirectoryStandIn.UnreachableServer;
        var protection = Protection();

        var (principal, status) = await protection.ResolveWithExchangeFallbackAsync(typed);

        Assert.Equal(ResolutionStatus.Unavailable, status);
        Assert.Null(principal);
    }

    [Theory]
    [InlineData(Primary)]
    [InlineData(Alias)]
    public async Task ASidNoAccountHolds_IsUnavailable(string typed)
    {
        _h.GraphSids[ObjectId] = FabrikamSid + "-9999";
        var protection = Protection();

        var (principal, status) = await protection.ResolveWithExchangeFallbackAsync(typed);

        Assert.Equal(ResolutionStatus.Unavailable, status);
        Assert.Null(principal);
    }

    [Theory]
    [InlineData(Primary)]
    [InlineData(Alias)]
    public async Task TwoObjectsHoldingTheSid_AreAmbiguous(string typed)
    {
        _h.Forest.Add(new SelfGrantDirectoryStandIn.Entry
        {
            Dn = "CN=twin,OU=Staff," + SelfGrantDirectoryStandIn.DomainDn,
            Class = "user",
            Sam = "twin",
            Sid = _far.Sid,
        });
        var protection = Protection();

        var (principal, status) = await protection.ResolveWithExchangeFallbackAsync(typed);

        Assert.Equal(ResolutionStatus.Ambiguous, status);
        Assert.Null(principal);
    }

    // ----- the targets the bind does not concern -----

    [Fact]
    public async Task ACloudOnlyRecipient_IsTheCloudOnlyPrincipal_AndGraphIsNotAsked()
    {
        const string cloud = "cloud@contoso.test";
        _exchange.ResolveRecipientAsync(cloud).Returns(
            new ResolvedRecipient(cloud, "ext-cloud", "UserMailbox", ExistsOnPrem: false, ExchangeGuid: Guid.NewGuid()));
        var protection = Protection();

        var (principal, status) = await protection.ResolveWithExchangeFallbackAsync(cloud);

        Assert.Equal(ResolutionStatus.Resolved, status);
        Assert.Equal("ProtectedPrincipalService-EXO", principal!.Source);
        Assert.Equal(cloud, principal.PrimarySmtpAddress);
        Assert.Null(principal.DistinguishedName);
        Assert.Equal(0, _h.GraphSidReads);
        Assert.False(ReadTheCatalogBySid());
    }

    [Fact]
    public async Task AUserOfTheDefaultDomain_IsResolvedByActiveDirectory_AndNeitherExchangeNorGraphIsAsked()
    {
        const string near = "near@contoso.test";
        var account = _h.Forest.AddUser("near", upn: near, mail: near);
        var protection = Protection();

        var (principal, status) = await protection.ResolveWithExchangeFallbackAsync(near);

        Assert.Equal(ResolutionStatus.Resolved, status);
        Assert.Equal("ProtectedPrincipalService-AD", principal!.Source);
        Assert.Equal(account.Sid, principal.ObjectSid);
        await _exchange.DidNotReceive().ResolveRecipientAsync(Arg.Any<string>());
        Assert.Equal(0, _h.GraphSidReads);
        Assert.False(ReadTheCatalogBySid());
    }
}
