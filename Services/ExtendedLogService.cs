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
    // aud-a28z. Entries that passed the level and were not written: a write the consumer could not make, an entry a
    // closed queue refused - and since aud-a46 an entry the full queue discarded. Incremented before the drop is
    // reported, so it holds when the report itself fails.
    private long _droppedEntries;
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
        // aud-a46. A full queue discards its oldest entry when writes outpace the disk, and nothing counted it. The
        // callback runs on the writer's thread, after the channel's own lock is released; it counts and never logs - a
        // discard is a burst, and a report per discarded entry would be a flood of its own.
        _writeQueue = Channel.CreateBounded<PendingLogEntry>(new BoundedChannelOptions(MaxQueuedWrites)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        }, _ => Interlocked.Increment(ref _droppedEntries));
        _writerTask = Task.Run(ProcessQueueAsync);
        Directory.CreateDirectory(_logFolder);
        ImportLegacyLevelIfPresent();
        LoadLevel();
    }

    public string CurrentLevel => MinimumLevel == LogEventLevel.Fatal ? "None" : MinimumLevel.ToString();

    public bool IsEnabled => MinimumLevel != LogEventLevel.Fatal;

    public bool IsEnabledFor(LogEventLevel level) => level >= MinimumLevel;

    /// <summary>
    /// How many entries passed the level but were not written since the service started (aud-a28z): the one count a
    /// health surface, or a later line, can show when the logging provider could not say so itself. It includes the full
    /// queue's discards (aud-a46), and the Admin Event Log shows it with the diagnostics.
    /// </summary>
    public long DroppedEntryCount => Interlocked.Read(ref _droppedEntries);

    /// <summary>Test seam: the watcher consulted before every read of the level.</summary>
    internal ConfigChangeWatcher ChangeWatcher => _changeWatcher;

    // The effective level: re-read from the store when the other instance has written since
    // this one loaded (throttled inside the watcher), else the field.
    private LogEventLevel MinimumLevel
    {
        get
        {
            try
            {
                if (_changeWatcher.HasChangedSince(_loadedToken))
                    LoadLevel();
            }
            catch (Exception)
            {
                // aud-a28z. The watcher reports an unreadable change token through this service's own logger, and a
                // provider that throws took its exception out of Write - and of every level check - into the caller.
                // A failed check serves the level in effect, as the watcher does for a failed read.
            }

            return _minimumLevel;
        }
    }

    /// <summary>
    /// Stores the extended-log level and applies it. Throws when it cannot be stored, leaving the level
    /// in effect unchanged (aud-a28k).
    /// </summary>
    /// <remarks>
    /// It used to apply the level first and swallow a store failure, so Admin Settings recorded and
    /// reported a level the store had not kept - in effect on this instance only until the next config
    /// write reloaded the stored one, and never on the other. Stored first, applied after, or neither.
    /// </remarks>
    public void SetLevel(string level)
    {
        try
        {
            _settings.Set(LevelSettingKey, level);
        }
        catch (Exception ex)
        {
            // aud-a28z. Guarded, so the exception rethrown is the store's: a provider that threw here replaced it.
            ReportSafely(logger => logger.LogError(ex, "Failed to persist extended log level"));
            throw;
        }

        _minimumLevel = ParseLevel(level);
        // Our own write bumped the token; re-read (token first) so the next check does not
        // count it as a foreign change.
        LoadLevel();
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
            // aud-a28z. On the caller's thread, after the caller's own work completed: a provider that threw here took
            // its exception into the caller (aud-a28q's class). The entry is still written, without its detail.
            ReportSafely(logger => logger.LogError(ex, "Failed to build extended log detail"));
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
        {
            // A closed queue (after Dispose) drops the entry: counted, and reported without the report's own failure
            // reaching the caller.
            Interlocked.Increment(ref _droppedEntries);
            ReportSafely(logger => logger.LogWarning("Extended log queue is closed; dropping extended log entry"));
        }
    }

    public ExtendedLogRead GetEntries(DateTime date, int maxLines = DefaultMaxLines)
        => GetEntries(date, date, maxLines);

    /// <summary>
    /// The newest <paramref name="maxLines"/> lines of the extended log over the dates, oldest first, and each file that
    /// could not be read, by name and reason (aud-a46). The readers never take the files' lock: they open with full
    /// sharing and repair nothing.
    /// </summary>
    /// <remarks>
    /// One bare catch around the whole read emptied the answer for one file it could not open, so the Admin Event Log
    /// showed "no diagnostics" where they could not be read, and every other file's lines went with it. Each file is now
    /// read on its own; one that fails is named and reported, and the read goes on. The lines it had read stay: they are
    /// whole lines.
    /// </remarks>
    public ExtendedLogRead GetEntries(DateTime startDate, DateTime endDate, int maxLines = DefaultMaxLines)
    {
        if (maxLines <= 0)
            return new ExtendedLogRead([], []);

        if (endDate.Date < startDate.Date)
            (startDate, endDate) = (endDate, startDate);

        var queue = new Queue<string>(maxLines);
        var unreadable = new List<string>();
        for (var date = startDate.Date; date <= endDate.Date; date = date.AddDays(1))
        {
            foreach (var logPath in GetLogPathsForDate(date))
            {
                if (!File.Exists(logPath)) continue;

                try
                {
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
                catch (Exception ex)
                {
                    unreadable.Add($"{Path.GetFileName(logPath)}: {ex.Message}");
                    ReportSafely(logger => logger.LogWarning(ex, "Could not read the extended log file {Path}", logPath));
                }
            }
        }

        return new ExtendedLogRead(queue.ToList(), unreadable);
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
            // Reached only if the consumer faulted, which since aud-a28z its catch prevents; guarded all the same.
            ReportSafely(logger => logger.LogError(ex, "Failed to flush extended log queue during shutdown"));
        }
    }

    private async Task ProcessQueueAsync()
    {
        await foreach (var entry in _writeQueue.Reader.ReadAllAsync())
        {
            try
            {
                // aud-a46. A StreamWriter over an append stream wrote here: a write that failed part-way left a cut line
                // the next entry was appended onto, which the event log dropped with it; and under a shared log root it
                // took no lock against the other instance (aud-a47). The JSONL writer's one-line append, under the file's
                // lock, writes each entry now. The folder is created before the lock, whose file lives in it.
                var logPath = ActivePath(entry.LogDate);
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                AppendLine(logPath, entry);
            }
            catch (Exception ex)
            {
                // aud-a28z. This is the one consumer: a report that threw out of this catch faulted it, and every later
                // entry waited in the queue until the queue dropped it, with nothing to say so. The entry is dropped and
                // counted, and the loop takes the next one whatever the report does.
                Interlocked.Increment(ref _droppedEntries);
                ReportSafely(logger => logger.LogError(ex, "Failed to write extended log entry"));
            }
        }
    }

    // aud-a28z. Every report this service makes goes through here, as the JSONL writer's do (aud-a28u): a logging
    // provider or formatter that throws while a failure is reported must not become a failure of its own - not in the
    // caller, not in the consumer, not in the constructor.
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

    private static long GetMaxFileBytes(IConfiguration config)
    {
        var explicitBytes = config.GetValue<long?>("ExtendedLog:MaxFileBytes");
        if (explicitBytes.HasValue)
            return Math.Max(1024, explicitBytes.Value);

        var maxFileMB = Math.Max(1, config.GetValue<int?>("ExtendedLog:MaxFileMB") ?? DefaultMaxFileMB);
        return maxFileMB * 1024L * 1024L;
    }

    /// <summary>
    /// Test seam (aud-a46): how an entry's bytes reach its file, so a test can fail a write part-way or hold the consumer.
    /// Production writes them in one call.
    /// </summary>
    internal Action<Stream, byte[]> WriteLineBytes { get; set; } = static (file, line) => file.Write(line);

    /// <summary>Test seam (aud-a46): how long the consumer waits for another writer's lock on its file before it drops.</summary>
    internal TimeSpan LockWait { get; set; } = JsonlFileAppender.DefaultLockWait;

    // One entry, one line or none, under the file's lock (aud-a28z part 3, aud-a47, shared since aud-a46). The two reports
    // are this service's own, guarded as all its reports are.
    private void AppendLine(string logPath, PendingLogEntry entry) =>
        JsonlFileAppender.Append(
            logPath,
            entry.Json,
            LockWait,
            JsonlFileAppender.DefaultAccessRefusalGrace,
            rotateIfFull: () => RotateIfFull(entry.LogDate, logPath),
            writeLineBytes: WriteLineBytes,
            repaired: () => ReportSafely(logger => logger.LogWarning(
                "The extended log file {Path} ended in a partial line, left by a write that did not finish; this entry was started on a new line",
                logPath)),
            truncationFailed: ex => ReportSafely(logger => logger.LogWarning(ex,
                "Could not remove the partial extended log line a failed write left in {Path}; the next entry will start on a new line",
                logPath)));

    // The entry's file, by its date: a name only, so it is chosen before the lock; the size check and the rotation read
    // and change the file system, so RotateIfFull runs under it.
    private string ActivePath(DateTime logDate) => Path.Combine(_logFolder, GetLogFilename(logDate));

    private void RotateIfFull(DateTime logDate, string activePath)
    {
        if (!File.Exists(activePath) || new FileInfo(activePath).Length < _maxFileBytes)
            return;

        RotateLogFiles(logDate, activePath);
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
            // Run by the constructor: a provider that threw here failed the service's construction (aud-a28z).
            ReportSafely(logger => logger.LogWarning(ex, "Failed to import legacy extended log level file"));
        }
    }

    private sealed record PendingLogEntry(DateTime LogDate, string Json);
}

/// <summary>
/// What a read of the extended log found (aud-a46): its lines, oldest first, and each file it could not read, as
/// "&lt;file name&gt;: &lt;reason&gt;". An empty <see cref="Unreadable"/> means every file there was, was read.
/// </summary>
public sealed record ExtendedLogRead(IReadOnlyList<string> Entries, IReadOnlyList<string> Unreadable);
