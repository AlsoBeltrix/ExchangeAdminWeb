using System.Reflection;
using System.Text;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Jobs;
using ExchangeAdminWeb.Services.Progress;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Page = ExchangeAdminWeb.Components.Pages.Comms10k;

namespace ExchangeAdminWeb.Tests;

// The page's compiled render tree is read directly; no browser or rendering host is involved.
#pragma warning disable BL0006
public sealed partial class Comms10kRefusalTests
{
    private const string ResolveSummaryAlias = "person.alias@example.test";
    private const string ResolveSummaryGhost = "ghost@example.test";

    [Fact]
    public async Task TheResolveStagePersistsAddressesAndDistinctMembersSeparately()
    {
        // cdr-followup-1. Two valid uploaded aliases of one person resolve to one GUID with no
        // rejection, and the stage persisted "1 of 2 matched in AD" - which tells whoever reads the
        // job's rows that one address missed. Driven through the real runner, the real processor
        // and the real service resolution (only the directory query is the fixture's), so what is
        // asserted is the message as STORED, which is what the job pages show.
        var alias = "person.alias@example.test";
        using var fixture = new Fixture();
        fixture.Service.Candidates = [new(PersonGuid, PersonDn, Address, alias)];
        var processor = fixture.Processor(fixture.Service);
        var (_, repository) = fixture.JobQueue();
        using var provider = new ServiceCollection().AddScoped(_ => processor).BuildServiceProvider();
        var runner = new BulkJobService(provider.GetRequiredService<IServiceScopeFactory>(), repository,
            new BulkJobProcessorRegistry([new("Comms10k", typeof(Comms10kReplaceProcessor))]), fixture.Config,
            NullLogger<BulkJobService>.Instance);
        var job = C2Job(C2Snapshot(), [new(PersonGuid, PersonDn, Address), new(PersonGuid, PersonDn, alias)]);
        repository.Insert(job);

        await runner.DrainQueueAsync(TestContext.Current.CancellationToken);

        var stage = Assert.Single(runner.GetRows(job.Id), row => row.RowIndex == Comms10kReplaceProcessor.ResolveRow);
        Assert.Equal(BulkJobRowStatus.Success, stage.Status);
        Assert.Equal("2 addresses resolved to 1 distinct member; 0 rejected", stage.Message);

        // Both addresses really were asked for, in one query, and came back as the one person.
        Assert.Equal([Address, alias], Assert.Single(fixture.Service.AddressQueries));
    }

    [Fact]
    public async Task TheValidationBannerNamesAddressesAndDistinctMembersSeparately()
    {
        // cdr-followup-2. The same two aliases on the page: Validate's banner read "1 of 2 email(s)
        // matched in AD." with nothing rejected and Replace live. The real ValidateEmails over the
        // real service resolution, then the page's own render tree.
        using var fixture = new Fixture();
        using var progress = new ActivityProgressService();
        var (page, _) = C6Page(fixture, progress);
        fixture.Service.Candidates = [new(PersonGuid, PersonDn, Address, ResolveSummaryAlias)];
        SetField(page, "parsedEmails", new List<string> { Address, ResolveSummaryAlias });

        await InvokePage(page, "ValidateEmails");

        Assert.True(CanReplace(page));
        var text = ResolveSummaryRenderedText(page);
        Assert.Contains("Validated; not applied yet.", text, StringComparison.Ordinal);
        Assert.Contains("2 addresses resolved to 1 distinct member.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("matched in AD", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheValidationFailureNamesAddressesAndDistinctMembersSeparately()
    {
        // cdr-followup-2. Two aliases and one unknown address: the failure banner carried the
        // service's "1 resolved, 1 rejected." for three uploaded rows.
        using var fixture = new Fixture();
        using var progress = new ActivityProgressService();
        var (page, _) = C6Page(fixture, progress);
        fixture.Service.Candidates = [new(PersonGuid, PersonDn, Address, ResolveSummaryAlias)];
        SetField(page, "parsedEmails", new List<string> { Address, ResolveSummaryAlias, ResolveSummaryGhost });

        await InvokePage(page, "ValidateEmails");

        Assert.False(CanReplace(page));
        var text = ResolveSummaryRenderedText(page);
        Assert.Contains("2 addresses resolved to 1 distinct member; 1 rejected.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("1 resolved, 1 rejected", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheServiceResolveMessageNamesAddressesAndDistinctMembersSeparately()
    {
        // cdr-followup-2. The service's message is also what the job's refusal reason carries when
        // the run-time resolve finds a rejection, so it is pinned at its source as well.
        using var fixture = new Fixture();
        fixture.Service.Candidates = [new(PersonGuid, PersonDn, Address, ResolveSummaryAlias)];

        var result = await fixture.Service.ResolveEmailsAsync([Address, ResolveSummaryAlias, ResolveSummaryGhost]);

        Assert.False(result.Success);
        Assert.Equal(ResolveSummaryGhost, Assert.Single(result.Rejections).Address);
        Assert.Equal("2 addresses resolved to 1 distinct member; 1 rejected.", result.Message);
    }

    private static string ResolveSummaryRenderedText(Page page)
    {
        using var builder = new RenderTreeBuilder();
        typeof(Page).GetMethod("BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, [builder]);
        var frames = builder.GetFrames();
        var text = new StringBuilder();
        for (var i = 0; i < frames.Count; i++)
        {
            var frame = frames.Array[i];
            if (frame.FrameType == RenderTreeFrameType.Text)
                text.Append(frame.TextContent);
            else if (frame.FrameType == RenderTreeFrameType.Markup)
                text.Append(frame.MarkupContent);
        }

        return text.ToString();
    }
}
#pragma warning restore BL0006
