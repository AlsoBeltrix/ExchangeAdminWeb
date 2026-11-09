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

    # rev-f1: the folders from the temp directory up are the host's; they are answered as clean so
    # the fixtures' own parents, created and narrowed here, are what each test judges.
    Use-TempAncestryStandIn

    # A narrowed parent folder holding a narrowed app folder (the app folder inherits the parent's
    # narrow entries). Returns both paths.
    function Initialize-ParentFixture {
        $parent = Join-Path ([System.IO.Path]::GetTempPath()) ("tfacl_" + [guid]::NewGuid().ToString('N'))
        Initialize-TightTestFolder -Path $parent | Out-Null
        $app = Join-Path $parent 'app'
        New-Item -ItemType Directory -Path $app | Out-Null
        Copy-Item -LiteralPath (Join-Path $script:AppPath 'ExchangeAdminWeb.dll') -Destination $app
        return [pscustomobject]@{ Parent = $parent; App = $app }
    }
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
            # Modify on the parent itself can also replace the new folder (rev-f1); the one entry is
            # named once, with both consequences (rev-i2).
            @(Get-ToolFolderInheritedWriteExposure -Path $notYet | ForEach-Object { "$($_.Kind)|$($_.Sid)|$($_.AlsoInherited)" }) | Should -Be @("ParentGrant|$($script:AuthenticatedUsers)|True")
            Test-Path -LiteralPath $notYet | Should -BeFalse -Because 'judging it creates nothing'
        } finally { Clear-AppFixture $parent }
    }
}

