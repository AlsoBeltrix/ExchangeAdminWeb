<#
Behavioral tests for tools/SqliteConfigBackup.psm1 (SqliteConfigStore-Plan Phase D).

These exercise the real module against a real sqlite3.exe and real temp DBs:
  - a present DB is backed up via VACUUM INTO and passes integrity_check;
  - a missing DB returns $null (nothing to back up);
  - a corrupt DB makes the backup THROW (abort the deploy);
  - the integrity helper passes on a good DB and throws on a corrupt one.

If sqlite3.exe is not already on PATH, the winget install location is prepended so the suite
runs in CI/dev without a shell restart. If it cannot be found at all, the sqlite3-dependent
tests are skipped (the module's own fail-fast behavior is still asserted separately).
#>

Set-StrictMode -Version Latest

# Resolved at DISCOVERY time (before BeforeAll) so -Skip can use it. Make sqlite3 resolvable if
# winget put it somewhere not yet on this shell's PATH.
if (-not (Get-Command sqlite3 -ErrorAction SilentlyContinue)) {
    $candidate = Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages\SQLite.SQLite_Microsoft.Winget.Source_8wekyb3d8bbwe'
    if (Test-Path -LiteralPath (Join-Path $candidate 'sqlite3.exe')) {
        $env:PATH = "$candidate;$env:PATH"
    }
}
$HasSqlite = [bool](Get-Command sqlite3 -ErrorAction SilentlyContinue)

BeforeAll {
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
        & sqlite3 $db "CREATE TABLE t(x); INSERT INTO t VALUES (1);"
        return $db
    }
}

