using System.Management.Automation.Runspaces;
using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Jobs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Page = ExchangeAdminWeb.Components.Pages.Comms10k;

namespace ExchangeAdminWeb.Tests;

public sealed partial class Comms10kRefusalTests
{
    private static readonly Guid PersonGuid = new("acb299e2-f9db-4136-9f45-479b295fc845");
    private const string PersonDn = "CN=Person,DC=example,DC=test";
    private const string Address = "person@example.test";

    [Theory]
    [InlineData("missing")]
    [InlineData("blank")]
    [InlineData("ambiguous")]
    [InlineData("missing-dn")]
    public async Task ServiceRejectsTheWholeResolutionWhenAnyAddressIsRejected(string rejection)
    {
        using var fixture = new Fixture();
        var service = fixture.Service;
        var rejectedAddress = rejection == "blank" ? " " : "rejected@example.test";
        service.Candidates = rejection switch
        {
            "ambiguous" => [Candidate(), Candidate(Guid.NewGuid(), "CN=Other,DC=x", rejectedAddress),
                Candidate(Guid.NewGuid(), "CN=Another,DC=x", rejectedAddress)],
            "missing-dn" => [Candidate(), Candidate(Guid.NewGuid(), "", rejectedAddress)],
            _ => [Candidate()],
        };

        var result = await service.ResolveEmailsAsync([Address, rejectedAddress]);

        Assert.False(result.Success);
        Assert.Equal(new Comms10kAddressResolver.ResolvedMember(PersonGuid, PersonDn), Assert.Single(result.ResolvedMembers));
        Assert.Equal(rejectedAddress, Assert.Single(result.Rejections).Address);
    }

    [Fact]
    public async Task ServiceCompleteResolutionSucceedsWithThePairedIdentity()
    {
        using var fixture = new Fixture();

        var result = await fixture.Service.ResolveEmailsAsync([Address]);

        Assert.True(result.Success);
        Assert.Empty(result.Rejections);
        Assert.Equal(new Comms10kAddressResolver.ResolvedMember(PersonGuid, PersonDn), Assert.Single(result.ResolvedMembers));
    }

    [Theory]
    [InlineData("partial")]
    [InlineData("reported-failure")]
    [InlineData("empty")]
    [InlineData("thrown")]
    public async Task FailedResolveAuditsImmediatelyAndNeverCallsReplaceEvenWhenTheRunnerContinues(string failure)
    {
        using var fixture = new Fixture();
        var service = Substitute.For<Comms10kService>(NullLogger<Comms10kService>.Instance, null!, null!, fixture.Config);
        var resolved = CompleteResolution();
        var expectedReason = failure switch
        {
            "partial" => "Address validation rejected 1 address(es). Fix the file and re-upload. One address did not match.",
            "reported-failure" => "AD credentials unavailable.",
            "empty" => "No members resolved. An empty list cannot replace the broadcast list.",
            _ => "Address resolution failed before any change: Directory lookup unavailable.",
        };
        if (failure == "partial")
        {
            // Deliberately inconsistent success: the processor must also enforce completeness.
            resolved.Rejections.Add(new("ghost@example.test", Comms10kAddressResolver.Reasons.NoMatch));
            resolved.Message = "One address did not match.";
        }
        else if (failure == "reported-failure")
        {
            resolved.Success = false;
            resolved.Message = expectedReason;
        }
        else if (failure == "empty")
            resolved = new Comms10kResolveResult { Success = true };

        service.ResolveEmailsAsync(Arg.Any<List<string>>()).Returns(failure == "thrown"
            ? Task.FromException<Comms10kResolveResult>(new InvalidOperationException("Directory lookup unavailable."))
            : Task.FromResult(resolved));
        var processor = fixture.Processor(service);
        var job = Job();

        var resolveRow = await processor.ProcessRowAsync(job, 0, CancellationToken.None);

        Assert.Equal(BulkJobRowStatus.Failed, resolveRow.Status);
        Assert.Equal(expectedReason, resolveRow.Message);
        fixture.AssertAudit(expectedReason, Comms10kOutcome.RefusedBeforeAnyChange);
        var replaceRow = await processor.ProcessRowAsync(job, 1, CancellationToken.None);
        Assert.Equal(BulkJobRowStatus.Failed, replaceRow.Status);
        Assert.Equal(expectedReason, replaceRow.Message);
        await service.DidNotReceive().ExecuteReplaceAsync(Arg.Any<IReadOnlyList<Comms10kAddressResolver.ResolvedMember>>(), Arg.Any<string>(), Arg.Any<Func<string?>>());
        await processor.OnJobCompletedAsync(job);
        fixture.AssertAudit(expectedReason, Comms10kOutcome.RefusedBeforeAnyChange);
        fixture.AssertNotification(expectedReason, Comms10kOutcome.RefusedBeforeAnyChange);
    }

