#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.5' }

<#
Static invariant tests for the ops scripts. Nothing here executes a script:
each test parses the script (AST/text) and asserts the invariants from
AGENTS.md "Architectural Invariants" and docs/ProjectConstitution.md
"Deployment And Versioning".

Regression anchor: commit 0021502 - a robocopy /XD mistake made deploys purge
runtime config. The exclusion tests below exist so that class of incident
cannot ship silently again.
#>

BeforeAll {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path

    function Get-ScriptUnderTest {
        param([string]$RelativePath)
        $path = Join-Path $RepoRoot $RelativePath
        $tokens = $null
        $errors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
        [pscustomobject]@{
            Path   = $path
            Text   = Get-Content -LiteralPath $path -Raw
            Ast    = $ast
            Errors = $errors
        }
    }

    function Get-RobocopyArgumentList {
        param($Script)
        # Every "$robocopyArgs = @(...)" assignment, as its literal string elements.
        $assignments = $Script.Ast.FindAll({
                param($node)
                $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
                $node.Left.Extent.Text -match 'robocopyArgs'
            }, $true)
        foreach ($assignment in $assignments) {
            $strings = $assignment.Right.FindAll({
                    param($node)
                    $node -is [System.Management.Automation.Language.StringConstantExpressionAst]
                }, $true)
            , @($strings | ForEach-Object Value)
        }
    }

    function Find-FunctionDefinition {
        [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', 'Name',
            Justification = 'Used inside the Find predicate scriptblock; analyzer cannot see into it')]
        param($Script, [string]$Name)
        $Script.Ast.Find({
                param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                $node.Name -eq $Name
            }, $true)
    }

    function Get-FreshInstallBranch {
        # The else clause of deploy.ps1's `if ($isUpgrade)` statement: the fresh-install path,
        # which every -Force run takes too, existing deployment or not. (The `$mode = if
        # ($isUpgrade) ...` expression is the right-hand side of an assignment, not the branch.)
        param($Script)
        $modeBranches = @($Script.Ast.FindAll({
                    param($node)
                    $node -is [System.Management.Automation.Language.IfStatementAst] -and
                    $node.Clauses[0].Item1.Extent.Text -eq '$isUpgrade' -and
                    -not ($node.Parent -is [System.Management.Automation.Language.AssignmentStatementAst])
                }, $true))
        if ($modeBranches.Count -ne 1) { throw "expected exactly one 'if (`$isUpgrade)' mode branch, found $($modeBranches.Count)" }
        if ($null -eq $modeBranches[0].ElseClause) { throw 'the mode branch has no else clause' }
        $modeBranches[0].ElseClause
    }

    function Get-ConditionalAncestor {
        # Every if, loop, switch, catch, trap, script block or function between $Node and $Stop:
        # the constructs that can make a statement run on some paths and not others.
        param($Node, $Stop)
        $types = @(
            [System.Management.Automation.Language.IfStatementAst],
            [System.Management.Automation.Language.LoopStatementAst],
            [System.Management.Automation.Language.SwitchStatementAst],
            [System.Management.Automation.Language.CatchClauseAst],
            [System.Management.Automation.Language.TrapStatementAst],
            [System.Management.Automation.Language.ScriptBlockExpressionAst],
            [System.Management.Automation.Language.FunctionDefinitionAst])
        $ancestor = $Node.Parent
        while ($null -ne $ancestor -and -not [object]::ReferenceEquals($ancestor, $Stop)) {
            foreach ($type in $types) {
                if ($ancestor -is $type) { $ancestor }
            }
            $ancestor = $ancestor.Parent
        }
    }

    function Get-NamedArgumentText {
        # The source text of the argument bound to -$Name on one command, or $null.
        param($Command, [string]$Name)
        $elements = $Command.CommandElements
        for ($i = 1; $i -lt $elements.Count; $i++) {
            $element = $elements[$i]
            if ($element -is [System.Management.Automation.Language.CommandParameterAst] -and $element.ParameterName -eq $Name) {
                if ($null -ne $element.Argument) { return $element.Argument.Extent.Text }
                if ($i + 1 -lt $elements.Count) { return $elements[$i + 1].Extent.Text }
            }
        }
        return $null
    }

    function Test-IcaclsCallsAreChecked {
        # Every icacls invocation must be guarded: the text immediately following the call
        # (up to the next ~150 chars) must reference $LASTEXITCODE. A bare "icacls ... |
        # Out-Null" followed by Write-Success/Write-Ok fails this. A simple count of
        # "$LASTEXITCODE -ne 0" vs icacls calls does NOT work (dotnet publish also checks
        # the var, and the Set-AclChecked helper's own icacls would balance the tally),
        # which is why this checks each call site structurally.
        param($Script)
        $calls = $Script.Ast.FindAll({
                param($node)
                $node -is [System.Management.Automation.Language.CommandAst] -and
                $node.GetCommandName() -eq 'icacls'
            }, $true)
        $unguarded = foreach ($call in $calls) {
            $tail = $Script.Text.Substring(
                $call.Extent.EndOffset,
                [Math]::Min(150, $Script.Text.Length - $call.Extent.EndOffset))
            if ($tail -notmatch '\$LASTEXITCODE') {
                "line $($call.Extent.StartLineNumber): $($call.Extent.Text)"
            }
        }
        [pscustomobject]@{ Count = $calls.Count; Unguarded = @($unguarded) }
    }
}

