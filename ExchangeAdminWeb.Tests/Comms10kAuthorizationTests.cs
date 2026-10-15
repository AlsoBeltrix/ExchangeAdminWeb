using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Security;
using System.Security.Claims;
using System.Text.Json;
using ExchangeAdminWeb.Authorization;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Jobs;
using ExchangeAdminWeb.Services.Progress;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using AdException = Microsoft.ActiveDirectory.Management.ADException;
using Page = ExchangeAdminWeb.Components.Pages.Comms10k;

namespace ExchangeAdminWeb.Tests;

public sealed partial class Comms10kRefusalTests
{
    private const string C2OtherSid = "S-1-5-21-100-200-300-5555";
    private const string C2SnapshotDenied = "The Comms-10k job authorization snapshot is missing or unreadable.";
    private const string C2SectionDenied = "The job authorization snapshot is not for Comms10k.";
    private const string C2GroupDenied = "The group that authorized this Comms-10k job is no longer allowed or section access is unavailable.";
    private const string C2Disabled = "Comms-10k is currently disabled.";

    [Theory]
    [InlineData("missing", 0)]
    [InlineData("malformed", 0)]
    [InlineData("wrong-section", 0)]
    [InlineData("wrong-case", 0)]
    [InlineData("no-grant", 0)]
    [InlineData("null-grant", 0)]
    [InlineData("removed", 0)]
    [InlineData("replacement-claim", 0)]
    [InlineData("unusable", 0)]
    [InlineData("disabled", 0)]
    [InlineData("unreadable-sections", 0)]
    [InlineData("unreadable-enablement", 0)]
    [InlineData("missing", 1)]
    [InlineData("wrong-section", 1)]
    [InlineData("removed", 1)]
    [InlineData("replacement-claim", 1)]
    [InlineData("disabled", 1)]
    public async Task C2EveryStageRequiresTheOriginalExactSectionGrantAndEnabledModule(string mode, int stage)
    {
        using var fixture = new Fixture();
        var service = fixture.ResolvingSubstitute();
        var processor = fixture.Processor(service);
        if (stage == 1)
            Assert.Equal(BulkJobRowStatus.Success, (await processor.ProcessRowAsync(Job(), 0, CancellationToken.None)).Status);

        var snapshot = mode switch
        {
            "missing" => null,
            "malformed" => "{not-json",
            "wrong-section" => C2Snapshot(section: "ConferenceRooms"),
            "wrong-case" => C2Snapshot(section: "comms10k"),
            "no-grant" => C2Snapshot(grants: []),
            "null-grant" => "{\"Section\":\"Comms10k\",\"RoleClaims\":[],\"AuthorizedGroups\":null}",
            "replacement-claim" => C2Snapshot(claims: [C5GroupSid, C2OtherSid]),
            "unusable" => C2Snapshot(grants: ["EXAMPLE\\Operators"]),
            _ => C2Snapshot(),
        };
        if (mode is "removed" or "replacement-claim") C2Groups(fixture, [C2OtherSid]);
        if (mode == "unusable") C2Groups(fixture, ["EXAMPLE\\Operators"]);
        if (mode == "disabled") C2Revoke(fixture, true);
        if (mode.StartsWith("unreadable-", StringComparison.Ordinal)) fixture.C2BreakStore(mode == "unreadable-enablement");
        var job = C2Job(snapshot);

        var refused = await processor.ProcessRowAsync(job, stage, CancellationToken.None);
        var continued = stage == 0 ? await processor.ProcessRowAsync(job, 1, CancellationToken.None) : refused;
        await processor.OnJobCompletedAsync(job);

        var reason = mode is "missing" or "malformed" ? C2SnapshotDenied
            : mode is "wrong-section" or "wrong-case" ? C2SectionDenied
            : mode is "disabled" or "unreadable-enablement" ? C2Disabled : C2GroupDenied;
        Assert.Equal(BulkJobRowStatus.Failed, refused.Status);
        Assert.Equal(reason, refused.Message);
        Assert.Equal(reason, continued.Message);
        await service.DidNotReceive().ExecuteReplaceAsync(Arg.Any<IReadOnlyList<Comms10kAddressResolver.ResolvedMember>>(), Arg.Any<string>(), Arg.Any<Func<string?>>());
        if (stage == 0) await service.DidNotReceive().ResolveEmailsAsync(Arg.Any<List<string>>());
        fixture.AssertAudit(reason, Comms10kOutcome.RefusedBeforeAnyChange);
        fixture.AssertNotification(reason, Comms10kOutcome.RefusedBeforeAnyChange);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task C2AnOriginalSurvivingGrantStillAllowsTheFrozenApprovedPeople(bool secondOriginalSurvives)
    {
        using var fixture = new Fixture();
        var directory = new C2Directory();
        fixture.Service.ReplaceState = directory.State;
        if (secondOriginalSurvives) C2Groups(fixture, [C2OtherSid]);
        var job = C2Job(C2Snapshot(grants: secondOriginalSurvives ? [C5GroupSid, C2OtherSid] : [C5GroupSid], claims: []));
        var processor = fixture.Processor(fixture.Service);

        Assert.Equal(BulkJobRowStatus.Success, (await processor.ProcessRowAsync(job, 0, CancellationToken.None)).Status);
        var row = await processor.ProcessRowAsync(job, 1, CancellationToken.None);
        await processor.OnJobCompletedAsync(job);

        Assert.Equal(BulkJobRowStatus.Success, row.Status);
        Assert.Equal([PersonGuid], directory.Actual);
        Assert.Equal(["clear", "add"], directory.Writes);
        fixture.AssertAudit(null, Comms10kOutcome.Succeeded, true);
        fixture.AssertNotification(row.Message!, Comms10kOutcome.Succeeded, true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task C2RevocationDuringAwaitedCredentialsRefusesBeforeActualDispatch(bool disableModule)
    {
        using var fixture = new Fixture();
        var directory = new C2Directory();
        fixture.Service.ReplaceState = directory.State;
        var job = Job();
        var processor = fixture.Processor(fixture.Service);
        await processor.ProcessRowAsync(job, 0, CancellationToken.None);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.BeforeCredentialResponse = async () => { entered.TrySetResult(); await release.Task; };
        var pending = processor.ProcessRowAsync(job, 1, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        try { C2Revoke(fixture, disableModule); }
        finally { release.TrySetResult(); }
        var row = await pending;
        await processor.OnJobCompletedAsync(job);

        C2AssertPreDispatch(fixture, directory, row, disableModule ? C2Disabled : C2GroupDenied);
    }

    [Theory]
    [InlineData("target-lookup", false)]
    [InlineData("runspace", true)]
    [InlineData("import", false)]
    [InlineData("target-reread", true)]
    [InlineData("primary", false)]
    [InlineData("primary", true)]
    public async Task C2TheFinalClearGateFollowsAllPreparation(string phase, bool disableModule)
    {
        using var fixture = new Fixture();
        var directory = new C2Directory();
        fixture.Service.ReplaceState = directory.State;
        Action revoke = () => C2Revoke(fixture, disableModule);
        if (phase == "target-lookup") fixture.Service.BeforeTargetLookup = revoke;
        else if (phase == "runspace") fixture.Service.BeforeRunspaceCreation = revoke;
        else directory.OnStep = step => { if (step == phase) revoke(); };
        var job = Job();
        var processor = fixture.Processor(fixture.Service);
        await processor.ProcessRowAsync(job, 0, CancellationToken.None);

        var row = await processor.ProcessRowAsync(job, 1, CancellationToken.None);
        await processor.OnJobCompletedAsync(job);

        C2AssertPreDispatch(fixture, directory, row, disableModule ? C2Disabled : C2GroupDenied);
        Assert.Contains("primary", directory.Calls);
    }

    [Theory]
    [InlineData("clear", false, false)]
    [InlineData("clear", true, false)]
    [InlineData("clear", false, true)]
    [InlineData("clear-retry", false, false)]
    [InlineData("between-batches", true, false)]
    [InlineData("add-retry", false, false)]
    public async Task C2AfterAnyClearAttemptRevocationStopsWritesAndReportsReadback(
        string boundary, bool disableModule, bool failReadback)
    {
        using var fixture = new Fixture();
        var directory = new C2Directory(boundary == "between-batches" ? 2001 : 1)
        {
            FailReadback = failReadback,
            FailClearOnce = boundary == "clear-retry",
            FailAddOnce = boundary == "add-retry",
        };
        fixture.Service.ReplaceState = directory.State;
        fixture.Service.Candidates = directory.Rows.Select(row => Candidate(row.Guid, row.Dn, row.Address)).ToArray();
        directory.OnStep = step =>
        {
            if ((boundary is "clear" or "clear-retry" && step == "clear")
                || (boundary is "between-batches" or "add-retry" && step == "add")) C2Revoke(fixture, disableModule);
        };
        var job = C2Job(C2Snapshot(), directory.Rows);
        var processor = fixture.Processor(fixture.Service);
        await processor.ProcessRowAsync(job, 0, CancellationToken.None);

        var row = await processor.ProcessRowAsync(job, 1, CancellationToken.None);
        await processor.OnJobCompletedAsync(job);

        var result = Assert.IsType<Comms10kUpdateResult>(fixture.Service.LastResult);
        Assert.Equal(BulkJobRowStatus.Partial, row.Status);
        Assert.Equal(failReadback ? Comms10kOutcome.CouldNotConfirm : Comms10kOutcome.PartlyApplied, result.Outcome);
        Assert.Equal(directory.Rows.Count, result.MembersRequested);
        Assert.Null(result.MembersWritten);
        Assert.Equal(failReadback ? (int?)null : boundary == "between-batches" ? 2000 : boundary == "clear-retry" ? 1 : 0, result.FinalCount);
        Assert.Equal(1, directory.Writes.Count(write => write == "clear"));
        Assert.Equal(boundary is "between-batches" or "add-retry" ? 1 : 0, directory.Writes.Count(write => write == "add"));
        Assert.Equal(1, directory.Calls.Count(call => call == "readback"));
        Assert.Contains(disableModule ? C2Disabled : C2GroupDenied, row.Message, StringComparison.Ordinal);
        fixture.AssertAudit(row.Message, result.Outcome);
        fixture.AssertNotification(row.Message!, result.Outcome);
    }

    [Fact]
    public async Task C2ReadbackCanConfirmTheApprovedPrimarySetAfterAuthorizationStopsAdds()
    {
        using var fixture = new Fixture();
        var directory = new C2Directory { Primaries = [PersonGuid] };
        fixture.Service.ReplaceState = directory.State;
        directory.OnStep = step => { if (step == "clear") C2Revoke(fixture, true); };
        var processor = fixture.Processor(fixture.Service);
        var job = Job();
        await processor.ProcessRowAsync(job, 0, CancellationToken.None);

        var row = await processor.ProcessRowAsync(job, 1, CancellationToken.None);
        await processor.OnJobCompletedAsync(job);

        var result = Assert.IsType<Comms10kUpdateResult>(fixture.Service.LastResult);
        Assert.Equal(Comms10kOutcome.Succeeded, result.Outcome);
        Assert.Equal(BulkJobRowStatus.Success, row.Status);
        Assert.Equal(1, result.FinalCount);
        Assert.Null(result.MembersWritten);
        Assert.Equal(["clear"], directory.Writes);
        Assert.Contains(C2Disabled, row.Message, StringComparison.Ordinal);
        fixture.AssertAudit(null, result.Outcome, true);
        fixture.AssertNotification(row.Message!, result.Outcome, true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void C2ADeniedQueuedMutationIsNeverInvokedOrReplayedDuringReadback(bool add)
    {
        var directory = new C2Directory();
        using var runspace = RunspaceFactory.CreateRunspace(directory.State);
        runspace.Open();
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;
        var credential = new PSCredential("EXAMPLE\\tester", new SecureString());
        var target = new Comms10kTarget("CN=Broadcast,DC=example,DC=test", Comms10kService.TargetGroupGuid);
        Action denied = () => throw new InvalidOperationException(C2Disabled);

        var error = Assert.Throws<InvalidOperationException>(() =>
        {
            if (add) Comms10kService.AddMemberBatch(ps, credential, target, [PersonGuid], denied);
            else Comms10kService.ClearMembers(ps, credential, target, denied);
        });
        Assert.Equal(C2Disabled, error.Message);
        Assert.Empty(ps.Commands.Commands);
        Assert.Empty(ps.Streams.Error);
        Comms10kService.ReadBackMembers(ps, credential, target);
        Assert.Empty(directory.Writes);
    }

    [Fact]
    public async Task C2TheRunnerStillAdvancesAfterDeniedResolveWithOneSpecificAuditAndNotification()
    {
        using var fixture = new Fixture();
        var service = fixture.ResolvingSubstitute();
        var processor = fixture.Processor(service);
        var (_, repository) = fixture.JobQueue();
        using var provider = new ServiceCollection().AddScoped(_ => processor).BuildServiceProvider();
        var runner = new BulkJobService(provider.GetRequiredService<IServiceScopeFactory>(), repository,
            new BulkJobProcessorRegistry([new("Comms10k", typeof(Comms10kReplaceProcessor))]), fixture.Config,
            NullLogger<BulkJobService>.Instance);
        var job = C2Job(null);
        repository.Insert(job);

        await runner.DrainQueueAsync(TestContext.Current.CancellationToken);

        var finished = Assert.IsType<BulkJob>(repository.Get(job.Id));
        Assert.Equal(2, finished.ProcessedRows);
        Assert.Equal(2, finished.FailedCount);
        Assert.All(runner.GetRows(job.Id), row => Assert.Equal(C2SnapshotDenied, row.Message));
        await service.DidNotReceive().ResolveEmailsAsync(Arg.Any<List<string>>());
        await service.DidNotReceive().ExecuteReplaceAsync(Arg.Any<IReadOnlyList<Comms10kAddressResolver.ResolvedMember>>(), Arg.Any<string>(), Arg.Any<Func<string?>>());
        fixture.AssertAudit(C2SnapshotDenied, Comms10kOutcome.RefusedBeforeAnyChange);
        fixture.AssertNotification(C2SnapshotDenied, Comms10kOutcome.RefusedBeforeAnyChange);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task C2RestoredConfigurationCannotOverwriteTheFirstAuthorizationRefusal(bool disableModule)
    {
        using var fixture = new Fixture();
        var service = fixture.ResolvingSubstitute();
        var processor = fixture.Processor(service);
        var job = Job();
        C2Revoke(fixture, disableModule);
        var first = await processor.ProcessRowAsync(job, 0, CancellationToken.None);
        C2Groups(fixture, [C5GroupSid]);
        fixture.EnablementRepository.SaveAll(new Dictionary<string, bool> { ["Comms10k"] = true });

        var continued = await processor.ProcessRowAsync(job, 1, CancellationToken.None);
        await processor.OnJobCompletedAsync(job);

        var reason = disableModule ? C2Disabled : C2GroupDenied;
        Assert.Equal(reason, first.Message);
        Assert.Equal(BulkJobRowStatus.Failed, continued.Status);
        Assert.Equal(reason, continued.Message);
        await service.DidNotReceive().ExecuteReplaceAsync(Arg.Any<IReadOnlyList<Comms10kAddressResolver.ResolvedMember>>(), Arg.Any<string>(), Arg.Any<Func<string?>>());
        fixture.AssertAudit(reason, Comms10kOutcome.RefusedBeforeAnyChange);
        fixture.AssertNotification(reason, Comms10kOutcome.RefusedBeforeAnyChange);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task C2SubmissionCapturesFreshGroupsAndTheSameCurrentPrincipalAsItsFullOwner(bool windowsOnlyRole)
    {
        using var fixture = new Fixture();
        using var progress = new ActivityProgressService();
        var page = C4Page(fixture, progress);
        var (queue, repository) = fixture.JobQueue();
        SetPageProperty(page, "BulkJobs", queue);
        SetField(page, "resolveResult", CompleteResolution());
        SetField(page, "confirmation", new Comms10kConfirmation([Address], "members.csv", "INC123", CompleteResolution().ResolvedMembers));
        SetField(page, "currentUser", "OLD\\page-initialization");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<AuthorizationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var current = windowsOnlyRole ? new C2WindowsPrincipal("OTHER\\fresh-operator", C2OtherSid)
            : new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "OTHER\\fresh-operator"),
                new Claim(ClaimTypes.GroupSid, C5GroupSid), new Claim(ClaimTypes.GroupSid, C2OtherSid)], "test"));
        var provider = Substitute.For<AuthenticationStateProvider>();
        var providerCalls = 0;
        provider.GetAuthenticationStateAsync().Returns(_ => new AuthenticationState(
            Interlocked.Increment(ref providerCalls) == 1 ? current : C5State().User));
        SetPageProperty(page, "AuthStateProvider", provider);
        var authorization = Substitute.For<IAuthorizationService>();
        var authorizationCalls = 0;
        ClaimsPrincipal? checkedPrincipal = null;
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), "Comms10k").Returns(call =>
        {
            if (Interlocked.Increment(ref authorizationCalls) != 1) return Task.FromResult(AuthorizationResult.Success());
            checkedPrincipal = call.ArgAt<ClaimsPrincipal>(0);
            entered.SetResult();
            return release.Task;
        });
        SetPageProperty(page, "AuthorizationService", authorization);
        var pending = InvokeReplace(page);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        try { C2Groups(fixture, [C2OtherSid]); }
        finally { release.TrySetResult(AuthorizationResult.Success()); }
        await pending;

        var job = Assert.Single(repository.GetActive());
        var snapshot = Assert.IsType<JobAuthorizationSnapshot>(JobAuthorizationSnapshot.FromJson(job.AuthSnapshotJson));
        Assert.Same(current, checkedPrincipal);
        Assert.Equal(current.Identity!.Name, job.SubmittedBy);
        Assert.Equal("OTHER\\fresh-operator", job.SubmittedByDisplay);
        Assert.Equal("Comms10k", snapshot.Section);
        Assert.Equal([C2OtherSid], snapshot.AuthorizedGroups);
        if (windowsOnlyRole) Assert.Empty(snapshot.RoleClaims);
        else Assert.Equal([C5GroupSid, C2OtherSid], snapshot.RoleClaims);
        var payload = JsonSerializer.Deserialize<Comms10kReplaceJobPayload>(job.PayloadJson)!;
        Assert.Equal(Comms10kMemberSet.Fingerprint([PersonGuid]), payload.ApprovedMemberFingerprint);
        Assert.Equal([Address], payload.Emails);
    }

    [Theory]
    [InlineData("policy")]
    [InlineData("new-section-grant")]
    [InlineData("disabled")]
    [InlineData("unreadable-sections")]
    [InlineData("capture-unreadable")]
    public async Task C2SubmissionDenialAtTheFreshCaptureClearsThePriorActorsPanel(string denial)
    {
        using var fixture = new Fixture();
        using var progress = new ActivityProgressService();
        var (page, repository) = await C5ReviewCachedPage(fixture, progress, true);
        SetField(page, "resolveResult", CompleteResolution());
        SetField(page, "confirmation", new Comms10kConfirmation([Address], "members.csv", "INC123", CompleteResolution().ResolvedMembers));
        if (denial == "new-section-grant") C2Groups(fixture, [C2OtherSid]);
        if (denial == "disabled") C2Revoke(fixture, true);
        if (denial == "unreadable-sections") fixture.C2BreakStore(false);
        if (denial == "capture-unreadable") SetPageProperty(page, "AuthStateProvider", C5Authentication(new C2UnreadablePrincipal()));
        if (denial == "policy")
        {
            var failed = Substitute.For<IAuthorizationService>();
            failed.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>()).Returns(AuthorizationResult.Failed());
            SetPageProperty(page, "AuthorizationService", failed);
        }

        await InvokeReplace(page);

        Assert.Equal(["a-private-active"], repository.GetActive().Select(job => job.Id));
        C5ReviewAssertEmptyScope(page);
        var reason = denial == "capture-unreadable" ? "Authorization could not be verified." : "Authorization denied.";
        Assert.Equal(reason, C5Field<Comms10kUpdateResult>(page, "result").Message);
        fixture.AssertAudit(reason, Comms10kOutcome.RefusedBeforeAnyChange);
        fixture.AssertNotification(reason, Comms10kOutcome.RefusedBeforeAnyChange);
    }

    private static string C2Snapshot(string section = "Comms10k", IReadOnlyList<string>? grants = null, IReadOnlyList<string>? claims = null) =>
        new JobAuthorizationSnapshot { Section = section, AuthorizedGroups = grants ?? [C5GroupSid], RoleClaims = claims ?? [C5GroupSid] }.ToJson();

    private static BulkJob C2Job(string? snapshot, IReadOnlyList<C2Member>? members = null) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        ModuleId = "Comms10k",
        JobType = Comms10kReplaceJobPayload.JobType,
        SubmittedBy = "EXAMPLE\\operator",
        SubmittedIp = "192.0.2.7",
        SubmittedAtUtc = DateTime.UtcNow,
        Ticket = "INC123",
        AuthSnapshotJson = snapshot,
        PayloadJson = JsonSerializer.Serialize(new Comms10kReplaceJobPayload
        {
            Emails = members?.Select(member => member.Address).ToList() ?? [Address],
            SourceFileName = "members.csv",
            ApprovedMemberFingerprint = Comms10kMemberSet.Fingerprint(members?.Select(member => member.Guid) ?? [PersonGuid]),
        }),
    };

