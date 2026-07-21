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

    public void Write(Dictionary<string, object?> evt)
    {
        var eventType = evt.GetValueOrDefault("eventType")?.ToString();
        if (eventType?.StartsWith("operation.", StringComparison.OrdinalIgnoreCase) == true)
            WriteTrace(evt);
        else
            WriteAudit(evt);
    }

    public void WriteAudit(Dictionary<string, object?> evt) => WriteToFile(evt, LogStream.Audit);

    public void WriteTrace(Dictionary<string, object?> evt) => WriteToFile(evt, LogStream.Trace);

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

    private void WriteToFile(Dictionary<string, object?> evt, LogStream stream)
    {
        var filtered = evt.Where(kv => kv.Value != null).ToDictionary(kv => kv.Key, kv => kv.Value);
        var json = JsonSerializer.Serialize(filtered, JsonOpts);
        var logPath = "(unresolved)";

        try
        {
            lock (_lock)
            {
                logPath = GetWritableLogPath(stream);
                using var fileStream = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                using var writer = new StreamWriter(fileStream);
                writer.WriteLine(json);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write JSONL event to {Path}", logPath);
        }
    }

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
