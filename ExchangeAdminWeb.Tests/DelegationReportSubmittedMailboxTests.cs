using System.Collections.Concurrent;
using System.Management.Automation.Runspaces;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using static ExchangeAdminWeb.Tests.MigrationStubExchange;
using static ExchangeAdminWeb.Tests.SubmittedValueHarness;
using Page = ExchangeAdminWeb.Components.Pages.DelegationReport;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// kd-72, from the owner's production report of 2026-10-27: a report submitted for mailbox A, then for B, showed A's
/// label and A's results while the audit row recorded B. RunReport read the bound address at the label, the read, the
/// audit row and the catch; Enter's keydown reached the server before the box's change event, the run started on A,
/// and the change event applied B while the read was in flight.
/// </summary>
/// <remarks>
/// The page's handler is driven through its compiled code over the real <see cref="DelegationReportService"/> and a
/// runspace whose Exchange cmdlets are functions - the <see cref="DelegationReportCalendarFailureTests"/> harness.
/// Get-MailboxPermission records the identity it is asked for and waits until the test releases it, so the change
/// event lands while the read is genuinely in flight. Nothing reaches Exchange.
/// </remarks>
public sealed class DelegationReportSubmittedMailboxTests : IDisposable
{
    private const string MailboxA = "first.mailbox@contoso.com";
    private const string MailboxB = "second.mailbox@contoso.com";

    private readonly TempDir _temp = new();
    private readonly SemaphoreSlim _entered = new(0);
    private readonly SemaphoreSlim _release = new(0);
    private readonly ConcurrentQueue<string> _asked = new();
    private readonly IConfiguration _config;
    private readonly AuditService _audit;
    private readonly RecordingProgress _progress = new();
    private readonly ExoConnectionPool _pool;
    private readonly DelegationReportService _service;

