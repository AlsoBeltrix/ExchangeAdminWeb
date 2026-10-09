namespace ExchangeAdminWeb.Tests;

/// <summary>
/// The gate on <see cref="RazorSyntax"/>'s own input: a component whose <c>@code</c> block does
/// not parse cleanly is refused by name, unless this file says why it is tolerated and what that
/// costs.
/// </summary>
/// <remarks>
/// <para>
/// THE DEFECT THIS EXISTS FOR, review finding prog-11. A <c>RenderFragment</c> whose value is an
/// inline Razor template (<c>@&lt;text&gt;</c>, <c>@&lt;div&gt;</c>) is written inside the
/// <c>@code</c> block. That is markup handed to a C# parser, so the block parses with errors and
/// every tree-shaped question about the text after the template is answered out of Roslyn's error
/// recovery instead of out of a parse. Three pages do it: Migration.razor, ServiceHealth.razor and
/// AdminBulkJobs.razor. Nothing anywhere said so, and the two guards that read those trees -
/// <see cref="ProgressScan"/> and <see cref="EventSubscriptionScan"/> - had no way to know they
/// were being handed a guess.
/// </para>
/// <para>
/// WHY A GATE AND NOT A FIX. Parsing the templates properly means
/// <c>Microsoft.AspNetCore.Razor.Language</c> in the test project, which is a larger change than
/// the finding warrants, and simply recording the limitation in prose leaves a blind spot that
/// grows every time somebody adds a fourth template. The gate is the middle: the blind spot is
/// still there, but it is NAMED, it is three pages wide, and a fourth cannot join them without an
/// entry being written here.
/// </para>
/// <para>
/// THE ALLOWLIST IS A CEILING, NOT A FLOOR. An entry whose page parses cleanly is refused
/// (<see cref="AllowlistFaults"/>), so taking the template markup out of a page forces its entry
/// out too. Without that rule the list only ever grows, and a list that only grows is a list
/// nobody reads.
/// </para>
/// <para>
/// WHAT IS ACTUALLY LOST, measured rather than assumed - the detail is in each entry's reason.
/// Lexical facts survive error recovery: comment trivia and string-literal tokens are still
/// produced for the whole block, so <see cref="RazorSyntax.CodeView"/> and
/// <see cref="RazorSyntax.CommentsBlanked"/> blank the same characters they would on a clean
/// parse, and the progress scanner's method table is a regex over those views rather than over the
/// tree. Structure does not survive: <c>MethodDeclarationSyntax</c> recovery stops at the template.
/// <see cref="EventSubscriptionScan"/> is the only reader of structure, and on structure-less text
/// it still FINDS a <c>+=</c> but finds no enclosing method for it, so it refuses the subscription
/// rather than clearing it. That is the direction a guard must fail in - but it is Roslyn's
/// recovery behaviour, not a contract, and resting on it silently is what prog-11 objected to.
/// </para>
/// </remarks>
internal static class RazorParseGate
{
    /// <summary>One file as the gate reads it: a name for the message, and the text to parse.</summary>
    internal sealed record Source(string Name, string Raw);

    /// <summary>
    /// A component whose <c>@code</c> block is known not to parse, with the reason it is
    /// tolerated and what the two guards lose on it.
    /// </summary>
    internal sealed record TemplateMarkupPage(string Page, string Reason);

