#Requires -Version 7.0
<#
.SYNOPSIS
    Reports Claude Code token usage from this machine's transcript files.

.DESCRIPTION
    A RELATIVE instrument, by design (docs/TokenBudget-Plan.md). It measures token counts,
    which are observed facts, and estimates cost at Anthropic first-party list rates, which
    is always an estimate: this deployment bills through a gateway at partner rates. Every
    question the tool exists to answer is a ratio computed the same way on both sides
    (September vs August, Sonnet vs Opus), and ratios survive an unknown constant multiplier.

    Scope: exactly one machine's Claude Code transcripts. It cannot see other dev boxes,
    reviewer harnesses billing through the gateway, or any other account-wide draw, and its
    output says so. Never reconcile its totals against an account budget figure.

    Counting rule: Claude Code writes one JSONL line per assistant content block, so a single
    billed API request appears as 1..n lines carrying IDENTICAL usage (verified 2026-08-27
    against this repo's transcripts). Usage is therefore deduplicated on message.id - one
    count per billed request. Synthetic placeholder entries (model '<synthetic>') carry zero
    usage and are excluded.

    Privacy: the transcript root holds full conversation content. This tool reads and emits
    ONLY numeric usage fields, dates, and session identifiers - never transcript content.

.PARAMETER TranscriptRoot
    Directory of Claude Code *.jsonl transcripts, searched recursively. Defaults to the
    'transcript-root:' entry in .agents/machines.md - machine-specific, so never hardcoded
    here.

.PARAMETER Since
    Inclusive start date (UTC). Default: first day of the current month.

.PARAMETER Until
    Inclusive end date (UTC). Default: today.

.PARAMETER GroupBy
    Day (default), Session, or Total.

.PARAMETER Model
    Rate table used for the cost ESTIMATE. Selects prices only; it does not filter requests.

.PARAMETER Baseline
    Path to a committed baseline (the -AsJson -GroupBy Total shape). Prints deltas of this
    run's totals against it.

.PARAMETER AsJson
    Emit machine-readable JSON instead of the text report.
#>
[CmdletBinding()]
param(
    [string] $TranscriptRoot,
    [datetime] $Since,
    [datetime] $Until,
    [ValidateSet('Day', 'Session', 'Total')]
    [string] $GroupBy = 'Day',
    [ValidateSet('opus-5', 'sonnet-5', 'sonnet-5-intro', 'haiku-4-5')]
    [string] $Model = 'opus-5',
    [string] $Baseline,
    [switch] $AsJson
)

$ErrorActionPreference = 'Stop'

# USD per million tokens. Anthropic first-party list rates, verified 2026-08-14 in
# docs/TokenBudget-Plan.md (L4). 'sonnet-5-intro' is kept because the plan's parameter table
# names it; the 2026-08-14 pricing correction found $2/$10 to be the standing Sonnet 5 rate,
# so the two Sonnet tables are identical.
$rateTables = @{
    'opus-5'         = @{ Input = 5.00; Output = 25.00; CacheRead = 0.50; CacheWrite = 6.25 }
    'sonnet-5'       = @{ Input = 2.00; Output = 10.00; CacheRead = 0.20; CacheWrite = 2.50 }
    'sonnet-5-intro' = @{ Input = 2.00; Output = 10.00; CacheRead = 0.20; CacheWrite = 2.50 }
    'haiku-4-5'      = @{ Input = 1.00; Output = 5.00; CacheRead = 0.10; CacheWrite = 1.25 }
}
$rates = $rateTables[$Model]

if (-not $TranscriptRoot) {
    # The default lives in .agents/machines.md so the script body stays machine-neutral.
    $machinesFile = Join-Path -Path $PSScriptRoot -ChildPath '..' -AdditionalChildPath '.agents', 'machines.md'
    if (-not (Test-Path -LiteralPath $machinesFile)) {
        Write-Error "No -TranscriptRoot given and machines file not found: $machinesFile"
        exit 1
    }
    $entry = Select-String -LiteralPath $machinesFile -Pattern 'transcript-root:\s*`([^`]+)`' |
        Select-Object -First 1
    if (-not $entry) {
        Write-Error "No -TranscriptRoot given and no 'transcript-root:' entry in $machinesFile. Add one (see the ASHBIAMWEB1 section) or pass -TranscriptRoot."
        exit 1
    }
    $TranscriptRoot = $entry.Matches[0].Groups[1].Value
}

if (-not (Test-Path -LiteralPath $TranscriptRoot)) {
    Write-Error "Transcript root does not exist: $TranscriptRoot"
    exit 1
}

if (-not $PSBoundParameters.ContainsKey('Since')) {
    $now = [datetime]::UtcNow
    $Since = [datetime]::new($now.Year, $now.Month, 1)
}
if (-not $PSBoundParameters.ContainsKey('Until')) {
    $Until = [datetime]::UtcNow.Date
}
$sinceCut = $Since.Date
$untilCut = $Until.Date.AddDays(1)   # -Until is inclusive
if ($sinceCut -ge $untilCut) {
    Write-Error "-Since ($($sinceCut.ToString('yyyy-MM-dd'))) must not be after -Until ($($Until.ToString('yyyy-MM-dd')))."
    exit 1
}

$seen = [System.Collections.Generic.HashSet[string]]::new()
$groups = @{}
$malformed = 0

function Get-EmptyAccumulator {
    @{
        Requests = 0; Input = 0L; CacheWrite = 0L; CacheRead = 0L; Output = 0L
        CtxSum = 0L; CtxMax = 0L; Over200K = 0
    }
}

foreach ($file in Get-ChildItem -Path $TranscriptRoot -Recurse -Filter '*.jsonl' -File) {
    # Streaming read: never Get-Content -Raw on a tree this size. The substring filter skips
    # the ~99% of lines that carry no usage; only survivors pay a real JSON parse.
    foreach ($line in [System.IO.File]::ReadLines($file.FullName)) {
        if ($line -notmatch '"output_tokens"') { continue }
        try {
            $entry = $line | ConvertFrom-Json
        }
        catch {
            $malformed++
            continue
        }
        if ($entry.type -ne 'assistant' -or -not $entry.message -or -not $entry.message.usage) { continue }
        if ([string]$entry.message.model -eq '<synthetic>') { continue }
        try {
            $ts = [datetime]::Parse([string]$entry.timestamp, [cultureinfo]::InvariantCulture,
                [System.Globalization.DateTimeStyles]::AdjustToUniversal)
        }
        catch {
            $malformed++
            continue
        }
        if ($ts -lt $sinceCut -or $ts -ge $untilCut) { continue }

        # One count per billed request: content blocks of one response repeat identical usage.
        $key = if ($entry.message.id) { [string]$entry.message.id } else { [string]$entry.uuid }
        if (-not $seen.Add($key)) { continue }

        $groupKey = switch ($GroupBy) {
            'Day' { $ts.ToString('yyyy-MM-dd') }
            'Session' { if ($entry.sessionId) { [string]$entry.sessionId } else { $file.BaseName } }
            'Total' { 'Total' }
        }
        if (-not $groups.ContainsKey($groupKey)) { $groups[$groupKey] = Get-EmptyAccumulator }
        $acc = $groups[$groupKey]

        $usage = $entry.message.usage
        $inTok = [long]$usage.input_tokens
        $cwTok = [long]$usage.cache_creation_input_tokens
        $crTok = [long]$usage.cache_read_input_tokens
        $outTok = [long]$usage.output_tokens
        $context = $inTok + $cwTok + $crTok

        $acc.Requests++
        $acc.Input += $inTok
        $acc.CacheWrite += $cwTok
        $acc.CacheRead += $crTok
        $acc.Output += $outTok
        $acc.CtxSum += $context
        if ($context -gt $acc.CtxMax) { $acc.CtxMax = $context }
        if ($context -gt 200000) { $acc.Over200K++ }
    }
}

function Get-EstimatedCost {
    param($Acc)
    [math]::Round((
            $Acc.Input * $rates.Input +
            $Acc.Output * $rates.Output +
            $Acc.CacheRead * $rates.CacheRead +
            $Acc.CacheWrite * $rates.CacheWrite) / 1e6, 2)
}

function ConvertTo-GroupRecord {
    param([string] $Name, $Acc)
    [ordered]@{
        group            = $Name
        requests         = $Acc.Requests
        inputTokens      = $Acc.Input
        cacheWriteTokens = $Acc.CacheWrite
        cacheReadTokens  = $Acc.CacheRead
        outputTokens     = $Acc.Output
        meanContext      = if ($Acc.Requests -gt 0) { [long][math]::Round($Acc.CtxSum / $Acc.Requests) } else { 0L }
        maxContext       = $Acc.CtxMax
        over200K         = $Acc.Over200K
        estimatedCostUsd = Get-EstimatedCost -Acc $Acc
    }
}

$totals = Get-EmptyAccumulator
foreach ($acc in $groups.Values) {
    $totals.Requests += $acc.Requests
    $totals.Input += $acc.Input
    $totals.CacheWrite += $acc.CacheWrite
    $totals.CacheRead += $acc.CacheRead
    $totals.Output += $acc.Output
    $totals.CtxSum += $acc.CtxSum
    if ($acc.CtxMax -gt $totals.CtxMax) { $totals.CtxMax = $acc.CtxMax }
    $totals.Over200K += $acc.Over200K
}

$groupRecords = foreach ($name in ($groups.Keys | Sort-Object)) {
    ConvertTo-GroupRecord -Name $name -Acc $groups[$name]
}
$groupRecords = @($groupRecords)
$totalRecord = ConvertTo-GroupRecord -Name 'Total' -Acc $totals

# AC1c: every total states its scope. A figure that could be mistaken for whole-account
# spend is a defect, not a formatting choice.
$scope = [ordered]@{
    source         = 'Claude Code JSONL transcripts'
    transcriptRoot = $TranscriptRoot
    machine        = [System.Environment]::MachineName
    since          = $sinceCut.ToString('yyyy-MM-dd')
    until          = $Until.Date.ToString('yyyy-MM-dd')
    excludes       = 'anything not routed through this root: other machines, reviewer harnesses billing through the gateway, other tools and accounts'
}
$rateNote = "estimated, first-party list rates ('$Model': input `$$($rates.Input)/M, output `$$($rates.Output)/M, cache read `$$($rates.CacheRead)/M, cache write `$$($rates.CacheWrite)/M); actual billing goes through a gateway at partner rates"

$baselineDelta = $null
if ($Baseline) {
    if (-not (Test-Path -LiteralPath $Baseline)) {
        Write-Error "Baseline file not found: $Baseline"
        exit 1
    }
    $base = Get-Content -LiteralPath $Baseline -Raw | ConvertFrom-Json
    if (-not $base.totals) {
        Write-Error "Baseline file has no 'totals' object: $Baseline"
        exit 1
    }
    $baselineDelta = [ordered]@{
        baselineFile     = $Baseline
        requests         = $totalRecord.requests - [long]$base.totals.requests
        inputTokens      = $totalRecord.inputTokens - [long]$base.totals.inputTokens
        cacheWriteTokens = $totalRecord.cacheWriteTokens - [long]$base.totals.cacheWriteTokens
        cacheReadTokens  = $totalRecord.cacheReadTokens - [long]$base.totals.cacheReadTokens
        outputTokens     = $totalRecord.outputTokens - [long]$base.totals.outputTokens
        estimatedCostUsd = [math]::Round($totalRecord.estimatedCostUsd - [double]$base.totals.estimatedCostUsd, 2)
    }
}

if ($AsJson) {
    $doc = [ordered]@{
        schema         = 'exchangeadminweb.token-usage/1'
        generatedAtUtc = [datetime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
        scope          = $scope
        groupBy        = $GroupBy
        rateTable      = [ordered]@{
            name           = $Model
            inputPerMUsd   = $rates.Input
            outputPerMUsd  = $rates.Output
            cacheReadPerM  = $rates.CacheRead
            cacheWritePerM = $rates.CacheWrite
            note           = $rateNote
        }
        skippedMalformedLines = $malformed
        groups         = $groupRecords
        totals         = $totalRecord
    }
    if ($baselineDelta) { $doc['baselineDelta'] = $baselineDelta }
    $doc | ConvertTo-Json -Depth 6
    return
}

"Token usage - $($scope.source)"
"  Root     : $($scope.transcriptRoot)"
"  Machine  : $($scope.machine) (this machine only)"
"  Window   : $($scope.since) .. $($scope.until) inclusive (UTC)"
"  Excludes : $($scope.excludes)"
if ($malformed -gt 0) { "  Skipped  : $malformed malformed line(s)" }
''
$fmt = '{0,-38} {1,8} {2,12} {3,14} {4,16} {5,12} {6,10} {7,10} {8,9} {9,12}'
$fmt -f $GroupBy, 'Reqs', 'Input', 'CacheWrite', 'CacheRead', 'Output', 'MeanCtx', 'MaxCtx', '>200K', 'EstCost*'
foreach ($rec in $groupRecords) {
    $fmt -f $rec.group, $rec.requests, $rec.inputTokens, $rec.cacheWriteTokens, $rec.cacheReadTokens,
    $rec.outputTokens, $rec.meanContext, $rec.maxContext, $rec.over200K, ('$' + $rec.estimatedCostUsd)
}
if ($GroupBy -ne 'Total') {
    $fmt -f 'TOTAL', $totalRecord.requests, $totalRecord.inputTokens, $totalRecord.cacheWriteTokens,
    $totalRecord.cacheReadTokens, $totalRecord.outputTokens, $totalRecord.meanContext,
    $totalRecord.maxContext, $totalRecord.over200K, ('$' + $totalRecord.estimatedCostUsd)
}
''
if ($baselineDelta) {
    "Delta vs baseline $($baselineDelta.baselineFile):"
    "  requests $($baselineDelta.requests); input $($baselineDelta.inputTokens); cache-write $($baselineDelta.cacheWriteTokens); cache-read $($baselineDelta.cacheReadTokens); output $($baselineDelta.outputTokens); est cost `$$($baselineDelta.estimatedCostUsd) (*)"
    ''
}
"* All dollar figures are ESTIMATES: $rateNote."
