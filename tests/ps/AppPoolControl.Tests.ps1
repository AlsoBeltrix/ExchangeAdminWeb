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
