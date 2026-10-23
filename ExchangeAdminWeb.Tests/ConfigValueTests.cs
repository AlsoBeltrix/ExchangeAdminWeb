using System.Reflection;
using System.Text.RegularExpressions;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Jobs;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a62: configuration numbers and the trace switch were parsed in singleton constructors with int.Parse and typed
/// GetValue, so one value that was not a number - or not a Boolean - threw, and the audit and trace writer, the extended
/// log, the mail service, the trace service or the bulk job runner could not be built. Every page that needed one failed,
/// and the job runner, resolved at start-up, stopped the app from starting.
/// </summary>
/// <remarks>
/// The warning is given once per process for each setting and value, so every case here uses a key or a value of its own.
/// </remarks>
public sealed class ConfigValueTests : IDisposable
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "eaw-a62-" + Guid.NewGuid().ToString("N"));

    public ConfigValueTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    // ---- the reads -------------------------------------------------------------------------------------

    [Theory]
    [InlineData("7", 7)]
    [InlineData(" 7 ", 7)]
    [InlineData("-3", -3)]
    [InlineData("+12", 12)]
    [InlineData(null, 42)]
    [InlineData("", 42)]
    [InlineData("   ", 42)]
    public void ReadInt_TakesAWholeNumber_AndBlankAsItsDefault(string? value, int expected)
    {
        var key = UniqueKey();
        var logger = new CapturingLogger<ConfigValueTests>();

        Assert.Equal(expected, ConfigValue.ReadInt(Config((key, value)), key, whenBlank: 42, logger));
        Assert.Empty(logger.Warnings);
    }

    [Theory]
    [InlineData("five")]
    [InlineData("50MB")]
    [InlineData("2.5")]
    [InlineData("99999999999")]
    [InlineData("1,000")]
    // The binder's converter also read a hexadecimal form; no setting documents one, and it is named like any other.
    [InlineData("0x32")]
    public void ReadInt_TakesAValueItCannotRead_AsItsDefault_AndNamesIt(string value)
    {
        var key = UniqueKey();
        var logger = new CapturingLogger<ConfigValueTests>();

        Assert.Equal(42, ConfigValue.ReadInt(Config((key, value)), key, whenBlank: 42, logger));

        var warning = Assert.Single(logger.Warnings);
        Assert.Contains(key, warning, StringComparison.Ordinal);
        Assert.Contains($"'{value}'", warning, StringComparison.Ordinal);
        Assert.Contains("cannot be read as a whole number; it is read as 42", warning, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("5000000000", 5000000000L)]
    [InlineData(" 2048 ", 2048L)]
    [InlineData(null, null)]
    [InlineData("", null)]
    public void ReadLong_TakesAWholeNumber_AndBlankAsNotSet(string? value, long? expected)
    {
        var key = UniqueKey();
        var logger = new CapturingLogger<ConfigValueTests>();

        Assert.Equal(expected, ConfigValue.ReadLong(Config((key, value)), key, whenBlank: null, logger));
        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public void ReadLong_TakesAValueItCannotRead_AsItsDefault_AndNamesIt()
    {
        var notSet = UniqueKey();
        var withDefault = UniqueKey();
        var logger = new CapturingLogger<ConfigValueTests>();

        Assert.Null(ConfigValue.ReadLong(Config((notSet, "1k")), notSet, whenBlank: null, logger));
        Assert.Equal(7L, ConfigValue.ReadLong(Config((withDefault, "1k")), withDefault, whenBlank: 7, logger));

        Assert.Equal(2, logger.Warnings.Count);
        Assert.Contains(logger.Warnings, w => w.Contains(notSet, StringComparison.Ordinal) && w.Contains("it is read as not set", StringComparison.Ordinal));
        Assert.Contains(logger.Warnings, w => w.Contains(withDefault, StringComparison.Ordinal) && w.Contains("it is read as 7", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("2.5", 2.5)]
    [InlineData("1e1", 10.0)]
    [InlineData(" -0.5 ", -0.5)]
    [InlineData(null, 5.0)]
    [InlineData("", 5.0)]
    public void ReadDouble_TakesAFiniteNumber_AndBlankAsItsDefault(string? value, double expected)
    {
        var key = UniqueKey();
        var logger = new CapturingLogger<ConfigValueTests>();

        Assert.Equal(expected, ConfigValue.ReadDouble(Config((key, value)), key, whenBlank: 5, logger));
        Assert.Empty(logger.Warnings);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("1e400")]
    [InlineData("abc")]
    [InlineData("2,5")]
    public void ReadDouble_TakesAValueThatIsNotAFiniteNumber_AsItsDefault_AndNamesIt(string value)
    {
        var key = UniqueKey();
        var logger = new CapturingLogger<ConfigValueTests>();

        Assert.Equal(5.0, ConfigValue.ReadDouble(Config((key, value)), key, whenBlank: 5, logger));

        var warning = Assert.Single(logger.Warnings);
        Assert.Contains(key, warning, StringComparison.Ordinal);
        Assert.Contains($"'{value}'", warning, StringComparison.Ordinal);
        Assert.Contains("cannot be read as a finite number; it is read as 5", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void AValueItCannotRead_IsNamedOncePerProcess()
    {
        var key = UniqueKey();
        var config = Config((key, "five"));
        var first = new CapturingLogger<ConfigValueTests>();

        ConfigValue.ReadInt(config, key, whenBlank: 5, first);
        ConfigValue.ReadInt(config, key, whenBlank: 5, first);
        Assert.Single(first.Warnings);

        // Another reader of the same setting and value - the next singleton, the next start of a test host - is not
        // told again; another value is.
        var second = new CapturingLogger<ConfigValueTests>();
        Assert.Equal(5, ConfigValue.ReadInt(config, key, whenBlank: 5, second));
        Assert.Empty(second.Warnings);

        ConfigValue.ReadInt(Config((key, "six")), key, whenBlank: 5, second);
        Assert.Single(second.Warnings);
    }

    [Fact]
    public void ALoggerThatThrows_DoesNotStopTheAnswer()
    {
        var key = UniqueKey();
        var logger = new ThrowingLogger();
        var answer = 0;

        Assert.Null(Record.Exception(() => answer = ConfigValue.ReadInt(Config((key, "five")), key, whenBlank: 5, logger)));

        Assert.Equal(5, answer);
        Assert.Equal(1, logger.Attempts);
    }

    // ---- the constructors ------------------------------------------------------------------------------

    [Fact]
    public void TheAuditWriter_GivenLimitsItCannotRead_IsBuilt_WithItsDefaults()
    {
        // The defect: GetValue<int?> and GetValue<long?> in the constructor threw, and the app had no audit writer.
        var files = "five-" + Unique();
        var bytes = "1k-" + Unique();
        var megabytes = "fifty-" + Unique();
        var logger = new CapturingLogger<JsonlLogService>();

        JsonlLogService? writer = null;
        Assert.Null(Record.Exception(() => writer = new JsonlLogService(
            Config(("Audit:LogRoot", _root), ("Audit:MaxFilesPerPeriod", files), ("Audit:MaxFileBytes", bytes), ("Audit:MaxFileMB", megabytes)),
            logger)));

        Assert.Equal(5, writer!.GetAuditLogPaths(DateTime.Now).Count());
        Assert.Equal(50L * 1024 * 1024, Field<long>(writer, "_maxFileBytes"));
        Assert.Equal(3, logger.Warnings.Count);
        AssertNamed(logger, "Audit:MaxFilesPerPeriod", files, "5");
        AssertNamed(logger, "Audit:MaxFileBytes", bytes, "not set");
        AssertNamed(logger, "Audit:MaxFileMB", megabytes, "50");
        Assert.Equal(JsonlWriteOutcome.Written, writer.WriteAudit(new Dictionary<string, object?>
        {
            ["eventType"] = "audit",
            ["action"] = "AfterAMistypedLimit",
            ["category"] = "ConfigValue",
        }));

        // A byte limit that cannot be read is not set, so the megabyte limit decides, as when it is left out.
        var fallback = new JsonlLogService(
            Config(("Audit:LogRoot", _root), ("Audit:MaxFileBytes", "2k-" + Unique()), ("Audit:MaxFileMB", "2")),
            NullLogger<JsonlLogService>.Instance);
        Assert.Equal(2L * 1024 * 1024, Field<long>(fallback, "_maxFileBytes"));
    }

    [Fact]
    public void TheExtendedLog_GivenLimitsItCannotRead_IsBuilt_WithItsDefaults()
    {
        var files = "five-" + Unique();
        var bytes = "1k-" + Unique();
        var megabytes = "ten-" + Unique();
        var logger = new CapturingLogger<ExtendedLogService>();
        var appDir = Path.Combine(_root, "app");
        Directory.CreateDirectory(appDir);
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(appDir);
        var config = Config(
            ("Audit:LogRoot", _root),
            ("ExtendedLog:MaxFilesPerDay", files),
            ("ExtendedLog:MaxFileBytes", bytes),
            ("ExtendedLog:MaxFileMB", megabytes));

        ExtendedLogService? service = null;
        Assert.Null(Record.Exception(() => service = new ExtendedLogService(config, env, TestConfigStore.CreateAppSettings(appDir), logger)));

        using (service)
        {
            Assert.Equal(5, Field<int>(service!, "_maxFilesPerDay"));
            Assert.Equal(10L * 1024 * 1024, Field<long>(service!, "_maxFileBytes"));
        }

        AssertNamed(logger, "ExtendedLog:MaxFilesPerDay", files, "5");
        AssertNamed(logger, "ExtendedLog:MaxFileBytes", bytes, "not set");
        AssertNamed(logger, "ExtendedLog:MaxFileMB", megabytes, "10");
        Assert.Equal(3, logger.Warnings.Count(w => w.Contains("cannot be read as", StringComparison.Ordinal)));
    }

    [Fact]
    public void TheMailService_GivenAPortThatIsNotANumber_IsBuilt_OnPort25()
    {
        // The defect: int.Parse in the constructor threw, and with it every page that sends mail.
        var port = "twenty-five-" + Unique();
        var logger = new CapturingLogger<EmailService>();

        EmailService? service = null;
        Assert.Null(Record.Exception(() => service = new EmailService(Config(("Email:SmtpPort", port)), logger)));

        Assert.Equal(25, Field<int>(service!, "_smtpPort"));
        AssertNamed(logger, "Email:SmtpPort", port, "25");
        Assert.Single(logger.Warnings);

        // Blank threw too; it is the default now, with nothing to name. A number is read as written.
        var blank = new CapturingLogger<EmailService>();
        Assert.Equal(25, Field<int>(new EmailService(Config(("Email:SmtpPort", "")), blank), "_smtpPort"));
        Assert.Empty(blank.Warnings);
        Assert.Equal(587, Field<int>(new EmailService(Config(("Email:SmtpPort", "587")), NullLogger<EmailService>.Instance), "_smtpPort"));
    }

    [Fact]
    public void TheTraceService_GivenASwitchThatIsNotABoolean_IsBuilt_AndTraces()
    {
        // The defect: GetValue<bool?> in the constructor threw, and the audit service and every traced service with it.
        var value = "yes-" + Unique();
        var logger = new CapturingLogger<OperationTraceService>();
        var log = new JsonlLogService(Config(("Audit:LogRoot", _root)), NullLogger<JsonlLogService>.Instance);

        OperationTraceService? trace = null;
        Assert.Null(Record.Exception(() => trace = new OperationTraceService(Config(("OperationTrace:Enabled", value)), log, logger)));

        Assert.True(Field<bool>(trace!, "_enabled"), "a mistyped OperationTrace:Enabled turned the trace off");
        AssertNamed(logger, "OperationTrace:Enabled", value, "true");
        Assert.Single(logger.Warnings);
        using (var operation = trace!.BeginOperation("ConfigValue", "Traced", "operator", "10.0.0.5"))
            operation.Complete(true);
        Assert.NotEmpty(File.ReadAllLines(log.GetTraceLogPaths(DateTime.Now).Last()));

        // "false" still turns it off, and left out it is on.
        Assert.False(Field<bool>(new OperationTraceService(Config(("OperationTrace:Enabled", "false")), log), "_enabled"));
        Assert.True(Field<bool>(new OperationTraceService(Config(), log), "_enabled"));
    }

    [Fact]
    public void TheTraceService_BuiltByTheContainer_NamesTheValueThroughItsLogger()
    {
        // Its logger is optional, so the tests' direct constructions compile unchanged; registered as Program.cs registers
        // it, the container injects one, and the warning reaches it.
        var value = "on-" + Unique();
        var provider = new CapturingProvider();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(Config(("Audit:LogRoot", _root), ("OperationTrace:Enabled", value)));
        services.AddLogging(logging => logging.AddProvider(provider));
        services.AddSingleton<JsonlLogService>();
        services.AddSingleton<OperationTraceService>();
        using var container = services.BuildServiceProvider();

        var trace = container.GetRequiredService<OperationTraceService>();

        Assert.True(Field<bool>(trace, "_enabled"));
        var warning = Assert.Single(provider.Warnings, w => w.Category == typeof(OperationTraceService).FullName);
        Assert.Contains("OperationTrace:Enabled", warning.Message, StringComparison.Ordinal);
        Assert.Contains($"'{value}'", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBulkJobRunner_GivenSettingsItCannotRead_IsBuilt_WithItsDefaults()
    {
        // The defect: GetValue<double?> and GetValue<int?> in the constructor threw, and the runner is resolved at
        // start-up. "NaN" passed the binder and then threw out of TimeSpan.FromMinutes.
        var limit = "lots-" + Unique();
        var retention = "a-month-" + Unique();
        var logger = new CapturingLogger<BulkJobService>();
        var config = Config(
            ("BulkJobs:StaleHeartbeatMinutes", "NaN"),
            ("BulkJobs:RecentJobLimit", limit),
            ("BulkJobs:RetentionDays", retention));

        BulkJobService? runner = null;
        Assert.Null(Record.Exception(() => runner = new BulkJobService(null!, null!, new BulkJobProcessorRegistry([]), config, logger)));

        Assert.Equal(TimeSpan.FromMinutes(5), Field<TimeSpan>(runner!, "_staleHeartbeat"));
        Assert.Equal(25, Field<int>(runner!, "_recentJobLimit"));
        Assert.Equal(30, runner!.RetentionDays);
        AssertNamed(logger, "BulkJobs:StaleHeartbeatMinutes", "NaN", "5");
        AssertNamed(logger, "BulkJobs:RecentJobLimit", limit, "25");
        AssertNamed(logger, "BulkJobs:RetentionDays", retention, "30");
        Assert.Equal(3, logger.Warnings.Count);
    }

    // ---- the source check ------------------------------------------------------------------------------

    /// <summary>
    /// A typed GetValue other than string, and a numeric Parse or Convert of a configuration indexer: each throws on a value
    /// it cannot convert. bool.Parse is aud-a58's check (ConfigBooleanTests), with its own allowance.
    /// </summary>
    private static readonly Regex ThrowingRead = new(
        @"\bGetValue\s*<\s*(?!string\s*\??\s*>)[^>]+>"
        + @"|\b(?:System\s*\.\s*)?(?:int|long|short|uint|ulong|ushort|byte|sbyte|double|float|decimal|Int16|Int32|Int64|UInt16|UInt32|UInt64|Byte|SByte|Double|Single|Decimal|TimeSpan|DateTime|DateTimeOffset|Guid)\s*\.\s*Parse\s*\(\s*(?:\w+\s*\.\s*)*_?[cC]onfig(?:uration)?\s*\["
        + @"|\bConvert\s*\.\s*To\w+\s*\(\s*(?:\w+\s*\.\s*)*_?[cC]onfig(?:uration)?\s*\[",
        RegexOptions.CultureInvariant);

    [Theory]
    [InlineData("_smtpPort = int.Parse(config[\"Email:SmtpPort\"] ?? \"25\");", true)]
    [InlineData("config.GetValue<int?>(\"Audit:MaxFilesPerPeriod\")", true)]
    [InlineData("config.GetValue<long?>(\"Audit:MaxFileBytes\")", true)]
    [InlineData("config.GetValue<bool?>(\"OperationTrace:Enabled\")", true)]
    [InlineData("Config.GetValue<double?>(\"BulkJobs:RetentionDays\")", true)]
    [InlineData("builder.Configuration.GetValue<int>(\"Port\")", true)]
    [InlineData("long.Parse(_config[\"Limit\"])", true)]
    [InlineData("System.Int32.Parse(Configuration[\"Limit\"])", true)]
    [InlineData("Convert.ToInt32(builder.Configuration[\"Limit\"])", true)]
    [InlineData("config.GetValue<string>(\"Name\")", false)]
    [InlineData("config.GetValue<string?>(\"Name\")", false)]
    [InlineData("int.TryParse(config[\"Limit\"], out var limit)", false)]
    [InlineData("ConfigValue.ReadInt(config, \"Limit\", 5, logger)", false)]
    [InlineData("values.TryGetValue(\"Limit\", out var limit)", false)]
    [InlineData("int.Parse(row.Capacity)", false)]
    public void TheSourceCheck_NamesTheParsersThatThrow_AndNotTheReadsThatDoNot(string code, bool named)
    {
        Assert.Equal(named, ThrowingRead.IsMatch(code));
    }

    [Fact]
    public void NoPageOrService_ReadsAConfigurationValueWithAParserThatThrows()
    {
        var root = RepoRoot();
        var offenders = new List<string>();
        var files = new[] { "Components", "Services" }
            .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(root, folder), "*.*", SearchOption.AllDirectories))
            .Where(file => file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".razor", StringComparison.OrdinalIgnoreCase));
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var code = RazorSyntax.CommentsBlanked(File.ReadAllText(file));
            offenders.AddRange(ThrowingRead.Matches(code)
                .Select(hit => $"{relative}:{code[..hit.Index].Count(c => c == '\n') + 1}"));
        }

        Assert.True(offenders.Count == 0,
            "A typed GetValue, int.Parse and their kind throw on a configuration value they cannot convert; read it through "
            + "ConfigValue or ConfigBoolean: " + string.Join(", ", offenders));
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private static string Unique() => Guid.NewGuid().ToString("N");

    private static string UniqueKey() => "Test:Number" + Unique();

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, PrivateInstance)!.GetValue(owner)!;

    private static void AssertNamed<T>(CapturingLogger<T> logger, string key, string value, string reading) =>
        Assert.Contains(logger.Warnings, warning =>
            warning.Contains(key, StringComparison.Ordinal)
            && warning.Contains($"'{value}'", StringComparison.Ordinal)
            && warning.Contains("it is read as " + reading + " until it is corrected", StringComparison.Ordinal));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "Components", "Pages")))
                return dir.FullName;

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repo root from the test base directory.");
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<string> _warnings = [];

        public IReadOnlyList<string> Warnings
        {
            get { lock (_warnings) return _warnings.ToList(); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Warning)
                return;

            lock (_warnings)
                _warnings.Add(formatter(state, exception));
        }
    }

    /// <summary>Counts every report and throws once it has, as a failing provider can (aud-a28u).</summary>
    private sealed class ThrowingLogger : ILogger
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

    /// <summary>The warnings every logger the container hands out receives, by category.</summary>
    private sealed class CapturingProvider : ILoggerProvider
    {
        private readonly List<(string Category, string Message)> _warnings = [];

        public IReadOnlyList<(string Category, string Message)> Warnings
        {
            get { lock (_warnings) return _warnings.ToList(); }
        }

        public ILogger CreateLogger(string categoryName) => new Sink(this, categoryName);

        public void Dispose()
        {
        }

        private void Add(string category, string message)
        {
            lock (_warnings)
                _warnings.Add((category, message));
        }

        private sealed class Sink(CapturingProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Warning)
                    owner.Add(category, formatter(state, exception));
            }
        }
    }
}
