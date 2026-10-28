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
    # (Test-PhysicalPathInside) - never by bare prefix. Exported for deploy.ps1 (cdt-36): its check
    # that a run does not move an existing application compares folders with this, as
    # Assert-AppPoolServesPath does later in the same run, so the two agree on what one folder is.
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

Export-ModuleMember -Function ConvertTo-ComparablePhysicalPath

# cdt-46. GetFinalPathNameByHandle through a small type compiled on first use: the filesystem's own
# answer to which folder a spelling names. C# 5, so the Windows PowerShell 5.1 compiler takes it too.
$script:FinalPathSource = @'
using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AppPoolControlNative
{
    public static class FinalPath
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder filePath, uint filePathLength, uint flags);

        public static string Get(string path)
        {
            // FILE_READ_ATTRIBUTES; share read, write and delete; OPEN_EXISTING; BACKUP_SEMANTICS, which opens folders.
            using (SafeFileHandle handle = CreateFileW(path, 0x80, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero))
            {
                if (handle.IsInvalid) { return null; }
                StringBuilder buffer = new StringBuilder(1024);
                uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
                if (length >= buffer.Capacity)
                {
                    buffer = new StringBuilder((int)length + 1);
                    length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
                }
                if (length == 0 || length >= buffer.Capacity) { return null; }
                string text = buffer.ToString();
                if (text.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) { return @"\\" + text.Substring(8); }
                if (text.StartsWith(@"\\?\", StringComparison.Ordinal)) { return text.Substring(4); }
                return text;
            }
        }
    }
}
'@

function Resolve-FinalPhysicalPath {
    # cdt-46. The final spelling of a folder that exists, as the filesystem resolves it: 8.3 short
    # names expanded to long ones, junctions and symbolic links followed, the case on disk. $null when
    # the path cannot be opened - it does not exist, access is denied - or the type cannot be compiled,
    # so a caller falls back to the lexical comparison and fails closed. Takes a comparable form
    # (ConvertTo-ComparablePhysicalPath) and returns one, trailing separators dropped. Not exported.
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) { return $null }
    try {
        $type = 'AppPoolControlNative.FinalPath' -as [type]
        if ($null -eq $type) {
            Add-Type -TypeDefinition $script:FinalPathSource -Language CSharp -ErrorAction Stop
            $type = 'AppPoolControlNative.FinalPath' -as [type]
        }
        $final = $type::Get($Path)
    } catch {
        return $null
    }
    if ([string]::IsNullOrWhiteSpace($final)) { return $null }
    return $final.TrimEnd('\', '/')
}

