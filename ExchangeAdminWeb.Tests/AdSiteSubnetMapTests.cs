using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Longest-prefix matching of a device IP to its Active Directory site.
/// </summary>
/// <remarks>
/// Every failure mode here is SILENT by nature: a wrong prefix length returns the wrong site
/// rather than an error, and the operator has no way to tell a wrong site from a right one.
/// That is why the cases below are specific rather than a couple of happy paths - someone is
/// going to walk to a building on the strength of this answer.
/// </remarks>
public class AdSiteSubnetMapTests
{
    private static AdSiteSubnetMap.Subnet Net(string cidr, string site) => new(cidr, site);

    [Fact]
    public void TheMostSpecificSubnetWinsRatherThanTheFirstOrTheBroadest()
    {
        // The case this exists for. A /16 is usually a region or a summary route; the /24 is the
        // floor the machine is on. Listed broad-first so a first-match implementation picks the
        // wrong one and fails here.
        var subnets = new[]
        {
            Net("10.185.0.0/16", "EMEA-Summary"),
            Net("10.185.4.0/24", "London-Floor-3"),
        };

        Assert.Equal("London-Floor-3", AdSiteSubnetMap.FindSite("10.185.4.17", subnets));
    }

    [Fact]
    public void TheMostSpecificSubnetWinsWhenListedNarrowFirstToo()
    {
        // Same assertion, opposite enumeration order, so the result cannot be an artifact of
        // whichever way the directory happened to return the rows.
        var subnets = new[]
        {
            Net("10.185.4.0/24", "London-Floor-3"),
            Net("10.185.0.0/16", "EMEA-Summary"),
        };

        Assert.Equal("London-Floor-3", AdSiteSubnetMap.FindSite("10.185.4.17", subnets));
    }

    [Fact]
    public void TwoEquallySpecificSubnetsResolveToTheFirstOneStably()
    {
        // Duplicate CIDRs with different sites is a directory fault and neither answer is
        // better. What matters is that the same input gives the same answer every run, so an
        // operator who re-runs a report does not get a different building. Added after a
        // mutation probe showed the strictly-greater comparison - the thing that makes "first
        // wins" true rather than "last wins" - was asserted by nothing.
        var subnets = new[]
        {
            Net("10.185.4.0/24", "London-Floor-3"),
            Net("10.185.4.0/24", "London-Floor-4"),
        };

        Assert.Equal("London-Floor-3", AdSiteSubnetMap.FindSite("10.185.4.17", subnets));
    }

    [Fact]
    public void AnAddressInNoSubnetReturnsBlankAndNotTheNearestMatch()
    {
        // A guess wearing the costume of an answer is worse than no answer: the operator cannot
        // tell it apart from a real one, and this is the module people act on physically.
        var subnets = new[] { Net("10.185.4.0/24", "London-Floor-3") };

        Assert.Equal("", AdSiteSubnetMap.FindSite("192.168.1.5", subnets));
    }

    [Fact]
    public void ASubnetWithNoSiteMatchesAndReturnsBlankRatherThanYieldingToABroaderOne()
    {
        // 515 of this forest's 531 subnets have a site, so 16 do not. The device IS in the
        // narrow one; the truthful answer is "that subnet has no site", not the broad subnet's
        // site - which would name a region the machine may not be in.
        var subnets = new[]
        {
            Net("10.185.0.0/16", "EMEA-Summary"),
            Net("10.185.4.0/24", ""),
        };

        Assert.Equal("", AdSiteSubnetMap.FindSite("10.185.4.17", subnets));
    }

    [Theory]
    [InlineData("10.0.0.1", "London-A")]    // inside the lower half
    [InlineData("10.0.0.127", "London-A")]  // last address of the /25
    [InlineData("10.0.0.128", "London-B")]  // first address of the next /25
    [InlineData("10.0.0.255", "London-B")]
    public void APrefixThatSplitsAByteIsMaskedBitwiseRatherThanByWholeOctets(string ip, string expected)
    {
        // Skipping the partial-byte mask makes every /25 behave like a /24 and silently merges
        // two sites that share a third octet. Both halves are asserted, and both boundary
        // addresses, because a mask that is off by one bit still passes a single mid-range case.
        var subnets = new[]
        {
            Net("10.0.0.0/25", "London-A"),
            Net("10.0.0.128/25", "London-B"),
        };

        Assert.Equal(expected, AdSiteSubnetMap.FindSite(ip, subnets));
    }

    [Fact]
    public void AMalformedSubnetIsSkippedRatherThanCostingEveryOtherDeviceItsSite()
    {
        // AD subnet objects are created by hand, so a malformed cn is a question of when. One
        // bad row must not throw the whole report away.
        var subnets = new[]
        {
            Net("not-a-subnet", "Nowhere"),
            Net("10.185.4.0/notanumber", "Nowhere"),
            Net("10.185.4.0/99", "Nowhere"),
            Net("", "Nowhere"),
            Net("10.185.4.0/24", "London-Floor-3"),
        };

        Assert.Equal("London-Floor-3", AdSiteSubnetMap.FindSite("10.185.4.17", subnets));
    }

    [Fact]
    public void AnUnparseableDeviceAddressReturnsBlank()
    {
        var subnets = new[] { Net("10.185.4.0/24", "London-Floor-3") };

        Assert.Equal("", AdSiteSubnetMap.FindSite("", subnets));
        Assert.Equal("", AdSiteSubnetMap.FindSite(null, subnets));
        Assert.Equal("", AdSiteSubnetMap.FindSite("not-an-ip", subnets));
    }

    [Fact]
    public void AnIpv6AddressIsNotMatchedAgainstIpv4SubnetsOrViceVersa()
    {
        // Address families are compared before bytes. Without that, a 4-byte and a 16-byte
        // address get length-compared and the answer depends on an implementation detail.
        var v4 = new[] { Net("10.0.0.0/8", "London") };
        var v6 = new[] { Net("2001:db8::/32", "London-v6") };

        Assert.Equal("", AdSiteSubnetMap.FindSite("2001:db8::1", v4));
        Assert.Equal("", AdSiteSubnetMap.FindSite("10.0.0.1", v6));
        Assert.Equal("London-v6", AdSiteSubnetMap.FindSite("2001:db8::1", v6));
    }

    [Fact]
    public void AnEmptySubnetListReturnsBlankRatherThanThrowing()
    {
        // The directory read returning nothing is a real state - a forest with no subnet
        // objects configured - and it means "no site known", not "crash the report".
        Assert.Equal("", AdSiteSubnetMap.FindSite("10.0.0.1", []));
    }

    [Fact]
    public void AHostAddressWrittenWhereTheNetworkAddressBelongsStillMatchesItsOwnSubnet()
    {
        // AD stores subnets as network/prefix, but a hand-created object can carry a host
        // address. Masking both sides means 10.0.0.5/24 still contains 10.0.0.17 rather than
        // matching nothing and sending the device to a broader site.
        var subnets = new[] { Net("10.0.0.5/24", "London-Floor-3") };

        Assert.Equal("London-Floor-3", AdSiteSubnetMap.FindSite("10.0.0.17", subnets));
    }
}
