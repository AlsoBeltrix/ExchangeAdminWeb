<#
Behavioral tests for tools/AppPoolControl.psm1 (audit finding A35).

The defect was an empty catch around Stop-WebAppPool followed by a fixed sleep, after
which robocopy /MIR replaced the binaries of a possibly-still-running application. These
drive the real function and assert on what it does when IIS does not cooperate.

No IIS is needed: the three IIS operations are injectable script blocks whose defaults are
the real cmdlets. The injection exists for this suite, so the suite uses it.
#>

Set-StrictMode -Version Latest

BeforeAll {
    $script:ModulePath = Join-Path $PSScriptRoot '..\..\tools\AppPoolControl.psm1'
    Import-Module $script:ModulePath -Force
}

AfterAll {
    Remove-Module AppPoolControl -Force -ErrorAction SilentlyContinue
}

Describe 'Stop-AppPoolVerified' {

    Context 'when the pool stops' {

        It 'returns Stopped once IIS reports it stopped' {
            $result = Stop-AppPoolVerified -Name 'Pool' `
                -ExistsProbe { $true } `
                -StopAction  { } `
                -StateProbe  { 'Stopped' } `
                -Sleep       { }

            $result | Should -Be 'Stopped'
        }

        It 'waits for a pool that is still stopping rather than trusting a fixed sleep' {
            # The old code slept 3 seconds and carried on. A pool draining a long request
            # takes longer than any guess, so the function must keep asking.
            $script:states = @('Starting', 'Stopping', 'Stopping', 'Stopped')
            $script:index = 0
            $script:sleeps = 0

            $result = Stop-AppPoolVerified -Name 'Pool' `
                -ExistsProbe { $true } `
                -StopAction  { } `
                -StateProbe  { $s = $script:states[$script:index]; $script:index++; $s } `
                -Sleep       { $script:sleeps++ }

            $result | Should -Be 'Stopped'
            $script:sleeps | Should -Be 3
        }

        It 'tolerates the already-stopped exception, which is why the catch existed' {
            # Stop-WebAppPool throws on an already-stopped pool. That must stay harmless -
            # a fix that propagated it would break every redeploy of a stopped instance.
            $result = Stop-AppPoolVerified -Name 'Pool' `
                -ExistsProbe { $true } `
                -StopAction  { throw 'The application pool is already stopped.' } `
                -StateProbe  { 'Stopped' } `
                -Sleep       { }

            $result | Should -Be 'Stopped'
        }
    }

    Context 'when the pool does not stop' {

        It 'throws rather than letting the caller deploy over a running application' {
            # THE FINDING. Previously this swallowed and continued into robocopy /MIR.
            {
                Stop-AppPoolVerified -Name 'Pool' -TimeoutSeconds 0 `
                    -ExistsProbe { $true } `
                    -StopAction  { throw 'Access denied.' } `
                    -StateProbe  { 'Started' } `
                    -Sleep       { }
            } | Should -Throw -ExpectedMessage "*still 'Started'*"
        }

        It 'surfaces what the stop attempt said, because the cause changes the fix' {
            # "Access denied" is a different problem from a worker that will not exit, and
            # the old empty catch threw that distinction away.
            {
                Stop-AppPoolVerified -Name 'Pool' -TimeoutSeconds 0 `
                    -ExistsProbe { $true } `
                    -StopAction  { throw 'Access denied.' } `
                    -StateProbe  { 'Started' } `
                    -Sleep       { }
            } | Should -Throw -ExpectedMessage '*stop attempt reported: Access denied.*'
        }

        It 'names the pool and refuses in terms the operator can act on' {
            {
                Stop-AppPoolVerified -Name 'ExchangeAdminWeb' -TimeoutSeconds 0 `
                    -ExistsProbe { $true } `
                    -StopAction  { } `
                    -StateProbe  { 'Stopping' } `
                    -Sleep       { }
            } | Should -Throw -ExpectedMessage '*ExchangeAdminWeb*Refusing to deploy over a running application*'
        }

        It 'fails closed when the state cannot be read at all' {
            # An unreadable state is not a stopped pool.
            {
                Stop-AppPoolVerified -Name 'Pool' `
                    -ExistsProbe { $true } `
                    -StopAction  { } `
                    -StateProbe  { throw 'RPC server unavailable' } `
                    -Sleep       { }
            } | Should -Throw -ExpectedMessage '*cannot be confirmed stopped*'
        }
    }

    Context 'when there is no such pool' {

        It 'reports Absent instead of throwing, because nothing is serving' {
            # The installer's first run. There is no live application to corrupt.
            $script:stopCalled = $false

            $result = Stop-AppPoolVerified -Name 'Pool' `
                -ExistsProbe { $false } `
                -StopAction  { $script:stopCalled = $true } `
                -StateProbe  { 'Started' } `
                -Sleep       { }

            $result | Should -Be 'Absent'
            $script:stopCalled | Should -BeFalse
        }
    }
}

Describe 'A35 call sites' {

    It 'leaves no bare Stop-WebAppPool before a mirroring deploy' {
        # The guard that would catch a new copy of the old pattern being pasted back in.
        $repo = Join-Path $PSScriptRoot '..\..'
        foreach ($file in @('deploy.ps1', 'tools\Install-ExchangeAdminWeb.ps1')) {
            $text = Get-Content (Join-Path $repo $file) -Raw
            $text | Should -Not -Match 'try \{ Stop-WebAppPool[^}]*\} catch \{\}'
        }

        # aud-a38b: the promotion's copy had a different shape - no empty catch, just a bare
        # call and a fixed sleep - so the pattern above could never have caught it.
        $promote = Get-Content (Join-Path $repo 'tools\promote-dev-to-prod.ps1') -Raw
        $promote | Should -Not -Match '(?m)^\s*Stop-WebAppPool\b'
    }

    It 'calls the verified stop in both deploy paths and in the installer' {
        $repo = Join-Path $PSScriptRoot '..\..'

        $deploy = Get-Content (Join-Path $repo 'deploy.ps1') -Raw
        ([regex]::Matches($deploy, 'Stop-AppPoolVerified')).Count | Should -Be 2

        $install = Get-Content (Join-Path $repo 'tools\Install-ExchangeAdminWeb.ps1') -Raw
        ([regex]::Matches($install, 'Stop-AppPoolVerified')).Count | Should -Be 1
    }

    It 'keeps the module pure ASCII, because Windows PowerShell 5.1 reads deploy.ps1' {
        # Architectural Invariant 6: a BOM-less file with non-ASCII is read as ANSI by 5.1
        # and becomes a parse error. deploy.ps1 imports this module.
        $bytes = [System.IO.File]::ReadAllBytes((Join-Path $PSScriptRoot '..\..\tools\AppPoolControl.psm1'))
        ($bytes | Where-Object { $_ -gt 127 }).Count | Should -Be 0
    }
}

Describe 'Resolve-AppPoolIdentityPlan' {

    # Audit finding A34. The installer treated an omitted -ServiceAccount as an instruction
    # to set identityType 4, so an UPDATE silently moved a pool off its domain service
    # account - taking the credential store, the certificate private key, the directory and
    # the shared config database with it, none of which failed at install time.

    Context 'an update that does not mention the service account' {

        It 'keeps an existing custom identity instead of resetting it' {
            $plan = Resolve-AppPoolIdentityPlan -AppPoolName 'ExchangeAdminWeb' `
                -PoolExists $true -ExistingIdentityType 3 -ExistingUserName 'CONTOSO\svc-eaw' `
                -ResetRequested $false

            $plan.Action | Should -Be 'PreserveExisting'
        }

        It 'ACLs the account the app actually runs as, not the virtual account' {
            # Preserving the identity and then granting Modify to "IIS AppPool\<name>" would
            # be the same outage by a longer route.
            $plan = Resolve-AppPoolIdentityPlan -AppPoolName 'ExchangeAdminWeb' `
                -PoolExists $true -ExistingIdentityType 3 -ExistingUserName 'CONTOSO\svc-eaw' `
                -ResetRequested $false

            $plan.IdentityForAcl | Should -Be 'CONTOSO\svc-eaw'
        }

        It 'tells the operator how to change it on purpose' {
            $plan = Resolve-AppPoolIdentityPlan -AppPoolName 'ExchangeAdminWeb' `
                -PoolExists $true -ExistingIdentityType 3 -ExistingUserName 'CONTOSO\svc-eaw' `
                -ResetRequested $false

            $plan.Reason | Should -Match '-ServiceAccount'
            $plan.Reason | Should -Match '-ResetAppPoolIdentity'
        }
    }

    Context 'a pool already on ApplicationPoolIdentity' {

        It 'sets ApplicationPoolIdentity without comment' {
            # Nothing is being taken away, so there is nothing to warn about.
            $plan = Resolve-AppPoolIdentityPlan -AppPoolName 'ExchangeAdminWeb' `
                -PoolExists $true -ExistingIdentityType 4 -ExistingUserName '' `
                -ResetRequested $false

            $plan.Action | Should -Be 'SetApplicationPoolIdentity'
            $plan.IdentityForAcl | Should -Be 'IIS AppPool\ExchangeAdminWeb'
            $plan.Reason | Should -BeNullOrEmpty
        }
    }

    Context 'a fresh install' {

        It 'defaults to ApplicationPoolIdentity when no account is given' {
            $plan = Resolve-AppPoolIdentityPlan -AppPoolName 'ExchangeAdminWeb' `
                -PoolExists $false -ExistingIdentityType $null -ExistingUserName '' `
                -ResetRequested $false

            $plan.Action | Should -Be 'SetApplicationPoolIdentity'
            $plan.IdentityForAcl | Should -Be 'IIS AppPool\ExchangeAdminWeb'
        }

        It 'uses the named account when one is given' {
            $plan = Resolve-AppPoolIdentityPlan -ServiceAccount 'CONTOSO\svc-eaw' `
                -AppPoolName 'ExchangeAdminWeb' -PoolExists $false `
                -ExistingIdentityType $null -ExistingUserName '' -ResetRequested $false

            $plan.Action | Should -Be 'SetCustom'
            $plan.IdentityForAcl | Should -Be 'CONTOSO\svc-eaw'
        }
    }

    Context 'changing the identity on purpose' {

        It 'switches to a different named account' {
            $plan = Resolve-AppPoolIdentityPlan -ServiceAccount 'CONTOSO\svc-new' `
                -AppPoolName 'ExchangeAdminWeb' -PoolExists $true `
                -ExistingIdentityType 3 -ExistingUserName 'CONTOSO\svc-old' -ResetRequested $false

            $plan.Action | Should -Be 'SetCustom'
            $plan.IdentityForAcl | Should -Be 'CONTOSO\svc-new'
        }

        It 'resets to ApplicationPoolIdentity when explicitly asked, and says what breaks' {
            $plan = Resolve-AppPoolIdentityPlan -AppPoolName 'ExchangeAdminWeb' `
                -PoolExists $true -ExistingIdentityType 3 -ExistingUserName 'CONTOSO\svc-eaw' `
                -ResetRequested $true

            $plan.Action | Should -Be 'SetApplicationPoolIdentity'
            $plan.IdentityForAcl | Should -Be 'IIS AppPool\ExchangeAdminWeb'
            $plan.Reason | Should -Match 'CONTOSO\\svc-eaw'
            $plan.Reason | Should -Match 'credential store'
        }
    }
}

Describe 'A34 call site' {

    It 'acts on the decision, not on whether -ServiceAccount was passed' {
        $installer = Get-Content (Join-Path $PSScriptRoot '..\..\tools\Install-ExchangeAdminWeb.ps1') -Raw

        $installer | Should -Match "identityPlan.Action -eq 'SetCustom'"
        $installer | Should -Match "identityPlan.Action -eq 'SetApplicationPoolIdentity'"

        # The old unconditional else is what reset the identity.
        $installer | Should -Not -Match '\} else \{\s*Set-ItemProperty[^\n]*identityType -Value 4'
    }

    It 'grants the folder ACLs to the identity the plan chose' {
        $installer = Get-Content (Join-Path $PSScriptRoot '..\..\tools\Install-ExchangeAdminWeb.ps1') -Raw

        $installer | Should -Match '\$appPoolIdentity = \$identityPlan.IdentityForAcl'
    }

    It 'offers the reset as an explicit switch' {
        $installer = Get-Content (Join-Path $PSScriptRoot '..\..\tools\Install-ExchangeAdminWeb.ps1') -Raw

        $installer | Should -Match '\[switch\]\$ResetAppPoolIdentity'
    }
}

