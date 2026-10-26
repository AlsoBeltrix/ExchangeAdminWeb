using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

public class ModuleConfigServiceTests
{
    [Fact]
    public void SaveAndGet_RoundTripsValues()
    {
        using var temp = new TempDir();
        var service = CreateService(temp.Path);

        service.SaveModuleConfig("Migration", new Dictionary<string, string>
        {
            ["OnPremTargetDatabases"] = "db-a,db-b",
        });

        Assert.Equal("db-a,db-b", service.GetValue("Migration", "OnPremTargetDatabases"));
        Assert.Equal("db-a,db-b", service.GetModuleConfig("Migration")["OnPremTargetDatabases"]);
    }

    [Fact]
    public void GetValue_UnknownKey_ReturnsNull()
    {
        using var temp = new TempDir();
        var service = CreateService(temp.Path);

        Assert.Null(service.GetValue("Migration", "Nope"));
    }

    [Fact]
    public void SaveModuleConfig_OverwritesWholeModule()
    {
        using var temp = new TempDir();
        var service = CreateService(temp.Path);

        service.SaveModuleConfig("Migration", new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" });
        service.SaveModuleConfig("Migration", new Dictionary<string, string> { ["a"] = "9" });

        var config = service.GetModuleConfig("Migration");
        Assert.Equal("9", config["a"]);
        Assert.False(config.ContainsKey("b"));
    }

    [Fact]
    public void Key_IsCaseInsensitive()
    {
        using var temp = new TempDir();
        var service = CreateService(temp.Path);

        service.SaveModuleConfig("Migration", new Dictionary<string, string> { ["AppId"] = "x" });

        Assert.Equal("x", service.GetValue("Migration", "appid"));
    }

    [Fact]
    public void ConfigSaved_EventFires_OnSave()
    {
        using var temp = new TempDir();
        var service = CreateService(temp.Path);
        string? saved = null;
        service.ConfigSaved += id => saved = id;

        service.SaveModuleConfig("Migration", new Dictionary<string, string> { ["a"] = "1" });

        Assert.Equal("Migration", saved);
    }

    [Fact]
    public void HasModuleConfigFile_TrueOnlyAfterSave()
    {
        using var temp = new TempDir();
        var service = CreateService(temp.Path);

        Assert.False(service.HasModuleConfigFile("Migration"));
        service.SaveModuleConfig("Migration", new Dictionary<string, string> { ["a"] = "1" });
        Assert.True(service.HasModuleConfigFile("Migration"));
    }

    [Fact]
    public void HasModuleConfigFile_TrueAfterEmptySave_SuppressesFallback()
    {
        // Parity with the file world: an explicitly-saved EMPTY config still counts as
        // "configured" (presence marker), so consumers that suppress the appsettings fallback
        // when HasModuleConfigFile is true keep doing so. Without this, an empty save would
        // silently re-enable the legacy fallback (B.3 review finding).
        using var temp = new TempDir();
        var service = CreateService(temp.Path);

        service.SaveModuleConfig("Migration", new Dictionary<string, string>());

        Assert.True(service.HasModuleConfigFile("Migration"));
        Assert.Empty(service.GetModuleConfig("Migration"));
    }

    [Fact]
    public void IsModuleCorrupt_TrueWhenStoreUnreadable_ElseFalse()
    {
        using var temp = new TempDir();
        var healthy = CreateService(temp.Path);
        Assert.False(healthy.IsModuleCorrupt("Migration"));

        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(temp.Path);
        var broken = new ModuleConfigService(new ModuleCatalog(), env,
            new ModuleConfigRepository(new ThrowingStore()), Substitute.For<ILogger<ModuleConfigService>>());
        Assert.True(broken.IsModuleCorrupt("Migration"));
    }

    /// <summary>
    /// aud-a57: a failed read was reported through the service's logger with no guard, so a provider that threw took its
    /// exception into every caller of GetValue and GetModuleConfig instead of the empty config they answer when the store
    /// cannot be read.
    /// </summary>
    [Theory]
    [InlineData("GetValue")]
    [InlineData("GetModuleConfig")]
    public void AReadThroughAStoreThatFails_WithALoggerThatThrows_AnswersEmpty(string reader)
    {
        using var temp = new TempDir();
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(temp.Path);
        var logger = new ThrowingLogger();
        var service = new ModuleConfigService(new ModuleCatalog(), env, new ModuleConfigRepository(new ThrowingStore()), logger);

        switch (reader)
        {
            case "GetValue":
                string? value = "not read";
                Assert.Null(Record.Exception(() => value = service.GetValue("Migration", "OnPremTargetDatabases")));
                Assert.Null(value);
                break;

            case "GetModuleConfig":
                Dictionary<string, string>? config = null;
                Assert.Null(Record.Exception(() => config = service.GetModuleConfig("Migration")));
                Assert.NotNull(config);
                Assert.Empty(config);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(reader), reader, null);
        }

        Assert.Equal(1, logger.Attempts);
    }

