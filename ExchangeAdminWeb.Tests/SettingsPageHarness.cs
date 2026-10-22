using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Progress;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using AdminSettingsPage = ExchangeAdminWeb.Components.Pages.AdminSettings;
using ExchangeOnlineConfigPage = ExchangeAdminWeb.Components.Pages.ExchangeOnlineConfig;
using ModuleConfigPage = ExchangeAdminWeb.Components.Pages.ModuleConfig;

namespace ExchangeAdminWeb.Tests;

#pragma warning disable BL0005, BL0006
/// <summary>
/// Drives the settings saves of Module Configuration, Admin Settings and Exchange Online Config
/// through the compiled pages' own handlers, with no directory, Exchange Online or SMTP.
/// </summary>
/// <remarks>
/// Compiled component and handler evidence only, the Comms10k and Defender pattern: each page is
/// constructed, its injected services set by reflection, its setup lifecycle method run and its
/// handlers invoked on a renderer's dispatcher. The pages are subclassed only to render nothing -
/// a do-nothing render is what lets the two enablement saves call StateHasChanged before they
/// reload - and, on Admin Settings, to skip the post-render sweep, which would ask the directory
/// about every saved protected principal. Everything a save touches is the shipped code: real
/// stores over one temp SQLite file (each behind a switch that can fail its reads or writes), the
/// real services over them and the real audit writer. Authorization is a substitute the test sets.
/// </remarks>
internal sealed class SettingsPageHarness : IDisposable
{
    public const string Ip = "10.0.0.7";
    public const string AdminSid = "S-1-5-21-1000-2000-3000-7777";
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "eaw-settings-" + Guid.NewGuid().ToString("N"));
    private readonly HandlerRenderer _renderer = new();

    /// <param name="beforeServices">
    /// Run with the content root before any service is built - where a test puts a legacy config file
    /// the services' imports read as they are constructed (aud-a26h).
    /// </param>
    public SettingsPageHarness(Action<string>? beforeServices = null)
    {
        Directory.CreateDirectory(_root);
        beforeServices?.Invoke(_root);

        // Unique per harness: ClientInfoService keeps its ip cache in a static, shared across tests.
        AdminName = "EXAMPLE\\settings-" + Guid.NewGuid().ToString("N")[..8];

        Config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Audit:LogRoot"] = _root,
            ["Audit:RotationPeriod"] = "daily",
            ["Delinea:SecretServerUrl"] = "https://credentials.invalid",
        }).Build();

        Env = Substitute.For<IWebHostEnvironment>();
        Env.ContentRootPath.Returns(_root);

        Catalog = new ModuleCatalog();
        ModuleConfigStore = new AttributeEditorHarness.SwitchableStore(TestConfigStore.Create(_root));
        ModuleConfig = new ModuleConfigService(
            Catalog, Env, new ModuleConfigRepository(ModuleConfigStore), NullLogger<ModuleConfigService>.Instance);
        EnablementStore = new AttributeEditorHarness.SwitchableStore(TestConfigStore.Create(_root));
        Enablement = new ModuleEnablementService(
            Catalog, Env, ModuleConfig, new ModuleEnablementRepository(EnablementStore), Config,
            NullLogger<ModuleEnablementService>.Instance);
        SectionStore = new AttributeEditorHarness.SwitchableStore(TestConfigStore.Create(_root));
        SectionAccess = new SectionAccessService(
            Config, NullLogger<SectionAccessService>.Instance, Env, Catalog, new SectionAccessRepository(SectionStore));
        AdminsStore = new AttributeEditorHarness.SwitchableStore(TestConfigStore.Create(_root));
        ModuleAdmins = new ModuleAdminService(Env, new ModuleAdminRepository(AdminsStore), NullLogger<ModuleAdminService>.Instance);

        var log = new JsonlLogService(Config, NullLogger<JsonlLogService>.Instance);
        Trace = new OperationTraceService(Config, log);
        Audit = new AuditService(log, Trace);

        LogLevelStore = new AttributeEditorHarness.SwitchableStore(TestConfigStore.Create(_root));
        ExtendedLog = new ExtendedLogService(
            Config, Env, new AppSettingRepository(LogLevelStore), NullLogger<ExtendedLogService>.Instance);
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new NoNetwork()));
        Delinea = new DelineaService(http, Config, NullLogger<DelineaService>.Instance, ExtendedLog, Trace);
        ProtectedStore = new AttributeEditorHarness.SwitchableStore(TestConfigStore.Create(_root));
        ProtectedPrincipals = new ProtectedPrincipalService(
            Env, Config, ModuleConfig, new ProtectedPrincipalRepository(ProtectedStore), Delinea,
            NullLogger<ProtectedPrincipalService>.Instance);
        ExoPool = new ExoConnectionPool(Config, ModuleConfig, Enablement, NullLogger<ExoConnectionPool>.Instance, Trace);

        ClientInfo = new ClientInfoService();
        ClientInfo.StoreForUser(AdminName, Ip, "settings harness");

        AuthState = Substitute.For<AuthenticationStateProvider>();
        AuthState.GetAuthenticationStateAsync().Returns(_ => new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, AdminName), new Claim(ClaimTypes.GroupSid, AdminSid)], "test"))));

        Authorization = Substitute.For<IAuthorizationService>();
        Authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(_ => AuthorizationFailure is { } failure
                ? Task.FromException<AuthorizationResult>(failure)
                : Task.FromResult(AdminAllowed ? AuthorizationResult.Success() : AuthorizationResult.Failed()));
    }

    public string AdminName { get; }

    /// <summary>The temp folder: the content root, the audit log root and the database's folder.</summary>
    public string Root => _root;

    public IConfiguration Config { get; }
    public IWebHostEnvironment Env { get; }
    public ModuleCatalog Catalog { get; }
    public AttributeEditorHarness.SwitchableStore ModuleConfigStore { get; }
    public ModuleConfigService ModuleConfig { get; }
    public AttributeEditorHarness.SwitchableStore EnablementStore { get; }
    public ModuleEnablementService Enablement { get; }
    public AttributeEditorHarness.SwitchableStore SectionStore { get; }
    public SectionAccessService SectionAccess { get; }
    public AttributeEditorHarness.SwitchableStore AdminsStore { get; }
    public ModuleAdminService ModuleAdmins { get; }
    public OperationTraceService Trace { get; }
    public AuditService Audit { get; }
    public AttributeEditorHarness.SwitchableStore LogLevelStore { get; }
    public ExtendedLogService ExtendedLog { get; }
    public DelineaService Delinea { get; }
    public AttributeEditorHarness.SwitchableStore ProtectedStore { get; }
    public ProtectedPrincipalService ProtectedPrincipals { get; }
    public ExoConnectionPool ExoPool { get; }
    public ClientInfoService ClientInfo { get; }
    public AuthenticationStateProvider AuthState { get; }
    public IAuthorizationService Authorization { get; }
    public RecordingNavigation Navigation { get; } = new();
    public ActivityProgressService Progress { get; } = new();

    /// <summary>The AdminSettings policy's answer; false refuses every save on these pages.</summary>
    public bool AdminAllowed { get; set; } = true;

    /// <summary>When set, every authorization call throws it.</summary>
    public Exception? AuthorizationFailure { get; set; }

    /// <summary>The audit record's <c>user</c> for <see cref="AdminName"/>.</summary>
    public string AdminSam => AdminName.Split('\\')[1];

    /// <summary>
    /// An AD Attribute Editor service over the same database, built when called so that a legacy
    /// allowlist file a test has written is imported - or refused - as the service is constructed.
    /// </summary>
    public ADAttributeEditorService CreateAttributeEditor() =>
        new(
            new ModuleCredentialService(ModuleConfig, Delinea, NullLogger<ModuleCredentialService>.Instance),
            ProtectedPrincipals,
            new ProtectedPrincipalServicerService(SectionAccess, NullLogger<ProtectedPrincipalServicerService>.Instance),
            Trace,
            Audit,
            new EmailService(Config, NullLogger<EmailService>.Instance),
            ModuleConfig,
            new AttributeEditorRepository(TestConfigStore.Create(_root)),
            Env,
            NullLogger<ADAttributeEditorService>.Instance);

    // ---- pages ---------------------------------------------------------------------------------

    /// <summary>
    /// Module Configuration for <paramref name="moduleId"/>, loaded as the page loads itself. With
    /// <paramref name="handlersOnly"/> false the page keeps its own markup, for reading what it renders.
    /// </summary>
    public ModuleConfigPage OpenModuleConfig(string moduleId, ADAttributeEditorService? attributeEditor = null, bool handlersOnly = true)
    {
        var page = handlersOnly ? new ModuleConfigHandlers() : new ModuleConfigPage();
        Inject(page, typeof(ModuleConfigPage), new Dictionary<string, object?>
        {
            ["ModuleConfigSvc"] = ModuleConfig,
            ["ModuleAdmin"] = ModuleAdmins,
            ["Enablement"] = Enablement,
            ["SectionAccess"] = SectionAccess,
            ["Catalog"] = Catalog,
            ["Audit"] = Audit,
            ["AuthStateProvider"] = AuthState,
            ["AuthorizationService"] = Authorization,
            ["Navigation"] = Navigation,
            ["ClientInfo"] = ClientInfo,
            ["AttrEditorService"] = attributeEditor,
            ["Logger"] = NullLogger<ModuleConfigPage>.Instance,
            ["Progress"] = Progress,
            // ADSearch and OUService are left unset on purpose: nothing driven here may reach the
            // directory, and a call that tried would fail on null rather than leave the machine.
        });
        page.ModuleId = moduleId;
        Attach(page);
        Setup(page, typeof(ModuleConfigPage), "OnParametersSetAsync");
        return page;
    }

    /// <summary>
    /// A second protected-principal service over its own store and connections on the same database
    /// file: the other instance, which shares the database (aud-a28j).
    /// </summary>
    public ProtectedPrincipalService CreateOtherProtectedPrincipals() =>
        new(Env, Config, ModuleConfig, new ProtectedPrincipalRepository(TestConfigStore.Create(_root)), Delinea,
            NullLogger<ProtectedPrincipalService>.Instance);

    /// <summary>The other instance's enablement service, over its own store on the same file (aud-a28k).</summary>
    public ModuleEnablementService CreateOtherEnablement() =>
        new(Catalog, Env, ModuleConfig, new ModuleEnablementRepository(TestConfigStore.Create(_root)), Config,
            NullLogger<ModuleEnablementService>.Instance);

    /// <summary>The other instance's section-access service, over its own store on the same file (aud-a28k).</summary>
    public SectionAccessService CreateOtherSectionAccess() =>
        new(Config, NullLogger<SectionAccessService>.Instance, Env, Catalog, new SectionAccessRepository(TestConfigStore.Create(_root)));

    /// <summary>The other instance's module-config service, over its own store on the same file (aud-a28n).</summary>
    public ModuleConfigService CreateOtherModuleConfig() =>
        new(Catalog, Env, new ModuleConfigRepository(TestConfigStore.Create(_root)), NullLogger<ModuleConfigService>.Instance);

    /// <summary>The other instance's module-admin service, over its own store on the same file (aud-a28n).</summary>
    public ModuleAdminService CreateOtherModuleAdmins() =>
        new(Env, new ModuleAdminRepository(TestConfigStore.Create(_root)), NullLogger<ModuleAdminService>.Instance);

    public AdminSettingsPage OpenAdminSettings()
    {
        var page = new AdminSettingsHandlers();
        Inject(page, typeof(AdminSettingsPage), new Dictionary<string, object?>
        {
            ["Audit"] = Audit,
            ["ExtendedLog"] = ExtendedLog,
            ["AuthStateProvider"] = AuthState,
            ["AuthorizationService"] = Authorization,
            ["Navigation"] = Navigation,
            ["ClientInfo"] = ClientInfo,
            ["Logger"] = NullLogger<AdminSettingsPage>.Instance,
            ["Catalog"] = Catalog,
            ["Enablement"] = Enablement,
            ["ProtectedPrincipalService"] = ProtectedPrincipals,
            ["ModuleConfigSvc"] = ModuleConfig,
            ["Progress"] = Progress,
        });
        Attach(page);
        Setup(page, typeof(AdminSettingsPage), "OnInitializedAsync");
        return page;
    }

    public ExchangeOnlineConfigPage OpenExchangeOnlineConfig()
    {
        var page = new ExchangeOnlineConfigHandlers();
        Inject(page, typeof(ExchangeOnlineConfigPage), new Dictionary<string, object?>
        {
            ["Audit"] = Audit,
            ["AuthStateProvider"] = AuthState,
            ["AuthorizationService"] = Authorization,
            ["Navigation"] = Navigation,
            ["ClientInfo"] = ClientInfo,
            ["Logger"] = NullLogger<ExchangeOnlineConfigPage>.Instance,
            ["Catalog"] = Catalog,
            ["Enablement"] = Enablement,
            ["ModuleConfigSvc"] = ModuleConfig,
            ["ExoPool"] = ExoPool,
            ["Progress"] = Progress,
            ["Config"] = Config,
        });
        Attach(page);
        Setup(page, typeof(ExchangeOnlineConfigPage), "OnInitializedAsync");
        return page;
    }

    /// <summary>Runs one of <paramref name="page"/>'s own methods on the renderer's dispatcher.</summary>
    public Task RunAsync<TPage>(TPage page, string method) where TPage : ComponentBase
    {
        var info = typeof(TPage).GetMethod(method, PrivateInstance)
            ?? throw new InvalidOperationException($"{typeof(TPage).Name}.{method} not found - this harness is stale");
        return _renderer.Dispatcher.InvokeAsync(() => info.Invoke(page, null) as Task ?? Task.CompletedTask);
    }

    // The setup lifecycle methods await only substitutes that have already completed.
    private void Setup(ComponentBase page, Type declaring, string method)
    {
        var info = declaring.GetMethod(method, PrivateInstance)
            ?? throw new InvalidOperationException($"{declaring.Name}.{method} not found - this harness is stale");
        _renderer.Dispatcher.InvokeAsync(() => info.Invoke(page, null) as Task ?? Task.CompletedTask).GetAwaiter().GetResult();
    }

    public static T Field<T>(object page, Type declaring, string name) =>
        (T)(declaring.GetField(name, PrivateInstance)
            ?? throw new InvalidOperationException($"{declaring.Name}.{name} not found - this harness is stale")).GetValue(page)!;

    public static void SetField(object page, Type declaring, string name, object? value) =>
        (declaring.GetField(name, PrivateInstance)
            ?? throw new InvalidOperationException($"{declaring.Name}.{name} not found - this harness is stale")).SetValue(page, value);

    /// <summary>The text and markup <paramref name="page"/> renders, its child components excluded.</summary>
    public static string RenderedText(ComponentBase page)
    {
        var frames = Frames(page);
        return string.Join(" ", frames.Select(frame => frame.FrameType switch
        {
            RenderTreeFrameType.Text => frame.TextContent,
            RenderTreeFrameType.Markup => frame.MarkupContent,
            _ => "",
        }));
    }

    /// <summary>
    /// Whether the button labelled <paramref name="label"/> renders disabled - a rendered disabled
    /// attribute, which is what stops the browser offering the click.
    /// </summary>
    public static bool IsButtonDisabled(ComponentBase page, string label)
    {
        var frames = Frames(page);
        for (var i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];
            if (frame.FrameType != RenderTreeFrameType.Element || frame.ElementName != "button")
                continue;

            var end = i + frame.ElementSubtreeLength;
            var text = string.Concat(frames.Skip(i + 1).Take(end - i - 1).Select(f => f.FrameType switch
            {
                RenderTreeFrameType.Text => f.TextContent,
                RenderTreeFrameType.Markup => f.MarkupContent,
                _ => "",
            })).Trim();
            if (text != label)
                continue;

            for (var a = i + 1; a < end && frames[a].FrameType == RenderTreeFrameType.Attribute; a++)
            {
                if (frames[a].AttributeName == "disabled")
                    return frames[a].AttributeValue is not false;
            }

            return false;
        }

        throw new InvalidOperationException($"no button labelled '{label}' is rendered - this harness is stale");
    }

    /// <summary>
    /// Whether the input whose placeholder is <paramref name="placeholder"/> renders disabled (aud-a26k).
    /// </summary>
    public static bool IsInputDisabled(ComponentBase page, string placeholder)
    {
        var frames = Frames(page);
        for (var i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];
            if (frame.FrameType != RenderTreeFrameType.Element || frame.ElementName != "input")
                continue;

            var attributes = new List<RenderTreeFrame>();
            for (var a = i + 1; a < frames.Count && frames[a].FrameType == RenderTreeFrameType.Attribute; a++)
                attributes.Add(frames[a]);

            if (!attributes.Any(a => a.AttributeName == "placeholder" && Equals(a.AttributeValue, placeholder)))
                continue;

            return attributes.Any(a => a.AttributeName == "disabled" && a.AttributeValue is not false);
        }

        throw new InvalidOperationException($"no input with placeholder '{placeholder}' is rendered - this harness is stale");
    }

    // The handler shells render nothing to the renderer, so a save that calls StateHasChanged builds no
    // child component; they render their markup only here, when a test asks to read it.
    private static List<RenderTreeFrame> Frames(ComponentBase page)
    {
        var shell = page as IMarkupShell;
        if (shell is not null)
            shell.Markup = true;

        try
        {
            var builder = new RenderTreeBuilder();
            typeof(ComponentBase).GetMethod("BuildRenderTree", PrivateInstance)!.Invoke(page, [builder]);
            var frames = builder.GetFrames();
            return frames.Array.Take(frames.Count).ToList();
        }
        finally
        {
            if (shell is not null)
                shell.Markup = false;
        }
    }

    private void Attach(IComponent page) =>
        _renderer.Dispatcher.InvokeAsync(() => _renderer.Attach(page)).GetAwaiter().GetResult();

    private static void Inject(object page, Type declaring, Dictionary<string, object?> services)
    {
        foreach (var (name, service) in services)
        {
            var property = declaring.GetProperty(name, PrivateInstance)
                ?? throw new InvalidOperationException($"{declaring.Name}.{name} not found - this harness is stale");
            property.SetValue(page, service);
        }
    }

    // ---- what the stores hold -----------------------------------------------------------------

    /// <summary>A store over the same database file, outside every switch, for reading what was written.</summary>
    private SqliteConfigStore Fresh() => TestConfigStore.Create(_root);

    public string StoredModuleConfig(string moduleId)
    {
        Assert.True(new ModuleConfigRepository(Fresh()).TryReadModule(moduleId, out var config), "module config unreadable");
        return Canonical(config);
    }

    public string StoredEnablement()
    {
        Assert.True(new ModuleEnablementRepository(Fresh()).TryGetAll(out var state), "enablement unreadable");
        return Canonical(state);
    }

    public string StoredSectionAccess()
    {
        Assert.True(new SectionAccessRepository(Fresh()).TryGetAll(out var access), "section access unreadable");
        return Canonical(access);
    }

    public string StoredModuleAdmins(string moduleId) =>
        JsonSerializer.Serialize(new ModuleAdminRepository(Fresh()).GetForModule(moduleId));

    public string StoredProtectedPrincipals()
    {
        Assert.True(new ProtectedPrincipalRepository(Fresh()).TryRead(out var data, out var configured), "protected principals unreadable");
        return JsonSerializer.Serialize(new { data, configured });
    }

    public string? StoredLogLevel() => new AppSettingRepository(Fresh()).Get("extended_log_level");

    private static string Canonical<T>(IReadOnlyDictionary<string, T> map) =>
        JsonSerializer.Serialize(map.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new { kv.Key, kv.Value }));

    /// <summary>Every audit record written so far, oldest first.</summary>
    public IReadOnlyList<JsonElement> AuditEvents()
    {
        var folder = Path.Combine(_root, "ExchangeAdminWeb");
        var events = new List<JsonElement>();
        if (!Directory.Exists(folder))
            return events;

        var files = Directory.GetFiles(folder, "exchangeadmin_*.jsonl")
            .Where(f => !Path.GetFileNameWithoutExtension(f).EndsWith("_trace", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal);

        foreach (var line in files.SelectMany(File.ReadAllLines).Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.TryGetProperty("eventType", out var type) && type.GetString() == "audit")
                events.Add(root.Clone());
        }

        return events;
    }

    public void Dispose()
    {
        _renderer.Dispose();
        Progress.Dispose();
        ExoPool.Dispose();
        ExtendedLog.Dispose();
        SqliteConnection.ClearPool(new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_root, "exchangeadmin.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString()));

        try { Directory.Delete(_root, recursive: true); }
        catch { /* best effort: a pooled connection can still hold the file */ }
    }

    // ---- the page shells ----------------------------------------------------------------------

    private interface IMarkupShell
    {
        bool Markup { get; set; }
    }

    private sealed class ModuleConfigHandlers : ModuleConfigPage, IMarkupShell
    {
        public bool Markup { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            if (Markup)
                base.BuildRenderTree(builder);
        }
    }

    private sealed class AdminSettingsHandlers : AdminSettingsPage, IMarkupShell
    {
        public bool Markup { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            if (Markup)
                base.BuildRenderTree(builder);
        }

        // The page's own sweep asks the directory about every saved entry. Never here.
        protected override Task OnAfterRenderAsync(bool firstRender) => Task.CompletedTask;
    }

    private sealed class ExchangeOnlineConfigHandlers : ExchangeOnlineConfigPage, IMarkupShell
    {
        public bool Markup { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            if (Markup)
                base.BuildRenderTree(builder);
        }
    }

    internal sealed class RecordingNavigation : NavigationManager
    {
        public RecordingNavigation() => Initialize("https://example.test/", "https://example.test/settings");

        public List<string> Navigations { get; } = [];

        protected override void NavigateToCore(string uri, bool forceLoad) => Navigations.Add(uri);
    }

    private sealed class HandlerRenderer() : Renderer(new ServiceCollection().BuildServiceProvider(), NullLoggerFactory.Instance)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

        public void Attach(IComponent component) => AssignRootComponentId(component);

        protected override Task UpdateDisplayAsync(in RenderBatch batch) => Task.CompletedTask;

        protected override void HandleException(Exception exception) => throw exception;
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("this harness has no network");
    }
}
#pragma warning restore BL0005, BL0006
