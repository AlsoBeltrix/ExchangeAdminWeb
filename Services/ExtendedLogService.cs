using System.Collections.Concurrent;
using System.Text.Json;
using Serilog.Events;

namespace ExchangeAdminWeb.Services;

public class ExtendedLogService
{
    private readonly string _logFolder;
    private readonly ILogger<ExtendedLogService> _logger;
    private readonly object _writeLock = new();
    private volatile LogEventLevel _minimumLevel = LogEventLevel.Fatal;
    private readonly string _configFilePath;

    public ExtendedLogService(IConfiguration config, IWebHostEnvironment env, ILogger<ExtendedLogService> logger)
    {
        _logger = logger;
        var logRoot = config["Audit:LogRoot"] ?? @"E:\WWWOutput";
        _logFolder = Path.Combine(logRoot, "ExchangeAdminWeb");
        _configFilePath = Path.Combine(env.ContentRootPath, "config", "extended-log-level.txt");
        Directory.CreateDirectory(_logFolder);
        LoadLevel();
    }

    public string CurrentLevel => _minimumLevel == LogEventLevel.Fatal ? "None" : _minimumLevel.ToString();

    public bool IsEnabled => _minimumLevel != LogEventLevel.Fatal;

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
            File.WriteAllText(_configFilePath, level);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist extended log level");
        }
    }

    public void Write(LogEventLevel level, string message, string? category = null, string? detail = null)
    {
        if (level < _minimumLevel) return;

        var entry = new
        {
            ts = DateTime.UtcNow.ToString("o"),
            level = level.ToString(),
            category,
            message,
            detail
        };

        var json = JsonSerializer.Serialize(entry, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        var logPath = Path.Combine(_logFolder, GetLogFilename());

        try
        {
            lock (_writeLock)
            {
                File.AppendAllText(logPath, json + "\n");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write extended log entry");
        }
    }

    public List<string> GetEntries(DateTime date, int maxLines = 500)
    {
        var logPath = Path.Combine(_logFolder, $"exchangeadmin_{date:yyyyMMdd}_extended.jsonl");
        if (!File.Exists(logPath)) return new();

        try
        {
            var lines = File.ReadAllLines(logPath);
            return lines.TakeLast(maxLines).ToList();
        }
        catch
        {
            return new();
        }
    }

    public bool HasExtendedLogs(DateTime date)
    {
        var logPath = Path.Combine(_logFolder, $"exchangeadmin_{date:yyyyMMdd}_extended.jsonl");
        return File.Exists(logPath);
    }

    private string GetLogFilename() => $"exchangeadmin_{DateTime.Now:yyyyMMdd}_extended.jsonl";

    private void LoadLevel()
    {
        if (File.Exists(_configFilePath))
        {
            try
            {
                var level = File.ReadAllText(_configFilePath).Trim();
                SetLevel(level);
            }
            catch { }
        }
    }
}
