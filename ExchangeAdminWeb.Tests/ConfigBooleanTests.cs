using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using HomePage = ExchangeAdminWeb.Components.Pages.Home;

namespace ExchangeAdminWeb.Tests;

#pragma warning disable BL0006
/// <summary>
/// aud-a58: configuration Booleans were read with bool.Parse, so a value that was neither true nor false threw. The Home
/// page threw during render for every user; the mail service and the ServiceNow client threw in their constructors, and
/// with them every page that needed one.
/// </summary>
/// <remarks>
/// The warning is given once per process for each setting and value, so every case here uses a key or a value of its own.
/// The Home page is driven through its own BuildRenderTree into a builder - no renderer - with its injected services set
/// by reflection, as the event log's page tests drive theirs.
/// </remarks>
public sealed class ConfigBooleanTests : IDisposable
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string NotifyUsers = "Email:NotifyUsersOnPermissionGrant";
    private const string NotifyNotice = "End users are notified";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eaw-a58-" + Guid.NewGuid().ToString("N"));

    public ConfigBooleanTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        // aud-a74. The pools of this test's own databases only, so their pooled connections let the folder go: the classes
        // running beside it hold connections of their own (the AttributeEditorHarness shape).
        foreach (var name in new[] { "exchangeadmin.db", "usage.db" })
        {
            SqliteConnection.ClearPool(new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(_dir, name),
                Mode = SqliteOpenMode.ReadWriteCreate,
            }.ToString()));
        }

        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    [Theory]
    [InlineData("true", false, false, true)]
    [InlineData("True", false, false, true)]
    [InlineData("TRUE", false, false, true)]
    [InlineData(" true ", false, false, true)]
    [InlineData("false", true, true, false)]
    [InlineData("False", true, true, false)]
    [InlineData(null, false, true, false)]
    [InlineData(null, true, false, true)]
    [InlineData("", false, true, false)]
    [InlineData("   ", true, false, true)]
    public void Read_TakesTrueAndFalseInAnyCase_AndBlankAsItsDefault(string? value, bool whenBlank, bool whenNotBoolean, bool expected)
    {
        var key = UniqueKey();
        var logger = new CapturingLogger<ConfigBooleanTests>();

        Assert.Equal(expected, ConfigBoolean.Read(Config((key, value)), key, whenBlank, whenNotBoolean, logger));
        Assert.Empty(logger.Warnings);
    }

    [Theory]
    [InlineData("yes", true)]
    [InlineData("1", true)]
    [InlineData("no", false)]
    [InlineData("on", false)]
    public void Read_TakesAValueThatIsNotABoolean_AsItsReading_AndNamesIt(string value, bool whenNotBoolean)
    {
        var key = UniqueKey();
        var logger = new CapturingLogger<ConfigBooleanTests>();

        Assert.Equal(whenNotBoolean, ConfigBoolean.Read(Config((key, value)), key, whenBlank: !whenNotBoolean, whenNotBoolean, logger));

        var warning = Assert.Single(logger.Warnings);
        Assert.Contains(key, warning, StringComparison.Ordinal);
        Assert.Contains($"'{value}'", warning, StringComparison.Ordinal);
        Assert.Contains("read as " + (whenNotBoolean ? "true" : "false"), warning, StringComparison.Ordinal);
    }

    [Fact]
    public void AValueThatIsNotABoolean_IsNamedOncePerProcess()
    {
        var key = UniqueKey();
        var first = new CapturingLogger<ConfigBooleanTests>();
        var config = Config((key, "yes"));

        ConfigBoolean.Read(config, key, whenBlank: false, whenNotBoolean: false, first);
        ConfigBoolean.Read(config, key, whenBlank: false, whenNotBoolean: false, first);
        Assert.Single(first.Warnings);

        var second = new CapturingLogger<ConfigBooleanTests>();
        Assert.False(ConfigBoolean.Read(config, key, whenBlank: false, whenNotBoolean: false, second));
        Assert.Empty(second.Warnings);

        ConfigBoolean.Read(Config((key, "no")), key, whenBlank: false, whenNotBoolean: false, second);
        Assert.Single(second.Warnings);
    }

    [Fact]
    public void ALoggerThatThrows_DoesNotStopTheAnswer()
    {
        var key = UniqueKey();
        var logger = new ThrowingLogger();
        var answer = false;

        Assert.Null(Record.Exception(() => answer = ConfigBoolean.Read(Config((key, "yes")), key, whenBlank: false, whenNotBoolean: true, logger)));

        Assert.True(answer);
        Assert.Equal(1, logger.Attempts);
    }

    [Fact]
    public void TheHomePage_GivenANotificationSwitchThatIsNotABoolean_Renders_WithoutClaimingNotifications()
    {
        // The defect: bool.Parse during render, so "yes" threw FormatException and the landing page failed for every user.
        var value = "yes-" + Guid.NewGuid().ToString("N");
        var logger = new CapturingLogger<HomePage>();
        var page = HomePageWith(value, logger);

        string first = "", second = "";
        Assert.Null(Record.Exception(() => first = Rendered(page)));
        Assert.Null(Record.Exception(() => second = Rendered(page)));

        Assert.Contains("All actions are logged", first, StringComparison.Ordinal);
        Assert.DoesNotContain(NotifyNotice, first, StringComparison.Ordinal);
        Assert.DoesNotContain(NotifyNotice, second, StringComparison.Ordinal);
        var warning = Assert.Single(logger.Warnings);
        Assert.Contains(NotifyUsers, warning, StringComparison.Ordinal);
        Assert.Contains($"'{value}'", warning, StringComparison.Ordinal);

        // The read itself works: a Boolean in any case turns the notice on.
        Assert.Contains(NotifyNotice, Rendered(HomePageWith("True", logger)), StringComparison.Ordinal);
        Assert.DoesNotContain(NotifyNotice, Rendered(HomePageWith(null, logger)), StringComparison.Ordinal);
    }

    [Fact]
    public void TheMailService_GivenSwitchesThatAreNotBooleans_IsBuilt_WithTlsOn_AndUserNotificationsOff()
    {
        var ssl = "yes-" + Guid.NewGuid().ToString("N");
        var notify = "yes-" + Guid.NewGuid().ToString("N");
        var logger = new CapturingLogger<EmailService>();

        EmailService? service = null;
        Assert.Null(Record.Exception(() => service = new EmailService(Config(("Email:SmtpUseSsl", ssl), (NotifyUsers, notify)), logger)));

        Assert.True(UsesTls(service!), "a mistyped Email:SmtpUseSsl was read as clear text");
        Assert.False(service!.UserNotificationsEnabled);
        Assert.Equal(2, logger.Warnings.Count);
        Assert.Contains(logger.Warnings, w => w.Contains("Email:SmtpUseSsl", StringComparison.Ordinal) && w.Contains($"'{ssl}'", StringComparison.Ordinal));
        Assert.Contains(logger.Warnings, w => w.Contains(NotifyUsers, StringComparison.Ordinal) && w.Contains($"'{notify}'", StringComparison.Ordinal));

        // Left out, both keep their documented default, false; and a Boolean is read as written.
        var defaults = new EmailService(Config(), NullLogger<EmailService>.Instance);
        Assert.False(UsesTls(defaults));
        Assert.False(defaults.UserNotificationsEnabled);
        var set = new EmailService(Config(("Email:SmtpUseSsl", "True"), (NotifyUsers, "true")), NullLogger<EmailService>.Instance);
        Assert.True(UsesTls(set));
        Assert.True(set.UserNotificationsEnabled);
    }

    [Fact]
    public void TheServiceNowClient_GivenAnEnabledSwitchThatIsNotABoolean_IsBuilt_AndValidatesTickets()
    {
        var enabled = "yes-" + Guid.NewGuid().ToString("N");
        var logger = new CapturingLogger<ServiceNowService>();
        using var http = new HttpClient();
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(http);

        ServiceNowService? client = null;
        Assert.Null(Record.Exception(() => client = new ServiceNowService(Config(("ServiceNow:Enabled", enabled)), logger, factory)));

        Assert.True(client!.Enabled, "a mistyped ServiceNow:Enabled was read as dormant, which passes every ticket");
        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("ServiceNow:Enabled", warning, StringComparison.Ordinal);
        Assert.Contains($"'{enabled}'", warning, StringComparison.Ordinal);

        // Left out, it keeps its documented default: dormant.
        Assert.False(new ServiceNowService(Config(), NullLogger<ServiceNowService>.Instance, factory).Enabled);
    }

    [Fact]
    public void NoPageOrService_ParsesAConfigurationBooleanWithBoolParse()
    {
        // The two pages are another agent's files in this round. aud-a58's record, under "Left for a follow-up", names the
        // change they need; the follow-up removes them from here.
        var allowance = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Components/Pages/MailboxPermissions.razor"] = 1,
            ["Components/Pages/CalendarPermissions.razor"] = 1,
        };

        var root = RepoRoot();
        var offenders = new List<string>();
        var files = new[] { "Components", "Services" }
            .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(root, folder), "*.*", SearchOption.AllDirectories))
            .Where(file => file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".razor", StringComparison.OrdinalIgnoreCase));
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var code = RazorSyntax.CommentsBlanked(File.ReadAllText(file));
            var hits = Regex.Matches(code, @"\b(?:System\s*\.\s*)?(?:bool|Boolean)\s*\.\s*Parse\s*\(");
            if (hits.Count == 0 || (allowance.TryGetValue(relative, out var allowed) && hits.Count <= allowed))
                continue;

            offenders.AddRange(hits.Select(hit => $"{relative}:{code[..hit.Index].Count(c => c == '\n') + 1}"));
        }

        Assert.True(offenders.Count == 0,
            "bool.Parse throws on a configuration value that is neither true nor false; read it through ConfigBoolean.Read: "
            + string.Join(", ", offenders));
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private static string UniqueKey() => "Test:Switch" + Guid.NewGuid().ToString("N");

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    private static bool UsesTls(EmailService service) =>
        (bool)typeof(EmailService).GetField("_smtpUseSsl", PrivateInstance)!.GetValue(service)!;

    private HomePage HomePageWith(string? notifyUsers, ILogger<HomePage> logger)
    {
        var config = Config((NotifyUsers, notifyUsers));
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_dir);
        var catalog = new ModuleCatalog();
        var moduleConfig = new ModuleConfigService(catalog, env, TestConfigStore.CreateModuleConfig(_dir), NullLogger<ModuleConfigService>.Instance);
        var enablement = new ModuleEnablementService(
            catalog, env, moduleConfig, TestConfigStore.CreateModuleEnablement(_dir), config, NullLogger<ModuleEnablementService>.Instance);
        var usage = new TelemetryOff(new UsageEventRepository(new SqliteConnectionFactory(Path.Combine(_dir, "usage.db"))), moduleConfig, catalog);

        var page = new HomePage();
        SetProperty(page, "Config", config);
        SetProperty(page, "Catalog", catalog);
        SetProperty(page, "Enablement", enablement);
        SetProperty(page, "Usage", usage);
        SetProperty(page, "Logger", logger);
        typeof(HomePage).GetField("authChecked", PrivateInstance)!.SetValue(page, true);
        return page;
    }

    private static void SetProperty(HomePage page, string name, object value) =>
        typeof(HomePage).GetProperty(name, PrivateInstance)!.SetValue(page, value);

    /// <summary>The text the page's own render tree carries.</summary>
    private static string Rendered(HomePage page)
    {
        using var builder = new RenderTreeBuilder();
        typeof(HomePage).GetMethod("BuildRenderTree", PrivateInstance)!.Invoke(page, [builder]);
        var frames = builder.GetFrames();
        var text = new StringBuilder();
        for (var i = 0; i < frames.Count; i++)
        {
            var frame = frames.Array[i];
            if (frame.FrameType == RenderTreeFrameType.Text)
                text.Append(frame.TextContent).Append(' ');
            else if (frame.FrameType == RenderTreeFrameType.Markup)
                text.Append(frame.MarkupContent).Append(' ');
        }

        return text.ToString();
    }

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

    /// <summary>Usage telemetry off, so the Home page's other notice does not depend on a store.</summary>
    private sealed class TelemetryOff(UsageEventRepository repository, ModuleConfigService moduleConfig, ModuleCatalog catalog)
        : UsageTelemetryService(repository, moduleConfig, catalog, NullLogger<UsageTelemetryService>.Instance)
    {
        public override bool Enabled() => false;
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
}
#pragma warning restore BL0006
