<#
.SYNOPSIS
    Consistent backup + integrity verification for the SQLite runtime config DB.

.DESCRIPTION
    Shared by deploy.ps1, promote-dev-to-prod.ps1 and Move-ConfigDbToShared.ps1
    (SqliteConfigStore-Plan Phase D; SharedConfigDb-Plan). The config DB is a live SQLite
    database; a raw file copy of a live WAL database can be torn/inconsistent, so a pre-deploy
    "backup" that just copies the file could produce a rollback snapshot that is itself corrupt.

    WHERE THE DATABASE LIVES (SharedConfigDb-Plan AC5, decision 2026-09-04): when an instance's
    appsettings.json names ConfigStore:Path, that is the database - one file shared by the dev
    and prod instances on this server. Without the key, the database is the instance's own
    <PublishPath>\config\exchangeadmin.db. Resolve-ConfigDbPath does that lookup; every
    function here takes an explicit -DbPath so callers never re-derive it.

    THE TOOL (docs/ConfigDbToolInApp-Plan.md, finding aud-sqlite3-path): the backup, the
    integrity check and the copy run the app's own command-line mode,
    `<ProgramFiles>\dotnet\dotnet.exe <AppPath>\ExchangeAdminWeb.dll db <verb>`. The .NET host
    is taken from Program Files, which only administrators can write, and never searched for
    on PATH; the assembly is the build the caller is about to deploy or already runs, so no
    new principal can put code in this path. sqlite3.exe is not used: an elevated deploy used
    to run whatever sqlite3.exe PATH resolved to first, and on the deploy host that was a
    user-writable, unsigned copy. Backups are still a true online backup ('VACUUM INTO')
    followed by 'PRAGMA integrity_check', done inside the app, which works while the app is
    running. If the tool is missing the backup THROWS rather than silently degrading to an
    unverified file copy - a deploy must not proceed without a verifiable rollback snapshot.

    Exit codes are the only success signal trusted here: 0 ok, 1 usage (an app that does not
    know the verb), 2 failure, anything else a crash. stdout is read only after exit 0.

    On an integrity-check FAILURE the function THROWS (owner decision 2026-06-18: abort the
    deploy rather than continue over a corrupt store with a worthless rollback snapshot).

    This module is imported by deploy.ps1 under Windows PowerShell 5.1: keep it pure ASCII and
    5.1-compatible (no ternary, no null-coalescing, no PS7-only cmdlet parameters).
#>

Set-StrictMode -Version Latest

# The first app version with the db command-line mode (ConfigDbToolInApp-Plan S1).
$script:ConfigDbToolMinimumVersion = [version]'2.34.0'

<#
.SYNOPSIS
    The .NET host and app assembly that run the config DB tool for one app folder.
.PARAMETER AppPath
    The folder holding ExchangeAdminWeb.dll: the staging folder a deploy is about to mirror, or
    the publish folder of the build already running.
.PARAMETER PlanOnly
    Return the intended paths without requiring them to exist (a plan runs before publish, so
    the staging folder is not there yet). Missing lists what is absent; Summary is a printable
    line that says so.
.OUTPUTS
    A hashtable: Host, Assembly, Missing, Summary. In action mode a missing file, or an app too
    old to have the db mode, is a throw naming it.
#>
function Resolve-ConfigDbTool {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$AppPath,
        [switch]$PlanOnly
    )

    $programFiles = $env:ProgramFiles
    if ([string]::IsNullOrWhiteSpace($programFiles)) {
        throw "The ProgramFiles environment variable is not set, so the .NET host cannot be located. Nothing was changed."
    }

    # Program Files only, never PATH: the PATH search is the finding this replaces.
    $dotnetHost = Join-Path (Join-Path $programFiles 'dotnet') 'dotnet.exe'
    $assembly = Join-Path $AppPath 'ExchangeAdminWeb.dll'

    $missing = @()
    if (-not (Test-Path -LiteralPath $dotnetHost -PathType Leaf)) { $missing += $dotnetHost }
    if (-not (Test-Path -LiteralPath $assembly -PathType Leaf)) { $missing += $assembly }

    $summary = "$dotnetHost $assembly db"
    if ($missing.Count -gt 0) { $summary += " (not present yet: $($missing -join ', '))" }

    $tool = @{
        Host     = $dotnetHost
        Assembly = $assembly
        Missing  = $missing
        Summary  = $summary
    }

    if ($PlanOnly) { return $tool }

    if ($missing.Count -gt 0) {
        throw "The config DB tool is missing: $($missing -join ' and ') not found. The backup runs the app's own db mode through the .NET host in Program Files; nothing else is searched. Nothing was changed."
    }

    # Checked BEFORE anything runs. An app that predates the db mode does not answer
    # "db backup ..." with a usage error: its Program.cs hands the arguments to the web host
    # builder, whose command-line configuration skips bare words, so it would try to start the
    # app instead of failing (inferred from the provider's behaviour; an old build was not run).
    # The file version is read without executing anything.
    $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($assembly)
    $version = New-Object System.Version -ArgumentList $info.FileMajorPart, $info.FileMinorPart, $info.FileBuildPart
    if ($version -lt $script:ConfigDbToolMinimumVersion) {
        throw "The app at $assembly is version $version, older than the backup tool (db mode needs $($script:ConfigDbToolMinimumVersion) or later): the deployed app is older than the backup tool; deploy dev first. Nothing was changed."
    }

    return $tool
}