    [Fact]
    public async Task ReplaceWithoutAResolveRefusesWithOneAuditAndItsCompletionReason()
    {
        using var fixture = new Fixture();
        var service = Substitute.For<Comms10kService>(NullLogger<Comms10kService>.Instance, null!, null!, fixture.Config);
        var processor = fixture.Processor(service);
        var job = Job();
        const string reason = "Addresses were not resolved, so nothing was written.";

        var row = await processor.ProcessRowAsync(job, 1, CancellationToken.None);
        await processor.OnJobCompletedAsync(job);

        Assert.Equal(BulkJobRowStatus.Failed, row.Status);
        Assert.Equal(reason, row.Message);
        await service.DidNotReceive().ExecuteReplaceAsync(Arg.Any<IReadOnlyList<Comms10kAddressResolver.ResolvedMember>>(), Arg.Any<string>(), Arg.Any<Func<string?>>());
        fixture.AssertAudit(reason, Comms10kOutcome.RefusedBeforeAnyChange);
        fixture.AssertNotification(reason, Comms10kOutcome.RefusedBeforeAnyChange);
    }

    [Theory]
    [InlineData("creation")]
    [InlineData("open")]
    [InlineData("import")]
    public async Task ServicePreparationFailureAfterInitialLookupRefusesAuditsAndNotifiesBeforeAnyWrite(string failure)
    {
        using var fixture = new Fixture();
        fixture.Service.PreparationFailure = failure;
        var processor = fixture.Processor(fixture.Service);
        var job = Job();

        Assert.Equal(BulkJobRowStatus.Success, (await processor.ProcessRowAsync(job, 0, CancellationToken.None)).Status);
        var row = await processor.ProcessRowAsync(job, 1, CancellationToken.None);
        await processor.OnJobCompletedAsync(job);

        Assert.Equal(BulkJobRowStatus.Failed, row.Status);
        var reason = Assert.IsType<string>(row.Message);
        const string preparationReason = "Directory write preparation failed before any change:";
        Assert.Contains(preparationReason, reason, StringComparison.Ordinal);
        Assert.True(reason.Length > reason.IndexOf(preparationReason, StringComparison.Ordinal) + preparationReason.Length);
        if (failure == "creation") Assert.Contains("Runspace creation unavailable.", reason, StringComparison.Ordinal);
        if (failure == "import") Assert.Contains("Directory import unavailable.", reason, StringComparison.Ordinal);
        Assert.Equal(1, fixture.Service.TargetLookups);
        Assert.Equal(1, fixture.Service.ReplaceRunspaces);
        Assert.Empty(fixture.Service.Writes);
        fixture.AssertAudit(row.Message, Comms10kOutcome.RefusedBeforeAnyChange);
        fixture.AssertNotification(row.Message, Comms10kOutcome.RefusedBeforeAnyChange);
    }

