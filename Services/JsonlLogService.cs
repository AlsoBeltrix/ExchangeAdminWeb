using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExchangeAdminWeb.Services;

public class JsonlLogService
{
    private const int DefaultMaxFileMB = 50;
    private const int DefaultMaxFilesPerPeriod = 5;
    private readonly string _logFolder;
    private readonly string _rotationPeriod;
    private readonly long _maxFileBytes;
    private readonly int _maxFilesPerPeriod;
    private readonly ILogger<JsonlLogService> _logger;
    private readonly object _lock = new();

    // aud-a56. The streams whose failed rotation has been reported, until a write to them finds no rotation to do or makes
    // one: one report per failure streak, not one per row. Under _lock.
    private readonly HashSet<LogStream> _rotationFailureReported = [];
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public JsonlLogService(IConfiguration config, ILogger<JsonlLogService> logger)
    {
        _logger = logger;
        var logRoot = AuditLogRoot.Require(config);
        _logFolder = Path.Combine(logRoot, "ExchangeAdminWeb");
        _rotationPeriod = config["Audit:RotationPeriod"]?.ToLowerInvariant() ?? "daily";
        _maxFileBytes = GetMaxFileBytes(config);
        _maxFilesPerPeriod = Math.Clamp(config.GetValue<int?>("Audit:MaxFilesPerPeriod") ?? DefaultMaxFilesPerPeriod, 2, 50);

        Directory.CreateDirectory(_logFolder);
    }

    public JsonlWriteOutcome Write(Dictionary<string, object?> evt)
    {
        // aud-a28z. Routed by the eventType only when it is a string, as the catch names a record: its ToString ran
        // here, outside WriteToFile's try, so a value whose ToString threw escaped the writer. Anything else is filed as
        // audit, as the event log's parser files a record whose eventType it cannot read.
        var eventType = TextField(evt, "eventType");
        return eventType?.StartsWith("operation.", StringComparison.OrdinalIgnoreCase) == true
            ? WriteTrace(evt)
            : WriteAudit(evt);
    }

    public JsonlWriteOutcome WriteAudit(Dictionary<string, object?> evt) => WriteToFile(evt, LogStream.Audit);

    public JsonlWriteOutcome WriteTrace(Dictionary<string, object?> evt) => WriteToFile(evt, LogStream.Trace);

    public IEnumerable<string> GetAuditLogPaths(DateTime date)
    {
        var activePath = Path.Combine(_logFolder, GetLogFilename(date, LogStream.Audit));
        for (var i = _maxFilesPerPeriod - 1; i >= 1; i--)
            yield return GetRotatedLogPath(activePath, i);

        yield return activePath;
    }

    public IEnumerable<string> GetTraceLogPaths(DateTime date)
    {
        var activePath = Path.Combine(_logFolder, GetLogFilename(date, LogStream.Trace));
        for (var i = _maxFilesPerPeriod - 1; i >= 1; i--)
            yield return GetRotatedLogPath(activePath, i);

        yield return activePath;
    }

