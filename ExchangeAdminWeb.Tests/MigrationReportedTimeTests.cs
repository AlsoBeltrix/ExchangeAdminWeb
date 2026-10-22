using System.Collections;
using System.Globalization;
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
/// Audit finding aud-a23d, the Migration half: the five timestamps Get-MigrationBatch and
/// Get-MigrationUser report were read <c>as DateTime?</c>, so any other type vanished as if none were set.
/// </summary>
/// <remarks>
/// Behaviour, not text. The real <see cref="MigrationService"/> on a real pool, whose connect seam hands
/// out an in-process runspace carrying stub cmdlets that report what each test sets (the
/// <c>MigrationMailboxLoadTests</c> idiom), and the page's compiled render tree and CSV. No Exchange.
/// </remarks>
public sealed class MigrationReportedTimeTests : IDisposable
{
    private const string Batch = "Wave 12 / Operations";

    // A different instant for each field, so a value that reached the wrong one would show.
    private static readonly DateTime CreatedUtc = new(2026, 10, 1, 20, 15, 0, DateTimeKind.Utc);
    private static readonly DateTime StartedUtc = new(2026, 10, 2, 9, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime CompletedUtc = new(2026, 10, 3, 17, 45, 0, DateTimeKind.Utc);
    private static readonly DateTime LastSyncUtc = new(2026, 10, 4, 6, 5, 0, DateTimeKind.Utc);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "migration-times-" + Guid.NewGuid().ToString("N"));
    private readonly ArrayList _batches = ArrayList.Synchronized(new ArrayList());
    private readonly ArrayList _users = ArrayList.Synchronized(new ArrayList());
    private ExoConnectionPool? _pool;

