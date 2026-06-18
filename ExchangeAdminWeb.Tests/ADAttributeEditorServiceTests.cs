using System.Text.Json;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

public class ADAttributeEditorServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configDir;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<ADAttributeEditorService> _logger;

    public ADAttributeEditorServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"adattr_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(_configDir);

        _env = Substitute.For<IWebHostEnvironment>();
        _env.ContentRootPath.Returns(_tempDir);

        _logger = Substitute.For<ILogger<ADAttributeEditorService>>();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); }
        catch { }
    }

    private ADAttributeEditorService CreateService()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delinea:SecretServerUrl"] = "https://fake.local",
                ["Audit:LogRoot"] = _tempDir,
                ["Audit:RotationPeriod"] = "daily",
                ["Email:SmtpHost"] = "localhost",
                ["Email:SmtpPort"] = "25",
                ["Email:SmtpUseSsl"] = "false"
            })
            .Build();

        var catalog = new ModuleCatalog();
        var moduleConfigLogger = Substitute.For<ILogger<ModuleConfigService>>();
        var moduleConfig = new ModuleConfigService(catalog, _env, moduleConfigLogger);

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());
        var extLog = new ExtendedLogService(config, _env, TestConfigStore.CreateAppSettings(_tempDir), Substitute.For<ILogger<ExtendedLogService>>());
        var jsonlLog = new JsonlLogService(config, Substitute.For<ILogger<JsonlLogService>>());
        var operationTrace = new OperationTraceService(config, jsonlLog);
        var delineaService = new DelineaService(httpClientFactory, config, Substitute.For<ILogger<DelineaService>>(), extLog, operationTrace);
        var moduleCredentials = new ModuleCredentialService(moduleConfig, delineaService, Substitute.For<ILogger<ModuleCredentialService>>());
        var protectedPrincipalService = new ProtectedPrincipalService(_env, config, moduleConfig, delineaService, Substitute.For<ILogger<ProtectedPrincipalService>>());
        var audit = new AuditService(jsonlLog, operationTrace);
        var email = new EmailService(config, Substitute.For<ILogger<EmailService>>());

        return new ADAttributeEditorService(moduleCredentials, protectedPrincipalService, operationTrace, audit, email, moduleConfig, _env, _logger);
    }

    private void WriteAllowlistConfig(object config)
    {
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(_configDir, "ad-editable-attributes.json"), json);
    }

    private void WriteRawAllowlistConfig(string content)
    {
        File.WriteAllText(Path.Combine(_configDir, "ad-editable-attributes.json"), content);
    }

    // --- Hard Denylist: Exact Match ---

    [Theory]
    [InlineData("userAccountControl")]
    [InlineData("pwdLastSet")]
    [InlineData("unicodePwd")]
    [InlineData("userPassword")]
    [InlineData("lockoutTime")]
    [InlineData("accountExpires")]
    [InlineData("memberOf")]
    [InlineData("primaryGroupID")]
    [InlineData("adminCount")]
    [InlineData("badPwdCount")]
    [InlineData("objectSid")]
    [InlineData("objectGUID")]
    [InlineData("distinguishedName")]
    [InlineData("servicePrincipalName")]
    [InlineData("altSecurityIdentities")]
    [InlineData("nTSecurityDescriptor")]
    public void IsDenylisted_ExactMatch_ReturnsTrue(string attributeName)
    {
        Assert.True(ADAttributeEditorService.IsDenylisted(attributeName));
    }

    // --- Hard Denylist: Case-Insensitive ---

    [Theory]
    [InlineData("UserAccountControl")]
    [InlineData("USERACCOUNTCONTROL")]
    [InlineData("useraccountcontrol")]
    [InlineData("PwdLastSet")]
    [InlineData("MEMBEROF")]
    [InlineData("ObjectSID")]
    [InlineData("NTSECURITYDESCRIPTOR")]
    public void IsDenylisted_CaseInsensitive_ReturnsTrue(string attributeName)
    {
        Assert.True(ADAttributeEditorService.IsDenylisted(attributeName));
    }

    // --- Hard Denylist: Prefix Match ---

    [Theory]
    [InlineData("lastLogonTimestamp")]
    [InlineData("lastLogon")]
    [InlineData("lastLogonDate")]
    [InlineData("LASTLOGONTIMESTAMP")]
    [InlineData("msDS-KeyCredentialLink")]
    [InlineData("msDS-AllowedToActOnBehalfOfOtherIdentity")]
    [InlineData("msDS-SupportedEncryptionTypes")]
    [InlineData("MSDS-KEYCREDENTIALLINK")]
    public void IsDenylisted_PrefixMatch_ReturnsTrue(string attributeName)
    {
        Assert.True(ADAttributeEditorService.IsDenylisted(attributeName));
    }

    // --- Non-Denylisted Attribute Is Allowed ---

    [Theory]
    [InlineData("extensionAttribute1")]
    [InlineData("extensionAttribute15")]
    [InlineData("department")]
    [InlineData("title")]
    [InlineData("telephoneNumber")]
    [InlineData("displayName")]
    [InlineData("givenName")]
    [InlineData("sn")]
    [InlineData("streetAddress")]
    [InlineData("company")]
    public void IsDenylisted_NonDenylisted_ReturnsFalse(string attributeName)
    {
        Assert.False(ADAttributeEditorService.IsDenylisted(attributeName));
    }

    // --- Denylist Enforced at Allowlist Load Time ---

    [Fact]
    public void GetAllowlist_DenylistedAttribute_IsStrippedFromResult()
    {
        WriteAllowlistConfig(new
        {
            Attributes = new[]
            {
                new { Name = "extensionAttribute1", Label = "Extension 1", Type = "String", Required = false, AllowClear = true },
                new { Name = "userAccountControl", Label = "UAC", Type = "String", Required = false, AllowClear = true },
                new { Name = "department", Label = "Department", Type = "String", Required = false, AllowClear = true },
                new { Name = "lastLogonTimestamp", Label = "Last Logon", Type = "String", Required = false, AllowClear = true },
                new { Name = "msDS-KeyCredentialLink", Label = "Key Cred", Type = "String", Required = false, AllowClear = true }
            }
        });

        var service = CreateService();
        var allowlist = service.GetAllowlist();

        Assert.NotNull(allowlist);
        Assert.Equal(2, allowlist!.Count);
        Assert.Contains(allowlist, a => a.Name == "extensionAttribute1");
        Assert.Contains(allowlist, a => a.Name == "department");
        Assert.DoesNotContain(allowlist, a => a.Name == "userAccountControl");
        Assert.DoesNotContain(allowlist, a => a.Name == "lastLogonTimestamp");
        Assert.DoesNotContain(allowlist, a => a.Name == "msDS-KeyCredentialLink");
    }

    // --- Contradictory Config: Required=true + AllowClear=true ---

    [Fact]
    public void GetAllowlist_ContradictoryRequiredAndAllowClear_ReturnsNull()
    {
        WriteAllowlistConfig(new
        {
            Attributes = new[]
            {
                new { Name = "extensionAttribute1", Label = "Extension 1", Type = "String", Required = true, AllowClear = true }
            }
        });

        var service = CreateService();
        var allowlist = service.GetAllowlist();

        Assert.Null(allowlist);
    }

    // --- Choice Type With No Choices ---

    [Fact]
    public void GetAllowlist_ChoiceTypeWithNoChoices_ReturnsNull()
    {
        WriteAllowlistConfig(new
        {
            Attributes = new[]
            {
                new { Name = "department", Label = "Department", Type = "Choice", Required = false, AllowClear = true }
            }
        });

        var service = CreateService();
        var allowlist = service.GetAllowlist();

        Assert.Null(allowlist);
    }

    [Fact]
    public void GetAllowlist_ChoiceTypeWithEmptyChoicesArray_ReturnsNull()
    {
        WriteAllowlistConfig(new
        {
            Attributes = new[]
            {
                new { Name = "department", Label = "Department", Type = "Choice", Choices = Array.Empty<string>(), Required = false, AllowClear = true }
            }
        });

        var service = CreateService();
        var allowlist = service.GetAllowlist();

        Assert.Null(allowlist);
    }

    // --- Missing Attributes Section ---

    [Fact]
    public void GetAllowlist_MissingAttributesSection_ReturnsNull()
    {
        WriteRawAllowlistConfig("{ \"SomethingElse\": [] }");

        var service = CreateService();
        var allowlist = service.GetAllowlist();

        Assert.Null(allowlist);
    }

    [Fact]
    public void GetAllowlist_EmptyObject_ReturnsNull()
    {
        WriteRawAllowlistConfig("{}");

        var service = CreateService();
        var allowlist = service.GetAllowlist();

        Assert.Null(allowlist);
    }

    // --- Corrupt JSON ---

    [Fact]
    public void GetAllowlist_CorruptJson_ReturnsNull()
    {
        WriteRawAllowlistConfig("NOT VALID JSON {{{");

        var service = CreateService();
        var allowlist = service.GetAllowlist();

        Assert.Null(allowlist);
    }

    [Fact]
    public void GetAllowlist_TruncatedJson_ReturnsNull()
    {
        WriteRawAllowlistConfig("{ \"Attributes\": [ { \"Name\": \"extensionAttribute1\"");

        var service = CreateService();
        var allowlist = service.GetAllowlist();

        Assert.Null(allowlist);
    }

    // --- Valid Allowlist Loads Correctly ---

    [Fact]
    public void GetAllowlist_ValidConfig_LoadsAllAttributes()
    {
        WriteAllowlistConfig(new
        {
            Attributes = new object[]
            {
                new { Name = "extensionAttribute1", Label = "Extension 1", Type = "String", Required = false, AllowClear = true, MaxLength = 256 },
                new { Name = "department", Label = "Department", Type = "Choice", Choices = new[] { "IT", "HR", "Finance" }, Required = true, AllowClear = false },
                new { Name = "telephoneNumber", Label = "Phone", Type = "String", Required = false, AllowClear = true, Pattern = @"^\+?[\d\s\-()]+$" }
            }
        });

        var service = CreateService();
        var allowlist = service.GetAllowlist();

        Assert.NotNull(allowlist);
        Assert.Equal(3, allowlist!.Count);

        var ext1 = allowlist.Single(a => a.Name == "extensionAttribute1");
        Assert.Equal("Extension 1", ext1.Label);
        Assert.Equal("String", ext1.Type);
        Assert.False(ext1.Required);
        Assert.True(ext1.AllowClear);
        Assert.Equal(256, ext1.MaxLength);

        var dept = allowlist.Single(a => a.Name == "department");
        Assert.Equal("Choice", dept.Type);
        Assert.True(dept.Required);
        Assert.False(dept.AllowClear);
        Assert.NotNull(dept.Choices);
        Assert.Equal(3, dept.Choices!.Length);
        Assert.Contains("IT", dept.Choices);
        Assert.Contains("HR", dept.Choices);
        Assert.Contains("Finance", dept.Choices);

        var phone = allowlist.Single(a => a.Name == "telephoneNumber");
        Assert.Equal(@"^\+?[\d\s\-()]+$", phone.Pattern);
    }

    [Fact]
    public void GetAllowlist_NoConfigFile_ReturnsEmptyList()
    {
        // Delete the config file if it exists (we created the dir but not the file)
        var configPath = Path.Combine(_configDir, "ad-editable-attributes.json");
        if (File.Exists(configPath))
            File.Delete(configPath);

        var service = CreateService();
        var allowlist = service.GetAllowlist();

        Assert.NotNull(allowlist);
        Assert.Empty(allowlist!);
    }

    // --- Allowlist Cache Invalidation ---

    [Fact]
    public void SaveAllowlist_InvalidatesCache_SubsequentGetReturnsUpdated()
    {
        WriteAllowlistConfig(new
        {
            Attributes = new[]
            {
                new { Name = "extensionAttribute1", Label = "Extension 1", Type = "String", Required = false, AllowClear = true }
            }
        });

        var service = CreateService();

        // First load caches the result
        var first = service.GetAllowlist();
        Assert.NotNull(first);
        Assert.Single(first!);
        Assert.Equal("extensionAttribute1", first[0].Name);

        // Save a new allowlist (this should invalidate the cache)
        var newAttrs = new List<EditableAttribute>
        {
            new("department", "Department", "Choice", new[] { "IT", "HR" }, true, false, null, null),
            new("title", "Job Title", "String", null, false, true, 100, null)
        };
        service.SaveAllowlist(newAttrs);

        // Subsequent get should reflect the new data
        var second = service.GetAllowlist();
        Assert.NotNull(second);
        Assert.Equal(2, second!.Count);
        Assert.Contains(second, a => a.Name == "department");
        Assert.Contains(second, a => a.Name == "title");
        Assert.DoesNotContain(second, a => a.Name == "extensionAttribute1");
    }

    [Fact]
    public void InvalidateAllowlistCache_ForcesReload()
    {
        WriteAllowlistConfig(new
        {
            Attributes = new[]
            {
                new { Name = "extensionAttribute1", Label = "Extension 1", Type = "String", Required = false, AllowClear = true }
            }
        });

        var service = CreateService();

        // Load initial
        var first = service.GetAllowlist();
        Assert.NotNull(first);
        Assert.Single(first!);

        // Overwrite file directly (bypassing SaveAllowlist)
        WriteAllowlistConfig(new
        {
            Attributes = new[]
            {
                new { Name = "extensionAttribute1", Label = "Extension 1", Type = "String", Required = false, AllowClear = true },
                new { Name = "title", Label = "Title", Type = "String", Required = false, AllowClear = true }
            }
        });

        // Without invalidation, cache should return the old result
        var cached = service.GetAllowlist();
        Assert.Single(cached!);

        // Invalidate and verify new result is loaded
        service.InvalidateAllowlistCache();
        var reloaded = service.GetAllowlist();
        Assert.NotNull(reloaded);
        Assert.Equal(2, reloaded!.Count);
    }

    // --- Disk-fresh corruption gate (IsAllowlistCorrupt) ---

    [Fact]
    public void IsAllowlistCorrupt_ValidThenCorruptedWithinTtl_DetectsCorruptionWhileCacheStaysValid()
    {
        // The pre-save gate bug (GPT finding 1): GetAllowlist serves a valid list from the
        // 30s cache, so if the file became corrupt after a valid load, a cache-based gate
        // would pass and let SaveAllowlist overwrite the corrupt store. IsAllowlistCorrupt
        // must read disk fresh and catch it. This test fails if the gate ever consults the
        // cache instead of disk.
        WriteAllowlistConfig(new
        {
            Attributes = new[]
            {
                new { Name = "extensionAttribute1", Label = "Extension 1", Type = "String", Required = false, AllowClear = true }
            }
        });

        var service = CreateService();

        // Prime the cache with a valid load.
        var first = service.GetAllowlist();
        Assert.NotNull(first);
        Assert.Single(first!);

        // Corrupt the file directly, simulating an operator/promote clobber within the TTL.
        WriteRawAllowlistConfig("{ this is not valid json");

        // The cache still hands back the stale-but-valid list (this is expected for the
        // runtime read path)...
        var stillCached = service.GetAllowlist();
        Assert.NotNull(stillCached);
        Assert.Single(stillCached!);

        // ...but the disk-fresh gate must see the corruption and report it.
        Assert.True(service.IsAllowlistCorrupt(),
            "IsAllowlistCorrupt must read disk fresh and detect corruption the cache masks");
    }

    [Fact]
    public void IsAllowlistCorrupt_ValidFile_ReturnsFalse()
    {
        WriteAllowlistConfig(new
        {
            Attributes = new[]
            {
                new { Name = "extensionAttribute1", Label = "Extension 1", Type = "String", Required = false, AllowClear = true }
            }
        });

        var service = CreateService();
        Assert.False(service.IsAllowlistCorrupt());
    }

    [Fact]
    public void IsAllowlistCorrupt_NoFile_ReturnsFalse()
    {
        // No config file is a valid "nothing allowlisted" state, not corruption.
        var configPath = Path.Combine(_configDir, "ad-editable-attributes.json");
        if (File.Exists(configPath)) File.Delete(configPath);

        var service = CreateService();
        Assert.False(service.IsAllowlistCorrupt());
    }

    // --- DefaultSearchBase Enforcement (Conceptual) ---

    [Fact]
    public void GetAllowlist_ValidChoiceWithChoicesProvided_Succeeds()
    {
        // Verify that a Choice attribute with valid choices passes validation
        WriteAllowlistConfig(new
        {
            Attributes = new[]
            {
                new { Name = "department", Label = "Department", Type = "Choice", Choices = new[] { "IT", "HR", "Finance" }, Required = true, AllowClear = false }
            }
        });

        var service = CreateService();
        var allowlist = service.GetAllowlist();

        Assert.NotNull(allowlist);
        Assert.Single(allowlist!);
        Assert.Equal("department", allowlist[0].Name);
    }

    // --- SaveAllowlist Atomic Write ---

    [Fact]
    public void SaveAllowlist_WritesAtomically_FileIsValid()
    {
        var service = CreateService();
        var attrs = new List<EditableAttribute>
        {
            new("extensionAttribute1", "Extension 1", "String", null, false, true, 256, null),
            new("department", "Department", "Choice", new[] { "IT", "HR" }, true, false, null, null)
        };

        service.SaveAllowlist(attrs);

        // Verify the file exists and contains valid JSON
        var configPath = Path.Combine(_configDir, "ad-editable-attributes.json");
        Assert.True(File.Exists(configPath));
        var json = File.ReadAllText(configPath);
        var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("attributes", out var attrsElement));
        Assert.Equal(2, attrsElement.GetArrayLength());
    }

    // --- SaveAllowlist Strips Denylisted Entries on Re-read ---

    [Fact]
    public void SaveAllowlist_ThenGetAllowlist_DenylistedNotReturned()
    {
        var service = CreateService();

        // SaveAllowlist does not itself enforce the denylist on write,
        // but GetAllowlist will strip denylisted on load
        var attrs = new List<EditableAttribute>
        {
            new("extensionAttribute1", "Extension 1", "String", null, false, true, null, null),
            new("memberOf", "Member Of", "String", null, false, true, null, null)
        };
        service.SaveAllowlist(attrs);

        var loaded = service.GetAllowlist();
        Assert.NotNull(loaded);
        Assert.Single(loaded!);
        Assert.Equal("extensionAttribute1", loaded[0].Name);
    }

    // --- Edge Cases for IsDenylisted ---

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("last")]
    [InlineData("msD")]
    [InlineData("msd-something")]
    public void IsDenylisted_NearMissPrefixes_ReturnsFalse(string attributeName)
    {
        // These should NOT match the prefixes "lastLogon" or "msDS-"
        Assert.False(ADAttributeEditorService.IsDenylisted(attributeName));
    }

    [Theory]
    [InlineData("lastLogon")]
    [InlineData("msDS-")]
    public void IsDenylisted_ExactPrefixString_ReturnsTrue(string attributeName)
    {
        // The prefix itself should be denied (starts with the prefix)
        Assert.True(ADAttributeEditorService.IsDenylisted(attributeName));
    }

    // --- Failure-path auditing (source-level guard) ---

    [Fact]
    public void PerformSave_ThrownSetAdUserFailure_IsAudited()
    {
        // Set-ADUser runs with -ErrorAction Stop, so real failures THROW from
        // ps.Invoke() and bypass the HadErrors branch. PerformSave opens a real
        // runspace requiring the ActiveDirectory module, so it cannot be unit-
        // hosted; this source-level guard (same approach as
        // PageAuthorizationRecheckTests) fails if the catch-and-audit around the
        // Set-ADUser invoke is ever removed, which would recreate the
        // unaudited-failure dead branch.
        var source = ReadServiceSource("ADAttributeEditorService.cs");
        var start = source.IndexOf("private AttributeSaveResult PerformSave", StringComparison.Ordinal);
        Assert.True(start >= 0, "PerformSave not found");
        var setAdUser = source.IndexOf("AddCommand(\"Set-ADUser\")", start, StringComparison.Ordinal);
        Assert.True(setAdUser >= 0, "Set-ADUser command not found in PerformSave");

        var afterSetAdUser = source[setAdUser..];
        var catchIdx = afterSetAdUser.IndexOf("catch (Exception ex)", StringComparison.Ordinal);
        Assert.True(catchIdx >= 0, "Set-ADUser invoke is no longer wrapped in try/catch");
        Assert.Contains("LogAudit(target, changes, performedBy, ip, ticket, false",
            afterSetAdUser[catchIdx..afterSetAdUser.IndexOf("ps.Commands.Clear()", catchIdx, StringComparison.Ordinal)]);
    }

    private static string ReadServiceSource(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var path = Path.Combine(dir.FullName, "Services", fileName);
            if (File.Exists(path))
                return File.ReadAllText(path);
            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not locate Services/{fileName} from test base directory.");
    }
}