    private static void C2Groups(Fixture fixture, string[] groups) =>
        fixture.SectionRepository.SaveAll(new Dictionary<string, string[]> { ["Comms10k"] = groups });

    private static void C2Revoke(Fixture fixture, bool disableModule)
    {
        if (disableModule) fixture.EnablementRepository.SaveAll(new Dictionary<string, bool> { ["Comms10k"] = false });
        else C2Groups(fixture, [C2OtherSid]);
    }

    private static void C2AssertPreDispatch(Fixture fixture, C2Directory directory, BulkJobRowOutcome row, string reason)
    {
        var result = Assert.IsType<Comms10kUpdateResult>(fixture.Service.LastResult);
        Assert.Equal(BulkJobRowStatus.Failed, row.Status);
        Assert.Equal(Comms10kOutcome.RefusedBeforeAnyChange, result.Outcome);
        Assert.Equal(1, result.MembersRequested);
        Assert.Equal(0, result.MembersWritten);
        Assert.Null(result.FinalCount);
        Assert.Empty(directory.Writes);
        Assert.DoesNotContain("readback", directory.Calls);
        Assert.Contains(reason, row.Message, StringComparison.Ordinal);
        fixture.AssertAudit(row.Message, result.Outcome);
        fixture.AssertNotification(row.Message!, result.Outcome);
    }