    // aud-a28q. An audit or trace write never throws into its caller. The serialisation ran before this try, so a
    // value System.Text.Json cannot write - a getter that throws, a cycle, a type it does not support - threw out of
    // the audit call and ended a handler with an exception instead of its outcome, the record lost; only the refusal
    // helpers' own catches hid it. Whatever stops a record is now logged here once, naming its action and category,
    // and the record is dropped whole, never half a line in the JSONL. The operation's outcome is its caller's
    // (Constitution, Auditing And Tracing: an audit failure is logged separately and never fails the operation).
    // aud-a28u. And the write says what became of the record. It returned nothing, so the audit service's
    // "AuditWritten" step read Success after a drop. A drop is judged by how far the write got - no line yet, the record
    // could not be serialised; a line, its file could not be written - never by the exception's type, which the
    // serialiser or the file system chooses.
    // aud-a28z (codex attr-batch-12, parts 3 and 4). "Never half a line" held for a record that could not be serialised,
    // not for one whose write failed part-way: that left a tail with no newline, the next record was appended onto it
    // and reported Written, and the event log, parsing line by line, dropped both. AppendLine takes such a line back out.
    // aud-a47. The lock below is this instance's only; the file's own lock, taken inside AppendLine, is every writer's -
    // the other instance under a shared log root included. A lock not taken in time is a FileWriteFailed like any other.
    // aud-a51. So is a lock file the folder refuses: dropped at once, its report saying the folder refused it, not after
    // the whole bound as if another writer held it.
    private JsonlWriteOutcome WriteToFile(Dictionary<string, object?> evt, LogStream stream)
    {
        var logPath = "(unresolved)";
        string? json = null;

        try
        {
            var filtered = evt.Where(kv => kv.Value != null).ToDictionary(kv => kv.Key, kv => kv.Value);
            json = JsonSerializer.Serialize(filtered, JsonOpts);

            lock (_lock)
            {
                logPath = ActivePath(stream);
                AppendLine(logPath, json, stream);
            }

            return JsonlWriteOutcome.Written;
        }
        catch (Exception ex)
        {
            var outcome = json is null ? JsonlWriteOutcome.NotSerializable : JsonlWriteOutcome.FileWriteFailed;

            // aud-a28u, folded in from codex attr-batch-11. Reporting the drop must not fail the write either: a logging
            // provider or formatter that throws took its exception out of the audit or trace call after the operation
            // had completed - aud-a28q's class, on its recovery path. Nothing is left to report that to, so it is
            // dropped, and the outcome still says the record was not written. This one catch is both streams': the
            // trace writer has no recovery path of its own.
            // aud-a28z part 4. The report says who did what - operation id, user, ip, target, ticket, result - because
            // when the trace is gone too it is all that is left to reconcile the missing row by. String fields only.
            ReportSafely(logger => logger.LogError(ex,
                "Failed to write a JSONL {Stream} event (action {Action}, category {Category}, operation {OperationId}, user {User}, ip {Ip}, target {Target}, ticket {Ticket}, result {Result}) to {Path}; the event was not written",
                stream, TextField(evt, "action"), TextField(evt, "category") ?? TextField(evt, "module"),
                TextField(evt, "operationId"), TextField(evt, "user"), TextField(evt, "ip"), TextField(evt, "target"),
                TextField(evt, "ticket"), TextField(evt, "result"), logPath));

            return outcome;
        }
    }

    /// <summary>
    /// Test seam (aud-a28z): how a line's bytes reach its file, so a test can fail a write part-way. Production writes
    /// them in one call.
    /// </summary>
    internal Action<Stream, byte[]> WriteLineBytes { get; set; } = static (file, line) => file.Write(line);

    /// <summary>Test seam (aud-a47): how long a write waits for another writer's lock on its file before it drops.</summary>
    internal TimeSpan LockWait { get; set; } = JsonlFileAppender.DefaultLockWait;

    /// <summary>
    /// Test seam (aud-a51): how long a refusal of the file's lock is retried before the write drops it as the folder's
    /// refusal.
    /// </summary>
    internal TimeSpan AccessRefusalGrace { get; set; } = JsonlFileAppender.DefaultAccessRefusalGrace;

    // aud-a28z part 3 and aud-a47: one record, one line or none, under the file's lock - see JsonlFileAppender. The two
    // reports are this writer's, word for word as they were before the append moved there.
    // aud-a56. A rotation that fails is reported once per failure streak of its stream, and the event is still written.
    private void AppendLine(string logPath, string json, LogStream stream) =>
        JsonlFileAppender.Append(
            logPath,
            json,
            LockWait,
            AccessRefusalGrace,
            rotateIfFull: () =>
            {
                RotateIfFull(logPath);
                _rotationFailureReported.Remove(stream);
            },
            rotationFailed: ex =>
            {
                if (_rotationFailureReported.Add(stream))
                {
                    ReportSafely(logger => logger.LogWarning(ex,
                        "Could not rotate the JSONL {Stream} file {Path}, which has reached its size limit: {Reason} The event was written to it, past the limit. Each write tries the rotation again; this is not reported again until a write finds the file rotated or under its limit",
                        stream, logPath, ex.Message));
                }
            },
            writeLineBytes: WriteLineBytes,
            repaired: () => ReportSafely(logger => logger.LogWarning(
                "The JSONL {Stream} file {Path} ended in a partial line, left by a write that did not finish; this event was started on a new line",
                stream, logPath)),
            truncationFailed: ex => ReportSafely(logger => logger.LogWarning(ex,
                "Could not remove the partial JSONL {Stream} line a failed write left in {Path}; the next event will start on a new line",
                stream, logPath)));

