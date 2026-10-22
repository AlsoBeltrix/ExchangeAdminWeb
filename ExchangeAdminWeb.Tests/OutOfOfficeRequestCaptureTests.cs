using System.Collections;
using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Progress;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using NSubstitute;
using Page = ExchangeAdminWeb.Components.Pages.OutOfOffice;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Audit finding aud-a7b: Out of Office must write, audit and notify the inputs it captured before
/// its first await, whatever the bound fields say by the time the awaits return.
/// </summary>
/// <remarks>
/// Behaviour, not text. A real page instance is driven through reflection (the Comms-10k idiom)
/// over a real <see cref="OutOfOfficeService"/> on a real pool, whose connect seam hands out an
/// in-process runspace carrying stub <c>Get-</c> / <c>Set-MailboxAutoReplyConfiguration</c>
/// functions; the Set stub records the parameters it was bound with. The validator is real with
/// nothing protected - it still asks a substituted resolver which mailbox the target is, because
/// the write is bound to that mailbox (aud-a2d) - the audit is real over a temp JSONL root and is
/// read back, and the email service is a substitute whose virtual admin and mailbox-owner
/// notifications are captured (the owner's since aud-a7d). The browser is a fake
/// <see cref="Microsoft.JSInterop.IJSRuntime"/> answering the page's time-zone read (aud-a7e).
/// No Exchange, no Graph.
/// </remarks>
public sealed class OutOfOfficeRequestCaptureTests : IDisposable
{
    private const string Mailbox = "alice@contoso.test";
    private const string Ticket = "INC100";
    private const string InternalText = "Away until Friday (internal)";
    private const string ExternalText = "Away until Friday (external)";
    private static readonly DateTime Start = new(2026, 11, 2, 9, 0, 0);
    private static readonly DateTime End = new(2026, 11, 6, 17, 0, 0);
    // aud-a7e. The browser's zone in every harness run unless a test names another. A fixed +05:30
    // offset, so a write that forgot to convert is off by hours, and not the zone of any server this is
    // likely to run on - the server's zone must play no part.
    private const string BrowserZone = "Asia/Kolkata";
    private static readonly DateTime StartUtc = new(2026, 11, 2, 3, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime EndUtc = new(2026, 11, 6, 11, 30, 0, DateTimeKind.Utc);
    private static readonly Guid AliceGuid = Guid.Parse("3c3c3c3c-0000-4000-8000-00000000a11c");
    private static readonly Guid MalloryGuid = Guid.Parse("3c3c3c3c-0000-4000-8000-0000000ba11d");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eaw-oof-" + Guid.NewGuid().ToString("N"));
    private readonly IList _setCalls = ArrayList.Synchronized(new ArrayList());
    private readonly IList _getCalls = ArrayList.Synchronized(new ArrayList());
    // aud-a7f. What Get-MailboxAutoReplyConfiguration reports; a test sets it before the read.
    private readonly Hashtable _read = Hashtable.Synchronized(new Hashtable { ["State"] = "Disabled", ["StartTime"] = null, ["EndTime"] = null });
    private readonly SemaphoreSlim _entered = new(0);
    private readonly ManualResetEventSlim _released = new(false);
    private readonly ActivityProgressService _progress = new();
    private ExoConnectionPool? _pool;
    private JsonlLogService? _jsonl;

    public void Dispose()
    {
        _released.Set();
        _pool?.Dispose();
        _progress.Dispose();
        _entered.Dispose();
        _released.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ---- the harness ------------------------------------------------------------------------

    /// <param name="holdAt">
    /// Where the operation pauses: "auth" (its first await), "read" (the previous-state read) or
    /// "write" (inside Set-MailboxAutoReplyConfiguration). Anything else never pauses.
    /// </param>
    private (Page Page, EmailService Email) BuildPage(string holdAt, bool authorized = true, string? browserZone = BrowserZone)
    {
        Directory.CreateDirectory(_dir);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Audit:LogRoot"] = _dir,
            ["Delinea:SecretServerUrl"] = "https://vault.invalid",
            ["ExchangeOnline:AppId"] = "11111111-1111-1111-1111-111111111111",
            ["ExchangeOnline:Organization"] = "contoso.onmicrosoft.com",
        }).Build();
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_dir);
        var store = TestConfigStore.Create(_dir);
        var catalog = new ModuleCatalog();
        var moduleConfig = new ModuleConfigService(catalog, env, new ModuleConfigRepository(store),
            Substitute.For<ILogger<ModuleConfigService>>());
        var enablement = new ModuleEnablementService(catalog, env, moduleConfig, new ModuleEnablementRepository(store),
            config, Substitute.For<ILogger<ModuleEnablementService>>());
        enablement.SaveEnablement(new Dictionary<string, bool> { [ExoConnectionPool.ConfigModuleKey] = true });
        _jsonl = new JsonlLogService(config, Substitute.For<ILogger<JsonlLogService>>());
        var trace = new OperationTraceService(config, _jsonl);

