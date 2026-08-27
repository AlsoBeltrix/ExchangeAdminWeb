#Requires -Version 7.0
<#
    Tests for tools/Get-TokenUsage.ps1 (docs/TokenBudget-Plan.md S2).

    Fixture-driven: a synthetic *.jsonl tree in TestDrive. The real transcript root is
    machine-specific, mutable, and absent on CI, so no test may read it - every invocation
    passes -TranscriptRoot explicitly (plan AC5).

    The transcripts hold full conversation content; the tool must emit only numbers, dates
    and session ids. That is asserted with a sentinel string planted in fixture content and
    grepped for in the entire output, both text and JSON (plan AC4).
#>

BeforeAll {
    $script:ScriptPath = Join-Path -Path $PSScriptRoot -ChildPath '..' -AdditionalChildPath '..', 'tools', 'Get-TokenUsage.ps1' |
        Resolve-Path | Select-Object -ExpandProperty Path

    $script:Sentinel = 'SENTINEL-TRANSCRIPT-CONTENT-MUST-NEVER-BE-EMITTED'

    # One transcript JSONL line as Claude Code writes it: type assistant, usage under
    # message.usage, one line per content block - duplicates share message.id and repeat
    # IDENTICAL usage.
    function Get-UsageLine {
        param(
            [string] $Time = '2026-08-05T10:00:00.000Z',
            [string] $Session = 'session-aaaa',
            [string] $MsgId = ('msg_' + [guid]::NewGuid().ToString('N')),
            [long] $In = 0, [long] $Cw = 0, [long] $Cr = 0, [long] $Out = 0,
            [string] $Model = 'claude-opus-5'
        )
        @{
            type      = 'assistant'
            timestamp = $Time
            sessionId = $Session
            uuid      = [guid]::NewGuid().ToString()
            message   = @{
                id      = $MsgId
                model   = $Model
                content = @(@{ type = 'text'; text = $script:Sentinel })
                usage   = @{
                    input_tokens                = $In
                    cache_creation_input_tokens = $Cw
                    cache_read_input_tokens     = $Cr
                    output_tokens               = $Out
                }
            }
        } | ConvertTo-Json -Compress -Depth 6
    }

    # A fresh fixture directory holding one .jsonl file per entry of $Files
    # (name -> array of lines). Non-usage chatter lines are the ~99% the filter must skip.
    function Get-FixtureTree {
        param([hashtable] $Files)
        $dir = Join-Path $TestDrive ("tree-{0}" -f [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        foreach ($name in $Files.Keys) {
            $lines = @('{"type":"user","message":{"role":"user","content":"' + $script:Sentinel + '"}}') + @($Files[$name])
            Set-Content -LiteralPath (Join-Path $dir $name) -Value ($lines -join "`n") -Encoding utf8
        }
        return $dir
    }

    function Invoke-Tool {
        param([string] $Root, [string[]] $Extra = @())
        $argList = @('-NoProfile', '-File', $script:ScriptPath, '-TranscriptRoot', $Root,
            '-Since', '2026-08-01', '-Until', '2026-08-31') + $Extra
        $output = & pwsh @argList 2>&1 | Out-String
        [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output }
    }

    function Invoke-ToolJson {
        param([string] $Root, [string[]] $Extra = @())
        $result = Invoke-Tool -Root $Root -Extra (@('-AsJson') + $Extra)
        $result.ExitCode | Should -Be 0
        [pscustomobject]@{ Raw = $result.Output; Doc = ($result.Output | ConvertFrom-Json) }
    }
}

Describe 'Cost estimation per rate table' {
    # One request of exactly 1M tokens in each class, so the estimated cost equals the sum of
    # the four per-million rates - readable straight off the rate table.

    BeforeEach {
        $script:Root = Get-FixtureTree @{ 'a.jsonl' = @(Get-UsageLine -In 1000000 -Cw 1000000 -Cr 1000000 -Out 1000000) }
    }

    It 'opus-5: $5 + $6.25 + $0.50 + $25 per class-million' {
        (Invoke-ToolJson -Root $script:Root -Extra @('-GroupBy', 'Total', '-Model', 'opus-5')).Doc.totals.estimatedCostUsd |
            Should -Be 36.75
    }

    It 'sonnet-5: $2 + $2.50 + $0.20 + $10 per class-million' {
        (Invoke-ToolJson -Root $script:Root -Extra @('-GroupBy', 'Total', '-Model', 'sonnet-5')).Doc.totals.estimatedCostUsd |
            Should -Be 14.70
    }

    It 'sonnet-5-intro matches sonnet-5 (pricing correction collapsed them)' {
        (Invoke-ToolJson -Root $script:Root -Extra @('-GroupBy', 'Total', '-Model', 'sonnet-5-intro')).Doc.totals.estimatedCostUsd |
            Should -Be 14.70
    }

    It 'haiku-4-5: $1 + $1.25 + $0.10 + $5 per class-million' {
        (Invoke-ToolJson -Root $script:Root -Extra @('-GroupBy', 'Total', '-Model', 'haiku-4-5')).Doc.totals.estimatedCostUsd |
            Should -Be 7.35
    }
}

Describe 'Counting and grouping' {

    It 'deduplicates on message.id: content-block lines of one response count once' {
        # Three lines, one billed request, identical usage - the exact shape that made the
        # plan's original baseline overstate every figure.
        $root = Get-FixtureTree @{ 'a.jsonl' = @(
                (Get-UsageLine -MsgId 'msg_dup' -In 10 -Cw 20 -Cr 30 -Out 40),
                (Get-UsageLine -MsgId 'msg_dup' -In 10 -Cw 20 -Cr 30 -Out 40),
                (Get-UsageLine -MsgId 'msg_dup' -In 10 -Cw 20 -Cr 30 -Out 40)
            ) }
        $totals = (Invoke-ToolJson -Root $root -Extra @('-GroupBy', 'Total')).Doc.totals
        $totals.requests | Should -Be 1
        $totals.inputTokens | Should -Be 10
        $totals.cacheWriteTokens | Should -Be 20
        $totals.cacheReadTokens | Should -Be 30
        $totals.outputTokens | Should -Be 40
    }

    It 'groups by day (UTC), split across files' {
        $root = Get-FixtureTree @{
            'a.jsonl' = @(
                (Get-UsageLine -Time '2026-08-05T10:00:00.000Z' -Out 5),
                (Get-UsageLine -Time '2026-08-05T23:59:59.000Z' -Out 7))
            'b.jsonl' = @((Get-UsageLine -Time '2026-08-06T00:00:01.000Z' -Out 11))
        }
        $doc = (Invoke-ToolJson -Root $root -Extra @('-GroupBy', 'Day')).Doc
        @($doc.groups).Count | Should -Be 2
        ($doc.groups | Where-Object group -EQ '2026-08-05').requests | Should -Be 2
        ($doc.groups | Where-Object group -EQ '2026-08-06').requests | Should -Be 1
        $doc.totals.requests | Should -Be 3
        $doc.totals.outputTokens | Should -Be 23
    }

    It 'groups by session id' {
        $root = Get-FixtureTree @{ 'a.jsonl' = @(
                (Get-UsageLine -Session 'session-one' -Out 1),
                (Get-UsageLine -Session 'session-one' -Out 2),
                (Get-UsageLine -Session 'session-two' -Out 4)
            ) }
        $doc = (Invoke-ToolJson -Root $root -Extra @('-GroupBy', 'Session')).Doc
        @($doc.groups).Count | Should -Be 2
        ($doc.groups | Where-Object group -EQ 'session-one').requests | Should -Be 2
        ($doc.groups | Where-Object group -EQ 'session-two').requests | Should -Be 1
    }

    It 'counts requests over 200K context from input + cache classes' {
        # 150K + 60K = 210K crosses; 100K alone does not; output tokens are not context.
        $root = Get-FixtureTree @{ 'a.jsonl' = @(
                (Get-UsageLine -In 150000 -Cr 60000 -Out 500000),
                (Get-UsageLine -Cr 100000)
            ) }
        $totals = (Invoke-ToolJson -Root $root -Extra @('-GroupBy', 'Total')).Doc.totals
        $totals.over200K | Should -Be 1
        $totals.maxContext | Should -Be 210000
        $totals.meanContext | Should -Be 155000
    }

    It 'honours the window: -Until is inclusive, out-of-window requests are dropped' {
        $root = Get-FixtureTree @{ 'a.jsonl' = @(
                (Get-UsageLine -Time '2026-07-31T23:00:00.000Z' -Out 100),
                (Get-UsageLine -Time '2026-08-31T23:00:00.000Z' -Out 1)
            ) }
        $totals = (Invoke-ToolJson -Root $root -Extra @('-GroupBy', 'Total')).Doc.totals
        $totals.requests | Should -Be 1
        $totals.outputTokens | Should -Be 1
    }

    It 'excludes synthetic placeholder entries' {
        $root = Get-FixtureTree @{ 'a.jsonl' = @(
                (Get-UsageLine -Model '<synthetic>'),
                (Get-UsageLine -Out 9)
            ) }
        (Invoke-ToolJson -Root $root -Extra @('-GroupBy', 'Total')).Doc.totals.requests | Should -Be 1
    }
}

Describe 'Robustness' {

    It 'skips a malformed line instead of throwing, and reports it' {
        $root = Get-FixtureTree @{ 'a.jsonl' = @(
                '{"broken json with "output_tokens" inside',
                (Get-UsageLine -Out 3)
            ) }
        $json = Invoke-ToolJson -Root $root -Extra @('-GroupBy', 'Total')
        $json.Doc.totals.requests | Should -Be 1
        $json.Doc.skippedMalformedLines | Should -Be 1
    }

    It 'reports zeros for an empty directory rather than dividing by zero' {
        $dir = Join-Path $TestDrive ("empty-{0}" -f [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        $json = Invoke-ToolJson -Root $dir -Extra @('-GroupBy', 'Total')
        $json.Doc.totals.requests | Should -Be 0
        $json.Doc.totals.meanContext | Should -Be 0
        $json.Doc.totals.estimatedCostUsd | Should -Be 0
    }

    It 'fails loudly on a transcript root that does not exist' {
        (Invoke-Tool -Root (Join-Path $TestDrive 'no-such-root')).ExitCode | Should -Be 1
    }
}

Describe 'Output contract' {

    It '-AsJson round-trips with schema, scope, rate note and matching totals' {
        $root = Get-FixtureTree @{ 'a.jsonl' = @(Get-UsageLine -In 5 -Cw 6 -Cr 7 -Out 8) }
        $json = Invoke-ToolJson -Root $root -Extra @('-GroupBy', 'Total')
        $doc = $json.Doc
        $doc.schema | Should -Be 'exchangeadminweb.token-usage/1'
        $doc.scope.transcriptRoot | Should -Be $root
        # AC1c: the scope must say what the total excludes, so it cannot read as account spend.
        $doc.scope.excludes | Should -Match 'other machines'
        # AC3: no bare dollar figure - the rate table and estimate qualifier travel with it.
        $doc.rateTable.note | Should -Match 'estimated'
        @($doc.groups).Count | Should -Be 1
        $doc.groups[0].requests | Should -Be $doc.totals.requests
        $doc.totals.inputTokens | Should -Be 5
    }

    It 'text report labels the cost as an estimate with its rate table (AC3)' {
        $root = Get-FixtureTree @{ 'a.jsonl' = @(Get-UsageLine -Out 8) }
        $result = Invoke-Tool -Root $root
        $result.ExitCode | Should -Be 0
        $result.Output | Should -Match 'ESTIMATES'
        $result.Output | Should -Match 'opus-5'
        $result.Output | Should -Match 'this machine only'
    }

    It 'never emits transcript content, in either output mode (AC4)' {
        $root = Get-FixtureTree @{ 'a.jsonl' = @(Get-UsageLine -Out 8) }
        (Invoke-Tool -Root $root).Output | Should -Not -Match $script:Sentinel
        (Invoke-Tool -Root $root -Extra @('-AsJson')).Output | Should -Not -Match $script:Sentinel
    }

    It 'prints a delta against a committed baseline' {
        $baseRoot = Get-FixtureTree @{ 'a.jsonl' = @(Get-UsageLine -In 100 -Out 10) }
        $baselineFile = Join-Path $TestDrive 'baseline.json'
        (Invoke-ToolJson -Root $baseRoot -Extra @('-GroupBy', 'Total')).Raw |
            Set-Content -LiteralPath $baselineFile -Encoding utf8

        $newRoot = Get-FixtureTree @{ 'a.jsonl' = @(
                (Get-UsageLine -In 100 -Out 10),
                (Get-UsageLine -In 40 -Out 2)
            ) }
        $doc = (Invoke-ToolJson -Root $newRoot -Extra @('-GroupBy', 'Total', '-Baseline', $baselineFile)).Doc
        $doc.baselineDelta.requests | Should -Be 1
        $doc.baselineDelta.inputTokens | Should -Be 40
        $doc.baselineDelta.outputTokens | Should -Be 2
    }
}