Describe 'Start-AppPoolVerified' {

    # Audit finding A38. The promotion script called Start-WebAppPool and printed "Prod app
    # pool started" on the next line, having checked nothing.

    It 'returns Started once IIS confirms it' {
        $result = Start-AppPoolVerified -Name 'Pool' `
            -ExistsProbe { $true } -StartAction { } -StateProbe { 'Started' } -Sleep { }

        $result | Should -Be 'Started'
    }

    It 'waits through Starting rather than reporting the request as the outcome' {
        $script:states = @('Stopped', 'Starting', 'Started')
        $script:index = 0

        $result = Start-AppPoolVerified -Name 'Pool' `
            -ExistsProbe { $true } -StartAction { } `
            -StateProbe { $s = $script:states[$script:index]; $script:index++; $s } -Sleep { }

        $result | Should -Be 'Started'
    }

    It 'throws when the pool never reaches Started, and says the application is down' {
        # THE FINDING. IIS accepts the request; the pool stays stopped; the operator was
        # told it was running.
        {
            Start-AppPoolVerified -Name 'ExchangeAdminWeb' -TimeoutSeconds 0 `
                -ExistsProbe { $true } -StartAction { } -StateProbe { 'Stopped' } -Sleep { }
        } | Should -Throw -ExpectedMessage '*ExchangeAdminWeb*DOWN*'
    }

    It 'surfaces what the start attempt said' {
        {
            Start-AppPoolVerified -Name 'Pool' -TimeoutSeconds 0 `
                -ExistsProbe { $true } `
                -StartAction { throw 'The service cannot be started.' } `
                -StateProbe { 'Stopped' } -Sleep { }
        } | Should -Throw -ExpectedMessage '*start attempt reported: The service cannot be started.*'
    }

    It 'treats a missing pool as an error, unlike the stop side' {
        # Asymmetry on purpose: nothing to stop is safe, nothing to start means the
        # application that was supposed to come back is not there.
        {
            Start-AppPoolVerified -Name 'Pool' `
                -ExistsProbe { $false } -StartAction { } -StateProbe { 'Started' } -Sleep { }
        } | Should -Throw -ExpectedMessage '*does not exist*'
    }

    It 'fails closed when the state cannot be read' {
        {
            Start-AppPoolVerified -Name 'Pool' `
                -ExistsProbe { $true } -StartAction { } `
                -StateProbe { throw 'RPC server unavailable' } -Sleep { }
        } | Should -Throw -ExpectedMessage '*cannot be confirmed running*'
    }
}

Describe 'A38: the promotion restart cannot erase the promotion verdict' {

    BeforeAll {
        $script:Promote = Get-Content (Join-Path $PSScriptRoot '..\..\tools\promote-dev-to-prod.ps1') -Raw
    }

    It 'does not let the restart throw out of the finally block' {
        # An exception raised in finally REPLACES whatever was propagating, so a failed
        # restart used to erase the promotion and rollback verdict entirely.
        $script:Promote | Should -Match '(?s)\} finally \{.*?try \{\s*Start-AppPoolChecked'
    }

    It 'records the restart failure instead of discarding it' {
        $script:Promote | Should -Match '\$poolStartError = \$_.Exception.Message'
    }

    It 'reports a failed promotion AND a dead pool together, not whichever threw last' {
        $script:Promote | Should -Match '(?s)if \(\$promotionFailed\).*?if \(\$poolStartError\).*?NOT running'
    }

    It 'reports the case the old shape could not: promotion succeeded, application down' {
        # New binaries in place, pool stopped, and previously a success message.
        #
        # The condition and the throw are matched TOGETHER. A first version of this guard
        # asserted only the message text, and a mutation that changed the branch to
        # if ($false) left the string sitting there unreachable and the guard green - the
        # repo's recurring "a source scan passes for the wrong reason" trap.
        $script:Promote | Should -Match '(?s)if \(\$poolStartError\) \{\s*throw "Promotion completed and prod now holds the new binaries, but the app pool did not start'
    }

    It 'confirms the pool is running rather than announcing it' {
        $script:Promote | Should -Match 'Start-AppPoolVerified -Name \$Name'
        $script:Promote | Should -Not -Match '(?m)^\s*Start-WebAppPool -Name \$Name\s*$'
    }
}

