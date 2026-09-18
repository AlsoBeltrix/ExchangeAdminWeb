#Requires -Version 7
<#
.SYNOPSIS
    Surveys Components/Pages/*.razor for controls that stay clickable while the page is busy.

.DESCRIPTION
    Implements the owner ruling of 2026-09-17 (.agents/decisions.md): "a control is clickable
    only when its click will definitively execute". A Blazor circuit stays interactive across
    every await, so a control left enabled during an async operation can be clicked, accepted,
    and then silently discarded or applied to state that has since been replaced underneath it.
    Guarding a handler against re-entering itself is not enough.

    This is text analysis of Razor markup and the C# inside a .razor file, not semantic
    analysis: a tripwire against drift, not a proof. There is no bUnit harness in this repo, so
    nothing can render a page; that limitation is inherited, not introduced here. Findings are
    candidates for a human read, and docs/ClickGatingAudit-Plan.md records the known false
    positives.

    Detection is STRUCTURAL, never by name. This app's verbs vary too much for a name list
    (actingDeviceId, isCsvProcessing, detailLoading, pendingOp). A field counts as an in-flight
    flag when, inside one method that contains an await, it is both raised (= true, or
    = <token> for a nullable) and lowered (= false / = null) in a finally. The finally is what
    separates a gate from a data field: nullable result and selection fields are also raised and
    lowered in one method, but only on the happy path.

.PARAMETER Root
    Repository root. Defaults to the parent of the directory holding this script.

.PARAMETER OutDir
    Directory to write clickgate-summary.csv and clickgate-detail.csv into. When omitted, no CSV
    is written and the objects are returned to the pipeline.

.PARAMETER Page
    Restrict the survey to these page file names (with or without the .razor extension).

.EXAMPLE
    ./tools/Get-ClickGateAudit.ps1 | Format-Table

.EXAMPLE
    ./tools/Get-ClickGateAudit.ps1 -Page Migration -OutDir $env:TEMP
#>
[CmdletBinding()]
param(
    [string] $Root,
    [string] $OutDir,
    [string[]] $Page
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $Root) {
    $Root = Split-Path -Parent $PSScriptRoot
}

<#
.SYNOPSIS
    Returns the text with the body of every comment replaced by spaces.
.DESCRIPTION
    Blanked rather than deleted, and newlines preserved, so every reported line number still
    refers to the real file. An earlier revision deleted them and shifted every line.

    Stripping at all is mandatory: the scans below look for words that also occur in prose
    ("await", "finally", "disabled", the flag names), and two of the Migration tripwires once
    failed against correct code by reading those words out of explanatory comments.
#>
function Clear-RazorComment {
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)][AllowEmptyString()][string] $Text)

    $blank = { param($m) ($m.Value -replace '[^\r\n]', ' ') }

    $result = [regex]::Replace($Text, '@\*.*?\*@', $blank, 'Singleline')
    $result = [regex]::Replace($result, '/\*.*?\*/', $blank, 'Singleline')
    # Line comments only where no quote opens earlier on the line, so an https:// inside an
    # attribute value, or a // inside a string literal, survives.
    $result = [regex]::Replace($result, '(?m)(?<=^[^"''\r\n]*)//[^\r\n]*', $blank)

    return $result
}

<#
.SYNOPSIS
    Returns every tag of the given name, quote-aware.
.DESCRIPTION
    A naive <button.*?> stops at the first > it meets, and many handlers in this app are lambdas
    - @onclick="() => Delete(...)" - whose arrow ends the match inside the attribute list, hiding
    every attribute after it, including the disabled one being looked for.
#>
function Get-TagText {
    [CmdletBinding()]
    [OutputType([psobject[]])]
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string] $Text,
        [Parameter(Mandatory)][string] $TagName
    )

    $tags = [System.Collections.Generic.List[object]]::new()
    $needle = "<$TagName"
    $i = 0

    while ($i -lt $Text.Length) {
        $i = $Text.IndexOf($needle, $i, [StringComparison]::OrdinalIgnoreCase)
        if ($i -lt 0) { break }

        # "<a" must not match "<audio": the next character has to end the tag name.
        $after = if ($i + $needle.Length -lt $Text.Length) { $Text[$i + $needle.Length] } else { '>' }
        if ($after -notmatch '[\s>/]') {
            $i += $needle.Length
            continue
        }

        $j = $i + $needle.Length
        $quote = [char]0
        while ($j -lt $Text.Length) {
            $c = $Text[$j]
            if ($quote -ne [char]0) {
                if ($c -eq $quote) { $quote = [char]0 }
            }
            elseif ($c -eq '"' -or $c -eq "'") { $quote = $c }
            elseif ($c -eq '>') { break }
            $j++
        }

        $tags.Add([pscustomobject]@{
                Line = ($Text.Substring(0, $i) -split "`n").Count
                Text = $Text.Substring($i, [Math]::Min($j - $i + 1, $Text.Length - $i))
            })

        if ($j -ge $Text.Length) { break }
        $i = $j + 1
    }

    return $tags.ToArray()
}

