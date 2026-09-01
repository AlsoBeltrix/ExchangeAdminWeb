using ExchangeAdminWeb.Components.Pages;
using ExchangeAdminWeb.Models;
using Xunit;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Guards the Migration status CSV export (docs/ModuleCsvExport-Plan.md AC2-AC4, S6):
/// the projector's column shape (AC3, batch-level only per the plan's per-user-rows
/// non-goal) and the page's wiring of the download button, audit event, and empty-set
/// guard (AC2/AC4). Source-text guard for the wiring because there is no bUnit harness
/// in this repo.
/// </summary>
public class MigrationCsvTests
{
    [Fact]
    public void BuildCsv_HeaderMatchesSpec()
    {
        var csv = Migration.BuildCsv([]);
        var header = csv.Split('\n')[0].TrimEnd('\r');

        Assert.Equal(
            "BatchName,Status,Direction,Created,Started,Completed,Total,Synced,Finalized,Failed,TargetEndpoint",
            header);
    }

    [Fact]
    public void BuildCsv_MapsARow()
    {
        var created = new DateTime(2026, 1, 2, 10, 30, 0, DateTimeKind.Utc);
        var rows = new List<MigrationBatchInfo>
        {
            new()
            {
                BatchName = "Batch-A",
                Status = "Synced",
                Direction = MigrationDirection.ToCloud,
                CreatedDateTime = created,
                StartDateTime = null,
                CompletedDateTime = null,
                TotalCount = 10,
                SyncedCount = 8,
                FinalizedCount = 2,
                FailedCount = 0,
                TargetEndpoint = "hybrid1",
            }
        };

        var csv = Migration.BuildCsv(rows);
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r')).ToArray();

        // The date format includes a comma, so CsvHelper's WriteField quotes the cell -
        // that is correct CSV output (AC1), not a formatting bug.
        var expectedCreated = created.ToLocalTime().ToString("MMM dd, yyyy HH:mm");
        Assert.Equal(
            $"Batch-A,Synced,ToCloud,\"{expectedCreated}\",,,10,8,2,0,hybrid1",
            lines[1]);
    }

    [Fact]
    public void Migration_WiresDownloadCsv()
    {
        var text = File.ReadAllText(
            AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "Migration.razor"));

        var start = text.IndexOf("private async Task DownloadCsvAsync()", StringComparison.Ordinal);
        Assert.True(start >= 0, "DownloadCsvAsync method not found in Migration.razor.");
        var end = text.IndexOf("\n    private ", start + 1, StringComparison.Ordinal);
        var body = end > start ? text[start..end] : text[start..];

        Assert.Contains("BuildCsv", body);
        Assert.Contains("downloadFile", body);
        Assert.Contains("GetSortedBatches", body);
        Assert.Contains("if (migrationBatches is not { Count: > 0 })", body);
        Assert.Contains("\"ExportCsv\"", body);
        Assert.Contains("LogModuleAction", body);
    }
}
