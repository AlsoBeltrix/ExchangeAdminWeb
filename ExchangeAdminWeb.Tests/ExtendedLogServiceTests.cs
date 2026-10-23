using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Serilog.Events;

namespace ExchangeAdminWeb.Tests;

public class ExtendedLogServiceTests
{
    // kd-16, kd-22, kd-25. How long one side of a test here waits for the other: the test for the consumer - another
    // thread - to do something it will do (take an entry, count one, write them, drain), and a consumer the test holds
    // for the test to release it. Far above any stall a loaded host has shown (about 10 s; a full drain took 33 s), so only
    // a side that never does its part fails the wait; a healthy run returns the moment it has.
    private static readonly TimeSpan ConsumerWait = TimeSpan.FromMinutes(2);

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

        var entries = service.GetEntries(DateTime.Today).Entries;

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

        var entries = service.GetEntries(DateTime.Today, maxLines: 10).Entries;

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

    // --- Shared config database: cross-process cache freshness (SharedConfigDb-Plan AC4) ---

    [Fact]
    public void ExtendedLogService_MinimumLevel_FollowsAnOutOfBandWrite()
    {
        using var temp = new TempDirectory();
        var appDir = Path.Combine(temp.Path, "app");
        Directory.CreateDirectory(appDir);
        var store = TestConfigStore.Create(appDir);
        using var service = CreateService(temp.Path, settings: new ExchangeAdminWeb.Services.Storage.AppSettingRepository(store), appDir: appDir);
        var now = DateTime.UtcNow;
        service.ChangeWatcher.UtcNow = () => now;

        Assert.False(service.IsEnabledFor(LogEventLevel.Error));

        // "The other instance" sets the level through its own repository on the same database.
        new ExchangeAdminWeb.Services.Storage.AppSettingRepository(store).Set(ExtendedLogService.LevelSettingKey, "Error");

        // Inside the 2 s throttle the old level still applies.
        Assert.False(service.IsEnabledFor(LogEventLevel.Error));

        now = now.Add(ExchangeAdminWeb.Services.Storage.ConfigChangeWatcher.Throttle);
        Assert.True(service.IsEnabledFor(LogEventLevel.Error));
        Assert.Equal("Error", service.CurrentLevel);
    }

    // scd-2: the level never expires, so a write made between construction and the first check
    // would otherwise be missed until the next unrelated write.
    [Fact]
    public void ExtendedLogService_DetectsAChangeMadeBeforeItsFirstPoll()
    {
        using var temp = new TempDirectory();
        var appDir = Path.Combine(temp.Path, "app");
        Directory.CreateDirectory(appDir);
        var store = TestConfigStore.Create(appDir);
        using var service = CreateService(temp.Path, settings: new ExchangeAdminWeb.Services.Storage.AppSettingRepository(store), appDir: appDir);

        new ExchangeAdminWeb.Services.Storage.AppSettingRepository(store).Set(ExtendedLogService.LevelSettingKey, "Error");

        Assert.True(service.IsEnabledFor(LogEventLevel.Error));
    }

    // A reader's own save must not read as a foreign change (that would reload on every check
    // for the next two seconds), and must not stop a later foreign write being seen.
    [Fact]
    public void ExtendedLogService_OwnSetLevel_ThenForeignWrite_BothApply()
    {
        using var temp = new TempDirectory();
        var appDir = Path.Combine(temp.Path, "app");
        Directory.CreateDirectory(appDir);
        var store = TestConfigStore.Create(appDir);
        using var service = CreateService(temp.Path, settings: new ExchangeAdminWeb.Services.Storage.AppSettingRepository(store), appDir: appDir);
        var now = DateTime.UtcNow;
        service.ChangeWatcher.UtcNow = () => now;

        service.SetLevel("Warning");
        Assert.Equal("Warning", service.CurrentLevel);
        Assert.Equal("Warning", service.CurrentLevel);

        new ExchangeAdminWeb.Services.Storage.AppSettingRepository(store).Set(ExtendedLogService.LevelSettingKey, "Debug");

        now = now.Add(ExchangeAdminWeb.Services.Storage.ConfigChangeWatcher.Throttle);
        Assert.Equal("Debug", service.CurrentLevel);
    }

