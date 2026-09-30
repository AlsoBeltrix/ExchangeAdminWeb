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
param($LdapFilter, $ProbeTimeoutSeconds, $Throttle)

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
        # -LDAPFilter, NOT -Identity. -Identity resolves a distinguished name, a GUID, a SID or a
        # sAMAccountName and NOTHING ELSE - it does not accept a userPrincipalName, so every UPN
        # this module was handed failed on every DC with 'Cannot find an object with identity'.
        # The filter is built and escaped in C# (BuildIdentityFilter) so the escaping is testable
        # without a directory.
        $found = @(Get-ADUser -LDAPFilter $using:LdapFilter -Server $dc -Properties lastLogon -ErrorAction Stop)

        if ($found.Count -eq 0) {
            # Not an answer of 'never logged on'. This DC holds no such object, and a null date
            # here would let it vote for dormancy.
            $row.Error = 'No user matched this identity on this domain controller'
            return $row
        }
        if ($found.Count -gt 1) {
            $row.Error = 'Ambiguous: ' + $found.Count + ' users match this identity'
            return $row
        }

        $raw = $found[0].lastLogon
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
              .AddArgument(BuildIdentityFilter(samOrUpn))
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
    /// The LDAP filter that resolves what the operator typed, whether that is a
    /// userPrincipalName or a sAMAccountName.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This exists because <c>-Identity</c> cannot resolve a UPN.</b> It accepts a
    /// distinguished name, an objectGUID, an objectSid or a sAMAccountName, and nothing else, so
    /// every UPN the module was given failed on every domain controller with "Cannot find an
    /// object with identity". The parameter was named <c>SamOrUpn</c> throughout, so the UPN case
    /// was intended from the start and was never reachable.
    /// </para>
    /// <para>
    /// <b>Both attributes, not a branch on "@".</b> Splitting on the character would be a guess
    /// about what the operator typed; matching either attribute needs no guess. A sAMAccountName
    /// cannot contain "@" and a UPN must, so the two clauses cannot collide on one account.
    /// </para>
    /// <para>
    /// <b>The value is RFC 4515 escaped</b> via the same helper the group modules use. It comes
    /// straight from a text box, and an unescaped parenthesis or asterisk would change the
    /// filter's structure rather than fail - an asterisk alone would turn this into a wildcard
    /// sweep and report a stranger's logon under the typed name.
    /// </para>
    /// </remarks>
    internal static string BuildIdentityFilter(string samOrUpn)
    {
        var escaped = SelfServiceGroups.AdOwnershipFilter.EscapeLdapFilterValue(samOrUpn.Trim());

        return $"(&(objectCategory=person)(objectClass=user)"
             + $"(|(userPrincipalName={escaped})(sAMAccountName={escaped})))";
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
