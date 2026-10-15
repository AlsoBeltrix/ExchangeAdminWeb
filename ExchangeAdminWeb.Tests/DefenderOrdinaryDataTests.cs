using System.Globalization;
using System.Net;
using System.Reflection;
using CsvHelper;
using ExchangeAdminWeb.Authorization;
using ExchangeAdminWeb.Components.Pages;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

public class DefenderOrdinaryDataTests
{
    [Theory]
    [InlineData("osBuild", "22631", "22631")]
    [InlineData("OsBuild", "0", "0")]
    [InlineData("osBuild", "\"19045.1234\"", "19045.1234")]
    public async Task OsBuildSurvivesTheApiRowAndActualCsv(
        string property, string jsonValue, string expected)
    {
        var result = await ListAsync(
            $"{{\"value\":[{{\"id\":\"m1\",\"{property}\":{jsonValue}}}]}}");

        Assert.Equal(DefenderDeviceListOutcome.Complete, result.Outcome);
        var device = Assert.Single(result.Devices);
        Assert.Equal(expected, device.OsBuild);

        var row = Assert.Single(CsvRows(DefenderEndpointDevices.BuildCsv(
            result.Devices, result.DiscoveryEnrichment)));
        Assert.Equal(expected, row["OsBuild"]);
    }

    [Theory]
    [InlineData("New York\\, Building A", "New York, Building A", "BUILDING A")]
    [InlineData("New York\\2C Building A", "New York, Building A", "BUILDING A")]
    [InlineData("Montr\\C3\\A9al\\, Building A", "Montr\u00e9al, Building A", "MONTR\u00c9AL")]
    [InlineData("Branch\\\\", "Branch\\", "BRANCH\\")]
    public async Task EscapedSiteNamesSurviveDirectoryMappingPageFilteringAndCsv(
        string escapedName, string expected, string filter)
    {
        var commands = Substitute.For<ISectionAccessDirectoryCommands>();
        commands.Invoke("Get-ADRootDSE", Arg.Any<IReadOnlyDictionary<string, object?>>())
            .Returns(new DirectoryCommandOutcome(
                [new DirectoryObject(new Dictionary<string, object?>
                {
                    ["configurationNamingContext"] = "CN=Configuration,DC=example,DC=test"
                })], null));
        commands.Invoke("Get-ADObject", Arg.Any<IReadOnlyDictionary<string, object?>>())
            .Returns(new DirectoryCommandOutcome(
            [
                Subnet("10.0.0.0/24", $"CN={escapedName},CN=Sites,CN=Configuration,DC=example,DC=test"),
                Subnet("10.0.1.0/24", "CN=London,CN=Sites,CN=Configuration,DC=example,DC=test")
            ], null));

        var result = await ListAsync("""
            {"value":[
                {"id":"m1","lastIpAddress":"10.0.0.17"},
                {"id":"m2","lastIpAddress":"10.0.1.17"},
                {"id":"m3","lastIpAddress":"10.0.2.17"}
            ]}
            """, new AdSiteDirectory(() => commands));

        Assert.Equal(DefenderDeviceListOutcome.Complete, result.Outcome);
        Assert.True(result.SiteMapRead);
        Assert.Equal([expected, "London", ""], result.Devices.Select(d => d.Site));

        // Exercise the compiled page properties that select both the visible and exported set.
        var page = new DefenderEndpointDevices();
        SetField(page, "result", result);
        SetField(page, "appliedSite", filter);
        Assert.Equal("m1", Assert.Single(Devices(page, "FilteredDevices")).Id);
        var exported = Assert.Single(CsvRows(DefenderEndpointDevices.BuildCsv(
            Devices(page, "ExportableDevices"), result.DiscoveryEnrichment)));
        Assert.Equal("m1", exported["DeviceId"]);
        Assert.Equal(expected, exported["Site"]);

        SetField(page, "appliedSite", "LONDON");
        Assert.Equal("m2", Assert.Single(Devices(page, "FilteredDevices")).Id);
        SetField(page, "appliedSite", "no matching site");
        Assert.Empty(Devices(page, "FilteredDevices"));
    }

    private static DirectoryObject Subnet(string cidr, string siteDn) =>
        new(new Dictionary<string, object?> { ["cn"] = cidr, ["siteObject"] = siteDn });

    private static async Task<DefenderDeviceListResult> ListAsync(
        string body, AdSiteDirectory? directory = null)
    {
        using var http = new HttpClient(new InventoryHandler(body));
        var client = new DefenderApiClient("tenant", "client", "secret",
            DefenderApiClient.DefenderBaseUrl, DefenderApiClient.DefenderTokenScope, http);
        var service = new DefenderEndpointDeviceService(
            () => Task.FromResult<DefenderApiClient?>(client), "50", siteDirectory: directory);
        return await service.ListDevicesAsync();
    }

    private sealed class InventoryHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri!.Host == "login.microsoftonline.com"
                    ? """{"access_token":"local-test-token","expires_in":3600}"""
                    : body)
            });
    }

    private static List<Dictionary<string, string>> CsvRows(string text)
    {
        using var reader = new StringReader(text);
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
        Assert.True(csv.Read());
        csv.ReadHeader();
        var records = new List<Dictionary<string, string>>();
        while (csv.Read())
            records.Add(csv.HeaderRecord!.ToDictionary(header => header, header => csv.GetField(header) ?? ""));
        return records;
    }

    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    private static void SetField(DefenderEndpointDevices page, string name, object value) =>
        typeof(DefenderEndpointDevices).GetField(name, PrivateInstance)!.SetValue(page, value);

    private static IReadOnlyList<DefenderDevice> Devices(DefenderEndpointDevices page, string name) =>
        Assert.IsAssignableFrom<IReadOnlyList<DefenderDevice>>(
            typeof(DefenderEndpointDevices).GetProperty(name, PrivateInstance)!.GetValue(page));
}