    // ---- aud-a28z: no logging failure escapes the extended log into its caller, or ends its consumer ---------------

    private const string DetailFactoryThrows = "a detail factory that throws";
    private const string QueueClosed = "a closed queue";
    private const string ChangeTokenUnreadable = "an unreadable change token";
    private const string LegacyFileUnreadable = "a legacy level file it cannot read";

    /// <summary>
    /// The aud-a28q tripwire's throwing-logger theory, for this service, by cause: each report the service makes is
    /// reached its own way, so each cause is induced as it happens - none takes a value the tripwire could hand it.
    /// </summary>
    [Theory]
    [InlineData(DetailFactoryThrows)]
    [InlineData(QueueClosed)]
    [InlineData(ChangeTokenUnreadable)]
    [InlineData(LegacyFileUnreadable)]
    public void ExtendedLogService_GivenALoggerThatThrows_StillReturns(string cause)
    {
        using var temp = new TempDirectory();
        var logger = new CapturingLogger();

        switch (cause)
        {
            case DetailFactoryThrows:
                {
                    // The report ran on the caller's thread, after the caller's own work had completed.
                    var service = CreateService(temp.Path, logger: logger);
                    service.SetLevel("Error");
                    logger.Throws = true;

                    var thrown = Record.Exception(() => service.Write(
                        LogEventLevel.Error, "an entry whose detail failed", "Test", () => throw new InvalidOperationException("no detail")));
                    service.Dispose();

                    Assert.Null(thrown);
                    Assert.Contains(logger.Entries, e => e.Text == "Failed to build extended log detail");
                    var line = Assert.Single(File.ReadAllLines(GetTodayLogPath(temp.Path)), l => l.Contains("an entry whose detail failed"));
                    Assert.DoesNotContain("\"detail\"", line);
                    break;
                }

            case QueueClosed:
                {
                    var service = CreateService(temp.Path, logger: logger);
                    service.SetLevel("Error");
                    service.Dispose();
                    logger.Throws = true;

                    var thrown = Record.Exception(() => service.Write(LogEventLevel.Error, "an entry after the queue closed", "Test"));

                    Assert.Null(thrown);
                    Assert.Equal(1, service.DroppedEntryCount);
                    Assert.Contains(logger.Entries, e => e.Text == "Extended log queue is closed; dropping extended log entry");
                    break;
                }

            case ChangeTokenUnreadable:
                {
                    // The change watcher reports an unreadable token through this service's own logger, from inside the
                    // level check Write makes first.
                    var store = Substitute.For<IConfigStore>();
                    var unreadable = false;
                    store.GetChangeToken().Returns(_ => unreadable ? throw new InvalidOperationException("the change token cannot be read") : 1L);
                    using var service = CreateService(temp.Path, settings: new AppSettingRepository(store), logger: logger);
                    unreadable = true;
                    logger.Throws = true;

                    var thrown = Record.Exception(() => service.Write(LogEventLevel.Error, "an entry while the store is unreadable", "Test"));

                    Assert.Null(thrown);
                    Assert.Null(Record.Exception(() => service.IsEnabledFor(LogEventLevel.Error)));
                    Assert.Null(Record.Exception(() => service.CurrentLevel));
                    Assert.Contains(logger.Entries, e => e.Text.StartsWith("Config change token could not be read", StringComparison.Ordinal));
                    break;
                }

            case LegacyFileUnreadable:
                {
                    // Reported from the constructor: a provider that threw there failed the service's construction.
                    var appDir = Path.Combine(temp.Path, "app");
                    Directory.CreateDirectory(Path.Combine(appDir, "config"));
                    var legacyPath = Path.Combine(appDir, "config", "extended-log-level.txt");
                    File.WriteAllText(legacyPath, "Error");
                    logger.Throws = true;

                    ExtendedLogService? service = null;
                    Exception? thrown;
                    using (new FileStream(legacyPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                        thrown = Record.Exception(() => service = CreateService(temp.Path, appDir: appDir, logger: logger));
                    service?.Dispose();

                    Assert.Null(thrown);
                    Assert.NotNull(service);
                    Assert.Contains(logger.Entries, e => e.Text == "Failed to import legacy extended log level file");
                    break;
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(cause), cause, null);
        }
    }

    [Fact]
    public void TheConsumer_GivenAnEntryItCannotWrite_AndALoggerThatThrows_DropsAndCountsIt_AndWritesTheNext()
    {
        // The report of a failed write is the one background consumer's: thrown out of its catch, it faulted the
        // consumer, and every later entry waited in the queue until the queue dropped it, with nothing to say so.
        using var temp = new TempDirectory();
        var logger = new CapturingLogger();
        var service = CreateService(temp.Path, logger: logger);
        service.SetLevel("Error");
        logger.Throws = true;

        using (HoldTodaysLogFile(temp.Path))
        {
            service.Write(LogEventLevel.Error, "an entry lost while its file is held", "Test");
            Assert.True(SpinWait.SpinUntil(() => service.DroppedEntryCount == 1, ConsumerWait),
                "the consumer never counted the entry it could not write");
        }

        service.Write(LogEventLevel.Error, "an entry written once the file is free", "Test");
        service.Dispose();

        var text = File.ReadAllText(GetTodayLogPath(temp.Path));
        Assert.Contains("an entry written once the file is free", text);
        Assert.DoesNotContain("an entry lost while its file is held", text);
        Assert.Equal(1, service.DroppedEntryCount);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Text == "Failed to write extended log entry");
    }

    [Fact]
    public void SetLevel_WhenTheStoreRefusesIt_AndTheLoggerThrows_ThrowsTheStoresFailure()
    {
        // The report before the rethrow could replace the store's exception with the provider's own.
        using var temp = new TempDirectory();
        var logger = new CapturingLogger();
        var store = Substitute.For<IConfigStore>();
        store.When(s => s.Write(Arg.Any<Action<SqliteConnection, SqliteTransaction>>()))
            .Do(_ => throw new InvalidOperationException("the store refused the level"));
        using var service = CreateService(temp.Path, settings: new AppSettingRepository(store), logger: logger);
        logger.Throws = true;

        var thrown = Record.Exception(() => service.SetLevel("Error"));

        Assert.Equal("the store refused the level", Assert.IsType<InvalidOperationException>(thrown).Message);
        Assert.Equal("None", service.CurrentLevel);
        Assert.Contains(logger.Entries, e => e.Text == "Failed to persist extended log level");
    }

    // ---- aud-a46: one line or none, under the file's lock; a read says what it could not read; every drop counted --

    [Fact]
    public void TheConsumer_GivenAnEntryThatFailsHalfWritten_TakesItBack_SoTheFileHoldsExactlyTheEntriesWritten()
    {
        // The consumer wrote through a StreamWriter over an append stream: a write that failed part-way left a tail with
        // no newline, the next entry was appended onto it, and the event log, parsing line by line, dropped both.
        using var temp = new TempDirectory();
        var service = CreateService(temp.Path);
        service.SetLevel("Error");
        service.WriteLineBytes = static (file, line) =>
        {
            if (Encoding.UTF8.GetString(line).Contains("\"lost\"", StringComparison.Ordinal))
            {
                file.Write(line, 0, line.Length / 2);
                throw new IOException("the disk is full");
            }

            file.Write(line);
        };

        foreach (var message in new[] { "before", "lost", "after", "after that" })
            service.Write(LogEventLevel.Error, message, "Test");
        service.Dispose();

        var (messages, unparsed) = ReadAsTheEventLogDoes(service);
        Assert.Equal(0, unparsed);
        Assert.Equal(["before", "after", "after that"], messages);
        Assert.Equal(1, service.DroppedEntryCount);
    }

    [Fact]
    public void TheConsumer_GivenATailADeadProcessLeft_StartsTheNextEntryOnItsOwnLine_AndReportsTheRepair()
    {
        using var temp = new TempDirectory();
        var logger = new CapturingLogger();
        var service = CreateService(temp.Path, logger: logger);
        service.SetLevel("Error");
        var path = GetTodayLogPath(temp.Path);
        File.WriteAllText(path, "{\"message\":\"earlier\"}" + Environment.NewLine + "{\"message\":\"cut off");

        service.Write(LogEventLevel.Error, "next", "Test");
        service.Dispose();

        var (messages, unparsed) = ReadAsTheEventLogDoes(service);
        Assert.Equal(["earlier", "next"], messages);
        Assert.Equal(1, unparsed);
        Assert.Equal(0, service.DroppedEntryCount);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Text.Contains(path, StringComparison.Ordinal));
    }

