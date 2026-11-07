using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// rev-a2. A directory read refused for the directory-read credential itself - the secret rotated under the same Secret ID
/// while the old password was still kept - drops what was kept, in the store's cache and the operation's, and the flow reads
/// once more on a fresh fetch. Over the real protected-principal service and <see cref="SelfGrantDirectoryStandIn"/>; the
/// rotation is the stand-in refusing every read, in the AD module's words, until the second fetch.
/// </summary>
public sealed class DirectoryCredentialRejectionTests : IDisposable
{
    private const string Rejected = "The server has rejected the client credentials.";

    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    private (ProtectedPrincipalService Service, List<int> Invalidated, Func<int> Fetches) Service(
        SelfGrantDirectoryStandIn forest, Action<int> onFetch)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Delinea:SecretServerUrl"] = "https://fake.local",
            ["Audit:LogRoot"] = _temp.Path,
            ["Security:ProtectedPrincipalDirectoryReadSecretId"] = "4242",
        }).Build();
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_temp.Path);
        var moduleConfig = new ModuleConfigService(
            new ModuleCatalog(), env, TestConfigStore.CreateModuleConfig(_temp.Path), NullLogger<ModuleConfigService>.Instance);
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());
        var extLog = new ExtendedLogService(configuration, env, TestConfigStore.CreateAppSettings(_temp.Path), NullLogger<ExtendedLogService>.Instance);
        var trace = new OperationTraceService(configuration, new JsonlLogService(configuration, NullLogger<JsonlLogService>.Instance));
        var delinea = new DelineaService(httpClientFactory, configuration, NullLogger<DelineaService>.Instance, extLog, trace);
        var invalidated = new List<int>();
        var fetches = 0;
        var service = new ProtectedPrincipalService(
            env, configuration, moduleConfig, TestConfigStore.CreateProtectedPrincipal(_temp.Path), delinea,
            NullLogger<ProtectedPrincipalService>.Instance)
        {
            DirectoryRunspace = forest.Open,
            DirectoryReadSecretInvalidate = invalidated.Add,
            DirectoryReadSecretFetch = _ =>
            {
                onFetch(++fetches);
                return Task.FromResult<(string username, string password, string domain)?>(("svc-read", $"pw{fetches}", "CONTOSO"));
            },
        };
        return (service, invalidated, () => fetches);
    }

    private static ResolvedDirectoryPrincipal Principal(SelfGrantDirectoryStandIn.Entry target) =>
        new("Test", "carol", target.Upn!, target.Sam, target.Upn, target.Dn, null, null, ObjectSid: target.Sid);

    [Fact]
    public async Task ARejectedCredential_IsDroppedAndFetchedAfresh_AndTheCheckAnswersOnTheRetry()
    {
        var forest = new SelfGrantDirectoryStandIn();
        var target = forest.AddUser("carol", upn: "carol@contoso.test");
        var group = forest.AddGroup("Tier0", members: target);
        forest.Failures["Get-ADObject"] = Rejected;
        forest.Failures["Get-ADGroup"] = Rejected;
        var (service, invalidated, fetches) = Service(forest, fetch =>
        {
            // The rotation: the store's second answer is the password the directory accepts.
            if (fetch == 2)
            {
                forest.Failures.Remove("Get-ADObject");
                forest.Failures.Remove("Get-ADGroup");
            }
        });
        service.SaveConfig(new ProtectedPrincipalConfig { GroupIds = [ProtectedPrincipalIdEntry.Format(group.Sid!, "Tier0")] });

        ProtectedPrincipalResult result;
        using (DirectoryReadOperation.Begin())
            result = await service.CheckAsync(Principal(target));

        Assert.False(result.CheckFailed, result.Reason);
        Assert.True(result.IsProtected);
        Assert.Equal(2, fetches());
        Assert.Equal([4242], invalidated);
    }

    [Fact]
    public async Task ACredentialStillRejectedOnTheRetry_FailsClosed_AfterOneRetryOnly()
    {
        var forest = new SelfGrantDirectoryStandIn();
        var target = forest.AddUser("carol", upn: "carol@contoso.test");
        var group = forest.AddGroup("Tier0", members: target);
        forest.Failures["Get-ADObject"] = Rejected;
        forest.Failures["Get-ADGroup"] = Rejected;
        var (service, invalidated, fetches) = Service(forest, _ => { });
        service.SaveConfig(new ProtectedPrincipalConfig { GroupIds = [ProtectedPrincipalIdEntry.Format(group.Sid!, "Tier0")] });

        var result = await service.CheckAsync(Principal(target));

        Assert.True(result.CheckFailed, result.Reason);
        Assert.False(result.IsProtected);
        Assert.Equal(2, fetches());
        Assert.Equal([4242], invalidated);
    }

    [Fact]
    public async Task AReadThatFailsForAnyOtherReason_IsNotRetried_AndKeepsTheCredential()
    {
        var forest = new SelfGrantDirectoryStandIn();
        var target = forest.AddUser("carol", upn: "carol@contoso.test");
        var group = forest.AddGroup("Tier0", members: target);
        forest.Failures["Get-ADObject"] = "The server is not operational.";
        var (service, invalidated, fetches) = Service(forest, _ => { });
        service.SaveConfig(new ProtectedPrincipalConfig { GroupIds = [ProtectedPrincipalIdEntry.Format(group.Sid!, "Tier0")] });

        var result = await service.CheckAsync(Principal(target));

        Assert.True(result.CheckFailed, result.Reason);
        Assert.Equal(1, fetches());
        Assert.Empty(invalidated);
    }

    [Theory]
    [InlineData("The server has rejected the client credentials.", true)]
    [InlineData("Logon failure: unknown user name or bad password.", true)]
    [InlineData("The user name or password is incorrect.", true)]
    [InlineData("The supplied credential is invalid.", true)]
    [InlineData("The server is not operational.", false)]
    [InlineData("Unable to contact the server.", false)]
    [InlineData("Insufficient access rights to perform the operation.", false)]
    public void ARejection_IsTheDirectorysRefusalOfTheCredential_AndNothingElse(string message, bool rejection)
    {
        Assert.Equal(rejection, DirectoryCredentialRejection.IsRejection(message));
        Assert.Equal(rejection, DirectoryCredentialRejection.IsRejection(new InvalidOperationException("outer", new Exception(message))));
    }
}