        _pool = new ExoConnectionPool(config, moduleConfig, enablement, Substitute.For<ILogger<ExoConnectionPool>>(),
            trace, (_, generation, _) => ExchangeRunspace(generation, holdAt));

        var email = Substitute.For<EmailService>(config, Substitute.For<ILogger<EmailService>>());

        var page = new Page();
        SetProperty(page, "OofService", new OutOfOfficeService(_pool, null!, Substitute.For<ILogger<OutOfOfficeService>>(), config));
        SetProperty(page, "Progress", _progress);
        SetProperty(page, "Audit", new AuditService(_jsonl, trace));
        SetProperty(page, "Email", email);
        SetProperty(page, "Validator", Validator(config, env, moduleConfig, trace));
        SetProperty(page, "AuthStateProvider", Authentication(holdFirstCall: holdAt == "auth"));
        SetProperty(page, "AuthorizationService", Authorization(authorized));
        SetProperty(page, "Logger", NullLogger<Page>.Instance);
        // aud-a7e: what the first interactive render does - ask the browser for its zone. The schedule
        // fields are set after it, because the read re-bases them to the browser's clock.
        SetProperty(page, "JS", new Browser(browserZone));
        var read = (Task)typeof(Page).GetMethod("ReadScheduleZoneAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(page, null)!;
        Assert.True(read.IsCompletedSuccessfully, "The page's time-zone read must finish without a renderer.");

        SetField(page, "currentUser", @"CONTOSO\operator");
        SetField(page, "clientIpAddress", "10.0.0.5");
        SetField(page, "setEmail", " " + Mailbox + " ");
        SetField(page, "oofState", "Scheduled");
        SetField(page, "internalMessage", InternalText);
        SetField(page, "externalMessage", ExternalText);
        SetField(page, "startTime", Start);
        SetField(page, "endTime", End);
        SetField(page, "ticketNumber", " " + Ticket + " ");
        return (page, email);
    }

