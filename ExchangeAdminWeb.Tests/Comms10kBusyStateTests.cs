using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CsvHelper;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Jobs;
using ExchangeAdminWeb.Services.Progress;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using NSubstitute;
using Page = ExchangeAdminWeb.Components.Pages.Comms10k;

namespace ExchangeAdminWeb.Tests;

public sealed partial class Comms10kRefusalTests
{
    [Theory]
    [InlineData("HandleFileUpload")]
    [InlineData("ValidateEmails")]
    [InlineData("ExecuteReplace")]
    [InlineData("LoadPreview")]
    [InlineData("DownloadFull")]
    [InlineData("DownloadRejections")]
    public async Task C7EachBusyOwnerRefusesDirectReentryBeforeAnyStateChange(string handler)
    {
        using var fixture = new Fixture();
        using var progress = new ActivityProgressService();
        var (page, repository, js) = C7Page(fixture, progress);
        var gate = new C7Gate();
        var firstFile = new C7File("first.csv", new C7StagedStream("Email\r\n" + Address + "\r\n", "", gate));
        var pending = C7StartOwner(page, fixture, js, gate, handler, firstFile);
        Task? second = null;
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.True(C5Field<bool>(page, "isLoading"));
            var emails = C5Field<List<string>>(page, "parsedEmails");
            var rejected = C5Field<List<Comms10kAddressResolver.Rejection>>(page, "rejectedBeforeLookup");
            var duplicates = C5Field<List<(string Raw, string Address)>>(page, "duplicateRows");
            var approval = C5Field<Comms10kConfirmation?>(page, "confirmation");
            var resolved = C5Field<Comms10kResolveResult?>(page, "resolveResult");
            var notice = C5Field<Comms10kUpdateResult?>(page, "result");
            var confirmPending = C5Field<bool>(page, "confirmPending");
            var fileName = C5Field<string?>(page, "uploadedFileName");
            var activityCount = progress.Active.Count;
            var transfers = js.Downloads.Count;
            var secondFile = new C7File("second.csv", new MemoryStream(Encoding.UTF8.GetBytes("Email\r\nother@example.test\r\n")));

            second = C7InvokeOwner(page, handler, secondFile);

            Assert.True(second.IsCompletedSuccessfully);
            Assert.True(C5Field<bool>(page, "isLoading"));
            Assert.Same(emails, C5Field<List<string>>(page, "parsedEmails"));
            Assert.Same(rejected, C5Field<List<Comms10kAddressResolver.Rejection>>(page, "rejectedBeforeLookup"));
            Assert.Same(duplicates, C5Field<List<(string Raw, string Address)>>(page, "duplicateRows"));
            Assert.Same(approval, C5Field<Comms10kConfirmation?>(page, "confirmation"));
            Assert.Same(resolved, C5Field<Comms10kResolveResult?>(page, "resolveResult"));
            Assert.Same(notice, C5Field<Comms10kUpdateResult?>(page, "result"));
            Assert.Equal(confirmPending, C5Field<bool>(page, "confirmPending"));
            Assert.Equal(fileName, C5Field<string?>(page, "uploadedFileName"));
            Assert.Equal(activityCount, progress.Active.Count);
            Assert.Equal(transfers, js.Downloads.Count);
            Assert.Equal(0, secondFile.OpenCount);
            Assert.Empty(repository.GetActive());
        }
        finally
        {
            gate.Release();
            await pending;
            if (second is not null) await second;
        }
        Assert.False(C5Field<bool>(page, "isLoading"));
        if (handler == "ExecuteReplace") Assert.Single(repository.GetActive());
    }

    [Fact]
    public async Task C7UploadPublishesAllCollectionsTogetherAndRefusesASecondUploadBeforeReset()
    {
        using var fixture = new Fixture();
        using var progress = new ActivityProgressService();
        var (page, _, _) = C7Page(fixture, progress);
        var gate = new C7Gate();
        var first = new C7File("first.csv", new C7StagedStream(
            "Email,Row\r\n  " + Address + "  ,1\r\nnot-an-address,2\r\nPERSON@example.test,3\r\n", "", gate));
        var second = new C7File("second.csv", new MemoryStream(Encoding.UTF8.GetBytes("Email\r\nother@example.test\r\n")));
        var pending = C7InvokeOwner(page, "HandleFileUpload", first);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.True(C5Field<bool>(page, "isLoading"));
            Assert.Empty(C5Field<List<string>>(page, "parsedEmails"));
            Assert.Empty(C7Rejections(page));
            Assert.Empty(C5Field<List<(string Raw, string Address)>>(page, "duplicateRows"));
            Assert.Null(C5Field<Comms10kConfirmation?>(page, "confirmation"));
            var cleared = C5Field<List<string>>(page, "parsedEmails");

            await C7InvokeOwner(page, "HandleFileUpload", second);
            await InvokePage(page, "ValidateEmails");
            await InvokeReplace(page);

            Assert.Equal(0, second.OpenCount);
            Assert.Same(cleared, C5Field<List<string>>(page, "parsedEmails"));
            Assert.True(C5Field<bool>(page, "isLoading"));
            Assert.Empty(fixture.Service.AddressQueries);
        }
        finally { gate.Release(); await pending; }

        Assert.False(C5Field<bool>(page, "isLoading"));
        Assert.Equal([Address], C5Field<List<string>>(page, "parsedEmails"));
        Assert.Equal("first.csv", C5Field<string>(page, "uploadedFileName"));
        Assert.Equal("not-an-address", Assert.Single(C7Rejections(page)).Address);
        Assert.Equal("PERSON@example.test", Assert.Single(C5Field<List<(string Raw, string Address)>>(page, "duplicateRows")).Raw);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task C7FailedParseNeverPublishesAUsablePrefix(bool ioFailure)
    {
        using var fixture = new Fixture();
        using var progress = new ActivityProgressService();
        var (page, repository, _) = C7Page(fixture, progress);
        var gate = new C7Gate();
        var stream = new C7StagedStream("Email,Row\r\n" + Address + ",1\r\nnot-an-address,2\r\n",
            "broken\"quote@example.test,3\r\n", gate, ioFailure);
        var pending = C7InvokeOwner(page, "HandleFileUpload", new C7File("broken.csv", stream));
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Empty(C5Field<List<string>>(page, "parsedEmails"));
            Assert.Empty(C7Rejections(page));
        }
        finally { gate.Release(); await pending; }

        Assert.Contains("Failed to parse CSV:", C5Field<Comms10kUpdateResult>(page, "result").Message, StringComparison.Ordinal);
        Assert.False(C5Field<bool>(page, "isLoading"));
        Assert.Empty(C5Field<List<string>>(page, "parsedEmails"));
        Assert.Empty(C7Rejections(page));
        Assert.Null(C5Field<string?>(page, "uploadedFileName"));
        Assert.False(CanReplace(page));
        await InvokePage(page, "ValidateEmails");
        Assert.Empty(Assert.Single(fixture.Service.AddressQueries));
        Assert.False(CanReplace(page));
        await InvokeReplace(page);
        Assert.Empty(repository.GetActive());
    }

    [Theory]
    [InlineData("ValidateEmails", "LoadPreview")]
    [InlineData("ValidateEmails", "DownloadFull")]
    [InlineData("ValidateEmails", "DownloadRejections")]
    [InlineData("ExecuteReplace", "LoadPreview")]
    [InlineData("ExecuteReplace", "DownloadFull")]
    [InlineData("ExecuteReplace", "DownloadRejections")]
    public async Task C7ReadsCannotReleaseValidationOrSubmissionBusyOwnership(string owner, string read)
    {
        using var fixture = new Fixture();
        using var progress = new ActivityProgressService();
        var (page, repository, js) = C7Page(fixture, progress);
        var gate = new C7Gate();
        var pending = C7StartOwner(page, fixture, js, gate, owner);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            // A cached report exists while the approved validation/submission is suspended.
            SetField(page, "rejectedBeforeLookup", new List<Comms10kAddressResolver.Rejection> { new("bad row", "Not an address") });
            var activityCount = progress.Active.Count;

            await InvokePage(page, read);

            Assert.True(C5Field<bool>(page, "isLoading"));
            Assert.False(pending.IsCompleted);
            Assert.Equal(activityCount, progress.Active.Count);
            Assert.Equal(0, fixture.Service.MemberReads);
            Assert.Empty(js.Downloads);
            SetField(page, "rejectedBeforeLookup", new List<Comms10kAddressResolver.Rejection>());
        }
        finally { gate.Release(); await pending; }
        Assert.False(C5Field<bool>(page, "isLoading"));
        if (owner == "ExecuteReplace") Assert.Single(repository.GetActive());
        else Assert.True(CanReplace(page));
    }

    [Theory]
    [InlineData("ticket")]
    [InlineData("addresses")]
    [InlineData("filename")]
    public async Task C7ValidationCapturesInputsBeforeItsFirstYield(string changed)
    {
        using var fixture = new Fixture();
        using var progress = new ActivityProgressService();
        var (page, _, _) = C7Page(fixture, progress);
        var service = fixture.ResolvingSubstitute();
        SetPageProperty(page, "Comms10kService", service);
        SetField(page, "confirmation", null);
        var context = new C7YieldContext();
        var pending = context.Start(() => InvokePage(page, "ValidateEmails"));
        Assert.False(pending.IsCompleted);
        if (changed == "ticket") SetField(page, "ticketNumber", "INC999");
        if (changed == "addresses") C5Field<List<string>>(page, "parsedEmails")[0] = "other@example.test";
        if (changed == "filename") SetField(page, "uploadedFileName", "other.csv");
        await context.ResumeAsync(pending);
        await pending;

        Assert.Equal([Address], C7ResolutionInputs(service));
        Assert.Equal("INC123", C5Field<string>(page, "validatedTicket"));
        if (changed == "ticket")
        {
            Assert.True(CanReplace(page));
            Assert.Equal("INC123", C5Field<Comms10kConfirmation>(page, "confirmation").Ticket);
        }
        else Assert.False(CanReplace(page));
    }

    [Fact]
    public async Task C7ValidationKeepsCapturedAddressesAndTicketAcrossTicketValidationAwait()
    {
        using var fixture = new Fixture();
        using var progress = new ActivityProgressService();
        var (page, _, _) = C7Page(fixture, progress);
        var service = fixture.ResolvingSubstitute();
        SetPageProperty(page, "Comms10kService", service);
        var gate = new C7Gate();
        var requests = new List<Uri>();
        using var http = new HttpClient(new C7TicketHandler(gate, requests));
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("ServiceNow").Returns(http);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ServiceNow:Enabled"] = "true",
            ["ServiceNow:InstanceUrl"] = "https://tickets.invalid",
        }).Build();
        SetPageProperty(page, "ServiceNow", new ServiceNowService(config, NullLogger<ServiceNowService>.Instance, factory));
        var pending = InvokePage(page, "ValidateEmails");
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            SetField(page, "ticketNumber", "INC999");
            C5Field<List<string>>(page, "parsedEmails")[0] = "other@example.test";
        }
        finally { gate.Release(); await pending; }

        Assert.Contains("number=INC123", Uri.UnescapeDataString(Assert.Single(requests).Query), StringComparison.Ordinal);
        Assert.Equal([Address], C7ResolutionInputs(service));
        Assert.Equal("INC123", C5Field<string>(page, "validatedTicket"));
        Assert.False(CanReplace(page));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task C7SubmissionKeepsTheApprovedInputsAndCompletenessAcrossAwaits(bool authAwait)
    {
        using var fixture = new Fixture();
        using var progress = new ActivityProgressService();
        var (page, repository, _) = C7Page(fixture, progress);
        var expected = C5Field<Comms10kConfirmation>(page, "confirmation");
        var gate = new C7Gate();
        var context = new C7YieldContext();
        if (authAwait)
        {
            var auth = Substitute.For<AuthenticationStateProvider>();
            auth.GetAuthenticationStateAsync().Returns(async _ => { await gate.WaitAsync(); return C5State(); });
            SetPageProperty(page, "AuthStateProvider", auth);
        }
        var pending = authAwait ? InvokeReplace(page) : context.Start(() => InvokeReplace(page));
        try
        {
            if (authAwait) await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            else Assert.False(pending.IsCompleted);
            C5Field<List<string>>(page, "parsedEmails")[0] = "other@example.test";
            SetField(page, "uploadedFileName", "other.csv");
            SetField(page, "validatedTicket", "INC999");
            SetField(page, "ticketNumber", "INC999");
            SetField(page, "confirmation", new Comms10kConfirmation(["other@example.test"], "other.csv", "INC999", [new(OtherGuid, PersonDn)]));
            SetField(page, "resolveResult", new Comms10kResolveResult { Success = false });
        }
        finally
        {
            gate.Release();
            if (!authAwait) await context.ResumeAsync(pending);
            await pending;
        }

        var job = Assert.Single(repository.GetActive());
        var payload = JsonSerializer.Deserialize<Comms10kReplaceJobPayload>(job.PayloadJson)!;
        Assert.Equal([Address], payload.Emails);
        Assert.Equal("members.csv", payload.SourceFileName);
        Assert.Equal(expected.MemberFingerprint, payload.ApprovedMemberFingerprint);
        Assert.Equal("INC123", job.Ticket);
        Assert.Equal(C5Owner, job.SubmittedBy);
        Assert.NotNull(JobAuthorizationSnapshot.FromJson(job.AuthSnapshotJson));
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("empty")]
    [InlineData("lookup-rejection")]
    [InlineData("parse-rejection")]
    public async Task C7DirectSubmissionRejectsIncompleteResolutionEvenWithAnExistingApproval(string invalid)
    {
        using var fixture = new Fixture();
        using var progress = new ActivityProgressService();
        var (page, repository, _) = C7Page(fixture, progress);
        if (invalid == "failed") C5Field<Comms10kResolveResult>(page, "resolveResult").Success = false;
        if (invalid == "empty") SetField(page, "resolveResult", new Comms10kResolveResult { Success = true });
        if (invalid == "lookup-rejection") C5Field<Comms10kResolveResult>(page, "resolveResult").Rejections.Add(new("missing@example.test", "No match"));
        if (invalid == "parse-rejection") SetField(page, "rejectedBeforeLookup", new List<Comms10kAddressResolver.Rejection> { new("bad row", "Not an address") });

        await InvokeReplace(page);

        Assert.Empty(repository.GetActive());
        const string reason = "The file must resolve completely with no rejected rows before Replace. Fix the file and re-upload.";
        fixture.AssertAudit(reason, Comms10kOutcome.RefusedBeforeAnyChange);
        fixture.AssertNotification(reason, Comms10kOutcome.RefusedBeforeAnyChange);
        Assert.False(C5Field<bool>(page, "isLoading"));
    }

    [Theory]
    [InlineData(" Email ")]
    [InlineData("UserPrincipalName")]
    public async Task C7RawRejectedRowsSurviveLookupAndDownloadWithMatchingUnchanged(string header)
    {
        using var fixture = new Fixture();
        using var progress = new ActivityProgressService();
        var (page, _, js) = C7Page(fixture, progress);
        const string firstMiss = "  Missing@EXAMPLE.test \t";
        const string duplicateMiss = "missing@example.test  ";
        var file = new C7File("rows.csv", new MemoryStream(Encoding.UTF8.GetBytes(
            header + ",Row\r\n" + firstMiss + ",1\r\n" + duplicateMiss + ",2\r\n  " + Address + "  ,3\r\nPERSON@EXAMPLE.TEST\t,4\r\n")));

        await C7InvokeOwner(page, "HandleFileUpload", file);
        await InvokePage(page, "ValidateEmails");

        Assert.Equal(["Missing@EXAMPLE.test", Address], Assert.Single(fixture.Service.AddressQueries));
        Assert.Equal(["Missing@EXAMPLE.test", Address], C5Field<List<string>>(page, "parsedEmails"));
        var resolved = C5Field<Comms10kResolveResult>(page, "resolveResult");
        Assert.Equal(PersonGuid, Assert.Single(resolved.ResolvedMembers).ObjectGuid);
        Assert.Equal([firstMiss, duplicateMiss], C7Rejections(page).Select(r => r.Address));
        Assert.All(C7Rejections(page), row => Assert.Equal(Comms10kAddressResolver.Reasons.NoMatch, row.Reason));
        Assert.False(CanReplace(page));
        await InvokePage(page, "DownloadRejections");
        var transfer = Assert.Single(js.Downloads);
        Assert.Equal("downloadFile", transfer.Identifier);
        var text = Encoding.UTF8.GetString(Convert.FromBase64String((string)transfer.Arguments![2]!));
        using var reader = new CsvReader(new StringReader(text), CultureInfo.InvariantCulture);
        var rows = reader.GetRecords<Comms10kAddressResolver.Rejection>().ToArray();
        Assert.Equal([firstMiss, duplicateMiss], rows.Select(r => r.Address));
    }

    private static (Page Page, BulkJobRepository Repository, C7Js Js) C7Page(Fixture fixture, ActivityProgressService progress)
    {
        var page = C4Page(fixture, progress);
        var (queue, repository) = fixture.JobQueue();
        var js = new C7Js();
        SetPageProperty(page, "BulkJobs", queue);
        SetPageProperty(page, "JS", js);
        SetField(page, "parsedEmails", new List<string> { Address });
        SetField(page, "ticketNumber", " INC123 ");
        SetField(page, "resolveResult", CompleteResolution());
        SetField(page, "confirmation", new Comms10kConfirmation([Address], "members.csv", "INC123", CompleteResolution().ResolvedMembers));
        SetField(page, "confirmPending", true);
        return (page, repository, js);
    }

    private static Task C7StartOwner(Page page, Fixture fixture, C7Js js, C7Gate gate, string owner, C7File? file = null)
    {
        switch (owner)
        {
            case "ValidateEmails":
                var service = fixture.ResolvingSubstitute();
                service.ResolveEmailsAsync(Arg.Any<List<string>>()).Returns(async _ => { await gate.WaitAsync(); return CompleteResolution(); });
                SetPageProperty(page, "Comms10kService", service);
                break;
            case "ExecuteReplace":
                var auth = Substitute.For<AuthenticationStateProvider>();
                auth.GetAuthenticationStateAsync().Returns(async _ => { await gate.WaitAsync(); return C5State(); });
                SetPageProperty(page, "AuthStateProvider", auth);
                break;
            case "LoadPreview":
            case "DownloadFull":
                fixture.BeforeCredentialResponse = gate.WaitAsync;
                break;
            case "DownloadRejections":
                SetField(page, "rejectedBeforeLookup", new List<Comms10kAddressResolver.Rejection> { new("bad row", "Not an address") });
                js.BeforeTransfer = gate.WaitAsync;
                break;
        }
        return C7InvokeOwner(page, owner, file);
    }

    private static Task C7InvokeOwner(Page page, string handler, C7File? file = null) => handler == "HandleFileUpload"
        ? C5Invoke(page, handler, new InputFileChangeEventArgs([file ?? new C7File("empty.csv", new MemoryStream())]))
        : InvokePage(page, handler);

    private static List<Comms10kAddressResolver.Rejection> C7Rejections(Page page) =>
        (List<Comms10kAddressResolver.Rejection>)typeof(Page).GetProperty("AllRejections", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;

    private static List<string> C7ResolutionInputs(Comms10kService service) =>
        Assert.IsType<List<string>>(Assert.Single(service.ReceivedCalls(), call => call.GetMethodInfo().Name == "ResolveEmailsAsync").GetArguments()[0]);

    private sealed class C7Gate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task WaitAsync() { Entered.TrySetResult(); await _release.Task; }
        public void Release() => _release.TrySetResult();
    }

    private sealed class C7File(string name, Stream stream) : IBrowserFile
    {
        public string Name => name;
        public DateTimeOffset LastModified => DateTimeOffset.UnixEpoch;
        public long Size => 1024;
        public string ContentType => "text/csv";
        public int OpenCount { get; private set; }
        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default)
        {
            OpenCount++;
            return stream;
        }
    }

    // The parser consumes real CSV rows before its next browser-stream read is suspended.
    private sealed class C7StagedStream(string prefix, string tail, C7Gate gate, bool throwOnTail = false) : Stream
    {
        private readonly byte[] _prefix = Encoding.UTF8.GetBytes(prefix);
        private readonly byte[] _tail = Encoding.UTF8.GetBytes(tail);
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position < _prefix.Length)
            {
                var take = Math.Min(buffer.Length, _prefix.Length - _position);
                _prefix.AsMemory(_position, take).CopyTo(buffer);
                _position += take;
                return take;
            }
            await gate.WaitAsync();
            if (throwOnTail) throw new IOException("Browser stream failed after valid rows.");
            var offset = _position - _prefix.Length;
            var remaining = Math.Min(buffer.Length, _tail.Length - offset);
            _tail.AsMemory(offset, remaining).CopyTo(buffer);
            _position += remaining;
            return remaining;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class C7Js : IJSRuntime
    {
        public Func<Task>? BeforeTransfer { get; set; }
        public ConcurrentQueue<(string Identifier, object?[]? Arguments)> Downloads { get; } = new();
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => new(Transfer<TValue>(identifier, args));
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
        private async Task<TValue> Transfer<TValue>(string identifier, object?[]? args)
        {
            Downloads.Enqueue((identifier, args));
            if (BeforeTransfer is { } before) await before();
            return default!;
        }
    }

    private sealed class C7YieldContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();
        private readonly SemaphoreSlim _posted = new(0);
        public override void Post(SendOrPostCallback d, object? state)
        {
            _queue.Enqueue((d, state));
            _posted.Release();
        }
        public Task Start(Func<Task> invoke)
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try { return invoke(); }
            finally { SetSynchronizationContext(previous); }
        }
        // Runs every continuation the resumed operation posts back until it completes, including
        // one posted from a pool thread after a real hop such as the resolver's Task.Run, as the
        // circuit's renderer context would. Draining only what was queued at release lost that
        // late post and left the test awaiting the operation forever (cdt-7). Each wait is
        // bounded, so a lost continuation fails the test instead of hanging the run.
        public async Task ResumeAsync(Task pending)
        {
            while (!pending.IsCompleted)
            {
                if (!await _posted.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken))
                    throw new TimeoutException("The resumed operation neither completed nor posted a continuation.");
                if (!_queue.TryDequeue(out var item))
                    throw new InvalidOperationException("Continuation signal without a queued callback.");
                var previous = Current;
                SetSynchronizationContext(this);
                try { item.Callback(item.State); }
                finally { SetSynchronizationContext(previous); }
            }
        }
    }

    private sealed class C7TicketHandler(C7Gate gate, List<Uri> requests, bool valid = true) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("tickets.invalid", request.RequestUri!.Host);
            requests.Add(request.RequestUri);
            await gate.WaitAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(valid ? """{"result":[{"number":"INC123","state":"1","short_description":"Local ticket","sys_id":"test"}]}""" : """{"result":[]}"""),
            };
        }
    }
}
