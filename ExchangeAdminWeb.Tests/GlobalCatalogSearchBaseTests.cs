using System.Management.Automation;
using System.Security;
using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// rev-a3, rev-b1, rev-c1. Every forest-wide read of the protection checks' directory and the conversion verb's goes to the
/// global catalog with an explicitly empty <c>-SearchBase</c>: with none the AD cmdlets start at the catalog's own domain
/// partition, which does not hold another tree's objects. Over <see cref="SelfGrantDirectoryStandIn"/>, whose catalog
/// (gc01.contoso.test) answers a search with no base from contoso.test only, and whose second domain, fabrikam.test, is a
/// separate tree. A domain-qualified read keeps its domain's own base: an empty base sent to a domain is refused there.
/// </summary>
public sealed class GlobalCatalogSearchBaseTests
{
    private const string Fabrikam = "fabrikam.test";

    private static SelfGrantDirectoryStandIn.Entry OtherTreeGroup(SelfGrantDirectoryStandIn forest, string name = "Tier0 Admins",
        bool domainLocal = false) =>
        forest.AddGroup(name, domainDn: SelfGrantDirectoryStandIn.OtherDomainDn, domainSid: SelfGrantDirectoryStandIn.OtherDomainSid,
            domainLocal: domainLocal);

    private static T Over<T>(SelfGrantDirectoryStandIn forest, Func<PowerShellProtectedGroupDirectory, T> read)
    {
        using var runspace = forest.Open();
        runspace.Open();
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;
        return read(new PowerShellProtectedGroupDirectory(ps, new PSCredential("svc", new SecureString())));
    }

    [Fact]
    public void AGroupInAnotherTree_IsFoundByEveryForestWideRead()
    {
        var forest = new SelfGrantDirectoryStandIn();
        var group = OtherTreeGroup(forest, domainLocal: true);
        var user = forest.AddUser("pat", domainDn: SelfGrantDirectoryStandIn.OtherDomainDn, domainSid: SelfGrantDirectoryStandIn.OtherDomainSid);

        Assert.Equal(group.Dn, Assert.Single(Over(forest, d => d.FindGroups(GroupLookupKey.Sid, group.Sid!, null))).DistinguishedName);
        Assert.Equal(group.Dn, Assert.Single(Over(forest, d => d.FindGroups(GroupLookupKey.SamAccountName, group.Sam!, null))).DistinguishedName);
        Assert.Equal(user.Dn, Assert.Single(Over(forest, d => d.ReadMemberOf([user.Dn], null))).DistinguishedName);
        Assert.Equal([group.Dn], Over(forest, d => d.FindObjectDnsBySid(group.Sid!)));
        Assert.Equal(group.Dn, Assert.Single(Over(forest, d => d.FindObjectsBySidAtCatalog(group.Sid!))).DistinguishedName);
        // Verify's lookups by SID and by DN at the catalog (DirectoryObjectVerifier's forest reads).
        Assert.Equal(group.Dn, Assert.Single(Over(forest, d => d.FindVerifyObjects(VerifyFilters.AnySid([group.Sid!]), null))).DistinguishedName);
        Assert.Equal(group.Dn, Assert.Single(Over(forest, d => d.FindVerifyObjects(VerifyFilters.AnyDn([group.Dn]), null))).DistinguishedName);
        Assert.Equal(user.Dn, Assert.Single(Over(forest, d => d.FindUsers($"(sAMAccountName={user.Sam})", null))).DistinguishedName);
        Assert.Empty(forest.Errors);
    }

    [Fact]
    public void ADomainQualifiedRead_KeepsItsDomainsOwnBase()
    {
        var forest = new SelfGrantDirectoryStandIn();
        var group = OtherTreeGroup(forest);

        // The stand-in refuses an empty base sent to a domain, as the AD module does off the catalog port.
        Assert.Equal(group.Dn, Assert.Single(Over(forest, d => d.FindGroups(GroupLookupKey.Sid, group.Sid!, Fabrikam))).DistinguishedName);
        Assert.Single(Over(forest, d => d.ReadMemberOf([group.Dn], Fabrikam)));
        Assert.Single(Over(forest, d => d.FindVerifyObjects(VerifyFilters.AnyDn([group.Dn]), Fabrikam)));
        Assert.Empty(forest.Errors);
    }

    [Fact]
    public void TheConversion_RefusesANameThatIsAGroupInTwoTrees_AsAmbiguous()
    {
        var forest = new SelfGrantDirectoryStandIn();
        forest.AddGroup("Ops Admins");
        OtherTreeGroup(forest, "Ops Admins");

        IReadOnlyList<ProtectedPrincipalRowResolution> resolved;
        using (var directory = new InvokingUserIdentifierDirectory(forest.Open))
            resolved = ProtectedPrincipalIdentifierResolver.ResolveModuleAdminGrants(["Ops Admins"], directory, CancellationToken.None);

        var only = Assert.Single(resolved);
        Assert.Null(only.Identifier);
        Assert.Equal(ProtectedPrincipalLeftReason.Ambiguous, only.Reason);
        Assert.Empty(forest.Errors);
    }

    [Fact]
    public void TheConversion_FindsAGroupThatOnlyAnotherTreeHolds()
    {
        var forest = new SelfGrantDirectoryStandIn();
        var group = OtherTreeGroup(forest, "Fabrikam Ops");

        IReadOnlyList<ProtectedPrincipalRowResolution> resolved;
        using (var directory = new InvokingUserIdentifierDirectory(forest.Open))
            resolved = ProtectedPrincipalIdentifierResolver.ResolveModuleAdminGrants(["Fabrikam Ops"], directory, CancellationToken.None);

        // As the stand-in hands the SID over: a SecurityIdentifier, read back in its canonical form.
        Assert.Equal(new System.Security.Principal.SecurityIdentifier(group.Sid!).Value, Assert.Single(resolved).Identifier);
        Assert.Empty(forest.Errors);
    }
}
