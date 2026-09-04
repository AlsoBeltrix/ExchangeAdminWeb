using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.Extensions.Configuration;
using Serilog.Events;

namespace ExchangeAdminWeb.Services;

public class ExtendedLogService : IDisposable
{
    private const int DefaultMaxLines = 500;
    private const int MaxQueuedWrites = 1024;
    private const int DefaultMaxFileMB = 10;
    private const int DefaultMaxFilesPerDay = 5;

    // app_setting key for the persisted extended-log level (replaces config/extended-log-level.txt).
    internal const string LevelSettingKey = "extended_log_level";

    private readonly string _logFolder;
    private readonly ILogger<ExtendedLogService> _logger;
    private readonly AppSettingRepository _settings;
    private readonly Channel<PendingLogEntry> _writeQueue;
    private readonly Task _writerTask;
    private readonly long _maxFileBytes;
    private readonly int _maxFilesPerDay;
    private volatile LogEventLevel _minimumLevel = LogEventLevel.Fatal;
    private readonly string _legacyConfigFilePath;

    // Cross-process freshness on the shared config database (docs/SharedConfigDb-Plan.md AC4):
    // the level never expires, so without this a level set on the other instance would never
    // reach this one. The watcher reads the token at most once per 2 s, so the per-log-line
    // cost of MinimumLevel is a clock read and a compare.
    private readonly ConfigChangeWatcher _changeWatcher;
    private long _loadedToken = ConfigChangeWatcher.Unknown;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ExtendedLogService(IConfiguration config, IWebHostEnvironment env, AppSettingRepository settings, ILogger<ExtendedLogService> logger)
    {
        _logger = logger;
        _settings = settings;
        _changeWatcher = settings.CreateChangeWatcher(logger);
        var logRoot = AuditLogRoot.Require(config);
        _logFolder = Path.Combine(logRoot, "ExchangeAdminWeb");
        _legacyConfigFilePath = Path.Combine(env.ContentRootPath, "config", "extended-log-level.txt");
        _maxFileBytes = GetMaxFileBytes(config);
        _maxFilesPerDay = Math.Clamp(config.GetValue<int?>("ExtendedLog:MaxFilesPerDay") ?? DefaultMaxFilesPerDay, 2, 50);
        _writeQueue = Channel.CreateBounded<PendingLogEntry>(new BoundedChannelOptions(MaxQueuedWrites)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        _writerTask = Task.Run(ProcessQueueAsync);
        Directory.CreateDirectory(_logFolder);
        ImportLegacyLevelIfPresent();
        LoadLevel();
    }

    public string CurrentLevel => MinimumLevel == LogEventLevel.Fatal ? "None" : MinimumLevel.ToString();

    public bool IsEnabled => MinimumLevel != LogEventLevel.Fatal;

    public bool IsEnabledFor(LogEventLevel level) => level >= MinimumLevel;

    /// <summary>Test seam: the watcher consulted before every read of the level.</summary>
    internal ConfigChangeWatcher ChangeWatcher => _changeWatcher;

    // The effective level: re-read from the store when the other instance has written since
    // this one loaded (throttled inside the watcher), else the field.
    private LogEventLevel MinimumLevel
    {
        get
        {
            if (_changeWatcher.HasChangedSince(_loadedToken))
                LoadLevel();
            return _minimumLevel;
        }
    }

    public void SetLevel(string level)
    {
        _minimumLevel = ParseLevel(level);

        try
        {
            _settings.Set(LevelSettingKey, level);
            // Our own write bumped the token; re-read (token first) so the next check does not
            // count it as a foreign change.
            LoadLevel();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist extended log level");
        }
    }

    private static LogEventLevel ParseLevel(string level) => level.ToLowerInvariant() switch
    {
        "debug" or "verbose" => LogEventLevel.Debug,
        "info" or "information" => LogEventLevel.Information,
        "warn" or "warning" => LogEventLevel.Warning,
        "error" => LogEventLevel.Error,
        _ => LogEventLevel.Fatal
    };

    public void Write(LogEventLevel level, string message, string? category = null, string? detail = null)
        => Write(level, message, category, detail is null ? null : () => detail);

    public void Write(LogEventLevel level, string message, string? category, Func<string?>? detailFactory)
    {
        if (!IsEnabledFor(level)) return;

        string? detail = null;
        try
        {
            detail = detailFactory?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to build extended log detail");
        }

        var entry = new
        {
            ts = DateTime.UtcNow.ToString("o"),
            level = level.ToString(),
            category,
            message,
            detail
        };

        var json = JsonSerializer.Serialize(entry, JsonOptions);

        if (!_writeQueue.Writer.TryWrite(new PendingLogEntry(DateTime.Now.Date, json)))
            _logger.LogWarning("Extended log queue is closed; dropping extended log entry");
    }

    public List<string> GetEntries(DateTime date, int maxLines = DefaultMaxLines)
        => GetEntries(date, date, maxLines);

    public List<string> GetEntries(DateTime startDate, DateTime endDate, int maxLines = DefaultMaxLines)
    {
        if (maxLines <= 0)
            return new();

        if (endDate.Date < startDate.Date)
            (startDate, endDate) = (endDate, startDate);

        var queue = new Queue<string>(maxLines);
        try
        {
            for (var date = startDate.Date; date <= endDate.Date; date = date.AddDays(1))
            {
                foreach (var logPath in GetLogPathsForDate(date))
                {
                    if (!File.Exists(logPath)) continue;

                    using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);

                    while (reader.ReadLine() is { } line)
                    {
                        if (string.IsNullOrWhiteSpace(line))
                            continue;

                        if (queue.Count == maxLines)
                            queue.Dequeue();
                        queue.Enqueue(line);
                    }
                }
            }

            return queue.ToList();
        }
        catch
        {
            return new();
        }
    }

