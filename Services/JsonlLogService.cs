using System.Globalization;
using System.Text;
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
                logPath = GetWritableLogPath(stream);
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
            ReportSafely(logger => logger.LogError(ex,
                "Failed to write a JSONL {Stream} event (action {Action}, category {Category}, operation {OperationId}, user {User}, ip {Ip}, target {Target}, ticket {Ticket}, result {Result}) to {Path}; the event was not written",
                stream, TextField(evt, "action"), TextField(evt, "category") ?? TextField(evt, "module"),
                TextField(evt, "operationId"), TextField(evt, "user"), TextField(evt, "ip"), TextField(evt, "target"),
                TextField(evt, "ticket"), TextField(evt, "result"), logPath));

            return outcome;
        }
    }

    internal Action<Stream, byte[]> WriteLineBytes { get; set; } = static (file, line) => file.Write(line);

    private void AppendLine(string logPath, string json, LogStream stream)
    {
        using var file = new FileStream(logPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);
        var start = file.Seek(0, SeekOrigin.End);
        var repair = start > 0 && !EndsWithNewline(file);
        var line = Encoding.UTF8.GetBytes(repair ? Environment.NewLine + json + Environment.NewLine : json + Environment.NewLine);

        try
        {
            WriteLineBytes(file, line);
        }
        catch (Exception)
        {
            TruncateTo(file, start, logPath, stream);
            throw;
        }

        if (repair)
        {
            ReportSafely(logger => logger.LogWarning(
                "The JSONL {Stream} file {Path} ended in a partial line, left by a write that did not finish; this event was started on a new line",
                stream, logPath));
        }
    }

    private static bool EndsWithNewline(FileStream file)
    {
        file.Seek(-1, SeekOrigin.End);
        return file.ReadByte() == '\n';
    }

    private void TruncateTo(FileStream file, long length, string logPath, LogStream stream)
    {
        try
        {
            file.SetLength(length);
        }
        catch (Exception ex)
        {
            ReportSafely(logger => logger.LogWarning(ex,
                "Could not remove the partial JSONL {Stream} line a failed write left in {Path}; the next event will start on a new line",
                stream, logPath));
        }
    }

    private void ReportSafely(Action<ILogger> report)
    {
        try
        {
            report(_logger);
        }
        catch (Exception)
        {
        }
    }

    // The name a failed record is logged under (aud-a28q): a field taken only when it is a string - the action, and
    // the category or, for a trace event, the module it is filed under. Never another field and never a value's
    // ToString: the payload can hold what the application log must not, and a ToString can itself throw.
    private static string? TextField(Dictionary<string, object?>? evt, string key) =>
        evt is not null && evt.TryGetValue(key, out var value) ? value as string : null;

    private string GetWritableLogPath(LogStream stream)
    {
        var activePath = Path.Combine(_logFolder, GetLogFilename(DateTime.Now, stream));
        if (!File.Exists(activePath) || new FileInfo(activePath).Length < _maxFileBytes)
            return activePath;

        RotateLogFiles(activePath);
        return activePath;
    }

    private void RotateLogFiles(string activePath)
    {
        var oldestPath = GetRotatedLogPath(activePath, _maxFilesPerPeriod - 1);
        if (File.Exists(oldestPath))
            File.Delete(oldestPath);

        for (var i = _maxFilesPerPeriod - 2; i >= 1; i--)
        {
            var source = GetRotatedLogPath(activePath, i);
            if (!File.Exists(source))
                continue;

            File.Move(source, GetRotatedLogPath(activePath, i + 1), overwrite: true);
        }

        File.Move(activePath, GetRotatedLogPath(activePath, 1), overwrite: true);
    }

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
