<#
.SYNOPSIS
    One-time cutover: make the dev and prod instances on this server share ONE config database.

.DESCRIPTION
    Implements the owner ruling of 2026-09-04 (docs/SharedConfigDb-Plan.md, .agents/decisions.md):
    both instances open the same exchangeadmin.db, promotion never copies it, and dev's current
    database becomes the shared one (prod's has only ever received copies of it).

    Plan mode is the DEFAULT: without -Apply the script prints every step and changes nothing.
    Run it elevated (it stops and starts IIS app pools) and with -PlanOnly first, then -Apply.
    Run the plan ELEVATED too: only then can it read IIS - which app pool serves each publish
    folder (step 1), and the app pool identities that step 5 needs. Unelevated, the plan says so
    and skips those checks rather than pretending.

    THE ORDER MATTERS: both instances must already run a build that accepts a database newer
    than itself (app version 2.19.0 or later, SharedConfigDb-Plan S1). Deploy that build to dev
    and promote it to prod BEFORE running this with -Apply; the script refuses otherwise, by
    reading each instance's ExchangeAdminWeb.dll file version.

    Steps (each honours -PlanOnly):
      1. Assert both builds are at or above -MinimumAppVersion, and that IIS serves each
         publish folder from the app pool named for it and from no other.
      2. Stop both app pools and confirm IIS reports each Stopped (warning about any active
         bulk job first). Nothing is copied until both are confirmed.
      3. Take verified backups of BOTH current databases into -BackupRoot.
      4. VACUUM INTO dev's database to -SharedDbPath and integrity-check the result.
      5. Grant both app pool identities (OI)(CI)M on the shared directory.
      6. Write ConfigStore:Path into both appsettings.json files (atomic temp-file write).
      7. Start both pools.
      8. Verify each instance logged "Config store schema ready" once since the restart.

    Any failure after step 3 prints the exact commands that return the instances to two
    databases. A successful run prints them too (that is the rollback story from now on).

    Windows PowerShell 5.1 compatible (WebAdministration provider); pure ASCII.

.PARAMETER DevPublishPath
    Dev instance publish folder (holds appsettings.json, config\, ExchangeAdminWeb.dll).
.PARAMETER ProdPublishPath
    Prod instance publish folder.
.PARAMETER SharedDbPath
    Where the shared database will live. Must be an absolute LOCAL path (no UNC share).
.PARAMETER DevAppPoolName
    IIS app pool of the dev instance.
.PARAMETER ProdAppPoolName
    IIS app pool of the prod instance.
.PARAMETER BackupRoot
    Directory for the two pre-cutover verified backups.
.PARAMETER MinimumAppVersion
    The lowest ExchangeAdminWeb.dll file version that tolerates a newer database.
.PARAMETER PlanOnly
    Print every step; change nothing. Default unless -Apply is given.
.PARAMETER Apply
    Make the changes.

.EXAMPLE
    .\tools\Move-ConfigDbToShared.ps1 -PlanOnly

.EXAMPLE
    .\tools\Move-ConfigDbToShared.ps1 -Apply
#>

[CmdletBinding()]
param(
    [string]$DevPublishPath = "D:\inetpub\ExchangeAdminWebDev",
    [string]$ProdPublishPath = "D:\inetpub\ExchangeAdminWeb",
    [string]$SharedDbPath = "D:\inetpub\ExchangeAdminWebShared\config\exchangeadmin.db",
    [string]$DevAppPoolName = "ExchangeAdminWebDev",
    [string]$ProdAppPoolName = "ExchangeAdminWeb",
    [string]$BackupRoot = "D:\backups\ExchangeAdminWeb\SharedConfigCutover",
    [version]$MinimumAppVersion = "2.19.0",
    [switch]$PlanOnly,
    [switch]$Apply
)

$ErrorActionPreference = "Stop"

# Plan mode is the default; -Apply is the only way to act. -PlanOnly is accepted explicitly so
# the invocation reads the same way as the other ops scripts.
if (-not $Apply) { $PlanOnly = $true }
if ($Apply -and $PSBoundParameters.ContainsKey('PlanOnly') -and $PlanOnly) {
    throw "Specify -PlanOnly or -Apply, not both."
}

Import-Module (Join-Path $PSScriptRoot 'SqliteConfigBackup.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'JobStateWarning.psm1') -Force
# aud-a38e: Stop-AppPoolVerified - IIS accepting a stop request is not the pool stopped.
# aud-a38g: Assert-AppPoolServesPath - and a stopped pool is not necessarily the one serving the instance.
Import-Module (Join-Path $PSScriptRoot 'AppPoolControl.psm1') -Force

function Write-Step { param([string]$Message) Write-Host ">>> $Message" -ForegroundColor Cyan }
function Write-Ok { param([string]$Message) Write-Host " OK  $Message" -ForegroundColor Green }
function Write-Warn { param([string]$Message) Write-Host "  !  $Message" -ForegroundColor Yellow }
function Write-Plan { param([string]$Message) Write-Host "PLAN $Message" -ForegroundColor DarkGray }
function Write-Fail { param([string]$Message) Write-Host "  X  $Message" -ForegroundColor Red; throw $Message }

function Invoke-PlanOrAction {
    param([string]$Description, [scriptblock]$Action)

    if ($PlanOnly) {
        Write-Plan $Description
        return
    }

    Write-Step $Description
    & $Action
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Assert-LocalAbsolutePath {
    # cdt-38: no copy of the rule here. This kept the old writers' own - a drive letter, a colon
    # and a BACKSLASH, matched case-insensitively - after cdt-37 moved the installer off it, so it
    # refused D:/... (which the app opens and the read side reads back), called \/server/share "not
    # absolute" rather than a share, and passed a Kelvin-sign drive letter the app refuses. The rule
    # is SqliteConfigBackup's Get-ConfigStorePathForm (imported above), the scripts' one copy of the
    # app's ConfigStorePath.Resolve: any two leading separators are a network share, and only an
    # ASCII letter, a colon, then \ or / is an absolute local path - so "D:file.db", which names a
    # drive but resolves against that drive's current directory, is refused too (review finding
    # scdi-2). Anything but Local is refused.
    param([string]$Path, [string]$Name)

    if ([string]::IsNullOrWhiteSpace($Path)) { Write-Fail "$Name is required." }
    $form = Get-ConfigStorePathForm -Value $Path
    if ($form -eq 'NetworkShare') {
        Write-Fail "$Name must be a local path, not a network share: $Path. SQLite locking is not reliable over a share and both instances run on this server."
    }
    if ($form -ne 'Local') {
        Write-Fail "$Name must be an absolute local path (a drive letter, a colon, then \ or / - for example D:\inetpub\ExchangeAdminWebShared\config\exchangeadmin.db): $Path"
    }
}

function Test-PathIsInside {
    <#
    .SYNOPSIS
        True when $Path lies within $Directory.
    .DESCRIPTION
        Audit finding A39. Textual and case-insensitive, matching how this script already
        compares the two publish paths. The trailing separator on the directory is what
        stops "D:\inetpub\App" matching "D:\inetpub\AppOther".
    #>
    param([string]$Path, [string]$Directory)

    if ([string]::IsNullOrWhiteSpace($Path) -or [string]::IsNullOrWhiteSpace($Directory)) { return $false }

    # Review of A39: comparing raw strings let `..`, `.`, forward slashes and doubled
    # separators walk straight past this check - "D:\inetpub\App\..\App\x" does not start
    # with "D:\inetpub\App\" as text but is inside it on disk. GetFullPath canonicalises
    # all of those and is purely in-memory, so it costs a read-only precondition nothing.
    # (It does NOT resolve junctions or subst drives; that limit is recorded on the finding.)
    try {
        $dir = [System.IO.Path]::GetFullPath($Directory).TrimEnd('\', '/') + '\'
        $candidate = [System.IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    } catch {
        # An unparseable path is not provably outside, so refuse to call it safe.
        return $true
    }

    return $candidate.StartsWith($dir, [System.StringComparison]::OrdinalIgnoreCase)
}

function Get-AppFileVersion {
    param([string]$PublishPath)

    $dll = Join-Path $PublishPath 'ExchangeAdminWeb.dll'
    if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) {
        Write-Fail "No ExchangeAdminWeb.dll in $PublishPath - is this an instance publish folder?"
    }
    $raw = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($dll).FileVersion
    if ([string]::IsNullOrWhiteSpace($raw)) {
        Write-Fail "ExchangeAdminWeb.dll in $PublishPath carries no file version."
    }
    return [version]($raw.Split(' ')[0])
}

function Assert-BuildTolerates {
    # Step 1. A build older than -MinimumAppVersion refuses a database migrated by a newer one
    # (the pre-S1 migrator). Sharing the file before both builds tolerate it would stop the
    # older instance on the first migration.
    param([string]$Label, [string]$PublishPath)

    $actual = Get-AppFileVersion -PublishPath $PublishPath
    if ($actual -lt $MinimumAppVersion) {
        Write-Fail "$Label build is $actual, below the minimum $MinimumAppVersion that tolerates a newer database. Deploy the current build to dev, promote it to prod, then run this script again. Nothing was changed."
    }
    Write-Ok "$Label build $actual is at or above $MinimumAppVersion"
}

function Get-AppPoolIdentity {
    # Get-Item on an app pool makes the IIS provider materialize processModel.identityType as an
    # integer, so a pool set to SpecificUser throws "Cannot convert value SpecificUser to type
    # System.Int32" and the pool object never comes back - the caller's own cast is never even
    # reached. Every other IIS read in this repo (deploy.ps1, Install-ExchangeAdminWeb.ps1, and
    # the State reads below) uses Get-ItemProperty, which returns the element without that cast.
    # Match identityType as text: the provider yields the name under PowerShell 7 and the number
    # under Windows PowerShell 5.1.
    param([string]$Name)

    $model = Get-ItemProperty "IIS:\AppPools\$Name" -Name processModel -ErrorAction Stop
    if ($null -eq $model) { Write-Fail "App pool $Name has no processModel configuration." }

    if ("$($model.identityType)" -in @('SpecificUser', '3')) {
        if ([string]::IsNullOrWhiteSpace($model.userName)) {
            Write-Fail "App pool $Name is set to run as a specific user but IIS carries no user name for it."
        }
        return $model.userName
    }

    return "IIS AppPool\$Name"
}

function Assert-PoolServesInstance {
    # Step 1, and audit finding aud-a38g. Step 2 stops the pool it is NAMED and aud-a38e confirms
    # that pool stopped - which proves nothing about a folder IIS serves from a different pool. A
    # wrong but existing -DevAppPoolName (DefaultAppPool, or the old name of a renamed pool) was
    # stopped and confirmed, dev ran on under its real pool while steps 3 and 4 snapshotted its
    # database, and the restore commands then copied that stale snapshot back over the live file.
    #
    # The rule is AppPoolControl's (aud-a38c): it refuses unless IIS serves the folder from the
    # named pool and from no other, and fails closed when IIS cannot be read or nothing serves the
    # folder. Its message names the pool that does serve the folder, or lists what IIS reported;
    # this adds the parameter to fix.
    #
    # aud-a38j: -NestedNodes Allow, where the promotion passes Refuse. The cutover copies databases
    # only - backups out to -BackupRoot, dev's database to the shared path, which lies outside both
    # folders - and rewrites each instance's appsettings.json; it never mirrors. So a site,
    # application or virtual directory rooted inside the folder loses no files to it, and one under
    # the named pool is stopped with it. One under another pool would keep a worker serving from
    # inside the instance's folder while its database is copied, and Allow still refuses that.
    param([string]$Label, [string]$Name, [string]$PublishPath, [string]$PoolParameter)

    try {
        $null = Assert-AppPoolServesPath -Name $Name -PhysicalPath $PublishPath -NestedNodes Allow
    } catch {
        Write-Fail "-$PoolParameter names app pool '$Name', but IIS does not confirm that this pool alone serves the $Label folder $PublishPath. $($_.Exception.Message) The cutover stops that pool and then copies the $Label database, so it must be the pool that serves the instance. Nothing has been stopped or copied."
    }
    Write-Ok "$Label folder $PublishPath is served by app pool $Name and no other"
}

function Stop-AppPoolChecked {
    param([string]$Name, [string]$ConfigDir, [string]$AppPath)

    Invoke-PlanOrAction "Stop app pool $Name and confirm IIS reports it Stopped before any database is copied (warns about active bulk jobs first)" {
        Assert-NoActiveBulkJobsBeforeRecycle -ConfigDir $ConfigDir -AppPath $AppPath | Out-Null
        # aud-a38e: this was a bare Stop-WebAppPool and a three-second sleep - a guess about how
        # long a worker takes to exit - and then "stopped" was printed either way and steps 3 and
        # 4 backed up and VACUUMed INTO dev's live database. A worker still draining could commit
        # a write after that snapshot, into the per-instance file dev stops opening at step 6.
        # Stop, then CONFIRM: Stop-AppPoolVerified polls IIS until the pool reports Stopped, and
        # throws if it never does or its state cannot be read.
        #
        # 'Absent' neither throws nor means Stopped. It is right for a first install, where
        # nothing is serving; here it means a wrong pool name, with the instance's real pool still
        # up. So nothing but 'Stopped' gets past this point. A refusal throws into the cutover's
        # catch, which restarts both pools: nothing has been copied or repointed yet.
        $poolState = Stop-AppPoolVerified -Name $Name
        if ($poolState -ne 'Stopped') {
            if ($poolState -eq 'Absent') {
                Write-Fail "App pool '$Name' does not exist. The cutover copies the databases of two running instances and must stop both first, so check -DevAppPoolName and -ProdAppPoolName. No database was copied."
            }
            Write-Fail "App pool '$Name' was not confirmed stopped (IIS reported '$poolState'). Refusing to copy a database its instance may still be writing. No database was copied."
        }
        Write-Ok "App pool $Name confirmed stopped"
    }
    if ($PlanOnly) { Assert-NoActiveBulkJobsBeforeRecycle -ConfigDir $ConfigDir -AppPath $AppPath -PlanOnly | Out-Null }
}

function Start-AppPoolChecked {
    param([string]$Name)

    Invoke-PlanOrAction "Start app pool $Name and confirm IIS reports it Started" {
        # aud-a38f: this set "started" the moment Start-WebAppPool returned - IIS accepting the
        # request - and printed it. A bad identity password, a missing runtime or rapid-fail
        # protection leaves the pool stopped with no exception, so the failure path's restart
        # ("so the outage ends here") could report a pool up that never came back. CONFIRM it:
        # Start-AppPoolVerified polls IIS until the pool reports Started, and throws otherwise -
        # including for a pool that does not exist, which on this side is an error (aud-a38).
        $null = Start-AppPoolVerified -Name $Name
        Write-Ok "App pool $Name confirmed started"
    }
}

function Set-ConfigStorePathInAppsettings {
    # Step 6. Same atomic temp-file pattern as promote-dev-to-prod.ps1 Set-AppsettingsPathBase:
    # serialize, re-parse the temp file, then move it into place. A backup of the original is
    # kept next to it so the key can be reverted by hand.
    param([string]$AppSettingsPath, [string]$Value, [string]$BackupSuffix)

    if (-not (Test-Path -LiteralPath $AppSettingsPath -PathType Leaf)) {
        Write-Fail "appsettings.json was not found: $AppSettingsPath"
    }

    Invoke-PlanOrAction "Set ConfigStore:Path = $Value in $AppSettingsPath (backup: appsettings.json.$BackupSuffix)" {
        Copy-Item -LiteralPath $AppSettingsPath -Destination "$AppSettingsPath.$BackupSuffix" -Force

        $json = Get-Content -LiteralPath $AppSettingsPath -Raw | ConvertFrom-Json
        if (-not (Get-Member -InputObject $json -Name ConfigStore -MemberType NoteProperty -ErrorAction SilentlyContinue)) {
            $json | Add-Member -NotePropertyName ConfigStore -NotePropertyValue ([pscustomobject]@{})
        }
        if (Get-Member -InputObject $json.ConfigStore -Name Path -MemberType NoteProperty -ErrorAction SilentlyContinue) {
            $json.ConfigStore.Path = $Value
        } else {
            $json.ConfigStore | Add-Member -NotePropertyName Path -NotePropertyValue $Value
        }

        $tmp = Join-Path (Split-Path -Parent $AppSettingsPath) ("appsettings.cutover.{0}.tmp" -f [guid]::NewGuid().ToString("N"))
        try {
            $json | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $tmp -Encoding UTF8
            $check = Get-Content -LiteralPath $tmp -Raw | ConvertFrom-Json
            if ($check.ConfigStore.Path -ne $Value) { Write-Fail "Re-read of $tmp did not carry ConfigStore:Path = $Value" }
            Move-Item -LiteralPath $tmp -Destination $AppSettingsPath -Force
            Write-Ok "ConfigStore:Path set in $AppSettingsPath"
        } finally {
            Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
        }
    }
}

function Get-LatestSerilogLog {
    param([string]$PublishPath)

    $logDir = Join-Path $PublishPath 'logs'
    if (-not (Test-Path -LiteralPath $logDir -PathType Container)) { return $null }
    return Get-ChildItem -LiteralPath $logDir -Filter 'app-*.log' -File |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
}

# Review of A40: a compiled validator for the warm-up's https attempt. A PowerShell
# scriptblock assigned to ServerCertificateValidationCallback needs a Runspace on the thread
# that invokes it, and the TLS handshake runs on a threadpool thread on both editions. This
# is IL, so it has no such dependency. It accepts any certificate, which is correct for a
# loopback request that reads nothing it needs to trust - and it is installed only around
# that one request, then removed in a finally.
if (-not ('ExchangeAdminWeb.WarmupTls' -as [type])) {
    Add-Type -TypeDefinition @'
namespace ExchangeAdminWeb {
    public static class WarmupTls {
        public static bool AcceptAny(
            object sender,
            System.Security.Cryptography.X509Certificates.X509Certificate certificate,
            System.Security.Cryptography.X509Certificates.X509Chain chain,
            System.Net.Security.SslPolicyErrors sslPolicyErrors) {
            return true;
        }
    }
}
'@
}

function Get-AppPathBase {
    <#
    .SYNOPSIS
        The instance's Application:PathBase, or '' when it has none.
    .DESCRIPTION
        Audit finding A40. Needed to build the URL that wakes the application. Read from the
        same appsettings.json this script has already been writing ConfigStore:Path into.
    #>
    param([string]$PublishPath)

    $settings = Join-Path $PublishPath 'appsettings.json'
    if (-not (Test-Path -LiteralPath $settings -PathType Leaf)) { return '' }

    try {
        $json = Get-Content -LiteralPath $settings -Raw | ConvertFrom-Json
    } catch {
        return ''
    }

    if ($null -eq $json.Application) { return '' }

    $base = [string]$json.Application.PathBase
    if ([string]::IsNullOrWhiteSpace($base)) { return '' }

    return '/' + $base.Trim().Trim('/')
}

function Invoke-AppWarmup {
    <#
    .SYNOPSIS
        Sends one request to the instance so ASP.NET Core actually initialises.

    .DESCRIPTION
        Audit finding A40. Step 8 waits for "Config store schema ready", which Program.cs
        writes during application startup - not when the app POOL starts. Starting a pool
        creates a worker process; IIS does not load the application into it until a request
        arrives, unless Application Initialization is installed AND the application has
        preloadEnabled set. startMode=AlwaysRunning keeps the worker alive and initialises
        nothing. An independent review confirmed that diagnosis: with preloadEnabled unset,
        GL_APPLICATION_PRELOAD never fires and Program.cs never runs until a request does.

        The SAME review found the first version of this function wrong in three ways, all
        of them because it treated "something answered" as "our application answered":

        1. Any HTTP error response counted as success. A 503 from a pool that is still
           starting - the exact case the caller retries for - therefore latched the
           caller's $warmed flag and stopped it retrying for the remaining 90 seconds.
        2. A 404 from another site on port 80 (Default Web Site), a 400 from http.sys for
           an unmatched host header, or a 403.4 from "Require SSL" all counted as success
           too, so the https attempt never happened and the real app was never woken.
        3. The https attempt could not work at all under Windows PowerShell 5.1: a
           PowerShell scriptblock assigned to ServerCertificateValidationCallback needs a
           Runspace on the thread that invokes it, and Invoke-WebRequest runs the handshake
           on an IOCP thread where there is none.

        So: only a 2xx or 3xx counts as having reached a live application. Everything else
        - including 503 - is "not yet", which is the honest answer and lets the caller keep
        trying. The request is issued synchronously on the pipeline thread, which is also
        what makes the certificate callback usable at all.

        A failed warm-up is still not fatal. The log line remains the verification; this
        only makes it reachable.

    .OUTPUTS
        $true when a live application answered 2xx/3xx, otherwise $false.
    #>
    param(
        [string]$PublishPath,

        # Injectable so this is testable without a listener, the same way
        # tools/AppPoolControl.psm1 injects its IIS calls. Returns the HTTP status code as
        # an int, or 0 when nothing answered. Nothing but the tests passes it.
        [scriptblock]$Requester = {
            param($Url)

            # Synchronous, on the pipeline thread, and deliberately NOT Invoke-WebRequest:
            # 5.1 runs that asynchronously, and the certificate callback below then fires on
            # a threadpool thread with no Runspace and aborts the handshake.
            $request = [System.Net.WebRequest]::Create($Url)
            $request.Method = 'GET'
            $request.Timeout = 30000

            # Review of A40, and the defect that made the whole warm-up inert: this app's
            # IIS site runs Windows Authentication with anonymousAuthentication DISABLED
            # (deploy.ps1 and the installer both set it). WindowsAuthenticationModule runs
            # at RQ_AUTHENTICATE_REQUEST and AspNetCoreModuleV2 starts the .NET host at
            # RQ_EXECUTE_REQUEST_HANDLER - so an unauthenticated request is rejected 401.2
            # BEFORE the application is ever asked to start. Every warm-up this function
            # sent was refused at the door, and no test could see it because they all
            # inject a fake requester.
            $request.UseDefaultCredentials = $true

            # A redirect from http to https would be followed during the http pass, while
            # validation is still enforced, and fail there instead of being retried.
            $request.AllowAutoRedirect = $false

            try {
                $response = $request.GetResponse()
                try { return [int]$response.StatusCode } finally { $response.Close() }
            } catch [System.Net.WebException] {
                if ($_.Exception.Response) { return [int]$_.Exception.Response.StatusCode }
                return 0
            }
        }
    )

    $pathBase = Get-AppPathBase -PublishPath $PublishPath
    $suffix = if ($pathBase) { $pathBase + '/' } else { '/' }

    foreach ($scheme in @('http', 'https')) {
        $url = "${scheme}://localhost${suffix}"

        # Windows PowerShell 5.1 has no -SkipCertificateCheck, and a host certificate will
        # not match "localhost". Relax validation for this call only and put it back in a
        # finally - a warm-up request reads nothing it needs to trust.
        $previousCallback = [System.Net.ServicePointManager]::ServerCertificateValidationCallback
        try {
            if ($scheme -eq 'https') {
                # Review of A40: a PowerShell scriptblock here cannot work on EITHER
                # edition. 5.1 runs the handshake on an IOCP thread; PowerShell 7's
                # HttpWebRequest wraps SocketsHttpHandler and runs it on a threadpool
                # thread. Neither has a Runspace, so the delegate throws
                # PSInvalidOperationException and the handshake aborts - silently, because
                # the catch below swallows it. A compiled delegate has no such dependency.
                [System.Net.ServicePointManager]::ServerCertificateValidationCallback =
                    [System.Net.Security.RemoteCertificateValidationCallback]::CreateDelegate(
                        [System.Net.Security.RemoteCertificateValidationCallback],
                        [ExchangeAdminWeb.WarmupTls].GetMethod('AcceptAny'))
            }

            $status = & $Requester $url

            # 2xx only. A 3xx on the http pass used to return true and break out before the
            # https attempt ever ran (review of A40); a 503 means the pool is still coming
            # up; a 4xx usually means something else on this port answered. None of those
            # proves our application started.
            if ($status -ge 200 -and $status -lt 300) { return $true }

            # A redirect means something is there but has not served the app. Keep going to
            # the next scheme rather than declaring success.
        } catch {
            # Nothing usable on this scheme; try the next one.
        } finally {
            [System.Net.ServicePointManager]::ServerCertificateValidationCallback = $previousCallback
        }
    }

    return $false
}

function Assert-SchemaReadyLogged {
    # Step 8. Each instance logs "Config store schema ready at version N" once per start
    # (Program.cs). One line since the restart proves the instance opened the shared file.
    param([string]$Label, [string]$PublishPath, [datetime]$Since)

    Invoke-PlanOrAction "Warm $Label on localhost, then verify it logged 'Config store schema ready' once since restart (logs\app-*.log under $PublishPath)" {
        $deadline = (Get-Date).AddSeconds(90)
        $count = 0
        $warmed = $false
        do {
            # A40: the log line is written during APPLICATION startup, which a request
            # triggers. Retried each pass because the first attempt can land while the
            # worker process is still coming up.
            # Review of A40: this used to be gated on `-not $warmed`, which latched on the
            # first non-null HTTP response - including the 503 a starting pool returns - and
            # then never retried. Warm on EVERY pass; it is one request per five seconds and
            # the whole point is to keep poking until the application comes up.
            $warmed = (Invoke-AppWarmup -PublishPath $PublishPath) -or $warmed
            $log = Get-LatestSerilogLog -PublishPath $PublishPath
            if ($log -and $log.LastWriteTime -ge $Since) {
                $lines = Get-Content -LiteralPath $log.FullName -ErrorAction SilentlyContinue |
                    Where-Object { $_ -match 'Config store schema ready' }
                $count = @($lines | Where-Object {
                    $stamp = $null
                    if ($_ -match '^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})') {
                        $stamp = [datetime]::ParseExact($Matches[1], 'yyyy-MM-dd HH:mm:ss', $null)
                    }
                    ($null -ne $stamp) -and ($stamp -ge $Since.AddSeconds(-5))
                }).Count
            }
            if ($count -ge 1) { break }
            Start-Sleep -Seconds 5
        } while ((Get-Date) -lt $deadline)

        if ($count -lt 1) {
            # A40: which of the two failed changes what the operator should look at.
            $reach = if ($warmed) {
                "The instance answered on localhost, so it was reached and still did not log the line - this is a real startup failure."
            } else {
                "Nothing answered for this instance on localhost (http or https), so it may never have been asked to start. Check the site bindings and that the application is running before reading too much into the missing log line."
            }
            Write-Fail "$Label did not log 'Config store schema ready' within 90 s of starting. $reach Check $PublishPath\logs for the startup error (a missing or unreadable shared database is fatal by design)."
        }
        if ($count -gt 1) {
            Write-Warn "$Label logged 'Config store schema ready' $count times since the restart - more than one start happened; check the log."
        } else {
            Write-Ok "$Label logged 'Config store schema ready' once"
        }
    }
}

function Write-RestoreInstructions {
    param([string]$DevDb, [string]$ProdDb, [string]$DevBackup, [string]$ProdBackup, [string]$DevAppSettings, [string]$ProdAppSettings, [string]$Suffix)

    Write-Host ""
    Write-Host "To return to two databases (run elevated, both pools stopped):" -ForegroundColor Yellow
    Write-Host "  Stop-WebAppPool -Name $DevAppPoolName; Stop-WebAppPool -Name $ProdAppPoolName" -ForegroundColor DarkGray
    Write-Host "  Import-Module '$PSScriptRoot\SqliteConfigBackup.psm1'" -ForegroundColor DarkGray
    Write-Host "  Copy-SqliteDbFile -SourceDbPath '$SharedDbPath' -DestDbPath '$DevDb' -AppPath '$DevPublishPath'    # or -SourceDbPath '$DevBackup'" -ForegroundColor DarkGray
    Write-Host "  Copy-SqliteDbFile -SourceDbPath '$SharedDbPath' -DestDbPath '$ProdDb' -AppPath '$DevPublishPath'   # or -SourceDbPath '$ProdBackup'" -ForegroundColor DarkGray
    Write-Host "  Copy-Item '$DevAppSettings.$Suffix' '$DevAppSettings' -Force    # removes ConfigStore:Path" -ForegroundColor DarkGray
    Write-Host "  Copy-Item '$ProdAppSettings.$Suffix' '$ProdAppSettings' -Force  # removes ConfigStore:Path" -ForegroundColor DarkGray
    Write-Host "  Start-WebAppPool -Name $DevAppPoolName; Start-WebAppPool -Name $ProdAppPoolName" -ForegroundColor DarkGray
    Write-Host ""
}

function Assert-SharedDbUsable {
    # Review finding scdi-3. When both appsettings already name the shared path, "nothing to do"
    # is only true if the file they are configured to open exists and is sound: the app opens a
    # configured path WITHOUT create, so a deleted or never-created shared file stops BOTH pools
    # at their next start. That is the failure to report - in plan mode too - never a success.
    # Prints the same return-to-two-databases commands the cutover itself prints, pointing at
    # the newest backups this script left behind (placeholders when none are found).
    param([string]$Path, [string]$DevConfigDir, [string]$ProdConfigDir, [string]$DevAppSettings, [string]$ProdAppSettings)

    $problem = $null
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        $problem = "no file exists there"
    } else {
        try {
            Test-SqliteConfigDbIntegrity -DbPath $Path -AppPath $DevPublishPath | Out-Null
        } catch {
            $problem = "it failed PRAGMA integrity_check: $($_.Exception.Message)"
        }
    }
    if (-not $problem) { return }

    $devBackup = Get-ChildItem -LiteralPath $BackupRoot -Recurse -File -Filter 'dev.exchangeadmin.*.db' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $prodBackup = Get-ChildItem -LiteralPath $BackupRoot -Recurse -File -Filter 'prod.exchangeadmin.*.db' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $appsettingsBackup = Get-ChildItem -LiteralPath (Split-Path -Parent $DevAppSettings) -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like 'appsettings.json.pre-shared.*.bak' } |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $suffix = if ($appsettingsBackup) { $appsettingsBackup.Name.Substring('appsettings.json.'.Length) } else { 'pre-shared.<timestamp>.bak' }

    Write-Host ""
    Write-Host "  X  Both instances name $Path in ConfigStore:Path but $problem. Both app pools will refuse to start on it (the configured path is opened without create)." -ForegroundColor Red
    Write-Host "     Restore the shared file from the newest verified backup under $BackupRoot (Copy-SqliteDbFile -SourceDbPath <backup> -DestDbPath '$Path' -AppPath '$DevPublishPath'), or return both instances to two databases:" -ForegroundColor Yellow
    Write-RestoreInstructions -DevDb (Join-Path $DevConfigDir 'exchangeadmin.db') -ProdDb (Join-Path $ProdConfigDir 'exchangeadmin.db') `
        -DevBackup $(if ($devBackup) { $devBackup.FullName } else { Join-Path $BackupRoot 'cutover.<timestamp>\dev.exchangeadmin.<timestamp>.db' }) `
        -ProdBackup $(if ($prodBackup) { $prodBackup.FullName } else { Join-Path $BackupRoot 'cutover.<timestamp>\prod.exchangeadmin.<timestamp>.db' }) `
        -DevAppSettings $DevAppSettings -ProdAppSettings $ProdAppSettings -Suffix $suffix
    Write-Fail "Both instances name $Path in ConfigStore:Path but $problem. Nothing was changed; see the restore commands above."
}

# --- Preconditions (read-only) --------------------------------------------------------------

Assert-LocalAbsolutePath -Path $SharedDbPath -Name 'SharedDbPath'
# cdt-38: one spelling from here on, the installer's idiom. A Local value is fully qualified, so
# GetFullPath consults no current directory, and it turns / into \ under .NET Framework and .NET
# alike: the banner, the comparisons below, the VACUUM INTO, both ConfigStore:Path writes and the
# restore commands all carry the spelling the read side returns.
$SharedDbPath = [System.IO.Path]::GetFullPath($SharedDbPath)
foreach ($p in @(@{ N = 'DevPublishPath'; V = $DevPublishPath }, @{ N = 'ProdPublishPath'; V = $ProdPublishPath })) {
    if (-not (Test-Path -LiteralPath $p.V -PathType Container)) { Write-Fail "$($p.N) does not exist or is not a directory: $($p.V)" }
}
if ($DevPublishPath.TrimEnd('\', '/') -ieq $ProdPublishPath.TrimEnd('\', '/')) {
    Write-Fail "DevPublishPath and ProdPublishPath are the same directory."
}

# A39. The shared database must live outside BOTH publish folders - the architectural
# invariant says so, and two separate things go wrong when it does not:
#
#   - Outside config\, a deploy mirrors the tree with robocopy /MIR, which deletes whatever
#     it does not find in the source. The next deploy of that instance destroys the shared
#     database, and with it every other instance's configuration.
#   - Inside config\, today's deploy exclusions happen to spare it, but it is then one
#     instance's private directory: the other instance depends on a folder that an uninstall
#     or a clean reinstall of the first one removes. Relying on a deploy script's exclusion
#     list to protect shared state is not a guarantee, it is a coincidence that holds.
#
# Refuse the whole tree. The distinction above is why the message names both reasons.
foreach ($tree in @(
        @{ N = 'DevPublishPath'; V = $DevPublishPath },
        @{ N = 'ProdPublishPath'; V = $ProdPublishPath })) {
    if (Test-PathIsInside -Path $SharedDbPath -Directory $tree.V) {
        Write-Fail ("SharedDbPath ($SharedDbPath) is inside $($tree.N) ($($tree.V)). " +
            "A publish folder is mirrored by deploys (robocopy /MIR deletes what is not in the source) " +
            "and belongs to one instance's lifecycle, so shared state cannot live there. " +
            "Use a path outside both publish folders, for example D:\inetpub\ExchangeAdminWebShared\config\exchangeadmin.db. " +
            "Nothing was changed.")
    }
}

$devAppSettings = Join-Path $DevPublishPath 'appsettings.json'
$prodAppSettings = Join-Path $ProdPublishPath 'appsettings.json'
$devConfigDir = Join-Path $DevPublishPath 'config'
$prodConfigDir = Join-Path $ProdPublishPath 'config'
$devDb = Resolve-ConfigDbPath -PublishPath $DevPublishPath
$prodDb = Resolve-ConfigDbPath -PublishPath $ProdPublishPath
$sharedDir = Split-Path -Parent $SharedDbPath
$timestamp = Get-Date -Format 'yyyyMMddHHmmss'
$backupDir = Join-Path $BackupRoot "cutover.$timestamp"
$devBackupFile = Join-Path $backupDir "dev.exchangeadmin.$timestamp.db"
$prodBackupFile = Join-Path $backupDir "prod.exchangeadmin.$timestamp.db"
$appsettingsBackupSuffix = "pre-shared.$timestamp.bak"
# Every backup, copy and integrity check here runs the app's db mode from the deployed dev
# build, through the .NET host in Program Files (docs/ConfigDbToolInApp-Plan.md).
$configDbToolPlan = Resolve-ConfigDbTool -AppPath $DevPublishPath -PlanOnly

Write-Host ""
Write-Host "ExchangeAdminWeb shared config database cutover" -ForegroundColor Magenta
Write-Host "  Dev       : $DevPublishPath ($DevAppPoolName)  DB now: $devDb" -ForegroundColor DarkGray
Write-Host "  Prod      : $ProdPublishPath ($ProdAppPoolName)  DB now: $prodDb" -ForegroundColor DarkGray
Write-Host "  Shared DB : $SharedDbPath" -ForegroundColor DarkGray
Write-Host "  Backups   : $backupDir" -ForegroundColor DarkGray
Write-Host "  Min build : $MinimumAppVersion" -ForegroundColor DarkGray
Write-Host "  Mode      : $(if ($PlanOnly) { 'PLAN ONLY' } else { 'APPLY' })" -ForegroundColor DarkGray
Write-Host ""

if ($devDb -ieq $SharedDbPath -and $prodDb -ieq $SharedDbPath) {
    # The no-op shortcut sits BEHIND the invariant it claims (scdi-3): a missing or corrupt
    # configured file is a failure to report, in plan mode too, never "nothing to do".
    Assert-SharedDbUsable -Path $SharedDbPath -DevConfigDir $devConfigDir -ProdConfigDir $prodConfigDir -DevAppSettings $devAppSettings -ProdAppSettings $prodAppSettings
    Write-Ok "Both instances already name $SharedDbPath in ConfigStore:Path and the file passes PRAGMA integrity_check - nothing to do."
    return
}
if ($devDb -ieq $SharedDbPath -or $prodDb -ieq $SharedDbPath) {
    Write-Fail "One instance already points at $SharedDbPath and the other does not (split state). Finish or revert that by hand before running this script; see the restore commands in the script help."
}
if (-not (Test-Path -LiteralPath $devDb -PathType Leaf)) {
    Write-Fail "Dev has no config database at $devDb - there is nothing to make shared. Start dev once with the current build first."
}
if (Test-Path -LiteralPath $SharedDbPath -PathType Leaf) {
    Write-Fail "A file already exists at $SharedDbPath. This script only creates the shared database; remove or rename the existing file deliberately first."
}

# Step 1 - both builds tolerate a newer database (read-only, runs in plan mode too).
Assert-BuildTolerates -Label 'Dev' -PublishPath $DevPublishPath
Assert-BuildTolerates -Label 'Prod' -PublishPath $ProdPublishPath

# Step 1b - confirm IIS serves each publish folder from the pool named for it (aud-a38g), then
# resolve both app pool identities, BEFORE anything is stopped.
#
# On 2026-09-08 the identity read lived inside step 5, so it ran after both pools were already
# stopped; it threw, and the failure left both sites down behind a half-finished cutover. Both
# are pure reads, so they belong here where a bad lookup costs nothing.
#
# Plan mode attempts the same reads whenever it can, because a dry run that cannot look at what
# it is about to change is not a dry run - that is exactly why the 2026-09-08 plan came back
# clean. But plan mode must stay runnable without IIS or elevation (that is how the Pester
# suite and CI exercise it), so there it degrades to a warning instead of a failure. Apply mode
# demands all of it.
$poolIdentities = @()
$identityLabel = 'both app pool identities'

if ($PlanOnly) {
    # Printed whatever happens next, so every plan shows the check and the rule the cutover applies
    # (aud-a38j: Allow, where the promotion's dry-run line says Refuse).
    Write-Plan "Assert-AppPoolServesPath -Name $DevAppPoolName -PhysicalPath $DevPublishPath -NestedNodes Allow (refuse unless IIS serves that folder, and everything rooted inside it, from this pool and no other)"
    Write-Plan "Assert-AppPoolServesPath -Name $ProdAppPoolName -PhysicalPath $ProdPublishPath -NestedNodes Allow (refuse unless IIS serves that folder, and everything rooted inside it, from this pool and no other)"
    $iisReason = $null
    if (-not (Test-IsAdministrator)) {
        $iisReason = 'this session is not elevated'
    } elseif (-not (Get-Module -ListAvailable -Name WebAdministration)) {
        $iisReason = 'the WebAdministration module is not installed on this host'
    }

    if ($iisReason) {
        Write-Warn "Skipping the IIS checks (which app pool serves each publish folder, and the app pool identities): $iisReason. Run the plan elevated on the deploy host to have them verified."
    } else {
        try {
            Import-Module WebAdministration -ErrorAction Stop
            # aud-a38g: read-only, like the identity read; a refusal becomes the warning below.
            Assert-PoolServesInstance -Label 'Dev' -Name $DevAppPoolName -PublishPath $DevPublishPath -PoolParameter 'DevAppPoolName'
            Assert-PoolServesInstance -Label 'Prod' -Name $ProdAppPoolName -PublishPath $ProdPublishPath -PoolParameter 'ProdAppPoolName'
            $poolIdentities = @(
                (Get-AppPoolIdentity -Name $DevAppPoolName),
                (Get-AppPoolIdentity -Name $ProdAppPoolName)
            ) | Select-Object -Unique
            # The plan line keeps its generic wording so the printed plan does not depend on
            # which host it was rendered on; the resolved names are reported here instead.
            Write-Ok "App pool identities resolved: $($poolIdentities -join ' and ')"
        } catch {
            Write-Warn "Could not confirm the IIS app pools: $($_.Exception.Message)"
            Write-Warn "-Apply would FAIL here. Fix this before running it."
            $poolIdentities = @()
        }
    }
} else {
    if (-not (Test-IsAdministrator)) {
        Write-Fail "Run this script from an elevated PowerShell session when using -Apply."
    }
    Import-Module WebAdministration -ErrorAction Stop
    Resolve-ConfigDbTool -AppPath $DevPublishPath | Out-Null

    # aud-a38g: inside no try. A refusal is a terminating throw here, ahead of the try that holds
    # the stops, so nothing has been stopped or copied and there is nothing for its catch to undo.
    Assert-PoolServesInstance -Label 'Dev' -Name $DevAppPoolName -PublishPath $DevPublishPath -PoolParameter 'DevAppPoolName'
    Assert-PoolServesInstance -Label 'Prod' -Name $ProdAppPoolName -PublishPath $ProdPublishPath -PoolParameter 'ProdAppPoolName'

    $poolIdentities = @(
        (Get-AppPoolIdentity -Name $DevAppPoolName),
        (Get-AppPoolIdentity -Name $ProdAppPoolName)
    ) | Select-Object -Unique
    $identityLabel = $poolIdentities -join ' and '
    Write-Ok "App pool identities resolved: $identityLabel"
}

# --- Steps 2-8 -------------------------------------------------------------------------------

$sharedWritten = $false
$appsettingsTouched = @()
$startedAt = Get-Date
try {
    # Step 2 - stop both pools.
    Stop-AppPoolChecked -Name $DevAppPoolName -ConfigDir $devConfigDir -AppPath $DevPublishPath
    Stop-AppPoolChecked -Name $ProdAppPoolName -ConfigDir $prodConfigDir -AppPath $DevPublishPath

    # Step 3 - verified backups of BOTH current databases.
    Invoke-PlanOrAction "Verified backup of dev DB $devDb to $devBackupFile via $($configDbToolPlan.Summary) backup" {
        New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
        $b = Backup-SqliteConfigDb -DbPath $devDb -DestDir $backupDir -Timestamp "dev.$timestamp" -AppPath $DevPublishPath
        if (-not $b) { Write-Fail "Dev database vanished at $devDb" }
        Move-Item -LiteralPath $b -Destination $devBackupFile -Force
        Write-Ok "Dev DB backed up (verified) to $devBackupFile"
    }
    Invoke-PlanOrAction "Verified backup of prod DB $prodDb to $prodBackupFile (skipped if prod has no DB yet) via $($configDbToolPlan.Summary) backup" {
        if (Test-Path -LiteralPath $prodDb -PathType Leaf) {
            $b = Backup-SqliteConfigDb -DbPath $prodDb -DestDir $backupDir -Timestamp "prod.$timestamp" -AppPath $DevPublishPath
            Move-Item -LiteralPath $b -Destination $prodBackupFile -Force
            Write-Ok "Prod DB backed up (verified) to $prodBackupFile"
        } else {
            Write-Warn "Prod has no config DB at $prodDb - nothing to back up there"
        }
    }

    # Step 4 - dev's database becomes the shared one.
    Invoke-PlanOrAction "VACUUM INTO dev DB $devDb -> $SharedDbPath, then PRAGMA integrity_check, via $($configDbToolPlan.Summary) backup" {
        Copy-SqliteDbFile -SourceDbPath $devDb -DestDbPath $SharedDbPath -AppPath $DevPublishPath | Out-Null
        Test-SqliteConfigDbIntegrity -DbPath $SharedDbPath -AppPath $DevPublishPath | Out-Null
        Write-Ok "Shared database written and verified at $SharedDbPath"
    }
    if (-not $PlanOnly) { $sharedWritten = $true }

    # Step 5 - both pool identities get Modify on the shared directory (WAL/SHM need write).
    Invoke-PlanOrAction "Grant $identityLabel (OI)(CI)M on $sharedDir" {
        if ($poolIdentities.Count -eq 0) {
            Write-Fail "No app pool identity was resolved; refusing to leave the shared directory ungranted."
        }
        foreach ($identity in $poolIdentities) {
            & icacls $sharedDir /grant "${identity}:(OI)(CI)M" | Out-Null
            if ($LASTEXITCODE -ne 0) {
                Write-Fail "icacls failed (exit $LASTEXITCODE) granting $identity (OI)(CI)M on $sharedDir"
            }
            Write-Ok "Granted $identity (OI)(CI)M on $sharedDir"
        }
    }

    # Step 6 - both appsettings.json files name the shared file.
    Set-ConfigStorePathInAppsettings -AppSettingsPath $devAppSettings -Value $SharedDbPath -BackupSuffix $appsettingsBackupSuffix
    if (-not $PlanOnly) { $appsettingsTouched += 'dev' }
    Set-ConfigStorePathInAppsettings -AppSettingsPath $prodAppSettings -Value $SharedDbPath -BackupSuffix $appsettingsBackupSuffix
    if (-not $PlanOnly) { $appsettingsTouched += 'prod' }

    # Step 7 - start both pools.
    Start-AppPoolChecked -Name $DevAppPoolName
    Start-AppPoolChecked -Name $ProdAppPoolName

    # Step 8 - each instance opened the shared database.
    Assert-SchemaReadyLogged -Label 'Dev' -PublishPath $DevPublishPath -Since $startedAt
    Assert-SchemaReadyLogged -Label 'Prod' -PublishPath $ProdPublishPath -Since $startedAt
} catch {
    Write-Host ""
    Write-Host "  X  Cutover FAILED: $_" -ForegroundColor Red
    if ($appsettingsTouched.Count -eq 0) {
        # Neither instance has been repointed, so both are still exactly as they were: their own
        # appsettings.json, their own database. The only damage is that they are STOPPED, and
        # leaving them that way is a self-inflicted outage - on 2026-09-08 a failure at step 5
        # took both sites down and kept them down. Bring them back before reporting anything.
        if (-not $PlanOnly) {
            Write-Warn "Both instances are unchanged; restarting their app pools so the outage ends here."
            foreach ($pool in @($DevAppPoolName, $ProdAppPoolName)) {
                try {
                    Start-AppPoolChecked -Name $pool
                } catch {
                    Write-Warn "Could not restart app pool ${pool}: $($_.Exception.Message). Start it by hand: Start-WebAppPool -Name $pool"
                }
            }
        }
        if ($sharedWritten) {
            Write-Warn "An orphan shared database was left at $SharedDbPath. Nothing points at it. Delete it before re-running this script, which refuses to start when that path already holds a file."
        }
        Write-Warn "Nothing else was changed: both appsettings.json files and both databases are untouched."
    } else {
        if ($appsettingsTouched.Count -eq 1) {
            Write-Warn "SPLIT STATE: the $($appsettingsTouched[0]) instance now names $SharedDbPath and the other still uses its own database. Revert with the commands below (the appsettings backups carry the '.$appsettingsBackupSuffix' suffix)."
        }
        Write-RestoreInstructions -DevDb $devDb -ProdDb $prodDb -DevBackup $devBackupFile -ProdBackup $prodBackupFile -DevAppSettings $devAppSettings -ProdAppSettings $prodAppSettings -Suffix $appsettingsBackupSuffix
    }
    throw
}

Write-Host ""
if ($PlanOnly) {
    Write-Warn "Plan only - nothing was changed. Re-run with -Apply after reviewing the steps above."
} else {
    Write-Ok "Cutover complete. Both instances open $SharedDbPath. Pre-cutover backups: $backupDir"
    Write-Host "The per-instance databases at $devDb and $prodDb are no longer opened by the app; keep them until you are satisfied, then delete them so nobody mistakes them for live." -ForegroundColor DarkGray
}
Write-RestoreInstructions -DevDb $devDb -ProdDb $prodDb -DevBackup $devBackupFile -ProdBackup $prodBackupFile -DevAppSettings $devAppSettings -ProdAppSettings $prodAppSettings -Suffix $appsettingsBackupSuffix
