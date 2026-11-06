<#
Sweep item 12 (sweep-g): the scripts run `<ProgramFiles>\dotnet\dotnet.exe <AppPath>\ExchangeAdminWeb.dll db ...`
elevated, and on the dev host that folder grants Authenticated Users Modify, inherited from the drive. These
pin the run-time check that refuses such a folder (tools/ToolFolderAcl.psm1), its use by
Resolve-ConfigDbTool and by the job check, and the plan line each caller prints instead of refusing.

The fixtures are real folders under the temp directory, narrowed with icacls the way the suites' copy of
the build is (Get-BuiltApp.ps1, Initialize-TightTestFolder), then widened with one grant for the case at hand.
#>

Set-StrictMode -Version Latest

BeforeAll {
    . (Join-Path $PSScriptRoot 'Get-BuiltApp.ps1')
    $script:AppPath = Get-BuiltApp
    $script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

    Import-Module (Join-Path $script:RepoRoot 'tools\ToolFolderAcl.psm1') -Force
    Import-Module (Join-Path $script:RepoRoot 'tools\SqliteConfigBackup.psm1') -Force
    Import-Module (Join-Path $script:RepoRoot 'tools\JobStateWarning.psm1') -Force

    # A narrowed folder holding the real assembly (so the version floor passes), a dependency file
    # and a nested native-library folder, as a publish folder has.
    function Initialize-AppFixture {
        $dir = Join-Path ([System.IO.Path]::GetTempPath()) ("tfacl_" + [guid]::NewGuid().ToString('N'))
        Initialize-TightTestFolder -Path $dir | Out-Null
        Copy-Item -LiteralPath (Join-Path $script:AppPath 'ExchangeAdminWeb.dll') -Destination $dir
        Set-Content -LiteralPath (Join-Path $dir 'ExchangeAdminWeb.deps.json') -Value '{}' -Encoding ascii
        $native = Join-Path $dir 'runtimes\win-x64\native'
        New-Item -ItemType Directory -Path $native -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $native 'e_sqlite3.dll') -Value 'stand-in' -Encoding ascii
        New-Item -ItemType Directory -Path (Join-Path $dir 'config'), (Join-Path $dir 'logs') -Force | Out-Null
        return $dir
    }

    function Grant-TestRight {
        param([string]$Path, [string]$Sid, [string]$Rights)
        & icacls $Path /grant "*${Sid}:$Rights" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "icacls failed (exit $LASTEXITCODE) granting $Sid $Rights on $Path" }
    }

    function Clear-AppFixture {
        param([string]$Path)
        if ($Path -and (Test-Path -LiteralPath $Path)) { Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue }
    }

    $script:Everyone = 'S-1-1-0'
    $script:Users = 'S-1-5-32-545'
    $script:AuthenticatedUsers = 'S-1-5-11'
}

