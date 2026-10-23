using System.Reflection;
using System.Text;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Progress;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Serilog.Events;
using Page = ExchangeAdminWeb.Components.Pages.AdminEventLog;

namespace ExchangeAdminWeb.Tests;

#pragma warning disable BL0006
/// <summary>
/// aud-a46: the Admin Event Log's diagnostics. A read of the extended log that failed came back empty, so the page said
/// there were no diagnostics where it should have said they could not be read; and the extended log's count of the
/// entries it dropped was shown nowhere.
/// </summary>
/// <remarks>
/// The page's own LoadEvents, driven by reflection over a real JSONL writer and a real extended log on a temp root, and
/// its own BuildRenderTree into a builder - no renderer - as the aud-a26u page tests read theirs.
/// </remarks>
public sealed class EventLogDiagnosticsTests : IDisposable
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "eaw-a46-" + Guid.NewGuid().ToString("N"));
    private readonly JsonlLogService _jsonl;
    private readonly ExtendedLogService _extended;
    private readonly ActivityProgressService _progress = new();

    public EventLogDiagnosticsTests()
    {
        var appDir = Path.Combine(_root, "app");
        Directory.CreateDirectory(appDir);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Audit:LogRoot"] = _root,
        }).Build();
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(appDir);

        _jsonl = new JsonlLogService(config, NullLogger<JsonlLogService>.Instance);
        _extended = new ExtendedLogService(config, env, TestConfigStore.CreateAppSettings(appDir), NullLogger<ExtendedLogService>.Instance);
        _extended.SetLevel("Error");
    }

    public void Dispose()
    {
        _extended.Dispose();
        _progress.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    [Fact]
    public async Task AnExtendedLogThatCannotBeRead_IsShownAsUnreadable_NotAsNoDiagnostics()
    {
        WriteOneDiagnostic();
        var page = PageWithDiagnostics(shown: true);

        using (HoldExclusively(ExtendedFile()))
            await LoadEvents(page);

        var text = Rendered(page);
        Assert.Contains("could not be read", text, StringComparison.Ordinal);
        Assert.Contains(Path.GetFileName(ExtendedFile()), text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDroppedEntryCount_IsShownWithTheDiagnostics_OnlyWhenThereAreSome()
    {
        WriteOneDiagnostic();
        const string Dropped = "extended log has dropped";

        var nothingDropped = PageWithDiagnostics(shown: true);
        await LoadEvents(nothingDropped);
        Assert.DoesNotContain(Dropped, Rendered(nothingDropped), StringComparison.Ordinal);

        // A closed queue's refusal is one drop the service counts at once.
        _extended.Dispose();
        _extended.Write(LogEventLevel.Error, "an entry after the queue closed", "Test");
        Assert.Equal(1, _extended.DroppedEntryCount);

        var shown = PageWithDiagnostics(shown: true);
        await LoadEvents(shown);
        Assert.Contains("extended log has dropped 1 entry since it started", Rendered(shown), StringComparison.Ordinal);

        var hidden = PageWithDiagnostics(shown: false);
        await LoadEvents(hidden);
        Assert.DoesNotContain(Dropped, Rendered(hidden), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ALogFileThatCannotBeRotated_IsShown_WithHowFarPastItsLimitItIs(bool diagnostics)
    {
        // aud-a65. A rotation that kept failing was one warning in the application log. This page, where the audit log is
        // read, said nothing while the file grew past its limit. It is shown whether diagnostics are on or not.
        var small = new JsonlLogService(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Audit:LogRoot"] = _root,
            ["Audit:MaxFileBytes"] = "1024",
        }).Build(), NullLogger<JsonlLogService>.Instance);
        var active = small.GetAuditLogPaths(DateTime.Now).Last();
        while (!File.Exists(active) || new FileInfo(active).Length < 1024)
            Assert.Equal(JsonlWriteOutcome.Written, small.WriteAudit(AuditRecord("Filler-" + Guid.NewGuid().ToString("N"))));

        var page = PageWithDiagnostics(shown: diagnostics);
        SetProperty(page, "JsonlLog", small);
        long size;
        using (new FileStream(active, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            size = new FileInfo(active).Length;
            Assert.Equal(JsonlWriteOutcome.Written, small.WriteAudit(AuditRecord("Held")));
            await LoadEvents(page);
        }

        var text = Rendered(page);
        Assert.Contains($"cannot rotate its audit log file {Path.GetFileName(active)}", text, StringComparison.Ordinal);
        Assert.Contains($"the file is {size:N0} bytes, {size - 1024:N0} bytes past its limit of {1024:N0} bytes", text, StringComparison.Ordinal);

        // Once the rotation succeeds, the next load shows nothing.
        Assert.Equal(JsonlWriteOutcome.Written, small.WriteAudit(AuditRecord("Rotates")));
        await LoadEvents(page);
        Assert.DoesNotContain("cannot rotate", Rendered(page), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATraceFileThatCannotBeRead_IsShownAsUnreadable_NotAsNoDiagnostics()
    {
        // aud-a49. The trace files' read only logged a failure, so with diagnostics on the page looked complete.
        Assert.Equal(JsonlWriteOutcome.Written, _jsonl.WriteTrace(TraceRecord("a trace step")));
        var page = PageWithDiagnostics(shown: true);

        using (HoldExclusively(TraceFile()))
            await LoadEvents(page);

        var text = Rendered(page);
        Assert.Contains("could not be read", text, StringComparison.Ordinal);
        Assert.Contains(Path.GetFileName(TraceFile()), text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LinesThatCannotBeParsed_AreCountedPerLog_AndShownInOneMutedLine()
    {
        // aud-a49. Each parser dropped a line it could not parse and counted nothing - among them the cut line a writer
        // that died mid-write leaves, which the aud-a28z repair keeps on a line of its own.
        const string Notice = "could not be parsed and";
        Assert.Equal(JsonlWriteOutcome.Written, _jsonl.WriteAudit(AuditRecord("an audit row")));
        Assert.Equal(JsonlWriteOutcome.Written, _jsonl.WriteTrace(TraceRecord("a trace step")));
        WriteOneDiagnostic();

        var whole = PageWithDiagnostics(shown: true);
        await LoadEvents(whole);
        Assert.DoesNotContain(Notice, Rendered(whole), StringComparison.Ordinal);

        AppendCutLine(AuditFile());
        var auditOnly = PageWithDiagnostics(shown: false);
        await LoadEvents(auditOnly);
        Assert.Contains("1 line in the audit log could not be parsed and is not shown.", Rendered(auditOnly), StringComparison.Ordinal);

        AppendCutLine(AuditFile());
        AppendCutLine(TraceFile());
        AppendCutLine(ExtendedFile());
        var everyLog = PageWithDiagnostics(shown: true);
        await LoadEvents(everyLog);
        Assert.Contains(
            "2 lines in the audit log, 1 line in the trace log and 1 line in the extended diagnostic log could not be parsed and are not shown.",
            Rendered(everyLog), StringComparison.Ordinal);

        var withoutDiagnostics = PageWithDiagnostics(shown: false);
        await LoadEvents(withoutDiagnostics);
        Assert.Contains("2 lines in the audit log could not be parsed and are not shown.", Rendered(withoutDiagnostics), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("audit")]
    [InlineData("trace")]
    public async Task AFileThatCannotBeRead_WithALoggerThatThrows_DoesNotEndTheLoad(string stream)
    {
        // aud-a57. The audit and trace reads' catches reported through the page's logger with no guard, so a provider that
        // threw there ended LoadEvents with its exception, the rest of the range unread.
        Assert.Equal(JsonlWriteOutcome.Written, _jsonl.WriteAudit(AuditRecord("an audit row")));
        Assert.Equal(JsonlWriteOutcome.Written, _jsonl.WriteTrace(TraceRecord("a trace step")));
        var logger = new ThrowingLogger();
        var page = PageWithDiagnostics(shown: true, logger);

        using (HoldExclusively(stream == "audit" ? AuditFile() : TraceFile()))
            Assert.Null(await Record.ExceptionAsync(() => LoadEvents(page)));

        Assert.Equal(1, logger.Attempts);
        var text = Rendered(page);
        if (stream == "audit")
        {
            Assert.Contains("Error reading log", text, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("could not be read", text, StringComparison.Ordinal);
            Assert.Contains(Path.GetFileName(TraceFile()), text, StringComparison.Ordinal);
        }
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private static Dictionary<string, object?> AuditRecord(string action) => new()
    {
        ["eventType"] = "audit",
        ["ts"] = DateTime.UtcNow.ToString("O"),
        ["action"] = action,
        ["category"] = "Test",
        ["result"] = "Success",
    };

    private static Dictionary<string, object?> TraceRecord(string stage) => new()
    {
        ["eventType"] = "operation.step",
        ["ts"] = DateTime.UtcNow.ToString("O"),
        ["module"] = "Test",
        ["stage"] = stage,
        ["result"] = "Success",
    };

    /// <summary>A line a writer that died mid-write leaves: the start of a record, ended by the next write's repair.</summary>
    private static void AppendCutLine(string path)
    {
        using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        file.Write(Encoding.UTF8.GetBytes("{\"eventType\":\"audit\",\"action\":\"cut off" + Environment.NewLine));
    }

    private string AuditFile() => _jsonl.GetAuditLogPaths(DateTime.Now).Last();

    private string TraceFile() => _jsonl.GetTraceLogPaths(DateTime.Now).Last();

    private void WriteOneDiagnostic()
    {
        _extended.Write(LogEventLevel.Error, "a diagnostic entry", "Test");
        Assert.True(SpinWait.SpinUntil(() => _extended.GetEntries(DateTime.Today).Entries.Count == 1, TimeSpan.FromSeconds(10)),
            "the extended log never wrote its entry");
    }

    /// <summary>
    /// Takes <paramref name="path"/> with no sharing, as another process can. The consumer that wrote it may still be
    /// closing its handle once the line is readable, so the open is retried for a moment.
    /// </summary>
    private static FileStream HoldExclusively(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (attempt < 100)
            {
                Thread.Sleep(50);
            }
        }
    }

    private string ExtendedFile() =>
        Path.Combine(_root, "ExchangeAdminWeb", $"exchangeadmin_{DateTime.Now:yyyyMMdd}_extended.jsonl");

    private Page PageWithDiagnostics(bool shown, ILogger<Page>? logger = null)
    {
        var page = new Page();
        SetProperty(page, "JsonlLog", _jsonl);
        SetProperty(page, "ExtendedLog", _extended);
        SetProperty(page, "Progress", _progress);
        SetProperty(page, "Logger", logger ?? NullLogger<Page>.Instance);
        SetField(page, "authChecked", true);
        SetField(page, "showDiagnostics", shown);
        return page;
    }

    private static Task LoadEvents(Page page) =>
        (Task)typeof(Page).GetMethod("LoadEvents", PrivateInstance)!.Invoke(page, null)!;

    private static void SetProperty(Page page, string name, object value) =>
        typeof(Page).GetProperty(name, PrivateInstance)!.SetValue(page, value);

    private static void SetField(Page page, string name, object? value) =>
        typeof(Page).GetField(name, PrivateInstance)!.SetValue(page, value);

    /// <summary>The text the page's own render tree carries.</summary>
    private static string Rendered(Page page)
    {
        using var builder = new RenderTreeBuilder();
        typeof(Page).GetMethod("BuildRenderTree", PrivateInstance)!.Invoke(page, [builder]);
        var frames = builder.GetFrames();
        var text = new StringBuilder();
        for (var i = 0; i < frames.Count; i++)
        {
            var frame = frames.Array[i];
            if (frame.FrameType == RenderTreeFrameType.Text)
                text.Append(frame.TextContent).Append(' ');
            else if (frame.FrameType == RenderTreeFrameType.Markup)
                text.Append(frame.MarkupContent).Append(' ');
        }

        return text.ToString();
    }

    /// <summary>Counts every report and throws once it has, as a failing provider can (aud-a28u).</summary>
    private sealed class ThrowingLogger : ILogger<Page>
    {
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Interlocked.Increment(ref _attempts);
            throw new InvalidOperationException("the logging provider failed");
        }
    }
}
#pragma warning restore BL0006
