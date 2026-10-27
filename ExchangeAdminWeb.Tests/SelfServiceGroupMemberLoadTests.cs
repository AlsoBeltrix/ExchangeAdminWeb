using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.RegularExpressions;
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
/// kd-19. Two Manage members clicks on different groups inside one round trip used to run two
/// member loads, and whichever finished last filled the table - so the second group's view could
/// list the first group's members, with Remove acting on the second group. SelectGroup now refuses
/// while the view is busy, and a load publishes only while its group is still the one shown.
/// </summary>
/// <remarks>
/// The member read is real and is held where it first leaves the process: the credential fetch.
/// Behind the page are the real <see cref="SelfServiceGroupService"/>, <see cref="ModuleCredentialService"/>
/// and <see cref="DelineaService"/>, with a vault stand-in that holds the first request until the
/// test releases it and then answers every request with a server error - so each load ends with no
/// credential, refused before any directory is touched, and the page shows the load as failed. The
/// handlers are driven by reflection with their injected services set, the kd-4 page-handler pattern.
/// <para>
/// kd-51. Back to groups retires a load that is still running - the token it publishes under and the
/// busy flag it holds - so a backend that never answers no longer leaves every way back into a group
/// refused until the browser is refreshed; and a load lowers the flag only under its own token, so a
/// retired one returning late cannot free the view while a newer load is still reading.
/// </para>
/// </remarks>
public sealed class SelfServiceGroupMemberLoadTests : IDisposable
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string CallerSid = "S-1-5-21-1000-2000-3000-1001";
    private const string LoadFailed =
        "The group's members could not be loaded right now. Please try again; if the problem persists, contact the IT Support Desk.";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly TempDir _temp = new();
    private readonly GatedVault _vault = new();
    private readonly SelfServiceGroupService _groups;
    private readonly AuditService _audit;
    private readonly EmailService _email;
    private readonly ActivityProgressService _progress = new();

    public SelfServiceGroupMemberLoadTests()
    {
        Directory.CreateDirectory(Path.Combine(_temp.Path, "config"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Audit:LogRoot"] = _temp.Path,
            ["Delinea:SecretServerUrl"] = "https://vault.local",
        }).Build();

        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_temp.Path);
        var catalog = new ModuleCatalog();
        var moduleConfig = new ModuleConfigService(
            catalog, env, TestConfigStore.CreateModuleConfig(_temp.Path), NullLogger<ModuleConfigService>.Instance);
        moduleConfig.SaveModuleConfig("SelfServiceGroups", new Dictionary<string, string> { ["DelineaSecretId"] = "101" });

        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(_vault, disposeHandler: false));
        var log = new JsonlLogService(config, NullLogger<JsonlLogService>.Instance);
        var trace = new OperationTraceService(config, log);
        var extendedLog = new ExtendedLogService(
            config, env, TestConfigStore.CreateAppSettings(_temp.Path), NullLogger<ExtendedLogService>.Instance);
        var delinea = new DelineaService(
            http, config, NullLogger<DelineaService>.Instance, extendedLog, trace, _ => ("fixture-client", "fixture-key"));
        var protectedPrincipals = new ProtectedPrincipalService(
            env, config, moduleConfig, TestConfigStore.CreateProtectedPrincipal(_temp.Path), delinea,
            NullLogger<ProtectedPrincipalService>.Instance);
        var sectionAccess = new SectionAccessService(
            config, NullLogger<SectionAccessService>.Instance, env, catalog,
            new ExchangeAdminWeb.Services.Storage.SectionAccessRepository(TestConfigStore.Create(_temp.Path)));
        var servicers = new ProtectedPrincipalServicerService(
            sectionAccess, NullLogger<ProtectedPrincipalServicerService>.Instance);

        _audit = new AuditService(log, trace);
        _email = Substitute.For<EmailService>(config, NullLogger<EmailService>.Instance);
        _groups = new SelfServiceGroupService(
            new ModuleCredentialService(moduleConfig, delinea, NullLogger<ModuleCredentialService>.Instance),
            protectedPrincipals, servicers, NullLogger<SelfServiceGroupService>.Instance);
    }

    public void Dispose()
    {
        _vault.Release.TrySetResult();
        _vault.ReleaseHeld.TrySetResult();
        _progress.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public async Task ASecondManageClickWhileTheFirstGroupIsLoading_IsRefused_AndTheFirstGroupGetsItsOwnOutcome()
    {
        var (first, second) = (Group("Finance Team"), Group("Payroll Team"));
        var page = PageBrowsing();

        var opening = Select(page, first);
        await _vault.FirstArrived.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

        // The second click, landing in the round trip before the disabled attribute reaches the
        // browser: refused at once, rather than queued behind the first group's read.
        var reopening = Select(page, second);
        Assert.Same(first, Field<ManageableGroup>(page, "selected"));
        Assert.True(reopening.IsCompleted, "the second Manage click was not refused while the first group loaded");

        _vault.Release.SetResult();
        await opening.WaitAsync(Patience, TestContext.Current.CancellationToken);

        // The control: a load whose group is still shown does publish, into its own view.
        Assert.Same(first, Field<ManageableGroup>(page, "selected"));
        Assert.Equal(LoadFailed, Field<string>(page, "memberLoadError"));
        Assert.False(Flag(page, "isLoadingMembers"));

        // And the refusal does not outlive the load: back, and the second group opens.
        Invoke(page, "BackToGroups");
        await Select(page, second).WaitAsync(Patience, TestContext.Current.CancellationToken);
        Assert.Same(second, Field<ManageableGroup>(page, "selected"));
    }

    [Fact]
    public async Task ALoadThatFinishesAfterTheOperatorWentBack_PublishesNothing()
    {
        var page = PageBrowsing();

        var opening = Select(page, Group("Finance Team"));
        await _vault.FirstArrived.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Invoke(page, "BackToGroups");
        _vault.Release.SetResult();
        await opening.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Null(Field<ManageableGroup>(page, "selected"));
        Assert.Null(Field<string>(page, "memberLoadError"));
        Assert.Empty(Field<IReadOnlyList<GroupMember>>(page, "groupMembers")!);

        // Not busy afterwards. Since kd-51 it is Back to groups that lowers the flag, when it retires
        // the load, and the load's late return leaves it lowered.
        Assert.False(Flag(page, "isLoadingMembers"));
    }

    [Fact]
    public async Task AMemberLoadThatNeverReturns_DoesNotStrandTheManageView()
    {
        // kd-51. The load raises isLoadingMembers, a member of IsManageBusy, and only its own finally
        // lowered it - so a backend that never answered left both Manage members buttons disabled and
        // SelectGroup refusing (kd-19's guard) until the browser was refreshed. The vault holds this
        // load's credential fetch and answers it only at the end, so every assertion about the view is
        // taken while the load is still hanging.
        var (first, second) = (Group("Finance Team"), Group("Payroll Team"));
        var page = PageBrowsing();

        var hung = Select(page, first);
        await _vault.FirstArrived.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
        Assert.True(Busy(page), "the load did not hold the view busy, so there is nothing for Back to free");

        Invoke(page, "BackToGroups");

        // Free the moment the operator leaves, though the load has not returned.
        Assert.False(hung.IsCompleted, "the load returned; this test needs it hanging");
        Assert.False(Flag(page, "isLoadingMembers"));
        Assert.False(Busy(page));

        // And usable: another group opens rather than being refused, and its own load holds the view.
        var opening = Select(page, second);
        Assert.Same(second, Field<ManageableGroup>(page, "selected"));
        Assert.True(Busy(page), "the second group's load is not holding the view");
        Assert.False(hung.IsCompleted, "the first load returned before the second group was opened");

        // Answered only now, inside the test, so nothing runs on after the fixture is disposed. The
        // first load's late return publishes nothing; the second group's view gets its own outcome.
        _vault.Release.SetResult();
        await hung.WaitAsync(Patience, TestContext.Current.CancellationToken);
        await opening.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Same(second, Field<ManageableGroup>(page, "selected"));
        Assert.Equal(LoadFailed, Field<string>(page, "memberLoadError"));
        Assert.False(Busy(page));
    }

    [Fact]
    public async Task ARetiredLoadReturningLate_DoesNotLowerTheFlagOfTheLoadThatReplacedIt()
    {
        // kd-51's other half. Back to groups may lower the flag only because a load lowers it under its
        // own token. Lowered unconditionally, the retired load returning late would free the view while
        // the load that replaced it is still reading.
        var (first, second) = (Group("Finance Team"), Group("Payroll Team"));
        var page = PageBrowsing();

        var retired = Select(page, first);
        await _vault.FirstArrived.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
        Invoke(page, "BackToGroups");

        // The second group's load, held at the vault on its own: answering the first does not answer it.
        var current = _vault.HoldingItsOwnRequests(() => Select(page, second));
        Assert.Same(second, Field<ManageableGroup>(page, "selected"));

        _vault.Release.SetResult();
        await retired.WaitAsync(Patience, TestContext.Current.CancellationToken);
        await _vault.HeldArrived.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

        // The retired load has returned; the current one is mid-read at the vault and still holds the view.
        Assert.False(current.IsCompleted, "the second group's load finished; this test needs it still reading");
        Assert.True(Flag(page, "isLoadingMembers"), "the retired load lowered the flag of the load still reading");
        Assert.True(Busy(page));

        _vault.ReleaseHeld.SetResult();
        await current.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Same(second, Field<ManageableGroup>(page, "selected"));
        Assert.Equal(LoadFailed, Field<string>(page, "memberLoadError"));
        Assert.False(Flag(page, "isLoadingMembers"));
    }

    [Fact]
    public void BothOfALoadsPublishesWaitForItsToken()
    {
        // The success half cannot be driven without a directory, so its guard is pinned as text: the
        // member list and the failure message are each written only under the current-token test.
        var page = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "SelfServiceGroups.razor"));
        var start = page.IndexOf("private async Task LoadMembers()", StringComparison.Ordinal);
        Assert.True(start >= 0, "LoadMembers not found - update this tripwire.");
        var end = page.IndexOf("private async Task RemoveMember(", start, StringComparison.Ordinal);
        Assert.True(end > start, "Could not bound LoadMembers - update this tripwire.");
        var body = page[start..end];

        Assert.Matches(new Regex(@"if \(token == memberLoadToken\)\s+groupMembers = members;"), body);
        Assert.Matches(new Regex(@"if \(token == memberLoadToken\)\s+memberLoadError = loadError;"), body);
        Assert.Contains("var token = ++memberLoadToken;", body, StringComparison.Ordinal);
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

    /// <summary>The page in the browse view, signed in, with nothing open.</summary>
    private Page PageBrowsing()
    {
        var page = new Page();
        var authState = Substitute.For<AuthenticationStateProvider>();
        authState.GetAuthenticationStateAsync().Returns(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "CONTOSO\\owner")], "Negotiate"))));
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(Task.FromResult(AuthorizationResult.Success()));

        SetProperty(page, "GroupService", _groups);
        SetProperty(page, "Audit", _audit);
        SetProperty(page, "Email", _email);
        SetProperty(page, "AuthStateProvider", authState);
        SetProperty(page, "AuthorizationService", authorization);
        SetProperty(page, "Logger", NullLogger<Page>.Instance);
        SetProperty(page, "Progress", _progress);
        SetField(page, "currentUser", "CONTOSO\\owner");
        SetField(page, "clientIpAddress", "10.0.0.5");
        SetField(page, "callerSid", CallerSid);
        return page;
    }

    private static Task Select(Page page, ManageableGroup group) => (Task)Invoke(page, "SelectGroup", group)!;

    private static object? Invoke(Page page, string method, params object?[] args) =>
        typeof(Page).GetMethod(method, PrivateInstance)!.Invoke(page, args);

    private static T? Field<T>(Page page, string name) where T : class =>
        (T?)typeof(Page).GetField(name, PrivateInstance)!.GetValue(page);

    private static bool Flag(Page page, string name) =>
        (bool)typeof(Page).GetField(name, PrivateInstance)!.GetValue(page)!;

    /// <summary>The manage view's own busy predicate, which both Manage members buttons and SelectGroup consult.</summary>
    private static bool Busy(Page page) =>
        (bool)typeof(Page).GetProperty("IsManageBusy", PrivateInstance)!.GetValue(page)!;

    private static void SetProperty(Page page, string name, object value) =>
        typeof(Page).GetProperty(name, PrivateInstance)!.SetValue(page, value);

    private static void SetField(Page page, string name, object? value) =>
        typeof(Page).GetField(name, PrivateInstance)!.SetValue(page, value);

    /// <summary>
    /// The vault: holds the first request until released, then answers every request with a server
    /// error, so the credential fetch fails and the member read is refused before any directory call.
    /// </summary>
    /// <remarks>
    /// kd-51. A load started through <see cref="HoldingItsOwnRequests"/> is held apart, until
    /// <see cref="ReleaseHeld"/>. Its requests are told apart by a mark in their execution context,
    /// not by count: they queue behind the first load's token lock, and that load's own retry, made
    /// once it is released, would otherwise be taken for them.
    /// </remarks>
    private sealed class GatedVault : HttpMessageHandler
    {
        private readonly AsyncLocal<bool> _held = new();
        private int _requests;

        public TaskCompletionSource FirstArrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource HeldArrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseHeld { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Starts <paramref name="start"/> with every vault request it goes on to make held until <see cref="ReleaseHeld"/>.</summary>
        public Task HoldingItsOwnRequests(Func<Task> start)
        {
            _held.Value = true;
            try
            {
                return start();
            }
            finally
            {
                _held.Value = false;
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_held.Value)
            {
                HeldArrived.TrySetResult();
                await ReleaseHeld.Task.WaitAsync(cancellationToken);
            }
            else if (Interlocked.Increment(ref _requests) == 1)
            {
                FirstArrived.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }
    }
}
