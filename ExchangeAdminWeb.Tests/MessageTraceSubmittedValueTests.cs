using System.Text;
using System.Text.Json;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using NSubstitute;
using static ExchangeAdminWeb.Tests.MigrationStubExchange;
using static ExchangeAdminWeb.Tests.SubmittedValueHarness;
using Page = ExchangeAdminWeb.Components.Pages.MessageTrace;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// kd-72 on Message Trace: four handlers read the trace form after their await. The trace named its audit target -
/// sender, recipient, message id - and its ticket after the search's await, and Fill Trace Form stays clickable during
/// a trace and rewrites them; header analysis, a delivery detail and the live download audited the live ticket after
/// theirs, and the ticket box is not disabled during any of them. Each now reads what it records before its await, and
/// a second trace, analysis or download during one is ignored.
/// </summary>
/// <remarks>
/// The real <see cref="MessageTraceService"/> over a pool that cannot connect and a vault whose secret request times
/// out by itself (<see cref="CancellableVaultCredentials"/>), so every read fails at once - fail-soft where the service
/// is - and is audited; nothing reaches Exchange or a vault. <see cref="SubmittedValueHarness.HeldContinuations"/>
/// holds each handler at its await while the test changes the form.
/// </remarks>
public sealed class MessageTraceSubmittedValueTests : IDisposable
{
    private const string Ticket = "INC0001";
    private const string SenderA = "first.sender@contoso.test";

    private readonly CancellableVaultCredentials _credentials = new("MessageTrace") { TimesOut = true };
    private readonly MigrationStubExchange _exchange = new();
    private readonly IJSRuntime _js = Substitute.For<IJSRuntime>();
    private readonly RecordingProgress _progress = new();

    public void Dispose()
    {
        _exchange.Dispose();
        _credentials.Dispose();
    }

    [Fact]
    public void FillTraceFormOrAKeystrokeLandingWhileATraceRuns_LeavesTheAuditRowOnTheSearchRun()
    {
        var page = PageOver();
        SetField(page, "sender", SenderA);

        RunWithChangeWhileSuspended(() => Call(page, "RunTrace"), () =>
        {
            // Fill Trace Form, which stays clickable during a trace, and a keystroke in the ticket box.
            SetField(page, "sender", "other.sender@contoso.test");
            SetField(page, "recipient", "other.rcpt@contoso.test");
            SetField(page, "messageId", "<other@contoso.test>");
            SetField(page, "ticketNumber", "INC9999");
        });

        var row = Assert.Single(_credentials.AuditRows());
        Assert.Equal("MessageTrace", Text(row, "action"));
        Assert.Equal($"{SenderA}->*", Text(row, "target"));
        Assert.Equal(Ticket, Text(row, "ticket"));
    }

    [Fact]
    public void ASecondTraceWhileATraceRuns_IsIgnored_NotRunBesideIt()
    {
        var page = PageOver();
        SetField(page, "sender", SenderA);

        RunWithChangeWhileSuspended(() => Call(page, "RunTrace"), () =>
        {
            var second = Call(page, "RunTrace");
            Assert.True(second.IsCompleted, "a second trace during a trace started a second trace");
        });

        Assert.Single(_progress.Handles);
        Assert.Single(_credentials.AuditRows());
    }

    [Fact]
    public void AKeystrokeLandingWhileADetailLoads_IsNotTheTicketTheReadIsRecordedUnder()
    {
        var page = PageOver();

        RunWithChangeWhileSuspended(() => Call(page, "ToggleDetail", 0, CloudMessage("<m1@contoso.test>")),
            () => SetField(page, "ticketNumber", "INC9999"));

        var row = Assert.Single(_credentials.AuditRows());
        Assert.Equal("MessageTrace_Detail", Text(row, "action"));
        Assert.Equal(Ticket, Text(row, "ticket"));
    }

    [Fact]
    public void AKeystrokeLandingWhileADownloadRuns_IsNotTheTicketTheDownloadIsRecordedUnder()
    {
        var page = PageOver(CloudMessage("<m1@contoso.test>"), CloudMessage("<m2@contoso.test>"));

        RunWithChangeWhileSuspended(() => Call(page, "DownloadSelectedDetails"),
            () => SetField(page, "ticketNumber", "INC9999"));

        var row = Assert.Single(_credentials.AuditRows());
        Assert.Equal("MessageTrace_DetailDownload", Text(row, "action"));
        Assert.Equal("Success", Text(row, "result"));
        Assert.Equal(Ticket, Text(row, "ticket"));
    }