Describe 'aud-a38b: the promotion confirms its pool stopped before mirroring over it' {

    # The promotion stopped prod with a bare Stop-WebAppPool and a three-second sleep, then
    # mirrored dev over the live prod folder. aud-a35 replaced that pattern in deploy.ps1 and
    # the installer, and aud-a38 replaced the START half of this script. These pin the STOP half.

    BeforeAll {
        $promotePath = (Resolve-Path (Join-Path $PSScriptRoot '..\..\tools\promote-dev-to-prod.ps1')).Path
        $tokens = $null
        $parseErrors = $null
        $script:PromoteAst = [System.Management.Automation.Language.Parser]::ParseFile($promotePath, [ref]$tokens, [ref]$parseErrors)
        $script:PromoteText = Get-Content -LiteralPath $promotePath -Raw

        function Get-PromoteFunction {
            param([string]$FunctionName)
            $script:PromoteAst.FindAll({
                    param($node)
                    $node -is [System.Management.Automation.Language.FunctionDefinitionAst]
                }, $true) | Where-Object Name -EQ $FunctionName | Select-Object -First 1
        }

        function Get-PromoteCommand {
            param([string]$CommandName)
            @($script:PromoteAst.FindAll({
                        param($node)
                        $node -is [System.Management.Automation.Language.CommandAst]
                    }, $true) | Where-Object { $_.GetCommandName() -eq $CommandName })
        }

        # The script runs its promotion at the top level, so it cannot be dot-sourced. Its stop
        # step, and the helpers that step calls, are lifted out of the file unchanged and
        # defined here, so the tests below drive the code that ships.
        foreach ($name in 'Write-Step', 'Write-Ok', 'Write-Warn', 'Write-Fail', 'Write-AppPoolSharingReport', 'Stop-AppPoolChecked') {
            . ([scriptblock]::Create((Get-PromoteFunction $name).Extent.Text))
        }

        # Stand-ins, each recording what it was asked to do. Stop-WebAppPool is among them so
        # that no run of this suite - a mutation that pastes the bare call back included - can
        # reach the real IIS cmdlet on a machine that has IIS.
        $script:promoteEvents = [System.Collections.Generic.List[string]]::new()
        function Write-Plan { param([string]$Message) $script:promoteEvents.Add("plan: $Message") }
        function Assert-NoActiveBulkJobsBeforeRecycle {
            param([string]$ConfigDir, [string]$AppPath, [switch]$PlanOnly)
            $script:promoteEvents.Add("jobs: $ConfigDir $AppPath PlanOnly=$([bool]$PlanOnly)")
            # An active job: the check warns, and must not stop the promotion.
            [pscustomobject]@{ JobId = 'job-1'; Status = 'Running' }
        }
        function Stop-WebAppPool { param([string]$Name) $script:promoteEvents.Add("bare Stop-WebAppPool: $Name") }

        # The module's own injection points, reached through the real Stop-AppPoolVerified that
        # the lifted stop step calls. $PSDefaultParameterValues is read from the caller's scope,
        # so each test sets it locally and nothing leaks between tests.
        #
        # A script block VALUE in $PSDefaultParameterValues is evaluated to produce the default,
        # so each probe is handed over wrapped in a block that returns it. The first draft passed
        # the probes bare: the binding failed with only a warning, the real IIS probe ran instead,
        # and two of the tests below still passed. Each test therefore also proves that its own
        # probe was the one consulted.
        function New-PoolProbe {
            [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '',
                Justification = 'Exists, State, Serving and Others are used inside GetNewClosure blocks; the analyzer cannot see into them')]
            param(
                [scriptblock]$Exists,
                [scriptblock]$State,
                [int]$TimeoutSeconds = 60,
                # aud-a38c: what IIS reports serving the prod folder. By default, the prod pool alone.
                [scriptblock]$Serving = {
                    $script:promoteEvents.Add('applications probe')
                    [pscustomobject]@{ Location = 'Web/prod'; ApplicationPool = 'aud-a38b-prod'; PhysicalPath = 'X:\prod' }
                },
                # cdt-58: what IIS reports to the sharing report before the stop. By default prod alone,
                # which is at the folder and so no OTHER application. Its own event name: the tests
                # above take IndexOf('applications probe') and it must keep meaning the folder check.
                [scriptblock]$Others = {
                    $script:promoteEvents.Add('other-applications probe')
                    [pscustomobject]@{ Location = 'Web/prod'; ApplicationPool = 'aud-a38b-prod'; PhysicalPath = 'X:\prod' }
                }
            )
            @{
                'Assert-AppPoolServesPath:ApplicationsProbe' = { $Serving }.GetNewClosure()
                'Get-AppPoolOtherApplication:ApplicationsProbe' = { $Others }.GetNewClosure()
                'Stop-AppPoolVerified:ExistsProbe'    = { $Exists }.GetNewClosure()
                'Stop-AppPoolVerified:StopAction'     = { { param($PoolName) $script:promoteEvents.Add("verified stop: $PoolName") } }
                'Stop-AppPoolVerified:StateProbe'     = { $State }.GetNewClosure()
                'Stop-AppPoolVerified:Sleep'          = { { } }
                'Stop-AppPoolVerified:TimeoutSeconds' = $TimeoutSeconds
            }
        }
    }

    BeforeEach {
        $script:promoteEvents.Clear()
    }

    Context 'the stop step, driven through the module''s injectable probes' {

        It 'stops the pool it was given, through the verified stop, when IIS confirms Stopped' {
            # The lifted functions read the script-level -Apply switch.
            Set-Variable -Name Apply -Value $true
            $PSDefaultParameterValues = New-PoolProbe -Exists { $true } -State { 'Stopped' }

            Stop-AppPoolChecked -Name 'aud-a38b-prod' -ConfigDir 'X:\prod\config' -AppPath 'X:\dev' -PublishPath 'X:\prod'

            $script:promoteEvents | Should -Contain 'verified stop: aud-a38b-prod'
            $script:promoteEvents | Should -Not -Contain 'bare Stop-WebAppPool: aud-a38b-prod'
        }

        It 'refuses an absent prod pool, because prod must exist to be promoted to' {
            # Stop-AppPoolVerified reports Absent without throwing - right for a first install,
            # wrong here: a promotion replaces a prod application that must already exist.
            Set-Variable -Name Apply -Value $true
            $PSDefaultParameterValues = New-PoolProbe -Exists { $script:promoteEvents.Add('exists probe'); $false } -State { 'Started' }

            { Stop-AppPoolChecked -Name 'aud-a38b-prod' -PublishPath 'X:\prod' } |
                Should -Throw -ExpectedMessage "*'aud-a38b-prod' does not exist*must exist to be promoted to*"
            $script:promoteEvents | Should -Contain 'exists probe'
        }

        It 'lets a pool that will not stop end the promotion before the mirror' {
            # THE FINDING. The old shape slept three seconds and carried on regardless.
            Set-Variable -Name Apply -Value $true
            $PSDefaultParameterValues = New-PoolProbe -Exists { $true } -State { 'Started' } -TimeoutSeconds 0

            { Stop-AppPoolChecked -Name 'aud-a38b-prod' -PublishPath 'X:\prod' } |
                Should -Throw -ExpectedMessage "*'aud-a38b-prod' is still 'Started'*Refusing to deploy over a running application*"
            $script:promoteEvents | Should -Contain 'verified stop: aud-a38b-prod'
        }

        It 'warns about active bulk jobs before the stop, and does not let them block it' {
            Set-Variable -Name Apply -Value $true
            $PSDefaultParameterValues = New-PoolProbe -Exists { $true } -State { 'Stopped' }

            Stop-AppPoolChecked -Name 'aud-a38b-prod' -ConfigDir 'X:\prod\config' -AppPath 'X:\dev' -PublishPath 'X:\prod'

            $jobs = $script:promoteEvents.IndexOf('jobs: X:\prod\config X:\dev PlanOnly=False')
            $stop = $script:promoteEvents.IndexOf('verified stop: aud-a38b-prod')
            $jobs | Should -BeGreaterOrEqual 0
            $stop | Should -BeGreaterThan $jobs
        }

        It 'in a dry run prints the plan and touches no pool' {
            Set-Variable -Name Apply -Value $false
            $PSDefaultParameterValues = New-PoolProbe `
                -Exists { $script:promoteEvents.Add('exists probe'); $true } `
                -State { $script:promoteEvents.Add('state probe'); 'Stopped' }

            Stop-AppPoolChecked -Name 'aud-a38b-prod' -ConfigDir 'X:\prod\config' -AppPath 'X:\dev' -PublishPath 'X:\prod'

            $script:promoteEvents | Should -Not -Contain 'exists probe'
            $script:promoteEvents | Should -Not -Contain 'state probe'
            $script:promoteEvents | Should -Not -Contain 'verified stop: aud-a38b-prod'
            $script:promoteEvents | Should -Contain 'jobs: X:\prod\config X:\dev PlanOnly=True'
            @($script:promoteEvents | Where-Object { $_ -like 'plan: Stop-AppPoolVerified -Name aud-a38b-prod*' }).Count | Should -Be 1
        }
    }

    Context 'cdt-58: what else the pool serves is named, and its jobs read, before the stop' {

        # The checks before the stop looked only at prod: its jobs, and the IIS nodes at or inside the
        # prod folder. An application the same pool serves from elsewhere went down with it unannounced,
        # and its bulk jobs were never read.

        BeforeAll {
            # A drive letter this host does not have, so that an application IIS still names on it can be
            # listed. Join-Path throws on such a drive (checked in the test that uses it).
            $inUse = @([System.IO.DriveInfo]::GetDrives() | ForEach-Object { $_.Name.Substring(0, 1).ToUpperInvariant() })
            $inUse += @(Get-PSDrive -PSProvider FileSystem | ForEach-Object { $_.Name.ToUpperInvariant() })
            $script:Cdt58MissingDrive = @('Q', 'R', 'S', 'T', 'U', 'V', 'W', 'Y', 'Z', 'J', 'K', 'L', 'M', 'N', 'O', 'P' | Where-Object { $inUse -notcontains $_ })[0]
        }

        BeforeEach {
            # Every line the lifted functions print, in order with the stand-ins' events. Defined in the
            # test's own scope, so it goes with the test.
            ${function:Write-Host} = { param([Parameter(Position = 0)]$Object, $ForegroundColor) $null = $ForegroundColor; $script:promoteEvents.Add("said: $Object") }
        }

        It 'names every other application the pool serves, reads the jobs of each whose folder is on disk, and only then stops' {
            $reports = Join-Path $TestDrive 'reports'
            New-Item -ItemType Directory -Path $reports -Force | Out-Null
            $gone = Join-Path $TestDrive 'gone'
            $script:Cdt58MissingDrive | Should -Not -BeNullOrEmpty -Because 'the test needs a drive letter this host does not have'
            $lost = "$($script:Cdt58MissingDrive):\lost"
            { Join-Path $lost 'config' -ErrorAction Stop } | Should -Throw -Because "the drive '$($script:Cdt58MissingDrive):' does not exist here, which is the case the on-disk guard is for"
            Set-Variable -Name Apply -Value $true
            $PSDefaultParameterValues = New-PoolProbe -Exists { $script:promoteEvents.Add('exists probe'); $true } -State { 'Stopped' } -Others {
                $script:promoteEvents.Add('other-applications probe')
                [pscustomobject]@{ Location = 'Web/prod'; ApplicationPool = 'aud-a38b-prod'; PhysicalPath = 'X:\prod' }
                [pscustomobject]@{ Location = 'Web/reports'; ApplicationPool = 'AUD-A38B-PROD'; PhysicalPath = $reports }
                [pscustomobject]@{ Location = 'Other/'; ApplicationPool = 'aud-a38b-prod'; PhysicalPath = '' }
                [pscustomobject]@{ Location = 'Other/gone'; ApplicationPool = 'aud-a38b-prod'; PhysicalPath = $gone }
                [pscustomobject]@{ Location = 'Other/lost'; ApplicationPool = 'aud-a38b-prod'; PhysicalPath = $lost }
                [pscustomobject]@{ Location = 'Web/elsewhere'; ApplicationPool = 'OtherPool'; PhysicalPath = $reports }
            }

            Stop-AppPoolChecked -Name 'aud-a38b-prod' -ConfigDir 'X:\prod\config' -AppPath 'X:\dev' -PublishPath 'X:\prod'

            @($script:promoteEvents | Where-Object { $_ -like 'said: *  !  *' }) |
                Should -Be @("said:   !  App pool 'aud-a38b-prod' also serves 4 other IIS application(s), which stop with it: Web/reports with its files at '$reports'; Other/ with its files at ''; Other/gone with its files at '$gone'; Other/lost with its files at '$lost'.") -Because 'prod is at the folder, and the other pool''s application stops with its own pool'
            @($script:promoteEvents | Where-Object { $_ -like 'jobs: *' }) |
                Should -Be @("jobs: $reports\config X:\dev PlanOnly=False", 'jobs: X:\prod\config X:\dev PlanOnly=False') -Because 'the other application with a folder on disk is read from its own config folder with the build being promoted, before prod; one with no folder, a folder that is gone or a drive that is not there holds no jobs database'
            @($script:promoteEvents | Where-Object { $_ -like 'said: *Active bulk jobs of IIS application*' }) |
                Should -Be @("said:        Active bulk jobs of IIS application Web/reports, with its files at '$reports', which stops with the pool:")
            $report = $script:promoteEvents.IndexOf('other-applications probe')
            $report | Should -BeGreaterOrEqual 0 -Because "the test's probe is the one the report consulted"
            $script:promoteEvents.IndexOf("jobs: $reports\config X:\dev PlanOnly=False") | Should -BeGreaterThan $report
            $script:promoteEvents.IndexOf('applications probe') | Should -BeGreaterThan $script:promoteEvents.IndexOf('jobs: X:\prod\config X:\dev PlanOnly=False')
            $script:promoteEvents.IndexOf('verified stop: aud-a38b-prod') | Should -BeGreaterThan $script:promoteEvents.IndexOf('applications probe') -Because 'the operator is told what the stop takes down before it is stopped'
        }

        It 'says so when the pool serves nothing else, and reads only prod''s jobs' {
            Set-Variable -Name Apply -Value $true
            $PSDefaultParameterValues = New-PoolProbe -Exists { $true } -State { 'Stopped' }

            Stop-AppPoolChecked -Name 'aud-a38b-prod' -ConfigDir 'X:\prod\config' -AppPath 'X:\dev' -PublishPath 'X:\prod'

            $script:promoteEvents | Should -Contain 'other-applications probe'
            @($script:promoteEvents | Where-Object { $_ -like 'said: *serves*' }) |
                Should -Be @("said:        App pool 'aud-a38b-prod' serves no IIS application other than prod at 'X:\prod': stopping it stops nothing else.")
            @($script:promoteEvents | Where-Object { $_ -like 'said: *  !  *' }) | Should -BeNullOrEmpty -Because 'nothing else stops, so there is nothing to warn about'
            @($script:promoteEvents | Where-Object { $_ -like 'jobs: *' }) | Should -Be @('jobs: X:\prod\config X:\dev PlanOnly=False')
            $script:promoteEvents | Should -Contain 'verified stop: aud-a38b-prod'
        }

        It 'warns, and never refuses, when the listing cannot be read; the folder check reading the same listing refuses before the stop' {
            Set-Variable -Name Apply -Value $true
            $expected = "said:   !  Stopping app pool 'aud-a38b-prod' may stop other IIS applications too, and their active bulk jobs are not read: Cannot read the IIS sites and applications, so the applications app pool 'aud-a38b-prod' serves besides the one at 'X:\prod' cannot be listed: Access is denied"

            # The report's read alone failing: a warning, and the promotion goes on to the stop.
            $PSDefaultParameterValues = New-PoolProbe -Exists { $true } -State { 'Stopped' } -Others { $script:promoteEvents.Add('other-applications probe'); throw 'Access is denied' }
            { Stop-AppPoolChecked -Name 'aud-a38b-prod' -ConfigDir 'X:\prod\config' -AppPath 'X:\dev' -PublishPath 'X:\prod' } | Should -Not -Throw
            $script:promoteEvents | Should -Contain 'other-applications probe'
            @($script:promoteEvents | Where-Object { $_ -like 'said: *may stop other IIS applications too*' }) | Should -Be @($expected)
            $script:promoteEvents | Should -Contain 'verified stop: aud-a38b-prod'

            # IIS unreadable, so the folder check cannot read it either: that check refuses, and nothing is stopped.
            $script:promoteEvents.Clear()
            $PSDefaultParameterValues = New-PoolProbe -Exists { $script:promoteEvents.Add('exists probe'); $true } -State { 'Stopped' } `
                -Serving { $script:promoteEvents.Add('applications probe'); throw 'Access is denied' } `
                -Others { $script:promoteEvents.Add('other-applications probe'); throw 'Access is denied' }
            { Stop-AppPoolChecked -Name 'aud-a38b-prod' -ConfigDir 'X:\prod\config' -AppPath 'X:\dev' -PublishPath 'X:\prod' } |
                Should -Throw -ExpectedMessage "Cannot read the IIS sites and applications, so it cannot be confirmed that app pool 'aud-a38b-prod' is what serves 'X:\prod': Access is denied"
            @($script:promoteEvents | Where-Object { $_ -like 'said: *may stop other IIS applications too*' }) | Should -Be @($expected)
            $script:promoteEvents | Should -Contain 'applications probe'
            $script:promoteEvents | Should -Not -Contain 'exists probe'
            $script:promoteEvents | Should -Not -Contain 'verified stop: aud-a38b-prod'
        }

        It 'in a dry run prints the plan line first and reads nothing from IIS' {
            Set-Variable -Name Apply -Value $false
            $PSDefaultParameterValues = New-PoolProbe -Exists { $true } -State { 'Stopped' }

            Stop-AppPoolChecked -Name 'aud-a38b-prod' -ConfigDir 'X:\prod\config' -AppPath 'X:\dev' -PublishPath 'X:\prod'

            $script:promoteEvents | Should -Not -Contain 'other-applications probe' -Because "this script's dry run reads nothing from IIS"
            @($script:promoteEvents | Where-Object { $_ -like 'said: *' }) | Should -BeNullOrEmpty
            $plans = @($script:promoteEvents | Where-Object { $_ -like 'plan: *' })
            $plans[0] | Should -BeExactly 'plan: Get-AppPoolOtherApplication -Name aud-a38b-prod -PhysicalPath X:\prod (name the IIS applications this pool serves besides prod, which stop with it, and read their active bulk jobs)'
            $plans[1] | Should -BeLike 'plan: Assert-AppPoolServesPath -Name aud-a38b-prod *'
        }

        It 'reports once, straight after the step line of the apply path, before the jobs, the folder check and the stop (AST)' {
            $stop = Get-PromoteFunction 'Stop-AppPoolChecked'
            $reports = @(Get-PromoteCommand 'Write-AppPoolSharingReport')
            $reports.Count | Should -Be 1 -Because 'the promotion reports once, in its one stop step'
            $reports[0].Extent.Text | Should -BeExactly 'Write-AppPoolSharingReport -Name $Name -PublishPath $PublishPath -AppPath $AppPath'
            $statements = @($stop.Body.EndBlock.Statements)
            $at = [array]::IndexOf($statements, $reports[0].Parent)
            $at | Should -BeGreaterThan 0 -Because 'the call is a statement of the stop step itself, not of a branch or a block'
            $statements[$at - 1].Extent.Text | Should -BeExactly 'Write-Step "Stopping prod app pool: $Name"'
            $after = @($statements | Select-Object -Skip ($at + 1) | ForEach-Object { $_.Extent.Text }) -join "`n"
            foreach ($later in 'Assert-NoActiveBulkJobsBeforeRecycle -ConfigDir $ConfigDir -AppPath $AppPath | Out-Null', '$null = Assert-AppPoolServesPath -Name $Name', '$poolState = Stop-AppPoolVerified -Name $Name') {
                $after | Should -Match ([regex]::Escape($later)) -Because 'the report comes before it'
            }
            $helper = @(Get-PromoteCommand 'Get-AppPoolOtherApplication')
            $helper.Count | Should -Be 1 -Because 'the report is the only caller, and it asks the shared module'
            $sharing = Get-PromoteFunction 'Write-AppPoolSharingReport'
            $helper[0].Extent.StartOffset | Should -BeGreaterThan $sharing.Extent.StartOffset
            $helper[0].Extent.EndOffset | Should -BeLessThan $sharing.Extent.EndOffset
        }
    }

    Context 'aud-a38c: the pool it stops is the pool that serves prod' {

        # Confirming that the NAMED pool stopped proves nothing when prod's folder is served by
        # a different one. The review of aud-a35 found this in deploy.ps1; the promotion had no
        # check at all.

        It 'refuses a prod folder served by another pool, before stopping anything' {
            # THE FINDING: a correctly spelled but wrong pool name was stopped, confirmed
            # stopped, and the mirror ran over prod still serving under its real pool.
            Set-Variable -Name Apply -Value $true
            $PSDefaultParameterValues = New-PoolProbe `
                -Exists { $script:promoteEvents.Add('exists probe'); $true } `
                -State { 'Stopped' } `
                -Serving {
                    $script:promoteEvents.Add('applications probe')
                    [pscustomobject]@{ Location = 'Web/prod'; ApplicationPool = 'aud-a38c-real-prod'; PhysicalPath = 'X:\prod' }
                }

            { Stop-AppPoolChecked -Name 'aud-a38b-prod' -PublishPath 'X:\prod' } |
                Should -Throw -ExpectedMessage "*'X:\prod'*Web/prod runs under 'aud-a38c-real-prod'*Stopping 'aud-a38b-prod' would leave that application running*"
            $script:promoteEvents | Should -Contain 'applications probe'
            $script:promoteEvents | Should -Not -Contain 'exists probe'
            $script:promoteEvents | Should -Not -Contain 'verified stop: aud-a38b-prod'
        }

        It 'asks IIS after the jobs warning and before the stop' {
            Set-Variable -Name Apply -Value $true
            $PSDefaultParameterValues = New-PoolProbe -Exists { $script:promoteEvents.Add('exists probe'); $true } -State { 'Stopped' }

            Stop-AppPoolChecked -Name 'aud-a38b-prod' -ConfigDir 'X:\prod\config' -AppPath 'X:\dev' -PublishPath 'X:\prod'

            $jobs = $script:promoteEvents.IndexOf('jobs: X:\prod\config X:\dev PlanOnly=False')
            $binding = $script:promoteEvents.IndexOf('applications probe')
            $exists = $script:promoteEvents.IndexOf('exists probe')
            $jobs | Should -BeGreaterOrEqual 0
            $binding | Should -BeGreaterThan $jobs
            $exists | Should -BeGreaterThan $binding
        }

        It 'in a dry run prints the check and reads nothing from IIS' {
            Set-Variable -Name Apply -Value $false
            $PSDefaultParameterValues = New-PoolProbe -Exists { $true } -State { 'Stopped' }

            Stop-AppPoolChecked -Name 'aud-a38b-prod' -ConfigDir 'X:\prod\config' -AppPath 'X:\dev' -PublishPath 'X:\prod'

            $script:promoteEvents | Should -Not -Contain 'applications probe'
            # aud-a38j: the plan line states the rule the promotion applies, nested nodes included.
            @($script:promoteEvents | Where-Object { $_ -like 'plan: Assert-AppPoolServesPath *' }) |
                Should -Be @('plan: Assert-AppPoolServesPath -Name aud-a38b-prod -PhysicalPath X:\prod -NestedNodes Refuse (refuse unless IIS serves that folder from this pool and no other, and nothing else is rooted inside it: the mirror replaces the whole folder)')
        }

        It 'aud-a38j: refuses a prod folder with a node rooted inside it under the same pool, before stopping anything' {
            # The promotion mirrors over the prod folder, so it states -NestedNodes Refuse: the nested
            # node is refused although it runs under the prod pool itself.
            Set-Variable -Name Apply -Value $true
            $PSDefaultParameterValues = New-PoolProbe `
                -Exists { $script:promoteEvents.Add('exists probe'); $true } `
                -State { 'Stopped' } `
                -Serving {
                    $script:promoteEvents.Add('applications probe')
                    [pscustomobject]@{ Location = 'Web/prod'; ApplicationPool = 'aud-a38b-prod'; PhysicalPath = 'X:\prod' }
                    [pscustomobject]@{ Location = 'Web/prod/reports'; ApplicationPool = 'aud-a38b-prod'; PhysicalPath = 'X:\prod\reports' }
                }

            { Stop-AppPoolChecked -Name 'aud-a38b-prod' -PublishPath 'X:\prod' } | Should -Throw -ExpectedMessage '*Web/prod/reports*'
            $script:promoteEvents | Should -Contain 'applications probe'
            $script:promoteEvents | Should -Not -Contain 'exists probe'
            $script:promoteEvents | Should -Not -Contain 'verified stop: aud-a38b-prod'
        }

        It 'aud-a38k: refuses a prod folder nothing in IIS serves in the mirror''s terms, before stopping anything' {
            # The promotion mirrors over the prod folder next, so it states -NestedNodes Refuse and its
            # refusals still say what the mirror would do; only the cutover's wording changed.
            Set-Variable -Name Apply -Value $true
            $PSDefaultParameterValues = New-PoolProbe `
                -Exists { $script:promoteEvents.Add('exists probe'); $true } `
                -State { 'Stopped' } `
                -Serving {
                    $script:promoteEvents.Add('applications probe')
                    [pscustomobject]@{ Location = 'Web/'; ApplicationPool = 'DefaultAppPool'; PhysicalPath = 'X:\inetpub' }
                }

            { Stop-AppPoolChecked -Name 'aud-a38b-prod' -PublishPath 'X:\prod' } |
                Should -Throw -ExpectedMessage "*No IIS site or application has its files at 'X:\prod'*Refusing to replace files that may still be served."
            $script:promoteEvents | Should -Contain 'applications probe'
            $script:promoteEvents | Should -Not -Contain 'exists probe'
            $script:promoteEvents | Should -Not -Contain 'verified stop: aud-a38b-prod'
        }

        It 'aud-a38k: refuses a prod folder another pool serves in the mirror''s terms, before stopping anything' {
            Set-Variable -Name Apply -Value $true
            $PSDefaultParameterValues = New-PoolProbe `
                -Exists { $script:promoteEvents.Add('exists probe'); $true } `
                -State { 'Stopped' } `
                -Serving {
                    $script:promoteEvents.Add('applications probe')
                    [pscustomobject]@{ Location = 'Web/prod'; ApplicationPool = 'aud-a38c-real-prod'; PhysicalPath = 'X:\prod' }
                }

            { Stop-AppPoolChecked -Name 'aud-a38b-prod' -PublishPath 'X:\prod' } |
                Should -Throw -ExpectedMessage "*Stopping 'aud-a38b-prod' would leave that application running while its files are replaced.*"
            $script:promoteEvents | Should -Contain 'applications probe'
            $script:promoteEvents | Should -Not -Contain 'exists probe'
            $script:promoteEvents | Should -Not -Contain 'verified stop: aud-a38b-prod'
        }

        It 'checks the folder the mirror replaces, inside the stop step, before the stop' {
            $checks = @(Get-PromoteCommand 'Assert-AppPoolServesPath')
            $checks.Count | Should -Be 1
            $checks[0].Extent.Text | Should -Be 'Assert-AppPoolServesPath -Name $Name -PhysicalPath $PublishPath -NestedNodes Refuse'

            $stopStep = Get-PromoteFunction 'Stop-AppPoolChecked'
            $verified = @(Get-PromoteCommand 'Stop-AppPoolVerified')[0]
            $checks[0].Extent.StartOffset | Should -BeGreaterThan $stopStep.Extent.StartOffset
            $checks[0].Extent.EndOffset | Should -BeLessThan $verified.Extent.StartOffset

            # ... and the folder it is handed is the one the mirror writes into.
            @(Get-PromoteCommand 'Stop-AppPoolChecked')[0].Extent.Text | Should -Match '-PublishPath \$prod$'
        }
    }

    Context 'the call site' {

        It 'imports AppPoolControl from beside the script' {
            $script:PromoteText | Should -Match '(?m)^Import-Module \(Join-Path \$PSScriptRoot ''AppPoolControl\.psm1''\) -Force'
        }

        It 'calls Stop-WebAppPool nowhere in the promotion' {
            @(Get-PromoteCommand 'Stop-WebAppPool').Count | Should -Be 0
        }

        It 'stops the pool through Stop-AppPoolVerified, once, inside the stop step' {
            $verified = @(Get-PromoteCommand 'Stop-AppPoolVerified')
            $verified.Count | Should -Be 1
            $verified[0].Extent.Text | Should -Be 'Stop-AppPoolVerified -Name $Name'

            $stopStep = Get-PromoteFunction 'Stop-AppPoolChecked'
            $verified[0].Extent.StartOffset | Should -BeGreaterThan $stopStep.Extent.StartOffset
            $verified[0].Extent.EndOffset | Should -BeLessThan $stopStep.Extent.EndOffset
        }

        It 'stops the prod pool at the top level, before any robocopy writes into prod' {
            $stops = @(Get-PromoteCommand 'Stop-AppPoolChecked')
            $stops.Count | Should -Be 1
            $stop = $stops[0]
            $stop.Extent.Text | Should -Match '^Stop-AppPoolChecked -Name \$ProdAppPoolName '

            # A direct statement of the script body: nothing - no try, no if - stands between its
            # throw and the end of the script, so a refusal cannot fall through to the mirror.
            [object]::ReferenceEquals($stop.Parent.Parent, $script:PromoteAst.EndBlock) | Should -BeTrue

            # Every robocopy whose DESTINATION is prod: the promotion mirror and the rollback.
            # The backup copy reads from prod and is meant to run while prod is still serving.
            $intoProd = @(
                foreach ($copy in Get-PromoteCommand 'robocopy') {
                    if ($copy.CommandElements.Count -ge 3 -and $copy.CommandElements[2].Extent.Text -eq '$prod') { $copy }
                }
                foreach ($copy in Get-PromoteCommand 'Invoke-RobocopyChecked') {
                    $arguments = $copy.Find({ param($node) $node -is [System.Management.Automation.Language.ArrayLiteralAst] }, $true)
                    if ($arguments -and $arguments.Elements.Count -ge 2 -and $arguments.Elements[1].Extent.Text -eq '$prod') { $copy }
                }
            )
            $intoProd.Count | Should -Be 2 -Because 'the promotion mirror and the rollback both write into prod'
            foreach ($copy in $intoProd) {
                $copy.Extent.StartOffset | Should -BeGreaterThan $stop.Extent.EndOffset `
                    -Because "line $($copy.Extent.StartLineNumber) writes into prod"
            }
        }
    }
}

Describe 'Assert-AppPoolServesPath' {

    # aud-a38c. Before a pool is stopped and a folder mirrored over, ask IIS which pools serve
    # that folder. Driven through -ApplicationsProbe; the default reads Get-Website,
    # Get-WebApplication and (aud-a38f) Get-WebConfiguration for virtual directories.

    BeforeAll {
        function Get-IisEntry {
            param([string]$Location, [string]$ApplicationPool, [string]$PhysicalPath)
            [pscustomobject]@{ Location = $Location; ApplicationPool = $ApplicationPool; PhysicalPath = $PhysicalPath }
        }
    }

    It 'passes, and returns what serves the folder, when the named pool alone serves it' {
        $result = @(Assert-AppPoolServesPath -Name 'ProdPool' -PhysicalPath 'D:\sites\App' -NestedNodes Refuse -ApplicationsProbe {
                Get-IisEntry -Location 'Web/' -ApplicationPool 'DefaultAppPool' -PhysicalPath 'D:\sites'
                Get-IisEntry -Location 'Web/App' -ApplicationPool 'ProdPool' -PhysicalPath 'D:\sites\App'
            })

        $result.Count | Should -Be 1
        $result[0].Location | Should -Be 'Web/App'
    }

    It 'refuses when the folder is served by a different pool, and says which and where' {
        # THE FINDING.
        {
            Assert-AppPoolServesPath -Name 'ProdPool' -PhysicalPath 'D:\sites\App' -NestedNodes Refuse -ApplicationsProbe {
                Get-IisEntry -Location 'Web/App' -ApplicationPool 'RealPool' -PhysicalPath 'D:\sites\App'
            }
        } | Should -Throw -ExpectedMessage "*'D:\sites\App'*Web/App runs under 'RealPool'*Stopping 'ProdPool' would leave that application running*"
    }

    It 'refuses when any one of several applications on the folder runs under another pool' {
        {
            Assert-AppPoolServesPath -Name 'ProdPool' -PhysicalPath 'D:\sites\App' -NestedNodes Refuse -ApplicationsProbe {
                Get-IisEntry -Location 'Web/App' -ApplicationPool 'ProdPool' -PhysicalPath 'D:\sites\App'
                Get-IisEntry -Location 'Other/App' -ApplicationPool 'OtherPool' -PhysicalPath 'D:\sites\App'
            }
        } | Should -Throw -ExpectedMessage "*Other/App runs under 'OtherPool'*"
    }

    It 'recognises the same folder however IIS spells it' {
        # Case, forward slashes, a trailing separator, a dot segment, an environment variable.
        $env:AUD_A38C_ROOT = 'D:\sites'
        try {
            $result = @(Assert-AppPoolServesPath -Name 'ProdPool' -PhysicalPath 'D:\sites\App' -NestedNodes Refuse -ApplicationsProbe {
                    Get-IisEntry -Location 'A/' -ApplicationPool 'ProdPool' -PhysicalPath 'd:/SITES/app/'
                    Get-IisEntry -Location 'B/' -ApplicationPool 'ProdPool' -PhysicalPath '%AUD_A38C_ROOT%\App'
                    Get-IisEntry -Location 'C/' -ApplicationPool 'ProdPool' -PhysicalPath 'D:\sites\.\App\'
                })
        } finally {
            Remove-Item -Path Env:\AUD_A38C_ROOT -ErrorAction SilentlyContinue
        }

        $result.Count | Should -Be 3
    }

    It 'does not mistake a sibling folder that shares a prefix for the folder' {
        # ...\AppDev starts with ...\App. A prefix test reads the sibling's pool as serving this
        # folder - refusing a correct promotion here, accepting a wrong one in the next test.
        $result = @(Assert-AppPoolServesPath -Name 'ProdPool' -PhysicalPath 'D:\sites\App' -NestedNodes Refuse -ApplicationsProbe {
                Get-IisEntry -Location 'Web/AppDev' -ApplicationPool 'DevPool' -PhysicalPath 'D:\sites\AppDev'
                Get-IisEntry -Location 'Web/App' -ApplicationPool 'ProdPool' -PhysicalPath 'D:\sites\App'
            })

        $result.Count | Should -Be 1
        $result[0].Location | Should -Be 'Web/App'
    }

    It 'fails closed when nothing in IIS serves the folder, and lists what IIS reported' {
        # deploy.ps1's check lets an empty answer through. Here, if nothing can be shown to
        # serve the folder, stopping the named pool cannot be shown to stop what runs from it.
        {
            Assert-AppPoolServesPath -Name 'ProdPool' -PhysicalPath 'D:\sites\App' -NestedNodes Refuse -ApplicationsProbe {
                Get-IisEntry -Location 'Web/AppDev' -ApplicationPool 'ProdPool' -PhysicalPath 'D:\sites\AppDev'
            }
        } | Should -Throw -ExpectedMessage "*No IIS site or application has its files at 'D:\sites\App'*IIS reported 1: Web/AppDev at 'D:\sites\AppDev' under 'ProdPool'*"
    }

    It 'fails closed when IIS reports nothing at all' {
        { Assert-AppPoolServesPath -Name 'ProdPool' -PhysicalPath 'D:\sites\App' -NestedNodes Refuse -ApplicationsProbe { } } |
            Should -Throw -ExpectedMessage '*IIS reported no sites or applications at all*'
    }

    It 'fails closed when IIS cannot be read, and says why' {
        { Assert-AppPoolServesPath -Name 'ProdPool' -PhysicalPath 'D:\sites\App' -NestedNodes Refuse -ApplicationsProbe { throw 'Cannot read configuration file' } } |
            Should -Throw -ExpectedMessage "*Cannot read the IIS sites and applications*'ProdPool'*Cannot read configuration file*"
    }

    It 'compares pool names without regard to case, as deploy.ps1''s check does' {
        $result = @(Assert-AppPoolServesPath -Name 'ProdPool' -PhysicalPath 'D:\sites\App' -NestedNodes Refuse -ApplicationsProbe {
                Get-IisEntry -Location 'Web/App' -ApplicationPool 'PRODPOOL' -PhysicalPath 'D:\sites\App'
            })

        $result.Count | Should -Be 1
    }

    Context 'aud-a38f: the mirror replaces the whole tree, so everything IIS serves inside it counts' {

        # robocopy /MIR replaces everything under the folder, not only the folder's own files. An
        # application or virtual directory rooted INSIDE it and served by another pool keeps running
        # while its files are replaced. Inside means at the folder or below a separator after it:
        # ...\AppDev shares the prefix and is still a sibling.

        It 'refuses when an application nested inside the folder runs under another pool, and names it' {
            # THE FINDING.
            {
                Assert-AppPoolServesPath -Name 'ProdPool' -PhysicalPath 'D:\sites\App' -NestedNodes Refuse -ApplicationsProbe {
                    Get-IisEntry -Location 'Web/App' -ApplicationPool 'ProdPool' -PhysicalPath 'D:\sites\App'
                    Get-IisEntry -Location 'Web/App/Admin' -ApplicationPool 'OtherPool' -PhysicalPath 'D:\sites\App\Admin'
                }
            } | Should -Throw -ExpectedMessage "*'D:\sites\App'*Web/App/Admin runs under 'OtherPool', with its files at 'D:\sites\App\Admin'*"
        }

        It 'does not count a folder that only shares the prefix as inside the folder' {
            $result = @(Assert-AppPoolServesPath -Name 'ProdPool' -PhysicalPath 'D:\sites\App' -NestedNodes Refuse -ApplicationsProbe {
                    Get-IisEntry -Location 'Web/App' -ApplicationPool 'ProdPool' -PhysicalPath 'D:\sites\App'
                    Get-IisEntry -Location 'Web/AppDev' -ApplicationPool 'DevPool' -PhysicalPath 'D:\sites\AppDev'
                    Get-IisEntry -Location 'Web/AppDev/Admin' -ApplicationPool 'DevPool' -PhysicalPath 'D:\sites\AppDev\Admin'
                    Get-IisEntry -Location 'Old/' -ApplicationPool 'OldPool' -PhysicalPath 'D:\sites\App.old'
                })

            ($result | ForEach-Object Location) -join ',' | Should -Be 'Web/App'
        }

        It 'counts a virtual directory inside the folder, under the pool of whatever owns it' {
            {
                Assert-AppPoolServesPath -Name 'ProdPool' -PhysicalPath 'D:\sites\App' -NestedNodes Refuse -ApplicationsProbe {
                    Get-IisEntry -Location 'Web/App' -ApplicationPool 'ProdPool' -PhysicalPath 'D:\sites\App'
                    Get-IisEntry -Location 'Web/media' -ApplicationPool 'SitePool' -PhysicalPath 'D:\sites\App\wwwroot\media'
                }
            } | Should -Throw -ExpectedMessage "*Web/media runs under 'SitePool', with its files at 'D:\sites\App\wwwroot\media'*"
        }

        It 'still refuses when something inside the folder is served but nothing at the folder itself' {
            # A parent folder named by mistake: /MIR would replace everything under it, the siblings
            # of the application included, so a nested match is not "what serves the folder".
            {
                Assert-AppPoolServesPath -Name 'ProdPool' -PhysicalPath 'D:\sites' -NestedNodes Refuse -ApplicationsProbe {
                    Get-IisEntry -Location 'Web/App' -ApplicationPool 'ProdPool' -PhysicalPath 'D:\sites\App'
                }
            } | Should -Throw -ExpectedMessage "*No IIS site or application has its files at 'D:\sites'*"
        }
    }

    Context 'aud-a38i: nothing may be rooted inside the folder, whatever pool serves it' {

        # Under aud-a38f a node nested inside the folder passed when it ran under the named pool.
        # Stopping that pool stops the node too, which is why it looked safe, but robocopy /MIR then
        # replaces the node's files with the publish output, or purges them because the output does
        # not have them - and the run reports success. The same-pool-at-the-folder pass is the first
        # test of this Describe, unchanged; siblings sharing the prefix are not inside (aud-a38f).

        It 'refuses an application nested inside the folder under the same pool, and says the mirror would replace or purge it' {
            # THE FINDING. This passed under aud-a38f.
            {
                Assert-AppPoolServesPath -Name 'ProdPool' -PhysicalPath 'D:\sites\App' -NestedNodes Refuse -ApplicationsProbe {
                    Get-IisEntry -Location 'Web/' -ApplicationPool 'DefaultAppPool' -PhysicalPath 'D:\sites'
                    Get-IisEntry -Location 'Web/App' -ApplicationPool 'ProdPool' -PhysicalPath 'D:\sites\App'
                    Get-IisEntry -Location 'Web/App/Admin' -ApplicationPool 'ProdPool' -PhysicalPath 'd:/SITES/app/admin/'
                }
            } | Should -Throw -ExpectedMessage "*inside 'D:\sites\App'*Web/App/Admin runs under 'ProdPool', with its files at 'd:/SITES/app/admin/'*replace or purge*"
        }

        It 'refuses a virtual directory nested inside the folder under the same pool' {
            {
                Assert-AppPoolServesPath -Name 'ProdPool' -PhysicalPath 'D:\sites\App' -NestedNodes Refuse -ApplicationsProbe {
                    Get-IisEntry -Location 'Web/App' -ApplicationPool 'ProdPool' -PhysicalPath 'D:\sites\App'
                    Get-IisEntry -Location 'Web/App/assets' -ApplicationPool 'ProdPool' -PhysicalPath 'D:\sites\App\wwwroot\assets'
                }
            } | Should -Throw -ExpectedMessage "*Web/App/assets runs under 'ProdPool', with its files at 'D:\sites\App\wwwroot\assets'*replace or purge*"
        }

        It 'names every node inside the folder in one refusal, whatever pool each runs under' {
            # One refusal for all of them: told only about the node under another pool, an operator
            # would move it to the named pool and be refused again for the same folder.
            {
                Assert-AppPoolServesPath -Name 'ProdPool' -PhysicalPath 'D:\sites\App' -NestedNodes Refuse -ApplicationsProbe {
                    Get-IisEntry -Location 'Web/App' -ApplicationPool 'ProdPool' -PhysicalPath 'D:\sites\App'
                    Get-IisEntry -Location 'Web/App/Admin' -ApplicationPool 'ProdPool' -PhysicalPath 'D:\sites\App\Admin'
                    Get-IisEntry -Location 'Web/media' -ApplicationPool 'SitePool' -PhysicalPath 'D:\sites\App\wwwroot\media'
                }
            } | Should -Throw -ExpectedMessage "*Web/App/Admin runs under 'ProdPool', with its files at 'D:\sites\App\Admin'; Web/media runs under 'SitePool', with its files at 'D:\sites\App\wwwroot\media'*"
        }
    }

    Context 'aud-a38j: every caller states the nested-node rule, and Allow is the aud-a38f rule' {

        # The aud-a38i refusal is right for a caller that mirrors the folder and wrong for one that
        # never does (the cutover). -NestedNodes has no default, so each caller says which it is.

        It 'fails binding when a caller does not say, and never reads IIS' {
            # In a runspace with no host a missing mandatory parameter is an error; in this one it
            # could be a prompt. The probe counts whether the function body ever ran.
            $module = (Resolve-Path $script:ModulePath).Path
            $ps = [powershell]::Create()
            try {
                $null = $ps.AddScript(@"
Import-Module '$module' -Force
`$global:probeAsked = 0
Assert-AppPoolServesPath -Name 'ProdPool' -PhysicalPath 'D:\sites\App' -ApplicationsProbe { `$global:probeAsked++; [pscustomobject]@{ Location = 'Web/App'; ApplicationPool = 'ProdPool'; PhysicalPath = 'D:\sites\App' } }
`$global:probeAsked
"@)
                $output = @($ps.Invoke())
                $errors = @($ps.Streams.Error)
            } finally {
                $ps.Dispose()
            }

            @($errors | Where-Object { $_.FullyQualifiedErrorId -like 'MissingMandatoryParameter,*' }).Count | Should -Be 1
            $output[-1] | Should -Be 0 -Because 'the function never ran, so IIS was never asked'
        }

        It 'declares -NestedNodes mandatory, limited to Refuse and Allow, with no default' {
            $command = Get-Command Assert-AppPoolServesPath -Module AppPoolControl
            $parameter = $command.Parameters['NestedNodes']
            @($parameter.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory }).Count | Should -Be 1
            $sets = @($parameter.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] })
            $sets.Count | Should -Be 1
            @($sets[0].ValidValues) | Should -Be @('Refuse', 'Allow')
            $declared = @($command.ScriptBlock.Ast.FindAll({
                        param($node)
                        $node -is [System.Management.Automation.Language.ParameterAst] -and $node.Name.VariablePath.UserPath -eq 'NestedNodes'
                    }, $true))
            $declared.Count | Should -Be 1
            $declared[0].DefaultValue | Should -BeNullOrEmpty -Because 'a default would choose the rule for a caller that did not'
        }

        It 'with Allow, passes nodes nested inside the folder under the named pool, and returns them with the folder''s own' {
            $result = @(Assert-AppPoolServesPath -Name 'ProdPool' -PhysicalPath 'D:\sites\App' -NestedNodes Allow -ApplicationsProbe {
                    Get-IisEntry -Location 'Web/' -ApplicationPool 'DefaultAppPool' -PhysicalPath 'D:\sites'
                    Get-IisEntry -Location 'Web/App' -ApplicationPool 'ProdPool' -PhysicalPath 'D:\sites\App'
                    Get-IisEntry -Location 'Web/App/Admin' -ApplicationPool 'ProdPool' -PhysicalPath 'd:/SITES/app/admin/'
                    Get-IisEntry -Location 'Web/App/assets' -ApplicationPool 'ProdPool' -PhysicalPath 'D:\sites\App\wwwroot\assets'
                    Get-IisEntry -Location 'Web/AppDev' -ApplicationPool 'DevPool' -PhysicalPath 'D:\sites\AppDev'
                })

            ($result | ForEach-Object Location) -join ',' | Should -Be 'Web/App,Web/App/Admin,Web/App/assets'
        }

        It 'with Allow, still refuses a node nested inside the folder under another pool, and blames only it' {
            $err = $null
            try {
                $null = Assert-AppPoolServesPath -Name 'ProdPool' -PhysicalPath 'D:\sites\App' -NestedNodes Allow -ApplicationsProbe {
                    Get-IisEntry -Location 'Web/App' -ApplicationPool 'ProdPool' -PhysicalPath 'D:\sites\App'
                    Get-IisEntry -Location 'Web/App/Admin' -ApplicationPool 'ProdPool' -PhysicalPath 'D:\sites\App\Admin'
                    Get-IisEntry -Location 'Web/media' -ApplicationPool 'SitePool' -PhysicalPath 'D:\sites\App\wwwroot\media'
                }
            } catch { $err = "$_" }

            $err | Should -Not -BeNullOrEmpty -Because 'stopping the named pool would leave the other pool serving from inside the folder'
            $err | Should -BeLike '*Web/media*'
            $err | Should -Not -BeLike '*Web/App/Admin*' -Because 'a nested node under the named pool is allowed'
        }

        It 'with Allow, still refuses a folder another pool serves' {
            { Assert-AppPoolServesPath -Name 'ProdPool' -PhysicalPath 'D:\sites\App' -NestedNodes Allow -ApplicationsProbe {
                    Get-IisEntry -Location 'Web/App' -ApplicationPool 'RealPool' -PhysicalPath 'D:\sites\App'
                } } | Should -Throw -ExpectedMessage '*Web/App*'
        }

        It 'with Allow, still refuses when nothing is at the folder itself' {
            { Assert-AppPoolServesPath -Name 'ProdPool' -PhysicalPath 'D:\sites' -NestedNodes Allow -ApplicationsProbe {
                    Get-IisEntry -Location 'Web/App' -ApplicationPool 'ProdPool' -PhysicalPath 'D:\sites\App'
                } } | Should -Throw -ExpectedMessage "*'D:\sites'*"
        }

        It 'with Allow, still refuses when IIS cannot be read' {
            { Assert-AppPoolServesPath -Name 'ProdPool' -PhysicalPath 'D:\sites\App' -NestedNodes Allow -ApplicationsProbe { throw 'Cannot read configuration file' } } |
                Should -Throw -ExpectedMessage '*Cannot read configuration file*'
        }
    }

    Context 'aud-a38k: each refusal ends in the terms of the rule the caller named' {

        # The rule a caller states (aud-a38j) also decides how a refusal ends: one that mirrors is told
        # what the mirror would do to files still being served; one that replaces nothing (the cutover)
        # is told that nothing will be stopped. Exact text, so a clause that drifts back is seen.

        BeforeAll {
            function Get-Refusal {
                param([string]$NestedNodes, [scriptblock]$Probe)
                try {
                    $null = Assert-AppPoolServesPath -Name 'ProdPool' -PhysicalPath 'D:\sites\App' -NestedNodes $NestedNodes -ApplicationsProbe $Probe
                } catch {
                    return $_.Exception.Message
                }
                throw "Assert-AppPoolServesPath -NestedNodes $NestedNodes did not refuse"
            }

            $script:NothingAtTheFolder = { Get-IisEntry -Location 'Web/AppDev' -ApplicationPool 'ProdPool' -PhysicalPath 'D:\sites\AppDev' }
            $script:AnotherPoolAtTheFolder = { Get-IisEntry -Location 'Web/App' -ApplicationPool 'RealPool' -PhysicalPath 'D:\sites\App' }
        }

        It 'with Refuse, refuses a folder nothing serves in the mirror''s terms, as before' {
            Get-Refusal Refuse $script:NothingAtTheFolder |
                Should -BeExactly "No IIS site or application has its files at 'D:\sites\App', so it cannot be confirmed that stopping app pool 'ProdPool' stops what runs from them. IIS reported 1: Web/AppDev at 'D:\sites\AppDev' under 'ProdPool'. Refusing to replace files that may still be served."
        }

        It 'with Allow, refuses a folder nothing serves without a word about replacing files, and says nothing will be stopped' {
            Get-Refusal Allow $script:NothingAtTheFolder |
                Should -BeExactly "No IIS site or application has its files at 'D:\sites\App', so it cannot be confirmed that stopping app pool 'ProdPool' stops what runs from them. IIS reported 1: Web/AppDev at 'D:\sites\AppDev' under 'ProdPool'. IIS does not show 'ProdPool' serving 'D:\sites\App', so nothing will be stopped."
        }

        It 'with Refuse, refuses a folder another pool serves in the mirror''s terms, as before' {
            Get-Refusal Refuse $script:AnotherPoolAtTheFolder |
                Should -BeExactly "Files at 'D:\sites\App' are served by an app pool other than 'ProdPool': Web/App runs under 'RealPool', with its files at 'D:\sites\App'. Stopping 'ProdPool' would leave that application running while its files are replaced. Name the pool that serves them, or fix the binding."
        }

        It 'with Allow, refuses a folder another pool serves without a word about replacing files, and says nothing will be stopped' {
            Get-Refusal Allow $script:AnotherPoolAtTheFolder |
                Should -BeExactly "Files at or under 'D:\sites\App' are served by an app pool other than 'ProdPool': Web/App runs under 'RealPool', with its files at 'D:\sites\App'. Stopping 'ProdPool' would leave that application running, so nothing will be stopped. Name the pool that serves them, or fix the binding."
        }

        It 'refuses an IIS it cannot read in the same words under either rule' {
            $unreadable = { throw 'Cannot read configuration file' }
            $refuse = Get-Refusal Refuse $unreadable
            $refuse | Should -BeExactly "Cannot read the IIS sites and applications, so it cannot be confirmed that app pool 'ProdPool' is what serves 'D:\sites\App': Cannot read configuration file"
            Get-Refusal Allow $unreadable | Should -BeExactly $refuse
        }
    }

    Context 'aud-a38f: the default probe reads virtual directories too' {

        # The default reads Get-Website and Get-WebApplication as before and, for every application
        # (a site's root one included), Get-WebConfiguration under the application's ItemXPath plus
        # /virtualDirectory - the form WebAdministration's own type data uses for an application's
        # PhysicalPath. These stand-ins answer the way that type data describes: they pin what the
        # probe does with the answer, not that real IIS gives it.

        BeforeAll {
            # Defined in the module's own scope, where the default probe resolves commands first,
            # and removed again after this block.
            InModuleScope AppPoolControl {
                function script:Get-Website {
                    [pscustomobject]@{ name = 'Web'; applicationPool = 'SitePool'; physicalPath = 'D:\sites'; ItemXPath = "/system.applicationHost/sites/site[@name='Web' and @id='1']" }
                }
                function script:Get-WebApplication {
                    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '',
                        Justification = 'The probe passes -Site; the stand-in knows one site')]
                    param([string]$Site)
                    [pscustomobject]@{ path = '/App'; applicationPool = 'ProdPool'; PhysicalPath = 'D:\sites\App'; ItemXPath = "/system.applicationHost/sites/site[@name='Web' and @id='1']/application[@path='/App']" }
                }
                function script:Get-WebConfiguration {
                    param([string]$PSPath, [string]$Filter)
                    if ($PSPath -ne 'MACHINE/WEBROOT/APPHOST') { throw "unexpected -PSPath '$PSPath'" }
                    # A filter that matches nothing writes nothing, as the cmdlet does - not a $null.
                    if ($script:AudA38fDirectories.ContainsKey($Filter)) { $script:AudA38fDirectories[$Filter] }
                }
            }

            function Initialize-IisDirectory {
                param([hashtable]$Map)
                InModuleScope AppPoolControl -Parameters @{ Map = $Map } {
                    param($Map)
                    $script:AudA38fDirectories = $Map
                }
            }

            $script:RootDirectories = "/system.applicationHost/sites/site[@name='Web' and @id='1']/application[@path='/']/virtualDirectory"
            $script:AppDirectories = "/system.applicationHost/sites/site[@name='Web' and @id='1']/application[@path='/App']/virtualDirectory"
        }

        AfterAll {
            InModuleScope AppPoolControl {
                Remove-Item -Path 'function:Get-Website', 'function:Get-WebApplication', 'function:Get-WebConfiguration' -ErrorAction SilentlyContinue
                Remove-Variable -Name AudA38fDirectories -Scope Script -ErrorAction SilentlyContinue
            }
        }

        It 'finds a virtual directory of the site root pointing inside the folder, and refuses its pool' {
            Initialize-IisDirectory @{
                $script:RootDirectories = @([pscustomobject]@{ path = '/'; physicalPath = 'D:\sites' }, [pscustomobject]@{ path = '/media'; physicalPath = 'D:\sites\App\media' })
                $script:AppDirectories  = @([pscustomobject]@{ path = '/'; physicalPath = 'D:\sites\App' })
            }

            { Assert-AppPoolServesPath -Name 'ProdPool' -PhysicalPath 'D:\sites\App' -NestedNodes Refuse } |
                Should -Throw -ExpectedMessage "*Web/media runs under 'SitePool', with its files at 'D:\sites\App\media'*"
        }

        It 'reads an application''s own virtual directories under that application''s pool' {
            # The virtual directory points AT the folder, so it passes only when it is held to the
            # application's pool and not the site's. Until aud-a38i it pointed inside the folder;
            # that now refuses whatever the pool (the next test).
            Initialize-IisDirectory @{
                $script:RootDirectories = @([pscustomobject]@{ path = '/'; physicalPath = 'D:\sites' })
                $script:AppDirectories  = @([pscustomobject]@{ path = '/'; physicalPath = 'D:\sites\App' }, [pscustomobject]@{ path = '/assets'; physicalPath = 'D:\sites\App' })
            }

            $result = @(Assert-AppPoolServesPath -Name 'ProdPool' -PhysicalPath 'D:\sites\App' -NestedNodes Refuse)
            ($result | ForEach-Object Location) -join ',' | Should -Be 'Web/App,Web/App/assets'
        }

        It 'aud-a38i: refuses an application''s own virtual directory inside the folder, though it runs under the named pool' {
            Initialize-IisDirectory @{
                $script:RootDirectories = @([pscustomobject]@{ path = '/'; physicalPath = 'D:\sites' })
                $script:AppDirectories  = @([pscustomobject]@{ path = '/'; physicalPath = 'D:\sites\App' }, [pscustomobject]@{ path = '/assets'; physicalPath = 'D:\sites\App\assets' })
            }

            { Assert-AppPoolServesPath -Name 'ProdPool' -PhysicalPath 'D:\sites\App' -NestedNodes Refuse } |
                Should -Throw -ExpectedMessage "*Web/App/assets runs under 'ProdPool', with its files at 'D:\sites\App\assets'*replace or purge*"
        }

        It 'aud-a38j: with Allow, passes an application''s own virtual directory inside the folder, under that application''s pool' {
            Initialize-IisDirectory @{
                $script:RootDirectories = @([pscustomobject]@{ path = '/'; physicalPath = 'D:\sites' })
                $script:AppDirectories  = @([pscustomobject]@{ path = '/'; physicalPath = 'D:\sites\App' }, [pscustomobject]@{ path = '/assets'; physicalPath = 'D:\sites\App\assets' })
            }

            $result = @(Assert-AppPoolServesPath -Name 'ProdPool' -PhysicalPath 'D:\sites\App' -NestedNodes Allow)
            ($result | ForEach-Object Location) -join ',' | Should -Be 'Web/App,Web/App/assets'
        }

        It 'refuses when an application''s virtual directories cannot be shown to have been read' {
            # Every IIS application has a root virtual directory. A listing without one means the
            # read missed, and a missed read must not pass as "nothing inside".
            Initialize-IisDirectory @{
                $script:RootDirectories = @([pscustomobject]@{ path = '/'; physicalPath = 'D:\sites' })
            }

            { Assert-AppPoolServesPath -Name 'ProdPool' -PhysicalPath 'D:\sites\App' -NestedNodes Refuse } |
                Should -Throw -ExpectedMessage '*Cannot read the IIS sites and applications*no root virtual directory for Web/App*'
        }
    }
}

