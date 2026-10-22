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

function Resolve-AppPoolIdentityPlan {
    <#
    .SYNOPSIS
        Decides what an installer run should do to an app pool's identity, and which
        principal the folder ACLs belong to.

    .DESCRIPTION
        Audit finding A34. The installer set the identity like this:

            if ($ServiceAccount) { ... identityType 3, userName, password ... }
            else                 { ... identityType 4 ... }

        Type 4 is ApplicationPoolIdentity. So re-running the installer to UPDATE an
        instance, without repeating -ServiceAccount, silently moved a pool that was running
        as a domain service account back to a local virtual account - and the ACL calls
        below it then granted Modify to "IIS AppPool\<name>" instead of to the account the
        app actually runs as.

        That single flag is what the app's access to the credential store, the certificate
        private key, Active Directory and the shared config database all hang off. Nothing
        failed at install time; the app simply could not do its job afterwards, for reasons
        several layers away from the command that caused it.

        An omitted parameter is not an instruction to change anything. So an existing custom
        identity is PRESERVED unless -ServiceAccount names a new one or the operator asks
        for the reset explicitly - the same shape as the installer's existing
        -RegenerateAppSettings, which does not rewrite appsettings.json unless told to.

    .OUTPUTS
        A PSCustomObject with:
          Action        - 'SetCustom', 'PreserveExisting' or 'SetApplicationPoolIdentity'
          IdentityForAcl - the principal the folder ACLs must be granted to
          Reason        - one line for the operator, or $null when nothing notable happened
    #>
    [CmdletBinding()]
    param(
        # The -ServiceAccount the run was given, if any.
        [string]$ServiceAccount,

        [string]$AppPoolName,

        [bool]$PoolExists,

        # processModel.identityType as IIS reports it: 3 is SpecificUser, 4 is
        # ApplicationPoolIdentity. Ignored when the pool does not exist.
        [Nullable[int]]$ExistingIdentityType,

        # processModel.userName as IIS reports it.
        [string]$ExistingUserName,

        # Explicit opt-in to move a pool back to ApplicationPoolIdentity.
        [bool]$ResetRequested
    )

    if ($ServiceAccount) {
        return [pscustomobject]@{
            Action         = 'SetCustom'
            IdentityForAcl = $ServiceAccount
            Reason         = $null
        }
    }

    $hasCustomIdentity = $PoolExists -and ($ExistingIdentityType -eq 3) -and $ExistingUserName

    if ($hasCustomIdentity -and -not $ResetRequested) {
        return [pscustomobject]@{
            Action         = 'PreserveExisting'
            IdentityForAcl = $ExistingUserName
            Reason         = "App pool $AppPoolName already runs as $ExistingUserName. Keeping it - pass -ServiceAccount to change it, or -ResetAppPoolIdentity to move it to ApplicationPoolIdentity."
        }
    }

    $reason = $null
    if ($hasCustomIdentity -and $ResetRequested) {
        $reason = "App pool $AppPoolName is being moved from $ExistingUserName to ApplicationPoolIdentity because -ResetAppPoolIdentity was passed. Access that depended on $ExistingUserName - credential store, certificate private key, directory, shared config database - will stop working until it is re-granted."
    }

    return [pscustomobject]@{
        Action         = 'SetApplicationPoolIdentity'
        IdentityForAcl = "IIS AppPool\$AppPoolName"
        Reason         = $reason
    }
}

Export-ModuleMember -Function Resolve-AppPoolIdentityPlan

