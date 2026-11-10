using System.Net;
using System.Text;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// docs/ProtectedPrincipalIdentifiers-Plan.md C.2 step 3 and the owner's ruling of 2026-11-03: the on-premises SID of a synced
/// recipient is read from Microsoft Graph through the protection service's own Admin Settings field, the Graph Directory
/// Read Credential. Only an answered read with no SID is "none reported"; anything that could not be asked or read is "not
/// answered", which the check refuses. Graph and the secret store are stand-ins.
/// </summary>
public sealed class GraphOnPremisesSidSourceTests : IDisposable
{
    private const string ObjectId = "8a1f0c55-2d3e-4b6f-9a7c-0e1d2c3b4a59";
    private const string Sid = "S-1-5-21-2000-3000-4000-5101";
    private const int SecretId = 4242;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"gosid-{Guid.NewGuid():N}");
    private readonly ModuleConfigService _moduleConfig;
    private readonly Graph _graph = new();
    private readonly CapturingLog _log = new();
    private int _secretFetches;

    public GraphOnPremisesSidSourceTests()
    {
        Directory.CreateDirectory(_dir);
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_dir);
        _moduleConfig = new ModuleConfigService(new ModuleCatalog(), env, TestConfigStore.CreateModuleConfig(_dir),
            NullLogger<ModuleConfigService>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private void SetGraphSecret(string value) =>
        _moduleConfig.UpdateModuleValues(ProtectedPrincipalService.ProtectedPrincipalsModuleKey,
            new Dictionary<string, string> { [ProtectedPrincipalService.GraphDirectoryReadSecretConfigKey] = value });

    private GraphOnPremisesSidSource Source(Dictionary<string, string>? fields = null) =>
        new(_moduleConfig, id =>
        {
            Interlocked.Increment(ref _secretFetches);
            return Task.FromResult(id == SecretId
                ? fields ?? new Dictionary<string, string> { ["Tenant ID"] = "t", ["Application ID"] = "a", ["Client Secret"] = "s" }
                : null);
        }, () => new HttpClient(_graph), _log);

    [Fact]
    public async Task NoSetting_IsNotAnswered_AndTheLogNamesTheEmptySetting_AndNothingIsAsked()
    {
        var answer = await Source().ReadAsync(ObjectId);

        Assert.Equal(OnPremisesSidOutcome.NotAnswered, answer.Outcome);
        Assert.Contains(_log.Lines, line => line.Contains("Graph Directory Read Credential (Delinea Secret ID) is not set", StringComparison.Ordinal));
        Assert.Equal(0, _secretFetches);
        Assert.Empty(_graph.Requests);
    }

    [Fact]
    public async Task CloudPasswordResetsGraphSetting_IsNeverRead()
    {
        // Owner ruling 2026-11-03: the field is its own; nothing is read from any module's Graph setting.
        _moduleConfig.UpdateModuleValues("CloudPasswordReset",
            new Dictionary<string, string> { ["GraphDelineaSecretId"] = SecretId.ToString(System.Globalization.CultureInfo.InvariantCulture) });

        var answer = await Source().ReadAsync(ObjectId);

        Assert.Equal(OnPremisesSidOutcome.NotAnswered, answer.Outcome);
        Assert.Equal(0, _secretFetches);
    }

    [Fact]
    public async Task AReportedSid_IsReturnedCanonical_FromTheUsersEndpoint_AndTheFirstIsLoggedByItsDomainOnly()
    {
        SetGraphSecret("4242");
        _graph.Answer = (HttpStatusCode.OK, $$"""{"onPremisesSecurityIdentifier":"{{Sid.ToLowerInvariant()}}","onPremisesSyncEnabled":true}""");
        var source = Source();

        var first = await source.ReadAsync(ObjectId);
        var second = await source.ReadAsync(ObjectId);

        Assert.Equal(new OnPremisesSidAnswer(OnPremisesSidOutcome.Reported, Sid), first);
        Assert.Equal(OnPremisesSidOutcome.Reported, second.Outcome);
        Assert.All(_graph.Requests.Where(r => r.Host == "graph.microsoft.com"), r => Assert.Equal(
            $"/v1.0/users/{ObjectId}?$select=onPremisesSecurityIdentifier,onPremisesSyncEnabled", r.PathAndQuery));
        var firstRead = Assert.Single(_log.Lines, line => line.Contains("for the first time", StringComparison.Ordinal));
        Assert.Contains("S-1-5-21-2000-3000-4000", firstRead, StringComparison.Ordinal);
        Assert.DoesNotContain("5101", firstRead, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheClientAndItsToken_AreKept_ForTheSecretId_AndAChangedIdBuildsANewOne()
    {
        SetGraphSecret("4242");
        _graph.Answer = (HttpStatusCode.OK, $$"""{"onPremisesSecurityIdentifier":"{{Sid}}"}""");
        var source = Source();

        await source.ReadAsync(ObjectId);
        await source.ReadAsync(ObjectId);
        await source.ReadAsync(ObjectId);

        Assert.Equal(1, _secretFetches);
        Assert.Equal(1, _graph.TokenRequests);
        Assert.Equal(3, _graph.Requests.Count(r => r.Host == "graph.microsoft.com"));

        SetGraphSecret("4343");
        Assert.Equal(OnPremisesSidOutcome.NotAnswered, (await source.ReadAsync(ObjectId)).Outcome);
        Assert.Equal(2, _secretFetches);
    }

    [Theory]
    [InlineData("""{"onPremisesSecurityIdentifier":null,"onPremisesSyncEnabled":null}""")]
    [InlineData("""{"onPremisesSyncEnabled":true}""")]
    [InlineData("""{"onPremisesSecurityIdentifier":""}""")]
    public async Task AnAnswerWithNoSid_IsNoneReported(string body)
    {
        SetGraphSecret("4242");
        _graph.Answer = (HttpStatusCode.OK, body);

        Assert.Equal(OnPremisesSidOutcome.NoneReported, (await Source().ReadAsync(ObjectId)).Outcome);
    }

    [Theory]
    [InlineData("""{"onPremisesSecurityIdentifier":null,"onPremisesSyncEnabled":true}""", true)]
    [InlineData("""{"onPremisesSecurityIdentifier":null,"onPremisesSyncEnabled":false}""", false)]
    [InlineData("""{"onPremisesSecurityIdentifier":null,"onPremisesSyncEnabled":null}""", false)]
    [InlineData("""{"onPremisesSecurityIdentifier":null}""", null)]
    [InlineData("""{"onPremisesSecurityIdentifier":null,"onPremisesSyncEnabled":"yes"}""", null)]
    public async Task AnAnswerWithNoSid_SaysWhetherGraphReportsTheObjectSynced(string body, bool? synced)
    {
        // Null or false is how Graph reports a cloud-only object; a flag that is absent or not a boolean is no answer.
        SetGraphSecret("4242");
        _graph.Answer = (HttpStatusCode.OK, body);

        var answer = await Source().ReadAsync(ObjectId);

        Assert.Equal(OnPremisesSidOutcome.NoneReported, answer.Outcome);
        Assert.Equal(synced, answer.Synced);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "{}")]
    [InlineData(HttpStatusCode.InternalServerError, "{}")]
    [InlineData(HttpStatusCode.NotFound, "{}")]
    [InlineData(HttpStatusCode.Forbidden, "{}")]
    [InlineData(HttpStatusCode.OK, """{"onPremisesSecurityIdentifier":"not-a-sid"}""")]
    [InlineData(HttpStatusCode.OK, """{"onPremisesSecurityIdentifier":42}""")]
    [InlineData(HttpStatusCode.OK, "[]")]
    public async Task AFailedThrottledOrUnreadableAnswer_IsNotAnswered_NeverNoneReported(HttpStatusCode status, string body)
    {
        SetGraphSecret("4242");
        _graph.Answer = (status, body);

        Assert.Equal(OnPremisesSidOutcome.NotAnswered, (await Source().ReadAsync(ObjectId)).Outcome);
    }

    [Fact]
    public async Task ATokenThatCannotBeHad_IsNotAnswered_AndTheClientIsDropped()
    {
        SetGraphSecret("4242");
        _graph.TokenStatus = HttpStatusCode.Unauthorized;
        var source = Source();

        Assert.Equal(OnPremisesSidOutcome.NotAnswered, (await source.ReadAsync(ObjectId)).Outcome);
        Assert.Equal(OnPremisesSidOutcome.NotAnswered, (await source.ReadAsync(ObjectId)).Outcome);
        Assert.Equal(2, _secretFetches);
    }

    [Fact]
    public async Task ASecretMissingAField_OrUnreadable_IsNotAnswered()
    {
        SetGraphSecret("4242");
        Assert.Equal(OnPremisesSidOutcome.NotAnswered,
            (await Source(new Dictionary<string, string> { ["Tenant ID"] = "t", ["Application ID"] = "a" }).ReadAsync(ObjectId)).Outcome);

        SetGraphSecret("999");
        Assert.Equal(OnPremisesSidOutcome.NotAnswered, (await Source().ReadAsync(ObjectId)).Outcome);
        Assert.Empty(_graph.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task NoUsableObjectId_IsNotAnswered_AndNothingIsAsked(string objectId)
    {
        SetGraphSecret("4242");

        Assert.Equal(OnPremisesSidOutcome.NotAnswered, (await Source().ReadAsync(objectId)).Outcome);
        Assert.Empty(_graph.Requests);
    }

    /// <summary>The token endpoint and Graph: a token request answers <see cref="TokenStatus"/>; a Graph read, <see cref="Answer"/>.</summary>
    private sealed class Graph : HttpMessageHandler
    {
        public (HttpStatusCode Status, string Body) Answer { get; set; } = (HttpStatusCode.OK, "{}");
        public HttpStatusCode TokenStatus { get; set; } = HttpStatusCode.OK;
        public List<Uri> Requests { get; } = [];
        public int TokenRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
                Requests.Add(request.RequestUri!);
            if (request.RequestUri!.Host == "login.microsoftonline.com")
            {
                TokenRequests++;
                return Task.FromResult(new HttpResponseMessage(TokenStatus)
                {
                    Content = new StringContent("""{"access_token":"stand-in","expires_in":3600}""", Encoding.UTF8, "application/json"),
                });
            }
            return Task.FromResult(new HttpResponseMessage(Answer.Status)
            {
                Content = new StringContent(Answer.Body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class CapturingLog : ILogger<GraphOnPremisesSidSource>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Lines)
                Lines.Add(formatter(state, exception));
        }
    }
}
