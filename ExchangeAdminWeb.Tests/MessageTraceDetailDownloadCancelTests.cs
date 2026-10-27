using System.Reflection;
using System.Text.Json;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Progress;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using NSubstitute;
using Page = ExchangeAdminWeb.Components.Pages.MessageTrace;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Finding kd-57. Message Trace's live detail download filed a cancel it caught as a failed lookup - result Failed, its error
/// "Cancelled by the operator". It is filed Cancelled now, in the same words, which are the app's one phrase for a cancel
/// (<see cref="AuditService.CancelledByTheOperator"/>); aud-a70 (13) counts only Success as a success, so the row is neither.
/// </summary>
/// <remarks>
/// The page's handler run by reflection, as the other page tests here run theirs (no bUnit harness). The selected message's
/// backend is one the service does not know, so its detail is answered locally and nothing reaches Exchange; the cancel is
/// the activity's own, raised as the frame reports the first message - a stand-in activity whose token is cancelled.
/// </remarks>
public sealed class MessageTraceDetailDownloadCancelTests : IDisposable
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "eaw-kd57-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task ACancelOfTheDetailDownload_IsFiledCancelled_InTheAppsPhraseForACancel()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Audit:LogRoot"] = _root,
            ["Audit:RotationPeriod"] = "daily",
        }).Build();
        var log = new JsonlLogService(config, NullLogger<JsonlLogService>.Instance);
        var trace = new OperationTraceService(config, log);

        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        var activity = Substitute.For<IActivityHandle>();
        activity.CancellationToken.Returns(cancel.Token);
        activity.When(handle => handle.Report(Arg.Any<int>(), Arg.Any<string?>()))
            .Do(_ => throw new OperationCanceledException(cancel.Token));
        var progress = Substitute.For<IActivityProgress>();
        progress.Begin(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ActivitySize>()).Returns(activity);

        var page = new Page();
        Set(page, "MsgTrace", new MessageTraceService(null!, null!, NullLogger<MessageTraceService>.Instance, config, null!, trace));
        Set(page, "Audit", new AuditService(log, trace));
        Set(page, "Progress", progress);
        Set(page, "JS", Substitute.For<IJSRuntime>());
        Set(page, "response", new MessageTraceResponse
        {
            Results =
            [
                new MessageTraceResult
                {
                    SenderAddress = "sender@contoso.test",
                    RecipientAddress = "recipient@contoso.test",
                    Subject = "Quarterly figures",
                    Status = "Delivered",
                    MessageId = "<m1@contoso.test>",
                    Backend = "NotABackend",
                },
            ],
        });
        ((HashSet<int>)Get(page, "selectedIndices")!).Add(0);
        Set(page, "currentUser", "CONTOSO\\operator");
        Set(page, "clientIpAddress", "10.0.0.5");
        Set(page, "ticketNumber", "INC0001");

        await (Task)typeof(Page).GetMethod("DownloadSelectedDetails", Members)!.Invoke(page, null)!;

        var row = Assert.Single(AuditRows());
        Assert.Equal("MessageTrace_DetailDownload", row.GetProperty("action").GetString());
        Assert.Equal("Cancelled", row.GetProperty("result").GetString());
        Assert.Equal("Cancelled by the operator", row.GetProperty("error").GetString());
        Assert.Equal("INC0001", row.GetProperty("ticket").GetString());
        Assert.Null(Get(page, "detailActionError")); // not an error, and not shown as one
    }

    private List<JsonElement> AuditRows() =>
        Directory.GetFiles(Path.Combine(_root, "ExchangeAdminWeb"), "exchangeadmin_*.jsonl")
            .Where(path => !Path.GetFileNameWithoutExtension(path).EndsWith("_trace", StringComparison.OrdinalIgnoreCase))
            .SelectMany(File.ReadAllLines)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonDocument.Parse(line).RootElement)
            .Where(row => row.TryGetProperty("eventType", out var type) && type.GetString() == "audit")
            .ToList();

    private static void Set(object page, string name, object value)
    {
        var property = page.GetType().GetProperty(name, Members);
        if (property is not null)
        {
            property.SetValue(page, value);
            return;
        }

        page.GetType().GetField(name, Members)!.SetValue(page, value);
    }

    private static object? Get(object page, string name) => page.GetType().GetField(name, Members)!.GetValue(page);
}