Describe 'ToolFolderAcl: the folders above the app folder (rev-f1)' {

    It 'finds nothing when the folders above only let the trusted set replace it, though others may read' {
        $f = Initialize-ParentFixture
        try {
            Grant-TestRight -Path $f.Parent -Sid $script:Users -Rights '(OI)(CI)RX'
            @(Get-ToolFolderWriteExposure -Path $f.App).Count | Should -Be 0
        } finally { Clear-AppFixture $f.Parent }
    }

    It 'names a non-inheritable grant on a parent that lets another principal replace the app folder: <Right>' -TestCases @(
        @{ Right = '(DC)'; Name = 'delete child' }
        @{ Right = '(DE)'; Name = 'delete' }
        @{ Right = '(WDAC)'; Name = 'change permissions' }
        @{ Right = '(WO)'; Name = 'take ownership' }
        @{ Right = '(M)'; Name = 'Modify' }
    ) {
        # THE FINDING: the app folder is narrow and inherits nothing from this grant, which sits on
        # the parent alone; a real run used to say the folder was safe.
        $f = Initialize-ParentFixture
        try {
            Grant-TestRight -Path $f.Parent -Sid $script:Users -Rights $Right
            $found = @(Get-ToolFolderWriteExposure -Path $f.App)
            ($found | ForEach-Object { "$($_.Kind)|$($_.Sid)|$($_.Path)|$($_.Inherited)" }) -join ',' |
                Should -BeExactly "ParentGrant|$($script:Users)|$($f.Parent)|False" -Because "$Name on the parent can replace the app folder; the app folder itself carries no grant"
            Format-ToolFolderWriteExposure $found | Should -BeLike "*Users holds * on $($f.Parent), a folder above the app folder, so can replace it"
        } finally { Clear-AppFixture $f.Parent }
    }

    It 'names an inherited grant on a parent, two folders up' {
        # The grandparent passes delete child to the parent only (no propagation further down), so
        # the app folder and its contents carry nothing; the parent does, inherited.
        $grand = Join-Path ([System.IO.Path]::GetTempPath()) ("tfacl_" + [guid]::NewGuid().ToString('N'))
        try {
            Initialize-TightTestFolder -Path $grand | Out-Null
            Grant-TestRight -Path $grand -Sid $script:Users -Rights '(CI)(NP)(IO)(DC)'
            $parent = Join-Path $grand 'parent'
            $app = Join-Path $parent 'app'
            New-Item -ItemType Directory -Path $app | Out-Null
            $found = @(Get-ToolFolderWriteExposure -Path $app)
            ($found | ForEach-Object { "$($_.Kind)|$($_.Path)|$($_.Inherited)" }) -join ',' | Should -BeExactly "ParentGrant|$parent|True"
        } finally { Clear-AppFixture $grand }
    }

    It 'does not count rights on a parent that only add entries beside the app folder: <Right>' -TestCases @(
        @{ Right = '(WD)' }
        @{ Right = '(AD)' }
        @{ Right = '(RX)' }
    ) {
        $f = Initialize-ParentFixture
        try {
            Grant-TestRight -Path $f.Parent -Sid $script:Users -Rights $Right
            @(Get-ToolFolderWriteExposure -Path $f.App).Count | Should -Be 0
        } finally { Clear-AppFixture $f.Parent }
    }

    It 'names a parent owned by a principal outside the trusted set' {
        # Taking ownership for another principal needs a privilege this suite does not hold, so the
        # parent's ACL read is answered with an in-memory one owned by Everyone that grants nothing.
        $f = Initialize-ParentFixture
        try {
            $script:OwnedParent = $f.Parent
            Mock -ModuleName ToolFolderAcl Get-Acl -ParameterFilter { $LiteralPath -eq $script:OwnedParent } -MockWith {
                $security = [System.Security.AccessControl.DirectorySecurity]::new()
                $security.SetOwner([System.Security.Principal.SecurityIdentifier]::new('S-1-1-0'))
                $security
            }
            $found = @(Get-ToolFolderWriteExposure -Path $f.App)
            ($found | ForEach-Object { "$($_.Kind)|$($_.Sid)|$($_.Path)" }) -join ',' | Should -BeExactly "ParentOwner|$($script:Everyone)|$($f.Parent)"
            Format-ToolFolderWriteExposure $found | Should -BeLike "Everyone is the owner of $($f.Parent), a folder above the app folder*"
        } finally { Clear-AppFixture $f.Parent }
    }

    It 'throws, naming the parent, when a parent''s permissions cannot be read' {
        $f = Initialize-ParentFixture
        try {
            $script:UnreadParent = $f.Parent
            Mock -ModuleName ToolFolderAcl Get-Acl -ParameterFilter { $LiteralPath -eq $script:UnreadParent } -MockWith { throw 'Attempted to perform an unauthorized operation.' }
            { Get-ToolFolderWriteExposure -Path $f.App } | Should -Throw -ExpectedMessage "The permissions of $($f.Parent), a folder above $($f.App), cannot be read (Attempted to perform an unauthorized operation.)*"
        } finally { Clear-AppFixture $f.Parent }
    }

    It 'judges a folder not created yet by the folders above it that exist, as well as by what it would inherit' {
        $f = Initialize-ParentFixture
        try {
            $notYet = Join-Path $f.Parent 'staging.1\app'
            @(Get-ToolFolderInheritedWriteExposure -Path $notYet).Count | Should -Be 0
            Grant-TestRight -Path $f.Parent -Sid $script:Users -Rights '(DC)'
            $found = @(Get-ToolFolderInheritedWriteExposure -Path $notYet)
            ($found | ForEach-Object { "$($_.Kind)|$($_.Sid)|$($_.Path)" }) -join ',' | Should -BeExactly "ParentGrant|$($script:Users)|$($f.Parent)"
            Test-Path -LiteralPath (Join-Path $f.Parent 'staging.1') | Should -BeFalse -Because 'judging it creates nothing'
        } finally { Clear-AppFixture $f.Parent }
    }

    It 'Resolve-ConfigDbTool refuses before anything runs, naming the parent and the principal' {
        $f = Initialize-ParentFixture
        try {
            Grant-TestRight -Path $f.Parent -Sid $script:Everyone -Rights '(DC)'
            $err = $null
            try { Resolve-ConfigDbTool -AppPath $f.App | Out-Null } catch { $err = "$_" }
            $err | Should -BeLike "Refusing to run the config DB tool from $($f.App):*Everyone holds * on $($f.Parent), a folder above the app folder, so can replace it*Nothing was changed."
        } finally { Clear-AppFixture $f.Parent }
    }

    It 'Resolve-ConfigDbTool refuses a parent whose permissions cannot be read, naming it' {
        $f = Initialize-ParentFixture
        try {
            $script:UnreadParent = $f.Parent
            Mock -ModuleName ToolFolderAcl Get-Acl -ParameterFilter { $LiteralPath -eq $script:UnreadParent } -MockWith { throw 'Attempted to perform an unauthorized operation.' }
            $err = $null
            try { Resolve-ConfigDbTool -AppPath $f.App | Out-Null } catch { $err = "$_" }
            $err | Should -BeLike "Refusing to run the config DB tool from $($f.App): the folder's permissions cannot be read (The permissions of $($f.Parent), a folder above*"
        } finally { Clear-AppFixture $f.Parent }
    }

    It 'Resolve-ConfigDbTool in plan mode prints the refusal a real run would make, for a folder there and one not there yet' {
        $f = Initialize-ParentFixture
        try {
            Grant-TestRight -Path $f.Parent -Sid $script:Everyone -Rights '(DC)'
            (Resolve-ConfigDbTool -AppPath $f.App -PlanOnly).AclSummary | Should -BeLike "*a real run REFUSES to run the db tool from $($f.App):*Everyone holds * on $($f.Parent), a folder above the app folder*"
            (Resolve-ConfigDbTool -AppPath (Join-Path $f.Parent 'staging.1') -PlanOnly).AclSummary | Should -BeLike "*REFUSES*not there yet*Everyone holds * on $($f.Parent), a folder above the app folder*"
        } finally { Clear-AppFixture $f.Parent }
    }
}

