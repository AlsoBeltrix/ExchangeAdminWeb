using ExchangeAdminWeb.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Serilog.Events;

namespace ExchangeAdminWeb.Tests;

public class ExtendedLogServiceTests
{
    [Fact]
    public void Write_DoesNotBuildDetail_WhenLevelDisabled()
    {
        using var temp = new TempDirectory();
        using var service = CreateService(temp.Path);
        var detailBuilt = false;

        service.Write(LogEventLevel.Error, "should not write", "Test", () =>
        {
            detailBuilt = true;
            return "secret detail";
        });

        Assert.False(detailBuilt);
        Assert.False(File.Exists(GetTodayLogPath(temp.Path)));
    }

    [Fact]
    public void GetEntries_ReadsLogFile_WhenAppendHandleIsOpen()
    {
        using var temp = new TempDirectory();
        var logFolder = Path.Combine(temp.Path, "ExchangeAdminWeb");
        Directory.CreateDirectory(logFolder);
        var logPath = GetTodayLogPath(temp.Path);
        File.WriteAllText(logPath, "{\"message\":\"first\"}" + Environment.NewLine);

        using var appendHandle = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        using var service = CreateService(temp.Path);

        var entries = service.GetEntries(DateTime.Today);

        Assert.Single(entries);
        Assert.Contains("first", entries[0]);
    }

    [Fact]
    public void GetEntries_ReadsRotatedLogFiles_InChronologicalOrder()
    {
        using var temp = new TempDirectory();
        var logFolder = Path.Combine(temp.Path, "ExchangeAdminWeb");
        Directory.CreateDirectory(logFolder);
        File.WriteAllText(GetTodayRotatedLogPath(temp.Path, 2), "{\"message\":\"oldest\"}" + Environment.NewLine);
        File.WriteAllText(GetTodayRotatedLogPath(temp.Path, 1), "{\"message\":\"older\"}" + Environment.NewLine);
        File.WriteAllText(GetTodayLogPath(temp.Path), "{\"message\":\"current\"}" + Environment.NewLine);
        using var service = CreateService(temp.Path, new Dictionary<string, string?>
        {
            ["ExtendedLog:MaxFilesPerDay"] = "3"
        });

        var entries = service.GetEntries(DateTime.Today, maxLines: 10);

        Assert.Equal(3, entries.Count);
        Assert.Contains("oldest", entries[0]);
        Assert.Contains("older", entries[1]);
        Assert.Contains("current", entries[2]);
        Assert.True(service.HasExtendedLogs(DateTime.Today));
    }

    [Fact]
    public void Write_RotatesActiveLog_WhenFileExceedsConfiguredLimit()
    {
        using var temp = new TempDirectory();
        var logFolder = Path.Combine(temp.Path, "ExchangeAdminWeb");
        Directory.CreateDirectory(logFolder);
        var logPath = GetTodayLogPath(temp.Path);
        File.WriteAllText(logPath, new string('x', 2048));
        var service = CreateService(temp.Path, new Dictionary<string, string?>
        {
            ["ExtendedLog:MaxFileBytes"] = "1024",
            ["ExtendedLog:MaxFilesPerDay"] = "3"
        });

        service.SetLevel("Error");
        service.Write(LogEventLevel.Error, "rotated write", "Test", () => "detail");
        service.Dispose();

        Assert.True(File.Exists(GetTodayRotatedLogPath(temp.Path, 1)));
        Assert.Contains("rotated write", File.ReadAllText(logPath));
        Assert.Equal(new string('x', 2048), File.ReadAllText(GetTodayRotatedLogPath(temp.Path, 1)));
    }

    [Fact]
    public void SetLevel_PersistsToDb_AndIsReloadedByNewInstance()
    {
        using var temp = new TempDirectory();
        var appDir = Path.Combine(temp.Path, "app");
        Directory.CreateDirectory(appDir);
        var settings = ExchangeAdminWeb.Tests.TestConfigStore.CreateAppSettings(appDir);

        using (var first = CreateService(temp.Path, settings: settings, appDir: appDir))
        {
            first.SetLevel("Error");
        }

        // A fresh instance over the same store must reload the persisted level.
        using var second = CreateService(temp.Path, settings: settings, appDir: appDir);
        Assert.True(second.IsEnabledFor(LogEventLevel.Error));
        Assert.False(second.IsEnabledFor(LogEventLevel.Information));
    }

    [Fact]
    public void Construction_ImportsLegacyLevelFile_ThenArchivesIt()
    {
        using var temp = new TempDirectory();
        var appDir = Path.Combine(temp.Path, "app");
        Directory.CreateDirectory(Path.Combine(appDir, "config"));
        var legacyPath = Path.Combine(appDir, "config", "extended-log-level.txt");
        File.WriteAllText(legacyPath, "Error");

        var settings = ExchangeAdminWeb.Tests.TestConfigStore.CreateAppSettings(appDir);
        using var service = CreateService(temp.Path, settings: settings, appDir: appDir);

        // Imported level took effect, and the legacy file was archived (no longer at its path).
        Assert.True(service.IsEnabledFor(LogEventLevel.Error));
        Assert.False(File.Exists(legacyPath));
        Assert.Single(Directory.GetFiles(Path.Combine(appDir, "config"), "extended-log-level.txt.imported-*"));
    }

    [Fact]
    public void Construction_DbValueWins_OverLegacyFile()
    {
        using var temp = new TempDirectory();
        var appDir = Path.Combine(temp.Path, "app");
        Directory.CreateDirectory(Path.Combine(appDir, "config"));
        var settings = ExchangeAdminWeb.Tests.TestConfigStore.CreateAppSettings(appDir);
        settings.Set("extended_log_level", "Error"); // DB already has a value

        File.WriteAllText(Path.Combine(appDir, "config", "extended-log-level.txt"), "Debug");

        using var service = CreateService(temp.Path, settings: settings, appDir: appDir);

        // The pre-existing DB value (Error) must win; the legacy file's Debug must NOT apply.
        Assert.True(service.IsEnabledFor(LogEventLevel.Error));
        Assert.False(service.IsEnabledFor(LogEventLevel.Debug));
    }

    private static ExtendedLogService CreateService(string logRoot, Dictionary<string, string?>? additionalSettings = null,
        ExchangeAdminWeb.Services.Storage.AppSettingRepository? settings = null, string? appDir = null)
    {
        var configValues = new Dictionary<string, string?>
        {
            ["Audit:LogRoot"] = logRoot
        };

        if (additionalSettings != null)
        {
            foreach (var setting in additionalSettings)
                configValues[setting.Key] = setting.Value;
        }

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        appDir ??= Path.Combine(logRoot, "app");
        Directory.CreateDirectory(appDir);
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(appDir);

        settings ??= TestConfigStore.CreateAppSettings(appDir);

        return new ExtendedLogService(config, env, settings, Substitute.For<ILogger<ExtendedLogService>>());
    }

    private static string GetTodayLogPath(string logRoot)
        => Path.Combine(logRoot, "ExchangeAdminWeb", $"exchangeadmin_{DateTime.Now:yyyyMMdd}_extended.jsonl");

    private static string GetTodayRotatedLogPath(string logRoot, int index)
        => Path.Combine(logRoot, "ExchangeAdminWeb", $"exchangeadmin_{DateTime.Now:yyyyMMdd}_extended.{index}.jsonl");

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ExchangeAdminWebTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }
}
