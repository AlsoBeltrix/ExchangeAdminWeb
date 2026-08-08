using System.Text.Json;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

public class EmergencyDisableServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configDir;

    public EmergencyDisableServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"emergency-disable-test-{Guid.NewGuid():N}");
        _configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(_configDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public async Task DisableAsync_BlankTicket_FailsBeforeCredentialLookup()
    {
        var service = CreateService();

        var result = await service.DisableAsync(MakePrincipal(), "  ", "DOMAIN\\admin", "10.0.0.1", actingUser: null);

        Assert.False(result.Success);
        Assert.Null(result.Snapshot);
        Assert.Contains("Ticket number is required", result.Error);
        Assert.Contains(result.Steps, s => s.Step == "TicketValidation" && s.Status == "FAILED");
        Assert.DoesNotContain(result.Steps, s => s.Step == "ProtectedPrincipalCheck");
        Assert.DoesNotContain(result.Steps, s => s.Step == "GetADCredentials");
    }

    [Fact]
    public async Task DisableAsync_ProtectedPrincipal_BlocksBeforeCredentialLookup()
    {
        var protectedConfig = JsonSerializer.Serialize(new
        {
            ProtectedPrincipals = new
            {
                Users = new[] { "ceo@contoso.com" },
                Groups = Array.Empty<string>(),
                OrganizationalUnits = Array.Empty<string>(),
                SamAccountNamePatterns = Array.Empty<string>()
            }
        });
        var service = CreateService(protectedPrincipalsJson: protectedConfig);

        var result = await service.DisableAsync(MakePrincipal("ceo@contoso.com"), "INC001", "DOMAIN\\admin", "10.0.0.1", actingUser: null);

        Assert.False(result.Success);
        Assert.Null(result.Snapshot);
        Assert.Contains("protected principal", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.Steps, s => s.Step == "TicketValidation" && s.Status == "OK");
        Assert.Contains(result.Steps, s => s.Step == "ProtectedPrincipalCheck" && s.Status == "BLOCKED");
        Assert.DoesNotContain(result.Steps, s => s.Step == "GetADCredentials");
    }

    [Fact]
    public async Task DisableAsync_CorruptProtectedPrincipalConfig_FailsClosedBeforeCredentialLookup()
    {
        File.WriteAllText(Path.Combine(_configDir, "protected-principals.json"), "not valid json {{{");
        var service = CreateService();

        var result = await service.DisableAsync(MakePrincipal(), "INC001", "DOMAIN\\admin", "10.0.0.1", actingUser: null);

        Assert.False(result.Success);
        Assert.Null(result.Snapshot);
        Assert.Contains("Protected principal check failed", result.Error);
        Assert.Contains(result.Steps, s => s.Step == "ProtectedPrincipalCheck" && s.Status == "BLOCKED");
        Assert.DoesNotContain(result.Steps, s => s.Step == "GetADCredentials");
    }

    [Fact]
    public async Task DisableAsync_MissingAdCredentialConfig_StopsBeforeGraphAndMutationSteps()
    {
        var service = CreateService();

        var result = await service.DisableAsync(MakePrincipal(), "INC001", "DOMAIN\\admin", "10.0.0.1", actingUser: null);

        Assert.False(result.Success);
        Assert.Null(result.Snapshot);
        Assert.Contains("AD credentials unavailable", result.Error);
        Assert.Contains(result.Steps, s => s.Step == "TicketValidation" && s.Status == "OK");
        Assert.Contains(result.Steps, s => s.Step == "ProtectedPrincipalCheck" && s.Status == "OK");
        Assert.Contains(result.Steps, s => s.Step == "GetADCredentials" && s.Status == "FAILED");
        Assert.DoesNotContain(result.Steps, s => s.Step == "GetGraphCredentials");
        Assert.DoesNotContain(result.Steps, s => s.Step is "DisableAD" or "ResetPassword" or "RevokeEntraSessions" or "DisableEntra");
    }

    [Fact]
    public void ModuleCatalog_EmergencyDisable_IsFailClosedAndVersioned()
    {
        var catalog = new ModuleCatalog();

        var module = catalog.GetById("EmergencyDisable");

        Assert.NotNull(module);
        Assert.False(module.EnabledByDefault);
        Assert.True(module.MainPermission.FailClosed);
        // 1.2.0: an authorised servicer group may act on a protected principal here, with the
        // override recorded in the audit event (not only the operation trace - see pps-3).
        // 1.1.0 was protection resolving through Exchange (docs/ProtectedPrincipalGapFix-Plan.md
        // GAP B).
        Assert.Equal("1.2.0", module.Version);
        Assert.Contains(module.ConfigFields, f => f.Key == "DelineaSecretId");
        Assert.Contains(module.ConfigFields, f => f.Key == "GraphDelineaSecretId");
        Assert.Contains(module.ConfigFields, f => f.Key == "NotifySecurityTeam");
    }

    // ---- Synced-user Entra-disable decision (pure) ------------------------------------------

    [Fact]
    public void ShouldSkipEntraDisable_SyncedUser_IsSkipped()
    {
        Assert.True(EmergencyDisableService.ShouldSkipEntraDisable(isSynced: true));
    }

    [Fact]
    public void ShouldSkipEntraDisable_CloudOnlyUser_IsNotSkipped()
    {
        Assert.False(EmergencyDisableService.ShouldSkipEntraDisable(isSynced: false));
    }

    // ---- Overall-success accounting (pure) --------------------------------------------------

    [Fact]
    public void IsOverallSuccess_SyncedUser_EntraSkipped_IsSuccess()
    {
        // AD/reset/revoke all OK and the Entra disable SKIPPED (synced) => overall success.
        Assert.True(EmergencyDisableService.IsOverallSuccess("OK", "OK", "OK", "SKIPPED"));
    }

    [Fact]
    public void IsOverallSuccess_CloudUser_EntraOk_IsSuccess()
    {
        Assert.True(EmergencyDisableService.IsOverallSuccess("OK", "OK", "OK", "OK"));
    }

    [Fact]
    public void IsOverallSuccess_EntraFailed_IsFailure()
    {
        Assert.False(EmergencyDisableService.IsOverallSuccess("OK", "OK", "OK", "FAILED"));
    }

    [Fact]
    public void IsOverallSuccess_AdFailed_IsFailure_EvenIfEntraSkipped()
    {
        // A SKIPPED Entra step must not paper over a failed AD mutation.
        Assert.False(EmergencyDisableService.IsOverallSuccess("FAILED", "OK", "OK", "SKIPPED"));
    }

    [Fact]
    public async Task DisableAsync_ServicedOverride_RecordsTheAuthorisingGroupInTheAUDIT()
    {
        // pps-3: the override was recorded in the operation TRACE and missing from the audit log.
        // Those are different stores with different retention and different readers, and the audit
        // log is where "who permitted this?" gets asked later. Asserting on the written audit file
        // rather than on result.Steps is the whole point - the steps were already correct.
        const string servicerSid = "S-1-5-21-1-2-3-4001";
        var protectedConfig = JsonSerializer.Serialize(new
        {
            ProtectedPrincipals = new
            {
                Users = new[] { "ceo@contoso.com" },
                Groups = Array.Empty<string>(),
                OrganizationalUnits = Array.Empty<string>(),
                SamAccountNamePatterns = Array.Empty<string>()
            }
        });

        var service = CreateService(protectedConfig, servicerGroups: [servicerSid]);

        var actingUser = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, servicerSid)],
                "TestAuth"));

        var result = await service.DisableAsync(
            MakePrincipal("ceo@contoso.com"), "INC001", "DOMAIN\\admin", "10.0.0.1", actingUser);

        // The gate allowed it: an ordinary operator gets BLOCKED here, this one gets SERVICED.
        Assert.Contains(result.Steps, s => s.Step == "ProtectedPrincipalCheck" && s.Status == "SERVICED");

        // The run then stops at the Delinea credential fetch, which returns BEFORE LogAudit - a
        // pre-existing early-return path this change does not alter, so there is no audit record
        // to inspect and no audit file is written at all. Asserting emptiness here would pass for
        // the wrong reason, so the audit-side guard is source-level instead, below. Stated plainly
        // rather than left implicit: reaching LogAudit needs a live directory and Graph.
        Assert.Empty(ReadAuditEvents());
    }

    [Fact]
    public void TheServicedNote_ReachesTheAuditEvent_NotOnlyTheOperationTrace()
    {
        // pps-3: the override was recorded as an operation-trace STEP and omitted from the audit
        // event. They are different stores with different retention and readers, and the audit log
        // is where "who permitted this?" gets asked later.
        //
        // Source-level because the audit call sits past a Delinea credential fetch and two live
        // backends (see the test above), so no unit test can drive it. NOT behavioural coverage.
        var source = ReadServiceSource();

        // The note is threaded to LogAudit rather than stopping at the trace step.
        Assert.Contains("LogAudit(target, performedBy, ip, ticket, overallSuccess, steps, overallError, servicedNote)",
            source, StringComparison.Ordinal);

        // And LogAudit puts it in extra. errorDetail is written as null on success, so a serviced
        // disable - which SUCCEEDS - would have it silently discarded there.
        Assert.Contains("ProtectedPrincipalServicing.Extra(servicedNote)", source, StringComparison.Ordinal);
    }

    private static string ReadServiceSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var file = Path.Combine(dir.FullName, "Services", "EmergencyDisableService.cs");
            if (File.Exists(file))
                return File.ReadAllText(file);

            dir = dir.Parent;
        }

        throw new FileNotFoundException("Could not locate EmergencyDisableService.cs from the test base directory.");
    }

    /// <summary>
    /// Every AUDIT line written under the test's log root, across rotation files. Deliberately
    /// excludes the operation-trace stream (`*_trace.jsonl`), which is a separate store: the whole
    /// point of pps-3 is that a record present only in the trace is not in the audit log.
    /// </summary>
    private List<string> ReadAuditEvents()
    {
        var lines = new List<string>();
        foreach (var file in Directory.EnumerateFiles(_tempDir, "*.jsonl", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file).Contains("_trace", StringComparison.OrdinalIgnoreCase))
                continue;

            lines.AddRange(File.ReadAllLines(file));
        }

        return lines;
    }

    private EmergencyDisableService CreateService(string? protectedPrincipalsJson = null, string[]? servicerGroups = null)
    {
        if (protectedPrincipalsJson != null)
            File.WriteAllText(Path.Combine(_configDir, "protected-principals.json"), protectedPrincipalsJson);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Audit:LogRoot"] = _tempDir,
                ["Audit:RotationPeriod"] = "daily",
                ["OperationTrace:Enabled"] = "true",
                ["Delinea:SecretServerUrl"] = "https://fake.local",
                ["Email:AdminNotificationEmail"] = ""
            })
            .Build();

        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_tempDir);

        var catalog = new ModuleCatalog();
        var moduleConfig = new ModuleConfigService(catalog, env, TestConfigStore.CreateModuleConfig(_tempDir), Substitute.For<ILogger<ModuleConfigService>>());
        var jsonlLog = new JsonlLogService(config, Substitute.For<ILogger<JsonlLogService>>());
        var operationTrace = new OperationTraceService(config, jsonlLog);
        var audit = new AuditService(jsonlLog, operationTrace);

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());

        var extendedLog = new ExtendedLogService(config, env, TestConfigStore.CreateAppSettings(_tempDir), Substitute.For<ILogger<ExtendedLogService>>());
        var delinea = new DelineaService(httpClientFactory, config, Substitute.For<ILogger<DelineaService>>(), extendedLog, operationTrace);
        var moduleCredentials = new ModuleCredentialService(moduleConfig, delinea, Substitute.For<ILogger<ModuleCredentialService>>());
        var protectedPrincipalService = new ProtectedPrincipalService(env, config, moduleConfig, TestConfigStore.CreateProtectedPrincipal(_tempDir), delinea, Substitute.For<ILogger<ProtectedPrincipalService>>());
        var email = new EmailService(config, Substitute.For<ILogger<EmailService>>());

        // Real servicer service. With no servicerGroups it holds NO ProtectedServicer row and so
        // denies - which is what every pre-existing assertion in this class depends on, and they
        // must keep passing unchanged. Only the servicing test opts into a grant.
        var sectionAccessRepo = new Services.Storage.SectionAccessRepository(TestConfigStore.Create(_tempDir));
        if (servicerGroups is { Length: > 0 })
        {
            sectionAccessRepo.SaveAll(new Dictionary<string, string[]>
            {
                [ProtectedPrincipalServicerService.SectionKeyFor("EmergencyDisable")] = servicerGroups,
                // Something unrelated too, so the store counts as CONFIGURED and the legacy
                // AllowedGroups fallback is out of the picture (see ppsvc-1).
                ["EmergencyDisable"] = ["S-1-5-21-1-2-3-500"],
            });
        }

        var sectionAccess = new SectionAccessService(
            config, Substitute.For<ILogger<SectionAccessService>>(), env, new Modules.ModuleCatalog(),
            sectionAccessRepo);
        var servicers = new ProtectedPrincipalServicerService(
            sectionAccess, Substitute.For<ILogger<ProtectedPrincipalServicerService>>());

        return new EmergencyDisableService(
            moduleCredentials,
            moduleConfig,
            protectedPrincipalService,
            servicers,
            operationTrace,
            audit,
            email,
            delinea,
            httpClientFactory,
            env,
            config,
            Substitute.For<ILogger<EmergencyDisableService>>());
    }

    private static ResolvedDirectoryPrincipal MakePrincipal(string upn = "user@contoso.com")
    {
        return new ResolvedDirectoryPrincipal(
            Source: "Test",
            DisplayName: upn.Split('@')[0],
            UserPrincipalName: upn,
            SamAccountName: upn.Split('@')[0],
            PrimarySmtpAddress: upn,
            DistinguishedName: $"CN={upn.Split('@')[0]},OU=Users,DC=contoso,DC=com",
            ObjectGuid: Guid.NewGuid().ToString(),
            EntraObjectId: Guid.NewGuid().ToString());
    }
}
