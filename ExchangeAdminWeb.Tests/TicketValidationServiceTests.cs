using System.Net;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Tests for TicketValidationService (docs/BitLockerMandatoryTicket-Plan.md S1).
/// </summary>
/// <remarks>
/// Module config is a real store over a temp DB (the BitLockerRecoveryTests
/// fixture shape); ServiceNow is the real client over a counting stub handler,
/// because the behaviors under test - dormant short-circuit, delegation, and
/// no-call-in-Off-mode - are exactly the ones a mocked client would assume away.
/// </remarks>
public sealed class TicketValidationServiceTests : IDisposable
{
    private const string ModuleId = "BitLockerRecovery";
    private const string OtherModuleId = "Migration";

    private readonly string _configDir =
        Path.Combine(Path.GetTempPath(), $"tv-config-{Guid.NewGuid():N}");

    private readonly CountingHandler _handler = new();

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_configDir))
            {
                Directory.Delete(_configDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Temp-dir cleanup is best-effort.
        }
    }

    private ModuleConfigService CreateModuleConfig(
        IDictionary<string, (string ModuleId, string Value)>? switches = null,
        bool corruptStore = false)
    {
        Directory.CreateDirectory(_configDir);

        var env = Substitute.For<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
        env.ContentRootPath.Returns(_configDir);

        var repository = corruptStore
            ? new ModuleConfigRepository(new ThrowingConfigStore())
            : TestConfigStore.CreateModuleConfig(_configDir);
        var moduleConfig = new ModuleConfigService(
            new ModuleCatalog(),
            env,
            repository,
            NullLogger<ModuleConfigService>.Instance);

        if (!corruptStore && switches is { Count: > 0 })
        {
            foreach (var (_, (moduleId, value)) in switches)
            {
                moduleConfig.SaveModuleConfig(
                    moduleId,
                    new Dictionary<string, string>
                    {
                        [TicketValidationService.ValidateTicketsKey] = value,
                    });
            }
        }

        return moduleConfig;
    }

    private ModuleConfigService ConfigWithSwitch(string value, string moduleId = ModuleId) =>
        CreateModuleConfig(new Dictionary<string, (string, string)>
        {
            ["only"] = (moduleId, value),
        });

    private ServiceNowService CreateServiceNow(bool enabled)
    {
        var settings = new Dictionary<string, string?>();
        if (enabled)
        {
            settings["ServiceNow:Enabled"] = "true";
            settings["ServiceNow:InstanceUrl"] = "https://example.invalid";
            settings["ServiceNow:Username"] = "svc";
            settings["ServiceNow:Password"] = "pw";
        }

        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("ServiceNow").Returns(new HttpClient(_handler));

        return new ServiceNowService(config, NullLogger<ServiceNowService>.Instance, factory);
    }

    private TicketValidationService CreateValidator(
        ModuleConfigService moduleConfig,
        bool serviceNowEnabled = false) =>
        new(moduleConfig, CreateServiceNow(serviceNowEnabled));

    // --- AC1 / AC5: presence is never waived -------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Off_BlankTicketRejected(string? ticket)
    {
        var validator = CreateValidator(CreateModuleConfig());

        var result = await validator.ValidateAsync(ModuleId, ticket);

        Assert.Equal(TicketGateOutcome.Rejected, result.Outcome);
        Assert.Contains("ticket", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task On_BlankTicketStillRejected()
    {
        // Rejected, not Unavailable: presence is checked before any dormancy or
        // delegation logic, so the operator hears "enter a ticket", not a
        // config complaint.
        var validator = CreateValidator(ConfigWithSwitch("true"));

        var result = await validator.ValidateAsync(ModuleId, "   ");

        Assert.Equal(TicketGateOutcome.Rejected, result.Outcome);
    }

    // --- AC1: Off mode is presence-only and never calls ServiceNow ----------

    [Fact]
    public async Task Off_AnyNonBlankTicketAccepted()
    {
        var absent = CreateValidator(CreateModuleConfig());
        Assert.True((await absent.ValidateAsync(ModuleId, "asdf")).Accepted);

        var explicitOff = CreateValidator(ConfigWithSwitch("false"));
        Assert.True((await explicitOff.ValidateAsync(ModuleId, "INC0001")).Accepted);

        Assert.Equal(0, _handler.Requests);
    }

    // --- AC2: On while ServiceNow is dormant refuses, never passes ----------

    [Fact]
    public async Task On_DormantServiceNowUnavailable()
    {
        var validator = CreateValidator(ConfigWithSwitch("true"), serviceNowEnabled: false);

        var result = await validator.ValidateAsync(ModuleId, "INC0001");

        Assert.Equal(TicketGateOutcome.Unavailable, result.Outcome);
        Assert.Contains("ServiceNow", result.Message);
        Assert.Equal(0, _handler.Requests);
    }

    // --- AC3: On with ServiceNow enabled delegates and maps IsValid ----------

    [Fact]
    public async Task On_EnabledDelegatesToServiceNow()
    {
        _handler.NextBody =
            """{"result":[{"number":"INC0001","state":"2","short_description":"d","sys_id":"s"}]}""";
        var validator = CreateValidator(ConfigWithSwitch("true"), serviceNowEnabled: true);

        var accepted = await validator.ValidateAsync(ModuleId, "INC0001");
        Assert.True(accepted.Accepted);
        Assert.Equal(1, _handler.Requests);

        _handler.NextBody = """{"result":[]}""";
        var rejected = await validator.ValidateAsync(ModuleId, "INC0002");
        Assert.Equal(TicketGateOutcome.Rejected, rejected.Outcome);
        Assert.Contains("not found", rejected.Message, StringComparison.OrdinalIgnoreCase);
    }

    // --- AC4: per-module isolation, corrupt config, invalid switch ----------

    [Fact]
    public async Task ReadsConfigForTheModuleItIsGiven()
    {
        // One validator, two modules: the switched-on module refuses (dormant
        // ServiceNow), the unconfigured one stays presence-only.
        var validator = CreateValidator(ConfigWithSwitch("true", ModuleId));

        var switchedOn = await validator.ValidateAsync(ModuleId, "INC0001");
        var untouched = await validator.ValidateAsync(OtherModuleId, "INC0001");

        Assert.Equal(TicketGateOutcome.Unavailable, switchedOn.Outcome);
        Assert.True(untouched.Accepted);
    }

    [Fact]
    public async Task CorruptConfigUnavailable()
    {
        var validator = CreateValidator(CreateModuleConfig(corruptStore: true));

        var result = await validator.ValidateAsync(ModuleId, "INC0001");

        Assert.Equal(TicketGateOutcome.Unavailable, result.Outcome);
    }

    [Fact]
    public async Task UnparseableSwitchUnavailable()
    {
        // A mistyped switch must not silently mean Off (btv-1). Blank is the
        // one non-boolean that stays presence-only: unset is not a mistype.
        var mistyped = CreateValidator(ConfigWithSwitch("banana"));
        var refused = await mistyped.ValidateAsync(ModuleId, "INC0001");
        Assert.Equal(TicketGateOutcome.Unavailable, refused.Outcome);
        Assert.Contains("banana", refused.Message);

        var blank = CreateValidator(ConfigWithSwitch(""));
        Assert.True((await blank.ValidateAsync(ModuleId, "INC0001")).Accepted);
    }

    // --- fixtures ------------------------------------------------------------

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }

        public string NextBody { get; set; } = """{"result":[]}""";

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(NextBody),
            });
        }
    }

    private sealed class ThrowingConfigStore : IConfigStore
    {
        public long GetChangeToken() => throw new InvalidOperationException("store unreadable");

        public T Read<T>(Func<Microsoft.Data.Sqlite.SqliteConnection, T> read) =>
            throw new InvalidOperationException("store unreadable");

        public T Write<T>(Func<Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite.SqliteTransaction, T> write) =>
            throw new InvalidOperationException("store unreadable");

        public void Write(Action<Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite.SqliteTransaction> write) =>
            throw new InvalidOperationException("store unreadable");
    }
}