    public DelegationReportSubmittedMailboxTests()
    {
        _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Audit:LogRoot"] = _temp.Path,
            ["ExchangeOnline:AppId"] = "11111111-1111-1111-1111-111111111111",
            ["ExchangeOnline:Organization"] = "contoso.onmicrosoft.com",
            ["Delinea:SecretServerUrl"] = "https://fake.local",
        }).Build();
        var log = new JsonlLogService(_config, NullLogger<JsonlLogService>.Instance);
        _audit = new AuditService(log, new OperationTraceService(_config, log));
        (_pool, _service) = Service();
    }

    public void Dispose()
    {
        // A read the test did not release - a mutant that re-entered - must not hold a runspace thread for its timeout.
        _release.Release(16);
        _pool.Dispose();
        _entered.Dispose();
        _release.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public async Task AChangeLandingWhileTheReportRuns_LeavesTheLabelTheResultAndTheAuditRowOnTheMailboxSubmitted()
    {
        var page = PageFor(MailboxA);

        var running = Call(page, "RunReport");
        Assert.True(_entered.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken), "the report never reached its read");
        Assert.False(running.IsCompleted);

        // The change event the Enter keydown outran, applied while the read is in flight.
        SetField(page, "emailAddress", MailboxB);
        _release.Release();
        await running;

        Assert.Equal([MailboxA], _asked.ToArray());
        var handle = Assert.Single(_progress.Handles);
        Assert.Equal($"Reading delegation for {MailboxA}", handle.Label);
        Assert.NotNull(handle.Outcome);
        Assert.True(handle.Outcome.Value.Success);
        Assert.Null(handle.Outcome.Value.Message);

        var result = Field<DelegationReportResult>(page, "result")!;
        Assert.Equal(MailboxA, result.EmailAddress);
        Assert.Contains(result.FullAccess, entry => entry.User == $"delegate-of-{MailboxA}");

        var row = Assert.Single(AuditRows(_temp.Path));
        Assert.Equal("DelegationReport", Text(row, "action"));
        Assert.Equal(MailboxA, Text(row, "target"));
        Assert.Equal("Success", Text(row, "result"));

        // The box now says B, and the next report is for B - label, read, result and row.
        var next = Call(page, "RunReport");
        Assert.True(_entered.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken), "the second report never reached its read");
        _release.Release();
        await next;

        Assert.Equal([MailboxA, MailboxB], _asked.ToArray());
        Assert.Equal($"Reading delegation for {MailboxB}", _progress.Handles[^1].Label);
        Assert.Equal(MailboxB, Field<DelegationReportResult>(page, "result")!.EmailAddress);
        Assert.Equal(MailboxB, Text(AuditRows(_temp.Path)[^1], "target"));
    }

    [Fact]
    public async Task ASecondSubmitWhileTheReportRuns_IsIgnored_NotRunBesideIt()
    {
        var page = PageFor(MailboxA);

        var running = Call(page, "RunReport");
        Assert.True(_entered.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken), "the report never reached its read");

        // A second Enter, or a click the browser sent before the disabled button rendered.
        var second = Call(page, "RunReport");
        Assert.True(second.IsCompleted, "a second submit during a run started a second report");
        await second;

        _release.Release();
        await running;

        Assert.Equal([MailboxA], _asked.ToArray());
        Assert.Single(_progress.Handles);
        var row = Assert.Single(AuditRows(_temp.Path));
        Assert.Equal(MailboxA, Text(row, "target"));
        Assert.Equal(MailboxA, Field<DelegationReportResult>(page, "result")!.EmailAddress);
    }

    [Fact]
    public void TheMailboxBox_BindsOnInput_SoEnterSubmitsWhatItShows()
    {
        // The other half of the production report: bound on change, the box had not told the server what it held when
        // Enter's keydown arrived, so the report ran on the address it held before. Read from the page's markup, as
        // the repo's markup tripwires are - there is no renderer in this suite.
        var tag = BoundInputTag("DelegationReport.razor", "emailAddress");

        Assert.Contains("@bind:event=\"oninput\"", tag, StringComparison.Ordinal);
        Assert.Contains("@onkeydown=\"HandleKeyDown\"", tag, StringComparison.Ordinal);
    }

    private Page PageFor(string mailbox)
    {
        var page = new Page();
        Set(page, "DelegationService", _service);
        Set(page, "Progress", _progress);
        Set(page, "Audit", _audit);
        SetField(page, "currentUser", @"CONTOSO\operator");
        SetField(page, "clientIpAddress", "10.0.0.5");
        SetField(page, "emailAddress", mailbox);
        return page;
    }

    private (ExoConnectionPool Pool, DelegationReportService Service) Service()
    {
        var environment = Substitute.For<IWebHostEnvironment>();
        environment.ContentRootPath.Returns(_temp.Path);

        var store = TestConfigStore.Create(_temp.Path);
        var catalog = new ModuleCatalog();
        var moduleConfig = new ModuleConfigService(catalog, environment,
            new ModuleConfigRepository(store), Substitute.For<ILogger<ModuleConfigService>>());
        var enablement = new ModuleEnablementService(catalog, environment, moduleConfig,
            new ModuleEnablementRepository(store), _config, Substitute.For<ILogger<ModuleEnablementService>>());
        enablement.SaveEnablement(new Dictionary<string, bool> { ["ExchangeOnline"] = true });

        var trace = new OperationTraceService(_config,
            new JsonlLogService(_config, Substitute.For<ILogger<JsonlLogService>>()));

        var pool = new ExoConnectionPool(_config, moduleConfig, enablement,
            Substitute.For<ILogger<ExoConnectionPool>>(), trace, (_, generation, _) =>
            {
                var state = InitialSessionState.CreateDefault2();
                state.Variables.Add(new SessionStateVariableEntry("Asked", _asked, null));
                state.Variables.Add(new SessionStateVariableEntry("Entered", _entered, null));
                state.Variables.Add(new SessionStateVariableEntry("Release", _release, null));
                state.Commands.Add(new SessionStateFunctionEntry("Disconnect-ExchangeOnline",
                    "[CmdletBinding()] param([string] $ConnectionId, [switch] $Confirm)"));
                // The read the change event lands during: it says which mailbox it was asked about, and waits for the
                // test to let it answer.
                state.Commands.Add(new SessionStateFunctionEntry("Get-MailboxPermission", """
                    [CmdletBinding()] param([string] $Identity)
                    $Asked.Enqueue($Identity)
                    [void]$Entered.Release()
                    if (-not $Release.Wait(30000)) { throw 'the test never released the read' }
                    [pscustomobject]@{ User = "delegate-of-$Identity"; AccessRights = 'FullAccess'; IsInherited = $false; Deny = $false }
                    """));
                state.Commands.Add(new SessionStateFunctionEntry("Get-RecipientPermission", """
                    [CmdletBinding()] param([string] $Identity)
                    @()
                    """));
                state.Commands.Add(new SessionStateFunctionEntry("Get-MailboxFolderStatistics", """
                    [CmdletBinding()] param([string] $Identity, [string] $FolderScope)
                    [pscustomobject]@{ Name = 'Calendar'; FolderType = 'Calendar' }
                    """));
                state.Commands.Add(new SessionStateFunctionEntry("Get-MailboxFolderPermission", """
                    [CmdletBinding()] param([string] $Identity)
                    @()
                    """));

                var runspace = RunspaceFactory.CreateRunspace(state);
                runspace.Open();
                var ps = System.Management.Automation.PowerShell.Create();
                ps.Runspace = runspace;
                return new PooledRunspace(runspace, ps, generation, "test-connection");
            });

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());
        var extendedLog = new ExtendedLogService(_config, environment,
            TestConfigStore.CreateAppSettings(_temp.Path), Substitute.For<ILogger<ExtendedLogService>>());
        var delinea = new DelineaService(httpClientFactory, _config,
            Substitute.For<ILogger<DelineaService>>(), extendedLog, trace);

        return (pool, new DelegationReportService(pool, delinea, Substitute.For<ILogger<DelegationReportService>>(), _config));
    }
}