    [Fact]
    public async Task ReturnedServiceRefusalKeepsTheSpecificReasonInAuditAndCompletion()
    {
        using var fixture = new Fixture();
        var service = fixture.ResolvingSubstitute();
        const string reason = "The target group could not be re-read immediately before the write: lookup unavailable.";
        service.ExecuteReplaceAsync(Arg.Any<IReadOnlyList<Comms10kAddressResolver.ResolvedMember>>(), Arg.Any<string>(), Arg.Any<Func<string?>>()).Returns(new Comms10kUpdateResult
        {
            Outcome = Comms10kOutcome.RefusedBeforeAnyChange,
            Message = reason,
        });
        var processor = fixture.Processor(service);
        var job = Job();

        await processor.ProcessRowAsync(job, 0, CancellationToken.None);
        var row = await processor.ProcessRowAsync(job, 1, CancellationToken.None);
        await processor.OnJobCompletedAsync(job);

        Assert.Equal(BulkJobRowStatus.Failed, row.Status);
        fixture.AssertAudit(reason, Comms10kOutcome.RefusedBeforeAnyChange);
        fixture.AssertNotification(reason, Comms10kOutcome.RefusedBeforeAnyChange);
    }

    [Fact]
    public async Task UnexpectedReplaceExceptionRetainsAnUnknownWriteStateAndConcreteReason()
    {
        using var fixture = new Fixture();
        var service = fixture.ResolvingSubstitute();
        service.ExecuteReplaceAsync(Arg.Any<IReadOnlyList<Comms10kAddressResolver.ResolvedMember>>(), Arg.Any<string>(), Arg.Any<Func<string?>>()).Returns(
            Task.FromException<Comms10kUpdateResult>(new InvalidOperationException("Transport lost after dispatch.")));
        var processor = fixture.Processor(service);
        var job = Job();
        const string reason = "The replace failed and its write state could not be confirmed: Transport lost after dispatch.";

        await processor.ProcessRowAsync(job, 0, CancellationToken.None);
        var row = await processor.ProcessRowAsync(job, 1, CancellationToken.None);
        await processor.OnJobCompletedAsync(job);

        Assert.Equal(BulkJobRowStatus.Partial, row.Status);
        Assert.Equal(reason, row.Message);
        fixture.AssertAudit(reason, Comms10kOutcome.CouldNotConfirm);
        fixture.AssertNotification(reason, Comms10kOutcome.CouldNotConfirm);
    }

    [Fact]
    public async Task CompleteResolveCallsReplaceWithTheApprovedMembersAndReportsItsSuccess()
    {
        using var fixture = new Fixture();
        var service = fixture.ResolvingSubstitute();
        service.ExecuteReplaceAsync(Arg.Any<IReadOnlyList<Comms10kAddressResolver.ResolvedMember>>(), Arg.Any<string>(), Arg.Any<Func<string?>>()).Returns(new Comms10kUpdateResult
        {
            Success = true,
            Outcome = Comms10kOutcome.Succeeded,
            Message = "Replaced and verified.",
            MembersWritten = 1,
            FinalCount = 1,
        });
        var processor = fixture.Processor(service);
        var job = Job();

        Assert.Equal(BulkJobRowStatus.Success, (await processor.ProcessRowAsync(job, 0, CancellationToken.None)).Status);
        var row = await processor.ProcessRowAsync(job, 1, CancellationToken.None);
        await processor.OnJobCompletedAsync(job);

        Assert.Equal(BulkJobRowStatus.Success, row.Status);
        await service.Received(1).ExecuteReplaceAsync(Arg.Is<IReadOnlyList<Comms10kAddressResolver.ResolvedMember>>(members => members.SequenceEqual(CompleteResolution().ResolvedMembers)), job.SubmittedBy, Arg.Any<Func<string?>>());
        fixture.AssertAudit(null, Comms10kOutcome.Succeeded, success: true);
        fixture.AssertNotification("Replaced and verified.", Comms10kOutcome.Succeeded, success: true);
    }

