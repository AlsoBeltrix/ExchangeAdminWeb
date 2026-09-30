using System.Text.RegularExpressions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Queue item 23, reported by the owner as a prod blocker: ticking migration batches in rapid
/// succession left a box drawn as ticked while the batch was absent from the right-hand selection
/// pane and the action bar counted one fewer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Root cause: a handler guard on a DOM-synced control.</b> A checkbox is toggled by the
/// BROWSER the instant it is clicked; the server hears about it afterwards. When
/// <c>ToggleBatchSelected</c> opened with <c>if (IsBusy) return;</c> it discarded that tick - and
/// Blazor then sent no correction, because the value it last rendered for that element
/// (<c>false</c>) still matched the unchanged model. The browser's tick stayed on screen with
/// nothing behind it.
/// </para>
/// <para>
/// <b>What made the window wide enough to hit by hand.</b> Under R9 the open batch is derived
/// from the selection, so ticking down to one batch calls <c>AdoptSelectionAsOpenBatch</c>, which
/// fetches that batch's mailboxes from Exchange and holds <c>IsBusy</c> for the whole round trip.
/// Every tick in that window was dropped.
/// </para>
/// <para>
/// <b>Why the repair is removing the guard rather than disabling the box.</b> Owner ruling D2(a)
/// is that these tick boxes are an input to the confirm step, not a value in flight, so they are
/// not given a disabled attribute - which this repository's own registry calls the only safe
/// refusal for a DOM-synced control. With the disabled attribute ruled out, a handler guard is
/// the wrong half of a mechanism, and the only correct option left is not to refuse. Overlapping
/// loads were already safe: <c>batchUsersGeneration</c> exists precisely because S1 made the URL
/// an entry point and the browser's Back button cannot be disabled.
/// </para>
/// <para>
/// <b>This was found once and parked.</b> The ClickGateRegistry entry for this control recorded
/// the contradiction and left it as an open question for the owner, because D2(a) is an owner
/// ruling. It then shipped. These tests exist so the next agent to reach for <c>if (IsBusy)</c>
/// in a tick handler fails a test instead of re-opening the question.
/// </para>
/// <para>
/// Source tripwires, and comments are stripped before matching - the handlers now explain in
/// prose exactly the construct the tests forbid, which is the trap this suite has fallen into
/// three times.
/// </para>
/// </remarks>
public class MigrationBatchTickBoxTests
{
    [Theory]
    [InlineData("private async Task ToggleBatchSelected(")]
    [InlineData("private async Task ToggleSelectAllBatches(")]
    public void ATickHandlerNeverRefusesTheTickWhileThePageIsBusy(string signature)
    {
        var body = MemberBody(PageSource(), signature);

        Assert.DoesNotContain("IsBusy", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("private void ToggleMailboxSelected(")]
    [InlineData("private void ToggleSelectAllMailboxes(")]
    public void TheMailboxTickHandlersStayFreeOfTheSameGuard(string signature)
    {
        // They were always correct. Pinned alongside the batch pair so a later sweep "for
        // consistency" cannot add the guard to all four.
        var body = MemberBody(PageSource(), signature);

        Assert.DoesNotContain("IsBusy", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTickBoxesAreNotGivenADisabledAttributeEither()
    {
        // The other half of the same rule, and the reason the repair had to be removing the
        // guard: owner ruling D2(a) rules out disabling these boxes, so there is no mechanism
        // left by which a tick can be correctly refused.
        var source = StripComments(PageSource());

        // Scanned by element boundary rather than by regex. A `[^>]*?` element match looks
        // right and is not: every one of these carries an @onchange lambda containing "=>", so
        // the match ends inside the attribute and the assertion tests a fragment that could
        // never hold the word. A mutation probe adding the attribute passed against that
        // version, which is the only reason this is written the long way.
        var titles = new[] { "Select for a bulk action", "Select all loaded batches" };
        var checkedAtLeastOne = 0;

        foreach (var title in titles)
        {
            var from = 0;
            while (true)
            {
                var at = source.IndexOf(title, from, StringComparison.Ordinal);
                if (at < 0) break;
                from = at + title.Length;

                var open = source.LastIndexOf("<input", at, StringComparison.Ordinal);
                var close = source.IndexOf("/>", at, StringComparison.Ordinal);
                Assert.True(open >= 0 && close > open, $"could not bound the element titled '{title}'.");

                var element = source[open..(close + 2)];
                Assert.DoesNotContain("disabled", element, StringComparison.Ordinal);
                checkedAtLeastOne++;
            }
        }

        // The loop above passes trivially if the markup is ever restructured out from under it.
        Assert.Equal(3, checkedAtLeastOne);
    }

    [Fact]
    public void TheHandlersSayWhyTheGuardIsAbsent()
    {
        // A missing guard leaves nothing in the code for a reader to notice, so the reason has to
        // be written where the next person to add one will read it. Matched against the RAW
        // source, since the thing being asserted is the comment.
        var source = PageSource();

        Assert.Equal(2, Regex.Matches(source, @"NO IsBusy guard \(item 23\)").Count);
        Assert.Contains("batchUsersGeneration", source, StringComparison.Ordinal);
    }

    // ----- harness -------------------------------------------------------------------------

    private static string PageSource() =>
        File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "Migration.razor"));

    private static string StripComments(string source)
    {
        source = Regex.Replace(source, @"@\*.*?\*@", " ", RegexOptions.Singleline);
        source = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        return Regex.Replace(source, @"//[^\r\n]*", " ");
    }

    /// <summary>The brace-matched body of the named member, comments already stripped.</summary>
    private static string MemberBody(string source, string signatureFragment)
    {
        var start = source.IndexOf(signatureFragment, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signatureFragment}' not found in Migration.razor.");

        var open = source.IndexOf('{', start);
        Assert.True(open >= 0, $"No body brace found after '{signatureFragment}'.");

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0)
                return StripComments(source[(open + 1)..i]);
        }

        Assert.Fail($"Unbalanced braces in the body of '{signatureFragment}'.");
        return string.Empty;
    }
}
