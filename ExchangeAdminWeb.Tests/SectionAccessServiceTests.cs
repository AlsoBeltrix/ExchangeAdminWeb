using System.Text.Json;
using System.Text.Json.Nodes;
using ExchangeAdminWeb.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

public class SectionAccessServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configDir;
    private readonly string _configFilePath;

    public SectionAccessServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sectionaccess_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _configDir = Path.Combine(_tempDir, "config");
        _configFilePath = Path.Combine(_configDir, "sectionaccess.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); }
        catch { }
    }

    private SectionAccessService CreateService(Dictionary<string, string?>? extraConfig = null)
    {
        var configEntries = new Dictionary<string, string?>
        {
            ["Security:AllowedGroups:0"] = "AllUsersGroup",
            ["Security:AllowedGroups:1"] = "PowerUsersGroup",
            ["Security:AdminGroups:0"] = "AdminGroup"
        };

        if (extraConfig != null)
        {
            foreach (var kv in extraConfig)
                configEntries[kv.Key] = kv.Value;
        }

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configEntries)
            .Build();

        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_tempDir);

        var logger = Substitute.For<ILogger<SectionAccessService>>();

        return new SectionAccessService(config, logger, env, new ExchangeAdminWeb.Modules.ModuleCatalog());
    }

    private void WriteFragmentFile(string json)
    {
        Directory.CreateDirectory(_configDir);
        File.WriteAllText(_configFilePath, json);
    }

    [Fact]
    public void GetGroupsForSection_FragmentExistsWithSection_ReturnsThoseGroups()
    {
        var fragment = new JsonObject
        {
            ["Security"] = new JsonObject
            {
                ["SectionAccess"] = new JsonObject
                {
                    ["MailboxPermissions"] = JsonSerializer.SerializeToNode(new[] { "GroupA", "GroupB" })
                }
            }
        };
        WriteFragmentFile(fragment.ToJsonString());

        var service = CreateService();
        var result = service.GetGroupsForSection("MailboxPermissions");

        Assert.Equal(new[] { "GroupA", "GroupB" }, result);
    }

    [Fact]
    public void GetGroupsForSection_FragmentExistsButSectionMissing_ReturnsEmptyArray()
    {
        var fragment = new JsonObject
        {
            ["Security"] = new JsonObject
            {
                ["SectionAccess"] = new JsonObject
                {
                    ["OtherSection"] = JsonSerializer.SerializeToNode(new[] { "GroupX" })
                }
            }
        };
        WriteFragmentFile(fragment.ToJsonString());

        var service = CreateService();
        var result = service.GetGroupsForSection("MailboxPermissions");

        Assert.Empty(result);
    }

    [Fact]
    public void GetGroupsForSection_FragmentHasCorruptJson_ReturnsEmptyArray()
    {
        WriteFragmentFile("{ this is not valid json !!!");

        var service = CreateService();
        var result = service.GetGroupsForSection("MailboxPermissions");

        Assert.Empty(result);
    }

    [Fact]
    public void GetGroupsForSection_FragmentAbsentButLegacyAppSettings_ReturnsLegacyGroups()
    {
        var legacyConfig = new Dictionary<string, string?>
        {
            ["Security:SectionAccess:MailboxPermissions:0"] = "LegacyGroup1",
            ["Security:SectionAccess:MailboxPermissions:1"] = "LegacyGroup2"
        };

        var service = CreateService(legacyConfig);
        var result = service.GetGroupsForSection("MailboxPermissions");

        Assert.Equal(new[] { "LegacyGroup1", "LegacyGroup2" }, result);
    }

    [Fact]
    public void GetGroupsForSection_NeitherExists_ReadOnlySection_ReturnsAllowedGroups()
    {
        // Read-only modules (DelegationReport, RecipientLookup) are the only
        // sections still allowed to fall back to the global AllowedGroups.
        var service = CreateService();
        var result = service.GetGroupsForSection("DelegationReport");

        Assert.Equal(new[] { "AllUsersGroup", "PowerUsersGroup" }, result);
    }

    [Theory]
    [InlineData("MailboxPermissions")]
    [InlineData("CalendarPermissions")]
    [InlineData("MigrationCheck")]
    [InlineData("MigrationCreate")]
    [InlineData("MigrationManage")]
    [InlineData("OutOfOffice")]
    [InlineData("MailboxPermissionsOnPrem")]
    public void GetGroupsForSection_NeitherExists_MutatingSection_ReturnsEmpty(string section)
    {
        // Mutating modules are FailClosed: losing config/sectionaccess.json
        // (which deploys have done before - commit 0021502) must deny access,
        // never fall back to the global AllowedGroups.
        var service = CreateService();
        var result = service.GetGroupsForSection(section);

        Assert.Empty(result);
    }

    [Fact]
    public void IsSectionAccessConfigured_FragmentFileExists_ReturnsTrue()
    {
        var fragment = new JsonObject
        {
            ["Security"] = new JsonObject
            {
                ["SectionAccess"] = new JsonObject()
            }
        };
        WriteFragmentFile(fragment.ToJsonString());

        var service = CreateService();
        var result = service.IsSectionAccessConfigured();

        Assert.True(result);
    }

    [Fact]
    public void IsSectionAccessConfigured_LegacyAppSettingsExists_ReturnsTrue()
    {
        var legacyConfig = new Dictionary<string, string?>
        {
            ["Security:SectionAccess:SomeSection:0"] = "SomeGroup"
        };

        var service = CreateService(legacyConfig);
        var result = service.IsSectionAccessConfigured();

        Assert.True(result);
    }

    [Fact]
    public void SaveSectionAccess_WritesValidJson_FileExists()
    {
        var service = CreateService();
        var data = new Dictionary<string, string[]>
        {
            ["MailboxPermissions"] = new[] { "GroupA", "GroupB" },
            ["CalendarPermissions"] = new[] { "GroupC" }
        };

        service.SaveSectionAccess(data);

        Assert.True(File.Exists(_configFilePath));

        var json = File.ReadAllText(_configFilePath);
        var doc = JsonNode.Parse(json);
        Assert.NotNull(doc?["Security"]?["SectionAccess"]);

        var mbxGroups = doc!["Security"]!["SectionAccess"]!["MailboxPermissions"]!
            .Deserialize<string[]>();
        Assert.Equal(new[] { "GroupA", "GroupB" }, mbxGroups);
    }

    [Fact]
    public void GetSectionAccess_ReadsBackWhatWasSaved()
    {
        var service = CreateService();
        var data = new Dictionary<string, string[]>
        {
            ["MailboxPermissions"] = new[] { "GroupA", "GroupB" },
            ["CalendarPermissions"] = new[] { "GroupC" }
        };

        service.SaveSectionAccess(data);
        var result = service.GetSectionAccess();

        Assert.Equal(2, result.Count);
        Assert.Equal(new[] { "GroupA", "GroupB" }, result["MailboxPermissions"]);
        Assert.Equal(new[] { "GroupC" }, result["CalendarPermissions"]);
    }

    // --- Corrupt-fragment probe (blank-render-save trap, incident fix #3) ---
    // Admin pages refuse to save over a fragment this probe flags: a save in that
    // state replaces the whole file and wipes every module's groups.

    [Fact]
    public void IsFragmentCorrupt_NoFile_ReturnsFalse()
    {
        Assert.False(CreateService().IsFragmentCorrupt());
    }

    [Fact]
    public void IsFragmentCorrupt_ValidFragment_ReturnsFalse()
    {
        var fragment = new System.Text.Json.Nodes.JsonObject
        {
            ["Security"] = new System.Text.Json.Nodes.JsonObject
            {
                ["SectionAccess"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["MailboxPermissions"] = new System.Text.Json.Nodes.JsonArray("GroupA")
                }
            }
        };
        WriteFragmentFile(fragment.ToJsonString());

        Assert.False(CreateService().IsFragmentCorrupt());
    }

    [Fact]
    public void IsFragmentCorrupt_InvalidJson_ReturnsTrue()
    {
        WriteFragmentFile("{ this is not valid json !!!");

        Assert.True(CreateService().IsFragmentCorrupt());
    }

    [Fact]
    public void IsFragmentCorrupt_MissingSectionAccessNode_ReturnsTrue()
    {
        WriteFragmentFile("""{ "Security": { } }""");

        Assert.True(CreateService().IsFragmentCorrupt());
    }
}
