using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExchangeAdminWeb.Services;

public class JsonlLogService
{
    private readonly string _logFolder;
    private readonly string _rotationPeriod;
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
        var logRoot = config["Audit:LogRoot"] ?? @"E:\WWWOutput";
        _logFolder = Path.Combine(logRoot, "ExchangeAdminWeb");
        _rotationPeriod = config["Audit:RotationPeriod"]?.ToLowerInvariant() ?? "daily";

        Directory.CreateDirectory(_logFolder);
    }

    public void Write(Dictionary<string, object?> evt)
    {
        var filtered = evt.Where(kv => kv.Value != null).ToDictionary(kv => kv.Key, kv => kv.Value);
        var json = JsonSerializer.Serialize(filtered, JsonOpts);
        var logPath = Path.Combine(_logFolder, GetLogFilename());

        try
        {
            lock (_lock)
            {
                File.AppendAllText(logPath, json + "\n");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write JSONL event to {Path}", logPath);
        }
    }

    private string GetLogFilename()
    {
        var now = DateTime.Now;
        var suffix = _rotationPeriod switch
        {
            "weekly" => $"{now.Year}W{ISOWeek.GetWeekOfYear(now):D2}",
            "monthly" => now.ToString("yyyyMM"),
            _ => now.ToString("yyyyMMdd")
        };
        return $"exchangeadmin_{suffix}.jsonl";
    }
}