Describe 'SqliteConfigBackup module' {

    It 'parses and exports the expected functions' {
        $m = Get-Module SqliteConfigBackup
        $m | Should -Not -BeNullOrEmpty
        $m.ExportedFunctions.Keys | Should -Contain 'Backup-SqliteConfigDb'
        $m.ExportedFunctions.Keys | Should -Contain 'Test-SqliteConfigDbIntegrity'
        $m.ExportedFunctions.Keys | Should -Contain 'Assert-Sqlite3Available'
        $m.ExportedFunctions.Keys | Should -Contain 'Resolve-ConfigDbPath'
        $m.ExportedFunctions.Keys | Should -Contain 'Get-ConfigStorePathSetting'
        $m.ExportedFunctions.Keys | Should -Contain 'Copy-SqliteDbFile'
        $m.ExportedFunctions.Keys | Should -Not -Contain 'Copy-SqliteConfigDb' `
            -Because 'the wholesale promote copy is gone with the shared database (decision 2026-09-04)'
    }

    It 'is pure ASCII (deploy.ps1 imports it under Windows PowerShell 5.1 - invariant 6)' {
        $bytes = [System.IO.File]::ReadAllBytes((Join-Path $PSScriptRoot '..\..\tools\SqliteConfigBackup.psm1'))
        ($bytes | Where-Object { $_ -gt 127 }).Count | Should -Be 0
    }

    It 'returns $null when there is no DB to back up' {
        $src = New-TempDir
        $dst = New-TempDir
        try {
            Backup-SqliteConfigDb -ConfigDir $src -DestDir $dst -Timestamp '20260618' | Should -BeNullOrEmpty
            Backup-SqliteConfigDb -DbPath (Join-Path $src 'shared.db') -DestDir $dst -Timestamp '20260618' | Should -BeNullOrEmpty
        } finally {
            Remove-Item $src, $dst -Recurse -Force -ErrorAction SilentlyContinue
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

    It 'Backup-SqliteConfigDb -DbPath backs up a database not named exchangeadmin.db' -Skip:(-not $HasSqlite) {
        $src = New-TempDir
        $dst = New-TempDir
        try {
            $db = Join-Path $src 'shared-config.db'
            & sqlite3 $db "CREATE TABLE t(x); INSERT INTO t VALUES (42);"
            $backup = Backup-SqliteConfigDb -DbPath $db -DestDir $dst -Timestamp '20260904'
            $backup | Should -Be (Join-Path $dst 'exchangeadmin.20260904.db')
            Test-SqliteConfigDbIntegrity -DbPath $backup | Should -BeTrue
            (& sqlite3 $backup "SELECT x FROM t;").Trim() | Should -Be '42'
        } finally {
            Remove-Item $src, $dst -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'makes a verified online backup of a valid DB' -Skip:(-not $HasSqlite) {
        $src = New-TempDir
        $dst = New-TempDir
        try {
            New-ValidDb -Dir $src | Out-Null
            $backup = Backup-SqliteConfigDb -ConfigDir $src -DestDir $dst -Timestamp '20260618'
            $backup | Should -Not -BeNullOrEmpty
            Test-Path -LiteralPath $backup | Should -BeTrue
            # The backup itself must be a valid, integrity-clean DB.
            Test-SqliteConfigDbIntegrity -DbPath $backup | Should -BeTrue
        } finally {
            Remove-Item $src, $dst -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'THROWS (aborts) when the source DB is corrupt' -Skip:(-not $HasSqlite) {
        $src = New-TempDir
        $dst = New-TempDir
        try {
            # Write garbage bytes under the DB name - VACUUM INTO / integrity_check must reject it.
            $db = Join-Path $src 'exchangeadmin.db'
            [System.IO.File]::WriteAllText($db, 'this is not a sqlite database, it is garbage')
            { Backup-SqliteConfigDb -ConfigDir $src -DestDir $dst -Timestamp '20260618' } |
                Should -Throw
        } finally {
            Remove-Item $src, $dst -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'Copy-SqliteDbFile writes a consistent copy of one DB file to another path (cutover seed)' -Skip:(-not $HasSqlite) {
        $srcDir = New-TempDir
        $dstDir = New-TempDir
        try {
            # Source (dev's DB): a DB with a distinctive value.
            $src = Join-Path $srcDir 'exchangeadmin.db'
            & sqlite3 $src "CREATE TABLE app_setting(key TEXT PRIMARY KEY, value TEXT); INSERT INTO app_setting VALUES ('k','dev-value');"
            # Destination: a shared path in a directory that does not exist yet, under a different file name.
            $dest = Join-Path (Join-Path $dstDir 'shared') 'exchangeadmin.db'

            $result = Copy-SqliteDbFile -SourceDbPath $src -DestDbPath $dest
            $result | Should -Be $dest
            Test-Path -LiteralPath $dest | Should -BeTrue

            (& sqlite3 $dest "SELECT value FROM app_setting WHERE key='k';").Trim() | Should -Be 'dev-value'
            Test-SqliteConfigDbIntegrity -DbPath $result | Should -BeTrue

            # The source is untouched (VACUUM INTO reads it; it is not moved).
            Test-Path -LiteralPath $src | Should -BeTrue
        } finally {
            Remove-Item $srcDir, $dstDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'Copy-SqliteDbFile replaces an existing destination and drops its WAL/SHM sidecars' -Skip:(-not $HasSqlite) {
        $srcDir = New-TempDir
        $dstDir = New-TempDir
        try {
            $src = Join-Path $srcDir 'a.db'
            & sqlite3 $src "CREATE TABLE t(x); INSERT INTO t VALUES ('new');"
            $dest = Join-Path $dstDir 'b.db'
            & sqlite3 $dest "CREATE TABLE t(x); INSERT INTO t VALUES ('old');"
            [System.IO.File]::WriteAllText("$dest-wal", 'stale')
            [System.IO.File]::WriteAllText("$dest-shm", 'stale')

            Copy-SqliteDbFile -SourceDbPath $src -DestDbPath $dest | Out-Null

            # Checked BEFORE opening the copy with sqlite3, which would itself discard a garbage WAL.
            Test-Path -LiteralPath "$dest-wal" | Should -BeFalse
            Test-Path -LiteralPath "$dest-shm" | Should -BeFalse
            (& sqlite3 $dest "SELECT x FROM t;").Trim() | Should -Be 'new'
        } finally {
            Remove-Item $srcDir, $dstDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'Copy-SqliteDbFile throws when the source DB is absent' {
        $srcDir = New-TempDir
        $dstDir = New-TempDir
        try {
            { Copy-SqliteDbFile -SourceDbPath (Join-Path $srcDir 'missing.db') -DestDbPath (Join-Path $dstDir 'x.db') } | Should -Throw
            Test-Path -LiteralPath (Join-Path $dstDir 'x.db') | Should -BeFalse
        } finally {
            Remove-Item $srcDir, $dstDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'Copy-SqliteDbFile honours -WhatIf (nothing written)' -Skip:(-not $HasSqlite) {
        $srcDir = New-TempDir
        $dstDir = New-TempDir
        try {
            $src = Join-Path $srcDir 'a.db'
            & sqlite3 $src "CREATE TABLE t(x);"
            $dest = Join-Path $dstDir 'b.db'
            Copy-SqliteDbFile -SourceDbPath $src -DestDbPath $dest -WhatIf | Out-Null
            Test-Path -LiteralPath $dest | Should -BeFalse
        } finally {
            Remove-Item $srcDir, $dstDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'integrity helper passes on a good DB and throws on a corrupt one' -Skip:(-not $HasSqlite) {
        $dir = New-TempDir
        try {
            $good = New-ValidDb -Dir $dir
            Test-SqliteConfigDbIntegrity -DbPath $good | Should -BeTrue

            $bad = Join-Path $dir 'bad.db'
            [System.IO.File]::WriteAllText($bad, 'garbage')
            { Test-SqliteConfigDbIntegrity -DbPath $bad } | Should -Throw
        } finally {
            Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