    [Fact]
    public void TheConsumer_TakesTheFilesLock_AndAWaitThatRunsOutIsACountedDrop()
    {
        // The other instance writes the same file under a shared log root (aud-a47): the consumer waits its bound for
        // the file's lock, and an entry it cannot lock for is dropped and counted, never written beside the other's.
        using var temp = new TempDirectory();
        var logger = new CapturingLogger();
        var service = CreateService(temp.Path, logger: logger);
        service.SetLevel("Error");
        service.LockWait = TimeSpan.FromMilliseconds(300);

        using (HoldTodaysLock(temp.Path))
        {
            service.Write(LogEventLevel.Error, "an entry while another writer holds the file", "Test");
            Assert.True(SpinWait.SpinUntil(() => service.DroppedEntryCount == 1, ConsumerWait),
                "the consumer never dropped the entry it could not take the lock for");
        }

        service.Write(LogEventLevel.Error, "an entry once the lock is free", "Test");
        service.Dispose();

        var (messages, _) = ReadAsTheEventLogDoes(service);
        Assert.Equal(["an entry once the lock is free"], messages);
        Assert.Equal(1, service.DroppedEntryCount);
        var report = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Equal("Failed to write extended log entry", report.Text);
        Assert.IsType<JsonlLockTimeoutException>(report.Exception);
    }

