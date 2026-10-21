<#
Audit finding A36: the generic installer upgraded an instance - mirroring new binaries over
it - without ever snapshotting the config database those binaries may migrate on first start.

These are source guards, and the reason is worth stating rather than glossing: the backup is
a dozen lines of inline script inside a 790-line installer that needs IIS, a build and a real
database to run. Extracting it into a function purely so a test could call it would move the
ordering question - which is the actual finding - out of the place the ordering has to hold.

So what is pinned here is ORDER and FATALITY, read from the source. The backup mechanism
itself is already covered behaviourally by SqliteConfigBackup.Tests.ps1 against the real
built app and a real database; this suite does not re-test it.
#>

Set-StrictMode -Version Latest

BeforeAll {
    $script:InstallerPath = Join-Path $PSScriptRoot '..\..\tools\Install-ExchangeAdminWeb.ps1'
    $script:Installer = Get-Content $script:InstallerPath -Raw
}

Describe 'A36: the pre-upgrade config database backup' {

    It 'backs up before the new binaries are mirrored' {
        # THE FINDING. A snapshot taken after the binaries land is not a snapshot of the
        # state they are about to migrate.
        $backupAt = $script:Installer.IndexOf('Back up the config database before upgrading')
        $mirrorAt = $script:Installer.IndexOf('Invoke-RobocopyChecked -Description "Deploy application files"')

        $backupAt | Should -BeGreaterThan 0
        $mirrorAt | Should -BeGreaterThan 0
        $backupAt | Should -BeLessThan $mirrorAt
    }

    It 'backs up before the app pool is stopped, so a failure aborts before anything is touched' {
        $backupAt = $script:Installer.IndexOf('Back up the config database before upgrading')
        $stopAt = $script:Installer.IndexOf('Stop app pool $AppPoolName and confirm it stopped')

        $backupAt | Should -BeLessThan $stopAt
    }

    It 'treats a missing app binary as fatal, not as a warning' {
        # The whole point of the backup is the upgrade that goes wrong. Warning and
        # proceeding would reproduce the finding with extra output. The action-mode resolve
        # (no -PlanOnly) is the one that throws naming the missing file; the backup then runs
        # the tool from the same staging folder.
        $script:Installer | Should -Match 'Resolve-ConfigDbTool -AppPath \$stagingPath(?! -PlanOnly)'
        $script:Installer | Should -Match 'Backup-SqliteConfigDb -DbPath \$existingDb -DestDir \$backupDir -Timestamp \$backupStamp -AppPath \$stagingPath'
        $script:Installer | Should -Not -Match 'Assert-Sqlite3Available'
    }

    It 'checks the published build for the app binary before deciding whether there is a database to back up' {
        # cdt-2. The action-mode resolve used to sit below the "nothing to back up" return,
        # so a fresh install with no ExchangeAdminWeb.dll in staging mirrored it anyway. The
        # no-database path skips the backup, not the payload check.
        $tokens = $null
        $errors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile(
            (Resolve-Path $script:InstallerPath).Path, [ref]$tokens, [ref]$errors)
        @($errors).Count | Should -Be 0

        $commands = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true)
        $actionResolves = @($commands | Where-Object {
                $_.GetCommandName() -eq 'Resolve-ConfigDbTool' -and
                $_.Extent.Text -match '-AppPath \$stagingPath' -and
                $_.Extent.Text -notmatch '-PlanOnly'
            })
        $actionResolves.Count | Should -Be 1 -Because 'one action-mode resolve from staging, reused by the whole run'
        $resolve = $actionResolves[0]

        $publish = @($commands | Where-Object {
                $_.GetCommandName() -eq 'Invoke-PlanOrAction' -and $_.Extent.Text -like '*"dotnet publish to staging*'
            })
        $backup = @($commands | Where-Object {
                $_.GetCommandName() -eq 'Invoke-PlanOrAction' -and $_.Extent.Text -like '*"Back up the config database before upgrading*'
            })
        $skipIf = @($ast.FindAll({
                    param($n)
                    $n -is [System.Management.Automation.Language.IfStatementAst] -and
                    $n.Clauses[0].Item1.Extent.Text -eq '$SkipConfigBackup'
                }, $true))
        $publish.Count | Should -Be 1
        $backup.Count | Should -Be 1
        $skipIf.Count | Should -Be 1

        # After publish has finished, before the backup/skip decision begins.
        $resolve.Extent.StartOffset | Should -BeGreaterThan $publish[0].Extent.EndOffset
        $resolve.Extent.StartOffset | Should -BeLessThan $skipIf[0].Extent.StartOffset

        # Not nested in the backup step, so neither its no-database return nor -SkipConfigBackup
        # can bypass it.
        $parent = $resolve.Parent
        while ($parent) {
            $parent | Should -Not -Be $backup[0]
            $parent | Should -Not -Be $skipIf[0]
            $parent = $parent.Parent
        }

        # Invariant 4: the new step is an Invoke-PlanOrAction step, so -PlanOnly runs nothing.
        $wrapper = $resolve.Parent
        while ($wrapper -and -not ($wrapper -is [System.Management.Automation.Language.CommandAst])) {
            $wrapper = $wrapper.Parent
        }
        $wrapper | Should -Not -BeNullOrEmpty
        $wrapper.GetCommandName() | Should -Be 'Invoke-PlanOrAction'
    }

    It 'prints the intended tool path in plan mode without requiring it to exist' {
        # AC7: plan mode runs before publish, so the staging folder does not exist yet.
        $script:Installer | Should -Match '\$configDbToolPlan = Resolve-ConfigDbTool -AppPath \$stagingPath -PlanOnly'
        $script:Installer | Should -Match 'Invoke-PlanOrAction "Back up the config database before upgrading \(via \$\(\$configDbToolPlan\.Summary\)'
    }

    It 'resolves the database the way the app does, honouring ConfigStore:Path' {
        # A shared database lives outside the publish folder. Backing up
        # <PublishPath>\config\exchangeadmin.db unconditionally would snapshot the wrong
        # file, or nothing at all, and report success.
        $script:Installer | Should -Match 'Resolve-ConfigDbPath -PublishPath \$PublishPath'
    }

    It 'refuses when ConfigStore:Path names a database that is not there' {
        # Review of aud-a36. A missing file with the key SET is not a fresh install - it is
        # a broken instance. The app refuses to start against a configured database it did
        # not find and never creates one, so proceeding mirrors new binaries over something
        # that cannot start, with no snapshot, and reports success.
        #
        # Matched as condition-plus-Write-Fail: asserting the message alone would pass with
        # the branch dead. Since cdt-11 the key comes from the plan decided before the
        # appsettings.json write, not from a re-read of the file (pinned by the cdt-11 tests).
        $script:Installer | Should -Match '\$configuredDbPath = \$configStorePlan\.Path'
        $script:Installer | Should -Match '(?s)if \(\$configuredDbPath\) \{\s*Write-Fail'

        # The refusal must precede the harmless "nothing to back up" exit, or it never runs.
        $failAt = $script:Installer.IndexOf('if ($configuredDbPath) {')
        $okAt = $script:Installer.IndexOf('No existing config database at')
        $failAt | Should -BeGreaterThan 0
        $failAt | Should -BeLessThan $okAt
    }

    It 'tells a dry run that -SkipConfigBackup means no snapshot' {
        # Review of aud-a36: the skip was decided inside the action block, so -PlanOnly
        # printed a backup step it would not perform and an operator could approve a plan
        # believing a snapshot was coming.
        $script:Installer | Should -Match '(?s)if \(\$SkipConfigBackup\) \{\s*if \(\$PlanOnly\) \{\s*Write-Plan'
        $script:Installer | Should -Match 'SKIP the config database backup entirely'
    }

    It 'narrows the publish ACL only when asked, and keeps Modify where the app writes' {
        # Operational hardening (lower-priority Wave 4). Opt-in deliberately: a permission
        # reduction that cannot be validated from a build machine must not reach every
        # installation silently - it would fail only after a pool restart, in an
        # environment nobody connects to the deploy.
        $script:Installer | Should -Match '\[switch\]\$NarrowPublishAcl'
        $script:Installer | Should -Match 'if \(\$NarrowPublishAcl\) \{ "\(OI\)\(CI\)RX" \} else \{ "\(OI\)\(CI\)M" \}'

        # The publish root is the ONLY path that narrows. logs, config and the shared
        # config directory are where the app actually writes.
        $script:Installer | Should -Match 'Set-DirectoryAcl -Path \$LogRoot -Identity \$appPoolIdentity -Rights "\(OI\)\(CI\)M"'
        $script:Installer | Should -Match 'Set-DirectoryAcl -Path \$configDir -Identity \$appPoolIdentity -Rights "\(OI\)\(CI\)M"'
    }

    It 'makes skipping the snapshot an explicit switch' {
        $script:Installer | Should -Match '\[switch\]\$SkipConfigBackup'
        $script:Installer | Should -Match 'if \(\$SkipConfigBackup\)'
    }

    It 'supports -PlanOnly like every other step' {
        # Architectural Invariant 4.
        $script:Installer | Should -Match 'Invoke-PlanOrAction "Back up the config database before upgrading[ "]'
    }
}