    /// <summary>
    /// The three pages, and only these three. Every line count and span in these reasons was
    /// measured against the files as they stood when prog-11 was closed; they are the shape of the
    /// damage, not a promise it will stay that size.
    /// </summary>
    internal static readonly IReadOnlyList<TemplateMarkupPage> Allowed =
    [
        new TemplateMarkupPage(
            "Migration.razor",
            "three inline Razor templates inside the @code block - RenderFragment values at 1227 "
            + "(@<text>), 1381 and 1445 (@<div>) - and the first is near the TOP of a block running "
            + "1220-4170, so the first parse error is at 1226 and roughly 2,950 lines of the block "
            + "are recovery output. WHAT IS LOST: structure, not lexis. Two method nodes survive out "
            + "of the 118 component-level declarations the regex table finds - OnInitializedAsync "
            + "(1743-1768, body intact, four awaits) and OnParametersSetAsync (1773, signature only) "
            + "- so almost every method on this page sits inside no method node at all. Comment "
            + "trivia and literal tokens ARE still produced for the whole block: all 1,098 quote "
            + "characters in it are blanked in the code view, and a comment and a string injected "
            + "after the template were blanked in both views. The progress scanner reads those views "
            + "and a regex, never the tree, so none of this page's 61 registered operations rests on "
            + "a structural claim. EventSubscriptionScan is the only structural reader, and this page "
            + "carries no subscription; a += injected after the template was SEEN and refused as "
            + "'<no enclosing method>', so the invisible-subscription hazard prog-11 feared is not "
            + "how this fails today"),

        new TemplateMarkupPage(
            "ServiceHealth.razor",
            "one inline Razor template inside the @code block - a RenderFragment value at 221 "
            + "(@<div>) near the top of a block running 196-540 - so the first parse error is at 220 "
            + "and most of the block is recovery output. WHAT IS LOST: the same split as Migration. "
            + "Two method nodes survive out of the 19 component-level declarations the regex table "
            + "finds, OnInitializedAsync (316-340) and OnAfterRenderAsync (342-352); the rest sit "
            + "inside no method node. The blanked views are unaffected, so the six registered "
            + "operations on this page are judged against the same text they would be on a clean "
            + "parse. The page carries no subscription, and a += injected after the template was seen "
            + "and refused as '<no enclosing method>'"),

        new TemplateMarkupPage(
            "AdminBulkJobs.razor",
            "one inline Razor template inside the @code block - the RenderFragment DetailsBody at "
            + "368, whose @<div> opens at 369 - and it is the LAST member of a block running 138-404, "
            + "which is why this page loses the least of the three. The first parse error is at 368 "
            + "and all 13 component-level methods recover intact, so the only structure-less text is "
            + "the fragment body itself. The live subscription at 181, BulkJobs.JobChanged += "
            + "OnJobChanged, is inside OnInitializedAsync (154-184), ahead of the template and "
            + "recovered whole: EventSubscriptionScan judges it normally today and credits the "
            + "'if (_disposed) return;' at 178-179, verified by deleting that guard and watching "
            + "NoComponentSubscribesToAnEventAfterAnAwaitWithoutADisposedGuard fail. Move a "
            + "subscription BELOW DetailsBody and it would be refused as '<no enclosing method>' "
            + "rather than cleared"),
    ];

    /// <summary>
    /// Every component on disk, read the same way <see cref="EventSubscriptionScan"/> reads them -
    /// from the filesystem, never from a list - so a new page cannot arrive outside this gate.
    /// </summary>
    internal static IReadOnlyList<Source> LiveSources() =>
        EventSubscriptionScan.ComponentFiles()
            .Select(path => new Source(Path.GetFileName(path), File.ReadAllText(path)))
            .ToList();

    private static readonly Dictionary<string, RazorSyntax.ParseError?> ErrorCache =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Parse by FILE NAME, never by content alone: a <c>.razor</c> file with no <c>@code</c> block
    /// has no C# in it at all, and anything that guessed from the text would hand its markup to a
    /// C# parser and report the whole page as broken.
    /// </summary>
    private static RazorSyntax.ParseError? ErrorIn(Source source)
    {
        var key = source.Name + "\0" + source.Raw;

        lock (ErrorCache)
        {
            if (ErrorCache.TryGetValue(key, out var cached))
                return cached;

            var error = RazorSyntax.ParseFile(source.Name, source.Raw).FirstError();
            ErrorCache[key] = error;
            return error;
        }
    }

