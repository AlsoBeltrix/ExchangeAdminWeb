using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a56: a JSONL rotation that failed failed the write, so every row to a full file was dropped until a rotation
/// succeeded; the rotation's retries each deleted one more rotated file; and a log folder removed while the app ran
/// refused every row until a restart.
/// </summary>
/// <remarks>
/// A real writer over a temp root, at the smallest size limit it takes (1024 bytes). The program holding the full file is
/// the test itself, with a handle that shares reading and writing but not deleting, as a tailer or an editor opens one.
/// </remarks>
public sealed class JsonlWriterRotationTests : IDisposable
{
    private const string HeldOpen = "the full file held open without delete sharing";
    private const string NoDelete = "a folder that denies delete";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "eaw-a56-" + Guid.NewGuid().ToString("N"));
    private readonly CapturingLogger _log = new();

    public JsonlWriterRotationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    private string Folder => Path.Combine(_root, "ExchangeAdminWeb");

    [Theory]
    [InlineData("audit", HeldOpen)]
    [InlineData("trace", HeldOpen)]
    [InlineData("audit", NoDelete)]
    [InlineData("trace", NoDelete)]
    public void AFullFileThatCannotBeRotated_StillTakesEveryRow_AndTheFailureIsReportedOnce(string stream, string cause)
    {
        var audit = stream == "audit";
        var writer = Writer();
        var fillers = FillPastTheLimit(writer, audit);
        var active = ActiveFile(writer, audit);

        List<JsonlWriteOutcome> outcomes;
        using (Obstruct(cause, active))
            outcomes = new[] { "First", "Second", "Third" }.Select(action => Write(writer, audit, action)).ToList();

        Assert.All(outcomes, outcome => Assert.Equal(JsonlWriteOutcome.Written, outcome));
        Assert.Empty(_log.Errors);
        var warning = Assert.Single(_log.Warnings);
        Assert.Equal(active, warning.Value("Path"));
        Assert.NotNull(warning.Exception);
        Assert.False(File.Exists(RotatedFile(writer, audit, 1)), "the full file was rotated after all");

        var (rows, unparsed) = Rows(audit);
        Assert.Equal(0, unparsed);
        Assert.Equal([.. fillers, "First", "Second", "Third"], rows);
    }

    [Fact]
    public void AFailureStreak_EndsWhenARotationSucceeds_AndTheNextFailureIsReportedAgain()
    {
        var writer = Writer();
        FillPastTheLimit(writer, audit: true);
        var active = ActiveFile(writer, audit: true);

        using (Hold(active))
        {
            Assert.Equal(JsonlWriteOutcome.Written, Write(writer, audit: true, "HeldOnce"));
            Assert.Equal(JsonlWriteOutcome.Written, Write(writer, audit: true, "HeldTwice"));
        }

        Assert.Single(_log.Warnings);

        Assert.Equal(JsonlWriteOutcome.Written, Write(writer, audit: true, "Rotates"));
        Assert.True(File.Exists(RotatedFile(writer, audit: true, 1)), "the rotation did not succeed once the file was free");
        Assert.Single(_log.Warnings);

        FillPastTheLimit(writer, audit: true);
        using (Hold(active))
            Assert.Equal(JsonlWriteOutcome.Written, Write(writer, audit: true, "HeldAgain"));

        Assert.Equal(2, _log.Warnings.Count);
        Assert.Empty(_log.Errors);
    }

    [Fact]
    public void ARotationThatFailedAtItsLastMove_DeletesNothingMoreWhenItIsRetried()
    {
        // Two places (Audit:MaxFilesPerPeriod 3). Two rotations fill them, as they would over a busy day.
        var writer = Writer(maxFiles: 3);
        FillPastTheLimit(writer, audit: true);
        Assert.Equal(JsonlWriteOutcome.Written, Write(writer, audit: true, "FirstTurn"));
        var second = FillPastTheLimit(writer, audit: true);
        Assert.Equal(JsonlWriteOutcome.Written, Write(writer, audit: true, "SecondTurn"));
        var third = FillPastTheLimit(writer, audit: true);
        var active = ActiveFile(writer, audit: true);
        Assert.True(File.Exists(RotatedFile(writer, audit: true, 2)), "the setup did not fill both places");

        using (Hold(active))
        {
            foreach (var action in new[] { "HeldOne", "HeldTwo", "HeldThree" })
                Assert.Equal(JsonlWriteOutcome.Written, Write(writer, audit: true, action));
        }

        Assert.Equal(JsonlWriteOutcome.Written, Write(writer, audit: true, "Free"));

        // The first attempt dropped the oldest place's rows, as the rotation that completes would have; the retries
        // dropped nothing more.
        Assert.Equal(["FirstTurn", .. second], RowsIn(RotatedFile(writer, audit: true, 2)));
        Assert.Equal(["SecondTurn", .. third, "HeldOne", "HeldTwo", "HeldThree"], RowsIn(RotatedFile(writer, audit: true, 1)));
        Assert.Equal(["Free"], RowsIn(active));
        Assert.Single(_log.Warnings);
        Assert.Empty(_log.Errors);
    }

    [Theory]
    [InlineData("audit")]
    [InlineData("trace")]
    public void ALogFolderRemovedWhileTheAppRuns_IsCreatedAgain_AndTheNextRowIsWritten(string stream)
    {
        var audit = stream == "audit";
        var writer = Writer();
        Assert.Equal(JsonlWriteOutcome.Written, Write(writer, audit, "BeforeTheFolderWent"));

        DeleteFolder();
        Assert.False(Directory.Exists(Folder));

        Assert.Equal(JsonlWriteOutcome.Written, Write(writer, audit, "AfterTheFolderWent"));
        Assert.Empty(_log.Errors);
        var (rows, unparsed) = Rows(audit);
        Assert.Equal(0, unparsed);
        Assert.Equal(["AfterTheFolderWent"], rows);
    }

    [Fact]
    public void ALogFolderThatCannotBeCreated_IsTheFoldersRefusal_AtOnce_AndTheNextRowIsWrittenOnceItCanBe()
    {
        var writer = Writer();
        DeleteFolder();
        File.WriteAllBytes(Folder, []);

        var clock = Stopwatch.StartNew();
        var outcome = Write(writer, audit: true, "Refused");
        clock.Stop();

        Assert.Equal(JsonlWriteOutcome.FileWriteFailed, outcome);
        Assert.InRange(clock.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(2));
        var report = Assert.Single(_log.Errors);
        Assert.Equal("Refused", report.Value("Action"));
        var refused = Assert.IsType<JsonlLockRefusedException>(report.Exception);
        Assert.Equal(JsonlFileAppender.LockPathFor(ActiveFile(writer, audit: true)), refused.LockPath);
        Assert.IsAssignableFrom<IOException>(refused.InnerException);

        File.Delete(Folder);
        Assert.Equal(JsonlWriteOutcome.Written, Write(writer, audit: true, "Next"));
        Assert.Equal(["Next"], Rows(audit: true).Rows);
    }

    [Fact]
    public void ALogFolderThatCannotBeCreatedAtStartUp_StillBuildsTheWriter_AndEachRowIsTheFoldersRefusal()
    {
        // aud-a64. The constructor still created the folder, with no guard, after aud-a56 moved that into every write: a log
        // root the folder could not be made in at start-up stopped the writer being built, and every page and service that
        // audits with it, instead of each row being refused and reported.
        File.WriteAllBytes(Folder, []);

        JsonlLogService? writer = null;
        Assert.Null(Record.Exception(() => writer = Writer()));
        var warning = Assert.Single(_log.Warnings);
        Assert.Equal(Folder, warning.Value("Folder"));

        Assert.Equal(JsonlWriteOutcome.FileWriteFailed, Write(writer!, audit: true, "Refused"));
        var report = Assert.Single(_log.Errors);
        Assert.Equal("Refused", report.Value("Action"));
        Assert.IsType<JsonlLockRefusedException>(report.Exception);

        File.Delete(Folder);
        Assert.Equal(JsonlWriteOutcome.Written, Write(writer!, audit: true, "Next"));
        Assert.Equal(["Next"], Rows(audit: true).Rows);
    }

    // ---- aud-a65: a rotation that keeps failing is reported for as long as it lasts ----------------------------------

    [Fact]
    public void AFullFileThatStaysUnrotatable_IsReportedAgain_EachTimeItGrowsByAnotherLimit()
    {
        // The defect: one warning at the first failed write, then nothing while the file grew - for days, under a tailer
        // that held it - until the volume filled.
        var writer = Writer();
        FillPastTheLimit(writer, audit: true);
        var active = ActiveFile(writer, audit: true);

        var sizes = new List<long>();
        using (Hold(active))
        {
            while (_log.Warnings.Count < 3)
            {
                Assert.True(sizes.Count < 200, "the third report never came");
                sizes.Add(new FileInfo(active).Length);
                Assert.Equal(JsonlWriteOutcome.Written, Write(writer, audit: true, "Held-" + sizes.Count));
            }
        }

        // A report at the first failure, then at the first write that finds the file grown by another limit since the last
        // report. Each names the size that write found.
        var expected = new List<long> { sizes[0] };
        foreach (var size in sizes)
        {
            if (size - expected[^1] >= 1024)
                expected.Add(size);
        }

        var warnings = _log.Warnings;
        Assert.Equal(expected, warnings.Select(w => long.Parse(w.Value("Size")!, CultureInfo.InvariantCulture)));
        Assert.All(warnings, w => Assert.Equal(active, w.Value("Path")));
        Assert.All(warnings, w => Assert.Equal("1024", w.Value("Limit")));
        Assert.All(warnings, w => Assert.Equal(
            (long.Parse(w.Value("Size")!, CultureInfo.InvariantCulture) - 1024).ToString(CultureInfo.InvariantCulture), w.Value("Over")));
        Assert.All(warnings.Skip(1), w => Assert.Contains("still cannot be rotated", w.Text, StringComparison.Ordinal));
        Assert.Empty(_log.Errors);

        // And every row is still there.
        var (rows, unparsed) = Rows(audit: true);
        Assert.Equal(0, unparsed);
        Assert.Equal(Enumerable.Range(1, sizes.Count).Select(i => "Held-" + i), rows.Where(r => r.StartsWith("Held-", StringComparison.Ordinal)));
    }

    [Fact]
    public void AFullFileThatStaysUnrotatable_IsReportedAgain_OnceAnHourHasPassed()
    {
        var since = new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);
        var clock = new ManualClock(since);
        var writer = Writer();
        writer.Clock = clock;
        FillPastTheLimit(writer, audit: true);
        var active = ActiveFile(writer, audit: true);

        using (Hold(active))
        {
            Assert.Equal(JsonlWriteOutcome.Written, Write(writer, audit: true, "AtNine"));
            clock.Advance(TimeSpan.FromMinutes(59));
            Assert.Equal(JsonlWriteOutcome.Written, Write(writer, audit: true, "AMinuteShort"));
            Assert.Single(_log.Warnings);

            clock.Advance(TimeSpan.FromMinutes(2));
            Assert.Equal(JsonlWriteOutcome.Written, Write(writer, audit: true, "PastTheHour"));
        }

        Assert.Equal(2, _log.Warnings.Count);
        var again = _log.Warnings[1];
        Assert.Contains("still cannot be rotated", again.Text, StringComparison.Ordinal);
        Assert.Equal(active, again.Value("Path"));
        Assert.Contains(since.UtcDateTime.ToString("o", CultureInfo.InvariantCulture), again.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStreak_IsShownWhileItLasts_AndGoneOnceTheRotationSucceeds()
    {
        var since = new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);
        var writer = Writer();
        writer.Clock = new ManualClock(since);
        FillPastTheLimit(writer, audit: true);
        var active = ActiveFile(writer, audit: true);
        Assert.Empty(writer.RotationFailures);

        using (Hold(active))
        {
            var size = new FileInfo(active).Length;
            Assert.Equal(JsonlWriteOutcome.Written, Write(writer, audit: true, "Held"));

            var failure = Assert.Single(writer.RotationFailures);
            Assert.Equal("audit", failure.Log);
            Assert.Equal(active, failure.Path);
            Assert.Equal(since.UtcDateTime, failure.SinceUtc);
            Assert.Equal(size, failure.SizeBytes);
            Assert.Equal(1024, failure.LimitBytes);
            Assert.Equal(size - 1024, failure.OverBytes);
        }

        Assert.Equal(JsonlWriteOutcome.Written, Write(writer, audit: true, "Rotates"));
        Assert.True(File.Exists(RotatedFile(writer, audit: true, 1)), "the rotation did not succeed once the file was free");
        Assert.Empty(writer.RotationFailures);
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private JsonlLogService Writer(int maxFiles = 5)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Audit:LogRoot"] = _root,
            ["Audit:RotationPeriod"] = "daily",
            ["Audit:MaxFileBytes"] = "1024",
            ["Audit:MaxFilesPerPeriod"] = maxFiles.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }).Build();

        // A bound no test means to reach.
        return new JsonlLogService(config, _log) { LockWait = TimeSpan.FromSeconds(30) };
    }

    private static JsonlWriteOutcome Write(JsonlLogService writer, bool audit, string action)
    {
        var record = new Dictionary<string, object?>
        {
            ["eventType"] = audit ? "audit" : "operation.step",
            ["action"] = action,
            ["category"] = "RotationCategory",
        };
        return audit ? writer.WriteAudit(record) : writer.WriteTrace(record);
    }

    /// <summary>
    /// Writes rows until the stream's file has reached its limit, so the next write rotates it; none of them rotates it.
    /// Returns their actions.
    /// </summary>
    private static List<string> FillPastTheLimit(JsonlLogService writer, bool audit)
    {
        var active = ActiveFile(writer, audit);
        var written = new List<string>();
        while (!File.Exists(active) || new FileInfo(active).Length < 1024)
        {
            var action = "Filler-" + Guid.NewGuid().ToString("N");
            Assert.Equal(JsonlWriteOutcome.Written, Write(writer, audit, action));
            written.Add(action);
        }

        return written;
    }

    private static string ActiveFile(JsonlLogService writer, bool audit) =>
        (audit ? writer.GetAuditLogPaths(DateTime.Now) : writer.GetTraceLogPaths(DateTime.Now)).Last();

    private static string RotatedFile(JsonlLogService writer, bool audit, int place)
    {
        var active = ActiveFile(writer, audit);
        return Path.Combine(
            Path.GetDirectoryName(active)!,
            $"{Path.GetFileNameWithoutExtension(active)}.{place}{Path.GetExtension(active)}");
    }

    private static IDisposable Obstruct(string cause, string active) => cause switch
    {
        HeldOpen => Hold(active),
        NoDelete => JsonlWriterLockTests.DenyDeleting(Path.GetDirectoryName(active)!),
        _ => throw new ArgumentOutOfRangeException(nameof(cause), cause, null),
    };

    /// <summary>The file held open as a tailer holds it: reading and writing shared, deleting - and so moving - not.</summary>
    private static FileStream Hold(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

    private void DeleteFolder()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(Folder, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 50)
            {
                Thread.Sleep(20);
            }
        }
    }

    /// <summary>
    /// The stream's rows by action across every file of it in the folder, oldest place first, as the Admin Event Log reads
    /// them - each non-blank line parsed on its own - and how many lines did not parse.
    /// </summary>
    private (List<string> Rows, int Unparsed) Rows(bool audit)
    {
        var rows = new List<string>();
        var unparsed = 0;
        var files = Directory.GetFiles(Folder, "exchangeadmin_*.jsonl")
            .Where(f => IsTrace(f) != audit)
            .OrderByDescending(Place);
        foreach (var file in files)
        {
            var (fileRows, fileUnparsed) = Parse(file);
            rows.AddRange(fileRows);
            unparsed += fileUnparsed;
        }

        return (rows, unparsed);
    }

    private static List<string> RowsIn(string path)
    {
        Assert.True(File.Exists(path), $"{Path.GetFileName(path)} is not there");
        var (rows, unparsed) = Parse(path);
        Assert.Equal(0, unparsed);
        return rows;
    }

    private static (List<string> Rows, int Unparsed) Parse(string path)
    {
        var rows = new List<string>();
        var unparsed = 0;
        foreach (var line in File.ReadAllLines(path).Where(line => !string.IsNullOrWhiteSpace(line)))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                rows.Add(document.RootElement.GetProperty("action").GetString() ?? "");
            }
            catch (JsonException)
            {
                unparsed++;
            }
        }

        return (rows, unparsed);
    }

    private static bool IsTrace(string path) =>
        Path.GetFileNameWithoutExtension(path).Split('.')[0].EndsWith("_trace", StringComparison.OrdinalIgnoreCase);

    // The active file is place 0; exchangeadmin_<date>.<n>.jsonl is place n.
    private static int Place(string path)
    {
        var parts = Path.GetFileNameWithoutExtension(path).Split('.');
        return parts.Length > 1 && int.TryParse(parts[^1], out var place) ? place : 0;
    }

    internal sealed record LogEntry(LogLevel Level, string Text, Exception? Exception, IReadOnlyDictionary<string, object?> Values)
    {
        public string? Value(string name) => Values.TryGetValue(name, out var value) ? value?.ToString() : null;
    }

    /// <summary>A clock the test moves by hand (aud-a65): the writer's rotation streak reads it.</summary>
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class CapturingLogger : ILogger<JsonlLogService>
    {
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Errors
        {
            get { lock (_entries) return _entries.Where(e => e.Level >= LogLevel.Error).ToList(); }
        }

        public IReadOnlyList<LogEntry> Warnings
        {
            get { lock (_entries) return _entries.Where(e => e.Level == LogLevel.Warning).ToList(); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = (state as IEnumerable<KeyValuePair<string, object?>> ?? [])
                .GroupBy(kv => kv.Key)
                .ToDictionary(g => g.Key, g => g.First().Value);
            lock (_entries)
                _entries.Add(new LogEntry(logLevel, formatter(state, exception), exception, values));
        }
    }
}
