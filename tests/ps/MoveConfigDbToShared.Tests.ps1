#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.5' }

<#
Behavioural tests for tools/Move-ConfigDbToShared.ps1 (SharedConfigDb-Plan AC8, AC10).

They run the real script in PLAN mode against fake dev/prod publish folders built in a temp
directory. A versioned system DLL stands in for ExchangeAdminWeb.dll so the build-version check
(step 1) has something real to read. Nothing here needs IIS, sqlite3 or elevation: plan mode
prints every step and must change nothing, and the refusals happen before any step runs.
#>

# Resolved at DISCOVERY time (before BeforeAll) so -Skip can use it; same fallback as
# SqliteConfigBackup.Tests.ps1. Only the integrity rows need sqlite3.exe.
if (-not (Get-Command sqlite3 -ErrorAction SilentlyContinue)) {
    $candidate = Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages\SQLite.SQLite_Microsoft.Winget.Source_8wekyb3d8bbwe'
    if (Test-Path -LiteralPath (Join-Path $candidate 'sqlite3.exe')) {
        $env:PATH = "$candidate;$env:PATH"
    }
}
$HasSqlite = [bool](Get-Command sqlite3 -ErrorAction SilentlyContinue)

BeforeAll {
    $script:ScriptPath = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' 'tools' 'Move-ConfigDbToShared.ps1')).Path

    # Any DLL with a real file version; copied under the app's name so step 1 can read it.
    $script:VersionedDll = [System.Text.Json.JsonSerializer].Assembly.Location
    $script:VersionedDllVersion = [version](([System.Diagnostics.FileVersionInfo]::GetVersionInfo($script:VersionedDll).FileVersion).Split(' ')[0])

    function New-FakeInstances {
        param([switch]$WithProdDb)
        $root = Join-Path ([System.IO.Path]::GetTempPath()) ("mcds_" + [guid]::NewGuid().ToString('N'))
        $dev = Join-Path $root 'Dev'
        $prod = Join-Path $root 'Prod'
        foreach ($p in $dev, $prod) {
            New-Item -ItemType Directory -Path (Join-Path $p 'config') -Force | Out-Null
            Copy-Item -LiteralPath $script:VersionedDll -Destination (Join-Path $p 'ExchangeAdminWeb.dll')
        }
        Set-Content -LiteralPath (Join-Path $dev 'appsettings.json') -Encoding UTF8 -Value '{ "Application": { "PathBase": "/Dev" } }'
        Set-Content -LiteralPath (Join-Path $prod 'appsettings.json') -Encoding UTF8 -Value '{ "Application": { "PathBase": "/Prod" } }'
        [System.IO.File]::WriteAllText((Join-Path $dev 'config\exchangeadmin.db'), 'dev-db-stand-in')
        if ($WithProdDb) { [System.IO.File]::WriteAllText((Join-Path $prod 'config\exchangeadmin.db'), 'prod-db-stand-in') }
        [pscustomobject]@{
            Root   = $root
            Dev    = $dev
            Prod   = $prod
            Shared = Join-Path $root 'Shared\config\exchangeadmin.db'
        }
    }

    function Invoke-Cutover {
        param($Fake, [hashtable]$Extra = @{})
        $args = @{
            DevPublishPath  = $Fake.Dev
            ProdPublishPath = $Fake.Prod
            SharedDbPath    = $Fake.Shared
            BackupRoot      = (Join-Path $Fake.Root 'backups')
        }
        foreach ($k in $Extra.Keys) { $args[$k] = $Extra[$k] }
        # Write-Host goes to the information stream; fold every stream into the captured text.
        return (& $script:ScriptPath @args *>&1 | Out-String)
    }
}