    /// <summary>
    /// The refusal itself: every source that does not parse cleanly and is not on
    /// <paramref name="allowlist"/>. The allowlist is a parameter so the rule can be exercised
    /// against a fixture rather than only against the files that happen to be on disk.
    /// </summary>
    internal static IReadOnlyList<string> UnallowedParseFailures(
        IReadOnlyList<Source> sources,
        IReadOnlyList<TemplateMarkupPage> allowlist)
    {
        var allowed = allowlist.Select(a => a.Page).ToHashSet(StringComparer.Ordinal);

        return sources
            .Select(s => (Source: s, Error: ErrorIn(s)))
            .Where(x => x.Error is not null && !allowed.Contains(x.Source.Name))
            .Select(x => $"{x.Source.Name}: first parse error at line {x.Error!.Line} "
                + $"({x.Error.Id}: {x.Error.Message}), {x.Error.Count} errors in total")
            .OrderBy(m => m, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// What is wrong with the allowlist itself. An entry must name a component that is really
    /// there, must not repeat one, must carry a reason, and - the ceiling rule - must name a page
    /// that really does fail to parse.
    /// </summary>
    internal static IReadOnlyList<string> AllowlistFaults(
        IReadOnlyList<Source> sources,
        IReadOnlyList<TemplateMarkupPage> allowlist)
    {
        var faults = new List<string>();
        var byName = sources.ToLookup(s => s.Name, StringComparer.Ordinal);

        foreach (var duplicate in byName.Where(g => g.Count() > 1))
        {
            faults.Add($"{duplicate.Key}: more than one component on disk carries this file name, so "
                + "an allowlist keyed by name cannot say which one it means");
        }

        foreach (var entry in allowlist.GroupBy(a => a.Page, StringComparer.Ordinal).Where(g => g.Count() > 1))
            faults.Add($"{entry.Key}: listed more than once");

        foreach (var entry in allowlist)
        {
            if (string.IsNullOrWhiteSpace(entry.Reason))
            {
                faults.Add($"{entry.Page}: allowlisted with no reason. An entry with no reason is a "
                    + "blind spot that looks like a decision; say what the page does that Roslyn "
                    + "cannot parse and what the guards lose on it");
            }

            var source = byName[entry.Page].FirstOrDefault();
            if (source is null)
            {
                faults.Add($"{entry.Page}: allowlisted, but no component on disk has that name. "
                    + "Remove the entry, or correct it");
                continue;
            }

            if (ErrorIn(source) is null)
            {
                faults.Add($"{entry.Page}: allowlisted, but its @code block now parses CLEANLY. The "
                    + "allowlist is a ceiling, not a floor: remove the entry so the page is held to "
                    + "the rule again");
            }
        }

        return faults;
    }
}

/// <summary>
/// Review finding prog-11. See <see cref="RazorParseGate"/> for the defect and the shape of the
/// answer.
/// </summary>
public class RazorParseGateTests
{
    /// <summary>
    /// A fixture with an inline template in its <c>@code</c> block, so the fixtures below do not
    /// depend on any real page keeping its templates.
    /// </summary>
    private const string TemplateFixture = """
        <h3>Fixture</h3>

        @code {
            private int count;

            private RenderFragment Row(string name) =>
                @<text>@name</text>;

            private void Bump() => count++;
        }
        """;

    /// <summary>The same fixture with the template taken out, which must parse.</summary>
    private const string CleanFixture = """
        <h3>Fixture</h3>

        @code {
            private int count;

            private void Bump() => count++;
        }
        """;

    private static readonly RazorParseGate.Source Template = new("Fixture.razor", TemplateFixture);
    private static readonly RazorParseGate.Source Clean = new("Clean.razor", CleanFixture);

    /// <summary>
    /// The rule, over every component on disk. Fail closed: a block that does not parse is refused
    /// by name unless the allowlist says why.
    /// </summary>
    [Fact]
    public void EveryComponentsCodeBlockParsesCleanlyOrIsAllowlisted()
    {
        var refused = RazorParseGate.UnallowedParseFailures(
            RazorParseGate.LiveSources(),
            RazorParseGate.Allowed);

        Assert.True(refused.Count == 0,
            "These components have a @code block Roslyn cannot parse, so every structural question "
            + "the guards ask about them - which method a statement is in, what dominates what - is "
            + "answered out of error RECOVERY rather than out of a parse. The usual cause is an "
            + "inline Razor template (@<text>, @<div>) used as a RenderFragment value inside the "
            + "block. Either move the fragment into the markup, or add an entry to "
            + "RazorParseGate.Allowed saying why it stays and what the guards lose on it:\n"
            + string.Join("\n", refused));
    }

    /// <summary>
    /// The allowlist held to its own rules, including the ceiling: an entry for a page that parses
    /// is refused, so a page cannot keep its exemption after the shape that earned it is gone.
    /// </summary>
    [Fact]
    public void TheAllowlistNamesOnlyRealPagesThatReallyFailToParse()
    {
        var faults = RazorParseGate.AllowlistFaults(
            RazorParseGate.LiveSources(),
            RazorParseGate.Allowed);

        Assert.True(faults.Count == 0,
            "RazorParseGate.Allowed is a ceiling on a known blind spot, so it has to describe the "
            + "blind spot that is actually there:\n" + string.Join("\n", faults));
    }

    /// <summary>
    /// Anti-vacuity for the two tests above, and the standing evidence for prog-11: the three
    /// pages really are the three pages, and the list is not quietly empty.
    /// </summary>
    [Fact]
    public void TheThreeKnownPagesAreTheOnesOnTheList()
    {
        Assert.Equal(
            new[] { "AdminBulkJobs.razor", "Migration.razor", "ServiceHealth.razor" },
            RazorParseGate.Allowed.Select(a => a.Page).OrderBy(p => p, StringComparer.Ordinal).ToArray());
    }

    /// <summary>The gate bites: an unlisted page with template markup is refused.</summary>
    [Fact]
    public void AFixturePageWithTemplateMarkupIsRefusedWhenItIsNotAllowlisted()
    {
        var refused = RazorParseGate.UnallowedParseFailures([Template], RazorParseGate.Allowed);

        Assert.Single(refused);
        Assert.Contains("Fixture.razor", refused[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// And it bites only on that: a page with no template markup is not refused, so the test above
    /// is not passing because everything is refused.
    /// </summary>
    [Fact]
    public void AFixturePageWithoutTemplateMarkupIsNotRefused()
    {
        Assert.Empty(RazorParseGate.UnallowedParseFailures([Clean], RazorParseGate.Allowed));
        Assert.Empty(RazorParseGate.UnallowedParseFailures([Template],
            [new RazorParseGate.TemplateMarkupPage("Fixture.razor", "fixture, listed on purpose")]));
    }

    /// <summary>An exemption with nothing written against it is not an exemption.</summary>
    [Fact]
    public void AnAllowlistEntryWithAnEmptyReasonIsRefused()
    {
        foreach (var blank in new[] { string.Empty, "   ", "\t\n" })
        {
            var faults = RazorParseGate.AllowlistFaults([Template],
                [new RazorParseGate.TemplateMarkupPage("Fixture.razor", blank)]);

            Assert.Contains(faults, f => f.Contains("no reason", StringComparison.Ordinal));
        }

        Assert.Empty(RazorParseGate.AllowlistFaults([Template],
            [new RazorParseGate.TemplateMarkupPage("Fixture.razor", "it is a fixture for this test")]));
    }

    /// <summary>
    /// The ceiling rule. A page that parses cannot sit on the list, so removing the template markup
    /// from one of the three forces its entry out and cannot leave a stale exemption behind.
    /// </summary>
    [Fact]
    public void APageThatParsesCleanlyCannotSitOnTheAllowlist()
    {
        var faults = RazorParseGate.AllowlistFaults([Clean],
            [new RazorParseGate.TemplateMarkupPage("Clean.razor", "a perfectly good reason")]);

        Assert.Contains(faults, f => f.Contains("parses CLEANLY", StringComparison.Ordinal));
    }

    /// <summary>An entry naming a page that is not there is stale, and stale is refused too.</summary>
    [Fact]
    public void AnAllowlistEntryNamingAPageThatIsNotThereIsRefused()
    {
        var faults = RazorParseGate.AllowlistFaults([Clean],
            [new RazorParseGate.TemplateMarkupPage("Deleted.razor", "a perfectly good reason")]);

        Assert.Contains(faults, f => f.Contains("no component on disk", StringComparison.Ordinal));
    }
}