    private sealed class C2WindowsPrincipal(string name, string sid) : ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, name)], "test"))
    {
        public override bool IsInRole(string role) => string.Equals(role, sid, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class C2UnreadablePrincipal() : ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, C5Owner)], "test"))
    {
        public override bool IsInRole(string role) => throw new InvalidOperationException("Token roles unavailable.");
    }

    private sealed record C2Member(Guid Guid, string Dn, string Address);

    // Real service/PowerShell dispatch over harmless local functions; no AD module or vault.
    private sealed class C2Directory
    {
        public InitialSessionState State { get; } = InitialSessionState.CreateDefault2();
        public List<C2Member> Rows { get; }
        public HashSet<Guid> Actual { get; } = [OtherGuid];
        public IReadOnlyList<Guid> Primaries { get; set; } = [];
        public List<string> Calls { get; } = [];
        public List<string> Writes { get; } = [];
        public Action<string>? OnStep { get; set; }
        public bool FailClearOnce { get; set; }
        public bool FailAddOnce { get; set; }
        public bool FailReadback { get; set; }
        public void Record(string step) { Calls.Add(step); OnStep?.Invoke(step); }
        public void Read(bool membership)
        {
            var readback = Writes.Contains("clear");
            Record(readback ? "readback" : membership ? "preflight-membership" : "target-reread");
            if (readback && FailReadback) throw new InvalidOperationException("Read-back unavailable.");
        }
        public void Clear()
        {
            Writes.Add("clear");
            Record("clear");
            if (FailClearOnce) { FailClearOnce = false; throw new AdException(); }
            Actual.Clear();
        }
        public void Add(string[] values)
        {
            Writes.Add("add");
            Record("add");
            if (FailAddOnce) { FailAddOnce = false; throw new AdException(); }
            Actual.UnionWith(values.Select(value => Guid.Parse(value[6..^1])));
        }
        public IEnumerable<string> MemberDns() => Actual.Select(guid => Rows.SingleOrDefault(row => row.Guid == guid)?.Dn ?? "CN=Other,DC=example,DC=test");

        public C2Directory(int count = 1)
        {
            Rows = Enumerable.Range(0, count).Select(index => new C2Member(index == 0 ? PersonGuid : new Guid(index, 0, 0, new byte[8]),
                index == 0 ? PersonDn : $"CN=Person{index},DC=example,DC=test", index == 0 ? Address : $"person{index}@example.test")).ToList();
            State.Variables.Add(new SessionStateVariableEntry("C2Directory", this, "local directory recorder"));
            State.Variables.Add(new SessionStateVariableEntry("C2TargetGuid", Comms10kService.TargetGroupGuid, "fixed target"));
            State.Commands.Add(new SessionStateFunctionEntry("Import-Module", "[CmdletBinding()] param($Name) $global:C2Directory.Record('import')"));
            State.Commands.Add(new SessionStateFunctionEntry("Get-ADGroup", """
                [CmdletBinding()]
                param($Identity, [string[]] $Properties, [pscredential] $Credential)
                $global:C2Directory.Read($Properties -contains 'member')
                [pscustomobject]@{ DistinguishedName = 'CN=Broadcast,DC=example,DC=test'; ObjectGUID = $global:C2TargetGuid;
                    SID = 'S-1-5-21-100-200-300-4242'; member = @($global:C2Directory.MemberDns()) }
                """));
            State.Commands.Add(new SessionStateFunctionEntry("Set-ADGroup", """
                [CmdletBinding()]
                param($Identity, [string] $Clear, [hashtable] $Add, [pscredential] $Credential)
                if ($PSBoundParameters.ContainsKey('Clear')) { $global:C2Directory.Clear() }
                else { $global:C2Directory.Add([string[]] $Add['member']) }
                """));
            State.Commands.Add(new SessionStateFunctionEntry("Get-ADObject", """
                [CmdletBinding()]
                param([string] $LDAPFilter, [string[]] $Properties, [pscredential] $Credential)
                if ($LDAPFilter -like '(primaryGroupID=*') {
                    $global:C2Directory.Record('primary')
                    foreach ($guid in $global:C2Directory.Primaries) { [pscustomobject]@{ ObjectGUID = $guid } }
                } else {
                    foreach ($row in $global:C2Directory.Rows) {
                        if ($LDAPFilter.Contains('(distinguishedName=' + $row.Dn + ')')) {
                            [pscustomobject]@{ DistinguishedName = $row.Dn; ObjectGUID = $row.Guid }
                        }
                    }
                    if ($LDAPFilter.Contains('(distinguishedName=CN=Other,DC=example,DC=test)')) {
                        [pscustomobject]@{ DistinguishedName = 'CN=Other,DC=example,DC=test'; ObjectGUID = [guid]::Parse('ed192cad-c7bf-491b-93b0-09fe2648d2c8') }
                    }
                }
                """));
        }
    }
}
