using System.Reflection;
using System.Text;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Progress;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
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

    // ---- helpers ---------------------------------------------------------------------------------------

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

    private Page PageWithDiagnostics(bool shown)
    {
        var page = new Page();
        SetProperty(page, "JsonlLog", _jsonl);
        SetProperty(page, "ExtendedLog", _extended);
        SetProperty(page, "Progress", _progress);
        SetProperty(page, "Logger", NullLogger<Page>.Instance);
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
}
#pragma warning restore BL0006
