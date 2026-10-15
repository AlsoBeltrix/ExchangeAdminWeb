using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Reflection;
using System.Text.Json;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Jobs;
using ExchangeAdminWeb.Services.Progress;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Page = ExchangeAdminWeb.Components.Pages.Comms10k;

namespace ExchangeAdminWeb.Tests;

public sealed partial class Comms10kRefusalTests
{
    private static readonly Guid OtherGuid = new("ed192cad-c7bf-491b-93b0-09fe2648d2c8");

    [Fact]
    public void C4FingerprintHasOneVersionedCanonicalEncoding()
    {
        Assert.Equal("v1:8ff268686acd0885d1de77693f4ca5aed9c8f26af84c3502565f0665bbb1e78e",
            Comms10kMemberSet.Fingerprint([OtherGuid, PersonGuid, OtherGuid]));
        Assert.Equal(Comms10kMemberSet.Fingerprint([PersonGuid, OtherGuid]),
            Comms10kMemberSet.Fingerprint([OtherGuid, PersonGuid, PersonGuid]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void C4EmptyIdentitiesCannotBecomeAnApproval(bool emptyGuid)
    {
        Assert.Throws<ArgumentException>(() => Comms10kMemberSet.Fingerprint(emptyGuid ? [Guid.Empty] : []));
    }

    [Fact]
    public void C4ConfirmationCopiesInputsBeforePublishingApproval()
    {
        var emails = new List<string> { Address };
        var approved = new Comms10kConfirmation(emails, "members.csv", "INC123", CompleteResolution().ResolvedMembers);
        emails[0] = "other@example.test";

        Assert.Equal([Address], approved.Emails);
        Assert.Equal("members.csv", approved.SourceFileName);
        Assert.Equal("INC123", approved.Ticket);
        Assert.Equal(Comms10kMemberSet.Fingerprint([PersonGuid]), approved.MemberFingerprint);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v1:bad")]
    [InlineData("v2:0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("v1:ZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZ")]
    public async Task C4OldOrMalformedApprovalRefusesWithOneAuditAndSpecificCompletion(string? fingerprint)
    {
        using var fixture = new Fixture();
        var service = fixture.ResolvingSubstitute();
        var processor = fixture.Processor(service);
        var job = C4Job(fingerprint);
        Assert.Equal(2, processor.CountRows(job));

        var resolve = await processor.ProcessRowAsync(job, 0, CancellationToken.None);
        var replace = await processor.ProcessRowAsync(job, 1, CancellationToken.None);
        await processor.OnJobCompletedAsync(job);

        const string reason = "The approved member fingerprint is missing, malformed or unsupported. Validate the file and resubmit.";
        Assert.Equal(BulkJobRowStatus.Failed, resolve.Status);
        Assert.Equal(reason, resolve.Message);
        Assert.Equal(reason, replace.Message);
        await service.DidNotReceive().ExecuteReplaceAsync(Arg.Any<IReadOnlyList<Comms10kAddressResolver.ResolvedMember>>(), Arg.Any<string>());
        fixture.AssertAudit(reason, Comms10kOutcome.RefusedBeforeAnyChange);
        fixture.AssertNotification(reason, Comms10kOutcome.RefusedBeforeAnyChange);
    }

    [Theory]
    [InlineData("same-count")]
    [InlineData("more-members")]
    [InlineData("invalid-identity")]
    public async Task C4AnyIdentityChangeRefusesBeforeTheWriter(string change)
    {
        using var fixture = new Fixture();
        var service = fixture.ResolvingSubstitute();
        service.ResolveEmailsAsync(Arg.Any<List<string>>()).Returns(new Comms10kResolveResult
        {
            Success = true,
            ResolvedMembers = change switch
            {
                "same-count" => [new(OtherGuid, PersonDn)],
                "more-members" => [new(PersonGuid, PersonDn), new(OtherGuid, "CN=Other,DC=x")],
                _ => [new(Guid.Empty, PersonDn)],
            },
        });
        var processor = fixture.Processor(service);
        var job = Job();

        var resolve = await processor.ProcessRowAsync(job, 0, CancellationToken.None);
        var replace = await processor.ProcessRowAsync(job, 1, CancellationToken.None);
        await processor.OnJobCompletedAsync(job);

        var reason = change == "invalid-identity"
            ? "Resolved member identities were invalid. Validate the file and resubmit."
            : "The resolved member set has changed since validation. Nothing was written. Validate the file and resubmit.";
        Assert.Equal(BulkJobRowStatus.Failed, resolve.Status);
        Assert.Equal(reason, resolve.Message);
        Assert.Equal(reason, replace.Message);
        await service.DidNotReceive().ExecuteReplaceAsync(Arg.Any<IReadOnlyList<Comms10kAddressResolver.ResolvedMember>>(), Arg.Any<string>());
        fixture.AssertAudit(reason, Comms10kOutcome.RefusedBeforeAnyChange);
        fixture.AssertNotification(reason, Comms10kOutcome.RefusedBeforeAnyChange);
    }

    [Theory]
    [InlineData("identical")]
    [InlineData("moved-dn")]
    [InlineData("order-and-duplicates")]
    public async Task C4SamePeopleRemainExecutableWithTheirPairedRuntimeMetadata(string change)
    {
        using var fixture = new Fixture();
        var service = fixture.ResolvingSubstitute();
        var first = new Comms10kAddressResolver.ResolvedMember(PersonGuid,
            change == "moved-dn" ? "CN=Renamed,OU=Moved,DC=example,DC=test" : PersonDn);
        var second = new Comms10kAddressResolver.ResolvedMember(OtherGuid, "CN=Other,DC=x");
        Comms10kAddressResolver.ResolvedMember[] members = change == "order-and-duplicates"
            ? [second, first, first] : [first, second];
        service.ResolveEmailsAsync(Arg.Any<List<string>>()).Returns(new Comms10kResolveResult { Success = true, ResolvedMembers = members });
        service.ExecuteReplaceAsync(Arg.Any<IReadOnlyList<Comms10kAddressResolver.ResolvedMember>>(), Arg.Any<string>())
            .Returns(new Comms10kUpdateResult { Success = true, Outcome = Comms10kOutcome.Succeeded });
        var processor = fixture.Processor(service);
        var job = C4Job(Comms10kMemberSet.Fingerprint([PersonGuid, OtherGuid]));

        Assert.Equal(BulkJobRowStatus.Success, (await processor.ProcessRowAsync(job, 0, CancellationToken.None)).Status);
        Assert.Equal(BulkJobRowStatus.Success, (await processor.ProcessRowAsync(job, 1, CancellationToken.None)).Status);

        await service.Received(1).ExecuteReplaceAsync(
            Arg.Is<IReadOnlyList<Comms10kAddressResolver.ResolvedMember>>(actual => actual.SequenceEqual(members)), job.SubmittedBy);
    }

    [Theory]
    [InlineData("extra")]
    [InlineData("missing-guid")]
    [InlineData("invalid-guid")]
    [InlineData("unreadable")]
    [InlineData("unreadable-sid")]
    public async Task C4PrimaryPreflightRefusesOutsideApprovalOrUnreadableIdentities(string mode)
    {
        using var fixture = new Fixture();
        var directory = new C4Directory(mode);
        fixture.Service.ReplaceState = directory.State;
        var processor = fixture.Processor(fixture.Service);
        var job = Job();

        await processor.ProcessRowAsync(job, 0, CancellationToken.None);
        var row = await processor.ProcessRowAsync(job, 1, CancellationToken.None);
        await processor.OnJobCompletedAsync(job);

        Assert.Equal(BulkJobRowStatus.Failed, row.Status);
        Assert.Empty(directory.Added);
        Assert.DoesNotContain("clear", directory.Calls);
        Assert.Contains(mode == "extra" ? "cannot be removed" : "unreadable-sid" == mode ? "SID was unreadable" : mode == "unreadable" ? "primary unavailable" : "invalid ObjectGUID",
            row.Message, StringComparison.Ordinal);
        fixture.AssertAudit(row.Message, Comms10kOutcome.RefusedBeforeAnyChange);
        fixture.AssertNotification(row.Message!, Comms10kOutcome.RefusedBeforeAnyChange);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("subset")]
    public async Task C4PreflightSubsetAllowsGuidOnlyMemberAddsAndIdentityReadback(string mode)
    {
        using var fixture = new Fixture();
        var directory = new C4Directory(mode);
        fixture.Service.ReplaceState = directory.State;
        fixture.Service.Candidates = [Candidate(PersonGuid, "CN=Moved,OU=Elsewhere,DC=example,DC=test")];
        var processor = fixture.Processor(fixture.Service);
        var job = Job();

        Assert.Equal(BulkJobRowStatus.Success, (await processor.ProcessRowAsync(job, 0, CancellationToken.None)).Status);
        var row = await processor.ProcessRowAsync(job, 1, CancellationToken.None);
        await processor.OnJobCompletedAsync(job);

        Assert.Equal(BulkJobRowStatus.Success, row.Status);
        Assert.Equal([$"<GUID={PersonGuid:D}>"], directory.Added);
        Assert.Equal(1, fixture.Service.ReplaceRunspaces);
        Assert.True(directory.Calls.IndexOf("primary") < directory.Calls.IndexOf("clear"));
        Assert.Equal(2, directory.Calls.Count(call => call == "primary"));
        Assert.Equal(1, directory.MappingQueries);
        fixture.AssertAudit(null, Comms10kOutcome.Succeeded, success: true);
        fixture.AssertNotification(row.Message!, Comms10kOutcome.Succeeded, success: true);
    }

    [Theory]
    [InlineData("unmapped")]
    [InlineData("ambiguous")]
    [InlineData("invalid-mapping")]
    [InlineData("blank-member")]
    [InlineData("mapping-error")]
    public async Task C4UnreadableOrAmbiguousReadbackIsUnconfirmed(string mode)
    {
        using var fixture = new Fixture();
        var directory = new C4Directory(mode);
        fixture.Service.ReplaceState = directory.State;
        var processor = fixture.Processor(fixture.Service);
        var job = Job();
        await processor.ProcessRowAsync(job, 0, CancellationToken.None);
        var row = await processor.ProcessRowAsync(job, 1, CancellationToken.None);
        await processor.OnJobCompletedAsync(job);

        Assert.Equal(BulkJobRowStatus.Partial, row.Status);
        Assert.Contains("COULD NOT CONFIRM", row.Message, StringComparison.Ordinal);
        Assert.Contains("Read-back failure:", row.Message, StringComparison.Ordinal);
        Assert.Contains("clear", directory.Calls);
        fixture.AssertAudit(row.Message, Comms10kOutcome.CouldNotConfirm);
        fixture.AssertNotification(row.Message!, Comms10kOutcome.CouldNotConfirm);
    }

    [Theory]
    [InlineData("reused-dn")]
    [InlineData("concurrent-primary")]
    public async Task C4ObservedOtherIdentityNeverExpandsTheApprovedSet(string mode)
    {
        using var fixture = new Fixture();
        var directory = new C4Directory(mode);
        fixture.Service.ReplaceState = directory.State;
        var processor = fixture.Processor(fixture.Service);
        var job = Job();
        await processor.ProcessRowAsync(job, 0, CancellationToken.None);
        var row = await processor.ProcessRowAsync(job, 1, CancellationToken.None);
        await processor.OnJobCompletedAsync(job);

        Assert.Equal(BulkJobRowStatus.Partial, row.Status);
        Assert.Contains("PARTLY APPLIED", row.Message, StringComparison.Ordinal);
        if (mode == "concurrent-primary")
        {
            Assert.Contains("primary-group memberships outside the approved set cannot be removed by this operation", row.Message, StringComparison.Ordinal);
            Assert.Contains("before validating the file and resubmitting", row.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Re-run the same CSV", row.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("converges", row.Message, StringComparison.Ordinal);
            Assert.Contains("The write reported: Write acknowledgement unavailable.", row.Message, StringComparison.Ordinal);
        }
        else
            Assert.Contains("Re-run the same CSV", row.Message, StringComparison.Ordinal);
        fixture.AssertAudit(row.Message, Comms10kOutcome.PartlyApplied);
        fixture.AssertNotification(row.Message!, Comms10kOutcome.PartlyApplied);

        if (mode == "concurrent-primary")
        {
            var clears = directory.Calls.Count(call => call == "clear");
            var nextAttempt = fixture.Processor(fixture.Service);
            var nextJob = Job();
            Assert.Equal(BulkJobRowStatus.Success, (await nextAttempt.ProcessRowAsync(nextJob, 0, CancellationToken.None)).Status);
            var refused = await nextAttempt.ProcessRowAsync(nextJob, 1, CancellationToken.None);
            Assert.Equal(BulkJobRowStatus.Failed, refused.Status);
            Assert.Contains("primary-group members outside the approved set", refused.Message, StringComparison.Ordinal);
            Assert.Contains("cannot be removed by this operation", refused.Message, StringComparison.Ordinal);
            Assert.Equal(clears, directory.Calls.Count(call => call == "clear"));
        }
    }

    [Fact]
    public async Task C4ReadbackMapsEveryMemberInBatchesAndWritesRemainTwoThousand()
    {
        using var fixture = new Fixture();
        var directory = new C4Directory("empty", 2001);
        fixture.Service.ReplaceState = directory.State;
        var members = directory.Rows.Select(row => new Comms10kAddressResolver.ResolvedMember(row.Guid, row.Dn)).ToArray();

        var result = await fixture.Service.ExecuteReplaceAsync(members, "EXAMPLE\\operator");

        Assert.Equal(Comms10kOutcome.Succeeded, result.Outcome);
        Assert.Equal(2001, result.FinalCount);
        Assert.Equal([2000, 1], directory.BatchSizes);
        Assert.Equal(5, directory.MappingQueries);
        Assert.Equal(2001, directory.MappedRows);
        Assert.All(directory.Added, value => Assert.StartsWith("<GUID=", value, StringComparison.Ordinal));
        Assert.Equal(1, fixture.Service.ReplaceRunspaces);
    }

    [Fact]
    public async Task C4PageApprovalCapturesMatchingInputsAndSurvivesAwaitedSubmission()
    {
        using var fixture = new Fixture();
        using var progress = new ActivityProgressService();
        var page = C4Page(fixture, progress);
        var emails = new List<string> { Address };
        SetField(page, "parsedEmails", emails);
        SetField(page, "ticketNumber", " INC123 ");
        await InvokePage(page, "ValidateEmails");
        Assert.True(CanReplace(page));
        var approved = (Comms10kConfirmation)typeof(Page).GetField("confirmation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
        Assert.Equal("INC123", approved.Ticket);
        Assert.Equal("members.csv", approved.SourceFileName);
        Assert.Equal([Address], approved.Emails);
        Assert.Equal(Comms10kMemberSet.Fingerprint([PersonGuid]), approved.MemberFingerprint);
        var (queue, repository) = fixture.JobQueue();
        SetPageProperty(page, "BulkJobs", queue);
        var auth = Substitute.For<AuthenticationStateProvider>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<AuthenticationState>(TaskCreationOptions.RunContinuationsAsynchronously);
        auth.GetAuthenticationStateAsync().Returns(_ => { entered.SetResult(); return release.Task; });
        SetPageProperty(page, "AuthStateProvider", auth);
        var pending = InvokeReplace(page);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        emails[0] = "other@example.test";
        SetField(page, "uploadedFileName", "other.csv");
        SetField(page, "validatedTicket", "INC999");
        SetField(page, "confirmation", new Comms10kConfirmation(["other@example.test"], "other.csv", "INC999", [new(OtherGuid, PersonDn)]));
        release.SetResult(new AuthenticationState(new System.Security.Claims.ClaimsPrincipal()));
        await pending;

        var job = Assert.Single(repository.GetActive());
        var payload = JsonSerializer.Deserialize<Comms10kReplaceJobPayload>(job.PayloadJson)!;
        Assert.Equal([Address], payload.Emails);
        Assert.Equal("members.csv", payload.SourceFileName);
        Assert.Equal(approved.MemberFingerprint, payload.ApprovedMemberFingerprint);
        Assert.Equal("INC123", job.Ticket);
        Assert.Equal([Address], approved.Emails);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task C4ValidationCannotApproveAnUploadChangedDuringLookup(bool replaceList)
    {
        using var fixture = new Fixture();
        using var progress = new ActivityProgressService();
        var page = C4Page(fixture, progress);
        var service = fixture.ResolvingSubstitute();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<Comms10kResolveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.ResolveEmailsAsync(Arg.Any<List<string>>()).Returns(_ => { entered.SetResult(); return release.Task; });
        SetPageProperty(page, "Comms10kService", service);
        var emails = new List<string> { Address };
        SetField(page, "parsedEmails", emails);
        var pending = InvokePage(page, "ValidateEmails");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        if (replaceList) SetField(page, "parsedEmails", new List<string> { "other@example.test" });
        else emails[0] = "other@example.test";
        release.SetResult(CompleteResolution());
        await pending;

        Assert.False(CanReplace(page));
        await service.Received(1).ResolveEmailsAsync(Arg.Is<List<string>>(actual => actual.SequenceEqual(new[] { Address })));
    }

    private static BulkJob C4Job(string? fingerprint)
    {
        var original = Job();
        return new BulkJob
        {
            Id = original.Id,
            ModuleId = original.ModuleId,
            JobType = original.JobType,
            SubmittedBy = original.SubmittedBy,
            SubmittedIp = original.SubmittedIp,
            Ticket = original.Ticket,
            SubmittedAtUtc = original.SubmittedAtUtc,
            PayloadJson = fingerprint is null
            ? "{\"Emails\":[\"person@example.test\"],\"SourceFileName\":\"members.csv\"}"
            : JsonSerializer.Serialize(new Comms10kReplaceJobPayload
            {
                Emails = [Address],
                SourceFileName = "members.csv",
                ApprovedMemberFingerprint = fingerprint,
            }),
        };
    }

    private static Page C4Page(Fixture fixture, ActivityProgressService progress)
    {
        var page = fixture.Page();
        SetPageProperty(page, "Progress", progress);
        SetPageProperty(page, "Comms10kService", fixture.Service);
        SetPageProperty(page, "ServiceNow", new ServiceNowService(fixture.Config, NullLogger<ServiceNowService>.Instance,
            Substitute.For<IHttpClientFactory>()));
        return page;
    }

    private static void SetPageProperty(Page page, string name, object value) =>
        typeof(Page).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);

    private static Task InvokePage(Page page, string method) =>
        (Task)typeof(Page).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, null)!;

    private sealed record C4Row(Guid Guid, string Dn);

    /// <summary>Real SDK calls against local functions. No AD module, vault or directory is used.</summary>
    private sealed class C4Directory
    {
        public InitialSessionState State { get; } = InitialSessionState.CreateDefault2();
        public List<string> Calls { get; } = [];
        public List<string> Added { get; } = [];
        public List<int> BatchSizes { get; } = [];
        public List<C4Row> Rows { get; }
        public int MappingQueries;
        public int MappedRows;

        public C4Directory(string mode, int count = 1)
        {
            Rows = Enumerable.Range(0, count).Select(index => new C4Row(index == 0 ? PersonGuid : new Guid(index, 0, 0, new byte[8]),
                index == 0 ? PersonDn : $"CN=Person{index},DC=example,DC=test")).ToList();
            State.Variables.Add(new SessionStateVariableEntry("C4Directory", this, "local directory recorder"));
            State.Variables.Add(new SessionStateVariableEntry("C4Mode", mode, "local behavior"));
            State.Variables.Add(new SessionStateVariableEntry("C4TargetGuid", Comms10kService.TargetGroupGuid, "fixed target"));
            State.Variables.Add(new SessionStateVariableEntry("C4OtherGuid", OtherGuid, "other person"));
            State.Commands.Add(new SessionStateFunctionEntry("Import-Module", "[CmdletBinding()] param($Name)"));
            State.Commands.Add(new SessionStateFunctionEntry("Get-ADGroup", """
                [CmdletBinding()]
                param($Identity, [string[]] $Properties, [pscredential] $Credential)
                $global:C4Directory.Calls.Add('group')
                $members = @()
                if ($global:C4Directory.Calls.Contains('clear')) {
                    $members = @($global:C4Directory.Rows | ForEach-Object { $_.Dn })
                    if ($global:C4Mode -eq 'blank-member') { $members = @('') }
                }
                $sid = 'S-1-5-21-100-200-300-4242'
                if ($global:C4Mode -eq 'unreadable-sid') { $sid = $null }
                [pscustomobject]@{ DistinguishedName = 'CN=Broadcast,DC=example,DC=test'; ObjectGUID = $global:C4TargetGuid; SID = $sid; member = $members }
                """));
            State.Commands.Add(new SessionStateFunctionEntry("Set-ADGroup", """
                [CmdletBinding()]
                param($Identity, [string] $Clear, [hashtable] $Add, [pscredential] $Credential)
                if ($PSBoundParameters.ContainsKey('Clear')) { $global:C4Directory.Calls.Add('clear') }
                else {
                    $global:C4Directory.Calls.Add('add')
                    $global:C4Directory.BatchSizes.Add($Add['member'].Count)
                    foreach ($value in $Add['member']) { $global:C4Directory.Added.Add([string] $value) }
                    if ($global:C4Mode -eq 'concurrent-primary') { throw 'Write acknowledgement unavailable.' }
                }
                """));
            State.Commands.Add(new SessionStateFunctionEntry("Get-ADObject", """
                [CmdletBinding()]
                param([string] $LDAPFilter, [string[]] $Properties, [pscredential] $Credential)
                if ($Properties -notcontains 'objectGUID') { throw 'ObjectGUID property required' }
                if ($LDAPFilter -like '(primaryGroupID=*') {
                    $global:C4Directory.Calls.Add('primary')
                    if ($global:C4Mode -eq 'unreadable') { throw 'primary unavailable' }
                    if ($global:C4Mode -eq 'missing-guid') { [pscustomobject]@{ DistinguishedName = 'CN=Primary,DC=x' }; return }
                    if ($global:C4Mode -eq 'invalid-guid') { [pscustomobject]@{ ObjectGUID = 'not-a-guid' }; return }
                    if ($global:C4Mode -eq 'subset') { [pscustomobject]@{ ObjectGUID = $global:C4Directory.Rows[0].Guid } }
                    if ($global:C4Mode -eq 'extra' -or ($global:C4Mode -eq 'concurrent-primary' -and $global:C4Directory.Calls.Contains('clear'))) {
                        [pscustomobject]@{ ObjectGUID = $global:C4OtherGuid }
                    }
                    return
                }
                $global:C4Directory.MappingQueries++
                if ($global:C4Mode -eq 'mapping-error') { Write-Error 'mapping unavailable' -ErrorAction Continue; return }
                if ($global:C4Mode -eq 'unmapped') { return }
                foreach ($row in $global:C4Directory.Rows) {
                    if ($LDAPFilter.Contains('(distinguishedName=' + $row.Dn + ')')) {
                        $global:C4Directory.MappedRows++
                        $guid = $row.Guid
                        if ($global:C4Mode -eq 'invalid-mapping') { $guid = $null }
                        if ($global:C4Mode -eq 'reused-dn') { $guid = $global:C4OtherGuid }
                        [pscustomobject]@{ DistinguishedName = $row.Dn; ObjectGUID = $guid }
                        if ($global:C4Mode -eq 'ambiguous') { [pscustomobject]@{ DistinguishedName = $row.Dn; ObjectGUID = $global:C4OtherGuid } }
                    }
                }
                """));
        }
    }
}
