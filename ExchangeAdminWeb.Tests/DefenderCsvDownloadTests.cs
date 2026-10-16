using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CsvHelper;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Progress;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using NSubstitute;
using Page = ExchangeAdminWeb.Components.Pages.DefenderEndpointDevices;

namespace ExchangeAdminWeb.Tests;

// Invoke the actual compiled handler and render frames without a browser or attached circuit.
#pragma warning disable BL0006
public sealed class DefenderCsvDownloadTests
{
    private const string SafeError = "CSV export could not be completed. Try Download CSV again.";
    private const string SensitiveFailure = "RAW_AUTH_BODY password=DO_NOT_LOG bearer=sentinel";
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData("selection", false)]
    [InlineData("projection", false)]
    [InlineData("browser", false)]
    [InlineData("selection", true)]
    [InlineData("projection", true)]
    [InlineData("browser", true)]
    public async Task ExportFailureIsHandledAuditedAndKeepsInventory(string stage, bool auditFails)
    {
        using var fixture = new Fixture(auditFails);
        var rows = new FaultingRows(new DefenderDevice { Id = "m1", ComputerDnsName = "retained.example.test", Site = "London" })
        {
            Fail = stage != "browser"
        };
        var loaded = Complete(rows);
        SetField(fixture.Page, "result", loaded);
        SetField(fixture.Page, "appliedSite", stage == "selection" ? "London" : "");
        fixture.Js.BeforeTransfer = () => Task.FromException(new JSException(SensitiveFailure));

        var escaped = await Record.ExceptionAsync(() => Download(fixture.Page));

        Assert.Null(escaped);
        Assert.Equal(SafeError, Field<string?>(fixture.Page, "csvExportError"));
        Assert.Same(loaded, Field<DefenderDeviceListResult>(fixture.Page, "result"));
        Assert.Null(Field<string?>(fixture.Page, "loadError"));
        Assert.False(Field<bool>(fixture.Page, "isDownloadingCsv"));
        Assert.Equal(stage == "browser" ? 1 : 0, fixture.Js.Attempts);
        Assert.Empty(fixture.Js.Delivered);
        Assert.DoesNotContain(fixture.Handle.ReceivedCalls(), call => call.GetMethodInfo().Name == "Complete"
            && Equals(call.GetArguments()[0], true));
        Assert.Equal(stage == "selection" ? 0 : 1,
            fixture.Handle.ReceivedCalls().Count(call => call.GetMethodInfo().Name == "Dispose"));
        var logs = fixture.Logger.ReceivedCalls().Where(call => call.GetMethodInfo().Name == "Log").ToArray();
        Assert.Equal(auditFails ? 2 : 1, logs.Length);
        Assert.Null(logs[0].GetArguments()[3]);
        Assert.Contains(stage == "browser" ? nameof(JSException) : nameof(InvalidOperationException),
            logs[0].GetArguments()[2]!.ToString());
        Assert.All(logs, call => Assert.DoesNotContain(SensitiveFailure,
            call.GetArguments()[2] + " " + call.GetArguments()[3]));
        if (auditFails)
        {
            Assert.IsType<NullReferenceException>(logs[1].GetArguments()[3]);
            Assert.Equal("Failed to write Defender for Endpoint Devices audit record.", logs[1].GetArguments()[2]!.ToString());
            Assert.Empty(fixture.Audits());
        }
        else
        {
            var audit = Assert.Single(fixture.Audits());
            AssertAudit(audit, "Failed", "Device inventory");
            Assert.Equal(SafeError, audit.GetProperty("error").GetString());
            Assert.DoesNotContain(SensitiveFailure, fixture.AllLogText());
        }

        // Restore the local throwing collection so compiled markup can read the retained table.
        rows.Fail = false;
        var rendered = RenderText(fixture.Page);
        Assert.Contains(SafeError, rendered);
        Assert.Contains("retained.example.test", rendered);
        Assert.DoesNotContain("Could not read the device inventory.", rendered);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeliveredExportKeepsFilteredRowsAndClearsFailureEvenIfAuditFails(bool auditFails)
    {
        using var fixture = new Fixture(auditFails);
        var rows = Enumerable.Range(0, 51).Select(index => new DefenderDevice
        {
            Id = "m" + index,
            ComputerDnsName = index == 0 ? "=host,\"quoted\"\nline.example.test" : "host" + index + ".example.test",
            Site = "London",
            RecentlySeenBy = "RECEPTION",
            MachineGroup = "ordinary=group"
        }).ToList();
        rows.Add(new DefenderDevice
        {
            Id = "unavailable",
            Site = DefenderEndpointDeviceService.EnrichmentUnavailable,
            RecentlySeenBy = DefenderEndpointDeviceService.EnrichmentUnavailable,
            DeviceType = DefenderEndpointDeviceService.EnrichmentUnavailable
        });
        rows.Add(new DefenderDevice { Id = "excluded", Site = "Paris", RecentlySeenBy = "RECEPTION" });
        var loaded = Complete(rows);
        SetField(fixture.Page, "result", loaded);
        SetField(fixture.Page, "csvExportError", SafeError);
        SetField(fixture.Page, "appliedSite", "London");
        SetField(fixture.Page, "appliedSeenBy", "RECEPTION");
        SetField(fixture.Page, "devicePageIndex", 1);
        fixture.Js.BeforeTransfer = () =>
        {
            Assert.True(Field<bool>(fixture.Page, "isDownloadingCsv"));
            Assert.Empty(fixture.Logger.ReceivedCalls());
            return Task.CompletedTask;
        };

        Assert.Null(await Record.ExceptionAsync(() => Download(fixture.Page)));

        var transfer = Assert.Single(fixture.Js.Delivered);
        Assert.Matches(@"^DefenderEndpointDevices_\d{8}_\d{6}\.csv$", (string)transfer[0]!);
        Assert.Equal("text/csv", transfer[1]);
        using var reader = new StringReader(Encoding.UTF8.GetString(Convert.FromBase64String((string)transfer[2]!)));
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
        Assert.True(csv.Read());
        csv.ReadHeader();
        Assert.Equal(29, csv.HeaderRecord!.Length);
        var exported = new List<Dictionary<string, string>>();
        while (csv.Read())
            exported.Add(csv.HeaderRecord.ToDictionary(header => header, header => csv.GetField(header) ?? ""));
        Assert.Equal(rows.Take(52).Select(row => row.Id), exported.Select(row => row["DeviceId"]));
        Assert.Equal("'" + rows[0].ComputerDnsName, exported[0]["ComputerDnsName"]);
        Assert.Equal("ordinary=group", exported[0]["MachineGroup"]);
        Assert.Equal(DefenderEndpointDeviceService.EnrichmentUnavailable, exported[^1]["Site"]);
        Assert.Equal(DefenderEndpointDeviceService.EnrichmentUnavailable, exported[^1]["DeviceType"]);
        Assert.Null(Field<string?>(fixture.Page, "csvExportError"));
        Assert.Same(loaded, Field<DefenderDeviceListResult>(fixture.Page, "result"));
        Assert.Equal(1, Field<int>(fixture.Page, "devicePageIndex"));
        Assert.False(Field<bool>(fixture.Page, "isDownloadingCsv"));
        var completion = Assert.Single(fixture.Handle.ReceivedCalls(), call => call.GetMethodInfo().Name == "Complete");
        Assert.Equal(true, completion.GetArguments()[0]);
        Assert.Equal("52 device(s) exported.", completion.GetArguments()[1]);
        Assert.Single(fixture.Handle.ReceivedCalls(), call => call.GetMethodInfo().Name == "Dispose");
        if (auditFails)
        {
            var log = Assert.Single(fixture.Logger.ReceivedCalls(), call => call.GetMethodInfo().Name == "Log");
            Assert.IsType<NullReferenceException>(log.GetArguments()[3]);
            Assert.Equal("Failed to write Defender for Endpoint Devices audit record.", log.GetArguments()[2]!.ToString());
            Assert.Empty(fixture.Audits());
        }
        else
        {
            Assert.Empty(fixture.Logger.ReceivedCalls());
            AssertAudit(Assert.Single(fixture.Audits()), "Success", "52 rows");
        }
    }

    [Theory]
    [InlineData("no-result")]
    [InlineData("empty")]
    [InlineData("refused")]
    public async Task IneligibleInventoryStillStartsNoExportOrAudit(string state)
    {
        using var fixture = new Fixture(false);
        var loaded = state switch
        {
            "empty" => Complete([]),
            "refused" => DefenderDeviceListResult.Refusal(DefenderDeviceListOutcome.IncompletePaging, "Incomplete", 1, 1, 0, 50),
            _ => null
        };
        SetField(fixture.Page, "result", loaded);

        Assert.Null(await Record.ExceptionAsync(() => Download(fixture.Page)));

        Assert.Equal(0, fixture.Js.Attempts);
        Assert.Empty(fixture.Progress.ReceivedCalls());
        Assert.Empty(fixture.Audits());
        Assert.Empty(fixture.Logger.ReceivedCalls());
        Assert.Null(Field<string?>(fixture.Page, "csvExportError"));
        Assert.False(Field<bool>(fixture.Page, "isDownloadingCsv"));
        Assert.Same(loaded, Field<DefenderDeviceListResult?>(fixture.Page, "result"));
    }

    private static DefenderDeviceListResult Complete(IReadOnlyList<DefenderDevice> rows) =>
        DefenderDeviceListResult.CompleteRun(rows, rows.Count, 1, 1, 100,
            DefenderDiscoveryEnrichmentState.PartiallySucceeded, "Some enrichment unavailable", 1, 2);

    private static void AssertAudit(JsonElement audit, string result, string target)
    {
        Assert.Equal("operator", audit.GetProperty("user").GetString());
        Assert.Equal("192.0.2.5", audit.GetProperty("ip").GetString());
        Assert.Equal("ExportCsv", audit.GetProperty("action").GetString());
        Assert.Equal("DefenderEndpointDevices", audit.GetProperty("category").GetString());
        Assert.Equal(result, audit.GetProperty("result").GetString());
        Assert.Equal(target, audit.GetProperty("target").GetString());
    }

    private static Task Download(Page page) => (Task)typeof(Page).GetMethod("DownloadCsvAsync", PrivateInstance)!.Invoke(page, null)!;
    private static void SetField(Page page, string name, object? value) => typeof(Page).GetField(name, PrivateInstance)!.SetValue(page, value);
    private static T Field<T>(Page page, string name) => (T)typeof(Page).GetField(name, PrivateInstance)!.GetValue(page)!;
    private static void SetProperty(Page page, string name, object value) => typeof(Page).GetProperty(name, PrivateInstance)!.SetValue(page, value);

    private static string RenderText(Page page)
    {
        using var builder = new RenderTreeBuilder();
        typeof(Page).GetMethod("BuildRenderTree", PrivateInstance)!.Invoke(page, [builder]);
        var frames = builder.GetFrames();
        return string.Join("\n", frames.Array.Take(frames.Count).Select(frame => frame.FrameType switch
        {
            RenderTreeFrameType.Text => frame.TextContent,
            RenderTreeFrameType.Markup => frame.MarkupContent,
            _ => ""
        }));
    }

    private sealed class FaultingRows(DefenderDevice row) : IReadOnlyList<DefenderDevice>
    {
        public bool Fail { get; set; }
        public int Count => 1;
        public DefenderDevice this[int index] => row;
        public IEnumerator<DefenderDevice> GetEnumerator()
        {
            if (Fail) throw new InvalidOperationException(SensitiveFailure);
            yield return row;
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class RecordingJs : IJSRuntime
    {
        public int Attempts { get; private set; }
        public List<object?[]> Delivered { get; } = [];
        public Func<Task> BeforeTransfer { get; set; } = () => Task.CompletedTask;
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, default, args);
        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Assert.Equal("downloadFile", identifier);
            Attempts++;
            await BeforeTransfer();
            Delivered.Add(args!);
            return default!;
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "defender-csv-" + Guid.NewGuid().ToString("N"));
        public Page Page { get; } = new();
        public RecordingJs Js { get; } = new();
        public IActivityProgress Progress { get; } = Substitute.For<IActivityProgress>();
        public IActivityHandle Handle { get; } = Substitute.For<IActivityHandle>();
        public ILogger<Page> Logger { get; } = Substitute.For<ILogger<Page>>();

        public Fixture(bool auditFails)
        {
            Directory.CreateDirectory(root);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Audit:LogRoot"] = root
            }).Build();
            var log = new JsonlLogService(config, NullLogger<JsonlLogService>.Instance);
            var audit = auditFails ? new AuditService(null!, null!) : new AuditService(log, new OperationTraceService(config, log));
            var store = Substitute.For<IConfigStore>();
            store.Read(Arg.Any<Func<SqliteConnection, Dictionary<string, string>>>())
                .Returns(_ => new Dictionary<string, string> { ["GraphDelineaSecretId"] = "1" });
            var env = Substitute.For<IWebHostEnvironment>();
            env.ContentRootPath.Returns(root);
            var moduleConfig = new ModuleConfigService(new ModuleCatalog(), env, new ModuleConfigRepository(store), NullLogger<ModuleConfigService>.Instance);
            SetProperty(Page, "DeviceService", new DefenderEndpointDeviceService(moduleConfig, null!, null!, null!));
            SetProperty(Page, "Audit", audit);
            SetProperty(Page, "JS", Js);
            SetProperty(Page, "Progress", Progress);
            SetProperty(Page, "Logger", Logger);
            Progress.Begin(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ActivitySize>()).Returns(Handle);
            SetField(Page, "currentUser", "EXAMPLE\\operator");
            SetField(Page, "clientIpAddress", "192.0.2.5");
            SetField(Page, "authChecked", true);
        }

        public string AllLogText() => string.Join("\n", Directory.GetFiles(root, "*.jsonl", SearchOption.AllDirectories).Select(File.ReadAllText));
        public JsonElement[] Audits() => Directory.GetFiles(root, "exchangeadmin_*.jsonl", SearchOption.AllDirectories)
            .Where(path => !Path.GetFileNameWithoutExtension(path).EndsWith("_trace", StringComparison.Ordinal))
            .SelectMany(File.ReadAllLines).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        public void Dispose() => Directory.Delete(root, true);
    }
}
#pragma warning restore BL0006