function Start-AppPoolVerified {
    <#
    .SYNOPSIS
        Starts an IIS application pool and PROVES it started.

    .DESCRIPTION
        Audit finding A38, and the symmetric twin of Stop-AppPoolVerified. The promotion
        script called Start-WebAppPool and reported "Prod app pool started" on the next
        line, having checked nothing. IIS accepts the start request and can still leave the
        pool stopped - a bad identity password, a missing runtime, a rapid-fail-protection
        trip - and the operator was told it was running.

        Combined with the restart living in a finally block, that is how a promotion ended
        with new binaries in place, the pool down, and a success message.

    .OUTPUTS
        'Started' when IIS confirms it. Throws otherwise - including when the pool does not
        exist, which here IS an error: something was supposed to be running.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [int]$TimeoutSeconds = 60,

        [int]$PollMilliseconds = 500,

        [scriptblock]$ExistsProbe = { param($PoolName) Test-Path "IIS:\AppPools\$PoolName" },

        [scriptblock]$StartAction = { param($PoolName) Start-WebAppPool -Name $PoolName -ErrorAction Stop },

        [scriptblock]$StateProbe = { param($PoolName) (Get-WebAppPoolState -Name $PoolName).Value },

        [scriptblock]$Sleep = { param($Milliseconds) Start-Sleep -Milliseconds $Milliseconds }
    )

    if (-not (& $ExistsProbe $Name)) {
        throw "App pool '$Name' does not exist, so it cannot be started. The application is down."
    }

    # An already-started pool throws here, exactly as the stop side does with an
    # already-stopped one. The state check decides.
    $startError = $null
    try { & $StartAction $Name } catch { $startError = $_.Exception.Message }

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $state = $null

    while ($true) {
        try {
            $state = & $StateProbe $Name
        } catch {
            throw "Cannot read the state of app pool '$Name', so it cannot be confirmed running: $($_.Exception.Message)"
        }

        if ($state -eq 'Started') { return 'Started' }

        if ((Get-Date) -ge $deadline) {
            $because = if ($startError) { " The start attempt reported: $startError." } else { '' }
            throw "App pool '$Name' is '$state' after $TimeoutSeconds seconds, not Started.$because The application is DOWN."
        }

        & $Sleep $PollMilliseconds
    }
}

Export-ModuleMember -Function Start-AppPoolVerified

function ConvertTo-ComparablePhysicalPath {
    # One spelling per folder: environment variables expanded, made absolute and normalised
    # (slashes, dot segments), trailing separators dropped. The callers compare the result
    # ordinal and case-insensitive, by equality or at a directory boundary
    # (Test-PhysicalPathInside) - never by bare prefix. Not exported.
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) { return '' }
    $expanded = [Environment]::ExpandEnvironmentVariables($Path.Trim())
    try {
        $full = [System.IO.Path]::GetFullPath($expanded)
    } catch {
        $full = $expanded
    }
    return $full.TrimEnd('\', '/')
}

