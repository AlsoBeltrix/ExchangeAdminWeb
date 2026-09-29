using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Reflection;
using ExchangeAdminWeb.Components.Pages;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

public sealed class MigrationMailboxLoadTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "migration-mailboxes-" + Guid.NewGuid().ToString("N"));
    private ExoConnectionPool? _pool;

    [Fact]
    public async Task LoadingABatchReturnsItsMailboxesBeyondTheDefaultResultLimit()
    {
        var service = CreateService(failQuery: false);

        var users = await service.GetMigrationBatchUsersAsync("Wave 12 / Operations");

        Assert.Equal(1205, users.Count);
        Assert.Equal("user1@contoso.com", users[0].EmailAddress);
        Assert.Equal("user1205@contoso.com", users[^1].EmailAddress);
        Assert.All(users, user => Assert.Equal("Synced", user.Status));
    }

    [Fact]
    public async Task AFailedMailboxQueryCannotBecomeASuccessfulEmptyBatch()
    {
        var service = CreateService(failQuery: true);

        var error = await Assert.ThrowsAnyAsync<Exception>(() =>
            service.GetMigrationBatchUsersAsync("Wave 12 / Operations"));

        Assert.Contains("Mailbox query unavailable", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadedMailboxesRenderIncludingWhenEveryMailboxIsTicked(bool selectAll)
    {
        var page = CreatePage(CreateService(failQuery: false));

        await LoadPageMailboxes(page);
        if (selectAll)
        {
            var selected = (HashSet<string>)PageField("selectedMailboxes").GetValue(page)!;
            foreach (var user in (List<MigrationUserInfo>)PageField("batchUsers").GetValue(page)!)
                selected.Add(user.EmailAddress);
        }
        var rendered = page.RenderText();

        // Lexical address sorting puts user1000 before user1 (the '@' follows the digits).
        Assert.Contains("user1000@contoso.com", rendered, StringComparison.Ordinal);
        Assert.Equal(selectAll ? 40 : 50,
            System.Text.RegularExpressions.Regex.Matches(rendered, @"user\d+@contoso\.com").Count);
        Assert.Contains("1205", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("contains no mailboxes", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("No mailbox details", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedLoadRendersAnErrorAndReleasesTheRefreshButton()
    {
        var page = CreatePage(CreateService(failQuery: true));

        await LoadPageMailboxes(page);
        var rendered = page.RenderText();

        Assert.Contains("Unable to load mailboxes. Refresh to try again.", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("contains no mailboxes", rendered, StringComparison.Ordinal);
        Assert.Null(PageField("loadingBatchUsers").GetValue(page));
    }

    private RenderableMigration CreatePage(MigrationService service)
    {
        var page = new RenderableMigration();
        var environment = Substitute.For<IWebHostEnvironment>();
        environment.ContentRootPath.Returns(_directory);
        const BindingFlags properties = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        typeof(Migration).GetProperty("MigrationSvc", properties)!.SetValue(page, service);
        typeof(Migration).GetProperty("ReportStore", properties)!.SetValue(page, new MigrationReportStore(environment));
        typeof(Migration).GetProperty("Logger", properties)!.SetValue(page, Substitute.For<ILogger<Migration>>());
        PageField("tabIndex").SetValue(page, 2);
        PageField("batchesLoaded").SetValue(page, true);
        PageField("canManage").SetValue(page, true);
        PageField("expandedBatch").SetValue(page, "Wave 12 / Operations");
        PageField("migrationBatches").SetValue(page, new List<MigrationBatchInfo>
        {
            new() { BatchName = "Wave 12 / Operations", Status = "Synced", TotalCount = 1205 }
        });
        ((HashSet<string>)PageField("selectedBatches").GetValue(page)!).Add("Wave 12 / Operations");
        return page;
    }

    private static FieldInfo PageField(string name) =>
        typeof(Migration).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static Task LoadPageMailboxes(Migration page) =>
        (Task)typeof(Migration).GetMethod("LoadMailboxesFor", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(page, ["Wave 12 / Operations"])!;

    // Execute the compiled Razor render tree, including the mailbox row fragments. This tests
    // the actual display branches, without a browser, component lifecycle or live Exchange.
    private sealed class RenderableMigration : Migration
    {
#pragma warning disable BL0006 // Inspect render frames solely in this focused component regression test.
        public string RenderText()
        {
            using var builder = new RenderTreeBuilder();
            BuildRenderTree(builder);
            var frames = builder.GetFrames();
            return string.Concat(frames.Array.Take(frames.Count).Select(frame => frame.FrameType switch
            {
                RenderTreeFrameType.Text => frame.TextContent,
                RenderTreeFrameType.Markup => frame.MarkupContent,
                _ => ""
            }));
        }
#pragma warning restore BL0006
    }

    private MigrationService CreateService(bool failQuery)
    {
        Directory.CreateDirectory(_directory);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Audit:LogRoot"] = _directory,
            ["ExchangeOnline:AppId"] = "11111111-1111-1111-1111-111111111111",
            ["ExchangeOnline:Organization"] = "contoso.onmicrosoft.com"
        }).Build();
        var environment = Substitute.For<IWebHostEnvironment>();
        environment.ContentRootPath.Returns(_directory);
        var store = TestConfigStore.Create(_directory);
        var catalog = new ModuleCatalog();
        var moduleConfig = new ModuleConfigService(catalog, environment,
            new ModuleConfigRepository(store), Substitute.For<ILogger<ModuleConfigService>>());
        var enablement = new ModuleEnablementService(catalog, environment, moduleConfig,
            new ModuleEnablementRepository(store), config, Substitute.For<ILogger<ModuleEnablementService>>());
        enablement.SaveEnablement(new Dictionary<string, bool> { ["ExchangeOnline"] = true });
        var trace = new OperationTraceService(config,
            new JsonlLogService(config, Substitute.For<ILogger<JsonlLogService>>()));

        _pool = new ExoConnectionPool(config, moduleConfig, enablement,
            Substitute.For<ILogger<ExoConnectionPool>>(), trace, (_, generation) =>
            {
                // Exercise the real service, PowerShell parameter binding and result mapping.
                // Only the network connection/cmdlets are replaced. A batch object's Identity
                // binds to a USER identity when piped; BatchId must be supplied explicitly.
                var state = InitialSessionState.CreateDefault2();
                state.Variables.Add(new SessionStateVariableEntry("FailMailboxQuery", failQuery, null));
                state.Commands.Add(new SessionStateFunctionEntry("Disconnect-ExchangeOnline", "[CmdletBinding()] param([switch] $Confirm)"));
                state.Commands.Add(new SessionStateFunctionEntry("Get-MigrationBatch", """
                    [CmdletBinding()]
                    param([string] $Identity)
                    [pscustomobject]@{ Identity = $Identity; TotalCount = 1205 }
                    """));
                state.Commands.Add(new SessionStateFunctionEntry("Get-MigrationUser", """
                    [CmdletBinding()]
                    param(
                        [Parameter(ValueFromPipelineByPropertyName = $true)] [string] $Identity,
                        [string] $BatchId,
                        [string] $ResultSize
                    )
                    process {
                        if ($FailMailboxQuery) { Write-Error 'Mailbox query unavailable'; return }
                        if ($Identity) { Write-Error 'A batch name is not a migration user identity'; return }
                        if ($BatchId -ne 'Wave 12 / Operations') { throw 'Wrong batch requested' }
                        $count = if ($ResultSize -eq 'Unlimited') { 1205 } else { 1000 }
                        foreach ($index in 1..$count) {
                            [pscustomobject]@{ Identity = "user$index@contoso.com"; Status = 'Synced' }
                        }
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

    public void Dispose()
    {
        _pool?.Dispose();
        // SQLite may retain pooled handles until the test host exits.
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }
}
