using System.Text;
using System.Text.RegularExpressions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Source scanner shared by <see cref="ProgressRegistryTests"/>. Kept separate from the
/// assertions so the discovery rules are readable on their own: what counts as an
/// operator-initiated operation is the load-bearing decision in this suite, and it is a
/// different question from what the registry then has to say about one.
/// </summary>
internal static class ProgressScan
{
    /// <summary>
    /// The markup surfaces that start operator work. The predecessor guard's rubric knew three
    /// click attributes, and real gaps live outside all of them: InputFile's OnChange
    /// (ConferenceRooms.razor:144 and :331, Comms10k.razor:91), @bind:after
    /// (AdminEventLog.razor:98) and OnAfterRenderAsync reaching slow work
    /// (AdminSettings.razor:546). A scanner that knows only clicks lets the next slow upload or
    /// bind-after refresh in silently.
    /// </summary>
    private static readonly Regex[] HandlerAttributes =
    [
        new(@"@onclick\s*=\s*(?:""([^""]*)""|'([^']*)')", RegexOptions.Compiled),
        new(@"@onsubmit\s*=\s*(?:""([^""]*)""|'([^']*)')", RegexOptions.Compiled),
        new(@"@onkeydown\s*=\s*(?:""([^""]*)""|'([^']*)')", RegexOptions.Compiled),
        new(@"@bind(?:-\w+)?:after\s*=\s*(?:""([^""]*)""|'([^']*)')", RegexOptions.Compiled),
        // InputFile's OnChange, and any component parameter spelled the same way. The negative
        // lookbehind keeps this off the DOM's own @onchange, which the pattern below owns; the
        // two are different surfaces and both are scanned.
        new(@"(?<![@\w])OnChange\s*=\s*(?:""([^""]*)""|'([^']*)')", RegexOptions.Compiled),
        // The DOM's own @onchange. This was excluded when the registry was written, recorded as a
        // limitation outside the plan's named surfaces - and the exclusion was not safe.
        // Migration.razor:510 and :641 wire tick boxes straight at AdoptSelectionAsOpenBatch,
        // which reaches LoadMailboxesFor: the same work, classified when a click reaches it and
        // unclassified when a checkbox does (review finding prog-2). A select element or a tick
        // box is an operator control like any other.
        new(@"@onchange\s*=\s*(?:""([^""]*)""|'([^']*)')", RegexOptions.Compiled),
    ];

    /// <summary>
    /// Lifecycle entry points that can reach slow work. An initial load is operator-initiated:
    /// the operator opened the page and is waiting for it.
    /// </summary>
    internal static readonly string[] LifecycleMethods =
    [
        "OnInitializedAsync",
        "OnAfterRenderAsync",
        "OnParametersSetAsync",
    ];

    /// <summary>A method declared in a page's @code block, with the span of its body.</summary>
    internal sealed record PageMethod(string Name, int Line, int Start, int End);

    /// <summary>What a page's source says, once scanned.</summary>
    /// <param name="Source">
    /// The DISCOVERY view: the raw file with comment bodies blanked and string literals left
    /// intact. Handler names live inside quoted markup attributes (<c>@onclick="Foo"</c>), so
    /// blanking literals here would delete the operation list outright.
    /// </param>
    /// <param name="Code">
    /// The CODE view: the raw file with comment bodies AND string/char literal contents blanked,
    /// so everything still readable in it is code. Every scan that asks "does this method DO x"
    /// reads this one. Blanking is same-length, so a method span found in <paramref name="Source"/>
    /// addresses the same text here.
    /// </param>
    internal sealed record PageScan(
        string Page,
        string Source,
        string Code,
        IReadOnlyDictionary<string, PageMethod> Methods,
        IReadOnlyList<string> Operations,
        IReadOnlyList<string> UnextractedLambdas);

    private static readonly Regex MethodDeclaration = new(
        // The return type may itself contain parentheses - a tuple return like
        // Task<(OnPremLogonResult?, string?)> is how TrueLastLogon's two halves are declared - so
        // the type is matched lazily up to the LAST identifier before the parameter list rather
        // than being forbidden parentheses. '=' is excluded instead, which is what keeps a field
        // initializer ("private static readonly Regex X = new(") out of the method table.
        @"(?m)^[ \t]*(?:(?:private|protected|public|internal|static|async|override|virtual|sealed|new|partial)[ \t]+)+(?<rest>[^\n=]*?)(?<name>\b\w+)[ \t]*\(",
        RegexOptions.Compiled);

    private static readonly Regex CallSite = new(@"\b([A-Za-z_]\w*)\s*\(", RegexOptions.Compiled);

