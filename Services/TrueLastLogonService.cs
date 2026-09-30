using System.Management.Automation;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// The on-prem half of True Last Logon (docs/TrueLastLogon-Plan.md S1): sweeps every domain
/// controller for one user's <c>lastLogon</c>.
/// </summary>
/// <remarks>
/// <para>
/// Runs under the app pool's ambient identity, no Delinea credential, read-only - the same
/// posture as <see cref="ADDirectorySearchService"/>.
/// </para>
/// <para>
/// <b>Why the sweep exists.</b> <c>lastLogon</c> does not replicate. Each DC records only the
/// logons it personally handled, so one DC's answer can be months stale. The decision of what
/// to do with the per-DC answers lives in <see cref="OnPremLogonAggregator"/>, which is pure
/// and unit-tested; this class is the I/O that feeds it.
/// </para>
/// <para>
/// <b>No domain is named here.</b> <c>Get-ADDomainController -Filter *</c> resolves against the
/// host's own domain membership, which is architectural invariant 7 - directory scope is
/// discovered at runtime, never named, defaulted or configured. The source script's
/// <c>-Domain ad.analog.com</c> default deliberately does not port.
/// </para>
/// <para>
/// <b>Why the work happens inside one PowerShell pipeline.</b> A serial sweep of ~37 DCs, each
/// waiting out its own LDAP timeout, is minutes of dead time for a page that must answer in
/// seconds. PowerShell 7's <c>ForEach-Object -Parallel</c> does the fan-out where it is
/// natural, so this stays one invocation rather than dozens of runspaces managed from C#.
/// </para>
/// </remarks>
public sealed class TrueLastLogonService
{
    private readonly ILogger<TrueLastLogonService> _logger;
    private readonly SemaphoreSlim _runspaceLock = new(1, 1);

    public TrueLastLogonService(ILogger<TrueLastLogonService> logger) => _logger = logger;

    /// <summary>Seconds to wait for a DC to accept a TCP connection on 389 before skipping it.</summary>
    private const int ProbeTimeoutSeconds = 2;

    /// <summary>How many DCs are queried at once.</summary>
    private const int Throttle = 12;

    /// <summary>
    /// The sweep, as one script. Emits one row per DC - never fewer - so a DC that failed is
    /// carried out with its reason rather than vanishing from the result.
    /// </summary>
    /// <remarks>
    /// The preflight is the reason this is fast. Probing TCP:389 with a short timeout costs two
    /// seconds for a dead DC; letting the LDAP call discover the same thing costs its full
    /// timeout, and one unreachable site would dominate the whole sweep.
    ///
    /// <c>lastLogon</c> comes back as a FILETIME integer, and 0 means "this DC has never seen
    /// them" - a real answer, not a missing one, which is why it maps to a null date with no
    /// error rather than to an error.
    /// </remarks>
    private const string SweepScript = @"
param($SamOrUpn, $ProbeTimeoutSeconds, $Throttle)

$dcs = @(Get-ADDomainController -Filter * -ErrorAction Stop |
         Select-Object -ExpandProperty HostName)

$dcs | ForEach-Object -ThrottleLimit $Throttle -Parallel {
    $dc = $_
    $row = [pscustomobject]@{ DomainController = $dc; LastLogon = $null; Error = $null }

    try {
        $client = [System.Net.Sockets.TcpClient]::new()
        $ok = $client.ConnectAsync($dc, 389).Wait([TimeSpan]::FromSeconds($using:ProbeTimeoutSeconds))
        $client.Dispose()
        if (-not $ok) {
            $row.Error = 'No answer on TCP 389 within ' + $using:ProbeTimeoutSeconds + 's'
            return $row
        }
    }
    catch {
        $row.Error = 'TCP 389 probe failed: ' + $_.Exception.Message
        return $row
    }

    try {
        $u = Get-ADUser -Identity $using:SamOrUpn -Server $dc -Properties lastLogon -ErrorAction Stop
        $raw = $u.lastLogon
        if ($raw -and $raw -gt 0) {
            $row.LastLogon = [DateTime]::FromFileTimeUtc([Int64]$raw).ToString('o')
        }
    }
    catch {
        $row.Error = $_.Exception.Message
    }

    $row
}
";

    /// <summary>
    /// Asks every domain controller when <paramref name="samOrUpn"/> last logged on.
    /// </summary>
    /// <remarks>
    /// A thrown failure here means the sweep could not START - typically the ActiveDirectory
    /// module is missing or no DC could be enumerated. That is reported as an error, NEVER as an
    /// empty result: an empty result is indistinguishable from "this user has never logged on",
    /// and that is the reading that gets a live account disabled.
    /// </remarks>
    public async Task<OnPremLogonResult> GetOnPremLastLogonAsync(string samOrUpn)
    {
        if (string.IsNullOrWhiteSpace(samOrUpn))
            throw new ArgumentException("A user identity is required.", nameof(samOrUpn));

        await _runspaceLock.WaitAsync();
        try
        {
            using var ps = PowerShell.Create();
            ps.AddScript(SweepScript)
              .AddArgument(samOrUpn.Trim())
              .AddArgument(ProbeTimeoutSeconds)
              .AddArgument(Throttle);

            var results = await ps.InvokeAsync();

            if (ps.HadErrors && results.Count == 0)
            {
                var first = ps.Streams.Error.FirstOrDefault()?.ToString() ?? "unknown error";
                throw new InvalidOperationException(
                    $"The domain controller sweep could not run: {first}");
            }

            var rows = results.Select(MapRow).Where(r => r != null).Select(r => r!).ToList();

            foreach (var skipped in rows.Where(r => r.Error != null))
                _logger.LogDebug("True last logon: {DC} did not answer: {Error}", skipped.DomainController, skipped.Error);

            if (rows.Count == 0)
            {
                throw new InvalidOperationException(
                    "No domain controller was enumerated, so no on-prem answer exists. This is "
                    + "not evidence that the account has never logged on.");
            }

            return OnPremLogonAggregator.Aggregate(rows);
        }
        finally
        {
            _runspaceLock.Release();
        }
    }

    /// <summary>
    /// Maps one PowerShell row. A row whose date will not parse becomes a SKIP with the reason,
    /// never a silent null - an unparseable date is a DC that did not answer usefully, and
    /// treating it as "no logon recorded" would let it vote for dormancy.
    /// </summary>
    internal static DomainControllerLogon? MapRow(PSObject row)
    {
        var dc = row.Properties["DomainController"]?.Value?.ToString();
        if (string.IsNullOrWhiteSpace(dc))
            return null;

        var error = row.Properties["Error"]?.Value?.ToString();
        if (!string.IsNullOrWhiteSpace(error))
            return new DomainControllerLogon(dc, null, error);

        var raw = row.Properties["LastLogon"]?.Value?.ToString();
        if (string.IsNullOrWhiteSpace(raw))
            return new DomainControllerLogon(dc, null, null);

        if (!DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var when))
        {
            return new DomainControllerLogon(dc, null, $"Unreadable lastLogon value '{raw}'");
        }

        return new DomainControllerLogon(dc, when.ToUniversalTime(), null);
    }
}
