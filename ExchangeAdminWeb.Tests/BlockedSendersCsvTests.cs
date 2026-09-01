using ExchangeAdminWeb.Components.Pages;
using ExchangeAdminWeb.Models.BlockedSenders;
using Xunit;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Guards the BlockedSenders CSV export (docs/ModuleCsvExport-Plan.md AC2-AC4, S4):
/// the projector's column shape (AC3) and the page's wiring of the download button,
/// audit event, and empty-set guard (AC2/AC4). Source-text guard for the wiring because
/// there is no bUnit harness in this repo.
/// </summary>
public class BlockedSendersCsvTests
{
    [Fact]
    public void BuildCsv_HeaderMatchesSpec()
    {
        var csv = BlockedSenders.BuildCsv([]);
        var header = csv.Split('\n')[0].TrimEnd('\r');

        Assert.Equal("SenderAddress,Reason,Blocked", header);
    }

    [Fact]
    public void BuildCsv_MapsARow()
    {
        var rows = new List<BlockedSenderInfo>
        {
            new() { SenderAddress = "spammer@contoso.com", Reason = "Outbound spam", BlockedDateRaw = "1/2/2026 3:04:05 PM" },
            new() { SenderAddress = "noreason@contoso.com", Reason = null, BlockedDateRaw = null },
        };

        var csv = BlockedSenders.BuildCsv(rows);
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r')).ToArray();

        Assert.Equal("spammer@contoso.com,Outbound spam,1/2/2026 3:04:05 PM", lines[1]);
        Assert.Equal("noreason@contoso.com,,", lines[2]);
    }

    [Fact]
    public void BlockedSenders_WiresDownloadCsv()
    {
        var text = File.ReadAllText(
            AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "BlockedSenders.razor"));

        var start = text.IndexOf("private async Task DownloadCsvAsync()", StringComparison.Ordinal);
        Assert.True(start >= 0, "DownloadCsvAsync method not found in BlockedSenders.razor.");
        var end = text.IndexOf("\n    private ", start + 1, StringComparison.Ordinal);
        var body = end > start ? text[start..end] : text[start..];

        Assert.Contains("BuildCsv", body);
        Assert.Contains("downloadFile", body);
        Assert.Contains("if (blockedSenders.Count == 0)", body);
        Assert.Contains("\"ExportCsv\"", body);
        Assert.Contains("LogModuleAction", body);
    }
}