    private static readonly Regex ActivityOpened = new(
        @"using var (\w+) = Progress\.Begin\(", RegexOptions.Compiled);

    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ExchangeAdminWeb.csproj")))
                return dir.FullName;

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root from the test base directory.");
    }

    /// <summary>
    /// The covered set, taken from the filesystem rather than from a hand-maintained list. This
    /// is the half of the guard that catches a page nobody registered: three pages that report
    /// nothing at all were invisible to the predecessor [Theory] simply by being absent from it.
    /// </summary>
    internal static IReadOnlyList<string> PageFiles() =>
        Directory.GetFiles(Path.Combine(RepoRoot(), "Components", "Pages"), "*.razor")
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Comments go before anything else is matched. Three guards in this repo have already failed
    /// by matching the prose that explained the rule instead of the code
    /// (GlobalProgressWiringTests, .agents/state.md 2026-09-30), and this suite is especially
    /// exposed: a comment naming a service call would satisfy a Reports entry on its own.
    /// Replaced with same-length blanks so every index stays a real source position.
    /// </summary>
    internal static string StripComments(string source)
    {
        var text = new StringBuilder(source);
        Blank(text, new Regex(@"/\*.*?\*/", RegexOptions.Singleline));
        Blank(text, new Regex(@"@\*.*?\*@", RegexOptions.Singleline));
        Blank(text, new Regex(@"//[^\n]*"));
        return text.ToString();

        static void Blank(StringBuilder text, Regex pattern)
        {
            foreach (Match match in pattern.Matches(text.ToString()))
            {
                for (var i = match.Index; i < match.Index + match.Length; i++)
                {
                    if (text[i] != '\n')
                        text[i] = ' ';
                }
            }
        }
    }

    /// <summary>
    /// The CODE view: one left-to-right lexical pass over the raw file that blanks comment bodies
    /// AND whole string and char literals, delimiters included, replacing each with same-length
    /// blanks so every index stays a real source position. What survives this pass is code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both halves have been demonstrated to matter. Prose naming a service satisfied a Reports
    /// entry on its own (three guards in this repo have failed that way); so did a string literal
    /// holding the same text - deleting CloudPasswordReset's real DeriveDestination call and
    /// leaving <c>var x = "ResetService.DeriveDestination(";</c> inside the activity passed all
    /// three conditions (review finding prog-1).
    /// </para>
    /// <para>
    /// The two are blanked in ONE pass, not in two, and that is load-bearing. Blanking comments
    /// first ends a line at any <c>//</c>, including a <c>//</c> that is inside a string
    /// ("https://..."), which deletes that string's closing quote; every literal after it on the
    /// page then pairs one quote out of step and real string content lands OUTSIDE a literal,
    /// unblanked. A single pass asks at each position which construct starts here, so a
    /// <c>//</c> inside a literal is text and a quote inside a comment is text.
    /// </para>
    /// <para>
    /// This is NOT the view handler discovery reads - see <see cref="PageScan.Source"/>. That
    /// asymmetry is deliberate: handler names are quoted markup attribute values, so discovery
    /// must keep literals. Every scan that asks what a method DOES reads the code view.
    /// </para>
    /// </remarks>
    internal static string CodeView(string source)
    {
        var text = new StringBuilder(source);

        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';

            int end;
            if (c == '/' && next == '*')
                end = CloseOf(source, i + 2, "*/");
            else if (c == '@' && next == '*')
                end = CloseOf(source, i + 2, "*@");
            else if (c == '/' && next == '/')
                end = EndOfLine(source, i);
            else if (c == '"' || c == '\'')
                end = SkipLiteral(source, i);
            else
                continue;

            // CloseOf answers -1 for a block comment that is never closed, which is not a
            // construct and must not move the cursor backwards onto itself for ever. SkipLiteral
            // and EndOfLine never answer below i, so this arm is CloseOf's alone.
            if (end < i)
                continue;

            for (var j = i; j <= end && j < text.Length; j++)
            {
                if (text[j] != '\n')
                    text[j] = ' ';
            }

            i = end;
        }

        return text.ToString();
    }

    /// <summary>Index of the last character of <paramref name="closer"/>, or -1 if unclosed.</summary>
    private static int CloseOf(string source, int from, string closer)
    {
        var at = source.IndexOf(closer, Math.Min(from, source.Length), StringComparison.Ordinal);
        return at < 0 ? -1 : at + closer.Length - 1;
    }

    private static int EndOfLine(string source, int from)
    {
        var at = source.IndexOf('\n', from);
        return at < 0 ? source.Length - 1 : at - 1;
    }

    internal static int LineOf(string source, int index) =>
        source[..Math.Clamp(index, 0, source.Length)].Count(c => c == '\n') + 1;

    private static readonly Dictionary<string, PageScan> Cache = new(StringComparer.Ordinal);

    internal static PageScan Scan(string path)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(path, out var cached))
                return cached;

            var raw = File.ReadAllText(path);
            var source = StripComments(raw);
            var code = CodeView(raw);
            var methods = DeclaredMethods(source);
            var (operations, unextracted) = Handlers(source, methods);

            var scan = new PageScan(Path.GetFileName(path), source, code, methods, operations, unextracted);
            Cache[path] = scan;
            return scan;
        }
    }

    internal static IReadOnlyList<PageScan> ScanAll() => PageFiles().Select(Scan).ToList();

    internal static IReadOnlyDictionary<string, PageMethod> DeclaredMethods(string source)
    {
        var methods = new Dictionary<string, PageMethod>(StringComparer.Ordinal);

        foreach (Match match in MethodDeclaration.Matches(source))
        {
            var name = match.Groups["name"].Value;
            if (methods.ContainsKey(name))
                continue;

            var openParen = match.Index + match.Length - 1;
            var closeParen = MatchDelimiter(source, openParen, '(', ')');
            if (closeParen < 0)
                continue;

            var cursor = closeParen + 1;
            while (cursor < source.Length && char.IsWhiteSpace(source[cursor]))
                cursor++;

            int end;
            if (cursor < source.Length && source[cursor] == '{')
            {
                var close = MatchDelimiter(source, cursor, '{', '}');
                if (close < 0)
                    continue;
                end = close + 1;
            }
            else if (cursor + 1 < source.Length && source[cursor] == '=' && source[cursor + 1] == '>')
            {
                var close = StatementEnd(source, cursor);
                if (close < 0)
                    continue;
                end = close + 1;
            }
            else
            {
                // A record or a declaration with no body; nothing here is a handler.
                continue;
            }

            methods[name] = new PageMethod(name, LineOf(source, match.Index), match.Index, end);
        }

        return methods;
    }

    private static int MatchDelimiter(string source, int open, char opener, char closer)
    {
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            var c = source[i];
            if (c == '"' || c == '\'')
            {
                i = SkipLiteral(source, i);
                continue;
            }

            if (c == opener)
                depth++;
            else if (c == closer && --depth == 0)
                return i;
        }

        return -1;
    }

    private static int StatementEnd(string source, int from)
    {
        var depth = 0;
        for (var i = from; i < source.Length; i++)
        {
            var c = source[i];
            if (c == '"' || c == '\'')
            {
                i = SkipLiteral(source, i);
                continue;
            }

            if (c is '(' or '{' or '[')
                depth++;
            else if (c is ')' or '}' or ']')
                depth--;
            else if (c == ';' && depth <= 0)
                return i;
        }

        return -1;
    }

    /// <summary>
    /// The index of the character that CLOSES the literal opening at <paramref name="start"/>;
    /// for a single-line literal that runs off the end of its line, the last character on that
    /// line. Never answers below <paramref name="start"/>, so every caller advances.
    /// </summary>
    /// <remarks>
    /// The prefix decides the escape rules and reading one character back got it wrong, which is
    /// review finding prog-3: <c>$</c> alone is an INTERPOLATED string and still honours
    /// backslash escapes - only <c>@</c> makes a literal verbatim. Treating <c>$"..\".."</c> as
    /// verbatim ended the literal on the escaped quote, and every literal after it on the page
    /// then paired one quote out of step, leaving real string content outside every literal and
    /// therefore unblanked. A verbatim literal escapes its quote by DOUBLING it and may span
    /// lines; a raw literal (three or more quotes, no <c>@</c> prefix) closes on a quote run at
    /// least as long as the one that opened it and escapes nothing at all.
    /// </remarks>
    private static int SkipLiteral(string source, int start)
    {
        var quote = source[start];

        var verbatim = false;
        var interpolated = false;
        for (var p = start - 1; p >= 0 && (source[p] == '@' || source[p] == '$'); p--)
        {
            if (source[p] == '@')
                verbatim = true;
            else
                interpolated = true;
        }

        if (quote == '"' && !verbatim)
        {
            var opener = 0;
            while (start + opener < source.Length && source[start + opener] == '"')
                opener++;

            // Two quotes is the empty string, not a raw literal; three or more opens one.
            if (opener >= 3)
                return SkipRawLiteral(source, start, opener);
        }

        for (var i = start + 1; i < source.Length; i++)
        {
            if (interpolated && source[i] == '{')
            {
                if (i + 1 < source.Length && source[i + 1] == '{')
                {
                    i++;
                    continue;
                }

                i = SkipInterpolationHole(source, i, verbatim);
                continue;
            }

            if (verbatim)
            {
                if (source[i] != quote)
                    continue;

                if (i + 1 < source.Length && source[i + 1] == quote)
                {
                    i++;
                    continue;
                }

                return i;
            }

            if (source[i] == '\n')
                return i - 1;

            if (source[i] == '\\')
            {
                // A backslash immediately before the newline escapes nothing in C#; treating it
                // as an escape would carry the literal onto the next line and swallow code.
                if (i + 1 < source.Length && source[i + 1] == '\n')
                    return i - 1;

                i++;
                continue;
            }

            if (source[i] == quote)
                return i;
        }

        return source.Length - 1;
    }

    /// <summary>
    /// The closing brace of the interpolation hole opening at <paramref name="start"/>. A hole
    /// holds CODE, and since C# 11 that code may carry its own string literals: without this, a
    /// quote inside a hole closed the surrounding literal early, and the text between that hole
    /// and the next quote - string content - was left readable as code.
    /// </summary>
    private static int SkipInterpolationHole(string source, int start, bool verbatim)
    {
        var depth = 0;
        for (var i = start; i < source.Length; i++)
        {
            var c = source[i];

            if (c == '"' || c == '\'')
            {
                i = SkipLiteral(source, i);
                continue;
            }

            if (c == '\n' && !verbatim)
                return i - 1;

            if (c == '{')
                depth++;
            else if (c == '}' && --depth == 0)
                return i;
        }

        return source.Length - 1;
    }

    private static int SkipRawLiteral(string source, int start, int opener)
    {
        for (var i = start + opener; i < source.Length; i++)
        {
            if (source[i] != '"')
                continue;

            var run = 0;
            while (i + run < source.Length && source[i + run] == '"')
                run++;

            if (run >= opener)
                return i + run - 1;

            i += run - 1;
        }

        return source.Length - 1;
    }

    private static (IReadOnlyList<string> Operations, IReadOnlyList<string> Unextracted) Handlers(
        string source,
        IReadOnlyDictionary<string, PageMethod> methods)
    {
        var operations = new SortedSet<string>(StringComparer.Ordinal);
        var unextracted = new List<string>();

        foreach (var pattern in HandlerAttributes)
        {
            foreach (Match match in pattern.Matches(source))
            {
                var value = match.Groups[1].Success && match.Groups[1].Value.Length > 0
                    ? match.Groups[1].Value
                    : match.Groups[2].Value;

                var named = Resolve(value, methods).ToList();
                if (named.Count > 0)
                {
                    foreach (var name in named)
                        operations.Add(name);
                    continue;
                }

                // An inline lambda that reaches no page method. It is accepted as pure local UI
                // only if it cannot be doing slow work: the moment it awaits, the operator is
                // waiting on something no registry entry can describe, and the fix is to extract
                // a method and register it.
                if (value.Contains("await", StringComparison.Ordinal))
                    unextracted.Add($"line {LineOf(source, match.Index)}: {value.Trim()}");
            }
        }

        foreach (var lifecycle in LifecycleMethods)
        {
            if (methods.ContainsKey(lifecycle))
                operations.Add(lifecycle);
        }

        return (operations.ToList(), unextracted);
    }

    private static IEnumerable<string> Resolve(string value, IReadOnlyDictionary<string, PageMethod> methods)
    {
        var text = value.Trim();
        if (text.StartsWith('@'))
            text = text[1..].Trim();

        if (methods.ContainsKey(text))
        {
            yield return text;
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match call in CallSite.Matches(text))
        {
            var name = call.Groups[1].Value;
            if (methods.ContainsKey(name) && seen.Add(name))
                yield return name;
        }
    }

    /// <summary>
    /// The method's body in the CODE view - comments and literals blanked. Everything that asks
    /// what a method does reads this; nothing asks the discovery view, which still holds the
    /// quoted markup attribute values handler discovery is made of.
    /// </summary>
    internal static string CodeOf(PageScan page, string method) =>
        page.Methods.TryGetValue(method, out var found)
            ? page.Code[found.Start..found.End]
            : string.Empty;

    /// <summary>An activity opened in a method body, with the span over which it is open.</summary>
    /// <param name="Name">The variable the activity is bound to.</param>
    /// <param name="Begin">Index of the opening <c>using var ... = Progress.Begin(</c>.</param>
    /// <param name="Complete">
    /// Index of the first <c>Name.Complete(</c> after the open, or -1 if the method never
    /// completes it explicitly.
    /// </param>
    /// <param name="ScopeEnd">
    /// Index of the closing brace of the block the <c>using</c> declaration sits in - where the
    /// activity is disposed and stops being open.
    /// </param>
    internal sealed record OpenActivity(string Name, int Begin, int Complete, int ScopeEnd);

    /// <summary>
    /// The activities a method opens. <paramref name="code"/> must be the CODE view
    /// (<see cref="CodeOf"/>): before review finding prog-3 this read the raw body, so a method
    /// holding the literal <c>"using var activity = Progress.Begin("</c> had an activity as far
    /// as the regex was concerned, and conditions 2 and 3 then passed with no activity at all.
    /// </summary>
    internal static IReadOnlyList<OpenActivity> ActivitiesIn(string code)
    {
        var found = new List<OpenActivity>();

        foreach (Match match in ActivityOpened.Matches(code))
        {
            var name = match.Groups[1].Value;
            var complete = code.IndexOf($"{name}.Complete(", match.Index, StringComparison.Ordinal);
            found.Add(new OpenActivity(name, match.Index, complete, EnclosingBlockEnd(code, match.Index)));
        }

        return found;
    }

    /// <summary>
    /// Where the block containing <paramref name="from"/> closes. Walked forward from the
    /// declaration rather than matched backwards from an opening brace: a `using var` can sit at
    /// any depth - inside a try, inside a local function - and what matters is only where the
    /// variable goes out of scope and the activity is disposed.
    /// </summary>
    private static int EnclosingBlockEnd(string code, int from)
    {
        var depth = 0;
        for (var i = from; i < code.Length; i++)
        {
            var c = code[i];

            // The code view has no literals left in it, so this arm should never fire. It stays
            // because a brace inside a literal must never count, and leaving the guard here
            // costs nothing if a caller ever hands this the raw body again.
            if (c == '"' || c == '\'')
            {
                i = SkipLiteral(code, i);
                continue;
            }

            if (c == '{')
                depth++;
            else if (c == '}' && depth-- == 0)
                return i;
        }

        return code.Length;
    }
}

