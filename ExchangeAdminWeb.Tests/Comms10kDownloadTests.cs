using System.Globalization;
using System.Text;
using CsvHelper;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Progress;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using NSubstitute;
using Page = ExchangeAdminWeb.Components.Pages.Comms10k;

namespace ExchangeAdminWeb.Tests;

public sealed partial class Comms10kRefusalTests
{
    [Theory]
    [InlineData("DownloadFull", "=")]
    [InlineData("DownloadFull", "+")]
    [InlineData("DownloadFull", "-")]
    [InlineData("DownloadFull", "@")]
    [InlineData("DownloadFull", "\t")]
    [InlineData("DownloadFull", "\r")]
    [InlineData("DownloadFull", "\n")]
    [InlineData("DownloadRejections", "=")]
    [InlineData("DownloadRejections", "+")]
    [InlineData("DownloadRejections", "-")]
    [InlineData("DownloadRejections", "@")]
    [InlineData("DownloadRejections", "\t")]
    [InlineData("DownloadRejections", "\r")]
    [InlineData("DownloadRejections", "\n")]
    public async Task C8DownloadsNeutralizeEveryColumnAndPreserveQuotedRowsAndOrder(string handler, string prefix)
    {
        using var fixture = new Fixture();
        using var progress = new ActivityProgressService();
        var (page, _, js) = C7Page(fixture, progress);
        var raw = Enumerable.Range(0, 14).Select(index => handler == "DownloadFull"
            ? new[] { $"mail{index}@example.test", $"sam{index}", $"Person {index}" }
            : new[] { $"bad{index}@example.test", $"Reason {index}" }).ToArray();
        for (var column = 0; column < raw[0].Length; column++)
        {
            raw[0][column] = prefix + $"cell{column},\"quoted\"\r\nend";
            raw[1][column] = $"plain{column},\"quoted\"\r\nmid=1+1";
            raw[2][column] = "";
        }
        // Display-name fallbacks are part of listing; leave that field nonempty here.
        if (handler == "DownloadFull") raw[2][2] = "'already literal";
        C8SetExportRows(page, fixture, handler, raw);

        await InvokePage(page, handler);

        var transfer = Assert.Single(js.Downloads);
        Assert.Equal("downloadFile", transfer.Identifier);
        var arguments = Assert.IsType<object[]>(transfer.Arguments);
        var name = Assert.IsType<string>(arguments[0]);
        Assert.Matches(handler == "DownloadFull" ? @"^comms10k_members_\d{8}\.csv$"
            : @"^comms10k_rejected_\d{8}_\d{6}\.csv$", name);
        Assert.Equal("text/csv", arguments[1]);
        var csv = Encoding.UTF8.GetString(Convert.FromBase64String(Assert.IsType<string>(arguments[2])));
        using var reader = new CsvReader(new StringReader(csv), CultureInfo.InvariantCulture);
        Assert.True(reader.Read());
        reader.ReadHeader();
        Assert.Equal(handler == "DownloadFull" ? ["Email", "SamAccountName", "DisplayName"]
            : new[] { "Address", "Reason" }, reader.HeaderRecord);
        var actual = new List<string[]>();
        while (reader.Read()) actual.Add(reader.Parser.Record!.ToArray());
        Assert.Equal(raw.Length, actual.Count);
        for (var row = 0; row < raw.Length; row++)
            Assert.Equal(row == 0 ? raw[row].Select(cell => "'" + cell).ToArray() : raw[row], actual[row]);
        Assert.Null(C5Field<Comms10kUpdateResult?>(page, "result"));
        Assert.False(C5Field<bool>(page, "isLoading"));
        Assert.Empty(progress.Active);
        Assert.Equal(handler == "DownloadFull" ? 1 : 0, fixture.Service.MemberReads);
    }

