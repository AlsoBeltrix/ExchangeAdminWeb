using System.Security.Claims;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using static ExchangeAdminWeb.Services.ProtectedPrincipalServicing;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// The protected-principal gate for a write to an Entra user (<see cref="ProtectedPrincipalServicing.ForCloudUserAsync"/>), end
/// to end over the real protection service, the stand-in forest and a stand-in Graph. The user it is given lives in the other
/// domain of the forest and has no Exchange recipient: resolved by its address it is found nowhere, so only the read of its
/// on-premises SID from Graph, bound at the global catalog, can find the account its protection rules name.
/// </summary>
public sealed class CloudUserProtectionTests : IDisposable
{
    internal const string FarUpn = "far@fabrikam.test";
    internal const string FarObjectId = "6a1d0c4e-2b3f-4a5b-8c6d-7e8f90a1b2c3";
    private const string FabrikamSid = "S-1-5-21-444444444-555555555-666666666";
    private const string ServicerSid = "S-1-5-21-1-2-3-4001";
    private const string Module = "RiskyUsers";
    private static readonly Guid StaffOuGuid = Guid.Parse("3c2b1a09-8f7e-4d6c-9b5a-4e3d2c1b0a99");

    private readonly ExchangeWriteHarness _h = new();
    private readonly SelfGrantDirectoryStandIn.Entry _far;
    private readonly IIdentityResolver _exchangeKnowsNobody = Substitute.For<IIdentityResolver>();

    public CloudUserProtectionTests()
    {
        _h.Forest.AddGroup("Fabrikam Domain Users", domainDn: SelfGrantDirectoryStandIn.OtherDomainDn, domainSid: FabrikamSid,
            rid: SelfGrantDirectoryStandIn.DomainUsersRid);
        _h.Forest.Add(new SelfGrantDirectoryStandIn.Entry
        {
            Dn = "OU=Staff," + SelfGrantDirectoryStandIn.OtherDomainDn, Class = "organizationalUnit", Guid = StaffOuGuid,
        });
        _far = _h.Forest.AddUser("far", upn: FarUpn, domainDn: SelfGrantDirectoryStandIn.OtherDomainDn, domainSid: FabrikamSid);
        _h.GraphSids[FarObjectId] = _far.Sid;
        _exchangeKnowsNobody.ResolveRecipientAsync(Arg.Any<string>()).Returns((ResolvedRecipient?)null);
    }

    public void Dispose() => _h.Dispose();

    private ProtectedPrincipalService Protection(ProtectedPrincipalConfig rules, IIdentityResolver? exchange = null)
    {
        _h.ProtectedValidator(exchange ?? _exchangeKnowsNobody, rules);
        return _h.ProtectedPrincipals!;
    }

    // A rule set that protects one account the tests never write.
    private static ProtectedPrincipalConfig SomeoneElseProtected() => new()
    {
        Users = ["someone@contoso.test"],
        UserIds = [$"{SelfGrantDirectoryStandIn.DomainSid}-9001|someone@contoso.test"],
    };

    private ProtectedPrincipalServicerService Servicers(bool grant = false)
    {
        var rows = new Dictionary<string, string[]> { [Module] = ["S-1-5-21-1-2-3-500"] };
        if (grant)
            rows[ProtectedPrincipalServicerService.SectionKeyFor(Module)] = [ServicerSid];
        var repository = new SectionAccessRepository(TestConfigStore.Create(_h.Dir));
        repository.SaveAll(rows);
        var env = Substitute.For<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
        env.ContentRootPath.Returns(_h.Dir);
        return new ProtectedPrincipalServicerService(
            new SectionAccessService(_h.Config, NullLogger<SectionAccessService>.Instance, env, new ModuleCatalog(), repository),
            NullLogger<ProtectedPrincipalServicerService>.Instance);
    }

