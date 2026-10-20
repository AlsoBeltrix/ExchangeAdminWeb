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
            # A40: the step warms the instance before reading the log, and says so.
            $out | Should -Match "PLAN Warm Dev on localhost, then verify it logged 'Config store schema ready'"
            $out | Should -Match "PLAN Warm Prod on localhost, then verify it logged 'Config store schema ready'"
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

Describe 'Get-AppPoolIdentity' {
    # Regression cover for the 2026-09-08 cutover failure. The function used Get-Item, which
    # makes the IIS provider materialize processModel.identityType as an integer and throw
    # "Cannot convert value SpecificUser to type System.Int32" on any pool that runs as a
    # specific user - which both ExchangeAdminWeb pools do. It threw at step 5, after both
    # pools had already been stopped, and took the sites down.
    #
    # The function is lifted out of the script by AST rather than dot-sourcing (dot-sourcing
    # would run the whole cutover), so these tests read the real source text.
    BeforeAll {
        $tokens = $null
        $errors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($script:ScriptPath, [ref]$tokens, [ref]$errors)
        $errors | Should -BeNullOrEmpty

        $fn = $ast.FindAll(
            {
                param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                $node.Name -eq 'Get-AppPoolIdentity'
            }, $true) | Select-Object -First 1
        $fn | Should -Not -BeNullOrEmpty -Because 'the script must still define Get-AppPoolIdentity'

        # Write-Fail is the script's throw helper; the extracted function calls it.
        function Write-Fail { param([string]$Message) throw $Message }
        . ([scriptblock]::Create($fn.Extent.Text))
    }

    It 'returns the account name for a SpecificUser pool (<IdentityType>)' -TestCases @(
        @{ IdentityType = 'SpecificUser' }   # what PowerShell 7 yields
        @{ IdentityType = 3 }                # what Windows PowerShell 5.1 yields
    ) {
        Mock Get-ItemProperty { [pscustomobject]@{ identityType = $IdentityType; userName = 'ANALOG\svc_scriptadm' } }

        Get-AppPoolIdentity -Name 'ExchangeAdminWeb' | Should -Be 'ANALOG\svc_scriptadm'
    }

    It 'never uses Get-Item, which is what threw on a SpecificUser pool' {
        Mock Get-ItemProperty { [pscustomobject]@{ identityType = 'SpecificUser'; userName = 'ANALOG\svc_scriptadm' } }
        Mock Get-Item { throw 'Cannot convert value "SpecificUser" to type "System.Int32".' }

        Get-AppPoolIdentity -Name 'ExchangeAdminWeb' | Should -Be 'ANALOG\svc_scriptadm'
        Should -Invoke Get-Item -Times 0 -Exactly
    }

    It 'falls back to the virtual account for a pool that is not SpecificUser (<IdentityType>)' -TestCases @(
        @{ IdentityType = 'ApplicationPoolIdentity' }
        @{ IdentityType = 4 }
        @{ IdentityType = 'NetworkService' }
    ) {
        Mock Get-ItemProperty { [pscustomobject]@{ identityType = $IdentityType; userName = '' } }

        Get-AppPoolIdentity -Name 'ExchangeAdminWebDev' | Should -Be 'IIS AppPool\ExchangeAdminWebDev'
    }

    It 'fails rather than granting rights to an empty account name' {
        Mock Get-ItemProperty { [pscustomobject]@{ identityType = 'SpecificUser'; userName = '  ' } }

        { Get-AppPoolIdentity -Name 'ExchangeAdminWeb' } | Should -Throw -ExpectedMessage '*no user name*'
    }

    It 'fails when the pool has no processModel at all' {
        Mock Get-ItemProperty { $null }

        { Get-AppPoolIdentity -Name 'ExchangeAdminWeb' } | Should -Throw -ExpectedMessage '*no processModel*'
    }
}

