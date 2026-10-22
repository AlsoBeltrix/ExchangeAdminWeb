<#
Behavioral tests for tools/SqliteConfigBackup.psm1 (SqliteConfigStore-Plan Phase D;
ConfigDbToolInApp-Plan S2).

These exercise the real module against the real built app (its `db` mode, run through the .NET
host under Program Files) and real temp DBs:
  - a present DB is backed up and the copy passes integrity_check;
  - a missing DB returns $null (nothing to back up);
  - a corrupt DB makes the backup THROW (abort the deploy);
  - the integrity helper passes on a good DB and throws on a corrupt one;
  - the tool resolver never searches PATH and refuses a missing or too-old app binary.

Nothing here skips: the suites build the app when no build output exists (Get-BuiltApp.ps1).
#>

Set-StrictMode -Version Latest

BeforeAll {
    . (Join-Path $PSScriptRoot 'Get-BuiltApp.ps1')
    $script:AppPath = Get-BuiltApp

    $modulePath = Join-Path $PSScriptRoot '..\..\tools\SqliteConfigBackup.psm1'
    Import-Module $modulePath -Force

    function New-TempDir {
        $d = Join-Path ([System.IO.Path]::GetTempPath()) ("sqbk_" + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $d -Force | Out-Null
        return $d
    }

    function New-ValidDb {
        param([string]$Dir)
        $db = Join-Path $Dir 'exchangeadmin.db'
        New-TestDb -AppPath $script:AppPath -Path $db -Sql 'CREATE TABLE t(x); INSERT INTO t VALUES (1);' | Out-Null
        return $db
    }
}

Describe 'SqliteConfigBackup module' {

    It 'parses and exports the expected functions' {
        $m = Get-Module SqliteConfigBackup
        $m | Should -Not -BeNullOrEmpty
        $m.ExportedFunctions.Keys | Should -Contain 'Backup-SqliteConfigDb'
        $m.ExportedFunctions.Keys | Should -Contain 'Test-SqliteConfigDbIntegrity'
        $m.ExportedFunctions.Keys | Should -Contain 'Resolve-ConfigDbTool'
        $m.ExportedFunctions.Keys | Should -Contain 'Resolve-ConfigDbPath'
        $m.ExportedFunctions.Keys | Should -Contain 'Get-ConfigStorePathSetting'
        $m.ExportedFunctions.Keys | Should -Contain 'Copy-SqliteDbFile'
        $m.ExportedFunctions.Keys | Should -Not -Contain 'Copy-SqliteConfigDb' `
            -Because 'the wholesale promote copy is gone with the shared database (decision 2026-09-04)'
        $m.ExportedFunctions.Keys | Should -Not -Contain 'Get-Sqlite3Path' `
            -Because 'the scripts no longer run sqlite3.exe (ConfigDbToolInApp-Plan)'
        $m.ExportedFunctions.Keys | Should -Not -Contain 'Assert-Sqlite3Available'
        $m.ExportedFunctions.Keys | Should -Not -Contain 'Invoke-ConfigDbTool' -Because 'the runner is private'
    }

    It 'is pure ASCII (deploy.ps1 imports it under Windows PowerShell 5.1 - invariant 6)' {
        $bytes = [System.IO.File]::ReadAllBytes((Join-Path $PSScriptRoot '..\..\tools\SqliteConfigBackup.psm1'))
        ($bytes | Where-Object { $_ -gt 127 }).Count | Should -Be 0
    }

    It 'returns $null when there is no DB to back up' {
        $src = New-TempDir
        $dst = New-TempDir
        try {
            Backup-SqliteConfigDb -ConfigDir $src -DestDir $dst -Timestamp '20260618' -AppPath $script:AppPath | Should -BeNullOrEmpty
            Backup-SqliteConfigDb -DbPath (Join-Path $src 'shared.db') -DestDir $dst -Timestamp '20260618' -AppPath $script:AppPath | Should -BeNullOrEmpty
        } finally {
            Remove-Item $src, $dst -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    Context 'Resolve-ConfigDbTool' {
        It 'takes the .NET host from Program Files and the assembly from the folder it is given' {
            $tool = Resolve-ConfigDbTool -AppPath $script:AppPath
            $tool.Host | Should -Be (Join-Path (Join-Path $env:ProgramFiles 'dotnet') 'dotnet.exe')
            $tool.Assembly | Should -Be (Join-Path $script:AppPath 'ExchangeAdminWeb.dll')
            @($tool.Missing).Count | Should -Be 0
        }

        It 'throws naming the missing assembly in action mode' {
            $empty = New-TempDir
            try {
                $err = $null
                try { Resolve-ConfigDbTool -AppPath $empty | Out-Null } catch { $err = $_ }
                $err | Should -Not -BeNullOrEmpty
                "$err" | Should -BeLike "*$(Join-Path $empty 'ExchangeAdminWeb.dll')*"
            } finally {
                Remove-Item $empty -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It 'in plan mode returns the intended paths and notes the absent one without throwing' {
            $notYet = Join-Path ([System.IO.Path]::GetTempPath()) ("sqbk_staging_" + [guid]::NewGuid().ToString('N'))
            $tool = Resolve-ConfigDbTool -AppPath $notYet -PlanOnly
            $tool.Assembly | Should -Be (Join-Path $notYet 'ExchangeAdminWeb.dll')
            @($tool.Missing) | Should -Contain (Join-Path $notYet 'ExchangeAdminWeb.dll')
            $tool.Summary | Should -BeLike "*$(Join-Path $notYet 'ExchangeAdminWeb.dll')*not present yet*"
            Test-Path -LiteralPath $notYet | Should -BeFalse -Because 'plan mode creates nothing'
        }

        It 'refuses an app binary older than the db mode, before running it' {
            # An app that predates db mode does not exit with a usage error: it would start
            # the web host. So the version is checked before anything is executed.
            $dir = New-TempDir
            try {
                # cdt-4: a deterministic fixture. A text file has no version resource, so
                # FileVersionInfo reads 0/0/0 and the resolver sees 0.0.0, below the floor. The
                # resolver never executes anything; with the floor gone it returns the tool and
                # this assertion fails on the missing throw.
                [System.IO.File]::WriteAllText((Join-Path $dir 'ExchangeAdminWeb.dll'), 'not an assembly')
                { Resolve-ConfigDbTool -AppPath $dir } | Should -Throw -ExpectedMessage '*version 0.0.0, older than the backup tool*deploy dev first*'
            } finally {
                Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }

    Context 'Resolve-ConfigDbPath (SharedConfigDb-Plan AC5)' {
        It 'returns ConfigStore:Path from appsettings.json when it is set' {
            $publish = New-TempDir
            try {
                Set-Content -LiteralPath (Join-Path $publish 'appsettings.json') -Encoding UTF8 -Value @'
{ "Application": { "PathBase": "/ExchangeAdminWebDev" }, "ConfigStore": { "Path": "D:\\inetpub\\ExchangeAdminWebShared\\config\\exchangeadmin.db" } }
'@
                Get-ConfigStorePathSetting -PublishPath $publish | Should -Be 'D:\inetpub\ExchangeAdminWebShared\config\exchangeadmin.db'
                Resolve-ConfigDbPath -PublishPath $publish | Should -Be 'D:\inetpub\ExchangeAdminWebShared\config\exchangeadmin.db'
            } finally {
                Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It 'falls back to <PublishPath>\config\exchangeadmin.db when the key is absent' {
            $publish = New-TempDir
            try {
                Set-Content -LiteralPath (Join-Path $publish 'appsettings.json') -Encoding UTF8 -Value '{ "Application": { "PathBase": "/x" } }'
                Get-ConfigStorePathSetting -PublishPath $publish | Should -BeNullOrEmpty
                Resolve-ConfigDbPath -PublishPath $publish | Should -Be (Join-Path (Join-Path $publish 'config') 'exchangeadmin.db')
            } finally {
                Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It 'treats a blank key as absent' {
            $publish = New-TempDir
            try {
                Set-Content -LiteralPath (Join-Path $publish 'appsettings.json') -Encoding UTF8 -Value '{ "ConfigStore": { "Path": "   " } }'
                Get-ConfigStorePathSetting -PublishPath $publish | Should -BeNullOrEmpty
                Resolve-ConfigDbPath -PublishPath $publish | Should -Be (Join-Path (Join-Path $publish 'config') 'exchangeadmin.db')
            } finally {
                Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It 'falls back to the default when there is no appsettings.json at all' {
            $publish = New-TempDir
            try {
                Get-ConfigStorePathSetting -PublishPath $publish | Should -BeNullOrEmpty
                Resolve-ConfigDbPath -PublishPath $publish | Should -Be (Join-Path (Join-Path $publish 'config') 'exchangeadmin.db')
            } finally {
                Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }

    Context 'cdt-17: a ConfigStore:Path the app would refuse is refused here, naming it' {
        # The app's ConfigStorePath.Resolve refuses UNC and any value that is not fully qualified.
        # The module used to return such a value, and every caller then tested for, backed up and
        # checked whatever it named relative to its own current directory.
        BeforeAll {
            function Write-TestKey {
                param([string]$Publish, [string]$Value)
                $json = @{ ConfigStore = @{ Path = $Value } } | ConvertTo-Json -Depth 3
                Set-Content -LiteralPath (Join-Path $Publish 'appsettings.json') -Encoding UTF8 -Value $json
            }
        }

        It 'refuses <Value> (<Form>) from both readers, naming the value and the file' -TestCases @(
            @{ Form = 'relative'; Value = 'config\exchangeadmin.db' }
            @{ Form = 'drive-relative'; Value = 'D:exchangeadmin.db' }
            @{ Form = 'rooted without a drive'; Value = '\config\exchangeadmin.db' }
            @{ Form = 'UNC'; Value = '\\server\share\exchangeadmin.db' }
            @{ Form = 'UNC with forward slashes'; Value = '//server/share/exchangeadmin.db' }
        ) {
            $publish = New-TempDir
            try {
                Write-TestKey -Publish $publish -Value $Value
                $appsettings = Join-Path $publish 'appsettings.json'
                foreach ($reader in 'Get-ConfigStorePathSetting', 'Resolve-ConfigDbPath') {
                    $err = $null
                    try { & $reader -PublishPath $publish | Out-Null } catch { $err = $_ }
                    $err | Should -Not -BeNullOrEmpty -Because "$reader must refuse a value the app refuses at startup"
                    "$err" | Should -BeLike "*$Value*" -Because 'the message names the value'
                    "$err" | Should -BeLike "*$appsettings*" -Because 'and the file it came from'
                    "$err" | Should -BeLike '*Nothing was changed*'
                }
            } finally {
                Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It 'accepts an absolute local path, trimmed as the app trims it' {
            $publish = New-TempDir
            try {
                Write-TestKey -Publish $publish -Value '  D:\shared\config\exchangeadmin.db  '
                Get-ConfigStorePathSetting -PublishPath $publish | Should -BeExactly 'D:\shared\config\exchangeadmin.db'
                Resolve-ConfigDbPath -PublishPath $publish | Should -BeExactly 'D:\shared\config\exchangeadmin.db'
            } finally {
                Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It 'refuses instead of resolving against the current directory when a matching file is there (the reproduced case)' {
            $root = New-TempDir
            $publish = Join-Path $root 'publish'
            $cwd = Join-Path $root 'cwd'
            New-Item -ItemType Directory -Path $publish, (Join-Path $cwd 'config') -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $cwd 'config\exchangeadmin.db') -Value 'an unrelated database' -Encoding ascii
            Write-TestKey -Publish $publish -Value 'config\exchangeadmin.db'
            Push-Location $cwd
            try {
                Test-Path -LiteralPath 'config\exchangeadmin.db' | Should -BeTrue -Because 'the trap is set: the relative value names a real file from here'
                { Resolve-ConfigDbPath -PublishPath $publish } | Should -Throw -ExpectedMessage '*must be an absolute local file path*config\exchangeadmin.db*'
            } finally {
                Pop-Location
                Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It 'accepts what the app accepts, refuses what it refuses, and reads back whatever the installer and the cutover write (cdt-17, cdt-30, cdt-35)' {
            # One rule in three places: the app's ConfigStorePath.Resolve decides what an instance
            # opens, this module holds the same rule for what the scripts READ, and the two scripts that
            # WRITE the key keep a stricter copy of their own (X:\ only). It used to compare the module
            # with the writers only, which refused D:/... that the app accepts (cdt-30); and the app
            # used to accept four values the module refuses, three of them network shares (cdt-35).
            #
            # The app's rule is pinned in its source and emulated line for line, rather than called:
            # the built app targets .NET 10, which a pwsh on an older runtime cannot load, and the
            # suites run under whatever pwsh CI has. A change to the rule fails the pin below, so the
            # module and this emulation are revisited with it. ExchangeAdminWeb.Tests' ConfigStorePathTests
            # pins the module's lines the same way from the other side.
            $repo = Join-Path $PSScriptRoot '..\..'
            $source = Get-Content -LiteralPath (Join-Path $repo 'Services\Storage\ConfigStorePath.cs') -Raw
            foreach ($line in @(
                    'var value = configured.Trim();',
                    'if (value.Length >= 2 && IsSeparator(value[0]) && IsSeparator(value[1]))',
                    'if (!IsDriveLetterPath(value))',
                    'return new Resolution(value.Replace(''/'', ''\\''), MustExist: true);',
                    'private static bool IsSeparator(char c) => c == ''\\'' || c == ''/'';',
                    'value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == '':'' && IsSeparator(value[2]);')) {
                $source.Contains($line) | Should -BeTrue -Because "ConfigStorePath.Resolve's rule is what the module mirrors and this test emulates ($line); if it changed, change both with it"
            }
            ([regex]::Matches($source, 'throw new InvalidOperationException\(')).Count | Should -Be 2 -Because 'the rule refuses two things: a network path, and anything but a drive-letter path'
            # The app's call on one value: $null when it refuses, else the path it opens.
            $appResolves = {
                param([string]$Value)
                $t = $Value.Trim()
                if ($t.Length -ge 2 -and ($t[0] -eq [char]'\' -or $t[0] -eq [char]'/') -and ($t[1] -eq [char]'\' -or $t[1] -eq [char]'/')) { return $null }
                if ($t.Length -lt 3) { return $null }
                $letter = [int]$t[0]
                $isAsciiLetter = ($letter -ge 65 -and $letter -le 90) -or ($letter -ge 97 -and $letter -le 122)
                if (-not ($isAsciiLetter -and $t[1] -eq [char]':' -and ($t[2] -eq [char]'\' -or $t[2] -eq [char]'/'))) { return $null }
                return $t.Replace('/', '\')
            }
            $validators = @{}
            foreach ($entry in @(@('installer', 'tools\Install-ExchangeAdminWeb.ps1', 'Assert-LocalAbsoluteFilePath'), @('cutover', 'tools\Move-ConfigDbToShared.ps1', 'Assert-LocalAbsolutePath'))) {
                $tokens = $null
                $errors = $null
                $ast = [System.Management.Automation.Language.Parser]::ParseFile((Resolve-Path (Join-Path $repo $entry[1])).Path, [ref]$tokens, [ref]$errors)
                $fn = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $entry[2] }, $true)
                $fn | Should -Not -BeNullOrEmpty -Because "$($entry[1]) defines $($entry[2])"
                $validators[$entry[0]] = [scriptblock]::Create("function Write-Fail { param(`$Message) throw `$Message }`n" + $fn.Extent.Text + "`n" + $entry[2] + ' -Path $args[0] -Name ConfigStorePath')
            }

            $inputs = @('D:\shared\config\exchangeadmin.db', 'c:\x\y.db', 'D:/shared/x.db', 'D:\shared/x.db', ' D:/x.db ', 'z:/a',
                'config\exchangeadmin.db', 'D:exchangeadmin.db', 'D:', '\config\exchangeadmin.db', '/config/exchangeadmin.db',
                '\\server\share\x.db', '//server/share/x.db', '\\?\D:\x\y.db', '\\.\D:\x.db', '1:\x.db', 'DD:\x.db', ':\x.db', 'x')
            # The app accepted these four until cdt-35, and three are network shares to Windows: it
            # reads any two leading separators as a UNC path, and \??\UNC\ is the NT namespace's name
            # for one. Both sides refuse all four now.
            $formerGap = @('\/server/share/x.db', '/\server\share\x.db', '\??\D:\x.db', '\??\UNC\server\share\x.db')
            $kelvin = [string][char]0x212A + ':\x.db'
            $publish = New-TempDir
            try {
                foreach ($value in @($inputs) + @($formerGap) + @($kelvin)) {
                    Write-TestKey -Publish $publish -Value $value
                    $moduleAccepts = $true
                    $moduleValue = $null
                    try { $moduleValue = Get-ConfigStorePathSetting -PublishPath $publish } catch { $moduleAccepts = $false }
                    $app = & $appResolves $value
                    $moduleAccepts | Should -Be ($null -ne $app) -Because "the module and the app must make the same call on '$value'"
                    if ($moduleAccepts) {
                        $moduleValue | Should -BeExactly $app -Because "the module must name the file the app opens for '$value', spelled the same way"
                    }
                    # The writers' own drive test is case-insensitive too, but neither can write the
                    # Kelvin-sign value: no such drive exists, so it is never a key on disk.
                    if ($value -ne $kelvin) {
                        foreach ($name in $validators.Keys) {
                            $scriptAccepts = $true
                            try { & $validators[$name] $value } catch { $scriptAccepts = $false }
                            if ($scriptAccepts) { $moduleAccepts | Should -BeTrue -Because "what the $name writes ('$value') must read back" }
                        }
                    }
                }
            } finally {
                Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        Context 'cdt-30: the app''s drive form with either separator, read back as one spelling' {
            # The module took the writers' X:\ rule, so a D:/... key the app accepts stopped every
            # deploy and upgrade, while its case-insensitive drive test passed a letter the app refuses.

            It 'accepts <Value> (<Form>) from both readers and returns it with backslashes' -TestCases @(
                @{ Form = 'forward slashes'; Value = 'D:/inetpub/ExchangeAdminWebShared/config/exchangeadmin.db'; Expect = 'D:\inetpub\ExchangeAdminWebShared\config\exchangeadmin.db' }
                @{ Form = 'backslashes'; Value = 'D:\inetpub\ExchangeAdminWebShared\config\exchangeadmin.db'; Expect = 'D:\inetpub\ExchangeAdminWebShared\config\exchangeadmin.db' }
                @{ Form = 'mixed'; Value = 'D:\shared/config/exchangeadmin.db'; Expect = 'D:\shared\config\exchangeadmin.db' }
                @{ Form = 'blank-padded, lower-case drive'; Value = '  d:/x/exchangeadmin.db  '; Expect = 'd:\x\exchangeadmin.db' }
            ) {
                $publish = New-TempDir
                try {
                    Write-TestKey -Publish $publish -Value $Value
                    foreach ($reader in 'Get-ConfigStorePathSetting', 'Resolve-ConfigDbPath') {
                        & $reader -PublishPath $publish | Should -BeExactly $Expect -Because "$reader returns the file the app opens, in one spelling"
                    }
                } finally {
                    Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue
                }
            }

            It 'refuses <Form> from both readers, saying why and naming the value and the file' -TestCases @(
                @{ Form = 'a UNC path'; Value = '\\server\share'; Says = 'not a network share' }
                @{ Form = 'a UNC path with forward slashes'; Value = '//server/share/x.db'; Says = 'not a network share' }
                @{ Form = 'a UNC path with mixed separators (the app missed it until cdt-35)'; Value = '\/server/share/x.db'; Says = 'not a network share' }
                @{ Form = 'a UNC path with mixed separators the other way (the app missed it until cdt-35)'; Value = '/\server\share\x.db'; Says = 'not a network share' }
                @{ Form = 'a drive-relative path'; Value = 'D:foo'; Says = 'must be an absolute local file path' }
                @{ Form = 'a drive alone'; Value = 'D:'; Says = 'must be an absolute local file path' }
                @{ Form = 'a relative path'; Value = 'foo\bar'; Says = 'must be an absolute local file path' }
                @{ Form = 'a rooted path without a drive'; Value = '/foo/bar'; Says = 'must be an absolute local file path' }
                @{ Form = 'an NT-namespace path (the app accepted it until cdt-35)'; Value = '\??\D:\x.db'; Says = 'must be an absolute local file path' }
                @{ Form = 'an NT-namespace UNC path (the app accepted it until cdt-35)'; Value = '\??\UNC\server\share\x.db'; Says = 'must be an absolute local file path' }
                @{ Form = 'a non-ASCII drive letter (U+212A KELVIN SIGN, which a case-insensitive match folds onto k)'; Value = ([string][char]0x212A + ':\x.db'); Says = 'must be an absolute local file path' }
            ) {
                $publish = New-TempDir
                try {
                    Write-TestKey -Publish $publish -Value $Value
                    $appsettings = Join-Path $publish 'appsettings.json'
                    foreach ($reader in 'Get-ConfigStorePathSetting', 'Resolve-ConfigDbPath') {
                        $err = $null
                        try { & $reader -PublishPath $publish | Out-Null } catch { $err = $_ }
                        $err | Should -Not -BeNullOrEmpty -Because "$reader must refuse $Form"
                        "$err".Contains($Says) | Should -BeTrue -Because "the refusal says why ($Says): $err"
                        "$err".Contains($Value) | Should -BeTrue -Because 'the message names the value as written'
                        "$err".Contains($appsettings) | Should -BeTrue -Because 'and the file it came from'
                        "$err".Contains('Nothing was changed') | Should -BeTrue
                    }
                } finally {
                    Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue
                }
            }

            It 'finds, and backs up with the real tool, the database a forward-slash key names' {
                $root = New-TempDir
                try {
                    $publish = Join-Path $root 'publish'
                    $shared = Join-Path $root 'shared'
                    New-Item -ItemType Directory -Path $publish, $shared -Force | Out-Null
                    $db = New-ValidDb -Dir $shared
                    Write-TestKey -Publish $publish -Value $db.Replace('\', '/')
                    $resolved = Resolve-ConfigDbPath -PublishPath $publish
                    $resolved | Should -BeExactly $db -Because 'the key names this file, spelled the way the scripts compare it'
                    Test-IsSqliteConfigDbPresent -DbPath $resolved | Should -BeTrue
                    $backup = Backup-SqliteConfigDb -DbPath $resolved -DestDir (Join-Path $root 'backup') -Timestamp 'cdt30' -AppPath $script:AppPath
                    $backup | Should -Not -BeNullOrEmpty -Because 'a deploy can take its verified backup again'
                    Test-SqliteConfigDbIntegrity -DbPath $backup -AppPath $script:AppPath | Should -BeTrue
                } finally {
                    Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
                }
            }
        }
    }

    It 'Test-IsSqliteConfigDbPresent accepts -DbPath and -ConfigDir' {
        $dir = New-TempDir
        try {
            Test-IsSqliteConfigDbPresent -ConfigDir $dir | Should -BeFalse
            Test-IsSqliteConfigDbPresent -DbPath (Join-Path $dir 'shared.db') | Should -BeFalse
            [System.IO.File]::WriteAllText((Join-Path $dir 'shared.db'), 'x')
            Test-IsSqliteConfigDbPresent -DbPath (Join-Path $dir 'shared.db') | Should -BeTrue
            Test-IsSqliteConfigDbPresent -ConfigDir $dir | Should -BeFalse
            { Test-IsSqliteConfigDbPresent } | Should -Throw
        } finally {
            Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }


    It 'Backup-SqliteConfigDb -DbPath backs up a database not named exchangeadmin.db' {
        $src = New-TempDir
        $dst = New-TempDir
        try {
            $db = Join-Path $src 'shared-config.db'
            New-TestDb -AppPath $script:AppPath -Path $db -Sql 'CREATE TABLE t(x); INSERT INTO t VALUES (42);' | Out-Null
            $backup = Backup-SqliteConfigDb -DbPath $db -DestDir $dst -Timestamp '20260904' -AppPath $script:AppPath
            $backup | Should -Be (Join-Path $dst 'exchangeadmin.20260904.db')
            Test-SqliteConfigDbIntegrity -DbPath $backup -AppPath $script:AppPath | Should -BeTrue
            Invoke-TestSqlScalar -AppPath $script:AppPath -Path $backup -Sql 'SELECT x FROM t;' | Should -Be 42
        } finally {
            Remove-Item $src, $dst -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'makes a verified online backup of a valid DB' {
        $src = New-TempDir
        $dst = New-TempDir
        try {
            New-ValidDb -Dir $src | Out-Null
            $backup = Backup-SqliteConfigDb -ConfigDir $src -DestDir $dst -Timestamp '20260618' -AppPath $script:AppPath
            $backup | Should -Not -BeNullOrEmpty
            Test-Path -LiteralPath $backup | Should -BeTrue
            # The backup itself must be a valid, integrity-clean DB.
            Test-SqliteConfigDbIntegrity -DbPath $backup -AppPath $script:AppPath | Should -BeTrue
        } finally {
            Remove-Item $src, $dst -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'THROWS (aborts) when the source DB is corrupt, and leaves no backup file' {
        $src = New-TempDir
        $dst = New-TempDir
        try {
            New-CorruptTestDb -AppPath $script:AppPath -Path (Join-Path $src 'exchangeadmin.db') | Out-Null
            { Backup-SqliteConfigDb -ConfigDir $src -DestDir $dst -Timestamp '20260618' -AppPath $script:AppPath } |
                Should -Throw
            Test-Path -LiteralPath (Join-Path $dst 'exchangeadmin.20260618.db') | Should -BeFalse
        } finally {
            Remove-Item $src, $dst -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'THROWS when the file under the DB name is not a database at all' {
        $src = New-TempDir
        $dst = New-TempDir
        try {
            [System.IO.File]::WriteAllText((Join-Path $src 'exchangeadmin.db'), 'this is not a sqlite database, it is garbage')
            { Backup-SqliteConfigDb -ConfigDir $src -DestDir $dst -Timestamp '20260618' -AppPath $script:AppPath } |
                Should -Throw
        } finally {
            Remove-Item $src, $dst -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'THROWS naming the missing app binary instead of skipping the backup' {
        $src = New-TempDir
        $dst = New-TempDir
        $noApp = New-TempDir
        try {
            New-ValidDb -Dir $src | Out-Null
            { Backup-SqliteConfigDb -ConfigDir $src -DestDir $dst -Timestamp '20260618' -AppPath $noApp } |
                Should -Throw -ExpectedMessage "*$(Join-Path $noApp 'ExchangeAdminWeb.dll')*"
        } finally {
            Remove-Item $src, $dst, $noApp -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'Copy-SqliteDbFile writes a consistent copy of one DB file to another path (cutover seed)' {
        $srcDir = New-TempDir
        $dstDir = New-TempDir
        try {
            # Source (dev's DB): a DB with a distinctive value.
            $src = Join-Path $srcDir 'exchangeadmin.db'
            New-TestDb -AppPath $script:AppPath -Path $src -Sql "CREATE TABLE app_setting(key TEXT PRIMARY KEY, value TEXT); INSERT INTO app_setting VALUES ('k','dev-value');" | Out-Null
            # Destination: a shared path in a directory that does not exist yet, under a different file name.
            $dest = Join-Path (Join-Path $dstDir 'shared') 'exchangeadmin.db'

            $result = Copy-SqliteDbFile -SourceDbPath $src -DestDbPath $dest -AppPath $script:AppPath
            $result | Should -Be $dest
            Test-Path -LiteralPath $dest | Should -BeTrue

            Invoke-TestSqlScalar -AppPath $script:AppPath -Path $dest -Sql "SELECT value FROM app_setting WHERE key='k';" | Should -Be 'dev-value'
            Test-SqliteConfigDbIntegrity -DbPath $result -AppPath $script:AppPath | Should -BeTrue

            # The source is untouched (the backup reads it; it is not moved).
            Test-Path -LiteralPath $src | Should -BeTrue
            @(Get-ChildItem -LiteralPath (Split-Path -Parent $dest) -Filter 'exchangeadmin.copy.*').Count |
                Should -Be 0 -Because 'the GUID temp file is moved into place, never left behind'
        } finally {
            Remove-Item $srcDir, $dstDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'Copy-SqliteDbFile replaces an existing destination and drops its WAL/SHM sidecars' {
        $srcDir = New-TempDir
        $dstDir = New-TempDir
        try {
            $src = Join-Path $srcDir 'a.db'
            New-TestDb -AppPath $script:AppPath -Path $src -Sql "CREATE TABLE t(x); INSERT INTO t VALUES ('new');" | Out-Null
            $dest = Join-Path $dstDir 'b.db'
            New-TestDb -AppPath $script:AppPath -Path $dest -Sql "CREATE TABLE t(x); INSERT INTO t VALUES ('old');" | Out-Null
            [System.IO.File]::WriteAllText("$dest-wal", 'stale')
            [System.IO.File]::WriteAllText("$dest-shm", 'stale')

            Copy-SqliteDbFile -SourceDbPath $src -DestDbPath $dest -AppPath $script:AppPath | Out-Null

            # Checked BEFORE opening the copy, which would itself discard a garbage WAL.
            Test-Path -LiteralPath "$dest-wal" | Should -BeFalse
            Test-Path -LiteralPath "$dest-shm" | Should -BeFalse
            Invoke-TestSqlScalar -AppPath $script:AppPath -Path $dest -Sql 'SELECT x FROM t;' | Should -Be 'new'
        } finally {
            Remove-Item $srcDir, $dstDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'Copy-SqliteDbFile throws and leaves the destination unchanged when the source is corrupt' {
        $srcDir = New-TempDir
        $dstDir = New-TempDir
        try {
            $src = New-CorruptTestDb -AppPath $script:AppPath -Path (Join-Path $srcDir 'a.db')
            $dest = Join-Path $dstDir 'b.db'
            New-TestDb -AppPath $script:AppPath -Path $dest -Sql "CREATE TABLE t(x); INSERT INTO t VALUES ('old');" | Out-Null

            { Copy-SqliteDbFile -SourceDbPath $src -DestDbPath $dest -AppPath $script:AppPath } | Should -Throw
            Invoke-TestSqlScalar -AppPath $script:AppPath -Path $dest -Sql 'SELECT x FROM t;' | Should -Be 'old'
            @(Get-ChildItem -LiteralPath $dstDir -Filter 'exchangeadmin.copy.*').Count | Should -Be 0
        } finally {
            Remove-Item $srcDir, $dstDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'Copy-SqliteDbFile throws when the source DB is absent' {
        $srcDir = New-TempDir
        $dstDir = New-TempDir
        try {
            { Copy-SqliteDbFile -SourceDbPath (Join-Path $srcDir 'missing.db') -DestDbPath (Join-Path $dstDir 'x.db') -AppPath $script:AppPath } | Should -Throw
            Test-Path -LiteralPath (Join-Path $dstDir 'x.db') | Should -BeFalse
        } finally {
            Remove-Item $srcDir, $dstDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'Copy-SqliteDbFile honours -WhatIf (nothing written)' {
        $srcDir = New-TempDir
        $dstDir = New-TempDir
        try {
            $src = Join-Path $srcDir 'a.db'
            New-TestDb -AppPath $script:AppPath -Path $src -Sql 'CREATE TABLE t(x);' | Out-Null
            $dest = Join-Path $dstDir 'b.db'
            Copy-SqliteDbFile -SourceDbPath $src -DestDbPath $dest -AppPath $script:AppPath -WhatIf | Out-Null
            Test-Path -LiteralPath $dest | Should -BeFalse
        } finally {
            Remove-Item $srcDir, $dstDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'Copy-SqliteDbFile -WhatIf does not create the destination directory' {
        # The existing -WhatIf test above passes a destination inside a directory that
        # already exists, so the New-Item was a no-op and the defect never showed. A dry run
        # that creates directories is a dry run that writes.
        $srcDir = New-TempDir
        $dstRoot = New-TempDir
        try {
            $src = Join-Path $srcDir 'a.db'
            New-TestDb -AppPath $script:AppPath -Path $src -Sql 'CREATE TABLE t(x);' | Out-Null

            $missingDir = Join-Path $dstRoot 'not-created-yet'
            $dest = Join-Path $missingDir 'b.db'

            Copy-SqliteDbFile -SourceDbPath $src -DestDbPath $dest -AppPath $script:AppPath -WhatIf | Out-Null

            Test-Path -LiteralPath $missingDir | Should -BeFalse -Because '-WhatIf must not touch the filesystem'
            Test-Path -LiteralPath $dest | Should -BeFalse
        } finally {
            Remove-Item $srcDir, $dstRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'Copy-SqliteDbFile still creates the destination directory when it acts' {
        # A fix that simply stopped creating the directory would pass the test above and
        # break every real caller.
        $srcDir = New-TempDir
        $dstRoot = New-TempDir
        try {
            $src = Join-Path $srcDir 'a.db'
            New-TestDb -AppPath $script:AppPath -Path $src -Sql 'CREATE TABLE t(x);' | Out-Null

            $dest = Join-Path (Join-Path $dstRoot 'made-on-demand') 'b.db'
            Copy-SqliteDbFile -SourceDbPath $src -DestDbPath $dest -AppPath $script:AppPath | Out-Null

            Test-Path -LiteralPath $dest | Should -BeTrue
        } finally {
            Remove-Item $srcDir, $dstRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'integrity helper passes on a good DB and throws on a corrupt one' {
        $dir = New-TempDir
        try {
            $good = New-ValidDb -Dir $dir
            Test-SqliteConfigDbIntegrity -DbPath $good -AppPath $script:AppPath | Should -BeTrue

            $bad = New-CorruptTestDb -AppPath $script:AppPath -Path (Join-Path $dir 'bad.db')
            { Test-SqliteConfigDbIntegrity -DbPath $bad -AppPath $script:AppPath } | Should -Throw

            $garbage = Join-Path $dir 'garbage.db'
            [System.IO.File]::WriteAllText($garbage, 'garbage')
            { Test-SqliteConfigDbIntegrity -DbPath $garbage -AppPath $script:AppPath } | Should -Throw
        } finally {
            Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
