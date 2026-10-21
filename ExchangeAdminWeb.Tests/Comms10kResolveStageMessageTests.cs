using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExchangeAdminWeb.Tests;

public sealed partial class Comms10kRefusalTests
{
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
}
