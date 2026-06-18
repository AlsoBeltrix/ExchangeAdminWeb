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
    }

    It 'returns $null when there is no DB to back up' {
        $src = New-TempDir
        $dst = New-TempDir
        try {
            Backup-SqliteConfigDb -ConfigDir $src -DestDir $dst -Timestamp '20260618' | Should -BeNullOrEmpty
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
            # Write garbage bytes under the DB name — VACUUM INTO / integrity_check must reject it.
            $db = Join-Path $src 'exchangeadmin.db'
            [System.IO.File]::WriteAllText($db, 'this is not a sqlite database, it is garbage')
            { Backup-SqliteConfigDb -ConfigDir $src -DestDir $dst -Timestamp '20260618' } |
                Should -Throw
        } finally {
            Remove-Item $src, $dst -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'Copy-SqliteConfigDb replaces dest with a consistent copy of source (wholesale promote)' -Skip:(-not $HasSqlite) {
        $srcDir = New-TempDir
        $dstDir = New-TempDir
        try {
            # Source (dev): a DB with a distinctive value.
            & sqlite3 (Join-Path $srcDir 'exchangeadmin.db') "CREATE TABLE app_setting(key TEXT PRIMARY KEY, value TEXT); INSERT INTO app_setting VALUES ('k','dev-value');"
            # Dest (prod): a DIFFERENT DB that must be fully replaced.
            & sqlite3 (Join-Path $dstDir 'exchangeadmin.db') "CREATE TABLE app_setting(key TEXT PRIMARY KEY, value TEXT); INSERT INTO app_setting VALUES ('k','prod-old'); INSERT INTO app_setting VALUES ('prod-only','x');"

            $result = Copy-SqliteConfigDb -SourceConfigDir $srcDir -DestConfigDir $dstDir
            $result | Should -Be (Join-Path $dstDir 'exchangeadmin.db')

            # Dest now mirrors source exactly: dev value present, prod-only row gone.
            $val = (& sqlite3 (Join-Path $dstDir 'exchangeadmin.db') "SELECT value FROM app_setting WHERE key='k';").Trim()
            $val | Should -Be 'dev-value'
            $prodOnly = (& sqlite3 (Join-Path $dstDir 'exchangeadmin.db') "SELECT COUNT(*) FROM app_setting WHERE key='prod-only';").Trim()
            $prodOnly | Should -Be '0'

            Test-SqliteConfigDbIntegrity -DbPath $result | Should -BeTrue
        } finally {
            Remove-Item $srcDir, $dstDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'Copy-SqliteConfigDb throws when the source DB is absent' {
        $srcDir = New-TempDir
        $dstDir = New-TempDir
        try {
            { Copy-SqliteConfigDb -SourceConfigDir $srcDir -DestConfigDir $dstDir } | Should -Throw
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
