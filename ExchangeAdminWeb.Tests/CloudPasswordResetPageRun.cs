using System.Net;
using System.Security.Claims;
using System.Text.Json;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Progress;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using static ExchangeAdminWeb.Tests.SubmittedValueHarness;
using Page = ExchangeAdminWeb.Components.Pages.CloudPasswordReset;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// The Cloud Password Reset page driven through its compiled handlers - the preflight, then the confirm - over the real
/// service, the S1 stand-in forest, a stand-in Graph, the real audit writer and a recording mail service.
/// </summary>
/// <remarks>
/// <para>
/// The page needs a circuit only to render; its handlers need none, so they are called by reflection
/// (<see cref="SubmittedValueHarness"/>, the kd-72 shape) with no synchronization context, and every await runs on the
/// pool. What the run writes is read back from where it went: Graph's requests from the recorder, the audit rows from
/// the JSONL file the real <see cref="AuditService"/> wrote, the mail from the substitute's received calls.
/// </para>
/// <para>
/// Graph answers as Entra does for one cloud-only account: the read, the directory roles (none), and the two writes - the
/// employee-ID save, answered by <see cref="SaveAnswer"/>, and the password write, accepted. The service reaches it
/// through its <c>GraphClientForTests</c> seam, so no Secret Server is asked. Nothing here reaches a directory, Graph,
/// Secret Server or a mail server.
/// </para>
/// </remarks>
internal sealed class CloudPasswordResetPageRun : IDisposable
{
    public const string AccountUpn = "jdoe-adm@tenant.example.test";
    public const string AccountObjectId = "0f0f0f0f-1111-4222-8333-444444444444";

    /// <summary>The fill-in switched on, with the neutral test ending this suite uses.</summary>
    public static readonly IReadOnlyDictionary<string, string> FillOn = new Dictionary<string, string>
    {
        [CloudPasswordResetService.FillMissingEmployeeIdKey] = "true",
        [CloudPasswordResetService.OwnerNameEndingsKey] = "-adm",
    };

    private readonly TempDir _logs = new();
    private int _resetChecks;

