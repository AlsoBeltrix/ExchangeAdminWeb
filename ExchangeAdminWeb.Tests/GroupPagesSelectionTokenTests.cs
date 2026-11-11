using System.Reflection;
using System.Security.Claims;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Progress;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using GmPage = ExchangeAdminWeb.Components.Pages.GroupManagement;
using M365Page = ExchangeAdminWeb.Components.Pages.M365GroupManagement;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// sweep-d item 22. A load that finishes after the operator has moved on must not publish into the
/// selection: on M365 Groups a late details or member answer switched the header back to the previous
/// group, and the next member operation acted on it; on AD Group Management every refresh checked the
/// selection BEFORE its await and published AFTER it. The kd-19 Self-Service Groups shape: a monotonic
/// selection token compared after every await, and Select refused while a load runs.
/// </summary>
/// <remarks>
/// The compiled pages' handlers are driven by reflection (the kd-4 page-handler pattern), with a
/// stand-in service whose reads wait on a gate the test opens, so a second click can be dispatched
/// while the first handler is genuinely suspended - the stale-attribute window the disabled buttons
/// cannot close.
/// </remarks>
public sealed class GroupPagesSelectionTokenTests : IDisposable
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string Ticket = "INC0042";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly TempDir _temp = new();
    private readonly IConfiguration _config;
    private readonly AuditService _audit;
    private readonly ActivityProgressService _progress = new();

    public GroupPagesSelectionTokenTests()
    {
        _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Audit:LogRoot"] = _temp.Path,
        }).Build();
        var log = new JsonlLogService(_config, NullLogger<JsonlLogService>.Instance);
        _audit = new AuditService(log, new OperationTraceService(_config, log));
    }

    public void Dispose()
    {
        _progress.Dispose();
        _temp.Dispose();
    }

    // ---- M365 Groups ------------------------------------------------------------------------

    private static readonly M365Group Alpha = new() { Id = "a1a1a1a1-0000-0000-0000-000000000001", DisplayName = "Alpha" };
    private static readonly M365Group Beta = new() { Id = "b2b2b2b2-0000-0000-0000-000000000002", DisplayName = "Beta" };

    [Fact]
    public async Task M365_ASecondView_WhileTheFirstGroupLoads_IsRefused()
    {
        var groups = new M365Groups { DetailsGate = new Gate(Alpha.Id) };
        var page = M365PageFor(groups);

        var first = Invoke(page, "SelectGroup", Alpha);
        await groups.DetailsGate.Entered.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);

        await Invoke(page, "SelectGroup", Beta);

        Assert.DoesNotContain(groups.Calls, call => call.EndsWith(Beta.Id, StringComparison.Ordinal));
        Assert.Same(Alpha, Field<M365Group>(page, "selectedGroup"));

        groups.DetailsGate.Open();
        await first;
        Assert.Equal(Alpha.Id, Field<M365Group>(page, "selectedGroup")!.Id);
        Assert.NotNull(Field<List<M365GroupMember>>(page, "members"));
    }

    [Theory]
    [InlineData("details")]
    [InlineData("members")]
    public async Task M365_ALateLoad_AfterASearch_DoesNotPutTheGroupBack(string gated)
    {
        var groups = new M365Groups();
        if (gated == "details") groups.DetailsGate = new Gate(Alpha.Id);
        else groups.MembersGate = new Gate(Alpha.Id);
        var gate = groups.DetailsGate ?? groups.MembersGate!;
        var page = M365PageFor(groups);

        var load = Invoke(page, "SelectGroup", Alpha);
        await gate.Entered.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);

        SetField(page, "searchTerm", "finance");
        await Invoke(page, "SearchGroups");
        Assert.Null(Field<M365Group>(page, "selectedGroup"));

        gate.Open();
        await load;

        // The late answer publishes nothing: no header, no member table to act on.
        Assert.Null(Field<M365Group>(page, "selectedGroup"));
        Assert.Null(Field<List<M365GroupMember>>(page, "members"));
        Assert.Null(Field<List<M365GroupMember>>(page, "owners"));
        Assert.False(Field<bool>(page, "isLoadingDetails"));
    }

    [Fact]
    public async Task M365_AMemberOperationsReload_AfterASearch_PublishesNothing()
    {
        var groups = new M365Groups { MembersGate = new Gate(Alpha.Id) };
        var page = M365PageFor(groups);
        SetField(page, "selectedGroup", Alpha);
        SetField(page, "members", new List<M365GroupMember>());
        SetField(page, "owners", new List<M365GroupMember>());
        SetField(page, "memberTicketNumber", Ticket);

        Func<M365Group, ClaimsPrincipal, Task<M365GroupResult>> op =
            (_, _) => Task.FromResult(new M365GroupResult { Success = true, Message = "Added." });
        var write = Invoke(page, "RunMemberOpAsync", "M365GroupManagement_AddMember", "jo@contoso.test", op, (Action)(() => { }));
        await groups.MembersGate.Entered.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);

        SetField(page, "searchTerm", "finance");
        await Invoke(page, "SearchGroups");

        groups.MembersGate.Open();
        await write;

        Assert.Null(Field<M365Group>(page, "selectedGroup"));
        Assert.Null(Field<List<M365GroupMember>>(page, "members"));
        Assert.Null(Field<List<M365GroupMember>>(page, "owners"));
    }

    // ---- AD Group Management ----------------------------------------------------------------

    private static readonly GroupInfo Finance = new()
    {
        Name = "Finance Team",
        Identity = "CN=Finance Team,OU=Groups,DC=contoso,DC=test",
        SamAccountName = "FinanceTeam",
    };

    private static readonly GroupInfo Payroll = new()
    {
        Name = "Payroll",
        Identity = "CN=Payroll,OU=Groups,DC=contoso,DC=test",
        SamAccountName = "Payroll",
    };

    private static readonly GroupMemberInfo Jo = new()
    {
        DisplayName = "Jo Doe",
        Email = "jo.doe@contoso.test",
        ObjectGuid = "3c4d5e6f-7081-92a3-b4c5-d6e7f8091a2b",
        DistinguishedName = "CN=Jo Doe,OU=Users,DC=contoso,DC=test",
        MemberKind = "User",
    };

    [Fact]
    public async Task Gm_AManageClick_WhileAMemberLoadRuns_IsRefused()
    {
        var groups = new GmGroups { MembersGate = new Gate(Finance.Identity) };
        var page = GmPageFor(groups);
        SetField(page, "selectedGroup", Finance);

        var load = Invoke(page, "LoadMembers");
        await groups.MembersGate.Entered.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);

        await Invoke(page, "SelectGroup", Payroll);

        Assert.DoesNotContain(groups.Calls, call => call.EndsWith(Payroll.Identity, StringComparison.Ordinal));
        Assert.Same(Finance, Field<GroupInfo>(page, "selectedGroup"));

        groups.MembersGate.Open();
        await load;
        Assert.NotNull(Field<GroupMemberList>(page, "memberList"));
    }

    // The site the sweep names at :516 - and the A -> B -> A case the reference check alone let
    // through: Search, then Manage on the same GroupInfo object, while the first load was suspended.
    [Fact]
    public async Task Gm_ALateMemberLoad_AfterTheSameGroupIsPickedAgain_PublishesNothing()
    {
        var groups = new GmGroups();
        var page = GmPageFor(groups);
        await Invoke(page, "SelectGroup", Finance);
        groups.MembersGate = new Gate(Finance.Identity);

        var load = Invoke(page, "LoadMembers");
        await groups.MembersGate.Entered.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);

        await ReselectTheSameGroup(page);

        groups.MembersGate.Open();
        await load;

        Assert.Same(Finance, Field<GroupInfo>(page, "selectedGroup"));
        Assert.Null(Field<GroupMemberList>(page, "memberList"));
    }

    public static TheoryData<string> GmWrites() => new() { "AddMember", "RemoveMember", "RemoveSelectedAsync", "AddResolvedAsync" };

    // The four write handlers' refreshes (:593-594, :717-718, :997-999, :1146-1148).
    [Theory]
    [MemberData(nameof(GmWrites))]
    public async Task Gm_AWritesLateRefresh_AfterTheSameGroupIsPickedAgain_PublishesNothing(string write)
    {
        var groups = new GmGroups();
        var page = GmPageFor(groups);
        await Invoke(page, "SelectGroup", Finance);
        StageWrites(page);
        groups.MembersGate = new Gate(Finance.Identity);

        var task = write == "RemoveMember" ? Invoke(page, write, Jo) : Invoke(page, write);
        await groups.MembersGate.Entered.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);
        Assert.Contains(groups.Calls, call => call.StartsWith(write.Contains("Add", StringComparison.Ordinal) ? "add:" : "remove:", StringComparison.Ordinal));

        await ReselectTheSameGroup(page);

        groups.MembersGate.Open();
        await task;

        Assert.Same(Finance, Field<GroupInfo>(page, "selectedGroup"));
        Assert.Null(Field<GroupMemberList>(page, "memberList"));
        Assert.Null(Field<List<BulkRowOutcome>>(page, "bulkOutcome"));
    }

    [Theory]
    [MemberData(nameof(GmWrites))]
    public async Task Gm_AWritesRefresh_StillPublishes_WhileItsGroupIsTheSelection(string write)
    {
        // The control: nothing moved, so the refresh lands. Without it the test above could pass on
        // a page that never refreshed at all.
        var groups = new GmGroups();
        var page = GmPageFor(groups);
        await Invoke(page, "SelectGroup", Finance);
        StageWrites(page);

        await (write == "RemoveMember" ? Invoke(page, write, Jo) : Invoke(page, write));

        Assert.Equal("refreshed", Field<GroupMemberList>(page, "memberList")!.GroupName);
    }

    private static async Task ReselectTheSameGroup(GmPage page)
    {
        SetField(page, "searchTerm", "finance");
        await Invoke(page, "Search");
        Assert.Null(Field<GroupInfo>(page, "selectedGroup"));
        await Invoke(page, "SelectGroup", Finance);
        Assert.Same(Finance, Field<GroupInfo>(page, "selectedGroup"));
    }

    private static void StageWrites(GmPage page)
    {
        SetField(page, "ticketNumber", Ticket);
        SetField(page, "newMember", Jo.Email);
        SetField(page, "memberList", new GroupMemberList { GroupName = Finance.Name, Members = [Jo] });
        Field<HashSet<string>>(page, "selectedGuids")!.Add(Jo.ObjectGuid);
        SetField(page, "resolution", new List<BulkIdentityList.Resolution>
        {
            new(new BulkIdentityList.Line(1, "jdoe"), BulkIdentityList.Status.Resolved,
                new BulkIdentityList.Candidate(Jo.DistinguishedName, "user", "jdoe", Jo.DisplayName,
                    Jo.Email, "jdoe", Jo.Email, Jo.ObjectGuid),
                ""),
        });
    }

    // ---- harness ----------------------------------------------------------------------------

    private M365Page M365PageFor(M365Groups groups)
    {
        var page = new M365Page();
        SetProperty(page, "GroupService", groups);
        SetProperty(page, "Audit", _audit);
        SetProperty(page, "Email", Substitute.For<EmailService>(_config, NullLogger<EmailService>.Instance));
        SetProperty(page, "AuthStateProvider", Authentication());
        SetProperty(page, "AuthorizationService", Allowing());
        SetProperty(page, "Logger", NullLogger<M365Page>.Instance);
        SetProperty(page, "Progress", _progress);
        SetProperty(page, "ServiceNow", DormantServiceNow());
        SetField(page, "currentUser", "CONTOSO\\operator");
        SetField(page, "clientIpAddress", "10.0.0.5");
        return page;
    }

    private GmPage GmPageFor(GmGroups groups)
    {
        var page = new GmPage();
        SetProperty(page, "GroupService", groups);
        SetProperty(page, "Audit", _audit);
        SetProperty(page, "Email", Substitute.For<EmailService>(_config, NullLogger<EmailService>.Instance));
        SetProperty(page, "AuthStateProvider", Authentication());
        SetProperty(page, "AuthorizationService", Allowing());
        SetProperty(page, "Logger", NullLogger<GmPage>.Instance);
        SetProperty(page, "Progress", _progress);
        SetProperty(page, "ServiceNow", DormantServiceNow());
        SetField(page, "currentUser", "CONTOSO\\operator");
        SetField(page, "clientIpAddress", "10.0.0.5");
        return page;
    }

    private static AuthenticationStateProvider Authentication()
    {
        var authState = Substitute.For<AuthenticationStateProvider>();
        authState.GetAuthenticationStateAsync().Returns(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "CONTOSO\\operator")], "Negotiate"))));
        return authState;
    }

    private static IAuthorizationService Allowing()
    {
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(Task.FromResult(AuthorizationResult.Success()));
        return authorization;
    }

    private ServiceNowService DormantServiceNow()
    {
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());
        return new ServiceNowService(_config, NullLogger<ServiceNowService>.Instance, http);
    }

    private static Task Invoke(object page, string method, params object[] args) =>
        (Task)page.GetType().GetMethod(method, PrivateInstance)!.Invoke(page, args.Length == 0 ? null : args)!;

    private static void SetProperty(object page, string name, object value) =>
        page.GetType().GetProperty(name, PrivateInstance)!.SetValue(page, value);

    private static void SetField(object page, string name, object? value) =>
        page.GetType().GetField(name, PrivateInstance)!.SetValue(page, value);

    private static T? Field<T>(object page, string name) =>
        (T?)page.GetType().GetField(name, PrivateInstance)!.GetValue(page);

    /// <summary>A read that waits until the test opens it, and says when it has been reached.</summary>
    private sealed class Gate(string key)
    {
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Key { get; } = key;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Open() => _open.TrySetResult();

        public async Task PassAsync(string key)
        {
            if (!string.Equals(key, Key, StringComparison.OrdinalIgnoreCase))
                return;
            Entered.TrySetResult();
            await _open.Task;
        }
    }

    private sealed class M365Groups() : M365GroupManagementService(
        null!, null!, null!, null!, null!, NullLogger<M365GroupManagementService>.Instance)
    {
        private readonly object _lock = new();
        private readonly List<string> _calls = [];
        public Gate? DetailsGate { get; set; }
        public Gate? MembersGate { get; set; }
        public IReadOnlyList<string> Calls { get { lock (_lock) return _calls.ToList(); } }

        private void Record(string call) { lock (_lock) _calls.Add(call); }

        public override Task<List<M365Group>> SearchGroupsAsync(string searchTerm) => Task.FromResult(new List<M365Group>());

        public override async Task<M365Group?> GetGroupDetailsAsync(string groupId)
        {
            Record("details:" + groupId);
            if (DetailsGate is { } gate) await gate.PassAsync(groupId);
            return new M365Group { Id = groupId, DisplayName = "Details of " + groupId };
        }

        public override async Task<List<M365GroupMember>> GetMembersAsync(string groupId)
        {
            Record("members:" + groupId);
            if (MembersGate is { } gate) await gate.PassAsync(groupId);
            return [new M365GroupMember { Id = "m-" + groupId, DisplayName = "Member of " + groupId }];
        }

        public override Task<List<M365GroupMember>> GetOwnersAsync(string groupId)
        {
            Record("owners:" + groupId);
            return Task.FromResult(new List<M365GroupMember>());
        }
    }

    private sealed class GmGroups() : GroupManagementService(null!, null!, null!, NullLogger<GroupManagementService>.Instance)
    {
        private readonly object _lock = new();
        private readonly List<string> _calls = [];
        public Gate? MembersGate { get; set; }
        public IReadOnlyList<string> Calls { get { lock (_lock) return _calls.ToList(); } }

        private void Record(string call) { lock (_lock) _calls.Add(call); }

        public override Task<List<GroupInfo>> SearchGroupsAsync(string searchTerm) => Task.FromResult(new List<GroupInfo>());

        public override Task<TargetProtectionCheck> CheckTargetProtectionAsync(
            string groupIdentity, string? samAccountName, ClaimsPrincipal? actingUser)
        {
            Record("protection:" + groupIdentity);
            return Task.FromResult(new TargetProtectionCheck(false, null, null));
        }

        public override async Task<GroupMemberList> GetMembersAsync(string groupIdentity, ClaimsPrincipal? actingUser, string? samAccountName = null)
        {
            Record("members:" + groupIdentity);
            if (MembersGate is { } gate) await gate.PassAsync(groupIdentity);
            return new GroupMemberList { GroupName = "refreshed", Members = [Jo] };
        }

        public override Task<PermissionResult> AddMemberAsync(string groupIdentity, string member, ClaimsPrincipal? actingUser, string? samAccountName = null, string? memberDn = null)
        {
            Record("add:" + groupIdentity);
            return Task.FromResult(PermissionResult.NoChange("Already a member."));
        }

        public override Task<PermissionResult> RemoveMemberAsync(string groupIdentity, string member, ClaimsPrincipal? actingUser, string? samAccountName = null, string? memberObjectGuid = null, string? memberDnHint = null)
        {
            Record("remove:" + groupIdentity);
            return Task.FromResult(PermissionResult.NoChange("Not a member."));
        }
    }
}
