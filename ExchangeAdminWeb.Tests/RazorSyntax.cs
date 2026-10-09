using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// The C# inside a Razor component, as a real Roslyn syntax tree, plus the two blanked views
/// the source-scanning guards read.
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS EXISTS. Eight guard findings in six days, and the named through-line is that regex
/// over source cannot see control flow and cannot reliably tell code from data. Three rounds on
/// the progress scanner went regex, then partial blanking, then a hand-written single-pass
/// lexer, each closing the previous hole and exposing the next (review findings prog-1, prog-3,
/// prog-4). Roslyn ends that cycle for the lexical half outright: a comment is trivia and a
/// string is a token value, so neither is text that has to be recognised and skipped by hand.
/// </para>
/// <para>
/// ROSLYN WAS ALREADY HERE. Microsoft.CodeAnalysis.CSharp arrives transitively through
/// Microsoft.PowerShell.SDK, which the app has shipped since the beginning; the test project
/// now names it explicitly so the guards do not depend on an accident of the app's dependency
/// graph. Nothing shipped gained a dependency.
/// </para>
/// <para>
/// RAZOR IS NOT C#, AND ROSLYN DOES NOT PARSE IT. A component is markup with one or more
/// <c>@code { ... }</c> blocks in it. The block bodies are extracted by position and each is
/// parsed on its own, wrapped in a synthetic class declaration so a member list parses as a
/// member list. Every position in a parsed tree maps back to the raw file by a single constant
/// offset (<see cref="CodeRegion.Offset"/>), so a span found in the tree still addresses the
/// same characters of the file on disk and nothing downstream has to know a tree was involved.
/// </para>
/// <para>
/// WHAT STAYS TEXT. Markup is outside every region and no syntax tree covers it, so an
/// assertion about MARKUP - a quoted attribute value, a handler name - is still a text match and
/// is meant to be. The split is the point: C# questions are answered by the tree, markup
/// questions by text, and neither pretends to be the other.
/// </para>
/// </remarks>
internal static class RazorSyntax
{
    // A member list only parses as one inside a type. The exact text is load-bearing only in
    // its LENGTH, which is subtracted back out of every position.
    private const string Prologue = "class RazorCodeBlock\n{\n";
    private const string Epilogue = "\n}\n";