Describe 'aud-a38d: the promotion refuses to run where it cannot see the app pools' {

    # Under PowerShell 7, WebAdministration loads through the compatibility session without its
    # IIS: drive, and the module's Test-Path IIS:\AppPools\... probes then read every pool as
    # absent. deploy.ps1 refuses at the import; the promotion carried on.

    BeforeAll {
        $promotePath = (Resolve-Path (Join-Path $PSScriptRoot '..\..\tools\promote-dev-to-prod.ps1')).Path
        $tokens = $null
        $parseErrors = $null
        $script:DriveAst = [System.Management.Automation.Language.Parser]::ParseFile($promotePath, [ref]$tokens, [ref]$parseErrors)

        function Get-DriveCommand {
            param([string]$CommandName)
            @($script:DriveAst.FindAll({
                        param($node)
                        $node -is [System.Management.Automation.Language.CommandAst]
                    }, $true) | Where-Object { $_.GetCommandName() -eq $CommandName })
        }

        # The import, and the statement after it in the same block.
        $script:Imports = @(Get-DriveCommand 'Import-Module' | Where-Object {
                $_.CommandElements.Count -ge 2 -and $_.CommandElements[1].Extent.Text -eq 'WebAdministration'
            })
        $importStatement = $script:Imports[0].Parent
        $block = $importStatement.Parent
        $script:DriveGuard = $block.Statements[$block.Statements.IndexOf($importStatement) + 1]

        # The real Write-Fail, so the lifted guard throws the way the script does - and the real
        # Write-Warn, so a guard downgraded to a warning fails these tests by not throwing,
        # rather than by a missing command.
        $helpers = $script:DriveAst.FindAll({
                param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst]
            }, $true) | Where-Object Name -In 'Write-Fail', 'Write-Warn'
        foreach ($helper in $helpers) { . ([scriptblock]::Create($helper.Extent.Text)) }
    }

    It 'imports WebAdministration once, in the apply path' {
        $script:Imports.Count | Should -Be 1
        $script:Imports[0].Extent.Text | Should -Be 'Import-Module WebAdministration -ErrorAction Stop'
    }

    It 'checks for the IIS: drive straight after the import, and refuses through Write-Fail' {
        $script:DriveGuard | Should -BeOfType [System.Management.Automation.Language.IfStatementAst]
        $script:DriveGuard.Clauses[0].Item1.Extent.Text | Should -Be '-not (Get-PSDrive -Name IIS -ErrorAction SilentlyContinue)'
        $script:DriveGuard.Clauses[0].Item2.Statements[0].PipelineElements[0].GetCommandName() | Should -Be 'Write-Fail'
    }

    It 'refuses in a session with no IIS: drive - which this test session is' {
        # The shipped statement, lifted from the file and run as it stands.
        Get-PSDrive -Name IIS -ErrorAction SilentlyContinue | Should -BeNullOrEmpty -Because 'the precondition: no IIS: drive here'

        { . ([scriptblock]::Create($script:DriveGuard.Extent.Text)) } |
            Should -Throw -ExpectedMessage '*IIS: drive is unavailable*Windows PowerShell 5.1*Prod was not touched*'
    }

    It 'lets the promotion carry on once a drive of that name exists' {
        $null = New-PSDrive -Name IIS -PSProvider FileSystem -Root $TestDrive
        try {
            { . ([scriptblock]::Create($script:DriveGuard.Extent.Text)) } | Should -Not -Throw
        } finally {
            Remove-PSDrive -Name IIS -ErrorAction SilentlyContinue
        }
    }

    It 'refuses before the backup, the mirror and the stop' {
        $guardEnd = $script:DriveGuard.Extent.EndOffset
        foreach ($name in 'Invoke-RobocopyChecked', 'Backup-SqliteConfigDb', 'Stop-AppPoolChecked') {
            $calls = @(Get-DriveCommand $name)
            $calls.Count | Should -BeGreaterThan 0 -Because "$name is called"
            foreach ($call in $calls) {
                $call.Extent.StartOffset | Should -BeGreaterThan $guardEnd -Because "line $($call.Extent.StartLineNumber) ($name) must come after the drive check"
            }
        }
    }
}