function Invoke-ConfigDbTool {
    # Private. Runs one db verb and returns its exit code with stdout and stderr split.
    param(
        [Parameter(Mandatory)][hashtable]$Tool,
        [Parameter(Mandatory)][string[]]$Arguments
    )

    # Local to this function. Under Windows PowerShell 5.1 with the callers' "Stop", the first
    # stderr line of a native command redirected with 2>&1 becomes a terminating error and the
    # exit code is lost. The exit code is the contract, so read it first and decide after.
    $ErrorActionPreference = 'Continue'
    $stdout = @()
    $stderr = @()
    $output = & $Tool.Host $Tool.Assembly 'db' @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    foreach ($line in @($output)) {
        if ($line -is [System.Management.Automation.ErrorRecord]) {
            $stderr += [string]$line.Exception.Message
        } elseif ($null -ne $line) {
            $stdout += [string]$line
        }
    }

    return [pscustomobject]@{
        ExitCode = $exitCode
        StdOut   = $stdout
        StdErr   = $stderr
    }
}

function Assert-ConfigDbToolSucceeded {
    # Private. Exit 0 returns; every other code throws. Exit 1 is the CLI's usage error, which
    # from a script means the app does not know the verb it was given.
    param(
        [Parameter(Mandatory)]$Result,
        [Parameter(Mandatory)][hashtable]$Tool,
        [Parameter(Mandatory)][string]$FailureMessage
    )

    $detail = (@($Result.StdErr) + @($Result.StdOut)) -join ' '
    if ($Result.ExitCode -eq 0) { return }
    if ($Result.ExitCode -eq 1) {
        throw "The app at $($Tool.Assembly) did not accept the db command (exit 1): the deployed app is older than the backup tool; deploy dev first. Tool output: $detail"
    }
    throw "$FailureMessage (db tool exit $($Result.ExitCode)): $detail"
}

<#
.SYNOPSIS
    Returns the ConfigStore:Path value from <PublishPath>\appsettings.json, or $null when the
    file or the key is absent or blank.
.DESCRIPTION
    Callers use this to tell "the key is set" (the named file MUST exist - a missing shared
    database is a deploy-stopping error, never something to create or skip) from "no key" (the
    per-instance default, where a missing database just means a first start has not happened).
    Strict-mode safe: never dereferences a property that is not there.

    A set value must be an absolute local path (cdt-17): anything else THROWS, naming the value and
    the file, so no caller tests for, backs up or checks a file the app would never open. An
    accepted value comes back with its forward slashes turned into backslashes (cdt-30), so every
    caller tests for, backs up and compares one spelling of the file the app opens.
#>
function Get-ConfigStorePathSetting {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$PublishPath)

    $appsettings = Join-Path $PublishPath 'appsettings.json'
    if (-not (Test-Path -LiteralPath $appsettings -PathType Leaf)) {
        return $null
    }

    $json = Get-Content -LiteralPath $appsettings -Raw | ConvertFrom-Json
    if ($null -eq $json) { return $null }

    $section = $json.PSObject.Properties['ConfigStore']
    if ($null -eq $section -or $null -eq $section.Value) { return $null }

    $pathProperty = $section.Value.PSObject.Properties['Path']
    if ($null -eq $pathProperty) { return $null }

    $value = [string]$pathProperty.Value
    if ([string]::IsNullOrWhiteSpace($value)) { return $null }
    $value = $value.Trim()
    Assert-ConfigStorePathValue -Value $value -AppSettingsPath $appsettings
    return $value.Replace('/', '\')
}

