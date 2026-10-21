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

    function Get-FreshInstallBranch {
        # The else clause of deploy.ps1's `if ($isUpgrade)` statement: the fresh-install path,
        # which every -Force run takes too, existing deployment or not. (The `$mode = if
        # ($isUpgrade) ...` expression is the right-hand side of an assignment, not the branch.)
        param($Script)
        $modeBranches = @($Script.Ast.FindAll({
                    param($node)
                    $node -is [System.Management.Automation.Language.IfStatementAst] -and
                    $node.Clauses[0].Item1.Extent.Text -eq '$isUpgrade' -and
                    -not ($node.Parent -is [System.Management.Automation.Language.AssignmentStatementAst])
                }, $true))
        if ($modeBranches.Count -ne 1) { throw "expected exactly one 'if (`$isUpgrade)' mode branch, found $($modeBranches.Count)" }
        if ($null -eq $modeBranches[0].ElseClause) { throw 'the mode branch has no else clause' }
        $modeBranches[0].ElseClause
    }

    function Get-ConditionalAncestor {
        # Every if, loop, switch, catch, trap, script block or function between $Node and $Stop:
        # the constructs that can make a statement run on some paths and not others.
        param($Node, $Stop)
        $types = @(
            [System.Management.Automation.Language.IfStatementAst],
            [System.Management.Automation.Language.LoopStatementAst],
            [System.Management.Automation.Language.SwitchStatementAst],
            [System.Management.Automation.Language.CatchClauseAst],
            [System.Management.Automation.Language.TrapStatementAst],
            [System.Management.Automation.Language.ScriptBlockExpressionAst],
            [System.Management.Automation.Language.FunctionDefinitionAst])
        $ancestor = $Node.Parent
        while ($null -ne $ancestor -and -not [object]::ReferenceEquals($ancestor, $Stop)) {
            foreach ($type in $types) {
                if ($ancestor -is $type) { $ancestor }
            }
            $ancestor = $ancestor.Parent
        }
    }

    function Get-NamedArgumentText {
        # The source text of the argument bound to -$Name on one command, or $null.
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

    It 'removes the staging publish on every failure after it, the pre-deploy backup included (cdt-6)' {
        # Staging holds a published appsettings.json with real environment values. Every
        # command from the publish to the end of the mode-specific section must sit in the
        # BODY of a try whose finally removes it - not merely in some finally that does (a
        # throw from Start-AppPoolWithRetry inside the inner finally skips that finally's own
        # Remove-Item).
        $publish = $s.Ast.Find({ param($node)
                $node -is [System.Management.Automation.Language.CommandAst] -and
                $node.Extent.Text -like 'dotnet publish*-o $StagingPath*' }, $true)
        $publish | Should -Not -BeNullOrEmpty
        $endAt = $s.Text.IndexOf('# --- Final output ---')
        $endAt | Should -BeGreaterThan $publish.Extent.StartOffset

        $commands = @($s.Ast.FindAll({ param($node)
                    $node -is [System.Management.Automation.Language.CommandAst] -and
                    $node.Extent.StartOffset -ge $publish.Extent.StartOffset -and
                    $node.Extent.StartOffset -lt $endAt -and
                    $node.Extent.Text -notmatch '^Remove-Item \$StagingPath\b' }, $true))

        $uncovered = foreach ($command in $commands) {
            $covered = $false
            $ancestor = $command.Parent
            while ($null -ne $ancestor -and -not $covered) {
                if ($ancestor -is [System.Management.Automation.Language.TryStatementAst] -and
                    $null -ne $ancestor.Finally -and
                    $ancestor.Finally.Extent.Text -match 'Remove-Item \$StagingPath' -and
                    $command.Extent.StartOffset -ge $ancestor.Body.Extent.StartOffset -and
                    $command.Extent.EndOffset -le $ancestor.Body.Extent.EndOffset) {
                    $covered = $true
                }
                $ancestor = $ancestor.Parent
            }
            if (-not $covered) { "line $($command.Extent.StartLineNumber): $($command.GetCommandName())" }
        }
        @($uncovered) | Should -BeNullOrEmpty -Because 'a throw there would leave the staging publish behind'

        $names = @($commands | ForEach-Object { $_.GetCommandName() })
        foreach ($name in 'Resolve-ConfigDbTool', 'Backup-SqliteConfigDb', 'Assert-NoActiveBulkJobsBeforeRecycle', 'Stop-AppPoolVerified', 'Start-AppPoolWithRetry', 'Write-DeploymentManifest') {
            $names | Should -Contain $name -Because 'the covered region must reach the pre-deploy backup and the pool restart'
        }
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

    It 'reads ConfigStore:Path before -Force changes anything and writes it into the regenerated appsettings.json (cdt-8)' {
        # -Force over an existing deployment takes the fresh-install branch, which regenerates
        # appsettings.json from parameters. ConfigStore:Path names the config database the
        # instance opens - on a shared-database server the one file dev and prod both use - and
        # without it the app silently opens its own config\exchangeadmin.db instead.
        $install = Get-FreshInstallBranch $s

        $changes = @($install.FindAll({ param($node)
                    $node -is [System.Management.Automation.Language.CommandAst] -and
                    $node.GetCommandName() -in @('Stop-AppPoolVerified', 'robocopy') }, $true))
        $changes.Count | Should -Be 2 -Because 'the branch stops the pool once and mirrors once'
        $firstChange = ($changes | ForEach-Object { $_.Extent.StartOffset } | Measure-Object -Minimum).Minimum

        $reads = @($install.FindAll({ param($node)
                    $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
                    $node.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and
                    $node.Left.VariablePath.UserPath -eq 'configStorePathSetting' }, $true))
        $reads.Count | Should -Be 1 -Because 'the value is read once, from the file about to be replaced'
        $reads[0].Right.Extent.Text | Should -BeExactly 'Get-ConfigStorePathSetting -PublishPath $PublishPath'
        @(Get-ConditionalAncestor $reads[0] $install) | Should -BeNullOrEmpty -Because 'every run down this branch must read it'
        $reads[0].Extent.EndOffset | Should -BeLessThan $firstChange -Because 'it is read before the pool stops or anything is mirrored'

        $writers = @($install.FindAll({ param($node)
                    $node -is [System.Management.Automation.Language.CommandAst] -and
                    $node.GetCommandName() -in @('Set-Content', 'Out-File', 'Add-Content', 'Write-AppSettingsFile') }, $true))
        $writers.Count | Should -Be 1 -Because 'appsettings.json is written in exactly one place on this path'
        $writers[0].GetCommandName() | Should -BeExactly 'Write-AppSettingsFile'
        Get-NamedArgumentText $writers[0] 'Path' | Should -BeExactly '$configPath'
        Get-NamedArgumentText $writers[0] 'ConfigStorePath' | Should -BeExactly '$configStorePathSetting' -Because 'the value read above is what the write carries'
        $writers[0].Extent.StartOffset | Should -BeGreaterThan $reads[0].Extent.EndOffset
    }

    It 'Write-AppSettingsFile carries ConfigStore:Path exactly as read, and adds none without it (cdt-8)' {
        # Runs the real function, lifted from deploy.ps1 by AST, against a temporary
        # appsettings.json, and reads the result back with the same reader the deploy uses.
        Import-Module (Join-Path $RepoRoot 'tools/SqliteConfigBackup.psm1') -Force
        $fn = Find-FunctionDefinition $s 'Write-AppSettingsFile'
        $fn | Should -Not -BeNullOrEmpty
        . ([scriptblock]::Create($fn.Extent.Text))

        $publish = Join-Path ([System.IO.Path]::GetTempPath()) ('cdt8_' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $publish -Force | Out-Null
        $appsettings = Join-Path $publish 'appsettings.json'
        try {
            Set-Content -LiteralPath $appsettings -Encoding UTF8 -Value '{ "AllowedHosts": "old", "Legacy": 1, "ConfigStore": { "Path": "D:\\shared\\config\\exchangeadmin.db" } }'
            $kept = Get-ConfigStorePathSetting -PublishPath $publish
            $kept | Should -BeExactly 'D:\shared\config\exchangeadmin.db'
            Write-AppSettingsFile -Settings ([ordered]@{ AllowedHosts = '*' }) -Path $appsettings -ConfigStorePath $kept
            Get-ConfigStorePathSetting -PublishPath $publish | Should -BeExactly 'D:\shared\config\exchangeadmin.db'
            $json = Get-Content -LiteralPath $appsettings -Raw | ConvertFrom-Json
            ($json.PSObject.Properties.Name -join ',') | Should -BeExactly 'AllowedHosts,ConfigStore' -Because 'only the key is carried; the rest is regenerated'
            ($json.ConfigStore.PSObject.Properties.Name -join ',') | Should -BeExactly 'Path'
            $json.AllowedHosts | Should -BeExactly '*'

            Set-Content -LiteralPath $appsettings -Encoding UTF8 -Value '{ "AllowedHosts": "old" }'
            $none = Get-ConfigStorePathSetting -PublishPath $publish
            $none | Should -BeNullOrEmpty
            Write-AppSettingsFile -Settings ([ordered]@{ AllowedHosts = '*' }) -Path $appsettings -ConfigStorePath $none
            ((Get-Content -LiteralPath $appsettings -Raw | ConvertFrom-Json).PSObject.Properties.Name -join ',') |
                Should -BeExactly 'AllowedHosts' -Because 'without the key nothing is added'
        } finally {
            Remove-Item -LiteralPath $publish -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'takes the verified online backup on -Force, before the pool stops or anything is mirrored (cdt-9)' {
        # Architectural Invariant 3: the config database is backed up by verified online backup,
        # from the path the instance opens, before each deploy - never by raw file copy. -Force
        # used to raw-copy the live config\ directory before the publish, verify nothing, and
        # never back up a shared database at all.
        $install = Get-FreshInstallBranch $s

        $changes = @($install.FindAll({ param($node)
                    $node -is [System.Management.Automation.Language.CommandAst] -and
                    $node.GetCommandName() -in @('Stop-AppPoolVerified', 'robocopy') }, $true))
        $changes.Count | Should -Be 2 -Because 'the branch stops the pool once and mirrors once'
        $firstChange = ($changes | ForEach-Object { $_.Extent.StartOffset } | Measure-Object -Minimum).Minimum

        $backups = @($install.FindAll({ param($node)
                    $node -is [System.Management.Automation.Language.CommandAst] -and
                    $node.GetCommandName() -eq 'Backup-SqliteConfigDb' }, $true))
        $backups.Count | Should -Be 1 -Because 'the -Force path must take the verified online backup'
        $backup = $backups[0]
        Get-NamedArgumentText $backup 'DbPath' | Should -BeExactly '$configDbPath' -Because 'the backup must target the database the instance opens'
        Get-NamedArgumentText $backup 'DestDir' | Should -BeExactly '$configDirBackup'
        Get-NamedArgumentText $backup 'Timestamp' | Should -BeExactly '$timestamp'
        Get-NamedArgumentText $backup 'AppPath' | Should -BeExactly '$StagingPath' -Because 'the tool is the build about to be deployed'
        $backup.Extent.EndOffset | Should -BeLessThan $firstChange -Because 'the snapshot must exist before the pool stops or any file changes'

        $gates = @(Get-ConditionalAncestor $backup $install)
        $gates.Count | Should -Be 1 -Because 'the backup is gated only on -Force over an existing deployment'
        $gates[0] | Should -BeOfType ([System.Management.Automation.Language.IfStatementAst])
        $gates[0].Clauses[0].Item1.Extent.Text | Should -BeExactly '$Force -and (Test-Path $configPath)'

        $resolves = @($install.FindAll({ param($node)
                    $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
                    $node.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and
                    $node.Left.VariablePath.UserPath -eq 'configDbPath' }, $true))
        $resolves.Count | Should -Be 1
        $resolves[0].Right.Extent.Text | Should -BeExactly 'Resolve-ConfigDbPath -PublishPath $PublishPath' -Because 'a shared database lives outside config\'
        @(Get-ConditionalAncestor $resolves[0] $install) | Should -BeNullOrEmpty
        $resolves[0].Extent.EndOffset | Should -BeLessThan $backup.Extent.StartOffset

        $refusals = @($install.FindAll({ param($node)
                    $node -is [System.Management.Automation.Language.IfStatementAst] -and
                    $node.Clauses[0].Item1.Extent.Text -eq '$configStorePathSetting -and -not (Test-IsSqliteConfigDbPresent -DbPath $configDbPath)' }, $true))
        $refusals.Count | Should -Be 1 -Because 'a configured path naming a missing file must stop the run, not back up nothing'
        $refusals[0].Clauses[0].Item2.Statements[0].Extent.Text | Should -BeLike 'Write-Fail "ConfigStore:Path*'
        @(Get-ConditionalAncestor $refusals[0] $install) | Should -BeNullOrEmpty
        $refusals[0].Extent.EndOffset | Should -BeLessThan $firstChange

        $install.Extent.Text | Should -Match ([regex]::Escape('$configDirBackup = Join-Path $BackupDir "config.${timestamp}.pre-force.bak"'))

        $copies = @($install.FindAll({ param($node)
                    $node -is [System.Management.Automation.Language.PipelineAst] -and
                    $node.PipelineElements.Count -ge 2 -and
                    $node.PipelineElements[0].Extent.Text -like 'Get-ChildItem*$runtimeConfigDir*' -and
                    $node.Extent.Text -match 'Copy-Item' }, $true))
        $copies.Count | Should -Be 1 -Because 'the rest of config\ is copied once, as the upgrade path copies it'
        # The filter's list (which holds the live triplet) is pinned by the cdt-10 test.
        $copies[0].Extent.Text | Should -Match ([regex]::Escape('Where-Object { $_.Name -notin $configCopyExclusions }')) `
            -Because 'a raw copy of the live database triplet is the torn snapshot this replaces'
        $copies[0].Extent.EndOffset | Should -BeLessThan $firstChange

        $recursive = @($s.Ast.FindAll({ param($node)
                    $node -is [System.Management.Automation.Language.CommandAst] -and
                    $node.GetCommandName() -eq 'Copy-Item' -and
                    @($node.CommandElements | Where-Object {
                            $_ -is [System.Management.Automation.Language.CommandParameterAst] -and $_.ParameterName -like 'r*' }).Count -gt 0 }, $true))
        @($recursive | ForEach-Object { "line $($_.Extent.StartLineNumber): $($_.Extent.Text)" }) |
            Should -BeNullOrEmpty -Because 'no raw recursive copy of a directory may stand in for the verified backup'
    }

    It 'leaves every SQLite database the app keeps in config\ out of the config backup copies (cdt-10)' {
        # The rest-of-config copy used to exclude only the config database triplet, so every deploy
        # copied the live jobs and usage databases into a backup nothing prunes, keeping bulk-job
        # records and usage telemetry past the retention the pages state, against Invariant 3.
        $lists = @($s.Ast.FindAll({ param($node)
                    $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
                    $node.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and
                    $node.Left.VariablePath.UserPath -eq 'configCopyExclusions' }, $true))
        $lists.Count | Should -Be 1 -Because 'one list, so the upgrade and the -Force copies cannot disagree'
        $list = $lists[0]
        @(Get-ConditionalAncestor $list $null) | Should -BeNullOrEmpty -Because 'both paths read it'
        $list.Extent.EndOffset | Should -BeLessThan (Get-FreshInstallBranch $s).Parent.Extent.StartOffset -Because 'it is set before the mode branch'
        $names = @($list.Right.FindAll({ param($node)
                    $node -is [System.Management.Automation.Language.StringConstantExpressionAst] }, $true) | ForEach-Object Value)

        # Tripwire: the databases come from the app's own code, so a new one cannot repeat this.
        $sources = @(& git -C $RepoRoot ls-files -- '*.cs' | Where-Object { $_ -notlike 'ExchangeAdminWeb.Tests/*' })
        $LASTEXITCODE | Should -Be 0
        $sources.Count | Should -BeGreaterThan 50 -Because 'the scan must actually see the app source'
        $appDbs = @(foreach ($rel in $sources) {
                $text = Get-Content -LiteralPath (Join-Path $RepoRoot $rel) -Raw
                foreach ($match in [regex]::Matches($text, 'Path\.Combine\([^;]*?"config",\s*"([^"]+\.db)"\)')) { $match.Groups[1].Value }
            }) | Sort-Object -Unique
        foreach ($known in 'exchangeadmin.db', 'exchangeadmin-jobs.db', 'exchangeadmin-usage.db') {
            $appDbs | Should -Contain $known -Because 'the scan must see the databases the app creates today'
        }
        $expected = @(foreach ($db in $appDbs) { $db; "$db-wal"; "$db-shm" }) | Sort-Object
        (@($names | Sort-Object) -join ',') | Should -BeExactly ($expected -join ',') `
            -Because 'exactly the databases the app keeps in config\, each with its WAL sidecars, and nothing else'

        $copies = @($s.Ast.FindAll({ param($node)
                    $node -is [System.Management.Automation.Language.PipelineAst] -and
                    $node.PipelineElements.Count -ge 2 -and
                    $node.PipelineElements[0].Extent.Text -like 'Get-ChildItem*$runtimeConfigDir*' -and
                    $node.Extent.Text -match 'Copy-Item' }, $true))
        $copies.Count | Should -Be 2 -Because 'the upgrade and the -Force path each copy the rest of config\'
        foreach ($copy in $copies) {
            $copy.Extent.Text | Should -Match ([regex]::Escape('Where-Object { $_.Name -notin $configCopyExclusions }')) -Because "the copy at line $($copy.Extent.StartLineNumber) must filter on the list"
            $copy.Extent.StartOffset | Should -BeGreaterThan $list.Extent.EndOffset
        }
    }

    It 'leaves the same databases out of the drift inventory as out of the backup copies (cdt-13)' {
        # The drift check compares config\ before the pool stops with config\ after it restarts. A
        # live WAL database changes across that by design; the drift list named only the config
        # database, so the jobs and usage databases raised false drift warnings that pointed at a
        # backup which, since cdt-10, deliberately does not contain them.
        $lists = @{}
        foreach ($listName in 'configCopyExclusions', 'driftCheckExclusions') {
            $lists[$listName] = @($s.Ast.FindAll({ param($node)
                        $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
                        $node.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and
                        $node.Left.VariablePath.UserPath -eq $listName }, $true))
        }
        $copyLists = $lists['configCopyExclusions']
        $copyLists.Count | Should -Be 1
        $driftLists = $lists['driftCheckExclusions']
        $driftLists.Count | Should -Be 1 -Because 'one drift list'
        $drift = $driftLists[0]
        $drift.Extent.StartOffset | Should -BeGreaterThan $copyLists[0].Extent.EndOffset

        $references = @($drift.Right.FindAll({ param($node)
                    $node -is [System.Management.Automation.Language.VariableExpressionAst] -and
                    $node.VariablePath.UserPath -eq 'configCopyExclusions' }, $true))
        $references.Count | Should -Be 1 -Because 'the drift list derives from the shared list, so one list owns the databases'

        # The two real statements, evaluated together: the shared list plus extended-log-level.txt.
        $evaluate = [scriptblock]::Create($copyLists[0].Extent.Text + "`n" + $drift.Extent.Text + "`n" + ',$driftCheckExclusions')
        $driftNames = @((& $evaluate) | ForEach-Object { $_ })
        $copyNames = @(([scriptblock]::Create($copyLists[0].Extent.Text + "`n" + ',$configCopyExclusions')).Invoke() | ForEach-Object { $_ })
        $copyNames.Count | Should -BeGreaterOrEqual 9 -Because 'the cdt-10 test pins what the shared list holds'
        (@($driftNames | Sort-Object -Unique) -join ',') | Should -BeExactly (@(@('extended-log-level.txt') + $copyNames | Sort-Object -Unique) -join ',') `
            -Because 'every database the backup leaves out, each with its WAL sidecars, and the rewritten log-level file'

        $inventories = @($s.Ast.FindAll({ param($node)
                    $node -is [System.Management.Automation.Language.PipelineAst] -and
                    $node.PipelineElements.Count -ge 2 -and
                    $node.PipelineElements[0].Extent.Text -like 'Get-ChildItem*$runtimeConfigDir*' -and
                    $node.Extent.Text -notmatch 'Copy-Item' }, $true))
        $inventories.Count | Should -Be 2 -Because 'the drift check inventories config\ before the stop and after the restart'
        foreach ($inventory in $inventories) {
            $inventory.Extent.Text | Should -Match ([regex]::Escape('Where-Object { $_.Name -notin $driftCheckExclusions }')) -Because "the inventory at line $($inventory.Extent.StartLineNumber) must filter on the drift list"
            $inventory.Extent.StartOffset | Should -BeGreaterThan $drift.Extent.EndOffset
        }
    }

    It 'checks the live config database after a -Force deploy restarts the pool, and only warns (cdt-12)' {
        # The upgrade path ends with a post-deploy check of the live store. -Force, the path that
        # regenerates appsettings.json and so the one most able to point the app at a different,
        # missing or damaged database, ended on the success banner without one.
        $install = Get-FreshInstallBranch $s

        $restarts = @($install.FindAll({ param($node)
                    $node -is [System.Management.Automation.Language.TryStatementAst] -and
                    $null -ne $node.Finally -and
                    @($node.Finally.FindAll({ param($n)
                                $n -is [System.Management.Automation.Language.CommandAst] -and
                                $n.GetCommandName() -eq 'Start-AppPoolWithRetry' }, $true)).Count -gt 0 }, $true))
        $restarts.Count | Should -Be 1 -Because 'the branch restarts the pool in one finally'
        $restartEnd = $restarts[0].Extent.EndOffset

        $resolves = @($install.FindAll({ param($node)
                    $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
                    $node.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and
                    $node.Left.VariablePath.UserPath -eq 'liveDb' }, $true))
        $resolves.Count | Should -Be 1 -Because 'the -Force path must resolve the database it checks'
        $resolves[0].Right.Extent.Text | Should -BeExactly 'Resolve-ConfigDbPath -PublishPath $PublishPath' -Because 'it reads the regenerated appsettings.json, the file the app opens'
        @(Get-ConditionalAncestor $resolves[0] $install) | Should -BeNullOrEmpty -Because 'every -Force run that gets this far is checked'
        $resolves[0].Extent.StartOffset | Should -BeGreaterThan $restartEnd -Because 'the check runs after the pool restart'

        $checks = @($install.FindAll({ param($node)
                    $node -is [System.Management.Automation.Language.CommandAst] -and
                    $node.GetCommandName() -eq 'Test-SqliteConfigDbIntegrity' }, $true))
        $checks.Count | Should -Be 1 -Because 'the -Force path must check the live store'
        $check = $checks[0]
        Get-NamedArgumentText $check 'DbPath' | Should -BeExactly '$liveDb'
        Get-NamedArgumentText $check 'AppPath' | Should -BeExactly '$PublishPath' -Because 'staging is gone by then; the publish folder holds the same build'
        $check.Extent.StartOffset | Should -BeGreaterThan $resolves[0].Extent.EndOffset

        $gates = @(Get-ConditionalAncestor $check $install)
        $gates.Count | Should -Be 1 -Because 'only its own presence test decides whether it runs'
        $gates[0].Clauses[0].Item1.Extent.Text | Should -BeExactly 'Test-Path -LiteralPath $liveDb'

        # Warn, not throw: the files are deployed and the pool is running; the verified pre-deploy
        # backup is the rollback.
        $try = $check.Parent
        while ($null -ne $try -and -not ($try -is [System.Management.Automation.Language.TryStatementAst])) { $try = $try.Parent }
        $try | Should -Not -BeNullOrEmpty
        $try.Extent.StartOffset | Should -BeGreaterThan $gates[0].Extent.StartOffset -Because 'the try is inside the presence test'
        $try.CatchClauses.Count | Should -Be 1
        $try.CatchClauses[0].Body.Statements[0].Extent.Text | Should -BeLike 'Write-Warn "POST-DEPLOY CHECK: live config DB integrity check FAILED*'
        @($try.CatchClauses[0].Body.FindAll({ param($n)
                    $n -is [System.Management.Automation.Language.ThrowStatementAst] -or
                    ($n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Write-Fail') }, $true)).Count |
            Should -Be 0 -Because 'a failed check is reported, never thrown'

        $gates[0].Clauses.Count | Should -Be 2
        $gates[0].Clauses[1].Item1.Extent.Text | Should -BeExactly '$configStorePathSetting'
        $gates[0].Clauses[1].Item2.Statements[0].Extent.Text | Should -BeLike 'Write-Warn "POST-DEPLOY CHECK: ConfigStore:Path names*'
        $gates[0].ElseClause.Statements[0].Extent.Text | Should -BeLike 'Write-Warn "POST-DEPLOY CHECK: no config DB at*'
    }

    It 'restarts the pool only after a complete mirror, and a failed restart never replaces the real error (cdt-16, AST)' {
        # robocopy /MIR failing part-way leaves a mix of builds in the publish folder; the finally
        # restarted the pool over it anyway. And a restart that threw inside the finally replaced
        # whatever was propagating.
        $modeIf = (Get-FreshInstallBranch $s).Parent
        $branches = [ordered]@{ upgrade = $modeIf.Clauses[0].Item2; '-Force' = $modeIf.ElseClause }
        foreach ($branchName in $branches.Keys) {
            $statements = @($branches[$branchName].Statements)
            $restartIndexes = @(for ($i = 0; $i -lt $statements.Count; $i++) {
                    $st = $statements[$i]
                    if ($st -is [System.Management.Automation.Language.TryStatementAst] -and $null -ne $st.Finally -and
                        @($st.Finally.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Start-AppPoolWithRetry' }, $true)).Count -gt 0) { $i }
                })
            $restartIndexes.Count | Should -Be 1 -Because "the $branchName branch restarts the pool in one finally"
            $restartIndex = $restartIndexes[0]
            $restart = $statements[$restartIndex]

            $flagSetters = @{}
            foreach ($i in 0..($restartIndex - 1)) {
                if ($statements[$i] -is [System.Management.Automation.Language.AssignmentStatementAst]) { $flagSetters[$statements[$i].Extent.Text] = $i }
            }
            $flagSetters.ContainsKey('$mirrorSucceeded = $false') | Should -BeTrue -Because "the $branchName branch starts with the mirror not done"
            $flagSetters.ContainsKey('$poolStartError = $null') | Should -BeTrue

            $body = @($restart.Body.Statements)
            $robocopyAt = -1; $checkAt = -1; $setAt = -1
            for ($i = 0; $i -lt $body.Count; $i++) {
                if ($body[$i].Extent.Text -like '& robocopy*') { $robocopyAt = $i }
                if ($body[$i] -is [System.Management.Automation.Language.IfStatementAst] -and $body[$i].Clauses[0].Item1.Extent.Text -eq '$LASTEXITCODE -ge 8') { $checkAt = $i }
                if ($body[$i].Extent.Text -eq '$mirrorSucceeded = $true') { $setAt = $i }
            }
            $robocopyAt | Should -BeGreaterOrEqual 0
            $checkAt | Should -Be ($robocopyAt + 1) -Because 'the exit code is checked right after the mirror'
            $setAt | Should -Be ($checkAt + 1) -Because 'the mirror counts as done only once its exit code has been checked'
            @($branches[$branchName].FindAll({ param($n) $n -is [System.Management.Automation.Language.AssignmentStatementAst] -and $n.Extent.Text -eq '$mirrorSucceeded = $true' }, $true)).Count | Should -Be 1
            $failure = $body[$checkAt].Clauses[0].Item2.Statements[0]
            $failure | Should -BeOfType ([System.Management.Automation.Language.ThrowStatementAst])
            foreach ($fact in 'LEFT STOPPED', '$PublishPath', '$StagingPath', '$ProjectRoot', '$dbBackup') {
                $failure.Extent.Text | Should -Match ([regex]::Escape($fact)) -Because "the mirror failure must say what state it left and what to do ($fact)"
            }

            $starts = @($restart.Finally.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Start-AppPoolWithRetry' }, $true))
            $starts.Count | Should -Be 1
            $gate = $starts[0].Parent
            while ($null -ne $gate -and -not ($gate -is [System.Management.Automation.Language.IfStatementAst] -and $gate.Clauses[0].Item1.Extent.Text -eq '$mirrorSucceeded')) { $gate = $gate.Parent }
            $gate | Should -Not -BeNullOrEmpty -Because "the $branchName restart must be conditional on a complete mirror"
            ($starts[0].Extent.StartOffset -ge $gate.Clauses[0].Item2.Extent.StartOffset -and $starts[0].Extent.EndOffset -le $gate.Clauses[0].Item2.Extent.EndOffset) | Should -BeTrue -Because 'it is in the then-branch'
            $guard = $starts[0].Parent
            while ($null -ne $guard -and -not ($guard -is [System.Management.Automation.Language.TryStatementAst])) { $guard = $guard.Parent }
            [object]::ReferenceEquals($guard, $restart) | Should -BeFalse -Because "the $branchName restart must have its own try inside the finally"
            $guard.CatchClauses.Count | Should -Be 1
            $catch = $guard.CatchClauses[0].Body
            $catch.Extent.Text | Should -Match ([regex]::Escape('$poolStartError = $_.Exception.Message'))
            @($catch.FindAll({ param($n)
                        $n -is [System.Management.Automation.Language.ThrowStatementAst] -or
                        ($n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Write-Fail') }, $true)).Count |
                Should -Be 0 -Because 'a throw from the finally would replace the error that is propagating'

            $after = $statements[$restartIndex + 1]
            $after | Should -BeOfType ([System.Management.Automation.Language.IfStatementAst])
            $after.Clauses[0].Item1.Extent.Text | Should -BeExactly '$poolStartError' -Because "a failed $branchName restart is its own failure once everything else succeeded"
            $after.Clauses[0].Item2.Statements[0] | Should -BeOfType ([System.Management.Automation.Language.ThrowStatementAst])
        }
    }

    It 'runs each branch''s real mirror-and-restart region: a failed mirror leaves the pool stopped, and the first error wins (cdt-16)' {
        # The region - the flags, the try/finally and the check after it - is lifted from deploy.ps1
        # by AST and run with only robocopy, the pool start and the manifest writer stubbed.
        foreach ($name in 'Write-Step', 'Write-Success', 'Write-Warn', 'Write-AppSettingsFile') {
            . ([scriptblock]::Create((Find-FunctionDefinition $s $name).Extent.Text))
        }
        function robocopy { $script:cdt16Calls.Add('robocopy'); $global:LASTEXITCODE = $script:cdt16Exit }
        ${function:Start-AppPoolWithRetry} = { param($Name) $null = $Name; $script:cdt16Calls.Add('start'); if ($script:cdt16StartThrows) { throw 'the pool would not start' } }
        function Write-DeploymentManifest { $script:cdt16Calls.Add('manifest') }

        $modeIf = (Get-FreshInstallBranch $s).Parent
        $regions = [ordered]@{}
        foreach ($entry in @(@('upgrade', $modeIf.Clauses[0].Item2), @('-Force', $modeIf.ElseClause))) {
            $statements = @($entry[1].Statements)
            $tryAt = @(for ($i = 0; $i -lt $statements.Count; $i++) {
                    if ($statements[$i] -is [System.Management.Automation.Language.TryStatementAst] -and $null -ne $statements[$i].Finally -and
                        $statements[$i].Finally.Extent.Text -match 'Start-AppPoolWithRetry') { $i }
                })[0]
            $from = $tryAt
            while ($from -gt 0 -and $statements[$from - 1].Extent.Text -match '^\$(mirrorSucceeded|poolStartError|deploymentReadyForManifest) = ') { $from-- }
            $to = $tryAt
            if ($statements[$to + 1].Extent.Text -match '^if \(\$poolStartError\)') { $to++ }
            $regions[$entry[0]] = ($statements[$from..$to] | ForEach-Object { $_.Extent.Text }) -join "`n"
        }

        $run = {
            param([string]$Branch, [int]$Exit, [bool]$FailAfterMirror, [bool]$StartThrows)
            $script:cdt16Calls = New-Object System.Collections.Generic.List[string]
            $script:cdt16Exit = $Exit
            $script:cdt16StartThrows = $StartThrows
            $root = Join-Path ([System.IO.Path]::GetTempPath()) ('cdt16_' + [guid]::NewGuid().ToString('N'))
            $StagingPath = Join-Path $root 'staging'
            $PublishPath = Join-Path $root 'publish'
            $ProjectRoot = Join-Path $root 'source'
            New-Item -ItemType Directory -Path $StagingPath, $PublishPath, $ProjectRoot -Force | Out-Null
            # Read only by the lifted region.
            Set-Variable -Name AppPoolName -Value 'ExamplePool'
            Set-Variable -Name dbBackup -Value (Join-Path $root 'backup\exchangeadmin.db')
            Set-Variable -Name configStorePathSetting -Value $null
            $configPath = Join-Path $PublishPath 'appsettings.json'
            if ($Branch -eq 'upgrade') {
                $json = '{ "AllowedHosts": "*" }'
                if ($FailAfterMirror) { $json = '{ "AllowedHosts": ' }
                Set-Content -LiteralPath $configPath -Value $json -Encoding ascii
            } elseif ($FailAfterMirror) {
                $configPath = Join-Path $root 'missing-dir\appsettings.json'
            }
            $ErrorActionPreference = 'Stop'
            $thrown = $null
            try {
                . ([scriptblock]::Create($regions[$Branch]))
            } catch {
                $thrown = $_.Exception.Message
            } finally {
                $stagingLeft = Test-Path -LiteralPath $StagingPath
                Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
            }
            [pscustomobject]@{ Thrown = $thrown; Calls = ($script:cdt16Calls -join ','); StagingLeft = $stagingLeft; Staging = $StagingPath }
        }

        foreach ($branch in $regions.Keys) {
            $failedMirror = & $run $branch 8 $false $false 6>$null
            $failedMirror.Calls | Should -BeExactly 'robocopy' -Because "a failed $branch mirror must not restart the pool over a partial publish"
            $failedMirror.Thrown | Should -Match 'robocopy failed with exit code 8'
            $failedMirror.Thrown | Should -Match 'LEFT STOPPED'
            $failedMirror.Thrown | Should -Match ([regex]::Escape($failedMirror.Staging))
            $failedMirror.StagingLeft | Should -BeFalse

            $both = & $run $branch 1 $true $true 6>$null
            $both.Calls | Should -BeExactly 'robocopy,start'
            $both.Thrown | Should -Not -BeNullOrEmpty
            $both.Thrown | Should -Not -Match 'pool would not start|did not start' -Because "the $branch step that failed first must be the error the caller sees"

            $restartOnly = & $run $branch 1 $false $true 6>$null
            $restartOnly.Calls | Should -BeExactly 'robocopy,start,manifest' -Because 'the deploy is complete, so the manifest is still written'
            $restartOnly.Thrown | Should -Match ([regex]::Escape('did not start (the pool would not start)'))
            $restartOnly.Thrown | Should -Match 'DOWN'

            $clean = & $run $branch 1 $false $false 6>$null
            $clean.Thrown | Should -BeNullOrEmpty
            $clean.Calls | Should -BeExactly 'robocopy,start,manifest'
            $clean.StagingLeft | Should -BeFalse
        }
    }

    It 'writes and reports the manifest even when git cannot answer (cdt-14)' {
        # Under PowerShell 7 a failed git leaves $gitCommit $null (5.1 maps it to "unknown"), and
        # the writer - which runs in the finally that restarts the pool - called Substring on it.
        foreach ($name in 'Write-Success', 'Write-DeploymentManifest') {
            $fn = Find-FunctionDefinition $s $name
            $fn | Should -Not -BeNullOrEmpty
            . ([scriptblock]::Create($fn.Extent.Text))
        }
        $publish = Join-Path ([System.IO.Path]::GetTempPath()) ('cdt14_' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $publish -Force | Out-Null
        # deploy.ps1's own preference. Under Continue a failed method call is written and skipped,
        # so the old line would not escape the function and this test would pass for the wrong reason.
        $ErrorActionPreference = 'Stop'
        try {
            $noRepository = Join-Path $publish 'no-such-repository'
            $said = @(Write-DeploymentManifest -PublishFolder $publish -RepoRoot $noRepository 6>&1 | ForEach-Object { "$_" })
            ($said -join "`n") | Should -Match 'Deployment manifest written: '
            $manifest = Get-Content -LiteralPath (Join-Path $publish 'deployment-manifest.json') -Raw | ConvertFrom-Json
            (@($null, '', 'unknown') -contains $manifest.gitCommit) | Should -BeTrue -Because 'git could not answer, so no commit is recorded'

            $head = "$(& git -C $RepoRoot rev-parse HEAD)".Trim()
            $LASTEXITCODE | Should -Be 0
            $said = @(Write-DeploymentManifest -PublishFolder $publish -RepoRoot $RepoRoot 6>&1 | ForEach-Object { "$_" })
            ($said -join "`n") | Should -Match ([regex]::Escape("@ $($head.Substring(0, 8))")) -Because 'a real commit is still shortened to eight characters'
        } finally {
            Remove-Item -LiteralPath $publish -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'shortens the manifest commit only when there is one long enough to shorten (cdt-14)' {
        $fn = Find-FunctionDefinition $s 'Write-DeploymentManifest'
        $calls = @($fn.FindAll({ param($node)
                    $node -is [System.Management.Automation.Language.InvokeMemberExpressionAst] -and
                    $node.Member.Extent.Text -eq 'Substring' }, $true))
        $calls.Count | Should -Be 1 -Because 'one place shortens the commit'
        $guard = $calls[0].Parent
        while ($null -ne $guard -and -not ($guard -is [System.Management.Automation.Language.IfStatementAst])) { $guard = $guard.Parent }
        $guard | Should -Not -BeNullOrEmpty -Because 'Substring on a null or short commit throws'
        $guard.Clauses[0].Item1.Extent.Text | Should -BeExactly '$gitCommit -and $gitCommit.Length -ge 8'
        $calls[0].Extent.StartOffset | Should -BeGreaterThan $guard.Clauses[0].Item2.Extent.StartOffset -Because 'it runs only in the then-branch'
        $calls[0].Extent.EndOffset | Should -BeLessThan $guard.Clauses[0].Item2.Extent.EndOffset
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
        $upgradeBlock | Should -Match "driftCheckExclusions = @\('extended-log-level.txt'\) \+ \`$configCopyExclusions" `
            -Because 'the DB triplets must be excluded from the size/mtime drift inventory (through the shared list since cdt-13)'
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
        $upgradeBlock | Should -Not -Match 'Write-AppSettingsFile' `
            -Because 'the regenerating writer belongs to the fresh-install/-Force path only (cdt-8 moved the write into it)'
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
        $s.Text | Should -Match '-ConfigStorePath \$configStorePlan\.Path\)' `
            -Because 'the generated appsettings must carry the key (the decided one since cdt-11: supplied, else kept)'
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
