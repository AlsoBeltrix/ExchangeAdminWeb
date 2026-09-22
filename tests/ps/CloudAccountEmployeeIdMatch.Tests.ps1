#Requires -Version 7.0
<#
    Tests for tools/CloudAccountEmployeeIdMatch.psm1 - the pure decision logic behind the
    employeeId coverage survey (docs/CloudPasswordReset-Plan.md, queue item 10).

    The survey's answer feeds a 100%-or-nothing owner decision, so the classification is tested
    exhaustively rather than sampled: a bug here does not crash, it reports a better number than
    reality.
#>

BeforeAll {
    $repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    $modulePath = Join-Path -Path $repoRoot -ChildPath 'tools/CloudAccountEmployeeIdMatch.psm1'
    Import-Module $modulePath -Force
}

Describe 'ConvertTo-LdapFilterValue' {

    It 'leaves an ordinary employee id untouched' {
        ConvertTo-LdapFilterValue -Value '0123456' | Should -Be '0123456'
    }

    It 'escapes an asterisk so an equality test cannot become a wildcard' {
        # The dangerous case. Unescaped, (employeeID=*) matches every stamped user in the forest,
        # which would report Ambiguous everywhere - or, for a lone user, a false Resolved.
        ConvertTo-LdapFilterValue -Value '*' | Should -Be '\2a'
    }

    It 'escapes parentheses so a value cannot close or open a filter clause' {
        ConvertTo-LdapFilterValue -Value '(a)' | Should -Be '\28a\29'
    }

    It 'escapes a backslash' {
        ConvertTo-LdapFilterValue -Value 'a\b' | Should -Be 'a\5cb'
    }

    It 'escapes the backslash FIRST so introduced escapes are not double-escaped' {
        # If '*' were handled before '\', the '\' of the resulting '\2a' would itself be escaped
        # to '\5c2a', which matches a literal backslash-2-a rather than an asterisk.
        ConvertTo-LdapFilterValue -Value '\*' | Should -Be '\5c\2a'
    }

    It 'escapes a NUL character' {
        ConvertTo-LdapFilterValue -Value ([string][char]0) | Should -Be '\00'
    }

    It 'handles a value combining every special character' {
        ConvertTo-LdapFilterValue -Value 'a\b*c(d)e' | Should -Be 'a\5cb\2ac\28d\29e'
    }

    It 'returns empty for an empty value rather than throwing' {
        ConvertTo-LdapFilterValue -Value '' | Should -Be ''
    }
}

Describe 'Get-DomainDnsFromDistinguishedName' {

    It 'joins the DC components in order' {
        Get-DomainDnsFromDistinguishedName -DistinguishedName 'CN=Jo,OU=Staff,DC=corp,DC=example,DC=test' |
            Should -Be 'corp.example.test'
    }

    It 'is case insensitive about the DC attribute name' {
        Get-DomainDnsFromDistinguishedName -DistinguishedName 'CN=Jo,dc=corp,Dc=test' |
            Should -Be 'corp.test'
    }

    It 'tolerates whitespace around the components' {
        Get-DomainDnsFromDistinguishedName -DistinguishedName 'CN=Jo, DC=corp , DC=test' |
            Should -Be 'corp.test'
    }

    It 'handles a DN that is nothing but DC components' {
        Get-DomainDnsFromDistinguishedName -DistinguishedName 'DC=corp,DC=test' |
            Should -Be 'corp.test'
    }

    It 'does not mistake a CN containing the letters dc for a domain component' {
        Get-DomainDnsFromDistinguishedName -DistinguishedName 'CN=adc user,OU=abcdc,DC=corp,DC=test' |
            Should -Be 'corp.test'
    }

    It 'returns null when the DN carries no DC component, rather than a plausible wrong server' {
        Get-DomainDnsFromDistinguishedName -DistinguishedName 'CN=Jo,OU=Staff' | Should -BeNullOrEmpty
    }

    It 'returns null for an empty DN' {
        Get-DomainDnsFromDistinguishedName -DistinguishedName '' | Should -BeNullOrEmpty
    }
}

