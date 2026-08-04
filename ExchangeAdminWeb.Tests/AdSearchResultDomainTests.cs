using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Slice 3 of docs/AdminUIRedesign-Plan.md, bug B1. Group search now spans the forest, so a
/// result set contains several domains and every row must say which one it came from -- a bare
/// name is exactly as ambiguous in the picker as it was on the admin page.
///
/// The directory call itself is untestable here; the DN parsing that labels each row is not, so
/// it lives in a pure function and is tested.
/// </summary>
public class AdSearchResultDomainTests
{
    [Theory]
    [InlineData(@"CN=ExchangeWebAdmins,OU=Groups,OU=NWD,OU=AMER,DC=ad,DC=analog,DC=com", "ad.analog.com")]
    [InlineData(@"CN=Enterprise Admins,CN=Users,DC=winroot,DC=analog,DC=com", "winroot.analog.com")]
    [InlineData(@"CN=x,DC=contoso,DC=local", "contoso.local")]
    public void ExtractsTheDnsDomainFromADn(string dn, string expected)
    {
        Assert.Equal(expected, ADDirectorySearchService.DnsDomainFromDn(dn));
    }

    [Fact]
    public void IsCaseInsensitiveOnTheDcPrefix()
    {
        // Directories are inconsistent about attribute-name casing.
        Assert.Equal("ad.analog.com",
            ADDirectorySearchService.DnsDomainFromDn(@"CN=x,dc=ad,Dc=analog,DC=com"));
    }

    [Fact]
    public void IgnoresDcTextInsideAnOrdinaryComponent()
    {
        // "OU=DCOps" starts with DC but is not a domain component. Splitting naively would
        // produce "Ops.ad.analog.com" and mislabel every row in that OU.
        Assert.Equal("ad.analog.com",
            ADDirectorySearchService.DnsDomainFromDn(@"CN=x,OU=DCOps,DC=ad,DC=analog,DC=com"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("CN=orphan,OU=NoDomain")]
    public void ReturnsNullRatherThanGuessing(string? dn)
    {
        // No DC components means the domain is unknown. The picker then shows no label, which is
        // honest; showing the local domain would assert something not in evidence.
        Assert.Null(ADDirectorySearchService.DnsDomainFromDn(dn));
    }

    // ---------------------------------------------------------------- The display label

    [Theory]
    [InlineData("ad.analog.com", "AD")]
    [InlineData("winroot.analog.com", "WINROOT")]
    public void LabelsARowWithItsUppercasedFirstLabel(string dns, string expected)
    {
        var r = Group(dns);

        Assert.Equal(expected, r.NetBiosDomain);
    }

    [Fact]
    public void HasNoLabelWhenTheDomainIsUnknown()
    {
        Assert.Null(Group(null).NetBiosDomain);
    }

    [Fact]
    public void TwoDomainsWithTheSameGroupNameRenderDifferently()
    {
        // The whole reason the label exists: a forest search returns both, and without the
        // domain an operator cannot tell which grant they are about to create.
        var local = Group("ad.analog.com");
        var foreign = Group("winroot.analog.com");

        Assert.NotEqual(local.NetBiosDomain, foreign.NetBiosDomain);
    }

    private static ADSearchResult Group(string? dnsDomain) => new(
        DisplayName: "Enterprise Admins",
        DistinguishedName: "CN=Enterprise Admins,CN=Users,DC=x",
        SamAccountName: "Enterprise Admins",
        UserPrincipalName: null,
        Email: null,
        ObjectType: "Group",
        ObjectSid: "S-1-5-21-725345543-2052111302-839522115-519",
        DnsDomain: dnsDomain);
}
