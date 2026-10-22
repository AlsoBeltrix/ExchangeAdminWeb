using System.Globalization;
using System.Text.RegularExpressions;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using static ExchangeAdminWeb.Tests.MigrationStubExchange;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Audit finding aud-a23i, part 5. The Migration page's three downloads were named from the web server's clock with no
/// zone in the name, so a file named for 14:05 said nothing about whose 14:05. Each is now named in UTC, and says so
/// with a trailing Z.
/// </summary>
/// <remarks>
/// The names are read off the browser call itself, through a recording JS runtime. This host runs on Eastern time, so
/// a name taken from the server's clock lands hours outside the run's UTC window.
/// </remarks>
public sealed class MigrationExportFileNameTests : IDisposable
{
    private const string Batch = "Wave 12";
    private const string Address = "alice@contoso.test";

    private readonly MigrationStubExchange _exchange = new();

    public void Dispose() => _exchange.Dispose();

    private (RenderableMigration Page, RecordingJs Js) Page()
    {
        var page = _exchange.Page(_exchange.Service());
        var js = new RecordingJs();
        Set(page, "JS", js);
        return (page, js);
    }

    // The stamp's instant, read as UTC, inside the window the download ran in (to the second).
    private static void InsideTheRun(string stamp, DateTime before, DateTime after)
    {
        var named = DateTime.ParseExact(stamp, "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        Assert.InRange(named, before.AddTicks(-(before.Ticks % TimeSpan.TicksPerSecond)), after);
    }

    [Fact]
    public async Task TheBatchListCsv_IsNamedInUtc_AndSaysSo()
    {
        var (page, js) = Page();
        Field("migrationBatches").SetValue(page, new List<MigrationBatchInfo> { new() { BatchName = Batch, Status = "Synced" } });

        var before = DateTime.UtcNow;
        await Call(page, "DownloadCsvAsync");
        var after = DateTime.UtcNow;

        var name = Assert.Single(js.FileNames);
        var match = Regex.Match(name, @"^Migration_(\d{8}_\d{6})Z\.csv$");
        Assert.True(match.Success, name);
        InsideTheRun(match.Groups[1].Value, before, after);
    }

    [Fact]
    public async Task TheReportZip_IsNamedInUtc_AndSaysSo()
    {
        var (page, js) = Page();
        new MigrationReportStore(_exchange.Environment).Write(Batch, Address, "the move report", DateTime.UtcNow);
        Field("expandedBatch").SetValue(page, Batch);
        ((HashSet<string>)Field("selectedMailboxes").GetValue(page)!).Add(Address);

        var before = DateTime.UtcNow;
        await Call(page, "DownloadReportExportAsync");
        var after = DateTime.UtcNow;

        var name = Assert.Single(js.FileNames);
        var match = Regex.Match(name, @"^MigrationReports_Wave_12_(\d{8}_\d{6})Z\.zip$");
        Assert.True(match.Success, name);
        InsideTheRun(match.Groups[1].Value, before, after);
    }

    // The report on screen is named for when it was taken - an instant this app recorded in UTC - and not for the server's
    // reading of it.
    [Fact]
    public async Task TheOpenReport_IsNamedForWhenItWasTaken_InUtc()
    {
        var (page, js) = Page();
        Field("reportUser").SetValue(page, Address);
        Field("userReport").SetValue(page, "the move report");
        Field("reportFetchedAtUtc").SetValue(page, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));

        await Call(page, "ExportOpenReportAsync");

        Assert.Equal(["MigrationReport_alice_contoso.test_20260102_030405Z.txt"], js.FileNames);
    }
}