Describe 'ToolFolderAcl: who besides the administrators can change the code in an app folder' {

    It 'is pure ASCII, because deploy.ps1 loads it under Windows PowerShell 5.1' {
        $bytes = [System.IO.File]::ReadAllBytes((Join-Path $script:RepoRoot 'tools\ToolFolderAcl.psm1'))
        @($bytes | Where-Object { $_ -gt 127 }).Count | Should -Be 0
    }

    It 'finds nothing in a folder only SYSTEM, the administrators and the current account can change' {
        $dir = Initialize-AppFixture
        try {
            @(Get-ToolFolderWriteExposure -Path $dir).Count | Should -Be 0
        } finally { Clear-AppFixture $dir }
    }

    It 'lets another principal read and execute: <Name>' -TestCases @(
        @{ Name = 'Users'; Sid = 'S-1-5-32-545' }
        @{ Name = 'Everyone'; Sid = 'S-1-1-0' }
    ) {
        $dir = Initialize-AppFixture
        try {
            Grant-TestRight -Path $dir -Sid $Sid -Rights '(OI)(CI)RX'
            @(Get-ToolFolderWriteExposure -Path $dir).Count | Should -Be 0 -Because 'the app pool identity, for one, needs to read the folder'
        } finally { Clear-AppFixture $dir }
    }

    It 'names an inherited Modify grant on the folder: <Name>' -TestCases @(
        @{ Name = 'Authenticated Users'; Sid = 'S-1-5-11' }
        @{ Name = 'Users'; Sid = 'S-1-5-32-545' }
        @{ Name = 'Everyone'; Sid = 'S-1-1-0' }
        @{ Name = 'INTERACTIVE'; Sid = 'S-1-5-4' }
    ) {
        # THE FINDING's shape: the grant sits on a folder above and reaches the app folder by
        # inheritance, which is how the dev host's D:\ grant reaches D:\inetpub\ExchangeAdminWebDev.
        $parent = Join-Path ([System.IO.Path]::GetTempPath()) ("tfacl_" + [guid]::NewGuid().ToString('N'))
        try {
            Initialize-TightTestFolder -Path $parent | Out-Null
            Grant-TestRight -Path $parent -Sid $Sid -Rights '(OI)(CI)M'
            $dir = Join-Path $parent 'app'
            New-Item -ItemType Directory -Path $dir | Out-Null
            Copy-Item -LiteralPath (Join-Path $script:AppPath 'ExchangeAdminWeb.dll') -Destination $dir

            $found = @(Get-ToolFolderWriteExposure -Path $dir)
            $grant = @($found | Where-Object { $_.Sid -eq $Sid -and $_.Path -eq $dir })
            $grant.Count | Should -Be 1
            $grant[0].Inherited | Should -BeTrue
            $grant[0].Kind | Should -Be 'Grant'
            @($found | Where-Object { $_.Sid -eq $Sid -and $_.Path -eq (Join-Path $dir 'ExchangeAdminWeb.dll') }).Count | Should -Be 1 -Because 'the assembly inherits it too'
        } finally { Clear-AppFixture $parent }
    }

    It 'names a grant on the assembly alone, though the folder is narrow' {
        $dir = Initialize-AppFixture
        try {
            $dll = Join-Path $dir 'ExchangeAdminWeb.dll'
            Grant-TestRight -Path $dll -Sid $script:Users -Rights '(W)'
            $found = @(Get-ToolFolderWriteExposure -Path $dir)
            ($found | ForEach-Object { "$($_.Sid)|$($_.Path)" }) -join ',' | Should -BeExactly "$($script:Users)|$dll"
        } finally { Clear-AppFixture $dir }
    }

    It 'names a grant on a file the tool loads from a folder under the app folder' {
        $dir = Initialize-AppFixture
        try {
            $native = Join-Path $dir 'runtimes\win-x64\native\e_sqlite3.dll'
            Grant-TestRight -Path $native -Sid $script:Everyone -Rights '(M)'
            @(Get-ToolFolderWriteExposure -Path $dir | ForEach-Object Path) | Should -Be @($native)
        } finally { Clear-AppFixture $dir }
    }

    It 'leaves out the config and logs folders at the top, which the app pool identity writes on purpose' {
        $dir = Initialize-AppFixture
        try {
            Grant-TestRight -Path (Join-Path $dir 'config') -Sid $script:Users -Rights '(OI)(CI)M'
            Grant-TestRight -Path (Join-Path $dir 'logs') -Sid $script:Users -Rights '(OI)(CI)M'
            Set-Content -LiteralPath (Join-Path $dir 'config\exchangeadmin.db') -Value 'x' -Encoding ascii
            @(Get-ToolFolderWriteExposure -Path $dir).Count | Should -Be 0
        } finally { Clear-AppFixture $dir }
    }

    It 'names an owner outside the trusted set, who can rewrite the permissions at any time' {
        # Taking ownership for another principal needs a privilege this suite does not hold, so the
        # ACL read is answered with an in-memory one whose owner is Everyone and which grants nothing.
        $dir = Initialize-AppFixture
        try {
            $security = [System.Security.AccessControl.DirectorySecurity]::new()
            $security.SetOwner([System.Security.Principal.SecurityIdentifier]::new($script:Everyone))
            Mock -ModuleName ToolFolderAcl Get-Acl { $security }.GetNewClosure()
            $found = @(Get-ToolFolderWriteExposure -Path $dir)
            @($found | Where-Object { $_.Kind -eq 'Owner' -and $_.Sid -eq $script:Everyone }).Count | Should -BeGreaterThan 0
        } finally { Clear-AppFixture $dir }
    }

    It 'throws, rather than answering "nothing found", for a folder that is not there' {
        $missing = Join-Path ([System.IO.Path]::GetTempPath()) ("tfacl_" + [guid]::NewGuid().ToString('N'))
        { Get-ToolFolderWriteExposure -Path $missing } | Should -Throw -ExpectedMessage '*does not exist*'
    }

    It 'judges a folder not created yet by what it would inherit from the folder above it' {
        $parent = Join-Path ([System.IO.Path]::GetTempPath()) ("tfacl_" + [guid]::NewGuid().ToString('N'))
        try {
            Initialize-TightTestFolder -Path $parent | Out-Null
            $notYet = Join-Path $parent 'staging.1'
            @(Get-ToolFolderInheritedWriteExposure -Path $notYet).Count | Should -Be 0

            Grant-TestRight -Path $parent -Sid $script:AuthenticatedUsers -Rights '(OI)(CI)M'
            @(Get-ToolFolderInheritedWriteExposure -Path $notYet | ForEach-Object Sid) | Should -Be @($script:AuthenticatedUsers)
            Test-Path -LiteralPath $notYet | Should -BeFalse -Because 'judging it creates nothing'
        } finally { Clear-AppFixture $parent }
    }
}

