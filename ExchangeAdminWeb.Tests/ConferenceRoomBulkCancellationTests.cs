using System.Collections;
using System.Management.Automation.Runspaces;
using System.Text.Json;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Jobs;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Finding kd-58, slice A9 of docs/CancellationDepth-Plan.md: a Room Finder bulk row takes the job's token - the operator's
/// cancel request, or the host stopping - through <see cref="IConferenceRoomBulkOperations"/> to the room service's steps
/// before Set-Place, as the page's setup takes its activity's (A8). A row the token stops is filed by the processor: the
/// operator's cancel Cancelled, "Cancelled by the operator. Nothing was written.", under the job's captured actor, address
/// and ticket; the host stopping Failed, "The service stopped before this row was written. Nothing was written." The cancel
/// then goes on to the runner, which stops the batch without recording the row, and the job ends Cancelled. Before, the row
/// passed no token: the job's cancel waited for the row - preflights, Set-Place and all - before it stopped the batch.
/// </summary>
/// <remarks>
/// The real runner over a job store in a temporary database, the real processor and the real room service over
/// <see cref="ExchangeWriteHarness"/>'s stand-ins, the credential through a vault that waits at its secret request until the
/// caller cancels (<see cref="CancellableVaultCredentials"/>). Every wait bounded. No Exchange, no vault, no directory.
/// </remarks>
public sealed class ConferenceRoomBulkCancellationTests : IDisposable
{
    private const string Typed = "room1@contoso.test";
    private const string Building = "Building 1";
    private const string CheckedObject = "5a5a5a5a-0000-4000-8000-000000000001";
    private static readonly Guid Checked = Guid.Parse("0f0f0f0f-1111-4222-8333-444444444444");
    private static readonly TimeSpan Bound = CancellableVaultCredentials.Bound;

    private readonly ExchangeWriteHarness _h = new();
    private readonly CancellableVaultCredentials _credentials = new("ConferenceRooms");
    private readonly SectionAccessService _sectionAccess;
    private readonly BulkJobRepository _jobs;

    public ConferenceRoomBulkCancellationTests()
    {
        _h.AddMailbox(Typed, Checked, recipientTypeDetails: "RoomMailbox");
        _h.DirectoryObjects[CheckedObject] = new Hashtable
        {
            ["ObjectGUID"] = Guid.Parse(CheckedObject),
            ["DistinguishedName"] = "CN=Room 1,OU=Rooms,DC=contoso,DC=test",
        };
        // The processor runs only for an enabled module; Exchange Online stays on for the pool.
        _h.Enablement.SaveEnablement(new Dictionary<string, bool>
        {
            [ExoConnectionPool.ConfigModuleKey] = true,
            [ConferenceRoomBulkProcessor.ModuleName] = true,
        });
        var store = TestConfigStore.Create(_h.Dir);
        new SectionAccessRepository(store).SaveAll(new Dictionary<string, string[]> { ["ConferenceRooms"] = ["AllUsers"] });
        _sectionAccess = new SectionAccessService(_h.Config, NullLogger<SectionAccessService>.Instance, Environment(),
            new ModuleCatalog(), new SectionAccessRepository(store));
        var factory = new SqliteConnectionFactory(Path.Combine(_h.Dir, "exchangeadmin-jobs.db"));
        new JobStoreMigrator(factory).Migrate();
        _jobs = new BulkJobRepository(factory);
    }

    public void Dispose()
    {
        _credentials.Dispose();
        _h.Dispose();
    }

    // The operator cancels the job while its first row waits at the City/State/Country preflight's credential fetch. The row
    // ends there - no Set-Place - and is filed Cancelled under the job's actor, address and ticket; the runner records no row
    // outcome for it, the second row never starts, and the job ends Cancelled. Before, the row passed no token: the fetch
    // waited on, and the row ran to its end before the job stopped.
    [Fact]
    public async Task TheOperatorsCancel_DuringARowsPreflight_EndsItBeforeSetPlace_FiledCancelled_AndStopsTheJob()
    {
        var runner = Runner(new Service(_h, _credentials.Service));
        _jobs.Insert(FinderJob(rows: 2));

        var drain = runner.DrainQueueAsync(CancellationToken.None);
        await _credentials.SecretRequested.WaitAsync(Bound, TestContext.Current.CancellationToken);
        Assert.True(runner.CancelJob(JobId));
        await drain.WaitAsync(Bound, TestContext.Current.CancellationToken);

        Assert.True(_credentials.RequestCancelled);
        Assert.Equal(1, _credentials.SecretRequests); // the second row never started
        Assert.Empty(_h.CallsTo("Set-Place"));
        Assert.Empty(_h.Writes);
        Assert.Equal(BulkJobStatus.Cancelled, _jobs.Get(JobId)!.Status);
        Assert.Empty(_jobs.GetRows(JobId)); // the stopped row is no row outcome, as a row the cancel stopped before it started is not
        var row = Assert.Single(BulkRows());
        Assert.Equal("Cancelled", row.GetProperty("result").GetString());
        Assert.Equal(ConferenceRoomBulkProcessor.CancelledRow, row.GetProperty("error").GetString());
        Assert.Equal("Cancelled by the operator. Nothing was written.", row.GetProperty("error").GetString());
        Assert.Equal("jdoe", row.GetProperty("user").GetString());
        Assert.Equal("10.0.0.9", row.GetProperty("ip").GetString());
        Assert.Equal("INC1", row.GetProperty("ticket").GetString());
        Assert.Equal(Typed, row.GetProperty("target").GetString());
    }

