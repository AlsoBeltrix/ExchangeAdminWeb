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
        foreach ($name in 'Write-Step', 'Write-Ok', 'Write-Warn', 'Write-Fail', 'Stop-AppPoolChecked') {
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
                Justification = 'Exists, State and Serving are used inside GetNewClosure blocks; the analyzer cannot see into them')]
            param(
                [scriptblock]$Exists,
                [scriptblock]$State,
                [int]$TimeoutSeconds = 60,
                # aud-a38c: what IIS reports serving the prod folder. By default, the prod pool alone.
                [scriptblock]$Serving = {
                    $script:promoteEvents.Add('applications probe')
                    [pscustomobject]@{ Location = 'Web/prod'; ApplicationPool = 'aud-a38b-prod'; PhysicalPath = 'X:\prod' }
                }
            )
            @{
                'Assert-AppPoolServesPath:ApplicationsProbe' = { $Serving }.GetNewClosure()
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
            @($script:promoteEvents | Where-Object { $_ -like 'plan: Assert-AppPoolServesPath -Name aud-a38b-prod -PhysicalPath X:\prod *' }).Count | Should -Be 1
        }

        It 'checks the folder the mirror replaces, inside the stop step, before the stop' {
            $checks = @(Get-PromoteCommand 'Assert-AppPoolServesPath')
            $checks.Count | Should -Be 1
            $checks[0].Extent.Text | Should -Be 'Assert-AppPoolServesPath -Name $Name -PhysicalPath $PublishPath'

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
    # that folder. Driven through -ApplicationsProbe; the default reads Get-Website and
    # Get-WebApplication.

    BeforeAll {
        function Get-IisEntry {
            param([string]$Location, [string]$ApplicationPool, [string]$PhysicalPath)
            [pscustomobject]@{ Location = $Location; ApplicationPool = $ApplicationPool; PhysicalPath = $PhysicalPath }
        }
    }

    It 'passes, and returns what serves the folder, when the named pool alone serves it' {
        $result = @(Assert-AppPoolServesPath -Name 'ProdPool' -PhysicalPath 'D:\sites\App' -ApplicationsProbe {
                Get-IisEntry -Location 'Web/' -ApplicationPool 'DefaultAppPool' -PhysicalPath 'D:\sites'
                Get-IisEntry -Location 'Web/App' -ApplicationPool 'ProdPool' -PhysicalPath 'D:\sites\App'
            })

        $result.Count | Should -Be 1
        $result[0].Location | Should -Be 'Web/App'
    }

    It 'refuses when the folder is served by a different pool, and says which and where' {
        # THE FINDING.
        {
            Assert-AppPoolServesPath -Name 'ProdPool' -PhysicalPath 'D:\sites\App' -ApplicationsProbe {
                Get-IisEntry -Location 'Web/App' -ApplicationPool 'RealPool' -PhysicalPath 'D:\sites\App'
            }
        } | Should -Throw -ExpectedMessage "*'D:\sites\App'*Web/App runs under 'RealPool'*Stopping 'ProdPool' would leave that application running*"
    }

    It 'refuses when any one of several applications on the folder runs under another pool' {
        {
            Assert-AppPoolServesPath -Name 'ProdPool' -PhysicalPath 'D:\sites\App' -ApplicationsProbe {
                Get-IisEntry -Location 'Web/App' -ApplicationPool 'ProdPool' -PhysicalPath 'D:\sites\App'
                Get-IisEntry -Location 'Other/App' -ApplicationPool 'OtherPool' -PhysicalPath 'D:\sites\App'
            }
        } | Should -Throw -ExpectedMessage "*Other/App runs under 'OtherPool'*"
    }

    It 'recognises the same folder however IIS spells it' {
        # Case, forward slashes, a trailing separator, a dot segment, an environment variable.
        $env:AUD_A38C_ROOT = 'D:\sites'
        try {
            $result = @(Assert-AppPoolServesPath -Name 'ProdPool' -PhysicalPath 'D:\sites\App' -ApplicationsProbe {
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
        $result = @(Assert-AppPoolServesPath -Name 'ProdPool' -PhysicalPath 'D:\sites\App' -ApplicationsProbe {
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
            Assert-AppPoolServesPath -Name 'ProdPool' -PhysicalPath 'D:\sites\App' -ApplicationsProbe {
                Get-IisEntry -Location 'Web/AppDev' -ApplicationPool 'ProdPool' -PhysicalPath 'D:\sites\AppDev'
            }
        } | Should -Throw -ExpectedMessage "*No IIS site or application has its files at 'D:\sites\App'*IIS reported 1: Web/AppDev at 'D:\sites\AppDev' under 'ProdPool'*"
    }

    It 'fails closed when IIS reports nothing at all' {
        { Assert-AppPoolServesPath -Name 'ProdPool' -PhysicalPath 'D:\sites\App' -ApplicationsProbe { } } |
            Should -Throw -ExpectedMessage '*IIS reported no sites or applications at all*'
    }

    It 'fails closed when IIS cannot be read, and says why' {
        { Assert-AppPoolServesPath -Name 'ProdPool' -PhysicalPath 'D:\sites\App' -ApplicationsProbe { throw 'Cannot read configuration file' } } |
            Should -Throw -ExpectedMessage "*Cannot read the IIS sites and applications*'ProdPool'*Cannot read configuration file*"
    }

    It 'compares pool names without regard to case, as deploy.ps1''s check does' {
        $result = @(Assert-AppPoolServesPath -Name 'ProdPool' -PhysicalPath 'D:\sites\App' -ApplicationsProbe {
                Get-IisEntry -Location 'Web/App' -ApplicationPool 'PRODPOOL' -PhysicalPath 'D:\sites\App'
            })

        $result.Count | Should -Be 1
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