    [Fact]
    public void ASecondDownloadWhileADownloadRuns_IsIgnored_NotRunBesideIt()
    {
        var page = PageOver(CloudMessage("<m1@contoso.test>"));

        RunWithChangeWhileSuspended(() => Call(page, "DownloadSelectedDetails"), () =>
        {
            var second = Call(page, "DownloadSelectedDetails");
            Assert.True(second.IsCompleted, "a second download during a download started a second download");
        });

        Assert.Single(_progress.Handles);
        Assert.Single(_credentials.AuditRows());
    }

    [Fact]
    public void AKeystrokeLandingWhileAFileIsAnalysed_IsNotTheTicketTheAnalysisIsRecordedUnder()
    {
        var page = PageOver();

        RunWithChangeWhileSuspended(() => Call(page, "AnalyzeUploadedFile", Upload("first.eml")),
            () => SetField(page, "ticketNumber", "INC9999"));

        var row = Assert.Single(_credentials.AuditRows());
        Assert.Equal("MessageHeaderAnalysis", Text(row, "action"));
        Assert.Equal("first.eml", Text(row, "target"));
        Assert.Equal(Ticket, Text(row, "ticket"));
    }

    [Fact]
    public void ASecondFileWhileAFileIsAnalysed_IsIgnored_AndDoesNotRelabelTheAnalysis()
    {
        var page = PageOver();

        RunWithChangeWhileSuspended(() => Call(page, "AnalyzeUploadedFile", Upload("first.eml")), () =>
        {
            var second = Call(page, "AnalyzeUploadedFile", Upload("second.eml"));
            Assert.True(second.IsCompleted, "a second file during an analysis started a second analysis");
        });

        Assert.Equal("first.eml", Field<string>(page, "uploadedFileName"));
        Assert.Equal("first.eml", Text(Assert.Single(_credentials.AuditRows()), "target"));
    }

    private Page PageOver(params MessageTraceResult[] messages)
    {
        var page = new Page();
        Set(page, "MsgTrace", new MessageTraceService(_exchange.Pool(connects: false), null!,
            NullLogger<MessageTraceService>.Instance, _credentials.Configuration, _credentials.Service, _credentials.Trace));
        Set(page, "HeaderAnalysis", new HeaderAnalysisService(NullLogger<HeaderAnalysisService>.Instance));
        Set(page, "Audit", _credentials.Audit);
        Set(page, "Progress", _progress);
        Set(page, "JS", _js);
        SetField(page, "response", new MessageTraceResponse { Results = [.. messages] });
        var selected = Field<HashSet<int>>(page, "selectedIndices")!;
        for (var i = 0; i < messages.Length; i++)
            selected.Add(i);
        SetField(page, "currentUser", @"CONTOSO\operator");
        SetField(page, "clientIpAddress", "10.0.0.5");
        SetField(page, "ticketNumber", Ticket);
        return page;
    }

    private static MessageTraceResult CloudMessage(string id) => new()
    {
        Received = DateTime.UtcNow,
        SenderAddress = SenderA,
        RecipientAddress = "rcpt@contoso.test",
        Subject = "Quarterly figures",
        Status = "Delivered",
        MessageId = id,
        MessageTraceId = Guid.NewGuid().ToString(),
        Backend = "ExchangeOnline",
    };

    private static InputFileChangeEventArgs Upload(string name) => new([new EmlFile(name)]);

    private static string? Text(JsonElement row, string property) =>
        row.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>A small .eml whose stream yields before each read, as a browser upload does.</summary>
    private sealed class EmlFile(string name) : IBrowserFile
    {
        private static readonly byte[] Content = Encoding.ASCII.GetBytes(
            "From: first.sender@contoso.test\r\nTo: rcpt@contoso.test\r\nSubject: Quarterly figures\r\n"
            + "Message-ID: <m1@contoso.test>\r\nDate: Tue, 27 Oct 2026 10:00:00 +0000\r\n\r\nbody\r\n");

        public string Name => name;

        public DateTimeOffset LastModified => DateTimeOffset.UnixEpoch;

        public long Size => Content.Length;

        public string ContentType => "message/rfc822";

        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default) =>
            new YieldingStream(Content);
    }

    private sealed class YieldingStream(byte[] content) : MemoryStream(content, writable: false)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            return Read(buffer.Span);
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await Task.Yield();
            return Read(buffer, offset, count);
        }
    }
}
