using System.Collections.Concurrent;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Security.Cryptography.X509Certificates;

namespace ExchangeAdminWeb.Services;

public sealed class PooledRunspace
{
    public PowerShell PowerShell { get; }
    public Runspace Runspace { get; }
    public DateTime LastUsed { get; set; }
    public long ConfigGeneration { get; }

    public PooledRunspace(Runspace runspace, PowerShell ps, long configGeneration = 0)
    {
        Runspace = runspace;
        PowerShell = ps;
        LastUsed = DateTime.UtcNow;
        ConfigGeneration = configGeneration;
    }
}

public sealed class ExoConnectionPool : IDisposable
{
    public const string ConfigModuleKey = "ExchangeOnline";
    public const string ConfigAppIdKey = "AppId";
    public const string ConfigOrganizationKey = "Organization";
    public const string ConfigCertSubjectKey = "CertificateSubject";

    private readonly ConcurrentBag<PooledRunspace> _available = new();
    private readonly SemaphoreSlim _slots;
    private readonly ILogger<ExoConnectionPool> _logger;
    private readonly OperationTraceService _operationTrace;
    private readonly ModuleEnablementService _enablement;
    private readonly ModuleConfigService _moduleConfig;
    private readonly IConfiguration _config;
    private readonly TimeSpan _idleTimeout = TimeSpan.FromMinutes(20);
    private readonly Timer _cleanupTimer;
    private long _configGeneration;
    private bool _disposed;