    // The host stops while the first row waits at that fetch: the row ends there, nothing written, and is filed Failed in its
    // own words - it was not the operator's cancel (kd-57's rule: only the job's own cancel request is).
    [Fact]
    public async Task TheHostStopping_DuringARowsPreflight_EndsTheRow_FiledFailedInItsOwnWords()
    {
        var runner = Runner(new Service(_h, _credentials.Service));
        _jobs.Insert(FinderJob(rows: 2));
        using var shutdown = new CancellationTokenSource();

        var drain = runner.DrainQueueAsync(shutdown.Token);
        await _credentials.SecretRequested.WaitAsync(Bound, TestContext.Current.CancellationToken);
        await shutdown.CancelAsync();
        await Record.ExceptionAsync(() => drain.WaitAsync(Bound, TestContext.Current.CancellationToken));

        Assert.True(_credentials.RequestCancelled);
        Assert.Empty(_h.CallsTo("Set-Place"));
        Assert.Empty(_h.Writes);
        var row = Assert.Single(BulkRows());
        Assert.Equal("Failed", row.GetProperty("result").GetString());
        Assert.Equal("The service stopped before this row was written. Nothing was written.", row.GetProperty("error").GetString());
    }

    // kd-59's rule, kept: once Set-Place is sent the row runs on. The operator cancels as the row's City/State/Country write
    // fetches its credential, after Set-Place: the row finishes - Set-ADUser written - and is recorded as a row outcome, as
    // today, and the job stops before its second row.
    [Fact]
    public async Task TheOperatorsCancel_AfterARowsSetPlace_LetsTheRowFinish_AndStopsBeforeTheNext()
    {
        _credentials.WaitsAtSecretRequest = int.MaxValue;
        BulkJobService? runner = null;
        var service = new Service(_h, _credentials.Service)
        {
            BeforeLookupFor = "Set-ADUser synced attributes",
            BeforeLookup = () => Assert.True(runner!.CancelJob(JobId)),
        };
        runner = Runner(service);
        _jobs.Insert(FinderJob(rows: 2));

        await runner.DrainQueueAsync(CancellationToken.None).WaitAsync(Bound, TestContext.Current.CancellationToken);

        Assert.Single(_h.CallsTo("Set-Place"));
        Assert.Single(_h.CallsTo("Set-ADUser"));
        var recorded = Assert.Single(_jobs.GetRows(JobId));
        Assert.Equal(BulkJobRowStatus.Success, recorded.Status);
        Assert.Equal(BulkJobStatus.Cancelled, _jobs.Get(JobId)!.Status);
        Assert.Equal("Success", Assert.Single(BulkRows()).GetProperty("result").GetString());
    }

    // The processor's new dependency is in the app's container: Program.cs registers the job store - the flag's store - as the
    // seam. No test builds the app's container, so without this a missing registration would show first as a Room Finder job
    // the runner could not build.
    [Fact]
    public void TheAppRegistersTheJobStoreAsTheCancelRequestSeam()
    {
        var program = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Program.cs"));

        Assert.Matches(@"AddSingleton<ExchangeAdminWeb\.Services\.Jobs\.IBulkJobCancelRequests>\(sp =>\s*sp\.GetRequiredService<ExchangeAdminWeb\.Services\.Jobs\.BulkJobRepository>\(\)\)", program);
    }

    // ---- the harness ------------------------------------------------------------------------------------------------------

    private const string JobId = "j-a9";

    /// <summary>The room service over the harness, its credentials through the fixture's vault.</summary>
    private sealed class Service(ExchangeWriteHarness h, ModuleCredentialService credentials)
        : ConferenceRoomService(h.Pool, null!, h.ModuleConfig, credentials, h.Trace, h.Config, NullLogger<ConferenceRoomService>.Instance)
    {
        /// <summary>Run as a lookup whose purpose contains <see cref="BeforeLookupFor"/> starts: where a test cancels.</summary>
        public Action? BeforeLookup { get; init; }

        public string? BeforeLookupFor { get; init; }

        protected override Task<ModuleCredentialLookup> LookupDirectoryCredentialAsync(string purpose, CancellationToken cancellationToken)
        {
            if (BeforeLookupFor is { } text && purpose.Contains(text, StringComparison.Ordinal))
                BeforeLookup?.Invoke();
            return base.LookupDirectoryCredentialAsync(purpose, cancellationToken);
        }

        protected override Runspace OpenDirectoryRunspace() => h.OpenDirectoryRunspace();
    }