<#
.SYNOPSIS
    Returns the brace-balanced body of every block introduced by the given pattern.
#>
function Get-BraceBlock {
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string] $Text,
        [Parameter(Mandatory)][string] $Pattern
    )

    $bodies = [System.Collections.Generic.List[string]]::new()

    foreach ($match in [regex]::Matches($Text, $Pattern)) {
        $open = $Text.IndexOf('{', $match.Index + $match.Length - 1)
        if ($open -lt 0) { continue }

        $depth = 1
        $k = $open + 1
        while ($k -lt $Text.Length -and $depth -gt 0) {
            if ($Text[$k] -eq '{') { $depth++ }
            elseif ($Text[$k] -eq '}') { $depth-- }
            $k++
        }

        $bodies.Add($Text.Substring($open + 1, [Math]::Max(0, $k - $open - 2)))
    }

    return $bodies.ToArray()
}

<#
.SYNOPSIS
    Classifies the click gating of a single .razor page.
#>
function Get-PageClickGate {
    [CmdletBinding()]
    [OutputType([psobject])]
    param([Parameter(Mandatory)][string] $Path)

    $src = Clear-RazorComment -Text (Get-Content -Raw -LiteralPath $Path)
    $name = Split-Path -Leaf $Path

    # Declared instance fields, name to type.
    $fields = @{}
    $fieldPattern = '(?m)^\s*(?:private|protected|internal)\s+(?:readonly\s+)?' +
    '([A-Za-z_][A-Za-z0-9_<>,\.\s\?\[\]]*?)\s+([A-Za-z_][A-Za-z0-9_]*)\s*(?:=[^>]|;)'
    foreach ($match in [regex]::Matches($src, $fieldPattern)) {
        $fields[$match.Groups[2].Value] = $match.Groups[1].Value.Trim()
    }

    $flagSet = [System.Collections.Generic.HashSet[string]]::new()
    $raisedNoFinally = [System.Collections.Generic.HashSet[string]]::new()
    $assign = '(?<![\.\w])([A-Za-z_][A-Za-z0-9_]*)\s*=\s*([^;=][^;]*?)\s*;'

    $methodPattern = '(?m)^\s*(?:private|protected|public)\s+(?:async\s+)?' +
    '[A-Za-z_][A-Za-z0-9_<>,\.\s\?\[\]]*\s+[A-Za-z_][A-Za-z0-9_]*\s*\([^)]*\)\s*\{'

    foreach ($body in (Get-BraceBlock -Text $src -Pattern $methodPattern)) {
        if ($body -notmatch '\bawait\b') { continue }

        $raised = [System.Collections.Generic.HashSet[string]]::new()
        $lowered = [System.Collections.Generic.HashSet[string]]::new()

        foreach ($match in [regex]::Matches($body, $assign)) {
            $fieldName = $match.Groups[1].Value
            if (-not $fields.ContainsKey($fieldName)) { continue }

            $value = $match.Groups[2].Value.Trim()
            if ($value -eq 'false' -or $value -eq 'null') { [void]$lowered.Add($fieldName) }
            elseif ($value -eq 'true' -or $fields[$fieldName] -match '\?$') { [void]$raised.Add($fieldName) }
        }

        # Raised AND lowered in the same awaiting method. A field only ever raised is a set-once
        # latch (authChecked, hasSearched, loaded); counting those would make every page look broken.
        $inFlight = @($raised | Where-Object { $lowered.Contains($_) })
        if (-not $inFlight) { continue }

        $finallyLowered = [System.Collections.Generic.HashSet[string]]::new()
        foreach ($cleanup in (Get-BraceBlock -Text $body -Pattern 'finally\s*\{')) {
            foreach ($match in [regex]::Matches($cleanup, $assign)) {
                $value = $match.Groups[2].Value.Trim()
                if ($value -eq 'false' -or $value -eq 'null') {
                    [void]$finallyLowered.Add($match.Groups[1].Value)
                }
            }
        }

        foreach ($fieldName in $inFlight) {
            if ($finallyLowered.Contains($fieldName)) { [void]$flagSet.Add($fieldName) }
            else { [void]$raisedNoFinally.Add($fieldName) }
        }
    }

    $flags = @($flagSet | Sort-Object)

    # A page-level predicate: an expression-bodied bool naming at least two in-flight flags.
    $predicate = '-'
    $predicatePattern = '(?m)^\s*(?:private|protected)\s+bool\s+([A-Za-z_][A-Za-z0-9_]*)\s*=>\s*([^;]+);'
    foreach ($match in [regex]::Matches($src, $predicatePattern)) {
        $named = @($flags | Where-Object { $match.Groups[2].Value -match ('\b' + [regex]::Escape($_) + '\b') })
        if ($named.Count -ge 2) {
            $predicate = $match.Groups[1].Value
            break
        }
    }

    $buttons = @(Get-TagText -Text $src -TagName 'button' |
        Where-Object { $_.Text -match '@onclick' -or $_.Text -match 'type="submit"' })
    $ungated = @($buttons | Where-Object { $_.Text -notmatch '(?i)\bdisabled\b' })
    $gated = @($buttons | Where-Object { $_.Text -match '(?i)\bdisabled\b' })

    # How each gated button's disabled expression relates to the page's in-flight flags.
    $gateNone = @()   # has disabled, names no in-flight flag: gated on data only
    $gatePart = @()   # names some but not all: the "guards only itself" shape
    foreach ($button in $gated) {
        if ($predicate -ne '-' -and $button.Text -match ('\b' + [regex]::Escape($predicate) + '\b')) { continue }

        $named = @($flags | Where-Object { $button.Text -match ('\b' + [regex]::Escape($_) + '\b') })
        if ($named.Count -eq 0) { $gateNone += $button }
        elseif ($named.Count -lt $flags.Count) { $gatePart += $button }
    }

    # Non-button click targets. The disabled attribute does not apply to these at all, so their
    # refusal has to live in the handler; markup can only grey them.
    $nonButton = @()
    foreach ($tag in 'a', 'div', 'span', 'td', 'tr', 'li', 'i', 'svg', 'img', 'label', 'h3', 'h4', 'p') {
        $nonButton += @(Get-TagText -Text $src -TagName $tag | Where-Object { $_.Text -match '@onclick' })
    }

    # Stuck-gate hazard: a field raised in an awaiting method with no finally to lower it, that a
    # control's disabled expression actually consults. Today it deadens one button; widened into a
    # page predicate it would deaden the page. Prerequisite work before any gate lands.
    $gateText = ($gated | ForEach-Object { $_.Text }) -join ' '
    $stuck = @($raisedNoFinally |
        Where-Object { $flags -contains $_ -or $gateText -match ('\b' + [regex]::Escape($_) + '\b') } |
        Sort-Object)

    $narrow = @($gateNone) + @($gatePart)

    return [pscustomobject]@{
        Page            = $name
        Buttons         = $buttons.Count
        Ungated         = $ungated.Count
        GateNoFlag      = $gateNone.Count
        GatePart        = $gatePart.Count
        Gap             = $ungated.Count + $gateNone.Count + $gatePart.Count
        Flags           = $flags.Count
        Predicate       = $predicate
        OtherClick      = $nonButton.Count
        StuckFlags      = $stuck.Count
        FlagNames       = ($flags -join ' ')
        StuckNames      = ($stuck -join ' ')
        UngatedLines    = (($ungated | ForEach-Object { $_.Line }) -join ' ')
        NarrowLines     = (($narrow | ForEach-Object { $_.Line }) -join ' ')
        OtherClickLines = (($nonButton | ForEach-Object { $_.Line } | Sort-Object) -join ' ')
    }
}

