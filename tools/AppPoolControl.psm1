<#
.SYNOPSIS
    Stops an IIS application pool and PROVES it stopped before the caller mirrors over a
    live application.

.DESCRIPTION
    Audit finding A35. Both deployment paths stopped the pool like this:

        try { Stop-WebAppPool -Name $AppPoolName -ErrorAction Stop } catch {}
        Start-Sleep -Seconds 3

    and then ran robocopy /MIR into the publish folder. The empty catch is the defect. It
    was put there for a good reason - Stop-WebAppPool throws when the pool is ALREADY
    stopped, which is a normal and harmless state - but it swallows every other failure
    with it: the pool does not exist under the name given, the caller is not elevated, IIS
    refuses, or the worker process will not exit. In each of those the script carried on
    and replaced the binaries of a RUNNING application, file by file, while it served
    requests.

    A fixed sleep cannot stand in for the check either. Three seconds is a guess about how
    long a worker process takes to exit; a pool draining a long request takes longer, and
    the deploy would proceed anyway.

    So: swallow the stop exception as before, then ASK IIS what state the pool is in, and
    keep asking until it says Stopped or the timeout expires. A pool that never stops
    throws, and the caller aborts before mirroring anything.

    A pool that does not exist is NOT an error. There is nothing running, so there is
    nothing to corrupt - the installer's first run is exactly this case. It is reported as
    'Absent' so the caller can log it rather than infer it.

.NOTES
    Pure ASCII, deliberately: deploy.ps1 is read by Windows PowerShell 5.1, which reads a
    BOM-less file as ANSI and turns non-ASCII characters into parse errors
    (.agents/repo-guidance.md, Architectural Invariant 6).

    The IIS calls are injectable script blocks so the behaviour can be tested without IIS.
    The defaults are the real cmdlets; no caller passes the others except the tests.
#>

Set-StrictMode -Version Latest

function Stop-AppPoolVerified {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        # Generous rather than tight: the cost of waiting is seconds, the cost of giving up
        # too early is a half-replaced live application.
        [int]$TimeoutSeconds = 60,

        [int]$PollMilliseconds = 500,

        [scriptblock]$ExistsProbe = { param($PoolName) Test-Path "IIS:\AppPools\$PoolName" },

        [scriptblock]$StopAction = { param($PoolName) Stop-WebAppPool -Name $PoolName -ErrorAction Stop },

        [scriptblock]$StateProbe = { param($PoolName) (Get-WebAppPoolState -Name $PoolName).Value },

        [scriptblock]$Sleep = { param($Milliseconds) Start-Sleep -Milliseconds $Milliseconds }
    )

    if (-not (& $ExistsProbe $Name)) {
        # Nothing is serving from this pool, so nothing can be corrupted by replacing files.
        return 'Absent'
    }

    # Already-stopped throws here, and that is fine - the state check below is what decides.
    # Any other failure is also tolerated here and also caught by the state check, which is
    # the point: we stop guessing from the exception and ask IIS instead.
    #
    # The reason is KEPT rather than discarded, though. If the pool then fails to stop, the
    # operator gets told what the stop attempt actually said - "Access denied" is a different
    # problem from a worker that will not exit, and the old empty catch threw that away.
    $stopError = $null
    try { & $StopAction $Name } catch { $stopError = $_.Exception.Message }

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $state = $null

    while ($true) {
        try {
            $state = & $StateProbe $Name
        } catch {
            # An unreadable state is not a stopped pool. Fail closed.
            throw "Cannot read the state of app pool '$Name', so it cannot be confirmed stopped before deploying over it: $($_.Exception.Message)"
        }

        if ($state -eq 'Stopped') { return 'Stopped' }

        if ((Get-Date) -ge $deadline) {
            $because = if ($stopError) { " The stop attempt reported: $stopError." } else { '' }
            throw "App pool '$Name' is still '$state' after $TimeoutSeconds seconds.$because Refusing to deploy over a running application - stop it by hand and retry."
        }

        & $Sleep $PollMilliseconds
    }
}

Export-ModuleMember -Function Stop-AppPoolVerified
