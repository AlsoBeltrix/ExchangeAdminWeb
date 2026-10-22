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
/// as if they were all there was. aud-a23g: a call that failed was an empty list, shown as "No migration
/// batches found"; a mailbox count <c>Convert.ToInt64</c> could not read lost the mailbox list; a negative
/// count read as a count; a cancellation read as an unreadable row.
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
    private readonly ArrayList _users = ArrayList.Synchronized(new ArrayList());

    // aud-a23g. Empty: Get-MigrationBatch emits the rows. Otherwise how it fails - "throw" (a terminating
    // error) or "error" (a non-terminating one, Write-Error) - and the message.
    private readonly ArrayList _failure = ArrayList.Synchronized(new ArrayList());
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

    /// <summary>aud-a23g. A live object whose count is fetched while the load is being cancelled.</summary>
    public sealed class RowWhoseReadIsCancelled(Exception cancellation)
    {
        public string Identity => Throwing;
        public string Status => "Synced";
        public int TotalCount => throw cancellation;
    }

    /// <summary>aud-a23g. A mailbox as Get-MigrationUser hands one back: a property bag.</summary>
    private static PSObject User(string email, params (string Name, object? Value)[] fields)
    {
        var user = new PSObject();
        user.Properties.Add(new PSNoteProperty("Identity", email));
        user.Properties.Add(new PSNoteProperty("Status", "Synced"));
        foreach (var (name, value) in fields)
            user.Properties.Add(new PSNoteProperty(name, value));
        return user;
    }

    private static IEnumerable<Exception> Chain(Exception error)
    {
        for (Exception? cause = error; cause != null; cause = cause.InnerException)
            yield return cause;
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

    /// <param name="connects">aud-a23g. False: opening the connection throws, before any cmdlet runs.</param>
    private MigrationService CreateService(bool connects = true)
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
                if (!connects)
                    throw new InvalidOperationException("the connection could not be opened");

                var state = InitialSessionState.CreateDefault2();
                state.Variables.Add(new SessionStateVariableEntry("Rows", _rows, null));
                state.Variables.Add(new SessionStateVariableEntry("Users", _users, null));
                state.Variables.Add(new SessionStateVariableEntry("Failure", _failure, null));
                state.Commands.Add(new SessionStateFunctionEntry("Disconnect-ExchangeOnline", "[CmdletBinding()] param([switch] $Confirm)"));
                state.Commands.Add(new SessionStateFunctionEntry("Get-MigrationBatch", """
                    [CmdletBinding()]
                    param([string] $Identity)
                    if ($Failure.Count -gt 0) {
                        if ($Failure[0] -eq 'throw') { throw $Failure[1] }
                        Write-Error $Failure[1]
                        return
                    }
                    foreach ($row in $Rows) { $row }
                    """));
                state.Commands.Add(new SessionStateFunctionEntry("Get-MigrationUser", """
                    [CmdletBinding()]
                    param([string] $BatchId, [string] $ResultSize)
                    foreach ($user in $Users) { $user }
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
    private static async Task<string> OpenBatchRendered(MigrationService service, MigrationBatchInfo batch) =>
        (await OpenBatchPage(service, batch)).Rendered;

    /// <summary>aud-a23i. <see cref="OpenBatchRendered"/>, with the page, for what it holds.</summary>
    private static async Task<(RenderableMigration Page, string Rendered)> OpenBatchPage(MigrationService service, MigrationBatchInfo batch)
    {
        var page = Page(new MigrationBatchList([batch], []));
        PageField("expandedBatch").SetValue(page, batch.BatchName);
        ((HashSet<string>)PageField("selectedBatches").GetValue(page)!).Add(batch.BatchName);
        typeof(Migration).GetProperty("MigrationSvc", Members)!.SetValue(page, service);
        await (Task)typeof(Migration).GetMethod("LoadMailboxesFor", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(page, [batch.BatchName])!;
        return (page, page.RenderText());
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

    /// <summary>aud-a23g. The page's own ordering of mailbox rows by a column, in a direction.</summary>
    private static List<string> SortedMailboxes(string column, bool ascending, IEnumerable<MigrationUserInfo> rows)
    {
        var page = new Migration();
        PageField("mailboxSortColumn").SetValue(page, column);
        PageField("mailboxSortAsc").SetValue(page, ascending);
        var sorted = (IEnumerable<MigrationUserInfo>)typeof(Migration).GetMethod("SortMailboxes", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(page, [rows])!;
        return sorted.Select(u => u.EmailAddress.Split('@')[0]).ToList();
    }

    /// <summary>aud-a23g. The Status tab before any load, over <paramref name="service"/>.</summary>
    private static RenderableMigration PageOver(MigrationService service)
    {
        var page = Page(new MigrationBatchList([], []));
        PageField("batchesLoaded").SetValue(page, false);
        PageField("migrationBatches").SetValue(page, null);
        typeof(Migration).GetProperty("MigrationSvc", Members)!.SetValue(page, service);
        return page;
    }

    /// <summary>aud-a23g. The page's own batch-list load: what Refresh, the tab and a bulk action's reload run.</summary>
    private static Task LoadBatchList(Migration page) =>
        (Task)typeof(Migration).GetMethod("LoadBatchList", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, [])!;

    private static HashSet<string> Ticked(Migration page) => (HashSet<string>)PageField("selectedBatches").GetValue(page)!;

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

    // ---- aud-a23g -----------------------------------------------------------------------------

    // aud-a23g, the finding: a Get-MigrationBatch call that failed came back as an empty list, and the page said
    // "No migration batches found", as if the tenant had none. It fails here both ways a cmdlet can - a
    // terminating error, and an error it writes and goes on - and neither got past -ErrorAction Ignore. The
    // result carries the failure, in the error's own words, and the page's own load shows it as an error: not
    // the empty state, and with no Export. The stub had a row to emit, so a partial list would show too.
    [Theory]
    [InlineData("throw")]
    [InlineData("error")]
    public async Task AFailedBatchQuery_IsShownAsAFailure_NeverAsNoBatches(string how)
    {
        const string message = "Get-MigrationBatch was refused: the account lacks the Migration role";
        _rows.Add(Row(Readable, ("TotalCount", 3)));
        _failure.AddRange(new[] { how, message });
        var service = CreateService();

        var list = await service.GetMigrationBatchesAsync();

        Assert.Equal(message, list.Failure);
        Assert.Empty(list.Batches);
        Assert.Empty(list.Unreadable);

        var page = PageOver(service);
        await LoadBatchList(page);
        var rendered = page.RenderText();

        Assert.Contains($"The migration batches could not be read: {message}", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("No migration batches found", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("There are currently no migration batches", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Export batch list", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(Readable, rendered, StringComparison.Ordinal);
    }

    // aud-a23g. A failure is never none: an exception with no message still fails the load, with a reason to show.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void AFailureWithNoReason_IsStillAFailure(string? reason)
    {
        var failed = MigrationBatchList.Failed(reason);

        Assert.Equal("no reason was given", failed.Failure);
        Assert.Empty(failed.Batches);
        Assert.Empty(failed.Unreadable);
    }

    // aud-a23g. A failed load clears the list. The page shows what the last load returned, and a failed one returned
    // none, so a reload that fails after one that read lists nothing the first one read, offers no Export of it, and
    // leaves nothing ticked for an action to be aimed at.
    [Fact]
    public async Task AFailedReload_ListsNothing_OffersNoExport_AndLeavesNothingTicked()
    {
        _rows.Add(Row(Readable, ("TotalCount", 3)));
        var service = CreateService();
        var page = PageOver(service);

        await LoadBatchList(page);
        Ticked(page).Add(Readable);
        var loaded = page.RenderText();
        Assert.Contains(Readable, loaded, StringComparison.Ordinal);
        Assert.Contains("Export batch list (1)", loaded, StringComparison.Ordinal);

        _failure.AddRange(new[] { "throw", "the service is throttling this account" });
        await LoadBatchList(page);
        var failed = page.RenderText();

        Assert.Contains("The migration batches could not be read: the service is throttling this account", failed, StringComparison.Ordinal);
        Assert.DoesNotContain(Readable, failed, StringComparison.Ordinal);
        Assert.DoesNotContain("Export batch list", failed, StringComparison.Ordinal);
        Assert.Empty(Ticked(page));
        Assert.Empty((List<MigrationBatchInfo>)PageField("migrationBatches").GetValue(page)!);
    }

    // aud-a23g. A load that throws before the query runs - here the connection cannot be opened - reaches the page's own
    // catch, not the service's. The same failure, the same banner: not the empty state, and not the old dismissible
    // "Failed to load migration batches" result banner, which on a first load left the empty state behind it.
    [Fact]
    public async Task ALoadThatFailsBeforeTheQueryRuns_IsShownAsTheSameFailure()
    {
        var page = PageOver(CreateService(connects: false));

        await LoadBatchList(page);
        var rendered = page.RenderText();

        Assert.Contains("The migration batches could not be read: the connection could not be opened", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("No migration batches found", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Failed to load migration batches", rendered, StringComparison.Ordinal);
        Assert.Null(PageField("batchActionResult").GetValue(page));
    }

    // aud-a23g, part 2: ItemsSynced and ItemsSkipped were read with Convert.ToInt64, so one value it could not convert
    // threw and the whole mailbox list was lost ("Unable to load mailboxes"). One this app cannot read is the marker in
    // its own cell now, and every other mailbox loads with its count; an absent one is still 0.
    [Fact]
    public async Task AMailboxCountThisAppCannotRead_IsTheMarkerInItsCell_AndEveryMailboxLoads()
    {
        const string marker = "reported in a form this app cannot read (System.String): 1,205";
        _rows.Add(Row(Readable, ("TotalCount", 3)));
        _users.Add(User("alice@contoso.test", ("ItemsSynced", 10), ("ItemsSkipped", 2L)));
        _users.Add(User("bob@contoso.test", ("ItemsSynced", "1,205"), ("ItemsSkipped", "a few")));
        _users.Add(User("carol@contoso.test", ("ItemsSynced", 7L)));
        var service = CreateService();
        var batch = Assert.Single((await service.GetMigrationBatchesAsync()).Batches);

        var users = (await service.GetMigrationBatchUsersAsync(Readable)).Mailboxes;

        Assert.Equal(["alice", "bob", "carol"], users.Select(u => u.EmailAddress.Split('@')[0]));
        Assert.Equal([10L, null, 7L], users.Select(u => u.ItemsSynced.Count));
        Assert.Equal([2L, null, 0L], users.Select(u => u.ItemsSkipped.Count));
        Assert.Equal("reported in a form this app cannot read (System.String): a few", users[1].ItemsSkipped.Show());

        var rendered = await OpenBatchRendered(service, batch);
        Assert.Equal(1, rendered.Split(marker).Length - 1);
        Assert.Contains("carol@contoso.test", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Unable to load mailboxes", rendered, StringComparison.Ordinal);
    }

    // aud-a23g, part 2. The page's own Synced sort over the mailboxes: an unreadable count is unknown, so after every
    // count in both directions, as the batch counts sort (aud-a23f).
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheMailboxSyncedSort_PutsACountThisAppCannotReadAfterEveryCount_InBothDirections(bool ascending)
    {
        // Input order puts the unreadable mailbox first, so a sort that took it for the lowest would leave it there.
        _users.Add(User("unreadable@contoso.test", ("ItemsSynced", "1,205")));
        _users.Add(User("five@contoso.test", ("ItemsSynced", 5)));
        _users.Add(User("two@contoso.test", ("ItemsSynced", 2)));
        _users.Add(User("nine@contoso.test", ("ItemsSynced", 9)));

        var users = (await CreateService().GetMigrationBatchUsersAsync(Readable)).Mailboxes;

        Assert.Equal(ascending ? ["two", "five", "nine", "unreadable"] : ["nine", "five", "two", "unreadable"],
            SortedMailboxes("Synced", ascending, users));
    }

    // aud-a23g, part 5: a negative count was read as one - a FailedCount of -1 showed in the red failed cell as a fact
    // and sorted before every 0. Nothing is counted below nought, so it is unreadable, whatever type carried it: the
    // marker in the cell and the CSV, and after every count in both directions of the Failed sort.
    [Theory]
    [InlineData("an Int32", "System.Int32")]
    [InlineData("an Int64", "System.Int64")]
    [InlineData("text", "System.String")]
    [InlineData("a whole Double", "System.Double")]
    public async Task ANegativeCount_IsTheMarker_NotACount(string form, string typeName)
    {
        object value = form switch
        {
            "an Int32" => -1,
            "an Int64" => -1L,
            "text" => "-1",
            _ => -1.0,
        };
        _rows.Add(Row("negative", ("FailedCount", value), ("TotalCount", 3)));
        _rows.Add(Row("none", ("FailedCount", 0), ("TotalCount", 3)));
        _rows.Add(Row("two", ("FailedCount", 2), ("TotalCount", 3)));

        var list = await CreateService().GetMigrationBatchesAsync();

        Assert.Null(list.Batches.Single(b => b.BatchName == "negative").FailedCount.Count);
        var marker = $"reported in a form this app cannot read ({typeName}): -1";
        Assert.Contains(marker, ListRendered(list), StringComparison.Ordinal);
        Assert.Contains(marker, Migration.BuildCsv(list.Batches, list.Unreadable), StringComparison.Ordinal);
        Assert.Equal(["none", "two", "negative"], Sorted("Failed", true, list.Batches));
        Assert.Equal(["two", "none", "negative"], Sorted("Failed", false, list.Batches));
    }

    // aud-a23g, part 6: a row whose reading was cancelled was listed as unreadable and the rest returned as a valid
    // partial list. A cancelled load is cancelled: it escapes both catches, with no list at all. PowerShell's adapter
    // wraps the getter's exception, so the cancellation is looked for in the chain.
    [Theory]
    [InlineData(typeof(OperationCanceledException))]
    [InlineData(typeof(PipelineStoppedException))]
    public async Task ACancellationWhileReadingARow_EscapesTheLoad(Type cancellation)
    {
        _rows.Add(Row(Readable, ("TotalCount", 3)));
        _rows.Add(new RowWhoseReadIsCancelled((Exception)Activator.CreateInstance(cancellation, "the load was cancelled")!));

        var error = await Assert.ThrowsAnyAsync<Exception>(() => CreateService().GetMigrationBatchesAsync());

        Assert.Contains(Chain(error), cause => cause.GetType() == cancellation);
    }

    // ---- aud-a23i -----------------------------------------------------------------------------

    /// <summary>aud-a23i. A mailbox row with no Identity property, as a module could hand one back.</summary>
    private static PSObject MailboxWithoutIdentity()
    {
        var user = new PSObject();
        user.Properties.Add(new PSNoteProperty("Status", "Synced"));
        return user;
    }

    /// <summary>aud-a23i. A live mailbox row whose Status cannot be fetched.</summary>
    public sealed class MailboxRowWhoseStatusThrows
    {
        public string Identity => "carol@contoso.test";
        public string Status => throw new InvalidOperationException("the status is not available");
    }

    /// <summary>aud-a23i. A live mailbox row whose Identity cannot be fetched.</summary>
    public sealed class MailboxRowWhoseIdentityThrows
    {
        public string Identity => throw new InvalidOperationException("the identity is not available");
        public string Status => "Synced";
    }

    /// <summary>aud-a23i. A live mailbox row read while the load is being cancelled.</summary>
    public sealed class MailboxRowWhoseReadIsCancelled(Exception cancellation)
    {
        public string Identity => "carol@contoso.test";
        public string Status => throw cancellation;
    }

    // aud-a23i, part 1: a mailbox row with no Identity became a mailbox named "Unknown", which a tick box and every
    // mailbox action could then be aimed at. It is unreadable now - named in a warning above the list, and absent from
    // the rows an action can reach.
    [Fact]
    public async Task AMailboxRowWithNoIdentity_IsUnreadable_NotAMailboxNamedUnknown()
    {
        _rows.Add(Row(Readable, ("TotalCount", 3)));
        _users.Add(User("alice@contoso.test"));
        _users.Add(MailboxWithoutIdentity());
        _users.Add(User("   "));
        var service = CreateService();
        var batch = Assert.Single((await service.GetMigrationBatchesAsync()).Batches);

        var list = await service.GetMigrationBatchUsersAsync(Readable);

        Assert.Equal(["alice@contoso.test"], list.Mailboxes.Select(u => u.EmailAddress));
        Assert.Equal(2, list.Unreadable.Count);
        Assert.All(list.Unreadable, row => Assert.Equal(new UnreadableMigrationMailbox(null, "a row with no Identity"), row));

        var (page, rendered) = await OpenBatchPage(service, batch);
        Assert.Contains("2 mailboxes could not be read: a row with no Identity; a row with no Identity", rendered, StringComparison.Ordinal);
        Assert.Equal(["alice@contoso.test"], ((List<MigrationUserInfo>)PageField("batchUsers").GetValue(page)!).Select(u => u.EmailAddress));
        Assert.DoesNotContain("Unknown", rendered, StringComparison.Ordinal);
    }

    // aud-a23i, part 1: a mailbox row that threw while being read lost the whole list. It is listed with its identity as
    // far as it was read and the reason, and every other mailbox loads.
    [Fact]
    public async Task AMailboxRowThatThrowsWhileBeingRead_IsListedWithItsReason_AndTheRestLoad()
    {
        _users.Add(User("alice@contoso.test"));
        _users.Add(new MailboxRowWhoseStatusThrows());
        _users.Add(new MailboxRowWhoseIdentityThrows());
        _users.Add(User("dave@contoso.test"));

        var list = await CreateService().GetMigrationBatchUsersAsync(Readable);

        Assert.Equal(["alice@contoso.test", "dave@contoso.test"], list.Mailboxes.Select(u => u.EmailAddress));
        Assert.Collection(list.Unreadable,
            row =>
            {
                Assert.Equal("carol@contoso.test", row.Identity);
                Assert.Contains("the status is not available", row.Reason, StringComparison.Ordinal);
            },
            row =>
            {
                Assert.Null(row.Identity);
                Assert.StartsWith("a row whose Identity could not be read: ", row.Reason, StringComparison.Ordinal);
                Assert.Contains("the identity is not available", row.Reason, StringComparison.Ordinal);
            });
    }

    // aud-a23i, part 1: when every row Exchange returned is unreadable, the batch is not empty, and the pane does not say
    // it is - neither "contains no mailboxes" nor "not started".
    [Theory]
    [InlineData("a count of none")]
    [InlineData("pending and not started")]
    public async Task ABatchWhoseEveryMailboxRowIsUnreadable_SaysNoneCouldBeRead_NotThatItIsEmpty(string state)
    {
        _rows.Add(state == "a count of none"
            ? Row(Readable, ("TotalCount", 0))
            : Row(Readable, ("TotalCount", 1), ("PendingCount", 1), ("WorkflowStage", "Injection")));
        _users.Add(MailboxWithoutIdentity());
        var service = CreateService();
        var batch = Assert.Single((await service.GetMigrationBatchesAsync()).Batches);

        var (_, rendered) = await OpenBatchPage(service, batch);

        Assert.Contains("1 mailbox could not be read: a row with no Identity", rendered, StringComparison.Ordinal);
        Assert.Contains("No mailbox in this batch could be read; the warning above names each one.", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("This batch contains no mailboxes.", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Batch not started", rendered, StringComparison.Ordinal);
    }

    // aud-a23i: a cancellation while reading a mailbox row is not an unreadable row; the load is cancelled.
    [Theory]
    [InlineData(typeof(OperationCanceledException))]
    [InlineData(typeof(PipelineStoppedException))]
    public async Task ACancellationWhileReadingAMailboxRow_EscapesTheLoad(Type cancellation)
    {
        _users.Add(User("alice@contoso.test"));
        _users.Add(new MailboxRowWhoseReadIsCancelled((Exception)Activator.CreateInstance(cancellation, "the load was cancelled")!));

        var error = await Assert.ThrowsAnyAsync<Exception>(() => CreateService().GetMigrationBatchUsersAsync(Readable));

        Assert.Contains(Chain(error), cause => cause.GetType() == cancellation);
    }
}
