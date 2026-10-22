using System.Collections;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Reflection;
using ExchangeAdminWeb.Components.Pages;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Audit finding aud-a23f: <c>GetMigrationBatchesAsync</c> caught a row that threw while being read - a count
/// <c>Convert.ToInt32</c> could not read, say - logged it and went on, so the page listed the other batches
/// as if they were all there was.
/// </summary>
/// <remarks>
/// Behaviour, not text. The real <see cref="MigrationService"/> on a real pool, whose connect seam hands out
/// an in-process runspace carrying a stub <c>Get-MigrationBatch</c> that emits the rows each test builds (the
/// <c>MigrationReportedTimeTests</c> idiom), and the page's compiled render tree and CSV. No Exchange.
/// </remarks>
public sealed class MigrationBatchReadTests : IDisposable
{
    private const string Readable = "Wave 12 / Operations";
    private const string Throwing = "Wave 13 / Finance";

    private static readonly string[] CountFields = ["TotalCount", "PendingCount", "SyncedCount", "FinalizedCount", "FailedCount"];

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "migration-rows-" + Guid.NewGuid().ToString("N"));
    private readonly ArrayList _rows = ArrayList.Synchronized(new ArrayList());
    private ExoConnectionPool? _pool;

    public void Dispose()
    {
        _pool?.Dispose();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    // ---- the harness ------------------------------------------------------------------------

    /// <summary>A row as the module hands one back: a property bag. No Identity property when it is null.</summary>
    private static PSObject Row(string? identity, params (string Name, object? Value)[] fields)
    {
        var row = new PSObject();
        if (identity != null)
            row.Properties.Add(new PSNoteProperty("Identity", identity));
        row.Properties.Add(new PSNoteProperty("Status", "Synced"));
        foreach (var (name, value) in fields)
            row.Properties.Add(new PSNoteProperty(name, value));
        return row;
    }

    /// <summary>A live object whose count cannot be fetched: reading the property throws, as a code or script property can.</summary>
    public sealed class RowWhoseCountThrows
    {
        public string Identity => Throwing;
        public string Status => "Synced";
        public int TotalCount => throw new InvalidOperationException("the count is not available");
    }

    /// <summary>A live object whose Identity cannot be fetched.</summary>
    public sealed class RowWhoseIdentityThrows
    {
        public string Identity => throw new InvalidOperationException("the identity is not available");
        public string Status => "Synced";
    }

    /// <summary>A count this app cannot read, and the marker it is shown as.</summary>
    private static (object Value, string Marker) UnreadableCount(string form)
    {
        switch (form)
        {
            case "text with a group separator":
                return ("1,205", "reported in a form this app cannot read (System.String): 1,205");
            case "a fraction":
                return (12.5, "reported in a form this app cannot read (System.Double): 12.5");
            default:
                var deserialized = new PSObject();
                deserialized.TypeNames.Insert(0, "Deserialized.Contoso.Data.ItemCount");
                deserialized.Properties.Add(new PSNoteProperty("Value", 1205));
                return (deserialized, "reported in a form this app cannot read (Deserialized.Contoso.Data.ItemCount): @{Value=1205}");
        }
    }

    private MigrationService CreateService()
    {
        Directory.CreateDirectory(_directory);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Audit:LogRoot"] = _directory,
            ["ExchangeOnline:AppId"] = "11111111-1111-1111-1111-111111111111",
            ["ExchangeOnline:Organization"] = "contoso.onmicrosoft.com",
        }).Build();
        var environment = Substitute.For<IWebHostEnvironment>();
        environment.ContentRootPath.Returns(_directory);
        var store = TestConfigStore.Create(_directory);
        var catalog = new ModuleCatalog();
        var moduleConfig = new ModuleConfigService(catalog, environment,
            new ModuleConfigRepository(store), Substitute.For<ILogger<ModuleConfigService>>());
        var enablement = new ModuleEnablementService(catalog, environment, moduleConfig,
            new ModuleEnablementRepository(store), config, Substitute.For<ILogger<ModuleEnablementService>>());
        enablement.SaveEnablement(new Dictionary<string, bool> { [ExoConnectionPool.ConfigModuleKey] = true });
        var trace = new OperationTraceService(config,
            new JsonlLogService(config, Substitute.For<ILogger<JsonlLogService>>()));

        _pool = new ExoConnectionPool(config, moduleConfig, enablement,
            Substitute.For<ILogger<ExoConnectionPool>>(), trace, (_, generation, _) =>
            {
                var state = InitialSessionState.CreateDefault2();
                state.Variables.Add(new SessionStateVariableEntry("Rows", _rows, null));
                state.Commands.Add(new SessionStateFunctionEntry("Disconnect-ExchangeOnline", "[CmdletBinding()] param([switch] $Confirm)"));
                state.Commands.Add(new SessionStateFunctionEntry("Get-MigrationBatch", """
                    [CmdletBinding()]
                    param([string] $Identity)
                    foreach ($row in $Rows) { $row }
                    """));
                state.Commands.Add(new SessionStateFunctionEntry("Get-MigrationUser", """
                    [CmdletBinding()]
                    param([string] $BatchId, [string] $ResultSize)
                    """));
                var runspace = RunspaceFactory.CreateRunspace(state);
                runspace.Open();
                var powershell = PowerShell.Create();
                powershell.Runspace = runspace;
                return new PooledRunspace(runspace, powershell, generation);
            });

        return new MigrationService(config, _pool, null!, Substitute.For<ILogger<MigrationService>>(),
            moduleConfig, null!, trace, null!, null!);
    }

    /// <summary>The Status tab after a load that returned <paramref name="list"/>, with no batch open.</summary>
    private static string ListRendered(MigrationBatchList list)
    {
        var page = Page(list);
        return page.RenderText();
    }

    /// <summary>The Status tab with <paramref name="batch"/> open and its (empty) mailbox list loaded.</summary>
    private static async Task<string> OpenBatchRendered(MigrationService service, MigrationBatchInfo batch)
    {
        var page = Page(new MigrationBatchList([batch], []));
        PageField("expandedBatch").SetValue(page, batch.BatchName);
        ((HashSet<string>)PageField("selectedBatches").GetValue(page)!).Add(batch.BatchName);
        typeof(Migration).GetProperty("MigrationSvc", Members)!.SetValue(page, service);
        await (Task)typeof(Migration).GetMethod("LoadMailboxesFor", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(page, [batch.BatchName])!;
        return page.RenderText();
    }

    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private static RenderableMigration Page(MigrationBatchList list)
    {
        var page = new RenderableMigration();
        var environment = Substitute.For<IWebHostEnvironment>();
        typeof(Migration).GetProperty("ReportStore", Members)!.SetValue(page, new MigrationReportStore(environment));
        typeof(Migration).GetProperty("Logger", Members)!.SetValue(page, Substitute.For<ILogger<Migration>>());
        typeof(Migration).GetProperty("Progress", Members)!
            .SetValue(page, new ExchangeAdminWeb.Services.Progress.ActivityProgressService());
        PageField("tabIndex").SetValue(page, 2);
        PageField("batchesLoaded").SetValue(page, true);
        PageField("canManage").SetValue(page, true);
        PageField("migrationBatches").SetValue(page, list.Batches);
        PageField("unreadableBatches").SetValue(page, list.Unreadable);
        return page;
    }

    private static FieldInfo PageField(string name) =>
        typeof(Migration).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>The page's own ordering of <paramref name="rows"/> by a column, in a direction.</summary>
    private static List<string> Sorted(string column, bool ascending, IEnumerable<MigrationBatchInfo> rows)
    {
        var page = new Migration();
        PageField("batchSortColumn").SetValue(page, column);
        PageField("batchSortAsc").SetValue(page, ascending);
        var sorted = (IEnumerable<MigrationBatchInfo>)typeof(Migration).GetMethod("SortBatches", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(page, [rows])!;
        return sorted.Select(b => b.BatchName).ToList();
    }

    private static string[] Lines(string csv) =>
        csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToArray();

    private sealed class RenderableMigration : Migration
    {
#pragma warning disable BL0006 // the page's compiled render tree is read directly; no renderer is involved
        public string RenderText()
        {
            using var builder = new RenderTreeBuilder();
            BuildRenderTree(builder);
            var frames = builder.GetFrames();
            return string.Concat(frames.Array.Take(frames.Count).Select(frame => frame.FrameType switch
            {
                RenderTreeFrameType.Text => frame.TextContent,
                RenderTreeFrameType.Markup => frame.MarkupContent,
                _ => "",
            }));
        }
#pragma warning restore BL0006
    }

    // ---- the guards -------------------------------------------------------------------------

    [Fact]
    public async Task ARowThatThrowsWhileBeingRead_IsListedWithItsIdentityAndReason_BesideTheBatchesThatRead()
    {
        _rows.Add(Row(Readable, ("TotalCount", 3)));
        _rows.Add(new RowWhoseCountThrows());

        var list = await CreateService().GetMigrationBatchesAsync();

        Assert.Equal([Readable], list.Batches.Select(b => b.BatchName));
        var unreadable = Assert.Single(list.Unreadable);
        Assert.Equal(Throwing, unreadable.Identity);
        Assert.Contains("TotalCount", unreadable.Reason, StringComparison.Ordinal);
        Assert.Contains("the count is not available", unreadable.Reason, StringComparison.Ordinal);

        var rendered = ListRendered(list);
        Assert.Contains($"1 batch could not be read: {Throwing}: {unreadable.Reason}", rendered, StringComparison.Ordinal);
        Assert.Contains(Readable, rendered, StringComparison.Ordinal);

        var lines = Lines(Migration.BuildCsv(list.Batches, list.Unreadable));
        Assert.Equal(3, lines.Length);
        Assert.StartsWith($"{Readable},Synced,", lines[1], StringComparison.Ordinal);
        Assert.StartsWith($"{Throwing},\"could not be read: ", lines[2], StringComparison.Ordinal);
        Assert.Contains("the count is not available", lines[2], StringComparison.Ordinal);
        Assert.EndsWith(",,,,,,,,,", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARowWhoseIdentityCannotBeRead_IsListedAsSuch_WithTheReason()
    {
        _rows.Add(new RowWhoseIdentityThrows());

        var list = await CreateService().GetMigrationBatchesAsync();

        Assert.Empty(list.Batches);
        var unreadable = Assert.Single(list.Unreadable);
        Assert.Null(unreadable.Identity);
        Assert.StartsWith("a row whose Identity could not be read: ", unreadable.Reason, StringComparison.Ordinal);
        Assert.Contains("the identity is not available", unreadable.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARowWithNoIdentity_IsListedAsUnreadable_NotAsABatchNamedUnknown()
    {
        _rows.Add(Row(null, ("TotalCount", 3)));
        _rows.Add(Row("   ", ("TotalCount", 4)));

        var list = await CreateService().GetMigrationBatchesAsync();

        Assert.Empty(list.Batches);
        Assert.Equal(2, list.Unreadable.Count);
        Assert.All(list.Unreadable, row => Assert.Equal(new UnreadableMigrationBatch(null, "a row with no Identity"), row));

        // With nothing readable, the page names what it could not read, and does not say there are no batches.
        var rendered = ListRendered(list);
        Assert.Contains("2 batches could not be read: a row with no Identity; a row with no Identity", rendered, StringComparison.Ordinal);
        Assert.Contains("No migration batch could be read", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("There are currently no migration batches", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Unknown", rendered, StringComparison.Ordinal);

        var lines = Lines(Migration.BuildCsv(list.Batches, list.Unreadable));
        Assert.Equal([",could not be read: a row with no Identity,,,,,,,,,", ",could not be read: a row with no Identity,,,,,,,,,"], lines[1..]);
    }

    [Fact]
    public async Task NoUnreadableRow_ShowsNoWarning()
    {
        _rows.Add(Row(Readable, ("TotalCount", 3)));

        var list = await CreateService().GetMigrationBatchesAsync();

        Assert.Empty(list.Unreadable);
        Assert.DoesNotContain("could not be read", ListRendered(list), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("text with a group separator")]
    [InlineData("a fraction")]
    [InlineData("a deserialized object")]
    public async Task ACountThisAppCannotRead_KeepsItsBatch_AndIsShownAsTheMarkerWhereTheCountGoes(string form)
    {
        var (value, marker) = UnreadableCount(form);
        _rows.Add(Row(Readable, [.. CountFields.Select(field => (field, (object?)value)), ("WorkflowStage", "Injection")]));
        var service = CreateService();

        var list = await service.GetMigrationBatchesAsync();

        var batch = Assert.Single(list.Batches);
        Assert.Empty(list.Unreadable);
        Assert.All([batch.TotalCount, batch.PendingCount, batch.SyncedCount, batch.FinalizedCount, batch.FailedCount],
            count => Assert.Null(count.Count));

        // The row's Total, Synced and Failed cells and the open batch's Total - none of them a number, and
        // the failed cell not the "0" it shows for none.
        var rendered = await OpenBatchRendered(service, batch);
        Assert.Equal(4, rendered.Split(marker).Length - 1);
        Assert.Contains($"Total: {marker}", rendered.Replace("</strong>", "", StringComparison.Ordinal), StringComparison.Ordinal);

        // A pending count and a total that cannot be read are neither "not started" nor "no mailboxes".
        Assert.DoesNotContain("Batch not started", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("contains no mailboxes", rendered, StringComparison.Ordinal);
        Assert.Contains("Mailbox details are not available yet.", rendered, StringComparison.Ordinal);

        // Total, Synced, Finalized and Failed in the CSV.
        Assert.Equal(4, Migration.BuildCsv(list.Batches, list.Unreadable).Split(marker).Length - 1);
    }

    [Theory]
    [InlineData("a PSObject wrapping it")]
    [InlineData("an Int64")]
    [InlineData("an Int16")]
    [InlineData("text")]
    [InlineData("a whole Double")]
    [InlineData("a whole Decimal")]
    public async Task ACountReportedInAnotherForm_IsReadAsThatCount(string form)
    {
        object value = form switch
        {
            "a PSObject wrapping it" => PSObject.AsPSObject(12),
            "an Int64" => 12L,
            "an Int16" => (short)12,
            "text" => " 12 ",
            "a whole Double" => 12.0,
            _ => 12m,
        };
        _rows.Add(Row(Readable, [.. CountFields.Select(field => (field, (object?)value))]));

        var batch = Assert.Single((await CreateService().GetMigrationBatchesAsync()).Batches);

        Assert.All([batch.TotalCount, batch.PendingCount, batch.SyncedCount, batch.FinalizedCount, batch.FailedCount],
            count => Assert.Equal(12, count.Count));
        Assert.EndsWith(",12,12,12,12,", Lines(Migration.BuildCsv([batch], []))[1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Total", true)]
    [InlineData("Total", false)]
    [InlineData("Synced", true)]
    [InlineData("Synced", false)]
    [InlineData("Failed", true)]
    [InlineData("Failed", false)]
    public async Task TheCountSorts_PutACountThisAppCannotReadAfterEveryCount_InBothDirections(string column, bool ascending)
    {
        var field = column + "Count";
        // Input order puts the unreadable row first, so a sort that took it for the lowest would leave it there.
        _rows.Add(Row("unreadable", (field, "1,205")));
        _rows.Add(Row("five", (field, 5)));
        _rows.Add(Row("two", (field, 2)));
        _rows.Add(Row("nine", (field, 9)));

        var batches = (await CreateService().GetMigrationBatchesAsync()).Batches;

        Assert.Equal(ascending ? ["two", "five", "nine", "unreadable"] : ["nine", "five", "two", "unreadable"],
            Sorted(column, ascending, batches));
    }

    [Theory]
    [InlineData("text that is not a flag", null)]
    [InlineData("a PSObject wrapping true", true)]
    [InlineData("nothing", false)]
    public async Task AnAutoStartThisAppCannotRead_KeepsItsBatch_AndIsNotTakenForFalse(string form, bool? expected)
    {
        object? value = form switch
        {
            "text that is not a flag" => "yes",
            "a PSObject wrapping true" => PSObject.AsPSObject(true),
            _ => null,
        };
        _rows.Add(Row(Readable, ("AutoStart", value)));

        var list = await CreateService().GetMigrationBatchesAsync();

        Assert.Empty(list.Unreadable);
        Assert.Equal(expected, Assert.Single(list.Batches).AutoStart);
    }
}
