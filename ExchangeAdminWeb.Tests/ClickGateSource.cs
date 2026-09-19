using System.Text.RegularExpressions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Reads a Blazor page as text for the click-gating tripwires in
/// <see cref="ClickGateTests"/>. There is no bUnit harness in this repo, so nothing can render a
/// .razor page; every gating assertion is necessarily a source-level scan, and this type holds the
/// scanning mechanics so the suite itself reads as rules rather than regex.
/// </summary>
/// <remarks>
/// Generalised from the per-page helpers in <c>MigrationStatusPageTests</c>, which proved the shape
/// on one page. Two lessons from that work are baked in here rather than left to each caller:
/// comments are removed before matching, and tags are walked quote-aware.
/// </remarks>
public sealed class ClickGateSource
{
    private readonly string _raw;

    private ClickGateSource(string file, string raw)
    {
        File = file;
        _raw = raw;
        Text = BlankComments(raw);
    }

    /// <summary>The page's file name, e.g. "Migration.razor". Used in assertion messages.</summary>
    public string File { get; }

    /// <summary>
    /// The page source with every comment blanked out. Scan this, not the raw text.
    /// </summary>
    /// <remarks>
    /// The scans below look for words that also occur in prose - "await", "finally", "disabled",
    /// the flag names themselves - so a comment explaining one of these rules would otherwise be
    /// read as code breaking it. Two of Migration's tripwires failed against correct code for
    /// exactly that reason before comment stripping was added.
    /// </remarks>
    public string Text { get; }

    public static ClickGateSource Load(string pageFile) =>
        FromText(pageFile, System.IO.File.ReadAllText(Path.Combine(PagesDirectory(), pageFile)));

    /// <summary>
    /// A source over <paramref name="raw"/> instead of a file on disk, so the scanning mechanics can
    /// be exercised against a fixture that depends on no page. <paramref name="file"/> is only the
    /// name assertion messages report.
    /// </summary>
    public static ClickGateSource FromText(string file, string raw) => new(file, raw);

    /// <summary>
    /// <paramref name="source"/> with the body of every comment replaced by spaces.
    /// </summary>
    /// <remarks>
    /// Blanked rather than deleted, and newlines are preserved, so every offset and line number
    /// still refers to the real file. Deleting instead shifts every subsequent position, which
    /// matters here because <see cref="EnclosingMethodName"/> and the assertion messages both work
    /// from offsets - a stripped-but-shifted source reports defects at the wrong line, which is
    /// worse than reporting none.
    /// </remarks>
    public static string BlankComments(string source)
    {
        // Razor comments, then C# block comments, then line comments. Line comments are only
        // blanked when no quote opens earlier on the same line, so a "https://" inside an attribute
        // value, or a "//" inside a string literal, survives intact.
        var text = Regex.Replace(source, @"@\*.*?\*@", Blank, RegexOptions.Singleline);
        text = Regex.Replace(text, @"/\*.*?\*/", Blank, RegexOptions.Singleline);
        text = Regex.Replace(text, @"(?m)(?<=^[^""'\r\n]*)//[^\r\n]*", Blank);
        return text;

        static string Blank(Match match) => Regex.Replace(match.Value, @"[^\r\n]", " ");
    }

    /// <summary>A regex matching <paramref name="identifier"/> only as a whole identifier.</summary>
    public static string WholeWord(string identifier) =>
        $@"(?<![A-Za-z0-9_]){Regex.Escape(identifier)}(?![A-Za-z0-9_])";

    /// <summary>The 1-based line number containing <paramref name="index"/>.</summary>
    public int LineAt(int index) => _raw[..index].Count(c => c == '\n') + 1;

    /// <summary>
    /// Every assignment of a non-clearing value to <paramref name="flag"/>: the places that put the
    /// page into a busy state, excluding the clears and the field declaration itself.
    /// </summary>
    public IEnumerable<Match> NonClearingSetters(string flag) => NonClearingSetters(Text, flag);

    /// <inheritdoc cref="NonClearingSetters(string)"/>
    public static IEnumerable<Match> NonClearingSetters(string source, string flag) =>
        Regex.Matches(source, WholeWord(flag) + @"\s*=(?!=)\s*(?<value>[^\r\n]+)")
            .Where(match => match.Groups["value"].Value.Trim() is not ("null;" or "false;"));