    /// <summary>What an operator's edit during the operation does: every bound field moves.</summary>
    private static void EditEveryBoundField(Page page)
    {
        SetField(page, "setEmail", "mallory@contoso.test");
        SetField(page, "oofState", "Enabled");
        SetField(page, "internalMessage", "edited internal message");
        SetField(page, "externalMessage", "edited external message");
        SetField(page, "startTime", new DateTime(2027, 1, 4, 8, 0, 0));
        SetField(page, "endTime", new DateTime(2027, 1, 8, 8, 0, 0));
        SetField(page, "ticketNumber", "INC999");
        // Not bound to a control, but an input to the same capture (aud-a7e): moving it proves the
        // write and the notice use the zone the times were captured with.
        SetField(page, "scheduleZone", TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));
    }

    /// <summary>The browser, as far as the page asks it anything: its time zone, or a failure to give one.</summary>
    private sealed class Browser(string? zone) : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            identifier == "getBrowserTimeZone" && zone is not null
                ? ValueTask.FromResult((TValue)(object)zone)
                : ValueTask.FromException<TValue>(new JSException($"{identifier} could not answer"));
    }

    private static DateTime Parse(string value) => DateTime.Parse(value, CultureInfo.InvariantCulture);

    /// <summary>An instant, as a DateTime of Kind Utc.</summary>
    private static DateTime Utc(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    /// <summary>What the next status check reads: a Scheduled auto-reply with this window.</summary>
    private void ReadBack(DateTime start, DateTime end)
    {
        _read["State"] = "Scheduled";
        _read["StartTime"] = start;
        _read["EndTime"] = end;
    }

    /// <summary>The Check Status tab after a check of the mailbox: its rendered text.</summary>
    private static async Task<string> CheckedStatus(Page page)
    {
        SetField(page, "tabIndex", 0);
        SetField(page, "checkEmail", Mailbox);
        await CheckStatus(page);
        Assert.Null(GetField<OutOfOfficeResult>(page, "checkResult").Error);
        return Rendered(page).Text;
    }

    private PooledRunspace ExchangeRunspace(long generation, string holdAt)
    {
        var state = InitialSessionState.CreateDefault2();
        state.Variables.Add(new SessionStateVariableEntry("SetCalls", _setCalls, null));
        state.Variables.Add(new SessionStateVariableEntry("GetCalls", _getCalls, null));
        state.Variables.Add(new SessionStateVariableEntry("Read", _read, null));
        state.Variables.Add(new SessionStateVariableEntry("HoldAt", holdAt, null));
        state.Variables.Add(new SessionStateVariableEntry("Entered", _entered, null));
        state.Variables.Add(new SessionStateVariableEntry("Released", _released, null));
        state.Commands.Add(new SessionStateFunctionEntry("Get-MailboxAutoReplyConfiguration", """
            [CmdletBinding()]
            param([string] $Identity)
            [void]$GetCalls.Add($Identity)
            if ($HoldAt -eq 'read') { [void]$Entered.Release(); [void]$Released.Wait([TimeSpan]::FromSeconds(30)) }
            [pscustomobject]@{ AutoReplyState = $Read['State']; InternalMessage = $null; ExternalMessage = $null; StartTime = $Read['StartTime']; EndTime = $Read['EndTime'] }
            """));
        state.Commands.Add(new SessionStateFunctionEntry("Set-MailboxAutoReplyConfiguration", """
            [CmdletBinding()]
            param([string] $Identity, [string] $AutoReplyState, [string] $InternalMessage,
                  [string] $ExternalMessage, [datetime] $StartTime, [datetime] $EndTime)
            if ($HoldAt -eq 'write') { [void]$Entered.Release(); [void]$Released.Wait([TimeSpan]::FromSeconds(30)) }
            $bound = @{}
            foreach ($key in $PSBoundParameters.Keys) { if ($key -ne 'ErrorAction') { $bound[$key] = $PSBoundParameters[$key] } }
            [void]$SetCalls.Add($bound)
            """));
        var runspace = RunspaceFactory.CreateRunspace(state);
        runspace.Open();
        var ps = PowerShell.Create();
        ps.Runspace = runspace;
        return new PooledRunspace(runspace, ps, generation);
    }

    private PermissionValidator Validator(IConfiguration config, IWebHostEnvironment env, ModuleConfigService moduleConfig,
        OperationTraceService trace)
    {
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());
        var extLog = new ExtendedLogService(config, env, TestConfigStore.CreateAppSettings(_dir),
            Substitute.For<ILogger<ExtendedLogService>>());
        var delinea = new DelineaService(httpClientFactory, config, Substitute.For<ILogger<DelineaService>>(), extLog, trace);
        var protectedPrincipals = new ProtectedPrincipalService(env, config, moduleConfig,
            TestConfigStore.CreateProtectedPrincipal(_dir), delinea, Substitute.For<ILogger<ProtectedPrincipalService>>());
        var servicers = new ProtectedPrincipalServicerService(
            new SectionAccessService(config, Substitute.For<ILogger<SectionAccessService>>(), env, new ModuleCatalog(),
                new SectionAccessRepository(TestConfigStore.Create(_dir))),
            Substitute.For<ILogger<ProtectedPrincipalServicerService>>());
        // Nothing is protected, but since aud-a2d the validator still asks Exchange which mailbox the
        // target is, so the write can be bound to it. Each address names its own mailbox.
        var resolver = Substitute.For<IIdentityResolver>();
        resolver.ResolveRecipientAsync(Arg.Any<string>()).Returns((ResolvedRecipient?)null);
        resolver.ResolveRecipientAsync(Mailbox)
            .Returns(new ResolvedRecipient(Mailbox, "ext-alice", "UserMailbox", ExistsOnPrem: false, ExchangeGuid: AliceGuid));
        resolver.ResolveRecipientAsync("mallory@contoso.test")
            .Returns(new ResolvedRecipient("mallory@contoso.test", "ext-mallory", "UserMailbox", ExistsOnPrem: false, ExchangeGuid: MalloryGuid));
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var provider = Substitute.For<IServiceProvider>();
        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(provider);
        provider.GetService(typeof(IIdentityResolver)).Returns(resolver);
        return new PermissionValidator(config, moduleConfig, _pool!, protectedPrincipals, servicers,
            Substitute.For<ILogger<PermissionValidator>>(), scopeFactory);
    }

    private AuthenticationStateProvider Authentication(bool holdFirstCall)
    {
        var state = new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, @"CONTOSO\operator")], "test")));
        var calls = 0;
        var provider = Substitute.For<AuthenticationStateProvider>();
        provider.GetAuthenticationStateAsync().Returns(_ =>
            holdFirstCall && Interlocked.Increment(ref calls) == 1
                ? Task.Run(() =>
                {
                    _entered.Release();
                    _released.Wait(TimeSpan.FromSeconds(30));
                    return state;
                })
                : Task.FromResult(state));
        return provider;
    }

    private static IAuthorizationService Authorization(bool allow)
    {
        var service = Substitute.For<IAuthorizationService>();
        service.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(allow ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        return service;
    }

    private static void SetProperty(Page page, string name, object value) =>
        typeof(Page).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);

    private static void SetField(Page page, string name, object? value) =>
        typeof(Page).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);

    private static T GetField<T>(Page page, string name) =>
        (T)typeof(Page).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;

    private static Task SetOof(Page page) =>
        (Task)typeof(Page).GetMethod("SetOof", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, null)!;

    private static Task CheckStatus(Page page) =>
        (Task)typeof(Page).GetMethod("CheckStatus", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, null)!;

    private async Task PauseEditAndRelease(Page page, Task pending)
    {
        Assert.True(await _entered.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken),
            "The operation never reached its pause point - this test is stale.");
        try
        {
            EditEveryBoundField(page);
        }
        finally
        {
            _released.Set();
        }
        await pending;
    }

    private static object? Unwrap(object? value) => value is PSObject wrapped ? wrapped.BaseObject : value;

    private Hashtable SingleWrite() => Assert.IsType<Hashtable>(Assert.Single(_setCalls.Cast<object>()));

    private List<JsonElement> AuditRows()
    {
        var rows = new List<JsonElement>();
        foreach (var path in _jsonl!.GetAuditLogPaths(DateTime.Now).Where(File.Exists))
        {
            foreach (var line in File.ReadAllLines(path).Where(line => !string.IsNullOrWhiteSpace(line)))
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("eventType", out var type) && type.GetString() == "audit")
                    rows.Add(document.RootElement.Clone());
            }
        }
        return rows;
    }

    private static JsonElement SingleRow(IEnumerable<JsonElement> rows, string action) =>
        Assert.Single(rows, row => row.GetProperty("action").GetString() == action);

    private static object?[] AdminNotification(EmailService email) =>
        Assert.Single(email.ReceivedCalls(), call => call.GetMethodInfo().Name == nameof(EmailService.SendAdminNotificationAsync))
            .GetArguments();

    private static object?[] UserNotification(EmailService email) =>
        Assert.Single(email.ReceivedCalls(), call => call.GetMethodInfo().Name == nameof(EmailService.SendOofNotificationAsync))
            .GetArguments();

