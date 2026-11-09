<#
Shared helper for the Pester suites that drive the app's db mode (docs/ConfigDbToolInApp-Plan.md
section 3.3). Dot-source it inside BeforeAll.

Get-BuiltApp returns a folder holding the build output with ExchangeAdminWeb.dll: bin/Release/<tfm>/
first, then bin/Debug/<tfm>/. When neither exists it builds the app once, in Release.

Sweep item 12: the scripts refuse to run the db tool from a folder that anyone but the administrators,
SYSTEM, TrustedInstaller and the current account can change (tools/ToolFolderAcl.psm1). A checkout's
bin folder inherits whatever its drive grants - on the dev host, Authenticated Users Modify - so the
suites run the tool from a copy instead: a folder under the temp directory with inheritance removed and
only those principals granted (Get-TightAppCopy). The copy is refreshed when the build changes.

The suites never use the sqlite3 shell. Databases are created with the tool's own `init` verb and
filled through Microsoft.Data.Sqlite loaded from the same build output, so the engine that writes a
fixture is the engine the app ships.
#>

$script:BuiltAppTfm = 'net10.0-windows10.0.17763.0'

function Initialize-TightTestFolder {
    # A new, empty folder whose permissions pass tools/ToolFolderAcl.psm1: inheritance removed, and
    # only SYSTEM, the administrators and the current account granted full control.
    param([Parameter(Mandatory)][string]$Path)

    New-Item -ItemType Directory -Path $Path -Force | Out-Null
    $me = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    & icacls $Path /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' "*${me}:(OI)(CI)F" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls failed (exit $LASTEXITCODE) narrowing the test folder $Path." }
    return $Path
}

