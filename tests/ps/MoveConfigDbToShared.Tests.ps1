#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.5' }

<#
Behavioural tests for tools/Move-ConfigDbToShared.ps1 (SharedConfigDb-Plan AC8, AC10).

They run the real script in PLAN mode against fake dev/prod publish folders built in a temp
directory. A versioned system DLL stands in for ExchangeAdminWeb.dll so the build-version check
(step 1) has something real to read. Nothing here needs IIS, sqlite3 or elevation: plan mode
prints every step and must change nothing, and the refusals happen before any step runs.
#>

BeforeAll {
    . (Join-Path $PSScriptRoot 'Get-BuiltApp.ps1')
    $script:AppPath = Get-BuiltApp

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
            $out | Should -Match '(?m)^PLAN Start app pool ExchangeAdminWeb and confirm IIS reports it Started\s*$'
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
        # The integrity rows run the real db tool from DevPublishPath, so dev is a runnable copy
        # of the built app here, made once for the context.
        BeforeAll {
            $script:AppCopy = Copy-BuiltApp -AppPath $script:AppPath -Destination (Join-Path ([System.IO.Path]::GetTempPath()) ("mcds_app_" + [guid]::NewGuid().ToString('N')))
        }
        AfterAll { Remove-Item -LiteralPath $script:AppCopy -Recurse -Force -ErrorAction SilentlyContinue }
        BeforeEach {
            $script:fake = New-FakeInstances
            $script:fake.Dev = $script:AppCopy
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

        It 'fails naming the path when the shared file is not a sound database (scdi-3)' {
            New-Item -ItemType Directory -Path (Split-Path -Parent $fake.Shared) -Force | Out-Null
            [System.IO.File]::WriteAllText($fake.Shared, 'this is not a sqlite database')
            $err = $null
            try { & $script:ScriptPath -DevPublishPath $fake.Dev -ProdPublishPath $fake.Prod -SharedDbPath $fake.Shared -PlanOnly -MinimumAppVersion '1.0' *>&1 | Out-Null }
            catch { $err = $_ }
            $err | Should -Not -BeNullOrEmpty
            "$err" | Should -BeLike "*$($fake.Shared)*"
            "$err" | Should -Match 'integrity_check'
            "$err" | Should -Match 'db tool exit 2' -Because 'the real db tool ran and judged the file, not a missing or crashed tool'
        }

        It 'reports nothing to do only when the shared file exists and passes integrity_check' {
            New-Item -ItemType Directory -Path (Split-Path -Parent $fake.Shared) -Force | Out-Null
            New-TestDb -AppPath $script:AppPath -Path $fake.Shared -Sql 'CREATE TABLE t(x); INSERT INTO t VALUES (1);' | Out-Null
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

        It 'does NOT count a redirect as a live application' {
            # Review of A40: a 3xx returned true on the http pass and broke out of the
            # scheme loop before https was ever tried. An IIS-level http-to-https redirect
            # is exactly that case, and it proves nothing about whether the app started.
            $dir = New-SettingsDir -Json '{ }'
            try {
                Invoke-AppWarmup -PublishPath $dir -Requester { param($Url) $script:requested.Add($Url); 302 } |
                    Should -BeFalse
                $script:requested | Should -Contain 'https://localhost/'
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

    Context 'the warm-up can actually reach this application' {

        # Review of A40 by a second reader. Every guard below exists because the warm-up
        # was INERT against this app and no test could see it: they all inject a fake
        # requester, so nothing exercised the real HTTP path.

        BeforeAll {
            $src = Get-Content $script:ScriptPath -Raw
            $script:WarmupBody = $src.Substring($src.IndexOf('function Invoke-AppWarmup'))
            $script:WarmupBody = $script:WarmupBody.Substring(0, $script:WarmupBody.IndexOf('function Assert-SchemaReadyLogged'))
        }

        It 'sends Windows credentials, because the site disables anonymous auth' {
            # THE DEFECT. deploy.ps1 and the installer both set
            # anonymousAuthentication=false with Windows Auth on. IIS rejects an
            # unauthenticated request 401.2 at RQ_AUTHENTICATE_REQUEST, which runs BEFORE
            # AspNetCoreModuleV2 starts the app at RQ_EXECUTE_REQUEST_HANDLER - so the
            # request never reached ASP.NET Core and never woke anything.
            $script:WarmupBody | Should -Match '\$request\.UseDefaultCredentials = \$true'
        }

        It 'uses a compiled TLS validator, not a scriptblock' {
            # A scriptblock delegate needs a Runspace on the invoking thread. 5.1 runs the
            # handshake on an IOCP thread; PowerShell 7's HttpWebRequest wraps
            # SocketsHttpHandler and uses a threadpool thread. Neither has one, so the
            # delegate throws and the catch swallowed it.
            $script:WarmupBody | Should -Not -Match 'ServerCertificateValidationCallback = \{ \$true \}'
            $script:WarmupBody | Should -Match 'ExchangeAdminWeb\.WarmupTls'
        }

        It 'counts only 2xx as a live application' {
            # A 3xx returned true on the http pass and broke out before https was tried.
            $script:WarmupBody | Should -Match '\$status -ge 200 -and \$status -lt 300'
            $script:WarmupBody | Should -Not -Match '\$status -lt 400'
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

Describe 'aud-a38e: the cutover confirms each pool stopped before copying its database' {

    # Step 2 stopped each pool with a bare Stop-WebAppPool and a three-second sleep, printed
    # "stopped" either way, and steps 3 and 4 then backed up dev's live database and VACUUMed it
    # INTO the shared file. A worker still draining past the sleep could commit a write after that
    # snapshot, into the per-instance file dev stops opening at step 6. aud-a35 retired the same
    # shape in deploy.ps1 and the installer, aud-a38b in the promotion.

    BeforeAll {
        $tokens = $null
        $parseErrors = $null
        $script:CutoverAst = [System.Management.Automation.Language.Parser]::ParseFile($script:ScriptPath, [ref]$tokens, [ref]$parseErrors)
        $parseErrors | Should -BeNullOrEmpty

        function Get-CutoverFunction {
            param([string]$FunctionName)
            $script:CutoverAst.FindAll({
                    param($node)
                    $node -is [System.Management.Automation.Language.FunctionDefinitionAst]
                }, $true) | Where-Object Name -EQ $FunctionName | Select-Object -First 1
        }

        function Get-CutoverCommand {
            param([string]$CommandName)
            @($script:CutoverAst.FindAll({
                        param($node)
                        $node -is [System.Management.Automation.Language.CommandAst]
                    }, $true) | Where-Object { $_.GetCommandName() -eq $CommandName })
        }

        # The cutover runs at the top level, so it cannot be dot-sourced. Its stop step and the
        # helpers that step calls are lifted out of the file unchanged, so the tests below drive
        # the code that ships, with the real Stop-AppPoolVerified underneath.
        foreach ($name in 'Write-Step', 'Write-Ok', 'Write-Warn', 'Write-Fail', 'Invoke-PlanOrAction', 'Stop-AppPoolChecked') {
            . ([scriptblock]::Create((Get-CutoverFunction $name).Extent.Text))
        }
        Import-Module (Join-Path $PSScriptRoot '..' '..' 'tools' 'AppPoolControl.psm1') -Force

        # Recording stand-ins. Stop-WebAppPool and Get-ItemProperty are among them so that no run
        # of this suite - a mutation pasting the old stop back included - reaches IIS on a machine
        # that has it; the Get-ItemProperty one answers 'Started', as a live pool would.
        $script:cutoverEvents = [System.Collections.Generic.List[string]]::new()
        function Write-Plan { param([string]$Message) $script:cutoverEvents.Add("plan: $Message") }
        function Assert-NoActiveBulkJobsBeforeRecycle {
            param([string]$ConfigDir, [string]$AppPath, [switch]$PlanOnly)
            $script:cutoverEvents.Add("jobs: $ConfigDir $AppPath PlanOnly=$([bool]$PlanOnly)")
            # An active job: the check warns, and must not stop the cutover.
            [pscustomobject]@{ JobId = 'job-1'; Status = 'Running' }
        }
        function Stop-WebAppPool { param([string]$Name) $script:cutoverEvents.Add("bare Stop-WebAppPool: $Name") }
        Mock Get-ItemProperty { $script:cutoverEvents.Add("Get-ItemProperty: $Path"); [pscustomobject]@{ State = 'Started' } }

        # The module's injection points, set per test through $PSDefaultParameterValues. A script
        # block VALUE there is evaluated to produce the default, so each probe is wrapped in a
        # block that returns it (the aud-a38b lesson), and each test proves its probe was asked.
        function New-PoolProbe {
            [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '',
                Justification = 'Exists and State are used inside GetNewClosure blocks; the analyzer cannot see into them')]
            param([scriptblock]$Exists, [scriptblock]$State, [int]$TimeoutSeconds = 60)
            @{
                'Stop-AppPoolVerified:ExistsProbe'    = { $Exists }.GetNewClosure()
                'Stop-AppPoolVerified:StopAction'     = { { param($PoolName) $script:cutoverEvents.Add("verified stop: $PoolName") } }
                'Stop-AppPoolVerified:StateProbe'     = { $State }.GetNewClosure()
                'Stop-AppPoolVerified:Sleep'          = { { } }
                'Stop-AppPoolVerified:TimeoutSeconds' = $TimeoutSeconds
            }
        }
    }

    BeforeEach {
        $script:cutoverEvents.Clear()
    }

    Context 'the stop step, driven through the module''s injectable probes' {

        It 'stops the pool through the verified stop and goes on only when IIS confirms Stopped' {
            # The lifted functions read the script-level -PlanOnly switch.
            Set-Variable -Name PlanOnly -Value $false
            $PSDefaultParameterValues = New-PoolProbe -Exists { $script:cutoverEvents.Add('exists probe'); $true } -State { 'Stopped' }

            Stop-AppPoolChecked -Name 'aud-a38e-dev' -ConfigDir 'X:\dev\config' -AppPath 'X:\dev'

            $script:cutoverEvents | Should -Contain 'exists probe'
            $script:cutoverEvents | Should -Contain 'verified stop: aud-a38e-dev'
            $script:cutoverEvents | Should -Not -Contain 'bare Stop-WebAppPool: aud-a38e-dev'
        }

        It 'lets a pool that will not stop end the cutover before any database is copied' {
            # THE FINDING. The old shape slept three seconds and carried on regardless.
            Set-Variable -Name PlanOnly -Value $false
            $PSDefaultParameterValues = New-PoolProbe -Exists { $true } -State { 'Started' } -TimeoutSeconds 0

            { Stop-AppPoolChecked -Name 'aud-a38e-dev' -ConfigDir 'X:\dev\config' -AppPath 'X:\dev' } |
                Should -Throw -ExpectedMessage "*'aud-a38e-dev' is still 'Started'*"
            $script:cutoverEvents | Should -Contain 'verified stop: aud-a38e-dev'
        }

        It 'refuses an absent pool, because a cutover stops two instances that must exist' {
            # Stop-AppPoolVerified reports Absent without throwing - right for a first install,
            # wrong here: an absent pool is a wrong name, and the instance's real pool is still up.
            Set-Variable -Name PlanOnly -Value $false
            $PSDefaultParameterValues = New-PoolProbe -Exists { $script:cutoverEvents.Add('exists probe'); $false } -State { 'Started' }

            { Stop-AppPoolChecked -Name 'aud-a38e-dev' -ConfigDir 'X:\dev\config' -AppPath 'X:\dev' } |
                Should -Throw -ExpectedMessage "*'aud-a38e-dev' does not exist*No database was copied*"
            $script:cutoverEvents | Should -Contain 'exists probe'
        }

        It 'refuses any answer from the verified stop other than Stopped' {
            # The module answers Stopped, Absent or throws today. A new answer is not a stop.
            Set-Variable -Name PlanOnly -Value $false
            Mock Stop-AppPoolVerified { 'Stopping' }

            { Stop-AppPoolChecked -Name 'aud-a38e-dev' -ConfigDir 'X:\dev\config' -AppPath 'X:\dev' } |
                Should -Throw -ExpectedMessage "*'aud-a38e-dev' was not confirmed stopped (IIS reported 'Stopping')*"
            Should -Invoke Stop-AppPoolVerified -Times 1 -Exactly
        }

        It 'warns about active bulk jobs before the stop, and does not let them block it' {
            Set-Variable -Name PlanOnly -Value $false
            $PSDefaultParameterValues = New-PoolProbe -Exists { $true } -State { 'Stopped' }

            Stop-AppPoolChecked -Name 'aud-a38e-dev' -ConfigDir 'X:\dev\config' -AppPath 'X:\dev'

            $jobs = $script:cutoverEvents.IndexOf('jobs: X:\dev\config X:\dev PlanOnly=False')
            $stop = $script:cutoverEvents.IndexOf('verified stop: aud-a38e-dev')
            $jobs | Should -BeGreaterOrEqual 0
            $stop | Should -BeGreaterThan $jobs
        }

        It 'in plan mode prints one plan line and touches no pool' {
            Set-Variable -Name PlanOnly -Value $true
            $PSDefaultParameterValues = New-PoolProbe `
                -Exists { $script:cutoverEvents.Add('exists probe'); $true } `
                -State { $script:cutoverEvents.Add('state probe'); 'Stopped' }

            Stop-AppPoolChecked -Name 'aud-a38e-dev' -ConfigDir 'X:\dev\config' -AppPath 'X:\dev'

            $script:cutoverEvents | Should -Not -Contain 'exists probe'
            $script:cutoverEvents | Should -Not -Contain 'state probe'
            @($script:cutoverEvents | Where-Object { $_ -like 'verified stop:*' -or $_ -like 'bare Stop-WebAppPool:*' -or $_ -like 'Get-ItemProperty:*' }).Count | Should -Be 0
            $script:cutoverEvents | Should -Contain 'jobs: X:\dev\config X:\dev PlanOnly=True'
            @($script:cutoverEvents | Where-Object { $_ -like 'plan: Stop app pool aud-a38e-dev and confirm IIS reports it Stopped*' }).Count | Should -Be 1
        }
    }

    Context 'the call sites' {

        It 'imports AppPoolControl from beside the script, at the top level, before any pool is stopped' {
            $imports = @(Get-CutoverCommand 'Import-Module' | Where-Object {
                    $_.CommandElements.Count -ge 2 -and $_.CommandElements[1].Extent.Text -eq '(Join-Path $PSScriptRoot ''AppPoolControl.psm1'')'
                })
            $imports.Count | Should -Be 1
            [object]::ReferenceEquals($imports[0].Parent.Parent, $script:CutoverAst.EndBlock) | Should -BeTrue
            $imports[0].Extent.EndOffset | Should -BeLessThan (Get-CutoverCommand 'Stop-AppPoolChecked')[0].Extent.StartOffset
        }

        It 'calls Stop-WebAppPool nowhere in the cutover' {
            # The restore instructions NAME it in printed text, which is a string, not a call.
            @(Get-CutoverCommand 'Stop-WebAppPool').Count | Should -Be 0
        }

        It 'stops through Stop-AppPoolVerified once, inside the action plan mode never runs' {
            $verified = @(Get-CutoverCommand 'Stop-AppPoolVerified')
            $verified.Count | Should -Be 1
            $verified[0].Extent.Text | Should -Be 'Stop-AppPoolVerified -Name $Name'

            $stopStep = Get-CutoverFunction 'Stop-AppPoolChecked'
            $plan = @($stopStep.FindAll({
                        param($node)
                        $node -is [System.Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Invoke-PlanOrAction'
                    }, $true))
            $plan.Count | Should -Be 1
            $action = @($plan[0].CommandElements | Where-Object { $_ -is [System.Management.Automation.Language.ScriptBlockExpressionAst] })
            $action.Count | Should -Be 1
            $verified[0].Extent.StartOffset | Should -BeGreaterThan $action[0].Extent.StartOffset
            $verified[0].Extent.EndOffset | Should -BeLessThan $action[0].Extent.EndOffset
        }

        It 'stops both pools before any database is backed up or copied' {
            $stops = @(Get-CutoverCommand 'Stop-AppPoolChecked')
            $stops.Count | Should -Be 2
            $stops[0].Extent.Text | Should -Match '^Stop-AppPoolChecked -Name \$DevAppPoolName '
            $stops[1].Extent.Text | Should -Match '^Stop-AppPoolChecked -Name \$ProdAppPoolName '

            # Direct statements of the cutover's one top-level try, so a refusal goes straight to
            # the catch that restarts both pools and says nothing was changed.
            $try = $stops[0].Parent.Parent.Parent
            $try | Should -BeOfType ([System.Management.Automation.Language.TryStatementAst])
            [object]::ReferenceEquals($try.Parent, $script:CutoverAst.EndBlock) | Should -BeTrue
            [object]::ReferenceEquals($stops[1].Parent.Parent.Parent, $try) | Should -BeTrue

            # The snapshot is every backup and copy of a database: dev's backup, prod's backup and
            # the VACUUM INTO that makes dev's the shared one. Each starts after both stops end.
            $snapshots = @(Get-CutoverCommand 'Backup-SqliteConfigDb') + @(Get-CutoverCommand 'Copy-SqliteDbFile')
            $snapshots.Count | Should -Be 3 -Because 'the dev backup, the prod backup and the VACUUM INTO copy'
            foreach ($copy in $snapshots) {
                $copy.Extent.StartOffset | Should -BeGreaterThan $stops[1].Extent.EndOffset `
                    -Because "line $($copy.Extent.StartLineNumber) copies a database"
            }
        }
    }
}

Describe 'aud-a38f: the cutover confirms each pool started before saying so' {

    # Start-AppPoolChecked set "started" the moment Start-WebAppPool returned - IIS accepting the
    # request - and printed it. Step 7 and the failure path's restart both rely on it, and the
    # failure path has nothing after it to notice a pool that never came back.

    BeforeAll {
        $tokens = $null
        $parseErrors = $null
        $script:StartAst = [System.Management.Automation.Language.Parser]::ParseFile($script:ScriptPath, [ref]$tokens, [ref]$parseErrors)
        $parseErrors | Should -BeNullOrEmpty

        function Get-StartFunction {
            param([string]$FunctionName)
            $script:StartAst.FindAll({
                    param($node)
                    $node -is [System.Management.Automation.Language.FunctionDefinitionAst]
                }, $true) | Where-Object Name -EQ $FunctionName | Select-Object -First 1
        }

        function Get-StartCommand {
            param([string]$CommandName)
            @($script:StartAst.FindAll({
                        param($node)
                        $node -is [System.Management.Automation.Language.CommandAst]
                    }, $true) | Where-Object { $_.GetCommandName() -eq $CommandName })
        }

        # Lifted unchanged, as in the aud-a38e block, with the real Start-AppPoolVerified below.
        foreach ($name in 'Write-Step', 'Write-Ok', 'Write-Warn', 'Write-Fail', 'Invoke-PlanOrAction', 'Start-AppPoolChecked') {
            . ([scriptblock]::Create((Get-StartFunction $name).Extent.Text))
        }
        Import-Module (Join-Path $PSScriptRoot '..' '..' 'tools' 'AppPoolControl.psm1') -Force

        # Recording stand-ins; none of them reaches IIS, the pre-fix start pasted back included.
        $script:startEvents = [System.Collections.Generic.List[string]]::new()
        function Write-Plan { param([string]$Message) $script:startEvents.Add("plan: $Message") }
        function Start-WebAppPool { param([string]$Name) $script:startEvents.Add("bare Start-WebAppPool: $Name") }
        Mock Get-ItemProperty { $script:startEvents.Add("Get-ItemProperty: $Path"); [pscustomobject]@{ State = 'Stopped' } }

        # As in the aud-a38e block: each probe wrapped in a block that returns it.
        function New-StartProbe {
            [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '',
                Justification = 'Exists and State are used inside GetNewClosure blocks; the analyzer cannot see into them')]
            param([scriptblock]$Exists, [scriptblock]$State, [int]$TimeoutSeconds = 60)
            @{
                'Start-AppPoolVerified:ExistsProbe'    = { $Exists }.GetNewClosure()
                'Start-AppPoolVerified:StartAction'    = { { param($PoolName) $script:startEvents.Add("verified start: $PoolName") } }
                'Start-AppPoolVerified:StateProbe'     = { $State }.GetNewClosure()
                'Start-AppPoolVerified:Sleep'          = { { } }
                'Start-AppPoolVerified:TimeoutSeconds' = $TimeoutSeconds
            }
        }
    }

    BeforeEach {
        $script:startEvents.Clear()
    }

    Context 'the start step, driven through the module''s injectable probes' {

        It 'starts the pool through the verified start and reports it only when IIS confirms Started' {
            Set-Variable -Name PlanOnly -Value $false
            $PSDefaultParameterValues = New-StartProbe -Exists { $script:startEvents.Add('exists probe'); $true } -State { 'Started' }

            Start-AppPoolChecked -Name 'aud-a38f-prod'

            $script:startEvents | Should -Contain 'exists probe'
            $script:startEvents | Should -Contain 'verified start: aud-a38f-prod'
            $script:startEvents | Should -Not -Contain 'bare Start-WebAppPool: aud-a38f-prod'
        }

        It 'refuses a pool that IIS leaves stopped after accepting the start' {
            # THE FINDING. The old shape counted the accepted request as a running pool.
            Set-Variable -Name PlanOnly -Value $false
            $PSDefaultParameterValues = New-StartProbe -Exists { $true } -State { $script:startEvents.Add('state probe'); 'Stopped' } -TimeoutSeconds 0

            { Start-AppPoolChecked -Name 'aud-a38f-prod' } |
                Should -Throw -ExpectedMessage "*'aud-a38f-prod' is 'Stopped' after 0 seconds, not Started*"
            $script:startEvents | Should -Contain 'verified start: aud-a38f-prod'
            $script:startEvents | Should -Contain 'state probe'
        }

        It 'refuses an absent pool, which cannot be started' {
            Set-Variable -Name PlanOnly -Value $false
            $PSDefaultParameterValues = New-StartProbe -Exists { $script:startEvents.Add('exists probe'); $false } -State { 'Started' }

            { Start-AppPoolChecked -Name 'aud-a38f-prod' } |
                Should -Throw -ExpectedMessage "*'aud-a38f-prod' does not exist*"
            $script:startEvents | Should -Contain 'exists probe'
            $script:startEvents | Should -Not -Contain 'verified start: aud-a38f-prod'
        }

        It 'in plan mode prints one plan line and touches no pool' {
            Set-Variable -Name PlanOnly -Value $true
            $PSDefaultParameterValues = New-StartProbe `
                -Exists { $script:startEvents.Add('exists probe'); $true } `
                -State { $script:startEvents.Add('state probe'); 'Started' }

            Start-AppPoolChecked -Name 'aud-a38f-prod'

            $script:startEvents | Should -Not -Contain 'exists probe'
            $script:startEvents | Should -Not -Contain 'state probe'
            @($script:startEvents | Where-Object { $_ -like 'verified start:*' -or $_ -like 'bare Start-WebAppPool:*' -or $_ -like 'Get-ItemProperty:*' }).Count | Should -Be 0
            @($script:startEvents | Where-Object { $_ -eq 'plan: Start app pool aud-a38f-prod and confirm IIS reports it Started' }).Count | Should -Be 1
        }
    }

    Context 'the call sites' {

        It 'calls Start-WebAppPool nowhere in the cutover' {
            # The restore instructions and the restart warning NAME it in printed text, which is a
            # string, not a call.
            @(Get-StartCommand 'Start-WebAppPool').Count | Should -Be 0
        }

        It 'starts through Start-AppPoolVerified once, inside the action plan mode never runs' {
            $verified = @(Get-StartCommand 'Start-AppPoolVerified')
            $verified.Count | Should -Be 1
            $verified[0].Extent.Text | Should -Be 'Start-AppPoolVerified -Name $Name'

            $startStep = Get-StartFunction 'Start-AppPoolChecked'
            $plan = @($startStep.FindAll({
                        param($node)
                        $node -is [System.Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Invoke-PlanOrAction'
                    }, $true))
            $plan.Count | Should -Be 1
            $action = @($plan[0].CommandElements | Where-Object { $_ -is [System.Management.Automation.Language.ScriptBlockExpressionAst] })
            $action.Count | Should -Be 1
            $verified[0].Extent.StartOffset | Should -BeGreaterThan $action[0].Extent.StartOffset
            $verified[0].Extent.EndOffset | Should -BeLessThan $action[0].Extent.EndOffset
        }
    }
}

Describe 'aud-a38g: the cutover refuses unless each named pool is the one serving its instance' {

    # Step 2 stops the pools it is NAMED, and aud-a38e confirms each one stopped. That proves
    # nothing about a folder served by a different pool: a wrong but existing -DevAppPoolName is
    # stopped, confirmed, and dev's database is snapshotted while dev keeps running under its own
    # pool. aud-a38c closed the same gap in the promotion with Assert-AppPoolServesPath.

    BeforeAll {
        $tokens = $null
        $parseErrors = $null
        $script:ServesAst = [System.Management.Automation.Language.Parser]::ParseFile($script:ScriptPath, [ref]$tokens, [ref]$parseErrors)
        $parseErrors | Should -BeNullOrEmpty

        function Get-ServesFunction {
            param([string]$FunctionName)
            $script:ServesAst.FindAll({
                    param($node)
                    $node -is [System.Management.Automation.Language.FunctionDefinitionAst]
                }, $true) | Where-Object Name -EQ $FunctionName | Select-Object -First 1
        }

        function Get-ServesCommand {
            param([string]$CommandName)
            @($script:ServesAst.FindAll({
                        param($node)
                        $node -is [System.Management.Automation.Language.CommandAst]
                    }, $true) | Where-Object { $_.GetCommandName() -eq $CommandName })
        }

        function Get-AstAncestor {
            param($Node, [type]$Type)
            $parent = $Node.Parent
            while ($parent) {
                if ($parent -is $Type) { $parent }
                $parent = $parent.Parent
            }
        }

        # Lifted unchanged, as in the aud-a38e block, with the real Assert-AppPoolServesPath below.
        foreach ($name in 'Write-Step', 'Write-Ok', 'Write-Warn', 'Write-Fail', 'Assert-PoolServesInstance') {
            . ([scriptblock]::Create((Get-ServesFunction $name).Extent.Text))
        }
        Import-Module (Join-Path $PSScriptRoot '..' '..' 'tools' 'AppPoolControl.psm1') -Force

        $script:servesEvents = [System.Collections.Generic.List[string]]::new()

        # The module's one injection point, set per test through $PSDefaultParameterValues and
        # wrapped in a block that returns it (the aud-a38b lesson); each test proves it was asked.
        function New-ServingProbe {
            [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '',
                Justification = 'Applications is used inside a GetNewClosure block; the analyzer cannot see into it')]
            param([scriptblock]$Applications)
            @{ 'Assert-AppPoolServesPath:ApplicationsProbe' = { $Applications }.GetNewClosure() }
        }

        $script:DevFolder = 'X:\inetpub\aud-a38g-Dev'
        $script:ServedByDevPool = @(
            [pscustomobject]@{ Location = 'Default Web Site/'; ApplicationPool = 'DefaultAppPool'; PhysicalPath = 'X:\inetpub\wwwroot' }
            [pscustomobject]@{ Location = 'Default Web Site/aud-a38g-Dev'; ApplicationPool = 'aud-a38g-dev'; PhysicalPath = $script:DevFolder }
        )
    }

    BeforeEach {
        $script:servesEvents.Clear()
    }

    Context 'the check, driven through the module''s injectable probe' {

        It 'passes, and says so, when IIS serves the folder from the named pool alone' {
            $PSDefaultParameterValues = New-ServingProbe -Applications { $script:servesEvents.Add('applications probe'); $script:ServedByDevPool }

            $out = Assert-PoolServesInstance -Label 'Dev' -Name 'aud-a38g-dev' -PublishPath $script:DevFolder -PoolParameter 'DevAppPoolName' 6>&1 | Out-String

            $script:servesEvents | Should -Contain 'applications probe'
            $out | Should -Match ([regex]::Escape("Dev folder $($script:DevFolder) is served by app pool aud-a38g-dev and no other"))
        }

        It 'refuses a folder another pool serves, naming the folder, the pool that serves it and the pool that was named' {
            # THE FINDING. DefaultAppPool exists, so the old cutover stopped it, IIS confirmed it
            # stopped, and dev's database was snapshotted while dev ran on under its own pool.
            $PSDefaultParameterValues = New-ServingProbe -Applications { $script:servesEvents.Add('applications probe'); $script:ServedByDevPool }

            $err = $null
            try { Assert-PoolServesInstance -Label 'Dev' -Name 'DefaultAppPool' -PublishPath $script:DevFolder -PoolParameter 'DevAppPoolName' 6>&1 | Out-Null }
            catch { $err = $_ }

            $err | Should -Not -BeNullOrEmpty -Because 'a pool that does not serve the folder must stop the cutover'
            $script:servesEvents | Should -Contain 'applications probe'
            "$err" | Should -BeLike "*-DevAppPoolName names app pool 'DefaultAppPool'*" -Because 'the pool that was named'
            "$err" | Should -BeLike "*$($script:DevFolder)*" -Because 'the folder'
            "$err" | Should -BeLike "*runs under 'aud-a38g-dev'*" -Because 'the pool that does serve it'
            "$err" | Should -BeLike '*Nothing has been stopped or copied.'
        }

        It 'fails closed when nothing in IIS serves the folder' {
            $PSDefaultParameterValues = New-ServingProbe -Applications { $script:servesEvents.Add('applications probe'); @($script:ServedByDevPool[0]) }

            { Assert-PoolServesInstance -Label 'Dev' -Name 'aud-a38g-dev' -PublishPath $script:DevFolder -PoolParameter 'DevAppPoolName' 6>&1 | Out-Null } |
                Should -Throw -ExpectedMessage "*No IIS site or application has its files at '$($script:DevFolder)'*Nothing has been stopped or copied."
            $script:servesEvents | Should -Contain 'applications probe'
        }

        It 'fails closed when IIS cannot be read' {
            $PSDefaultParameterValues = New-ServingProbe -Applications { $script:servesEvents.Add('applications probe'); throw 'Cannot read configuration file' }

            { Assert-PoolServesInstance -Label 'Prod' -Name 'aud-a38g-prod' -PublishPath 'X:\inetpub\aud-a38g-Prod' -PoolParameter 'ProdAppPoolName' 6>&1 | Out-Null } |
                Should -Throw -ExpectedMessage "*-ProdAppPoolName names app pool 'aud-a38g-prod'*Cannot read the IIS sites and applications*Cannot read configuration file*Nothing has been stopped or copied."
            $script:servesEvents | Should -Contain 'applications probe'
        }
    }

    Context 'the cutover' {

        It 'prints the check for both instances in plan mode, before either stop, and changes nothing' {
            $fake = New-FakeInstances -WithProdDb
            try {
                $out = Invoke-Cutover $fake @{ PlanOnly = $true; MinimumAppVersion = '1.0' }

                $dev = "PLAN Assert-AppPoolServesPath -Name ExchangeAdminWebDev -PhysicalPath $($fake.Dev) (refuse unless IIS serves that folder from this pool and no other)"
                $prod = "PLAN Assert-AppPoolServesPath -Name ExchangeAdminWeb -PhysicalPath $($fake.Prod) (refuse unless IIS serves that folder from this pool and no other)"
                $out.IndexOf($dev) | Should -BeGreaterOrEqual 0 -Because 'the plan shows the dev check'
                $out.IndexOf($prod) | Should -BeGreaterThan $out.IndexOf($dev) -Because 'the plan shows the prod check, after the dev one'
                $out.IndexOf('PLAN Stop app pool ') | Should -BeGreaterThan $out.IndexOf($prod) -Because 'both checks come before either stop'
                Test-Path -LiteralPath $fake.Shared | Should -BeFalse
                Test-Path -LiteralPath (Join-Path $fake.Root 'backups') | Should -BeFalse
            } finally {
                Remove-Item -LiteralPath $fake.Root -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }

    Context 'the call sites' {

        It 'asks the module, with the promotion''s parameters, from one place' {
            $calls = @(Get-ServesCommand 'Assert-AppPoolServesPath')
            $calls.Count | Should -Be 1 -Because 'one call into the module and no copy of its rule'
            $calls[0].Extent.Text | Should -BeExactly 'Assert-AppPoolServesPath -Name $Name -PhysicalPath $PublishPath'

            $wrapper = Get-ServesFunction 'Assert-PoolServesInstance'
            $tries = @($wrapper.FindAll({ param($node) $node -is [System.Management.Automation.Language.TryStatementAst] }, $true))
            $tries.Count | Should -Be 1
            $calls[0].Extent.StartOffset | Should -BeGreaterThan $tries[0].Body.Extent.StartOffset
            $calls[0].Extent.EndOffset | Should -BeLessThan $tries[0].Body.Extent.EndOffset
        }

        It 'checks both instances before either pool is stopped, and lets an apply-mode refusal end the run' {
            $checks = @(Get-ServesCommand 'Assert-PoolServesInstance')
            $dev = 'Assert-PoolServesInstance -Label ''Dev'' -Name $DevAppPoolName -PublishPath $DevPublishPath -PoolParameter ''DevAppPoolName'''
            $prod = 'Assert-PoolServesInstance -Label ''Prod'' -Name $ProdAppPoolName -PublishPath $ProdPublishPath -PoolParameter ''ProdAppPoolName'''
            @($checks | ForEach-Object { $_.Extent.Text }) | Should -Be @($dev, $prod, $dev, $prod) -Because 'dev then prod, in the plan branch and in the apply branch'

            $firstStop = @(Get-ServesCommand 'Stop-AppPoolChecked')[0]
            foreach ($check in $checks) {
                $check.Extent.EndOffset | Should -BeLessThan $firstStop.Extent.StartOffset `
                    -Because "the check at line $($check.Extent.StartLineNumber) must run before any pool is stopped"
            }

            # Step 1b is if ($PlanOnly) { ... } else { ... }. The apply pair is a direct statement
            # of the else branch, inside no try, so a refusal is a terminating throw before step 2.
            $branch = @(Get-AstAncestor $checks[2] ([System.Management.Automation.Language.IfStatementAst]))[0]
            $branch.Clauses[0].Item1.Extent.Text | Should -BeExactly '$PlanOnly'
            foreach ($apply in $checks[2], $checks[3]) {
                [object]::ReferenceEquals($apply.Parent.Parent, $branch.ElseClause) | Should -BeTrue
                @(Get-AstAncestor $apply ([System.Management.Automation.Language.TryStatementAst])).Count | Should -Be 0
            }
            foreach ($plan in $checks[0], $checks[1]) {
                $plan.Extent.StartOffset | Should -BeGreaterThan $branch.Clauses[0].Item2.Extent.StartOffset
                $plan.Extent.EndOffset | Should -BeLessThan $branch.Clauses[0].Item2.Extent.EndOffset
            }
        }
    }
}

Describe 'cdt-38: the -SharedDbPath check is the module''s rule, and the path is written in one spelling' {

    # The cutover kept its own copy of the old writers' rule - a drive letter, a colon and a
    # BACKSLASH, matched case-insensitively - after cdt-37 moved the installer onto the module's
    # Get-ConfigStorePathForm. It refused D:/... (which the app opens and the read side reads
    # back), called \/server/share "not absolute" rather than a share, and passed a Kelvin-sign
    # drive letter the app refuses.

    BeforeAll {
        $tokens = $null
        $parseErrors = $null
        $script:FormAst = [System.Management.Automation.Language.Parser]::ParseFile($script:ScriptPath, [ref]$tokens, [ref]$parseErrors)
        $parseErrors | Should -BeNullOrEmpty
        $script:FormCheck = $script:FormAst.Find({
                param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Assert-LocalAbsolutePath'
            }, $true)
        Import-Module (Join-Path $PSScriptRoot '..' '..' 'tools' 'SqliteConfigBackup.psm1') -Force
        # Built at run time so this file stays ASCII.
        $script:Kelvin = [string][char]0x212A
    }

    It 'accepts a <Form> -SharedDbPath, and plans and reports the backslash spelling' -TestCases @(
        @{ Form = 'forward-slash' }
        @{ Form = 'mixed-separator' }
        @{ Form = 'lower-case-drive forward-slash' }
    ) {
        $fake = New-FakeInstances -WithProdDb
        try {
            $split = $fake.Shared.LastIndexOf('\Shared\')
            $given = switch ($Form) {
                'forward-slash' { $fake.Shared.Replace('\', '/') }
                'mixed-separator' { $fake.Shared.Substring(0, $split) + $fake.Shared.Substring($split).Replace('\', '/') }
                'lower-case-drive forward-slash' { $fake.Shared.Substring(0, 1).ToLowerInvariant() + $fake.Shared.Substring(1).Replace('\', '/') }
            }
            $expected = $given.Replace('/', '\')
            $given | Should -Not -Be $expected

            $out = Invoke-Cutover $fake @{ SharedDbPath = $given; PlanOnly = $true; MinimumAppVersion = '1.0' }

            $out | Should -Match 'Plan only - nothing was changed' -Because 'the app opens this path and the read side reads it back'
            $out.Contains("Shared DB : $expected") | Should -BeTrue -Because 'the banner reports the one spelling'
            ([regex]::Matches($out, [regex]::Escape("PLAN Set ConfigStore:Path = $expected in "))).Count | Should -Be 2 -Because 'both appsettings.json files get the one spelling'
            $out.Contains($given) | Should -BeFalse -Because 'the given spelling is normalised before anything uses it'
            Test-Path -LiteralPath $fake.Shared | Should -BeFalse
        } finally {
            Remove-Item -LiteralPath $fake.Root -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'refuses <Value> as a network share, before anything' -TestCases @(
        @{ Value = '\\server\share\exchangeadmin.db' }
        @{ Value = '//server/share/exchangeadmin.db' }
        @{ Value = '\/server/share/exchangeadmin.db' }
        @{ Value = '/\server\share\exchangeadmin.db' }
    ) {
        $fake = New-FakeInstances
        try {
            { & $script:ScriptPath -DevPublishPath $fake.Dev -ProdPublishPath $fake.Prod -SharedDbPath $Value -BackupRoot (Join-Path $fake.Root 'backups') -PlanOnly -MinimumAppVersion '1.0' *>&1 | Out-Null } |
                Should -Throw -ExpectedMessage "SharedDbPath must be a local path, not a network share: $Value. *"
            Test-Path -LiteralPath (Join-Path $fake.Root 'backups') | Should -BeFalse
        } finally {
            Remove-Item -LiteralPath $fake.Root -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'refuses a <Form> -SharedDbPath as not an absolute local path, naming it, before anything' -TestCases @(
        @{ Form = 'relative'; Value = 'shared\exchangeadmin.db' }
        @{ Form = 'drive-relative'; Value = 'D:exchangeadmin.db' }
        @{ Form = 'driveless rooted'; Value = '\shared\exchangeadmin.db' }
        @{ Form = 'driveless rooted forward-slash'; Value = '/shared/exchangeadmin.db' }
        @{ Form = 'NT-namespace UNC'; Value = '\??\UNC\server\share\exchangeadmin.db' }
        @{ Form = 'Kelvin-sign drive (U+212A)'; Value = '<kelvin>:\shared\exchangeadmin.db' }
    ) {
        $actual = $Value.Replace('<kelvin>', $script:Kelvin)
        $fake = New-FakeInstances
        try {
            { & $script:ScriptPath -DevPublishPath $fake.Dev -ProdPublishPath $fake.Prod -SharedDbPath $actual -BackupRoot (Join-Path $fake.Root 'backups') -PlanOnly -MinimumAppVersion '1.0' *>&1 | Out-Null } |
                Should -Throw -ExpectedMessage "SharedDbPath must be an absolute local path (a drive letter, a colon, then \ or / - *): $actual"
            Test-Path -LiteralPath (Join-Path $fake.Root 'backups') | Should -BeFalse
        } finally {
            Remove-Item -LiteralPath $fake.Root -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'makes the module''s call on every value: it accepts exactly what Get-ConfigStorePathForm calls Local' {
        $check = [scriptblock]::Create("function Write-Fail { param(`$Message) throw `$Message }`n" + $script:FormCheck.Extent.Text + "`nAssert-LocalAbsolutePath -Path `$args[0] -Name SharedDbPath")
        $values = @(
            'D:\shared\x.db', 'D:/shared/x.db', 'D:\shared/x.db', 'd:/x.db', 'z:\a', 'config\x.db', 'D:x.db', 'D:', '\x.db', '/x.db',
            '\\server\share\x.db', '//server/share/x.db', '\/server/share/x.db', '/\server\share\x.db', '\\?\D:\x.db', '\\.\D:\x.db',
            '\??\UNC\server\share\x.db', '1:\x.db', 'DD:\x.db', ':\x.db', ' D:\x.db', ($script:Kelvin + ':\x.db'))
        foreach ($value in $values) {
            $accepted = $true
            try { & $check $value } catch { $accepted = $false }
            $accepted | Should -Be ((Get-ConfigStorePathForm -Value $value) -eq 'Local') -Because "the cutover must make the module's call on '$value'"
        }
    }

    It 'holds no rule of its own, asks the module after importing it, and normalises the path next (AST)' {
        $calls = @($script:FormCheck.FindAll({
                    param($node)
                    $node -is [System.Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Get-ConfigStorePathForm'
                }, $true))
        $calls.Count | Should -Be 1
        $calls[0].Extent.Text | Should -BeExactly 'Get-ConfigStorePathForm -Value $Path'

        $patternOperators = 'Imatch', 'Inotmatch', 'Cmatch', 'Cnotmatch', 'Ilike', 'Inotlike', 'Clike', 'Cnotlike'
        @($script:FormCheck.FindAll({
                    param($node)
                    $node -is [System.Management.Automation.Language.BinaryExpressionAst] -and $patternOperators -contains "$($node.Operator)"
                }, $true)).Count | Should -Be 0 -Because 'no -match or -like of its own'
        @($script:FormCheck.FindAll({
                    param($node)
                    $node -is [System.Management.Automation.Language.InvokeMemberExpressionAst] -and
                    @('StartsWith', 'EndsWith', 'IsPathRooted', 'IsPathFullyQualified', 'GetPathRoot') -contains "$($node.Member.Value)"
                }, $true)).Count | Should -Be 0 -Because 'no string or path test of its own'

        $top = @($script:FormAst.EndBlock.Statements)
        $importAt = -1
        $checkAt = -1
        for ($i = 0; $i -lt $top.Count; $i++) {
            if ($top[$i].Extent.Text -eq 'Import-Module (Join-Path $PSScriptRoot ''SqliteConfigBackup.psm1'') -Force') { $importAt = $i }
            if ($top[$i].Extent.Text -eq 'Assert-LocalAbsolutePath -Path $SharedDbPath -Name ''SharedDbPath''') { $checkAt = $i }
        }
        $importAt | Should -BeGreaterOrEqual 0
        $checkAt | Should -BeGreaterThan $importAt -Because 'the module is imported before the one call'
        $top[$checkAt + 1].Extent.Text | Should -BeExactly '$SharedDbPath = [System.IO.Path]::GetFullPath($SharedDbPath)' -Because 'the accepted value is normalised before anything uses it'
    }
}
