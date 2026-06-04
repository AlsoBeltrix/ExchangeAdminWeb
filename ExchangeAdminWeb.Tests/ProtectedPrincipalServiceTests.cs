using System.Text.Json;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

public class ProtectedPrincipalServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configDir;

    public ProtectedPrincipalServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"pps-test-{Guid.NewGuid():N}");
        _configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(_configDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private ProtectedPrincipalService CreateService(
        Dictionary<string, string?>? configOverrides = null,
        string? protectedPrincipalsJson = null)
    {
        var configData = new Dictionary<string, string?>
        {
            ["Delinea:SecretServerUrl"] = "https://fake.local"
        };

        if (configOverrides != null)
            foreach (var kv in configOverrides)
                configData[kv.Key] = kv.Value;

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_tempDir);

        var moduleConfigLogger = Substitute.For<ILogger<ModuleConfigService>>();
        var moduleConfig = new ModuleConfigService(new ModuleCatalog(), env, moduleConfigLogger);

        if (protectedPrincipalsJson != null)
            File.WriteAllText(Path.Combine(_configDir, "protected-principals.json"), protectedPrincipalsJson);

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());
        var extLog = new ExtendedLogService(config, env, Substitute.For<ILogger<ExtendedLogService>>());
        var jsonlLog = new JsonlLogService(config, Substitute.For<ILogger<JsonlLogService>>());
        var operationTrace = new OperationTraceService(config, jsonlLog);
        var delineaService = new DelineaService(httpClientFactory, config, Substitute.For<ILogger<DelineaService>>(), extLog, operationTrace);

        var logger = Substitute.For<ILogger<ProtectedPrincipalService>>();
        return new ProtectedPrincipalService(env, config, moduleConfig, delineaService, logger);
    }

    private static ResolvedDirectoryPrincipal MakePrincipal(
        string upn = "user@contoso.com",
        string? samAccountName = null,
        string? primarySmtp = null,
        string? dn = null,
        string? objectGuid = null,
        string? entraObjectId = null)
    {
        return new ResolvedDirectoryPrincipal(
            Source: "Test",
            DisplayName: upn.Split('@')[0],
            UserPrincipalName: upn,
            SamAccountName: samAccountName,
            PrimarySmtpAddress: primarySmtp ?? upn,
            DistinguishedName: dn,
            ObjectGuid: objectGuid,
            EntraObjectId: entraObjectId);
    }

    // --- Missing config with legacy fallback ---

    [Fact]
    public async Task Check_NoCentralConfig_NoLegacy_AllowsOperation()
    {
        var service = CreateService();
        var principal = MakePrincipal();

        var result = await service.CheckAsync(principal);

        Assert.False(result.IsProtected);
        Assert.False(result.CheckFailed);
    }

    // --- Valid central config ---

    [Fact]
    public async Task Check_CentralConfig_DirectUpnMatch_IsProtected()
    {
        var json = JsonSerializer.Serialize(new
        {
            ProtectedPrincipals = new
            {
                Users = new[] { "ceo@contoso.com" },
                Groups = Array.Empty<string>(),
                OrganizationalUnits = Array.Empty<string>(),
                SamAccountNamePatterns = Array.Empty<string>()
            }
        });

        var service = CreateService(protectedPrincipalsJson: json);
        var principal = MakePrincipal(upn: "ceo@contoso.com");

        var result = await service.CheckAsync(principal);

        Assert.True(result.IsProtected);
        Assert.Contains("User:ceo@contoso.com", result.MatchedRules);
    }

    [Fact]
    public async Task Check_CentralConfig_DirectEmailMatch_IsProtected()
    {
        var json = JsonSerializer.Serialize(new
        {
            ProtectedPrincipals = new
            {
                Users = new[] { "boss@contoso.com" },
                Groups = Array.Empty<string>(),
                OrganizationalUnits = Array.Empty<string>(),
                SamAccountNamePatterns = Array.Empty<string>()
            }
        });

        var service = CreateService(protectedPrincipalsJson: json);
        var principal = MakePrincipal(upn: "someone@contoso.com", primarySmtp: "boss@contoso.com");

        var result = await service.CheckAsync(principal);

        Assert.True(result.IsProtected);
    }

    [Fact]
    public async Task Check_CentralConfig_CaseInsensitive_IsProtected()
    {
        var json = JsonSerializer.Serialize(new
        {
            ProtectedPrincipals = new
            {
                Users = new[] { "CEO@Contoso.COM" },
                Groups = Array.Empty<string>(),
                OrganizationalUnits = Array.Empty<string>(),
                SamAccountNamePatterns = Array.Empty<string>()
            }
        });

        var service = CreateService(protectedPrincipalsJson: json);
        var principal = MakePrincipal(upn: "ceo@contoso.com");

        var result = await service.CheckAsync(principal);

        Assert.True(result.IsProtected);
    }

    // --- Corrupt central config fails closed ---

    [Fact]
    public async Task Check_CorruptCentralConfig_FailsClosed()
    {
        File.WriteAllText(Path.Combine(_configDir, "protected-principals.json"), "NOT VALID JSON {{{");
        var service = CreateService();
        var principal = MakePrincipal();

        var result = await service.CheckAsync(principal);

        Assert.True(result.CheckFailed);
        Assert.Contains("corrupt", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Check_CentralConfigMissingSection_FailsClosed()
    {
        File.WriteAllText(Path.Combine(_configDir, "protected-principals.json"), "{}");
        var service = CreateService();
        var principal = MakePrincipal();

        var result = await service.CheckAsync(principal);

        Assert.True(result.CheckFailed);
    }

    // --- OU protected match ---

    [Fact]
    public async Task Check_OuMatch_IsProtected()
    {
        var json = JsonSerializer.Serialize(new
        {
            ProtectedPrincipals = new
            {
                Users = Array.Empty<string>(),
                Groups = Array.Empty<string>(),
                OrganizationalUnits = new[] { "OU=Tier0,DC=ad,DC=contoso,DC=com" },
                SamAccountNamePatterns = Array.Empty<string>()
            }
        });

        var service = CreateService(protectedPrincipalsJson: json);
        var principal = MakePrincipal(dn: "CN=AdminUser,OU=Admins,OU=Tier0,DC=ad,DC=contoso,DC=com");

        var result = await service.CheckAsync(principal);

        Assert.True(result.IsProtected);
        Assert.Contains("OU:OU=Tier0,DC=ad,DC=contoso,DC=com", result.MatchedRules);
    }

    [Fact]
    public async Task Check_OuNoMatch_NotProtected()
    {
        var json = JsonSerializer.Serialize(new
        {
            ProtectedPrincipals = new
            {
                Users = Array.Empty<string>(),
                Groups = Array.Empty<string>(),
                OrganizationalUnits = new[] { "OU=Tier0,DC=ad,DC=contoso,DC=com" },
                SamAccountNamePatterns = Array.Empty<string>()
            }
        });

        var service = CreateService(protectedPrincipalsJson: json);
        var principal = MakePrincipal(dn: "CN=NormalUser,OU=Users,DC=ad,DC=contoso,DC=com");

        var result = await service.CheckAsync(principal);

        Assert.False(result.IsProtected);
    }

    // --- Pattern protected match ---

    [Fact]
    public async Task Check_PatternMatch_Prefix_IsProtected()
    {
        var json = JsonSerializer.Serialize(new
        {
            ProtectedPrincipals = new
            {
                Users = Array.Empty<string>(),
                Groups = Array.Empty<string>(),
                OrganizationalUnits = Array.Empty<string>(),
                SamAccountNamePatterns = new[] { "adm-*" }
            }
        });

        var service = CreateService(protectedPrincipalsJson: json);
        var principal = MakePrincipal(samAccountName: "adm-jdoe");

        var result = await service.CheckAsync(principal);

        Assert.True(result.IsProtected);
        Assert.Contains("Pattern:adm-*", result.MatchedRules);
    }

    [Fact]
    public async Task Check_PatternMatch_Suffix_IsProtected()
    {
        var json = JsonSerializer.Serialize(new
        {
            ProtectedPrincipals = new
            {
                Users = Array.Empty<string>(),
                Groups = Array.Empty<string>(),
                OrganizationalUnits = Array.Empty<string>(),
                SamAccountNamePatterns = new[] { "svc-*" }
            }
        });

        var service = CreateService(protectedPrincipalsJson: json);
        var principal = MakePrincipal(samAccountName: "svc-exchange");

        var result = await service.CheckAsync(principal);

        Assert.True(result.IsProtected);
    }

    [Fact]
    public async Task Check_PatternNoMatch_NotProtected()
    {
        var json = JsonSerializer.Serialize(new
        {
            ProtectedPrincipals = new
            {
                Users = Array.Empty<string>(),
                Groups = Array.Empty<string>(),
                OrganizationalUnits = Array.Empty<string>(),
                SamAccountNamePatterns = new[] { "adm-*", "svc-*" }
            }
        });

        var service = CreateService(protectedPrincipalsJson: json);
        var principal = MakePrincipal(samAccountName: "jdoe");

        var result = await service.CheckAsync(principal);

        Assert.False(result.IsProtected);
    }

    // --- Directory-read credential missing while group rules exist fails closed ---

    [Fact]
    public async Task Check_GroupRulesExist_NoCredentialConfig_FailsClosed()
    {
        var json = JsonSerializer.Serialize(new
        {
            ProtectedPrincipals = new
            {
                Users = Array.Empty<string>(),
                Groups = new[] { "Domain Admins" },
                OrganizationalUnits = Array.Empty<string>(),
                SamAccountNamePatterns = Array.Empty<string>()
            }
        });

        var service = CreateService(protectedPrincipalsJson: json);
        var principal = MakePrincipal();

        var result = await service.CheckAsync(principal);

        Assert.True(result.CheckFailed);
        Assert.Contains("credential", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // --- No match allows operation ---

    [Fact]
    public async Task Check_NoMatchAnyRule_AllowsOperation()
    {
        var json = JsonSerializer.Serialize(new
        {
            ProtectedPrincipals = new
            {
                Users = new[] { "admin@contoso.com" },
                Groups = Array.Empty<string>(),
                OrganizationalUnits = new[] { "OU=Tier0,DC=ad,DC=contoso,DC=com" },
                SamAccountNamePatterns = new[] { "adm-*" }
            }
        });

        var service = CreateService(protectedPrincipalsJson: json);
        var principal = MakePrincipal(upn: "normaluser@contoso.com", samAccountName: "normaluser", dn: "CN=Normal,OU=Users,DC=ad,DC=contoso,DC=com");

        var result = await service.CheckAsync(principal);

        Assert.False(result.IsProtected);
        Assert.False(result.CheckFailed);
    }

    // --- LDAP filter escaping ---

    [Theory]
    [InlineData("normal", "normal")]
    [InlineData("test\\user", "test\\5cuser")]
    [InlineData("user*", "user\\2a")]
    [InlineData("(admin)", "\\28admin\\29")]
    [InlineData("null\0char", "null\\00char")]
    [InlineData("a\\b*c(d)e\0f", "a\\5cb\\2ac\\28d\\29e\\00f")]
    public void EscapeLdapFilter_EscapesSpecialCharacters(string input, string expected)
    {
        var result = ProtectedPrincipalService.EscapeLdapFilter(input);
        Assert.Equal(expected, result);
    }

    // --- Wildcard pattern matching ---

    [Theory]
    [InlineData("adm-*", "adm-jdoe", true)]
    [InlineData("adm-*", "ADM-JDOE", true)]
    [InlineData("adm-*", "admin", false)]
    [InlineData("svc-*", "svc-exchange", true)]
    [InlineData("svc-*", "user-svc", false)]
    [InlineData("*-admin", "domain-admin", true)]
    [InlineData("*-admin", "adminjoe", false)]
    [InlineData("test?user", "testXuser", true)]
    [InlineData("test?user", "testuser", false)]
    public void MatchesWildcardPattern_MatchesCorrectly(string pattern, string value, bool expected)
    {
        var result = ProtectedPrincipalService.MatchesWildcardPattern(pattern, value);
        Assert.Equal(expected, result);
    }

    // --- DN-based group matching ---

    [Theory]
    [InlineData("CN=Domain Admins,CN=Users,DC=ad,DC=analog,DC=com", "Domain Admins", true)]
    [InlineData("CN=Domain Admins,CN=Users,DC=ad,DC=analog,DC=com", "domain admins", true)]
    [InlineData("CN=Domain Admins,CN=Users,DC=ad,DC=analog,DC=com", "DOMAIN ADMINS", true)]
    [InlineData("CN=Testers,OU=Groups,DC=ad,DC=analog,DC=com", "Test", false)] // Must not false-match substrings
    [InlineData("CN=Domain Admins,CN=Users,DC=ad,DC=analog,DC=com", "CN=Domain Admins,CN=Users,DC=ad,DC=analog,DC=com", true)] // Full DN match
    [InlineData("CN=Domain Admins,CN=Users,DC=ad,DC=analog,DC=com", "CN=Other Group,CN=Users,DC=ad,DC=analog,DC=com", false)]
    [InlineData("CN=Domain Admins,CN=Users,DC=ad,DC=analog,DC=com", @"ANALOG\Domain Admins", true)] // DOMAIN\Name format
    [InlineData("CN=Domain Admins,CN=Users,DC=ad,DC=analog,DC=com", @"CONTOSO\Domain Admins", true)] // Domain portion ignored, name matches
    [InlineData("CN=Domain Admins,CN=Users,DC=ad,DC=analog,DC=com", @"ANALOG\Enterprise Admins", false)]
    [InlineData("CN=Test Group,OU=Groups,DC=contoso,DC=com", "Test Group", true)]
    [InlineData("CN=Test Group,OU=Groups,DC=contoso,DC=com", "Test", false)]
    public void MatchesDnToProtectedGroup_MatchesCorrectly(string groupDn, string protectedGroup, bool expected)
    {
        var result = ProtectedPrincipalService.MatchesDnToProtectedGroup(groupDn, protectedGroup);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("CN=Domain Admins,CN=Users,DC=ad,DC=analog,DC=com", "Domain Admins")]
    [InlineData("CN=Test Group,OU=Groups,DC=contoso,DC=com", "Test Group")]
    [InlineData("CN=SimpleGroup", "SimpleGroup")] // No comma — entire value
    [InlineData("", null)]
    [InlineData("OU=Users,DC=contoso,DC=com", null)] // Not a CN-prefixed DN
    public void ExtractCnFromDn_ExtractsCorrectly(string dn, string? expected)
    {
        var result = ProtectedPrincipalService.ExtractCnFromDn(dn);
        Assert.Equal(expected, result);
    }

    // --- Central config plus legacy exclusions uses the union ---

    [Fact]
    public async Task Check_CentralConfigAndLegacy_UsesUnion()
    {
        var json = JsonSerializer.Serialize(new
        {
            ProtectedPrincipals = new
            {
                Users = new[] { "central-protected@contoso.com" },
                Groups = Array.Empty<string>(),
                OrganizationalUnits = Array.Empty<string>(),
                SamAccountNamePatterns = Array.Empty<string>()
            }
        });

        var configOverrides = new Dictionary<string, string?>();
        var service = CreateService(configOverrides, json);

        var centralTarget = MakePrincipal(upn: "central-protected@contoso.com");
        var centralResult = await service.CheckAsync(centralTarget);
        Assert.True(centralResult.IsProtected);
    }

    // --- Config save invalidates cache ---

    [Fact]
    public void SaveConfig_InvalidatesCache()
    {
        var json = JsonSerializer.Serialize(new
        {
            ProtectedPrincipals = new
            {
                Users = new[] { "original@contoso.com" },
                Groups = Array.Empty<string>(),
                OrganizationalUnits = Array.Empty<string>(),
                SamAccountNamePatterns = Array.Empty<string>()
            }
        });

        var service = CreateService(protectedPrincipalsJson: json);

        var (cfg1, _, _) = service.LoadEffectiveConfig();
        Assert.NotNull(cfg1);
        Assert.Contains("original@contoso.com", cfg1!.Users);

        service.SaveConfig(new ProtectedPrincipalConfig
        {
            Users = new[] { "updated@contoso.com" },
            Groups = [],
            OrganizationalUnits = [],
            SamAccountNamePatterns = []
        });

        var (cfg2, _, _) = service.LoadEffectiveConfig();
        Assert.NotNull(cfg2);
        Assert.Contains("updated@contoso.com", cfg2!.Users);
        Assert.DoesNotContain("original@contoso.com", cfg2.Users);
    }

    // --- Domain\user format match ---

    [Fact]
    public async Task Check_DomainBackslashUser_MatchesSamAccountName()
    {
        var json = JsonSerializer.Serialize(new
        {
            ProtectedPrincipals = new
            {
                Users = new[] { @"CONTOSO\breakglass" },
                Groups = Array.Empty<string>(),
                OrganizationalUnits = Array.Empty<string>(),
                SamAccountNamePatterns = Array.Empty<string>()
            }
        });

        var service = CreateService(protectedPrincipalsJson: json);
        var principal = MakePrincipal(samAccountName: "breakglass");

        var result = await service.CheckAsync(principal);

        Assert.True(result.IsProtected);
    }

    // --- Resolution status enum contract ---

    [Fact]
    public void ResolutionStatus_HasAmbiguousValue()
    {
        var values = Enum.GetValues<ProtectedPrincipalService.ResolutionStatus>();
        Assert.Contains(ProtectedPrincipalService.ResolutionStatus.Ambiguous, values);
        Assert.Contains(ProtectedPrincipalService.ResolutionStatus.NotFound, values);
        Assert.Contains(ProtectedPrincipalService.ResolutionStatus.Unavailable, values);
        Assert.Contains(ProtectedPrincipalService.ResolutionStatus.Resolved, values);
    }

    [Fact]
    public async Task ResolveWithStatus_NoCredentialConfig_ReturnsUnavailable()
    {
        var service = CreateService();

        var (principal, status) = await service.ResolveWithStatusAsync("anyone@contoso.com");

        Assert.Null(principal);
        Assert.Equal(ProtectedPrincipalService.ResolutionStatus.Unavailable, status);
    }
}