Describe 'A39: the shared database may not live inside a publish folder' {

    # Audit finding A39. Assert-LocalAbsolutePath rejected UNC paths and drive-relative
    # paths, and accepted a path inside an instance's publish tree - which a deploy mirrors
    # with robocopy /MIR, deleting whatever is not in the source.
    #
    # Write-Fail throws, so these use the same Should -Throw idiom as the UNC and
    # drive-relative cases above rather than inspecting captured output.

    It 'refuses a path inside the dev publish folder' {
        $fake = New-FakeInstances
        try {
            $inside = Join-Path $fake.Dev 'shared\exchangeadmin.db'
            { & $script:ScriptPath -DevPublishPath $fake.Dev -ProdPublishPath $fake.Prod -SharedDbPath $inside -PlanOnly -MinimumAppVersion '1.0' *>&1 | Out-Null } |
                Should -Throw -ExpectedMessage '*is inside DevPublishPath*'
        } finally {
            Remove-Item -LiteralPath $fake.Root -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'refuses a path inside the prod publish folder' {
        $fake = New-FakeInstances
        try {
            $inside = Join-Path $fake.Prod 'shared\exchangeadmin.db'
            { & $script:ScriptPath -DevPublishPath $fake.Dev -ProdPublishPath $fake.Prod -SharedDbPath $inside -PlanOnly -MinimumAppVersion '1.0' *>&1 | Out-Null } |
                Should -Throw -ExpectedMessage '*is inside ProdPublishPath*'
        } finally {
            Remove-Item -LiteralPath $fake.Root -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It "refuses an instance's own config folder, which deploy exclusions only happen to spare" {
        # The deploy excludes config\ today, so /MIR would not delete it - but it is then one
        # instance's private directory and the other instance depends on a folder that an
        # uninstall removes. Relying on an exclusion list to protect shared state is a
        # coincidence, not a guarantee.
        $fake = New-FakeInstances
        try {
            $inside = Join-Path $fake.Dev 'config\exchangeadmin.db'
            { & $script:ScriptPath -DevPublishPath $fake.Dev -ProdPublishPath $fake.Prod -SharedDbPath $inside -PlanOnly -MinimumAppVersion '1.0' *>&1 | Out-Null } |
                Should -Throw -ExpectedMessage '*is inside DevPublishPath*'
        } finally {
            Remove-Item -LiteralPath $fake.Root -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'explains why, so the operator can choose a path rather than guess' {
        $fake = New-FakeInstances
        try {
            $inside = Join-Path $fake.Dev 'shared\exchangeadmin.db'
            { & $script:ScriptPath -DevPublishPath $fake.Dev -ProdPublishPath $fake.Prod -SharedDbPath $inside -PlanOnly -MinimumAppVersion '1.0' *>&1 | Out-Null } |
                Should -Throw -ExpectedMessage '*robocopy /MIR deletes what is not in the source*'
        } finally {
            Remove-Item -LiteralPath $fake.Root -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'refuses before doing any work at all' {
        # A precondition, so a bad path must not leave a backup root or a database behind.
        $fake = New-FakeInstances
        try {
            $inside = Join-Path $fake.Dev 'shared\exchangeadmin.db'
            try { & $script:ScriptPath -DevPublishPath $fake.Dev -ProdPublishPath $fake.Prod -SharedDbPath $inside -BackupRoot (Join-Path $fake.Root 'backups') -MinimumAppVersion '1.0' *>&1 | Out-Null } catch { }

            Test-Path -LiteralPath (Join-Path $fake.Root 'backups') | Should -BeFalse
            Test-Path -LiteralPath $inside | Should -BeFalse
        } finally {
            Remove-Item -LiteralPath $fake.Root -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'still accepts a path outside both trees' {
        # A fix that refused everything would pass every test above.
        $fake = New-FakeInstances -WithProdDb
        try {
            $out = Invoke-Cutover $fake @{ PlanOnly = $true; MinimumAppVersion = '1.0' }

            $out | Should -Not -Match 'is inside (Dev|Prod)PublishPath'
            $out | Should -Match 'Plan only - nothing was changed'
        } finally {
            Remove-Item -LiteralPath $fake.Root -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'does not mistake a sibling folder with a shared prefix for a containing one' {
        # "...\Dev" must not match "...\DevShared". The trailing separator is what stops it.
        $fake = New-FakeInstances -WithProdDb
        try {
            $sibling = Join-Path ($fake.Dev + 'Shared') 'config\exchangeadmin.db'
            $out = Invoke-Cutover $fake @{ SharedDbPath = $sibling; PlanOnly = $true; MinimumAppVersion = '1.0' }

            $out | Should -Not -Match 'is inside DevPublishPath'
        } finally {
            Remove-Item -LiteralPath $fake.Root -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

Describe 'A40: the cutover wakes the app before reading its startup log' {

    # Audit finding A40. Step 8 waits for "Config store schema ready", which Program.cs
    # writes during APPLICATION startup. Starting an app pool creates a worker process; IIS
    # does not load the application into it until a request arrives unless Application
    # Initialization is installed and preloadEnabled is set. startMode=AlwaysRunning, which
    # the installer does set, keeps the worker alive and initialises nothing.
    #
    # So a correct, healthy, idle instance never wrote the line and the cutover failed after
    # 90 seconds reporting a startup error that had not happened.
    #
    # Functions are lifted out by AST, as Get-AppPoolIdentity above is - dot-sourcing the
    # script would run the whole cutover.

    BeforeAll {
        $tokens = $null
        $errors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($script:ScriptPath, [ref]$tokens, [ref]$errors)
        $errors | Should -BeNullOrEmpty

        foreach ($name in @('Get-AppPathBase', 'Invoke-AppWarmup')) {
            $fn = $ast.FindAll(
                {
                    param($node)
                    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                    $node.Name -eq $name
                }, $true) | Select-Object -First 1
            $fn | Should -Not -BeNullOrEmpty -Because "the script must still define $name"
            . ([scriptblock]::Create($fn.Extent.Text))
        }

        function New-SettingsDir {
            param([string]$Json)
            $dir = Join-Path ([System.IO.Path]::GetTempPath()) ("a40_" + [guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
            if ($PSBoundParameters.ContainsKey('Json')) {
                Set-Content -LiteralPath (Join-Path $dir 'appsettings.json') -Encoding UTF8 -Value $Json
            }
            return $dir
        }
    }

    Context 'Get-AppPathBase' {

        It 'reads the configured path base' {
            $dir = New-SettingsDir -Json '{ "Application": { "PathBase": "/Dev" } }'
            try { Get-AppPathBase -PublishPath $dir | Should -Be '/Dev' }
            finally { Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue }
        }

        It 'normalises a path base written without a leading slash' {
            $dir = New-SettingsDir -Json '{ "Application": { "PathBase": "Dev" } }'
            try { Get-AppPathBase -PublishPath $dir | Should -Be '/Dev' }
            finally { Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue }
        }

        It 'returns empty when there is no path base' {
            $dir = New-SettingsDir -Json '{ "Application": { "PathBase": "" } }'
            try { Get-AppPathBase -PublishPath $dir | Should -Be '' }
            finally { Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue }
        }

        It 'returns empty rather than throwing on unreadable settings' {
            # This runs during a cutover. A warm-up helper must not be the thing that fails it.
            $dir = New-SettingsDir -Json '{ not json'
            try { Get-AppPathBase -PublishPath $dir | Should -Be '' }
            finally { Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue }
        }

        It 'returns empty when appsettings.json is absent' {
            $dir = New-SettingsDir
            try { Get-AppPathBase -PublishPath $dir | Should -Be '' }
            finally { Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue }
        }
    }

    Context 'Invoke-AppWarmup' {

        # The injected requester returns an HTTP STATUS CODE (0 = nothing answered), which
        # is the contract the real one implements with a synchronous WebRequest.

        BeforeEach {
            $script:requested = [System.Collections.Generic.List[string]]::new()
        }

        It 'requests the instance on localhost, including its path base' {
            # THE FINDING: without this request the application is never initialised and the
            # log line it writes on startup never appears.
            $dir = New-SettingsDir -Json '{ "Application": { "PathBase": "/Dev" } }'
            try {
                Invoke-AppWarmup -PublishPath $dir -Requester { param($Url) $script:requested.Add($Url); 200 } | Should -BeTrue
                $script:requested[0] | Should -Be 'http://localhost/Dev/'
            } finally { Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue }
        }

        It 'requests the root when there is no path base' {
            $dir = New-SettingsDir -Json '{ }'
            try {
                $null = Invoke-AppWarmup -PublishPath $dir -Requester { param($Url) $script:requested.Add($Url); 200 }
                $script:requested[0] | Should -Be 'http://localhost/'
            } finally { Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue }
        }

        It 'counts a redirect as a live application' {
            $dir = New-SettingsDir -Json '{ }'
            try {
                Invoke-AppWarmup -PublishPath $dir -Requester { param($Url) 302 } | Should -BeTrue
            } finally { Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue }
        }

        It 'does NOT count a 503 as warmed - that is a pool still starting' {
            # Review of A40, and the sharpest of its three findings. The old code returned
            # true for any HTTP response, so the 503 a starting pool returns latched the
            # caller's flag and stopped it retrying for the rest of the 90 seconds - exactly
            # the case the retry exists for.
            $dir = New-SettingsDir -Json '{ }'
            try {
                Invoke-AppWarmup -PublishPath $dir -Requester { param($Url) $script:requested.Add($Url); 503 } | Should -BeFalse
            } finally { Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue }
        }

        It 'does NOT count a 404 from some other site as warmed, and still tries https' {
            # Review of A40: a 404 from Default Web Site on port 80, a 400 from http.sys for
            # an unmatched host header, or a 403.4 from "Require SSL" all have a response
            # object. Treating those as success meant the https attempt never ran and the
            # real application was never woken.
            $dir = New-SettingsDir -Json '{ }'
            try {
                Invoke-AppWarmup -PublishPath $dir -Requester { param($Url) $script:requested.Add($Url); 404 } | Should -BeFalse
                $script:requested | Should -Contain 'https://localhost/'
            } finally { Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue }
        }

        It 'falls back to https when http is not listening' {
            $dir = New-SettingsDir -Json '{ }'
            try {
                $requester = {
                    param($Url)
                    $script:requested.Add($Url)
                    if ($Url -like 'http://*') { return 0 }
                    return 200
                }
                Invoke-AppWarmup -PublishPath $dir -Requester $requester | Should -BeTrue
                $script:requested | Should -Contain 'https://localhost/'
            } finally { Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue }
        }

        It 'reports false when nothing answers, without throwing' {
            $dir = New-SettingsDir -Json '{ }'
            try { Invoke-AppWarmup -PublishPath $dir -Requester { param($Url) 0 } | Should -BeFalse }
            finally { Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue }
        }

        It 'restores certificate validation after relaxing it for https' {
            $before = [System.Net.ServicePointManager]::ServerCertificateValidationCallback
            $dir = New-SettingsDir -Json '{ }'
            try {
                $null = Invoke-AppWarmup -PublishPath $dir -Requester { param($Url) throw 'boom' }
                [System.Net.ServicePointManager]::ServerCertificateValidationCallback | Should -Be $before
            } finally { Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue }
        }

        It 'issues the real request synchronously, not through Invoke-WebRequest' {
            # Review of A40: a PowerShell scriptblock assigned to
            # ServerCertificateValidationCallback needs a Runspace on the invoking thread.
            # Windows PowerShell 5.1 runs Invoke-WebRequest asynchronously, so the handshake
            # fires on an IOCP thread with no Runspace and every https attempt aborts.
            $source = Get-Content $script:ScriptPath -Raw
            $body = $source.Substring($source.IndexOf('function Invoke-AppWarmup'))
            $body = $body.Substring(0, $body.IndexOf('function Assert-SchemaReadyLogged'))

            # Comments stripped: the claim is about the CODE, and the remarks above
            # deliberately NAME Invoke-WebRequest to explain why it is not used. A
            # guard that reads prose proves nothing about what executes.
            $code = ((($body -replace '(?s)<#.*?#>', '') -split "`n") | Where-Object { $_.TrimStart() -notlike '#*' }) -join "`n"

            $code | Should -Not -Match 'Invoke-WebRequest'
            $code | Should -Match '\[System\.Net\.WebRequest\]::Create'
            $code | Should -Match 'AllowAutoRedirect = \$false'
        }
    }

    Context 'the wait loop actually calls it' {

        # This guard exists because its absence was caught by a mutation, not by review.
        # Disabling the warm-up call inside Assert-SchemaReadyLogged left all 41 tests
        # green: every other test here drives Invoke-AppWarmup directly, so none of them
        # noticed that nothing in the cutover was calling it. Proving a helper works is not
        # proving it is used - the same trap as the A28 counting tests and the A38 message
        # assertion.

        BeforeAll {
            $tokens = $null
            $errors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($script:ScriptPath, [ref]$tokens, [ref]$errors)
            $fn = $ast.FindAll(
                {
                    param($node)
                    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                    $node.Name -eq 'Assert-SchemaReadyLogged'
                }, $true) | Select-Object -First 1
            $fn | Should -Not -BeNullOrEmpty
            $script:SchemaReadyBody = $fn.Extent.Text
        }

        It 'warms the instance from inside Assert-SchemaReadyLogged' {
            $script:SchemaReadyBody | Should -Match 'Invoke-AppWarmup -PublishPath \$PublishPath'
        }

        It 'warms on every pass rather than latching on the first answer' {
            # Review of A40: `if (-not $warmed)` stopped retrying as soon as anything
            # answered, including the 503 of a pool that had not finished starting.
            $script:SchemaReadyBody | Should -Not -Match 'if \(-not \$warmed\)'
        }

        It 'warms inside the retry loop, not once before it' {
            # The first attempt can land while the worker process is still coming up, so the
            # call has to be retried alongside the log poll.
            $doAt = $script:SchemaReadyBody.IndexOf('do {')
            $whileAt = $script:SchemaReadyBody.IndexOf('} while')
            $warmAt = $script:SchemaReadyBody.IndexOf('Invoke-AppWarmup')

            $doAt | Should -BeGreaterThan 0
            $warmAt | Should -BeGreaterThan $doAt
            $warmAt | Should -BeLessThan $whileAt
        }

        It 'warms before reading the log, not after' {
            $warmAt = $script:SchemaReadyBody.IndexOf('Invoke-AppWarmup')
            $readAt = $script:SchemaReadyBody.IndexOf('Get-LatestSerilogLog')

            $warmAt | Should -BeLessThan $readAt
        }
    }

    Context 'the failure message' {

        It 'distinguishes "never asked to start" from "asked and failed"' {
            # Which of the two happened changes what the operator should go and look at.
            $source = Get-Content $script:ScriptPath -Raw
            $source | Should -Match 'Nothing answered for this instance on localhost'
            $source | Should -Match 'it was reached and still did not log the line'
        }
    }
}
