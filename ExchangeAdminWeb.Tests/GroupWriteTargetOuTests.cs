using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// rev-b3. The write-target check compares a protected OU by the objectGUIDs of the target group's containers, read from the
/// directory as the principal check reads them - not by the OU row's stored DN, which a rename or a move of the OU leaves
/// behind. Over the real protected-principal service and <see cref="SelfGrantDirectoryStandIn"/>, modelled on
/// <c>ProtectedPrincipalIdentifierTests.AnOuGuidRow_MatchesAfterTheOuIsRenamed</c> for users.
/// </summary>
public sealed class GroupWriteTargetOuTests : IDisposable
{
    private static readonly Guid OuGuid = Guid.Parse("0b5e3c2a-7d41-4f8e-9c6a-2e1f3d4b5a69");
    private const string StoredOuDn = "OU=Tier0," + SelfGrantDirectoryStandIn.DomainDn;

    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    private ProtectedPrincipalService Service(SelfGrantDirectoryStandIn forest)
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
        var service = new ProtectedPrincipalService(
            env, configuration, moduleConfig, TestConfigStore.CreateProtectedPrincipal(_temp.Path), delinea,
            NullLogger<ProtectedPrincipalService>.Instance)
        {
            DirectoryRunspace = forest.Open,
            DirectoryReadSecretFetch = _ => Task.FromResult<(string username, string password, string domain)?>(("svc-read", "pw", "CONTOSO")),
        };
        service.SaveConfig(new ProtectedPrincipalConfig
        {
            OrganizationalUnits = [StoredOuDn],
            OuIds = [ProtectedPrincipalIdEntry.Format(OuGuid.ToString(), StoredOuDn)],
        });
        return service;
    }

    // The OU (whatever it is called now), and a group in it, in the stand-in forest.
    private static SelfGrantDirectoryStandIn.Entry GroupIn(SelfGrantDirectoryStandIn forest, string ouDn, Guid ouGuid)
    {
        forest.Add(new SelfGrantDirectoryStandIn.Entry { Dn = ouDn, Class = "organizationalUnit", Guid = ouGuid });
        return forest.Add(new SelfGrantDirectoryStandIn.Entry
        {
            Dn = "CN=Ops Group," + ouDn,
            Class = "group",
            Sid = SelfGrantDirectoryStandIn.DomainSid + "-8801",
        });
    }

    private static ResolvedDirectoryPrincipal Snapshot(SelfGrantDirectoryStandIn.Entry group) =>
        new("Test-AD", "Ops Group", string.Empty, "Ops Group", null, group.Dn, group.Guid.ToString(), null, ObjectSid: group.Sid);

    [Theory]
    [InlineData("OU=Tier Zero," + SelfGrantDirectoryStandIn.DomainDn)]               // renamed
    [InlineData("OU=Tier0,OU=Privileged," + SelfGrantDirectoryStandIn.DomainDn)]     // moved under another OU
    public async Task AGroupInAProtectedOu_StaysProtected_AfterTheOuIsRenamedOrMoved(string ouNow)
    {
        var forest = new SelfGrantDirectoryStandIn();
        if (ouNow.Contains("OU=Privileged", StringComparison.Ordinal))
            forest.Add(new SelfGrantDirectoryStandIn.Entry { Dn = "OU=Privileged," + SelfGrantDirectoryStandIn.DomainDn, Class = "organizationalUnit" });
        var group = GroupIn(forest, ouNow, OuGuid);

        var result = await Service(forest).CheckWriteTargetAsync(Snapshot(group));

        Assert.False(result.CheckFailed, result.Reason);
        Assert.True(result.IsProtected);
        Assert.Equal([$"OU:{StoredOuDn}"], result.MatchedRules);
        Assert.Empty(forest.Errors);
    }

    [Fact]
    public async Task AGroupInAnotherOuThatTookTheStoredName_IsNotProtected()
    {
        // The opposite direction: the label matches the group's DN, the objectGUID does not.
        var forest = new SelfGrantDirectoryStandIn();
        var group = GroupIn(forest, StoredOuDn, Guid.Parse("77777777-7777-4777-8777-777777777777"));

        var result = await Service(forest).CheckWriteTargetAsync(Snapshot(group));

        Assert.False(result.CheckFailed, result.Reason);
        Assert.False(result.IsProtected);
    }

    [Fact]
    public async Task AContainerReadThatFails_RefusesTheWrite_NamingWhy()
    {
        var forest = new SelfGrantDirectoryStandIn();
        var group = GroupIn(forest, "OU=Tier Zero," + SelfGrantDirectoryStandIn.DomainDn, OuGuid);
        forest.Failures["Get-ADObject"] = "The server is not operational.";

        var result = await Service(forest).CheckWriteTargetAsync(Snapshot(group));

        Assert.True(result.CheckFailed, result.Reason);
        Assert.Contains(ProtectedPrincipalService.WriteTargetContainersNotRead, result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSharedGate_AsksTheFullCheck_WithItsOuRead()
    {
        // The one write-target gate every on-prem group module consults must ask the check that reads the OU rule, or the
        // rule never applies to a write. Raw text: a tripwire on the call.
        var raw = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Services", "ProtectedPrincipalServicing.cs"));

        Assert.Contains("await protection.CheckWriteTargetAsync(targetGroup)", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("protection.CheckWriteTarget(targetGroup)", raw, StringComparison.Ordinal);
    }
}