    [Fact]
    public void TheConsumer_InAFolderThatLetsItWriteButNotDelete_WritesEveryEntry()
    {
        // aud-a51. The shared appender opened its lock DeleteOnClose: where the folder grants create, read and write but
        // not delete, a lock file already there refused every open, and every entry waited out the bound and was dropped.
        using var temp = new TempDirectory();
        var logger = new CapturingLogger();
        var service = CreateService(temp.Path, logger: logger);
        service.SetLevel("Error");

        using (JsonlWriterLockTests.DenyDeleting(Path.Combine(temp.Path, "ExchangeAdminWeb")))
        {
            foreach (var lockPath in TodaysLockPaths(temp.Path))
                File.WriteAllBytes(lockPath, []);

            service.Write(LogEventLevel.Error, "first", "Test");
            service.Write(LogEventLevel.Error, "second", "Test");
            service.Dispose();
        }

        var (messages, unparsed) = ReadAsTheEventLogDoes(service);
        Assert.Equal(0, unparsed);
        Assert.Equal(["first", "second"], messages);
        Assert.Equal(0, service.DroppedEntryCount);
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public void TheConsumer_GivenAFullFileItCannotRotate_WritesEveryEntry_AndReportsItOnce()
    {
        // aud-a56. The shared appender let a rotation that threw fail the write, so every entry to a full file was dropped
        // and counted until a rotation succeeded. The full file here is held open as a tailer holds it: no delete sharing,
        // so it cannot be moved.
        using var temp = new TempDirectory();
        var logger = new CapturingLogger();
        var service = CreateService(temp.Path, new Dictionary<string, string?> { ["ExtendedLog:MaxFileBytes"] = "1024" }, logger: logger);
        service.SetLevel("Error");
        var logPath = GetTodayLogPath(temp.Path);
        var fillers = Enumerable.Range(1, 12).Select(i => $"filler {i:00}").ToList();
        File.WriteAllLines(logPath, fillers.Select(message => JsonSerializer.Serialize(new { message, pad = new string('x', 90) })));
        Assert.True(new FileInfo(logPath).Length >= 1024, "the setup did not fill the file");

        using (new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            service.Write(LogEventLevel.Error, "first", "Test");
            service.Write(LogEventLevel.Error, "second", "Test");
            service.Dispose();
        }

        var (messages, unparsed) = ReadAsTheEventLogDoes(service);
        Assert.Equal(0, unparsed);
        Assert.Equal([.. fillers, "first", "second"], messages);
        Assert.Equal(0, service.DroppedEntryCount);
        Assert.False(File.Exists(GetTodayRotatedLogPath(temp.Path, 1)), "the full file was rotated after all");
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains(logPath, warning.Text, StringComparison.Ordinal);
        Assert.NotNull(warning.Exception);
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public void TheConsumer_GivenAFullFileItCannotRotate_ReportsItAgainAsItGrows_AndShowsTheStreak()
    {
        // aud-a65. One warning at the first failed write, then nothing while the file grew past its limit, until the volume
        // filled; and the streak was shown nowhere. The clock stands still here, so only the file's growth re-reports.
        using var temp = new TempDirectory();
        var logger = new CapturingLogger();
        var service = CreateService(temp.Path, new Dictionary<string, string?> { ["ExtendedLog:MaxFileBytes"] = "1024" }, logger: logger);
        var since = new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);
        service.Clock = new FixedClock(since);
        service.SetLevel("Error");
        var logPath = GetTodayLogPath(temp.Path);
        var fillers = Enumerable.Range(1, 12).Select(i => $"filler {i:00}").ToList();
        File.WriteAllLines(logPath, fillers.Select(message => JsonSerializer.Serialize(new { message, pad = new string('x', 90) })));
        Assert.Null(service.RotationFailure);

        var held = Enumerable.Range(1, 30).Select(i => $"held {i:00} " + new string('y', 100)).ToList();
        LogRotationFailure? failure;
        using (new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            foreach (var message in held)
                service.Write(LogEventLevel.Error, message, "Test");

            Assert.True(
                SpinWait.SpinUntil(() => ReadAsTheEventLogDoes(service).Messages.Count == fillers.Count + held.Count, ConsumerWait),
                "the consumer did not write every held entry");
            failure = service.RotationFailure;
        }

        // Reported at the first failure, then at the first entry that found the file grown by another limit since the last
        // report: never sooner, and never later than one entry past it.
        var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        Assert.True(warnings.Count >= 3, $"{warnings.Count} warning(s) for a file that grew by more than three limits");
        var sizes = warnings.Select(w => long.Parse(Regex.Match(w.Text, @"it is (\d+) bytes").Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
        for (var i = 1; i < sizes.Count; i++)
        {
            Assert.InRange(sizes[i] - sizes[i - 1], 1024, 1024 + 400);
            Assert.Contains("still cannot be rotated", warnings[i].Text, StringComparison.Ordinal);
        }

        // Shown while it lasts: the log, the file, since when, its size and how far past its limit.
        Assert.NotNull(failure);
        Assert.Equal("extended", failure.Log);
        Assert.Equal(logPath, failure.Path);
        Assert.Equal(since.UtcDateTime, failure.SinceUtc);
        Assert.Equal(1024, failure.LimitBytes);
        Assert.Equal(failure.SizeBytes - 1024, failure.OverBytes);
        Assert.True(failure.SizeBytes >= sizes[^1], "the streak's size is older than its last report");

        // Gone once an entry finds the file free and rotates it; and nothing was dropped.
        service.Write(LogEventLevel.Error, "after the hold", "Test");
        service.Dispose();
        Assert.True(File.Exists(GetTodayRotatedLogPath(temp.Path, 1)), "the rotation did not succeed once the file was free");
        Assert.Null(service.RotationFailure);
        Assert.Equal(0, service.DroppedEntryCount);
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public void TheConsumer_GivenItsFolderRemoved_CreatesItAgain_AndWritesTheNextEntry()
    {
        // aud-a56. The appender creates the folder before every write now, for both writers; the consumer created it
        // itself, and moving that into the appender must not lose it.
        using var temp = new TempDirectory();
        var logger = new CapturingLogger();
        var service = CreateService(temp.Path, logger: logger);
        service.SetLevel("Error");
        service.Write(LogEventLevel.Error, "before the folder went", "Test");
        Assert.True(SpinWait.SpinUntil(() => ReadAsTheEventLogDoes(service).Messages.Count == 1, ConsumerWait),
            "the consumer never wrote the first entry");

        var folder = Path.Combine(temp.Path, "ExchangeAdminWeb");
        Assert.True(SpinWait.SpinUntil(() => TryDelete(folder), ConsumerWait), "the folder could not be removed");

        service.Write(LogEventLevel.Error, "after the folder went", "Test");
        service.Dispose();

        var (messages, unparsed) = ReadAsTheEventLogDoes(service);
        Assert.Equal(0, unparsed);
        Assert.Equal(["after the folder went"], messages);
        Assert.Equal(0, service.DroppedEntryCount);
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Error);

        static bool TryDelete(string path)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
        }
    }

    [Fact]
    public void GetEntries_WhenAFileCannotBeRead_SaysWhichAndWhy_AndKeepsTheLinesItCouldRead()
    {
        // A bare catch emptied the whole answer, so the event log showed "no diagnostics" for "could not be read".
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "ExchangeAdminWeb"));
        File.WriteAllText(GetTodayRotatedLogPath(temp.Path, 1), "{\"message\":\"older\"}" + Environment.NewLine);
        File.WriteAllText(GetTodayLogPath(temp.Path), "{\"message\":\"current\"}" + Environment.NewLine);
        using var service = CreateService(temp.Path, new Dictionary<string, string?>
        {
            ["ExtendedLog:MaxFilesPerDay"] = "2"
        });

        ExtendedLogRead read;
        using (new FileStream(GetTodayLogPath(temp.Path), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            read = service.GetEntries(DateTime.Today, maxLines: 10);

        Assert.Contains("older", Assert.Single(read.Entries));
        Assert.StartsWith(Path.GetFileName(GetTodayLogPath(temp.Path)) + ": ", Assert.Single(read.Unreadable), StringComparison.Ordinal);
    }

    [Fact]
    public void AFullQueue_CountsTheEntriesItDiscards()
    {
        // The queue discards its oldest entry when writes outpace the disk; nothing counted those.
        using var temp = new TempDirectory();
        var service = CreateService(temp.Path);
        service.SetLevel("Error");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        service.WriteLineBytes = (file, line) =>
        {
            if (!entered.IsSet)
            {
                entered.Set();
                release.Wait(ConsumerWait);
            }

            file.Write(line);
        };

        service.Write(LogEventLevel.Error, "held", "Test");
        Assert.True(entered.Wait(ConsumerWait, TestContext.Current.CancellationToken), "the consumer never took the first entry");
        for (var i = 0; i < 1024 + 5; i++)
            service.Write(LogEventLevel.Error, $"queued {i}", "Test");

        Assert.Equal(5, service.DroppedEntryCount);
        release.Set();

        // A deadline, not a count of sleeps (kd-25): each round also reads the entries back, which a count would add on top.
        var deadline = DateTime.UtcNow + ConsumerWait;
        var drained = false;
        while (!drained && DateTime.UtcNow < deadline)
        {
            drained = service.GetEntries(DateTime.Today, maxLines: 5000).Entries.Count == 1025;
            if (!drained)
                Thread.Sleep(100);
        }

        Assert.True(drained, "the queue never drained");
        service.Dispose();

        var (messages, unparsed) = ReadAsTheEventLogDoes(service);
        Assert.Equal(0, unparsed);
        Assert.Equal(1025, messages.Count);
        Assert.Equal(["held", "queued 5"], messages.Take(2));
        Assert.Equal("queued 1028", messages[^1]);
        Assert.Equal(5, service.DroppedEntryCount);
    }

    [Fact]
    public void Dispose_GivesTheQueueTimeInProportionToItsLength()
    {
        // aud-a49. Dispose waited a fixed 5 s, and a full queue needs about 11 s at the append cost aud-a46 measured: the
        // rest was neither written nor counted.
        //
        // kd-16. The bound for the 100 queued is 100 s - far above any drain a loaded host has shown (about 28 s, at 280 ms
        // an entry; at a 100 ms allowance that run dropped 64). Dispose returns the moment the writer is done, so a healthy
        // run pays nothing for it. The 20 ms sleep below keeps the drain at least 2 s - Thread.Sleep never returns early -
        // so it outlasts the floor (a fixed wait) and one entry's allowance (a wait that ignores the queue's length), and
        // both of those still fail this test however loaded the host is.
        using var temp = new TempDirectory();
        var service = CreateService(temp.Path);
        service.SetLevel("Error");
        service.DisposeFloor = TimeSpan.FromMilliseconds(100);
        service.DisposeAllowancePerEntry = TimeSpan.FromSeconds(1);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        service.WriteLineBytes = (file, line) =>
        {
            if (!entered.IsSet)
            {
                entered.Set();
                release.Wait(ConsumerWait);
            }
            else
            {
                Thread.Sleep(20);
            }

            file.Write(line);
        };

        service.Write(LogEventLevel.Error, "held", "Test");

        // A signal, not a timing: the wait only bounds a consumer that never starts, so it is far above a stall too (kd-16).
        Assert.True(entered.Wait(ConsumerWait, TestContext.Current.CancellationToken), "the consumer never took the first entry");
        for (var i = 0; i < 100; i++)
            service.Write(LogEventLevel.Error, $"queued {i}", "Test");

        release.Set();
        service.Dispose();

        Assert.Equal(0, service.DroppedEntryCount);
        var (messages, unparsed) = ReadAsTheEventLogDoes(service);
        Assert.Equal(0, unparsed);
        Assert.Equal(101, messages.Count);
    }

    [Fact]
    public void Dispose_CountsWhatItCouldNotWriteInItsBound_BeforeItReturns()
    {
        // aud-a49. What Dispose left in the queue when its wait ran out was dropped with the process and counted nowhere.
        using var temp = new TempDirectory();
        var logger = new CapturingLogger();
        var service = CreateService(temp.Path, logger: logger);
        service.SetLevel("Error");
        service.DisposeFloor = TimeSpan.Zero;
        service.DisposeAllowancePerEntry = TimeSpan.FromMilliseconds(1);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        service.WriteLineBytes = (file, line) =>
        {
            if (!entered.IsSet)
            {
                entered.Set();
                release.Wait(ConsumerWait);
            }

            file.Write(line);
        };

        try
        {
            service.Write(LogEventLevel.Error, "held", "Test");
            Assert.True(entered.Wait(ConsumerWait, TestContext.Current.CancellationToken), "the consumer never took the first entry");
            for (var i = 0; i < 50; i++)
                service.Write(LogEventLevel.Error, $"queued {i}", "Test");

            service.Dispose();

            Assert.Equal(50, service.DroppedEntryCount);
            var report = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
            Assert.Contains("50 entries", report.Text, StringComparison.Ordinal);
        }
        finally
        {
            release.Set();
        }

        Assert.True(SpinWait.SpinUntil(() => service.GetEntries(DateTime.Today).Entries.Count == 1, ConsumerWait),
            "the entry the consumer held was never written");
        var (messages, _) = ReadAsTheEventLogDoes(service);
        Assert.Equal(["held"], messages);
        Assert.Equal(50, service.DroppedEntryCount);
    }

    /// <summary>
    /// Today's entries as the Admin Event Log reads them - each line GetEntries returns parsed on its own, as
    /// ParseExtendedLine does, a line that does not parse dropped - by message, and how many were dropped.
    /// </summary>
    private static (List<string> Messages, int Unparsed) ReadAsTheEventLogDoes(ExtendedLogService service)
    {
        var messages = new List<string>();
        var unparsed = 0;
        foreach (var line in service.GetEntries(DateTime.Today, maxLines: 5000).Entries)
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                messages.Add(document.RootElement.GetProperty("message").GetString() ?? "");
            }
            catch (JsonException)
            {
                unparsed++;
            }
        }

        return (messages, unparsed);
    }