Describe 'Resolve-ConfigDbTool refuses an app folder others can change (sweep item 12)' {

    It 'returns the tool for a narrow folder' {
        $dir = Initialize-AppFixture
        try {
            $tool = Resolve-ConfigDbTool -AppPath $dir
            $tool.Assembly | Should -Be (Join-Path $dir 'ExchangeAdminWeb.dll')
        } finally { Clear-AppFixture $dir }
    }

    It 'refuses, naming the grant, before anything runs' {
        $dir = Initialize-AppFixture
        try {
            Grant-TestRight -Path $dir -Sid $script:Everyone -Rights '(OI)(CI)M'
            $err = $null
            try { Resolve-ConfigDbTool -AppPath $dir | Out-Null } catch { $err = "$_" }
            $err | Should -BeLike "Refusing to run the config DB tool from ${dir}:*"
            $err | Should -BeLike '*Everyone holds*'
            $err | Should -BeLike '*Nothing was changed.'
        } finally { Clear-AppFixture $dir }
    }

    It 'refuses through every function that runs the tool: <Name>' -TestCases @(
        @{ Name = 'Backup-SqliteConfigDb' }
        @{ Name = 'Test-SqliteConfigDbIntegrity' }
        @{ Name = 'Copy-SqliteDbFile' }
    ) {
        $dir = Initialize-AppFixture
        $work = Join-Path ([System.IO.Path]::GetTempPath()) ("tfacl_" + [guid]::NewGuid().ToString('N'))
        try {
            New-Item -ItemType Directory -Path $work | Out-Null
            $db = Join-Path $work 'a.db'
            Set-Content -LiteralPath $db -Value 'not opened' -Encoding ascii
            Grant-TestRight -Path $dir -Sid $script:Users -Rights '(OI)(CI)M'
            $call = switch ($Name) {
                'Backup-SqliteConfigDb' { { Backup-SqliteConfigDb -DbPath $db -DestDir (Join-Path $work 'out') -Timestamp 't' -AppPath $dir } }
                'Test-SqliteConfigDbIntegrity' { { Test-SqliteConfigDbIntegrity -DbPath $db -AppPath $dir } }
                'Copy-SqliteDbFile' { { Copy-SqliteDbFile -SourceDbPath $db -DestDbPath (Join-Path $work 'b.db') -AppPath $dir } }
            }
            $call | Should -Throw -ExpectedMessage "Refusing to run the config DB tool from ${dir}:*"
            Test-Path -LiteralPath (Join-Path $work 'out') | Should -BeFalse -Because 'the refusal comes before anything is written'
        } finally {
            Clear-AppFixture $dir
            Clear-AppFixture $work
        }
    }

    It 'in plan mode reports what a real run would do, and refuses nothing' {
        $dir = Initialize-AppFixture
        try {
            (Resolve-ConfigDbTool -AppPath $dir -PlanOnly).AclSummary | Should -BeLike '*only the administrators, SYSTEM, TrustedInstaller and the account running this script*'

            Grant-TestRight -Path $dir -Sid $script:Everyone -Rights '(OI)(CI)M'
            $plan = Resolve-ConfigDbTool -AppPath $dir -PlanOnly
            $plan.AclSummary | Should -BeLike "*a real run REFUSES to run the db tool from ${dir}:*Everyone holds*"
            @($plan.AclExposure).Count | Should -BeGreaterThan 0
        } finally { Clear-AppFixture $dir }
    }

    It 'in plan mode judges a staging folder not published yet by its parent' {
        $parent = Join-Path ([System.IO.Path]::GetTempPath()) ("tfacl_" + [guid]::NewGuid().ToString('N'))
        try {
            Initialize-TightTestFolder -Path $parent | Out-Null
            Grant-TestRight -Path $parent -Sid $script:AuthenticatedUsers -Rights '(OI)(CI)M'
            $plan = Resolve-ConfigDbTool -AppPath (Join-Path $parent 'staging.1') -PlanOnly
            $plan.AclSummary | Should -BeLike '*REFUSES*not there yet; judged by what it would inherit*Authenticated Users*'
        } finally { Clear-AppFixture $parent }
    }

    It 'is printed by every caller''s plan: <Script>' -TestCases @(
        @{ Script = 'tools\promote-dev-to-prod.ps1' }
        @{ Script = 'tools\Move-ConfigDbToShared.ps1' }
        @{ Script = 'tools\Install-ExchangeAdminWeb.ps1' }
    ) {
        $text = Get-Content -LiteralPath (Join-Path $script:RepoRoot $Script) -Raw
        $text | Should -Match 'Write-Plan \$configDbToolPlan\.AclSummary' -Because 'a plan that resolves the tool must say whether the real run would refuse its folder'
    }
}

Describe 'The job check does not run the tool from an app folder others can change (sweep item 12)' {

    It 'warns, names the grant, and reads no jobs' {
        $dir = Initialize-AppFixture
        $config = Join-Path ([System.IO.Path]::GetTempPath()) ("tfacl_" + [guid]::NewGuid().ToString('N'))
        try {
            New-Item -ItemType Directory -Path $config | Out-Null
            # Not a database: a tool that ran would fail on it with a different warning.
            Set-Content -LiteralPath (Join-Path $config 'exchangeadmin-jobs.db') -Value 'not opened' -Encoding ascii
            Grant-TestRight -Path $dir -Sid $script:Everyone -Rights '(OI)(CI)M'
            $warnings = $null
            $jobs = @(Get-ActiveBulkJobs -ConfigDir $config -AppPath $dir -WarningVariable warnings 3>$null)
            $jobs.Count | Should -Be 0
            ($warnings -join ' ') | Should -BeLike "*db tool in $dir is not run*Everyone holds*"
            ($warnings -join ' ') | Should -Not -BeLike '*db tool exit*' -Because 'the tool was never started'
        } finally {
            Clear-AppFixture $dir
            Clear-AppFixture $config
        }
    }
}