    // Every report the writer makes - the drop, a repair, a truncation that failed - goes through here (aud-a28u's guard,
    // one place since aud-a28z): a provider that throws while reporting must not fail the write.
    private void ReportSafely(Action<ILogger> report)
    {
        try
        {
            report(_logger);
        }
        catch (Exception)
        {
            // Nowhere left to say so.
        }
    }

    // A field a failed record is logged under, and the eventType it is routed by (aud-a28q, aud-a28z): taken only when it
    // is a string - the action, the category or a trace event's module, and since aud-a28z the operation id, user, ip,
    // target, ticket and result. Never the payload's other fields and never a value's ToString: the payload can hold
    // what the application log must not, and a ToString can itself throw.
    private static string? TextField(Dictionary<string, object?>? evt, string key) =>
        evt is not null && evt.TryGetValue(key, out var value) ? value as string : null;

    // The record's file, by date and stream: a name only, read from nothing on disk, so it is chosen before the lock.
    private string ActivePath(LogStream stream) => Path.Combine(_logFolder, GetLogFilename(DateTime.Now, stream));

    // Reads and changes the file system, so it runs under the file's lock (aud-a47): two writers that both found the file
    // full would otherwise both rotate it.
    private void RotateIfFull(string activePath)
    {
        if (!File.Exists(activePath) || new FileInfo(activePath).Length < _maxFileBytes)
            return;

        RotateLogFiles(activePath);
    }

    // aud-a56. Gap-aware, so a rotation retried after a failed last move deletes nothing - see JsonlFileAppender.Rotate.
    private void RotateLogFiles(string activePath) =>
        JsonlFileAppender.Rotate(activePath, _maxFilesPerPeriod - 1, index => GetRotatedLogPath(activePath, index));

    private static string GetRotatedLogPath(string activePath, int index)
    {
        var directory = Path.GetDirectoryName(activePath) ?? "";
        var name = Path.GetFileNameWithoutExtension(activePath);
        var extension = Path.GetExtension(activePath);
        return Path.Combine(directory, $"{name}.{index}{extension}");
    }

    private static long GetMaxFileBytes(IConfiguration config)
    {
        var explicitBytes = config.GetValue<long?>("Audit:MaxFileBytes");
        if (explicitBytes.HasValue)
            return Math.Max(1024, explicitBytes.Value);

        var maxFileMB = Math.Max(1, config.GetValue<int?>("Audit:MaxFileMB") ?? DefaultMaxFileMB);
        return maxFileMB * 1024L * 1024L;
    }

    private string GetLogFilename(DateTime date, LogStream stream)
    {
        var suffix = _rotationPeriod switch
        {
            "weekly" => $"{date.Year}W{ISOWeek.GetWeekOfYear(date):D2}",
            "monthly" => date.ToString("yyyyMM"),
            _ => date.ToString("yyyyMMdd")
        };
        return stream == LogStream.Trace
            ? $"exchangeadmin_{suffix}_trace.jsonl"
            : $"exchangeadmin_{suffix}.jsonl";
    }

    private enum LogStream
    {
        Audit,
        Trace
    }
}

/// <summary>
/// What became of a record handed to the JSONL writer (aud-a28u): written, or dropped and why. The writer logs a drop
/// itself and never throws; this tells its caller, so the audit service's trace can say whether its row exists.
/// </summary>
public enum JsonlWriteOutcome
{
    /// <summary>The record is a line in its log file.</summary>
    Written,

    /// <summary>Dropped before any file was touched: the record could not be serialised.</summary>
    NotSerializable,

    /// <summary>Dropped: the record serialised, but its log file could not be chosen, rotated, opened or written.</summary>
    FileWriteFailed
}
