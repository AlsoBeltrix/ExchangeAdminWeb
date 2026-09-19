#Requires -Version 7.0
<#
    Tests for tools/Get-ClickGateAudit.ps1 (docs/ClickGatingAudit-Plan.md slice 0).

    Fixture-driven: synthetic .razor pages in TestDrive, never the real Components/Pages. The
    real pages are the thing the tool is pointed at in anger, and they change with every slice
    of the sweep; asserting against them would make this suite a moving target that fails for
    reasons unrelated to the tool.

    What these tests are for: the tool's value is entirely in its classification rules, and each
    rule here was added because a naive implementation got it wrong during the audit. Every
    Context below names the mistake it pins.
#>

BeforeAll {
    $script:ScriptPath = Join-Path -Path $PSScriptRoot -ChildPath '..' -AdditionalChildPath '..', 'tools', 'Get-ClickGateAudit.ps1' |
        Resolve-Path | Select-Object -ExpandProperty Path

    # Writes $Lines as a .razor page under a fake repo root and returns that root, so the tool
    # can be invoked exactly as it is in production: -Root pointing at a tree with
    # Components/Pages in it.
    function Get-FixtureRepo {
        param(
            [Parameter(Mandatory)][hashtable] $Pages
        )

        $root = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $pagesDir = Join-Path $root 'Components/Pages'
        New-Item -ItemType Directory -Path $pagesDir -Force | Out-Null

        foreach ($name in $Pages.Keys) {
            $body = ($Pages[$name]) -join "`n"
            Set-Content -LiteralPath (Join-Path $pagesDir "$name.razor") -Value $body -Encoding utf8NoBOM
        }

        return $root
    }

    function Invoke-Audit {
        param([Parameter(Mandatory)][string] $Root, [string[]] $Page)

        if ($Page) { & $script:ScriptPath -Root $Root -Page $Page }
        else { & $script:ScriptPath -Root $Root }
    }
}