    /// <summary>Holds today's extended log lock - and the next minute's, across midnight - as another writer would.</summary>
    private static IDisposable HoldTodaysLock(string logRoot)
    {
        Directory.CreateDirectory(Path.Combine(logRoot, "ExchangeAdminWeb"));
        var now = DateTime.Now;
        var held = new[] { now, now.AddMinutes(1) }
            .Select(date => Path.Combine(logRoot, "ExchangeAdminWeb", $"exchangeadmin_{date:yyyyMMdd}_extended.jsonl"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => JsonlFileAppender.Lock(path, TimeSpan.FromSeconds(10), JsonlFileAppender.DefaultAccessRefusalGrace))
            .ToList();
        return new HeldFiles(held);
    }

    /// <summary>Today's extended log lock files - and the next minute's, across midnight.</summary>
    private static List<string> TodaysLockPaths(string logRoot)
    {
        var now = DateTime.Now;
        return new[] { now, now.AddMinutes(1) }
            .Select(date => JsonlFileAppender.LockPathFor(
                Path.Combine(logRoot, "ExchangeAdminWeb", $"exchangeadmin_{date:yyyyMMdd}_extended.jsonl")))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Holds today's extended log file - and the next minute's, across midnight - open with no sharing.</summary>
    private static IDisposable HoldTodaysLogFile(string logRoot)
    {
        Directory.CreateDirectory(Path.Combine(logRoot, "ExchangeAdminWeb"));
        var now = DateTime.Now;
        var held = new[] { now, now.AddMinutes(1) }
            .Select(date => Path.Combine(logRoot, "ExchangeAdminWeb", $"exchangeadmin_{date:yyyyMMdd}_extended.jsonl"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            .ToList();
        return new HeldFiles(held);
    }

    private sealed class HeldFiles(IEnumerable<IDisposable> held) : IDisposable
    {
        public void Dispose()
        {
            foreach (var item in held)
                item.Dispose();
        }
    }

    /// <summary>A clock that stands still (aud-a65): the consumer's rotation streak reads it.</summary>
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record LogEntry(LogLevel Level, string Text, Exception? Exception = null);

    /// <summary>Records every entry; when <see cref="Throws"/> is set, throws once it has, as a failing provider can.</summary>
    private sealed class CapturingLogger : ILogger<ExtendedLogService>
    {
        private readonly List<LogEntry> _entries = [];

        public bool Throws { get; set; }

        public IReadOnlyList<LogEntry> Entries
        {
            get { lock (_entries) return _entries.ToList(); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
                _entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));

            if (Throws)
                throw new InvalidOperationException("the logging provider failed");
        }
    }

    private static ExtendedLogService CreateService(string logRoot, Dictionary<string, string?>? additionalSettings = null,
        ExchangeAdminWeb.Services.Storage.AppSettingRepository? settings = null, string? appDir = null,
        ILogger<ExtendedLogService>? logger = null)
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

        return new ExtendedLogService(config, env, settings, logger ?? Substitute.For<ILogger<ExtendedLogService>>());
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
