using System.Text.RegularExpressions;
using ExchangeAdminWeb.Components.Shared;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// The suggestion-row half of the autocomplete gate: the shared-component contract change named
/// in docs/ClickGatingAudit-Plan.md Revision 1 ("The shared-component problem is outside any
/// page's blast radius") and scoped by the owner in .agents/decisions.md 2026-09-18, option A.
/// </summary>
/// <remarks>
/// The defect: all three autocompletes gated only their text input via disabled="@Disabled" and
/// left the suggestion rows ungated. A page that gates the component during a write therefore
/// left a dropdown that was already open fully clickable, and one click called SelectResult,
/// which rewrites the page's selected identity mid-write.
///
/// Two mechanisms close it, because neither is sufficient alone. The markup withholds the rows -
/// an li is not a form element, so disabled= is inert on it and greying the rows would look
/// refused while acting live. And SelectResult refuses on entry, because the diff that removes
/// the rows is a network round-trip away and a mousedown dispatched before it lands still
/// arrives. Plan Revision 1 records exactly this "attribute plus in-handler entry guard, belt and
/// braces" shape for the interleaving question it could not settle from source.
///
/// These are source-text guards for the markup half because the repo has no bUnit harness -
/// nothing renders a component - and a page-level registry such as ClickGateTests cannot see
/// inside a shared component at all. The behaviour half is a real unit test against the extracted
/// predicate, which is why the predicate was extracted.
/// </remarks>
public class AutocompleteSuggestionGateTests
{
    private const string GateCall = "@if (ShouldRenderSuggestions(Disabled, _showDropdown, _results.Count))";
    private const string RowHandler = "@onmousedown=\"() => SelectResult(result)\"";

    public static TheoryData<string, string> Components() => new()
    {
        { "ADIdentityAutocomplete.razor", "private async Task SelectResult(ExchangeAdminWeb.Services.ADSearchResult result)" },
        { "RecipientAutocomplete.razor", "private async Task SelectResult(RecipientSearchResult result)" },
        { "ADGroupAutocomplete.razor", "private async Task SelectResult(ExchangeAdminWeb.Services.ADSearchResult result)" },
    };

    private static string ReadComponent(string fileName) =>
        File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Components", "Shared", fileName))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

    // ---- The rule itself ---------------------------------------------------------------------
    //
    // Mutation floor: neither half of this truth table can be satisfied by a constant. "=> false"
    // passes every suppression case and fails every render case; "=> true" does the reverse. The
    // pair pins the predicate, and deleting it stops the markup compiling.

    [Theory]
    [InlineData(3)]
    [InlineData(1)]
    public void AGatedAutocompleteRendersNoSuggestionRowsAtAll(int resultCount)
    {
        // The whole point of the slice: results are in hand and the dropdown was open, but the
        // page has gated the control, so there is nothing left to click.
        Assert.False(ADIdentityAutocomplete.ShouldRenderSuggestions(true, true, resultCount));
        Assert.False(RecipientAutocomplete.ShouldRenderSuggestions(true, true, resultCount));
        Assert.False(ADGroupAutocomplete.ShouldRenderSuggestions(true, true, resultCount));
    }

    [Fact]
    public void AnUngatedAutocompleteStillShowsItsSuggestions()
    {
        // The other half of the mutation floor, and the backward-compatibility statement for the
        // call sites that pass no Disabled at all: false is the default, and false must render.
        Assert.True(ADIdentityAutocomplete.ShouldRenderSuggestions(false, true, 1));
        Assert.True(RecipientAutocomplete.ShouldRenderSuggestions(false, true, 1));
        Assert.True(ADGroupAutocomplete.ShouldRenderSuggestions(false, true, 1));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    public void TheGateDoesNotDisturbTheTwoConditionsThatAlreadyHidTheDropdown(bool showDropdown, int resultCount)
    {
        // Pre-existing behaviour the contract change must not alter: no results, or a dropdown
        // that was dismissed, still means no rows. Guards against a fix written as a bare
        // "!disabled" that drops the original predicate.
        Assert.False(ADIdentityAutocomplete.ShouldRenderSuggestions(false, showDropdown, resultCount));
        Assert.False(RecipientAutocomplete.ShouldRenderSuggestions(false, showDropdown, resultCount));
        Assert.False(ADGroupAutocomplete.ShouldRenderSuggestions(false, showDropdown, resultCount));
    }

    [Fact]
    public void NotPassingDisabledLeavesAnAutocompleteUngated()
    {
        // Three of the eighteen instantiation sites pass no Disabled at all (ModuleConfig 217,
        // 317 and 615). If a future author decided components should default to gated, those
        // sites would silently stop suggesting and nothing else in the suite would notice.
        // Mutation: add "= true" to any of the three Disabled parameters.
        Assert.False(new ADIdentityAutocomplete().Disabled);
        Assert.False(new RecipientAutocomplete().Disabled);
        Assert.False(new ADGroupAutocomplete().Disabled);
    }

    // ---- The markup actually consults the rule -------------------------------------------------

    [Theory]
    [MemberData(nameof(Components))]
    public void TheSuggestionListIsRenderedOnlyThroughTheGate(string fileName, string _)
    {
        var text = ReadComponent(fileName);

        // Mutation: revert the @if to "_showDropdown && _results.Count > 0" and this fails on
        // both assertions. A predicate that exists but is never called is the failure mode the
        // second assertion exists for - the truth-table tests above would still pass.
        Assert.Contains(GateCall, text, StringComparison.Ordinal);
        Assert.DoesNotContain("@if (_showDropdown", text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Components))]
    public void EverySuggestionRowSitsInsideTheGate(string fileName, string _)
    {
        var text = ReadComponent(fileName);

        // One row template, and it is downstream of the gate. A second one added later - a
        // "recent selections" list, a no-results row carrying a handler - would be ungated by
        // construction, and this is the only assertion in the repo positioned to catch it.
        var rows = Regex.Matches(text, Regex.Escape(RowHandler));
        Assert.Single(rows);
        Assert.True(
            text.IndexOf(GateCall, StringComparison.Ordinal) < rows[0].Index,
            $"{fileName} renders a selectable suggestion row outside the Disabled gate.");
    }

    // ---- The handler refuses too ----------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Components))]
    public void SelectResultRefusesBeforeItTouchesAnything(string fileName, string signature)
    {
        var body = AuditCategoryFilingTests.MethodBody(ReadComponent(fileName), signature);

        // Mutation A: delete the guard, or weaken it to "if (Disabled && false)" - the regex
        // fails. Mutation B: move the guard below "Value = ..." so it refuses after the damage -
        // the first-statement assertion fails while the regex still passes, which is why both
        // are here. This is the msr-1 shape: a guard that runs after the write reads as present
        // to any containment-only assertion.
        Assert.Equal("if (Disabled)", FirstStatement(body));
        Assert.Matches(@"if \(Disabled\)\s*\n\s*return;", body);
    }

    /// <summary>
    /// The first executable line of a method body, skipping the signature, the opening brace,
    /// blank lines and comments.
    /// </summary>
    private static string FirstStatement(string methodBody)
    {
        foreach (var line in methodBody.Split('\n').Skip(1))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed == "{" || trimmed.StartsWith("//", StringComparison.Ordinal))
                continue;

            return trimmed;
        }

        return string.Empty;
    }
}