    /// <summary>
    /// aud-a63: a committed write was reported through the service's logger with no guard, inside the lock and before
    /// ConfigSaved, so a provider that threw made a saved change look failed to its caller, and this instance's ConfigSaved
    /// subscribers never heard of it.
    /// </summary>
    [Theory]
    [InlineData("SaveModuleConfig")]
    [InlineData("UpdateModuleValues")]
    [InlineData("SaveChangedValues")]
    public void ACommittedWrite_WithALoggerThatThrows_IsReportedSaved_AndRaisesConfigSaved(string writer)
    {
        using var temp = new TempDir();
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(temp.Path);
        var logger = new ThrowingLogger();
        var service = new ModuleConfigService(new ModuleCatalog(), env, TestConfigStore.CreateModuleConfig(temp.Path), logger);
        var raised = new List<string>();
        service.ConfigSaved += raised.Add;
        var values = new Dictionary<string, string> { ["OnPremTargetDatabases"] = "db-a" };

        ModuleConfigUpdate? update = null;
        ModuleConfigSaveOutcome? outcome = null;
        var thrown = writer switch
        {
            "SaveModuleConfig" => Record.Exception(() => service.SaveModuleConfig("Migration", values)),
            "UpdateModuleValues" => Record.Exception(() => update = service.UpdateModuleValues("Migration", values)),
            "SaveChangedValues" => Record.Exception(() => outcome = service.SaveChangedValues("Migration", new Dictionary<string, string>(), values)),
            _ => throw new ArgumentOutOfRangeException(nameof(writer), writer, null),
        };

        Assert.Null(thrown);
        if (writer == "UpdateModuleValues")
            Assert.Equal("db-a", update!.After["OnPremTargetDatabases"]);
        if (writer == "SaveChangedValues")
            Assert.Equal(ModuleConfigSaveStatus.Saved, outcome!.Status);
        Assert.Equal(["Migration"], raised);
        Assert.Equal("db-a", service.GetValue("Migration", "OnPremTargetDatabases"));
        Assert.Equal(1, logger.Attempts);
    }

    /// <summary>
    /// aud-a64: the start-up repair of the renamed Graph secret key reported with no guard, and Program.cs runs it once,
    /// outside any try, so a provider that threw stopped the app starting - from a failed remap's report, or from the
    /// summary of one that worked.
    /// </summary>
    [Theory]
    [InlineData("a remap that writes")]
    [InlineData("a remap whose write fails")]
    public void TheGraphSecretKeyRemap_WithALoggerThatThrows_Returns(string cause)
    {
        using var temp = new TempDir();
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(temp.Path);
        var store = TestConfigStore.Create(temp.Path);
        new ModuleConfigRepository(store).SaveModule("MfaReset", new Dictionary<string, string> { ["DelineaSecretId"] = "123" });
        var writesFail = cause == "a remap whose write fails";
        var logger = new ThrowingLogger();
        var service = new ModuleConfigService(new ModuleCatalog(), env,
            new ModuleConfigRepository(writesFail ? new WritesFailStore(store) : store), logger);

        IReadOnlyList<string>? migrated = null;
        Assert.Null(Record.Exception(() => migrated = service.MigrateGraphSecretKeys()));

        if (writesFail)
        {
            Assert.Empty(migrated!);
            Assert.Equal("123", service.GetValue("MfaReset", "DelineaSecretId"));
        }
        else
        {
            Assert.Equal(["MfaReset"], migrated!);
            Assert.Equal("123", service.GetValue("MfaReset", "GraphDelineaSecretId"));
        }

        Assert.Equal(1, logger.Attempts);
    }