Describe 'ToolFolderAcl: the walk above the app folder stops at the root of the volume, a share on a network path (rev-g1)' {
    # No network is touched: every ACL read of the stand-in share (and of the stand-in drive) is
    # answered in memory, and each path asked is recorded. Split-Path -Parent answers \\server for a
    # share root, which is not a folder; asking for its permissions failed, so an app folder on a
    # share was refused as "a folder above it cannot be read" though its share and folders were safe.

    It 'names the parent the walk reads next, and nothing past the root: <Path>' -TestCases @(
        @{ Path = '\\server\share\App\Sub'; Parent = '\\server\share\App' }
        @{ Path = '\\server\share\App'; Parent = '\\server\share' }
        @{ Path = '\\server\share\App\'; Parent = '\\server\share' }
        @{ Path = '\\server\share'; Parent = $null }
        @{ Path = '\\server\share\'; Parent = $null }
        @{ Path = 'D:\App'; Parent = 'D:\' }
        @{ Path = 'D:\'; Parent = $null }
    ) {
        $next = InModuleScope ToolFolderAcl -Parameters @{ P = $Path } { param($P) Get-AclWalkParent -Path $P }
        $next | Should -Be $Parent
    }

    It 'reads every folder above a share app folder up to the share itself, and never asks for \\server' {
        $script:Asked = [System.Collections.Generic.List[string]]::new()
        Mock -ModuleName ToolFolderAcl Get-Acl -ParameterFilter { ([string]$LiteralPath).StartsWith('\\server', [System.StringComparison]::OrdinalIgnoreCase) } -MockWith {
            $script:Asked.Add([string]$LiteralPath)
            $security = [System.Security.AccessControl.DirectorySecurity]::new()
            $security.SetOwner([System.Security.Principal.SecurityIdentifier]::new('S-1-5-18'))
            $security
        }
        $found = @(InModuleScope ToolFolderAcl {
                Get-ParentChainExposure -FirstParent '\\server\share\App' -Below '\\server\share\App\Sub' -TrustedSid (Get-ToolFolderTrustedSid)
            })
        $found.Count | Should -Be 0
        ($script:Asked | ForEach-Object { $_.TrimEnd('\') }) -join ',' | Should -BeExactly '\\server\share\App,\\server\share'
    }

    It 'still refuses a share root owned by a principal outside the trusted set' {
        Mock -ModuleName ToolFolderAcl Get-Acl -ParameterFilter { ([string]$LiteralPath).StartsWith('\\server', [System.StringComparison]::OrdinalIgnoreCase) } -MockWith {
            $owner = if (([string]$LiteralPath).TrimEnd('\') -eq '\\server\share') { 'S-1-1-0' } else { 'S-1-5-18' }
            $security = [System.Security.AccessControl.DirectorySecurity]::new()
            $security.SetOwner([System.Security.Principal.SecurityIdentifier]::new($owner))
            $security
        }
        $found = @(InModuleScope ToolFolderAcl {
                Get-ParentChainExposure -FirstParent '\\server\share\App' -Below '\\server\share\App\Sub' -TrustedSid (Get-ToolFolderTrustedSid)
            })
        ($found | ForEach-Object { "$($_.Kind)|$($_.Sid)|$($_.Path.TrimEnd('\'))" }) -join ',' | Should -BeExactly "ParentOwner|$($script:Everyone)|\\server\share"
    }

    It 'judges an app folder that is the share root itself, owner included, with no walk above it' {
        $script:Asked = [System.Collections.Generic.List[string]]::new()
        Mock -ModuleName ToolFolderAcl Test-Path -ParameterFilter { ([string]$LiteralPath).StartsWith('\\server', [System.StringComparison]::OrdinalIgnoreCase) } -MockWith { $true }
        Mock -ModuleName ToolFolderAcl Get-Item -ParameterFilter { ([string]$LiteralPath).StartsWith('\\server', [System.StringComparison]::OrdinalIgnoreCase) } -MockWith { [pscustomobject]@{ FullName = '\\server\share\' } }
        Mock -ModuleName ToolFolderAcl Get-ChildItem -ParameterFilter { ([string]$LiteralPath).StartsWith('\\server', [System.StringComparison]::OrdinalIgnoreCase) } -MockWith { @() }
        Mock -ModuleName ToolFolderAcl Get-Acl -ParameterFilter { ([string]$LiteralPath).StartsWith('\\server', [System.StringComparison]::OrdinalIgnoreCase) } -MockWith {
            $script:Asked.Add([string]$LiteralPath)
            $security = [System.Security.AccessControl.DirectorySecurity]::new()
            $security.SetOwner([System.Security.Principal.SecurityIdentifier]::new('S-1-1-0'))
            $security
        }
        $found = @(Get-ToolFolderWriteExposure -Path '\\server\share\')
        ($found | ForEach-Object { "$($_.Kind)|$($_.Sid)|$($_.Path)" }) -join ',' | Should -BeExactly "Owner|$($script:Everyone)|\\server\share"
        ($script:Asked | ForEach-Object { $_.TrimEnd('\') }) -join ',' | Should -BeExactly '\\server\share'
    }

    It 'judges a folder not created yet on a share by the share, and never asks for \\server' {
        $script:Asked = [System.Collections.Generic.List[string]]::new()
        Mock -ModuleName ToolFolderAcl Test-Path -ParameterFilter { ([string]$LiteralPath).StartsWith('\\server', [System.StringComparison]::OrdinalIgnoreCase) } -MockWith {
            ([string]$LiteralPath).TrimEnd('\') -eq '\\server\share'
        }
        Mock -ModuleName ToolFolderAcl Get-Acl -ParameterFilter { ([string]$LiteralPath).StartsWith('\\server', [System.StringComparison]::OrdinalIgnoreCase) } -MockWith {
            $script:Asked.Add([string]$LiteralPath)
            $security = [System.Security.AccessControl.DirectorySecurity]::new()
            $security.SetOwner([System.Security.Principal.SecurityIdentifier]::new('S-1-5-18'))
            $security
        }
        @(Get-ToolFolderInheritedWriteExposure -Path '\\server\share\staging.1\app').Count | Should -Be 0
        $script:Asked | Should -Not -Contain '\\server'
        ($script:Asked | ForEach-Object { $_.TrimEnd('\') } | Select-Object -Unique) -join ',' | Should -BeExactly '\\server\share'
    }

    It 'walks a drive path up to the drive root and stops there' {
        $script:Asked = [System.Collections.Generic.List[string]]::new()
        Mock -ModuleName ToolFolderAcl Get-Acl -ParameterFilter { ([string]$LiteralPath).StartsWith('Q:', [System.StringComparison]::OrdinalIgnoreCase) } -MockWith {
            $script:Asked.Add([string]$LiteralPath)
            $security = [System.Security.AccessControl.DirectorySecurity]::new()
            $security.SetOwner([System.Security.Principal.SecurityIdentifier]::new('S-1-5-18'))
            $security
        }
        $found = @(InModuleScope ToolFolderAcl {
                Get-ParentChainExposure -FirstParent 'Q:\EawApp' -Below 'Q:\EawApp\Sub' -TrustedSid (Get-ToolFolderTrustedSid)
            })
        $found.Count | Should -Be 0
        $script:Asked -join ',' | Should -BeExactly 'Q:\EawApp,Q:\'
    }
}

Describe 'ToolFolderAcl: generic rights are named, not printed as a number (rev-i1)' {
    # The live plan run said "Authenticated Users holds -536805376 (inherited) on ...": an inherit-only
    # entry carrying GENERIC_READ | GENERIC_WRITE | GENERIC_EXECUTE | DELETE (0xE0010000), which
    # FileSystemRights cannot name. Every ACL read of the stand-in drive Q: is answered in memory.

    It 'names the rights in a mask: <Mask>' -TestCases @(
        @{ Mask = 0xE0010000L; Text = 'GenericRead, GenericWrite, GenericExecute, Delete' }
        @{ Mask = 0x10000000L; Text = 'GenericAll' }
        @{ Mask = 0x1301BFL; Text = 'Modify, Synchronize' }
        @{ Mask = 0x1F01FFL; Text = 'FullControl' }
        @{ Mask = 0x40000040L; Text = 'GenericWrite, DeleteSubdirectoriesAndFiles' }
        @{ Mask = 0x01000000L; Text = '0x1000000' }
    ) {
        InModuleScope ToolFolderAcl -Parameters @{ M = $Mask } { param($M) ConvertTo-RightsText -Mask $M } | Should -BeExactly $Text
    }

    It 'reports an inherit-only generic entry a folder not created yet would inherit by name, and counts it as content-changing' {
        Mock -ModuleName ToolFolderAcl Test-Path -ParameterFilter { ([string]$LiteralPath).StartsWith('Q:', [System.StringComparison]::OrdinalIgnoreCase) } -MockWith {
            ([string]$LiteralPath).TrimEnd('\') -eq 'Q:\EawSites'
        }
        Mock -ModuleName ToolFolderAcl Get-Acl -ParameterFilter { ([string]$LiteralPath).StartsWith('Q:', [System.StringComparison]::OrdinalIgnoreCase) } -MockWith {
            $security = [System.Security.AccessControl.DirectorySecurity]::new()
            if (([string]$LiteralPath).TrimEnd('\') -eq 'Q:\EawSites') {
                # GR GW GX SD, object and container inherit, inherit only, inherited.
                $security.SetSecurityDescriptorSddlForm('O:SYG:SYD:(A;OICIIOID;GRGWGXSD;;;S-1-5-11)')
            } else {
                $security.SetOwner([System.Security.Principal.SecurityIdentifier]::new('S-1-5-18'))
            }
            $security
        }
        $found = @(Get-ToolFolderInheritedWriteExposure -Path 'Q:\EawSites\staging.1')
        ($found | ForEach-Object { "$($_.Kind)|$($_.Sid)|$($_.Path)|$($_.Rights)|$($_.Inherited)" }) -join ',' |
            Should -BeExactly 'Grant|S-1-5-11|Q:\EawSites|GenericRead, GenericWrite, GenericExecute, Delete|True' -Because 'the generic bits change content, and the entry passes them to the new folder'
        Format-ToolFolderWriteExposure $found | Should -BeExactly 'NT AUTHORITY\Authenticated Users holds GenericRead, GenericWrite, GenericExecute, Delete (inherited) on Q:\EawSites'
    }
}

Describe 'ToolFolderAcl: one line per entry for a folder not created yet (rev-i2)' {
    # The live plan run named "<group> holds FullControl on <parent>" twice: once from the parent walk
    # (it can replace the new folder) and once from what the new folder would inherit. Both reads are
    # still made; an entry both find is one finding naming both consequences.

    It 'names an untrusted FullControl entry on the parent once, with both consequences' {
        $parent = Join-Path ([System.IO.Path]::GetTempPath()) ("tfacl_" + [guid]::NewGuid().ToString('N'))
        try {
            Initialize-TightTestFolder -Path $parent | Out-Null
            Grant-TestRight -Path $parent -Sid $script:Users -Rights '(OI)(CI)F'
            $found = @(Get-ToolFolderInheritedWriteExposure -Path (Join-Path $parent 'staging.1'))
            ($found | ForEach-Object { "$($_.Kind)|$($_.Sid)|$($_.Path)|$($_.Rights)" }) -join ',' |
                Should -BeExactly "ParentGrant|$($script:Users)|$parent|FullControl"
            Format-ToolFolderWriteExposure $found |
                Should -BeExactly "BUILTIN\Users holds FullControl on $parent, a folder above the app folder, so can replace it, and the new folder would inherit it"
            (Resolve-ConfigDbTool -AppPath (Join-Path $parent 'staging.1') -PlanOnly).AclRefuses | Should -BeTrue -Because 'the merged line still refuses'
        } finally { Clear-AppFixture $parent }
    }

    It 'keeps both lines when the parent holds one entry that replaces and another that is only inherited' {
        $parent = Join-Path ([System.IO.Path]::GetTempPath()) ("tfacl_" + [guid]::NewGuid().ToString('N'))
        try {
            Initialize-TightTestFolder -Path $parent | Out-Null
            Grant-TestRight -Path $parent -Sid $script:Users -Rights '(DC)'
            Grant-TestRight -Path $parent -Sid $script:Everyone -Rights '(OI)(CI)(IO)M'
            $found = @(Get-ToolFolderInheritedWriteExposure -Path (Join-Path $parent 'staging.1'))
            ($found | ForEach-Object { "$($_.Kind)|$($_.Sid)" }) -join ',' | Should -BeExactly "ParentGrant|$($script:Users),Grant|$($script:Everyone)"
        } finally { Clear-AppFixture $parent }
    }

    It 'leaves the existing-folder read as it was: the parent entry and the app folder''s inherited copy are two lines' {
        $f = Initialize-ParentFixture
        try {
            Grant-TestRight -Path $f.Parent -Sid $script:Users -Rights '(OI)(CI)F'
            # The app folder was created before the grant; icacls propagated it.
            $found = @(Get-ToolFolderWriteExposure -Path $f.App | Where-Object { $_.Path -eq $f.Parent -or $_.Path -eq $f.App })
            ($found | ForEach-Object { "$($_.Kind)|$($_.Sid)|$($_.Path)" }) -join ',' | Should -BeExactly "ParentGrant|$($script:Users)|$($f.Parent),Grant|$($script:Users)|$($f.App)"
            @($found | Where-Object { $_.PSObject.Properties['AlsoInherited'] }).Count | Should -Be 0
        } finally { Clear-AppFixture $f.Parent }
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

    It 'rev-f3: in plan mode says whether a real run refuses, so a plan can count it' {
        $dir = Initialize-AppFixture
        try {
            (Resolve-ConfigDbTool -AppPath $dir -PlanOnly).AclRefuses | Should -BeFalse
            Grant-TestRight -Path $dir -Sid $script:Everyone -Rights '(OI)(CI)M'
            (Resolve-ConfigDbTool -AppPath $dir -PlanOnly).AclRefuses | Should -BeTrue
            $script:UnreadApp = $dir
            Mock -ModuleName ToolFolderAcl Get-Acl -ParameterFilter { $LiteralPath -eq $script:UnreadApp } -MockWith { throw 'Attempted to perform an unauthorized operation.' }
            (Resolve-ConfigDbTool -AppPath $dir -PlanOnly).AclRefuses | Should -BeTrue -Because 'unreadable permissions refuse too'
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
