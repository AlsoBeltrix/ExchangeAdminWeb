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