Describe 'Get-EmployeeIdMatchOutcome' {

    It 'reports Resolved for one enabled match with a mailbox' {
        Get-EmployeeIdMatchOutcome -EmployeeId '1' -MatchCount 1 -OwnerHasMailbox $true -OwnerEnabled $true |
            Should -Be 'Resolved'
    }

    It 'reports NoEmployeeId for a blank id' {
        Get-EmployeeIdMatchOutcome -EmployeeId '' -MatchCount 0 | Should -Be 'NoEmployeeId'
    }

    It 'reports NoEmployeeId for a whitespace-only id, which is not a stamped account' {
        Get-EmployeeIdMatchOutcome -EmployeeId '   ' -MatchCount 0 | Should -Be 'NoEmployeeId'
    }

    It 'reports NoDirectoryMatch when a populated id resolves to nobody' {
        Get-EmployeeIdMatchOutcome -EmployeeId '1' -MatchCount 0 | Should -Be 'NoDirectoryMatch'
    }

    It 'reports Ambiguous when more than one user carries the id' {
        Get-EmployeeIdMatchOutcome -EmployeeId '1' -MatchCount 2 -OwnerHasMailbox $true -OwnerEnabled $true |
            Should -Be 'Ambiguous'
    }

    It 'reports MatchedOwnerDisabled for a single disabled match' {
        Get-EmployeeIdMatchOutcome -EmployeeId '1' -MatchCount 1 -OwnerHasMailbox $true -OwnerEnabled $false |
            Should -Be 'MatchedOwnerDisabled'
    }

    It 'reports MatchedNoMailbox for a single enabled match with no mail' {
        Get-EmployeeIdMatchOutcome -EmployeeId '1' -MatchCount 1 -OwnerHasMailbox $false -OwnerEnabled $true |
            Should -Be 'MatchedNoMailbox'
    }

    Context 'precedence, which is the whole judgment of the survey' {

        It 'ranks Unavailable above every other outcome, so a failed read never reads as a clean negative' {
            Get-EmployeeIdMatchOutcome -EmployeeId '1' -MatchCount 1 -OwnerHasMailbox $true -OwnerEnabled $true -Unavailable $true |
                Should -Be 'Unavailable'
        }

        It 'reports Unavailable even when the id is blank, because the two say different things' {
            Get-EmployeeIdMatchOutcome -EmployeeId '' -MatchCount 0 -Unavailable $true | Should -Be 'Unavailable'
        }

        It 'ranks Ambiguous above the mailbox and enabled checks, which would describe an arbitrary user' {
            Get-EmployeeIdMatchOutcome -EmployeeId '1' -MatchCount 3 -OwnerHasMailbox $false -OwnerEnabled $false |
                Should -Be 'Ambiguous'
        }

        It 'ranks a disabled owner above a missing mailbox when both are true' {
            Get-EmployeeIdMatchOutcome -EmployeeId '1' -MatchCount 1 -OwnerHasMailbox $false -OwnerEnabled $false |
                Should -Be 'MatchedOwnerDisabled'
        }

        It 'never reports Resolved for a blank id however healthy the other inputs look' {
            Get-EmployeeIdMatchOutcome -EmployeeId '' -MatchCount 1 -OwnerHasMailbox $true -OwnerEnabled $true |
                Should -Not -Be 'Resolved'
        }
    }

    It 'only ever returns a declared outcome class' {
        $classes = Get-EmployeeIdOutcomeClasses
        foreach ($id in @('', '1')) {
            foreach ($count in @(0, 1, 2)) {
                foreach ($mail in @($true, $false)) {
                    foreach ($enabled in @($true, $false)) {
                        foreach ($unavailable in @($true, $false)) {
                            $outcome = Get-EmployeeIdMatchOutcome -EmployeeId $id -MatchCount $count `
                                -OwnerHasMailbox $mail -OwnerEnabled $enabled -Unavailable $unavailable
                            $classes | Should -Contain $outcome
                        }
                    }
                }
            }
        }
    }
}

Describe 'Get-CloudAccountEmployeeIdCoverage.ps1 connection contract' {

    BeforeAll {
        $repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
        $script:SurveyPath = Join-Path -Path $repoRoot -ChildPath 'tools/Get-CloudAccountEmployeeIdCoverage.ps1'
        $script:SurveyText = Get-Content -LiteralPath $script:SurveyPath -Raw
        $script:MachinesPath = Join-Path -Path $repoRoot -ChildPath '.agents/machines.md'

        # Every "must not appear" assertion below runs against CODE ONLY. The script explains in
        # prose why it does not call Connect-MgGraph, Connect-AllM365Services or -Scopes, and an
        # assertion over the raw text would be tripped by the explanation rather than by a
        # regression - punishing the script for documenting itself.
        $noBlockComments = [regex]::Replace($script:SurveyText, '(?s)<#.*?#>', '')
        $script:SurveyCode = ($noBlockComments -split "`n" |
                Where-Object { $_ -notmatch '^\s*#' } |
                ForEach-Object { $_ -replace '\s#.*$', '' }) -join "`n"
    }

    It 'parses without syntax errors' {
        $errors = $null
        [System.Management.Automation.Language.Parser]::ParseFile($script:SurveyPath, [ref]$null, [ref]$errors) | Out-Null
        $errors | Should -BeNullOrEmpty
    }

    It 'sets $ErrorActionPreference = Stop (repo error model)' {
        $script:SurveyText | Should -Match "\`$ErrorActionPreference\s*=\s*'Stop'"
    }

    It 'exposes a -PlanOnly switch and gates every step through Invoke-PlanOrAction' {
        $script:SurveyText | Should -Match '\[switch\]\s*\$PlanOnly'
        # Nothing may act outside the gate, or -PlanOnly would query the tenant.
        $script:SurveyText | Should -Match 'Invoke-PlanOrAction'
    }

    It 'connects Graph through GraphConnect, not its own Connect-MgGraph' {
        # Owner, 2026-09-22: "I don't log in to graph like this. I use the connection module's
        # GraphConnect." An interactive Connect-MgGraph here would prompt the operator for a
        # delegated sign-in that this tenant's tooling does not use.
        $script:SurveyText | Should -Match '(?m)^\s*GraphConnect\s*$'
        $script:SurveyCode | Should -Not -Match 'Connect-MgGraph\s+-'
    }

    It 'requests no delegated scopes, because GraphConnect is app-only' {
        # Matches a -Scopes that is actually PASSED something, so the script stays free to
        # explain in a comment why it does not use one.
        $script:SurveyCode | Should -Not -Match "-Scopes\s+['`"@\$]"
    }

    It 'loads Active Directory through ADImport rather than opening its own' {
        $script:SurveyText | Should -Match '(?m)^\s*ADImport\s*$'
    }

    It 'never calls Connect-AllM365Services, which does more than this survey needs' {
        $script:SurveyCode | Should -Not -Match 'Connect-AllM365Services'
    }

    It 'reads the module path from machines.md instead of hardcoding it' {
        $script:SurveyText | Should -Match 'm365-connections-module:'
        $script:SurveyCode | Should -Not -Match 'D:\\\\source\\\\scripts'
    }

    It 'has a machines.md entry for the path to resolve' {
        Select-String -LiteralPath $script:MachinesPath -Pattern 'm365-connections-module:\s*`([^`]+)`' |
            Should -Not -BeNullOrEmpty
    }

    It 'names no environment-specific domain, host or address' {
        foreach ($needle in @('analog', 'ashbex', 'winroot')) {
            $script:SurveyCode | Should -Not -Match $needle
        }
    }

    It 'fails closed when the forest cannot be resolved, rather than falling back to one domain' {
        # A local-domain search cannot see a cross-domain duplicate employeeId, so a fallback
        # would under-report Ambiguous and read as a cleaner result.
        $script:SurveyText | Should -Match 'Could not resolve the forest'
        $script:SurveyText | Should -Match 'under-report'
    }

    It 'never writes to the directory: no Set, New or Remove AD or Mg cmdlets' {
        $script:SurveyCode | Should -Not -Match '(Set|New|Remove|Update)-(AD|Mg)\w+'
    }

    It 'NEVER enumerates the directory' {
        # Owner, 2026-09-22: "you cannot enumerate all users. that is a data exfill security
        # flag." Reading the tenant to find a few hundred named accounts is an exfiltration
        # pattern whatever the intent, and the module this gates resolves one named target per
        # operation too. Every Get-MgUser here must be -UserId.
        $script:SurveyCode | Should -Not -Match 'Get-MgUser\s+-All'
        $script:SurveyCode | Should -Not -Match '-All\b'
        foreach ($call in [regex]::Matches($script:SurveyCode, 'Get-MgUser[^\r\n|]*')) {
            $call.Value | Should -Match '-UserId'
        }
    }

    It 'takes the in-scope population as a mandatory input instead of discovering it' {
        $script:SurveyCode | Should -Match '\[Parameter\(Mandatory\)\]'
        $script:SurveyCode | Should -Match '\$UpnListPath'
        $script:SurveyCode | Should -Match 'ConvertTo-UpnList'
    }

    It 'computes the rate over in-scope accounts and shows what it excluded' {
        # A denominator change raises the percentage without changing a directory fact, so the
        # excluded count has to reach the screen.
        $script:SurveyCode | Should -Match 'Get-EmployeeIdScopeExclusions'
        $script:SurveyCode | Should -Match 'excluded from the rate'
        $script:SurveyCode | Should -Match '\$inScope'
    }
}

Describe 'ConvertTo-UpnList' {

    It 'reads one UPN per line' {
        ConvertTo-UpnList -Lines @('a@x.test', 'b@x.test') | Should -Be @('a@x.test', 'b@x.test')
    }

    It 'ignores blank lines and comments so the operator can annotate the file' {
        ConvertTo-UpnList -Lines @('# admins', '', 'a@x.test', '   ', '# tactical', 'b@x.test') |
            Should -Be @('a@x.test', 'b@x.test')
    }

    It 'collapses duplicates, which would otherwise inflate both sides of the rate' {
        ConvertTo-UpnList -Lines @('a@x.test', 'a@x.test', 'b@x.test') | Should -Be @('a@x.test', 'b@x.test')
    }

    It 'reads a CSV by its UserPrincipalName header rather than by position' {
        $csv = @('DisplayName,UserPrincipalName,Id', 'Jo,a@x.test,1', 'Sam,b@x.test,2')
        ConvertTo-UpnList -Lines $csv | Should -Be @('a@x.test', 'b@x.test')
    }

    It 'reads a CloudUPN column, which is what the existing employeeId true-up files use' {
        # So original_corrected.csv and its siblings can be passed in unchanged. Re-shaping a list
        # of privileged accounts by hand is a step where one can be dropped or mistyped.
        $csv = @('"CloudUPN","OwnerADUPN","EmployeeId"', '"a@x.test","owner@y.test","0001"')
        ConvertTo-UpnList -Lines $csv | Should -Be @('a@x.test')
    }

    It 'prefers the UPN column even when other columns also end in Name' {
        $csv = @('DisplayName,CloudUPN', 'Jo,a@x.test')
        ConvertTo-UpnList -Lines $csv | Should -Be @('a@x.test')
    }

    It 'strips quotes from a quoted CSV export' {
        $csv = @('"DisplayName","UserPrincipalName"', '"Jo","a@x.test"')
        ConvertTo-UpnList -Lines $csv | Should -Be @('a@x.test')
    }

    It 'returns empty for an empty file rather than throwing' {
        ConvertTo-UpnList -Lines @() | Should -BeNullOrEmpty
    }

    It 'returns empty when the file is only comments' {
        ConvertTo-UpnList -Lines @('# nothing here', '') | Should -BeNullOrEmpty
    }
}

Describe 'Get-CloudAccountScopeOutcome' {

    It 'admits a cloud-only member account' {
        Get-CloudAccountScopeOutcome -Found $true -OnPremisesSyncEnabled $null -UserType 'Member' |
            Should -Be 'InScope'
    }

    It 'reports CloudAccountNotFound for a listed account that is not in the tenant' {
        Get-CloudAccountScopeOutcome -Found $false | Should -Be 'CloudAccountNotFound'
    }

    It 'excludes a synced account, which L2 already resets on-premises' {
        Get-CloudAccountScopeOutcome -Found $true -OnPremisesSyncEnabled $true -UserType 'Member' |
            Should -Be 'NotCloudOnly'
    }

    It 'excludes a guest' {
        Get-CloudAccountScopeOutcome -Found $true -OnPremisesSyncEnabled $null -UserType 'Guest' |
            Should -Be 'NotCloudOnly'
    }

    It 'reads the sync flag fail-closed: only a definite true counts as synced' {
        # onPremisesSyncEnabled is NULL rather than false for a cloud-only account. Treating an
        # unreadable value as synced would drop the account out of the denominator silently.
        Get-CloudAccountScopeOutcome -Found $true -OnPremisesSyncEnabled $false -UserType 'Member' |
            Should -Be 'InScope'
        Get-CloudAccountScopeOutcome -Found $true -OnPremisesSyncEnabled $null -UserType $null |
            Should -Be 'InScope'
    }

    It 'only ever returns a declared outcome class' {
        $classes = @(Get-EmployeeIdOutcomeClasses) + 'InScope'
        foreach ($found in @($true, $false)) {
            foreach ($sync in @($true, $false, $null)) {
                foreach ($type in @('Member', 'Guest', $null)) {
                    $classes | Should -Contain (Get-CloudAccountScopeOutcome -Found $found -OnPremisesSyncEnabled $sync -UserType $type)
                }
            }
        }
    }
}

Describe 'Get-EmployeeIdScopeExclusions' {

    It 'excludes exactly the two list problems, and nothing that is a matching failure' {
        # Widening this raises the reported percentage without changing a directory fact. Pinned
        # so that doing so is a visible, deliberate edit.
        Get-EmployeeIdScopeExclusions | Should -Be @('NotCloudOnly', 'CloudAccountNotFound')
    }

    It 'never excludes a real coverage failure' {
        foreach ($outcome in @('NoEmployeeId', 'NoDirectoryMatch', 'Ambiguous', 'MatchedOwnerDisabled', 'MatchedNoMailbox', 'Unavailable')) {
            Get-EmployeeIdScopeExclusions | Should -Not -Contain $outcome
        }
    }

    It 'names only declared outcome classes' {
        foreach ($excluded in (Get-EmployeeIdScopeExclusions)) {
            Get-EmployeeIdOutcomeClasses | Should -Contain $excluded
        }
    }
}

Describe 'Get-EmployeeIdOutcomeClasses' {

    It 'lists Resolved first, because the summary leads with the number the bar is set against' {
        (Get-EmployeeIdOutcomeClasses)[0] | Should -Be 'Resolved'
    }

    It 'contains no duplicates, so no class can be printed twice' {
        $classes = Get-EmployeeIdOutcomeClasses
        ($classes | Sort-Object -Unique).Count | Should -Be $classes.Count
    }

    It 'includes every outcome the classifier can produce' {
        # The guard against a new outcome being added to the classifier and silently vanishing
        # from the report, which would make the percentages not add up to 100.
        Get-EmployeeIdOutcomeClasses | Should -Contain 'Unavailable'
        Get-EmployeeIdOutcomeClasses | Should -Contain 'Ambiguous'
        Get-EmployeeIdOutcomeClasses | Should -Contain 'NoEmployeeId'
        Get-EmployeeIdOutcomeClasses | Should -Contain 'NoDirectoryMatch'
        Get-EmployeeIdOutcomeClasses | Should -Contain 'MatchedOwnerDisabled'
        Get-EmployeeIdOutcomeClasses | Should -Contain 'MatchedNoMailbox'
    }
}
