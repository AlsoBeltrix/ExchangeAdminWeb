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

    public PooledRunspace(Runspace runspace, PowerShell ps)
    {
        Runspace = runspace;
        PowerShell = ps;
        LastUsed = DateTime.UtcNow;
    }
}

public sealed class ExoConnectionPool : IDisposable
{
    private readonly ConcurrentBag<PooledRunspace> _available = new();
    private readonly SemaphoreSlim _slots;
    private readonly ILogger<ExoConnectionPool> _logger;
    private readonly OperationTraceService _operationTrace;
    private readonly string _appId;
    private readonly string _organization;
    private readonly string _certSubject;
    private readonly TimeSpan _idleTimeout = TimeSpan.FromMinutes(20);
    private readonly Timer _cleanupTimer;
    private bool _disposed;

    public ExoConnectionPool(IConfiguration config, ILogger<ExoConnectionPool> logger, OperationTraceService operationTrace)
    {
        _logger = logger;
        _operationTrace = operationTrace;
        _appId = config["ExchangeOnline:AppId"] ?? "";
        _organization = config["ExchangeOnline:Organization"] ?? "";
        _certSubject = config["ExchangeOnline:CertificateSubject"] ?? "CN=EXO-Automation";
        _slots = new SemaphoreSlim(5, 5);
        _cleanupTimer = new Timer(CleanupIdle, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    public async Task<PooledRunspace> BorrowAsync(CancellationToken ct = default)
    {
        _operationTrace.Step("ExoPoolSlotRequested", backend: "ExchangeOnline", details: new Dictionary<string, object?> { ["organization"] = _organization });
        if (!await _slots.WaitAsync(TimeSpan.FromMinutes(2), ct))
        {
            _operationTrace.Step("ExoPoolSlotRequested", "Failed", backend: "ExchangeOnline", details: new Dictionary<string, object?> { ["reason"] = "Pool busy" });
            throw new InvalidOperationException("Exchange service is busy. Please try again shortly.");
        }

        _operationTrace.Step("ExoPoolSlotAcquired", backend: "ExchangeOnline");

        try
        {
            if (_available.TryTake(out var pooled))
            {
                if (DateTime.UtcNow - pooled.LastUsed < _idleTimeout)
                {
                    pooled.LastUsed = DateTime.UtcNow;
                    pooled.PowerShell.Commands.Clear();
                    pooled.PowerShell.Streams.ClearStreams();
                    _operationTrace.Step("ExoConnectionBorrowed", backend: "ExchangeOnline", details: new Dictionary<string, object?> { ["source"] = "Pool" });
                    return pooled;
                }

                _operationTrace.Step("ExoConnectionExpired", backend: "ExchangeOnline");
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

    private PooledRunspace CreateConnected()
    {
        var iss = InitialSessionState.CreateDefault();
        iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
        var runspace = RunspaceFactory.CreateRunspace(iss);
        runspace.Open();
        var ps = PowerShell.Create();
        ps.Runspace = runspace;

        try
        {
            _operationTrace.Step("ExoConnectionCreating", backend: "ExchangeOnline", command: "Connect-ExchangeOnline", details: new Dictionary<string, object?> { ["organization"] = _organization });

            ps.AddCommand("Import-Module")
              .AddParameter("Name", "ExchangeOnlineManagement")
              .AddParameter("ErrorAction", "Stop");
            ps.Invoke();
            ps.Commands.Clear();

            var cert = FindCertificate();
            ps.AddCommand("Connect-ExchangeOnline")
              .AddParameter("AppId", _appId)
              .AddParameter("CertificateThumbprint", cert.Thumbprint)
              .AddParameter("Organization", _organization)
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

            _logger.LogInformation("EXO pool: created new connection (Org={Org})", _organization);
            _operationTrace.Step("ExoConnectionCreated", backend: "ExchangeOnline", command: "Connect-ExchangeOnline", details: new Dictionary<string, object?> { ["organization"] = _organization });
            return new PooledRunspace(runspace, ps);
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

    private X509Certificate2 FindCertificate()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        var cert = store.Certificates
            .Find(X509FindType.FindBySubjectDistinguishedName, _certSubject, false)
            .OfType<X509Certificate2>()
            .Where(c => c.HasPrivateKey)
            .OrderByDescending(c => c.NotBefore)
            .FirstOrDefault();

        if (cert != null) return cert;

        using var userStore = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        userStore.Open(OpenFlags.ReadOnly);
        cert = userStore.Certificates
            .Find(X509FindType.FindBySubjectDistinguishedName, _certSubject, false)
            .OfType<X509Certificate2>()
            .Where(c => c.HasPrivateKey)
            .OrderByDescending(c => c.NotBefore)
            .FirstOrDefault();

        return cert ?? throw new InvalidOperationException($"Certificate '{_certSubject}' not found");
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