    private static readonly ClaimsPrincipal Servicer = new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, "CONTOSO\\servicer"), new Claim(ClaimTypes.Role, ServicerSid)], "TestAuth"));

    private Task<CloudUserDecision> Decide(ProtectedPrincipalService protection, ProtectedPrincipalServicerService? servicers = null,
        ClaimsPrincipal? actingUser = null, string objectId = FarObjectId, string upn = FarUpn) =>
        ForCloudUserAsync(protection, servicers ?? Servicers(), objectId, upn, actingUser ?? _h.Operator, Module);

    // ----- a SID reported: the account holding it is checked, in its own domain -----

    [Fact]
    public async Task AProtectedUserRow_OnTheAccountInTheOtherDomain_Refuses()
    {
        var protection = Protection(new ProtectedPrincipalConfig { Users = [FarUpn], UserIds = [$"{_far.Sid}|{FarUpn}"] });

        var decision = await Decide(protection);

        Assert.Equal(CloudUserRefusal.Protected, decision.Refusal);
        Assert.Equal([$"User:{FarUpn}"], decision.MatchedRules);
        Assert.False(decision.Unresolved);
        Assert.Contains(_h.Forest.Searches,
            read => read == $"Get-ADObject {SelfGrantDirectoryStandIn.GlobalCatalogEndpoint} (objectSid={_far.Sid})");
    }

    [Fact]
    public async Task AProtectedGroupOfTheOtherDomain_HoldingTheAccount_Refuses()
    {
        var vips = _h.Forest.AddGroup("Fabrikam VIPs", domainDn: SelfGrantDirectoryStandIn.OtherDomainDn, domainSid: FabrikamSid,
            members: [_far]);
        var protection = Protection(new ProtectedPrincipalConfig { Groups = [vips.Sid!] });

        var decision = await Decide(protection);

        Assert.Equal(CloudUserRefusal.Protected, decision.Refusal);
        Assert.Contains($"Group:{vips.Sid}", decision.MatchedRules);
    }

    [Fact]
    public async Task AProtectedOu_HoldingTheAccount_Refuses()
    {
        var ouDn = "OU=Staff," + SelfGrantDirectoryStandIn.OtherDomainDn;
        var protection = Protection(new ProtectedPrincipalConfig
        {
            OrganizationalUnits = [ouDn],
            OuIds = [ProtectedPrincipalIdEntry.Format(StaffOuGuid.ToString(), ouDn)],
        });

        var decision = await Decide(protection);

        Assert.Equal(CloudUserRefusal.Protected, decision.Refusal);
        Assert.Equal([$"OU:{ouDn}"], decision.MatchedRules);
    }

    [Fact]
    public async Task AnAccountNoRuleNames_Proceeds_WithNoNote()
    {
        var other = _h.Forest.AddUser("other", upn: "other@fabrikam.test", domainDn: SelfGrantDirectoryStandIn.OtherDomainDn,
            domainSid: FabrikamSid);
        var protection = Protection(new ProtectedPrincipalConfig { Users = ["other@fabrikam.test"], UserIds = [$"{other.Sid}|other@fabrikam.test"] });

        var decision = await Decide(protection);

        Assert.True(decision.Allowed);
        Assert.Null(decision.ServicedNote);
        Assert.Equal(1, _h.DirectoryReadFetches);
    }

    [Fact]
    public async Task AnAuthorisedServicer_ProceedsOnAProtectedUser_WithTheNote()
    {
        var protection = Protection(new ProtectedPrincipalConfig { Users = [FarUpn], UserIds = [$"{_far.Sid}|{FarUpn}"] });

        var decision = await Decide(protection, Servicers(grant: true), Servicer);

        Assert.True(decision.Allowed);
        Assert.Contains("serviced by an authorised operator", decision.ServicedNote, StringComparison.Ordinal);
        Assert.Contains($"User:{FarUpn}", decision.ServicedNote, StringComparison.Ordinal);
        Assert.DoesNotContain("directory-unresolved", decision.ServicedNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SomeoneOutsideTheServicerGroup_IsRefused()
    {
        var protection = Protection(new ProtectedPrincipalConfig { Users = [FarUpn], UserIds = [$"{_far.Sid}|{FarUpn}"] });

        var decision = await Decide(protection, Servicers(grant: true), _h.Operator);

        Assert.Equal(CloudUserRefusal.Protected, decision.Refusal);
        Assert.Null(decision.ServicedNote);
    }

    // ----- fail closed -----

    [Fact]
    public async Task ASyncedUserGraphGivesNoSidFor_Refuses_AsUnavailable()
    {
        _h.GraphSids[FarObjectId] = null;
        _h.GraphSyncFlags[FarObjectId] = true;
        var protection = Protection(SomeoneElseProtected());

        var decision = await Decide(protection);

        Assert.Equal(CloudUserRefusal.Unavailable, decision.Refusal);
        Assert.DoesNotContain(_h.Forest.Searches, read => read.Contains("(objectSid=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASyncFlagGraphDidNotAnswer_Refuses_AsUnavailable()
    {
        _h.GraphSids[FarObjectId] = null;
        var protection = Protection(SomeoneElseProtected());

        var decision = await Decide(protection);

        Assert.Equal(CloudUserRefusal.Unavailable, decision.Refusal);
    }

    [Fact]
    public async Task GraphNotAnswered_Refuses_AsUnavailable_WithNoReadBySid()
    {
        _h.GraphSids.Remove(FarObjectId);
        var protection = Protection(SomeoneElseProtected());

        var decision = await Decide(protection);

        Assert.Equal(CloudUserRefusal.Unavailable, decision.Refusal);
        Assert.Equal(1, _h.GraphSidReads);
        Assert.DoesNotContain(_h.Forest.Searches, read => read.Contains("(objectSid=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheCatalogReadFailing_Refuses_AsUnavailable()
    {
        _h.Forest.Failures[$"Get-ADObject {SelfGrantDirectoryStandIn.GlobalCatalogEndpoint}"] = SelfGrantDirectoryStandIn.UnreachableServer;
        var protection = Protection(SomeoneElseProtected());

        var decision = await Decide(protection);

        Assert.Equal(CloudUserRefusal.Unavailable, decision.Refusal);
    }

    [Fact]
    public async Task ASidNoAccountHolds_Refuses_AsUnavailable()
    {
        _h.GraphSids[FarObjectId] = FabrikamSid + "-9999";
        var protection = Protection(SomeoneElseProtected());

        var decision = await Decide(protection);

        Assert.Equal(CloudUserRefusal.Unavailable, decision.Refusal);
    }

    [Fact]
    public async Task TwoObjectsHoldingTheSid_Refuse_AsAmbiguous()
    {
        _h.Forest.Add(new SelfGrantDirectoryStandIn.Entry
        {
            Dn = "CN=twin,OU=Staff," + SelfGrantDirectoryStandIn.DomainDn, Class = "user", Sam = "twin", Sid = _far.Sid,
        });
        var protection = Protection(SomeoneElseProtected());

        var decision = await Decide(protection);

        Assert.Equal(CloudUserRefusal.Ambiguous, decision.Refusal);
    }

    [Theory]
    [InlineData("", FarUpn)]
    [InlineData(FarObjectId, " ")]
    public async Task ABlankObjectIdOrUpn_Refuses_AsUnavailable_AskingNothing(string objectId, string upn)
    {
        var protection = Protection(SomeoneElseProtected());

        var decision = await Decide(protection, objectId: objectId, upn: upn);

        Assert.Equal(CloudUserRefusal.Unavailable, decision.Refusal);
        Assert.Equal(0, _h.GraphSidReads);
    }

    // ----- cloud-only: checked as the cloud-identity modules checked it -----

    [Fact]
    public async Task ACloudOnlyUserExchangeKnows_IsCheckedAsTheCloudOnlyRecipient_AndProceeds()
    {
        const string cloud = "cloud@contoso.test";
        _h.GraphSids[FarObjectId] = null;
        _h.GraphSyncFlags[FarObjectId] = false;
        var exchange = ExchangeWriteHarness.ExchangeKnows(cloud, cloud, Guid.NewGuid());
        var protection = Protection(SomeoneElseProtected(), exchange);

        var decision = await Decide(protection, upn: cloud);

        Assert.True(decision.Allowed);
        Assert.False(decision.Unresolved);
        await exchange.Received().ResolveRecipientAsync(cloud);
    }

    [Fact]
    public async Task ACloudOnlyUserNobodyKnows_IsCheckedByItsUpnAndObjectId_SoALegacyExclusionByObjectIdRefuses()
    {
        _h.GraphSids[FarObjectId] = null;
        _h.GraphSyncFlags[FarObjectId] = false;
        _h.ModuleConfig.SaveModuleConfig("MailboxPermissions", new Dictionary<string, string> { ["ExcludedUsers"] = FarObjectId });
        var protection = Protection(new ProtectedPrincipalConfig());

        var decision = await Decide(protection);

        Assert.Equal(CloudUserRefusal.Protected, decision.Refusal);
        Assert.True(decision.Unresolved);
        Assert.Equal([$"LegacyExclusion:{FarObjectId}"], decision.MatchedRules);
    }

    [Fact]
    public async Task ACloudOnlyUserNobodyKnows_ServicedNote_IsQualifiedDirectoryUnresolved()
    {
        _h.GraphSids[FarObjectId] = null;
        _h.GraphSyncFlags[FarObjectId] = false;
        _h.ModuleConfig.SaveModuleConfig("MailboxPermissions", new Dictionary<string, string> { ["ExcludedUsers"] = FarUpn });
        var protection = Protection(new ProtectedPrincipalConfig());

        var decision = await Decide(protection, Servicers(grant: true), Servicer);

        Assert.True(decision.Allowed);
        Assert.Contains("(directory-unresolved)", decision.ServicedNote, StringComparison.Ordinal);
    }
}
