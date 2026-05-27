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

        using var appendHandle = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        using var service = CreateService(temp.Path);

        var entries = service.GetEntries(DateTime.Today);

        Assert.Single(entries);
        Assert.Contains("first", entries[0]);
    }

    private static ExtendedLogService CreateService(string logRoot)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Audit:LogRoot"] = logRoot
            })
            .Build();

        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(Path.Combine(logRoot, "app"));

        return new ExtendedLogService(config, env, Substitute.For<ILogger<ExtendedLogService>>());
    }

    private static string GetTodayLogPath(string logRoot)
        => Path.Combine(logRoot, "ExchangeAdminWeb", $"exchangeadmin_{DateTime.Now:yyyyMMdd}_extended.jsonl");

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