    private static readonly CSharpParseOptions Options =
        CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);

    /// <summary>One parsed span of C# within a raw source file.</summary>
    /// <param name="Raw">The span the region occupies in the RAW file.</param>
    /// <param name="Tree">The parsed tree, which may carry a synthetic wrapper.</param>
    /// <param name="Offset">Added to a position in <paramref name="Tree"/> to get a raw position.</param>
    internal sealed record CodeRegion(TextSpan Raw, SyntaxTree Tree, int Offset)
    {
        private SyntaxNode? root;

        internal SyntaxNode Root => this.root ??= this.Tree.GetRoot();

        /// <summary>The raw-file position of <paramref name="treePosition"/>.</summary>
        internal int RawAt(int treePosition) => treePosition + this.Offset;
    }

    /// <summary>A source file split into the C# regions a tree can answer questions about.</summary>
    /// <param name="IsRazor">
    /// True when the regions came from <c>@code</c> blocks, so everything outside them is markup.
    /// </param>
    internal sealed record ParsedSource(string Raw, IReadOnlyList<CodeRegion> Regions, bool IsRazor);

    private static readonly Dictionary<string, ParsedSource> Cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Parse by file name, which is the honest way round: a <c>.razor</c> file holds markup plus
    /// <c>@code</c> blocks even when it happens to have none of the latter, and a component with
    /// no code block has no C# at all rather than a fileful of it.
    /// </summary>
    internal static ParsedSource ParseFile(string path, string raw) =>
        Path.GetExtension(path).Equals(".razor", StringComparison.OrdinalIgnoreCase)
            ? Parsed(raw, CodeBlockBodies(raw), razor: true)
            : Parsed(raw, [], razor: false);

    /// <summary>
    /// Parse bare text, for callers that hold a fragment rather than a file. Text carrying a
    /// <c>@code</c> block is treated as a component and everything outside the block is markup;
    /// anything else is taken as C# whole, which is what a method body extracted from a page is.
    /// </summary>
    internal static ParsedSource Parse(string raw)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(raw, out var cached))
                return cached;

            var bodies = CodeBlockBodies(raw);
            var built = Parsed(raw, bodies, razor: bodies.Count > 0);
            Cache[raw] = built;
            return built;
        }
    }

    private static ParsedSource Parsed(string raw, IReadOnlyList<TextSpan> bodies, bool razor)
    {
        if (!razor)
        {
            return new ParsedSource(
                raw,
                [new CodeRegion(TextSpan.FromBounds(0, raw.Length), CSharpSyntaxTree.ParseText(raw, Options), 0)],
                false);
        }

        var regions = bodies
            .Select(body => new CodeRegion(
                body,
                CSharpSyntaxTree.ParseText(Prologue + raw.Substring(body.Start, body.Length) + Epilogue, Options),
                body.Start - Prologue.Length))
            .ToList();

        return new ParsedSource(raw, regions, true);
    }

    /// <summary>
    /// The body spans of every <c>@code { ... }</c> block, braces excluded. The closing brace is
    /// found by LEXING rather than by counting characters, so a brace inside a string, a comment
    /// or an interpolation hole is not a brace.
    /// </summary>
    private static IReadOnlyList<TextSpan> CodeBlockBodies(string raw)
    {
        var bodies = new List<TextSpan>();
        var at = 0;

        while (at < raw.Length)
        {
            var found = raw.IndexOf("@code", at, StringComparison.Ordinal);
            if (found < 0)
                break;

            at = found + "@code".Length;

            if (found > 0 && (char.IsLetterOrDigit(raw[found - 1]) || raw[found - 1] is '_' or '@'))
                continue;

            if (at < raw.Length && (char.IsLetterOrDigit(raw[at]) || raw[at] == '_'))
                continue;

            var open = at;
            while (open < raw.Length && char.IsWhiteSpace(raw[open]))
                open++;

            if (open >= raw.Length || raw[open] != '{')
                continue;

            var close = MatchingBrace(raw, open);
            if (close < 0)
                continue;

            bodies.Add(TextSpan.FromBounds(open + 1, close));
            at = close + 1;
        }

        return bodies;
    }

    private static int MatchingBrace(string raw, int open)
    {
        var depth = 0;

        foreach (var token in SyntaxFactory.ParseTokens(raw, offset: open, initialTokenPosition: open, options: Options))
        {
            if (token.IsKind(SyntaxKind.OpenBraceToken))
            {
                depth++;
            }
            else if (token.IsKind(SyntaxKind.CloseBraceToken))
            {
                if (--depth == 0)
                    return token.SpanStart;
            }
            else if (token.IsKind(SyntaxKind.EndOfFileToken))
            {
                break;
            }
        }

        return -1;
    }

    /// <summary>
    /// The CODE view: the raw file with everything that is not C# code replaced by same-length
    /// blanks. Comments, string and char literals, and markup all go; what survives is code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An interpolated string is blanked WHOLE, holes included. A hole does hold code, but the
    /// scans that read this view ask "does this method call X", and a call written inside a hole
    /// is reported as a missing call rather than silently credited - a loud answer either way,
    /// which is the direction a guard must fail in.
    /// </para>
    /// <para>
    /// Markup goes too, and that is a strengthening of what the character scanner did by
    /// accident: it treated every quoted markup attribute value as a string literal. Nothing in
    /// a component's markup is C# that a "does this method DO x" scan has any business reading.
    /// </para>
    /// </remarks>
    internal static string CodeView(string raw)
    {
        var parsed = Parse(raw);
        var buffer = raw.ToCharArray();

        if (parsed.IsRazor)
            BlankOutsideRegions(buffer, parsed.Regions);

        foreach (var region in parsed.Regions)
        {
            BlankComments(buffer, region);
            BlankLiterals(buffer, region);
        }

        return new string(buffer);
    }

    /// <summary>
    /// Comment bodies blanked, everything else left alone - including string literals, because
    /// handler names live inside quoted markup attribute values.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only a C# comment inside a C# region is a C# comment. <c>//</c> in markup is text: it is
    /// the middle of <c>https://</c> far more often than it is anything else, and treating it as
    /// a comment blanked the rest of the line and erased any handler attribute that followed it
    /// on the same tag (review finding prog-4b). The same goes for <c>/* */</c>, which in markup
    /// is a CSS comment at best.
    /// </para>
    /// <para>
    /// Razor's own <c>@* *@</c> IS a comment, and it is markup, so it is blanked outside the code
    /// regions and only there: inside <c>@code</c> the same two characters can be the contents of
    /// a string literal, and this view exists to keep literals intact.
    /// </para>
    /// </remarks>
    internal static string CommentsBlanked(string raw)
    {
        var parsed = Parse(raw);
        var buffer = raw.ToCharArray();

        foreach (var region in parsed.Regions)
            BlankComments(buffer, region);

        if (parsed.IsRazor)
            BlankRazorComments(buffer, raw, parsed.Regions);

        return new string(buffer);
    }

    private static void BlankRazorComments(char[] buffer, string raw, IReadOnlyList<CodeRegion> regions)
    {
        var at = 0;

        while (at < raw.Length)
        {
            var open = raw.IndexOf("@*", at, StringComparison.Ordinal);
            if (open < 0)
                break;

            var close = raw.IndexOf("*@", open + 2, StringComparison.Ordinal);
            if (close < 0)
                break;

            var end = close + 2;
            at = end;

            if (regions.Any(r => r.Raw.OverlapsWith(TextSpan.FromBounds(open, end))))
                continue;

            BlankRange(buffer, open, end);
        }
    }

    internal static int LineOf(string raw, int index) =>
        raw[..Math.Clamp(index, 0, raw.Length)].Count(c => c == '\n') + 1;

    private static void BlankComments(char[] buffer, CodeRegion region)
    {
        foreach (var trivia in region.Root.DescendantTrivia(descendIntoTrivia: true))
        {
            if (IsComment(trivia.Kind()))
                Blank(buffer, region, trivia.FullSpan);
        }
    }

    private static void BlankLiterals(char[] buffer, CodeRegion region)
    {
        foreach (var node in region.Root.DescendantNodes(descendIntoTrivia: true))
        {
            if (node is InterpolatedStringExpressionSyntax)
                Blank(buffer, region, node.Span);
        }

        foreach (var token in region.Root.DescendantTokens(descendIntoTrivia: true))
        {
            if (IsTextToken(token.Kind()))
                Blank(buffer, region, token.Span);
        }
    }

    private static void BlankOutsideRegions(char[] buffer, IReadOnlyList<CodeRegion> regions)
    {
        var cursor = 0;

        foreach (var region in regions.OrderBy(r => r.Raw.Start))
        {
            BlankRange(buffer, cursor, region.Raw.Start);
            cursor = Math.Max(cursor, region.Raw.End);
        }

        BlankRange(buffer, cursor, buffer.Length);
    }

    /// <summary>
    /// Blank a span of a tree, clamped to its region so a synthetic wrapper can never write
    /// outside the text it was wrapped around.
    /// </summary>
    private static void Blank(char[] buffer, CodeRegion region, TextSpan spanInTree) =>
        BlankRange(
            buffer,
            Math.Max(region.Raw.Start, region.RawAt(spanInTree.Start)),
            Math.Min(region.Raw.End, region.RawAt(spanInTree.End)));

    private static void BlankRange(char[] buffer, int from, int to)
    {
        for (var i = Math.Max(0, from); i < Math.Min(buffer.Length, to); i++)
        {
            if (buffer[i] != '\n')
                buffer[i] = ' ';
        }
    }

    private static bool IsComment(SyntaxKind kind) => kind is
        SyntaxKind.SingleLineCommentTrivia
        or SyntaxKind.MultiLineCommentTrivia
        or SyntaxKind.SingleLineDocumentationCommentTrivia
        or SyntaxKind.MultiLineDocumentationCommentTrivia;

    private static bool IsTextToken(SyntaxKind kind) => kind is
        SyntaxKind.StringLiteralToken
        or SyntaxKind.CharacterLiteralToken
        or SyntaxKind.SingleLineRawStringLiteralToken
        or SyntaxKind.MultiLineRawStringLiteralToken
        or SyntaxKind.Utf8StringLiteralToken
        or SyntaxKind.Utf8SingleLineRawStringLiteralToken
        or SyntaxKind.Utf8MultiLineRawStringLiteralToken
        or SyntaxKind.InterpolatedStringToken
        or SyntaxKind.InterpolatedStringTextToken
        or SyntaxKind.InterpolatedStringStartToken
        or SyntaxKind.InterpolatedStringEndToken
        or SyntaxKind.InterpolatedVerbatimStringStartToken
        or SyntaxKind.InterpolatedSingleLineRawStringStartToken
        or SyntaxKind.InterpolatedMultiLineRawStringStartToken
        or SyntaxKind.InterpolatedRawStringEndToken;
}