Describe 'Get-ClickGateAudit' {

    Context 'in-flight flag detection is structural, not by name' {

        It 'counts a flag that is raised and lowered in a finally' {
            # The definition of an in-flight gate: however the operation ends, the flag comes down.
            $root = Get-FixtureRepo -Pages @{
                Sample = @(
                    '@code {'
                    '    private bool isWorking = false;'
                    ''
                    '    private async Task DoWork()'
                    '    {'
                    '        isWorking = true;'
                    '        try { await Svc.RunAsync(); }'
                    '        finally { isWorking = false; }'
                    '    }'
                    '}'
                )
            }

            $row = Invoke-Audit -Root $root
            $row.Flags | Should -Be 1
            $row.FlagNames | Should -Be 'isWorking'
            $row.StuckFlags | Should -Be 0
        }

        It 'does not count a set-once latch' {
            # authChecked / hasSearched / loaded are raised and never lowered. Counting them made
            # every page in the app look broken in an earlier revision of this tool.
            $root = Get-FixtureRepo -Pages @{
                Sample = @(
                    '@code {'
                    '    private bool authChecked = false;'
                    ''
                    '    private async Task Init()'
                    '    {'
                    '        await Svc.LoadAsync();'
                    '        authChecked = true;'
                    '    }'
                    '}'
                )
            }

            (Invoke-Audit -Root $root).Flags | Should -Be 0
        }

        It 'does not count a nullable data field cleared only on the happy path' {
            # A result or selection field is also raised and lowered inside one awaiting method.
            # The finally requirement is the only thing separating it from a real gate; without
            # it the tool reported Migration as having 15 flags instead of its documented 8.
            $root = Get-FixtureRepo -Pages @{
                Sample = @(
                    '@code {'
                    '    private string? report;'
                    ''
                    '    private async Task Show()'
                    '    {'
                    '        report = null;'
                    '        report = await Svc.FetchAsync();'
                    '    }'
                    '}'
                )
            }

            (Invoke-Audit -Root $root).Flags | Should -Be 0
        }

        It 'reports a flag a gate consults but no finally clears as a stuck-flag hazard' {
            # The BlockedSenders / MessageTrace shape. Today it deadens one button; widened into
            # a page-wide predicate it would deaden the whole page, which is why the sweep has to
            # fix these first.
            $root = Get-FixtureRepo -Pages @{
                Sample = @(
                    '@code {'
                    '    private bool isLoading = false;'
                    ''
                    '    private async Task Load()'
                    '    {'
                    '        isLoading = true;'
                    '        await Svc.GetAsync();'
                    '        isLoading = false;'
                    '    }'
                    '}'
                    '<button @onclick="Load" disabled="@isLoading">Load</button>'
                )
            }

            $row = Invoke-Audit -Root $root
            $row.StuckFlags | Should -Be 1
            $row.StuckNames | Should -Be 'isLoading'
        }
    }

    Context 'comments are blanked before matching, not deleted' {

        It 'is not fooled by the words finally and disabled appearing in prose' {
            # Two of the Migration tripwires failed against correct code for exactly this reason:
            # the scanner read "await" and "finally" out of the explanatory comments added
            # alongside the fix.
            $root = Get-FixtureRepo -Pages @{
                Sample = @(
                    '@code {'
                    '    private bool isWorking = false;'
                    ''
                    '    private async Task DoWork()'
                    '    {'
                    '        // This handler is careful to use finally so the flag is never stuck.'
                    '        isWorking = true;'
                    '        await Svc.RunAsync();'
                    '        isWorking = false;'
                    '    }'
                    '}'
                )
            }

            # No real finally exists, so this is a hazard despite the comment claiming otherwise.
            (Invoke-Audit -Root $root).Flags | Should -Be 0
        }

        It 'reports the true line number of a button that follows a comment block' {
            # Blanking rather than deleting is what preserves this. An earlier revision stripped
            # comments outright and shifted every reported line, which is worse than reporting
            # none: it sends the reader to innocent code.
            $lines = @(
                '@* a razor comment'
                '   spanning'
                '   several lines *@'
                '<div>'
                '<button @onclick="Go">Go</button>'
                '</div>'
            )
            $root = Get-FixtureRepo -Pages @{ Sample = $lines }

            $expected = [array]::IndexOf($lines, '<button @onclick="Go">Go</button>') + 1
            (Invoke-Audit -Root $root).UngatedLines | Should -Be "$expected"
        }
    }

    Context 'tag walking is quote-aware' {

        It 'sees the disabled attribute that follows a lambda arrow in the handler' {
            # A naive <button.*?> stops at the > inside "() =>", hiding every attribute after it,
            # so a correctly gated button reads as ungated.
            $root = Get-FixtureRepo -Pages @{
                Sample = @(
                    '@code {'
                    '    private bool isWorking = false;'
                    ''
                    '    private async Task DoWork(int id)'
                    '    {'
                    '        isWorking = true;'
                    '        try { await Svc.RunAsync(id); }'
                    '        finally { isWorking = false; }'
                    '    }'
                    '}'
                    '<button @onclick="() => DoWork(1)" disabled="@isWorking">Go</button>'
                )
            }

            $row = Invoke-Audit -Root $root
            $row.Buttons | Should -Be 1
            $row.Ungated | Should -Be 0
        }

        It 'does not treat a prose apostrophe inside a value as an opening quote' {
            # The measured defect. GroupManagement's per-row Remove carries a title whose Razor
            # expression holds a C# string reading "This is the member's primary group". The inner
            # string closes the attribute value and the apostrophe then opened a quote state in the
            # middle of the tag, so this scanner never found that tag's real ">", ran it to the end
            # of the file and swallowed the Manage button below it: 13 buttons reported on a page
            # with 14, and nothing said anything was wrong.
            #
            # Cheapest broken implementation that still passes? None. Returning no tags fails
            # Buttons; returning the old runaway tag fails Buttons AND Ungated AND UngatedLines,
            # since a tag holding the rest of the file contains the word "disabled"; dropping
            # quote-awareness entirely fails on the lambda in the first handler. Hard-coding a
            # count fails UngatedLines, which names the line the SECOND button is on.
            $root = Get-FixtureRepo -Pages @{
                Sample = @(
                    '<button @onclick="() => Remove(m)" title="@(x ? "This is the member''s primary group." : null)" disabled="@isWorking">Remove</button>'
                    '<button @onclick="Manage">Manage</button>'
                    '@code {'
                    '    private bool isWorking = false;'
                    '    private string? report;'
                    ''
                    '    private async Task Go()'
                    '    {'
                    '        isWorking = true;'
                    '        try { await Svc.RunAsync(); }'
                    '        finally { isWorking = false; }'
                    '    }'
                    ''
                    '    private async Task Show()'
                    '    {'
                    '        report = null;'
                    '        report = await Svc.FetchAsync();'
                    '    }'
                    '}'
                )
            }

            $row = Invoke-Audit -Root $root
            $row.Buttons | Should -Be 2
            $row.Ungated | Should -Be 1
            $row.UngatedLines | Should -Be '2'

            # The second, quieter half of the same defect, and the reason StuckFlags on the real
            # page fell from 4 to 1 when this was fixed. The stuck-flag test asks whether a GATED
            # BUTTON'S TEXT names a field raised with no finally to lower it. A runaway tag holding
            # the rest of the file names every field in the code block, so it manufactured hazards:
            # "report" here is exactly that shape and must NOT be reported.
            $row.StuckFlags | Should -Be 0
            $row.StuckNames | Should -Be ''
        }

        It 'treats a single-quoted attribute value as a delimiter' {
            # Valid HTML and used in this repo - ConferenceRooms 48 binds
            # @onclick='() => activeTab = "finder"'. A fix that honoured only the double quote
            # would end this tag at the arrow and hide the gate behind it, so the button would read
            # as ungated. The title carries a ">" as well, in a single-quoted value this time.
            #
            # Cheapest broken implementation that still passes? Not "ignore single quotes": that
            # ends the tag at the "=>" and reports Ungated 1. Not "ignore quotes entirely", same
            # reason. Not "return everything" or "return nothing": both fail Buttons.
            $root = Get-FixtureRepo -Pages @{
                Sample = @(
                    '@code {'
                    '    private bool isWorking = false;'
                    ''
                    '    private async Task Go()'
                    '    {'
                    '        isWorking = true;'
                    '        try { await Svc.RunAsync(); }'
                    '        finally { isWorking = false; }'
                    '    }'
                    '}'
                    '<button @onclick=''() => activeTab = "finder"'' title=''a > b'' disabled="@isWorking">Finder</button>'
                )
            }

            $row = Invoke-Audit -Root $root
            $row.Buttons | Should -Be 1
            $row.Ungated | Should -Be 0
        }

        It 'drops a tag that never closes instead of running it to the end of the file' {
            # Pins the behaviour to ClickGateSource.Tags in ExchangeAdminWeb.Tests, which drops it.
            # The two scanners disagreeing is what made the original defect hard to see: each lost
            # a different button and both still reported 13. Emitting an unterminated tag is also
            # actively worse than dropping it, because every gate check in the C# suite is a
            # containment test over the tag text and a tag holding the rest of the file passes all
            # of them.
            #
            # Cheapest broken implementation that still passes? "Return nothing, ever" - which
            # every other It in this Context rejects, so it is not available.
            $root = Get-FixtureRepo -Pages @{
                Sample = @('<button @onclick="Go" disabled="@isWorking"')
            }

            (Invoke-Audit -Root $root).Buttons | Should -Be 0
        }
    }

    Context 'button classification' {

        It 'separates ungated, data-only gated, and partially gated buttons' {
            $root = Get-FixtureRepo -Pages @{
                Sample = @(
                    '@code {'
                    '    private bool isSaving = false;'
                    '    private bool isLoading = false;'
                    '    private string? name;'
                    ''
                    '    private async Task Save()'
                    '    {'
                    '        isSaving = true;'
                    '        try { await Svc.SaveAsync(); }'
                    '        finally { isSaving = false; }'
                    '    }'
                    ''
                    '    private async Task Load()'
                    '    {'
                    '        isLoading = true;'
                    '        try { await Svc.LoadAsync(); }'
                    '        finally { isLoading = false; }'
                    '    }'
                    '}'
                    '<button @onclick="Save">No gate at all</button>'
                    '<button @onclick="Load" disabled="@string.IsNullOrEmpty(name)">Data only</button>'
                    '<button @onclick="Save" disabled="@isSaving">Guards only itself</button>'
                )
            }

            $row = Invoke-Audit -Root $root
            $row.Flags | Should -Be 2
            $row.Buttons | Should -Be 3
            $row.Ungated | Should -Be 1
            $row.GateNoFlag | Should -Be 1
            $row.GatePart | Should -Be 1
            $row.Gap | Should -Be 3
        }

        It 'treats a button consulting the page predicate as fully gated' {
            $root = Get-FixtureRepo -Pages @{
                Sample = @(
                    '@code {'
                    '    private bool isSaving = false;'
                    '    private bool isLoading = false;'
                    ''
                    '    private bool IsBusy => isSaving || isLoading;'
                    ''
                    '    private async Task Save()'
                    '    {'
                    '        isSaving = true;'
                    '        try { await Svc.SaveAsync(); }'
                    '        finally { isSaving = false; }'
                    '    }'
                    ''
                    '    private async Task Load()'
                    '    {'
                    '        isLoading = true;'
                    '        try { await Svc.LoadAsync(); }'
                    '        finally { isLoading = false; }'
                    '    }'
                    '}'
                    '<button @onclick="Save" disabled="@IsBusy">Save</button>'
                    '<button @onclick="Load" disabled="@IsBusy">Load</button>'
                )
            }

            $row = Invoke-Audit -Root $root
            $row.Predicate | Should -Be 'IsBusy'
            $row.Gap | Should -Be 0
        }

        It 'counts a submit button with no onclick' {
            $root = Get-FixtureRepo -Pages @{
                Sample = @('<button type="submit">Send</button>')
            }

            (Invoke-Audit -Root $root).Buttons | Should -Be 1
        }
    }

    Context 'non-button click targets' {

        It 'reports anchors and divs carrying an onclick, which disabled cannot reach' {
            # An <a> ignores the disabled attribute entirely, so these can never be gated in
            # markup; the refusal has to live in the handler.
            $lines = @(
                '<a href="#" @onclick="SelectTab">Tab</a>'
                '<div @onclick="Pick">Row</div>'
                '<button @onclick="Go">Go</button>'
            )
            $root = Get-FixtureRepo -Pages @{ Sample = $lines }

            $row = Invoke-Audit -Root $root
            $row.OtherClick | Should -Be 2
            $row.Buttons | Should -Be 1
        }

        It 'does not mistake a longer tag name for a short one' {
            # "<a" must not match "<audio", or every media element becomes a click target.
            $root = Get-FixtureRepo -Pages @{
                Sample = @('<audio @onclick="Play"></audio>')
            }

            (Invoke-Audit -Root $root).OtherClick | Should -Be 0
        }
    }

    Context 'invocation' {

        It 'restricts the survey with -Page, with or without the extension' {
            $root = Get-FixtureRepo -Pages @{
                Alpha = @('<button @onclick="A">A</button>')
                Beta  = @('<button @onclick="B">B</button>')
            }

            (Invoke-Audit -Root $root).Count | Should -Be 2
            (Invoke-Audit -Root $root -Page 'Alpha').Page | Should -Be 'Alpha.razor'
            (Invoke-Audit -Root $root -Page 'Beta.razor').Page | Should -Be 'Beta.razor'
        }

        It 'fails loudly when pointed at a tree with no Components/Pages' {
            $empty = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Path $empty -Force | Out-Null

            { & $script:ScriptPath -Root $empty } | Should -Throw '*Components/Pages not found*'
        }
    }
}