    [Theory]
    [InlineData("unvalidated")]
    [InlineData("failed")]
    [InlineData("resolver-rejection")]
    [InlineData("parse-rejection")]
    [InlineData("empty")]
    public async Task PageRefusesDirectReplaceForEveryIncompleteFileState(string state)
    {
        using var fixture = new Fixture();
        var page = fixture.Page();
        var resolved = CompleteResolution();
        if (state == "failed") resolved.Success = false;
        if (state == "resolver-rejection") resolved.Rejections.Add(new("ghost@example.test", "No match"));
        if (state == "empty") resolved = new Comms10kResolveResult { Success = true };
        SetField(page, "resolveResult", state == "unvalidated" ? null : resolved);
        if (state == "parse-rejection")
            SetField(page, "rejectedBeforeLookup", new List<Comms10kAddressResolver.Rejection> { new("bad row", "Not an address") });
        const string reason = "The file must resolve completely with no rejected rows before Replace. Fix the file and re-upload.";

        Assert.False(CanReplace(page));
        await InvokeReplace(page);

        var result = (Comms10kUpdateResult)typeof(Page).GetField("result", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
        Assert.False(result.Success);
        Assert.Equal(reason, result.Message);
        fixture.AssertAudit(reason, Comms10kOutcome.RefusedBeforeAnyChange);
        fixture.AssertNotification(reason, Comms10kOutcome.RefusedBeforeAnyChange);
    }

    [Fact]
    public async Task PageAuthorizationRefusalHasOneAuditAndConcreteNotification()
    {
        using var fixture = new Fixture();
        var page = fixture.Page(authorized: false);
        SetField(page, "resolveResult", CompleteResolution());
        SetField(page, "confirmation", new Comms10kConfirmation([Address], "members.csv", "INC123", CompleteResolution().ResolvedMembers));

        Assert.True(CanReplace(page));
        await InvokeReplace(page);

        fixture.AssertAudit("Authorization denied.", Comms10kOutcome.RefusedBeforeAnyChange);
        fixture.AssertNotification("Authorization denied.", Comms10kOutcome.RefusedBeforeAnyChange);
    }

    [Fact]
    public void PageOffersBothReplaceButtonsOnlyThroughTheCompleteFileGuard()
    {
        using var fixture = new Fixture();
        var page = fixture.Page();
        SetField(page, "resolveResult", CompleteResolution());
        SetField(page, "confirmation", new Comms10kConfirmation([Address], "members.csv", "INC123", CompleteResolution().ResolvedMembers));

        Assert.True(CanReplace(page));
        // Markup wiring only; the compiled property and direct handler are exercised above.
        var source = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "Comms10k.razor"));
        Assert.Contains("@if (CanReplace && confirmPending)", source, StringComparison.Ordinal);
        Assert.Contains("else if (CanReplace)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("address(es) will be excluded", source, StringComparison.Ordinal);
    }

    private static Comms10kAddressResolver.Candidate Candidate(Guid? guid = null, string dn = PersonDn, string address = Address) =>
        new(guid ?? PersonGuid, dn, address, address);

    private static Comms10kResolveResult CompleteResolution() => new()
    {
        Success = true,
        ResolvedMembers = [new(PersonGuid, PersonDn)],
    };

