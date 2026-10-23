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

        // Lowered all the same: a load that may not publish must still release the view.
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

    private static void SetProperty(Page page, string name, object value) =>
        typeof(Page).GetProperty(name, PrivateInstance)!.SetValue(page, value);

    private static void SetField(Page page, string name, object? value) =>
        typeof(Page).GetField(name, PrivateInstance)!.SetValue(page, value);

    /// <summary>
    /// The vault: holds the first request until released, then answers every request with a server
    /// error, so the credential fetch fails and the member read is refused before any directory call.
    /// </summary>
    private sealed class GatedVault : HttpMessageHandler
    {
        private int _requests;

        public TaskCompletionSource FirstArrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _requests) == 1)
            {
                FirstArrived.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }
    }
}