#pragma warning disable BL0006 // the page's compiled render tree is read directly; no renderer is involved
    /// <summary>The page as it renders now: its text, and how many schedule (datetime-local) inputs it offers.</summary>
    private static (string Text, int ScheduleInputs) Rendered(Page page)
    {
        using var builder = new RenderTreeBuilder();
        typeof(Page).GetMethod("BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, [builder]);
        var frames = builder.GetFrames();
        var text = new StringBuilder();
        var inputs = 0;
        for (var i = 0; i < frames.Count; i++)
        {
            var frame = frames.Array[i];
            if (frame.FrameType == RenderTreeFrameType.Text)
                text.Append(frame.TextContent);
            else if (frame.FrameType == RenderTreeFrameType.Markup)
                text.Append(frame.MarkupContent);
            else if (frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "type"
                     && Equals(frame.AttributeValue, "datetime-local"))
                inputs++;
        }

        return (text.ToString(), inputs);
    }
#pragma warning restore BL0006

    // ---- the guards -------------------------------------------------------------------------

    [Theory]
    [InlineData("auth")]
    [InlineData("read")]
    [InlineData("write")]
    public async Task AnEditDuringTheOperation_ChangesNeitherWhatIsWrittenNorWhatIsRecorded(string holdAt)
    {
        var (page, email) = BuildPage(holdAt);

        await PauseEditAndRelease(page, SetOof(page));

        // aud-a2d: written to the captured mailbox's GUID - the mailbox the check validated - and
        // the previous-state read is of the same mailbox. The edit moved the box to mallory, whose
        // mailbox has a GUID of its own; neither reaches the write.
        var write = SingleWrite();
        Assert.Equal(AliceGuid.ToString("D"), Unwrap(write["Identity"]));
        Assert.Equal(AliceGuid.ToString("D"), Unwrap(Assert.Single(_getCalls.Cast<object?>())));
        Assert.Equal("Scheduled", Unwrap(write["AutoReplyState"]));
        Assert.Equal(InternalText, Unwrap(write["InternalMessage"]));
        Assert.Equal(ExternalText, Unwrap(write["ExternalMessage"]));
        // aud-a7e: the captured times, as the instants they name in the captured zone.
        Assert.Equal(StartUtc, Unwrap(write["StartTime"]));
        Assert.Equal(EndUtc, Unwrap(write["EndTime"]));

        var row = SingleRow(AuditRows(), "OutOfOffice_Set");
        Assert.Equal(Mailbox, row.GetProperty("target").GetString());
        Assert.Equal(AliceGuid.ToString("D"), row.GetProperty(ValidatedMailbox.AuditKey).GetString());
        Assert.Equal(Ticket, row.GetProperty("ticket").GetString());
        Assert.Equal("Success", row.GetProperty("result").GetString());
        Assert.Equal("Scheduled", row.GetProperty("newState").GetString());

        var notice = AdminNotification(email);
        Assert.Equal("SetOutOfOffice_Scheduled", notice[2]);
        Assert.Equal(Mailbox, notice[3]);
        Assert.Equal(Ticket, notice[7]);
    }

    // aud-a7d. The mailbox owner's notification took a two-way word, "disabled" or "enabled", so a
    // Scheduled write told the owner their auto-reply "has been enabled", with no window named. Every
    // bound field moves while the write is in flight, so the owner must be told what was written,
    // not what the form says afterwards (aud-a7b's rule). The notification is rendered through the
    // service's own builder: what the page hands over is what the owner reads.
    [Theory]
    [InlineData("Scheduled")]
    [InlineData("Enabled")]
    [InlineData("Disabled")]
    public async Task TheMailboxOwnerIsToldWhatWasWritten_AScheduleWithItsWindow(string state)
    {
        var (page, email) = BuildPage("write");
        SetField(page, "oofState", state);

        await PauseEditAndRelease(page, SetOof(page));

        var notice = UserNotification(email);
        Assert.Equal(Mailbox, notice[0]);
        Assert.Equal(@"CONTOSO\operator", notice[1]);
        var request = Assert.IsType<OutOfOfficeRequest>(notice[2]);
        Assert.Equal(state, request.State);

        var (subject, body) = EmailService.BuildOofNotification((string)notice[1]!, request, DateTime.Now);
        Assert.Equal($"Your Auto-Reply Has Been {state}", subject);
        Assert.Contains($"<h2>Auto-Reply {state}</h2>", body);
        Assert.Contains($"has been <strong>{state.ToLowerInvariant()}</strong>", body);
        if (state == "Scheduled")
        {
            Assert.Contains(WebUtility.HtmlEncode(Start.ToString("MMMM dd, yyyy 'at' h:mm tt")), body);
            Assert.Contains(WebUtility.HtmlEncode(End.ToString("MMMM dd, yyyy 'at' h:mm tt")), body);
        }
        else
        {
            Assert.DoesNotContain("Until:", body);
        }
    }

    [Fact]
    public async Task ARefusal_RecordsTheCapturedTicket_AndLeavesTheFormUsable()
    {
        var (page, _) = BuildPage("auth", authorized: false);

        await PauseEditAndRelease(page, SetOof(page));

        Assert.Empty(_setCalls);
        var row = SingleRow(AuditRows(), "OutOfOffice_Denied");
        Assert.Equal(Mailbox, row.GetProperty("target").GetString());
        Assert.Equal(Ticket, row.GetProperty("ticket").GetString());
        Assert.False(GetField<bool>(page, "isSetting"), "A refusal must not leave the form disabled.");
    }

    [Fact]
    public async Task ASecondSubmitWhileOneIsInFlight_WritesNothing()
    {
        var (page, _) = BuildPage("write");
        var first = SetOof(page);
        Assert.True(await _entered.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken),
            "The first submit never reached the write - this test is stale.");

        Task second;
        try
        {
            EditEveryBoundField(page);
            second = SetOof(page);
            Assert.True(second.IsCompleted, "A second submit while one is in flight must return at once.");
        }
        finally
        {
            _released.Set();
        }
        await first;
        await second;

        Assert.Equal(AliceGuid.ToString("D"), Unwrap(SingleWrite()["Identity"]));
        SingleRow(AuditRows(), "OutOfOffice_Set");
        Assert.False(GetField<bool>(page, "isSetting"));
    }

    // aud-a7c, the read path's twin of aud-a7b: the status check used to take the lookup audit's
    // target from the box AFTER the read returned, so an edit during the read recorded a lookup of a
    // mailbox that was never read.
    [Fact]
    public async Task AStatusCheck_AuditsTheMailboxItRead_NotWhatTheBoxSaysAfterwards()
    {
        var (page, _) = BuildPage("read");
        SetField(page, "checkEmail", " " + Mailbox + " ");

        var pending = CheckStatus(page);
        Assert.True(await _entered.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken),
            "The status check never reached its read - this test is stale.");
        try
        {
            SetField(page, "checkEmail", "mallory@contoso.test");
        }
        finally
        {
            _released.Set();
        }
        await pending;

        Assert.Equal(Mailbox, Unwrap(Assert.Single(_getCalls.Cast<object?>())));
        var row = SingleRow(AuditRows(), "CheckOutOfOffice");
        Assert.Equal(Mailbox, row.GetProperty("target").GetString());
        Assert.Equal("Success", row.GetProperty("result").GetString());
        Assert.False(GetField<bool>(page, "isChecking"));
    }

    [Theory]
    [InlineData("Enabled", true, false)]
    [InlineData("Scheduled", true, true)]
    [InlineData("Disabled", false, false)]
    public void TheRequestKeepsOnlyWhatItsStateWrites(string state, bool messages, bool schedule)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(BrowserZone);
        var request = OutOfOfficeRequest.FromForm(" " + Mailbox + " ", state, InternalText, ExternalText, Start, End, zone, " " + Ticket + " ");

        Assert.Equal(Mailbox, request.Mailbox);
        Assert.Equal(Ticket, request.Ticket);
        Assert.Equal(state, request.State);
        Assert.Equal(messages ? InternalText : null, request.InternalMessage);
        Assert.Equal(messages ? ExternalText : null, request.ExternalMessage);
        Assert.Equal(schedule ? Start : null, request.StartTime);
        Assert.Equal(schedule ? End : null, request.EndTime);
        Assert.Equal(schedule ? zone : null, request.ScheduleZone);
    }

    // aud-a7e. The times are typed as wall clock in the browser's zone; Exchange must be handed the
    // instants they name, as UTC, not the unzoned values it would read in a zone of its own. New York's
    // window crosses the end of daylight saving (1 November 2026), so its two ends convert at different
    // offsets - one offset applied to both would get one of them wrong by an hour. The expected values
    // are literals, not computed by the code under test.
    [Theory]
    [InlineData("Asia/Kolkata", "2026-11-02T09:00", "2026-11-06T17:00", "2026-11-02T03:30", "2026-11-06T11:30")]
    [InlineData("America/New_York", "2026-10-30T09:00", "2026-11-06T17:00", "2026-10-30T13:00", "2026-11-06T22:00")]
    public async Task AScheduleEnteredInTheBrowsersZone_IsWrittenAsTheSameInstant_InUtc(
        string zone, string start, string end, string startUtc, string endUtc)
    {
        var (page, _) = BuildPage("none", browserZone: zone);
        SetField(page, "startTime", Parse(start));
        SetField(page, "endTime", Parse(end));

        await SetOof(page);

        var write = SingleWrite();
        var writtenStart = Assert.IsType<DateTime>(Unwrap(write["StartTime"]));
        var writtenEnd = Assert.IsType<DateTime>(Unwrap(write["EndTime"]));
        // DateTime equality ignores Kind, so the Kind is asserted on its own.
        Assert.Equal(DateTimeKind.Utc, writtenStart.Kind);
        Assert.Equal(DateTimeKind.Utc, writtenEnd.Kind);
        Assert.Equal(Parse(startUtc), writtenStart);
        Assert.Equal(Parse(endUtc), writtenEnd);
    }

    // aud-a7e, the finding as reported: the owner's notice printed From and Until as bare clock times.
    // Each now carries the zone it was entered in and that zone's offset at that time.
    [Theory]
    [InlineData("Asia/Kolkata", "2026-11-02T09:00", "2026-11-06T17:00", "UTC+05:30", "UTC+05:30")]
    [InlineData("America/New_York", "2026-10-30T09:00", "2026-11-06T17:00", "UTC-04:00", "UTC-05:00")]
    public async Task TheOwnersNotice_NamesTheZone_AndTheOffsetOfEachTime(
        string zone, string start, string end, string startOffset, string endOffset)
    {
        var (page, email) = BuildPage("none", browserZone: zone);
        SetField(page, "startTime", Parse(start));
        SetField(page, "endTime", Parse(end));

        await SetOof(page);

        var request = Assert.IsType<OutOfOfficeRequest>(UserNotification(email)[2]);
        var (_, body) = EmailService.BuildOofNotification(@"CONTOSO\operator", request, DateTime.Now);
        Assert.Contains("<strong>From:</strong> "
            + WebUtility.HtmlEncode($"{Parse(start):MMMM dd, yyyy 'at' h:mm tt} ({zone}, {startOffset})"), body);
        Assert.Contains("<strong>Until:</strong> "
            + WebUtility.HtmlEncode($"{Parse(end):MMMM dd, yyyy 'at' h:mm tt} ({zone}, {endOffset})"), body);
    }

    // aud-a7e. What the operator is told the times mean, beside the fields, and what the owner is told
    // afterwards must be the same zone - and the one the browser reported, not the server's.
    [Fact]
    public async Task ThePageLabelAndTheNotice_NameTheSameZone_TheOneTheTimesWereEnteredIn()
    {
        var (page, email) = BuildPage("none");
        SetField(page, "tabIndex", 1);

        var (text, inputs) = Rendered(page);
        Assert.Equal(2, inputs);
        var stated = Assert.Single(Regex.Matches(text, @"Times are in your browser's time zone, (?<zone>[^\s]+)\."))
            .Groups["zone"].Value;
        Assert.Equal(BrowserZone, stated);

        await SetOof(page);

        var request = Assert.IsType<OutOfOfficeRequest>(UserNotification(email)[2]);
        var (_, body) = EmailService.BuildOofNotification(@"CONTOSO\operator", request, DateTime.Now);
        var from = Regex.Match(body, "<strong>From:</strong> (?<when>[^<]+)<br>").Groups["when"].Value;
        var until = Regex.Match(body, "<strong>Until:</strong> (?<when>[^<]+)</p>").Groups["when"].Value;
        Assert.Contains($"({stated}, ", WebUtility.HtmlDecode(from), StringComparison.Ordinal);
        Assert.Contains($"({stated}, ", WebUtility.HtmlDecode(until), StringComparison.Ordinal);
    }

    // aud-a7e, fail closed. A browser that cannot say which zone it is in leaves the schedule with no
    // moment to name: the fields are not offered, the page says why, and a Scheduled submit is refused
    // before the first await - nothing read, written, audited or sent - with the form left usable.
    [Fact]
    public async Task WithoutTheBrowsersZone_AScheduleIsRefused_BeforeAnythingIsAuditedNotifiedOrWritten()
    {
        var (page, email) = BuildPage("none", browserZone: null);
        SetField(page, "tabIndex", 1);

        var (text, inputs) = Rendered(page);
        Assert.Equal(0, inputs);
        var reason = GetField<string?>(page, "scheduleZoneError");
        Assert.False(string.IsNullOrEmpty(reason));
        Assert.Contains(reason, text, StringComparison.Ordinal);

        await SetOof(page);

        Assert.NotNull(GetField<string?>(page, "setValidationError"));
        Assert.Empty(_setCalls);
        Assert.Empty(_getCalls);
        Assert.Empty(AuditRows());
        Assert.DoesNotContain(email.ReceivedCalls(), call => call.GetMethodInfo().Name
            is nameof(EmailService.SendAdminNotificationAsync) or nameof(EmailService.SendOofNotificationAsync));
        Assert.False(GetField<bool>(page, "isSetting"));
    }

    // aud-a7f, the finding: the Check Status tab printed a read schedule as bare clock times. A time
    // whose Kind names an instant is shown as the wall clock it reads in the browser's zone, with the
    // zone and that instant's offset. New York's second case is its repeated hour - two instants that
    // both read 01:30, told apart only by their offsets. The expected values are literals.
    [Theory]
    [InlineData("America/New_York", "2026-10-30T13:00:00Z", "2026-11-06T22:00:00Z",
        "2026-10-30 09:00 (America/New_York, UTC-04:00)", "2026-11-06 17:00 (America/New_York, UTC-05:00)")]
    [InlineData("America/New_York", "2026-11-01T05:30:00Z", "2026-11-01T06:30:00Z",
        "2026-11-01 01:30 (America/New_York, UTC-04:00)", "2026-11-01 01:30 (America/New_York, UTC-05:00)")]
    [InlineData("Asia/Kolkata", "2026-11-02T03:30:00Z", "2026-11-06T11:30:00Z",
        "2026-11-02 09:00 (Asia/Kolkata, UTC+05:30)", "2026-11-06 17:00 (Asia/Kolkata, UTC+05:30)")]
    public async Task TheStatusCheck_ShowsAReadScheduleInTheBrowsersZone_WithTheZoneAndEachTimesOffset(
        string zone, string startUtc, string endUtc, string shownStart, string shownEnd)
    {
        var (page, _) = BuildPage("none", browserZone: zone);
        ReadBack(Utc(startUtc), Utc(endUtc));

        var text = await CheckedStatus(page);

        Assert.Contains($"{shownStart} \u2014 {shownEnd}", text, StringComparison.Ordinal);
    }

    // aud-a7f. Local, in .NET, is a time in this server's own zone: still one instant, whatever the
    // server's zone is, so it is shown exactly as the same instant of Kind Utc is.
    [Fact]
    public async Task AReadScheduleOfKindLocal_IsShownAsTheSameInstant()
    {
        var (page, _) = BuildPage("none", browserZone: "America/New_York");
        ReadBack(Utc("2026-10-30T13:00:00Z").ToLocalTime(), Utc("2026-11-06T22:00:00Z").ToLocalTime());

        var text = await CheckedStatus(page);

        Assert.Contains("2026-10-30 09:00 (America/New_York, UTC-04:00) \u2014 2026-11-06 17:00 (America/New_York, UTC-05:00)",
            text, StringComparison.Ordinal);
    }

    // aud-a7f. A time of Kind Unspecified names no instant. It is shown as reported and given no zone
    // and no offset: reading it as UTC would label it confidently and, whenever that guess is wrong,
    // wrongly by the zone's whole offset.
    [Fact]
    public async Task AReadScheduleThatNamesNoZone_IsShownAsReported_AndIsGivenNone()
    {
        var (page, _) = BuildPage("none", browserZone: "America/New_York");
        ReadBack(new DateTime(2026, 10, 30, 13, 0, 0, DateTimeKind.Unspecified), new DateTime(2026, 11, 6, 22, 0, 0, DateTimeKind.Unspecified));

        var text = await CheckedStatus(page);

        Assert.Contains("2026-10-30 13:00", text, StringComparison.Ordinal);
        Assert.Contains("2026-11-06 22:00", text, StringComparison.Ordinal);
        Assert.DoesNotContain("America/New_York", text, StringComparison.Ordinal);
        Assert.DoesNotContain("UTC", text, StringComparison.Ordinal);
    }

    // aud-a7f. With no browser zone the instant is still known, so it is shown in UTC and labelled so -
    // a stated reference, not a guessed zone.
    [Fact]
    public async Task WithoutTheBrowsersZone_AReadScheduleIsShownInUtc_AndSaysSo()
    {
        var (page, _) = BuildPage("none", browserZone: null);
        ReadBack(Utc("2026-10-30T13:00:00Z"), Utc("2026-11-06T22:00:00Z"));

        var text = await CheckedStatus(page);

        Assert.Contains("2026-10-30 13:00 (UTC, UTC+00:00) \u2014 2026-11-06 22:00 (UTC, UTC+00:00)", text, StringComparison.Ordinal);
    }

    // aud-a7f. The owner's notice dated the change with the server's clock and no zone. It is the
    // change's instant in UTC, and says so; handed over at another offset, it is the same instant.
    [Theory]
    [InlineData("Scheduled")]
    [InlineData("Enabled")]
    [InlineData("Disabled")]
    public void TheOwnersNotice_DatesTheChangeInUtc_AndSaysSo(string state)
    {
        var request = OutOfOfficeRequest.FromForm(Mailbox, state, InternalText, ExternalText, Start, End,
            TimeZoneInfo.FindSystemTimeZoneById(BrowserZone), Ticket);
        var changedAt = new DateTimeOffset(2026, 10, 22, 9, 52, 0, TimeSpan.FromHours(-4));

        var (_, body) = EmailService.BuildOofNotification(@"CONTOSO\operator", request, changedAt);

        var date = Regex.Match(body, "<strong>Date:</strong> (?<when>[^<]+)</p>").Groups["when"].Value;
        Assert.Equal(WebUtility.HtmlEncode($"{new DateTime(2026, 10, 22, 13, 52, 0):MMMM dd, yyyy 'at' h:mm tt} UTC"), date);
    }
}
