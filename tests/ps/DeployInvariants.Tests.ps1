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