    /// <summary>
    /// Every tag of the given name, from "&lt;name" through its closing "&gt;".
    /// </summary>
    /// <remarks>
    /// A naive "&lt;button.*?&gt;" stops at the first "&gt;" it meets, and many handlers in this app
    /// are lambdas - @onclick="() =&gt; Delete(...)" - whose arrow ends the match inside the
    /// attribute list, hiding every attribute after it, including the disabled one this suite is
    /// looking for. This walks the tag with <see cref="IndexOfTagEnd"/> and ignores any "&gt;"
    /// inside a quoted attribute value.
    /// </remarks>
    public IReadOnlyList<Tag> Tags(string tagName)
    {
        var tags = new List<Tag>();
        var needle = "<" + tagName;

        for (var start = Text.IndexOf(needle, StringComparison.OrdinalIgnoreCase); start >= 0;
             start = Text.IndexOf(needle, start + 1, StringComparison.OrdinalIgnoreCase))
        {
            // "<a" must not match "<audio"; the next character has to end the tag name.
            var after = start + needle.Length;
            if (after < Text.Length && !char.IsWhiteSpace(Text[after]) && Text[after] is not ('>' or '/'))
                continue;

            var end = IndexOfTagEnd(Text, after);
            if (end < 0)
                continue;

            tags.Add(new Tag(Text[start..(end + 1)], LineAt(start), start));
        }

        return tags;
    }

    /// <summary>
    /// The index of the "&gt;" that closes the tag whose attribute list begins at
    /// <paramref name="from"/>, or -1 if <paramref name="text"/> ends first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The quote rule is the whole of this method, and it is narrower than it looks: a quote
    /// character opens an attribute value ONLY where it directly follows the "=" that introduces
    /// that value, whitespace allowed between. Anywhere else a quote is just a character.
    /// </para>
    /// <para>
    /// Opening on any quote at all is what this replaces, and it was a measured defect, not a
    /// theoretical one. GroupManagement's per-row Remove carries
    /// title="@(member.IsPrimaryMember ? "This is the member's primary group; ..." : ...)". The
    /// inner C# string closes the attribute value, and the apostrophe in "member's" then opened a
    /// single-quote state in the middle of the tag, after which the tag's real "&gt;" was never
    /// seen: the most destructive per-row control on that page was invisible to every button
    /// assertion in this suite, and to tools/Get-ClickGateAudit.ps1, which lost a different button
    /// to the same cause. Requiring the "=" fixes the class, because an apostrophe inside prose
    /// never follows one.
    /// </para>
    /// <para>
    /// Four shapes this has to get right, all of which occur in Components/Pages: a "&gt;" inside a
    /// value (title="a &gt; b"), a Razor lambda (@onclick="() =&gt; Pick(row)"), a single-quoted
    /// value (@onclick='() =&gt; activeTab = "finder"', ConferenceRooms 48), and the apostrophe
    /// above. TagWalkFindsTheTagBoundariesItClaimsTo in ClickGateTests pins all four plus an
    /// unquoted value, against a fixture rather than a page.
    /// </para>
    /// <para>
    /// Honest limitation. A "&gt;" inside a C# string nested in a Razor expression inside an
    /// attribute value - title="@(x ? "a &gt; b" : null)" - would still end the tag early, because
    /// the nested string's opening quote does not follow an "=" and so does not re-open a value.
    /// No page has that shape today; it was swept for when this was fixed. Closing it properly
    /// means parsing the Razor transition, which is more machinery than a text tripwire earns.
    /// </para>
    /// <para>
    /// A tag that never closes is DROPPED, by returning -1, and both this and the PowerShell
    /// scanner do the same thing with it. Returning the rest of the file instead would be worse
    /// than dropping it: every gate check in this suite is a containment test over the tag text, so
    /// a tag running to EOF passes all of them for the wrong reason. With the "=" rule an
    /// unterminated tag means a file with no "&gt;" after the tag name at all, which cannot compile
    /// as a page.
    /// </para>
    /// </remarks>
    public static int IndexOfTagEnd(string text, int from) => WalkTag(text, from, values: null);