$pagesDir = Join-Path $Root 'Components/Pages'
if (-not (Test-Path -LiteralPath $pagesDir)) {
    throw "Components/Pages not found under '$Root'. Pass -Root with the repository root."
}

$files = Get-ChildItem -LiteralPath $pagesDir -Filter *.razor -Recurse | Sort-Object Name
if ($Page) {
    $wanted = $Page | ForEach-Object { if ($_ -like '*.razor') { $_ } else { "$_.razor" } }
    $files = $files | Where-Object { $wanted -contains $_.Name }
}

$rows = foreach ($file in $files) { Get-PageClickGate -Path $file.FullName }

if ($OutDir) {
    if (-not (Test-Path -LiteralPath $OutDir)) {
        New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
    }

    $summary = $rows | Select-Object Page, Buttons, Ungated, GateNoFlag, GatePart, Gap, Flags,
    Predicate, OtherClick, StuckFlags
    $summary | Export-Csv -NoTypeInformation -Path (Join-Path $OutDir 'clickgate-summary.csv')

    $detail = $rows | Select-Object Page, FlagNames, StuckNames, UngatedLines, NarrowLines,
    OtherClickLines
    $detail | Export-Csv -NoTypeInformation -Path (Join-Path $OutDir 'clickgate-detail.csv')

    Write-Verbose "Wrote clickgate-summary.csv and clickgate-detail.csv to $OutDir"
}

return $rows
