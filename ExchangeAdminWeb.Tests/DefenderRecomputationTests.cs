using System.Collections;
using System.Reflection;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using Page = ExchangeAdminWeb.Components.Pages.DefenderEndpointDevices;

namespace ExchangeAdminWeb.Tests;

public class DefenderRecomputationTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string Unavailable = DefenderEndpointDeviceService.EnrichmentUnavailable;

    [Fact]
    public void SubnetsAreParsedOncePerLoadAcrossMultipleDevicesAndSubsequentLoads()
    {
        AdSiteSubnetMap.Subnet[] subnets =
        [
            new("10.0.0.0/8", "Region"),
            new("10.1.2.0/24", "Floor"),
            new("10.1.2.0/24", "Duplicate"),
            new("10.1.2.128/25", ""),
            new("2001:db8::/32", "IPv6"),
            new("malformed", "Invalid"),
            new("10.0.0.0/33", "Invalid prefix")
        ];
        var attempts = new List<AdSiteSubnetMap.Subnet>();
        for (var load = 0; load < 2; load++)
        {
            DefenderDevice[] devices =
            [
                new() { LastIpAddress = "10.1.2.17" },
                new() { LastIpAddress = "10.1.2.129" },
                new() { LastIpAddress = "10.2.0.1" },
                new() { LastIpAddress = "2001:db8::1" },
                new() { LastIpAddress = "192.0.2.1" },
                new() { LastIpAddress = "not-an-ip" }
            ];
            var complete = DefenderEndpointDeviceService.ApplySiteMatches(
                devices, subnets, CancellationToken.None, attempts.Add);

            Assert.True(complete);
            Assert.Equal(["Floor", "", "Region", "IPv6", "", ""], devices.Select(device => device.Site));
            Assert.Equal(subnets.Length * (load + 1), attempts.Count);
            Assert.Equal(subnets, attempts.Skip(load * subnets.Length));
        }
    }

    [Fact]
    public void RepeatedFilterCountPagingAndExportReadsReuseTheComputedRows()
    {
        var page = FilteredPage();
        var rows = Rows(page);

        Assert.Equal(["a", "u"], rows.Select(device => device.Id));
        Assert.Equal(2, Rows(page).Count);
        _ = Rows(page, "PagedDevices");
        Assert.Same(rows, Rows(page));
        Assert.Same(rows, Rows(page, "ExportableDevices"));
    }

    [Theory]
    [InlineData("result", "replacement")]
    [InlineData("appliedSite", "b")]
    [InlineData("appliedSeenBy", "c")]
    public void EachCacheInputIndependentlyInvalidatesTheRows(string input, string expected)
    {
        var page = FilteredPage();
        var before = Rows(page);
        if (input == "result")
            Set(page, input, Result([new() { Id = expected, Site = "London", RecentlySeenBy = "Alpha" }]));
        else
            Set(page, input, input == "appliedSite" ? "  PAR  " : "  BETA  ");

        var after = Rows(page);
        Assert.NotSame(before, after);
        Assert.Equal(input == "result" ? [expected] : new[] { expected, "u" }, after.Select(device => device.Id));
        Assert.Same(after, Rows(page));
        Assert.Same(after, Rows(page, "ExportableDevices"));
        if (input == "result")
        {
            Set(page, input, null);
            Assert.Empty(Rows(page));
            Set(page, input, Result([new() { Id = "next", Site = "London", RecentlySeenBy = "Alpha" }]));
            Assert.Equal("next", Assert.Single(Rows(page)).Id);
        }
    }

    [Theory]
    [InlineData(DefenderDiscoveryEnrichmentState.Succeeded)]
    [InlineData(DefenderDiscoveryEnrichmentState.PartiallySucceeded)]
    [InlineData(DefenderDiscoveryEnrichmentState.Failed)]
    [InlineData(DefenderDiscoveryEnrichmentState.NotAttempted)]
    public void CachedFiltersKeepUnknownsAndKnownNegativeSemanticsInOriginalOrder(DefenderDiscoveryEnrichmentState state)
    {
        var page = new Page();
        DefenderDevice[] devices =
        [
            new() { Id = "unknown-site", Site = Unavailable, RecentlySeenBy = "Alpha" },
            new() { Id = "known-negative-site", Site = "", RecentlySeenBy = "Alpha" },
            new() { Id = "known-negative-observer", Site = "London", RecentlySeenBy = "" },
            new() { Id = "match", Site = "London North", RecentlySeenBy = "Alpha-West" },
            new() { Id = "unknown-observer", Site = "London", RecentlySeenBy = Unavailable },
            new() { Id = "different-site", Site = "Paris", RecentlySeenBy = Unavailable }
        ];
        Set(page, "result", Result(devices, state));
        Set(page, "appliedSite", "  LON  ");
        Set(page, "appliedSeenBy", "  ALPHA  ");
        var evaluated = state is DefenderDiscoveryEnrichmentState.Succeeded or DefenderDiscoveryEnrichmentState.PartiallySucceeded;
        string[] expected = evaluated
            ? ["unknown-site", "match", "unknown-observer"]
            : ["unknown-site", "known-negative-observer", "match", "unknown-observer"];

        var rows = Rows(page);
        Assert.Equal(expected, rows.Select(device => device.Id));
        Assert.Same(rows, Rows(page));
        Set(page, "appliedSite", " ");
        Set(page, "appliedSeenBy", " ");
        Assert.Same(devices, Rows(page));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    public void SubnetPreparationUsesTheSameDeadlineAndLeavesAllUnfinishedSitesUnavailable(int cancelAt, int parsedCount)
    {
        var clock = new D7Clock();
        using var budget = new CancellationTokenSource(DefenderEndpointDeviceService.DefaultEnrichmentDeadline, clock);
        var subnets = new CancelingSubnets(index =>
        {
            if (index == cancelAt)
                clock.Advance(TimeSpan.FromMinutes(4));
        });
        if (cancelAt == -1)
            clock.Advance(TimeSpan.FromMinutes(4));
        DefenderDevice[] devices =
        [
            new() { Id = "a", LastIpAddress = "10.0.0.1", DiscoverySources = "Network" },
            new() { Id = "b", LastIpAddress = "10.0.1.1", DiscoverySources = "Network" }
        ];
        var parsed = 0;

        var complete = DefenderEndpointDeviceService.ApplySiteMatches(devices, subnets, budget.Token, _ => parsed++);

        Assert.False(complete);
        Assert.Equal(parsedCount, parsed);
        Assert.All(devices, device => Assert.Equal(Unavailable, device.Site));
        Assert.Equal(["a", "b"], devices.Select(device => device.Id));
        Assert.All(devices, device => Assert.Equal("Network", device.DiscoverySources));
        Assert.Equal(1, clock.TimerCount);

        var preparationClock = new D7Clock();
        using var preparationBudget = new CancellationTokenSource(DefenderEndpointDeviceService.DefaultEnrichmentDeadline, preparationClock);
        var preparationSubnets = new CancelingSubnets(index =>
        {
            if (index == cancelAt)
                preparationClock.Advance(TimeSpan.FromMinutes(4));
        });
        if (cancelAt == -1)
            preparationClock.Advance(TimeSpan.FromMinutes(4));
        Assert.Throws<OperationCanceledException>(() => AdSiteSubnetMap.Prepare(preparationSubnets, preparationBudget.Token));
    }

    private static Page FilteredPage()
    {
        var page = new Page();
        Set(page, "result", Result(
        [
            new() { Id = "a", Site = "London", RecentlySeenBy = "Alpha" },
            new() { Id = "b", Site = "Paris", RecentlySeenBy = "Alpha" },
            new() { Id = "c", Site = "London", RecentlySeenBy = "Beta" },
            new() { Id = "u", Site = Unavailable, RecentlySeenBy = Unavailable }
        ]));
        Set(page, "appliedSite", "lon");
        Set(page, "appliedSeenBy", "alpha");
        return page;
    }

    private static DefenderDeviceListResult Result(IReadOnlyList<DefenderDevice> devices,
        DefenderDiscoveryEnrichmentState state = DefenderDiscoveryEnrichmentState.Succeeded) =>
        DefenderDeviceListResult.CompleteRun(devices, devices.Count, 1, 1, 50, state, "");

    private static void Set(Page page, string field, object? value) =>
        typeof(Page).GetField(field, Instance)!.SetValue(page, value);

    private static IReadOnlyList<DefenderDevice> Rows(Page page, string property = "FilteredDevices") =>
        (IReadOnlyList<DefenderDevice>)typeof(Page).GetProperty(property, Instance)!.GetValue(page)!;

    private sealed class CancelingSubnets(Action<int> visit) : IReadOnlyList<AdSiteSubnetMap.Subnet>
    {
        private readonly AdSiteSubnetMap.Subnet[] _rows = [new("10.0.0.0/24", "London"), new("10.0.1.0/24", "Paris")];
        public int Count => _rows.Length;
        public AdSiteSubnetMap.Subnet this[int index] => _rows[index];
        public IEnumerator<AdSiteSubnetMap.Subnet> GetEnumerator()
        {
            for (var i = 0; i < _rows.Length; i++)
            {
                visit(i);
                yield return _rows[i];
            }
            visit(_rows.Length);
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
