using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CsvHelper;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Progress;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Page = ExchangeAdminWeb.Components.Pages.AdminEventLog;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a26i: a settings save refused or failed across several sections is one record filed under the
/// save's own scope, naming the sections in <c>sections</c> (aud-a28h), and the Admin Event Log showed
/// and exported only its <c>section</c> - so an admin reviewing one section did not find it.
/// </summary>
/// <remarks>
/// The records are written by aud-a28h's own writers through the real audit writer and read back from
/// its JSONL; each line is parsed by the compiled page's ParseAuditLine, and the entries are exported by
/// its DownloadCsv, both invoked by reflection, the file captured from a recording JS runtime. The page
/// has no section or target filter, so the filter here is the one an admin applies to the target text:
/// the table's, or the CSV's Target column. Conditions are asserted, not wording.
/// </remarks>
public sealed class EventLogSectionsTargetTests : IDisposable
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string Admin = "EXAMPLE\\settings-admin";
    private const string Ip = "10.0.0.7";
    private const string Mailbox = "Module:MailboxPermissions";
    private const string Calendar = "Module:CalendarPermissions";
    private const string AccessAlias = "MailboxPermissionsOnPrem";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "eaw-eventlog-" + Guid.NewGuid().ToString("N"));
    private readonly AuditService _audit;

    public EventLogSectionsTargetTests()
    {
        Directory.CreateDirectory(_root);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Audit:LogRoot"] = _root,
            ["Audit:RotationPeriod"] = "daily",
        }).Build();
        var log = new JsonlLogService(config, NullLogger<JsonlLogService>.Instance);
        _audit = new AuditService(log, new OperationTraceService(config, log));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best effort */ }
    }

    /// <summary>
    /// The two aud-a28h multi-section saves - enablement refused, a module's section access failed - and,
    /// beside them, one section's own rows: a single-section refusal and a success.
    /// </summary>
    private List<object> WriteTheRecordsAndParseThem(Page page)
    {
        _audit.LogSettingsSaveRefused(NullLogger.Instance, Admin, Ip, "ModuleEnablement", "Authorization",
            "Only global admins can change module enablement.", [Mailbox, Calendar]);
        _audit.LogSettingsSaveFailed(NullLogger.Instance, Admin, Ip, "MailboxPermissions",
            "Failed to save section access: the store could not be written.", ["MailboxPermissions", AccessAlias]);
        _audit.LogSettingsSaveRefused(NullLogger.Instance, Admin, Ip, Mailbox, "Authorization",
            "Only global admins can change module enablement.");
        _audit.LogSettingsChange(Admin, Ip, Mailbox, [], ["S-1-5-21-1000-2000-3000-7777"]);

        var parse = typeof(Page).GetMethod("ParseAuditLine", PrivateInstance)!;
        var entries = AuditLines()
            .Select(line => parse.Invoke(page, [line, DateTime.MinValue, DateTime.MaxValue])
                ?? throw new InvalidOperationException("the page did not parse an audit line: " + line))
            .ToList();
        Assert.Equal(4, entries.Count);
        return entries;
    }

    /// <summary>Every audit line written so far, oldest first.</summary>
    private List<string> AuditLines()
    {
        var folder = Path.Combine(_root, "ExchangeAdminWeb");
        return Directory.GetFiles(folder, "exchangeadmin_*.jsonl")
            .Where(f => !Path.GetFileNameWithoutExtension(f).EndsWith("_trace", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .SelectMany(File.ReadAllLines)
            .Where(line => !string.IsNullOrWhiteSpace(line) && IsAudit(line))
            .ToList();
    }

    private static bool IsAudit(string line)
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.TryGetProperty("eventType", out var type) && type.GetString() == "audit";
    }

    private static string Read(object entry, string property) =>
        (string)entry.GetType().GetProperty(property)!.GetValue(entry)!;

    private static List<object> Filter(IEnumerable<object> entries, string text) =>
        entries.Where(e => Read(e, "FullTarget").Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();

    private static async Task<List<Dictionary<string, string>>> ExportCsv(Page page, IEnumerable<object> entries)
    {
        var js = new RecordingJs();
        using var progress = new ActivityProgressService();
        typeof(Page).GetProperty("JS", PrivateInstance)!.SetValue(page, js);
        typeof(Page).GetProperty("Progress", PrivateInstance)!.SetValue(page, progress);
        var shown = (IList)typeof(Page).GetField("filteredEvents", PrivateInstance)!.GetValue(page)!;
        foreach (var entry in entries)
            shown.Add(entry);

        await (Task)typeof(Page).GetMethod("DownloadCsv", PrivateInstance)!.Invoke(page, null)!;

        var transfer = Assert.Single(js.Delivered);
        using var reader = new CsvReader(
            new StringReader(Encoding.UTF8.GetString(Convert.FromBase64String((string)transfer[2]!))),
            CultureInfo.InvariantCulture);
        Assert.True(reader.Read());
        reader.ReadHeader();
        var rows = new List<Dictionary<string, string>>();
        while (reader.Read())
            rows.Add(reader.HeaderRecord!.ToDictionary(header => header, header => reader.GetField(header) ?? ""));
        return rows;
    }

    [Fact]
    public void AMultiSectionRecord_IsFoundByAFilterOnAnyOfItsSections()
    {
        var entries = WriteTheRecordsAndParseThem(new Page());

        // Only the enablement refusal names this section, and only in its "sections".
        var calendar = Assert.Single(Filter(entries, Calendar));
        Assert.Contains("ModuleEnablement", Read(calendar, "FullTarget"), StringComparison.Ordinal);
        Assert.Contains(Mailbox, Read(calendar, "FullTarget"), StringComparison.Ordinal);
        Assert.Contains(Calendar, Read(calendar, "Target"), StringComparison.Ordinal);
        Assert.Equal("Failed", Read(calendar, "Result"));

        // Found beside the section's own refusal and success, not instead of them.
        var mailbox = Filter(entries, Mailbox);
        Assert.Equal(3, mailbox.Count);
        Assert.Contains(calendar, mailbox);

        var access = Assert.Single(Filter(entries, AccessAlias));
        Assert.StartsWith("MailboxPermissions", Read(access, "FullTarget"), StringComparison.Ordinal);
        Assert.Equal("Failed", Read(access, "Result"));
    }

    [Fact]
    public async Task AMultiSectionRecord_CarriesItsSectionsIntoTheCsvTarget()
    {
        var page = new Page();
        var rows = await ExportCsv(page, WriteTheRecordsAndParseThem(page));

        Assert.Equal(4, rows.Count);
        var calendar = Assert.Single(rows, r => r["Target"].Contains(Calendar, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(Mailbox, calendar["Target"], StringComparison.Ordinal);
        Assert.Contains("ModuleEnablement", calendar["Target"], StringComparison.Ordinal);
        Assert.Equal("Failed", calendar["Result"]);
        Assert.Equal(3, rows.Count(r => r["Target"].Contains(Mailbox, StringComparison.OrdinalIgnoreCase)));
        Assert.Single(rows, r => r["Target"].Contains(AccessAlias, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ARecordThatListsNoSections_KeepsItsSectionAsItsTarget()
    {
        var page = new Page();
        var entries = WriteTheRecordsAndParseThem(page);

        // The control: the single-section refusal and the success read exactly as before.
        var own = entries.Where(e => Read(e, "FullTarget") == Mailbox).ToList();
        Assert.Equal(2, own.Count);
        Assert.All(own, e => Assert.Equal(Mailbox, Read(e, "Target")));
        Assert.Equal(["Failed", "Success"], own.Select(e => Read(e, "Result")).Order(StringComparer.Ordinal));

        var rows = await ExportCsv(page, entries);
        Assert.Equal(2, rows.Count(r => r["Target"] == Mailbox));
    }

    private sealed class RecordingJs : IJSRuntime
    {
        public List<object?[]> Delivered { get; } = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Assert.Equal("downloadFile", identifier);
            Delivered.Add(args!);
            return ValueTask.FromResult<TValue>(default!);
        }
    }
}