    /// <summary>
    /// The value of a tag's <paramref name="name"/> attribute - "disabled" is the one this suite
    /// asks for - without its quotes, or null when the tag carries no such attribute with a quoted
    /// value. <paramref name="name"/> is matched case-insensitively, so it finds both the HTML
    /// <c>disabled=</c> and a child component's <c>Disabled=</c> parameter.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists because "the clause is somewhere in the tag" is not the same claim as "the clause
    /// is in the gate", and the difference was measured, not imagined. GroupManagement's per-row
    /// Remove carries a title that explains both of its refusals in prose NAMING THE SAME
    /// EXPRESSIONS the disabled attribute tests. A clause registered in
    /// <see cref="ClickGateRegistry"/> and checked by containment over the whole tag therefore kept
    /// passing after it had been deleted from the gate, because the tooltip describing it still
    /// matched. Two more controls on that page have the same shape (its lines 141 and 190, both a
    /// disabled expression mirrored by a title that names its clauses). Anchoring each registered
    /// string by hand closes one entry at a time and rots on the next whitespace change; reading the
    /// attribute value closes the class.
    /// </para>
    /// <para>
    /// Boundary rule: the name must be preceded by whitespace, so <c>aria-disabled=</c> and
    /// <c>data-disabled=</c> are NOT read as <c>disabled=</c>. And the candidates come from
    /// <see cref="WalkTag"/> rather than from a search of the tag text, so the word "disabled"
    /// occurring inside some other attribute's value cannot be mistaken for an attribute.
    /// </para>
    /// <para>
    /// Honest limitations, both of which fail LOUDLY - the caller finds no clause and its assertion
    /// reports the control by name - rather than quietly passing. An unquoted value
    /// (<c>disabled=@IsBusy</c>) is not returned at all, because WalkTag records only quoted spans;
    /// no converted page has one, and that was checked rather than assumed. And a value containing a
    /// nested C# string inherits IndexOfTagEnd's limitation above: the inner quote ends the recorded
    /// span early. No disabled attribute on any converted page contains a quote, also checked.
    /// </para>
    /// </remarks>
    public static string? AttributeValue(string tag, string name)
    {
        var values = new List<(int Start, int End)>();
        WalkTag(tag, 0, values);

        foreach (var (start, end) in values)
        {
            // WalkTag only opens a value directly after an "=", so what precedes the opening quote
            // at start - 1 is that "=", and what precedes THAT is the attribute name.
            var before = tag[..(start - 1)].TrimEnd();
            if (!before.EndsWith('='))
                continue;

            before = before[..^1].TrimEnd();
            if (!before.EndsWith(name, StringComparison.OrdinalIgnoreCase))
                continue;

            // "aria-disabled" and "data-disabled" both end with "disabled" and are different
            // attributes; only a whitespace boundary makes this the attribute that was asked for.
            var nameStart = before.Length - name.Length;
            if (nameStart == 0 || !char.IsWhiteSpace(before[nameStart - 1]))
                continue;

            return tag[start..end];
        }

        return null;
    }

    /// <summary>
    /// The one tag walker. Returns the index of the "&gt;" that closes the tag whose attribute list
    /// begins at <paramref name="from"/>, or -1; and, when <paramref name="values"/> is given,
    /// records the span of every quoted attribute value it passes as (first character, closing
    /// quote). The quote rule is documented on <see cref="IndexOfTagEnd"/>, which is this with no
    /// recording; <see cref="AttributeValue"/> is this with it. Kept as one method on purpose - the
    /// defect IndexOfTagEnd's remarks record was found in a THIRD copy of this walk that had drifted
    /// from the other two.
    /// </summary>
    private static int WalkTag(string text, int from, List<(int Start, int End)>? values)
    {
        var quote = '\0';
        var valueStart = 0;
        var afterEquals = false;

        for (var i = from; i < text.Length; i++)
        {
            var c = text[i];

            if (quote != '\0')
            {
                if (c != quote)
                    continue;

                quote = '\0';
                values?.Add((valueStart, i));
                continue;
            }

            if (afterEquals && c is '"' or '\'')
            {
                quote = c;
                valueStart = i + 1;
                afterEquals = false;
                continue;
            }

            if (c == '>')
                return i;

            if (!char.IsWhiteSpace(c))
                afterEquals = c == '=';
        }

        return -1;
    }

    /// <summary>Every &lt;button&gt; that is actually clickable: it has a handler or submits.</summary>
    public IReadOnlyList<Tag> ClickableButtons() =>
        Tags("button")
            .Where(tag => tag.Text.Contains("@onclick", StringComparison.Ordinal)
                          || tag.Text.Contains("type=\"submit\"", StringComparison.Ordinal))
            .ToList();