Describe 'A36: the installer no longer claims it cannot touch SQLite' {

    It 'has dropped the stale "does not speak SQLite" claim' {
        # Drift: the sentence was true until this finding was fixed. Leaving it would tell
        # the next reader the opposite of what the code does.
        $script:Installer | Should -Not -Match 'it does not speak SQLite or couple to'
    }

    It 'still states the standalone and environment-neutral position' {
        # The claim was narrowed, not deleted - the invariant it describes still holds.
        $script:Installer | Should -Match 'environment-neutral and standalone'
    }
}

Describe 'cdt-11: ConfigStore:Path survives -RegenerateAppSettings, and the backup snapshots the database decided before the write' {
    # -RegenerateAppSettings rebuilt appsettings.json from parameters alone, so an omitted
    # -ConfigStorePath silently dropped the key that names a shared database, and the backup then
    # re-read the rewritten file and snapshotted the stale per-instance copy. Unlike the A36 guards
    # above, these drive the installer's own functions (lifted by AST) against real files, then pin
    # by AST where the script calls them.

    BeforeAll {
        Import-Module (Join-Path $PSScriptRoot '..\..\tools\SqliteConfigBackup.psm1') -Force
        $tokens = $null
        $errors = $null
        $script:InstallerAst = [System.Management.Automation.Language.Parser]::ParseFile(
            (Resolve-Path $script:InstallerPath).Path, [ref]$tokens, [ref]$errors)
        foreach ($name in 'Write-Ok', 'Write-Plan', 'Write-Fail', 'Invoke-PlanOrAction', 'Write-AppSettings', 'New-AppSettingsObject', 'Resolve-ConfigStorePlan') {
            $fn = $script:InstallerAst.Find({
                    param($n)
                    $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name
                }, $true)
            if ($null -eq $fn) { throw "the installer defines no function $name" }
            . ([scriptblock]::Create($fn.Extent.Text))
        }

        # What the lifted functions read from the installer's script scope (each It sets
        # $script:RegenerateAppSettings itself).
        $script:PlanOnly = $false
        $script:CertSubject = ''; $script:SmtpHost = 'localhost'; $script:SmtpPort = 25; $script:SmtpUseSsl = $false
        $script:FromAddress = 'a@example.test'; $script:FromName = 'x'; $script:AdminNotificationEmail = 'a@example.test'
        $script:DelineaUrl = ''; $script:DelineaCredentialTarget = 'Delinea_Client'; $script:OnPremExchangeServerUri = ''
        $script:ContactEmail = 'a@example.test'; $script:PublicBaseUrl = ''

        function Initialize-TestInstance {
            # A publish folder and, optionally, an existing appsettings.json naming $Key. The two
            # database paths need not exist: nothing here opens them.
            param([string]$Key, [switch]$NoAppSettings, [switch]$Malformed)
            $root = Join-Path ([System.IO.Path]::GetTempPath()) ('cdt11_' + [guid]::NewGuid().ToString('N'))
            $publish = Join-Path $root 'publish'
            New-Item -ItemType Directory -Path (Join-Path $publish 'config') -Force | Out-Null
            $appSettings = Join-Path $publish 'appsettings.json'
            if ($Malformed) {
                Set-Content -LiteralPath $appSettings -Encoding UTF8 -Value '{ "ConfigStore": { "Path": '
            } elseif (-not $NoAppSettings) {
                $json = [ordered]@{ Legacy = 1; AllowedHosts = 'old' }
                if ($Key) { $json['ConfigStore'] = [ordered]@{ Path = $Key } }
                Set-Content -LiteralPath $appSettings -Encoding UTF8 -Value ($json | ConvertTo-Json -Depth 5)
            }
            return [pscustomobject]@{
                Root        = $root
                Publish     = $publish
                AppSettings = $appSettings
                Default     = Join-Path (Join-Path $publish 'config') 'exchangeadmin.db'
            }
        }

        function Invoke-InstallerWrite {
            # The installer's own call, with the plan's path, exactly as the script makes it.
            param($Instance, $Plan)
            Write-AppSettings -Path $Instance.AppSettings -Settings (New-AppSettingsObject `
                    -Name 'n' -BasePath '/x' -LogPath 'C:\logs' `
                    -SecurityAllowedGroups @('CONTOSO\g') -SecurityAdminGroups @('CONTOSO\g') `
                    -ConfigStorePath $Plan.Path)
        }

        function Get-NamedArgument {
            param($Command, [string]$Name)
            $elements = $Command.CommandElements
            for ($i = 1; $i -lt $elements.Count; $i++) {
                $element = $elements[$i]
                if ($element -is [System.Management.Automation.Language.CommandParameterAst] -and $element.ParameterName -eq $Name) {
                    if ($null -ne $element.Argument) { return $element.Argument.Extent.Text }
                    if ($i + 1 -lt $elements.Count) { return $elements[$i + 1].Extent.Text }
                }
            }
            return $null
        }

        $script:Shared = 'D:\shared\config\exchangeadmin.db'
        $script:Other = 'E:\elsewhere\config\exchangeadmin.db'
    }

    It 'keeps the existing key when -RegenerateAppSettings is passed without -ConfigStorePath' {
        $t = Initialize-TestInstance -Key $script:Shared
        try {
            $script:RegenerateAppSettings = $true
            $plan = Resolve-ConfigStorePlan -PublishPath $t.Publish -SuppliedPath '' -RegenerateAppSettings $true
            $plan.Source | Should -BeExactly 'Existing'
            $plan.Path | Should -BeExactly $script:Shared
            $plan.DbPath | Should -BeExactly $script:Shared -Because 'the backup must snapshot the shared database, not the stale per-instance copy'
            $plan.WritesAppSettings | Should -BeTrue
            $plan.Summary | Should -BeExactly "$($script:Shared) (shared, ConfigStore:Path kept from the existing appsettings.json)"

            Invoke-InstallerWrite -Instance $t -Plan $plan
            Get-ConfigStorePathSetting -PublishPath $t.Publish | Should -BeExactly $script:Shared
            $names = @((Get-Content -LiteralPath $t.AppSettings -Raw | ConvertFrom-Json).PSObject.Properties.Name)
            $names | Should -Contain 'Serilog' -Because 'every other value is still regenerated'
            $names | Should -Not -Contain 'Legacy' -Because 'only the key is carried over'
            @(Get-ChildItem -LiteralPath $t.Publish -Filter 'appsettings.json.bak.*').Count | Should -Be 1
        } finally {
            Remove-Item -LiteralPath $t.Root -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'lets a supplied -ConfigStorePath win when the file is written' {
        $t = Initialize-TestInstance -Key $script:Shared
        try {
            $script:RegenerateAppSettings = $true
            $plan = Resolve-ConfigStorePlan -PublishPath $t.Publish -SuppliedPath $script:Other -RegenerateAppSettings $true
            $plan.Source | Should -BeExactly 'Supplied'
            $plan.Path | Should -BeExactly $script:Other
            $plan.DbPath | Should -BeExactly $script:Other -Because 'the new binaries open, and may migrate, the supplied database'
            Invoke-InstallerWrite -Instance $t -Plan $plan
            Get-ConfigStorePathSetting -PublishPath $t.Publish | Should -BeExactly $script:Other
        } finally {
            Remove-Item -LiteralPath $t.Root -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'writes no key, and backs up the per-instance database, when there is none to keep and none supplied' {
        $t = Initialize-TestInstance
        try {
            $script:RegenerateAppSettings = $true
            $plan = Resolve-ConfigStorePlan -PublishPath $t.Publish -SuppliedPath '' -RegenerateAppSettings $true
            $plan.Source | Should -BeExactly 'None'
            $plan.Path | Should -BeNullOrEmpty
            $plan.DbPath | Should -BeExactly $t.Default
            $plan.Summary | Should -BeExactly "$($t.Default) (this instance only)"
            Invoke-InstallerWrite -Instance $t -Plan $plan
            Get-ConfigStorePathSetting -PublishPath $t.Publish | Should -BeNullOrEmpty
            @((Get-Content -LiteralPath $t.AppSettings -Raw | ConvertFrom-Json).PSObject.Properties.Name) | Should -Not -Contain 'ConfigStore'
        } finally {
            Remove-Item -LiteralPath $t.Root -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'on a fresh install writes the supplied key, or none' {
        $t = Initialize-TestInstance -NoAppSettings
        try {
            $script:RegenerateAppSettings = $false
            $none = Resolve-ConfigStorePlan -PublishPath $t.Publish -SuppliedPath '' -RegenerateAppSettings $false
            $none.Source | Should -BeExactly 'None'
            $none.WritesAppSettings | Should -BeTrue -Because 'there is no file to keep'
            $none.DbPath | Should -BeExactly $t.Default

            $supplied = Resolve-ConfigStorePlan -PublishPath $t.Publish -SuppliedPath $script:Shared -RegenerateAppSettings $false
            $supplied.Source | Should -BeExactly 'Supplied'
            Invoke-InstallerWrite -Instance $t -Plan $supplied
            Get-ConfigStorePathSetting -PublishPath $t.Publish | Should -BeExactly $script:Shared
        } finally {
            Remove-Item -LiteralPath $t.Root -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'leaves a kept file untouched, and its own key is the database the plan reports' {
        $t = Initialize-TestInstance -Key $script:Shared
        try {
            $script:RegenerateAppSettings = $false
            $bytes = [System.IO.File]::ReadAllBytes($t.AppSettings)
            $plan = Resolve-ConfigStorePlan -PublishPath $t.Publish -SuppliedPath '' -RegenerateAppSettings $false
            $plan.Source | Should -BeExactly 'Existing'
            $plan.WritesAppSettings | Should -BeFalse
            $plan.DbPath | Should -BeExactly $script:Shared
            Invoke-InstallerWrite -Instance $t -Plan $plan
            [System.IO.File]::ReadAllBytes($t.AppSettings) | Should -Be $bytes -Because 'the file is preserved byte for byte'

            # A supplied path is not written into a kept file, so the instance still opens the
            # file's own database - which is therefore the one the backup must snapshot.
            $kept = Resolve-ConfigStorePlan -PublishPath $t.Publish -SuppliedPath $script:Other -RegenerateAppSettings $false
            $kept.DbPath | Should -BeExactly $script:Shared
        } finally {
            Remove-Item -LiteralPath $t.Root -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'stops on an appsettings.json it cannot parse, before writing anything' {
        $t = Initialize-TestInstance -Malformed
        try {
            $bytes = [System.IO.File]::ReadAllBytes($t.AppSettings)
            { Resolve-ConfigStorePlan -PublishPath $t.Publish -SuppliedPath '' -RegenerateAppSettings $true } | Should -Throw
            [System.IO.File]::ReadAllBytes($t.AppSettings) | Should -Be $bytes
        } finally {
            Remove-Item -LiteralPath $t.Root -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'decides it once, at script level, before anything changes, and nothing re-reads the key from disk (AST)' {
        $ast = $script:InstallerAst
        $commands = @($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true))
        $functions = @($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true))
        $inFunction = {
            param($node)
            foreach ($f in $functions) {
                if ($node.Extent.StartOffset -ge $f.Extent.StartOffset -and $node.Extent.EndOffset -le $f.Extent.EndOffset) { return $f.Name }
            }
            return $null
        }

        $resolves = @($commands | Where-Object { $_.GetCommandName() -eq 'Resolve-ConfigStorePlan' })
        $resolves.Count | Should -Be 1 -Because 'the decision is made once'
        $assignment = $resolves[0].Parent.Parent
        $assignment | Should -BeOfType ([System.Management.Automation.Language.AssignmentStatementAst])
        $assignment.Extent.Text | Should -BeExactly '$configStorePlan = Resolve-ConfigStorePlan -PublishPath $PublishPath -SuppliedPath $ConfigStorePath -RegenerateAppSettings $RegenerateAppSettings.IsPresent'
        $assignment.Parent | Should -BeOfType ([System.Management.Automation.Language.NamedBlockAst]) -Because 'a top-level statement runs on every path'
        [object]::ReferenceEquals($assignment.Parent.Parent, $ast) | Should -BeTrue

        $import = @($commands | Where-Object { $_.GetCommandName() -eq 'Import-Module' -and $_.Extent.Text -like '*SqliteConfigBackup.psm1*' })
        $import.Count | Should -Be 1
        $import[0].Extent.EndOffset | Should -BeLessThan $assignment.Extent.StartOffset -Because 'the reader must be loaded first'
        # The script-level check (New-AppSettingsObject tests the same condition for its own use).
        $validation = @($ast.FindAll({
                    param($n)
                    $n -is [System.Management.Automation.Language.IfStatementAst] -and
                    $n.Clauses[0].Item1.Extent.Text -eq '-not [string]::IsNullOrWhiteSpace($ConfigStorePath)'
                }, $true) | Where-Object { -not (& $inFunction $_) })
        $validation.Count | Should -Be 1
        $validation[0].Extent.EndOffset | Should -BeLessThan $assignment.Extent.StartOffset -Because 'the supplied path is validated and made absolute first'

        $stateChanges = @($commands | Where-Object {
                $_.GetCommandName() -in @('Invoke-PlanOrAction', 'Set-DirectoryAcl', 'Write-AppSettings', 'Write-JsonFileIfMissing', 'Invoke-RobocopyChecked', 'Write-DeploymentManifest', 'Remove-Item', 'New-Item', 'Set-Content', 'Copy-Item') -and
                -not (& $inFunction $_)
            })
        $stateChanges.Count | Should -BeGreaterThan 5
        $firstChange = ($stateChanges | ForEach-Object { $_.Extent.StartOffset } | Measure-Object -Minimum).Minimum
        $assignment.Extent.EndOffset | Should -BeLessThan $firstChange -Because 'it is decided before anything is changed or written'
        $banner = @($commands | Where-Object { $_.GetCommandName() -eq 'Write-Host' -and $_.Extent.Text -like '*Config DB*' })
        $banner.Count | Should -Be 1
        $banner[0].Extent.Text | Should -BeExactly 'Write-Host "  Config DB   : $($configStorePlan.Summary)" -ForegroundColor DarkGray'
        $banner[0].Extent.StartOffset | Should -BeGreaterThan $assignment.Extent.EndOffset

        $writes = @($commands | Where-Object { $_.GetCommandName() -eq 'Write-AppSettings' -and -not (& $inFunction $_) })
        $writes.Count | Should -Be 1
        $generate = @($writes[0].FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'New-AppSettingsObject' }, $true))
        $generate.Count | Should -Be 1
        Get-NamedArgument $generate[0] 'ConfigStorePath' | Should -BeExactly '$configStorePlan.Path' -Because 'the write carries the decided key'

        $readers = @($commands | Where-Object { $_.GetCommandName() -in @('Get-ConfigStorePathSetting', 'Resolve-ConfigDbPath') })
        $readers.Count | Should -BeGreaterThan 0
        foreach ($reader in $readers) {
            & $inFunction $reader | Should -BeExactly 'Resolve-ConfigStorePlan' -Because "line $($reader.Extent.StartLineNumber) would re-read ConfigStore:Path from a file this run may have rewritten"
        }

        $backup = @($commands | Where-Object { $_.GetCommandName() -eq 'Invoke-PlanOrAction' -and $_.Extent.Text -like '*"Back up the config database before upgrading*' })
        $backup.Count | Should -Be 1
        $backup[0].Extent.Text | Should -Match ([regex]::Escape('$existingDb = $configStorePlan.DbPath'))
        $backup[0].Extent.Text | Should -Match ([regex]::Escape('$configuredDbPath = $configStorePlan.Path'))
        $snapshots = @($backup[0].FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Backup-SqliteConfigDb' }, $true))
        $snapshots.Count | Should -Be 1
        Get-NamedArgument $snapshots[0] 'DbPath' | Should -BeExactly '$existingDb'
        foreach ($name in 'existingDb', 'configuredDbPath') {
            @($ast.FindAll({
                        param($n)
                        $n -is [System.Management.Automation.Language.AssignmentStatementAst] -and
                        $n.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and
                        $n.Left.VariablePath.UserPath -eq $name
                    }, $true)).Count | Should -Be 1 -Because "`$$name is set once, from the plan"
        }
    }

    It 'a -PlanOnly run prints the kept key and creates nothing (Windows PowerShell 5.1, the installer runtime)' {
        $t = Initialize-TestInstance -Key 'placeholder'
        # A real file, so the plan sees an instance that could run.
        $shared = Join-Path $t.Root 'shared\exchangeadmin.db'
        New-Item -ItemType Directory -Path (Split-Path -Parent $shared) -Force | Out-Null
        Set-Content -LiteralPath $shared -Value 'placeholder' -Encoding ascii
        Set-Content -LiteralPath $t.AppSettings -Encoding UTF8 -Value (@{ AllowedHosts = '*'; ConfigStore = @{ Path = $shared } } | ConvertTo-Json -Depth 5)
        $logs = Join-Path $t.Root 'logs'
        # A name no IIS pool or application on the machine running this can already have.
        $alias = 'cdt11' + [guid]::NewGuid().ToString('N').Substring(0, 12)
        # Paths and file contents, not timestamps: a directory's write time, read back by enumeration,
        # lagged the file just created in it by one clock tick often enough to fail this test (2 of
        # 10 runs alone). A plan that creates, deletes or changes anything still changes this.
        $snapshot = {
            param($Root)
            @(Get-ChildItem -LiteralPath $Root -Recurse -Force | Sort-Object FullName | ForEach-Object {
                    if ($_.PSIsContainer) {
                        '{0}|dir' -f $_.FullName.Substring($Root.Length)
                    } else {
                        '{0}|{1}|{2}' -f $_.FullName.Substring($Root.Length), $_.Length, (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
                    }
                }) -join "`n"
        }
        try {
            $before = & $snapshot $t.Root
            $out = & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Resolve-Path $script:InstallerPath).Path `
                -PlanOnly -NonInteractive -RegenerateAppSettings -PublishPath $t.Publish -LogRoot $logs `
                -AppAlias $alias -AppPoolName $alias -AdminGroups 'CONTOSO\Exchange Admins' 2>&1
            $exitCode = $LASTEXITCODE
            $text = @($out | ForEach-Object { "$_" }) -join "`n"
            $exitCode | Should -Be 0 -Because $text
            $text | Should -Match ([regex]::Escape("Config DB   : $shared (shared, ConfigStore:Path kept from the existing appsettings.json)"))
            $text | Should -Match ([regex]::Escape("PLAN Write appsettings.json at $($t.AppSettings) with ConfigStore:Path = $shared"))
            $text | Should -Match ('PLAN Back up the config database before upgrading \(via [^\r\n]*\): ' + [regex]::Escape($shared))
            $text | Should -Match 'Plan only\. No changes were made\.'
            & $snapshot $t.Root | Should -BeExactly $before -Because 'a plan creates and changes nothing'
        } finally {
            Remove-Item -LiteralPath $t.Root -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

Describe 'cdt-18: the pool restarts only after a complete mirror, confirmed, and a failed restart never hides the real error' {
    # The mirror ran in a try whose finally called Start-WebAppPool unconditionally: a mirror that
    # failed part-way was followed by a restart over a mix of builds, a start that threw replaced the
    # mirror failure, and a start IIS accepted but never completed went unnoticed.

    BeforeAll {
        $tokens = $null
        $errors = $null
        $script:Cdt18Ast = [System.Management.Automation.Language.Parser]::ParseFile(
            (Resolve-Path $script:InstallerPath).Path, [ref]$tokens, [ref]$errors)
        $script:Cdt18Statements = @($script:Cdt18Ast.EndBlock.Statements)
        $script:Cdt18TryAt = -1
        for ($i = 0; $i -lt $script:Cdt18Statements.Count; $i++) {
            $st = $script:Cdt18Statements[$i]
            if ($st -is [System.Management.Automation.Language.TryStatementAst] -and
                $st.Body.Extent.Text -match 'Invoke-RobocopyChecked -Description "Deploy application files"') { $script:Cdt18TryAt = $i }
        }
    }

    It 'gates the confirmed restart on a complete mirror and keeps every failure its own (AST)' {
        $statements = $script:Cdt18Statements
        $script:Cdt18TryAt | Should -BeGreaterThan 1 -Because 'the mirror runs in a try at script level'
        $mirror = $statements[$script:Cdt18TryAt]

        $statements[$script:Cdt18TryAt - 2].Extent.Text | Should -BeExactly '$mirrorSucceeded = $false'
        $statements[$script:Cdt18TryAt - 1].Extent.Text | Should -BeExactly '$poolStartError = $null'

        $body = @($mirror.Body.Statements)
        $body.Count | Should -Be 2
        $body[0].Extent.Text | Should -BeLike 'Invoke-RobocopyChecked -Description "Deploy application files"*'
        $body[1].Extent.Text | Should -BeExactly '$mirrorSucceeded = $true' -Because 'the mirror counts as done only once the checked runner has returned'
        @($script:Cdt18Ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.AssignmentStatementAst] -and $n.Extent.Text -eq '$mirrorSucceeded = $true' }, $true)).Count | Should -Be 1

        $mirror.CatchClauses.Count | Should -Be 1
        $failure = $mirror.CatchClauses[0].Body.Statements[0].Extent.Text
        $failure | Should -BeLike 'Write-Fail "$($_.Exception.Message)*' -Because 'the original failure leads the message'
        foreach ($fact in 'LEFT STOPPED', '$PublishPath', '$stagingPath', 'ConfigBackups') {
            $failure | Should -Match ([regex]::Escape($fact)) -Because "a failed mirror must say what it left and what to do ($fact)"
        }

        $starts = @($mirror.Finally.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Start-AppPoolVerified' }, $true))
        $starts.Count | Should -Be 1 -Because 'the restart is confirmed, not assumed (A38)'
        @($script:Cdt18Ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Start-WebAppPool' }, $true)).Count |
            Should -Be 0 -Because 'an unconfirmed start reported a down application as installed'
        $chain = @()
        $node = $starts[0].Parent
        while ($null -ne $node -and -not [object]::ReferenceEquals($node, $mirror)) { $chain += $node; $node = $node.Parent }
        $gate = @($chain | Where-Object { $_ -is [System.Management.Automation.Language.IfStatementAst] })
        $gate.Count | Should -Be 1
        $gate[0].Clauses[0].Item1.Extent.Text | Should -BeExactly '$mirrorSucceeded' -Because 'no restart over a partial publish'
        ($starts[0].Extent.StartOffset -ge $gate[0].Clauses[0].Item2.Extent.StartOffset -and $starts[0].Extent.EndOffset -le $gate[0].Clauses[0].Item2.Extent.EndOffset) | Should -BeTrue
        $plan = @($chain | Where-Object { $_ -is [System.Management.Automation.Language.CommandAst] -and $_.GetCommandName() -eq 'Invoke-PlanOrAction' })
        $plan.Count | Should -Be 1 -Because 'the restart is still a plan step (Invariant 4)'
        $guards = @($chain | Where-Object { $_ -is [System.Management.Automation.Language.TryStatementAst] })
        $guards.Count | Should -Be 1 -Because 'the restart has its own try inside the finally'
        $guards[0].CatchClauses.Count | Should -Be 1
        $catch = $guards[0].CatchClauses[0].Body
        $catch.Extent.Text | Should -Match ([regex]::Escape('$poolStartError = $_.Exception.Message'))
        @($catch.FindAll({ param($n)
                    $n -is [System.Management.Automation.Language.ThrowStatementAst] -or
                    ($n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Write-Fail') }, $true)).Count |
            Should -Be 0 -Because 'a throw from the finally would replace the error that is propagating'

        $checks = @(for ($j = $script:Cdt18TryAt + 1; $j -lt $statements.Count; $j++) {
                if ($statements[$j] -is [System.Management.Automation.Language.IfStatementAst] -and $statements[$j].Clauses[0].Item1.Extent.Text -eq '$poolStartError') { $statements[$j] }
            })
        $checks.Count | Should -Be 1 -Because 'a failed restart is its own failure once the files are deployed'
        $checks[0].Clauses[0].Item2.Statements[0].Extent.Text | Should -BeLike 'Write-Fail "The files are deployed, but app pool*'
        $done = $script:Cdt18Ast.Extent.Text.IndexOf('Write-Ok "Install/update complete."')
        $done | Should -BeGreaterThan $checks[0].Extent.EndOffset -Because 'the check comes before the success banner'
    }

    It 'runs the real region: a failed mirror never starts the pool, and an unconfirmed start fails the run (behavioural)' {
        foreach ($name in 'Write-Ok', 'Write-Warn', 'Write-Plan', 'Write-Fail', 'Invoke-PlanOrAction') {
            $fn = $script:Cdt18Ast.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name }, $true)
            . ([scriptblock]::Create($fn.Extent.Text))
        }
        $statements = $script:Cdt18Statements
        $from = $script:Cdt18TryAt
        while ($from -gt 0 -and $statements[$from - 1].Extent.Text -match '^\$(mirrorSucceeded|poolStartError) = ') { $from-- }
        $to = $script:Cdt18TryAt
        for ($j = $script:Cdt18TryAt + 1; $j -lt $statements.Count; $j++) {
            if ($statements[$j].Extent.Text -match '^if \(\$poolStartError\)') { $to = $j; break }
        }
        $region = ($statements[$from..$to] | ForEach-Object { $_.Extent.Text }) -join "`n"

        ${function:Invoke-RobocopyChecked} = { param($Arguments, $Description) $null = $Arguments; $script:cdt18Calls.Add('robocopy'); if ($script:cdt18MirrorFails) { Write-Fail "robocopy failed with exit code 8 during: $Description" } }
        ${function:Start-AppPoolVerified} = { param($Name) $script:cdt18Calls.Add('start'); if ($script:cdt18StartFails) { throw "App pool '$Name' is 'Stopped' after 60 seconds, not Started. The application is DOWN." }; 'Started' }
        ${function:Start-WebAppPool} = { param($Name) $null = $Name; $script:cdt18Calls.Add('unverified-start') }
        function Write-DeploymentManifest { $script:cdt18Calls.Add('manifest') }

        $run = {
            param([bool]$MirrorFails, [bool]$StartFails, [bool]$Plan)
            $script:cdt18Calls = New-Object System.Collections.Generic.List[string]
            $script:cdt18MirrorFails = $MirrorFails
            $script:cdt18StartFails = $StartFails
            $root = Join-Path ([System.IO.Path]::GetTempPath()) ('cdt18_' + [guid]::NewGuid().ToString('N'))
            # Read only by the lifted region.
            Set-Variable -Name stagingPath -Value (Join-Path $root 'publish.staging.x')
            Set-Variable -Name PublishPath -Value (Join-Path $root 'publish')
            Set-Variable -Name LogRoot -Value (Join-Path $root 'logs')
            Set-Variable -Name repoRoot -Value (Join-Path $root 'source')
            Set-Variable -Name AppPoolName -Value 'ExamplePool'
            Set-Variable -Name PlanOnly -Value $Plan
            New-Item -ItemType Directory -Path (Join-Path $root 'publish.staging.x'), (Join-Path $root 'publish') -Force | Out-Null
            $ErrorActionPreference = 'Stop'
            $thrown = $null
            try {
                . ([scriptblock]::Create($region))
            } catch {
                $thrown = $_.Exception.Message
            } finally {
                Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
            }
            [pscustomobject]@{ Thrown = $thrown; Calls = ($script:cdt18Calls -join ',') }
        }

        foreach ($startFails in $false, $true) {
            $failed = & $run $true $startFails $false 6>$null
            $failed.Calls | Should -BeExactly 'robocopy' -Because 'a failed mirror must not restart the pool over a partial publish'
            $failed.Thrown | Should -BeLike 'robocopy failed with exit code 8 during: Deploy application files.*LEFT STOPPED*' -Because 'the mirror failure is the error, and it says what to do'
        }

        $down = & $run $false $true $false 6>$null
        $down.Calls | Should -BeExactly 'robocopy,start,manifest' -Because 'the deploy is complete, so the manifest is still written'
        $down.Thrown | Should -BeLike "The files are deployed, but app pool ExamplePool did not start (App pool 'ExamplePool' is 'Stopped'*DOWN*"

        $clean = & $run $false $false $false 6>$null
        $clean.Thrown | Should -BeNullOrEmpty
        $clean.Calls | Should -BeExactly 'robocopy,start,manifest'

        $planned = & $run $false $false $true 6>$null
        $planned.Thrown | Should -BeNullOrEmpty
        $planned.Calls | Should -Not -Match 'start' -Because '-PlanOnly prints the restart and runs nothing'
    }
}

Describe 'cdt-15: an update refuses to move an existing application to another pool or folder' {
    # "Create or update IIS web application" set an EXISTING application's pool and folder to this
    # run's -AppPoolName and -PublishPath, which default to fixed values when omitted: an update
    # without them moved the live application to a new pool (new identity) and/or a new folder (a
    # fresh, default-config instance).

    BeforeAll {
        $tokens = $null
        $errors = $null
        $script:Cdt15Ast = [System.Management.Automation.Language.Parser]::ParseFile(
            (Resolve-Path $script:InstallerPath).Path, [ref]$tokens, [ref]$errors)
        foreach ($name in 'Write-Fail', 'ConvertTo-InstallerComparablePath', 'Assert-ApplicationTargetUnchanged') {
            $fn = $script:Cdt15Ast.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name }, $true)
            if ($null -eq $fn) { throw "the installer defines no function $name" }
            . ([scriptblock]::Create($fn.Extent.Text))
        }
    }

    It 'passes a fresh install and an in-place update, however IIS spells the folder' {
        { Assert-ApplicationTargetUnchanged -Location 'Site/App' -Exists $false -ExistingPool '' -ExistingPhysicalPath '' -AppPoolName 'Pool' -PublishPath 'D:\apps\App' } | Should -Not -Throw
        foreach ($spelling in 'D:\apps\App', 'd:\APPS\app', 'D:\apps\App\', 'D:/apps/App', 'D:\apps\x\..\App') {
            { Assert-ApplicationTargetUnchanged -Location 'Site/App' -Exists $true -ExistingPool 'pool' -ExistingPhysicalPath $spelling -AppPoolName 'Pool' -PublishPath 'D:\apps\App' } |
                Should -Not -Throw -Because "'$spelling' is the same folder, and pool names compare case-insensitively"
        }
        $env:CDT15_ROOT = 'D:\apps'
        try {
            { Assert-ApplicationTargetUnchanged -Location 'Site/App' -Exists $true -ExistingPool 'Pool' -ExistingPhysicalPath '%CDT15_ROOT%\App' -AppPoolName 'Pool' -PublishPath 'D:\apps\App' } |
                Should -Not -Throw -Because 'IIS often stores the folder with an environment variable'
        } finally {
            Remove-Item Env:\CDT15_ROOT -ErrorAction SilentlyContinue
        }
    }

    It 'refuses <Case>, naming what IIS reports and what to re-run with' -TestCases @(
        @{ Case = 'a different pool'; Pool = 'PortalPool'; Folder = 'D:\apps\App'; Expect = @("app pool 'PortalPool'", "-AppPoolName 'Pool'") }
        @{ Case = 'a different folder'; Pool = 'Pool'; Folder = 'D:\apps\Portal'; Expect = @("files at 'D:\apps\Portal'", "-PublishPath 'D:\apps\App'") }
        @{ Case = 'both'; Pool = 'PortalPool'; Folder = 'D:\apps\Portal'; Expect = @("app pool 'PortalPool'", "files at 'D:\apps\Portal'") }
        @{ Case = 'a folder that only starts with the run''s'; Pool = 'Pool'; Folder = 'D:\apps\AppOther'; Expect = @("files at 'D:\apps\AppOther'") }
        @{ Case = 'an existing application with no folder'; Pool = 'Pool'; Folder = ''; Expect = @("files at ''") }
    ) {
        $err = $null
        try { Assert-ApplicationTargetUnchanged -Location 'Site/App' -Exists $true -ExistingPool $Pool -ExistingPhysicalPath $Folder -AppPoolName 'Pool' -PublishPath 'D:\apps\App' } catch { $err = $_ }
        $err | Should -Not -BeNullOrEmpty -Because "an update must not move the live application ($Case)"
        foreach ($text in $Expect) { "$err" | Should -BeLike "*$text*" }
        "$err" | Should -BeLike "*Re-run with -AppPoolName '$Pool' -PublishPath '$Folder'*"
        "$err" | Should -BeLike '*Nothing was changed*'
    }

    It 'reads the application and asserts once, at script level, before anything changes (AST)' {
        $ast = $script:Cdt15Ast
        $statements = @($ast.EndBlock.Statements)
        $find = { param($test) for ($i = 0; $i -lt $statements.Count; $i++) { if (& $test $statements[$i]) { $i } } }

        $reads = @(& $find { param($st) $st -is [System.Management.Automation.Language.TryStatementAst] -and $st.Body.Extent.Text -match ([regex]::Escape('$existingApplication = Get-WebApplication -Name $AppAlias -Site $ParentSite -ErrorAction Stop')) })
        $reads.Count | Should -Be 1 -Because 'the application is read once, at script level'
        $asserts = @(& $find { param($st) $st.Extent.Text -like 'Assert-ApplicationTargetUnchanged *' })
        $asserts.Count | Should -Be 1
        $assert = $statements[$asserts[0]].PipelineElements[0]
        foreach ($pair in @(@('Location', '"$ParentSite/$AppAlias"'), @('Exists', '($null -ne $existingApplication)'), @('ExistingPool', '"$($existingApplication.applicationPool)"'), @('ExistingPhysicalPath', '"$($existingApplication.PhysicalPath)"'), @('AppPoolName', '$AppPoolName'), @('PublishPath', '$PublishPath'))) {
            $element = @($assert.CommandElements)
            $value = $null
            for ($k = 1; $k -lt $element.Count; $k++) {
                if ($element[$k] -is [System.Management.Automation.Language.CommandParameterAst] -and $element[$k].ParameterName -eq $pair[0]) { $value = $element[$k + 1].Extent.Text }
            }
            $value | Should -BeExactly $pair[1] -Because "-$($pair[0]) carries what IIS reported or what this run would set"
        }
        $refusal = @(& $find { param($st) $st -is [System.Management.Automation.Language.IfStatementAst] -and $st.Clauses[0].Item1.Extent.Text -eq '$applicationLookupFailed' })
        $refusal.Count | Should -Be 1
        $statements[$refusal[0]].Extent.Text | Should -Match '(?s)if \(\$PlanOnly\) \{\s*Write-Warn.*\} else \{\s*Write-Fail' -Because 'an unreadable application warns a plan and refuses a real run'

        $import = @(& $find { param($st) $st -is [System.Management.Automation.Language.IfStatementAst] -and $st.Extent.Text -match 'Import-Module WebAdministration' })
        $import.Count | Should -Be 1
        $import[0] | Should -BeLessThan $reads[0] -Because 'the module and the site come first'
        $reads[0] | Should -BeLessThan $refusal[0]
        $refusal[0] | Should -BeLessThan $asserts[0]
        $identity = @(& $find { param($st) $st.Extent.Text -like '$identityPlan = Resolve-AppPoolIdentityPlan*' })
        $asserts[0] | Should -BeLessThan $identity[0] -Because 'nothing about the pool is decided before the target is known to be the existing one'
        $firstChange = @(& $find { param($st) $st.Extent.Text -like 'Invoke-PlanOrAction *' -or $st.Extent.Text -like 'Set-DirectoryAcl *' })[0]
        $asserts[0] | Should -BeLessThan $firstChange -Because 'it refuses before anything is changed'
    }
}
