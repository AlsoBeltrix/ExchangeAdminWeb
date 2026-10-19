using System.Management.Automation.Runspaces;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Audit finding A18: a failed calendar-permission read rendered as "No calendar delegates"
/// and audited as a successful report.
/// </summary>
/// <remarks>
/// Behaviour, not a source scan. The real service runs against a real runspace with the
/// Exchange cmdlets replaced by functions - the harness shape already used by
/// <see cref="MigrationMailboxLoadTests"/> - so the service's own try/catch, result mapping
/// and error text are all exercised. Only the network is absent.
/// </remarks>
public sealed class DelegationReportCalendarFailureTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "drc-" + Guid.NewGuid().ToString("N"));

    public DelegationReportCalendarFailureTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best effort */ }
    }

    private DelegationReportService Service(bool calendarFails, bool malformedRow = false)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Audit:LogRoot"] = _directory,
            ["ExchangeOnline:AppId"] = "11111111-1111-1111-1111-111111111111",
            ["ExchangeOnline:Organization"] = "contoso.onmicrosoft.com",
            ["Delinea:SecretServerUrl"] = "https://fake.local",
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

        var pool = new ExoConnectionPool(config, moduleConfig, enablement,
            Substitute.For<ILogger<ExoConnectionPool>>(), trace, (_, generation, _) =>
            {
                var state = InitialSessionState.CreateDefault2();
                state.Variables.Add(new SessionStateVariableEntry("CalendarFails", calendarFails, null));
                state.Variables.Add(new SessionStateVariableEntry("MalformedRow", malformedRow, null));
                state.Commands.Add(new SessionStateFunctionEntry("Disconnect-ExchangeOnline",
                    "[CmdletBinding()] param([string] $ConnectionId, [switch] $Confirm)"));
                state.Commands.Add(new SessionStateFunctionEntry("Get-MailboxPermission", """
                    [CmdletBinding()] param([string] $Identity)
                    [pscustomobject]@{ User = 'someone@contoso.com'; AccessRights = 'FullAccess'; IsInherited = $false; Deny = $false }
                    """));
                state.Commands.Add(new SessionStateFunctionEntry("Get-RecipientPermission", """
                    [CmdletBinding()] param([string] $Identity)
                    @()
                    """));
                state.Commands.Add(new SessionStateFunctionEntry("Get-MailboxFolderStatistics", """
                    [CmdletBinding()] param([string] $Identity, [string] $FolderScope)
                    [pscustomobject]@{ Name = 'Calendar'; FolderType = 'Calendar' }
                    """));
                // The one that matters: a read that fails rather than returning nothing.
                state.Commands.Add(new SessionStateFunctionEntry("Get-MailboxFolderPermission", """
                    [CmdletBinding()] param([string] $Identity)
                    if ($CalendarFails) { throw 'The operation could not be completed: calendar unavailable' }
                    if ($MalformedRow) { [pscustomobject]@{ User = $null; AccessRights = $null } }
                    [pscustomobject]@{ User = 'delegate@contoso.com'; AccessRights = 'Editor' }
                    """));

                var runspace = RunspaceFactory.CreateRunspace(state);
                runspace.Open();
                var ps = System.Management.Automation.PowerShell.Create();
                ps.Runspace = runspace;
                return new PooledRunspace(runspace, ps, generation, "test-connection");
            });

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());
        var extendedLog = new ExtendedLogService(config, environment,
            TestConfigStore.CreateAppSettings(_directory), Substitute.For<ILogger<ExtendedLogService>>());
        var delinea = new DelineaService(httpClientFactory, config,
            Substitute.For<ILogger<DelineaService>>(), extendedLog, trace);

        return new DelegationReportService(pool, delinea,
            Substitute.For<ILogger<DelegationReportService>>(), config);
    }

    [Fact]
    public async Task AFailedCalendarRead_IsReportedAsUnknown_NotAsNoDelegates()
    {
        // The defect: this returned an empty Calendar list and a null Error, so the page said
        // "No calendar delegates" and the audit recorded a success.
        var result = await Service(calendarFails: true).GetMailboxDelegationAsync("user@contoso.com");

        Assert.Empty(result.Calendar);
        Assert.NotNull(result.CalendarError);
        Assert.Contains("calendar unavailable", result.CalendarError, StringComparison.OrdinalIgnoreCase);

        // The rest of the report is still valid and must not be failed wholesale.
        Assert.Null(result.Error);
        Assert.NotEmpty(result.FullAccess);
    }

    [Fact]
    public async Task ASkippedMalformedRow_StillMarksTheSectionIncomplete()
    {
        // Review of A18. A row missing User or AccessRights was dropped in silence, so the
        // list could be short while CalendarError stayed null - the same lie, one row at a
        // time. The surviving row must still be reported.
        var result = await Service(calendarFails: false, malformedRow: true)
            .GetMailboxDelegationAsync("user@contoso.com");

        Assert.Contains(result.Calendar, entry => entry.User == "delegate@contoso.com");
        Assert.NotNull(result.CalendarError);
    }

    [Fact]
    public async Task AMailboxThatGenuinelyHasADelegate_StillReportsIt()
    {
        var result = await Service(calendarFails: false).GetMailboxDelegationAsync("user@contoso.com");

        Assert.Null(result.CalendarError);
        Assert.Contains(result.Calendar, entry => entry.User == "delegate@contoso.com");
    }
}
