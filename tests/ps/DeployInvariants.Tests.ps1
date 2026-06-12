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
}

Describe 'deploy.ps1' {
    BeforeAll { $script:s = Get-ScriptUnderTest 'deploy.ps1' }

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

    It 'backs up the whole runtime config directory before the upgrade mirror (incident fix #4)' {
        $upgradeBlock = [regex]::Match(
            $s.Text,
            '(?s)# --- UPGRADE ---.*?# --- FRESH INSTALL ---'
        ).Value

        $upgradeBlock | Should -Match 'Copy-Item \$runtimeConfigDir \$configDirBackup -Recurse' `
            -Because 'the 2026-06-12 incident had no pre-deploy snapshot of config/ to diagnose from'
        $upgradeBlock | Should -Match 'config\.\$\{timestamp\}\.bak' `
            -Because 'the snapshot must be timestamped and retained like appsettings backups'
        $upgradeBlock.IndexOf('Copy-Item $runtimeConfigDir') |
            Should -BeLessThan $upgradeBlock.IndexOf('robocopy') `
            -Because 'the snapshot must be taken before any files change'
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
}