Describe 'cdt-46: one folder however the filesystem spells it - an 8.3 short name, a junction' {

    # ConvertTo-ComparablePhysicalPath normalises a spelling; it cannot know that a short name and a
    # long one, or a junction and its target, are one folder. Test-SamePhysicalFolder and
    # Assert-AppPoolServesPath also compare the final path the filesystem resolves both to, when both
    # exist; a spelling that does not resolve is still not taken as the same folder. Real folders and
    # junctions under the temp folder; nothing in IIS.

    BeforeAll {
        $script:Cdt46Root = Join-Path ([System.IO.Path]::GetTempPath()) ('cdt46_' + [guid]::NewGuid().ToString('N'))
        $script:Cdt46Folder = Join-Path $script:Cdt46Root 'Publish Folder With A Long Name'
        $script:Cdt46Sibling = Join-Path $script:Cdt46Root 'Publish Folder With A Long Name Too'
        $script:Cdt46Outside = Join-Path $script:Cdt46Root 'Reports Kept Elsewhere'
        New-Item -ItemType Directory -Path $script:Cdt46Folder, $script:Cdt46Sibling, $script:Cdt46Outside, (Join-Path $script:Cdt46Folder 'reports') -Force | Out-Null
        # Junctions: one to the folder; one inside the folder that leads out of it; one elsewhere that
        # leads into the folder's reports.
        $script:Cdt46Link = Join-Path $script:Cdt46Root 'link'
        $script:Cdt46OutLink = Join-Path $script:Cdt46Folder 'outlink'
        $script:Cdt46InLink = Join-Path $script:Cdt46Root 'inlink'
        foreach ($pair in @(@($script:Cdt46Link, $script:Cdt46Folder), @($script:Cdt46OutLink, $script:Cdt46Outside), @($script:Cdt46InLink, (Join-Path $script:Cdt46Folder 'reports')))) {
            $null = cmd /c "mklink /J `"$($pair[0])`" `"$($pair[1])`""
            if (-not (Test-Path -LiteralPath $pair[0])) { throw "could not create the junction fixture $($pair[0])" }
        }
        # An 8.3 spelling of a folder that exists (cdt-50: found without a skip path), in order: the
        # fixture folder's own, when its volume generates short names; one set on it with fsutil, where
        # the volume allows short names but did not make one (that needs elevation; a refusal just falls
        # through); Program Files', which most Windows hosts have. The source is kept for the messages,
        # and so is why each one gave nothing, so that a host with none fails the test that needs one
        # and names the cause (cdt-56).
        $shortOf = { param([string]$Path) (cmd /c "for %I in (`"$Path`") do @echo %~sI").Trim() }
        $script:Cdt46ShortTried = New-Object System.Collections.Generic.List[string]
        $script:Cdt46Long = $script:Cdt46Folder
        $script:Cdt46Short = & $shortOf $script:Cdt46Folder
        $script:Cdt46ShortSource = 'the fixture folder''s own short name'
        if ($script:Cdt46Short -eq $script:Cdt46Long) {
            $script:Cdt46ShortTried.Add("the fixture folder '$($script:Cdt46Folder)' has no short name: its volume made none")
            $fsutil = @(cmd /c "fsutil file setshortname `"$($script:Cdt46Folder)`" CDT46L~1 2>&1")
            $fsutilExit = $LASTEXITCODE
            $script:Cdt46Short = & $shortOf $script:Cdt46Folder
            $script:Cdt46ShortSource = 'a short name set on the fixture folder with fsutil'
            if ($script:Cdt46Short -eq $script:Cdt46Long) {
                $script:Cdt46ShortTried.Add("fsutil file setshortname on it exited $fsutilExit and set none: $((($fsutil | ForEach-Object { "$_".Trim() }) -join ' ').Trim())")
            }
        }
        if ($script:Cdt46Short -eq $script:Cdt46Long) {
            $script:Cdt46Long = $env:ProgramFiles
            $script:Cdt46Short = & $shortOf $env:ProgramFiles
            $script:Cdt46ShortSource = 'Program Files'' short name'
            if ($script:Cdt46Short -eq $script:Cdt46Long) {
                $script:Cdt46ShortTried.Add("Program Files ('$($env:ProgramFiles)') has no short name")
            }
        }
        $script:Cdt46HasShortName = ($script:Cdt46Short -ne $script:Cdt46Long)
        if (-not $script:Cdt46HasShortName) { $script:Cdt46ShortSource = 'none: ' + ($script:Cdt46ShortTried -join '; ') }

        function Get-IisEntry {
            param([string]$Location, [string]$ApplicationPool, [string]$PhysicalPath)
            [pscustomobject]@{ Location = $Location; ApplicationPool = $ApplicationPool; PhysicalPath = $PhysicalPath }
        }
    }

    AfterAll {
        # The junctions first, on their own, so nothing follows them into their targets.
        foreach ($junction in @($script:Cdt46OutLink, $script:Cdt46InLink, $script:Cdt46Link)) {
            if (Test-Path -LiteralPath $junction) { [System.IO.Directory]::Delete($junction) }
        }
        Remove-Item -LiteralPath $script:Cdt46Root -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'takes an 8.3 short spelling for the folder it names, and never a short-looking spelling that is not on disk (cdt-50: no skip path)' {
        # On every host: [System.IO.Path]::GetFullPath does not check that a path exists, and expands a
        # short component only when it names one on disk. So a short-looking spelling that is not there
        # stays as written, and is not taken for a long folder - the comparison fails closed.
        $syntheticShort = 'Z:\CDT50X~1\APP'
        $syntheticLong = 'Z:\CDT50 Not Here\APP'
        (ConvertTo-ComparablePhysicalPath $syntheticShort) | Should -BeExactly $syntheticShort -Because 'a short component that names nothing on disk is not expanded'
        Test-SamePhysicalFolder -Path $syntheticShort -Other $syntheticLong | Should -BeFalse -Because 'a spelling that cannot be resolved on disk is not taken as the same folder'

        # A real short spelling - see BeforeAll for where it came from - is taken for its folder.
        # GetFullPath already expands the short name of a folder that exists, under .NET Framework and
        # .NET alike (probed for cdt-46), so this pins that the comparison keeps taking it, whichever half
        # of it does the work. A host that gives none fails here and names why: these lines used to sit
        # under an if on that, so such a host passed the suite on the synthetic case alone (cdt-56).
        $script:Cdt46HasShortName | Should -BeTrue -Because "a real 8.3 spelling is needed to show that one is taken for its folder, and this host gave none ($($script:Cdt46ShortSource))"
        Test-SamePhysicalFolder -Path $script:Cdt46Short -Other $script:Cdt46Long | Should -BeTrue -Because "'$($script:Cdt46Short)' ($($script:Cdt46ShortSource)) is '$($script:Cdt46Long)'"
        Test-SamePhysicalFolder -Path $script:Cdt46Long -Other $script:Cdt46Short | Should -BeTrue
        $served = @(Assert-AppPoolServesPath -Name 'ProdPool' -PhysicalPath $script:Cdt46Long -NestedNodes Refuse -ApplicationsProbe {
                Get-IisEntry -Location 'Web/App' -ApplicationPool 'ProdPool' -PhysicalPath $script:Cdt46Short
            })
        $served.Count | Should -Be 1 -Because "a node IIS spells with the short name ($($script:Cdt46ShortSource)) serves the folder"
    }

    It 'takes a junction for the folder it points at, but not a sibling, and not a spelling that does not exist' {
        Test-SamePhysicalFolder -Path $script:Cdt46Link -Other $script:Cdt46Folder | Should -BeTrue -Because 'a junction and its target are one folder'
        Test-SamePhysicalFolder -Path "$($script:Cdt46Link)\" -Other $script:Cdt46Folder.ToUpperInvariant() | Should -BeTrue -Because 'whatever the separators and the case'
        Test-SamePhysicalFolder -Path $script:Cdt46Link -Other $script:Cdt46Sibling | Should -BeFalse -Because 'a sibling whose name starts with the folder''s is another folder'
        Test-SamePhysicalFolder -Path (Join-Path $script:Cdt46Root 'missing') -Other $script:Cdt46Folder | Should -BeFalse -Because 'a spelling that does not resolve is not taken as the same folder'
        Test-SamePhysicalFolder -Path 'Z:\cdt46\missing\' -Other 'z:/CDT46/missing' | Should -BeTrue -Because 'two spellings that do not exist still compare lexically, as before'
    }

    It 'holds a node IIS spells through a junction to the named pool (Assert-AppPoolServesPath)' {
        $served = @(Assert-AppPoolServesPath -Name 'ProdPool' -PhysicalPath $script:Cdt46Folder -NestedNodes Refuse -ApplicationsProbe {
                Get-IisEntry -Location 'Web/App' -ApplicationPool 'ProdPool' -PhysicalPath $script:Cdt46Link
            })
        $served.Count | Should -Be 1 -Because 'the node at the junction serves the folder'
        {
            Assert-AppPoolServesPath -Name 'ProdPool' -PhysicalPath $script:Cdt46Folder -NestedNodes Refuse -ApplicationsProbe {
                Get-IisEntry -Location 'Web/App' -ApplicationPool 'ProdPool' -PhysicalPath $script:Cdt46Folder
                Get-IisEntry -Location 'Other/Copy' -ApplicationPool 'OtherPool' -PhysicalPath $script:Cdt46Link
            }
        } | Should -Throw -ExpectedMessage "*served by an app pool other than 'ProdPool': Other/Copy runs under 'OtherPool'*" -Because 'a node serving the folder''s files through a junction under another pool would keep serving them while they are replaced'
    }

    It 'keeps a node rooted inside the folder nested though its junction leads out, and counts one whose files reach in through a junction' {
        # The union of the two forms: inside by its lexical path stays inside; inside by its final path is inside too.
        {
            Assert-AppPoolServesPath -Name 'ProdPool' -PhysicalPath $script:Cdt46Folder -NestedNodes Refuse -ApplicationsProbe {
                Get-IisEntry -Location 'Web/App' -ApplicationPool 'ProdPool' -PhysicalPath $script:Cdt46Folder
                Get-IisEntry -Location 'Web/App/out' -ApplicationPool 'ProdPool' -PhysicalPath $script:Cdt46OutLink
            }
        } | Should -Throw -ExpectedMessage '*IIS serves content from inside*Web/App/out*' -Because 'the node is rooted inside the folder the mirror replaces, wherever its junction leads'
        {
            Assert-AppPoolServesPath -Name 'ProdPool' -PhysicalPath $script:Cdt46Folder -NestedNodes Refuse -ApplicationsProbe {
                Get-IisEntry -Location 'Web/App' -ApplicationPool 'ProdPool' -PhysicalPath $script:Cdt46Folder
                Get-IisEntry -Location 'Other/Reports' -ApplicationPool 'ProdPool' -PhysicalPath $script:Cdt46InLink
            }
        } | Should -Throw -ExpectedMessage '*IIS serves content from inside*Other/Reports*' -Because 'its files are the folder''s reports, which the mirror replaces'
    }
}

Describe 'cdt-54: Get-AppPoolOtherApplication lists what else a stop of the pool takes down' {

    # Stopping a pool stops every application it serves, and deploy.ps1's checks before its stop looked
    # only at the instance being deployed. Driven through -ApplicationsProbe, with one case against a
    # real folder and a junction to it, and the default probe through in-module stand-ins.

    BeforeAll {
        function Get-PoolNode {
            param([string]$Location, [string]$ApplicationPool, [string]$PhysicalPath)
            [pscustomobject]@{ Location = $Location; ApplicationPool = $ApplicationPool; PhysicalPath = $PhysicalPath }
        }
    }

    It 'lists the pool''s applications elsewhere, one with no folder among them, and nothing of another pool' {
        $others = @(Get-AppPoolOtherApplication -Name 'ExamplePool' -PhysicalPath 'D:\sites\App' -ApplicationsProbe {
                Get-PoolNode -Location 'Web/' -ApplicationPool 'SitePool' -PhysicalPath 'D:\sites'
                Get-PoolNode -Location 'Web/App' -ApplicationPool 'ExamplePool' -PhysicalPath 'D:\sites\App'
                Get-PoolNode -Location 'Web/Reports' -ApplicationPool 'examplepool' -PhysicalPath 'D:\sites\Reports'
                Get-PoolNode -Location 'Other/' -ApplicationPool 'ExamplePool' -PhysicalPath ''
                Get-PoolNode -Location 'Web/AppOther' -ApplicationPool 'ExamplePool' -PhysicalPath 'D:\sites\AppOther'
                Get-PoolNode -Location 'Web/Billing' -ApplicationPool 'BillingPool' -PhysicalPath 'D:\sites\Billing'
            })
        ($others | ForEach-Object Location) -join ',' | Should -BeExactly 'Web/Reports,Other/,Web/AppOther' -Because 'pool names match case-insensitively, a folder that only starts with the instance''s is elsewhere, and an application with no folder is listed rather than assumed to be the instance'
    }

    It 'leaves out the instance itself, however IIS spells its folder, and anything inside it' {
        $env:CDT54_ROOT = 'D:\sites'
        try {
            $others = @(Get-AppPoolOtherApplication -Name 'ExamplePool' -PhysicalPath 'D:\sites\App' -ApplicationsProbe {
                    Get-PoolNode -Location 'Web/App' -ApplicationPool 'ExamplePool' -PhysicalPath 'd:\SITES\app\'
                    Get-PoolNode -Location 'Web2/App' -ApplicationPool 'ExamplePool' -PhysicalPath '%CDT54_ROOT%\App'
                    Get-PoolNode -Location 'Web3/App' -ApplicationPool 'ExamplePool' -PhysicalPath 'D:/sites/App'
                    Get-PoolNode -Location 'Web/App/api' -ApplicationPool 'ExamplePool' -PhysicalPath 'D:\sites\App\api'
                })
        } finally {
            Remove-Item Env:\CDT54_ROOT -ErrorAction SilentlyContinue
        }
        $others.Count | Should -Be 0 -Because 'the folder''s own nodes are the instance being deployed, and a node inside it is refused before the stop'
    }

    It 'takes a junction to the instance''s folder for the instance, and lists a sibling (real files)' {
        $script:Cdt54Root = Join-Path ([System.IO.Path]::GetTempPath()) ('cdt54_' + [guid]::NewGuid().ToString('N'))
        $script:Cdt54Folder = Join-Path $script:Cdt54Root 'Publish Folder'
        $script:Cdt54Sibling = Join-Path $script:Cdt54Root 'Another Publish Folder'
        $script:Cdt54Link = Join-Path $script:Cdt54Root 'link'
        try {
            New-Item -ItemType Directory -Path $script:Cdt54Folder, $script:Cdt54Sibling -Force | Out-Null
            $null = cmd /c "mklink /J `"$script:Cdt54Link`" `"$script:Cdt54Folder`""
            Test-Path -LiteralPath $script:Cdt54Link | Should -BeTrue -Because 'the junction fixture exists'

            $others = @(Get-AppPoolOtherApplication -Name 'ExamplePool' -PhysicalPath $script:Cdt54Folder -ApplicationsProbe {
                    Get-PoolNode -Location 'Web/App' -ApplicationPool 'ExamplePool' -PhysicalPath $script:Cdt54Link
                    Get-PoolNode -Location 'Web/Copy' -ApplicationPool 'ExamplePool' -PhysicalPath $script:Cdt54Sibling
                })
            ($others | ForEach-Object Location) -join ',' | Should -BeExactly 'Web/Copy' -Because 'IIS at the junction is the instance''s folder; a sibling is another application'
        } finally {
            if (Test-Path -LiteralPath $script:Cdt54Link) { [System.IO.Directory]::Delete($script:Cdt54Link) }
            Remove-Item -LiteralPath $script:Cdt54Root -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'with -IncludeNested, lists the pool''s applications inside the folder and leaves out only the instance itself, however IIS spells it (cdt-60)' {
        # cdt-60. The cutover's serving check allows a node rooted inside the instance's folder under the same pool, and its
        # stop takes that node down; the rule above, which leaves out everything at or inside the folder, never named it.
        $env:CDT54_ROOT = 'D:\sites'
        try {
            $probe = {
                Get-PoolNode -Location 'Web/App' -ApplicationPool 'ExamplePool' -PhysicalPath 'd:\SITES\app\'
                Get-PoolNode -Location 'Web2/App' -ApplicationPool 'ExamplePool' -PhysicalPath '%CDT54_ROOT%\App'
                Get-PoolNode -Location 'Web3/App' -ApplicationPool 'ExamplePool' -PhysicalPath 'D:/sites/App'
                Get-PoolNode -Location 'Web/App/api' -ApplicationPool 'ExamplePool' -PhysicalPath 'D:\sites\App\api'
                Get-PoolNode -Location 'Web/App/Reports' -ApplicationPool 'examplepool' -PhysicalPath 'D:\sites\App\Reports\'
                Get-PoolNode -Location 'Web/App/Other' -ApplicationPool 'OtherPool' -PhysicalPath 'D:\sites\App\Other'
                Get-PoolNode -Location 'Web/AppOther' -ApplicationPool 'ExamplePool' -PhysicalPath 'D:\sites\AppOther'
            }
            $nested = @(Get-AppPoolOtherApplication -Name 'ExamplePool' -PhysicalPath 'D:\sites\App' -IncludeNested -ApplicationsProbe $probe)
            $default = @(Get-AppPoolOtherApplication -Name 'ExamplePool' -PhysicalPath 'D:\sites\App' -ApplicationsProbe $probe)
        } finally {
            Remove-Item Env:\CDT54_ROOT -ErrorAction SilentlyContinue
        }
        ($nested | ForEach-Object Location) -join ',' | Should -BeExactly 'Web/App/api,Web/App/Reports,Web/AppOther' -Because 'the nodes at the folder, however spelled, are the instance; the pool''s nodes inside it stop with it; another pool''s node does not'
        ($default | ForEach-Object Location) -join ',' | Should -BeExactly 'Web/AppOther' -Because 'without the switch the rule is unchanged for the callers that refuse a nested node'
    }

    It 'with -IncludeNested, takes a junction to the folder for the instance and lists a real folder inside it (cdt-60, real files)' {
        $script:Cdt60Root = Join-Path ([System.IO.Path]::GetTempPath()) ('cdt60_' + [guid]::NewGuid().ToString('N'))
        $script:Cdt60Folder = Join-Path $script:Cdt60Root 'Publish Folder'
        $script:Cdt60Inside = Join-Path $script:Cdt60Folder 'Reports'
        $script:Cdt60Link = Join-Path $script:Cdt60Root 'link'
        try {
            New-Item -ItemType Directory -Path $script:Cdt60Inside -Force | Out-Null
            $null = cmd /c "mklink /J `"$script:Cdt60Link`" `"$script:Cdt60Folder`""
            Test-Path -LiteralPath $script:Cdt60Link | Should -BeTrue -Because 'the junction fixture exists'

            $others = @(Get-AppPoolOtherApplication -Name 'ExamplePool' -PhysicalPath $script:Cdt60Folder -IncludeNested -ApplicationsProbe {
                    Get-PoolNode -Location 'Web/App' -ApplicationPool 'ExamplePool' -PhysicalPath $script:Cdt60Link
                    Get-PoolNode -Location 'Web/App/Reports' -ApplicationPool 'ExamplePool' -PhysicalPath $script:Cdt60Inside
                })
            ($others | ForEach-Object Location) -join ',' | Should -BeExactly 'Web/App/Reports' -Because 'IIS at the junction is the instance''s folder; the folder inside it is another application'
        } finally {
            if (Test-Path -LiteralPath $script:Cdt60Link) { [System.IO.Directory]::Delete($script:Cdt60Link) }
            Remove-Item -LiteralPath $script:Cdt60Root -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'throws, naming the pool and the folder, when IIS cannot be read' {
        { Get-AppPoolOtherApplication -Name 'ExamplePool' -PhysicalPath 'D:\sites\App' -ApplicationsProbe { throw 'Access is denied' } } |
            Should -Throw -ExpectedMessage "Cannot read the IIS sites and applications, so the applications app pool 'ExamplePool' serves besides the one at 'D:\sites\App' cannot be listed: Access is denied"
    }

    Context 'the default probe' {

        # Stand-ins answer as WebAdministration's cmdlets do, in the module's own scope, where the
        # default probe resolves commands first; removed again after this block.

        BeforeAll {
            InModuleScope AppPoolControl {
                function script:Get-Website {
                    [pscustomobject]@{ name = 'Web'; applicationPool = 'ExamplePool'; physicalPath = 'D:\sites' }
                    [pscustomobject]@{ name = 'Other'; applicationPool = 'OtherPool'; physicalPath = 'D:\other' }
                }
                function script:Get-WebApplication {
                    param([string]$Site)
                    if ($Site -eq 'Web') {
                        [pscustomobject]@{ path = '/App'; applicationPool = 'ExamplePool'; PhysicalPath = 'D:\apps\App' }
                        [pscustomobject]@{ path = '/Reports'; applicationPool = 'ExamplePool'; PhysicalPath = 'D:\apps\Reports' }
                    }
                    if ($Site -eq 'Other') {
                        [pscustomobject]@{ path = '/Tools'; applicationPool = 'ExamplePool'; PhysicalPath = 'D:\other\Tools' }
                    }
                }
                function script:Get-WebConfiguration { throw 'the default probe read virtual directories' }
            }
        }

        AfterAll {
            InModuleScope AppPoolControl {
                Remove-Item -Path 'function:Get-Website', 'function:Get-WebApplication', 'function:Get-WebConfiguration' -ErrorAction SilentlyContinue
            }
        }

        It 'reads every site root and application, and no virtual directory' {
            $others = @(Get-AppPoolOtherApplication -Name 'ExamplePool' -PhysicalPath 'D:\apps\App')
            ($others | ForEach-Object { "$($_.Location)|$($_.PhysicalPath)" }) -join ',' |
                Should -BeExactly 'Web/|D:\sites,Web/Reports|D:\apps\Reports,Other/Tools|D:\other\Tools' -Because 'a site root is an application too, an application of another site under the pool is listed, and the instance and the other pool''s site are not'
        }
    }
}
