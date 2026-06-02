using System.Text.Json;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

public class ModuleEnablementServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configDir;
    private readonly string _configFilePath;
    private readonly ModuleCatalog _catalog;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<ModuleEnablementService> _logger;
    private readonly ModuleConfigService _moduleConfig;
    private readonly IConfiguration _config;

    public ModuleEnablementServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"moduleenablement_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _configDir = Path.Combine(_tempDir, "config");
        _configFilePath = Path.Combine(_configDir, "modules-enabled.json");

        _catalog = new ModuleCatalog();

        _env = Substitute.For<IWebHostEnvironment>();
        _env.ContentRootPath.Returns(_tempDir);

        _logger = Substitute.For<ILogger<ModuleEnablementService>>();

        var moduleConfigLogger = Substitute.For<ILogger<ModuleConfigService>>();
        _moduleConfig = new ModuleConfigService(_catalog, _env, moduleConfigLogger);
        _config = new ConfigurationBuilder().Build();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); }
        catch { }
    }

    private ModuleEnablementService CreateService()
    {
        return new ModuleEnablementService(_catalog, _env, _moduleConfig, _config, _logger);
    }

    private void WriteEnablementFile(Dictionary<string, bool> state)
    {
        Directory.CreateDirectory(_configDir);
        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_configFilePath, json);
    }

    private void WriteRawFile(string content)
    {
        Directory.CreateDirectory(_configDir);
        File.WriteAllText(_configFilePath, content);
    }

    [Fact]
    public void IsModuleEnabled_SystemModule_ReturnsTrue_RegardlessOfFileState()
    {
        // System module should always be enabled even with no file
        var service = CreateService();
        Assert.True(service.IsModuleEnabled("AdminSettings"));
    }

    [Fact]
    public void IsModuleRawEnabled_NoFileExists_ReturnsEnabledByDefault()
    {
        var service = CreateService();

        // MailboxPermissions has EnabledByDefault = true
        Assert.True(service.IsModuleRawEnabled("MailboxPermissions"));
    }

    [Fact]
    public void IsModuleEnabled_NoFileExists_ChildDisabledWhenParentDisabled()
    {
        var service = CreateService();

        // MailboxPermissions has EnabledByDefault = true but DependsOn = ExchangeOnline
        // ExchangeOnline has EnabledByDefault = false, so effective = false
        Assert.False(service.IsModuleEnabled("MailboxPermissions"));
    }

    [Fact]
    public void IsModuleEnabled_FileExists_ReadsStateFromFile()
    {
        var state = new Dictionary<string, bool>
        {
            ["ExchangeOnline"] = true,
            ["MailboxPermissions"] = false,
            ["Migration"] = true
        };
        WriteEnablementFile(state);

        var service = CreateService();

        Assert.False(service.IsModuleEnabled("MailboxPermissions"));
        Assert.True(service.IsModuleEnabled("Migration"));
    }

    [Fact]
    public void IsModuleEnabled_CorruptFile_ReturnsFalseForAllNonSystemModules()
    {
        WriteRawFile("{ this is not valid json !!!");

        var service = CreateService();

        // All non-system modules should be disabled (fail-closed)
        foreach (var module in _catalog.GetAll().Where(m => !m.IsSystemModule))
        {
            Assert.False(service.IsModuleEnabled(module.Id),
                $"Expected module '{module.Id}' to be disabled when file is corrupt");
        }
    }

    [Fact]
    public void GetAllEnablement_ReturnsAllModulesWithCorrectState()
    {
        var state = new Dictionary<string, bool>
        {
            ["MailboxPermissions"] = false,
            ["Migration"] = true,
            ["MessageTrace"] = false
        };
        WriteEnablementFile(state);

        var service = CreateService();
        var result = service.GetAllEnablement();

        // Should contain all modules
        Assert.Equal(_catalog.GetAll().Count, result.Count);

        // System modules always enabled
        Assert.True(result["AdminSettings"]);
        Assert.True(result["AdminEventLog"]);

        // Non-system modules follow file state
        Assert.False(result["MailboxPermissions"]);
        Assert.True(result["Migration"]);
        Assert.False(result["MessageTrace"]);

        // Modules not in file use EnabledByDefault
        Assert.True(result["CalendarPermissions"]);
    }

    [Fact]
    public void SaveEnablement_WritesValidJson_ThatCanBeReadBack()
    {
        var service = CreateService();
        var enablement = new Dictionary<string, bool>
        {
            ["MailboxPermissions"] = true,
            ["CalendarPermissions"] = false,
            ["Migration"] = true,
            ["DelegationReport"] = false,
            ["MessageTrace"] = true,
            ["RecipientLookup"] = false,
            ["OutOfOffice"] = true
        };

        service.SaveEnablement(enablement);

        Assert.True(File.Exists(_configFilePath));

        // Read back and verify it's valid JSON
        var json = File.ReadAllText(_configFilePath);
        var readBack = JsonSerializer.Deserialize<Dictionary<string, bool>>(json);
        Assert.NotNull(readBack);
        Assert.False(readBack!["CalendarPermissions"]);
        Assert.True(readBack["MailboxPermissions"]);
    }

    [Fact]
    public void SaveEnablement_AtomicWrite_FileExistsAfterSave()
    {
        var service = CreateService();

        // First save to create the file
        var first = new Dictionary<string, bool>
        {
            ["MailboxPermissions"] = true,
            ["CalendarPermissions"] = true
        };
        service.SaveEnablement(first);
        Assert.True(File.Exists(_configFilePath));

        // Second save exercises the File.Replace path (file already exists)
        var second = new Dictionary<string, bool>
        {
            ["MailboxPermissions"] = false,
            ["CalendarPermissions"] = false
        };
        service.SaveEnablement(second);
        Assert.True(File.Exists(_configFilePath));

        // Verify the second write took effect
        var json = File.ReadAllText(_configFilePath);
        var readBack = JsonSerializer.Deserialize<Dictionary<string, bool>>(json);
        Assert.NotNull(readBack);
        Assert.False(readBack!["MailboxPermissions"]);
        Assert.False(readBack["CalendarPermissions"]);
    }

    [Fact]
    public void SystemModules_AlwaysReportEnabled_EvenIfFileExplicitlySaysFalse()
    {
        // Write a file that explicitly sets system module to false
        var state = new Dictionary<string, bool>
        {
            ["AdminSettings"] = false,
            ["AdminEventLog"] = false
        };
        WriteEnablementFile(state);

        var service = CreateService();

        // AdminSettings is a system module — always enabled regardless of file
        Assert.True(service.IsModuleEnabled("AdminSettings"));
        // AdminEventLog is no longer a system module — respects the file setting
        Assert.False(service.IsModuleEnabled("AdminEventLog"));
    }

    [Fact]
    public void DisableModule_SaveThenRead_ReturnsFalse()
    {
        var service = CreateService();

        // Save with MailboxPermissions disabled
        var enablement = new Dictionary<string, bool>
        {
            ["MailboxPermissions"] = false,
            ["CalendarPermissions"] = true,
            ["Migration"] = true,
            ["DelegationReport"] = true,
            ["MessageTrace"] = true,
            ["RecipientLookup"] = true,
            ["OutOfOffice"] = true
        };
        service.SaveEnablement(enablement);

        // Create a new service instance to read from file (no caching)
        var service2 = CreateService();
        Assert.False(service2.IsModuleEnabled("MailboxPermissions"));
    }

    // --- Parent/child cascade tests ---

    [Fact]
    public void IsModuleEnabled_ParentOffChildRawOn_EffectiveFalse()
    {
        var state = new Dictionary<string, bool>
        {
            ["ExchangeOnline"] = false,
            ["MailboxPermissions"] = true
        };
        WriteEnablementFile(state);

        var service = CreateService();

        Assert.True(service.IsModuleRawEnabled("MailboxPermissions"));
        Assert.False(service.IsModuleEnabled("MailboxPermissions"));
    }

    [Fact]
    public void IsModuleEnabled_ParentOnChildRawOn_EffectiveTrue()
    {
        var state = new Dictionary<string, bool>
        {
            ["ExchangeOnline"] = true,
            ["MailboxPermissions"] = true
        };
        WriteEnablementFile(state);

        var service = CreateService();

        Assert.True(service.IsModuleRawEnabled("MailboxPermissions"));
        Assert.True(service.IsModuleEnabled("MailboxPermissions"));
    }

    [Fact]
    public void IsModuleEnabled_ParentOnChildRawOff_EffectiveFalse()
    {
        var state = new Dictionary<string, bool>
        {
            ["ExchangeOnline"] = true,
            ["MailboxPermissions"] = false
        };
        WriteEnablementFile(state);

        var service = CreateService();

        Assert.False(service.IsModuleRawEnabled("MailboxPermissions"));
        Assert.False(service.IsModuleEnabled("MailboxPermissions"));
    }

    [Fact]
    public void RawChildState_SurvivesParentDisableEnableCycle()
    {
        // Start with both enabled
        var state = new Dictionary<string, bool>
        {
            ["ExchangeOnline"] = true,
            ["MailboxPermissions"] = true
        };
        WriteEnablementFile(state);

        var service = CreateService();
        Assert.True(service.IsModuleEnabled("MailboxPermissions"));

        // Disable parent
        state["ExchangeOnline"] = false;
        WriteEnablementFile(state);

        var service2 = CreateService();
        Assert.False(service2.IsModuleEnabled("MailboxPermissions"));
        Assert.True(service2.IsModuleRawEnabled("MailboxPermissions")); // raw state preserved

        // Re-enable parent
        state["ExchangeOnline"] = true;
        WriteEnablementFile(state);

        var service3 = CreateService();
        Assert.True(service3.IsModuleEnabled("MailboxPermissions")); // child reactivated
    }

    [Fact]
    public void IsModuleEnabled_IndependentModule_NotAffectedByExchangeOnline()
    {
        var state = new Dictionary<string, bool>
        {
            ["ExchangeOnline"] = false,
            ["MfaReset"] = true
        };
        WriteEnablementFile(state);

        var service = CreateService();

        // MfaReset has no DependsOn, so it should be enabled regardless of ExchangeOnline
        Assert.True(service.IsModuleEnabled("MfaReset"));
    }

    [Fact]
    public void UpgradeMigration_NoExoConfig_SetsExchangeOnlineFalse()
    {
        // Write enablement file without ExchangeOnline key
        var state = new Dictionary<string, bool>
        {
            ["MailboxPermissions"] = true
        };
        WriteEnablementFile(state);

        var service = CreateService();
        var result = service.GetAllEnablement();

        // Migration should have set ExchangeOnline = false (no config exists)
        Assert.False(result["ExchangeOnline"]);
    }

    [Fact]
    public void UpgradeMigration_ExistingKey_NotOverwritten()
    {
        var state = new Dictionary<string, bool>
        {
            ["ExchangeOnline"] = true,
            ["MailboxPermissions"] = true
        };
        WriteEnablementFile(state);

        var service = CreateService();
        var result = service.GetAllEnablement();

        // Existing key should not be overwritten by migration
        Assert.True(result["ExchangeOnline"]);
    }
}
