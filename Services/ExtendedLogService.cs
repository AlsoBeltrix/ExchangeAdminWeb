using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Serilog.Events;

namespace ExchangeAdminWeb.Services;

public class ExtendedLogService : IDisposable
{
    private const int DefaultMaxLines = 500;
    private const int MaxQueuedWrites = 1024;
    private readonly string _logFolder;
    private readonly ILogger<ExtendedLogService> _logger;
    private readonly Channel<PendingLogEntry> _writeQueue;
    private readonly Task _writerTask;
    private volatile LogEventLevel _minimumLevel = LogEventLevel.Fatal;
    private readonly string _configFilePath;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ExtendedLogService(IConfiguration config, IWebHostEnvironment env, ILogger<ExtendedLogService> logger)
    {
        _logger = logger;
        var logRoot = config["Audit:LogRoot"] ?? @"E:\WWWOutput";
        _logFolder = Path.Combine(logRoot, "ExchangeAdminWeb");
        _configFilePath = Path.Combine(env.ContentRootPath, "config", "extended-log-level.txt");
        _writeQueue = Channel.CreateBounded<PendingLogEntry>(new BoundedChannelOptions(MaxQueuedWrites)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        _writerTask = Task.Run(ProcessQueueAsync);
        Directory.CreateDirectory(_logFolder);
        LoadLevel();
    }

    public string CurrentLevel => _minimumLevel == LogEventLevel.Fatal ? "None" : _minimumLevel.ToString();

    public bool IsEnabled => _minimumLevel != LogEventLevel.Fatal;

    public bool IsEnabledFor(LogEventLevel level) => level >= _minimumLevel;

    public void SetLevel(string level)
    {
        _minimumLevel = level.ToLowerInvariant() switch
        {
            "debug" or "verbose" => LogEventLevel.Debug,
            "info" or "information" => LogEventLevel.Information,
            "warn" or "warning" => LogEventLevel.Warning,
            "error" => LogEventLevel.Error,
            _ => LogEventLevel.Fatal
        };

        try
        {
            var dir = Path.GetDirectoryName(_configFilePath)!;
            Directory.CreateDirectory(dir);
            using var stream = new FileStream(_configFilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(stream);
            writer.Write(level);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist extended log level");
        }
    }

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
        var logPath = Path.Combine(_logFolder, GetLogFilename());

        if (!_writeQueue.Writer.TryWrite(new PendingLogEntry(logPath, json)))
            _logger.LogWarning("Extended log queue is closed; dropping extended log entry");
    }

    public List<string> GetEntries(DateTime date, int maxLines = DefaultMaxLines)
        => GetEntries(date, date, maxLines);

    public List<string> GetEntries(DateTime startDate, DateTime endDate, int maxLines = DefaultMaxLines)
    {
        if (endDate.Date < startDate.Date)
            (startDate, endDate) = (endDate, startDate);

        var queue = new Queue<string>(maxLines);
        try
        {
            for (var date = startDate.Date; date <= endDate.Date; date = date.AddDays(1))
            {
                var logPath = Path.Combine(_logFolder, $"exchangeadmin_{date:yyyyMMdd}_extended.jsonl");
                if (!File.Exists(logPath)) continue;

                using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
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
            var logPath = Path.Combine(_logFolder, $"exchangeadmin_{date:yyyyMMdd}_extended.jsonl");
            if (File.Exists(logPath))
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
                Directory.CreateDirectory(Path.GetDirectoryName(entry.LogPath)!);
                await using var stream = new FileStream(entry.LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096, useAsync: true);
                await using var writer = new StreamWriter(stream);
                await writer.WriteLineAsync(entry.Json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write extended log entry");
            }
        }
    }

    private string GetLogFilename() => $"exchangeadmin_{DateTime.Now:yyyyMMdd}_extended.jsonl";

    private void LoadLevel()
    {
        if (File.Exists(_configFilePath))
        {
            try
            {
                using var stream = new FileStream(_configFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                var level = reader.ReadToEnd().Trim();
                SetLevel(level);
            }
            catch { }
        }
    }

    private sealed record PendingLogEntry(string LogPath, string Json);
}