    /// <summary>
    /// aud-a64: the constructor's one-time legacy import reported with no guard. A provider that threw at a skipped file's
    /// report reached the import's outer catch and threw again there, so the singleton could not be built; one that threw
    /// at a failed archive's report - the shared helper's, documented never to throw - ended the import before the next
    /// file. Each case is built, reports once, and imports what it can.
    /// </summary>
    [Theory]
    [InlineData("an unparseable per-module file")]
    [InlineData("an unparseable single file")]
    [InlineData("a legacy file whose archive fails")]
    public void Construction_WithALoggerThatThrows_IsBuilt_AndImportsWhatItCan(string cause)
    {
        using var temp = new TempDir();
        var configDir = Path.Combine(temp.Path, "config");
        Directory.CreateDirectory(configDir);
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(temp.Path);
        File.WriteAllText(Path.Combine(configDir, "module-config-Migration.json"), "{\"OnPremTargetDatabases\":\"db-legacy\"}");
        FileStream? held = null;
        switch (cause)
        {
            case "an unparseable per-module file":
                File.WriteAllText(Path.Combine(configDir, "module-config-Comms10k.json"), "{ not json");
                break;
            case "an unparseable single file":
                File.WriteAllText(Path.Combine(configDir, "module-config.json"), "{ not json");
                break;
            case "a legacy file whose archive fails":
                // Read sharing only, and read before the other file: the import can read it, its archive's move is refused.
                var first = Path.Combine(configDir, "module-config-Comms10k.json");
                File.WriteAllText(first, "{\"k\":\"v\"}");
                held = new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.Read);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(cause), cause, null);
        }

        var logger = new ThrowingLogger();
        ModuleConfigService? service = null;
        try
        {
            Assert.Null(Record.Exception(() =>
                service = new ModuleConfigService(new ModuleCatalog(), env, TestConfigStore.CreateModuleConfig(temp.Path), logger)));
        }
        finally
        {
            held?.Dispose();
        }

        Assert.NotNull(service);
        Assert.Equal("db-legacy", service.GetValue("Migration", "OnPremTargetDatabases"));
        if (cause == "a legacy file whose archive fails")
            Assert.Equal("v", service.GetValue("Comms10k", "k"));
        Assert.Equal(1, logger.Attempts);
    }

    [Fact]
    public void Construction_ImportsPerModuleLegacyFile_ThenArchives()
    {
        using var temp = new TempDir();
        var configDir = Path.Combine(temp.Path, "config");
        Directory.CreateDirectory(configDir);
        var legacy = Path.Combine(configDir, "module-config-Migration.json");
        File.WriteAllText(legacy, "{\"OnPremTargetDatabases\":\"db-legacy\"}");

        var service = CreateService(temp.Path);

        Assert.Equal("db-legacy", service.GetValue("Migration", "OnPremTargetDatabases"));
        Assert.False(File.Exists(legacy));
        Assert.Single(Directory.GetFiles(configDir, "module-config-Migration.json.imported-*"));
    }

    [Fact]
    public void Construction_ImportsOldSingleFileShape()
    {
        using var temp = new TempDir();
        var configDir = Path.Combine(temp.Path, "config");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(Path.Combine(configDir, "module-config.json"),
            "{\"Migration\":{\"OnPremTargetDatabases\":\"db-old\"},\"Comms10k\":{\"k\":\"v\"}}");

        var service = CreateService(temp.Path);

        Assert.Equal("db-old", service.GetValue("Migration", "OnPremTargetDatabases"));
        Assert.Equal("v", service.GetValue("Comms10k", "k"));
    }

    [Fact]
    public void Construction_DbValueWins_OverLegacyFile()
    {
        using var temp = new TempDir();
        var configDir = Path.Combine(temp.Path, "config");
        Directory.CreateDirectory(configDir);

        // DB pre-seeded for Migration.
        var store = TestConfigStore.Create(temp.Path);
        new ModuleConfigRepository(store).SaveModule("Migration",
            new Dictionary<string, string> { ["OnPremTargetDatabases"] = "db-from-db" });

        // Legacy file with a different value.
        File.WriteAllText(Path.Combine(configDir, "module-config-Migration.json"),
            "{\"OnPremTargetDatabases\":\"db-from-file\"}");

        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(temp.Path);
        var service = new ModuleConfigService(new ModuleCatalog(), env,
            new ModuleConfigRepository(store), Substitute.For<ILogger<ModuleConfigService>>());

        Assert.Equal("db-from-db", service.GetValue("Migration", "OnPremTargetDatabases"));
    }

    private static ModuleConfigService CreateService(string contentRoot)
    {
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(contentRoot);
        return new ModuleConfigService(new ModuleCatalog(), env,
            TestConfigStore.CreateModuleConfig(contentRoot), Substitute.For<ILogger<ModuleConfigService>>());
    }

    /// <summary>Counts every report and throws once it has, as a failing provider can (aud-a28u).</summary>
    private sealed class ThrowingLogger : ILogger<ModuleConfigService>
    {
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Interlocked.Increment(ref _attempts);
            throw new InvalidOperationException("the logging provider failed");
        }
    }

    /// <summary>A real store whose writes fail; its reads and its change token pass.</summary>
    private sealed class WritesFailStore(IConfigStore inner) : IConfigStore
    {
        public long GetChangeToken() => inner.GetChangeToken();
        public T Read<T>(Func<Microsoft.Data.Sqlite.SqliteConnection, T> read) => inner.Read(read);
        public T Write<T>(Func<Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite.SqliteTransaction, T> write) => throw new InvalidOperationException("the store refused the write");
        public void Write(Action<Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite.SqliteTransaction> write) => throw new InvalidOperationException("the store refused the write");
    }

    private sealed class ThrowingStore : IConfigStore
    {
        public long GetChangeToken() => throw new InvalidOperationException();
        public T Read<T>(Func<Microsoft.Data.Sqlite.SqliteConnection, T> read) => throw new InvalidOperationException();
        public T Write<T>(Func<Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite.SqliteTransaction, T> write) => throw new InvalidOperationException();
        public void Write(Action<Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite.SqliteTransaction> write) => throw new InvalidOperationException();
    }
}
