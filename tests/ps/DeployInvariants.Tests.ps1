#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.5' }

<#
Static invariant tests for the ops scripts. Nothing here executes a script:
each test parses the script (AST/text) and asserts the invariants from
AGENTS.md "Architectural Invariants" and docs/ProjectConstitution.md
"Deployment And Versioning".

Regression anchor: commit 0021502 - a robocopy /XD mistake made deploys purge
runtime config. The exclusion tests below exist so that class of incident
cannot ship silently again.
#>

BeforeAll {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path

    function Get-ScriptUnderTest {
        param([string]$RelativePath)
        $path = Join-Path $RepoRoot $RelativePath
        $tokens = $null
        $errors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
        [pscustomobject]@{
            Path   = $path
            Text   = Get-Content -LiteralPath $path -Raw
            Ast    = $ast
            Errors = $errors
        }
    }

    function Get-RobocopyArgumentList {
        param($Script)
        # Every "$robocopyArgs = @(...)" assignment, as its literal string elements.
        $assignments = $Script.Ast.FindAll({
                param($node)
                $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
                $node.Left.Extent.Text -match 'robocopyArgs'
            }, $true)
        foreach ($assignment in $assignments) {
            $strings = $assignment.Right.FindAll({
                    param($node)
                    $node -is [System.Management.Automation.Language.StringConstantExpressionAst]
                }, $true)
            , @($strings | ForEach-Object Value)
        }
    }

    function Find-FunctionDefinition {
        [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', 'Name',
            Justification = 'Used inside the Find predicate scriptblock; analyzer cannot see into it')]
        param($Script, [string]$Name)
        $Script.Ast.Find({
                param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                $node.Name -eq $Name
            }, $true)
    }

    function Test-IcaclsCallsAreChecked {
        # Every icacls invocation must be guarded: the text immediately following the call
        # (up to the next ~150 chars) must reference $LASTEXITCODE. A bare "icacls ... |
        # Out-Null" followed by Write-Success/Write-Ok fails this. A simple count of
        # "$LASTEXITCODE -ne 0" vs icacls calls does NOT work (dotnet publish also checks
        # the var, and the Set-AclChecked helper's own icacls would balance the tally),
        # which is why this checks each call site structurally.
        param($Script)
        $calls = $Script.Ast.FindAll({
                param($node)
                $node -is [System.Management.Automation.Language.CommandAst] -and
                $node.GetCommandName() -eq 'icacls'
            }, $true)
        $unguarded = foreach ($call in $calls) {
            $tail = $Script.Text.Substring(
                $call.Extent.EndOffset,
                [Math]::Min(150, $Script.Text.Length - $call.Extent.EndOffset))
            if ($tail -notmatch '\$LASTEXITCODE') {
                "line $($call.Extent.StartLineNumber): $($call.Extent.Text)"
            }
        }
        [pscustomobject]@{ Count = $calls.Count; Unguarded = @($unguarded) }
    }
}