function Test-SamePhysicalFolder {
    # cdt-44. True when two spellings name the same folder: both through
    # ConvertTo-ComparablePhysicalPath, then equal, ordinal and case-insensitive - never by prefix. A
    # spelling that reads as empty is never the same folder as anything. deploy.ps1 asks this twice,
    # in its check that a run does not move an existing application and in its choice of which
    # folder's configuration is live, so the two answers cannot disagree.
    # cdt-46. Or both spellings exist and resolve to one final path: an 8.3 short name, a junction. A
    # spelling that does not resolve is not taken as the same folder, so the caller fails closed.
    param([string]$Path, [string]$Other)

    $first = ConvertTo-ComparablePhysicalPath $Path
    $second = ConvertTo-ComparablePhysicalPath $Other
    if ([string]::IsNullOrWhiteSpace($first) -or [string]::IsNullOrWhiteSpace($second)) { return $false }
    if ([string]::Equals($first, $second, [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
    $firstFinal = Resolve-FinalPhysicalPath $first
    $secondFinal = Resolve-FinalPhysicalPath $second
    if ($null -eq $firstFinal -or $null -eq $secondFinal) { return $false }
    return [string]::Equals($firstFinal, $secondFinal, [System.StringComparison]::OrdinalIgnoreCase)
}

Export-ModuleMember -Function Test-SamePhysicalFolder

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
        AT the folder, everything at the folder runs under the named pool, and nothing is rooted
        inside it. aud-a38f: robocopy /MIR replaces the whole tree, so an application or virtual
        directory rooted inside the folder and served by another pool would keep running while
        its files are replaced; and a nested match alone is not "what serves the folder" - a
        parent folder named by mistake would have its other children replaced too. aud-a38i: the
        same pool does not make a nested node safe either. Stopping it stops the node, but the
        mirror still replaces the node's files with the publish output, or purges them because
        that output does not have them, so a node rooted inside the folder is refused whatever
        its pool. An answer that cannot be read, or that has nothing at the folder, is a refusal
        as well: if nothing can be shown to serve the folder, stopping the named pool cannot be
        shown to stop what runs from it. The refusal lists what IIS reported, so a folder that
        IIS spells differently is visible in the message itself.

        Folders compare after normalisation, by equality or at a directory boundary, never by
        bare prefix - one instance's folder is easily a prefix of a sibling's.

        aud-a38j. The aud-a38i refusal is the rule for a caller that mirrors over the folder, and
        the wrong one for a caller that never does, so -NestedNodes has no default and every caller
        states which it is. Refuse: the folder's own nodes run under the named pool and nothing is
        rooted inside it, whatever its pool - deploy.ps1, the promotion and the installer, which
        mirror. Allow: everything at or inside the folder runs under the named pool (the aud-a38f
        rule) - the shared-config cutover, which stops the pool and copies databases but replaces
        none of the folder's files. Under either rule IIS unreadable, nothing at the folder, or the
        folder's own nodes under another pool refuse.

        aud-a38k. The same choice words the refusals: under Refuse they say what the mirror would do
        to files still being served; under Allow, that nothing will be stopped.

    .OUTPUTS
        What IIS serves at or inside the folder (Location, ApplicationPool, PhysicalPath) when the
        rule the caller named holds - with Refuse, only what is at the folder. Throws otherwise.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [string]$PhysicalPath,

        # aud-a38j. Refuse for a caller that mirrors over the folder, Allow for one that replaces
        # nothing inside it. Mandatory with no default, deliberately: the two rules are each wrong
        # for the other kind of caller, so a default would pick a rule for a caller that never chose
        # one - the way the cutover inherited the mirror's refusal.
        [Parameter(Mandatory = $true)]
        [ValidateSet('Refuse', 'Allow')]
        [string]$NestedNodes,

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
    # cdt-46. And, when the folder exists, the final path the filesystem resolves it to, so a node IIS
    # spells with an 8.3 short name or through a junction is matched too. A node is at the folder, or
    # inside it, when either its lexical or its final form says so - never fewer nodes than the lexical
    # comparison alone: a node rooted inside the folder through a junction that leads elsewhere stays
    # inside, and one whose files reach into the folder through a junction elsewhere is inside as well.
    $targetFinal = Resolve-FinalPhysicalPath $target
    $finals = @{}
    $finalOf = {
        param([string]$Lexical)
        if (-not $finals.ContainsKey($Lexical)) { $finals[$Lexical] = Resolve-FinalPhysicalPath $Lexical }
        $finals[$Lexical]
    }
    $isAtFolder = {
        param($Node)
        $lexical = ConvertTo-ComparablePhysicalPath $Node.PhysicalPath
        if ([string]::Equals($lexical, $target, [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
        if ($null -eq $targetFinal) { return $false }
        $final = & $finalOf $lexical
        return ($null -ne $final -and [string]::Equals($final, $targetFinal, [System.StringComparison]::OrdinalIgnoreCase))
    }
    $isInFolder = {
        param($Node)
        $lexical = ConvertTo-ComparablePhysicalPath $Node.PhysicalPath
        if (Test-PhysicalPathInside $lexical $target) { return $true }
        if ($null -eq $targetFinal) { return $false }
        $final = & $finalOf $lexical
        return ($null -ne $final -and (Test-PhysicalPathInside $final $targetFinal))
    }

    try {
        $applications = @(& $ApplicationsProbe)
    } catch {
        throw "Cannot read the IIS sites and applications, so it cannot be confirmed that app pool '$Name' is what serves '$PhysicalPath': $($_.Exception.Message)"
    }

    $inside = @($applications | Where-Object { & $isInFolder $_ })
    $serving = @($inside | Where-Object { & $isAtFolder $_ })

    # Which nodes must run under the named pool. Refuse: the folder's own. A node inside it is refused
    # below whatever its pool, and in the same refusal as every other nested node: "name the pool that
    # serves them" would be the wrong advice for it, since no pool makes it safe to mirror over.
    # Allow (aud-a38j): everything at or inside the folder, the aud-a38f rule. The caller replaces no
    # files, so a nested node under the named pool is safe - stopping the pool stops it too - but one
    # under another pool would go on serving from inside the folder while the caller works on it. No
    # files are replaced then, so the refusal does not say they are.
    #
    # aud-a38k: how each refusal ends is chosen here too, with the rule, so the two cannot disagree. A
    # caller that mirrors is told what the refusal prevents - replacing files that may still be served.
    # A caller that replaces nothing has only the stop gated by this check, so it is told the named
    # pool is not shown serving the folder and nothing will be stopped. The nothing-at-the-folder
    # refusal used to end "Refusing to replace files" for every caller, the cutover included, which
    # gave its operator a reason the cutover does not have.
    if ($NestedNodes -eq 'Refuse') {
        $held = $serving
        $scope = "at '$PhysicalPath'"
        $consequence = ' while its files are replaced'
        $unserved = 'Refusing to replace files that may still be served.'
    } else {
        $held = $inside
        $scope = "at or under '$PhysicalPath'"
        $consequence = ', so nothing will be stopped'
        $unserved = "IIS does not show '$Name' serving '$PhysicalPath', so nothing will be stopped."
    }

    if ($serving.Count -eq 0) {
        if ($applications.Count -eq 0) {
            $reported = 'IIS reported no sites or applications at all'
        } else {
            $reported = "IIS reported $($applications.Count): " + (@($applications | Select-Object -First 10 | ForEach-Object { "$($_.Location) at '$($_.PhysicalPath)' under '$($_.ApplicationPool)'" }) -join '; ')
        }
        throw "No IIS site or application has its files at '$PhysicalPath', so it cannot be confirmed that stopping app pool '$Name' stops what runs from them. $reported. $unserved"
    }

    $elsewhere = @($held | Where-Object { -not [string]::Equals([string]$_.ApplicationPool, $Name, [System.StringComparison]::OrdinalIgnoreCase) })
    if ($elsewhere.Count -gt 0) {
        $named = @($elsewhere | ForEach-Object { "$($_.Location) runs under '$($_.ApplicationPool)', with its files at '$($_.PhysicalPath)'" }) -join '; '
        throw "Files $scope are served by an app pool other than '$Name': $named. Stopping '$Name' would leave that application running$consequence. Name the pool that serves them, or fix the binding."
    }

    # aud-a38i, for a caller that mirrors. Strictly inside: in the tree by the aud-a38f boundary
    # match, and not the folder itself. No pool filter, deliberately - under aud-a38f a nested node
    # under the named pool passed, its pool was stopped, and the mirror then replaced or purged its
    # files while the run reported success.
    if ($NestedNodes -eq 'Refuse') {
        $nested = @($inside | Where-Object { -not (& $isAtFolder $_) })
        if ($nested.Count -gt 0) {
            $named = @($nested | ForEach-Object { "$($_.Location) runs under '$($_.ApplicationPool)', with its files at '$($_.PhysicalPath)'" }) -join '; '
            throw "IIS serves content from inside '$PhysicalPath': $named. The mirror replaces '$PhysicalPath' with the publish output and purges whatever that output lacks, so it would replace or purge those files, whatever pool serves them. Move each out of '$PhysicalPath', or remove it from IIS."
        }
    }

    # On a pass under Refuse nothing is inside the folder, so this is what is at it.
    return $inside
}

Export-ModuleMember -Function Assert-AppPoolServesPath

function Get-AppPoolOtherApplication {
    <#
    .SYNOPSIS
        Lists the IIS applications an app pool serves besides the instance whose files are at a folder.

    .DESCRIPTION
        cdt-54. Stopping a pool stops every application it serves. Assert-AppPoolServesPath looks only
        at what is at or inside the folder about to be replaced, so an application elsewhere that runs
        under the same pool was stopped with it, unannounced. This lists those: every site root and
        application IIS runs under the named pool whose files are neither at the folder nor inside it.
        The folder's own nodes are the instance being deployed; a node inside the folder is refused
        before the stop anyway (aud-a38i).

        Folders compare as in Assert-AppPoolServesPath: lexically, at a directory boundary, or by
        final path when both resolve (cdt-46). Pool names compare case-insensitively. It errs toward
        listing: a node is left out only when its lexical or final path puts it at or inside the
        folder, so an application with no folder, or one whose folder cannot be resolved, is listed.

        Virtual directories are not read. Each belongs to an application, which is listed if it is
        another one; counting them would report the instance's own virtual directories as other
        applications.

        cdt-60. -IncludeNested leaves out only the nodes AT the folder - the instance itself - and
        lists those inside it. It is for a caller whose serving check allows a node rooted inside the
        instance's folder under the same pool (the cutover's -NestedNodes Allow): such a node stops
        with the pool, so it is another application to name and to read for bulk jobs. Without the
        switch, the rule above, for the callers that refuse a nested node before their stop.

    .OUTPUTS
        Location, ApplicationPool and PhysicalPath of each other application; nothing when there is
        none. Throws when IIS cannot be read.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [string]$PhysicalPath,

        # Every site root and application IIS knows: the first half of Assert-AppPoolServesPath's
        # probe, without the virtual directories.
        [scriptblock]$ApplicationsProbe = {
            foreach ($site in @(Get-Website)) {
                [pscustomobject]@{ Location = "$($site.name)/"; ApplicationPool = $site.applicationPool; PhysicalPath = $site.physicalPath }
                foreach ($app in @(Get-WebApplication -Site $site.name)) {
                    [pscustomobject]@{ Location = "$($site.name)$($app.path)"; ApplicationPool = $app.applicationPool; PhysicalPath = $app.PhysicalPath }
                }
            }
        },

        # cdt-60. Leave out only the nodes at the folder, and list those inside it (see the description).
        [switch]$IncludeNested
    )

    try {
        $applications = @(& $ApplicationsProbe)
    } catch {
        throw "Cannot read the IIS sites and applications, so the applications app pool '$Name' serves besides the one at '$PhysicalPath' cannot be listed: $($_.Exception.Message)"
    }

    $target = ConvertTo-ComparablePhysicalPath $PhysicalPath
    $targetFinal = Resolve-FinalPhysicalPath $target
    # The instance's own nodes, left out: those at the folder and, unless -IncludeNested, those inside it.
    $isTheInstance = if ($IncludeNested) {
        { param([string]$Path, [string]$Root) [string]::Equals($Path, $Root, [System.StringComparison]::OrdinalIgnoreCase) }
    } else {
        { param([string]$Path, [string]$Root) Test-PhysicalPathInside $Path $Root }
    }
    foreach ($node in $applications) {
        if (-not [string]::Equals([string]$node.ApplicationPool, $Name, [System.StringComparison]::OrdinalIgnoreCase)) { continue }
        $lexical = ConvertTo-ComparablePhysicalPath $node.PhysicalPath
        if ($lexical) {
            if (& $isTheInstance $lexical $target) { continue }
            if ($null -ne $targetFinal) {
                $final = Resolve-FinalPhysicalPath $lexical
                if ($null -ne $final -and (& $isTheInstance $final $targetFinal)) { continue }
            }
        }
        $node
    }
}

Export-ModuleMember -Function Get-AppPoolOtherApplication