    public ExoConnectionPool(IConfiguration config, ModuleConfigService moduleConfig, ModuleEnablementService enablement, ILogger<ExoConnectionPool> logger, OperationTraceService operationTrace)
    {
        _logger = logger;
        _operationTrace = operationTrace;
        _enablement = enablement;
        _moduleConfig = moduleConfig;
        _config = config;
        _slots = new SemaphoreSlim(5, 5);
        _cleanupTimer = new Timer(CleanupIdle, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    public bool IsConfigured
    {
        get
        {
            if (!_enablement.IsModuleEnabled("ExchangeOnline"))
                return false;
            var (appId, org, _) = GetExoConfig();
            return !string.IsNullOrWhiteSpace(appId) && !string.IsNullOrWhiteSpace(org);
        }
    }

    private (string appId, string organization, string certSubject) GetExoConfig()
    {
        var appId = _moduleConfig.GetValue(ConfigModuleKey, ConfigAppIdKey);
        var organization = _moduleConfig.GetValue(ConfigModuleKey, ConfigOrganizationKey);
        var certSubject = _moduleConfig.GetValue(ConfigModuleKey, ConfigCertSubjectKey);

        if (_moduleConfig.HasModuleConfigFile(ConfigModuleKey) && _moduleConfig.IsModuleCorrupt(ConfigModuleKey))
        {
            _logger.LogError("ExchangeOnline module config is corrupt - refusing to fall back to appsettings");
            return ("", "", "");
        }

        appId ??= _config["ExchangeOnline:AppId"] ?? "";
        organization ??= _config["ExchangeOnline:Organization"] ?? "";
        certSubject ??= _config["ExchangeOnline:CertificateSubject"]
            ?? "CN=EXO-Automation";
        return (appId, organization, certSubject);
    }

    public async Task<PooledRunspace> BorrowAsync(CancellationToken ct = default)
    {
        if (!_enablement.IsModuleEnabled("ExchangeOnline"))
            throw new InvalidOperationException("Exchange Online module is not enabled. Enable it in Admin Settings and configure the connection on the Exchange Online config page.");

        var (appId, org, _) = GetExoConfig();
        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(org))
            throw new InvalidOperationException("Exchange Online is not configured. Set AppId and Organization on the Exchange Online config page.");

        _operationTrace.Step("ExoPoolSlotRequested", backend: "ExchangeOnline", details: new Dictionary<string, object?> { ["organization"] = org });
        if (!await _slots.WaitAsync(TimeSpan.FromMinutes(2), ct))
        {
            _operationTrace.Step("ExoPoolSlotRequested", "Failed", backend: "ExchangeOnline", details: new Dictionary<string, object?> { ["reason"] = "Pool busy" });
            throw new InvalidOperationException("Exchange service is busy. Please try again shortly.");
        }

        _operationTrace.Step("ExoPoolSlotAcquired", backend: "ExchangeOnline");

        try
        {
            var currentGen = Interlocked.Read(ref _configGeneration);
            if (_available.TryTake(out var pooled))
            {
                if (pooled.ConfigGeneration == currentGen && DateTime.UtcNow - pooled.LastUsed < _idleTimeout)
                {
                    pooled.LastUsed = DateTime.UtcNow;
                    pooled.PowerShell.Commands.Clear();
                    pooled.PowerShell.Streams.ClearStreams();
                    _operationTrace.Step("ExoConnectionBorrowed", backend: "ExchangeOnline", details: new Dictionary<string, object?> { ["source"] = "Pool" });
                    return pooled;
                }

                _operationTrace.Step(pooled.ConfigGeneration != currentGen ? "ExoConnectionStaleConfig" : "ExoConnectionExpired", backend: "ExchangeOnline");
                DestroyRunspace(pooled);
            }

            return CreateConnected();
        }
        catch (Exception ex)
        {
            _operationTrace.Step("ExoConnectionBorrowed", "Failed", backend: "ExchangeOnline", exception: ex);
            _slots.Release();
            throw;
        }
    }

    public void Return(PooledRunspace runspace)
    {
        if (runspace.ConfigGeneration != Interlocked.Read(ref _configGeneration))
        {
            DestroyRunspace(runspace);
            _operationTrace.Step("ExoConnectionDiscardedStaleConfig", backend: "ExchangeOnline");
            _slots.Release();
            return;
        }

        runspace.LastUsed = DateTime.UtcNow;
        runspace.PowerShell.Commands.Clear();
        runspace.PowerShell.Streams.ClearStreams();
        _available.Add(runspace);
        _operationTrace.Step("ExoConnectionReturned", backend: "ExchangeOnline");
        _slots.Release();
    }

    public void Discard(PooledRunspace runspace)
    {
        DestroyRunspace(runspace);
        _operationTrace.Step("ExoConnectionDiscarded", backend: "ExchangeOnline");
        _slots.Release();
    }

    /// <summary>
    /// Destroys all pooled connections so that subsequent borrows create new
    /// connections using the current config. Call after EXO config changes.
    /// </summary>
    public void DrainPool()
    {
        Interlocked.Increment(ref _configGeneration);
        var drained = 0;
        while (_available.TryTake(out var item))
        {
            DestroyRunspace(item);
            drained++;
        }

        if (drained > 0)
            _logger.LogInformation("EXO pool: drained {Count} pooled connection(s) after config change", drained);

        _operationTrace.Step("ExoPoolDrained", backend: "ExchangeOnline",
            details: new Dictionary<string, object?> { ["connectionsDestroyed"] = drained });
    }

    private PooledRunspace CreateConnected()
    {
        var (appId, organization, certSubject) = GetExoConfig();

        var iss = InitialSessionState.CreateDefault();
        iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
        var runspace = RunspaceFactory.CreateRunspace(iss);
        runspace.Open();
        var ps = PowerShell.Create();
        ps.Runspace = runspace;

        try
        {
            _operationTrace.Step("ExoConnectionCreating", backend: "ExchangeOnline", command: "Connect-ExchangeOnline", details: new Dictionary<string, object?> { ["organization"] = organization });

            ps.AddCommand("Import-Module")
              .AddParameter("Name", "ExchangeOnlineManagement")
              .AddParameter("ErrorAction", "Stop");
            ps.Invoke();
            ps.Commands.Clear();

            var cert = FindCertificate(certSubject);
            ps.AddCommand("Connect-ExchangeOnline")
              .AddParameter("AppId", appId)
              .AddParameter("CertificateThumbprint", cert.Thumbprint)
              .AddParameter("Organization", organization)
              .AddParameter("ShowBanner", false)
              .AddParameter("ErrorAction", "Stop");
            ps.Invoke();

            if (ps.Streams.Error.Count > 0)
            {
                var msg = ps.Streams.Error.First().Exception?.Message ?? ps.Streams.Error.First().ToString();
                throw new InvalidOperationException($"EXO Connect failed: {msg}");
            }

            ps.Commands.Clear();
            ps.Streams.ClearStreams();

            _logger.LogInformation("EXO pool: created new connection (Org={Org})", organization);
            _operationTrace.Step("ExoConnectionCreated", backend: "ExchangeOnline", command: "Connect-ExchangeOnline", details: new Dictionary<string, object?> { ["organization"] = organization });
            return new PooledRunspace(runspace, ps, Interlocked.Read(ref _configGeneration));
        }
        catch (Exception ex)
        {
            _operationTrace.Step("ExoConnectionCreated", "Failed", backend: "ExchangeOnline", command: "Connect-ExchangeOnline", exception: ex);
            ps.Dispose();
            runspace.Dispose();
            throw;
        }
    }

    private void DestroyRunspace(PooledRunspace pooled)
    {
        try
        {
            pooled.PowerShell.Commands.Clear();
            pooled.PowerShell.AddCommand("Disconnect-ExchangeOnline")
                  .AddParameter("Confirm", false);
            pooled.PowerShell.Invoke();
        }
        catch { }
        finally
        {
            try { pooled.PowerShell.Dispose(); } catch { }
            try { pooled.Runspace.Dispose(); } catch { }
        }
    }

    private void CleanupIdle(object? state)
    {
        var snapshot = new List<PooledRunspace>();
        while (_available.TryTake(out var item))
            snapshot.Add(item);

        foreach (var item in snapshot)
        {
            if (DateTime.UtcNow - item.LastUsed > _idleTimeout)
            {
                DestroyRunspace(item);
                _logger.LogInformation("EXO pool: disposed idle connection");
            }
            else
            {
                _available.Add(item);
            }
        }
    }

    private static X509Certificate2 FindCertificate(string certSubject)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        var cert = store.Certificates
            .Find(X509FindType.FindBySubjectDistinguishedName, certSubject, false)
            .OfType<X509Certificate2>()
            .Where(c => c.HasPrivateKey)
            .OrderByDescending(c => c.NotBefore)
            .FirstOrDefault();

        if (cert != null) return cert;

        using var userStore = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        userStore.Open(OpenFlags.ReadOnly);
        cert = userStore.Certificates
            .Find(X509FindType.FindBySubjectDistinguishedName, certSubject, false)
            .OfType<X509Certificate2>()
            .Where(c => c.HasPrivateKey)
            .OrderByDescending(c => c.NotBefore)
            .FirstOrDefault();

        return cert ?? throw new InvalidOperationException($"Certificate '{certSubject}' not found in LocalMachine or CurrentUser certificate stores.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cleanupTimer.Dispose();

        while (_available.TryTake(out var item))
            DestroyRunspace(item);
    }
}
