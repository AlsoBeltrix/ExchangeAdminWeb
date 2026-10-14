using System.Net;
using System.Text;
using ExchangeAdminWeb.Authorization;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Reading the forest's subnet-to-site map, and the failure rule that makes it fatal.
/// </summary>
public class AdSiteDirectoryTests
{
    [Fact]
    public void TheConfigurationPartitionIsDiscoveredAtRuntimeAndNeverNamed()
    {
        // Environment neutrality (.agents/repo-guidance.md). The search base must be built from
        // what RootDSE returned, so the same binary works in a forest nobody has described to
        // it. A hardcoded or configured partition would fail here.
        var commands = Fake(configNc: "CN=Configuration,DC=example,DC=test", subnets: []);

        new AdSiteDirectory(() => commands).ReadSubnets();

        var query = commands.Calls.Single(c => c.Command == "Get-ADObject");
        Assert.Equal("CN=Subnets,CN=Sites,CN=Configuration,DC=example,DC=test", query.Parameters["SearchBase"]);
        Assert.Equal("(objectClass=subnet)", query.Parameters["LDAPFilter"]);
    }

    [Fact]
    public void EverySubnetIsReadInOneQueryRatherThanOnePerDevice()
    {
        var commands = Fake("CN=Configuration,DC=example,DC=test",
        [
            Subnet("10.0.0.0/24", "CN=London,CN=Sites,CN=Configuration,DC=example,DC=test"),
            Subnet("10.0.1.0/24", "CN=Paris,CN=Sites,CN=Configuration,DC=example,DC=test"),
        ]);

        var subnets = new AdSiteDirectory(() => commands).ReadSubnets();

        Assert.Equal(1, commands.Calls.Count(c => c.Command == "Get-ADObject"));
        Assert.Equal(["London", "Paris"], subnets.Select(s => s.SiteName));
        Assert.Equal(["10.0.0.0/24", "10.0.1.0/24"], subnets.Select(s => s.Cidr));
    }

    [Fact]
    public void ASubnetWithNoSiteIsKeptWithAnEmptySiteRatherThanDropped()
    {
        // Dropping it would let a BROADER subnet claim the device and name a site it is not in.
        // 16 of this forest's 531 subnets were unlinked when last measured, so this is the
        // normal case rather than a defensive one.
        var commands = Fake("CN=Configuration,DC=example,DC=test",
        [
            Subnet("10.0.0.0/16", "CN=Region,CN=Sites,CN=Configuration,DC=example,DC=test"),
            Subnet("10.0.4.0/24", siteObject: null),
        ]);

        var subnets = new AdSiteDirectory(() => commands).ReadSubnets();

        Assert.Equal(2, subnets.Count);
        Assert.Equal("", subnets[1].SiteName);
        Assert.Equal("10.0.4.0/24", subnets[1].Cidr);
    }

    [Fact]
    public void ASubnetRowWithNoCidrIsSkipped()
    {
        var commands = Fake("CN=Configuration,DC=example,DC=test",
        [
            Subnet(cidr: null, "CN=London,CN=Sites,CN=Configuration,DC=example,DC=test"),
            Subnet("10.0.0.0/24", "CN=London,CN=Sites,CN=Configuration,DC=example,DC=test"),
        ]);

        var subnet = Assert.Single(new AdSiteDirectory(() => commands).ReadSubnets());
        Assert.Equal("10.0.0.0/24", subnet.Cidr);
    }

    // ---- the failure rule, which is the opposite of the hunting enrichment's ----

    [Fact]
    public void AnUnreadableConfigurationPartitionThrowsRatherThanReturningAnEmptyMap()
    {
        // An empty map is indistinguishable from "this forest has no subnets", and it would
        // render 27,000 rows with a blank Site column - sending an operator hunting through a
        // report that cannot answer the question they opened it for. Plan Q10.
        var commands = new FakeCommands()
            .Returns("Get-ADRootDSE", new DirectoryCommandOutcome([], null));

        Assert.Throws<InvalidOperationException>(() => new AdSiteDirectory(() => commands).ReadSubnets());
    }