function Assert-ConfigStorePathValue {
    # Private (cdt-17, cdt-30, cdt-35). The app's ConfigStorePath.Resolve refuses anything but a local
    # drive-letter path - a network path in any spelling, and a relative, drive-relative (D:file.db) or
    # driveless rooted path - so it will not start on one. This module used to return such a value,
    # and every caller then tested for, backed up and checked whatever it named relative to its own
    # current directory: a verified backup of the wrong file, then an outage.
    #
    # TRIPWIRE: this mirrors Services/Storage/ConfigStorePath.cs (Resolve), and the two rules are
    # identical (cdt-35). Change one, change the other: tests/ps/SqliteConfigBackup.Tests.ps1 pins
    # that method's rule in its source and holds this module to it, and
    # ExchangeAdminWeb.Tests/ConfigStorePathTests.cs pins these lines and holds the method to them.
    # The rule: the trim (Get-ConfigStorePathSetting); any two leading separators refused as a
    # network share (Windows reads \\, //, \/ and /\ alike as a UNC or device path); then only an
    # ASCII letter (the app's char.IsAsciiLetter), a colon, then \ or / - matched case-SENSITIVELY,
    # because a case-insensitive match folds the Kelvin sign (U+212A) onto k - so \??\UNC\..., which
    # names a share, fails with every other form; and the value returned with / turned into \. The
    # two writers' own checks (the installer's -ConfigStorePath, the cutover's -SharedDbPath) accept
    # only X:\, which this reads back. [IO.Path]::IsPathFullyQualified does not exist on .NET
    # Framework (Windows PowerShell 5.1), hence the explicit tests.
    param([string]$Value, [string]$AppSettingsPath)

    if ($Value -match '^[\\/][\\/]') {
        throw "ConfigStore:Path in $AppSettingsPath must be a local path, not a network share: $Value. SQLite's locking is not reliable over a network share; fix the key in that file. Nothing was changed."
    }
    if ($Value -cnotmatch '^[A-Za-z]:[\\/]') {
        throw "ConfigStore:Path in $AppSettingsPath must be an absolute local file path (a drive letter, a colon, then \ or / - for example D:\inetpub\ExchangeAdminWebShared\config\exchangeadmin.db): $Value. The app refuses a relative or drive-relative path at startup, and these scripts read only the drive-letter form; fix the key in that file. Nothing was changed."
    }
}

<#
.SYNOPSIS
    The config database an instance opens: ConfigStore:Path from its appsettings.json when set,
    else <PublishPath>\config\exchangeadmin.db.
#>
function Resolve-ConfigDbPath {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$PublishPath)

    $configured = Get-ConfigStorePathSetting -PublishPath $PublishPath
    if ($configured) { return $configured }
    return (Join-Path (Join-Path $PublishPath 'config') 'exchangeadmin.db')
}

function Resolve-DbPathArgument {
    # Shared by the functions that accept either -DbPath or the older -ConfigDir form.
    param([string]$DbPath, [string]$ConfigDir)

    if ($DbPath) { return $DbPath }
    if ($ConfigDir) { return (Join-Path $ConfigDir 'exchangeadmin.db') }
    throw "Specify -DbPath (the database file) or -ConfigDir (a directory containing exchangeadmin.db)."
}

function Test-IsSqliteConfigDbPresent {
    param(
        [string]$ConfigDir,
        [string]$DbPath
    )
    $path = Resolve-DbPathArgument -DbPath $DbPath -ConfigDir $ConfigDir
    return (Test-Path -LiteralPath $path -PathType Leaf)
}

<#
.SYNOPSIS
    Backs up the config DB to $DestDir via a verified online backup.
.PARAMETER DbPath
    The database file to back up (the resolved shared or per-instance path). Preferred.
.PARAMETER ConfigDir
    Older form: the runtime config directory containing exchangeadmin.db.
.PARAMETER DestDir
    Directory to write the backup into (created if missing).
.PARAMETER Timestamp
    Caller's deploy timestamp, used in the backup file name.
.OUTPUTS
    The path to the backup .db file, or $null if there was no DB to back up. Whether a missing
    file is acceptable is the CALLER's decision (no key: nothing to back up yet; key set: stop).
#>
function Backup-SqliteConfigDb {
    [CmdletBinding()]
    param(
        [string]$ConfigDir,
        [string]$DbPath,
        [Parameter(Mandatory)][string]$DestDir,
        [Parameter(Mandatory)][string]$Timestamp,
        [Parameter(Mandatory)][string]$AppPath
    )

    $dbPath = Resolve-DbPathArgument -DbPath $DbPath -ConfigDir $ConfigDir
    if (-not (Test-Path -LiteralPath $dbPath -PathType Leaf)) {
        return $null
    }

    $tool = Resolve-ConfigDbTool -AppPath $AppPath

    if (-not (Test-Path -LiteralPath $DestDir)) {
        New-Item -ItemType Directory -Path $DestDir -Force | Out-Null
    }

    $backupPath = Join-Path $DestDir "exchangeadmin.${Timestamp}.db"

    $result = Invoke-ConfigDbTool -Tool $tool -Arguments @('backup', '--source', $dbPath, '--dest', $backupPath)
    Assert-ConfigDbToolSucceeded -Result $result -Tool $tool -FailureMessage "Config DB backup FAILED for $dbPath. Aborting before any changes - the live config DB may be corrupt or unreadable. Investigate before deploying"
    return $backupPath
}

