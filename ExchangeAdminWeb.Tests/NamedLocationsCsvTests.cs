using ExchangeAdminWeb.Components.Pages;
using ExchangeAdminWeb.Services;
using Xunit;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Guards the NamedLocations CSV export (docs/ModuleCsvExport-Plan.md AC2-AC4, S3):
/// the projector's column shape (AC3) and the page's wiring of the download button,
/// audit event, and empty-set guard (AC2/AC4). Source-text guard for the wiring because
/// there is no bUnit harness in this repo.
/// </summary>
public class NamedLocationsCsvTests
{
    [Fact]
    public void BuildCsv_HeaderMatchesSpec()
    {
        var csv = NamedLocations.BuildCsv([]);
        var header = csv.Split('\n')[0].TrimEnd('\r');

        Assert.Equal("Name,Type,Trusted,IpRanges,CountryCodes,IncludeUnknownCountries,Created,Modified", header);
    }

    [Fact]
    public void BuildCsv_MapsARow()
    {
        var rows = new List<NamedLocation>
        {
            new()
            {
                DisplayName = "Corp Trusted Ranges",
                LocationType = NamedLocationType.Ip,
                IsTrusted = true,
                IpRanges = ["10.0.0.0/8", "192.168.1.0/24"],
                CountryCodes = [],
                IncludeUnknownCountries = false,
                CreatedDateTime = "2026-01-01T00:00:00Z",
                ModifiedDateTime = null!
            }
        };

        var csv = NamedLocations.BuildCsv(rows);
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r')).ToArray();

        Assert.Equal(
            "Corp Trusted Ranges,Ip,true,10.0.0.0/8; 192.168.1.0/24,,false,2026-01-01T00:00:00Z,",
            lines[1]);
    }

    [Fact]
    public void NamedLocations_WiresDownloadCsv()
    {
        var text = File.ReadAllText(
            AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "NamedLocations.razor"));

        var start = text.IndexOf("private async Task DownloadCsvAsync()", StringComparison.Ordinal);
        Assert.True(start >= 0, "DownloadCsvAsync method not found in NamedLocations.razor.");
        var end = text.IndexOf("\n    private ", start + 1, StringComparison.Ordinal);
        var body = end > start ? text[start..end] : text[start..];

        Assert.Contains("BuildCsv", body);
        Assert.Contains("downloadFile", body);
        Assert.Contains("if (locations.Count == 0)", body);
        Assert.Contains("\"ExportCsv\"", body);
        Assert.Contains("LogModuleAction", body);
    }
}
