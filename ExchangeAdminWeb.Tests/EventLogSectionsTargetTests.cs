using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    // ---- aud-a28z part 5, folded in from aud-a23l: a check that could not decide is no ineligibility ---------------

    [Theory]
    [InlineData("Undetermined")]
    [InlineData("Error")]
    public void AnEligibilityCheckThatDidNotDecide_IsFiledUndetermined_AndTheResultFilterFindsItThere(string status)
    {
        // Every status but Eligible was filed under "Ineligible": a check whose read failed (aud-a23h's Undetermined),
        // or that threw (the page's "Error"), read in the log as a verdict against the mailbox, and the Result filter had
        // nowhere else to look for it.
        _audit.LogMigrationCheck(Admin, Ip, "alex.morgan@example.test", status, "CHG0500", "the excluded-group check failed");
        var page = new Page();
        var parse = typeof(Page).GetMethod("ParseAuditLine", PrivateInstance)!;
        var entry = parse.Invoke(page, [Assert.Single(AuditLines()), DateTime.MinValue, DateTime.MaxValue])
            ?? throw new InvalidOperationException("the page did not parse the check's audit line");

        Assert.Equal("Undetermined", Read(entry, "Result"));

        ((IList)typeof(Page).GetField("allEvents", PrivateInstance)!.GetValue(page)!).Add(entry);
        Assert.Same(entry, Assert.Single(ShownUnderResult(page, "Undetermined")));
        Assert.Empty(ShownUnderResult(page, "Ineligible"));
        Assert.Same(entry, Assert.Single(ShownUnderResult(page, "")));
    }

    [Fact]
    public void TheResultFilter_OffersUndetermined()
    {
        // Tripwire, as the undo panel's close buttons are pinned: no host renders the page, so its markup is read, its
        // comments blanked. kd-71: and Cancelled, last.
        Assert.Equal(["", "Success", "Failed", "Ineligible", "Undetermined", "Cancelled"], ResultFilterOptions());
    }

    // ---- kd-71: every result the app files has an option in the Result filter ----------------------------------------

    /// <summary>
    /// Every result an audit row is filed under: aud-a70 (13)'s inventory (Success, Failed, Undetermined, Ineligible) and
    /// kd-57's Cancelled - with the names the code itself files, so a result added there without an option fails here too:
    /// every <see cref="AuditResult"/> name (what <c>filedAs</c> files) and <see cref="AuditService.CancelledResult"/> (what
    /// <see cref="AuditService.FiledCancelled"/> files).
    /// </summary>
    private static IReadOnlyList<string> ResultsTheAppFiles() =>
        new[] { "Success", "Failed", "Undetermined", "Ineligible", "Cancelled" }
            .Concat(Enum.GetNames<AuditResult>())
            .Append(AuditService.CancelledResult)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    [Fact]
    public void TheResultFilter_OffersEveryResultTheAppFiles()
    {
        // The kd-65 class, for this filter: aud-a28z part 5 added Undetermined with its option; kd-57 added Cancelled without
        // one, so a cancelled row could be found only under All. A result with no option is a row the filter cannot find.
        var options = ResultFilterOptions();

        Assert.All(ResultsTheAppFiles(), result => Assert.Contains(result, options));
    }

    [Fact]
    public void ACancelledRow_IsFoundUnderCancelled_AndUnderNoOtherResult()
    {
        _audit.LogLookupAction(Admin, Ip, "MessageTrace_DetailDownload", "<m1@example.test>", false, AuditService.CancelledByTheOperator,
            "CHG0502", AuditService.FiledCancelled());
        var page = new Page();
        var parse = typeof(Page).GetMethod("ParseAuditLine", PrivateInstance)!;
        var entry = parse.Invoke(page, [Assert.Single(AuditLines()), DateTime.MinValue, DateTime.MaxValue])
            ?? throw new InvalidOperationException("the page did not parse the cancel's audit line");

        Assert.Equal("Cancelled", Read(entry, "Result"));

        ((IList)typeof(Page).GetField("allEvents", PrivateInstance)!.GetValue(page)!).Add(entry);
        Assert.Same(entry, Assert.Single(ShownUnderResult(page, "Cancelled")));
        Assert.All(ResultFilterOptions().Where(option => option is not ("" or "Cancelled")),
            option => Assert.Empty(ShownUnderResult(page, option)));
        Assert.Same(entry, Assert.Single(ShownUnderResult(page, "")));
    }

    /// <summary>The Result filter's option values, read from the page's markup with its comments blanked (no host renders it).</summary>
    private static List<string> ResultFilterOptions()
    {
        var markup = RazorSyntax.CommentsBlanked(File.ReadAllText(
            AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "AdminEventLog.razor")));

        var select = Regex.Match(markup, @"<select[^>]*@bind=""selectedResult""[^>]*>(?<options>.*?)</select>", RegexOptions.Singleline);
        Assert.True(select.Success, "the Result filter's select was not found");
        return Regex.Matches(select.Groups["options"].Value, @"<option value=""(?<value>[^""]*)"">")
            .Select(m => m.Groups["value"].Value)
            .ToList();
    }

    // ---- aud-a73: a permission change whose outcome could not be confirmed is no failure either ----------------------------

    [Theory]
    [InlineData("MailboxPermission")]
    [InlineData("CalendarPermission")]
    public void APermissionChangeThatCouldNotBeConfirmed_IsFiledUndetermined_AndTheResultFilterFindsItThere(string category)
    {
        // The row of a single change whose write threw said "Whether the change was made could not be confirmed" and was
        // filed "Failed": the Undetermined filter did not find it, and a reviewer reconciling Exchange read a change that
        // may have applied as one that had not.
        const string banner = "Whether the change was made could not be confirmed: the write failed: the session was lost. Check the permissions before trying again.";
        if (category == "MailboxPermission")
            _audit.LogMailboxPermission(Admin, Ip, "AddFullAccess", "shared@example.test", "alex.morgan@example.test", "FullAccess",
                false, "CHG0501", errorDetail: banner, filedAs: AuditResult.Undetermined);
        else
            _audit.LogCalendarPermission(Admin, Ip, "SetCalendar", "shared@example.test", "alex.morgan@example.test", "Reviewer",
                false, "CHG0501", errorDetail: banner, filedAs: AuditResult.Undetermined);
        var page = new Page();
        var parse = typeof(Page).GetMethod("ParseAuditLine", PrivateInstance)!;
        var entry = parse.Invoke(page, [Assert.Single(AuditLines()), DateTime.MinValue, DateTime.MaxValue])
            ?? throw new InvalidOperationException("the page did not parse the change's audit line");

        Assert.Equal("Undetermined", Read(entry, "Result"));

        ((IList)typeof(Page).GetField("allEvents", PrivateInstance)!.GetValue(page)!).Add(entry);
        Assert.Same(entry, Assert.Single(ShownUnderResult(page, "Undetermined")));
        Assert.Empty(ShownUnderResult(page, "Failed"));
        Assert.Same(entry, Assert.Single(ShownUnderResult(page, "")));
    }

    /// <summary>The page's rows after its own ApplyFilters, with <paramref name="result"/> chosen in the Result filter.</summary>
    private static IList ShownUnderResult(Page page, string result)
    {
        typeof(Page).GetField("selectedResult", PrivateInstance)!.SetValue(page, result);
        typeof(Page).GetMethod("ApplyFilters", PrivateInstance)!.Invoke(page, null);
        return (IList)typeof(Page).GetField("filteredEvents", PrivateInstance)!.GetValue(page)!;
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