    [Theory]
    [InlineData("DownloadFull")]
    [InlineData("DownloadRejections")]
    public async Task C8AuditFaultAfterDeliveryLogsSeparatelyAndKeepsTheSuccessfulOutcome(string handler)
    {
        using var fixture = new Fixture();
        using var progress = new ActivityProgressService();
        var (page, _, js) = C7Page(fixture, progress);
        C8SetExportRows(page, fixture, handler, [[Address, "person", "Person"]]);
        var handle = C8CaptureActivity(page);
        var logger = Substitute.For<ILogger<Page>>();
        SetPageProperty(page, "Logger", logger);
        // The existing concrete audit fault seam throws on the actual audit call.
        SetPageProperty(page, "Audit", new AuditService(null!, null!));
        var delivered = false;
        js.BeforeTransfer = () =>
        {
            Assert.Empty(logger.ReceivedCalls());
            delivered = true;
            return Task.CompletedTask;
        };
        var prior = new Comms10kUpdateResult { Success = true, Message = "Existing operation result." };
        SetField(page, "result", prior);

        await InvokePage(page, handler);

        Assert.True(delivered);
        Assert.Single(js.Downloads);
        Assert.Same(prior, C5Field<Comms10kUpdateResult?>(page, "result"));
        var completion = Assert.Single(handle.ReceivedCalls(), call => call.GetMethodInfo().Name == "Complete");
        Assert.Equal(true, completion.GetArguments()[0]);
        Assert.Equal(handler == "DownloadFull" ? "1 member(s) exported." : "1 rejected address(es) exported.",
            completion.GetArguments()[1]);
        var logged = Assert.Single(logger.ReceivedCalls(), call => call.GetMethodInfo().Name == "Log");
        Assert.Equal(LogLevel.Error, logged.GetArguments()[0]);
        Assert.IsType<NullReferenceException>(logged.GetArguments()[3]);
        Assert.Equal("Failed to write Comms-10k audit record.", logged.GetArguments()[2]!.ToString());
        Assert.False(C5Field<bool>(page, "isLoading"));
        Assert.Single(handle.ReceivedCalls(), call => call.GetMethodInfo().Name == "Dispose");
    }

    [Theory]
    [InlineData("DownloadFull")]
    [InlineData("DownloadRejections")]
    public async Task C8BrowserDeliveryFailureRetainsItsFailureResultAndReleasesBusy(string handler)
    {
        using var fixture = new Fixture();
        using var progress = new ActivityProgressService();
        var (page, _, js) = C7Page(fixture, progress);
        C8SetExportRows(page, fixture, handler, [[Address, "person", "Person"]]);
        var handle = C8CaptureActivity(page);
        var failure = new JSException("Browser delivery unavailable.");
        js.BeforeTransfer = () => Task.FromException(failure);
        var logger = Substitute.For<ILogger<Page>>();
        SetPageProperty(page, "Logger", logger);
        SetPageProperty(page, "Audit", new AuditService(null!, null!));

        await InvokePage(page, handler);

        Assert.Single(js.Downloads);
        var result = C5Field<Comms10kUpdateResult>(page, "result");
        Assert.False(result.Success);
        Assert.Equal(failure.Message, result.Message);
        Assert.DoesNotContain(handle.ReceivedCalls(), call => call.GetMethodInfo().Name == "Complete"
            && Equals(call.GetArguments()[0], true));
        Assert.Empty(logger.ReceivedCalls());
        Assert.False(C5Field<bool>(page, "isLoading"));
        Assert.Single(handle.ReceivedCalls(), call => call.GetMethodInfo().Name == "Dispose");
    }

    [Fact]
    public async Task C8MemberReadFailureRetainsItsFailureResultWithoutDeliveryOrAudit()
    {
        using var fixture = new Fixture();
        using var progress = new ActivityProgressService();
        var (page, _, js) = C7Page(fixture, progress);
        var handle = C8CaptureActivity(page);
        fixture.BeforeCredentialResponse = () => throw new InvalidOperationException("Credential read unavailable.");
        var logger = Substitute.For<ILogger<Page>>();
        SetPageProperty(page, "Logger", logger);
        SetPageProperty(page, "Audit", new AuditService(null!, null!));

        await InvokePage(page, "DownloadFull");

        Assert.Empty(js.Downloads);
        var result = C5Field<Comms10kUpdateResult>(page, "result");
        Assert.False(result.Success);
        Assert.Equal("Cannot connect to AD: credentials unavailable.", result.Message);
        Assert.DoesNotContain(handle.ReceivedCalls(), call => call.GetMethodInfo().Name == "Complete");
        Assert.Empty(logger.ReceivedCalls());
        Assert.False(C5Field<bool>(page, "isLoading"));
        Assert.Single(handle.ReceivedCalls(), call => call.GetMethodInfo().Name == "Dispose");
    }

    private static void C8SetExportRows(Page page, Fixture fixture, string handler, string[][] rows)
    {
        if (handler == "DownloadFull")
            fixture.Service.ExportRows = rows.Select((row, index) => new Comms10kMemberResolver.Detail(
                $"CN=Export{index},DC=example,DC=test", row[1], row[0], row[2])).ToArray();
        else
            SetField(page, "rejectedBeforeLookup", rows.Select(row => new Comms10kAddressResolver.Rejection(row[0], row[1])).ToList());
    }

    private static IActivityHandle C8CaptureActivity(Page page)
    {
        var sink = Substitute.For<IActivityProgress>();
        var handle = Substitute.For<IActivityHandle>();
        sink.Begin(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ActivitySize>()).Returns(handle);
        SetPageProperty(page, "Progress", sink);
        return handle;
    }
}
