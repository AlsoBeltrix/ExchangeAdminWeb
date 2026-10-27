using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using static ExchangeAdminWeb.Tests.MigrationStubExchange;
using static ExchangeAdminWeb.Tests.SubmittedValueHarness;
using Page = ExchangeAdminWeb.Components.Pages.RiskyUsers;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// kd-72 on Risky Users: the single-user lookup and the list query took what they ran on - the user, the filter - on
/// the line after their paint yield, so a keystroke landing in that yield changed what was asked for, labelled and
/// audited. Both now read it above the yield, and a second submit during either is ignored.
/// </summary>
/// <remarks>
/// The handlers are driven through their compiled code over the real <see cref="RiskyUsersService"/>'s test seam with
/// no Graph client, so each read fails at once without reaching Graph and the failure is audited under the target the
/// handler built - which is the record this asserts. <see cref="SubmittedValueHarness.HeldContinuations"/> holds each
/// handler at its yield while the test changes the boxes.
/// </remarks>
public sealed class RiskyUsersSubmittedQueryTests : IDisposable
{
    private const string UserA = "first.user@contoso.com";
    private const string UserB = "second.user@contoso.com";

    private readonly TempDir _temp = new();
    private readonly AuditService _audit;
    private readonly RecordingProgress _progress = new();
    private int _graphAsks;

    public RiskyUsersSubmittedQueryTests()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Audit:LogRoot"] = _temp.Path,
        }).Build();
        var log = new JsonlLogService(config, NullLogger<JsonlLogService>.Instance);
        _audit = new AuditService(log, new OperationTraceService(config, log));
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void AChangeLandingInTheLookupsYield_LeavesTheLabelAndTheAuditRowOnTheUserSubmitted()
    {
        var page = PageFor();
        SetField(page, "lookupUpn", UserA);

        RunWithChangeWhileSuspended(() => Call(page, "LookupUserAsync"), () => SetField(page, "lookupUpn", UserB));

        Assert.Equal($"Looking up {UserA}", Assert.Single(_progress.Handles).Label);
        var row = Assert.Single(AuditRows(_temp.Path));
        Assert.Equal("RiskyUsers_Lookup", Text(row, "action"));
        Assert.Equal(UserA, Text(row, "target"));
    }

    [Fact]
    public void AChangeLandingInTheQuerysYield_LeavesTheAuditRowOnTheFilterSubmitted()
    {
        var page = PageFor();
        SetField(page, "riskLevelFilter", "high");
        SetField(page, "riskStateFilter", "atRisk");
        SetField(page, "upnContains", "first");

        RunWithChangeWhileSuspended(() => Call(page, "LoadRiskyUsersAsync"), () =>
        {
            SetField(page, "riskLevelFilter", "low");
            SetField(page, "riskStateFilter", "dismissed");
            SetField(page, "upnContains", "second");
        });

        var row = Assert.Single(AuditRows(_temp.Path));
        Assert.Equal("RiskyUsers_List", Text(row, "action"));
        Assert.Equal("riskLevel=high;riskState=atRisk;upnContains=first", Text(row, "target"));
    }

    [Theory]
    [InlineData("LookupUserAsync")]
    [InlineData("LoadRiskyUsersAsync")]
    public void ASecondSubmitWhileAReadRuns_IsIgnored_NotRunBesideIt(string handler)
    {
        var page = PageFor();
        SetField(page, "lookupUpn", UserA);

        RunWithChangeWhileSuspended(() => Call(page, handler), () =>
        {
            // A second Enter, or a click the browser sent before the disabled button rendered.
            var second = Call(page, handler);
            Assert.True(second.IsCompleted, $"a second submit during {handler} started a second read");
        });

        Assert.Single(_progress.Handles);
        Assert.Single(AuditRows(_temp.Path));
        Assert.Equal(1, _graphAsks);
    }

    private Page PageFor()
    {
        var page = new Page();
        Set(page, "RiskyUsersService", new RiskyUsersService(() =>
        {
            Interlocked.Increment(ref _graphAsks);
            return Task.FromResult<GraphTokenClient?>(null);
        }));
        Set(page, "Audit", _audit);
        Set(page, "Progress", _progress);
        Set(page, "Logger", NullLogger<Page>.Instance);
        SetField(page, "currentUser", @"CONTOSO\operator");
        SetField(page, "clientIpAddress", "10.0.0.5");
        return page;
    }
}
