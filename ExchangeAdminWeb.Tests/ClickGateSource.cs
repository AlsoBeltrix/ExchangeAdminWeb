using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

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
    /// <para>
    /// Blanked rather than deleted, and newlines are preserved, so every offset and line number
    /// still refers to the real file. Deleting instead shifts every subsequent position, which
    /// matters here because <see cref="EnclosingMethodName"/> and the assertion messages both work
    /// from offsets - a stripped-but-shifted source reports defects at the wrong line, which is
    /// worse than reporting none.
    /// </para>
    /// <para>
    /// kd-12. Lexed, not matched: <see cref="RazorSyntax.ComponentCommentsBlanked"/>, the comment
    /// view the progress guards read. A C# comment is Roslyn trivia inside an @code block, so a
    /// "/*" or "//" inside a string literal is text, and a comment after a string on the same line
    /// is still a comment; a Razor comment is blanked in the markup. The regexes this replaced
    /// blanked from a "/*" inside a string to the next "*/" wherever it was, and kept any line
    /// comment that followed a quote on its line. In markup, "//" and "/* */" are text, as the code
    /// view already treats them (review finding prog-4b). Every input is read as a component, so a
    /// markup fixture with no @code block is all markup.
    /// </para>
    /// <para>
    /// kd-21. The bodies of the markup's control-flow constructs ("@if", "@foreach" and the rest) are
    /// code with markup in them, and their C# comments are blanked too; see
    /// <see cref="BlankControlFlowComments"/>.
    /// </para>
    /// <para>
    /// kd-31. So are the markup's HTML comments (<see cref="BlankHtmlComments"/>): what one holds is
    /// never a control, so a handler or a flag it names must not be read as one.
    /// </para>
    /// <para>
    /// kd-42. And so are the HTML comments in an @code block's render-fragment templates, which are
    /// markup inside a code region; and in a control-flow body, an element after a brace-less header
    /// ("if (x) &lt;p&gt;...&lt;/p&gt;") is read as markup, not as code.
    /// </para>
    /// </remarks>
    public static string BlankComments(string source) =>
        BlankControlFlowComments(source, BlankHtmlComments(source, RazorSyntax.ComponentCommentsBlanked(source)));

    /// <summary>
    /// kd-21. <paramref name="blanked"/> - <paramref name="source"/> as kd-12's component view left it -
    /// with the C# comments of the markup's control-flow bodies blanked too: the bodies of
    /// "@if", "@foreach", "@for", "@while", "@switch", "@lock", "@using", "@do" and "@try" between the
    /// tags, and of their "else", "else if", "catch", "finally" and "while" continuations.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A body like that is code with markup elements in it, so neither the Roslyn region view (which
    /// covers @code only) nor a regex reads it. Each body is walked from its brace: a markup element
    /// at code level - a start tag through its matching end tag, or a self-closing or void one -
    /// and a "@:" line are skipped as markup; a C# literal is skipped as one Roslyn token, so a "//"
    /// inside a string is not a comment; a "//" or "/*" at code level is a comment and is blanked;
    /// and the braces at code level are counted, so a brace inside a comment, a literal or an
    /// element neither opens nor closes the body. A construct nested inside an element is found by
    /// the same scan and walked on its own.
    /// </para>
    /// <para>
    /// Not <see cref="ExtractMarkupBlock"/>: that balances raw braces over the markup view, which is
    /// right for an assertion's block and wrong here, because the comments being looked for are the
    /// text that can hold a stray brace.
    /// </para>
    /// </remarks>
    private static string BlankControlFlowComments(string source, string blanked)
    {
        var buffer = blanked.ToCharArray();
        var parsed = RazorSyntax.Parse(source);
        var regions = parsed.IsRazor ? parsed.Regions.Select(r => r.Raw).ToList() : [];
        var values = AttributeValueSpans(blanked);

        foreach (Match construct in ControlFlowConstruct.Matches(source))
        {
            var at = construct.Index;
            if ((at > 0 && source[at - 1] == '@') || blanked[at] != '@'
                || regions.Any(r => r.Contains(at)) || values.Any(v => at >= v.Start && at < v.End))
            {
                continue;
            }

            var pos = SkipParenthesised(source, at + construct.Length);
            while (true)
            {
                pos = SkipWhitespace(source, pos);
                if (pos >= source.Length || source[pos] != '{')
                    break;

                pos = WalkCodeBody(source, blanked, buffer, pos);
                if (pos < 0)
                    break;

                var next = SkipWhitespace(source, pos);
                var continuation = ControlFlowContinuation.Match(source, next);
                if (!continuation.Success || continuation.Index != next)
                    break;

                pos = SkipParenthesised(source, next + continuation.Length);
            }
        }

        return new string(buffer);
    }

    private static readonly Regex ControlFlowConstruct =
        new(@"@(?:if|foreach|for|while|switch|lock|using|do|try)\b", RegexOptions.Compiled);

    private static readonly Regex ControlFlowContinuation =
        new(@"\G(?:else\s+if|else|catch|finally|while)\b", RegexOptions.Compiled);

    private static readonly Regex ControlFlowAt =
        new(@"\G@(?:if|foreach|for|while|switch|lock|using|do|try)\b", RegexOptions.Compiled);

    private static readonly Regex StatementKeyword =
        new(@"\G(?:if|else|foreach|for|while|do|using|lock|fixed)\b", RegexOptions.Compiled);

    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr",
    };

    /// <summary>
    /// Walks one body from its opening brace, blanking its code-level comments into
    /// <paramref name="buffer"/>; returns the index after its closing brace, or -1 if it never closes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// kd-31: a "&lt;" opens markup only where a statement can begin - at the body's start, after a
    /// "{", "}", ";" or a label's ":", after a skipped element or "@:" line, or straight after a
    /// template's "@" - and only when it has a tag's shape (<see cref="IsTagShape"/>). Elsewhere it
    /// is C#: "count&lt;limit" compares and "List&lt;string&gt;" is generic, and reading either as a tag
    /// lost the rest of the body.
    /// </para>
    /// <para>
    /// kd-42: so can one after "else" or "do", or after the ")" that closes the header of an "if",
    /// "for", "foreach", "while", "using", "lock" or "fixed" - a brace-less body. Razor refuses markup
    /// there in a compiled page (RZ1008), so only text that is not one, a fixture, can hold it; read
    /// as C#, the element's text was code and a brace in it ended the body early. Any other ")" is
    /// not a statement start: "(a) &lt;b ? x : y" compares.
    /// </para>
    /// </remarks>
    private static int WalkCodeBody(string source, string blanked, char[] buffer, int open)
    {
        var depth = 1;
        var i = open + 1;
        var statementStart = true;
        var parens = 0;
        var headers = new Stack<int>();
        var headerPending = false;
        while (i < source.Length)
        {
            var c = source[i];
            if (blanked[i] != c || char.IsWhiteSpace(c))
            {
                // Already blanked (a Razor or HTML comment, kd-12's comments) or whitespace.
                i++;
                continue;
            }

            var next = i + 1 < source.Length ? source[i + 1] : '\0';
            if (c == '/' && next == '/')
            {
                var end = source.IndexOf('\n', i);
                end = end < 0 ? source.Length : end;
                BlankSpan(buffer, i, end);
                i = end;
            }
            else if (c == '/' && next == '*')
            {
                var end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? source.Length : end + 2;
                BlankSpan(buffer, i, end);
                i = end;
            }
            else if (c == '@' && next == ':')
            {
                var end = source.IndexOf('\n', i);
                i = end < 0 ? source.Length : end;
                statementStart = true;
            }
            else if (c == '@' && next == '<')
            {
                // A Razor template, "@<p>...</p>": what follows is markup wherever it sits.
                i++;
                statementStart = true;
            }
            else if (c == '<' && statementStart && IsTagShape(blanked, i))
            {
                i = EndOfElement(blanked, i);
                statementStart = true;
            }
            else if (c is '"' or '\'' || (c is '@' or '$' && next is '"' or '$' or '@'))
            {
                var literal = SyntaxFactory.ParseToken(source, i);
                i += Math.Max(1, literal.Span.Length);
                statementStart = false;
            }
            else if (c == '{')
            {
                depth++;
                i++;
                statementStart = true;
            }
            else if (c == '}')
            {
                i++;
                if (--depth == 0)
                    return i;
                statementStart = true;
            }
            else if (statementStart && StatementKeyword.Match(source, i) is { Success: true } keyword)
            {
                // kd-42. "else" and "do" leave a statement start; a header keyword waits for its "(".
                i += keyword.Length;
                headerPending = keyword.Value is not ("else" or "do");
                statementStart = !headerPending;
            }
            else
            {
                i++;
                var closesHeader = false;
                if (c == '(')
                {
                    parens++;
                    if (headerPending)
                        headers.Push(parens);
                }
                else if (c == ')')
                {
                    closesHeader = headers.Count > 0 && headers.Peek() == parens;
                    if (closesHeader)
                        headers.Pop();
                    parens--;
                }

                headerPending = false;
                statementStart = closesHeader || c is ';' or ':';
            }
        }

        return -1;
    }

    /// <summary>
    /// kd-31. Whether the "&lt;" at <paramref name="at"/> has a tag's shape: an end-tag slash or
    /// none, a letter-led name, then whitespace, "&gt;" or "/". "&lt;b)" and "&lt;b;" are comparisons.
    /// </summary>
    private static bool IsTagShape(string text, int at)
    {
        var nameStart = at + 1 < text.Length && text[at + 1] == '/' ? at + 2 : at + 1;
        if (nameStart >= text.Length || !char.IsLetter(text[nameStart]))
            return false;

        var nameEnd = nameStart;
        while (nameEnd < text.Length && IsNameChar(text[nameEnd]))
            nameEnd++;

        return nameEnd < text.Length && (char.IsWhiteSpace(text[nameEnd]) || text[nameEnd] is '>' or '/');
    }

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '-' or '.' or ':' or '_';

    /// <summary>
    /// The index after the markup element whose tag opens at <paramref name="start"/>: after its
    /// matching end tag, or after the tag itself when it is an end tag, self-closing or void.
    /// </summary>
    /// <remarks>
    /// kd-31: a nested tag of the same name counts only with a tag's shape, so "&lt;b&gt;@(a&lt;b)&lt;/b&gt;"
    /// closes at its "&lt;/b&gt;" instead of reading "&lt;b)" as a second "&lt;b&gt;" that never closes.
    /// </remarks>
    private static int EndOfElement(string source, int start)
    {
        var nameStart = source[start + 1] == '/' ? start + 2 : start + 1;
        var nameEnd = nameStart;
        while (nameEnd < source.Length && IsNameChar(source[nameEnd]))
            nameEnd++;

        var tagEnd = IndexOfTagEnd(source, nameEnd);
        if (tagEnd < 0)
            return source.Length;

        var name = source[nameStart..nameEnd];
        if (nameStart == start + 2 || source[tagEnd - 1] == '/' || VoidElements.Contains(name))
            return tagEnd + 1;

        var depth = 1;
        for (var at = tagEnd + 1; at < source.Length; at++)
        {
            if (source[at] == '@')
            {
                // A Razor expression or a construct's header in the content is C#: "@(a<b ? x : y)" and
                // "@if (a<b && c)" compare, they do not open a tag.
                var construct = ControlFlowAt.Match(source, at);
                if (at + 1 < source.Length && source[at + 1] == '(')
                    at = SkipParenthesised(source, at + 1) - 1;
                else if (construct.Success)
                    at = SkipParenthesised(source, at + construct.Length) - 1;
                continue;
            }

            if (source[at] != '<')
                continue;

            var closing = at + 1 < source.Length && source[at + 1] == '/';
            var candidate = closing ? at + 2 : at + 1;
            if (string.Compare(source, candidate, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) != 0)
                continue;

            var after = candidate + name.Length;
            if (!IsTagShape(source, at) || (after < source.Length && IsNameChar(source[after])))
                continue;

            var end = IndexOfTagEnd(source, after);
            if (end < 0)
                return source.Length;

            if (closing && --depth == 0)
                return end + 1;

            if (!closing && source[end - 1] != '/')
                depth++;

            at = end;
        }

        return source.Length;
    }

    /// <summary>
    /// kd-31. <paramref name="blanked"/> with every HTML comment in the markup blanked as well - a
    /// "&lt;!--" outside the @code regions, not already blanked, through its "--&gt;". One that never
    /// closes is left alone rather than taken to the end of the file.
    /// </summary>
    /// <remarks>
    /// kd-42. Inside an @code region a render-fragment template is markup too (<see cref="TemplatesIn"/>),
    /// and an HTML comment in one is blanked through the first "--&gt;" before the template ends - found
    /// in the source, because the region's Roslyn view reads a "//" in the comment's text as a C#
    /// comment and has already blanked the rest of that line. Anywhere else in a region a "&lt;!--" is
    /// C# text, a string's contents, and stays.
    /// </remarks>
    private static string BlankHtmlComments(string source, string blanked)
    {
        var buffer = blanked.ToCharArray();
        var parsed = RazorSyntax.Parse(source);
        var regions = parsed.IsRazor ? parsed.Regions.Select(r => r.Raw).ToList() : [];
        var templates = regions.SelectMany(r => TemplatesIn(source, blanked, r.Start, r.End)).ToList();

        // kd-56: the search resumes at at + 1. After a blanked comment "at" is its "-->", which holds
        // no "<", so the next "<!--" is found from the comment's very end; at + 4 started one past it
        // and missed a comment written straight after another ("<!-- a --><!-- b -->").
        for (var at = blanked.IndexOf("<!--", StringComparison.Ordinal); at >= 0;
             at = blanked.IndexOf("<!--", at + 1, StringComparison.Ordinal))
        {
            if (regions.Any(r => r.Contains(at)))
            {
                var template = templates.FindIndex(t => at >= t.Start && at < t.End);
                if (template < 0)
                    continue;

                var end = templates[template].End;
                var inTemplate = source.IndexOf("-->", at + 4, Math.Max(0, end - at - 4), StringComparison.Ordinal);
                if (inTemplate >= 0)
                {
                    BlankSpan(buffer, at, inTemplate + 3);
                    at = inTemplate;
                }

                continue;
            }

            var close = blanked.IndexOf("-->", at + 4, StringComparison.Ordinal);
            if (close < 0)
                break;

            BlankSpan(buffer, at, close + 3);
            at = close;
        }

        return new string(buffer);
    }

    /// <summary>
    /// kd-42. The render-fragment templates in the @code region from <paramref name="start"/> to
    /// <paramref name="end"/>: each "@&lt;" at code level - not in a comment or a literal - through the
    /// end of the element it opens. One whose element does not end inside the region is not taken.
    /// </summary>
    private static List<(int Start, int End)> TemplatesIn(string source, string blanked, int start, int end)
    {
        var templates = new List<(int Start, int End)>();
        var i = start;
        while (i < end)
        {
            var c = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';
            if (blanked[i] != c || char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '@' && next == '<' && IsTagShape(blanked, i + 1))
            {
                var element = EndOfElement(blanked, i + 1);
                if (element <= end)
                    templates.Add((i + 1, element));
                i = element <= end ? element : i + 2;
            }
            else if (c is '"' or '\'' || (c is '@' or '$' && next is '"' or '$' or '@'))
            {
                i += Math.Max(1, SyntaxFactory.ParseToken(source, i).Span.Length);
            }
            else
            {
                i++;
            }
        }

        return templates;
    }

    /// <summary>After any whitespace, then a parenthesised header's matching ")" if one opens there.</summary>
    private static int SkipParenthesised(string source, int from)
    {
        var at = SkipWhitespace(source, from);
        if (at >= source.Length || source[at] != '(')
            return at;

        var depth = 0;
        foreach (var token in SyntaxFactory.ParseTokens(source, offset: at, initialTokenPosition: at))
        {
            if (token.IsKind(SyntaxKind.OpenParenToken))
                depth++;
            else if ((token.IsKind(SyntaxKind.CloseParenToken) && --depth == 0) || token.IsKind(SyntaxKind.EndOfFileToken))
                return token.Span.End;
        }

        return source.Length;
    }

    private static int SkipWhitespace(string source, int from)
    {
        var at = from;
        while (at < source.Length && char.IsWhiteSpace(source[at]))
            at++;
        return at;
    }

    /// <summary>Every quoted attribute value of every tag in <paramref name="text"/>, as WalkTag records them.</summary>
    private static List<(int Start, int End)> AttributeValueSpans(string text)
    {
        var spans = new List<(int Start, int End)>();
        for (var at = text.IndexOf('<'); at >= 0; at = text.IndexOf('<', at + 1))
        {
            if (at + 1 >= text.Length || !char.IsLetter(text[at + 1]))
                continue;

            var end = WalkTag(text, at + 1, spans);
            if (end < 0)
                continue;

            at = end;
        }

        return spans;
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
    /// <remarks>
    /// kd-3. Lexed, not counted. The opener is looked for, and the braces balanced, in
    /// <see cref="RazorSyntax.CodeView"/> - the Roslyn view the progress guards read, in which every
    /// comment and every string, char, verbatim, raw and interpolated string literal (holes
    /// included) is blanked to the same length. So a "}" in a log message or a '{' char literal
    /// neither closes nor opens the block, and the opener's text inside a literal or a comment is
    /// not the opener. Counting raw characters ended a block at the first brace inside a string -
    /// <see cref="Tags"/> was quote-aware and this was not - so an assertion scoped to the block read
    /// the wrong code. The text returned is the source's own, literals and comments included,
    /// because the code view keeps every offset. C# only: the code view blanks a component's markup,
    /// so an opener in markup is not found - <see cref="ExtractMarkupBlock"/> reads that.
    /// </remarks>
    public static string ExtractBlock(string source, string opener)
    {
        var code = RazorSyntax.CodeView(source);

        return BalancedBlock(source, code, opener);
    }

    /// <summary>
    /// The brace-balanced block a Razor construct in a component's MARKUP opens - an
    /// "@if (...)" or "@foreach (...)" between the tags - which <see cref="ExtractBlock"/> cannot
    /// read, because its code view blanks markup. Returns an empty string when the opener is absent.
    /// </summary>
    /// <remarks>
    /// kd-11. Balanced over a markup view that is quote-aware the way <see cref="Tags"/> is: every
    /// quoted attribute value of every tag (a quote directly after its "=") is blanked, so a brace in
    /// a title, a placeholder or a lambda handler neither opens nor closes the block; comments are
    /// blanked as <see cref="BlankComments"/> blanks them; and the component's @code regions are
    /// blanked whole. Like the code view it keeps every offset, so the text returned is the
    /// source's own. Honest limit, as <see cref="IndexOfTagEnd"/> has one: a brace inside a C#
    /// string literal in a Razor expression in text content - @("}") - is still counted, and seeing
    /// it would take Razor's own parser.
    /// </remarks>
    public static string ExtractMarkupBlock(string source, string opener) =>
        BalancedBlock(source, MarkupView(source), opener);

    /// <summary>
    /// The block <paramref name="opener"/> introduces, found and balanced in <paramref name="view"/> -
    /// the same source with what is not to be read blanked to the same length - and returned as the
    /// source's own text.
    /// </summary>
    private static string BalancedBlock(string source, string view, string opener)
    {
        var start = IndexOfUnblanked(source, view, opener);
        if (start < 0)
            return "";

        var open = view.IndexOf('{', start + opener.Length);
        if (open < 0)
            return "";

        var depth = 0;
        for (var i = open; i < view.Length; i++)
        {
            if (view[i] == '{') depth++;
            else if (view[i] == '}' && --depth == 0)
                return source[open..(i + 1)];
        }

        return "";
    }

    /// <summary>
    /// kd-11. The markup view <see cref="ExtractMarkupBlock"/> balances over: comments, quoted
    /// attribute values and the component's @code regions blanked, newlines kept.
    /// </summary>
    private static string MarkupView(string source)
    {
        var blanked = BlankComments(source);
        var buffer = blanked.ToCharArray();

        for (var at = blanked.IndexOf('<'); at >= 0; at = blanked.IndexOf('<', at + 1))
        {
            if (at + 1 >= blanked.Length || !char.IsLetter(blanked[at + 1]))
                continue;

            var values = new List<(int Start, int End)>();
            var end = WalkTag(blanked, at + 1, values);
            if (end < 0)
                continue;

            foreach (var (valueStart, valueEnd) in values)
                BlankSpan(buffer, valueStart, valueEnd);

            at = end;
        }

        var parsed = RazorSyntax.Parse(source);
        if (parsed.IsRazor)
        {
            foreach (var region in parsed.Regions)
                BlankSpan(buffer, region.Raw.Start, region.Raw.End);
        }

        return new string(buffer);
    }

    private static void BlankSpan(char[] buffer, int from, int to)
    {
        for (var i = Math.Max(0, from); i < Math.Min(buffer.Length, to); i++)
        {
            if (buffer[i] != '\n')
                buffer[i] = ' ';
        }
    }

    /// <summary>
    /// The first occurrence of <paramref name="text"/> in <paramref name="source"/> that starts in
    /// what <paramref name="view"/> keeps: its first non-blank character survives in the view of
    /// the same source, rather than having been blanked as part of a literal, a comment or an
    /// attribute value.
    /// </summary>
    private static int IndexOfUnblanked(string source, string view, string text)
    {
        var lead = text.Length - text.TrimStart().Length;
        for (var at = source.IndexOf(text, StringComparison.Ordinal); at >= 0;
             at = source.IndexOf(text, at + 1, StringComparison.Ordinal))
        {
            var first = at + lead;
            if (first >= source.Length || view[first] == source[first])
                return at;
        }

        return -1;
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
