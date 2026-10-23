using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Page = ExchangeAdminWeb.Components.Pages.AdminEventLog;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a47: two app instances writing one JSONL file could overwrite or truncate each other's rows. Dev and prod share
/// one log root by default, so both append to the same daily audit and trace files; the writer's lock was one
/// instance's monitor, so another instance could write at an offset that was stale by then, or take back a failed line
/// over a row the other had just reported Written. Every writer now takes the file's lock - a lock file beside it,
/// opened exclusively - before it looks at the file.
/// </summary>
/// <remarks>
/// Two real writers over one temp folder stand in for the two instances: each has its own in-process lock, so only the
/// file's lock stands between them, and the file system refuses a second exclusive handle whichever process asks. Where
/// the other instance is the test itself, it holds the lock with a raw handle, as any other process can.
/// </remarks>
public sealed class JsonlWriterLockTests : IDisposable
{
    private const string WaiterWrites = "a waiting writer that then writes";
    private const string WaiterFailsHalfWritten = "a waiting writer that then fails half-written";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "eaw-a47-" + Guid.NewGuid().ToString("N"));
    private readonly CapturingLogger _logA = new();
    private readonly CapturingLogger _logB = new();
    private readonly JsonlLogService _a;
    private readonly JsonlLogService _b;

    public JsonlWriterLockTests()
    {
        Directory.CreateDirectory(_root);
        _a = Writer(_logA);
        _b = Writer(_logB);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    [Theory]
    [InlineData("audit")]
    [InlineData("trace")]
    public async Task TwoWriters_OneFailingHalfWrittenWhileTheOtherWaits_LeaveExactlyTheRowsReportedWritten(string stream)
    {
        // The truncation half of the defect: A's take-back cut B's row, which B had reported Written.
        var audit = stream == "audit";
        var cancel = TestContext.Current.CancellationToken;
        Assert.Equal(JsonlWriteOutcome.Written, Write(_a, audit, "Before"));

        Task<JsonlWriteOutcome>? other = null;
        var otherFinishedWhileAHeldTheFile = false;
        _a.WriteLineBytes = (file, line) =>
        {
            file.Write(line, 0, line.Length / 2);
            var started = Task.Run(() => Write(_b, audit, "Other"), cancel);
            other = started;
            otherFinishedWhileAHeldTheFile = SpinWait.SpinUntil(() => started.IsCompleted, TimeSpan.FromMilliseconds(500));
            throw new IOException("the disk is full");
        };

        var lost = Write(_a, audit, "Lost");

        Assert.NotNull(other);
        Assert.True(await Finishes(other), "the other writer never finished");
        Assert.Equal(JsonlWriteOutcome.FileWriteFailed, lost);
        Assert.Equal(JsonlWriteOutcome.Written, await other);
        var (rows, unparsed) = Read(audit);
        Assert.Equal(0, unparsed);
        Assert.Equal(["Before", "Other"], rows);
        Assert.False(otherFinishedWhileAHeldTheFile, "the other writer wrote while the first held the file");
    }

    [Theory]
    [InlineData(WaiterWrites)]
    [InlineData(WaiterFailsHalfWritten)]
    public async Task ARowThatLandsWhileAWriterWaitsForTheLock_IsNeitherOverwrittenNorTakenBack(string shape)
    {
        // The stale-offset half: a writer that read its offset before it held the file wrote over, or took back, a row
        // the other instance appended in between. The test is the other instance here.
        var cancel = TestContext.Current.CancellationToken;
        var path = ActiveFile(audit: true);
        if (shape == WaiterFailsHalfWritten)
        {
            _a.WriteLineBytes = static (file, line) =>
            {
                file.Write(line, 0, line.Length / 2);
                throw new IOException("the disk is full");
            };
        }

        Task<JsonlWriteOutcome> waiter;
        using (HoldTheLock(audit: true))
        {
            waiter = Task.Run(() => Write(_a, audit: true, "Waiter"), cancel);
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancel);
            Assert.False(waiter.IsCompleted, "the writer did not wait for the file's lock");
            AppendRowAsTheOtherInstance(path, "OtherInstance");
        }

        Assert.True(await Finishes(waiter), "the waiting writer never finished");
        var outcome = await waiter;
        var (rows, unparsed) = Read(audit: true);
        Assert.Equal(0, unparsed);
        if (shape == WaiterWrites)
        {
            Assert.Equal(JsonlWriteOutcome.Written, outcome);
            Assert.Equal(["OtherInstance", "Waiter"], rows);
        }
        else
        {
            Assert.Equal(JsonlWriteOutcome.FileWriteFailed, outcome);
            Assert.Equal(["OtherInstance"], rows);
        }
    }

    [Fact]
    public void AWriterThatCannotTakeTheLockInItsBound_DropsTheRow_SaysTheLockWasTheCause_AndTheNextRowIsWritten()
    {
        // A live writer that hangs holding the lock - a dead one's handle the OS closes - must cost a bounded wait and a
        // dropped row that says why, never a spin that does not end and never a write without the lock.
        _a.LockWait = TimeSpan.FromMilliseconds(300);
        JsonlWriteOutcome outcome;
        var clock = Stopwatch.StartNew();
        using (HoldTheLock(audit: true))
            outcome = Write(_a, audit: true, "Dropped");
        clock.Stop();

        Assert.Equal(JsonlWriteOutcome.FileWriteFailed, outcome);
        Assert.InRange(clock.Elapsed, TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(10));
        Assert.Empty(Read(audit: true).Rows);
        var report = Assert.Single(_logA.Errors);
        Assert.Equal("Dropped", report.Value("Action"));
        var timeout = Assert.IsType<JsonlLockTimeoutException>(report.Exception);
        Assert.Equal(JsonlFileAppender.LockPathFor(ActiveFile(audit: true)), timeout.LockPath);
        Assert.Contains(timeout.LockPath, timeout.Message, StringComparison.Ordinal);

        Assert.Equal(JsonlWriteOutcome.Written, Write(_a, audit: true, "Next"));
        Assert.Equal(["Next"], Read(audit: true).Rows);
    }

    [Fact]
    public void TheLockIsNeitherNeededToReadNorReadAsALog()
    {
        // The readers - the Admin Event Log's audit and trace reads - open by name with full sharing and never take the
        // lock; and nothing that lists the folder's logs, or names a day's files, picks up a lock file.
        Assert.Equal(JsonlWriteOutcome.Written, Write(_a, audit: true, "Readable"));
        var path = ActiveFile(audit: true);
        Assert.False(File.Exists(JsonlFileAppender.LockPathFor(path)), "the lock outlived the write that took it");

        using (HoldTheLock(audit: true))
        {
            Assert.True(File.Exists(JsonlFileAppender.LockPathFor(path)));
            Assert.Contains(EventLogReadLinesShared(path), line => line.Contains("\"Readable\"", StringComparison.Ordinal));
            var folder = Path.GetDirectoryName(path)!;
            Assert.DoesNotContain(Directory.GetFiles(folder, "exchangeadmin_*.jsonl"), IsLockFile);
            Assert.DoesNotContain(Directory.GetFiles(folder, "*.jsonl"), IsLockFile);
            Assert.DoesNotContain(_a.GetAuditLogPaths(DateTime.Now).Concat(_a.GetTraceLogPaths(DateTime.Now)), IsLockFile);
        }
    }

    [Fact]
    public async Task TwoWritersOnSeveralThreads_WithInterleavedFailures_LeaveExactlyTheRowsReportedWritten()
    {
        var cancel = TestContext.Current.CancellationToken;
        var writesByA = 0;
        _a.WriteLineBytes = (file, line) =>
        {
            if (Interlocked.Increment(ref writesByA) % 5 == 0)
            {
                file.Write(line, 0, line.Length / 2);
                throw new IOException("the disk is full");
            }

            file.Write(line);
        };

        var reported = new ConcurrentBag<(string Action, JsonlWriteOutcome Outcome)>();
        var writers = new[] { ("A", _a), ("A", _a), ("A", _a), ("B", _b), ("B", _b), ("B", _b) };
        var threads = writers.Select((writer, thread) => Task.Run(() =>
        {
            for (var i = 0; i < 20; i++)
            {
                var action = $"{writer.Item1}{thread}-{i}";
                reported.Add((action, Write(writer.Item2, audit: true, action)));
            }
        }, cancel)).ToArray();

        Assert.True(await Finishes(Task.WhenAll(threads), TimeSpan.FromSeconds(120)), "the writers did not finish");
        var (rows, unparsed) = Read(audit: true);
        Assert.Equal(0, unparsed);
        Assert.Equal(12, reported.Count(r => r.Outcome == JsonlWriteOutcome.FileWriteFailed));
        Assert.Equal(
            reported.Where(r => r.Outcome == JsonlWriteOutcome.Written).Select(r => r.Action).Order(StringComparer.Ordinal),
            rows.Order(StringComparer.Ordinal));
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private JsonlLogService Writer(CapturingLogger logger)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Audit:LogRoot"] = _root,
            ["Audit:RotationPeriod"] = "daily",
        }).Build();

        // A bound no test means to reach, but for the one that sets its own.
        return new JsonlLogService(config, logger) { LockWait = TimeSpan.FromSeconds(30) };
    }

    private static JsonlWriteOutcome Write(JsonlLogService writer, bool audit, string action)
    {
        var record = new Dictionary<string, object?>
        {
            ["eventType"] = audit ? "audit" : "operation.step",
            ["action"] = action,
            ["category"] = "LockCategory",
        };
        return audit ? writer.WriteAudit(record) : writer.WriteTrace(record);
    }

    /// <summary>Whether <paramref name="task"/> finishes within <paramref name="within"/> (30 seconds by default).</summary>
    private static async Task<bool> Finishes(Task task, TimeSpan? within = null)
    {
        var limit = Task.Delay(within ?? TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        return await Task.WhenAny(task, limit) == task;
    }

    private string ActiveFile(bool audit) =>
        (audit ? _a.GetAuditLogPaths(DateTime.Now) : _a.GetTraceLogPaths(DateTime.Now)).Last();

    /// <summary>
    /// Holds the stream's lock as another instance's writer would - an exclusive handle on the lock file - for this
    /// minute's file and the next minute's, so a run across midnight still holds the one written to.
    /// </summary>
    private IDisposable HoldTheLock(bool audit)
    {
        var now = DateTime.Now;
        var handles = new[] { now, now.AddMinutes(1) }
            .Select(date => (audit ? _a.GetAuditLogPaths(date) : _a.GetTraceLogPaths(date)).Last())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new FileStream(JsonlFileAppender.LockPathFor(path), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            .ToList();
        return new HeldFiles(handles);
    }

    /// <summary>A row appended the way the other instance's writer appends one: a whole line at the end.</summary>
    private static void AppendRowAsTheOtherInstance(string path, string action)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["eventType"] = "audit",
            ["action"] = action,
            ["category"] = "LockCategory",
        });
        using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        file.Write(Encoding.UTF8.GetBytes(json + Environment.NewLine));
    }

    /// <summary>The Admin Event Log's own reader of an audit or trace file.</summary>
    private static List<string> EventLogReadLinesShared(string path) =>
        ((IEnumerable<string>)typeof(Page)
            .GetMethod("ReadLinesShared", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [path])!).ToList();

    private static bool IsLockFile(string path) => path.EndsWith(".lock", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The stream's rows by action, as the Admin Event Log reads them - each non-blank line parsed on its own, a line that
    /// does not parse dropped - and how many were dropped.
    /// </summary>
    private (List<string> Rows, int Unparsed) Read(bool audit)
    {
        var folder = Path.Combine(_root, "ExchangeAdminWeb");
        var rows = new List<string>();
        var unparsed = 0;
        var files = Directory.GetFiles(folder, "exchangeadmin_*.jsonl")
            .Where(f => Path.GetFileNameWithoutExtension(f).EndsWith("_trace", StringComparison.OrdinalIgnoreCase) != audit)
            .Order(StringComparer.Ordinal);
        foreach (var line in files.SelectMany(File.ReadAllLines).Where(line => !string.IsNullOrWhiteSpace(line)))
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

    private sealed class HeldFiles(List<FileStream> streams) : IDisposable
    {
        public void Dispose()
        {
            foreach (var stream in streams)
                stream.Dispose();
        }
    }

    internal sealed record LogEntry(LogLevel Level, string Text, Exception? Exception, IReadOnlyDictionary<string, object?> Values)
    {
        public string? Value(string name) => Values.TryGetValue(name, out var value) ? value?.ToString() : null;
    }

    private sealed class CapturingLogger : ILogger<JsonlLogService>
    {
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Errors
        {
            get { lock (_entries) return _entries.Where(e => e.Level >= LogLevel.Error).ToList(); }
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
