using System.Collections;
using System.Security.Claims;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static ExchangeAdminWeb.Tests.MigrationStubExchange;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Audit finding aud-a23l, parts 1 and 2. (1) <c>FindMigrationUserBatchAsync</c>, <c>GetMigrationUserReportAsync</c> and
/// the page's own catches over those reads formatted <c>ex.Message</c> themselves, so an error a cmdlet wrote under
/// <c>-ErrorAction Stop</c> still read "The running command stopped because the preference variable ..." in front of the
/// cmdlet's words - the prefix aud-a23i removed from RunAsync alone. (2) When every target was excluded because the
/// protection check could not answer, <c>CreateMigrationBatchAsync</c> said the target "is a protected principal".
/// </summary>
/// <remarks>
/// Part 3 (the audit's result filing) is in files another agent owns and is recorded in the finding for a follow-up.
/// The real service over <see cref="MigrationStubExchange"/>, its protection stand-in answering per target, and the page's
/// own handlers. No Exchange, no directory.
/// </remarks>
public sealed class MigrationMessageTests : IDisposable
{
    private const string Address = "alice@contoso.test";
    private const string Other = "bob@contoso.test";
    private const string Batch = "Wave 12";

    private static readonly ClaimsPrincipal Operator =
        new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "CONTOSO\\operator")], "test"));

    private readonly MigrationStubExchange _exchange = new();
    private readonly RecordingProgress _progress = new();

    public void Dispose() => _exchange.Dispose();

    private RenderableMigration Page()
    {
        var page = _exchange.Page(_exchange.Service());
        Set(page, "Progress", _progress);
        return page;
    }

    // ---- part 1: the cmdlet's own words ------------------------------------------------------------

    // The finding, the search. An error Get-MigrationUser writes under Stop is the search's error, word for word.
    [Fact]
    public async Task ASearchThatFails_GivesTheErrorsOwnWords()
    {
        _exchange.Failures["Get-MigrationUser"] = Refused("the search was refused");

        var result = await _exchange.Service().FindMigrationUserBatchAsync("alice");

        Assert.Equal("the search was refused", result.Error);
    }

    // The page's own catch over the mailbox load: its activity text ends in the error's words, not PowerShell's prefix.
    [Fact]
    public async Task AMailboxLoadThatFails_GivesTheErrorsOwnWords()
    {
        _exchange.Failures["Get-MigrationUser"] = Refused("the mailbox query was refused");
        var page = Page();
        Field("expandedBatch").SetValue(page, Batch);

        await Call(page, "LoadMailboxesFor", Batch);

        Assert.Equal((false, $"Unable to load mailboxes in {Batch}: the mailbox query was refused"),
            Assert.Single(_progress.Handles).Outcome);
    }

    // The same load entered from the address bar.
    [Fact]
    public async Task AMailboxLoadFromTheAddressThatFails_GivesTheErrorsOwnWords()
    {
        _exchange.Batches[Batch] = MigrationStubExchange.Batch(Batch);
        _exchange.Failures["Get-MigrationUser"] = Refused("the mailbox query was refused");
        var page = Page();
        page.OpenBatchParameter = Batch;
        using var renderer = new InteractiveRenderer();
        renderer.Attach(page);

        await renderer.Dispatcher.InvokeAsync(() => Call(page, "SyncOpenBatchFromUrl"));

        Assert.Equal((false, $"Unable to load mailboxes in {Batch}: the mailbox query was refused"),
            Assert.Single(_progress.Handles, handle => handle.Label == $"Loading mailboxes in {Batch}").Outcome);
    }

    // The report fetch: its failure, and the page's activity text, in the error's words.
    [Fact]
    public async Task AReportFetchThatFails_GivesTheErrorsOwnWords()
    {
        _exchange.Failures["Get-MigrationUserStatistics"] = Refused("the statistics service is unavailable");
        var page = Page();
        Field("expandedBatch").SetValue(page, Batch);

        await Call(page, "FetchUserReport", Address);

        Assert.Equal("the statistics service is unavailable", Field("reportFailure").GetValue(page));
        Assert.Equal((false, $"The report for {Address} could not be fetched: the statistics service is unavailable"),
            Assert.Single(_progress.Handles).Outcome);
    }

    // ---- part 2: "is a protected principal" only when the check said so ----------------------------

    private Task<PermissionResult> Create(params string[] targets) =>
        _exchange.Service().CreateMigrationBatchAsync(MigrationDirection.ToCloud, [.. targets], "b1", false, false, Operator);

    // The finding. A check that could not answer is not an answer that the target is protected: the target could not be
    // verified as unprotected, and nothing was written.
    [Theory]
    [InlineData("unavailable", "Protection check unavailable. Cannot verify if this mailbox is protected")]
    [InlineData("ambiguous", "Identity is ambiguous - matches multiple AD users")]
    [InlineData("failed", "Protection check failed: the protection rules could not be read")]
    public async Task ATargetWhoseCheckDidNotAnswer_CouldNotBeVerified_ItIsNotCalledProtected(string verdict, string reason)
    {
        _exchange.Protection = new Hashtable { ["*"] = verdict };

        var result = await Create(Address);

        Assert.False(result.Success);
        Assert.Equal($"The migration target could not be verified as unprotected: {Address} - {reason}; nothing was written.", result.Message);
        Assert.DoesNotContain("protected principal", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_exchange.CallList(), call => call.StartsWith("New-MigrationBatch", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SeveralTargets_NoneVerified_SaySo()
    {
        _exchange.Protection = new Hashtable { ["*"] = "unavailable" };

        var result = await Create(Address, Other);

        Assert.Equal("None of the 2 migration targets could be verified as unprotected; nothing was written.", result.Message);
        Assert.Equal(2, result.ExcludedTargets!.Count);
    }

    // The control: a target the check found protected is still called a protected principal.
    [Fact]
    public async Task AProtectedTarget_IsStillAProtectedPrincipal()
    {
        _exchange.Protection = new Hashtable { ["*"] = "protected" };

        var result = await Create(Address);

        Assert.Equal(
            "The migration target is a protected principal and was excluded. Nothing was created. Escalate to an administrator outside this tool if migration is required.",
            result.Message);
    }

    [Fact]
    public async Task SeveralProtectedTargets_AreStillProtectedPrincipals()
    {
        _exchange.Protection = new Hashtable { ["*"] = "protected" };

        var result = await Create(Address, Other);

        Assert.StartsWith("All 2 migration target(s) are protected principals and were excluded.", result.Message, StringComparison.Ordinal);
    }

    // A mix is counted apart: which kind each is, never all one kind.
    [Fact]
    public async Task AMixOfProtectedAndUnverified_CountsEachApart()
    {
        _exchange.Protection = new Hashtable { [Address] = "protected", [Other] = "unavailable" };

        var result = await Create(Address, Other);

        Assert.Equal(
            "All 2 migration targets were excluded: 1 is a protected principal and 1 could not be verified as unprotected; nothing was written. "
            + "Escalate the protected ones to an administrator outside this tool if migration is required.",
            result.Message);
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