    public void Dispose()
    {
        _pool?.Dispose();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    // ---- the harness ------------------------------------------------------------------------

    /// <summary>A time handed to the stub in one of the forms a module might report it in.</summary>
    private static object As(string form, DateTime utc) => form switch
    {
        "DateTimeOffset" => new DateTimeOffset(utc).ToOffset(TimeSpan.FromHours(5.5)),
        "PSObject wrapping a DateTimeOffset" => new PSObject(new DateTimeOffset(utc).ToOffset(TimeSpan.FromHours(5.5))),
        "PSObject wrapping a DateTime" => new PSObject(utc),
        "DateTime of Kind Local" => utc.ToLocalTime(),
        _ => throw new ArgumentOutOfRangeException(nameof(form), form, null),
    };

    /// <summary>A value this app cannot read as a time, and the type name and text its marker carries.</summary>
    private static (object Value, string Marker) Unreadable(string form)
    {
        switch (form)
        {
            case "text in a culture's own format":
                return ("10/02/2026 9:30:00 AM", "reported in a form this app cannot read (System.String): 10/02/2026 9:30:00 AM");
            case "a number":
                return (42, "reported in a form this app cannot read (System.Int32): 42");
            default:
                var deserialized = new PSObject();
                deserialized.TypeNames.Insert(0, "Deserialized.Contoso.Data.ExDateTime");
                deserialized.Properties.Add(new PSNoteProperty("DateTime", "Friday, October 2, 2026 9:30:00 AM"));
                return (deserialized,
                    "reported in a form this app cannot read (Deserialized.Contoso.Data.ExDateTime): @{DateTime=Friday, October 2, 2026 9:30:00 AM}");
        }
    }

    private void ReportBatch(string name, object? created = null, object? started = null, object? completed = null,
        object? completeAfter = null, int pending = 0, string stage = "Processing", int total = 3)
    {
        _batches.Add(new Hashtable
        {
            ["Identity"] = name,
            ["Status"] = "Synced",
            ["TotalCount"] = total,
            ["PendingCount"] = pending,
            ["WorkflowStage"] = stage,
            ["CreationDateTime"] = created,
            ["StartDateTime"] = started,
            ["CompletionDateTime"] = completed,
            ["CompleteAfter"] = completeAfter,
        });
    }

    private void ReportUser(string email, object? lastSync) =>
        _users.Add(new Hashtable { ["Identity"] = email, ["Status"] = "Synced", ["LastSyncTime"] = lastSync });

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
                state.Variables.Add(new SessionStateVariableEntry("Batches", _batches, null));
                state.Variables.Add(new SessionStateVariableEntry("Users", _users, null));
                state.Commands.Add(new SessionStateFunctionEntry("Disconnect-ExchangeOnline", "[CmdletBinding()] param([switch] $Confirm)"));
                state.Commands.Add(new SessionStateFunctionEntry("Get-MigrationBatch", """
                    [CmdletBinding()]
                    param([string] $Identity)
                    foreach ($b in $Batches) {
                        [pscustomobject]@{
                            Identity = $b['Identity']; Status = $b['Status']; TotalCount = $b['TotalCount']
                            PendingCount = $b['PendingCount']; WorkflowStage = $b['WorkflowStage']
                            CreationDateTime = $b['CreationDateTime']; StartDateTime = $b['StartDateTime']
                            CompletionDateTime = $b['CompletionDateTime']; CompleteAfter = $b['CompleteAfter']
                        }
                    }
                    """));
                state.Commands.Add(new SessionStateFunctionEntry("Get-MigrationUser", """
                    [CmdletBinding()]
                    param([string] $BatchId, [string] $ResultSize)
                    foreach ($u in $Users) {
                        [pscustomobject]@{ Identity = $u['Identity']; Status = $u['Status']; LastSyncTime = $u['LastSyncTime'] }
                    }
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

    /// <summary>The Status tab with <paramref name="batch"/> open and its mailboxes loaded, as it renders.</summary>
    private static async Task<string> OpenBatchRendered(MigrationService service, MigrationBatchInfo batch)
    {
        var page = new RenderableMigration();
        var environment = Substitute.For<IWebHostEnvironment>();
        const BindingFlags properties = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        typeof(Migration).GetProperty("MigrationSvc", properties)!.SetValue(page, service);
        typeof(Migration).GetProperty("ReportStore", properties)!.SetValue(page, new MigrationReportStore(environment));
        typeof(Migration).GetProperty("Logger", properties)!.SetValue(page, Substitute.For<ILogger<Migration>>());
        typeof(Migration).GetProperty("Progress", properties)!
            .SetValue(page, new ExchangeAdminWeb.Services.Progress.ActivityProgressService());
        PageField("tabIndex").SetValue(page, 2);
        PageField("batchesLoaded").SetValue(page, true);
        PageField("canManage").SetValue(page, true);
        PageField("expandedBatch").SetValue(page, batch.BatchName);
        PageField("migrationBatches").SetValue(page, new List<MigrationBatchInfo> { batch });
        ((HashSet<string>)PageField("selectedBatches").GetValue(page)!).Add(batch.BatchName);

        await (Task)typeof(Migration).GetMethod("LoadMailboxesFor", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(page, [batch.BatchName])!;
        return page.RenderText();
    }

    private static FieldInfo PageField(string name) =>
        typeof(Migration).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>The page's own ordering of <paramref name="rows"/> by a column, in a direction.</summary>
    private static List<string> Sorted<T>(string method, string column, bool ascending, IEnumerable<T> rows, Func<T, string> key)
    {
        var page = new Migration();
        var prefix = method == "SortMailboxes" ? "mailbox" : "batch";
        PageField(prefix + "SortColumn").SetValue(page, column);
        PageField(prefix + "SortAsc").SetValue(page, ascending);
        var sorted = (IEnumerable<T>)typeof(Migration).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(page, [rows])!;
        return sorted.Select(key).ToList();
    }

    private static string Local(DateTime utc, string format) => utc.ToLocalTime().ToString(format, CultureInfo.CurrentCulture);

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

    // aud-a23d, the finding: each of the five reads took its value `as DateTime?`, so a DateTimeOffset or a
    // wrapped value vanished - no Started, no Completed, a "-" for the last sync, no schedule. Each is now
    // read as its instant and shown as the Kind-Utc DateTime of that instant is: in the header, the
    // mailbox row and the CSV. The offset handed over (+05:30) is not this server's, so a reading that
    // kept the reported wall clock would show other times. A Kind-Local DateTime, which the old read
    // accepted, is the control.
    [Theory]
    [InlineData("DateTimeOffset")]
    [InlineData("PSObject wrapping a DateTimeOffset")]
    [InlineData("PSObject wrapping a DateTime")]
    [InlineData("DateTime of Kind Local")]
    public async Task EachTimeReportedInAnotherForm_IsShownAsTheSameInstant(string form)
    {
        var completeAfterUtc = DateTime.SpecifyKind(DateTime.UtcNow.AddDays(30).Date.AddHours(22), DateTimeKind.Utc);
        ReportBatch(Batch, As(form, CreatedUtc), As(form, StartedUtc), As(form, CompletedUtc), As(form, completeAfterUtc));
        ReportUser("alice@contoso.test", As(form, LastSyncUtc));
        var service = CreateService();

        var batch = Assert.Single(await service.GetMigrationBatchesAsync());
        var rendered = await OpenBatchRendered(service, batch);
        var csv = Migration.BuildCsv([batch]);

        Assert.Contains($"Created:</strong> {Local(CreatedUtc, "MMM dd, yyyy HH:mm")}", rendered, StringComparison.Ordinal);
        Assert.Contains($"Started:</strong> {Local(StartedUtc, "MMM dd, yyyy HH:mm")}", rendered, StringComparison.Ordinal);
        Assert.Contains($"Completed:</strong> {Local(CompletedUtc, "MMM dd, yyyy HH:mm")}", rendered, StringComparison.Ordinal);
        Assert.Contains(Local(LastSyncUtc, "MMM dd HH:mm"), rendered, StringComparison.Ordinal);
        Assert.Contains($"\"{Local(CreatedUtc, "MMM dd, yyyy HH:mm")}\",\"{Local(StartedUtc, "MMM dd, yyyy HH:mm")}\",\"{Local(CompletedUtc, "MMM dd, yyyy HH:mm")}\"",
            csv, StringComparison.Ordinal);
        Assert.True(batch.AutoComplete);
        Assert.False(batch.CompleteAfterUnreadable);
        Assert.Equal(completeAfterUtc, batch.ScheduledCompletionUtc);
        Assert.DoesNotContain("cannot read", rendered, StringComparison.Ordinal);
    }

    // aud-a23d. What cannot be read as a time is shown as the aud-a7i marker, with its type and its
    // text, wherever the time would be - never as a blank, a "-" or an empty CSV cell.
    [Theory]
    [InlineData("text in a culture's own format")]
    [InlineData("a number")]
    [InlineData("a deserialized object")]
    public async Task EachTimeThisAppCannotRead_IsShownAsTheMarker_WithItsText_NeverAsBlank(string form)
    {
        var (value, marker) = Unreadable(form);
        ReportBatch(Batch, value, value, value, value);
        ReportUser("alice@contoso.test", value);
        var service = CreateService();

        var batch = Assert.Single(await service.GetMigrationBatchesAsync());
        var rendered = await OpenBatchRendered(service, batch);
        var csv = Migration.BuildCsv([batch]);

        Assert.Contains($"Created:</strong> {marker}", rendered, StringComparison.Ordinal);
        Assert.Contains($"Started:</strong> {marker}", rendered, StringComparison.Ordinal);
        Assert.Contains($"Completed:</strong> {marker}", rendered, StringComparison.Ordinal);
        // The three in the header and the mailbox row's: none of them blank, and the row not "-".
        Assert.Equal(4, rendered.Split(marker).Length - 1);
        Assert.Equal(3, csv.Split(marker).Length - 1);
    }

    // aud-a23d, logic. A batch with pending mailboxes in Injection and no start reported is told "Batch
    // not started." A start Exchange did report, in a form this app cannot read, is not "no start": the
    // batch may well have started, so it is not told it has not, and the header shows the marker.
    [Theory]
    [InlineData("text in a culture's own format")]
    [InlineData("a number")]
    [InlineData("a deserialized object")]
    public async Task AStartThisAppCannotRead_IsNotABatchThatHasNotStarted(string form)
    {
        var (value, marker) = Unreadable(form);
        ReportBatch(Batch, started: value, pending: 2, stage: "Injection", total: 2);
        var service = CreateService();

        var batch = Assert.Single(await service.GetMigrationBatchesAsync());
        var rendered = await OpenBatchRendered(service, batch);

        Assert.DoesNotContain("Batch not started", rendered, StringComparison.Ordinal);
        Assert.Contains($"Started:</strong> {marker}", rendered, StringComparison.Ordinal);
    }

    // aud-a23d, logic. The page's own LastSync sort: an unreadable time is unknown, so it goes after every
    // time in both directions, but Exchange did report one, so it goes before the mailboxes with none.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheLastSyncSort_PutsATimeThisAppCannotReadAfterEveryTime_AndBeforeNone(bool ascending)
    {
        var (value, _) = Unreadable("text in a culture's own format");
        // Input order puts the unreadable mailbox AFTER the one with none, so a sort that took it for none
        // would leave it there.
        ReportUser("none@contoso.test", null);
        ReportUser("unreadable@contoso.test", value);
        ReportUser("early@contoso.test", As("DateTimeOffset", LastSyncUtc));
        ReportUser("late@contoso.test", As("DateTimeOffset", LastSyncUtc.AddHours(5)));
        var service = CreateService();

        var users = await service.GetMigrationBatchUsersAsync(Batch);
        var order = Sorted("SortMailboxes", "LastSync", ascending, users, u => u.EmailAddress.Split('@')[0]);

        Assert.Equal(ascending ? ["early", "late", "unreadable", "none"] : ["late", "early", "unreadable", "none"], order);
    }

    // aud-a23d, logic. The page's default sort, by Created: read as MinValue an unreadable creation time
    // would lead an ascending list as the oldest batch. It goes after every time in both directions.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheCreatedSort_PutsATimeThisAppCannotReadAfterEveryTime_InBothDirections(bool ascending)
    {
        var (value, _) = Unreadable("a number");
        ReportBatch("unreadable", created: value);
        ReportBatch("early", created: As("DateTimeOffset", CreatedUtc));
        ReportBatch("late", created: As("DateTimeOffset", CreatedUtc.AddDays(3)));
        var service = CreateService();

        var batches = await service.GetMigrationBatchesAsync();
        var order = Sorted("SortBatches", "Created", ascending, batches, b => b.BatchName);

        Assert.Equal(ascending ? ["early", "late", "unreadable"] : ["late", "early", "unreadable"], order);
    }

    // aud-a23d, logic. A CompleteAfter Exchange reported means the batch is set to finalise, whatever its
    // form; the old read made such a batch not auto-completing. Whether it is a schedule cannot be said
    // without the time, so the result says that rather than "not scheduled".
    [Theory]
    [InlineData("text in a culture's own format")]
    [InlineData("a number")]
    [InlineData("a deserialized object")]
    public async Task ACompleteAfterThisAppCannotRead_IsAutoCompleting_AndSaysItsScheduleIsUnknown(string form)
    {
        var (value, _) = Unreadable(form);
        ReportBatch(Batch, completeAfter: value);
        var service = CreateService();

        var batch = Assert.Single(await service.GetMigrationBatchesAsync());

        Assert.True(batch.AutoComplete);
        Assert.True(batch.CompleteAfterUnreadable);
        Assert.Null(batch.ScheduledCompletionUtc);
    }

    // aud-a23d. None reported is still none: not auto-completing, and not "unknown".
    [Fact]
    public async Task NoCompleteAfter_IsNeitherAutoCompletingNorUnknown()
    {
        ReportBatch(Batch);
        var service = CreateService();

        var batch = Assert.Single(await service.GetMigrationBatchesAsync());

        Assert.False(batch.AutoComplete);
        Assert.False(batch.CompleteAfterUnreadable);
        Assert.Null(batch.ScheduledCompletionUtc);
    }
}
