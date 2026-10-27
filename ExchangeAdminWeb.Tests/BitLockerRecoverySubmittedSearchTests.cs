using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using static ExchangeAdminWeb.Tests.MigrationStubExchange;
using static ExchangeAdminWeb.Tests.SubmittedValueHarness;
using Page = ExchangeAdminWeb.Components.Pages.BitLockerRecovery;

namespace ExchangeAdminWeb.Tests;

#pragma warning disable BL0006 // the handler renderer the page's StateHasChanged needs (SubmittedValueHarness)

/// <summary>
/// kd-72 on BitLocker Recovery: <c>SearchAsync</c> read the name, the recovery-screen value, the ticket and the live-AD
/// choice after its paint yield, and none of those controls is disabled while a search runs - so a keystroke, or a
/// tick, landing in the yield changed the search, its label and the audit row recording who looked for which keys.
/// They are now read above the yield, and a second submit during a search is ignored.
/// </summary>
/// <remarks>
/// Driven through the compiled handler over the real <see cref="BitLockerRecoveryService"/> with a ticket gate that
/// records the ticket it is asked about and no archive configured, so the search fails at the archive and is audited -
/// nothing reaches a directory. The handler renders before its yield, so it runs on a renderer's dispatcher and the
/// change is applied in the yield (<see cref="SubmittedValueHarness.RunOnDispatcherWithChangeInTheYield"/>).
/// </remarks>
public sealed class BitLockerRecoverySubmittedSearchTests : IDisposable
{
    private const string NameA = "PC-FIRST";
    private const string TicketA = "INC0001";

    private readonly TempDir _temp = new();
    private readonly AuditService _audit;
    private readonly RecordingProgress _progress = new();
    private readonly RecordingTicketGate _gate = new();
    private readonly HandlerRenderer _renderer = new();
    private readonly BitLockerRecoveryService _service;

    public BitLockerRecoverySubmittedSearchTests()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Audit:LogRoot"] = _temp.Path,
        }).Build();
        var log = new JsonlLogService(config, NullLogger<JsonlLogService>.Instance);
        _audit = new AuditService(log, new OperationTraceService(config, log));

        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_temp.Path);
        var moduleConfig = new ModuleConfigService(new ModuleCatalog(), env,
            TestConfigStore.CreateModuleConfig(_temp.Path), NullLogger<ModuleConfigService>.Instance);
        _service = new BitLockerRecoveryService(moduleConfig, Substitute.For<IBitLockerLiveDirectorySearch>(), _gate,
            NullLogger<BitLockerRecoveryService>.Instance);
    }

    public void Dispose()
    {
        _renderer.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public async Task AChangeLandingInTheSearchsYield_LeavesTheSearchTheLabelAndTheAuditRowOnWhatWasSubmitted()
    {
        var page = PageFor();

        var running = RunOnDispatcherWithChangeInTheYield(_renderer, () => Call(page, "SearchAsync"), () =>
        {
            SetField(page, "searchTerm", "PC-SECOND");
            SetField(page, "ticketNumber", "INC9999");
            SetField(page, "includeLiveAd", false);
            SetField(page, "keyId", "ABCD1234");
        });
        await running;

        Assert.Equal("Searching the archive and live AD - this can take a while on a broad name",
            Assert.Single(_progress.Handles).Label);
        Assert.Equal([TicketA], _gate.Asked);
        var row = Assert.Single(AuditRows(_temp.Path));
        Assert.Equal("BitLockerSearchByName", Text(row, "action"));
        Assert.Equal(NameA, Text(row, "target"));
        Assert.Equal(TicketA, Text(row, "ticket"));
        Assert.True(Field<bool>(page, "searchedLiveAd"));
        Assert.Equal(TicketA, Field<string>(page, "searchTicket"));
    }

    [Fact]
    public async Task ASecondSubmitWhileASearchRuns_IsIgnored_NotRunBesideIt()
    {
        var page = PageFor();

        var running = RunOnDispatcherWithChangeInTheYield(_renderer, () => Call(page, "SearchAsync"), () =>
        {
            // A second Enter, or a click the browser sent before the disabled button rendered.
            var second = Call(page, "SearchAsync");
            Assert.True(second.IsCompleted, "a second submit during a search started a second search");
        });
        await running;

        Assert.Single(_progress.Handles);
        Assert.Single(_gate.Asked);
        Assert.Single(AuditRows(_temp.Path));
    }

    private Page PageFor()
    {
        var page = new QuietPage();
        _renderer.Attach(page);
        Set(page, "RecoveryService", _service);
        Set(page, "Progress", _progress);
        Set(page, "Audit", _audit);
        Set(page, "Logger", NullLogger<Page>.Instance);
        SetField(page, "currentUser", @"CONTOSO\operator");
        SetField(page, "clientIpAddress", "10.0.0.5");
        SetField(page, "searchTerm", NameA);
        SetField(page, "ticketNumber", TicketA);
        SetField(page, "includeLiveAd", true);
        return page;
    }

    /// <summary>The page with nothing rendered: the handler's StateHasChanged needs a renderer, not markup.</summary>
    private sealed class QuietPage : Page
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
        }
    }

    /// <summary>Accepts every ticket and records which it was asked about.</summary>
    private sealed class RecordingTicketGate : ITicketValidator
    {
        private readonly List<string?> _asked = [];

        public IReadOnlyList<string?> Asked
        {
            get { lock (_asked) return _asked.ToList(); }
        }

        public Task<TicketGateResult> ValidateAsync(string moduleId, string? ticketNumber)
        {
            lock (_asked) _asked.Add(ticketNumber);
            return Task.FromResult(new TicketGateResult(TicketGateOutcome.Accepted, null));
        }
    }
}
