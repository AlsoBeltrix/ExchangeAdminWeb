using System.Collections.Concurrent;
using System.Management.Automation.Runspaces;
using System.Net;
using System.Reflection;
using System.Text.Json;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Jobs;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

public sealed class Comms10kInterruptionTests
{
    private const int Requested = 10_000;
    private const string WrittenLabel = "Members written (acknowledged adds)";

    [Theory]
    [InlineData("Queued", 0)]
    [InlineData("Running", 0)]
    [InlineData("Running", 1)]
    [InlineData("Running", 2)]
    public async Task RestartReconciliationReportsInterruptedAndUnknownEvenWithPersistedStages(string status, int recordedStages)
    {
        using var fixture = new Fixture();
        var job = fixture.Job();
        fixture.Repository.Insert(job);
        if (status == "Running")
            Assert.True(fixture.Repository.TryStart(job.Id, 2, DateTime.UtcNow));
        for (var stage = 0; stage < recordedStages; stage++)
        {
            fixture.Repository.RecordRow(new BulkJobRow
            {
                JobId = job.Id,
                RowIndex = stage,
                Target = "members.csv",
                Status = BulkJobRowStatus.Success,
                Message = stage == 0 ? "10,000 of 10,000 matched in AD" : "Successfully updated broadcast: 10000 members.",
                RecordedAtUtc = DateTime.UtcNow,
            }, DateTime.UtcNow);
        }

        // The production startup entry point reloads the job and resolves a fresh processor.
        // A persisted success message cannot reconstruct which writes were acknowledged.
        fixture.Runner.InitializeAsync();
        var notification = await fixture.Notification.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        var interrupted = Assert.IsType<BulkJob>(fixture.Repository.Get(job.Id));
        Assert.Equal(BulkJobStatus.Interrupted, interrupted.Status);
        Assert.Equal(recordedStages, interrupted.ProcessedRows);
        Assert.Equal("Interrupted by application restart.", interrupted.Message);
        Assert.Single(fixture.Processors);
        Assert.Empty(fixture.Directory.Calls);
        Assert.Equal(0, fixture.Service.Resolutions);
        Assert.False(notification.Success);
        Assert.Equal("Interrupted", notification.Details["Outcome"]);
        Assert.Contains(interrupted.Message!, notification.Details["Detail"], StringComparison.Ordinal);
        Assert.Contains("empty or partial", notification.Details["Detail"], StringComparison.Ordinal);
        Assert.Equal(notification.Details["Detail"], notification.Error);
        Assert.StartsWith("UNKNOWN", notification.Details[WrittenLabel], StringComparison.Ordinal);
        Assert.StartsWith("UNKNOWN", notification.Details["Members requested"], StringComparison.Ordinal);
        Assert.StartsWith("UNKNOWN", notification.Details["Final membership"], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("first-add-fails", 0, null, Comms10kOutcome.PartlyApplied)]
    [InlineData("second-add-fails", 2000, null, Comms10kOutcome.PartlyApplied)]
    [InlineData("clear-fails", 3, null, Comms10kOutcome.PartlyApplied)]
    [InlineData("last-add-applies-then-throws", Requested, null, Comms10kOutcome.Succeeded)]
    [InlineData("success", Requested, Requested, Comms10kOutcome.Succeeded)]
    [InlineData("readback-fails", null, Requested, Comms10kOutcome.CouldNotConfirm)]
    [InlineData("add-and-readback-fail", null, null, Comms10kOutcome.CouldNotConfirm)]
    [InlineData("concurrent-removal", Requested - 1, Requested, Comms10kOutcome.PartlyApplied)]
    public async Task ActualWriteSequenceKeepsRequestedAcknowledgedAndObservedCountsDistinct(
        string mode, int? finalCount, int? written, Comms10kOutcome outcome)
    {
        using var fixture = new Fixture(mode);
        var job = fixture.Job();
        fixture.Repository.Insert(job);

        await fixture.Runner.DrainQueueAsync(TestContext.Current.CancellationToken);

        var result = Assert.IsType<Comms10kUpdateResult>(fixture.Service.LastResult);
        Assert.Equal(Requested, result.MembersRequested);
        Assert.Equal(written, result.MembersWritten);
        Assert.Equal(finalCount, result.FinalCount);
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(outcome == Comms10kOutcome.Succeeded, result.Success);
        Assert.Single(fixture.Directory.Calls, call => call == "clear");
        Assert.All(fixture.Directory.BatchSizes, size => Assert.Equal(2000, size));
        if (mode is "first-add-fails" or "add-and-readback-fail")
            Assert.Single(fixture.Directory.BatchSizes);
        if (mode == "second-add-fails")
            Assert.Equal(2, fixture.Directory.BatchSizes.Count);
        if (mode is "first-add-fails" or "second-add-fails" or "clear-fails" or "last-add-applies-then-throws" or "add-and-readback-fail")
            Assert.Contains("Write acknowledgement unavailable.", result.Message, StringComparison.Ordinal);
        if (outcome == Comms10kOutcome.PartlyApplied)
            Assert.Contains("Re-run the same CSV", result.Message, StringComparison.Ordinal);
        if (finalCount is null)
            Assert.Contains("Read-back unavailable.", result.Message, StringComparison.Ordinal);

        fixture.AssertRecordedCounts(job, result, written, finalCount);
        var log = Assert.Single(fixture.ServiceLog.Entries, entry => entry.Values.ContainsKey("Written"));
        Assert.Equal(Requested.ToString(), log.Values["Requested"]);
        Assert.Equal(written?.ToString() ?? "unknown", log.Values["Written"]);
        Assert.Equal(finalCount?.ToString() ?? "unknown", log.Values["Final"]);
        Assert.Contains("written (acknowledged adds)", log.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("preparation-refusal")]
    [InlineData("primary-refusal")]
    public async Task RefusalBeforeDispatchProvesZeroWrittenAndPreservesTheReason(string mode)
    {
        using var fixture = new Fixture(mode);
        var job = fixture.Job();
        fixture.Repository.Insert(job);

        await fixture.Runner.DrainQueueAsync(TestContext.Current.CancellationToken);

        var result = Assert.IsType<Comms10kUpdateResult>(fixture.Service.LastResult);
        Assert.Equal(Requested, result.MembersRequested);
        Assert.Equal(0, result.MembersWritten);
        Assert.Equal(Comms10kOutcome.RefusedBeforeAnyChange, result.Outcome);
        Assert.Contains(mode == "primary-refusal" ? "primary-group members outside the approved set" : "Preparation unavailable.", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("clear", fixture.Directory.Calls);
        Assert.Empty(fixture.Directory.BatchSizes);
        fixture.AssertRecordedCounts(job, result, 0, null);
    }

    [Fact]
    public async Task RefusalBeforeResolutionHasZeroWrittenAndUnknownRequestedMembership()
    {
        using var fixture = new Fixture();
        var job = fixture.Job(hasApproval: false);
        fixture.Repository.Insert(job);

        await fixture.Runner.DrainQueueAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, fixture.Service.Resolutions);
        Assert.Empty(fixture.Directory.Calls);
        var audit = fixture.MutationAudit();
        Assert.Equal(Comms10kOutcome.RefusedBeforeAnyChange.ToString(), audit.GetProperty("Outcome").GetString());
        Assert.Equal(JsonValueKind.Number, audit.GetProperty("MembersWritten").ValueKind);
        Assert.Equal(0, audit.GetProperty("MembersWritten").GetInt32());
        Assert.StartsWith("UNKNOWN", audit.GetProperty("MembersRequested").GetString()!, StringComparison.Ordinal);
        var notification = await fixture.Notification.Task;
        Assert.False(notification.Success);
        Assert.Equal("0", notification.Details[WrittenLabel]);
        Assert.StartsWith("UNKNOWN", notification.Details["Members requested"], StringComparison.Ordinal);
        Assert.Contains("fingerprint", notification.Details["Detail"], StringComparison.Ordinal);
        Assert.Equal(audit.GetProperty("error").GetString(), notification.Details["Detail"]);
    }

    [Fact]
    public async Task UnexpectedServiceExceptionHasUnknownWrittenCountAndOneFailedAudit()
    {
        using var fixture = new Fixture("unexpected-service-exception");
        var job = fixture.Job();
        fixture.Repository.Insert(job);

        await fixture.Runner.DrainQueueAsync(TestContext.Current.CancellationToken);

        var row = Assert.Single(fixture.Runner.GetRows(job.Id), row => row.RowIndex == 1);
        Assert.Equal(BulkJobRowStatus.Partial, row.Status);
        Assert.Contains("Transport lost after dispatch.", row.Message, StringComparison.Ordinal);
        var audit = fixture.MutationAudit();
        Assert.Equal(Comms10kOutcome.CouldNotConfirm.ToString(), audit.GetProperty("Outcome").GetString());
        Assert.Equal(Requested, audit.GetProperty("MembersRequested").GetInt32());
        Assert.Equal(JsonValueKind.String, audit.GetProperty("MembersWritten").ValueKind);
        Assert.StartsWith("UNKNOWN", audit.GetProperty("MembersWritten").GetString()!, StringComparison.Ordinal);
        Assert.Equal(row.Message, audit.GetProperty("error").GetString());
        var notification = await fixture.Notification.Task;
        Assert.False(notification.Success);
        Assert.Equal(Comms10kOutcome.CouldNotConfirm.ToString(), notification.Details["Outcome"]);
        Assert.StartsWith("UNKNOWN", notification.Details[WrittenLabel], StringComparison.Ordinal);
        Assert.StartsWith("UNKNOWN", notification.Details["Final membership"], StringComparison.Ordinal);
        Assert.Equal(row.Message, notification.Details["Detail"]);
        Assert.Equal(row.Message, notification.Error);
    }

    private sealed record Notification(bool Success, IReadOnlyDictionary<string, string> Details, string? Error);
    private sealed record DirectoryMember(Guid Guid, string Dn, string Address);

    private sealed class Fixture : IDisposable
    {
        private readonly TempDir _temp = new();
        private readonly JsonlLogService _log;
        private readonly HttpClient _http;
        private readonly ServiceProvider _provider;
        public LocalDirectory Directory { get; }
        public TestService Service { get; }
        public CaptureLogger ServiceLog { get; } = new();
        public BulkJobRepository Repository { get; }
        public BulkJobService Runner { get; }
        public List<Comms10kReplaceProcessor> Processors { get; } = [];
        public TaskCompletionSource<Notification> Notification { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Fixture(string mode = "success")
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Audit:LogRoot"] = _temp.Path,
                ["Delinea:SecretServerUrl"] = "https://credentials.invalid",
                ["Delinea:CredentialTarget"] = "Comms-C11-tests-" + Guid.NewGuid().ToString("N"),
            }).Build();
            var env = Substitute.For<IWebHostEnvironment>();
            env.ContentRootPath.Returns(_temp.Path);
            var moduleConfig = new ModuleConfigService(new ModuleCatalog(), env, TestConfigStore.CreateModuleConfig(_temp.Path), NullLogger<ModuleConfigService>.Instance);
            moduleConfig.SaveModuleConfig("Comms10k", new Dictionary<string, string> { ["DelineaSecretId"] = "1" });
            _log = new JsonlLogService(config, NullLogger<JsonlLogService>.Instance);
            var trace = new OperationTraceService(config, _log);
            var audit = new AuditService(_log, trace);
            var email = Substitute.For<EmailService>(config, NullLogger<EmailService>.Instance);
            email.SendAdminNotificationAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<string?>()).Returns(call =>
                {
                    Assert.True(Notification.TrySetResult(new Notification(call.ArgAt<bool>(3), call.ArgAt<IReadOnlyDictionary<string, string>>(5), call.ArgAt<string?>(6))));
                    return Task.CompletedTask;
                });
            _http = new HttpClient(new CredentialHandler());
            var clientFactory = Substitute.For<IHttpClientFactory>();
            clientFactory.CreateClient(Arg.Any<string>()).Returns(_http);
            var delinea = new DelineaService(clientFactory, config, NullLogger<DelineaService>.Instance,
                new ExtendedLogService(config, env, TestConfigStore.CreateAppSettings(_temp.Path), NullLogger<ExtendedLogService>.Instance), trace);
            typeof(DelineaService).GetField("_apiUsername", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(delinea, "test-api");
            typeof(DelineaService).GetField("_apiKey", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(delinea, "test-key");
            var credentials = new ModuleCredentialService(moduleConfig, delinea, NullLogger<ModuleCredentialService>.Instance);
            Directory = new LocalDirectory(mode);
            Service = new TestService(ServiceLog, moduleConfig, credentials, config, Directory);

            var factory = new SqliteConnectionFactory(Path.Combine(_temp.Path, "exchangeadmin-jobs.db"));
            new JobStoreMigrator(factory).Migrate();
            Repository = new BulkJobRepository(factory);
            var services = new ServiceCollection();
            services.AddScoped(_ =>
            {
                var processor = new Comms10kReplaceProcessor(Service, audit, email, NullLogger<Comms10kReplaceProcessor>.Instance);
                Processors.Add(processor);
                return processor;
            });
            _provider = services.BuildServiceProvider();
            var registry = new BulkJobProcessorRegistry([new(Comms10kReplaceProcessor.ModuleName, typeof(Comms10kReplaceProcessor))]);
            Runner = new BulkJobService(_provider.GetRequiredService<IServiceScopeFactory>(), Repository, registry, config, NullLogger<BulkJobService>.Instance);
        }

        public BulkJob Job(bool hasApproval = true) => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            ModuleId = Comms10kReplaceProcessor.ModuleName,
            JobType = Comms10kReplaceJobPayload.JobType,
            SubmittedBy = "EXAMPLE\\operator",
            SubmittedIp = "192.0.2.7",
            Ticket = "INC123",
            SubmittedAtUtc = DateTime.UtcNow,
            PayloadJson = JsonSerializer.Serialize(new Comms10kReplaceJobPayload
            {
                Emails = Directory.Rows.Select(row => row.Address).ToList(),
                SourceFileName = "members.csv",
                ApprovedMemberFingerprint = hasApproval ? Comms10kMemberSet.Fingerprint(Directory.Rows.Select(row => row.Guid)) : null,
            }),
        };

        public JsonElement MutationAudit()
        {
            var events = _log.GetAuditLogPaths(DateTime.Now).Where(File.Exists).SelectMany(File.ReadAllLines)
                .Select(line =>
                {
                    using var document = JsonDocument.Parse(line);
                    return document.RootElement.Clone();
                });
            var audit = Assert.Single(events, evt => evt.GetProperty("action").GetString() == "Comms10k_Replace");
            Assert.Equal("operator", audit.GetProperty("user").GetString());
            Assert.Equal("192.0.2.7", audit.GetProperty("ip").GetString());
            Assert.Equal("INC123", audit.GetProperty("ticket").GetString());
            Assert.Equal("members.csv", audit.GetProperty("target").GetString());
            return audit;
        }

        public void AssertRecordedCounts(BulkJob job, Comms10kUpdateResult result, int? written, int? finalCount)
        {
            var finished = Assert.IsType<BulkJob>(Repository.Get(job.Id));
            Assert.Equal(BulkJobStatus.Completed, finished.Status);
            var row = Assert.Single(Runner.GetRows(job.Id), row => row.RowIndex == 1);
            Assert.Equal(result.Success ? BulkJobRowStatus.Success
                : result.Outcome == Comms10kOutcome.RefusedBeforeAnyChange ? BulkJobRowStatus.Failed : BulkJobRowStatus.Partial, row.Status);
            Assert.Equal(result.Message, row.Message);
            var audit = MutationAudit();
            Assert.Equal(result.Success ? "Success" : "Failed", audit.GetProperty("result").GetString());
            Assert.Equal(result.Outcome.ToString(), audit.GetProperty("Outcome").GetString());
            Assert.Equal(Requested, audit.GetProperty("MembersRequested").GetInt32());
            if (written.HasValue)
            {
                Assert.Equal(JsonValueKind.Number, audit.GetProperty("MembersWritten").ValueKind);
                Assert.Equal(written.Value, audit.GetProperty("MembersWritten").GetInt32());
            }
            else
            {
                Assert.Equal(JsonValueKind.String, audit.GetProperty("MembersWritten").ValueKind);
                Assert.StartsWith("UNKNOWN", audit.GetProperty("MembersWritten").GetString()!, StringComparison.Ordinal);
            }
            Assert.Contains("Acknowledged adds", audit.GetProperty("WrittenCountBasis").GetString(), StringComparison.Ordinal);
            Assert.Equal(finalCount?.ToString() ?? "UNKNOWN - no confirmed read-back", audit.GetProperty("FinalCount").GetString());
            if (!result.Success) Assert.Equal(result.Message, audit.GetProperty("error").GetString());
            var notification = Notification.Task.GetAwaiter().GetResult();
            Assert.Equal(result.Success, notification.Success);
            Assert.Equal(result.Outcome.ToString(), notification.Details["Outcome"]);
            Assert.Equal(result.Message, notification.Details["Detail"]);
            Assert.Equal(result.Success ? null : result.Message, notification.Error);
            Assert.Equal(Requested.ToString(), notification.Details["Members requested"]);
            if (written.HasValue) Assert.Equal(written.Value.ToString(), notification.Details[WrittenLabel]);
            else Assert.StartsWith("UNKNOWN", notification.Details[WrittenLabel], StringComparison.Ordinal);
            if (finalCount.HasValue) Assert.Equal(finalCount.Value.ToString(), notification.Details["Final membership"]);
            else Assert.StartsWith("UNKNOWN", notification.Details["Final membership"], StringComparison.Ordinal);
        }

        public void Dispose()
        {
            _provider.Dispose();
            _http.Dispose();
            foreach (var name in new[] { "exchangeadmin.db", "exchangeadmin-jobs.db" })
            {
                using var pooled = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = Path.Combine(_temp.Path, name),
                    Mode = SqliteOpenMode.ReadWriteCreate,
                }.ToString());
                SqliteConnection.ClearPool(pooled);
            }
            _temp.Dispose();
        }
    }

    private sealed class TestService(ILogger<Comms10kService> logger, ModuleConfigService config,
        ModuleCredentialService credentials, IConfiguration settings, LocalDirectory directory)
        : Comms10kService(logger, config, credentials, settings)
    {
        public int Resolutions { get; private set; }
        public Comms10kUpdateResult? LastResult { get; private set; }

        public override async Task<Comms10kUpdateResult> ExecuteReplaceAsync(IReadOnlyList<Comms10kAddressResolver.ResolvedMember> resolvedMembers, string performedBy)
        {
            if (directory.Mode == "unexpected-service-exception") throw new InvalidOperationException("Transport lost after dispatch.");
            return LastResult = await base.ExecuteReplaceAsync(resolvedMembers, performedBy);
        }

        internal override IReadOnlyList<Comms10kAddressResolver.Candidate> QueryBatchCandidates(
            (string username, string password, string domain) creds, IReadOnlyList<string> addresses)
        {
            Resolutions++;
            return directory.Rows.Select(row => new Comms10kAddressResolver.Candidate(row.Guid, row.Dn, row.Address, row.Address)).ToArray();
        }

        internal override Comms10kTarget QueryTargetGroup((string username, string password, string domain) creds) =>
            new("CN=Broadcast,DC=example,DC=test", TargetGroupGuid);

        internal override Runspace CreateReplaceRunspace() => RunspaceFactory.CreateRunspace(directory.State);
    }

    private sealed class LocalDirectory
    {
        public string Mode { get; }
        public InitialSessionState State { get; } = InitialSessionState.CreateDefault2();
        public List<DirectoryMember> Rows { get; } = Enumerable.Range(1, Requested)
            .Select(index => new DirectoryMember(new Guid(index, 0, 0, new byte[8]), $"CN=Person{index},DC=example,DC=test", $"person{index}@example.test")).ToList();
        public List<DirectoryMember> Members { get; } = [];
        public List<string> Calls { get; } = [];
        public List<int> BatchSizes { get; } = [];
        public int AddCalls { get; private set; }

        public LocalDirectory(string mode)
        {
            Mode = mode;
            Members.AddRange(Rows.Take(3));
            State.Variables.Add(new SessionStateVariableEntry("C11Directory", this, "local directory recorder"));
            State.Variables.Add(new SessionStateVariableEntry("C11TargetGuid", Comms10kService.TargetGroupGuid, "fixed target"));
            State.Commands.Add(new SessionStateFunctionEntry("Import-Module", """
                [CmdletBinding()] param($Name)
                if ($global:C11Directory.Mode -eq 'preparation-refusal') { throw 'Preparation unavailable.' }
                """));
            State.Commands.Add(new SessionStateFunctionEntry("Get-ADGroup", """
                [CmdletBinding()] param($Identity, [string[]] $Properties, [pscredential] $Credential)
                if ([guid] $Identity -ne $global:C11TargetGuid) { throw 'Target must remain pinned.' }
                $directory = $global:C11Directory
                $directory.Calls.Add('group')
                if ($directory.Calls.Contains('clear') -and $directory.Mode -in @('readback-fails', 'add-and-readback-fail')) { throw 'Read-back unavailable.' }
                if ($directory.Calls.Contains('clear') -and $directory.Mode -eq 'concurrent-removal') { $directory.Members.RemoveAt(0) }
                [pscustomobject]@{ DistinguishedName = 'CN=Broadcast,DC=example,DC=test'; ObjectGUID = $global:C11TargetGuid; SID = 'S-1-5-21-100-200-300-4242'; member = @($directory.Members | ForEach-Object { $_.Dn }) }
                """));
            State.Commands.Add(new SessionStateFunctionEntry("Set-ADGroup", """
                [CmdletBinding()] param($Identity, [string] $Clear, [hashtable] $Add, [pscredential] $Credential)
                if ([guid] $Identity -ne $global:C11TargetGuid) { throw 'Target must remain pinned.' }
                $directory = $global:C11Directory
                if ($PSBoundParameters.ContainsKey('Clear')) {
                    $directory.Calls.Add('clear')
                    if ($directory.Mode -eq 'clear-fails') { throw 'Write acknowledgement unavailable.' }
                    $directory.Members.Clear()
                } else {
                    $directory.RecordAdd($Add['member'])
                }
                """));
            State.Commands.Add(new SessionStateFunctionEntry("Get-ADObject", """
                [CmdletBinding()] param([string] $LDAPFilter, [string[]] $Properties, [pscredential] $Credential)
                $directory = $global:C11Directory
                if ($LDAPFilter -like '(primaryGroupID=*') {
                    if ($directory.Mode -eq 'primary-refusal') { [pscustomobject]@{ ObjectGUID = [guid] 'c32945dd-d6ca-4d8f-9683-64d51325338a' } }
                    return
                }
                foreach ($member in $directory.Members) {
                    if ($LDAPFilter.Contains('(distinguishedName=' + $member.Dn + ')')) {
                        [pscustomobject]@{ DistinguishedName = $member.Dn; ObjectGUID = $member.Guid }
                    }
                }
                """));
        }

        public void RecordAdd(string[] values)
        {
            Calls.Add("add");
            BatchSizes.Add(values.Length);
            AddCalls++;
            if ((Mode is "first-add-fails" or "add-and-readback-fail" && AddCalls == 1)
                || (Mode == "second-add-fails" && AddCalls == 2))
                throw new InvalidOperationException("Write acknowledgement unavailable.");
            foreach (var value in values)
            {
                Assert.StartsWith("<GUID=", value, StringComparison.Ordinal);
                var guid = Guid.Parse(value[6..^1]);
                Members.Add(Rows.Single(row => row.Guid == guid));
            }
            if (Mode == "last-add-applies-then-throws" && AddCalls == Requested / 2000)
                throw new InvalidOperationException("Write acknowledgement unavailable.");
        }
    }

    private sealed record LogEntry(string Message, IReadOnlyDictionary<string, object?> Values);
    private sealed class CaptureLogger : ILogger<Comms10kService>
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue(new LogEntry(formatter(state, exception), ((IEnumerable<KeyValuePair<string, object?>>)(object)state!).ToDictionary()));
    }

    private sealed class CredentialHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("credentials.invalid", request.RequestUri!.Host);
            var body = request.RequestUri.AbsolutePath.EndsWith("/oauth2/token", StringComparison.Ordinal)
                ? """{"access_token":"test-token","expires_in":3600}"""
                : """{"items":[{"fieldName":"Username","itemValue":"test-user"},{"fieldName":"Password","itemValue":"test-password"},{"fieldName":"Domain","itemValue":"EXAMPLE"}]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
