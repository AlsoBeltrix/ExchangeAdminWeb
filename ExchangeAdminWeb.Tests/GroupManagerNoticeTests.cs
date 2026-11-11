using System.Management.Automation.Runspaces;
using System.Reflection;
using System.Text.Json;
using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Page = ExchangeAdminWeb.Components.Pages.GroupManagement;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// kd-92. Group Management's manager notice (aud-a25) read the manager's <c>objectClass</c> as a list and looked for an
/// element "user". The AD module returns it as a string - the object's most specific class - which enumerates as its
/// characters, so no manager ever matched: every successful change logged "is not a user (objectClass: u,s,e,r)", filed a
/// Failed <c>&lt;action&gt;_ManagerNotNotified</c> row and sent nothing. A group with no manager filed that Failed row too.
/// </summary>
/// <remarks>
/// Behaviour, not text: the real <see cref="GroupManagementService.GetGroupManagerAsync"/> over a local session whose
/// Import-Module and Get-ADObject are functions (the <c>CreateDirectoryRunspace</c> seam), and the page's own
/// <c>NotifyGroupManagerAsync</c> driven by reflection over it, a recording mail service and the real audit log. No
/// directory, no mail server, no secret store. The notice's template is not tested here and was not changed (the sweep
/// flags it; the record carries it).
/// </remarks>
public sealed class GroupManagerNoticeTests : IDisposable
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string ManagerDn = "CN=Pat Manager,OU=Users,DC=contoso,DC=test";
    private const string ManagerMail = "pat.manager@contoso.test";
    private const string Operator = "CONTOSO\\operator";

    private readonly TempDir _temp = new();
    private readonly AuditService _audit;

    public GroupManagerNoticeTests()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Audit:LogRoot"] = _temp.Path,
        }).Build();
        var log = new JsonlLogService(config, NullLogger<JsonlLogService>.Instance);
        _audit = new AuditService(log, new OperationTraceService(config, log));
    }

    public void Dispose() => _temp.Dispose();

    private static GroupInfo Group(string managedBy) => new()
    {
        Name = "Finance Team",
        Identity = "CN=Finance Team,OU=Groups,DC=contoso,DC=test",
        SamAccountName = "FinanceTeam",
        ManagedBy = managedBy,
    };

    // ---- the service: the manager's class, as the directory reports it -------------------------

    /// <summary>The class as Get-ADObject reports it (a string), and as the multi-valued attribute would be read.</summary>
    public static TheoryData<string, object, bool> Classes() => new()
    {
        { "a string: a user", "user", true },
        { "the multi-valued attribute: a user", new[] { "top", "person", "organizationalPerson", "user" }, true },
        { "a string: a group", "group", false },
        { "the multi-valued attribute: a group", new[] { "top", "group" }, false },
        { "the multi-valued attribute: a computer, a user by descent", new[] { "top", "person", "organizationalPerson", "user", "computer" }, false },
    };

    [Theory]
    [MemberData(nameof(Classes))]
    public async Task TheManagersClass_IsReadAsTheDirectoryReportsIt(string shape, object objectClass, bool isAUser)
    {
        var calls = new List<string>();
        var service = new StandInGroups(() => DirectorySession(objectClass, calls));

        var manager = await service.GetGroupManagerAsync(Group(ManagerDn));

        Assert.True((manager is not null) == isAUser, $"{shape}: {(isAUser ? "the manager is a user and is the recipient" : "the manager is no recipient")}");
        if (manager is not null)
        {
            Assert.Equal(ManagerMail, manager.Email);
            Assert.Equal("Pat Manager", manager.DisplayName);
        }

        Assert.Equal([$"Get-ADObject|{ManagerDn}|contoso.test"], calls);
    }

    // ---- the page: who hears about a change, and what is filed ---------------------------------

    // The finding. A change to a group whose manager is a user mails that manager, once, at the address the directory
    // gave, and files no gap. Before, the class read as characters, the notice was refused and a Failed row was filed.
    [Fact]
    public async Task AChangeToAGroupManagedByAUser_MailsTheManager_AndFilesNoGap()
    {
        var calls = new List<string>();
        var email = new RecordingEmail();
        var page = PageFor(new StandInGroups(() => DirectorySession("user", calls)), email, new CapturingLogger<Page>());

        await Notify(page, Group(ManagerDn));

        var sent = Assert.Single(email.Sent);
        Assert.Equal(ManagerMail, sent.Recipients);
        Assert.Equal("GroupManagement_AddMember", sent.Action);
        Assert.Equal("Pat Manager", sent.Details["Manager"]);
        Assert.Empty(AuditRows());
    }

    // A group that names no manager has no one to tell, and that is not a failure: one Information line, no audit row, no
    // mail, and no directory read at all. Before, it filed a Failed row on every change.
    [Fact]
    public async Task AChangeToAGroupWithNoManager_FilesNothing_AndSaysSoAtInformation()
    {
        var calls = new List<string>();
        var email = new RecordingEmail();
        var logger = new CapturingLogger<Page>();
        var page = PageFor(new StandInGroups(() => DirectorySession("user", calls)), email, logger);

        await Notify(page, Group(managedBy: ""));

        Assert.Empty(email.Sent);
        Assert.Empty(AuditRows());
        Assert.Empty(calls);
        var line = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Equal("Group Finance Team names no manager (managedBy is empty); no change summary was sent for GroupManagement_AddMember.", line.Message);
    }

    // The rule that stays: a managedBy that names something other than a user - a mail-enabled group would mail its every
    // member - is no recipient, and the gap is the durable Failed row it always was.
    [Fact]
    public async Task AManagerThatIsAGroup_IsNotMailed_AndTheGapIsFiled()
    {
        var calls = new List<string>();
        var email = new RecordingEmail();
        var page = PageFor(new StandInGroups(() => DirectorySession("group", calls)), email, new CapturingLogger<Page>());

        await Notify(page, Group(ManagerDn));

        Assert.Empty(email.Sent);
        var row = Assert.Single(AuditRows());
        Assert.Equal("GroupManagement_AddMember_ManagerNotNotified", row.GetProperty("action").GetString());
        Assert.Equal("Failed", row.GetProperty("result").GetString());
        Assert.Contains("does not resolve to an object with a mailbox", row.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    // ---- the stand-ins ----------------------------------------------------------------------------

    /// <summary>The service with its directory session and its credential replaced.</summary>
    private sealed class StandInGroups(Func<Runspace> open)
        : GroupManagementService(null!, null!, null!, NullLogger<GroupManagementService>.Instance)
    {
        internal override Runspace CreateDirectoryRunspace() => open();

        internal override Task<(string username, string password, string domain)?> GetCredentialsAsync(string purpose) =>
            Task.FromResult<(string username, string password, string domain)?>(("svc-groups", "not-a-secret", "CONTOSO"));
    }

    /// <summary>
    /// A local session: Import-Module does nothing, and Get-ADObject notes the call and answers the manager with
    /// <paramref name="objectClass"/> as its objectClass - a string, or the values of the multi-valued attribute.
    /// </summary>
    private static Runspace DirectorySession(object objectClass, List<string> calls)
    {
        var state = InitialSessionState.CreateDefault();
        state.Variables.Add(new SessionStateVariableEntry("PSModuleAutoloadingPreference", "None", "no autoloading"));
        state.Variables.Add(new SessionStateVariableEntry("DirectoryCalls", calls, "the calls the session took"));
        state.Variables.Add(new SessionStateVariableEntry("ManagerClass", objectClass, "the class the directory reports"));
        state.Commands.Add(new SessionStateFunctionEntry("Import-Module", "[CmdletBinding()] param([Parameter(Position = 0)] $Name)"));
        state.Commands.Add(new SessionStateFunctionEntry("Get-ADObject", $$"""
            [CmdletBinding()] param($Identity, $Properties, $Credential, $Server)
            $global:DirectoryCalls.Add("Get-ADObject|$Identity|$Server")
            [pscustomobject]@{ mail = '{{ManagerMail}}'; displayName = 'Pat Manager'; objectClass = $global:ManagerClass }
            """));
        return RunspaceFactory.CreateRunspace(state);
    }

    /// <summary>A mail service that sends nothing and records what it was asked to send.</summary>
    private sealed class RecordingEmail() : EmailService(new ConfigurationBuilder().Build(), NullLogger<EmailService>.Instance)
    {
        public List<(string Recipients, string Action, IReadOnlyDictionary<string, string> Details)> Sent { get; } = [];

        public override Task<NotificationDispatch> SendModuleNotificationAsync(string recipients, string performedBy, string ipAddress,
            string action, bool success, string ticketNumber, IReadOnlyDictionary<string, string> details, string? errorDetail = null)
        {
            Sent.Add((recipients, action, details));
            return Task.FromResult(new NotificationDispatch(1, 1, null));
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    // ---- the page -----------------------------------------------------------------------------------

    private Page PageFor(GroupManagementService service, EmailService email, ILogger<Page> logger)
    {
        var page = new Page();
        SetProperty(page, "GroupService", service);
        SetProperty(page, "Email", email);
        SetProperty(page, "Audit", _audit);
        SetProperty(page, "Logger", logger);
        typeof(Page).GetField("currentUser", PrivateInstance)!.SetValue(page, Operator);
        typeof(Page).GetField("clientIpAddress", PrivateInstance)!.SetValue(page, "10.0.0.5");
        return page;
    }

    private static Task Notify(Page page, GroupInfo group) =>
        (Task)typeof(Page).GetMethod("NotifyGroupManagerAsync", PrivateInstance)!
            .Invoke(page, [group, "GroupManagement_AddMember", "INC0042", "Added: Jo Doe", true])!;

    private static void SetProperty(Page page, string name, object value) =>
        typeof(Page).GetProperty(name, PrivateInstance)!.SetValue(page, value);

    private List<JsonElement> AuditRows()
    {
        var folder = Path.Combine(_temp.Path, "ExchangeAdminWeb");
        if (!System.IO.Directory.Exists(folder))
            return [];

        return System.IO.Directory.GetFiles(folder, "exchangeadmin_*.jsonl")
            .Where(file => !Path.GetFileNameWithoutExtension(file).EndsWith("_trace", StringComparison.OrdinalIgnoreCase))
            .SelectMany(File.ReadAllLines)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .Where(row => row.TryGetProperty("eventType", out var type) && type.GetString() == "audit")
            .ToList();
    }
}