    [Fact]
    public void ASubnetQueryThatReportsAnErrorThrowsRatherThanReturningWhatItGot()
    {
        // A partial map is worse than none: the devices it does cover look authoritative and
        // the ones it misses look like they have no site, with nothing saying which is which.
        var commands = new FakeCommands()
            .Returns("Get-ADRootDSE", new DirectoryCommandOutcome(
                [new DirectoryObject(new Dictionary<string, object?>
                {
                    ["configurationNamingContext"] = "CN=Configuration,DC=example,DC=test"
                })], null))
            .Returns("Get-ADObject", new DirectoryCommandOutcome([], "the server is not operational"));

        var ex = Assert.Throws<InvalidOperationException>(
            () => new AdSiteDirectory(() => commands).ReadSubnets());

        Assert.Contains("the server is not operational", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRunspaceIsDisposedEvenWhenTheReadThrows()
    {
        var commands = new FakeCommands()
            .Returns("Get-ADRootDSE", new DirectoryCommandOutcome([], null));

        Assert.Throws<InvalidOperationException>(() => new AdSiteDirectory(() => commands).ReadSubnets());
        Assert.True(commands.Disposed);
    }

    // ---- what the caller does with a failed read ----

    [Fact]
    public async Task ADirectoryFailure_MarksEverySiteUnavailableAndLeavesTheRestOfTheReport()
    {
        // Plan Q10 said an AD failure fails the whole report, and its reasoning was that a
        // blank Site cannot be told apart from "no site known". The marker satisfies that
        // reasoning; failing the run no longer does, because Site became the module's primary
        // column after Q10 was ruled - killing the report means an operator hunting a machine
        // gets nothing at all instead of a device list with one column degraded.
        var inventory = OneDeviceAt("10.0.0.1");

        // A directory whose RootDSE read yields nothing, which AdSiteDirectory turns into a throw.
        var broken = new AdSiteDirectory(() => new FakeCommands()
            .Returns("Get-ADRootDSE", new DirectoryCommandOutcome([], null)));

        var service = new DefenderEndpointDeviceService(
            () => Task.FromResult<DefenderApiClient?>(inventory),
            "50",
            siteDirectory: broken);

        var result = await service.ListDevicesAsync();

        Assert.Equal(DefenderDeviceListOutcome.Complete, result.Outcome);
        Assert.False(result.SiteMapRead);

        var device = Assert.Single(result.Devices);
        Assert.Equal(DefenderEndpointDeviceService.EnrichmentUnavailable, device.Site);

        // The rest of the row is untouched: a directory outage is not a statement about the
        // device inventory, which was read successfully.
        Assert.Equal("host1.corp.example", device.ComputerDnsName);
        Assert.Equal("10.0.0.1", device.LastIpAddress);
    }

    [Fact]
    public async Task ASearchThatMatchedNothing_DoesNotTouchTheDirectory()
    {
        // Two reasons. It spends a directory round trip on an empty answer, and - worse - a
        // directory outage would surface on a search that simply matched no devices, which the
        // operator would then have to tell apart from a real one.
        var inventory = new DefenderApiClient(
            "tenant", "client", "secret",
            DefenderApiClient.DefenderBaseUrl,
            DefenderApiClient.DefenderTokenScope,
            new HttpClient(new OneShotHandler("{\"value\":[]}")));

        var commands = new FakeCommands();  // scripted with nothing: any call throws
        var service = new DefenderEndpointDeviceService(
            () => Task.FromResult<DefenderApiClient?>(inventory),
            "50",
            siteDirectory: new AdSiteDirectory(() => commands));

        var result = await service.ListDevicesAsync();

        Assert.Empty(result.Devices);
        Assert.True(result.SiteMapRead);
        Assert.Empty(commands.Calls);
    }

    [Fact]
    public async Task AReadThatSucceeds_ReportsTheMapAsRead()
    {
        // The negative case: without it, SiteMapRead could be hardcoded false and the banner
        // would warn on every healthy run.
        var inventory = OneDeviceAt("10.0.0.17");

        var working = new AdSiteDirectory(() => Fake("CN=Configuration,DC=example,DC=test",
            [Subnet("10.0.0.0/24", "CN=London,CN=Sites,CN=Configuration,DC=example,DC=test")]));

        var service = new DefenderEndpointDeviceService(
            () => Task.FromResult<DefenderApiClient?>(inventory),
            "50",
            siteDirectory: working);

        var result = await service.ListDevicesAsync();

        Assert.True(result.SiteMapRead);
        Assert.Equal("London", Assert.Single(result.Devices).Site);
    }

    // ---- site name extraction ----

    [Theory]
    [InlineData("CN=London-HQ,CN=Sites,CN=Configuration,DC=example,DC=test", "London-HQ")]
    [InlineData("CN=A B C,CN=Sites,DC=x", "A B C")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("no-equals-sign", "")]
    [InlineData("CN=", "")]
    public void TheSiteNameIsTheFirstRdnValue(string? dn, string expected) =>
        Assert.Equal(expected, AdSiteDirectory.SiteNameFromDn(dn));

    // ---- helpers ----

    private static FakeCommands Fake(string configNc, IReadOnlyList<DirectoryObject?> subnets)
    {
        return new FakeCommands()
            .Returns("Get-ADRootDSE", new DirectoryCommandOutcome(
                [new DirectoryObject(new Dictionary<string, object?>
                {
                    ["configurationNamingContext"] = configNc
                })], null))
            .Returns("Get-ADObject", new DirectoryCommandOutcome(subnets, null));
    }

    private static DirectoryObject Subnet(string? cidr, string? siteObject) =>
        new(new Dictionary<string, object?> { ["cn"] = cidr, ["siteObject"] = siteObject });

    /// <summary>A Defender client whose inventory answers with one device at the given IP.</summary>
    private static DefenderApiClient OneDeviceAt(string ip) =>
        new("tenant", "client", "secret",
            DefenderApiClient.DefenderBaseUrl,
            DefenderApiClient.DefenderTokenScope,
            new HttpClient(new OneShotHandler(
                "{\"value\":[{\"id\":\"m1\",\"computerDnsName\":\"host1.corp.example\","
                + "\"osPlatform\":\"Windows10\",\"onboardingStatus\":\"CanBeOnboarded\","
                + $"\"lastIpAddress\":\"{ip}\"}}]}}")));

    /// <summary>
    /// Answers the token request and then one inventory page.
    /// </summary>
    /// <remarks>
    /// Local rather than the Defender fixture's richer stub, which is private to that class.
    /// These tests need one page and no scripting, so a shared seam would be more coupling than
    /// the duplication costs.
    /// </remarks>
    private sealed class OneShotHandler(string body) : HttpMessageHandler
    {
        private readonly string _body = body;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var isToken = request.RequestUri?.AbsoluteUri.Contains("/oauth2/", StringComparison.Ordinal) == true;
            var payload = isToken ? "{\"access_token\":\"t\",\"expires_in\":3600}" : _body;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed record RecordedCall(string Command, IReadOnlyDictionary<string, object?> Parameters);

    private sealed class FakeCommands : ISectionAccessDirectoryCommands
    {
        private readonly Dictionary<string, Queue<DirectoryCommandOutcome>> _responses =
            new(StringComparer.OrdinalIgnoreCase);

        public List<RecordedCall> Calls { get; } = [];

        public bool Disposed { get; private set; }

        public FakeCommands Returns(string command, DirectoryCommandOutcome outcome)
        {
            if (!_responses.TryGetValue(command, out var queue))
                _responses[command] = queue = new Queue<DirectoryCommandOutcome>();
            queue.Enqueue(outcome);
            return this;
        }

        public DirectoryCommandOutcome Invoke(string command, IReadOnlyDictionary<string, object?> parameters)
        {
            Calls.Add(new RecordedCall(command, parameters));

            if (_responses.TryGetValue(command, out var queue) && queue.Count > 0)
                return queue.Dequeue();

            // An unscripted command is a test bug, not an empty directory.
            throw new InvalidOperationException($"No response scripted for '{command}'.");
        }

        public string? TranslateSidToNTAccount(string sid) => null;

        public void Dispose() => Disposed = true;
    }
}