/// <summary>
/// The assertions over <see cref="ProgressRegistry"/>. Read that file's remarks first: it carries
/// the design, including what this suite deliberately cannot prove.
/// </summary>
public class ProgressRegistryTests
{
    private static ProgressRegistry.PageEntry EntryFor(string page) =>
        ProgressRegistry.Pages.Single(p => string.Equals(p.Page, page, StringComparison.Ordinal));

    private static IEnumerable<string> RegisteredMethods(ProgressRegistry.PageEntry entry) =>
        entry.Reports.Select(r => r.Method)
            .Concat(entry.KnownGaps.Select(g => g.Method))
            .Concat(entry.Exempt.Select(e => e.Method));

    /// <summary>
    /// The headline. The covered set comes from the filesystem, so a page cannot be uncovered by
    /// being forgotten - which is exactly how AdminSettings, AdminBulkJobs and
    /// ExchangeOnlineConfig came to report nothing with no test looking at any of them.
    /// </summary>
    [Fact]
    public void EveryPageUnderComponentsPagesIsRegistered()
    {
        var onDisk = ProgressScan.PageFiles().Select(Path.GetFileName).ToList();
        var registered = ProgressRegistry.Pages.Select(p => p.Page).ToList();

        var missing = onDisk.Except(registered, StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0,
            "These pages exist under Components/Pages and have no entry in ProgressRegistry. A "
            + "page is covered or it carries a written exemption; forgetting is not one of the "
            + "options:\n" + string.Join("\n", missing));

        var stale = registered.Except(onDisk, StringComparer.Ordinal).ToList();
        Assert.True(stale.Count == 0,
            "These ProgressRegistry entries name a page that no longer exists:\n"
            + string.Join("\n", stale));

        Assert.Equal(registered.Count, registered.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// The price of keying by name instead of by line: a rename must not silently orphan an
    /// entry. A line-keyed registry fails loudly on any edit, which is its whole cost; this one
    /// survives edits and so needs this one guard to catch the edit it cannot survive.
    /// </summary>
    [Fact]
    public void EveryRegisteredOperationNamesAMethodThatStillExists()
    {
        var offenders = new List<string>();

        foreach (var page in ProgressScan.ScanAll())
        {
            var entry = EntryFor(page.Page);
            foreach (var method in RegisteredMethods(entry))
            {
                if (!page.Methods.ContainsKey(method))
                    offenders.Add($"{page.Page}: {method}");
            }

            var duplicated = RegisteredMethods(entry)
                .GroupBy(m => m, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => $"{page.Page}: {g.Key} appears in more than one list");
            offenders.AddRange(duplicated);
        }

        Assert.True(offenders.Count == 0,
            "These registry entries no longer match the page. A renamed method orphans its entry "
            + "and the operation becomes unregistered; re-point the entry at the new name:\n"
            + string.Join("\n", offenders));
    }

    /// <summary>
    /// Failure condition 4: an operation handler on a covered page that is in none of the three
    /// lists.
    /// </summary>
    [Fact]
    public void EveryOperationHandlerOnACoveredPageIsClassified()
    {
        var offenders = new List<string>();

        foreach (var page in ProgressScan.ScanAll())
        {
            var registered = RegisteredMethods(EntryFor(page.Page)).ToHashSet(StringComparer.Ordinal);

            foreach (var operation in page.Operations)
            {
                if (registered.Contains(operation))
                    continue;

                var line = page.Methods.TryGetValue(operation, out var m) ? m.Line : 0;
                offenders.Add($"{page.Page}:{line} {operation}");
            }
        }

        Assert.True(offenders.Count == 0,
            "These operator-initiated handlers are in none of ProgressRegistry's three lists. "
            + "Each needs a Reported entry naming the call it covers, a KnownGap recording the "
            + "defect, or an Exempted entry with a written reason:\n" + string.Join("\n", offenders));
    }

    /// <summary>
    /// An inline lambda handler either classifies as pure local UI or gets its method extracted.
    /// An awaiting lambda is neither: it is slow work with no name for an entry to hold.
    /// </summary>
    [Fact]
    public void NoHandlerIsAnInlineLambdaThatAwaits()
    {
        var offenders = ProgressScan.ScanAll()
            .SelectMany(p => p.UnextractedLambdas.Select(l => $"{p.Page} {l}"))
            .ToList();

        Assert.True(offenders.Count == 0,
            "These handlers await inside an inline lambda, so there is no method for the registry "
            + "to name. Extract a method and register it:\n" + string.Join("\n", offenders));
    }

    /// <summary>
    /// Every covered call an entry names, paired with its method body. One Reported entry may name
    /// several, and conditions 1 to 3 each hold for every one of them independently - which is the
    /// whole point of allowing several (see <see cref="ProgressRegistry.Reported"/>).
    /// </summary>
    /// <param name="Code">
    /// The method's body in the CODE view - comments and string literals blanked. The call is
    /// looked for in this, the activity is found in this, and the window is measured in this.
    /// Before review finding prog-3 the activity was found in the RAW body instead, and a string
    /// literal reading <c>"using var activity = Progress.Begin("</c> was enough to satisfy
    /// conditions 2 and 3 with no activity in the method at all.
    /// </param>
    private static IEnumerable<(ProgressScan.PageScan Page, ProgressRegistry.Reported Entry, string Call, string Code)>
        CoveredCalls()
    {
        foreach (var page in ProgressScan.ScanAll())
        {
            foreach (var reported in EntryFor(page.Page).Reports)
            {
                var code = ProgressScan.CodeOf(page, reported.Method);
                foreach (var call in reported.CoveredCalls)
                    yield return (page, reported, call, code);
            }
        }
    }

    /// <summary>
    /// Every position in <paramref name="body"/> at which <paramref name="call"/> appears. Checked
    /// at EVERY occurrence, not just the first: an operation that does the named call once inside
    /// the window and again after it has completed is the same defect as doing it outside the
    /// window altogether, and a first-occurrence check cannot see the second one.
    /// </summary>
    private static List<int> OccurrencesOf(string body, string call)
    {
        var found = new List<int>();
        for (var i = body.IndexOf(call, StringComparison.Ordinal); i >= 0;
             i = body.IndexOf(call, i + 1, StringComparison.Ordinal))
        {
            found.Add(i);
        }

        return found;
    }

    /// <summary>
    /// An entry that named NOTHING would satisfy conditions 1 to 3 vacuously - every one of them
    /// iterates the covered calls, and iterating an empty list passes. That is precisely the
    /// failure class this registry exists to stop, so the empty entry is refused here.
    /// </summary>
    [Fact]
    public void EveryReportedOperationNamesAtLeastOneCallAndNamesEachOnce()
    {
        var offenders = new List<string>();

        foreach (var entry in ProgressRegistry.Pages)
        {
            foreach (var reported in entry.Reports)
            {
                if (reported.CoveredCalls.Count == 0)
                {
                    offenders.Add($"{entry.Page}: {reported.Method} names no covered call at all");
                    continue;
                }

                if (reported.CoveredCalls.Any(string.IsNullOrWhiteSpace))
                    offenders.Add($"{entry.Page}: {reported.Method} names a blank covered call");

                var duplicated = reported.CoveredCalls
                    .GroupBy(c => c, StringComparer.Ordinal)
                    .Where(g => g.Count() > 1)
                    .Select(g => $"{entry.Page}: {reported.Method} names '{g.Key}' more than once");
                offenders.AddRange(duplicated);
            }
        }

        Assert.True(offenders.Count == 0,
            "A Reported entry claims its activity covers something, so it has to say what. An "
            + "entry naming nothing passes conditions 1 to 3 by having nothing to check, which "
            + "is a vacuous guard wearing the costume of a real one:\n" + string.Join("\n", offenders));
    }

    /// <summary>
    /// Failure condition 1: a Reports entry whose named call does not appear in the method.
    /// Matched against the code view, so neither prose nor a string literal naming a service can
    /// satisfy it.
    /// </summary>
    [Fact]
    public void AReportedOperationContainsTheCallItClaimsToCover()
    {
        var offenders = new List<string>();

        foreach (var (page, reported, call, code) in CoveredCalls())
        {
            if (code.Length == 0)
                continue; // Reported by EveryRegisteredOperationNamesAMethodThatStillExists.

            if (!code.Contains(call, StringComparison.Ordinal))
                offenders.Add($"{page.Page}: {reported.Method} does not call '{call}'");
        }

        Assert.True(offenders.Count == 0,
            "A Reported entry names the slow or mutating call its activity covers, the call must "
            + "be in the method's OWN body, and it must be CODE - comments and string literals "
            + "are blanked before the search. Crediting a call made by a helper is what let "
            + "BlockedSenders.ConfirmUnblock - which calls the reporting LoadBlockedSenders - "
            + "pass the first draft of this guard while its Exchange write stayed silent:\n"
            + string.Join("\n", offenders));
    }

    /// <summary>
    /// Failure condition 2: a Reports entry where Progress.Begin does not dominate the named call
    /// in source order. Every named call, at every occurrence.
    /// </summary>
    [Fact]
    public void AReportedOperationOpensItsActivityBeforeTheCallItCovers()
    {
        var offenders = new List<string>();

        foreach (var (page, reported, call, code) in CoveredCalls())
        {
            var activities = ProgressScan.ActivitiesIn(code);

            foreach (var at in OccurrencesOf(code, call))
            {
                if (activities.Any(a => a.Begin < at))
                    continue;

                offenders.Add(
                    $"{page.Page}: {reported.Method} opens no activity before '{call}' "
                    + $"at offset {at}"
                    + (activities.Count == 0
                        ? " (it begins no activity at all)"
                        : " (its earliest Begin is after it)"));
            }
        }

        Assert.True(offenders.Count == 0,
            "The activity must be OPEN before the covered call runs. An activity opened "
            + "afterwards leaves the operator watching an idle bar through the slow part, which "
            + "is the PARTIAL shape this registry records on nine operations today:\n"
            + string.Join("\n", offenders));
    }

    /// <summary>
    /// Failure condition 3: a Reports entry where Complete, or the end of the using scope,
    /// precedes the named call. Every named call, at every occurrence.
    /// </summary>
    [Fact]
    public void AReportedOperationIsStillReportingWhenTheCoveredCallRuns()
    {
        var offenders = new List<string>();

        foreach (var (page, reported, call, code) in CoveredCalls())
        {
            var activities = ProgressScan.ActivitiesIn(code);

            foreach (var at in OccurrencesOf(code, call))
            {
                var dominating = activities.Where(a => a.Begin < at).ToList();
                if (dominating.Count == 0)
                    continue; // Reported by condition 2.

                if (dominating.Any(a => a.ScopeEnd > at && (a.Complete < 0 || a.Complete > at)))
                    continue;

                var first = dominating[0];
                var why = first.Complete >= 0 && first.Complete < at
                    ? $"'{first.Name}.Complete(' runs at offset {first.Complete}, before '{call}' at {at}"
                    : $"'{first.Name}' leaves scope at offset {first.ScopeEnd}, before '{call}' at {at}";

                offenders.Add($"{page.Page}: {reported.Method} - {why}");
            }
        }

        Assert.True(offenders.Count == 0,
            "The activity must still be open AT the covered call. Completing first reports "
            + "success while the operator is still waiting - the defect recorded on "
            + "MessageTraceReports.Download, where the activity completes at :159 and the "
            + "transfer runs at :163:\n" + string.Join("\n", offenders));
    }

    /// <summary>Failure condition 5: an Exempt entry with an empty reason.</summary>
    [Fact]
    public void EveryExemptionCarriesAWrittenReason()
    {
        var offenders = new List<string>();

        foreach (var entry in ProgressRegistry.Pages)
        {
            foreach (var exempt in entry.Exempt)
            {
                if (string.IsNullOrWhiteSpace(exempt.Reason))
                    offenders.Add($"{entry.Page}: {exempt.Method}");
            }
        }

        Assert.True(offenders.Count == 0,
            "An exemption without a reason is an oversight that looks like a decision. Say why "
            + "the operation needs no activity:\n" + string.Join("\n", offenders));
    }

    /// <summary>
    /// The guard on the one way an exemption is allowed to lean on another method's activity. An
    /// entry may say "this is only a dispatcher" - but then it has to call what it names, and
    /// what it names has to be registered in its own right, so the work is accounted for exactly
    /// once and never implicitly.
    /// </summary>
    [Fact]
    public void ADispatchersExemptionNamesAMethodItActuallyCallsAndThatIsItselfRegistered()
    {
        var offenders = new List<string>();

        foreach (var page in ProgressScan.ScanAll())
        {
            var entry = EntryFor(page.Page);
            var registered = RegisteredMethods(entry).ToHashSet(StringComparer.Ordinal);

            foreach (var exempt in entry.Exempt.Where(e => e.DelegatesTo.Length > 0))
            {
                // The code view, for the same reason conditions 1 to 3 read it: a dispatcher
                // whose delegation target appeared only inside a string literal would otherwise
                // satisfy "it really calls it" without calling it (review finding prog-3).
                var body = ProgressScan.CodeOf(page, exempt.Method);
                if (body.Length > 0 && !body.Contains(exempt.DelegatesTo, StringComparison.Ordinal))
                    offenders.Add($"{page.Page}: {exempt.Method} does not call {exempt.DelegatesTo}");

                if (!registered.Contains(exempt.DelegatesTo))
                    offenders.Add($"{page.Page}: {exempt.Method} delegates to the unregistered {exempt.DelegatesTo}");
            }
        }

        Assert.True(offenders.Count == 0,
            "A dispatcher's exemption must name a method it really calls and that carries its own "
            + "entry. Otherwise 'it only delegates' becomes the loophole that credits a helper's "
            + "activity to a caller doing its own slow work:\n" + string.Join("\n", offenders));
    }

    /// <summary>
    /// The registry is now the gap list (docs/ProgressCoverage-Plan.md S1), so it has to stay
    /// readable as one: a KnownGap says what is wrong and points at a line.
    /// </summary>
    [Fact]
    public void EveryKnownGapSaysWhatIsWrongAndWhere()
    {
        var offenders = new List<string>();

        foreach (var entry in ProgressRegistry.Pages)
        {
            foreach (var gap in entry.KnownGaps)
            {
                if (string.IsNullOrWhiteSpace(gap.Defect) || gap.Line <= 0)
                    offenders.Add($"{entry.Page}: {gap.Method}");
            }
        }

        Assert.True(offenders.Count == 0,
            "A KnownGap is a recorded defect; it needs its line and a description of what is "
            + "silent:\n" + string.Join("\n", offenders));
    }

    /// <summary>
    /// The six operations where an operator can start an irreversible change and see nothing stay
    /// named until they are fixed. Not a structural rule - a tripwire, so a later slice that
    /// quietly drops one of these entries instead of fixing the page has to say so here.
    /// </summary>
    [Fact]
    public void TheSurveysSixSilentWritesAreStillAccountedFor()
    {
        var silentWrites = new[]
        {
            ("CloudPasswordReset.razor", "ExecuteResetAsync"),
            ("OutOfOffice.razor", "SetOof"),
            ("ADAttributeEditor.razor", "ConfirmSave"),
            ("ConferenceRooms.razor", "SetSingleRoomType"),
            ("BlockedSenders.razor", "ConfirmUnblock"),
            ("ExchangeOnlineConfig.razor", "SaveExoConfig"),
        };

        foreach (var (page, method) in silentWrites)
        {
            var entry = EntryFor(page);
            var known = entry.KnownGaps.Any(g => string.Equals(g.Method, method, StringComparison.Ordinal));
            var reports = entry.Reports.Any(r => string.Equals(r.Method, method, StringComparison.Ordinal));

            Assert.True(known || reports,
                $"{page} {method} is one of the six silent mutating operations in "
                + "docs/ProgressCoverage-Plan.md. It is either still a KnownGap or it has been "
                + "fixed and is now Reported; dropping it to Exempt needs an owner decision, not "
                + "a registry edit.");
        }
    }

    // ---------------------------------------------------------------------------------------
    // The scanner's own tests. Everything above asks what the pages say; everything below asks
    // whether the thing doing the asking can tell code from data. Review findings prog-1 and
    // prog-3 were both "the rule can be satisfied by text that is not code", so the answer is
    // now asserted rather than assumed.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The headline negative: the literal review finding prog-3 was written about. A method that
    /// merely CONTAINS the text of a Progress.Begin opens no activity, and conditions 2 and 3
    /// must find none.
    /// </summary>
    [Fact]
    public void AProgressBeginInsideAStringLiteralOpensNoActivity()
    {
        const string method = """
            private async Task Probe()
            {
                var pretend = "using var activity = Progress.Begin(";
                await Svc.SlowCallAsync();
            }
            """;

        var code = ProgressScan.CodeView(method);

        Assert.Empty(ProgressScan.ActivitiesIn(code));
        Assert.DoesNotContain("Progress.Begin(", code, StringComparison.Ordinal);

        // And the same text as real code still is one, so the assertion above is about the
        // quoting and not about the pattern having stopped working.
        var real = ProgressScan.CodeView(method.Replace("\"using var", "using var", StringComparison.Ordinal));
        Assert.Single(ProgressScan.ActivitiesIn(real));
    }

    /// <summary>
    /// The other half: a covered call's name inside a string literal does not satisfy condition
    /// 1, which is the prog-1 probe re-run against the scanner instead of against a live page.
    /// </summary>
    [Fact]
    public void ACoveredCallNameInsideAStringLiteralIsNotACall()
    {
        const string method = """
            private async Task Probe()
            {
                using var activity = Progress.Begin("Resetting");
                var pretend = "ResetService.DeriveDestination(";
                await Svc.SomethingElseAsync();
            }
            """;

        var code = ProgressScan.CodeView(method);

        Assert.DoesNotContain("ResetService.DeriveDestination(", code, StringComparison.Ordinal);
        Assert.Single(ProgressScan.ActivitiesIn(code));
    }

    /// <summary>
    /// Every literal form that has to be blanked, each one a bypass attempted against the
    /// blanker. The payload is the same in all of them, so a row that survives shows up as the
    /// payload surviving rather than as a parse detail.
    /// </summary>
    [Theory]
    // The prog-3 defect itself: '$' alone is interpolated, not verbatim, so the escaped quote
    // does not end the literal. Reading one character back called this verbatim, ended the
    // literal early and left everything after it outside any literal.
    [InlineData("""var s = $"he said \"hi\" {Payload.Call(} and more";""")]
    // An escaped backslash immediately before the closing quote: the quote really does close,
    // so the NEXT literal still starts where it should.
    [InlineData("""var a = "C:\\"; var s = "Payload.Call(";""")]
    // Verbatim escapes its quote by doubling it, and nothing by backslash.
    [InlineData(""" var s = @"a ""Payload.Call("" b"; """)]
    // An interpolation hole holding its own quoted string.
    [InlineData(""" var s = $"{Fmt("Payload.Call(")} tail"; """)]
    // A char literal that IS a quote: the quote it holds must not open a literal.
    [InlineData(""" var c = '"'; var s = "Payload.Call("; """)]
    // A '//' inside a string is text, not a comment - the reason comments and literals are
    // blanked in ONE pass. Blanking comments first eats this literal's closing quote; a VERBATIM
    // literal then runs on past the end of its line, the next literal pairs one quote out of
    // step, and the payload - real string content - is left outside every literal and readable
    // as code. Verbatim deliberately: a single-line literal ends at the newline, so the same
    // mistake on a plain string is self-limiting and would not show the defect.
    [InlineData("""
                var s = @"a // b";
                var t = "Payload.Call(";
                """)]
    // The plain-string form of the same attempt. It does NOT discriminate - a non-verbatim
    // literal ends at the newline, so a comment-first pass would swallow the payload with the
    // rest of that line rather than expose it. Kept because it is the obvious shape to try and
    // because it pins the newline rule that makes it harmless.
    [InlineData("""
                var u = "https://example.test/x";
                var s = "Payload.Call(";
                """)]
    // A quote inside a comment is text, not a literal.
    [InlineData("""
                // he said "hi
                var s = "Payload.Call(";
                """)]
    public void NoStringFormSmugglesTextPastTheBlanker(string line)
    {
        Assert.DoesNotContain("Payload.Call(", ProgressScan.CodeView(line), StringComparison.Ordinal);
    }

    /// <summary>
    /// Raw string literals, which the blanker could not see at all before: the multi-line form
    /// does not terminate at a newline, so its contents were left as code.
    /// </summary>
    [Fact]
    public void ARawStringLiteralIsBlankedWholeIncludingItsNewlines()
    {
        var method = "private void Probe()\n{\n    var s = \"\"\"\n"
            + "        using var activity = Progress.Begin(\n"
            + "        ResetService.DeriveDestination(\n"
            + "        \"\"\";\n"
            + "    Done();\n}\n";

        var code = ProgressScan.CodeView(method);

        Assert.DoesNotContain("Progress.Begin(", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ResetService.DeriveDestination(", code, StringComparison.Ordinal);
        Assert.Empty(ProgressScan.ActivitiesIn(code));

        // The code around it survives, so the blanker is not simply erasing the method.
        Assert.Contains("Done();", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// Blanking is same-length, which is what lets a method span found in the discovery view
    /// address the same text in the code view. A shortening blanker would silently mis-slice
    /// every body and the conditions would be reading the wrong method.
    /// </summary>
    [Fact]
    public void TheCodeViewIsTheSameLengthAndShapeAsTheSource()
    {
        foreach (var page in ProgressScan.ScanAll())
        {
            Assert.Equal(page.Source.Length, page.Code.Length);
            Assert.Equal(
                page.Source.Count(c => c == '\n'),
                page.Code.Count(c => c == '\n'));
        }
    }

    /// <summary>
    /// The asymmetry, asserted so it cannot be "tidied up". Handler discovery reads the
    /// DISCOVERY view because handler names are quoted markup attribute values; blanking
    /// literals before discovery would delete the operation list and leave every assertion over
    /// it passing on an empty set. This is the shape review finding prog-2 already cost once.
    /// </summary>
    [Fact]
    public void HandlerDiscoveryStillReadsQuotedMarkupAttributes()
    {
        var page = ProgressScan.ScanAll()
            .Single(p => string.Equals(p.Page, "CloudPasswordReset.razor", StringComparison.Ordinal));

        // Both are @onclick="..." in the markup at :51 and :167 - quoted values, invisible to
        // any scan that reads the code view.
        Assert.Contains("LookupAsync", page.Operations);
        Assert.Contains("ExecuteResetAsync", page.Operations);

        Assert.DoesNotContain("ExecuteResetAsync\"", page.Code, StringComparison.Ordinal);
    }
}