    /// <summary>
    /// Every non-button element carrying an @onclick. These ignore the disabled attribute
    /// entirely, so their refusal has to live in the handler; the markup can only grey them.
    /// </summary>
    public IReadOnlyList<Tag> NonButtonClickTargets() =>
        NonButtonTags
            .SelectMany(Tags)
            .Where(tag => tag.Text.Contains("@onclick", StringComparison.Ordinal))
            .OrderBy(tag => tag.Index)
            .ToList();

    private static readonly string[] NonButtonTags =
    {
        "a", "div", "span", "td", "tr", "li", "i", "svg", "img", "label", "h3", "h4", "p",
    };

    /// <summary>The handler named by a tag's @onclick, or an empty string if it has none.</summary>
    public static string HandlerOf(Tag tag) =>
        Regex.Match(tag.Text, @"@onclick=""(?<handler>[^""]*)""").Groups["handler"].Value;

    /// <summary>
    /// The method name a handler expression ultimately calls: "Foo" for both <c>Foo</c> and
    /// <c>() =&gt; Foo(x)</c>, so a guard can be looked for in the method either form reaches.
    /// </summary>
    public static string CalledMethod(string handler)
    {
        var call = Regex.Match(handler, @"(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(");
        return call.Success ? call.Groups["name"].Value : handler.Trim();
    }

    /// <summary>
    /// The name of the method whose body contains <paramref name="index"/>, found by walking back
    /// to the nearest member signature. Lets an assertion start from an occurrence rather than from
    /// a hard-coded list of method names, so a new setter cannot skip the rule by being new.
    /// </summary>
    public string EnclosingMethodName(int index)
    {
        var signatures = Regex.Matches(Text[..index],
            @"\n    (?:private|protected|public)\s+(?:async\s+)?[A-Za-z][^\r\n=]*?\b(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(");

        return signatures.Count > 0 ? signatures[^1].Groups["name"].Value : "";
    }

    /// <summary>The source of a method, from its signature to the next member declaration.</summary>
    public string MethodBody(string methodName) =>
        MemberSource(
            $@"(?:private|protected|public)\s+(?:async\s+)?[A-Za-z][^\r\n=]*?\b{Regex.Escape(methodName)}\s*\(");

    /// <summary>Same, for an expression-bodied property, which has no parameter list.</summary>
    public string MemberBody(string memberName) =>
        MemberSource($@"(?:private|protected|public)\s+[A-Za-z][^\r\n(]*?\b{Regex.Escape(memberName)}\s*=>");

    /// <summary>True if the page declares a member of this name at all.</summary>
    public bool HasMember(string memberName) =>
        Regex.IsMatch(Text, $@"\b{Regex.Escape(memberName)}\s*(?:=>|\()");

    private string MemberSource(string signaturePattern)
    {
        var signature = Regex.Match(Text, signaturePattern);
        if (!signature.Success)
            return "";

        var start = signature.Index;
        var next = Regex.Match(Text[(start + signature.Length)..],
            @"\n    (?:private|protected|public)\s+(?:async\s+)?[A-Za-z]");

        return next.Success
            ? Text.Substring(start, signature.Length + next.Index)
            : Text[start..];
    }

    /// <summary>
    /// The brace-balanced block introduced by <paramref name="opener"/>, so an assertion can be
    /// scoped to a loop, a conditional or a finally rather than the whole method. Returns an empty
    /// string when the opener is absent, leaving the caller to decide whether that is a failure.
    /// </summary>
    public static string ExtractBlock(string source, string opener)
    {
        var start = source.IndexOf(opener, StringComparison.Ordinal);
        if (start < 0)
            return "";

        var open = source.IndexOf('{', start + opener.Length);
        if (open < 0)
            return "";

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0)
                return source[open..(i + 1)];
        }

        return "";
    }

    public static string PagesDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var pages = Path.Combine(dir.FullName, "Components", "Pages");
            if (Directory.Exists(pages))
                return pages;

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Components/Pages from test base directory.");
    }

    /// <summary>A tag occurrence: its text, and where it is, so failures can name a line.</summary>
    public sealed record Tag(string Text, int Line, int Index);
}