    public CloudPasswordResetPageRun(StandInForest forest, IReadOnlyDictionary<string, string>? moduleConfig = null)
    {
        Backend = new CloudPasswordResetHarness(forest, moduleConfig ?? FillOn, standInGraph: true);
        Backend.Graph.Respond = AnswerGraph;

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Audit:LogRoot"] = _logs.Path })
            .Build();
        var log = new JsonlLogService(config, NullLogger<JsonlLogService>.Instance);
        Audit = new AuditService(log, new OperationTraceService(config, log));

        Email = Substitute.For<EmailService>(config, Substitute.For<ILogger<EmailService>>());
        Email.UserNotificationsEnabled.Returns(true);
        Email.SendCloudPasswordResetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(Task.FromResult(true));

        Authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), "CloudPasswordReset")
            .Returns(_ => Task.FromResult(
                Interlocked.Increment(ref _resetChecks) <= ResetChecksAllowed ? AuthorizationResult.Success() : AuthorizationResult.Failed()));
        Authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), "CloudPasswordResetReveal")
            .Returns(_ => Task.FromResult(CanSeePasswords ? AuthorizationResult.Success() : AuthorizationResult.Failed()));

        var authState = Substitute.For<AuthenticationStateProvider>();
        authState.GetAuthenticationStateAsync().Returns(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, @"CONTOSO\operator")], "Negotiate"))));

        Protection = new StandInProtection(
            Backend.Environment, Backend.Settings, Backend.ModuleConfig,
            TestConfigStore.CreateProtectedPrincipal(Backend.TempPath), Backend.Delinea);

        Page = new Page();
        Set(Page, "ResetService", Backend.Service);
        Set(Page, "Progress", Progress);
        Set(Page, "Audit", Audit);
        Set(Page, "Email", Email);
        Set(Page, "AuthStateProvider", authState);
        Set(Page, "AuthorizationService", Authorization);
        Set(Page, "Logger", NullLogger<Page>.Instance);
        Set(Page, "ProtectedPrincipalService", Protection);
        SetField(Page, "currentUser", @"CONTOSO\operator");
        SetField(Page, "clientIpAddress", "10.0.0.5");
        SetField(Page, "authChecked", true);
    }

    public CloudPasswordResetHarness Backend { get; }

    public Page Page { get; }

    public AuditService Audit { get; }

    public EmailService Email { get; }

    public IAuthorizationService Authorization { get; } = Substitute.For<IAuthorizationService>();

    public StandInProtection Protection { get; }

    public StepProgress Progress { get; } = new();

    /// <summary>The employee ID the cloud account carries in Entra; null is an account with none.</summary>
    public string? AccountEmployeeId { get; set; }

    /// <summary>How Entra answers the employee-ID save. Accepted by default.</summary>
    public Func<HttpResponseMessage> SaveAnswer { get; set; } = () => new HttpResponseMessage(HttpStatusCode.NoContent);

    /// <summary>Whether the operator can see passwords on screen - the reveal permission, and the page's view of it.</summary>
    public bool CanSeePasswords { get; set; }

    /// <summary>How many of the reset permission's checks succeed before it is lost; unlimited by default.</summary>
    public int ResetChecksAllowed { get; set; } = int.MaxValue;

    /// <summary>The preflight: the operator types the account and checks it.</summary>
    public void Preflight(string upn = AccountUpn)
    {
        SetField(Page, "canReveal", CanSeePasswords);
        SetField(Page, "upnInput", upn);
        Call(Page, "LookupAsync").GetAwaiter().GetResult();
        Assert.True(string.IsNullOrEmpty(ErrorMessage), $"The preflight failed: {ErrorMessage}");
    }

    /// <summary>The confirm, with a ticket: the reset itself.</summary>
    public void Confirm(string ticket = "INC0001")
    {
        SetField(Page, "ticketInput", ticket);
        Call(Page, "ExecuteResetAsync").GetAwaiter().GetResult();
    }

    public CloudPasswordDestination? Destination => Field<CloudPasswordDestination>(Page, "destination");

    public string ErrorMessage => Field<string>(Page, "errorMessage") ?? "";

    public string ResultMessage => Field<string>(Page, "resultMessage") ?? "";

    public bool ResultSuccess => Field<bool>(Page, "resultSuccess");

    public string? RevealedPassword => Field<string>(Page, "revealedPassword");

    /// <summary>Every write the run sent to Graph, in order.</summary>
    public List<CloudPasswordResetHarness.GraphRecorder.Request> Writes =>
        Backend.Graph.Requests.Where(r => r.Method == HttpMethod.Patch).ToList();

    /// <summary>The password the run wrote to Entra, read from the write itself; null when it wrote none.</summary>
    public string? WrittenPassword
    {
        get
        {
            var write = Writes.LastOrDefault(w => w.Body?.Contains("passwordProfile", StringComparison.Ordinal) == true);
            if (write?.Body is null)
                return null;

            using var body = JsonDocument.Parse(write.Body);
            return body.RootElement.GetProperty("passwordProfile").GetProperty("password").GetString();
        }
    }

    public List<JsonElement> AuditRows() => SubmittedValueHarness.AuditRows(_logs.Path);

    /// <summary>The raw text of every audit file the run wrote, for the password-absence check.</summary>
    public string AuditText() =>
        string.Join('\n', System.IO.Directory.GetFiles(_logs.Path, "*.jsonl", SearchOption.AllDirectories).Select(File.ReadAllText));

    /// <summary>The administrator alerts sent with <paramref name="action"/> as their action: success, then details.</summary>
    public List<(bool Success, IReadOnlyDictionary<string, string> Details)> AdminAlerts(string action) =>
        Email.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(EmailService.SendAdminNotificationAsync)
                           && call.GetArguments().Length == 7
                           && Equals(call.GetArguments()[2], action))
            .Select(call => ((bool)call.GetArguments()[3]!, (IReadOnlyDictionary<string, string>)call.GetArguments()[5]!))
            .ToList();

    /// <summary>Every argument list the owner's password mail was sent with.</summary>
    public List<object?[]> OwnerMails =>
        Email.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(EmailService.SendCloudPasswordResetAsync))
            .Select(call => call.GetArguments())
            .ToList();

    public void Dispose()
    {
        Backend.Dispose();
        _logs.Dispose();
    }

    private HttpResponseMessage AnswerGraph(CloudPasswordResetHarness.GraphRecorder.Request request)
    {
        var path = request.Uri.AbsolutePath;

        if (request.Method == HttpMethod.Get && path.EndsWith("/microsoft.graph.directoryRole", StringComparison.Ordinal))
            return Json("""{"value":[]}""");

        if (request.Method == HttpMethod.Get && path.StartsWith("/v1.0/users/", StringComparison.Ordinal))
        {
            return Json(JsonSerializer.Serialize(new
            {
                id = AccountObjectId,
                displayName = "Jo Doe (admin)",
                userPrincipalName = AccountUpn,
                accountEnabled = true,
                onPremisesSyncEnabled = (bool?)null,
                userType = "Member",
                employeeId = AccountEmployeeId,
            }));
        }

        if (request.Method == HttpMethod.Patch && request.Body?.Contains("\"employeeId\"", StringComparison.Ordinal) == true)
            return SaveAnswer();

        if (request.Method == HttpMethod.Patch)
            return new HttpResponseMessage(HttpStatusCode.NoContent);

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    /// <summary>The protection gate, answering as for an account no rule protects.</summary>
    internal sealed class StandInProtection(
        IWebHostEnvironment env, IConfiguration config, ModuleConfigService moduleConfig,
        ProtectedPrincipalRepository repository, DelineaService delinea)
        : ProtectedPrincipalService(env, config, moduleConfig, repository, delinea, NullLogger<ProtectedPrincipalService>.Instance)
    {
        public override Task<(ResolvedDirectoryPrincipal? principal, ResolutionStatus status)> ResolveWithExchangeFallbackAsync(string identity) =>
            Task.FromResult<(ResolvedDirectoryPrincipal?, ResolutionStatus)>((null, ResolutionStatus.NotFound));

        public override Task<ProtectedPrincipalResult> CheckAsync(ResolvedDirectoryPrincipal target) =>
            Task.FromResult(ProtectedPrincipalResult.NotProtected());
    }

    /// <summary>An activity recorder that keeps each step's label, so a test can read what the bar said.</summary>
    internal sealed class StepProgress : IActivityProgress
    {
        private readonly List<Handle> _handles = [];

        public IReadOnlyList<Handle> Handles
        {
            get { lock (_handles) return _handles.ToList(); }
        }

        public IReadOnlyList<ProgressActivity> Active => [];

        public bool HasActive => false;

        public event Action? Changed
        {
            add { }
            remove { }
        }

        public IActivityHandle Begin(string moduleId, string label, ActivitySize size)
        {
            var handle = new Handle(label, size);
            lock (_handles) _handles.Add(handle);
            return handle;
        }

        public void Cancel(string id)
        {
        }

        public void CancelAll()
        {
        }

        internal sealed class Handle(string label, ActivitySize size) : IActivityHandle
        {
            private readonly List<string> _steps = [];

            public string Label { get; } = label;

            public ActivitySize Size { get; } = size;

            public IReadOnlyList<string> Steps
            {
                get { lock (_steps) return _steps.ToList(); }
            }

            public (bool Success, string? Message)? Outcome { get; private set; }

            public string Id { get; } = Guid.NewGuid().ToString("N");

            public CancellationToken CancellationToken => CancellationToken.None;

            public void Report(int done, string? detail = null)
            {
            }

            public void Step(string detail)
            {
                lock (_steps) _steps.Add(detail);
            }

            public void Complete(bool success, string? message = null) => Outcome ??= (success, message);

            public IProgress<ActivityUpdate> AsProgress() => new Progress<ActivityUpdate>(_ => { });

            public void Dispose() => Complete(false, "This did not finish.");
        }
    }
}