function Test-PhysicalPathInside {
    # aud-a38f. True when Path is Root or lies inside it: equal, or Root followed by a directory
    # separator. Both are comparable forms (ConvertTo-ComparablePhysicalPath). A bare prefix test
    # would put ...\AppDev inside ...\App. Not exported.
    param([string]$Path, [string]$Root)

    if ([string]::Equals($Path, $Root, [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
    foreach ($separator in '\', '/') {
        if ($Path.StartsWith($Root + $separator, [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}

function Assert-AppPoolServesPath {
    <#
    .SYNOPSIS
        Refuses unless the app pool about to be stopped is the pool IIS serves a folder from.

    .DESCRIPTION
        aud-a38c, after the independent review of A35. Stop-AppPoolVerified proves that the
        pool it was NAMED has stopped. That proves nothing about a folder served by a
        different pool: a wrong but existing pool name stops that pool, the stop is truthfully
        confirmed, and robocopy /MIR then replaces the files of an application still running
        under the real one.

        So, before the stop, ask IIS for every site root, application and virtual directory,
        keep those whose files are at the folder or inside it, and refuse unless at least one is
        AT the folder and every one of them runs under the named pool. aud-a38f: robocopy /MIR
        replaces the whole tree, so an application or virtual directory rooted inside the folder
        and served by another pool would keep running while its files are replaced; and a
        nested match alone is not "what serves the folder" - a parent folder named by mistake
        would have its other children replaced too. An answer that cannot be read, or that has
        nothing at the folder, is a refusal as well: if nothing can be shown to serve the
        folder, stopping the named pool cannot be shown to stop what runs from it. The refusal
        lists what IIS reported, so a folder that IIS spells differently is visible in the
        message itself.

        Folders compare after normalisation, by equality or at a directory boundary, never by
        bare prefix - one instance's folder is easily a prefix of a sibling's.

    .OUTPUTS
        Everything IIS serves at or inside the folder (Location, ApplicationPool,
        PhysicalPath) when all of it runs under the named pool. Throws otherwise.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [string]$PhysicalPath,

        # Every site root, application and virtual directory IIS knows. Get-WebApplication's
        # PhysicalPath is the script property WebAdministration's own type data defines (the root
        # virtual directory's physicalPath); Get-Website exposes the root's physicalPath and
        # applicationPool. aud-a38f: every other virtual directory is read with
        # Get-WebConfiguration under its application's ItemXPath plus /virtualDirectory - the
        # form that same type data uses - and is served by the pool of the application that owns
        # it. Every application has a root virtual directory, so a listing without one is a read
        # that missed and is thrown, not taken for "nothing else". Under this module's strict
        # mode a missing property throws too, and either throw is a refusal below, not a pass.
        [scriptblock]$ApplicationsProbe = {
            foreach ($site in @(Get-Website)) {
                [pscustomobject]@{ Location = "$($site.name)/"; ApplicationPool = $site.applicationPool; PhysicalPath = $site.physicalPath }
                $owners = @([pscustomobject]@{ Path = '/'; ApplicationPool = $site.applicationPool; ItemXPath = "$($site.ItemXPath)/application[@path='/']" })
                foreach ($app in @(Get-WebApplication -Site $site.name)) {
                    [pscustomobject]@{ Location = "$($site.name)$($app.path)"; ApplicationPool = $app.applicationPool; PhysicalPath = $app.PhysicalPath }
                    $owners += [pscustomobject]@{ Path = $app.path; ApplicationPool = $app.applicationPool; ItemXPath = $app.ItemXPath }
                }
                foreach ($owner in $owners) {
                    $directories = @(Get-WebConfiguration -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter "$($owner.ItemXPath)/virtualDirectory")
                    if (@($directories | Where-Object { $_.path -eq '/' }).Count -eq 0) {
                        throw "IIS listed no root virtual directory for $($site.name)$($owner.Path), so its other virtual directories cannot be shown to have been read."
                    }
                    foreach ($directory in $directories) {
                        if ($directory.path -eq '/') { continue }
                        [pscustomobject]@{ Location = "$($site.name)$($owner.Path.TrimEnd('/'))$($directory.path)"; ApplicationPool = $owner.ApplicationPool; PhysicalPath = $directory.physicalPath }
                    }
                }
            }
        }
    )

    $target = ConvertTo-ComparablePhysicalPath $PhysicalPath

    try {
        $applications = @(& $ApplicationsProbe)
    } catch {
        throw "Cannot read the IIS sites and applications, so it cannot be confirmed that app pool '$Name' is what serves '$PhysicalPath': $($_.Exception.Message)"
    }

    $inside = @($applications | Where-Object { Test-PhysicalPathInside (ConvertTo-ComparablePhysicalPath $_.PhysicalPath) $target })
    $serving = @($inside | Where-Object {
            [string]::Equals((ConvertTo-ComparablePhysicalPath $_.PhysicalPath), $target, [System.StringComparison]::OrdinalIgnoreCase)
        })

    if ($serving.Count -eq 0) {
        if ($applications.Count -eq 0) {
            $reported = 'IIS reported no sites or applications at all'
        } else {
            $reported = "IIS reported $($applications.Count): " + (@($applications | Select-Object -First 10 | ForEach-Object { "$($_.Location) at '$($_.PhysicalPath)' under '$($_.ApplicationPool)'" }) -join '; ')
        }
        throw "No IIS site or application has its files at '$PhysicalPath', so it cannot be confirmed that stopping app pool '$Name' stops what runs from them. $reported. Refusing to replace files that may still be served."
    }

    $elsewhere = @($inside | Where-Object { -not [string]::Equals([string]$_.ApplicationPool, $Name, [System.StringComparison]::OrdinalIgnoreCase) })
    if ($elsewhere.Count -gt 0) {
        $named = @($elsewhere | ForEach-Object { "$($_.Location) runs under '$($_.ApplicationPool)', with its files at '$($_.PhysicalPath)'" }) -join '; '
        throw "Files at or under '$PhysicalPath' are served by an app pool other than '$Name': $named. Stopping '$Name' would leave that application running while its files are replaced. Name the pool that serves them, or fix the binding."
    }

    return $inside
}

Export-ModuleMember -Function Assert-AppPoolServesPath
