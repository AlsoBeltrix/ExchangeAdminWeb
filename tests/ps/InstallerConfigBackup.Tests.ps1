<#
Audit finding A36: the generic installer upgraded an instance - mirroring new binaries over
it - without ever snapshotting the config database those binaries may migrate on first start.

These are source guards, and the reason is worth stating rather than glossing: the backup is
a dozen lines of inline script inside a 790-line installer that needs IIS, a build and a real
database to run. Extracting it into a function purely so a test could call it would move the
ordering question - which is the actual finding - out of the place the ordering has to hold.

So what is pinned here is ORDER and FATALITY, read from the source. The backup mechanism
itself is already covered behaviourally by SqliteConfigBackup.Tests.ps1 against a real
sqlite3 and a real database; this suite does not re-test it.
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

    It 'treats a missing sqlite3 as fatal, not as a warning' {
        # The whole point of the backup is the upgrade that goes wrong. Warning and
        # proceeding would reproduce the finding with extra output.
        $script:Installer | Should -Match 'Assert-Sqlite3Available'
    }

    It 'checks for an existing database before demanding sqlite3' {
        # A fresh install has nothing to back up and must not acquire a new hard dependency.
        $presenceAt = $script:Installer.IndexOf('Test-IsSqliteConfigDbPresent')
        $assertAt = $script:Installer.IndexOf('Assert-Sqlite3Available')

        $presenceAt | Should -BeGreaterThan 0
        $presenceAt | Should -BeLessThan $assertAt
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
        # the branch dead.
        $script:Installer | Should -Match '\$configuredDbPath = Get-ConfigStorePathSetting'
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

    It 'makes skipping the snapshot an explicit switch' {
        $script:Installer | Should -Match '\[switch\]\$SkipConfigBackup'
        $script:Installer | Should -Match 'if \(\$SkipConfigBackup\)'
    }

    It 'supports -PlanOnly like every other step' {
        # Architectural Invariant 4.
        $script:Installer | Should -Match 'Invoke-PlanOrAction "Back up the config database before upgrading"'
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