Describe 'Move-ConfigDbToShared.ps1 plan mode' {

    It 'prints all eight steps and changes nothing' {
        $fake = New-FakeInstances -WithProdDb
        try {
            $devBefore = Get-Content -LiteralPath (Join-Path $fake.Dev 'appsettings.json') -Raw
            $prodBefore = Get-Content -LiteralPath (Join-Path $fake.Prod 'appsettings.json') -Raw

            $out = Invoke-Cutover $fake @{ PlanOnly = $true; MinimumAppVersion = '1.0' }

            # Step 1 ran for real (read-only) in plan mode.
            $out | Should -Match 'Dev build .* is at or above 1\.0'
            $out | Should -Match 'Prod build .* is at or above 1\.0'
            # Steps 2-8 printed as PLAN lines.
            $out | Should -Match 'PLAN Stop app pool ExchangeAdminWebDev'
            $out | Should -Match 'PLAN Stop app pool ExchangeAdminWeb '
            $out | Should -Match 'PLAN Verified backup of dev DB'
            $out | Should -Match 'PLAN Verified backup of prod DB'
            $out | Should -Match 'PLAN VACUUM INTO dev DB .* PRAGMA integrity_check'
            $out | Should -Match 'PLAN Grant both app pool identities \(OI\)\(CI\)M on'
            $out | Should -Match 'PLAN Set ConfigStore:Path = .* in .*Dev\\appsettings\.json'
            $out | Should -Match 'PLAN Set ConfigStore:Path = .* in .*Prod\\appsettings\.json'
            $out | Should -Match 'PLAN Start app pool ExchangeAdminWebDev'
            $out | Should -Match '(?m)^PLAN Start app pool ExchangeAdminWeb\s*$'
            $out | Should -Match "PLAN Verify Dev logged 'Config store schema ready'"
            $out | Should -Match "PLAN Verify Prod logged 'Config store schema ready'"
            $out | Should -Match 'Plan only - nothing was changed'
            $out | Should -Match 'To return to two databases'

            # Nothing changed.
            Test-Path -LiteralPath $fake.Shared | Should -BeFalse
            Test-Path -LiteralPath (Join-Path $fake.Root 'backups') | Should -BeFalse
            Get-Content -LiteralPath (Join-Path $fake.Dev 'appsettings.json') -Raw | Should -Be $devBefore
            Get-Content -LiteralPath (Join-Path $fake.Prod 'appsettings.json') -Raw | Should -Be $prodBefore
            # (-like, not -Filter: the Windows filter 'appsettings.json.*' also matches appsettings.json itself.)
            Get-ChildItem -LiteralPath $fake.Dev -File | Where-Object { $_.Name -like 'appsettings.json.*' } | Should -BeNullOrEmpty
        } finally {
            Remove-Item -LiteralPath $fake.Root -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'is plan mode by default (no switch at all changes nothing)' {
        $fake = New-FakeInstances
        try {
            $out = Invoke-Cutover $fake @{ MinimumAppVersion = '1.0' }
            $out | Should -Match 'Mode      : PLAN ONLY'
            $out | Should -Match 'Plan only - nothing was changed'
            Test-Path -LiteralPath $fake.Shared | Should -BeFalse
        } finally {
            Remove-Item -LiteralPath $fake.Root -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'refuses a UNC -SharedDbPath before doing anything' {
        $fake = New-FakeInstances
        try {
            { & $script:ScriptPath -DevPublishPath $fake.Dev -ProdPublishPath $fake.Prod -SharedDbPath '\\server\share\exchangeadmin.db' -PlanOnly -MinimumAppVersion '1.0' *>&1 | Out-Null } |
                Should -Throw -ExpectedMessage '*network share*'
        } finally {
            Remove-Item -LiteralPath $fake.Root -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'refuses a relative -SharedDbPath' {
        $fake = New-FakeInstances
        try {
            { & $script:ScriptPath -DevPublishPath $fake.Dev -ProdPublishPath $fake.Prod -SharedDbPath 'shared\exchangeadmin.db' -PlanOnly -MinimumAppVersion '1.0' *>&1 | Out-Null } |
                Should -Throw -ExpectedMessage '*absolute local path*'
        } finally {
            Remove-Item -LiteralPath $fake.Root -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    # scdi-2: rooted is not fully qualified. "D:exchangeadmin.db" names a drive but resolves
    # against that drive's current directory, so two instances could open two different files.
    It 'refuses a drive-relative -SharedDbPath (<Path>)' -TestCases @(
        @{ Path = 'D:exchangeadmin.db' }
        @{ Path = 'C:config\exchangeadmin.db' }
    ) {
        $fake = New-FakeInstances
        try {
            { & $script:ScriptPath -DevPublishPath $fake.Dev -ProdPublishPath $fake.Prod -SharedDbPath $Path -PlanOnly -MinimumAppVersion '1.0' *>&1 | Out-Null } |
                Should -Throw -ExpectedMessage '*absolute local path*'
        } finally {
            Remove-Item -LiteralPath $fake.Root -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'refuses when a build is below -MinimumAppVersion, before stopping anything' {
        $fake = New-FakeInstances
        try {
            $tooHigh = [version]"$($script:VersionedDllVersion.Major + 1).0"
            $err = $null
            try { & $script:ScriptPath -DevPublishPath $fake.Dev -ProdPublishPath $fake.Prod -SharedDbPath $fake.Shared -PlanOnly -MinimumAppVersion $tooHigh *>&1 | Out-Null }
            catch { $err = $_ }
            $err | Should -Not -BeNullOrEmpty
            "$err" | Should -Match 'below the minimum'
            "$err" | Should -Match 'Nothing was changed'
        } finally {
            Remove-Item -LiteralPath $fake.Root -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'refuses when the shared file already exists (it only ever creates it)' {
        $fake = New-FakeInstances
        try {
            New-Item -ItemType Directory -Path (Split-Path -Parent $fake.Shared) -Force | Out-Null
            [System.IO.File]::WriteAllText($fake.Shared, 'already-here')
            { & $script:ScriptPath -DevPublishPath $fake.Dev -ProdPublishPath $fake.Prod -SharedDbPath $fake.Shared -PlanOnly -MinimumAppVersion '1.0' *>&1 | Out-Null } |
                Should -Throw -ExpectedMessage '*already exists*'
        } finally {
            Remove-Item -LiteralPath $fake.Root -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'refuses when dev has no database to share' {
        $fake = New-FakeInstances
        try {
            Remove-Item -LiteralPath (Join-Path $fake.Dev 'config\exchangeadmin.db') -Force
            { & $script:ScriptPath -DevPublishPath $fake.Dev -ProdPublishPath $fake.Prod -SharedDbPath $fake.Shared -PlanOnly -MinimumAppVersion '1.0' *>&1 | Out-Null } |
                Should -Throw -ExpectedMessage '*nothing to make shared*'
        } finally {
            Remove-Item -LiteralPath $fake.Root -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'refuses a split state (one instance already points at the shared file)' {
        $fake = New-FakeInstances
        try {
            $escaped = $fake.Shared -replace '\\', '\\'
            Set-Content -LiteralPath (Join-Path $fake.Dev 'appsettings.json') -Encoding UTF8 -Value "{ `"ConfigStore`": { `"Path`": `"$escaped`" } }"
            { & $script:ScriptPath -DevPublishPath $fake.Dev -ProdPublishPath $fake.Prod -SharedDbPath $fake.Shared -PlanOnly -MinimumAppVersion '1.0' *>&1 | Out-Null } |
                Should -Throw -ExpectedMessage '*split state*'
        } finally {
            Remove-Item -LiteralPath $fake.Root -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    # scdi-3: "both already shared - nothing to do" must first prove the file both instances are
    # configured to open exists and is sound; the app opens a configured path without create, so
    # a missing file stops both pools at their next start. Plan mode is the default and must fail
    # the same way.
    Context 'both instances already name the shared path' {
        BeforeEach {
            $script:fake = New-FakeInstances
            $escaped = $script:fake.Shared -replace '\\', '\\'
            foreach ($p in $script:fake.Dev, $script:fake.Prod) {
                Set-Content -LiteralPath (Join-Path $p 'appsettings.json') -Encoding UTF8 -Value "{ `"ConfigStore`": { `"Path`": `"$escaped`" } }"
            }
        }
        AfterEach { Remove-Item -LiteralPath $script:fake.Root -Recurse -Force -ErrorAction SilentlyContinue }

        It 'fails naming the path when the shared file does not exist (plan mode, scdi-3)' {
            $err = $null
            $out = $null
            try { $out = & $script:ScriptPath -DevPublishPath $fake.Dev -ProdPublishPath $fake.Prod -SharedDbPath $fake.Shared -MinimumAppVersion '1.0' *>&1 | Out-String }
            catch { $err = $_ }
            $err | Should -Not -BeNullOrEmpty -Because 'a missing configured file is never "nothing to do"'
            "$err" | Should -BeLike "*$($fake.Shared)*"
            "$err" | Should -Match 'no file exists there'
            "$err" | Should -Match 'Nothing was changed'
            "$out" | Should -Not -Match 'nothing to do'
            Test-Path -LiteralPath $fake.Shared | Should -BeFalse
        }

        It 'fails naming the path when the shared file is not a sound database (scdi-3)' -Skip:(-not $HasSqlite) {
            New-Item -ItemType Directory -Path (Split-Path -Parent $fake.Shared) -Force | Out-Null
            [System.IO.File]::WriteAllText($fake.Shared, 'this is not a sqlite database')
            $err = $null
            try { & $script:ScriptPath -DevPublishPath $fake.Dev -ProdPublishPath $fake.Prod -SharedDbPath $fake.Shared -PlanOnly -MinimumAppVersion '1.0' *>&1 | Out-Null }
            catch { $err = $_ }
            $err | Should -Not -BeNullOrEmpty
            "$err" | Should -BeLike "*$($fake.Shared)*"
            "$err" | Should -Match 'integrity_check'
        }

        It 'reports nothing to do only when the shared file exists and passes integrity_check' -Skip:(-not $HasSqlite) {
            New-Item -ItemType Directory -Path (Split-Path -Parent $fake.Shared) -Force | Out-Null
            & sqlite3 $fake.Shared "CREATE TABLE t(x); INSERT INTO t VALUES (1);"
            $out = & $script:ScriptPath -DevPublishPath $fake.Dev -ProdPublishPath $fake.Prod -SharedDbPath $fake.Shared -PlanOnly -MinimumAppVersion '1.0' *>&1 | Out-String
            $out | Should -Match 'passes PRAGMA integrity_check - nothing to do'
        }
    }

    It 'refuses -PlanOnly together with -Apply' {
        $fake = New-FakeInstances
        try {
            { & $script:ScriptPath -DevPublishPath $fake.Dev -ProdPublishPath $fake.Prod -SharedDbPath $fake.Shared -PlanOnly -Apply *>&1 | Out-Null } |
                Should -Throw -ExpectedMessage '*not both*'
        } finally {
            Remove-Item -LiteralPath $fake.Root -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