Describe 'deploy.ps1' {
    BeforeAll { $script:s = Get-ScriptUnderTest 'deploy.ps1' }

    It 'verifies which pool serves the app before stopping one (review of aud-a35)' {
        # Confirming that $AppPoolName stopped proves nothing if the IIS application being
        # mirrored runs under a DIFFERENT pool. A mistyped -AppPoolName creates and stops
        # that wrong pool, and robocopy /MIR then replaces the binaries of an application
        # still serving requests - the A35 failure reached by another door.
        #
        # Both deploy paths, and matched as condition-plus-refusal so the check cannot be
        # left present but dead.
        ([regex]::Matches($s.Text, '\$boundPool = try \{ \(Get-WebApplication')).Count |
            Should -Be 2 -Because 'the update path and the fresh-install path both mirror'
        ([regex]::Matches($s.Text, 'if \(\$boundPool -and \$boundPool -ne \$AppPoolName\)')).Count |
            Should -Be 2 -Because 'each site must refuse, not merely read the binding'
        ([regex]::Matches($s.Text, '(?s)if \(\$boundPool -and \$boundPool -ne \$AppPoolName\) \{\s*Write-Fail')).Count |
            Should -Be 2 -Because 'the condition must lead to a refusal, not sit there dead'

        # And it must come before the stop, not after it.
        $checkAt = $s.Text.IndexOf('$boundPool =')
        $stopAt = $s.Text.IndexOf('Stop-AppPoolVerified')
        $checkAt | Should -BeLessThan $stopAt
    }

    It 'parses without syntax errors' {
        $s.Errors | Should -BeNullOrEmpty
    }

    It 'sets $ErrorActionPreference = Stop' {
        $s.Text | Should -Match '\$ErrorActionPreference\s*=\s*"Stop"'
    }

    It 'imports WebAdministration before touching the IIS: drive' {
        # Without the import, Test-Path IIS:\AppPools\... silently returns false
        # and an upgrade wrongly demands a ServiceAccount for the existing pool.
        $importIdx = $s.Text.IndexOf('Import-Module WebAdministration')
        $firstIisIdx = $s.Text.IndexOf('IIS:\')
        $importIdx | Should -BeGreaterOrEqual 0
        $importIdx | Should -BeLessThan $firstIisIdx
    }

    It 'imports WebAdministration exactly once (the guarded top-of-script import)' {
        ([regex]::Matches($s.Text, 'Import-Module WebAdministration')).Count | Should -Be 1
        $s.Text | Should -Match 'Get-PSDrive -Name IIS' -Because 'the import must keep its IIS:-drive availability guard (PS7 loads the module without the provider)'
    }

    It 'excludes runtime config from every robocopy mirror (regression: commit 0021502)' {
        $arrays = @(Get-RobocopyArgumentList $s)
        $arrays.Count | Should -BeGreaterOrEqual 2 -Because 'both the upgrade and fresh-install paths mirror with robocopy'
        foreach ($robocopyArgs in $arrays) {
            $robocopyArgs | Should -Contain '/MIR'
            $robocopyArgs | Should -Contain '/XF'
            $robocopyArgs | Should -Contain 'appsettings*.json'
            $robocopyArgs | Should -Contain '/XD'
            $robocopyArgs | Should -Contain 'logs'
            $robocopyArgs | Should -Contain 'config'
            # /XD only excludes directories listed after it
            [array]::IndexOf($robocopyArgs, '/XD') | Should -BeLessThan ([array]::IndexOf($robocopyArgs, 'logs'))
            [array]::IndexOf($robocopyArgs, '/XD') | Should -BeLessThan ([array]::IndexOf($robocopyArgs, 'config'))
        }
    }

    It 'checks $LASTEXITCODE -ge 8 after every robocopy invocation' {
        $invocations = $s.Ast.FindAll({
                param($node)
                $node -is [System.Management.Automation.Language.CommandAst] -and
                $node.GetCommandName() -eq 'robocopy'
            }, $true)
        $invocations.Count | Should -BeGreaterThan 0
        [regex]::Matches($s.Text, '\$LASTEXITCODE\s+-ge\s+8').Count |
            Should -BeGreaterOrEqual $invocations.Count
    }

    It 'fails on dotnet publish errors' {
        $s.Text | Should -Match 'dotnet publish failed'
    }

    It 'checks the native exit code after every icacls invocation' {
        # icacls is a native exe: $ErrorActionPreference="Stop" does not catch its
        # failures and "| Out-Null" hides the error text, so an unchecked grant fails
        # silently and the next Write-Success lies. Each icacls call site must be guarded
        # by a $LASTEXITCODE check (icacls returns 0 on success). Bare folder grants are
        # routed through Set-AclChecked, whose own icacls is guarded inline.
        $result = Test-IcaclsCallsAreChecked $s
        $result.Count | Should -BeGreaterThan 0
        $result.Unguarded | Should -BeNullOrEmpty -Because "every icacls call must be followed by a `$LASTEXITCODE check; unguarded: $($result.Unguarded -join '; ')"
    }

    It 'routes icacls through a checked helper that throws on failure' {
        $fn = Find-FunctionDefinition $s 'Set-AclChecked'
        $fn | Should -Not -BeNullOrEmpty
        $fn.Extent.Text | Should -Match '\$LASTEXITCODE\s+-ne\s+0'
        $fn.Extent.Text | Should -Match 'Write-Fail'
    }

    It 'Write-Fail throws (repo error model, not exit)' {
        $fn = Find-FunctionDefinition $s 'Write-Fail'
        $fn | Should -Not -BeNullOrEmpty
        $fn.Find({ param($node) $node -is [System.Management.Automation.Language.ThrowStatementAst] }, $true) |
            Should -Not -BeNullOrEmpty
        $fn.Extent.Text | Should -Not -Match '\bexit\b'
    }

    It 'cleans up the staging folder inside finally blocks (reached on failure paths)' {
        $tries = $s.Ast.FindAll({ param($node)
                $node -is [System.Management.Automation.Language.TryStatementAst] -and
                $null -ne $node.Finally }, $true)
        $cleaning = @($tries | Where-Object { $_.Finally.Extent.Text -match 'Remove-Item \$StagingPath' })
        $cleaning.Count | Should -BeGreaterOrEqual 2 -Because 'both the upgrade and fresh-install paths stage a publish containing live appsettings.json'
    }

    It 'defaults to the DEV site, never prod (incident fix #6)' {
        # Bare .\deploy.ps1 used to target the prod alias/pool/path while the
        # docs called it "the dev deploy". Prod is reached only via the promote
        # pipeline.
        $defaults = @{}
        foreach ($p in $s.Ast.ParamBlock.Parameters) {
            if ($p.DefaultValue -is [System.Management.Automation.Language.StringConstantExpressionAst]) {
                $defaults[$p.Name.VariablePath.UserPath] = $p.DefaultValue.Value
            }
        }
        $defaults['AppAlias'] | Should -Be 'ExchangeAdminWebDev'
        $defaults['AppPoolName'] | Should -Be 'ExchangeAdminWebDev'
        $defaults['PublishPath'] | Should -Be 'D:\inetpub\ExchangeAdminWebDev'
    }

    It 'refuses a fresh install without explicit consent (incident fix #6)' {
        $s.Ast.ParamBlock.Parameters.Name.VariablePath.UserPath | Should -Contain 'ConfirmFreshInstall'
        $s.Text | Should -Match '-not \$isUpgrade -and -not \$Force -and -not \$ConfirmFreshInstall' `
            -Because 'an unexpected INSTALL usually means the target parameters are wrong'
    }

    It 'backs up the runtime config directory before the upgrade mirror (incident fix #4)' {
        $upgradeBlock = [regex]::Match(
            $s.Text,
            '(?s)# --- UPGRADE ---.*?# --- FRESH INSTALL ---'
        ).Value

        # The live SQLite DB must be backed up via the verified online backup helper, NOT a raw
        # recursive Copy-Item (a torn copy of a live WAL DB is a worthless rollback snapshot).
        $upgradeBlock | Should -Match 'Backup-SqliteConfigDb' `
            -Because 'the live config DB needs a consistent, integrity-verified backup (SQLite Phase D)'
        $upgradeBlock | Should -Not -Match 'Copy-Item \$runtimeConfigDir \$configDirBackup -Recurse' `
            -Because 'a raw recursive copy of a live WAL DB can be inconsistent - replaced by the online backup'
        $upgradeBlock | Should -Match 'config\.\$\{timestamp\}\.bak' `
            -Because 'the snapshot must be timestamped and retained like appsettings backups'
        $upgradeBlock.IndexOf('Backup-SqliteConfigDb') |
            Should -BeLessThan $upgradeBlock.IndexOf('robocopy') `
            -Because 'the snapshot must be taken before any files change'
    }

    It 'backs up the config DB from the path appsettings resolves, and stops when a configured path is missing (scd-1)' {
        # SharedConfigDb-Plan AC6: with ConfigStore:Path set the backup (and the post-deploy
        # check) must target that file - the one shared with the other instance - not the
        # publish folder's config\. A configured path naming a missing file is fatal BEFORE
        # anything is mirrored; without the key a missing DB is still just "nothing to back up".
        $upgradeBlock = [regex]::Match(
            $s.Text,
            '(?s)# --- UPGRADE ---.*?# --- FRESH INSTALL ---'
        ).Value

        $upgradeBlock | Should -Match 'Get-ConfigStorePathSetting -PublishPath \$PublishPath'
        $upgradeBlock | Should -Match 'Resolve-ConfigDbPath -PublishPath \$PublishPath'
        $upgradeBlock | Should -Match 'Backup-SqliteConfigDb -DbPath \$configDbPath' `
            -Because 'the backup must use the resolved path'
        $upgradeBlock | Should -Not -Match 'Backup-SqliteConfigDb -ConfigDir' `
            -Because 'the ConfigDir form would back up a stale per-instance file'
        $upgradeBlock | Should -Match '(?s)if \(\$configStorePathSetting -and -not \(Test-IsSqliteConfigDbPresent -DbPath \$configDbPath\)\) \{\s*Write-Fail' `
            -Because 'key set + file absent must Write-Fail (throw)'
        $upgradeBlock.IndexOf('Write-Fail "ConfigStore:Path') |
            Should -BeLessThan $upgradeBlock.IndexOf('robocopy') `
            -Because 'the check must run before any files change'
        $upgradeBlock | Should -Match 'nothing to back up' `
            -Because 'the no-key, no-DB case keeps the warning posture'

        # The post-deploy health check resolves the same way (never the hardcoded config\ path).
        $upgradeBlock | Should -Match '\$liveDb = Resolve-ConfigDbPath -PublishPath \$PublishPath'
        $upgradeBlock | Should -Not -Match 'Join-Path \$PublishPath "config\\exchangeadmin\.db"'
    }

    It 'runs the db tool from the binary it is about to deploy, resolved before anything changes (ConfigDbToolInApp-Plan S2)' {
        # AC4: a staging folder without the app binary stops the deploy, naming the path,
        # before any backup or mirror. The backup and the job check run that same binary.
        $resolveAt = $s.Text.IndexOf('Resolve-ConfigDbTool -AppPath $StagingPath')
        $resolveAt | Should -BeGreaterThan $s.Text.IndexOf('dotnet publish -c Release -o $StagingPath')
        $resolveAt | Should -BeLessThan $s.Text.IndexOf('# --- Mode-specific deployment ---')

        $upgradeBlock = [regex]::Match($s.Text, '(?s)# --- UPGRADE ---.*?# --- FRESH INSTALL ---').Value
        $upgradeBlock | Should -Match 'Backup-SqliteConfigDb -DbPath \$configDbPath -DestDir \$configDirBackup -Timestamp \$timestamp -AppPath \$StagingPath'
        $upgradeBlock | Should -Match 'Assert-NoActiveBulkJobsBeforeRecycle -ConfigDir \$runtimeConfigDir -AppPath \$StagingPath'
        # Staging is removed in the finally that restarts the pool, so the post-deploy check
        # runs the same binary from where it now lives.
        $upgradeBlock | Should -Match 'Test-SqliteConfigDbIntegrity -DbPath \$liveDb -AppPath \$PublishPath'
    }

    It 'sets $runtimeConfigDir unconditionally before any use, so the fresh-install jobs check gets a path (cdt-5)' {
        $isRuntimeConfigDir = {
            param($node)
            $node -is [System.Management.Automation.Language.VariableExpressionAst] -and
            $node.VariablePath.UserPath -eq 'runtimeConfigDir'
        }
        $assignments = @($s.Ast.FindAll({
                    param($node)
                    $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
                    $node.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and
                    $node.Left.VariablePath.UserPath -eq 'runtimeConfigDir'
                }, $true))
        $assignments.Count | Should -Be 1
        $assignment = $assignments[0]

        $conditional = @(
            [System.Management.Automation.Language.IfStatementAst],
            [System.Management.Automation.Language.LoopStatementAst],
            [System.Management.Automation.Language.SwitchStatementAst],
            [System.Management.Automation.Language.CatchClauseAst],
            [System.Management.Automation.Language.TrapStatementAst],
            [System.Management.Automation.Language.ScriptBlockExpressionAst],
            [System.Management.Automation.Language.FunctionDefinitionAst])
        $guarding = @()
        $ancestor = $assignment.Parent
        while ($null -ne $ancestor) {
            foreach ($type in $conditional) {
                if ($ancestor -is $type) { $guarding += "$($type.Name) at line $($ancestor.Extent.StartLineNumber)" }
            }
            $ancestor = $ancestor.Parent
        }
        $guarding | Should -BeNullOrEmpty -Because 'the assignment must run on both the upgrade and the fresh-install path'

        $reads = @($s.Ast.FindAll($isRuntimeConfigDir, $true) | Where-Object { $_ -ne $assignment.Left })
        $reads.Count | Should -BeGreaterThan 0
        foreach ($read in $reads) {
            $read.Extent.StartOffset | Should -BeGreaterThan $assignment.Extent.EndOffset -Because "line $($read.Extent.StartLineNumber) reads it"
        }

        $jobChecks = @($s.Ast.FindAll({
                    param($node)
                    $node -is [System.Management.Automation.Language.CommandAst] -and
                    $node.GetCommandName() -eq 'Assert-NoActiveBulkJobsBeforeRecycle'
                }, $true))
        $jobChecks.Count | Should -Be 2 -Because 'the upgrade and the fresh-install path both recycle'
        foreach ($call in $jobChecks) {
            $call.Extent.Text | Should -Match '-ConfigDir \$runtimeConfigDir\b'
        }
    }

    It 'runs a post-deploy live DB integrity check (DB is excluded from file drift by design)' {
        $upgradeBlock = [regex]::Match(
            $s.Text,
            '(?s)# --- UPGRADE ---.*?# --- FRESH INSTALL ---'
        ).Value

        # The DB triplet is excluded from the file-drift inventory (it changes across the pool
        # restart by design), so the live store must instead be verified by integrity_check after
        # the pool starts - otherwise a missing/corrupt live DB ends on the success banner.
        $upgradeBlock | Should -Match 'Test-SqliteConfigDbIntegrity' `
            -Because 'the live DB needs a real post-deploy health check, not file-drift comparison'
        $upgradeBlock | Should -Match "driftCheckExclusions = @\('extended-log-level.txt', 'exchangeadmin.db'" `
            -Because 'the DB triplet must be excluded from the size/mtime drift inventory'
        $upgradeBlock.IndexOf('Start-AppPoolWithRetry') |
            Should -BeLessThan $upgradeBlock.IndexOf('Test-SqliteConfigDbIntegrity') `
            -Because 'the live-DB check must run after the pool restarts (post migrate/seed)'
    }

    It 'verifies runtime config against a pre-deploy snapshot after the pool restarts (incident fix #5)' {
        $upgradeBlock = [regex]::Match(
            $s.Text,
            '(?s)# --- UPGRADE ---.*?# --- FRESH INSTALL ---'
        ).Value

        # Snapshot taken before the pool stops; comparison after the pool restarts.
        $upgradeBlock.IndexOf('$preConfigInventory') | Should -BeGreaterOrEqual 0
        $upgradeBlock.IndexOf('$preConfigInventory') | Should -BeLessThan $upgradeBlock.IndexOf('Stopping app pool')
        $upgradeBlock.IndexOf('Verifying runtime config integrity') | Should -BeGreaterThan $upgradeBlock.IndexOf('Start-AppPoolWithRetry')
        $upgradeBlock | Should -Match 'POST-DEPLOY CHECK' -Because 'drift must be warned about loudly'
        $upgradeBlock | Should -Match '\$lostKeys' -Because 'appsettings top-level key loss must be part of the check'
    }

    It 'does not rewrite appsettings.json during upgrade reconciliation' {
        $upgradeBlock = [regex]::Match(
            $s.Text,
            '(?s)# --- UPGRADE ---.*?# --- FRESH INSTALL ---'
        ).Value

        $upgradeBlock | Should -Not -Match 'ConvertTo-Json\s+-Depth\s+10\s+\|\s+Set-Content\s+\$configPath' `
            -Because 'upgrade deploys preserve environment-owned appsettings.json byte-for-byte except for explicit operator edits'
        $upgradeBlock | Should -Not -Match '\.PSObject\.Properties\.Remove' `
            -Because 'obsolete appsettings keys are warned about, not silently removed by deploy'
        $upgradeBlock | Should -Not -Match 'Add-Member\s+-NotePropertyName\s+"AdminGroups"' `
            -Because 'deploy must not synthesize Security:AdminGroups by reserializing the whole file'
    }
}

Describe 'tools/Install-ExchangeAdminWeb.ps1' {
    BeforeAll { $script:s = Get-ScriptUnderTest 'tools/Install-ExchangeAdminWeb.ps1' }

    It 'parses without syntax errors' {
        $s.Errors | Should -BeNullOrEmpty
    }

    It 'sets $ErrorActionPreference = Stop' {
        $s.Text | Should -Match '\$ErrorActionPreference\s*=\s*"Stop"'
    }

    It 'is environment-neutral: no ADI-specific strings' {
        $s.Text | Should -Not -Match '(?i)analog'
    }

    It 'is standalone: never references deploy.ps1' {
        $s.Text | Should -Not -Match 'deploy\.ps1'
    }

    It 'exposes a -PlanOnly switch' {
        $s.Ast.ParamBlock.Parameters.Name.VariablePath.UserPath | Should -Contain 'PlanOnly'
    }

    It 'gates actions on $PlanOnly through Invoke-PlanOrAction' {
        $fn = Find-FunctionDefinition $s 'Invoke-PlanOrAction'
        $fn | Should -Not -BeNullOrEmpty
        $fn.Extent.Text | Should -Match '\$PlanOnly'
    }

    It 'Write-Fail throws (repo error model)' {
        $fn = Find-FunctionDefinition $s 'Write-Fail'
        $fn | Should -Not -BeNullOrEmpty
        $fn.Find({ param($node) $node -is [System.Management.Automation.Language.ThrowStatementAst] }, $true) |
            Should -Not -BeNullOrEmpty
    }

    It 'checks the native exit code after every icacls invocation' {
        # Same native-exe trap as deploy.ps1: icacls failures are silent under
        # ErrorActionPreference=Stop + "| Out-Null". Every call site must guard on
        # $LASTEXITCODE (icacls returns 0 on success).
        $result = Test-IcaclsCallsAreChecked $s
        $result.Count | Should -BeGreaterThan 0
        $result.Unguarded | Should -BeNullOrEmpty -Because "every icacls call must be followed by a `$LASTEXITCODE check; unguarded: $($result.Unguarded -join '; ')"
    }

    It 'grants the pool identity inheritable Modify on config/ so the SQLite DB inherits write' {
        # The app creates config/exchangeadmin.db at runtime; it must inherit Modify (WAL needs
        # write) from the config-dir ACL. (OI)(CI)M = object+container inherit, Modify.
        $s.Text | Should -Match 'Set-DirectoryAcl -Path \$configDir -Identity \$appPoolIdentity -Rights "\(OI\)\(CI\)M"' `
            -Because 'the runtime DB inherits the config-dir ACL - no DB-specific grant needed'
    }

    It 'with -ConfigStorePath: still ACLs config\, ACLs the shared directory too, and refuses a missing shared file (scd-1, scd-4)' {
        # SharedConfigDb-Plan AC9. The local config\ ACL must stay (per-instance jobs DB and
        # seed files live there); the shared directory is ACLed IN ADDITION; the shared file
        # must already exist (the installer never creates it - the cutover script does).
        $s.Ast.ParamBlock.Parameters.Name.VariablePath.UserPath | Should -Contain 'ConfigStorePath'
        $s.Text | Should -Match 'Set-DirectoryAcl -Path \$configDir -Identity \$appPoolIdentity -Rights "\(OI\)\(CI\)M"' `
            -Because 'the per-instance config folder must still be created and ACLed'
        $s.Text | Should -Match '(?s)if \(\$sharedConfigDir\) \{.*?Set-DirectoryAcl -Path \$sharedConfigDir -Identity \$appPoolIdentity -Rights "\(OI\)\(CI\)M"' `
            -Because 'the shared directory needs Modify for the WAL/SHM sidecars, in addition'
        $s.Text.IndexOf('Set-DirectoryAcl -Path $configDir') |
            Should -BeLessThan $s.Text.IndexOf('Set-DirectoryAcl -Path $sharedConfigDir') `
            -Because 'the local ACL is unconditional and comes first; the shared one is additional'
        $s.Text | Should -Match '(?s)if \(-not \(Test-Path -LiteralPath \$ConfigStorePath -PathType Leaf\)\) \{\s*Write-Fail' `
            -Because 'a configured path naming a missing file must be refused, never created'
        $s.Text | Should -Match 'Move-ConfigDbToShared\.ps1' `
            -Because 'the refusal must point the operator at the cutover script that creates the shared database'
        $s.Text | Should -Not -Match 'New-Item[^\r\n]*\$ConfigStorePath' `
            -Because 'the installer must never create the shared database file'
        $s.Text | Should -Match 'StartsWith\(''\\\\''\)' -Because 'a UNC path must be refused'
        $s.Text | Should -Match '-ConfigStorePath \$ConfigStorePath\)' `
            -Because 'the generated appsettings must carry the key'
    }

    It 'generated appsettings carries ConfigStore:Path only when -ConfigStorePath is given (byte-identical otherwise)' {
        # Runs the real New-AppSettingsObject function (lifted from the script by AST) so the
        # generated object is checked, not just the source text.
        $fn = Find-FunctionDefinition $s 'New-AppSettingsObject'
        $fn | Should -Not -BeNullOrEmpty
        $CertSubject = ''; $SmtpHost = 'localhost'; $SmtpPort = 25; $SmtpUseSsl = $false
        $FromAddress = 'a@b'; $FromName = 'x'; $AdminNotificationEmail = 'a@b'; $DelineaUrl = ''
        $DelineaCredentialTarget = 'Delinea_Client'; $OnPremExchangeServerUri = ''; $ContactEmail = 'a@b'; $PublicBaseUrl = ''
        Invoke-Expression $fn.Extent.Text

        $without = New-AppSettingsObject -Name 'n' -BasePath '/x' -LogPath 'C:\logs' -SecurityAllowedGroups @('g') -SecurityAdminGroups @('g')
        $without.Keys | Should -Not -Contain 'ConfigStore' -Because 'no key means the instance keeps its own database'

        $blank = New-AppSettingsObject -Name 'n' -BasePath '/x' -LogPath 'C:\logs' -SecurityAllowedGroups @('g') -SecurityAdminGroups @('g') -ConfigStorePath '   '
        $blank.Keys | Should -Not -Contain 'ConfigStore' -Because 'a blank value must not write an empty key'
        ($blank | ConvertTo-Json -Depth 20) | Should -Be ($without | ConvertTo-Json -Depth 20) -Because 'byte-identical without the key'

        $with = New-AppSettingsObject -Name 'n' -BasePath '/x' -LogPath 'C:\logs' -SecurityAllowedGroups @('g') -SecurityAdminGroups @('g') -ConfigStorePath 'D:\shared\config\exchangeadmin.db'
        $with.ConfigStore.Path | Should -Be 'D:\shared\config\exchangeadmin.db'
        $json = $with | ConvertTo-Json -Depth 20 | ConvertFrom-Json
        $json.ConfigStore.Path | Should -Be 'D:\shared\config\exchangeadmin.db'
    }

    It '-ConfigStorePath refuses a drive-relative path (<Path>) - rooted is not fully qualified (scdi-2)' -TestCases @(
        @{ Path = 'D:exchangeadmin.db' }
        @{ Path = 'C:config\exchangeadmin.db' }
    ) {
        # Runs the real Assert-LocalAbsoluteFilePath (lifted by AST) with the script's own
        # throwing Write-Fail, and checks the main body actually routes -ConfigStorePath through it.
        $fn = Find-FunctionDefinition $s 'Assert-LocalAbsoluteFilePath'
        $fn | Should -Not -BeNullOrEmpty
        $s.Text | Should -Match 'Assert-LocalAbsoluteFilePath -Path \$ConfigStorePath -Name ''ConfigStorePath''' `
            -Because 'the validation must sit on the -ConfigStorePath path, not only be defined'
        $s.Text | Should -Not -Match 'IsPathRooted\(\$ConfigStorePath\)' `
            -Because 'IsPathRooted accepts D:file.db; the drive-relative form is exactly the bug'
        Invoke-Expression (Find-FunctionDefinition $s 'Write-Fail').Extent.Text
        Invoke-Expression $fn.Extent.Text

        { Assert-LocalAbsoluteFilePath -Path $Path -Name 'ConfigStorePath' } |
            Should -Throw -ExpectedMessage '*absolute local file path*'
        { Assert-LocalAbsoluteFilePath -Path '\\server\share\exchangeadmin.db' -Name 'ConfigStorePath' } |
            Should -Throw -ExpectedMessage '*network share*'
        { Assert-LocalAbsoluteFilePath -Path 'D:\shared\config\exchangeadmin.db' -Name 'ConfigStorePath' } |
            Should -Not -Throw
    }

    It 'still writes the section-access seed file (consumed as first-run DB import seed)' {
        # Fresh installs get correct initial authorization via this seed, which the app imports
        # into section_access on first start. Assert the actual WRITE call + path, not just the
        # helper name (which also appears in the function definition), so removing the write is
        # caught.
        $s.Text | Should -Match 'Write-JsonFileIfMissing[^\r\n]*sectionaccess\.json[^\r\n]*New-SectionAccessSeed' `
            -Because 'a fresh install must actually write the section-access (authorization) seed'
    }
}

Describe 'tools/promote-dev-to-prod.ps1' {
    BeforeAll { $script:s = Get-ScriptUnderTest 'tools/promote-dev-to-prod.ps1' }

    It 'parses without syntax errors' {
        $s.Errors | Should -BeNullOrEmpty
    }

    It 'sets $ErrorActionPreference = Stop' {
        $s.Text | Should -Match '\$ErrorActionPreference\s*=\s*"Stop"'
    }

    It 'defaults to dry run: applying requires an explicit -Apply switch' {
        $s.Ast.ParamBlock.Parameters.Name.VariablePath.UserPath | Should -Contain 'Apply'
    }

    It 'gates robocopy on $Apply and checks exit code >= 8' {
        $fn = Find-FunctionDefinition $s 'Invoke-RobocopyChecked'
        $fn | Should -Not -BeNullOrEmpty
        $fn.Extent.Text | Should -Match '-not \$Apply'
        $fn.Extent.Text | Should -Match '-ge\s+8'
    }

    It 'patches prod appsettings.json atomically via a temp file' {
        $s.Text | Should -Match 'appsettings\.promote\..*\.tmp'
    }

    It 'never copies, replaces or merges the config database (shared DB, decision 2026-09-04)' {
        # SharedConfigDb-Plan AC7: dev and prod open ONE config database, so promotion has no
        # config step at all. The wholesale copy (Copy-SqliteConfigDb), the older JSON-fragment
        # merge, the -SkipConfigFragments opt-out and the prod->dev -Refresh are all gone.
        $s.Text | Should -Not -Match 'Copy-SqliteConfigDb' `
            -Because 'promotion must not replace the shared config database with a copy'
        $s.Text | Should -Not -Match 'Copy-SqliteDbFile' `
            -Because 'promotion must not copy any database file'
        $s.Text | Should -Not -Match 'SkipConfigFragments' `
            -Because 'there is no config promotion to skip'
        $s.Text | Should -Not -Match '\$Refresh' `
            -Because 'a prod->dev refresh would overwrite the shared database with itself or a stale copy'
        $s.Ast.ParamBlock.Parameters.Name.VariablePath.UserPath | Should -Not -Contain 'Refresh'
        $s.Ast.ParamBlock.Parameters.Name.VariablePath.UserPath | Should -Not -Contain 'SkipConfigFragments'
        $s.Text | Should -Not -Match 'function Merge-JsonConfig' `
            -Because 'the JSON-fragment merge helpers are dead after the SQLite cutover'
        $s.Text | Should -Not -Match '\$jsonConfigFiles' `
            -Because 'the per-file fragment list merged files that no longer exist'
    }

    It 'backs up the config DB prod opens (resolved via ConfigStore:Path) before the pool stops' {
        # SharedConfigDb-Plan AC7: the verified backup is taken from the path prod actually opens
        # - the shared file when ConfigStore:Path is set, else prod's own config\exchangeadmin.db
        # - and it is taken BEFORE the prod pool is stopped and anything is mirrored.
        $s.Text | Should -Match 'Resolve-ConfigDbPath -PublishPath \$prod' `
            -Because 'the DB path must come from prod''s appsettings.json, never be hardcoded to config\'
        $s.Text | Should -Match 'Backup-SqliteConfigDb -DbPath \$prodConfigDbPath' `
            -Because 'the backup must use the resolved path'
        $s.Text.IndexOf('Backup-SqliteConfigDb -DbPath') |
            Should -BeLessThan $s.Text.IndexOf('Stop-AppPoolChecked -Name $ProdAppPoolName') `
            -Because 'the backup must exist before prod is touched'
        $s.Text | Should -Not -Match 'Backup-SqliteConfigDb -ConfigDir' `
            -Because 'the ConfigDir form would silently back up a stale per-instance file'
    }

    It 'stops (Write-Fail) when ConfigStore:Path is set and the file is absent (scd-1)' {
        # A configured path that names a missing file is a misconfiguration prod cannot start
        # on; promoting would take prod down. The script must throw before anything changes,
        # and only when the key is SET (the no-key case keeps the "nothing to back up" posture).
        $s.Text | Should -Match 'Get-ConfigStorePathSetting -PublishPath \$prod'
        $s.Text | Should -Match '(?s)if \(\$prodConfigStorePathSetting -and -not \(Test-IsSqliteConfigDbPresent -DbPath \$prodConfigDbPath\)\) \{\s*Write-Fail' `
            -Because 'key set + file absent must Write-Fail (throw)'
        $fn = Find-FunctionDefinition $s 'Write-Fail'
        $fn | Should -Not -BeNullOrEmpty
        $fn.Extent.Text | Should -Match '\bthrow\b'
        $s.Text.IndexOf('Write-Fail "Prod''s ConfigStore:Path') |
            Should -BeLessThan $s.Text.IndexOf('Stop-AppPoolChecked -Name $ProdAppPoolName') `
            -Because 'the check must run before prod is touched'
    }

    It 'rollback restores binaries only and names the DB backup instead of restoring it' {
        # SharedConfigDb-Plan AC7: promotion no longer replaces the config DB, so restoring the
        # verified backup on rollback would roll back DEV''s live data too. The rollback mirror
        # excludes config\ (per-instance jobs DB, any per-instance config DB), never copies
        # exchangeadmin.*.db anywhere, and tells the operator where the backup is.
        $rollbackBlock = [regex]::Match($s.Text, '(?s)Rolling back prod binaries from backup.*?Rolled back prod binaries').Value
        $rollbackBlock | Should -Not -BeNullOrEmpty
        $rollbackBlock | Should -Match "'/XD' 'logs' 'config'" `
            -Because 'the rollback mirror must leave config\ alone'
        $rollbackBlock | Should -Not -Match 'Copy-Item -LiteralPath \$verifiedDb' `
            -Because 'rollback must not overwrite the config DB'
        $rollbackBlock | Should -Not -Match 'Test-SqliteConfigDbIntegrity' `
            -Because 'nothing is restored, so nothing is re-checked'
        $rollbackBlock | Should -Match 'Write-Warn .*NOT restored by the rollback.*\$verifiedDb' `
            -Because 'the operator must be told where the verified backup is'
    }

    It 'keeps every remaining step behind the -Apply gate (plan mode prints, never acts)' {
        foreach ($name in 'Invoke-RobocopyChecked', 'Copy-FileChecked', 'Set-AppsettingsPathBase', 'Stop-AppPoolChecked', 'Start-AppPoolChecked') {
            $fn = Find-FunctionDefinition $s $name
            $fn | Should -Not -BeNullOrEmpty -Because "$name must exist"
            $fn.Extent.Text | Should -Match '-not \$Apply' -Because "$name must honour dry run"
        }
        $s.Text | Should -Match 'Write-Plan "Verified online backup of the config DB at \$prodConfigDbPath' `
            -Because 'the backup step must print its plan in dry run'
        $s.Text | Should -Match 'Write-Plan "Config database at \$prodConfigDbPath is shared with dev and is NOT copied' `
            -Because 'dry run must state that no config copy happens'
    }

    It 'only claims prod was restored from backup when rollback actually completed' {
        # Success-aggregation trap: the closing throw used to assert "Prod has been
        # restored from backup" unconditionally, even when the rollback robocopy failed
        # (exit >= 8), the rollback catch fired, or no backup existed. The
        # restored-from-backup claim must be gated on a flag set ONLY in the rollback
        # success branch, and a distinct "restore manually" message must exist for the
        # paths where rollback did not complete.
        $fn = $s.Ast.Find({
                param($node)
                $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
                $node.Left.Extent.Text -match 'rolledBack'
            }, $true)
        $fn | Should -Not -BeNullOrEmpty -Because 'a flag must track whether rollback succeeded'

        # The success message must be guarded by the rolledBack flag, not thrown blind.
        $s.Text | Should -Match '(?s)if \(\$rolledBack\).*?Prod has been restored from backup' `
            -Because 'the restored-from-backup claim must be conditional on rollback success'
        $s.Text | Should -Match 'rollback did not complete' `
            -Because 'paths where rollback failed/threw/had no backup need an honest message'
    }
}

Describe 'tools/test-delinea.ps1' {
    BeforeAll { $script:s = Get-ScriptUnderTest 'tools/test-delinea.ps1' }

    It 'parses without syntax errors' {
        $s.Errors | Should -BeNullOrEmpty
    }

    It 'sets $ErrorActionPreference = Stop' {
        $s.Text | Should -Match '\$ErrorActionPreference\s*=\s*"Stop"'
    }

    It 'never prints raw Delinea auth response bodies (Constitution line 42)' {
        # The token/secrets endpoints return secret-bearing bodies in $_.ErrorDetails.Message.
        # Echoing them to the console leaks them into transcripts/CI logs. Failures must
        # surface only an HTTP status/reason via Get-SafeHttpError.
        $s.Text | Should -Not -Match 'ErrorDetails' `
            -Because 'the raw Delinea auth response body must never reach the console'
        Find-FunctionDefinition $s 'Get-SafeHttpError' | Should -Not -BeNullOrEmpty `
            -Because 'failures must be reported through the sanitizing helper'
    }

    It 'takes the password as a SecureString, not a plain string' {
        $param = $s.Ast.ParamBlock.Parameters |
            Where-Object { $_.Name.VariablePath.UserPath -eq 'Password' }
        $param | Should -Not -BeNullOrEmpty
        $param.StaticType.Name | Should -Be 'SecureString' `
            -Because 'a [string] password lands in PSReadLine history and the process command line'
    }

    It 'does not hardcode an environment-specific Secret Server endpoint' {
        $s.Text | Should -Not -Match '(?i)secretserver\.ad\.analog\.com' `
            -Because 'tools/ scripts are environment-neutral; ServerUrl is mandatory with no default'
        $serverParam = $s.Ast.ParamBlock.Parameters |
            Where-Object { $_.Name.VariablePath.UserPath -eq 'ServerUrl' }
        $serverParam.DefaultValue | Should -BeNullOrEmpty `
            -Because 'ServerUrl must be supplied by the operator, not defaulted to an internal host'
    }
}

Describe 'tools/Move-ConfigDbToShared.ps1 (static)' {
    BeforeAll { $script:s = Get-ScriptUnderTest 'tools/Move-ConfigDbToShared.ps1' }

    It 'parses without syntax errors' {
        $s.Errors | Should -BeNullOrEmpty
    }

    It 'sets $ErrorActionPreference = Stop' {
        $s.Text | Should -Match '\$ErrorActionPreference\s*=\s*"Stop"'
    }

    It 'is pure ASCII (Windows PowerShell 5.1 reads it - invariant 6)' {
        $bytes = [System.IO.File]::ReadAllBytes($s.Path)
        ($bytes | Where-Object { $_ -gt 127 }).Count | Should -Be 0
    }

    It 'defaults to plan mode: acting requires -Apply' {
        $s.Ast.ParamBlock.Parameters.Name.VariablePath.UserPath | Should -Contain 'Apply'
        $s.Ast.ParamBlock.Parameters.Name.VariablePath.UserPath | Should -Contain 'PlanOnly'
        $s.Text | Should -Match 'if \(-not \$Apply\) \{ \$PlanOnly = \$true \}'
    }

    It 'gates every step on $PlanOnly through Invoke-PlanOrAction' {
        $fn = Find-FunctionDefinition $s 'Invoke-PlanOrAction'
        $fn | Should -Not -BeNullOrEmpty
        $fn.Extent.Text | Should -Match '\$PlanOnly'
        # Eight steps, each through the gate (the stop/start/appsettings/verify helpers wrap it).
        ([regex]::Matches($s.Text, 'Invoke-PlanOrAction "')).Count | Should -BeGreaterOrEqual 8
    }

    It 'Write-Fail throws (repo error model)' {
        $fn = Find-FunctionDefinition $s 'Write-Fail'
        $fn | Should -Not -BeNullOrEmpty
        $fn.Find({ param($node) $node -is [System.Management.Automation.Language.ThrowStatementAst] }, $true) |
            Should -Not -BeNullOrEmpty
    }

    It 'checks the native exit code after every icacls invocation' {
        $result = Test-IcaclsCallsAreChecked $s
        $result.Count | Should -BeGreaterThan 0
        $result.Unguarded | Should -BeNullOrEmpty -Because "unguarded: $($result.Unguarded -join '; ')"
    }

    It 'seeds the shared file from DEV via the verified copy helper, after backing up BOTH databases' {
        $s.Text | Should -Match 'Copy-SqliteDbFile -SourceDbPath \$devDb -DestDbPath \$SharedDbPath'
        $s.Text.IndexOf('Backup-SqliteConfigDb -DbPath $devDb') | Should -BeLessThan $s.Text.IndexOf('Copy-SqliteDbFile -SourceDbPath $devDb')
        $s.Text.IndexOf('Backup-SqliteConfigDb -DbPath $prodDb') | Should -BeLessThan $s.Text.IndexOf('Copy-SqliteDbFile -SourceDbPath $devDb')
        $s.Text | Should -Match 'Test-SqliteConfigDbIntegrity -DbPath \$SharedDbPath'
    }

    It 'writes ConfigStore:Path atomically (temp file, re-parse, move) with a backup of the original' {
        $s.Text | Should -Match 'appsettings\.cutover\..*\.tmp'
        $s.Text | Should -Match 'Copy-Item -LiteralPath \$AppSettingsPath -Destination "\$AppSettingsPath\.\$BackupSuffix"'
        $s.Text | Should -Match 'Move-Item -LiteralPath \$tmp -Destination \$AppSettingsPath -Force'
    }

    It 'prints the restore commands on failure and on success' {
        $fn = Find-FunctionDefinition $s 'Write-RestoreInstructions'
        $fn | Should -Not -BeNullOrEmpty
        ([regex]::Matches($s.Text, 'Write-RestoreInstructions -DevDb')).Count | Should -BeGreaterOrEqual 2
        $s.Text | Should -Match 'SPLIT STATE' -Because 'a failure between the two appsettings writes must be named'
    }
}

Describe 'tools/New-ExchangeAdminWebGraphApps.ps1' {
    BeforeAll { $script:g = Get-ScriptUnderTest 'tools/New-ExchangeAdminWebGraphApps.ps1' }

    # A client secret cannot be read back from Entra. The script created both registrations
    # in a loop and printed every secret in one summary AFTER it, so a failure on the second
    # registration left the first one existing in the tenant with a secret nobody ever saw.

    It 'parses without syntax errors' {
        $g.Errors | Should -BeNullOrEmpty
    }

    It 'prints each registration as it is created, not only in the final summary' {
        # The defect. Buffering an unrecoverable value until the end of a loop that can
        # fail is what loses it.
        $loopAt = $g.Text.IndexOf('foreach ($reg in $Registrations) {', $g.Text.IndexOf('$results = @()'))
        $perItemAt = $g.Text.IndexOf('$record | Format-List')
        $summaryAt = $g.Text.LastIndexOf('$results | Format-List')

        $loopAt | Should -BeGreaterThan 0
        $perItemAt | Should -BeGreaterThan $loopAt
        $perItemAt | Should -BeLessThan $summaryAt -Because 'the per-registration print happens inside the loop'
    }

    It 'resolves every Graph role before creating anything' {
        # The role lookup used to run inside the creation loop, so a hand-edited permission
        # name that does not exist threw after the previous registration was already made.
        $preflightAt = $g.Text.IndexOf('$rolesByRegistration = @{}')
        $createAt = $g.Text.IndexOf('New-MgApplication')

        $preflightAt | Should -BeGreaterThan 0
        $preflightAt | Should -BeLessThan $createAt
    }

    It 'looks up AppRoles in exactly one place' {
        # Two lookups would mean the preflight and the creation loop could disagree.
        ([regex]::Matches($g.Text, '\$graphSp\.AppRoles')).Count | Should -Be 1
    }

    It 'names what was created when it fails partway' {
        # Without this the tenant is half-configured and the only evidence is scrollback.
        $g.Text | Should -Match '(?s)\} catch \{.*?WERE created and still exist'
    }

    It 'rethrows after reporting, so a partial run is not reported as success' {
        $catchBlock = [regex]::Match($g.Text, '(?s)\} catch \{.*?\n\}')
        $catchBlock.Success | Should -BeTrue
        $catchBlock.Value | Should -Match '(?m)^\s*throw\s*$'
    }

    It 'still refuses before creating anything when a name already exists' {
        # The pre-existing preflight must survive the new one.
        $g.Text | Should -Match 'Refusing: app registration\(s\) already exist'
    }

    It 'supports -PlanOnly' {
        $g.Ast.ParamBlock.Parameters.Name.VariablePath.UserPath | Should -Contain 'PlanOnly'
    }
}

Describe 'CI gates' {
    # Lower-priority Wave 4 items: the SQLite-dependent tests were silently skipping in CI
    # because nothing installed sqlite3, and every CI step ran under pwsh so Architectural
    # Invariant 6 - the 5.1-readable scripts - was never exercised anywhere. Since
    # ConfigDbToolInApp-Plan S2 the suites drive the built app instead of sqlite3.
    BeforeAll {
        $script:ci = Get-Content (Join-Path $RepoRoot '.github/workflows/ci.yml') -Raw
        $psJobAt = $script:ci.IndexOf("`n  powershell:")
        $script:psJob = if ($psJobAt -ge 0) { $script:ci.Substring($psJobAt) } else { '' }
    }

    It 'builds the app before running Pester' {
        # The COMMAND, not the step name, for the same reason the sqlite3 guard pinned
        # its command: a renamed or disabled step can keep a matching name.
        $buildAt = $script:psJob.IndexOf('run: dotnet build ExchangeAdminWeb.csproj -c Release')
        $pesterAt = $script:psJob.IndexOf('- name: Pester')

        $buildAt | Should -BeGreaterThan 0 -Because 'the powershell job must build the app the suites run'
        $pesterAt | Should -BeGreaterThan 0
        $buildAt | Should -BeLessThan $pesterAt
    }

    It 'installs the .NET 10 SDK in the powershell job before building' {
        # The runner's default SDK is not guaranteed to be 10 (ConfigDbToolInApp-Plan 3.4).
        $setupAt = $script:psJob.IndexOf('- uses: actions/setup-dotnet@v4')
        $setupAt | Should -BeGreaterThan 0
        $script:psJob.Substring($setupAt) | Should -Match "^- uses: actions/setup-dotnet@v4\s+with:\s+dotnet-version: '10\.0\.x'"
        $setupAt | Should -BeLessThan $script:psJob.IndexOf('run: dotnet build ExchangeAdminWeb.csproj -c Release')
    }

    It 'no longer installs sqlite3' {
        $script:ci | Should -Not -Match 'choco install sqlite'
    }

    It 'fails the build when a test skips in CI' {
        # No suite skips by design. A skip that creeps back would hide exactly the tests
        # the job is there to run, so a skip is a failure.
        $script:ci | Should -Match '\$result\.SkippedCount -gt 0'
    }

    It 'parses the 5.1-critical scripts under Windows PowerShell 5.1' {
        # shell: powershell is Windows PowerShell 5.1 on windows-latest; shell: pwsh is 7.
        $script:ci | Should -Match 'shell: powershell'
        $script:ci | Should -Match 'PSVersion\.Major -ne 5'

        foreach ($f in @('deploy.ps1', 'tools/SqliteConfigBackup.psm1', 'tools/JobStateWarning.psm1', 'tools/AppPoolControl.psm1')) {
            $script:ci | Should -Match ([regex]::Escape($f))
        }
    }
}

Describe 'No sqlite3 on the deploy path (aud-sqlite3-path, ConfigDbToolInApp-Plan S2)' {

    It 'no tracked ops script resolves sqlite3 with Get-Command' {
        # The finding: an elevated deploy ran whatever sqlite3.exe PATH resolved to first,
        # and on the deploy host that was a user-writable, unsigned copy.
        $tracked = @(& git -C $RepoRoot ls-files -- 'tools/*.ps1' 'tools/*.psm1' '*.ps1' '*.psm1')
        $LASTEXITCODE | Should -Be 0
        $scripts = @($tracked | Where-Object { $_ -notmatch '/' -or $_ -like 'tools/*' })
        $scripts.Count | Should -BeGreaterThan 5 -Because 'the scan must actually see the ops scripts'

        $hits = foreach ($rel in $scripts) {
            $text = Get-Content -LiteralPath (Join-Path $RepoRoot $rel) -Raw
            if ($text -match '(?i)Get-Command\s+sqlite3') { $rel }
        }
        @($hits).Count | Should -Be 0 -Because ("these scripts still search PATH for sqlite3: " + (@($hits) -join ', '))
    }

    It 'resolves the .NET host from Program Files, never from PATH' {
        foreach ($rel in 'tools/SqliteConfigBackup.psm1', 'tools/JobStateWarning.psm1') {
            $text = Get-Content -LiteralPath (Join-Path $RepoRoot $rel) -Raw
            $text | Should -Match ([regex]::Escape('$env:ProgramFiles')) -Because "$rel must build the host path from Program Files"
            $text | Should -Not -Match '(?i)Get-Command\s+dotnet' -Because "$rel must not search PATH for the host"
        }
    }
}

Describe 'tools/deploy-pipeline.ps1' {
    BeforeAll { $script:s = Get-ScriptUnderTest 'tools/deploy-pipeline.ps1' }

    It 'parses without syntax errors' {
        $s.Errors | Should -BeNullOrEmpty
    }

    It 'exposes a -PlanOnly switch' {
        $s.Ast.ParamBlock.Parameters.Name.VariablePath.UserPath | Should -Contain 'PlanOnly'
    }

    It 'never applies robocopy exit-code thresholds to PowerShell child scripts' {
        # deploy.ps1 / promote-dev-to-prod.ps1 signal failure by throwing; an
        # exit-code threshold here silently swallowed `exit 1` failures (and a
        # successful run leaves robocopy residue in $LASTEXITCODE anyway).
        $s.Text | Should -Not -Match '\$LASTEXITCODE\s+-ge\s+8'
    }

    It 'asserts the prod apply/consent switches only outside -PlanOnly' {
        # With -PlanOnly the promote script must run its native dry run, so the
        # pipeline may not hardcode Apply / IUnderstandThisOverwritesProd.
        $assignment = [regex]::Match($s.Text, '(?s)if \(-not \$PlanOnly\) \{.*?IUnderstandThisOverwritesProd.*?\}')
        $assignment.Success | Should -BeTrue
        ([regex]::Matches($s.Text, 'IUnderstandThisOverwritesProd')).Count | Should -Be 1 -Because 'the consent switch must not be asserted anywhere else'
    }

    It 'never reports a commit match it did not make (provenance fails closed)' {
        # The check stood between a stale build and production and failed OPEN: when either
        # side of the comparison was missing it skipped the comparison and printed
        # "Dev commit matches source HEAD" anyway.
        #
        # Matched as condition-plus-statement, not as the message alone. A guard asserting
        # only the text passes while the branch above it is dead - that mistake was made on
        # aud-a38 and caught by a mutation.
        $s.Text | Should -Match '(?s)\} else \{\s*Write-Ok "Dev commit matches source HEAD"'
    }

    It 'treats an unusable git probe as unverified, not as verified' {
        # git is a native executable and does not throw, so the old try/catch caught
        # nothing; an empty answer flowed straight into the skipped comparison.
        $s.Text | Should -Match '\$commitProbeOk = -not \$commitProbeFailed'
        $s.Text | Should -Match 'Cannot verify that dev was built from the current source'
    }

    It 'names the two distinct ways provenance can be unknown' {
        # "git did not answer" and "the manifest has no commit" send the operator to
        # different places.
        $s.Text | Should -Match 'git could not report HEAD'
        $s.Text | Should -Match 'manifest carries no usable gitCommit'
    }

    It 'makes promoting without verified provenance an explicit switch' {
        $s.Ast.ParamBlock.Parameters.Name.VariablePath.UserPath | Should -Contain 'AllowUnverifiedProvenance'
        $s.Text | Should -Match 'if \(-not \$AllowUnverifiedProvenance\)'
    }

    It 'does not let a failed status probe read as a clean tree' {
        # The drift warning was gated on a value that is $false both when the tree is clean
        # and when git failed, so silence meant either.
        $s.Text | Should -Match 'if \(-not \$statusProbeOk\)'
        $s.Text | Should -Match 'has NOT been checked'
    }

    It 'survives git being absent, so -AllowUnverifiedProvenance is usable' {
        # Review: the exit-code check was right, but a MISSING git raises
        # CommandNotFoundException before any exit code exists, and with
        # $ErrorActionPreference = Stop that killed the run instead of reporting
        # unverifiable provenance - so the override could not be used on a host without git.
        $s.Text | Should -Match 'try \{ \$commitProbe = git'
        $s.Text | Should -Match 'catch \{ \$commitProbeFailed = \$true \}'
    }

    It 'does not crash on a short or placeholder gitCommit' {
        # "unknown" is what the manifest writer records when it cannot resolve HEAD.
        # Substring(0,8) on it threw ArgumentOutOfRangeException, replacing a clear
        # "dev is stale" message with a crash.
        $s.Text | Should -Match '\$manifest\.gitCommit\.Length -ge 8'
        $s.Text | Should -Match "gitCommit -eq .unknown."
    }

    It 'never claims that config was promoted (the config database is shared - scd-4)' {
        # SharedConfigDb-Plan AC6: help text and final messages must not tell the operator that
        # module configs or settings were promoted from dev; nothing is copied any more.
        $s.Text | Should -Not -Match '(?i)promoted from dev' `
            -Because 'the shared config database is never copied by promotion'
        $s.Text | Should -Not -Match '(?i)config promotion' `
            -Because 'there is no config promotion step'
        $s.Text | Should -Not -Match '(?i)dev values win' `
            -Because 'the dev-wins rule was superseded on 2026-09-04'
        $s.Text | Should -Match 'ConfigStore:Path' `
            -Because 'the help text should point the operator at where the shared database is named'
        $s.Text | Should -Not -Match 'SkipConfigFragments|\bRefresh\b' `
            -Because 'the pipeline must not pass switches the promote script no longer has'
    }
}