<#
.SYNOPSIS
    Writes a consistent, integrity-verified copy of one SQLite database file to another path.

.DESCRIPTION
    Used by the one-time cutover (Move-ConfigDbToShared.ps1) to seed the shared database from
    dev's, and by an operator returning to per-instance databases. Promotion no longer copies
    the config database (decision 2026-09-04).

    Uses 'VACUUM INTO' to write a fresh consistent snapshot of the SOURCE onto a temp file next
    to the destination, integrity-checks it, then moves it into place. Any WAL/SHM sidecars of a
    destination being replaced are removed (the fresh DB has no pending WAL). The CALLER is
    responsible for backing up an existing destination first and for stopping any process that
    has it open.

.PARAMETER SourceDbPath
    The database file to copy.
.PARAMETER DestDbPath
    The file to write. Its directory is created if missing.
.OUTPUTS
    The destination path on success. Throws if the source is absent or the copy fails integrity.
#>
function Copy-SqliteDbFile {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)][string]$SourceDbPath,
        [Parameter(Mandatory)][string]$DestDbPath,
        [Parameter(Mandatory)][string]$AppPath
    )

    if (-not (Test-Path -LiteralPath $SourceDbPath -PathType Leaf)) {
        throw "Source database not found at $SourceDbPath - cannot copy it to $DestDbPath."
    }

    $tool = Resolve-ConfigDbTool -AppPath $AppPath

    $destDir = Split-Path -Parent $DestDbPath

    if (-not $PSCmdlet.ShouldProcess($DestDbPath, "Replace with consistent copy of $SourceDbPath")) {
        return $DestDbPath
    }

    # Creating the destination directory is an ACTION, so it sits below the gate.
    #
    # It used to sit above it. The 2026-10-16 audit read that as "-WhatIf creates a
    # directory before ShouldProcess", and that is NOT what happened: New-Item declares
    # SupportsShouldProcess itself and inherits $WhatIfPreference from this function, so a
    # -WhatIf run printed "What if: Create Directory" and created nothing. Verified by
    # running the two tests below against the unmodified module - both passed.
    #
    # The move is kept anyway, as hardening rather than a fix: the guarantee now holds
    # because of where the call is, not because of what New-Item happens to support.
    # Swapping it for [IO.Directory]::CreateDirectory, which honours nothing, would have
    # made the audit right.
    if ($destDir -and -not (Test-Path -LiteralPath $destDir)) {
        New-Item -ItemType Directory -Path $destDir -Force | Out-Null
    }

    $tmpDb = Join-Path $destDir ("exchangeadmin.copy.{0}.db" -f ([guid]::NewGuid().ToString('N')))
    try {
        $result = Invoke-ConfigDbTool -Tool $tool -Arguments @('backup', '--source', $SourceDbPath, '--dest', $tmpDb)
        Assert-ConfigDbToolSucceeded -Result $result -Tool $tool -FailureMessage "Copying $SourceDbPath failed (the copy did not pass integrity_check or could not be written). Destination NOT changed"

        foreach ($suffix in '-wal', '-shm') {
            $side = "$DestDbPath$suffix"
            if (Test-Path -LiteralPath $side -PathType Leaf) { Remove-Item -LiteralPath $side -Force }
        }

        Move-Item -LiteralPath $tmpDb -Destination $DestDbPath -Force
        return $DestDbPath
    } finally {
        if (Test-Path -LiteralPath $tmpDb -PathType Leaf) {
            Remove-Item -LiteralPath $tmpDb -Force -ErrorAction SilentlyContinue
        }
    }
}

<#
.SYNOPSIS
    Runs PRAGMA integrity_check against a DB through the app's db mode; throws on a fail.
#>
function Test-SqliteConfigDbIntegrity {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$DbPath,
        [Parameter(Mandatory)][string]$AppPath
    )

    if (-not (Test-Path -LiteralPath $DbPath -PathType Leaf)) {
        throw "Config DB not found at $DbPath"
    }

    $tool = Resolve-ConfigDbTool -AppPath $AppPath
    $result = Invoke-ConfigDbTool -Tool $tool -Arguments @('check', $DbPath)
    Assert-ConfigDbToolSucceeded -Result $result -Tool $tool -FailureMessage "Config DB integrity check FAILED on $DbPath (PRAGMA integrity_check)"
    return $true
}

Export-ModuleMember -Function Resolve-ConfigDbTool, Get-ConfigStorePathSetting, Resolve-ConfigDbPath, Test-IsSqliteConfigDbPresent, Backup-SqliteConfigDb, Copy-SqliteDbFile, Test-SqliteConfigDbIntegrity