    /// <summary>
    /// The runner over the fixture's job store, its one processor the real Room Finder processor over
    /// <paramref name="service"/>, which reads the job's cancel flag from that store.
    /// </summary>
    private BulkJobService Runner(ConferenceRoomService service)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => new ConferenceRoomBulkProcessor(service, Gate(), _sectionAccess, _h.Enablement, _h.Trace, _h.Audit,
            Substitute.For<EmailService>(_h.Config, NullLogger<EmailService>.Instance), NullLogger<ConferenceRoomBulkProcessor>.Instance,
            _jobs));
        var provider = services.BuildServiceProvider();
        var registry = new BulkJobProcessorRegistry(
            [new KeyValuePair<string, Type>(ConferenceRoomBulkProcessor.ModuleName, typeof(ConferenceRoomBulkProcessor))]);
        return new BulkJobService(provider.GetRequiredService<IServiceScopeFactory>(), _jobs, registry,
            new ConfigurationBuilder().Build(), NullLogger<BulkJobService>.Instance);
    }

    /// <summary>A queued Room Finder job of <paramref name="rows"/> rows, each the same room with a city - so each row's first step after its room read is the City/State/Country preflight's credential fetch.</summary>
    private static BulkJob FinderJob(int rows)
    {
        var row = new FinderCsvRow
        {
            Email = Typed,
            City = "Dublin",
            Building = Building,
            Capacity = 8,
            Floor = "2",
            FloorLabel = "Second",
            DisplayDeviceName = "TV",
            VideoDeviceName = "Polycom",
            TimeZone = "GMT Standard Time",
        };
        string[] groups = ["AllUsers"];
        var snapshot = new JobAuthorizationSnapshot { Section = "ConferenceRooms", RoleClaims = groups, AuthorizedGroups = groups };
        return new BulkJob
        {
            Id = JobId,
            ModuleId = ConferenceRoomBulkProcessor.ModuleName,
            JobType = ConferenceRoomJobPayload.FinderJobType,
            Status = BulkJobStatus.Queued,
            SubmittedBy = "jdoe",
            SubmittedIp = "10.0.0.9",
            Ticket = "INC1",
            AuthSnapshotJson = snapshot.ToJson(),
            PayloadJson = JsonSerializer.Serialize(new ConferenceRoomJobPayload
            {
                Kind = ConferenceRoomJobPayload.FinderJobType,
                FinderRows = Enumerable.Repeat(row, rows).ToList(),
            }),
            SubmittedAtUtc = DateTime.UtcNow,
        };
    }

    /// <summary>The audit rows the job's rows filed.</summary>
    private List<JsonElement> BulkRows() =>
        _h.AuditRows().Where(r => r.GetProperty("action").GetString() == "ConferenceRooms_SetMetadata_Bulk").ToList();

    /// <summary>A protected-principal check that resolves every target to the room, unprotected.</summary>
    private sealed class RoomResolvingCheck(IWebHostEnvironment env, IConfiguration config, ModuleConfigService moduleConfig,
        ProtectedPrincipalRepository repo, DelineaService delinea)
        : ProtectedPrincipalService(env, config, moduleConfig, repo, delinea, NullLogger<ProtectedPrincipalService>.Instance)
    {
        public override Task<(ResolvedDirectoryPrincipal? principal, ResolutionStatus status)> ResolveExchangeTargetAsync(string identity) =>
            Task.FromResult<(ResolvedDirectoryPrincipal? principal, ResolutionStatus status)>((
                new ResolvedDirectoryPrincipal("Test", "Room 1", Typed, null, Typed, null, CheckedObject, null, ExchangeGuid: Checked),
                ResolutionStatus.Resolved));

        public override Task<ProtectedPrincipalResult> CheckAsync(ResolvedDirectoryPrincipal target) =>
            Task.FromResult(ProtectedPrincipalResult.NotProtected());
    }

    private ConferenceRoomProtectionGate Gate()
    {
        var env = Environment();
        var extLog = new ExtendedLogService(_h.Config, env, TestConfigStore.CreateAppSettings(_h.Dir), NullLogger<ExtendedLogService>.Instance);
        var delinea = new DelineaService(HttpClients(), _h.Config, NullLogger<DelineaService>.Instance, extLog, _h.Trace);
        var check = new RoomResolvingCheck(env, _h.Config, _h.ModuleConfig, TestConfigStore.CreateProtectedPrincipal(_h.Dir), delinea);
        var servicers = new ProtectedPrincipalServicerService(_sectionAccess, NullLogger<ProtectedPrincipalServicerService>.Instance);
        return new ConferenceRoomProtectionGate(check, servicers, NullLogger<ConferenceRoomProtectionGate>.Instance);
    }

    private IWebHostEnvironment Environment()
    {
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_h.Dir);
        return env;
    }

    private static IHttpClientFactory HttpClients()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());
        return factory;
    }
}
