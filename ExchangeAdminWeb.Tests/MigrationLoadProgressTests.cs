using System.Text;
using ExchangeAdminWeb.Components.Pages;
using ExchangeAdminWeb.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using static ExchangeAdminWeb.Tests.MigrationStubExchange;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Audit finding aud-a23i, part 2. Every load on the Migration page completed its progress activity as succeeded in its
/// <c>finally</c>, so a load that failed told the status frame it had finished well. Each now completes with its real
/// outcome: not succeeded, with the failure text, when its read failed or could not reach an answer.
/// </summary>
/// <remarks>
/// The page's own handlers over <see cref="MigrationStubExchange"/>, with a recording progress channel whose first
/// Complete wins, as the real one's does. No Exchange.
/// </remarks>
public sealed class MigrationLoadProgressTests : IDisposable
{
    private const string Batch = "Wave 12 / Operations";
    private const string Address = "alice@contoso.test";

    private readonly MigrationStubExchange _exchange = new();
    private readonly RecordingProgress _progress = new();

    public void Dispose() => _exchange.Dispose();

    private RenderableMigration Page(bool connects = true)
    {
        var page = _exchange.Page(_exchange.Service(connects));
        Set(page, "Progress", _progress);
        return page;
    }

    private (bool Success, string? Message) Outcome(string label) =>
        Assert.Single(_progress.Handles, handle => handle.Label == label).Outcome
        ?? throw new Xunit.Sdk.XunitException($"the activity '{label}' was never completed");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadBatchList_CompletesWithItsOutcome(bool fails)
    {
        _exchange.Batches[Batch] = MigrationStubExchange.Batch(Batch);
        if (fails)
            _exchange.Failures["Get-MigrationBatch"] = Refused("the service is throttling this account");

        await Call(Page(), "LoadBatchList");

        Assert.Equal(fails
            ? (false, "The migration batches could not be read: the service is throttling this account")
            : (true, null), Outcome("Loading migration batches"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadMailboxesFor_CompletesWithItsOutcome(bool fails)
    {
        _exchange.BatchUsers[Batch] = new List<object> { Row(("Identity", Address), ("Status", "Synced")) };
        if (fails)
            _exchange.Failures["Get-MigrationUser"] = Refused("Mailbox query unavailable");
        var page = Page();
        Field("expandedBatch").SetValue(page, Batch);

        await Call(page, "LoadMailboxesFor", Batch);

        var (success, message) = Outcome($"Loading mailboxes in {Batch}");
        Assert.Equal(!fails, success);
        if (fails)
        {
            Assert.StartsWith($"Unable to load mailboxes in {Batch}: ", message, StringComparison.Ordinal);
            Assert.Contains("Mailbox query unavailable", message, StringComparison.Ordinal);
        }
        else
        {
            Assert.Null(message);
        }
    }

    // Entered from the address bar: the same load, reached by a link or browser Back.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SyncOpenBatchFromUrl_CompletesWithItsOutcome(bool fails)
    {
        _exchange.Batches[Batch] = MigrationStubExchange.Batch(Batch);
        _exchange.BatchUsers[Batch] = new List<object> { Row(("Identity", Address), ("Status", "Synced")) };
        if (fails)
            _exchange.Failures["Get-MigrationUser"] = Refused("Mailbox query unavailable");
        var page = Page();
        page.OpenBatchParameter = Batch;
        using var renderer = new InteractiveRenderer();
        renderer.Attach(page);

        await renderer.Dispatcher.InvokeAsync(() => Call(page, "SyncOpenBatchFromUrl"));

        var (success, message) = Outcome($"Loading mailboxes in {Batch}");
        Assert.Equal(!fails, success);
        Assert.Equal(fails, message?.Contains("Mailbox query unavailable", StringComparison.Ordinal) == true);
    }

    // A check that could not reach an answer failed; one that answered - eligible or not - succeeded.
    [Theory]
    [InlineData("undetermined")]
    [InlineData("eligible")]
    [InlineData("ineligible")]
    [InlineData("throws before the service returns")]
    public async Task CheckSingleEligibility_CompletesWithItsOutcome(string answer)
    {
        _exchange.Mailboxes[Address] = CloudMailbox(Address);
        if (answer == "undetermined")
            _exchange.Failures["Get-MigrationUser"] = Refused("the account lacks the Mail Recipients role");
        if (answer == "ineligible")
            _exchange.MigrationUsers[Address] = Row(("Identity", Address));
        var page = Page(connects: answer != "throws before the service returns");
        Field("singleEmail").SetValue(page, Address);
        Field("singleMigrationDirection").SetValue(page, MigrationDirection.ToOnPrem);

        await Call(page, "CheckSingleEligibility");

        Assert.Equal(answer switch
        {
            "undetermined" => (false, "Could not be determined: Get-MigrationUser failed: the account lacks the Mail Recipients role"),
            "throws before the service returns" => (false, "Could not be determined: the Exchange Online sign-in could not be completed"),
            _ => (true, (string?)null),
        }, Outcome("Checking eligibility"));
    }

    [Theory]
    [InlineData("one row undetermined")]
    [InlineData("every row answered")]
    [InlineData("the file cannot be read")]
    public async Task CheckBulkEligibility_CompletesWithItsOutcome(string run)
    {
        _exchange.Mailboxes[Address] = CloudMailbox(Address);
        _exchange.Mailboxes["bob@contoso.test"] = CloudMailbox("bob@contoso.test");
        if (run == "one row undetermined")
            _exchange.Failures[$"Get-MigrationUser {Address}"] = Refused("the account lacks the Mail Recipients role");
        var file = Substitute.For<IBrowserFile>();
        file.Name.Returns("wave.csv");
        if (run == "the file cannot be read")
            file.OpenReadStream(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(_ => throw new IOException("the upload was interrupted"));
        else
            file.OpenReadStream(Arg.Any<long>(), Arg.Any<CancellationToken>())
                .Returns(_ => new MemoryStream(Encoding.UTF8.GetBytes($"EmailAddress\n{Address}\nbob@contoso.test\n")));
        var page = Page();
        Field("csvFile").SetValue(page, file);
        Field("migrationDirection").SetValue(page, MigrationDirection.ToOnPrem);

        await Call(page, "CheckBulkEligibility");

        Assert.Equal(run switch
        {
            "one row undetermined" => (false, "1 of 2 eligibility checks could not be determined"),
            "the file cannot be read" => (false, "The bulk eligibility check failed: the upload was interrupted"),
            _ => (true, (string?)null),
        }, Outcome("Checking bulk eligibility"));
    }

    // A search that ran and found nothing answered; one that could not run failed.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SearchUser_CompletesWithItsOutcome(bool fails)
    {
        if (fails)
            _exchange.Failures["Get-MigrationUser"] = Refused("the search was refused");
        var page = Page();
        Field("migrationBatches").SetValue(page, new List<MigrationBatchInfo>());
        Field("searchQuery").SetValue(page, "zed@contoso.test");

        await Call(page, "SearchUser");

        var (success, message) = Outcome("Searching for the user");
        Assert.Equal(!fails, success);
        if (fails)
        {
            Assert.StartsWith("Search failed: ", message, StringComparison.Ordinal);
            Assert.Contains("the search was refused", message, StringComparison.Ordinal);
        }
        else
        {
            Assert.Null(message);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FetchUserReport_CompletesWithItsOutcome(bool fails)
    {
        var page = Page(connects: !fails);
        Field("expandedBatch").SetValue(page, Batch);

        await Call(page, "FetchUserReport", Address);

        Assert.Equal(fails
            ? (false, $"The report for {Address} could not be fetched: the Exchange Online sign-in could not be completed")
            : (true, null), Outcome($"Fetching the report for {Address}"));
    }

#pragma warning disable BL0006 // a test renderer, so the page attaches without a circuit; no frames are read
    /// <summary>A renderer that says it is interactive, so a handler gated on RendererInfo runs.</summary>
    private sealed class InteractiveRenderer() : Renderer(new ServiceCollection().BuildServiceProvider(), NullLoggerFactory.Instance)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        protected override RendererInfo RendererInfo { get; } = new("Server", isInteractive: true);
        public int Attach(IComponent component) => AssignRootComponentId(component);
        protected override Task UpdateDisplayAsync(in RenderBatch batch) => Task.CompletedTask;
        protected override void HandleException(Exception exception) => throw exception;
    }
#pragma warning restore BL0006
}
