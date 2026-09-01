using ExchangeAdminWeb.Components.Pages;
using ExchangeAdminWeb.Services;
using Xunit;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Guards the DhcpAuthorization CSV export (docs/ModuleCsvExport-Plan.md AC2-AC4, S2):
/// the projector's column shape (AC3) and the page's wiring of the download button,
/// audit event, and empty-set guard (AC2/AC4). Source-text guard for the wiring because
/// there is no bUnit harness in this repo.
/// </summary>
public class DhcpAuthorizationCsvTests
{
    [Fact]
    public void BuildCsv_HeaderMatchesSpec()
    {
        var csv = DhcpAuthorization.BuildCsv([]);
        var header = csv.Split('\n')[0].TrimEnd('\r');

        Assert.Equal("DnsName,IpAddress", header);
    }

    [Fact]
    public void BuildCsv_MapsARow()
    {
        var rows = new List<DhcpServerEntry>
        {
            new() { DnsName = "dhcp01.contoso.com", IpAddress = "10.0.0.1" }
        };

        var csv = DhcpAuthorization.BuildCsv(rows);
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r')).ToArray();

        Assert.Equal("dhcp01.contoso.com,10.0.0.1", lines[1]);
    }

    [Fact]
    public void DhcpAuthorization_WiresDownloadCsv()
    {
        var text = File.ReadAllText(
            AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "DhcpAuthorization.razor"));

        var start = text.IndexOf("private async Task DownloadCsvAsync()", StringComparison.Ordinal);
        Assert.True(start >= 0, "DownloadCsvAsync method not found in DhcpAuthorization.razor.");
        var end = text.IndexOf("\n    private ", start + 1, StringComparison.Ordinal);
        var body = end > start ? text[start..end] : text[start..];

        Assert.Contains("BuildCsv", body);
        Assert.Contains("downloadFile", body);
        Assert.Contains("if (servers.Count == 0)", body);
        Assert.Contains("\"ExportCsv\"", body);
        Assert.Contains("LogModuleAction", body);
    }
}