    private static BulkJob Job() => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        ModuleId = "Comms10k",
        JobType = Comms10kReplaceJobPayload.JobType,
        SubmittedBy = "EXAMPLE\\operator",
        SubmittedIp = "192.0.2.7",
        SubmittedAtUtc = DateTime.UtcNow,
        Ticket = "INC123",
        AuthSnapshotJson = C2Snapshot(),
        PayloadJson = JsonSerializer.Serialize(new Comms10kReplaceJobPayload { Emails = [Address], SourceFileName = "members.csv", ApprovedMemberFingerprint = Comms10kMemberSet.Fingerprint([PersonGuid]) }),
    };

    private static void SetField(object target, string name, object? value) =>
        target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);

    private static bool CanReplace(Page page) =>
        (bool)typeof(Page).GetProperty("CanReplace", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!;

    private static Task InvokeReplace(Page page) =>
        (Task)typeof(Page).GetMethod("ExecuteReplace", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, null)!;

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "comms-c3-" + Guid.NewGuid().ToString("N"));
        private readonly JsonlLogService _log;
        private readonly AuditService _audit;
        private readonly EmailService _email;
        private readonly HttpClient _http;
        private readonly List<(string Actor, bool Success, IReadOnlyDictionary<string, string> Details, string? Error)> _notifications = [];
        public IConfiguration Config { get; }
        public TestService Service { get; }
        public SectionAccessService Sections { get; }
        public ModuleEnablementService Enablement { get; }
        public ExchangeAdminWeb.Services.Storage.SectionAccessRepository SectionRepository { get; }
        public ExchangeAdminWeb.Services.Storage.ModuleEnablementRepository EnablementRepository { get; }
        public Func<Task>? BeforeCredentialResponse { get; set; }
        public int C5NotificationCount => _notifications.Count;
        public IReadOnlyList<JsonElement> C5ControlAudits() => _log.GetAuditLogPaths(DateTime.Now)
            .Where(File.Exists).SelectMany(File.ReadAllLines).Select(line =>
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.Clone();
            }).Where(e => e.GetProperty("action").GetString() is "Comms10k_CancelJob" or "Comms10k_DeleteJob").ToArray();

        public (BulkJobService Queue, BulkJobRepository Repository) JobQueue()
        {
            var factory = new ExchangeAdminWeb.Services.Storage.SqliteConnectionFactory(
                Path.Combine(_root, "exchangeadmin-jobs.db"));
            new JobStoreMigrator(factory).Migrate();
            var repository = new BulkJobRepository(factory);
            var queue = new BulkJobService(null!, repository, new BulkJobProcessorRegistry([]), Config,
                NullLogger<BulkJobService>.Instance);
            // Test the real persistence boundary without dispatching a background worker.
            SetField(queue, "_pumpRunning", true);
            return (queue, repository);
        }

        public Fixture()
        {
            Directory.CreateDirectory(_root);
            Config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Audit:LogRoot"] = _root,
                ["Delinea:SecretServerUrl"] = "https://credentials.invalid",
                ["Delinea:CredentialTarget"] = "Comms-C3-tests-" + Guid.NewGuid().ToString("N"),
            }).Build();
            var env = Substitute.For<IWebHostEnvironment>();
            env.ContentRootPath.Returns(_root);
            var moduleConfig = new ModuleConfigService(new ModuleCatalog(), env, TestConfigStore.CreateModuleConfig(_root), NullLogger<ModuleConfigService>.Instance);
            moduleConfig.SaveModuleConfig("Comms10k", new Dictionary<string, string> { ["DelineaSecretId"] = "1" });
            SectionRepository = new(TestConfigStore.Create(_root));
            SectionRepository.SaveAll(new Dictionary<string, string[]> { ["Comms10k"] = [C5GroupSid] });
            EnablementRepository = TestConfigStore.CreateModuleEnablement(_root);
            EnablementRepository.SaveAll(new Dictionary<string, bool> { ["Comms10k"] = true });
            Sections = new SectionAccessService(Config, NullLogger<SectionAccessService>.Instance, env, new ModuleCatalog(), SectionRepository);
            Enablement = new ModuleEnablementService(new ModuleCatalog(), env, moduleConfig, EnablementRepository, Config, NullLogger<ModuleEnablementService>.Instance);
            _log = new JsonlLogService(Config, NullLogger<JsonlLogService>.Instance);
            var trace = new OperationTraceService(Config, _log);
            _audit = new AuditService(_log, trace);
            _email = Substitute.For<EmailService>(Config, NullLogger<EmailService>.Instance);
            _email.SendAdminNotificationAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<string?>()).Returns(call =>
                {
                    _notifications.Add((call.ArgAt<string>(0), call.ArgAt<bool>(3), call.ArgAt<IReadOnlyDictionary<string, string>>(5), call.ArgAt<string?>(6)));
                    return Task.CompletedTask;
                });
            _http = new HttpClient(new CredentialHandler(async request =>
            {
                if (!request.RequestUri!.AbsolutePath.EndsWith("/oauth2/token", StringComparison.Ordinal)
                    && BeforeCredentialResponse is { } beforeResponse)
                    await beforeResponse();
            }));
            var factory = Substitute.For<IHttpClientFactory>();
            factory.CreateClient(Arg.Any<string>()).Returns(_http);
            var delinea = new DelineaService(factory, Config, NullLogger<DelineaService>.Instance,
                new ExtendedLogService(Config, env, TestConfigStore.CreateAppSettings(_root), NullLogger<ExtendedLogService>.Instance), trace);
            // Only these dummy fields reach the local HTTP handler. No vault or AD is contacted.
            SetField(delinea, "_apiUsername", "test-api");
            SetField(delinea, "_apiKey", "test-key");
            var credentials = new ModuleCredentialService(moduleConfig, delinea, NullLogger<ModuleCredentialService>.Instance);
            Service = new TestService(moduleConfig, credentials, Config);
        }

        public Comms10kReplaceProcessor Processor(Comms10kService service) =>
            new(service, _audit, _email, Sections, Enablement, NullLogger<Comms10kReplaceProcessor>.Instance);

        public void C2BreakStore(bool enablement)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(_root, "exchangeadmin.db"),
                Pooling = false,
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = enablement ? "DROP TABLE module_enablement;" : "DROP TABLE section_access;";
            command.ExecuteNonQuery();
        }

        public Comms10kService ResolvingSubstitute()
        {
            var service = Substitute.For<Comms10kService>(NullLogger<Comms10kService>.Instance, null!, null!, Config);
            service.ResolveEmailsAsync(Arg.Any<List<string>>()).Returns(CompleteResolution());
            service.ExecuteReplaceAsync(Arg.Any<IReadOnlyList<Comms10kAddressResolver.ResolvedMember>>(), Arg.Any<string>(), Arg.Any<Func<string?>>())
                .Returns(new Comms10kUpdateResult { Success = true, Outcome = Comms10kOutcome.Succeeded });
            return service;
        }

        public Page Page(bool authorized = true)
        {
            var page = new Page();
            var authorization = Substitute.For<IAuthorizationService>();
            authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
                .Returns(authorized ? AuthorizationResult.Success() : AuthorizationResult.Failed());
            var authState = Substitute.For<AuthenticationStateProvider>();
            authState.GetAuthenticationStateAsync().Returns(new AuthenticationState(new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.Name, "EXAMPLE\\operator"), new Claim(ClaimTypes.GroupSid, C5GroupSid)], "test"))));
            foreach (var (name, value) in new (string, object)[]
            {
                ("Audit", _audit), ("Email", _email), ("AuthorizationService", authorization),
                ("AuthStateProvider", authState), ("Logger", NullLogger<Page>.Instance),
                ("SectionAccess", Sections), ("Enablement", Enablement),
            })
                typeof(Page).GetProperty(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, value);
            SetField(page, "currentUser", "EXAMPLE\\operator");
            SetField(page, "clientIpAddress", "192.0.2.7");
            SetField(page, "validatedTicket", "INC123");
            SetField(page, "uploadedFileName", "members.csv");
            return page;
        }

        public void AssertAudit(string? reason, Comms10kOutcome outcome, bool success = false, string actor = "operator")
        {
            var events = _log.GetAuditLogPaths(DateTime.Now).Where(File.Exists).SelectMany(File.ReadAllLines)
                .Select(line => JsonDocument.Parse(line)).ToList();
            try
            {
                var evt = Assert.Single(events, d => d.RootElement.GetProperty("action").GetString() == "Comms10k_Replace").RootElement;
                Assert.Equal(success ? "Success" : "Failed", evt.GetProperty("result").GetString());
                Assert.Equal(outcome.ToString(), evt.GetProperty("Outcome").GetString());
                if (!success) Assert.Equal(reason, evt.GetProperty("error").GetString());
                Assert.Equal(actor, evt.GetProperty("user").GetString());
                Assert.Equal("192.0.2.7", evt.GetProperty("ip").GetString());
                Assert.Equal("INC123", evt.GetProperty("ticket").GetString());
                Assert.Equal("members.csv", evt.GetProperty("target").GetString());
            }
            finally { foreach (var evt in events) evt.Dispose(); }
        }

        public void AssertNotification(string reason, Comms10kOutcome outcome, bool success = false, string? actor = null)
        {
            var notification = Assert.Single(_notifications);
            if (actor is not null) Assert.Equal(actor, notification.Actor);
            Assert.Equal(success, notification.Success);
            Assert.Equal(outcome.ToString(), notification.Details["Outcome"]);
            Assert.Equal(reason, notification.Details["Detail"]);
            Assert.Equal(success ? null : reason, notification.Error);
        }

        public void Dispose()
        {
            _http.Dispose();
            foreach (var name in new[] { "exchangeadmin.db", "exchangeadmin-jobs.db" })
            {
                using var pooled = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = Path.Combine(_root, name),
                    Mode = SqliteOpenMode.ReadWriteCreate,
                }.ToString());
                SqliteConnection.ClearPool(pooled);
            }
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class TestService(ModuleConfigService config, ModuleCredentialService credentials, IConfiguration settings)
        : Comms10kService(NullLogger<Comms10kService>.Instance, config, credentials, settings)
    {
        public IReadOnlyList<Comms10kAddressResolver.Candidate> Candidates { get; set; } = [Candidate()];
        public string? PreparationFailure { get; set; }
        public InitialSessionState? ReplaceState { get; set; }
        public int TargetLookups { get; private set; }
        public int ReplaceRunspaces { get; private set; }
        public Action? BeforeTargetLookup { get; set; }
        public Action? BeforeRunspaceCreation { get; set; }
        public Comms10kUpdateResult? LastResult { get; private set; }
        public List<string> Writes { get; } = [];
        public List<IReadOnlyList<string>> AddressQueries { get; } = [];
        public int MemberReads { get; private set; }

        public override async Task<Comms10kUpdateResult> ExecuteReplaceAsync(
            IReadOnlyList<Comms10kAddressResolver.ResolvedMember> resolvedMembers, string performedBy,
            Func<string?> authorizationDenial) =>
            LastResult = await base.ExecuteReplaceAsync(resolvedMembers, performedBy, authorizationDenial);

        internal override IReadOnlyList<Comms10kAddressResolver.Candidate> QueryBatchCandidates(
            (string username, string password, string domain) creds, IReadOnlyList<string> addresses)
        {
            AddressQueries.Add(addresses.ToArray());
            return Candidates;
        }

        internal override IReadOnlyList<string> QueryMemberDns((string username, string password, string domain) creds)
        {
            MemberReads++;
            return [PersonDn];
        }

        internal override IReadOnlyList<Comms10kMemberResolver.Detail> QueryMemberDetails(
            (string username, string password, string domain) creds, IReadOnlyList<string> distinguishedNames) =>
            [new(PersonDn, "person", Address, "Person")];

        internal override Comms10kTarget QueryTargetGroup((string username, string password, string domain) creds)
        {
            TargetLookups++;
            BeforeTargetLookup?.Invoke();
            return new("CN=Broadcast,DC=example,DC=test", TargetGroupGuid);
        }

        internal override Runspace CreateReplaceRunspace()
        {
            ReplaceRunspaces++;
            BeforeRunspaceCreation?.Invoke();
            if (ReplaceState is not null) return RunspaceFactory.CreateRunspace(ReplaceState);
            if (PreparationFailure == "creation") throw new InvalidOperationException("Runspace creation unavailable.");
            var state = InitialSessionState.CreateDefault2();
            state.Commands.Add(new SessionStateFunctionEntry("Import-Module", "[CmdletBinding()] param($Name) throw 'Directory import unavailable.'"));
            state.Commands.Add(new SessionStateFunctionEntry("Set-ADGroup", "[CmdletBinding()] param($Identity, $Clear, $Add, $Credential) $global:C3Writes.Add('write')"));
            state.Variables.Add(new SessionStateVariableEntry("C3Writes", Writes, "local mutation recorder"));
            var runspace = RunspaceFactory.CreateRunspace(state);
            if (PreparationFailure == "open") runspace.Dispose();
            return runspace;
        }
    }

    private sealed class CredentialHandler(Func<HttpRequestMessage, Task>? beforeResponse = null) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("credentials.invalid", request.RequestUri!.Host);
            if (beforeResponse is not null) await beforeResponse(request);
            var body = request.RequestUri.AbsolutePath.EndsWith("/oauth2/token", StringComparison.Ordinal)
                ? """{"access_token":"test-token","expires_in":3600}"""
                : """{"items":[{"fieldName":"Username","itemValue":"test-user"},{"fieldName":"Password","itemValue":"test-password"},{"fieldName":"Domain","itemValue":"EXAMPLE"}]}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }
}