Describe 'deploy.ps1' {
    BeforeAll { $script:s = Get-ScriptUnderTest 'deploy.ps1' }

    It 'verifies which pool serves the app before stopping one (review of aud-a35)' {
        # Confirming that $AppPoolName stopped proves nothing if the IIS application being
        # mirrored runs under a DIFFERENT pool. A mistyped -AppPoolName creates and stops
        # that wrong pool, and robocopy /MIR then replaces the binaries of an application
        # still serving requests - the A35 failure reached by another door.
        #
        # Both deploy paths, and matched as condition-plus-refusal so the check cannot be
        # left present but dead.
        ([regex]::Matches($s.Text, '\$boundPool = try \{ \(Get-WebApplication')).Count |
            Should -Be 2 -Because 'the update path and the fresh-install path both mirror'
        # Since cdt-19 an unreadable binding is refused just above, so this no longer needs (and must
        # not have) a `$boundPool -and` that lets the null case through.
        ([regex]::Matches($s.Text, 'if \(\$boundPool -ne \$AppPoolName\)')).Count |
            Should -Be 2 -Because 'each site must refuse, not merely read the binding'
        ([regex]::Matches($s.Text, '(?s)if \(\$boundPool -ne \$AppPoolName\) \{\s*Write-Fail')).Count |
            Should -Be 2 -Because 'the condition must lead to a refusal, not sit there dead'

        # And it must come before the stop, not after it.
        $checkAt = $s.Text.IndexOf('$boundPool =')
        $stopAt = $s.Text.IndexOf('Stop-AppPoolVerified')
        $checkAt | Should -BeLessThan $stopAt
    }

    It 'refuses to stop and mirror when the binding cannot be read, and continues only on a confirmed stop (cdt-19, AST)' {
        # Both branches configure the pool and the application before they get here, so an
        # unreadable or empty binding, or a pool reported absent, is IIS not answering - not a
        # fresh install. Neither may reach the mirror.
        $modeIf = (Get-FreshInstallBranch $s).Parent
        foreach ($entry in @(@('upgrade', $modeIf.Clauses[0].Item2), @('-Force', $modeIf.ElseClause))) {
            $statements = @($entry[1].Statements)
            $lookupAt = -1; $stopAt = -1
            for ($i = 0; $i -lt $statements.Count; $i++) {
                if ($statements[$i].Extent.Text -like '$boundPool = try { (Get-WebApplication*') { $lookupAt = $i }
                if ($statements[$i].Extent.Text -like '$poolState = Stop-AppPoolVerified -Name $AppPoolName') { $stopAt = $i }
            }
            $lookupAt | Should -BeGreaterOrEqual 0 -Because "the $($entry[0]) branch reads the binding"
            $stopAt | Should -BeGreaterThan $lookupAt

            $unknown = $statements[$lookupAt + 1]
            $unknown | Should -BeOfType ([System.Management.Automation.Language.IfStatementAst])
            $unknown.Clauses[0].Item1.Extent.Text | Should -BeExactly '-not $boundPool' -Because "an unreadable binding must stop the $($entry[0]) run"
            $unknown.ElseClause | Should -BeNullOrEmpty
            $unknownFail = $unknown.Clauses[0].Item2.Statements[0].Extent.Text
            $unknownFail | Should -BeLike 'Write-Fail "*'
            $unknownFail | Should -Match ([regex]::Escape('$ParentSite/$AppAlias')) -Because 'the refusal names the application it could not read'

            $wrong = $statements[$lookupAt + 2]
            $wrong.Clauses[0].Item1.Extent.Text | Should -BeExactly '$boundPool -ne $AppPoolName'
            $wrong.Clauses[0].Item2.Statements[0].Extent.Text | Should -BeLike 'Write-Fail "*'

            $confirmed = $statements[$stopAt + 1]
            $confirmed | Should -BeOfType ([System.Management.Automation.Language.IfStatementAst])
            $confirmed.Clauses[0].Item1.Extent.Text | Should -BeExactly "`$poolState -ne 'Stopped'" -Because "only a confirmed stop may reach the $($entry[0]) mirror"
            $confirmed.ElseClause | Should -BeNullOrEmpty
            $confirmed.Clauses[0].Item2.Statements[0].Extent.Text | Should -BeLike 'Write-Fail "*'
        }
        $s.Text | Should -Not -Match "poolState -eq 'Absent'" -Because 'an absent pool is no longer something to carry on past'
    }

    It 'runs each branch''s real binding-and-stop region: only the named pool, confirmed stopped, reaches the mirror (cdt-19)' {
        foreach ($name in 'Write-Success', 'Write-Warn', 'Write-Fail') {
            . ([scriptblock]::Create((Find-FunctionDefinition $s $name).Extent.Text))
        }
        ${function:Get-WebApplication} = {
            $script:cdt19Calls.Add('lookup')
            if ($script:cdt19Lookup -eq 'throws') { throw 'Cannot read configuration file due to insufficient permissions' }
            if ($script:cdt19Lookup -eq 'nothing') { return $null }
            [pscustomobject]@{ applicationPool = $script:cdt19Lookup }
        }
        ${function:Stop-AppPoolVerified} = { param($Name) $null = $Name; $script:cdt19Calls.Add('stop'); $script:cdt19Stop }
        # Since cdt-28 the region also asks IIS which nodes serve the folder; that check has its own
        # test below, so here it passes and records nothing.
        ${function:Assert-AppPoolServesPath} = { param($Name, $PhysicalPath, $NestedNodes) $null = $Name, $PhysicalPath, $NestedNodes }

        $modeIf = (Get-FreshInstallBranch $s).Parent
        foreach ($entry in @(@('upgrade', $modeIf.Clauses[0].Item2), @('-Force', $modeIf.ElseClause))) {
            $statements = @($entry[1].Statements)
            $from = -1; $stopAt = -1
            for ($i = 0; $i -lt $statements.Count; $i++) {
                if ($statements[$i].Extent.Text -like '$boundPool = *') { $from = $i }
                if ($statements[$i].Extent.Text -like '$poolState = Stop-AppPoolVerified*') { $stopAt = $i }
            }
            $region = ($statements[$from..($stopAt + 1)] | ForEach-Object { $_.Extent.Text }) -join "`n"
            $run = {
                param([string]$Lookup, [string]$Stop)
                $script:cdt19Calls = New-Object System.Collections.Generic.List[string]
                $script:cdt19Lookup = $Lookup
                $script:cdt19Stop = $Stop
                # Read only by the lifted region.
                Set-Variable -Name AppAlias -Value 'ExampleApp'
                Set-Variable -Name ParentSite -Value 'Example Site'
                Set-Variable -Name AppPoolName -Value 'ExamplePool'
                Set-Variable -Name PublishPath -Value 'D:\example\publish'
                $ErrorActionPreference = 'Stop'
                $thrown = $null
                try { . ([scriptblock]::Create($region)) } catch { $thrown = $_.Exception.Message }
                [pscustomobject]@{ Thrown = $thrown; Calls = ($script:cdt19Calls -join ',') }
            }

            foreach ($lookup in 'throws', 'nothing') {
                $r = & $run $lookup 'Stopped' 6>$null
                $r.Calls | Should -BeExactly 'lookup' -Because "an unreadable binding ($lookup) must stop the $($entry[0]) run before any stop"
                # Its own message: the wrong-pool refusal also fires on a null binding, but would tell
                # the operator the application "runs under app pool ''", which is not what happened.
                $r.Thrown | Should -BeLike '*Cannot read which app pool serves IIS application Example Site/ExampleApp*Nothing was deployed*'
            }
            $absent = & $run 'ExamplePool' 'Absent' 6>$null
            $absent.Calls | Should -BeExactly 'lookup,stop'
            $absent.Thrown | Should -BeLike "*reported 'Absent', not confirmed stopped*" -Because "an absent pool must not reach the $($entry[0]) mirror"
            $other = & $run 'SomeOtherPool' 'Stopped' 6>$null
            $other.Calls | Should -BeExactly 'lookup'
            $other.Thrown | Should -BeLike "*runs under app pool 'SomeOtherPool'*"
            $good = & $run 'ExamplePool' 'Stopped' 6>$null
            $good.Thrown | Should -BeNullOrEmpty -Because "the named pool, confirmed stopped, is the one way through to the $($entry[0]) mirror"
            $good.Calls | Should -BeExactly 'lookup,stop'
        }
    }

    It 'asks IIS for every site and application serving the folder right before each verified stop, and nothing catches its refusal (cdt-28, AST)' {
        # The alias checks are about one IIS node, $ParentSite/$AppAlias. Another site or application
        # with its files in $PublishPath under a different pool kept serving them while the mirror
        # replaced them; Assert-AppPoolServesPath (aud-a38c) is the promotion script's answer.
        $modeIf = (Get-FreshInstallBranch $s).Parent
        foreach ($entry in @(@('upgrade', $modeIf.Clauses[0].Item2), @('-Force', $modeIf.ElseClause))) {
            $statements = @($entry[1].Statements)
            $lookupAt = -1; $stopAt = -1
            for ($i = 0; $i -lt $statements.Count; $i++) {
                if ($statements[$i].Extent.Text -like '$boundPool = try { (Get-WebApplication*') { $lookupAt = $i }
                if ($statements[$i].Extent.Text -like '$poolState = Stop-AppPoolVerified -Name $AppPoolName') { $stopAt = $i }
            }
            $lookupAt | Should -BeGreaterOrEqual 0 -Because "the $($entry[0]) branch reads the alias binding"
            $stopAt | Should -BeGreaterThan $lookupAt
            # A statement of the branch itself, so nothing conditional or catching sits around it.
            $statements[$stopAt - 1].Extent.Text | Should -BeExactly '$null = Assert-AppPoolServesPath -Name $AppPoolName -PhysicalPath $PublishPath -NestedNodes Refuse' -Because "the folder the $($entry[0]) mirror replaces is checked right before the stop, not only the alias, and nothing may be rooted inside it (aud-a38j)"
            ($stopAt - 1) | Should -BeGreaterThan ($lookupAt + 2) -Because 'the alias checks still run first; they catch a different mistake, with their own message'
        }
        $calls = @($s.Ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Assert-AppPoolServesPath' }, $true))
        $calls.Count | Should -Be 2 -Because 'one per branch, and nowhere else'
        foreach ($call in $calls) {
            $ancestor = $call.Parent
            while ($null -ne $ancestor) {
                if ($ancestor -is [System.Management.Automation.Language.TryStatementAst]) {
                    $ancestor.CatchClauses.Count | Should -Be 0 -Because "a catch around line $($call.Extent.StartLineNumber) could swallow the refusal and let the stop and the mirror run"
                }
                $ancestor = $ancestor.Parent
            }
        }
    }

    It 'runs each branch''s real region: a folder also served by another pool is refused before anything is stopped (cdt-28)' {
        foreach ($name in 'Write-Success', 'Write-Warn', 'Write-Fail') {
            . ([scriptblock]::Create((Find-FunctionDefinition $s $name).Extent.Text))
        }
        ${function:Get-WebApplication} = { $script:cdt28Calls.Add('lookup'); [pscustomobject]@{ applicationPool = $script:cdt28AliasPool } }
        ${function:Assert-AppPoolServesPath} = {
            param($Name, $PhysicalPath, $NestedNodes)
            $script:cdt28Calls.Add("serves($Name|$PhysicalPath|$NestedNodes)")
            if ($script:cdt28ServedElsewhere) {
                throw "The files at '$PhysicalPath' are served by an app pool other than '$Name': Other Site/Copy runs under 'OtherPool'. Stopping '$Name' would leave that application running while its files are replaced. Name the pool that serves them, or fix the binding."
            }
            [pscustomobject]@{ Location = 'Example Site/ExampleApp'; ApplicationPool = $Name; PhysicalPath = $PhysicalPath }
        }
        ${function:Stop-AppPoolVerified} = { param($Name) $script:cdt28Calls.Add("stop($Name)"); 'Stopped' }

        $modeIf = (Get-FreshInstallBranch $s).Parent
        foreach ($entry in @(@('upgrade', $modeIf.Clauses[0].Item2), @('-Force', $modeIf.ElseClause))) {
            $statements = @($entry[1].Statements)
            $from = -1; $stopAt = -1
            for ($i = 0; $i -lt $statements.Count; $i++) {
                if ($statements[$i].Extent.Text -like '$boundPool = *') { $from = $i }
                if ($statements[$i].Extent.Text -like '$poolState = Stop-AppPoolVerified*') { $stopAt = $i }
            }
            $region = ($statements[$from..($stopAt + 1)] | ForEach-Object { $_.Extent.Text }) -join "`n"
            $run = {
                param([string]$AliasPool, [bool]$ServedElsewhere)
                $script:cdt28Calls = New-Object System.Collections.Generic.List[string]
                $script:cdt28AliasPool = $AliasPool
                $script:cdt28ServedElsewhere = $ServedElsewhere
                # Read only by the lifted region.
                Set-Variable -Name AppAlias -Value 'ExampleApp'
                Set-Variable -Name ParentSite -Value 'Example Site'
                Set-Variable -Name AppPoolName -Value 'ExamplePool'
                Set-Variable -Name PublishPath -Value 'D:\example\publish'
                $ErrorActionPreference = 'Stop'
                $thrown = $null
                $output = @()
                try { $output = @(. ([scriptblock]::Create($region))) } catch { $thrown = $_.Exception.Message }
                [pscustomobject]@{ Thrown = $thrown; Calls = ($script:cdt28Calls -join ','); Output = $output }
            }

            $elsewhere = & $run 'ExamplePool' $true 6>$null
            $elsewhere.Calls | Should -BeExactly 'lookup,serves(ExamplePool|D:\example\publish|Refuse)' -Because "a folder another pool also serves must stop the $($entry[0]) run before any pool is stopped"
            $elsewhere.Thrown | Should -BeLike "*served by an app pool other than 'ExamplePool'*" -Because 'the refusal is the module''s own, unchanged'

            $aliasElsewhere = & $run 'SomeOtherPool' $false 6>$null
            $aliasElsewhere.Calls | Should -BeExactly 'lookup' -Because 'the alias check still runs first'
            $aliasElsewhere.Thrown | Should -BeLike "*runs under app pool 'SomeOtherPool'*"

            $good = & $run 'ExamplePool' $false 6>$null
            $good.Thrown | Should -BeNullOrEmpty
            $good.Calls | Should -BeExactly 'lookup,serves(ExamplePool|D:\example\publish|Refuse),stop(ExamplePool)' -Because "only a folder served by the named pool alone reaches the $($entry[0]) stop"
            @($good.Output).Count | Should -Be 0 -Because 'what the check returns is discarded, not written to the deploy''s output'
        }
    }

    It 'runs each branch''s real region over the real module: a node rooted inside the folder under the same pool is refused before the stop (aud-a38j)' {
        # deploy.ps1 mirrors the folder, so it states -NestedNodes Refuse. Here the module decides,
        # not a stand-in, through its one injection point; each run proves its probe was asked.
        foreach ($name in 'Write-Success', 'Write-Warn', 'Write-Fail') {
            . ([scriptblock]::Create((Find-FunctionDefinition $s $name).Extent.Text))
        }
        Import-Module (Join-Path $RepoRoot 'tools/AppPoolControl.psm1') -Force
        try {
            ${function:Get-WebApplication} = { $script:a38jCalls.Add('lookup'); [pscustomobject]@{ applicationPool = 'ExamplePool' } }
            ${function:Stop-AppPoolVerified} = { param($Name) $script:a38jCalls.Add("stop($Name)"); 'Stopped' }
            # A script block value is evaluated to produce the default, so the probe is handed over
            # wrapped in a block that returns it (the aud-a38b lesson).
            $PSDefaultParameterValues = @{
                'Assert-AppPoolServesPath:ApplicationsProbe' = {
                    {
                        $script:a38jCalls.Add('applications probe')
                        [pscustomobject]@{ Location = 'Example Site/ExampleApp'; ApplicationPool = 'ExamplePool'; PhysicalPath = 'D:\example\publish' }
                        if ($script:a38jNested) {
                            [pscustomobject]@{ Location = 'Example Site/ExampleApp/reports'; ApplicationPool = 'ExamplePool'; PhysicalPath = 'D:\example\publish\reports' }
                        }
                    }
                }
            }

            $modeIf = (Get-FreshInstallBranch $s).Parent
            foreach ($entry in @(@('upgrade', $modeIf.Clauses[0].Item2), @('-Force', $modeIf.ElseClause))) {
                $statements = @($entry[1].Statements)
                $from = -1; $stopAt = -1
                for ($i = 0; $i -lt $statements.Count; $i++) {
                    if ($statements[$i].Extent.Text -like '$boundPool = *') { $from = $i }
                    if ($statements[$i].Extent.Text -like '$poolState = Stop-AppPoolVerified*') { $stopAt = $i }
                }
                $region = ($statements[$from..($stopAt + 1)] | ForEach-Object { $_.Extent.Text }) -join "`n"
                $run = {
                    param([bool]$Nested)
                    $script:a38jCalls = New-Object System.Collections.Generic.List[string]
                    $script:a38jNested = $Nested
                    # Read only by the lifted region.
                    Set-Variable -Name AppAlias -Value 'ExampleApp'
                    Set-Variable -Name ParentSite -Value 'Example Site'
                    Set-Variable -Name AppPoolName -Value 'ExamplePool'
                    Set-Variable -Name PublishPath -Value 'D:\example\publish'
                    $ErrorActionPreference = 'Stop'
                    $thrown = $null
                    try { . ([scriptblock]::Create($region)) } catch { $thrown = $_.Exception.Message }
                    [pscustomobject]@{ Thrown = $thrown; Calls = ($script:a38jCalls -join ',') }
                }

                $nested = & $run $true 6>$null
                $nested.Calls | Should -BeExactly 'lookup,applications probe' -Because "the $($entry[0]) mirror would replace or purge the nested node's files, so its pool must not be stopped and the mirror must not run"
                $nested.Thrown | Should -BeLike '*Example Site/ExampleApp/reports*'

                $alone = & $run $false 6>$null
                $alone.Thrown | Should -BeNullOrEmpty
                $alone.Calls | Should -BeExactly 'lookup,applications probe,stop(ExamplePool)'
            }
        } finally {
            Remove-Module AppPoolControl -Force -ErrorAction SilentlyContinue
        }
    }

    It 'runs each branch''s real region over the real module: a folder the named pool is not shown to serve alone is refused in the mirror''s terms, before the stop (aud-a38k)' {
        # deploy.ps1 mirrors the folder, so both refusals still say what the mirror would do; only the
        # cutover's (-NestedNodes Allow) wording changed. The module decides here, not a stand-in.
        foreach ($name in 'Write-Success', 'Write-Warn', 'Write-Fail') {
            . ([scriptblock]::Create((Find-FunctionDefinition $s $name).Extent.Text))
        }
        Import-Module (Join-Path $RepoRoot 'tools/AppPoolControl.psm1') -Force
        try {
            ${function:Get-WebApplication} = { $script:a38kCalls.Add('lookup'); [pscustomobject]@{ applicationPool = 'ExamplePool' } }
            ${function:Stop-AppPoolVerified} = { param($Name) $script:a38kCalls.Add("stop($Name)"); 'Stopped' }
            $PSDefaultParameterValues = @{
                'Assert-AppPoolServesPath:ApplicationsProbe' = {
                    {
                        $script:a38kCalls.Add('applications probe')
                        [pscustomobject]@{ Location = 'Example Site/'; ApplicationPool = 'DefaultAppPool'; PhysicalPath = 'D:\example\wwwroot' }
                        if ($script:a38kElsewhere) {
                            [pscustomobject]@{ Location = 'Other Site/Copy'; ApplicationPool = 'OtherPool'; PhysicalPath = 'D:\example\publish' }
                        }
                    }
                }
            }

            $modeIf = (Get-FreshInstallBranch $s).Parent
            foreach ($entry in @(@('upgrade', $modeIf.Clauses[0].Item2), @('-Force', $modeIf.ElseClause))) {
                $statements = @($entry[1].Statements)
                $from = -1; $stopAt = -1
                for ($i = 0; $i -lt $statements.Count; $i++) {
                    if ($statements[$i].Extent.Text -like '$boundPool = *') { $from = $i }
                    if ($statements[$i].Extent.Text -like '$poolState = Stop-AppPoolVerified*') { $stopAt = $i }
                }
                $region = ($statements[$from..($stopAt + 1)] | ForEach-Object { $_.Extent.Text }) -join "`n"
                $run = {
                    param([bool]$Elsewhere)
                    $script:a38kCalls = New-Object System.Collections.Generic.List[string]
                    $script:a38kElsewhere = $Elsewhere
                    # Read only by the lifted region.
                    Set-Variable -Name AppAlias -Value 'ExampleApp'
                    Set-Variable -Name ParentSite -Value 'Example Site'
                    Set-Variable -Name AppPoolName -Value 'ExamplePool'
                    Set-Variable -Name PublishPath -Value 'D:\example\publish'
                    $ErrorActionPreference = 'Stop'
                    $thrown = $null
                    try { . ([scriptblock]::Create($region)) } catch { $thrown = $_.Exception.Message }
                    [pscustomobject]@{ Thrown = $thrown; Calls = ($script:a38kCalls -join ',') }
                }

                $nothing = & $run $false 6>$null
                $nothing.Calls | Should -BeExactly 'lookup,applications probe' -Because "nothing at the folder must stop the $($entry[0]) run before the stop"
                $nothing.Thrown | Should -BeLike "No IIS site or application has its files at 'D:\example\publish'*Refusing to replace files that may still be served."

                $elsewhere = & $run $true 6>$null
                $elsewhere.Calls | Should -BeExactly 'lookup,applications probe' -Because "a folder another pool serves must stop the $($entry[0]) run before the stop"
                $elsewhere.Thrown | Should -BeLike "*Other Site/Copy runs under 'OtherPool'*Stopping 'ExamplePool' would leave that application running while its files are replaced.*"
            }
        } finally {
            Remove-Module AppPoolControl -Force -ErrorAction SilentlyContinue
        }
    }

    It 'confirms the pool is running after starting it, not only that IIS accepted the request (cdt-25)' {
        # Start-AppPoolWithRetry returned as soon as Start-WebAppPool returned, and the script then
        # printed "App pool started" - with the pool possibly still stopped.
        . ([scriptblock]::Create((Find-FunctionDefinition $s 'Start-AppPoolWithRetry').Extent.Text))
        ${function:Get-ItemProperty} = { param([Parameter(Position = 0)]$Path) $null = $Path; [pscustomobject]@{ State = $script:cdt25State } }
        ${function:Start-WebAppPool} = {
            param($Name, $ErrorAction)
            $null = $Name, $ErrorAction
            $script:cdt25Calls.Add('start')
            if ($script:cdt25ThrowOnce -and -not $script:cdt25HasThrown) { $script:cdt25HasThrown = $true; throw 'The object identifier does not represent a valid object.' }
        }
        ${function:Start-AppPoolVerified} = {
            param($Name)
            $script:cdt25Calls.Add('verify')
            if ($script:cdt25NeverRuns) { throw "App pool '$Name' is 'Stopped' after 60 seconds, not Started. The application is DOWN." }
            'Started'
        }
        ${function:Start-Sleep} = { }
        $run = {
            param([string]$State, [bool]$NeverRuns, [bool]$ThrowOnce)
            $script:cdt25State = $State
            $script:cdt25NeverRuns = $NeverRuns
            $script:cdt25ThrowOnce = $ThrowOnce
            $script:cdt25HasThrown = $false
            $script:cdt25Calls = New-Object System.Collections.Generic.List[string]
            $ErrorActionPreference = 'Stop'
            $thrown = $null
            try { Start-AppPoolWithRetry -Name 'ExamplePool' } catch { $thrown = $_.Exception.Message }
            [pscustomobject]@{ Thrown = $thrown; Calls = ($script:cdt25Calls -join ',') }
        }

        $accepted = & $run 'Stopped' $true $false 6>$null
        $accepted.Thrown | Should -BeLike "*is 'Stopped' after 60 seconds*" -Because 'IIS accepting the start is not the pool running (A38)'
        $accepted.Calls | Should -BeExactly 'start,verify'
        $normal = & $run 'Stopped' $false $false 6>$null
        $normal.Thrown | Should -BeNullOrEmpty
        $normal.Calls | Should -BeExactly 'start,verify'
        $transient = & $run 'Stopped' $false $true 6>$null
        $transient.Thrown | Should -BeNullOrEmpty -Because 'a start request that throws once is still retried'
        $transient.Calls | Should -BeExactly 'start,start,verify'
        $already = & $run 'Started' $false $false 6>$null
        $already.Calls | Should -BeExactly 'verify' -Because 'a pool read as started is still confirmed'
    }

    It 'reads the password out of its SecureString and zeroes the unmanaged copy it made (cdt-27)' {
        # SecureString-ToPlain dropped the pointer SecureStringToBSTR returned, so the plaintext stayed
        # in unmanaged memory, never zeroed. A leak is not observable from here; the round trip is
        # driven, and the shape that frees the pointer is pinned.
        $fn = Find-FunctionDefinition $s 'SecureString-ToPlain'
        $fn | Should -Not -BeNullOrEmpty
        . ([scriptblock]::Create($fn.Extent.Text))
        $secure = New-Object System.Security.SecureString
        foreach ($ch in 'example-Passw0rd!'.ToCharArray()) { $secure.AppendChar($ch) }
        SecureString-ToPlain $secure | Should -BeExactly 'example-Passw0rd!'

        $keep = $fn.Find({ param($n)
                $n -is [System.Management.Automation.Language.AssignmentStatementAst] -and
                $n.Right.Extent.Text -like '*SecureStringToBSTR($Secure)*' }, $true)
        $keep | Should -Not -BeNullOrEmpty -Because 'the pointer must be kept to be freed'
        $pointer = $keep.Left.VariablePath.UserPath
        $try = $fn.Find({ param($n) $n -is [System.Management.Automation.Language.TryStatementAst] }, $true)
        $try | Should -Not -BeNullOrEmpty
        $try.Extent.StartOffset | Should -BeGreaterThan $keep.Extent.EndOffset
        $try.Body.Extent.Text | Should -Match ('PtrToStringAuto\(\$' + $pointer + '\)')
        $try.Finally.Extent.Text | Should -Match ('ZeroFreeBSTR\(\$' + $pointer + '\)') -Because 'the unmanaged copy is zeroed and freed on every path'
    }

    It 'never hands a pool, or icacls, an empty identity (cdt-24)' {
        # With -ServiceAccount omitted the existing pool's userName became the account and the pool
        # was set to SpecificUser with it - empty for a pool that runs as ApplicationPoolIdentity or
        # a built-in account. icacls then granted the empty name, which it resolves to
        # BUILTIN\BUILTIN, and exited 0.
        foreach ($name in 'Write-Step', 'Write-Success', 'Write-Warn', 'Write-Fail', 'Prompt-Value', 'Prompt-SecureValue', 'SecureString-ToPlain', 'Set-AclChecked') {
            . ([scriptblock]::Create((Find-FunctionDefinition $s $name).Extent.Text))
        }
        $statements = @($s.Ast.EndBlock.Statements)
        $from = @(for ($i = 0; $i -lt $statements.Count; $i++) { if ($statements[$i].Extent.Text -eq '$needsPassword = $false') { $i } })
        $to = @(for ($i = 0; $i -lt $statements.Count; $i++) { if ($statements[$i].Extent.Text -eq 'Write-Success "App pool configured"') { $i } })
        $from.Count | Should -Be 1
        $to.Count | Should -Be 1
        $region = ($statements[$from[0]..$to[0]] | ForEach-Object { $_.Extent.Text }) -join "`n"

        ${function:Test-Path} = { param([Parameter(Position = 0)]$Path) $null = $Path; $true }
        ${function:Get-ItemProperty} = { param([Parameter(Position = 0)]$Path, $Name) $null = $Path, $Name; [pscustomobject]@{ identityType = $script:cdt24Type; userName = $script:cdt24User } }
        ${function:New-WebAppPool} = { $script:cdt24Changes.Add('New-WebAppPool') }
        ${function:Set-ItemProperty} = { param([Parameter(Position = 0)]$Path, $Name, $Value) $null = $Path; $script:cdt24Changes.Add("$Name=$Value") }
        ${function:icacls} = { $script:cdt24Changes.Add('icacls') }

        $run = {
            param([string]$Type, [string]$User, [string]$Account)
            $script:cdt24Type = $Type
            $script:cdt24User = $User
            $script:cdt24Changes = New-Object System.Collections.Generic.List[string]
            # Read only by the lifted region.
            Set-Variable -Name AppPoolName -Value 'ExamplePool'
            Set-Variable -Name NonInteractive -Value $true
            Set-Variable -Name ServiceAccountPassword -Value $null
            Set-Variable -Name ServiceAccount -Value $Account
            $ErrorActionPreference = 'Stop'
            $thrown = $null
            try { . ([scriptblock]::Create($region)) } catch { $thrown = $_.Exception.Message }
            [pscustomobject]@{ Thrown = $thrown; Changes = @($script:cdt24Changes); Account = $ServiceAccount }
        }

        foreach ($case in @(@('4', ''), @('ApplicationPoolIdentity', ''), @('2', ''), @('3', ''), @('SpecificUser', '  '))) {
            $r = & $run $case[0] $case[1] '' 6>$null
            $r.Thrown | Should -BeLike '*does not run as a specific user*Pass -ServiceAccount*' -Because "a pool reported as identityType '$($case[0])' with user '$($case[1])' must not be given an empty identity"
            $r.Changes.Count | Should -Be 0 -Because 'the refusal comes before anything about the pool is changed'
        }
        foreach ($type in '3', 'SpecificUser') {
            $r = & $run $type 'CONTOSO\svc' '' 6>$null
            $r.Thrown | Should -BeNullOrEmpty -Because "a pool that runs as a user (identityType '$type') keeps it"
            $r.Account | Should -BeExactly 'CONTOSO\svc'
            $r.Changes | Should -Contain 'processModel.userName=CONTOSO\svc'
        }
        $stale = & $run '4' 'CONTOSO\svc' 'CONTOSO\svc' 6>$null
        $stale.Thrown | Should -BeLike '*ServiceAccountPassword is required*' -Because 'moving a pool to a specific user is an identity change, even when a stale user name matches'

        $script:cdt24Changes = New-Object System.Collections.Generic.List[string]
        { Set-AclChecked -Path 'D:\example' -Identity '' -Rights '(OI)(CI)M' } | Should -Throw -ExpectedMessage '*empty identity*BUILTIN\BUILTIN*'
        $script:cdt24Changes | Should -Not -Contain 'icacls' -Because 'an empty identity must never reach icacls'
    }

    It 'parses without syntax errors' {
        $s.Errors | Should -BeNullOrEmpty
    }

    It 'sets $ErrorActionPreference = Stop' {
        $s.Text | Should -Match '\$ErrorActionPreference\s*=\s*"Stop"'
    }

    It 'imports WebAdministration before touching the IIS: drive' {
        # Without the import, Test-Path IIS:\AppPools\... silently returns false
        # and an upgrade wrongly demands a ServiceAccount for the existing pool.
        $importIdx = $s.Text.IndexOf('Import-Module WebAdministration')
        $firstIisIdx = $s.Text.IndexOf('IIS:\')
        $importIdx | Should -BeGreaterOrEqual 0
        $importIdx | Should -BeLessThan $firstIisIdx
    }

    It 'imports WebAdministration exactly once (the guarded top-of-script import)' {
        ([regex]::Matches($s.Text, 'Import-Module WebAdministration')).Count | Should -Be 1
        $s.Text | Should -Match 'Get-PSDrive -Name IIS' -Because 'the import must keep its IIS:-drive availability guard (PS7 loads the module without the provider)'
    }

    It 'excludes runtime config from every robocopy mirror (regression: commit 0021502)' {
        $arrays = @(Get-RobocopyArgumentList $s)
        $arrays.Count | Should -BeGreaterOrEqual 2 -Because 'both the upgrade and fresh-install paths mirror with robocopy'
        foreach ($robocopyArgs in $arrays) {
            $robocopyArgs | Should -Contain '/MIR'
            $robocopyArgs | Should -Contain '/XF'
            $robocopyArgs | Should -Contain 'appsettings*.json'
            $robocopyArgs | Should -Contain '/XD'
            $robocopyArgs | Should -Contain 'logs'
            $robocopyArgs | Should -Contain 'config'
            # /XD only excludes directories listed after it
            [array]::IndexOf($robocopyArgs, '/XD') | Should -BeLessThan ([array]::IndexOf($robocopyArgs, 'logs'))
            [array]::IndexOf($robocopyArgs, '/XD') | Should -BeLessThan ([array]::IndexOf($robocopyArgs, 'config'))
        }
    }

    It 'checks $LASTEXITCODE -ge 8 after every robocopy invocation' {
        $invocations = $s.Ast.FindAll({
                param($node)
                $node -is [System.Management.Automation.Language.CommandAst] -and
                $node.GetCommandName() -eq 'robocopy'
            }, $true)
        $invocations.Count | Should -BeGreaterThan 0
        [regex]::Matches($s.Text, '\$LASTEXITCODE\s+-ge\s+8').Count |
            Should -BeGreaterOrEqual $invocations.Count
    }

    It 'fails on dotnet publish errors' {
        $s.Text | Should -Match 'dotnet publish failed'
    }

    It 'checks the native exit code after every icacls invocation' {
        # icacls is a native exe: $ErrorActionPreference="Stop" does not catch its
        # failures and "| Out-Null" hides the error text, so an unchecked grant fails
        # silently and the next Write-Success lies. Each icacls call site must be guarded
        # by a $LASTEXITCODE check (icacls returns 0 on success). Bare folder grants are
        # routed through Set-AclChecked, whose own icacls is guarded inline.
        $result = Test-IcaclsCallsAreChecked $s
        $result.Count | Should -BeGreaterThan 0
        $result.Unguarded | Should -BeNullOrEmpty -Because "every icacls call must be followed by a `$LASTEXITCODE check; unguarded: $($result.Unguarded -join '; ')"
    }

    It 'routes icacls through a checked helper that throws on failure' {
        $fn = Find-FunctionDefinition $s 'Set-AclChecked'
        $fn | Should -Not -BeNullOrEmpty
        $fn.Extent.Text | Should -Match '\$LASTEXITCODE\s+-ne\s+0'
        $fn.Extent.Text | Should -Match 'Write-Fail'
    }

    It 'Write-Fail throws (repo error model, not exit)' {
        $fn = Find-FunctionDefinition $s 'Write-Fail'
        $fn | Should -Not -BeNullOrEmpty
        $fn.Find({ param($node) $node -is [System.Management.Automation.Language.ThrowStatementAst] }, $true) |
            Should -Not -BeNullOrEmpty
        $fn.Extent.Text | Should -Not -Match '\bexit\b'
    }

    It 'cleans up the staging folder inside finally blocks (reached on failure paths)' {
        $tries = $s.Ast.FindAll({ param($node)
                $node -is [System.Management.Automation.Language.TryStatementAst] -and
                $null -ne $node.Finally }, $true)
        $cleaning = @($tries | Where-Object { $_.Finally.Extent.Text -match 'Remove-Item \$StagingPath' })
        $cleaning.Count | Should -BeGreaterOrEqual 2 -Because 'both the upgrade and fresh-install paths stage a publish containing live appsettings.json'
    }

    It 'removes the staging publish on every failure after it, the pre-deploy backup included (cdt-6)' {
        # Staging holds a published appsettings.json with real environment values. Every
        # command from the publish to the end of the mode-specific section must sit in the
        # BODY of a try whose finally removes it - not merely in some finally that does (a
        # throw from Start-AppPoolWithRetry inside the inner finally skips that finally's own
        # Remove-Item).
        $publish = $s.Ast.Find({ param($node)
                $node -is [System.Management.Automation.Language.CommandAst] -and
                $node.Extent.Text -like 'dotnet publish*-o $StagingPath*' }, $true)
        $publish | Should -Not -BeNullOrEmpty
        $endAt = $s.Text.IndexOf('# --- Final output ---')
        $endAt | Should -BeGreaterThan $publish.Extent.StartOffset

        $commands = @($s.Ast.FindAll({ param($node)
                    $node -is [System.Management.Automation.Language.CommandAst] -and
                    $node.Extent.StartOffset -ge $publish.Extent.StartOffset -and
                    $node.Extent.StartOffset -lt $endAt -and
                    $node.Extent.Text -notmatch '^Remove-Item \$StagingPath\b' }, $true))

        $uncovered = foreach ($command in $commands) {
            $covered = $false
            $ancestor = $command.Parent
            while ($null -ne $ancestor -and -not $covered) {
                if ($ancestor -is [System.Management.Automation.Language.TryStatementAst] -and
                    $null -ne $ancestor.Finally -and
                    $ancestor.Finally.Extent.Text -match 'Remove-Item \$StagingPath' -and
                    $command.Extent.StartOffset -ge $ancestor.Body.Extent.StartOffset -and
                    $command.Extent.EndOffset -le $ancestor.Body.Extent.EndOffset) {
                    $covered = $true
                }
                $ancestor = $ancestor.Parent
            }
            if (-not $covered) { "line $($command.Extent.StartLineNumber): $($command.GetCommandName())" }
        }
        @($uncovered) | Should -BeNullOrEmpty -Because 'a throw there would leave the staging publish behind'

        $names = @($commands | ForEach-Object { $_.GetCommandName() })
        foreach ($name in 'Resolve-ConfigDbTool', 'Backup-SqliteConfigDb', 'Assert-NoActiveBulkJobsBeforeRecycle', 'Stop-AppPoolVerified', 'Start-AppPoolWithRetry', 'Write-DeploymentManifest') {
            $names | Should -Contain $name -Because 'the covered region must reach the pre-deploy backup and the pool restart'
        }
    }

    It 'defaults to the DEV site, never prod (incident fix #6)' {
        # Bare .\deploy.ps1 used to target the prod alias/pool/path while the
        # docs called it "the dev deploy". Prod is reached only via the promote
        # pipeline.
        $defaults = @{}
        foreach ($p in $s.Ast.ParamBlock.Parameters) {
            if ($p.DefaultValue -is [System.Management.Automation.Language.StringConstantExpressionAst]) {
                $defaults[$p.Name.VariablePath.UserPath] = $p.DefaultValue.Value
            }
        }
        $defaults['AppAlias'] | Should -Be 'ExchangeAdminWebDev'
        $defaults['AppPoolName'] | Should -Be 'ExchangeAdminWebDev'
        $defaults['PublishPath'] | Should -Be 'D:\inetpub\ExchangeAdminWebDev'
    }

    It 'refuses a fresh install without explicit consent (incident fix #6)' {
        $s.Ast.ParamBlock.Parameters.Name.VariablePath.UserPath | Should -Contain 'ConfirmFreshInstall'
        $s.Text | Should -Match '-not \$isUpgrade -and -not \$Force -and -not \$ConfirmFreshInstall' `
            -Because 'an unexpected INSTALL usually means the target parameters are wrong'
    }

    It 'refuses a fresh-install or -Force run that would move an existing application, unless -AllowRepoint names the move (cdt-36)' {
        # The fresh-install branch - every -Force run, and -ConfirmFreshInstall - set an EXISTING
        # application's physicalPath and applicationPool to this run's -PublishPath and -AppPoolName,
        # so a mistyped or defaulted value moved the live application. Runs the real function, lifted
        # by AST, with deploy.ps1's own helpers and the module's real ConvertTo-ComparablePhysicalPath.
        foreach ($name in 'Write-Success', 'Write-Warn', 'Write-Fail', 'Assert-ApplicationTargetUnchanged') {
            $fn = Find-FunctionDefinition $s $name
            $fn | Should -Not -BeNullOrEmpty -Because "deploy.ps1 defines $name"
            . ([scriptblock]::Create($fn.Extent.Text))
        }
        Import-Module (Join-Path $RepoRoot 'tools/AppPoolControl.psm1') -Force
        try {
            ${function:Write-Host} = { param([Parameter(Position = 0)]$Object, $ForegroundColor) $null = $ForegroundColor; $script:cdt36Printed.Add("$Object") }
            $check = {
                param([bool]$Exists, [string]$Pool, [string]$Folder, [bool]$Allow)
                $script:cdt36Printed = New-Object System.Collections.Generic.List[string]
                $thrown = $null
                try {
                    Assert-ApplicationTargetUnchanged -Location 'Example Site/ExampleApp' -Exists $Exists -ExistingPool $Pool -ExistingPhysicalPath $Folder -AppPoolName 'ExamplePool' -PublishPath 'D:\example\publish' -AllowRepoint:$Allow
                } catch {
                    $thrown = $_.Exception.Message
                }
                [pscustomobject]@{ Thrown = $thrown; Printed = ($script:cdt36Printed -join "`n") }
            }

            $fresh = & $check $false '' '' $false
            $fresh.Thrown | Should -BeNullOrEmpty -Because 'with no application there is nothing to move'
            $fresh.Printed | Should -BeLike "*No IIS application Example Site/ExampleApp yet*'D:\example\publish'*'ExamplePool'*"

            foreach ($spelling in 'D:\example\publish', 'd:\EXAMPLE\Publish', 'D:\example\publish\', 'D:/example/publish', 'D:\example\x\..\publish') {
                $same = & $check $true 'examplepool' $spelling $false
                $same.Thrown | Should -BeNullOrEmpty -Because "'$spelling' is the folder this run names, and pool names compare case-insensitively"
                $same.Printed | Should -BeLike "*runs under app pool 'examplepool' with its files at '$spelling'*deployed in place*" -Because 'what was compared is printed'
            }
            $env:CDT36_ROOT = 'D:\example'
            try {
                (& $check $true 'ExamplePool' '%CDT36_ROOT%\publish' $false).Thrown | Should -BeNullOrEmpty -Because 'IIS often stores the folder with an environment variable'
            } finally {
                Remove-Item Env:\CDT36_ROOT -ErrorAction SilentlyContinue
            }

            $cases = @(
                @{ Case = 'a different pool'; Pool = 'OtherPool'; Folder = 'D:\example\publish'; Expect = @("app pool 'OtherPool' (this run: -AppPoolName 'ExamplePool')") }
                @{ Case = 'a different folder'; Pool = 'ExamplePool'; Folder = 'D:\example\other'; Expect = @("files at 'D:\example\other' (this run: -PublishPath 'D:\example\publish')") }
                @{ Case = 'both'; Pool = 'OtherPool'; Folder = 'D:\example\other'; Expect = @("app pool 'OtherPool' (this run: -AppPoolName 'ExamplePool')", "files at 'D:\example\other' (this run: -PublishPath 'D:\example\publish')") }
                @{ Case = 'a folder that only starts with the run''s'; Pool = 'ExamplePool'; Folder = 'D:\example\publishOld'; Expect = @("files at 'D:\example\publishOld'") }
                @{ Case = 'an application with no folder'; Pool = 'ExamplePool'; Folder = ''; Expect = @("files at '' (this run: -PublishPath 'D:\example\publish')") }
            )
            foreach ($c in $cases) {
                $refused = & $check $true $c.Pool $c.Folder $false
                $refused.Thrown | Should -Not -BeNullOrEmpty -Because "a run must not move the live application ($($c.Case))"
                foreach ($text in $c.Expect) { $refused.Thrown | Should -BeLike "*$text*" -Because "the refusal names what IIS reports and what the run names ($($c.Case))" }
                $refused.Thrown | Should -BeLike "IIS application Example Site/ExampleApp already exists*Re-run with -AppPoolName '$($c.Pool)' -PublishPath '$($c.Folder)' to deploy it in place, or add -AllowRepoint if the move is intended. Nothing was deployed."

                $moved = & $check $true $c.Pool $c.Folder $true
                $moved.Thrown | Should -BeNullOrEmpty -Because "-AllowRepoint is the explicit consent to the move ($($c.Case))"
                foreach ($text in $c.Expect) { $moved.Printed | Should -BeLike "*$text*" -Because 'the warning names what is moved' }
                $moved.Printed | Should -BeLike "*-AllowRepoint: this run MOVES it to app pool 'ExamplePool' with its files at 'D:\example\publish'*"
            }
        } finally {
            Remove-Module AppPoolControl -Force -ErrorAction SilentlyContinue
        }
    }

    It 'runs the real check before a fresh-install or -Force run configures anything: one IIS read, the comparison printed, nothing changed (cdt-36)' {
        foreach ($name in 'Write-Step', 'Write-Success', 'Write-Warn', 'Write-Fail', 'Assert-ApplicationTargetUnchanged') {
            . ([scriptblock]::Create((Find-FunctionDefinition $s $name).Extent.Text))
        }
        # The check is script-level statements, from its step line to the assertion; since cdt-42 they
        # run on every run, so no condition wraps them.
        $statements = @($s.Ast.EndBlock.Statements)
        $from = @(for ($i = 0; $i -lt $statements.Count; $i++) { if ($statements[$i].Extent.Text -eq 'Write-Step "Checking IIS application $ParentSite/$AppAlias"') { $i } })
        $to = @(for ($i = 0; $i -lt $statements.Count; $i++) { if ($statements[$i].Extent.Text -like 'Assert-ApplicationTargetUnchanged *') { $i } })
        $from.Count | Should -Be 1 -Because 'one script-level step line opens the check'
        $to.Count | Should -Be 1 -Because 'one script-level statement checks the application'
        $to[0] | Should -BeGreaterThan $from[0]
        $region = ($statements[$from[0]..$to[0]] | ForEach-Object { $_.Extent.Text }) -join "`n"

        Import-Module (Join-Path $RepoRoot 'tools/AppPoolControl.psm1') -Force
        try {
            ${function:Write-Host} = { param([Parameter(Position = 0)]$Object, $ForegroundColor) $null = $ForegroundColor; $script:cdt36Printed.Add("$Object") }
            ${function:Get-WebApplication} = {
                param($Name, $Site, $ErrorAction)
                $script:cdt36Calls.Add("lookup($Site/$Name|$ErrorAction)")
                if ($script:cdt36Iis -eq 'throws') { throw 'Cannot read configuration file due to insufficient permissions' }
                if ($script:cdt36Iis -eq 'absent') { return }
                $pool, $folder = $script:cdt36Iis -split '\|', 2
                [pscustomobject]@{ applicationPool = $pool; PhysicalPath = $folder }
            }
            # Everything that would change IIS, an ACL or a file records itself: none may run here.
            foreach ($cmd in 'New-WebAppPool', 'Set-ItemProperty', 'New-WebApplication', 'Set-WebConfigurationProperty', 'Remove-WebConfigurationProperty', 'Stop-WebAppPool', 'Start-WebAppPool', 'Stop-AppPoolVerified', 'Start-AppPoolVerified', 'Set-AclChecked', 'icacls', 'robocopy', 'dotnet', 'New-Item', 'Copy-Item', 'Remove-Item', 'Set-Content') {
                Set-Item -Path "function:$cmd" -Value ([scriptblock]::Create("`$script:cdt36Calls.Add('$cmd')"))
            }

            $run = {
                param([string]$Iis, [string]$Mode, [bool]$Allow)
                $script:cdt36Iis = $Iis
                $script:cdt36Calls = New-Object System.Collections.Generic.List[string]
                $script:cdt36Printed = New-Object System.Collections.Generic.List[string]
                # Read only by the lifted statement. -Force and -ConfirmFreshInstall both make the run a
                # fresh install; an upgrade has appsettings.json and neither switch.
                Set-Variable -Name isUpgrade -Value ($Mode -eq 'upgrade')
                Set-Variable -Name Force -Value ([switch]($Mode -eq 'force'))
                Set-Variable -Name ConfirmFreshInstall -Value ([switch]($Mode -eq 'confirm'))
                Set-Variable -Name AllowRepoint -Value ([switch]$Allow)
                Set-Variable -Name ParentSite -Value 'Example Site'
                Set-Variable -Name AppAlias -Value 'ExampleApp'
                Set-Variable -Name AppPoolName -Value 'ExamplePool'
                Set-Variable -Name PublishPath -Value 'D:\example\publish'
                $ErrorActionPreference = 'Stop'
                $thrown = $null
                try { . ([scriptblock]::Create($region)) } catch { $thrown = $_.Exception.Message }
                [pscustomobject]@{ Thrown = $thrown; Calls = ($script:cdt36Calls -join ','); Printed = ($script:cdt36Printed -join "`n") }
            }
            $read = 'lookup(Example Site/ExampleApp|Stop)'

            foreach ($mode in 'force', 'confirm') {
                $same = & $run 'ExamplePool|D:\example\publish' $mode $false
                $same.Thrown | Should -BeNullOrEmpty -Because "IIS serves the application from the pool and folder this $mode run names"
                $same.Calls | Should -BeExactly $read -Because 'the one IIS call is the read'
                $same.Printed | Should -BeLike "*Checking IIS application Example Site/ExampleApp*runs under app pool 'ExamplePool' with its files at 'D:\example\publish'*deployed in place*" -Because 'what was compared is printed'

                $pool = & $run 'OtherPool|D:\example\publish' $mode $false
                $pool.Thrown | Should -BeLike "IIS application Example Site/ExampleApp already exists, with app pool 'OtherPool' (this run: -AppPoolName 'ExamplePool')*Nothing was deployed." -Because "a $mode run must not move the application into another pool"
                $pool.Calls | Should -BeExactly $read -Because 'the refusal comes before anything is changed'

                $folder = & $run 'ExamplePool|D:\example\other' $mode $false
                $folder.Thrown | Should -BeLike "IIS application Example Site/ExampleApp already exists, with files at 'D:\example\other' (this run: -PublishPath 'D:\example\publish')*Nothing was deployed." -Because "a $mode run must not move the application to another folder"
                $folder.Calls | Should -BeExactly $read

                $moved = & $run 'OtherPool|D:\example\other' $mode $true
                $moved.Thrown | Should -BeNullOrEmpty -Because '-AllowRepoint is the explicit way to move the application'
                $moved.Calls | Should -BeExactly $read -Because 'the move is the fresh-install branch''s, later; this only reads'
                $moved.Printed | Should -BeLike "*app pool 'OtherPool'*files at 'D:\example\other'*-AllowRepoint: this run MOVES it*"

                $absent = & $run 'absent' $mode $false
                $absent.Thrown | Should -BeNullOrEmpty -Because 'with no application there is nothing to move'
                $absent.Calls | Should -BeExactly $read
                $absent.Printed | Should -BeLike '*No IIS application Example Site/ExampleApp yet*'

                $unreadable = & $run 'throws' $mode $true
                $unreadable.Thrown | Should -BeLike 'Cannot read IIS application Example Site/ExampleApp (Cannot read configuration file due to insufficient permissions)*Nothing was deployed.' -Because 'a run that cannot read the application cannot show it does not move it, -AllowRepoint or not'
                $unreadable.Calls | Should -BeExactly $read
            }

            # cdt-42 checks an upgrade too, and its own tests cover what it refuses there. Here: the same
            # values pass, and the read is still the one call.
            $upgrade = & $run 'ExamplePool|D:\example\publish' 'upgrade' $false
            $upgrade.Thrown | Should -BeNullOrEmpty -Because 'an upgrade of the application IIS serves from this pool and folder proceeds'
            $upgrade.Calls | Should -BeExactly $read -Because 'an upgrade reads the application before anything as well (cdt-42), and changes nothing here'
        } finally {
            Remove-Module AppPoolControl -Force -ErrorAction SilentlyContinue
        }
    }

    It 'checks the application at script level, after the consent check and before anything is configured, and nothing catches its refusal (cdt-36, AST)' {
        $statements = @($s.Ast.EndBlock.Statements)
        $find = { param($test) for ($i = 0; $i -lt $statements.Count; $i++) { if (& $test $statements[$i]) { $i } } }

        # The step line, the variable's reset, the read and the assertion: four consecutive script-level
        # statements, under no condition - since cdt-42 every run checks, upgrades included.
        $stepAt = @(& $find { param($st) $st.Extent.Text -eq 'Write-Step "Checking IIS application $ParentSite/$AppAlias"' })
        $stepAt.Count | Should -Be 1 -Because 'the application is checked once, at script level'
        $check = $stepAt
        $readAt = @(& $find { param($st) $st -is [System.Management.Automation.Language.TryStatementAst] -and $st.Body.Extent.Text -match ([regex]::Escape('$existingApplication = Get-WebApplication -Name $AppAlias -Site $ParentSite -ErrorAction Stop')) })
        $readAt.Count | Should -Be 1 -Because 'the application is read once, at script level'
        $assertAt = @(& $find { param($st) $st.Extent.Text -like 'Assert-ApplicationTargetUnchanged *' })
        $assertAt.Count | Should -Be 1 -Because 'and checked once, at script level'
        $statements[$stepAt[0] + 1].Extent.Text | Should -BeExactly '$existingApplication = $null'
        $readAt[0] | Should -Be ($stepAt[0] + 2)
        $assertAt[0] | Should -Be ($readAt[0] + 1) -Because 'nothing runs between the read and the check'

        $read = $statements[$readAt[0]]
        $read.CatchClauses.Count | Should -Be 1
        $read.Finally | Should -BeNullOrEmpty
        @($read.CatchClauses[0].Body.Statements).Count | Should -Be 1
        $read.CatchClauses[0].Body.Statements[0].Extent.Text | Should -BeLike 'Write-Fail "Cannot read IIS application $ParentSite/$AppAlias *' -Because 'a lookup that fails refuses the run'

        $asserts = @($s.Ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Assert-ApplicationTargetUnchanged' }, $true))
        $asserts.Count | Should -Be 1 -Because 'one call, in the script itself'
        $assert = $asserts[0]
        [object]::ReferenceEquals($assert.Parent.Parent, $s.Ast.EndBlock) | Should -BeTrue -Because 'a statement of the script itself: nothing conditional or catching sits around it, so every run checks (cdt-42)'
        foreach ($pair in @(
                @('Location', '"$ParentSite/$AppAlias"'),
                @('Exists', '($null -ne $existingApplication)'),
                @('ExistingPool', '"$($existingApplication.applicationPool)"'),
                @('ExistingPhysicalPath', '"$($existingApplication.PhysicalPath)"'),
                @('AppPoolName', '$AppPoolName'),
                @('PublishPath', '$PublishPath'),
                @('AllowRepoint', '$AllowRepoint'),
                @('Upgrade', '$isUpgrade'))) {
            Get-NamedArgumentText $assert $pair[0] | Should -BeExactly $pair[1] -Because "-$($pair[0]) carries what IIS reported or what this run names"
        }

        # In order: the consent check and the banner, this statement, then everything that prompts,
        # reads the pool or configures anything.
        $consent = @(& $find { param($st) $st -is [System.Management.Automation.Language.IfStatementAst] -and $st.Clauses[0].Item1.Extent.Text -eq '-not $isUpgrade -and -not $Force -and -not $ConfirmFreshInstall' })
        $banner = @(& $find { param($st) $st.Extent.Text -eq 'Write-Host "  App     : $ParentSite/$AppAlias" -ForegroundColor DarkGray' })
        $collect = @(& $find { param($st) $st -is [System.Management.Automation.Language.IfStatementAst] -and $st.Extent.Text -match 'Write-Step "Collecting configuration values"' })
        $poolRead = @(& $find { param($st) $st.Extent.Text -like '$poolExists = *' })
        $changes = @(& $find { param($st)
                -not ($st -is [System.Management.Automation.Language.FunctionDefinitionAst]) -and
                $null -ne $st.Find({ param($n) $n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -in @('New-WebAppPool', 'Set-ItemProperty', 'Set-AclChecked', 'New-WebApplication', 'Set-WebConfigurationProperty', 'Remove-WebConfigurationProperty', 'robocopy', 'dotnet', 'Stop-AppPoolVerified') }, $true) })
        $modeTry = @(& $find { param($st) $st -is [System.Management.Automation.Language.TryStatementAst] -and $null -ne $st.Find({ param($n) $n -is [System.Management.Automation.Language.IfStatementAst] -and $n.Clauses[0].Item1.Extent.Text -eq '$isUpgrade' }, $true) })
        foreach ($anchor in @(@('consent check', $consent), @('banner', $banner), @('config collection', $collect), @('pool read', $poolRead), @('mode branch', $modeTry))) {
            @($anchor[1]).Count | Should -Be 1 -Because "the $($anchor[0]) is one script-level statement"
        }
        $changes.Count | Should -BeGreaterThan 0

        $consent[0] | Should -BeLessThan $check[0] -Because 'an unconsented fresh install is still refused by the consent check first'
        $banner[0] | Should -BeLessThan $check[0]
        $assertAt[0] | Should -BeLessThan $collect[0] -Because 'a run that would move the application is refused before anything is prompted for'
        $assertAt[0] | Should -BeLessThan $poolRead[0] -Because 'and before the pool it names is read (cdt-42)'
        $assertAt[0] | Should -BeLessThan $changes[0] -Because 'it refuses before IIS, an ACL or the publish folder is touched - on an upgrade too (cdt-42)'
        $assertAt[0] | Should -BeLessThan $modeTry[0]

        # cdt-45: and before the run writes anything at all. The backup folder and the pre-force copy
        # used to come before the consent check; they follow the check now, and nothing above the
        # check's step line runs a command that writes.
        $backupFolder = @(& $find { param($st) $st -is [System.Management.Automation.Language.IfStatementAst] -and $st.Clauses[0].Item1.Extent.Text -eq '-not (Test-Path $BackupDir)' })
        $preForceCopy = @(& $find { param($st) $st -is [System.Management.Automation.Language.IfStatementAst] -and $st.Extent.Text -match ([regex]::Escape('"appsettings.${timestamp}.pre-force.bak"')) })
        $backupFolder.Count | Should -Be 1 -Because 'the backup folder is created by one script-level statement'
        $preForceCopy.Count | Should -Be 1 -Because 'the pre-force copy is one script-level statement'
        $assertAt[0] | Should -BeLessThan $backupFolder[0] -Because 'a refused run creates no backup folder (cdt-45)'
        $assertAt[0] | Should -BeLessThan $preForceCopy[0] -Because 'a refused run copies no appsettings.json (cdt-45)'
        $writers = @('New-Item', 'Copy-Item', 'Move-Item', 'Remove-Item', 'Set-Content', 'Add-Content', 'Out-File', 'Set-ItemProperty', 'New-WebAppPool', 'New-WebApplication', 'Set-WebConfigurationProperty', 'Remove-WebConfigurationProperty', 'Set-AclChecked', 'icacls', 'robocopy', 'dotnet', 'Backup-SqliteConfigDb', 'Write-AppSettingsFile')
        $earlyWrites = @(for ($i = 0; $i -lt $check[0]; $i++) {
                $st = $statements[$i]
                if ($st -is [System.Management.Automation.Language.FunctionDefinitionAst]) { continue }
                foreach ($command in @($st.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true))) {
                    if ($command.GetCommandName() -in $writers) { "line $($command.Extent.StartLineNumber): $($command.Extent.Text)" }
                }
            })
        $earlyWrites | Should -BeNullOrEmpty -Because 'nothing may be written before the check has read the application (cdt-45)'

        $switch = @($s.Ast.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'AllowRepoint' })
        $switch.Count | Should -Be 1
        $switch[0].StaticType | Should -Be ([System.Management.Automation.SwitchParameter]) -Because 'a move is opted into by name, never defaulted'
    }

    It 'refuses an upgrade whose pool or folder does not serve the application, -AllowRepoint or not (cdt-42)' {
        # An upgrade never moves the application, but it configures the pool and the folders it names
        # before its own binding check, so on an upgrade every difference refuses: the switch that
        # consents to a fresh install's move cannot make an upgrade right.
        foreach ($name in 'Write-Success', 'Write-Warn', 'Write-Fail', 'Assert-ApplicationTargetUnchanged') {
            . ([scriptblock]::Create((Find-FunctionDefinition $s $name).Extent.Text))
        }
        Import-Module (Join-Path $RepoRoot 'tools/AppPoolControl.psm1') -Force
        try {
            ${function:Write-Host} = { param([Parameter(Position = 0)]$Object, $ForegroundColor) $null = $ForegroundColor; $script:cdt42Printed.Add("$Object") }
            $check = {
                param([bool]$Exists, [string]$Pool, [string]$Folder, [bool]$Allow)
                $script:cdt42Printed = New-Object System.Collections.Generic.List[string]
                $thrown = $null
                try {
                    Assert-ApplicationTargetUnchanged -Location 'Example Site/ExampleApp' -Exists $Exists -ExistingPool $Pool -ExistingPhysicalPath $Folder -AppPoolName 'ExamplePool' -PublishPath 'D:\example\publish' -AllowRepoint:$Allow -Upgrade
                } catch {
                    $thrown = $_.Exception.Message
                }
                [pscustomobject]@{ Thrown = $thrown; Printed = ($script:cdt42Printed -join "`n") }
            }

            (& $check $false '' '' $false).Thrown | Should -BeNullOrEmpty -Because 'an upgrade whose application is missing creates it later, as before'
            $same = & $check $true 'examplepool' 'd:\EXAMPLE\publish\' $false
            $same.Thrown | Should -BeNullOrEmpty -Because 'the same pool and folder, however spelled, is the application this upgrade deploys to'
            $same.Printed | Should -BeLike '*deployed in place*'

            $cases = @(
                @{ Case = 'another pool'; Pool = 'OtherPool'; Folder = 'D:\example\publish'; Expect = "app pool 'OtherPool' (this run: -AppPoolName 'ExamplePool')" }
                @{ Case = 'another folder'; Pool = 'ExamplePool'; Folder = 'D:\example\other'; Expect = "files at 'D:\example\other' (this run: -PublishPath 'D:\example\publish')" }
                @{ Case = 'a folder that only starts with the run''s'; Pool = 'ExamplePool'; Folder = 'D:\example\publishOld'; Expect = "files at 'D:\example\publishOld'" }
                @{ Case = 'no folder'; Pool = 'ExamplePool'; Folder = ''; Expect = "files at '' (this run: -PublishPath 'D:\example\publish')" }
            )
            foreach ($c in $cases) {
                foreach ($allow in $false, $true) {
                    $r = & $check $true $c.Pool $c.Folder $allow
                    $r.Thrown | Should -BeLike "IIS application Example Site/ExampleApp already exists, with *$($c.Expect)*An upgrade configures the app pool and the folders it names before it deploys, and never moves the application*Re-run with -AppPoolName '$($c.Pool)' -PublishPath '$($c.Folder)', or move the application in IIS first.*Nothing was deployed." -Because "an upgrade must not configure a pool or folder that does not serve the application ($($c.Case), -AllowRepoint $allow)"
                    if ($allow) {
                        $r.Thrown | Should -BeLike '*-AllowRepoint does not apply: only a -Force or -ConfirmFreshInstall run re-points an application.*'
                    } else {
                        $r.Thrown | Should -Not -BeLike '*-AllowRepoint does not apply*'
                    }
                    $r.Printed | Should -Not -BeLike '*MOVES it*' -Because 'an upgrade never moves the application'
                }
            }
        } finally {
            Remove-Module AppPoolControl -Force -ErrorAction SilentlyContinue
        }
    }

    It 'runs an upgrade''s real steps from the check through the ACL grants: a pool or folder that does not serve the application is refused before the pool is configured or any folder is granted (cdt-42)' {
        # The upgrade path configured the pool -AppPoolName names and granted its account Modify on this
        # instance's folders, and only then, inside its branch, asked IIS which pool serves the
        # application. The real statements run here in order, from the one after the banner through the
        # last grant, with IIS, the pool provider, the certificate store, the folders and the ACL helper
        # stubbed, each call recorded.
        foreach ($name in 'Write-Step', 'Write-Success', 'Write-Warn', 'Write-Fail', 'Prompt-Value', 'Prompt-SecureValue', 'SecureString-ToPlain', 'Assert-ApplicationTargetUnchanged') {
            . ([scriptblock]::Create((Find-FunctionDefinition $s $name).Extent.Text))
        }
        $statements = @($s.Ast.EndBlock.Statements)
        $from = @(for ($i = 0; $i -lt $statements.Count; $i++) { if ($statements[$i].Extent.Text -eq 'Write-Host "  App     : $ParentSite/$AppAlias" -ForegroundColor DarkGray') { $i } })
        $to = @(for ($i = 0; $i -lt $statements.Count; $i++) { if ($statements[$i].Extent.Text -eq 'Write-Success "Config folder ACL set: $configFolder"') { $i } })
        $from.Count | Should -Be 1 -Because 'the banner names the application once'
        $to.Count | Should -Be 1 -Because 'the last grant reports once'
        $region = ($statements[($from[0] + 1)..$to[0]] | ForEach-Object { $_.Extent.Text }) -join "`n"
        $region | Should -Match ([regex]::Escape('Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name managedRuntimeVersion')) -Because 'the region holds the pool step'
        $region | Should -Match ([regex]::Escape('Set-AclChecked $configFolder $ServiceAccount')) -Because 'and the grants'

        Import-Module (Join-Path $RepoRoot 'tools/AppPoolControl.psm1') -Force
        try {
            ${function:Write-Host} = { param([Parameter(Position = 0)]$Object, $ForegroundColor) $null = $ForegroundColor; $script:cdt42Printed.Add("$Object") }
            ${function:Get-WebApplication} = {
                param($Name, $Site, $ErrorAction)
                $null = $ErrorAction
                $script:cdt42Calls.Add("lookup($Site/$Name)")
                if ($script:cdt42Iis -eq 'throws') { throw 'Cannot read configuration file due to insufficient permissions' }
                if ($script:cdt42Iis -eq 'absent') { return }
                $pool, $folder = $script:cdt42Iis -split '\|', 2
                [pscustomobject]@{ applicationPool = $pool; PhysicalPath = $folder }
            }
            # The pool the run names exists and runs as a user - another instance's pool would - and
            # every folder exists, so nothing is created.
            ${function:Test-Path} = { param([Parameter(Position = 0)]$Path) $script:cdt42Calls.Add("exists($Path)"); $true }
            ${function:Get-ItemProperty} = { param([Parameter(Position = 0)]$Path, $Name) $script:cdt42Calls.Add("read($Path|$Name)"); [pscustomobject]@{ identityType = 'SpecificUser'; userName = 'EXAMPLE\svc-other' } }
            ${function:New-WebAppPool} = { param($Name) $script:cdt42Calls.Add("New-WebAppPool($Name)") }
            ${function:Set-ItemProperty} = { param([Parameter(Position = 0)]$Path, $Name, $Value) $null = $Value; $script:cdt42Calls.Add("set($Path|$Name)") }
            ${function:Get-ChildItem} = { param([Parameter(Position = 0)]$Path) $script:cdt42Calls.Add("certs($Path)") }
            ${function:New-Item} = { $script:cdt42Calls.Add('New-Item') }
            ${function:Set-AclChecked} = { param([Parameter(Position = 0)]$Path, [Parameter(Position = 1)]$Identity, [Parameter(Position = 2)]$Rights) $script:cdt42Calls.Add("grant($Path|$Identity|$Rights)") }

            $run = {
                param([string]$Iis, [bool]$Allow)
                $script:cdt42Iis = $Iis
                $script:cdt42Calls = New-Object System.Collections.Generic.List[string]
                $script:cdt42Printed = New-Object System.Collections.Generic.List[string]
                # Read only by the lifted statements: an upgrade - appsettings.json present, no -Force.
                Set-Variable -Name isUpgrade -Value $true
                Set-Variable -Name Force -Value ([switch]$false)
                Set-Variable -Name ConfirmFreshInstall -Value ([switch]$false)
                Set-Variable -Name AllowRepoint -Value ([switch]$Allow)
                Set-Variable -Name NonInteractive -Value ([switch]$true)
                Set-Variable -Name ParentSite -Value 'Example Site'
                Set-Variable -Name AppAlias -Value 'ExampleApp'
                Set-Variable -Name AppPoolName -Value 'ExamplePool'
                Set-Variable -Name PublishPath -Value 'D:\example\publish'
                Set-Variable -Name LogRoot -Value 'D:\example\logs'
                Set-Variable -Name CertSubject -Value 'CN=Example'
                Set-Variable -Name ServiceAccount -Value $null
                Set-Variable -Name ServiceAccountPassword -Value $null
                $ErrorActionPreference = 'Stop'
                $thrown = $null
                try { . ([scriptblock]::Create($region)) } catch { $thrown = $_.Exception.Message }
                [pscustomobject]@{ Thrown = $thrown; Calls = @($script:cdt42Calls); Printed = ($script:cdt42Printed -join "`n") }
            }
            $read = 'lookup(Example Site/ExampleApp)'

            $same = & $run 'ExamplePool|D:\example\publish' $false
            $same.Thrown | Should -BeNullOrEmpty -Because 'the pool and folder IIS serves the application from are the ones this upgrade names'
            $same.Calls[0] | Should -BeExactly $read -Because 'the application is read before anything else'
            $same.Calls | Should -Contain 'set(IIS:\AppPools\ExamplePool|managedRuntimeVersion)' -Because 'the pool step runs once the check passes'
            $same.Calls | Should -Contain 'grant(D:\example\publish\config|EXAMPLE\svc-other|(OI)(CI)M)' -Because 'and so do the grants'

            $cases = @(
                @{ Case = 'another existing pool that runs as a user'; Iis = 'OtherPool|D:\example\publish'; Allow = $false; Expect = "IIS application Example Site/ExampleApp already exists, with app pool 'OtherPool' (this run: -AppPoolName 'ExamplePool')*An upgrade configures*" }
                @{ Case = 'another folder'; Iis = 'ExamplePool|D:\example\other'; Allow = $false; Expect = "IIS application Example Site/ExampleApp already exists, with files at 'D:\example\other' (this run: -PublishPath 'D:\example\publish')*An upgrade configures*" }
                @{ Case = 'another pool, with -AllowRepoint'; Iis = 'OtherPool|D:\example\publish'; Allow = $true; Expect = '*An upgrade configures*-AllowRepoint does not apply*' }
                @{ Case = 'an application IIS cannot read'; Iis = 'throws'; Allow = $false; Expect = 'Cannot read IIS application Example Site/ExampleApp (Cannot read configuration file due to insufficient permissions)*' }
            )
            foreach ($c in $cases) {
                $r = & $run $c.Iis $c.Allow
                $r.Thrown | Should -BeLike "$($c.Expect)Nothing was deployed." -Because "an upgrade must be refused before it changes anything ($($c.Case))"
                ($r.Calls -join ',') | Should -BeExactly $read -Because "the read is the only call - no pool read, set or creation, no folder, no grant ($($c.Case))"
            }

            $absent = & $run 'absent' $false
            $absent.Thrown | Should -BeNullOrEmpty -Because 'an upgrade whose application is missing still goes on to create it, as before'
            $absent.Calls[0] | Should -BeExactly $read
            $absent.Calls | Should -Contain 'set(IIS:\AppPools\ExamplePool|startMode)'
        } finally {
            Remove-Module AppPoolControl -Force -ErrorAction SilentlyContinue
        }
    }

    It 'runs a -Force run''s real steps from the check through the pre-force copy: a run the check refuses creates no backup folder and copies nothing (cdt-45)' {
        # The backup folder was created, and appsettings.json copied into it, before the check that
        # then refused the run. The real statements run here in order, from the one after the banner
        # through the pre-force copy, with IIS, the folders and the copy stubbed, each call recorded.
        foreach ($name in 'Write-Step', 'Write-Success', 'Write-Warn', 'Write-Fail', 'Assert-ApplicationTargetUnchanged') {
            . ([scriptblock]::Create((Find-FunctionDefinition $s $name).Extent.Text))
        }
        $statements = @($s.Ast.EndBlock.Statements)
        $from = @(for ($i = 0; $i -lt $statements.Count; $i++) { if ($statements[$i].Extent.Text -eq 'Write-Host "  App     : $ParentSite/$AppAlias" -ForegroundColor DarkGray') { $i } })
        $to = @(for ($i = 0; $i -lt $statements.Count; $i++) { if ($statements[$i] -is [System.Management.Automation.Language.IfStatementAst] -and $statements[$i].Extent.Text -match ([regex]::Escape('"appsettings.${timestamp}.pre-force.bak"'))) { $i } })
        $from.Count | Should -Be 1
        $to.Count | Should -Be 1 -Because 'one script-level statement takes the pre-force copy'
        $to[0] | Should -BeGreaterThan $from[0]
        $region = ($statements[($from[0] + 1)..$to[0]] | ForEach-Object { $_.Extent.Text }) -join "`n"

        Import-Module (Join-Path $RepoRoot 'tools/AppPoolControl.psm1') -Force
        try {
            ${function:Write-Host} = { param([Parameter(Position = 0)]$Object, $ForegroundColor) $null = $ForegroundColor; $script:cdt45Printed.Add("$Object") }
            ${function:Get-WebApplication} = {
                param($Name, $Site, $ErrorAction)
                $null = $ErrorAction
                $script:cdt45Calls.Add("lookup($Site/$Name)")
                $pool, $folder = $script:cdt45Iis -split '\|', 2
                [pscustomobject]@{ applicationPool = $pool; PhysicalPath = $folder }
            }
            # The backup folder does not exist yet; the run's appsettings.json does.
            ${function:Test-Path} = { param([Parameter(Position = 0)]$Path) $script:cdt45Calls.Add("exists($Path)"); $Path -like '*appsettings.json' }
            ${function:New-Item} = { param($ItemType, $Path, [switch]$Force) $null = $ItemType, $Force; $script:cdt45Calls.Add("New-Item($Path)") }
            ${function:Copy-Item} = { param([Parameter(Position = 0)]$Path, [Parameter(Position = 1)]$Destination) $script:cdt45Calls.Add("Copy-Item($Path|$Destination)") }

            $run = {
                param([string]$Iis)
                $script:cdt45Iis = $Iis
                $script:cdt45Calls = New-Object System.Collections.Generic.List[string]
                $script:cdt45Printed = New-Object System.Collections.Generic.List[string]
                # Read only by the lifted statements: -Force over an existing deployment.
                Set-Variable -Name isUpgrade -Value $false
                Set-Variable -Name Force -Value ([switch]$true)
                Set-Variable -Name ConfirmFreshInstall -Value ([switch]$false)
                Set-Variable -Name AllowRepoint -Value ([switch]$false)
                Set-Variable -Name ParentSite -Value 'Example Site'
                Set-Variable -Name AppAlias -Value 'ExampleApp'
                Set-Variable -Name AppPoolName -Value 'ExamplePool'
                Set-Variable -Name PublishPath -Value 'D:\example\publish'
                Set-Variable -Name configPath -Value 'D:\example\publish\appsettings.json'
                Set-Variable -Name LogRoot -Value 'D:\example\logs'
                Set-Variable -Name timestamp -Value '20261023000000'
                $ErrorActionPreference = 'Stop'
                $thrown = $null
                try { . ([scriptblock]::Create($region)) } catch { $thrown = $_.Exception.Message }
                [pscustomobject]@{ Thrown = $thrown; Calls = ($script:cdt45Calls -join ','); Printed = ($script:cdt45Printed -join "`n") }
            }
            $read = 'lookup(Example Site/ExampleApp)'

            foreach ($iis in 'OtherPool|D:\example\publish', 'ExamplePool|D:\example\other') {
                $refused = & $run $iis
                $refused.Thrown | Should -BeLike 'IIS application Example Site/ExampleApp already exists, with *Nothing was deployed.' -Because "the check refuses this -Force run ($iis)"
                $refused.Calls | Should -BeExactly $read -Because "a refused run creates no backup folder and copies nothing ($iis)"
            }

            $same = & $run 'ExamplePool|D:\example\publish'
            $same.Thrown | Should -BeNullOrEmpty
            $same.Calls | Should -BeExactly ($read + ',exists(D:\example\logs\ExchangeAdminWeb\ConfigBackups),New-Item(D:\example\logs\ExchangeAdminWeb\ConfigBackups),exists(D:\example\publish\appsettings.json),Copy-Item(D:\example\publish\appsettings.json|D:\example\logs\ExchangeAdminWeb\ConfigBackups\appsettings.20261023000000.pre-force.bak)') -Because 'a run the check passes creates the backup folder and takes the copy, after the read'
            $same.Printed | Should -BeLike '*Existing config backed up to D:\example\logs\ExchangeAdminWeb\ConfigBackups\appsettings.20261023000000.pre-force.bak*'
        } finally {
            Remove-Module AppPoolControl -Force -ErrorAction SilentlyContinue
        }
    }

    It 'takes a -AllowRepoint move''s configuration from the folder IIS serves now, and refuses a move that would strand its database (cdt-44, real files)' {
        # The backup and the ConfigStore:Path carry-forward read -PublishPath, which in a move is only
        # where the application is going. The real script-level statements from the serving-folder
        # choice through the pre-move copy run here against temporary folders, with the real modules.
        foreach ($name in 'Write-Warn', 'Write-Fail') {
            . ([scriptblock]::Create((Find-FunctionDefinition $s $name).Extent.Text))
        }
        $statements = @($s.Ast.EndBlock.Statements)
        $from = @(for ($i = 0; $i -lt $statements.Count; $i++) { if ($statements[$i].Extent.Text -eq '$servingFolder = $PublishPath') { $i } })
        $to = @(for ($i = 0; $i -lt $statements.Count; $i++) { if ($statements[$i] -is [System.Management.Automation.Language.IfStatementAst] -and $statements[$i].Extent.Text -match ([regex]::Escape('"appsettings.${timestamp}.pre-move.bak"'))) { $i } })
        $from.Count | Should -Be 1 -Because 'one script-level statement starts the serving-folder choice'
        $to.Count | Should -Be 1 -Because 'one script-level statement takes the pre-move copy'
        $region = ($statements[$from[0]..$to[0]] | ForEach-Object { $_.Extent.Text }) -join "`n"

        Import-Module (Join-Path $RepoRoot 'tools/AppPoolControl.psm1') -Force
        Import-Module (Join-Path $RepoRoot 'tools/SqliteConfigBackup.psm1') -Force
        $root = Join-Path ([System.IO.Path]::GetTempPath()) ('cdt44_' + [guid]::NewGuid().ToString('N'))
        try {
            ${function:Write-Host} = { param([Parameter(Position = 0)]$Object, $ForegroundColor) $null = $ForegroundColor; $script:cdt44Printed.Add("$Object") }
            $case = 0
            $run = {
                param([string]$OldSettings, [bool]$OldDb, [string]$Served, [bool]$Upgrade, [bool]$Allow, [string]$Publish = '<new>')
                $script:cdt44Printed = New-Object System.Collections.Generic.List[string]
                $script:case++
                $base = Join-Path $root "case$($script:case)"
                $old = Join-Path $base 'old'
                $new = Join-Path $base 'new'
                New-Item -ItemType Directory -Path (Join-Path $old 'config'), $new, (Join-Path $base 'shared') -Force | Out-Null
                if ($OldSettings) { Set-Content -LiteralPath (Join-Path $old 'appsettings.json') -Value ($OldSettings.Replace('<shared>', (Join-Path $base 'shared\exchangeadmin.db').Replace('\', '\\'))) -Encoding UTF8 }
                if ($OldDb) { Set-Content -LiteralPath (Join-Path $old 'config\exchangeadmin.db') -Value 'db' -Encoding UTF8 }
                Set-Content -LiteralPath (Join-Path $base 'shared\exchangeadmin.db') -Value 'db' -Encoding UTF8
                # Read only by the lifted statements.
                Set-Variable -Name isUpgrade -Value $Upgrade
                Set-Variable -Name Force -Value ([switch]$true)
                Set-Variable -Name AllowRepoint -Value ([switch]$Allow)
                Set-Variable -Name existingApplication -Value ([pscustomobject]@{ applicationPool = 'ExamplePool'; PhysicalPath = $Served.Replace('<old>', $old).Replace('<new>', $new) })
                $publishFolder = $Publish.Replace('<old>', $old).Replace('<new>', $new)
                Set-Variable -Name PublishPath -Value $publishFolder
                Set-Variable -Name configPath -Value (Join-Path $publishFolder 'appsettings.json')
                Set-Variable -Name LogRoot -Value (Join-Path $base 'logs')
                Set-Variable -Name timestamp -Value '20261023000000'
                Set-Variable -Name ParentSite -Value 'Example Site'
                Set-Variable -Name AppAlias -Value 'ExampleApp'
                $ErrorActionPreference = 'Stop'
                $thrown = $null
                try { . ([scriptblock]::Create($region)) } catch { $thrown = $_.Exception.Message }
                $backups = Join-Path $base 'logs\ExchangeAdminWeb\ConfigBackups'
                [pscustomobject]@{
                    Thrown        = $thrown
                    Old           = $old
                    New           = $new
                    ServingFolder = $servingFolder
                    BackupFolder  = (Test-Path -LiteralPath $backups)
                    MoveCopy      = (Join-Path $backups 'appsettings.20261023000000.pre-move.bak')
                    Printed       = ($script:cdt44Printed -join "`n")
                }
            }
            $shared = '{ "ConfigStore": { "Path": "<shared>" }, "AllowedHosts": "*" }'
            $own = '{ "AllowedHosts": "*" }'

            $moved = & $run $shared $true '<old>' $false $true
            $moved.Thrown | Should -BeNullOrEmpty -Because 'a move whose configuration names a shared database carries it'
            $moved.ServingFolder | Should -BeExactly $moved.Old -Because 'the configuration the application runs on now is in the folder IIS serves it from'
            Test-Path -LiteralPath $moved.MoveCopy | Should -BeTrue -Because 'the appsettings.json the application runs on now is kept'
            (Get-Content -LiteralPath $moved.MoveCopy -Raw) | Should -BeLike '*"ConfigStore"*' -Because 'the copy is the old folder''s file, not the new folder''s'
            $moved.Printed | Should -BeLike "*-AllowRepoint: the configuration is backed up from '$($moved.Old)'*"

            $stranded = & $run $own $true '<old>' $false $true
            $stranded.Thrown | Should -BeLike "IIS application Example Site/ExampleApp keeps its config database inside the folder it is being moved from ('$($stranded.Old)\config\exchangeadmin.db')*names no ConfigStore:Path*Nothing was deployed." -Because 'the move would leave the database behind and open an empty one'
            $stranded.BackupFolder | Should -BeFalse -Because 'the refusal comes before anything is written'

            $empty = & $run $own $false '<old>' $false $true
            $empty.Thrown | Should -BeNullOrEmpty -Because 'an old folder with no database yet has nothing to strand'
            $empty.ServingFolder | Should -BeExactly $empty.Old
            Test-Path -LiteralPath $empty.MoveCopy | Should -BeTrue

            foreach ($outside in @(
                    @{ Case = 'the same folder'; Served = '<new>'; Upgrade = $false; Allow = $true }
                    @{ Case = 'an upgrade'; Served = '<old>'; Upgrade = $true; Allow = $true }
                    @{ Case = 'no -AllowRepoint'; Served = '<old>'; Upgrade = $false; Allow = $false }
                    @{ Case = 'no folder reported'; Served = ''; Upgrade = $false; Allow = $true })) {
                $r = & $run $shared $true $outside.Served $outside.Upgrade $outside.Allow
                $r.Thrown | Should -BeNullOrEmpty -Because "nothing is moved ($($outside.Case)), so nothing is refused here"
                $r.ServingFolder | Should -BeExactly $r.New -Because "outside a move the configuration is -PublishPath's, as before ($($outside.Case))"
                Test-Path -LiteralPath $r.MoveCopy | Should -BeFalse -Because "there is no move to keep a copy for ($($outside.Case))"
            }

            # A -AllowRepoint run that moves only the pool: IIS spells the folder with a trailing
            # separator, and the folder stays, with its appsettings.json and the database kept inside it -
            # nothing is stranded, and nothing is moved.
            $poolOnly = & $run $own $true '<old>\' $false $true '<old>'
            $poolOnly.Thrown | Should -BeNullOrEmpty -Because 'the folder keeps its database; refusing would block a pool-only move'
            $poolOnly.ServingFolder | Should -BeExactly $poolOnly.Old -Because 'the configuration is -PublishPath''s'
            Test-Path -LiteralPath $poolOnly.MoveCopy | Should -BeFalse -Because 'the folder does not move, so there is no move to keep a copy for'
        } finally {
            Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
            Remove-Module AppPoolControl -Force -ErrorAction SilentlyContinue
        }
    }

    It 'backs up, and carries ConfigStore:Path from, the serving folder in the -Force branch, not the new -PublishPath (cdt-44, real files)' {
        # The fresh-install branch's real statements from the ConfigStore:Path read through the
        # verified backup, lifted by AST and run against temporary folders with the real module; only
        # the backup tool is stubbed, recording which database it was asked to back up.
        foreach ($name in 'Write-Step', 'Write-Success', 'Write-Warn', 'Write-Fail') {
            . ([scriptblock]::Create((Find-FunctionDefinition $s $name).Extent.Text))
        }
        $branch = @((Get-FreshInstallBranch $s).Statements)
        $from = @(for ($i = 0; $i -lt $branch.Count; $i++) { if ($branch[$i].Extent.Text -like '$configStorePathSetting = *') { $i } })
        $to = @(for ($i = 0; $i -lt $branch.Count; $i++) { if ($branch[$i] -is [System.Management.Automation.Language.IfStatementAst] -and $branch[$i].Extent.Text -match 'Backup-SqliteConfigDb') { $i } })
        $from.Count | Should -Be 1
        $to.Count | Should -Be 1 -Because 'one statement of the branch takes the verified backup'
        $region = ($branch[$from[0]..$to[0]] | ForEach-Object { $_.Extent.Text }) -join "`n"

        # The rest-of-config copy in that region reads $runtimeConfigDir, set once above the branches:
        # it must be the serving folder's config\ too, not -PublishPath's.
        $configDirs = @($s.Ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.AssignmentStatementAst] -and $n.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and $n.Left.VariablePath.UserPath -eq 'runtimeConfigDir' }, $true))
        $configDirs.Count | Should -Be 1
        $configDirs[0].Right.Extent.Text | Should -BeExactly 'Join-Path $servingFolder "config"' -Because 'the live configuration directory is the serving folder''s (cdt-44)'

        Import-Module (Join-Path $RepoRoot 'tools/SqliteConfigBackup.psm1') -Force
        $root = Join-Path ([System.IO.Path]::GetTempPath()) ('cdt44b_' + [guid]::NewGuid().ToString('N'))
        try {
            ${function:Write-Host} = { param([Parameter(Position = 0)]$Object, $ForegroundColor) $null = $Object, $ForegroundColor }
            ${function:Backup-SqliteConfigDb} = {
                param($DbPath, $DestDir, $Timestamp, $AppPath)
                $null = $AppPath
                $script:cdt44Backups.Add("$DbPath|$DestDir")
                Join-Path $DestDir "exchangeadmin.$Timestamp.db"
            }
            $old = Join-Path $root 'old'
            $new = Join-Path $root 'new'
            $sharedDb = Join-Path $root 'shared\exchangeadmin.db'
            New-Item -ItemType Directory -Path (Join-Path $old 'config'), $new, (Join-Path $root 'shared'), (Join-Path $root 'backups') -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $old 'appsettings.json') -Value ('{ "ConfigStore": { "Path": "' + $sharedDb.Replace('\', '\\') + '" } }') -Encoding UTF8
            Set-Content -LiteralPath $sharedDb -Value 'db' -Encoding UTF8
            Set-Content -LiteralPath (Join-Path $old 'config\sectionaccess.json') -Value '{}' -Encoding UTF8
            Set-Content -LiteralPath (Join-Path $old 'config\exchangeadmin-jobs.db') -Value 'jobs' -Encoding UTF8

            $script:cdt44Backups = New-Object System.Collections.Generic.List[string]
            # Read only by the lifted statements: a -AllowRepoint move off $old (what the script-level
            # choice sets), with nothing in the new folder yet.
            $servingFolder = $old
            $servingConfigPath = Join-Path $old 'appsettings.json'
            $runtimeConfigDir = Join-Path $old 'config'
            $PublishPath = $new
            $configPath = Join-Path $new 'appsettings.json'
            $BackupDir = Join-Path $root 'backups'
            $timestamp = '20261023000000'
            $StagingPath = 'D:\example\staging'
            $configCopyExclusions = @('exchangeadmin.db', 'exchangeadmin.db-wal', 'exchangeadmin.db-shm', 'exchangeadmin-jobs.db', 'exchangeadmin-jobs.db-wal', 'exchangeadmin-jobs.db-shm', 'exchangeadmin-usage.db', 'exchangeadmin-usage.db-wal', 'exchangeadmin-usage.db-shm')
            $null = $servingFolder, $servingConfigPath, $runtimeConfigDir, $PublishPath, $configPath, $BackupDir, $timestamp, $StagingPath, $configCopyExclusions
            $ErrorActionPreference = 'Stop'
            . ([scriptblock]::Create($region))

            $configStorePathSetting | Should -BeExactly $sharedDb -Because 'the key carried into the regenerated appsettings.json is the one the application runs on now'
            ($script:cdt44Backups -join ';') | Should -BeExactly "$sharedDb|$(Join-Path $root 'backups\config.20261023000000.pre-force.bak')" -Because 'the verified backup is of the database the application opens now, before it is moved'
            $copied = @(Get-ChildItem -LiteralPath (Join-Path $root 'backups\config.20261023000000.pre-force.bak') -File | ForEach-Object Name)
            $copied | Should -Contain 'sectionaccess.json' -Because 'the rest of the old folder''s config\ is kept'
            $copied | Should -Not -Contain 'exchangeadmin-jobs.db' -Because 'the per-instance databases stay out of the backup (cdt-10)'
        } finally {
            Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'deploys in place when IIS spells the folder through a junction, and names a spelling it cannot resolve on disk (cdt-46, real files)' {
        # The check took one folder under two spellings for a move - a refusal, or with -AllowRepoint a
        # "MOVES it" for a folder that stays. The real function, lifted, against a temporary folder and
        # a junction to it, with the real module.
        foreach ($name in 'Write-Success', 'Write-Warn', 'Write-Fail', 'Assert-ApplicationTargetUnchanged') {
            . ([scriptblock]::Create((Find-FunctionDefinition $s $name).Extent.Text))
        }
        Import-Module (Join-Path $RepoRoot 'tools/AppPoolControl.psm1') -Force
        $root = Join-Path ([System.IO.Path]::GetTempPath()) ('cdt46d_' + [guid]::NewGuid().ToString('N'))
        $folder = Join-Path $root 'Publish Folder With A Long Name'
        $sibling = Join-Path $root 'Another Publish Folder'
        $link = Join-Path $root 'link'
        try {
            New-Item -ItemType Directory -Path $folder, $sibling -Force | Out-Null
            $null = cmd /c "mklink /J `"$link`" `"$folder`""
            Test-Path -LiteralPath $link | Should -BeTrue -Because 'the junction fixture exists'
            ${function:Write-Host} = { param([Parameter(Position = 0)]$Object, $ForegroundColor) $null = $ForegroundColor; $script:cdt46Printed.Add("$Object") }
            $check = {
                param([string]$Served, [string]$Publish, [bool]$Allow, [bool]$Upgrade)
                $script:cdt46Printed = New-Object System.Collections.Generic.List[string]
                $thrown = $null
                try {
                    Assert-ApplicationTargetUnchanged -Location 'Example Site/ExampleApp' -Exists $true -ExistingPool 'ExamplePool' -ExistingPhysicalPath $Served -AppPoolName 'ExamplePool' -PublishPath $Publish -AllowRepoint:$Allow -Upgrade:$Upgrade
                } catch {
                    $thrown = $_.Exception.Message
                }
                [pscustomobject]@{ Thrown = $thrown; Printed = ($script:cdt46Printed -join "`n") }
            }

            foreach ($mode in @(@{ Allow = $false; Upgrade = $false }, @{ Allow = $true; Upgrade = $false }, @{ Allow = $false; Upgrade = $true })) {
                $same = & $check $link $folder $mode.Allow $mode.Upgrade
                $same.Thrown | Should -BeNullOrEmpty -Because "the junction is the folder this run names (-AllowRepoint $($mode.Allow), upgrade $($mode.Upgrade))"
                $same.Printed | Should -BeLike '*deployed in place*'
                $same.Printed | Should -Not -BeLike '*MOVES it*' -Because 'nothing moves'
            }

            $missing = Join-Path $root 'missing'
            $unresolved = & $check $folder $missing $false $false
            $unresolved.Thrown | Should -BeLike "IIS application Example Site/ExampleApp already exists, with files at '$folder' (this run: -PublishPath '$missing'). A spelling that cannot be resolved on disk is not taken as the same folder: '$missing'. -Force and -ConfirmFreshInstall*Nothing was deployed." -Because 'a folder that does not exist cannot be shown to be the same one under another spelling, and the refusal says which'

            $different = & $check $folder $sibling $false $false
            $different.Thrown | Should -BeLike "IIS application Example Site/ExampleApp already exists, with files at '$folder' (this run: -PublishPath '$sibling'). -Force and -ConfirmFreshInstall*" -Because 'two folders that both exist and differ are refused as before, with no note'
        } finally {
            if (Test-Path -LiteralPath $link) { [System.IO.Directory]::Delete($link) }
            Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
            Remove-Module AppPoolControl -Force -ErrorAction SilentlyContinue
        }
    }

    It 'reports the active jobs of the instance a -AllowRepoint move takes down before the re-point, and checks the stopped pool''s own (cdt-49)' {
        # The one check came after the re-point - which already took the instance IIS ran down - and read
        # the serving folder whatever pool the stop then acted on. The fresh-install branch's real
        # statements from the move report through the check before the stop, with IIS and the jobs
        # check stubbed, each call recorded in order.
        foreach ($name in 'Write-Step', 'Write-Success', 'Write-Warn', 'Write-Fail') {
            . ([scriptblock]::Create((Find-FunctionDefinition $s $name).Extent.Text))
        }
        $branch = @((Get-FreshInstallBranch $s).Statements)
        $from = @(for ($i = 0; $i -lt $branch.Count; $i++) { if ($branch[$i].Extent.Text -like '$movedOffPool = *') { $i } })
        # Up to the statement right before the binding read, whatever the check before the stop reads.
        $bound = @(for ($i = 0; $i -lt $branch.Count; $i++) { if ($branch[$i].Extent.Text -like '$boundPool = *') { $i } })
        $from.Count | Should -Be 1 -Because 'one statement of the branch starts the move report'
        $bound.Count | Should -Be 1
        $to = @($bound[0] - 1)
        $branch[$to[0]].Extent.Text | Should -Match 'Assert-NoActiveBulkJobsBeforeRecycle' -Because 'the jobs of the pool that is stopped are checked right before its binding is read'
        $region = ($branch[$from[0]..$to[0]] | ForEach-Object { $_.Extent.Text }) -join "`n"

        Import-Module (Join-Path $RepoRoot 'tools/AppPoolControl.psm1') -Force
        try {
            ${function:Write-Host} = { param([Parameter(Position = 0)]$Object, $ForegroundColor) $null = $ForegroundColor; $script:cdt49Printed.Add("$Object") }
            ${function:Get-Website} = { param($Name, $ErrorAction) $null = $ErrorAction; [pscustomobject]@{ name = $Name } }
            ${function:Get-WebApplication} = { param($Name, $Site, $ErrorAction) $null = $Name, $Site, $ErrorAction; $script:cdt49App }
            ${function:Set-ItemProperty} = { param([Parameter(Position = 0)]$Path, $Name, $Value) $null = $Path, $Value; $script:cdt49Calls.Add("set($Name)") }
            ${function:Test-Path} = { param([Parameter(Position = 0)]$Path) $null = $Path; $true }
            ${function:New-Item} = { $script:cdt49Calls.Add('New-Item') }
            ${function:New-WebApplication} = { $script:cdt49Calls.Add('New-WebApplication') }
            ${function:Set-WebConfigurationProperty} = { $script:cdt49Calls.Add('auth') }
            ${function:Remove-WebConfigurationProperty} = { $script:cdt49Calls.Add('auth') }
            ${function:Assert-NoActiveBulkJobsBeforeRecycle} = { param($ConfigDir, $AppPath) $null = $AppPath; $script:cdt49Calls.Add("jobs($ConfigDir)"); 0 }

            $run = {
                param([string]$OldPool, [string]$OldFolder, [string]$Pool, [bool]$Allow, [bool]$Exists)
                $script:cdt49Calls = New-Object System.Collections.Generic.List[string]
                $script:cdt49Printed = New-Object System.Collections.Generic.List[string]
                $app = $null
                if ($Exists) { $app = [pscustomobject]@{ applicationPool = $OldPool; PhysicalPath = $OldFolder } }
                $script:cdt49App = $app
                $publish = 'D:\example\publish'
                $moves = $Allow -and $Exists -and -not (Test-SamePhysicalFolder -Path $OldFolder -Other $publish)
                $serving = $publish
                if ($moves) { $serving = $OldFolder }
                # Read only by the lifted statements: what the script level set before the branch.
                Set-Variable -Name AllowRepoint -Value ([switch]$Allow)
                Set-Variable -Name existingApplication -Value $app
                Set-Variable -Name movesFolder -Value $moves
                Set-Variable -Name servingFolder -Value $serving
                Set-Variable -Name runtimeConfigDir -Value (Join-Path $serving 'config')
                Set-Variable -Name PublishPath -Value $publish
                Set-Variable -Name AppPoolName -Value $Pool
                Set-Variable -Name ParentSite -Value 'Example Site'
                Set-Variable -Name AppAlias -Value 'ExampleApp'
                Set-Variable -Name StagingPath -Value 'D:\example\staging'
                $ErrorActionPreference = 'Stop'
                $thrown = $null
                try { . ([scriptblock]::Create($region)) } catch { $thrown = $_.Exception.Message }
                [pscustomobject]@{ Thrown = $thrown; Calls = @($script:cdt49Calls | Where-Object { $_ -ne 'auth' }); Printed = ($script:cdt49Printed -join "`n") }
            }

            $both = & $run 'OldPool' 'D:\example\old' 'NewPool' $true $true
            $both.Thrown | Should -BeNullOrEmpty
            ($both.Calls -join ',') | Should -BeExactly 'jobs(D:\example\old\config),set(physicalPath),set(applicationPool),jobs(D:\example\publish\config)' -Because 'the instance the move takes down is reported before the re-point, and the pool that is stopped is checked from its own folder'
            $both.Printed | Should -BeLike "*Active bulk jobs of the instance IIS runs today, under app pool 'OldPool' with its files at 'D:\example\old': re-pointing the application interrupts them.*Active bulk jobs of app pool 'NewPool', with its files at 'D:\example\publish', which is stopped next:*" -Because 'each check says which instance it read'

            $poolOnly = & $run 'OldPool' 'D:\example\publish' 'NewPool' $true $true
            ($poolOnly.Calls -join ',') | Should -BeExactly 'jobs(D:\example\publish\config),set(physicalPath),set(applicationPool)' -Because 'a pool-only move reports the instance once, before the re-point; the stopped pool now serves the same folder'
            $poolOnly.Printed | Should -BeLike "*under app pool 'OldPool' with its files at 'D:\example\publish': re-pointing*"

            $inPlace = & $run 'ExamplePool' 'D:\example\publish' 'ExamplePool' $false $true
            ($inPlace.Calls -join ',') | Should -BeExactly 'set(physicalPath),set(applicationPool),jobs(D:\example\publish\config)' -Because 'a run that moves nothing checks once, before the stop, as before'

            $fresh = & $run '' '' 'ExamplePool' $false $false
            ($fresh.Calls -join ',') | Should -BeExactly 'New-WebApplication,jobs(D:\example\publish\config)' -Because 'a fresh install checks once, before the stop'
        } finally {
            Remove-Module AppPoolControl -Force -ErrorAction SilentlyContinue
        }
    }

    It 'backs up the runtime config directory before the upgrade mirror (incident fix #4)' {
        $upgradeBlock = [regex]::Match(
            $s.Text,
            '(?s)# --- UPGRADE ---.*?# --- FRESH INSTALL ---'
        ).Value

        # The live SQLite DB must be backed up via the verified online backup helper, NOT a raw
        # recursive Copy-Item (a torn copy of a live WAL DB is a worthless rollback snapshot).
        $upgradeBlock | Should -Match 'Backup-SqliteConfigDb' `
            -Because 'the live config DB needs a consistent, integrity-verified backup (SQLite Phase D)'
        $upgradeBlock | Should -Not -Match 'Copy-Item \$runtimeConfigDir \$configDirBackup -Recurse' `
            -Because 'a raw recursive copy of a live WAL DB can be inconsistent - replaced by the online backup'
        $upgradeBlock | Should -Match 'config\.\$\{timestamp\}\.bak' `
            -Because 'the snapshot must be timestamped and retained like appsettings backups'
        $upgradeBlock.IndexOf('Backup-SqliteConfigDb') |
            Should -BeLessThan $upgradeBlock.IndexOf('robocopy') `
            -Because 'the snapshot must be taken before any files change'
    }

    It 'backs up the config DB from the path appsettings resolves, and stops when a configured path is missing (scd-1)' {
        # SharedConfigDb-Plan AC6: with ConfigStore:Path set the backup (and the post-deploy
        # check) must target that file - the one shared with the other instance - not the
        # publish folder's config\. A configured path naming a missing file is fatal BEFORE
        # anything is mirrored; without the key a missing DB is still just "nothing to back up".
        $upgradeBlock = [regex]::Match(
            $s.Text,
            '(?s)# --- UPGRADE ---.*?# --- FRESH INSTALL ---'
        ).Value

        $upgradeBlock | Should -Match 'Get-ConfigStorePathSetting -PublishPath \$PublishPath'
        $upgradeBlock | Should -Match 'Resolve-ConfigDbPath -PublishPath \$PublishPath'
        $upgradeBlock | Should -Match 'Backup-SqliteConfigDb -DbPath \$configDbPath' `
            -Because 'the backup must use the resolved path'
        $upgradeBlock | Should -Not -Match 'Backup-SqliteConfigDb -ConfigDir' `
            -Because 'the ConfigDir form would back up a stale per-instance file'
        $upgradeBlock | Should -Match '(?s)if \(\$configStorePathSetting -and -not \(Test-IsSqliteConfigDbPresent -DbPath \$configDbPath\)\) \{\s*Write-Fail' `
            -Because 'key set + file absent must Write-Fail (throw)'
        $upgradeBlock.IndexOf('Write-Fail "ConfigStore:Path') |
            Should -BeLessThan $upgradeBlock.IndexOf('robocopy') `
            -Because 'the check must run before any files change'
        $upgradeBlock | Should -Match 'nothing to back up' `
            -Because 'the no-key, no-DB case keeps the warning posture'

        # The post-deploy health check resolves the same way (never the hardcoded config\ path).
        $upgradeBlock | Should -Match '\$liveDb = Resolve-ConfigDbPath -PublishPath \$PublishPath'
        $upgradeBlock | Should -Not -Match 'Join-Path \$PublishPath "config\\exchangeadmin\.db"'
    }

    It 'runs the db tool from the binary it is about to deploy, resolved before anything changes (ConfigDbToolInApp-Plan S2)' {
        # AC4: a staging folder without the app binary stops the deploy, naming the path,
        # before any backup or mirror. The backup and the job check run that same binary.
        $resolveAt = $s.Text.IndexOf('Resolve-ConfigDbTool -AppPath $StagingPath')
        $resolveAt | Should -BeGreaterThan $s.Text.IndexOf('dotnet publish -c Release -o $StagingPath')
        $resolveAt | Should -BeLessThan $s.Text.IndexOf('# --- Mode-specific deployment ---')

        $upgradeBlock = [regex]::Match($s.Text, '(?s)# --- UPGRADE ---.*?# --- FRESH INSTALL ---').Value
        $upgradeBlock | Should -Match 'Backup-SqliteConfigDb -DbPath \$configDbPath -DestDir \$configDirBackup -Timestamp \$timestamp -AppPath \$StagingPath'
        $upgradeBlock | Should -Match 'Assert-NoActiveBulkJobsBeforeRecycle -ConfigDir \$runtimeConfigDir -AppPath \$StagingPath'
        # Staging is removed in the finally that restarts the pool, so the post-deploy check
        # runs the same binary from where it now lives.
        $upgradeBlock | Should -Match 'Test-SqliteConfigDbIntegrity -DbPath \$liveDb -AppPath \$PublishPath'
    }

    It 'sets $runtimeConfigDir unconditionally before any use, so the fresh-install jobs check gets a path (cdt-5)' {
        $isRuntimeConfigDir = {
            param($node)
            $node -is [System.Management.Automation.Language.VariableExpressionAst] -and
            $node.VariablePath.UserPath -eq 'runtimeConfigDir'
        }
        $assignments = @($s.Ast.FindAll({
                    param($node)
                    $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
                    $node.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and
                    $node.Left.VariablePath.UserPath -eq 'runtimeConfigDir'
                }, $true))
        $assignments.Count | Should -Be 1
        $assignment = $assignments[0]

        $conditional = @(
            [System.Management.Automation.Language.IfStatementAst],
            [System.Management.Automation.Language.LoopStatementAst],
            [System.Management.Automation.Language.SwitchStatementAst],
            [System.Management.Automation.Language.CatchClauseAst],
            [System.Management.Automation.Language.TrapStatementAst],
            [System.Management.Automation.Language.ScriptBlockExpressionAst],
            [System.Management.Automation.Language.FunctionDefinitionAst])
        $guarding = @()
        $ancestor = $assignment.Parent
        while ($null -ne $ancestor) {
            foreach ($type in $conditional) {
                if ($ancestor -is $type) { $guarding += "$($type.Name) at line $($ancestor.Extent.StartLineNumber)" }
            }
            $ancestor = $ancestor.Parent
        }
        $guarding | Should -BeNullOrEmpty -Because 'the assignment must run on both the upgrade and the fresh-install path'

        $reads = @($s.Ast.FindAll($isRuntimeConfigDir, $true) | Where-Object { $_ -ne $assignment.Left })
        $reads.Count | Should -BeGreaterThan 0
        foreach ($read in $reads) {
            $read.Extent.StartOffset | Should -BeGreaterThan $assignment.Extent.EndOffset -Because "line $($read.Extent.StartLineNumber) reads it"
        }

        $jobChecks = @($s.Ast.FindAll({
                    param($node)
                    $node -is [System.Management.Automation.Language.CommandAst] -and
                    $node.GetCommandName() -eq 'Assert-NoActiveBulkJobsBeforeRecycle'
                }, $true))
        # The upgrade's check and, since cdt-49, the fresh-install path's two: the report of the instance a
        # -AllowRepoint move takes down (the serving folder's, $runtimeConfigDir) and the check before the
        # stop, of the instance the stop acts on ($stoppedConfigDir, -PublishPath's config\).
        $jobChecks.Count | Should -Be 3 -Because 'the upgrade recycles; the fresh-install path reports a moved instance and checks the stopped one'
        $configDirs = @($jobChecks | ForEach-Object { if ($_.Extent.Text -match '-ConfigDir (\$\w+)\b') { $Matches[1] } })
        ($configDirs | Sort-Object) -join ',' | Should -BeExactly '$runtimeConfigDir,$runtimeConfigDir,$stoppedConfigDir'
        foreach ($call in $jobChecks) {
            $call.Extent.Text | Should -Match '-ConfigDir \$(runtimeConfigDir|stoppedConfigDir)\b'
        }
    }

    It 'reads ConfigStore:Path before -Force changes anything and writes it into the regenerated appsettings.json (cdt-8)' {
        # -Force over an existing deployment takes the fresh-install branch, which regenerates
        # appsettings.json from parameters. ConfigStore:Path names the config database the
        # instance opens - on a shared-database server the one file dev and prod both use - and
        # without it the app silently opens its own config\exchangeadmin.db instead.
        $install = Get-FreshInstallBranch $s

        $changes = @($install.FindAll({ param($node)
                    $node -is [System.Management.Automation.Language.CommandAst] -and
                    $node.GetCommandName() -in @('Stop-AppPoolVerified', 'robocopy') }, $true))
        $changes.Count | Should -Be 2 -Because 'the branch stops the pool once and mirrors once'
        $firstChange = ($changes | ForEach-Object { $_.Extent.StartOffset } | Measure-Object -Minimum).Minimum

        $reads = @($install.FindAll({ param($node)
                    $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
                    $node.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and
                    $node.Left.VariablePath.UserPath -eq 'configStorePathSetting' }, $true))
        $reads.Count | Should -Be 1 -Because 'the value is read once, from the configuration the application runs on now'
        # Since cdt-44 that is the serving folder's file: -PublishPath's, the file about to be replaced,
        # except in a -AllowRepoint move, where it is the folder the application is moved off.
        $reads[0].Right.Extent.Text | Should -BeExactly 'Get-ConfigStorePathSetting -PublishPath $servingFolder'
        @(Get-ConditionalAncestor $reads[0] $install) | Should -BeNullOrEmpty -Because 'every run down this branch must read it'
        $reads[0].Extent.EndOffset | Should -BeLessThan $firstChange -Because 'it is read before the pool stops or anything is mirrored'

        $writers = @($install.FindAll({ param($node)
                    $node -is [System.Management.Automation.Language.CommandAst] -and
                    $node.GetCommandName() -in @('Set-Content', 'Out-File', 'Add-Content', 'Write-AppSettingsFile') }, $true))
        $writers.Count | Should -Be 1 -Because 'appsettings.json is written in exactly one place on this path'
        $writers[0].GetCommandName() | Should -BeExactly 'Write-AppSettingsFile'
        Get-NamedArgumentText $writers[0] 'Path' | Should -BeExactly '$configPath'
        Get-NamedArgumentText $writers[0] 'ConfigStorePath' | Should -BeExactly '$configStorePathSetting' -Because 'the value read above is what the write carries'
        $writers[0].Extent.StartOffset | Should -BeGreaterThan $reads[0].Extent.EndOffset
    }

    It 'Write-AppSettingsFile carries ConfigStore:Path exactly as read, and adds none without it (cdt-8)' {
        # Runs the real function, lifted from deploy.ps1 by AST, against a temporary
        # appsettings.json, and reads the result back with the same reader the deploy uses.
        Import-Module (Join-Path $RepoRoot 'tools/SqliteConfigBackup.psm1') -Force
        $fn = Find-FunctionDefinition $s 'Write-AppSettingsFile'
        $fn | Should -Not -BeNullOrEmpty
        . ([scriptblock]::Create($fn.Extent.Text))

        $publish = Join-Path ([System.IO.Path]::GetTempPath()) ('cdt8_' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $publish -Force | Out-Null
        $appsettings = Join-Path $publish 'appsettings.json'
        try {
            Set-Content -LiteralPath $appsettings -Encoding UTF8 -Value '{ "AllowedHosts": "old", "Legacy": 1, "ConfigStore": { "Path": "D:\\shared\\config\\exchangeadmin.db" } }'
            $kept = Get-ConfigStorePathSetting -PublishPath $publish
            $kept | Should -BeExactly 'D:\shared\config\exchangeadmin.db'
            Write-AppSettingsFile -Settings ([ordered]@{ AllowedHosts = '*' }) -Path $appsettings -ConfigStorePath $kept
            Get-ConfigStorePathSetting -PublishPath $publish | Should -BeExactly 'D:\shared\config\exchangeadmin.db'
            $json = Get-Content -LiteralPath $appsettings -Raw | ConvertFrom-Json
            ($json.PSObject.Properties.Name -join ',') | Should -BeExactly 'AllowedHosts,ConfigStore' -Because 'only the key is carried; the rest is regenerated'
            ($json.ConfigStore.PSObject.Properties.Name -join ',') | Should -BeExactly 'Path'
            $json.AllowedHosts | Should -BeExactly '*'

            Set-Content -LiteralPath $appsettings -Encoding UTF8 -Value '{ "AllowedHosts": "old" }'
            $none = Get-ConfigStorePathSetting -PublishPath $publish
            $none | Should -BeNullOrEmpty
            Write-AppSettingsFile -Settings ([ordered]@{ AllowedHosts = '*' }) -Path $appsettings -ConfigStorePath $none
            ((Get-Content -LiteralPath $appsettings -Raw | ConvertFrom-Json).PSObject.Properties.Name -join ',') |
                Should -BeExactly 'AllowedHosts' -Because 'without the key nothing is added'
        } finally {
            Remove-Item -LiteralPath $publish -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'takes the verified online backup on -Force, before the pool stops or anything is mirrored (cdt-9)' {
        # Architectural Invariant 3: the config database is backed up by verified online backup,
        # from the path the instance opens, before each deploy - never by raw file copy. -Force
        # used to raw-copy the live config\ directory before the publish, verify nothing, and
        # never back up a shared database at all.
        $install = Get-FreshInstallBranch $s

        $changes = @($install.FindAll({ param($node)
                    $node -is [System.Management.Automation.Language.CommandAst] -and
                    $node.GetCommandName() -in @('Stop-AppPoolVerified', 'robocopy') }, $true))
        $changes.Count | Should -Be 2 -Because 'the branch stops the pool once and mirrors once'
        $firstChange = ($changes | ForEach-Object { $_.Extent.StartOffset } | Measure-Object -Minimum).Minimum

        $backups = @($install.FindAll({ param($node)
                    $node -is [System.Management.Automation.Language.CommandAst] -and
                    $node.GetCommandName() -eq 'Backup-SqliteConfigDb' }, $true))
        $backups.Count | Should -Be 1 -Because 'the -Force path must take the verified online backup'
        $backup = $backups[0]
        Get-NamedArgumentText $backup 'DbPath' | Should -BeExactly '$configDbPath' -Because 'the backup must target the database the instance opens'
        Get-NamedArgumentText $backup 'DestDir' | Should -BeExactly '$configDirBackup'
        Get-NamedArgumentText $backup 'Timestamp' | Should -BeExactly '$timestamp'
        Get-NamedArgumentText $backup 'AppPath' | Should -BeExactly '$StagingPath' -Because 'the tool is the build about to be deployed'
        $backup.Extent.EndOffset | Should -BeLessThan $firstChange -Because 'the snapshot must exist before the pool stops or any file changes'

        $gates = @(Get-ConditionalAncestor $backup $install)
        $gates.Count | Should -Be 1 -Because 'the backup is gated only on there being a live configuration to protect'
        $gates[0] | Should -BeOfType ([System.Management.Automation.Language.IfStatementAst])
        # Since cdt-44 the gate is the serving folder's appsettings.json: outside a -AllowRepoint move
        # that is -PublishPath's, the old '$Force -and (Test-Path $configPath)' (a fresh install without
        # -Force has none there); a move backs up the configuration it moves the application off.
        $gates[0].Clauses[0].Item1.Extent.Text | Should -BeExactly 'Test-Path -LiteralPath $servingConfigPath'

        $resolves = @($install.FindAll({ param($node)
                    $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
                    $node.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and
                    $node.Left.VariablePath.UserPath -eq 'configDbPath' }, $true))
        $resolves.Count | Should -Be 1
        $resolves[0].Right.Extent.Text | Should -BeExactly 'Resolve-ConfigDbPath -PublishPath $servingFolder' -Because 'a shared database lives outside config\, and the database to back up is the one the application opens now (cdt-44)'
        @(Get-ConditionalAncestor $resolves[0] $install) | Should -BeNullOrEmpty
        $resolves[0].Extent.EndOffset | Should -BeLessThan $backup.Extent.StartOffset

        $refusals = @($install.FindAll({ param($node)
                    $node -is [System.Management.Automation.Language.IfStatementAst] -and
                    $node.Clauses[0].Item1.Extent.Text -eq '$configStorePathSetting -and -not (Test-IsSqliteConfigDbPresent -DbPath $configDbPath)' }, $true))
        $refusals.Count | Should -Be 1 -Because 'a configured path naming a missing file must stop the run, not back up nothing'
        $refusals[0].Clauses[0].Item2.Statements[0].Extent.Text | Should -BeLike 'Write-Fail "ConfigStore:Path*'
        @(Get-ConditionalAncestor $refusals[0] $install) | Should -BeNullOrEmpty
        $refusals[0].Extent.EndOffset | Should -BeLessThan $firstChange

        $install.Extent.Text | Should -Match ([regex]::Escape('$configDirBackup = Join-Path $BackupDir "config.${timestamp}.pre-force.bak"'))

        $copies = @($install.FindAll({ param($node)
                    $node -is [System.Management.Automation.Language.PipelineAst] -and
                    $node.PipelineElements.Count -ge 2 -and
                    $node.PipelineElements[0].Extent.Text -like 'Get-ChildItem*$runtimeConfigDir*' -and
                    $node.Extent.Text -match 'Copy-Item' }, $true))
        $copies.Count | Should -Be 1 -Because 'the rest of config\ is copied once, as the upgrade path copies it'
        # The filter's list (which holds the live triplet) is pinned by the cdt-10 test.
        $copies[0].Extent.Text | Should -Match ([regex]::Escape('Where-Object { $_.Name -notin $configCopyExclusions }')) `
            -Because 'a raw copy of the live database triplet is the torn snapshot this replaces'
        $copies[0].Extent.EndOffset | Should -BeLessThan $firstChange

        $recursive = @($s.Ast.FindAll({ param($node)
                    $node -is [System.Management.Automation.Language.CommandAst] -and
                    $node.GetCommandName() -eq 'Copy-Item' -and
                    @($node.CommandElements | Where-Object {
                            $_ -is [System.Management.Automation.Language.CommandParameterAst] -and $_.ParameterName -like 'r*' }).Count -gt 0 }, $true))
        @($recursive | ForEach-Object { "line $($_.Extent.StartLineNumber): $($_.Extent.Text)" }) |
            Should -BeNullOrEmpty -Because 'no raw recursive copy of a directory may stand in for the verified backup'
    }

    It 'leaves every SQLite database the app keeps in config\ out of the config backup copies (cdt-10)' {
        # The rest-of-config copy used to exclude only the config database triplet, so every deploy
        # copied the live jobs and usage databases into a backup nothing prunes, keeping bulk-job
        # records and usage telemetry past the retention the pages state, against Invariant 3.
        $lists = @($s.Ast.FindAll({ param($node)
                    $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
                    $node.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and
                    $node.Left.VariablePath.UserPath -eq 'configCopyExclusions' }, $true))
        $lists.Count | Should -Be 1 -Because 'one list, so the upgrade and the -Force copies cannot disagree'
        $list = $lists[0]
        @(Get-ConditionalAncestor $list $null) | Should -BeNullOrEmpty -Because 'both paths read it'
        $list.Extent.EndOffset | Should -BeLessThan (Get-FreshInstallBranch $s).Parent.Extent.StartOffset -Because 'it is set before the mode branch'
        $names = @($list.Right.FindAll({ param($node)
                    $node -is [System.Management.Automation.Language.StringConstantExpressionAst] }, $true) | ForEach-Object Value)

        # Tripwire: the databases come from the app's own code, so a new one cannot repeat this.
        $sources = @(& git -C $RepoRoot ls-files -- '*.cs' | Where-Object { $_ -notlike 'ExchangeAdminWeb.Tests/*' })
        $LASTEXITCODE | Should -Be 0
        $sources.Count | Should -BeGreaterThan 50 -Because 'the scan must actually see the app source'
        $appDbs = @(foreach ($rel in $sources) {
                $text = Get-Content -LiteralPath (Join-Path $RepoRoot $rel) -Raw
                foreach ($match in [regex]::Matches($text, 'Path\.Combine\([^;]*?"config",\s*"([^"]+\.db)"\)')) { $match.Groups[1].Value }
            }) | Sort-Object -Unique
        foreach ($known in 'exchangeadmin.db', 'exchangeadmin-jobs.db', 'exchangeadmin-usage.db') {
            $appDbs | Should -Contain $known -Because 'the scan must see the databases the app creates today'
        }
        $expected = @(foreach ($db in $appDbs) { $db; "$db-wal"; "$db-shm" }) | Sort-Object
        (@($names | Sort-Object) -join ',') | Should -BeExactly ($expected -join ',') `
            -Because 'exactly the databases the app keeps in config\, each with its WAL sidecars, and nothing else'

        $copies = @($s.Ast.FindAll({ param($node)
                    $node -is [System.Management.Automation.Language.PipelineAst] -and
                    $node.PipelineElements.Count -ge 2 -and
                    $node.PipelineElements[0].Extent.Text -like 'Get-ChildItem*$runtimeConfigDir*' -and
                    $node.Extent.Text -match 'Copy-Item' }, $true))
        $copies.Count | Should -Be 2 -Because 'the upgrade and the -Force path each copy the rest of config\'
        foreach ($copy in $copies) {
            $copy.Extent.Text | Should -Match ([regex]::Escape('Where-Object { $_.Name -notin $configCopyExclusions }')) -Because "the copy at line $($copy.Extent.StartLineNumber) must filter on the list"
            $copy.Extent.StartOffset | Should -BeGreaterThan $list.Extent.EndOffset
        }
    }

    It 'leaves the same databases out of the drift inventory as out of the backup copies (cdt-13)' {
        # The drift check compares config\ before the pool stops with config\ after it restarts. A
        # live WAL database changes across that by design; the drift list named only the config
        # database, so the jobs and usage databases raised false drift warnings that pointed at a
        # backup which, since cdt-10, deliberately does not contain them.
        $lists = @{}
        foreach ($listName in 'configCopyExclusions', 'driftCheckExclusions') {
            $lists[$listName] = @($s.Ast.FindAll({ param($node)
                        $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
                        $node.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and
                        $node.Left.VariablePath.UserPath -eq $listName }, $true))
        }
        $copyLists = $lists['configCopyExclusions']
        $copyLists.Count | Should -Be 1
        $driftLists = $lists['driftCheckExclusions']
        $driftLists.Count | Should -Be 1 -Because 'one drift list'
        $drift = $driftLists[0]
        $drift.Extent.StartOffset | Should -BeGreaterThan $copyLists[0].Extent.EndOffset

        $references = @($drift.Right.FindAll({ param($node)
                    $node -is [System.Management.Automation.Language.VariableExpressionAst] -and
                    $node.VariablePath.UserPath -eq 'configCopyExclusions' }, $true))
        $references.Count | Should -Be 1 -Because 'the drift list derives from the shared list, so one list owns the databases'

        # The two real statements, evaluated together: the shared list plus extended-log-level.txt.
        $evaluate = [scriptblock]::Create($copyLists[0].Extent.Text + "`n" + $drift.Extent.Text + "`n" + ',$driftCheckExclusions')
        $driftNames = @((& $evaluate) | ForEach-Object { $_ })
        $copyNames = @(([scriptblock]::Create($copyLists[0].Extent.Text + "`n" + ',$configCopyExclusions')).Invoke() | ForEach-Object { $_ })
        $copyNames.Count | Should -BeGreaterOrEqual 9 -Because 'the cdt-10 test pins what the shared list holds'
        (@($driftNames | Sort-Object -Unique) -join ',') | Should -BeExactly (@(@('extended-log-level.txt') + $copyNames | Sort-Object -Unique) -join ',') `
            -Because 'every database the backup leaves out, each with its WAL sidecars, and the rewritten log-level file'

        $inventories = @($s.Ast.FindAll({ param($node)
                    $node -is [System.Management.Automation.Language.PipelineAst] -and
                    $node.PipelineElements.Count -ge 2 -and
                    $node.PipelineElements[0].Extent.Text -like 'Get-ChildItem*$runtimeConfigDir*' -and
                    $node.Extent.Text -notmatch 'Copy-Item' }, $true))
        $inventories.Count | Should -Be 2 -Because 'the drift check inventories config\ before the stop and after the restart'
        foreach ($inventory in $inventories) {
            $inventory.Extent.Text | Should -Match ([regex]::Escape('Where-Object { $_.Name -notin $driftCheckExclusions }')) -Because "the inventory at line $($inventory.Extent.StartLineNumber) must filter on the drift list"
            $inventory.Extent.StartOffset | Should -BeGreaterThan $drift.Extent.EndOffset
        }
    }

    It 'checks the live config database after a -Force deploy restarts the pool, and only warns (cdt-12)' {
        # The upgrade path ends with a post-deploy check of the live store. -Force, the path that
        # regenerates appsettings.json and so the one most able to point the app at a different,
        # missing or damaged database, ended on the success banner without one.
        $install = Get-FreshInstallBranch $s

        $restarts = @($install.FindAll({ param($node)
                    $node -is [System.Management.Automation.Language.TryStatementAst] -and
                    $null -ne $node.Finally -and
                    @($node.Finally.FindAll({ param($n)
                                $n -is [System.Management.Automation.Language.CommandAst] -and
                                $n.GetCommandName() -eq 'Start-AppPoolWithRetry' }, $true)).Count -gt 0 }, $true))
        $restarts.Count | Should -Be 1 -Because 'the branch restarts the pool in one finally'
        $restartEnd = $restarts[0].Extent.EndOffset

        $resolves = @($install.FindAll({ param($node)
                    $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
                    $node.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and
                    $node.Left.VariablePath.UserPath -eq 'liveDb' }, $true))
        $resolves.Count | Should -Be 1 -Because 'the -Force path must resolve the database it checks'
        $resolves[0].Right.Extent.Text | Should -BeExactly 'Resolve-ConfigDbPath -PublishPath $PublishPath' -Because 'it reads the regenerated appsettings.json, the file the app opens'
        @(Get-ConditionalAncestor $resolves[0] $install) | Should -BeNullOrEmpty -Because 'every -Force run that gets this far is checked'
        $resolves[0].Extent.StartOffset | Should -BeGreaterThan $restartEnd -Because 'the check runs after the pool restart'

        $checks = @($install.FindAll({ param($node)
                    $node -is [System.Management.Automation.Language.CommandAst] -and
                    $node.GetCommandName() -eq 'Test-SqliteConfigDbIntegrity' }, $true))
        $checks.Count | Should -Be 1 -Because 'the -Force path must check the live store'
        $check = $checks[0]
        Get-NamedArgumentText $check 'DbPath' | Should -BeExactly '$liveDb'
        Get-NamedArgumentText $check 'AppPath' | Should -BeExactly '$PublishPath' -Because 'staging is gone by then; the publish folder holds the same build'
        $check.Extent.StartOffset | Should -BeGreaterThan $resolves[0].Extent.EndOffset

        $gates = @(Get-ConditionalAncestor $check $install)
        $gates.Count | Should -Be 1 -Because 'only its own presence test decides whether it runs'
        $gates[0].Clauses[0].Item1.Extent.Text | Should -BeExactly 'Test-Path -LiteralPath $liveDb'

        # Warn, not throw: the files are deployed and the pool is running; the verified pre-deploy
        # backup is the rollback.
        $try = $check.Parent
        while ($null -ne $try -and -not ($try -is [System.Management.Automation.Language.TryStatementAst])) { $try = $try.Parent }
        $try | Should -Not -BeNullOrEmpty
        $try.Extent.StartOffset | Should -BeGreaterThan $gates[0].Extent.StartOffset -Because 'the try is inside the presence test'
        $try.CatchClauses.Count | Should -Be 1
        $try.CatchClauses[0].Body.Statements[0].Extent.Text | Should -BeLike 'Write-Warn "POST-DEPLOY CHECK: live config DB integrity check FAILED*'
        @($try.CatchClauses[0].Body.FindAll({ param($n)
                    $n -is [System.Management.Automation.Language.ThrowStatementAst] -or
                    ($n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Write-Fail') }, $true)).Count |
            Should -Be 0 -Because 'a failed check is reported, never thrown'

        $gates[0].Clauses.Count | Should -Be 2
        $gates[0].Clauses[1].Item1.Extent.Text | Should -BeExactly '$configStorePathSetting'
        $gates[0].Clauses[1].Item2.Statements[0].Extent.Text | Should -BeLike 'Write-Warn "POST-DEPLOY CHECK: ConfigStore:Path names*'
        $gates[0].ElseClause.Statements[0].Extent.Text | Should -BeLike 'Write-Warn "POST-DEPLOY CHECK: no config DB at*'
    }

    It 'restarts the pool only after a complete mirror, and a failed restart never replaces the real error (cdt-16, AST)' {
        # robocopy /MIR failing part-way leaves a mix of builds in the publish folder; the finally
        # restarted the pool over it anyway. And a restart that threw inside the finally replaced
        # whatever was propagating.
        $modeIf = (Get-FreshInstallBranch $s).Parent
        $branches = [ordered]@{ upgrade = $modeIf.Clauses[0].Item2; '-Force' = $modeIf.ElseClause }
        foreach ($branchName in $branches.Keys) {
            $statements = @($branches[$branchName].Statements)
            $restartIndexes = @(for ($i = 0; $i -lt $statements.Count; $i++) {
                    $st = $statements[$i]
                    if ($st -is [System.Management.Automation.Language.TryStatementAst] -and $null -ne $st.Finally -and
                        @($st.Finally.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Start-AppPoolWithRetry' }, $true)).Count -gt 0) { $i }
                })
            $restartIndexes.Count | Should -Be 1 -Because "the $branchName branch restarts the pool in one finally"
            $restartIndex = $restartIndexes[0]
            $restart = $statements[$restartIndex]

            $flagSetters = @{}
            foreach ($i in 0..($restartIndex - 1)) {
                if ($statements[$i] -is [System.Management.Automation.Language.AssignmentStatementAst]) { $flagSetters[$statements[$i].Extent.Text] = $i }
            }
            $flagSetters.ContainsKey('$mirrorSucceeded = $false') | Should -BeTrue -Because "the $branchName branch starts with the mirror not done"
            $flagSetters.ContainsKey('$poolStartError = $null') | Should -BeTrue

            $body = @($restart.Body.Statements)
            $robocopyAt = -1; $checkAt = -1; $setAt = -1
            for ($i = 0; $i -lt $body.Count; $i++) {
                if ($body[$i].Extent.Text -like '& robocopy*') { $robocopyAt = $i }
                if ($body[$i] -is [System.Management.Automation.Language.IfStatementAst] -and $body[$i].Clauses[0].Item1.Extent.Text -eq '$LASTEXITCODE -ge 8') { $checkAt = $i }
                if ($body[$i].Extent.Text -eq '$mirrorSucceeded = $true') { $setAt = $i }
            }
            $robocopyAt | Should -BeGreaterOrEqual 0
            $checkAt | Should -Be ($robocopyAt + 1) -Because 'the exit code is checked right after the mirror'
            $setAt | Should -Be ($checkAt + 1) -Because 'the mirror counts as done only once its exit code has been checked'
            @($branches[$branchName].FindAll({ param($n) $n -is [System.Management.Automation.Language.AssignmentStatementAst] -and $n.Extent.Text -eq '$mirrorSucceeded = $true' }, $true)).Count | Should -Be 1
            $failure = $body[$checkAt].Clauses[0].Item2.Statements[0]
            $failure | Should -BeOfType ([System.Management.Automation.Language.ThrowStatementAst])
            foreach ($fact in 'LEFT STOPPED', '$PublishPath', '$StagingPath', '$ProjectRoot', '$dbBackup') {
                $failure.Extent.Text | Should -Match ([regex]::Escape($fact)) -Because "the mirror failure must say what state it left and what to do ($fact)"
            }

            $starts = @($restart.Finally.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Start-AppPoolWithRetry' }, $true))
            $starts.Count | Should -Be 1
            $gate = $starts[0].Parent
            while ($null -ne $gate -and -not ($gate -is [System.Management.Automation.Language.IfStatementAst] -and $gate.Clauses[0].Item1.Extent.Text -eq '$mirrorSucceeded')) { $gate = $gate.Parent }
            $gate | Should -Not -BeNullOrEmpty -Because "the $branchName restart must be conditional on a complete mirror"
            ($starts[0].Extent.StartOffset -ge $gate.Clauses[0].Item2.Extent.StartOffset -and $starts[0].Extent.EndOffset -le $gate.Clauses[0].Item2.Extent.EndOffset) | Should -BeTrue -Because 'it is in the then-branch'
            $guard = $starts[0].Parent
            while ($null -ne $guard -and -not ($guard -is [System.Management.Automation.Language.TryStatementAst])) { $guard = $guard.Parent }
            [object]::ReferenceEquals($guard, $restart) | Should -BeFalse -Because "the $branchName restart must have its own try inside the finally"
            $guard.CatchClauses.Count | Should -Be 1
            $catch = $guard.CatchClauses[0].Body
            $catch.Extent.Text | Should -Match ([regex]::Escape('$poolStartError = $_.Exception.Message'))
            @($catch.FindAll({ param($n)
                        $n -is [System.Management.Automation.Language.ThrowStatementAst] -or
                        ($n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Write-Fail') }, $true)).Count |
                Should -Be 0 -Because 'a throw from the finally would replace the error that is propagating'

            $after = $statements[$restartIndex + 1]
            $after | Should -BeOfType ([System.Management.Automation.Language.IfStatementAst])
            $after.Clauses[0].Item1.Extent.Text | Should -BeExactly '$poolStartError' -Because "a failed $branchName restart is its own failure once everything else succeeded"
            $after.Clauses[0].Item2.Statements[0] | Should -BeOfType ([System.Management.Automation.Language.ThrowStatementAst])
        }
    }

    It 'runs each branch''s real mirror-and-restart region: a failed mirror leaves the pool stopped, and the first error wins (cdt-16)' {
        # The region - the flags, the try/finally and the check after it - is lifted from deploy.ps1
        # by AST and run with only robocopy, the pool start and the manifest writer stubbed.
        foreach ($name in 'Write-Step', 'Write-Success', 'Write-Warn', 'Write-AppSettingsFile') {
            . ([scriptblock]::Create((Find-FunctionDefinition $s $name).Extent.Text))
        }
        function robocopy { $script:cdt16Calls.Add('robocopy'); $global:LASTEXITCODE = $script:cdt16Exit }
        ${function:Start-AppPoolWithRetry} = { param($Name) $null = $Name; $script:cdt16Calls.Add('start'); if ($script:cdt16StartThrows) { throw 'the pool would not start' } }
        function Write-DeploymentManifest { $script:cdt16Calls.Add('manifest') }

        $modeIf = (Get-FreshInstallBranch $s).Parent
        $regions = [ordered]@{}
        foreach ($entry in @(@('upgrade', $modeIf.Clauses[0].Item2), @('-Force', $modeIf.ElseClause))) {
            $statements = @($entry[1].Statements)
            $tryAt = @(for ($i = 0; $i -lt $statements.Count; $i++) {
                    if ($statements[$i] -is [System.Management.Automation.Language.TryStatementAst] -and $null -ne $statements[$i].Finally -and
                        $statements[$i].Finally.Extent.Text -match 'Start-AppPoolWithRetry') { $i }
                })[0]
            $from = $tryAt
            # manifestError since cdt-31.
            while ($from -gt 0 -and $statements[$from - 1].Extent.Text -match '^\$(mirrorSucceeded|poolStartError|manifestError|deploymentReadyForManifest) = ') { $from-- }
            $to = $tryAt
            if ($statements[$to + 1].Extent.Text -match '^if \(\$poolStartError\)') { $to++ }
            $regions[$entry[0]] = ($statements[$from..$to] | ForEach-Object { $_.Extent.Text }) -join "`n"
        }

        $run = {
            param([string]$Branch, [int]$Exit, [bool]$FailAfterMirror, [bool]$StartThrows)
            $script:cdt16Calls = New-Object System.Collections.Generic.List[string]
            $script:cdt16Exit = $Exit
            $script:cdt16StartThrows = $StartThrows
            $root = Join-Path ([System.IO.Path]::GetTempPath()) ('cdt16_' + [guid]::NewGuid().ToString('N'))
            $StagingPath = Join-Path $root 'staging'
            $PublishPath = Join-Path $root 'publish'
            $ProjectRoot = Join-Path $root 'source'
            New-Item -ItemType Directory -Path $StagingPath, $PublishPath, $ProjectRoot -Force | Out-Null
            # Read only by the lifted region.
            Set-Variable -Name AppPoolName -Value 'ExamplePool'
            Set-Variable -Name dbBackup -Value (Join-Path $root 'backup\exchangeadmin.db')
            Set-Variable -Name configStorePathSetting -Value $null
            $configPath = Join-Path $PublishPath 'appsettings.json'
            if ($Branch -eq 'upgrade') {
                $json = '{ "AllowedHosts": "*" }'
                if ($FailAfterMirror) { $json = '{ "AllowedHosts": ' }
                Set-Content -LiteralPath $configPath -Value $json -Encoding ascii
            } elseif ($FailAfterMirror) {
                $configPath = Join-Path $root 'missing-dir\appsettings.json'
            }
            $ErrorActionPreference = 'Stop'
            $thrown = $null
            try {
                . ([scriptblock]::Create($regions[$Branch]))
            } catch {
                $thrown = $_.Exception.Message
            } finally {
                $stagingLeft = Test-Path -LiteralPath $StagingPath
                Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
            }
            [pscustomobject]@{ Thrown = $thrown; Calls = ($script:cdt16Calls -join ','); StagingLeft = $stagingLeft; Staging = $StagingPath }
        }

        foreach ($branch in $regions.Keys) {
            $failedMirror = & $run $branch 8 $false $false 6>$null
            $failedMirror.Calls | Should -BeExactly 'robocopy' -Because "a failed $branch mirror must not restart the pool over a partial publish"
            $failedMirror.Thrown | Should -Match 'robocopy failed with exit code 8'
            $failedMirror.Thrown | Should -Match 'LEFT STOPPED'
            $failedMirror.Thrown | Should -Match ([regex]::Escape($failedMirror.Staging))
            $failedMirror.StagingLeft | Should -BeFalse

            $both = & $run $branch 1 $true $true 6>$null
            $both.Calls | Should -BeExactly 'robocopy,start'
            $both.Thrown | Should -Not -BeNullOrEmpty
            $both.Thrown | Should -Not -Match 'pool would not start|did not start' -Because "the $branch step that failed first must be the error the caller sees"

            $restartOnly = & $run $branch 1 $false $true 6>$null
            $restartOnly.Calls | Should -BeExactly 'robocopy,start,manifest' -Because 'the deploy is complete, so the manifest is still written'
            $restartOnly.Thrown | Should -Match ([regex]::Escape('did not start (the pool would not start)'))
            $restartOnly.Thrown | Should -Match 'DOWN'

            $clean = & $run $branch 1 $false $false 6>$null
            $clean.Thrown | Should -BeNullOrEmpty
            $clean.Calls | Should -BeExactly 'robocopy,start,manifest'
            $clean.StagingLeft | Should -BeFalse
        }
    }

    It 'records a failed manifest write like a failed restart, and a failed restart stays the error the caller gets (cdt-31, AST)' {
        # The manifest write in the restart's finally was unguarded: when it threw after a failed
        # restart, its exception replaced "The application is DOWN", which only a red line had said.
        $modeIf = (Get-FreshInstallBranch $s).Parent
        $branches = [ordered]@{ upgrade = $modeIf.Clauses[0].Item2; '-Force' = $modeIf.ElseClause }
        foreach ($branchName in $branches.Keys) {
            $statements = @($branches[$branchName].Statements)
            $restartIndexes = @(for ($i = 0; $i -lt $statements.Count; $i++) {
                    $st = $statements[$i]
                    if ($st -is [System.Management.Automation.Language.TryStatementAst] -and $null -ne $st.Finally -and
                        @($st.Finally.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Start-AppPoolWithRetry' }, $true)).Count -gt 0) { $i }
                })
            $restartIndexes.Count | Should -Be 1
            $restartIndex = $restartIndexes[0]
            $restart = $statements[$restartIndex]
            @($statements[0..($restartIndex - 1)] | Where-Object { $_.Extent.Text -eq '$manifestError = $null' }).Count | Should -Be 1 -Because "the $branchName branch starts with no manifest failure recorded"

            $writes = @($restart.Finally.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Write-DeploymentManifest' }, $true))
            $writes.Count | Should -Be 1 -Because "the $branchName finally writes the manifest once"
            $guard = $writes[0].Parent
            while ($null -ne $guard -and -not ($guard -is [System.Management.Automation.Language.TryStatementAst])) { $guard = $guard.Parent }
            [object]::ReferenceEquals($guard, $restart) | Should -BeFalse -Because "the $branchName manifest write must have its own try inside the finally"
            $guard.CatchClauses.Count | Should -Be 1
            $catch = $guard.CatchClauses[0].Body
            $catch.Extent.Text | Should -Match ([regex]::Escape('$manifestError = $_.Exception.Message'))
            @($catch.FindAll({ param($n)
                        $n -is [System.Management.Automation.Language.ThrowStatementAst] -or
                        ($n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Write-Fail') }, $true)).Count |
                Should -Be 0 -Because 'a throw from the finally would replace the error the caller must get'
            $start = @($restart.Finally.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Start-AppPoolWithRetry' }, $true))[0]
            $guard.Extent.StartOffset | Should -BeGreaterThan $start.Extent.EndOffset -Because 'the restart is attempted, and its failure recorded, first'

            # The order after the finally: the restart failure first, carrying the manifest failure;
            # only then a manifest failure on its own.
            $down = $statements[$restartIndex + 1]
            $down | Should -BeOfType ([System.Management.Automation.Language.IfStatementAst])
            $down.Clauses[0].Item1.Extent.Text | Should -BeExactly '$poolStartError'
            $downThrow = $down.Clauses[0].Item2.Statements[0]
            $downThrow | Should -BeOfType ([System.Management.Automation.Language.ThrowStatementAst])
            $downThrow.Extent.Text | Should -Match ([regex]::Escape('$manifestError')) -Because 'the DOWN failure also reports a manifest that was not written'
            $manifest = $statements[$restartIndex + 2]
            $manifest | Should -BeOfType ([System.Management.Automation.Language.IfStatementAst])
            $manifest.Clauses[0].Item1.Extent.Text | Should -BeExactly '$manifestError' -Because "a failed $branchName manifest write is still a failure, after the restart's"
            $manifest.Clauses[0].Item2.Statements[0] | Should -BeOfType ([System.Management.Automation.Language.ThrowStatementAst])
        }
    }

    It 'runs each branch''s real region: a failed manifest write never hides a failed restart (cdt-31)' {
        foreach ($name in 'Write-Step', 'Write-Success', 'Write-Warn', 'Write-AppSettingsFile') {
            . ([scriptblock]::Create((Find-FunctionDefinition $s $name).Extent.Text))
        }
        function robocopy { $script:cdt31Calls.Add('robocopy'); $global:LASTEXITCODE = 1 }
        ${function:Start-AppPoolWithRetry} = { param($Name) $null = $Name; $script:cdt31Calls.Add('start'); if ($script:cdt31StartThrows) { throw 'the pool would not start' } }
        ${function:Write-DeploymentManifest} = {
            param($PublishFolder, $RepoRoot)
            $null = $RepoRoot
            $script:cdt31Calls.Add('manifest')
            if ($script:cdt31ManifestThrows) { throw "Access to the path '$PublishFolder\deployment-manifest.json' is denied." }
        }

        $modeIf = (Get-FreshInstallBranch $s).Parent
        $regions = [ordered]@{}
        foreach ($entry in @(@('upgrade', $modeIf.Clauses[0].Item2), @('-Force', $modeIf.ElseClause))) {
            $statements = @($entry[1].Statements)
            $tryAt = @(for ($i = 0; $i -lt $statements.Count; $i++) {
                    if ($statements[$i] -is [System.Management.Automation.Language.TryStatementAst] -and $null -ne $statements[$i].Finally -and
                        $statements[$i].Finally.Extent.Text -match 'Start-AppPoolWithRetry') { $i }
                })[0]
            $from = $tryAt
            while ($from -gt 0 -and $statements[$from - 1].Extent.Text -match '^\$(mirrorSucceeded|poolStartError|manifestError|deploymentReadyForManifest) = ') { $from-- }
            # The checks after the finally, whichever order they are in.
            $to = $tryAt
            while ($to + 1 -lt $statements.Count -and $statements[$to + 1].Extent.Text -match '^if \(\$(poolStartError|manifestError)\)') { $to++ }
            $regions[$entry[0]] = ($statements[$from..$to] | ForEach-Object { $_.Extent.Text }) -join "`n"
        }

        $run = {
            param([string]$Branch, [bool]$StartThrows, [bool]$ManifestThrows)
            $script:cdt31Calls = New-Object System.Collections.Generic.List[string]
            $script:cdt31StartThrows = $StartThrows
            $script:cdt31ManifestThrows = $ManifestThrows
            $root = Join-Path ([System.IO.Path]::GetTempPath()) ('cdt31_' + [guid]::NewGuid().ToString('N'))
            $StagingPath = Join-Path $root 'staging'
            $PublishPath = Join-Path $root 'publish'
            $ProjectRoot = Join-Path $root 'source'
            New-Item -ItemType Directory -Path $StagingPath, $PublishPath, $ProjectRoot -Force | Out-Null
            # Read only by the lifted region.
            Set-Variable -Name AppPoolName -Value 'ExamplePool'
            Set-Variable -Name dbBackup -Value (Join-Path $root 'backup\exchangeadmin.db')
            Set-Variable -Name configStorePathSetting -Value $null
            $configPath = Join-Path $PublishPath 'appsettings.json'
            Set-Content -LiteralPath $configPath -Value '{ "AllowedHosts": "*" }' -Encoding ascii
            $ErrorActionPreference = 'Stop'
            $thrown = $null
            try {
                . ([scriptblock]::Create($regions[$Branch]))
            } catch {
                $thrown = $_.Exception.Message
            } finally {
                $stagingLeft = Test-Path -LiteralPath $StagingPath
                Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
            }
            [pscustomobject]@{ Thrown = $thrown; Calls = ($script:cdt31Calls -join ','); StagingLeft = $stagingLeft }
        }

        foreach ($branch in $regions.Keys) {
            $both = & $run $branch $true $true 6>$null
            $both.Calls | Should -BeExactly 'robocopy,start,manifest'
            $both.Thrown | Should -BeLike '*did not start (the pool would not start). The application is DOWN until the pool is started by hand.*' -Because "a failed $branch restart is the error the caller gets, whatever the manifest did"
            $both.Thrown | Should -BeLike "*The deployment manifest was not written either (Access to the path '*deployment-manifest.json' is denied.)*" -Because 'and the manifest failure is reported with it, not lost'
            $both.StagingLeft | Should -BeFalse -Because 'the finally no longer throws before its own staging removal'

            $downOnly = & $run $branch $true $false 6>$null
            $downOnly.Thrown | Should -BeLike '*did not start (the pool would not start). The application is DOWN until the pool is started by hand.'
            $downOnly.Calls | Should -BeExactly 'robocopy,start,manifest' -Because 'the deploy is complete, so the manifest is still written'

            $manifestOnly = & $run $branch $false $true 6>$null
            $manifestOnly.Thrown | Should -BeLike "*app pool ExamplePool is running, but the deployment manifest was not written to * (Access to the path '*deployment-manifest.json' is denied.)*" -Because 'a failed manifest write is still a failure'

            $clean = & $run $branch $false $false 6>$null
            $clean.Thrown | Should -BeNullOrEmpty
            $clean.Calls | Should -BeExactly 'robocopy,start,manifest'
        }
    }

    It 'writes and reports the manifest even when git cannot answer (cdt-14)' {
        # Under PowerShell 7 a failed git leaves $gitCommit $null (5.1 maps it to "unknown"), and
        # the writer - which runs in the finally that restarts the pool - called Substring on it.
        foreach ($name in 'Write-Success', 'Write-DeploymentManifest') {
            $fn = Find-FunctionDefinition $s $name
            $fn | Should -Not -BeNullOrEmpty
            . ([scriptblock]::Create($fn.Extent.Text))
        }
        $publish = Join-Path ([System.IO.Path]::GetTempPath()) ('cdt14_' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $publish -Force | Out-Null
        # deploy.ps1's own preference. Under Continue a failed method call is written and skipped,
        # so the old line would not escape the function and this test would pass for the wrong reason.
        $ErrorActionPreference = 'Stop'
        try {
            $noRepository = Join-Path $publish 'no-such-repository'
            $said = @(Write-DeploymentManifest -PublishFolder $publish -RepoRoot $noRepository 6>&1 | ForEach-Object { "$_" })
            ($said -join "`n") | Should -Match 'Deployment manifest written: '
            $manifest = Get-Content -LiteralPath (Join-Path $publish 'deployment-manifest.json') -Raw | ConvertFrom-Json
            (@($null, '', 'unknown') -contains $manifest.gitCommit) | Should -BeTrue -Because 'git could not answer, so no commit is recorded'

            $head = "$(& git -C $RepoRoot rev-parse HEAD)".Trim()
            $LASTEXITCODE | Should -Be 0
            $said = @(Write-DeploymentManifest -PublishFolder $publish -RepoRoot $RepoRoot 6>&1 | ForEach-Object { "$_" })
            ($said -join "`n") | Should -Match ([regex]::Escape("@ $($head.Substring(0, 8))")) -Because 'a real commit is still shortened to eight characters'
        } finally {
            Remove-Item -LiteralPath $publish -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'records an unreadable git status as unknown, not as a clean tree, in both manifest writers and both shells (cdt-26)' {
        # Both writers recorded a git status that failed as gitDirty = false, and the promotion's
        # "built from a dirty tree" gate reads that as clean. rev-parse can succeed while status fails
        # (a corrupt index), so the manifest then also carries a valid commit.
        $root = Join-Path ([System.IO.Path]::GetTempPath()) ('cdt26_' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $root -Force | Out-Null
        try {
            $repos = [ordered]@{}
            foreach ($kind in 'clean', 'dirty', 'corrupt') {
                $repo = Join-Path $root $kind
                New-Item -ItemType Directory -Path $repo -Force | Out-Null
                & git -C $repo init -q 2>&1 | Out-Null
                & git -C $repo -c user.email=probe@example.test -c user.name=probe commit -q --allow-empty -m init 2>&1 | Out-Null
                $LASTEXITCODE | Should -Be 0 -Because 'the fixture repository needs a commit'
                if ($kind -ne 'clean') { Set-Content -LiteralPath (Join-Path $repo 'untracked.txt') -Value 'x' -Encoding ascii }
                if ($kind -eq 'corrupt') { [System.IO.File]::WriteAllBytes((Join-Path $repo '.git\index'), [byte[]](1..64)) }
                $repos[$kind] = [pscustomobject]@{ Path = $repo; Head = "$(& git -C $repo rev-parse HEAD)".Trim() }
            }
            & git -C $repos['corrupt'].Path status --porcelain 2>&1 | Out-Null
            $LASTEXITCODE | Should -Not -Be 0 -Because 'the corrupt-index fixture must make git status fail'

            $expected = @{ clean = $false; dirty = $true; corrupt = 'unknown' }
            $child = Join-Path $root 'writer51.ps1'
            Set-Content -LiteralPath $child -Encoding ascii -Value @'
param($ScriptPath, $Repo, $Out)
$ErrorActionPreference = 'Stop'
$ast = [System.Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$null, [ref]$null)
foreach ($name in 'Write-DeploymentManifest', 'Write-Success', 'Write-Ok', 'Write-Plan', 'Invoke-PlanOrAction') {
    $fn = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name }, $true)
    if ($fn) { . ([scriptblock]::Create($fn.Extent.Text)) }
}
$PlanOnly = $false
New-Item -ItemType Directory -Path $Out -Force | Out-Null
Write-DeploymentManifest -PublishFolder $Out -RepoRoot $Repo | Out-Null
'@
            foreach ($writer in 'deploy.ps1', 'tools/Install-ExchangeAdminWeb.ps1') {
                $scriptPath = Join-Path $RepoRoot $writer
                $ast = [System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$null, [ref]$null)
                foreach ($name in 'Write-DeploymentManifest', 'Write-Success', 'Write-Ok', 'Write-Plan', 'Invoke-PlanOrAction') {
                    $fn = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name }, $true)
                    if ($fn) { . ([scriptblock]::Create($fn.Extent.Text)) }
                }
                Set-Variable -Name PlanOnly -Value $false
                foreach ($kind in $repos.Keys) {
                    foreach ($shell in 'pwsh', 'powershell.exe') {
                        $out = Join-Path $root ("out-$kind-" + [guid]::NewGuid().ToString('N'))
                        if ($shell -eq 'pwsh') {
                            $ErrorActionPreference = 'Stop'
                            New-Item -ItemType Directory -Path $out -Force | Out-Null
                            Write-DeploymentManifest -PublishFolder $out -RepoRoot $repos[$kind].Path 6>$null | Out-Null
                            $ErrorActionPreference = 'Continue'
                        } else {
                            & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $child -ScriptPath $scriptPath -Repo $repos[$kind].Path -Out $out 6>$null | Out-Null
                            $LASTEXITCODE | Should -Be 0 -Because "the $writer writer must run under Windows PowerShell 5.1"
                        }
                        $manifest = Get-Content -LiteralPath (Join-Path $out 'deployment-manifest.json') -Raw | ConvertFrom-Json
                        $manifest.gitDirty | Should -Be $expected[$kind] -Because "$writer under $shell, $kind repository"
                        if ($kind -eq 'corrupt') {
                            $manifest.gitDirty | Should -BeOfType ([string]) -Because 'unknown is not a boolean'
                            $manifest.gitCommit | Should -BeExactly $repos[$kind].Head -Because 'the commit is still known, so only the dirty gate stood between this build and production'
                        } else {
                            $manifest.gitDirty | Should -BeOfType ([bool])
                        }
                    }
                }
            }
        } finally {
            Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'shortens the manifest commit only when there is one long enough to shorten (cdt-14)' {
        $fn = Find-FunctionDefinition $s 'Write-DeploymentManifest'
        $calls = @($fn.FindAll({ param($node)
                    $node -is [System.Management.Automation.Language.InvokeMemberExpressionAst] -and
                    $node.Member.Extent.Text -eq 'Substring' }, $true))
        $calls.Count | Should -Be 1 -Because 'one place shortens the commit'
        $guard = $calls[0].Parent
        while ($null -ne $guard -and -not ($guard -is [System.Management.Automation.Language.IfStatementAst])) { $guard = $guard.Parent }
        $guard | Should -Not -BeNullOrEmpty -Because 'Substring on a null or short commit throws'
        $guard.Clauses[0].Item1.Extent.Text | Should -BeExactly '$gitCommit -and $gitCommit.Length -ge 8'
        $calls[0].Extent.StartOffset | Should -BeGreaterThan $guard.Clauses[0].Item2.Extent.StartOffset -Because 'it runs only in the then-branch'
        $calls[0].Extent.EndOffset | Should -BeLessThan $guard.Clauses[0].Item2.Extent.EndOffset
    }

    It 'runs a post-deploy live DB integrity check (DB is excluded from file drift by design)' {
        $upgradeBlock = [regex]::Match(
            $s.Text,
            '(?s)# --- UPGRADE ---.*?# --- FRESH INSTALL ---'
        ).Value

        # The DB triplet is excluded from the file-drift inventory (it changes across the pool
        # restart by design), so the live store must instead be verified by integrity_check after
        # the pool starts - otherwise a missing/corrupt live DB ends on the success banner.
        $upgradeBlock | Should -Match 'Test-SqliteConfigDbIntegrity' `
            -Because 'the live DB needs a real post-deploy health check, not file-drift comparison'
        $upgradeBlock | Should -Match "driftCheckExclusions = @\('extended-log-level.txt'\) \+ \`$configCopyExclusions" `
            -Because 'the DB triplets must be excluded from the size/mtime drift inventory (through the shared list since cdt-13)'
        $upgradeBlock.IndexOf('Start-AppPoolWithRetry') |
            Should -BeLessThan $upgradeBlock.IndexOf('Test-SqliteConfigDbIntegrity') `
            -Because 'the live-DB check must run after the pool restarts (post migrate/seed)'
    }

    It 'verifies runtime config against a pre-deploy snapshot after the pool restarts (incident fix #5)' {
        $upgradeBlock = [regex]::Match(
            $s.Text,
            '(?s)# --- UPGRADE ---.*?# --- FRESH INSTALL ---'
        ).Value

        # Snapshot taken before the pool stops; comparison after the pool restarts.
        $upgradeBlock.IndexOf('$preConfigInventory') | Should -BeGreaterOrEqual 0
        $upgradeBlock.IndexOf('$preConfigInventory') | Should -BeLessThan $upgradeBlock.IndexOf('Stopping app pool')
        $upgradeBlock.IndexOf('Verifying runtime config integrity') | Should -BeGreaterThan $upgradeBlock.IndexOf('Start-AppPoolWithRetry')
        $upgradeBlock | Should -Match 'POST-DEPLOY CHECK' -Because 'drift must be warned about loudly'
        $upgradeBlock | Should -Match '\$lostKeys' -Because 'appsettings top-level key loss must be part of the check'
    }

    It 'does not rewrite appsettings.json during upgrade reconciliation' {
        $upgradeBlock = [regex]::Match(
            $s.Text,
            '(?s)# --- UPGRADE ---.*?# --- FRESH INSTALL ---'
        ).Value

        $upgradeBlock | Should -Not -Match 'ConvertTo-Json\s+-Depth\s+10\s+\|\s+Set-Content\s+\$configPath' `
            -Because 'upgrade deploys preserve environment-owned appsettings.json byte-for-byte except for explicit operator edits'
        $upgradeBlock | Should -Not -Match 'Write-AppSettingsFile' `
            -Because 'the regenerating writer belongs to the fresh-install/-Force path only (cdt-8 moved the write into it)'
        $upgradeBlock | Should -Not -Match '\.PSObject\.Properties\.Remove' `
            -Because 'obsolete appsettings keys are warned about, not silently removed by deploy'
        $upgradeBlock | Should -Not -Match 'Add-Member\s+-NotePropertyName\s+"AdminGroups"' `
            -Because 'deploy must not synthesize Security:AdminGroups by reserializing the whole file'
    }
}

Describe 'tools/Install-ExchangeAdminWeb.ps1' {
    BeforeAll { $script:s = Get-ScriptUnderTest 'tools/Install-ExchangeAdminWeb.ps1' }

    It 'parses without syntax errors' {
        $s.Errors | Should -BeNullOrEmpty
    }

    It 'sets $ErrorActionPreference = Stop' {
        $s.Text | Should -Match '\$ErrorActionPreference\s*=\s*"Stop"'
    }

    It 'is environment-neutral: no ADI-specific strings' {
        $s.Text | Should -Not -Match '(?i)analog'
    }

    It 'is standalone: never references deploy.ps1' {
        $s.Text | Should -Not -Match 'deploy\.ps1'
    }

    It 'exposes a -PlanOnly switch' {
        $s.Ast.ParamBlock.Parameters.Name.VariablePath.UserPath | Should -Contain 'PlanOnly'
    }

    It 'gates actions on $PlanOnly through Invoke-PlanOrAction' {
        $fn = Find-FunctionDefinition $s 'Invoke-PlanOrAction'
        $fn | Should -Not -BeNullOrEmpty
        $fn.Extent.Text | Should -Match '\$PlanOnly'
    }

    It 'Write-Fail throws (repo error model)' {
        $fn = Find-FunctionDefinition $s 'Write-Fail'
        $fn | Should -Not -BeNullOrEmpty
        $fn.Find({ param($node) $node -is [System.Management.Automation.Language.ThrowStatementAst] }, $true) |
            Should -Not -BeNullOrEmpty
    }

    It 'checks the native exit code after every icacls invocation' {
        # Same native-exe trap as deploy.ps1: icacls failures are silent under
        # ErrorActionPreference=Stop + "| Out-Null". Every call site must guard on
        # $LASTEXITCODE (icacls returns 0 on success).
        $result = Test-IcaclsCallsAreChecked $s
        $result.Count | Should -BeGreaterThan 0
        $result.Unguarded | Should -BeNullOrEmpty -Because "every icacls call must be followed by a `$LASTEXITCODE check; unguarded: $($result.Unguarded -join '; ')"
    }

    It 'grants the pool identity inheritable Modify on config/ so the SQLite DB inherits write' {
        # The app creates config/exchangeadmin.db at runtime; it must inherit Modify (WAL needs
        # write) from the config-dir ACL. (OI)(CI)M = object+container inherit, Modify.
        $s.Text | Should -Match 'Set-DirectoryAcl -Path \$configDir -Identity \$appPoolIdentity -Rights "\(OI\)\(CI\)M"' `
            -Because 'the runtime DB inherits the config-dir ACL - no DB-specific grant needed'
    }

    It 'with -ConfigStorePath: still ACLs config\, ACLs the shared directory too, and refuses a missing shared file (scd-1, scd-4)' {
        # SharedConfigDb-Plan AC9. The local config\ ACL must stay (per-instance jobs DB and
        # seed files live there); the shared directory is ACLed IN ADDITION; the shared file
        # must already exist (the installer never creates it - the cutover script does).
        $s.Ast.ParamBlock.Parameters.Name.VariablePath.UserPath | Should -Contain 'ConfigStorePath'
        $s.Text | Should -Match 'Set-DirectoryAcl -Path \$configDir -Identity \$appPoolIdentity -Rights "\(OI\)\(CI\)M"' `
            -Because 'the per-instance config folder must still be created and ACLed'
        $s.Text | Should -Match '(?s)if \(\$sharedConfigDir\) \{.*?Set-DirectoryAcl -Path \$sharedConfigDir -Identity \$appPoolIdentity -Rights "\(OI\)\(CI\)M"' `
            -Because 'the shared directory needs Modify for the WAL/SHM sidecars, in addition'
        $s.Text.IndexOf('Set-DirectoryAcl -Path $configDir') |
            Should -BeLessThan $s.Text.IndexOf('Set-DirectoryAcl -Path $sharedConfigDir') `
            -Because 'the local ACL is unconditional and comes first; the shared one is additional'
        $s.Text | Should -Match '(?s)if \(-not \(Test-Path -LiteralPath \$ConfigStorePath -PathType Leaf\)\) \{\s*Write-Fail' `
            -Because 'a configured path naming a missing file must be refused, never created'
        $s.Text | Should -Match 'Move-ConfigDbToShared\.ps1' `
            -Because 'the refusal must point the operator at the cutover script that creates the shared database'
        $s.Text | Should -Not -Match 'New-Item[^\r\n]*\$ConfigStorePath' `
            -Because 'the installer must never create the shared database file'
        # Since cdt-37 the rule is SqliteConfigBackup's Get-ConfigStorePathForm, not a StartsWith copy
        # here; the scdi-2 test below runs the check on a UNC path.
        $s.Text | Should -Match '(?s)\$form = Get-ConfigStorePathForm -Value \$Path\s*if \(\$form -eq ''NetworkShare''\) \{\s*Write-Fail' -Because 'a UNC path must be refused'
        $s.Text | Should -Match '-ConfigStorePath \$configStorePlan\.Path\)' `
            -Because 'the generated appsettings must carry the key (the decided one since cdt-11: supplied, else kept)'
    }

    It 'generated appsettings carries ConfigStore:Path only when -ConfigStorePath is given (byte-identical otherwise)' {
        # Runs the real New-AppSettingsObject function (lifted from the script by AST) so the
        # generated object is checked, not just the source text.
        $fn = Find-FunctionDefinition $s 'New-AppSettingsObject'
        $fn | Should -Not -BeNullOrEmpty
        $CertSubject = ''; $SmtpHost = 'localhost'; $SmtpPort = 25; $SmtpUseSsl = $false
        $FromAddress = 'a@b'; $FromName = 'x'; $AdminNotificationEmail = 'a@b'; $DelineaUrl = ''
        $DelineaCredentialTarget = 'Delinea_Client'; $OnPremExchangeServerUri = ''; $ContactEmail = 'a@b'; $PublicBaseUrl = ''
        Invoke-Expression $fn.Extent.Text

        $without = New-AppSettingsObject -Name 'n' -BasePath '/x' -LogPath 'C:\logs' -SecurityAllowedGroups @('g') -SecurityAdminGroups @('g')
        $without.Keys | Should -Not -Contain 'ConfigStore' -Because 'no key means the instance keeps its own database'

        $blank = New-AppSettingsObject -Name 'n' -BasePath '/x' -LogPath 'C:\logs' -SecurityAllowedGroups @('g') -SecurityAdminGroups @('g') -ConfigStorePath '   '
        $blank.Keys | Should -Not -Contain 'ConfigStore' -Because 'a blank value must not write an empty key'
        ($blank | ConvertTo-Json -Depth 20) | Should -Be ($without | ConvertTo-Json -Depth 20) -Because 'byte-identical without the key'

        $with = New-AppSettingsObject -Name 'n' -BasePath '/x' -LogPath 'C:\logs' -SecurityAllowedGroups @('g') -SecurityAdminGroups @('g') -ConfigStorePath 'D:\shared\config\exchangeadmin.db'
        $with.ConfigStore.Path | Should -Be 'D:\shared\config\exchangeadmin.db'
        $json = $with | ConvertTo-Json -Depth 20 | ConvertFrom-Json
        $json.ConfigStore.Path | Should -Be 'D:\shared\config\exchangeadmin.db'
    }

    It '-ConfigStorePath refuses a drive-relative path (<Path>) - rooted is not fully qualified (scdi-2)' -TestCases @(
        @{ Path = 'D:exchangeadmin.db' }
        @{ Path = 'C:config\exchangeadmin.db' }
    ) {
        # Runs the real Assert-LocalAbsoluteFilePath (lifted by AST) with the script's own
        # throwing Write-Fail, and checks the main body actually routes -ConfigStorePath through it.
        # Its rule is SqliteConfigBackup's Get-ConfigStorePathForm (cdt-37), which the installer
        # imports before the check; import it here the same way.
        Import-Module (Join-Path $RepoRoot 'tools/SqliteConfigBackup.psm1') -Force
        $fn = Find-FunctionDefinition $s 'Assert-LocalAbsoluteFilePath'
        $fn | Should -Not -BeNullOrEmpty
        $s.Text | Should -Match 'Assert-LocalAbsoluteFilePath -Path \$ConfigStorePath -Name ''ConfigStorePath''' `
            -Because 'the validation must sit on the -ConfigStorePath path, not only be defined'
        $s.Text | Should -Not -Match 'IsPathRooted\(\$ConfigStorePath\)' `
            -Because 'IsPathRooted accepts D:file.db; the drive-relative form is exactly the bug'
        Invoke-Expression (Find-FunctionDefinition $s 'Write-Fail').Extent.Text
        Invoke-Expression $fn.Extent.Text

        { Assert-LocalAbsoluteFilePath -Path $Path -Name 'ConfigStorePath' } |
            Should -Throw -ExpectedMessage '*absolute local file path*'
        { Assert-LocalAbsoluteFilePath -Path '\\server\share\exchangeadmin.db' -Name 'ConfigStorePath' } |
            Should -Throw -ExpectedMessage '*network share*'
        { Assert-LocalAbsoluteFilePath -Path 'D:\shared\config\exchangeadmin.db' -Name 'ConfigStorePath' } |
            Should -Not -Throw
    }

    It 'still writes the section-access seed file (consumed as first-run DB import seed)' {
        # Fresh installs get correct initial authorization via this seed, which the app imports
        # into section_access on first start. Assert the actual WRITE call + path, not just the
        # helper name (which also appears in the function definition), so removing the write is
        # caught.
        $s.Text | Should -Match 'Write-JsonFileIfMissing[^\r\n]*sectionaccess\.json[^\r\n]*New-SectionAccessSeed' `
            -Because 'a fresh install must actually write the section-access (authorization) seed'
    }
}

Describe 'tools/promote-dev-to-prod.ps1' {
    BeforeAll { $script:s = Get-ScriptUnderTest 'tools/promote-dev-to-prod.ps1' }

    It 'parses without syntax errors' {
        $s.Errors | Should -BeNullOrEmpty
    }

    It 'sets $ErrorActionPreference = Stop' {
        $s.Text | Should -Match '\$ErrorActionPreference\s*=\s*"Stop"'
    }

    It 'defaults to dry run: applying requires an explicit -Apply switch' {
        $s.Ast.ParamBlock.Parameters.Name.VariablePath.UserPath | Should -Contain 'Apply'
    }

    It 'gates robocopy on $Apply and checks exit code >= 8' {
        $fn = Find-FunctionDefinition $s 'Invoke-RobocopyChecked'
        $fn | Should -Not -BeNullOrEmpty
        $fn.Extent.Text | Should -Match '-not \$Apply'
        $fn.Extent.Text | Should -Match '-ge\s+8'
    }

    It 'patches prod appsettings.json atomically via a temp file' {
        $s.Text | Should -Match 'appsettings\.promote\..*\.tmp'
    }

    It 'never copies, replaces or merges the config database (shared DB, decision 2026-09-04)' {
        # SharedConfigDb-Plan AC7: dev and prod open ONE config database, so promotion has no
        # config step at all. The wholesale copy (Copy-SqliteConfigDb), the older JSON-fragment
        # merge, the -SkipConfigFragments opt-out and the prod->dev -Refresh are all gone.
        $s.Text | Should -Not -Match 'Copy-SqliteConfigDb' `
            -Because 'promotion must not replace the shared config database with a copy'
        $s.Text | Should -Not -Match 'Copy-SqliteDbFile' `
            -Because 'promotion must not copy any database file'
        $s.Text | Should -Not -Match 'SkipConfigFragments' `
            -Because 'there is no config promotion to skip'
        $s.Text | Should -Not -Match '\$Refresh' `
            -Because 'a prod->dev refresh would overwrite the shared database with itself or a stale copy'
        $s.Ast.ParamBlock.Parameters.Name.VariablePath.UserPath | Should -Not -Contain 'Refresh'
        $s.Ast.ParamBlock.Parameters.Name.VariablePath.UserPath | Should -Not -Contain 'SkipConfigFragments'
        $s.Text | Should -Not -Match 'function Merge-JsonConfig' `
            -Because 'the JSON-fragment merge helpers are dead after the SQLite cutover'
        $s.Text | Should -Not -Match '\$jsonConfigFiles' `
            -Because 'the per-file fragment list merged files that no longer exist'
    }

    It 'backs up the config DB prod opens (resolved via ConfigStore:Path) before the pool stops' {
        # SharedConfigDb-Plan AC7: the verified backup is taken from the path prod actually opens
        # - the shared file when ConfigStore:Path is set, else prod's own config\exchangeadmin.db
        # - and it is taken BEFORE the prod pool is stopped and anything is mirrored.
        $s.Text | Should -Match 'Resolve-ConfigDbPath -PublishPath \$prod' `
            -Because 'the DB path must come from prod''s appsettings.json, never be hardcoded to config\'
        $s.Text | Should -Match 'Backup-SqliteConfigDb -DbPath \$prodConfigDbPath' `
            -Because 'the backup must use the resolved path'
        $s.Text.IndexOf('Backup-SqliteConfigDb -DbPath') |
            Should -BeLessThan $s.Text.IndexOf('Stop-AppPoolChecked -Name $ProdAppPoolName') `
            -Because 'the backup must exist before prod is touched'
        $s.Text | Should -Not -Match 'Backup-SqliteConfigDb -ConfigDir' `
            -Because 'the ConfigDir form would silently back up a stale per-instance file'
    }

    It 'stops (Write-Fail) when ConfigStore:Path is set and the file is absent (scd-1)' {
        # A configured path that names a missing file is a misconfiguration prod cannot start
        # on; promoting would take prod down. The script must throw before anything changes,
        # and only when the key is SET (the no-key case keeps the "nothing to back up" posture).
        $s.Text | Should -Match 'Get-ConfigStorePathSetting -PublishPath \$prod'
        $s.Text | Should -Match '(?s)if \(\$prodConfigStorePathSetting -and -not \(Test-IsSqliteConfigDbPresent -DbPath \$prodConfigDbPath\)\) \{\s*Write-Fail' `
            -Because 'key set + file absent must Write-Fail (throw)'
        $fn = Find-FunctionDefinition $s 'Write-Fail'
        $fn | Should -Not -BeNullOrEmpty
        $fn.Extent.Text | Should -Match '\bthrow\b'
        $s.Text.IndexOf('Write-Fail "Prod''s ConfigStore:Path') |
            Should -BeLessThan $s.Text.IndexOf('Stop-AppPoolChecked -Name $ProdAppPoolName') `
            -Because 'the check must run before prod is touched'
    }

    It 'rollback restores binaries only and names the DB backup instead of restoring it' {
        # SharedConfigDb-Plan AC7: promotion no longer replaces the config DB, so restoring the
        # verified backup on rollback would roll back DEV''s live data too. The rollback mirror
        # excludes config\ (per-instance jobs DB, any per-instance config DB), never copies
        # exchangeadmin.*.db anywhere, and tells the operator where the backup is.
        $rollbackBlock = [regex]::Match($s.Text, '(?s)Rolling back prod binaries from backup.*?Rolled back prod binaries').Value
        $rollbackBlock | Should -Not -BeNullOrEmpty
        $rollbackBlock | Should -Match "'/XD' 'logs' 'config'" `
            -Because 'the rollback mirror must leave config\ alone'
        $rollbackBlock | Should -Not -Match 'Copy-Item -LiteralPath \$verifiedDb' `
            -Because 'rollback must not overwrite the config DB'
        $rollbackBlock | Should -Not -Match 'Test-SqliteConfigDbIntegrity' `
            -Because 'nothing is restored, so nothing is re-checked'
        $rollbackBlock | Should -Match 'Write-Warn .*NOT restored by the rollback.*\$verifiedDb' `
            -Because 'the operator must be told where the verified backup is'
    }

    It 'keeps every remaining step behind the -Apply gate (plan mode prints, never acts)' {
        foreach ($name in 'Invoke-RobocopyChecked', 'Copy-FileChecked', 'Set-AppsettingsPathBase', 'Stop-AppPoolChecked', 'Start-AppPoolChecked') {
            $fn = Find-FunctionDefinition $s $name
            $fn | Should -Not -BeNullOrEmpty -Because "$name must exist"
            $fn.Extent.Text | Should -Match '-not \$Apply' -Because "$name must honour dry run"
        }
        $s.Text | Should -Match 'Write-Plan "Verified online backup of the config DB at \$prodConfigDbPath' `
            -Because 'the backup step must print its plan in dry run'
        $s.Text | Should -Match 'Write-Plan "Config database at \$prodConfigDbPath is shared with dev and is NOT copied' `
            -Because 'dry run must state that no config copy happens'
    }

    It 'only claims prod was restored from backup when rollback actually completed' {
        # Success-aggregation trap: the closing throw used to assert "Prod has been
        # restored from backup" unconditionally, even when the rollback robocopy failed
        # (exit >= 8), the rollback catch fired, or no backup existed. The
        # restored-from-backup claim must be gated on a flag set ONLY in the rollback
        # success branch, and a distinct "restore manually" message must exist for the
        # paths where rollback did not complete.
        $fn = $s.Ast.Find({
                param($node)
                $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
                $node.Left.Extent.Text -match 'rolledBack'
            }, $true)
        $fn | Should -Not -BeNullOrEmpty -Because 'a flag must track whether rollback succeeded'

        # The success message must be guarded by the rolledBack flag, not thrown blind.
        $s.Text | Should -Match '(?s)if \(\$rolledBack\).*?Prod has been restored from backup' `
            -Because 'the restored-from-backup claim must be conditional on rollback success'
        $s.Text | Should -Match 'rollback did not complete' `
            -Because 'paths where rollback failed/threw/had no backup need an honest message'
    }
}

Describe 'tools/test-delinea.ps1' {
    BeforeAll { $script:s = Get-ScriptUnderTest 'tools/test-delinea.ps1' }

    It 'parses without syntax errors' {
        $s.Errors | Should -BeNullOrEmpty
    }

    It 'sets $ErrorActionPreference = Stop' {
        $s.Text | Should -Match '\$ErrorActionPreference\s*=\s*"Stop"'
    }

    It 'never prints raw Delinea auth response bodies (Constitution line 42)' {
        # The token/secrets endpoints return secret-bearing bodies in $_.ErrorDetails.Message.
        # Echoing them to the console leaks them into transcripts/CI logs. Failures must
        # surface only an HTTP status/reason via Get-SafeHttpError.
        $s.Text | Should -Not -Match 'ErrorDetails' `
            -Because 'the raw Delinea auth response body must never reach the console'
        Find-FunctionDefinition $s 'Get-SafeHttpError' | Should -Not -BeNullOrEmpty `
            -Because 'failures must be reported through the sanitizing helper'
    }

    It 'takes the password as a SecureString, not a plain string' {
        $param = $s.Ast.ParamBlock.Parameters |
            Where-Object { $_.Name.VariablePath.UserPath -eq 'Password' }
        $param | Should -Not -BeNullOrEmpty
        $param.StaticType.Name | Should -Be 'SecureString' `
            -Because 'a [string] password lands in PSReadLine history and the process command line'
    }

    It 'does not hardcode an environment-specific Secret Server endpoint' {
        $s.Text | Should -Not -Match '(?i)secretserver\.ad\.analog\.com' `
            -Because 'tools/ scripts are environment-neutral; ServerUrl is mandatory with no default'
        $serverParam = $s.Ast.ParamBlock.Parameters |
            Where-Object { $_.Name.VariablePath.UserPath -eq 'ServerUrl' }
        $serverParam.DefaultValue | Should -BeNullOrEmpty `
            -Because 'ServerUrl must be supplied by the operator, not defaulted to an internal host'
    }
}

Describe 'tools/Move-ConfigDbToShared.ps1 (static)' {
    BeforeAll { $script:s = Get-ScriptUnderTest 'tools/Move-ConfigDbToShared.ps1' }

    It 'parses without syntax errors' {
        $s.Errors | Should -BeNullOrEmpty
    }

    It 'sets $ErrorActionPreference = Stop' {
        $s.Text | Should -Match '\$ErrorActionPreference\s*=\s*"Stop"'
    }

    It 'is pure ASCII (Windows PowerShell 5.1 reads it - invariant 6)' {
        $bytes = [System.IO.File]::ReadAllBytes($s.Path)
        ($bytes | Where-Object { $_ -gt 127 }).Count | Should -Be 0
    }

    It 'defaults to plan mode: acting requires -Apply' {
        $s.Ast.ParamBlock.Parameters.Name.VariablePath.UserPath | Should -Contain 'Apply'
        $s.Ast.ParamBlock.Parameters.Name.VariablePath.UserPath | Should -Contain 'PlanOnly'
        $s.Text | Should -Match 'if \(-not \$Apply\) \{ \$PlanOnly = \$true \}'
    }

    It 'gates every step on $PlanOnly through Invoke-PlanOrAction' {
        $fn = Find-FunctionDefinition $s 'Invoke-PlanOrAction'
        $fn | Should -Not -BeNullOrEmpty
        $fn.Extent.Text | Should -Match '\$PlanOnly'
        # Eight steps, each through the gate (the stop/start/appsettings/verify helpers wrap it).
        ([regex]::Matches($s.Text, 'Invoke-PlanOrAction "')).Count | Should -BeGreaterOrEqual 8
    }

    It 'Write-Fail throws (repo error model)' {
        $fn = Find-FunctionDefinition $s 'Write-Fail'
        $fn | Should -Not -BeNullOrEmpty
        $fn.Find({ param($node) $node -is [System.Management.Automation.Language.ThrowStatementAst] }, $true) |
            Should -Not -BeNullOrEmpty
    }

    It 'checks the native exit code after every icacls invocation' {
        $result = Test-IcaclsCallsAreChecked $s
        $result.Count | Should -BeGreaterThan 0
        $result.Unguarded | Should -BeNullOrEmpty -Because "unguarded: $($result.Unguarded -join '; ')"
    }

    It 'seeds the shared file from DEV via the verified copy helper, after backing up BOTH databases' {
        $s.Text | Should -Match 'Copy-SqliteDbFile -SourceDbPath \$devDb -DestDbPath \$SharedDbPath'
        $s.Text.IndexOf('Backup-SqliteConfigDb -DbPath $devDb') | Should -BeLessThan $s.Text.IndexOf('Copy-SqliteDbFile -SourceDbPath $devDb')
        $s.Text.IndexOf('Backup-SqliteConfigDb -DbPath $prodDb') | Should -BeLessThan $s.Text.IndexOf('Copy-SqliteDbFile -SourceDbPath $devDb')
        $s.Text | Should -Match 'Test-SqliteConfigDbIntegrity -DbPath \$SharedDbPath'
    }

    It 'writes ConfigStore:Path atomically (temp file, re-parse, move) with a backup of the original' {
        $s.Text | Should -Match 'appsettings\.cutover\..*\.tmp'
        $s.Text | Should -Match 'Copy-Item -LiteralPath \$AppSettingsPath -Destination "\$AppSettingsPath\.\$BackupSuffix"'
        $s.Text | Should -Match 'Move-Item -LiteralPath \$tmp -Destination \$AppSettingsPath -Force'
    }

    It 'prints the restore commands on failure and on success' {
        $fn = Find-FunctionDefinition $s 'Write-RestoreInstructions'
        $fn | Should -Not -BeNullOrEmpty
        ([regex]::Matches($s.Text, 'Write-RestoreInstructions -DevDb')).Count | Should -BeGreaterOrEqual 2
        $s.Text | Should -Match 'SPLIT STATE' -Because 'a failure between the two appsettings writes must be named'
    }
}

Describe 'tools/New-ExchangeAdminWebGraphApps.ps1' {
    BeforeAll { $script:g = Get-ScriptUnderTest 'tools/New-ExchangeAdminWebGraphApps.ps1' }

    # A client secret cannot be read back from Entra. The script created both registrations
    # in a loop and printed every secret in one summary AFTER it, so a failure on the second
    # registration left the first one existing in the tenant with a secret nobody ever saw.

    It 'parses without syntax errors' {
        $g.Errors | Should -BeNullOrEmpty
    }

    It 'prints each registration as it is created, not only in the final summary' {
        # The defect. Buffering an unrecoverable value until the end of a loop that can
        # fail is what loses it.
        $loopAt = $g.Text.IndexOf('foreach ($reg in $Registrations) {', $g.Text.IndexOf('$results = @()'))
        $perItemAt = $g.Text.IndexOf('$record | Format-List')
        $summaryAt = $g.Text.LastIndexOf('$results | Format-List')

        $loopAt | Should -BeGreaterThan 0
        $perItemAt | Should -BeGreaterThan $loopAt
        $perItemAt | Should -BeLessThan $summaryAt -Because 'the per-registration print happens inside the loop'
    }

    It 'resolves every Graph role before creating anything' {
        # The role lookup used to run inside the creation loop, so a hand-edited permission
        # name that does not exist threw after the previous registration was already made.
        $preflightAt = $g.Text.IndexOf('$rolesByRegistration = @{}')
        $createAt = $g.Text.IndexOf('New-MgApplication')

        $preflightAt | Should -BeGreaterThan 0
        $preflightAt | Should -BeLessThan $createAt
    }

    It 'looks up AppRoles in exactly one place' {
        # Two lookups would mean the preflight and the creation loop could disagree.
        ([regex]::Matches($g.Text, '\$graphSp\.AppRoles')).Count | Should -Be 1
    }

    It 'names what was created when it fails partway' {
        # Without this the tenant is half-configured and the only evidence is scrollback.
        $g.Text | Should -Match '(?s)\} catch \{.*?WERE created and still exist'
    }

    It 'rethrows after reporting, so a partial run is not reported as success' {
        $catchBlock = [regex]::Match($g.Text, '(?s)\} catch \{.*?\n\}')
        $catchBlock.Success | Should -BeTrue
        $catchBlock.Value | Should -Match '(?m)^\s*throw\s*$'
    }

    It 'still refuses before creating anything when a name already exists' {
        # The pre-existing preflight must survive the new one.
        $g.Text | Should -Match 'Refusing: app registration\(s\) already exist'
    }

    It 'supports -PlanOnly' {
        $g.Ast.ParamBlock.Parameters.Name.VariablePath.UserPath | Should -Contain 'PlanOnly'
    }
}

Describe 'CI gates' {
    # Lower-priority Wave 4 items: the SQLite-dependent tests were silently skipping in CI
    # because nothing installed sqlite3, and every CI step ran under pwsh so Architectural
    # Invariant 6 - the 5.1-readable scripts - was never exercised anywhere. Since
    # ConfigDbToolInApp-Plan S2 the suites drive the built app instead of sqlite3.
    BeforeAll {
        $script:ci = Get-Content (Join-Path $RepoRoot '.github/workflows/ci.yml') -Raw
        $psJobAt = $script:ci.IndexOf("`n  powershell:")
        $script:psJob = if ($psJobAt -ge 0) { $script:ci.Substring($psJobAt) } else { '' }

        function Get-CiStepScript {
            # One powershell-job step's `run: |` block, de-indented and parsed as the PowerShell
            # it is, so the guards read commands and loops: a commented-out loop, or a retry in
            # another step, cannot satisfy them. It throws - failing the test, never passing it -
            # when the step or its block is not in this file's layout (`- name:` at six spaces,
            # `run: |` at eight).
            param([string]$Name)
            $lines = $script:psJob -split '\r?\n'
            $at = [array]::IndexOf($lines, "      - name: $Name")
            if ($at -lt 0) { throw "the powershell job has no '$Name' step" }
            $runAt = -1
            for ($i = $at + 1; $i -lt $lines.Count -and $lines[$i] -notmatch '^      - '; $i++) {
                if ($lines[$i] -eq '        run: |') { $runAt = $i; break }
            }
            if ($runAt -lt 0) { throw "the '$Name' step has no 'run: |' block" }

            $body = [System.Collections.Generic.List[string]]::new()
            $indent = $null
            for ($i = $runAt + 1; $i -lt $lines.Count; $i++) {
                $line = $lines[$i]
                if ($line.Trim().Length -eq 0) { $body.Add(''); continue }
                $lead = $line.Length - $line.TrimStart(' ').Length
                if ($null -eq $indent) { $indent = $lead }
                if ($lead -lt $indent -or $lead -le 8) { break }
                $body.Add($line.Substring($indent))
            }
            if ($null -eq $indent -or $indent -le 8) { throw "the '$Name' step's 'run: |' block is empty" }

            $text = $body -join "`n"
            $tokens = $null
            $errors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseInput($text, [ref]$tokens, [ref]$errors)
            [pscustomobject]@{ Text = $text; Ast = $ast; Errors = $errors }
        }
    }

    It 'builds the app before running Pester' {
        # The COMMAND, not the step name, for the same reason the sqlite3 guard pinned
        # its command: a renamed or disabled step can keep a matching name.
        $buildAt = $script:psJob.IndexOf('run: dotnet build ExchangeAdminWeb.csproj -c Release')
        $pesterAt = $script:psJob.IndexOf('- name: Pester')

        $buildAt | Should -BeGreaterThan 0 -Because 'the powershell job must build the app the suites run'
        $pesterAt | Should -BeGreaterThan 0
        $buildAt | Should -BeLessThan $pesterAt
    }

    It 'installs the .NET 10 SDK in the powershell job before building' {
        # The runner's default SDK is not guaranteed to be 10 (ConfigDbToolInApp-Plan 3.4).
        $setupAt = $script:psJob.IndexOf('- uses: actions/setup-dotnet@v4')
        $setupAt | Should -BeGreaterThan 0
        $script:psJob.Substring($setupAt) | Should -Match "^- uses: actions/setup-dotnet@v4\s+with:\s+dotnet-version: '10\.0\.x'"
        $setupAt | Should -BeLessThan $script:psJob.IndexOf('run: dotnet build ExchangeAdminWeb.csproj -c Release')
    }

    It 'no longer installs sqlite3' {
        $script:ci | Should -Not -Match 'choco install sqlite'
    }

    It 'fails the build when a test skips in CI' {
        # No suite skips by design. A skip that creeps back would hide exactly the tests
        # the job is there to run, so a skip is a failure.
        $script:ci | Should -Match '\$result\.SkippedCount -gt 0'
    }

    It 'parses the 5.1-critical scripts under Windows PowerShell 5.1' {
        # shell: powershell is Windows PowerShell 5.1 on windows-latest; shell: pwsh is 7.
        $script:ci | Should -Match 'shell: powershell'
        $script:ci | Should -Match 'PSVersion\.Major -ne 5'

        foreach ($f in @('deploy.ps1', 'tools/SqliteConfigBackup.psm1', 'tools/JobStateWarning.psm1', 'tools/AppPoolControl.psm1')) {
            $script:ci | Should -Match ([regex]::Escape($f))
        }
    }

    It 'retries the <Module> install three times, rethrowing the last failure (cdt-39)' -TestCases @(
        @{ Step = 'PSScriptAnalyzer'; Module = 'PSScriptAnalyzer' }
        @{ Step = 'Pester'; Module = 'Pester' }
    ) {
        # cdt-39: the Gallery fails downloads transiently, and the analyzer's single install
        # failed the job on a docs-only commit. The Pester install got this loop in
        # ConfigDbToolInApp S2 with no test; the analyzer install copies it. One test for both
        # holds the two copies to one shape.
        $run = Get-CiStepScript $Step
        $run.Errors | Should -BeNullOrEmpty -Because "the $Step step's script must parse"

        $installs = @($run.Ast.FindAll({
                    param($node)
                    $node -is [System.Management.Automation.Language.CommandAst] -and
                    $node.GetCommandName() -eq 'Install-Module'
                }, $true))
        $installs.Count | Should -Be 1 -Because "the $Step step must install $Module exactly once, inside the retry"
        $install = $installs[0]
        $install.CommandElements[1].Extent.Text | Should -Be $Module
        # GitHub's pwsh shell prepends $ErrorActionPreference = 'stop', which alone routes the
        # failure to the catch; the parameter keeps it there without relying on the runner. Probe
        # (cdt-39): with neither, a failed install skipped the catch and the loop reported success.
        Get-NamedArgumentText $install 'ErrorAction' | Should -Be 'Stop' -Because 'without it the catch sees a failed install only when the shell''s error preference is Stop'

        $try = $install.Parent.Parent.Parent
        $try | Should -BeOfType ([System.Management.Automation.Language.TryStatementAst]) -Because "the $Module install must run inside the retry's try"
        [object]::ReferenceEquals($try.Body.Statements[0], $install.Parent) | Should -BeTrue -Because 'the install is the first statement of the try'
        $try.Body.Statements[1] | Should -BeOfType ([System.Management.Automation.Language.BreakStatementAst]) -Because 'a successful install must leave the loop'

        $for = $try.Parent.Parent
        $for | Should -BeOfType ([System.Management.Automation.Language.ForStatementAst]) -Because 'the try must be a statement of the attempt loop'
        $for.Initializer.Extent.Text | Should -Be '$attempt = 1'
        $for.Condition.Extent.Text | Should -Be '$attempt -le 3'
        $for.Iterator.Extent.Text | Should -Be '$attempt++'

        $try.CatchClauses.Count | Should -Be 1
        $catch = $try.CatchClauses[0]
        $catch.IsCatchAll | Should -BeTrue -Because 'any install failure is retried'
        $last = $catch.Body.Statements[0]
        $last | Should -BeOfType ([System.Management.Automation.Language.IfStatementAst])
        $last.Clauses[0].Item1.Extent.Text | Should -Be '$attempt -eq 3'
        $rethrow = $last.Clauses[0].Item2.Statements[0]
        $rethrow | Should -BeOfType ([System.Management.Automation.Language.ThrowStatementAst]) -Because 'the third failure must fail the step'
        $rethrow.Pipeline | Should -BeNullOrEmpty -Because 'a bare throw rethrows the install error itself'
        @($catch.Body.FindAll({
                    param($node)
                    $node -is [System.Management.Automation.Language.CommandAst] -and
                    $node.GetCommandName() -eq 'Start-Sleep'
                }, $true)).Count | Should -Be 1 -Because 'each retry waits before the next attempt'
    }

    It 'runs <Module> <Version>, the version the dev host runs (<Finding>)' -TestCases @(
        @{ Step = 'PSScriptAnalyzer'; Module = 'PSScriptAnalyzer'; Version = '1.25.0'; Runner = 'Invoke-ScriptAnalyzer'; Finding = 'cdt-39' }
        @{ Step = 'Pester'; Module = 'Pester'; Version = '6.2.0'; Runner = 'Invoke-Pester'; Finding = 'cdt-40' }
    ) {
        # So CI and a local run apply the same analyzer rules (cdt-39) and the same test runner
        # (cdt-40), and a new release of either cannot change CI's verdict with nothing in the
        # repo changing. The numbers are pinned here too, so a bump is deliberate. The import is
        # the pin that decides: auto-loading takes the first copy on PSModulePath, not the newest
        # (probed in cdt-39, and for Pester in cdt-40), so an install alone does not.
        $run = Get-CiStepScript $Step
        $run.Errors | Should -BeNullOrEmpty
        $commands = @($run.Ast.FindAll({
                    param($node)
                    $node -is [System.Management.Automation.Language.CommandAst]
                }, $true))

        $install = @($commands | Where-Object { $_.GetCommandName() -eq 'Install-Module' })
        $install.Count | Should -Be 1
        Get-NamedArgumentText $install[0] 'RequiredVersion' | Should -Be $Version -Because "CI installs the $Module version the dev host runs"

        $import = @($commands | Where-Object { $_.GetCommandName() -eq 'Import-Module' -and $_.CommandElements[1].Extent.Text -eq $Module })
        $import.Count | Should -Be 1 -Because 'installing a version does not make it the copy that loads; the import does'
        Get-NamedArgumentText $import[0] 'RequiredVersion' | Should -Be $Version
        Get-NamedArgumentText $import[0] 'ErrorAction' | Should -Be 'Stop' -Because "a missing $Version must fail the step, not fall back to another copy"
        $import[0].Parent.Parent | Should -BeOfType ([System.Management.Automation.Language.NamedBlockAst]) -Because 'the import runs on every path, not inside the loop or a branch'

        $runs = @($commands | Where-Object { $_.GetCommandName() -eq $Runner })
        $runs.Count | Should -Be 1
        $import[0].Extent.StartOffset | Should -BeGreaterThan $install[0].Extent.EndOffset -Because 'the import follows the install'
        $import[0].Extent.EndOffset | Should -BeLessThan $runs[0].Extent.StartOffset -Because "the pinned $Module is loaded before it runs"
    }
}

Describe 'No sqlite3 on the deploy path (aud-sqlite3-path, ConfigDbToolInApp-Plan S2)' {

    It 'no tracked ops script resolves sqlite3 with Get-Command' {
        # The finding: an elevated deploy ran whatever sqlite3.exe PATH resolved to first,
        # and on the deploy host that was a user-writable, unsigned copy.
        $tracked = @(& git -C $RepoRoot ls-files -- 'tools/*.ps1' 'tools/*.psm1' '*.ps1' '*.psm1')
        $LASTEXITCODE | Should -Be 0
        $scripts = @($tracked | Where-Object { $_ -notmatch '/' -or $_ -like 'tools/*' })
        $scripts.Count | Should -BeGreaterThan 5 -Because 'the scan must actually see the ops scripts'

        $hits = foreach ($rel in $scripts) {
            $text = Get-Content -LiteralPath (Join-Path $RepoRoot $rel) -Raw
            if ($text -match '(?i)Get-Command\s+sqlite3') { $rel }
        }
        @($hits).Count | Should -Be 0 -Because ("these scripts still search PATH for sqlite3: " + (@($hits) -join ', '))
    }

    It 'resolves the .NET host from Program Files, never from PATH' {
        foreach ($rel in 'tools/SqliteConfigBackup.psm1', 'tools/JobStateWarning.psm1') {
            $text = Get-Content -LiteralPath (Join-Path $RepoRoot $rel) -Raw
            $text | Should -Match ([regex]::Escape('$env:ProgramFiles')) -Because "$rel must build the host path from Program Files"
            $text | Should -Not -Match '(?i)Get-Command\s+dotnet' -Because "$rel must not search PATH for the host"
        }
    }
}

Describe 'tools/deploy-pipeline.ps1' {
    BeforeAll { $script:s = Get-ScriptUnderTest 'tools/deploy-pipeline.ps1' }

    It 'parses without syntax errors' {
        $s.Errors | Should -BeNullOrEmpty
    }

    It 'exposes a -PlanOnly switch' {
        $s.Ast.ParamBlock.Parameters.Name.VariablePath.UserPath | Should -Contain 'PlanOnly'
    }

    It 'never applies robocopy exit-code thresholds to PowerShell child scripts' {
        # deploy.ps1 / promote-dev-to-prod.ps1 signal failure by throwing; an
        # exit-code threshold here silently swallowed `exit 1` failures (and a
        # successful run leaves robocopy residue in $LASTEXITCODE anyway).
        $s.Text | Should -Not -Match '\$LASTEXITCODE\s+-ge\s+8'
    }

    It 'asserts the prod apply/consent switches only outside -PlanOnly' {
        # With -PlanOnly the promote script must run its native dry run, so the
        # pipeline may not hardcode Apply / IUnderstandThisOverwritesProd.
        $assignment = [regex]::Match($s.Text, '(?s)if \(-not \$PlanOnly\) \{.*?IUnderstandThisOverwritesProd.*?\}')
        $assignment.Success | Should -BeTrue
        ([regex]::Matches($s.Text, 'IUnderstandThisOverwritesProd')).Count | Should -Be 1 -Because 'the consent switch must not be asserted anywhere else'
    }

    It 'never reports a commit match it did not make (provenance fails closed)' {
        # The check stood between a stale build and production and failed OPEN: when either
        # side of the comparison was missing it skipped the comparison and printed
        # "Dev commit matches source HEAD" anyway.
        #
        # Matched as condition-plus-statement, not as the message alone. A guard asserting
        # only the text passes while the branch above it is dead - that mistake was made on
        # aud-a38 and caught by a mutation.
        $s.Text | Should -Match '(?s)\} else \{\s*Write-Ok "Dev commit matches source HEAD"'
    }

    It 'treats an unusable git probe as unverified, not as verified' {
        # git is a native executable and does not throw, so the old try/catch caught
        # nothing; an empty answer flowed straight into the skipped comparison.
        $s.Text | Should -Match '\$commitProbeOk = -not \$commitProbeFailed'
        $s.Text | Should -Match 'Cannot verify that dev was built from the current source'
    }

    It 'names the two distinct ways provenance can be unknown' {
        # "git did not answer" and "the manifest has no commit" send the operator to
        # different places.
        $s.Text | Should -Match 'git could not report HEAD'
        $s.Text | Should -Match 'manifest carries no usable gitCommit'
    }

    It 'makes promoting without verified provenance an explicit switch' {
        $s.Ast.ParamBlock.Parameters.Name.VariablePath.UserPath | Should -Contain 'AllowUnverifiedProvenance'
        $s.Text | Should -Match 'if \(-not \$AllowUnverifiedProvenance\)'
    }

    It 'does not let a failed status probe read as a clean tree' {
        # The drift warning was gated on a value that is $false both when the tree is clean
        # and when git failed, so silence meant either.
        $s.Text | Should -Match 'if \(-not \$statusProbeOk\)'
        $s.Text | Should -Match 'has NOT been checked'
    }

    It 'survives git being absent, so -AllowUnverifiedProvenance is usable' {
        # Review: the exit-code check was right, but a MISSING git raises
        # CommandNotFoundException before any exit code exists, and with
        # $ErrorActionPreference = Stop that killed the run instead of reporting
        # unverifiable provenance - so the override could not be used on a host without git.
        $s.Text | Should -Match 'try \{ \$commitProbe = git'
        $s.Text | Should -Match 'catch \{ \$commitProbeFailed = \$true \}'
    }

    It 'does not crash on a short or placeholder gitCommit' {
        # "unknown" is what the manifest writer records when it cannot resolve HEAD.
        # Substring(0,8) on it threw ArgumentOutOfRangeException, replacing a clear
        # "dev is stale" message with a crash.
        $s.Text | Should -Match '\$manifest\.gitCommit\.Length -ge 8'
        $s.Text | Should -Match "gitCommit -eq .unknown."
    }

    It 'never claims that config was promoted (the config database is shared - scd-4)' {
        # SharedConfigDb-Plan AC6: help text and final messages must not tell the operator that
        # module configs or settings were promoted from dev; nothing is copied any more.
        $s.Text | Should -Not -Match '(?i)promoted from dev' `
            -Because 'the shared config database is never copied by promotion'
        $s.Text | Should -Not -Match '(?i)config promotion' `
            -Because 'there is no config promotion step'
        $s.Text | Should -Not -Match '(?i)dev values win' `
            -Because 'the dev-wins rule was superseded on 2026-09-04'
        $s.Text | Should -Match 'ConfigStore:Path' `
            -Because 'the help text should point the operator at where the shared database is named'
        $s.Text | Should -Not -Match 'SkipConfigFragments|\bRefresh\b' `
            -Because 'the pipeline must not pass switches the promote script no longer has'
    }
}