    public bool HasExtendedLogs(DateTime date)
        => HasExtendedLogs(date, date);

    public bool HasExtendedLogs(DateTime startDate, DateTime endDate)
    {
        if (endDate.Date < startDate.Date)
            (startDate, endDate) = (endDate, startDate);

        for (var date = startDate.Date; date <= endDate.Date; date = date.AddDays(1))
        {
            if (GetLogPathsForDate(date).Any(File.Exists))
                return true;
        }

        return false;
    }

    public void Dispose()
    {
        _writeQueue.Writer.TryComplete();
        try
        {
            _writerTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to flush extended log queue during shutdown");
        }
    }

    private async Task ProcessQueueAsync()
    {
        await foreach (var entry in _writeQueue.Reader.ReadAllAsync())
        {
            try
            {
                var logPath = GetWritableLogPath(entry.LogDate);
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                await using var stream = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
                await using var writer = new StreamWriter(stream);
                await writer.WriteLineAsync(entry.Json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write extended log entry");
            }
        }
    }

    private static long GetMaxFileBytes(IConfiguration config)
    {
        var explicitBytes = config.GetValue<long?>("ExtendedLog:MaxFileBytes");
        if (explicitBytes.HasValue)
            return Math.Max(1024, explicitBytes.Value);

        var maxFileMB = Math.Max(1, config.GetValue<int?>("ExtendedLog:MaxFileMB") ?? DefaultMaxFileMB);
        return maxFileMB * 1024L * 1024L;
    }

    private string GetWritableLogPath(DateTime logDate)
    {
        var activePath = Path.Combine(_logFolder, GetLogFilename(logDate));
        if (!File.Exists(activePath) || new FileInfo(activePath).Length < _maxFileBytes)
            return activePath;

        RotateLogFiles(logDate, activePath);
        return activePath;
    }

    private void RotateLogFiles(DateTime logDate, string activePath)
    {
        var oldestPath = GetRotatedLogPath(logDate, _maxFilesPerDay - 1);
        if (File.Exists(oldestPath))
            File.Delete(oldestPath);

        for (var i = _maxFilesPerDay - 2; i >= 1; i--)
        {
            var source = GetRotatedLogPath(logDate, i);
            if (!File.Exists(source))
                continue;

            File.Move(source, GetRotatedLogPath(logDate, i + 1), overwrite: true);
        }

        File.Move(activePath, GetRotatedLogPath(logDate, 1), overwrite: true);
    }

    private IEnumerable<string> GetLogPathsForDate(DateTime date)
    {
        for (var i = _maxFilesPerDay - 1; i >= 1; i--)
            yield return GetRotatedLogPath(date, i);

        yield return Path.Combine(_logFolder, GetLogFilename(date));
    }

    private static string GetLogFilename(DateTime date) => $"exchangeadmin_{date:yyyyMMdd}_extended.jsonl";

    private string GetRotatedLogPath(DateTime date, int index)
        => Path.Combine(_logFolder, $"exchangeadmin_{date:yyyyMMdd}_extended.{index}.jsonl");

    // Reads the persisted level WITHOUT writing it back (a write here would bump the token and
    // make every reload trigger the next one). Token read BEFORE the value (scd-2).
    private void LoadLevel()
    {
        try
        {
            var token = _changeWatcher.CurrentToken();
            var level = _settings.Get(LevelSettingKey);
            if (!string.IsNullOrWhiteSpace(level))
                _minimumLevel = ParseLevel(level.Trim());
            _loadedToken = token;
        }
        catch { }
    }

    // One-time import of the legacy config/extended-log-level.txt into app_setting, then archive
    // the file (SqliteConfigStore-Plan Section 4). Only writes if the DB has no level yet, so a value
    // already in the DB always wins over the (now stale) file.
    private void ImportLegacyLevelIfPresent()
    {
        try
        {
            if (!File.Exists(_legacyConfigFilePath))
                return;

            var level = File.ReadAllText(_legacyConfigFilePath).Trim();
            if (!string.IsNullOrWhiteSpace(level))
                _settings.SetIfMissing(LevelSettingKey, level);

            LegacyConfigImport.ArchiveFile(_legacyConfigFilePath, _logger);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to import legacy extended log level file");
        }
    }

    private sealed record PendingLogEntry(DateTime LogDate, string Json);
}
