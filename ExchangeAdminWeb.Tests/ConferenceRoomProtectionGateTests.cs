using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Guards ConferenceRoomProtectionGate — the single protected-principal enforcement point for every
/// ConferenceRooms room-mutating write. These drive the PRODUCTION GuardThenRunAsync executor with a
/// spy write delegate: the write must be reachable only on the allow path, and never on protected or
/// any fail-closed outcome. This is the non-vacuity home for the single-room Finder gap fix — with
/// the denial branch present, a protected target leaves the write delegate uncalled; removing that
/// branch makes the "protected => write not invoked" assertions fail.
/// </summary>
public class ConferenceRoomProtectionGateTests : IDisposable
{
    private readonly string _tempDir;

    public ConferenceRoomProtectionGateTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"crpg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_tempDir, "config"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    // Fake PP service overriding the two virtual seam methods to a scripted verdict.
    private sealed class FakePpService : ProtectedPrincipalService
    {
        public ResolutionStatus Status = ResolutionStatus.NotFound;
        public ProtectedPrincipalResult Verdict = ProtectedPrincipalResult.NotProtected();
        public bool Throw;

        public FakePpService(IWebHostEnvironment env, IConfiguration config, ModuleConfigService moduleConfig,
            ProtectedPrincipalRepository repo, DelineaService delinea)
            : base(env, config, moduleConfig, repo, delinea, NullLogger<ProtectedPrincipalService>.Instance)
        { }

        public override Task<(ResolvedDirectoryPrincipal? principal, ResolutionStatus status)> ResolveWithStatusAsync(string identity)
        {
            if (Throw) throw new InvalidOperationException("boom");
            ResolvedDirectoryPrincipal? p = Status == ResolutionStatus.Resolved
                ? new ResolvedDirectoryPrincipal("AD", identity, identity, identity, identity, null, "guid", null)
                : null;
            return Task.FromResult((p, Status));
        }

        public override Task<ProtectedPrincipalResult> CheckAsync(ResolvedDirectoryPrincipal target)
            => Task.FromResult(Verdict);
    }

    private (ConferenceRoomProtectionGate gate, FakePpService pp) CreateGate()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Delinea:SecretServerUrl"] = "https://fake.local",
        }).Build();
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_tempDir);
        var catalog = new Modules.ModuleCatalog();
        var moduleConfig = new ModuleConfigService(catalog, env, TestConfigStore.CreateModuleConfig(_tempDir), NullLogger<ModuleConfigService>.Instance);
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());
        var extLog = new ExtendedLogService(config, env, TestConfigStore.CreateAppSettings(_tempDir), NullLogger<ExtendedLogService>.Instance);
        var jsonlLog = new JsonlLogService(config, NullLogger<JsonlLogService>.Instance);
        var trace = new OperationTraceService(config, jsonlLog);
        var delinea = new DelineaService(httpClientFactory, config, NullLogger<DelineaService>.Instance, extLog, trace);
        var pp = new FakePpService(env, config, moduleConfig, TestConfigStore.CreateProtectedPrincipal(_tempDir), delinea);
        var gate = new ConferenceRoomProtectionGate(pp, NullLogger<ConferenceRoomProtectionGate>.Instance);
        return (gate, pp);
    }

    // Runs the gate with a spy write delegate. Returns (writeInvoked, result-marker).
    private async Task<(bool writeInvoked, string marker)> RunAsync(ConferenceRoomProtectionGate gate)
    {
        var writeInvoked = false;
        var marker = await gate.GuardThenRunAsync("room@x",
            onDenied: d => $"DENIED:{d.Message}",
            onAllowed: () =>
            {
                writeInvoked = true;
                return Task.FromResult("WROTE");
            });
        return (writeInvoked, marker);
    }

    [Fact]
    public async Task Allowed_NonProtected_InvokesWriteOnce()
    {
        var (gate, pp) = CreateGate();
        pp.Status = ProtectedPrincipalService.ResolutionStatus.Resolved;
        pp.Verdict = ProtectedPrincipalResult.NotProtected();

        var (writeInvoked, marker) = await RunAsync(gate);

        Assert.True(writeInvoked);
        Assert.Equal("WROTE", marker);
    }

    [Fact]
    public async Task NotFound_TreatedAsAllow_InvokesWrite()
    {
        var (gate, pp) = CreateGate();
        pp.Status = ProtectedPrincipalService.ResolutionStatus.NotFound; // cloud-only: AD cannot resolve

        var (writeInvoked, marker) = await RunAsync(gate);

        Assert.True(writeInvoked);
        Assert.Equal("WROTE", marker);
    }

    [Fact]
    public async Task Protected_Denied_WriteNotInvoked()
    {
        // The core single-room Finder gap assertion (and Type + bulk share this gate):
        // a protected target must never reach the write delegate.
        var (gate, pp) = CreateGate();
        pp.Status = ProtectedPrincipalService.ResolutionStatus.Resolved;
        pp.Verdict = ProtectedPrincipalResult.Protected("matched", "Users");

        var (writeInvoked, marker) = await RunAsync(gate);

        Assert.False(writeInvoked);
        Assert.StartsWith("DENIED:", marker);
        Assert.Contains("protected principal", marker, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(ProtectedPrincipalService.ResolutionStatus.Unavailable)]
    [InlineData(ProtectedPrincipalService.ResolutionStatus.Ambiguous)]
    public async Task ResolutionFailClosed_Denied_WriteNotInvoked(ProtectedPrincipalService.ResolutionStatus status)
    {
        var (gate, pp) = CreateGate();
        pp.Status = status;

        var (writeInvoked, marker) = await RunAsync(gate);

        Assert.False(writeInvoked);
        Assert.StartsWith("DENIED:", marker);
    }

    [Fact]
    public async Task CheckFailed_Denied_WriteNotInvoked()
    {
        var (gate, pp) = CreateGate();
        pp.Status = ProtectedPrincipalService.ResolutionStatus.Resolved;
        pp.Verdict = ProtectedPrincipalResult.Failed("directory error");

        var (writeInvoked, marker) = await RunAsync(gate);

        Assert.False(writeInvoked);
        Assert.StartsWith("DENIED:", marker);
    }

    [Fact]
    public async Task Exception_FailsClosed_WriteNotInvoked()
    {
        var (gate, pp) = CreateGate();
        pp.Throw = true;

        var (writeInvoked, marker) = await RunAsync(gate);

        Assert.False(writeInvoked);
        Assert.StartsWith("DENIED:", marker);
    }
}
