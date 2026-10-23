using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Progress;
using ExchangeAdminWeb.Services.SelfServiceGroups;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Page = ExchangeAdminWeb.Components.Pages.SelfServiceGroups;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// kd-8. A Self-Service Groups operation publishes its outcome - the banner, and for a batch the
/// per-row table - only into the view of the group it ran for. One that finishes after the operator
/// has gone back and opened another group leaves that group's view alone.
/// </summary>
/// <remarks>
/// <para>
/// The four mutating handlers are driven by reflection with their injected services set, the kd-4
/// and AttributeEditor page-handler pattern. Each is held at its first authorization re-check - a
/// substituted <see cref="IAuthorizationService"/> whose first answer is a task the test completes -
/// and while it waits the test does what the operator would: Back to groups, then Manage members on
/// another group, through the page's own BackToGroups and SelectGroup. The member read SelectGroup
/// starts runs against a real <see cref="SelfServiceGroupService"/> with no credential configured, so
/// it fails at once without touching a directory.
/// </para>
/// <para>
/// What stops a second group being opened mid-operation is the Manage members gate, which is markup,
/// and since kd-19 SelectGroup's own refusal while the view is busy. The switch test shows that
/// refusal and then goes past both, which is exactly the case ShowFor has to survive on its own. The
/// operation is then refused at that held re-check (or, for a batch, at its rows' re-checks), so
/// nothing here writes to a directory either.
/// </para>
/// </remarks>
public sealed class SelfServiceGroupsResultScopeTests : IDisposable
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string Operator = "CONTOSO\\owner";
    private const string CallerSid = "S-1-5-21-1000-2000-3000-1001";

    private static readonly GroupMember Member = new()
    {
        ObjectGuid = "1f2e3d4c-5b6a-7980-a1b2-c3d4e5f60718",
        DistinguishedName = "CN=Jo Doe,OU=Users,DC=contoso,DC=com",
        DisplayName = "Jo Doe",
        Identity = "jdoe",
        Kind = "User",
        IsRemovable = true,
    };

    private readonly TempDir _temp = new();
    private readonly IConfiguration _config;
    private readonly AuditService _audit;
    private readonly EmailService _email;
    private readonly SelfServiceGroupService _groups;
    private readonly ActivityProgressService _progress = new();

    // The held authorization re-check: its first call waits on the gate, every later one answers
    // with LaterAnswer.
    private readonly TaskCompletionSource<AuthorizationResult> _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _authorizations;
    private AuthorizationResult _laterAnswer = AuthorizationResult.Failed();

    public SelfServiceGroupsResultScopeTests()
    {
        Directory.CreateDirectory(Path.Combine(_temp.Path, "config"));
        _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Audit:LogRoot"] = _temp.Path,
            ["Delinea:SecretServerUrl"] = "https://fake.local",
        }).Build();

        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_temp.Path);
        var catalog = new ModuleCatalog();
        var moduleConfig = new ModuleConfigService(
            catalog, env, TestConfigStore.CreateModuleConfig(_temp.Path), NullLogger<ModuleConfigService>.Instance);
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());
        var log = new JsonlLogService(_config, NullLogger<JsonlLogService>.Instance);
        var trace = new OperationTraceService(_config, log);
        var extendedLog = new ExtendedLogService(
            _config, env, TestConfigStore.CreateAppSettings(_temp.Path), NullLogger<ExtendedLogService>.Instance);
        var delinea = new DelineaService(http, _config, NullLogger<DelineaService>.Instance, extendedLog, trace);
        var protectedPrincipals = new ProtectedPrincipalService(
            env, _config, moduleConfig, TestConfigStore.CreateProtectedPrincipal(_temp.Path), delinea,
            NullLogger<ProtectedPrincipalService>.Instance);
        var sectionAccess = new SectionAccessService(
            _config, NullLogger<SectionAccessService>.Instance, env, catalog,
            new ExchangeAdminWeb.Services.Storage.SectionAccessRepository(TestConfigStore.Create(_temp.Path)));
        var servicers = new ProtectedPrincipalServicerService(
            sectionAccess, NullLogger<ProtectedPrincipalServicerService>.Instance);

        _audit = new AuditService(log, trace);
        _email = Substitute.For<EmailService>(_config, NullLogger<EmailService>.Instance);
        _groups = new SelfServiceGroupService(
            new ModuleCredentialService(moduleConfig, delinea, NullLogger<ModuleCredentialService>.Instance),
            protectedPrincipals, servicers, NullLogger<SelfServiceGroupService>.Instance);
    }

    public void Dispose()
    {
        _progress.Dispose();
        _temp.Dispose();
    }

    /// <summary>
    /// Every mutating handler, and how its held re-check is answered: the single-member paths are
    /// refused there; a batch is let through its upfront check so its rows run - each refused at its
    /// own re-check - and it reaches its summary, the table and the banner.
    /// </summary>
    public static TheoryData<string, bool> Operations() => new()
    {
        { "ChangeMember", false },
        { "RemoveListedMember", false },
        { "RemoveSelectedAsync", true },
        { "AddResolvedAsync", true },
        { "RemoveSelectedAsync", false },
        { "AddResolvedAsync", false },
    };

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task AnOperationThatFinishesAfterAnotherGroupIsShown_PublishesNothingIntoThatGroupsView(
        string operation, bool letTheBatchThrough)
    {
        var (first, second) = (Group("Finance Team"), Group("Payroll Team"));
        var page = PageManaging(first);

        var running = Start(page, operation);
        await _held.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // The operator goes back and clicks Manage members on another group while the operation is
        // held. Since kd-19 that click is refused - the view is busy - and the page stays on the
        // browse view.
        Invoke(page, "BackToGroups");
        await (Task)Invoke(page, "SelectGroup", second);
        Assert.Null(Field<ManageableGroup>(page, "selected"));

        // ShowFor is the line behind that refusal, so the test goes past it: the other group is put
        // on screen directly, as a re-entry neither the gate nor the refusal stopped would put it.
        SetField(page, "selected", second);

        _gate.SetResult(letTheBatchThrough ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        await running.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Same(second, Field<ManageableGroup>(page, "selected"));
        Assert.Null(Field<PermissionResult>(page, "changeResult"));
        Assert.Null(Field<List<BulkRowOutcome>>(page, "bulkOutcome"));

        // Only the screen is withheld: the operation's own record of the group it ran for stands.
        Assert.Contains(AuditRows(), row => row.GetProperty("target").GetString() == first.Name
            && row.GetProperty("result").GetString() == "Failed");
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task AnOperationOnTheGroupStillShown_PublishesItsOutcome(string operation, bool letTheBatchThrough)
    {
        // The control for the test above: the same handler, the same refusal, no group switch - and
        // the outcome IS shown, so a null above is the group test withholding it, not a handler
        // that never published.
        var group = Group("Finance Team");
        var page = PageManaging(group);

        var running = Start(page, operation);
        await _held.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        _gate.SetResult(letTheBatchThrough ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        await running.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Same(group, Field<ManageableGroup>(page, "selected"));
        var shown = Field<PermissionResult>(page, "changeResult");
        Assert.NotNull(shown);
        Assert.False(shown.Success);

        var table = Field<List<BulkRowOutcome>>(page, "bulkOutcome");
        if (letTheBatchThrough)
        {
            var row = Assert.Single(table!);
            Assert.False(row.Done);
            Assert.Contains("of 1 member(s) were not", shown.Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.Null(table);
            Assert.Contains("Authorization denied", shown.Message, StringComparison.Ordinal);
        }
    }

    // ---- the page -----------------------------------------------------------------------------

    private static ManageableGroup Group(string name) => new()
    {
        ObjectGuid = Guid.NewGuid().ToString(),
        DistinguishedName = $"CN={name},OU=Groups,DC=contoso,DC=com",
        Name = name,
        GroupType = "Security",
        CanManageMembers = true,
    };

    /// <summary>The page in the manage view of <paramref name="group"/>, with something staged for each handler.</summary>
    private Page PageManaging(ManageableGroup group)
    {
        var page = new Page();
        var authState = Substitute.For<AuthenticationStateProvider>();
        authState.GetAuthenticationStateAsync().Returns(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, Operator)], "Negotiate"))));
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(_ =>
            {
                if (Interlocked.Increment(ref _authorizations) == 1)
                {
                    _held.TrySetResult();
                    return _gate.Task;
                }

                return Task.FromResult(_laterAnswer);
            });

        SetProperty(page, "GroupService", _groups);
        SetProperty(page, "Audit", _audit);
        SetProperty(page, "Email", _email);
        SetProperty(page, "AuthStateProvider", authState);
        SetProperty(page, "AuthorizationService", authorization);
        SetProperty(page, "Logger", NullLogger<Page>.Instance);
        SetProperty(page, "Progress", _progress);
        SetField(page, "currentUser", Operator);
        SetField(page, "clientIpAddress", "10.0.0.5");
        SetField(page, "callerSid", CallerSid);

        SetField(page, "selected", group);
        SetField(page, "memberIdentity", "jdoe");
        SetField(page, "groupMembers", new List<GroupMember> { Member });
        ((HashSet<string>)typeof(Page).GetField("selectedGuids", PrivateInstance)!.GetValue(page)!).Add(Member.ObjectGuid);
        SetField(page, "resolution", new List<BulkIdentityList.Resolution>
        {
            new(new BulkIdentityList.Line(1, "jdoe"), BulkIdentityList.Status.Resolved,
                new BulkIdentityList.Candidate(Member.DistinguishedName, "user", "jdoe", "Jo Doe",
                    "jdoe@contoso.com", "jdoe", "jdoe@contoso.com", Member.ObjectGuid),
                ""),
        });
        return page;
    }

    private static Task Start(Page page, string operation) => operation switch
    {
        "ChangeMember" => (Task)Invoke(page, "ChangeMember", MembershipOperation.Add, null),
        "RemoveListedMember" => (Task)Invoke(page, "RemoveListedMember", Member),
        "RemoveSelectedAsync" => (Task)Invoke(page, "RemoveSelectedAsync"),
        "AddResolvedAsync" => (Task)Invoke(page, "AddResolvedAsync"),
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
    };

    private static object Invoke(Page page, string method, params object?[] args) =>
        typeof(Page).GetMethod(method, PrivateInstance)!.Invoke(page, args)!;

    private static T? Field<T>(Page page, string name) where T : class =>
        (T?)typeof(Page).GetField(name, PrivateInstance)!.GetValue(page);

    private static void SetProperty(Page page, string name, object value) =>
        typeof(Page).GetProperty(name, PrivateInstance)!.SetValue(page, value);

    private static void SetField(Page page, string name, object? value) =>
        typeof(Page).GetField(name, PrivateInstance)!.SetValue(page, value);

    // ---- the audit log ------------------------------------------------------------------------

    private List<JsonElement> AuditRows()
    {
        var folder = Path.Combine(_temp.Path, "ExchangeAdminWeb");
        if (!Directory.Exists(folder))
            return [];

        return Directory.GetFiles(folder, "exchangeadmin_*.jsonl")
            .Where(file => !Path.GetFileNameWithoutExtension(file).EndsWith("_trace", StringComparison.OrdinalIgnoreCase))
            .SelectMany(File.ReadAllLines)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .Where(row => row.TryGetProperty("eventType", out var type) && type.GetString() == "audit")
            .ToList();
    }
}