function Use-TempAncestryStandIn {
    # Finding rev-f1: the folder check reads every folder above the app folder, up to the volume
    # root, and refuses when anyone outside the trusted set can replace what is below it. The suites'
    # folders all sit under the temp directory, and the folders from the temp directory up are the
    # host's: on the dev host other accounts hold Modify there, so every real tool run here would be
    # refused for a reason that is not the test's. Call this inside a Pester block (BeforeAll): the
    # ACL reads of the temp directory and the folders above it are answered with an in-memory one
    # owned by SYSTEM that grants nothing. Every folder a test creates under the temp directory is
    # still read for real, so a test can still put a grant on a fixture's parent and see it refused.
    Import-Module (Join-Path $PSScriptRoot '..\..\tools\ToolFolderAcl.psm1')
    # Every other read goes to the real cmdlet: this Pester does not fall through to it when only
    # filtered mocks exist.
    Mock -ModuleName ToolFolderAcl Get-Acl -MockWith { Microsoft.PowerShell.Security\Get-Acl @PesterBoundParameters }
    Mock -ModuleName ToolFolderAcl Get-Acl -ParameterFilter {
        $temp = [System.IO.Path]::GetTempPath().TrimEnd('\')
        $asked = ([string]$LiteralPath).TrimEnd('\')
        $asked -and ($temp.Equals($asked, [System.StringComparison]::OrdinalIgnoreCase) -or
            $temp.StartsWith($asked + '\', [System.StringComparison]::OrdinalIgnoreCase))
    } -MockWith {
        $security = [System.Security.AccessControl.DirectorySecurity]::new()
        $security.SetOwner([System.Security.Principal.SecurityIdentifier]::new('S-1-5-18'))
        $security
    }
}

function Get-TightAppCopy {
    # The build output at $Source, copied into a Initialize-TightTestFolder under the temp directory. One
    # folder per source folder, reused while the build in it is unchanged.
    param([Parameter(Mandatory)][string]$Source)

    $dll = Get-Item -LiteralPath (Join-Path $Source 'ExchangeAdminWeb.dll')
    $stamp = "$Source|$($dll.LastWriteTimeUtc.Ticks)|$($dll.Length)"
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $key = -join ($sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($Source.ToLowerInvariant())) | Select-Object -First 8 | ForEach-Object { $_.ToString('x2') })
    } finally {
        $sha.Dispose()
    }
    $dest = Join-Path ([System.IO.Path]::GetTempPath()) "eaw-builtapp-$key"
    $marker = Join-Path $dest 'eaw-builtapp.stamp'
    if ((Test-Path -LiteralPath $marker -PathType Leaf) -and ((Get-Content -LiteralPath $marker -Raw).Trim() -eq $stamp)) {
        return $dest
    }
    if (Test-Path -LiteralPath $dest) { Remove-Item -LiteralPath $dest -Recurse -Force }
    Initialize-TightTestFolder -Path $dest | Out-Null
    Copy-Item -Path (Join-Path $Source '*') -Destination $dest -Recurse -Force
    Set-Content -LiteralPath $marker -Value $stamp -Encoding ascii
    return $dest
}

function Get-BuiltApp {
    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
    foreach ($configuration in 'Release', 'Debug') {
        $dir = Join-Path $repoRoot "bin\$configuration\$script:BuiltAppTfm"
        if (Test-Path -LiteralPath (Join-Path $dir 'ExchangeAdminWeb.dll') -PathType Leaf) {
            return (Get-TightAppCopy -Source $dir)
        }
    }

    Push-Location $repoRoot
    try {
        & dotnet build ExchangeAdminWeb.csproj -c Release --nologo | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "dotnet build ExchangeAdminWeb.csproj -c Release failed (exit $LASTEXITCODE)." }
    } finally {
        Pop-Location
    }

    $dir = Join-Path $repoRoot "bin\Release\$script:BuiltAppTfm"
    if (-not (Test-Path -LiteralPath (Join-Path $dir 'ExchangeAdminWeb.dll') -PathType Leaf)) {
        throw "The build succeeded but $dir holds no ExchangeAdminWeb.dll."
    }
    return (Get-TightAppCopy -Source $dir)
}

function Get-TestDotnetHost {
    return (Join-Path (Join-Path $env:ProgramFiles 'dotnet') 'dotnet.exe')
}

function Initialize-TestSqlite {
    param([Parameter(Mandatory)][string]$AppPath)

    if ('Microsoft.Data.Sqlite.SqliteConnection' -as [type]) { return }

    $native = Join-Path $AppPath 'runtimes\win-x64\native\e_sqlite3.dll'
    [void][System.Runtime.InteropServices.NativeLibrary]::Load($native)
    foreach ($name in 'SQLitePCLRaw.core.dll', 'SQLitePCLRaw.provider.e_sqlite3.dll', 'SQLitePCLRaw.batteries_v2.dll', 'Microsoft.Data.Sqlite.dll') {
        Add-Type -Path (Join-Path $AppPath $name)
    }
}

function New-TestDb {
    param(
        [Parameter(Mandatory)][string]$AppPath,
        [Parameter(Mandatory)][string]$Path,
        [string]$Sql
    )

    $out = & (Get-TestDotnetHost) (Join-Path $AppPath 'ExchangeAdminWeb.dll') db init $Path 2>&1
    if ($LASTEXITCODE -ne 0) { throw "db init $Path failed (exit $LASTEXITCODE): $out" }
    if ($Sql) { Invoke-TestSql -AppPath $AppPath -Path $Path -Sql $Sql }
    return $Path
}

function Open-TestConnection {
    param([Parameter(Mandatory)][string]$AppPath, [Parameter(Mandatory)][string]$Path)

    Initialize-TestSqlite -AppPath $AppPath
    $builder = New-Object Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
    $builder.DataSource = $Path
    $builder.Pooling = $false
    $connection = New-Object Microsoft.Data.Sqlite.SqliteConnection -ArgumentList $builder.ToString()
    $connection.Open()
    return $connection
}

function Invoke-TestSql {
    param(
        [Parameter(Mandatory)][string]$AppPath,
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Sql
    )

    $connection = Open-TestConnection -AppPath $AppPath -Path $Path
    try {
        $command = $connection.CreateCommand()
        $command.CommandText = $Sql
        [void]$command.ExecuteNonQuery()
    } finally {
        $connection.Dispose()
    }
}

function Invoke-TestSqlScalar {
    param(
        [Parameter(Mandatory)][string]$AppPath,
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Sql
    )

    $connection = Open-TestConnection -AppPath $AppPath -Path $Path
    try {
        $command = $connection.CreateCommand()
        $command.CommandText = $Sql
        return $command.ExecuteScalar()
    } finally {
        $connection.Dispose()
    }
}

function New-CorruptTestDb {
    # Same technique as ConfigDbTestFiles.CreateCorrupt in ExchangeAdminWeb.Tests: a table and an
    # index over it, then the index page's copies of the values are overwritten in place. The
    # file still opens, but the index no longer matches the table, so PRAGMA integrity_check
    # fails - and VACUUM INTO copies the index b-tree as it is, so a backup of it fails too.
    param(
        [Parameter(Mandatory)][string]$AppPath,
        [Parameter(Mandatory)][string]$Path
    )

    $rows = (0..39 | ForEach-Object { "INSERT INTO t (v) VALUES ('marker-{0:D4}');" -f $_ }) -join ' '
    New-TestDb -AppPath $AppPath -Path $Path -Sql "CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT NOT NULL); CREATE INDEX ix_t_v ON t (v); $rows" | Out-Null
    $indexRoot = [long](Invoke-TestSqlScalar -AppPath $AppPath -Path $Path -Sql "SELECT rootpage FROM sqlite_master WHERE name = 'ix_t_v';")
    $pageSize = [long](Invoke-TestSqlScalar -AppPath $AppPath -Path $Path -Sql 'PRAGMA page_size;')

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $start = [int](($indexRoot - 1) * $pageSize)
    $end = [int][Math]::Min($bytes.Length, $start + $pageSize)
    $needle = [System.Text.Encoding]::ASCII.GetBytes('marker')
    $replacement = [System.Text.Encoding]::ASCII.GetBytes('MARKER')
    $replaced = 0
    for ($i = $start; $i -le $end - $needle.Length; $i++) {
        $match = $true
        for ($j = 0; $j -lt $needle.Length; $j++) {
            if ($bytes[$i + $j] -ne $needle[$j]) { $match = $false; break }
        }
        if ($match) {
            [Array]::Copy($replacement, 0, $bytes, $i, $replacement.Length)
            $replaced++
        }
    }
    if ($replaced -eq 0) { throw "New-CorruptTestDb found nothing to overwrite in the index page of $Path." }
    [System.IO.File]::WriteAllBytes($Path, $bytes)
    return $Path
}

function Copy-BuiltApp {
    # A runnable copy of the build output for a suite that needs the app inside a fake publish
    # folder: every top-level file plus the win-x64 native SQLite engine. The other runtimes and
    # the satellite folders are not needed to run db mode. Narrowed like Get-BuiltApp's copy, so the
    # scripts' app-folder check (sweep item 12) lets the tool run from it.
    param(
        [Parameter(Mandatory)][string]$AppPath,
        [Parameter(Mandatory)][string]$Destination
    )

    Initialize-TightTestFolder -Path $Destination | Out-Null
    Get-ChildItem -LiteralPath $AppPath -File | Copy-Item -Destination $Destination
    $native = Join-Path $AppPath 'runtimes\win-x64'
    $nativeDest = Join-Path $Destination 'runtimes'
    New-Item -ItemType Directory -Path $nativeDest -Force | Out-Null
    Copy-Item -LiteralPath $native -Destination $nativeDest -Recurse
    return $Destination
}
