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
        // lookbehind keeps this off the DOM's own @onchange, which is a different surface and
        // carries no recorded gap today.
        new(@"(?<![@\w])OnChange\s*=\s*(?:""([^""]*)""|'([^']*)')", RegexOptions.Compiled),
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
    internal sealed record PageScan(
        string Page,
        string Source,
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
    /// The same same-length blanking as <see cref="StripComments"/>, applied to the CONTENTS of
    /// string literals. Comments were blanked because prose naming a service would satisfy a
    /// Reports entry on its own; a string literal holding the same text does exactly that too,
    /// and it was demonstrated: deleting CloudPasswordReset's real DeriveDestination call and
    /// leaving `var x = "ResetService.DeriveDestination(";` inside the activity passed all three
    /// conditions (review finding prog-1, known gaps).
    /// </summary>
    /// <remarks>
    /// Used ONLY for locating covered calls, never for finding activities and never for method
    /// discovery. Discovery reads handler names out of markup attributes, which ARE quoted, so
    /// blanking there would delete the operation list; and <see cref="ActivitiesIn"/> keeps the
    /// raw body so that a blanking mistake can only hide an activity's Complete from itself -
    /// the direction that fails a test rather than passing one. Indices are preserved, so the
    /// blanked body and the raw body address the same positions.
    /// </remarks>
    internal static string StripStringLiterals(string source)
    {
        var text = new StringBuilder(source);

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '"' && text[i] != '\'')
                continue;

            // SkipLiteral answers i-1 for a single-quoted run that hits a newline unterminated.
            // Without this the cursor would walk backwards onto the same quote for ever.
            var close = SkipLiteral(source, i);
            if (close <= i)
                continue;

            for (var j = i + 1; j < close && j < text.Length; j++)
            {
                if (text[j] != '\n')
                    text[j] = ' ';
            }

            i = close;
        }

        return text.ToString();
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

            var source = StripComments(File.ReadAllText(path));
            var methods = DeclaredMethods(source);
            var (operations, unextracted) = Handlers(source, methods);

            var scan = new PageScan(Path.GetFileName(path), source, methods, operations, unextracted);
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

    private static int SkipLiteral(string source, int start)
    {
        var quote = source[start];
        var verbatim = start > 0 && (source[start - 1] == '@' || source[start - 1] == '$');

        for (var i = start + 1; i < source.Length; i++)
        {
            if (source[i] == '\\' && !verbatim)
            {
                i++;
                continue;
            }

            if (source[i] == quote)
                return i;

            if (source[i] == '\n' && !verbatim)
                return i - 1;
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

    internal static string BodyOf(PageScan page, string method) =>
        page.Methods.TryGetValue(method, out var found)
            ? page.Source[found.Start..found.End]
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

    internal static IReadOnlyList<OpenActivity> ActivitiesIn(string body)
    {
        var found = new List<OpenActivity>();

        foreach (Match match in ActivityOpened.Matches(body))
        {
            var name = match.Groups[1].Value;
            var complete = body.IndexOf($"{name}.Complete(", match.Index, StringComparison.Ordinal);
            found.Add(new OpenActivity(name, match.Index, complete, EnclosingBlockEnd(body, match.Index)));
        }

        return found;
    }

    /// <summary>
    /// Where the block containing <paramref name="from"/> closes. Walked forward from the
    /// declaration rather than matched backwards from an opening brace: a `using var` can sit at
    /// any depth - inside a try, inside a local function - and what matters is only where the
    /// variable goes out of scope and the activity is disposed.
    /// </summary>
    private static int EnclosingBlockEnd(string body, int from)
    {
        var depth = 0;
        for (var i = from; i < body.Length; i++)
        {
            var c = body[i];
            if (c == '"' || c == '\'')
            {
                i = SkipLiteral(body, i);
                continue;
            }

            if (c == '{')
                depth++;
            else if (c == '}' && depth-- == 0)
                return i;
        }

        return body.Length;
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
    /// <param name="Body">
    /// The method's source as written. Activities are found in THIS, not in <c>Code</c>.
    /// </param>
    /// <param name="Code">
    /// The same body with string-literal contents blanked, which is where a covered call is
    /// looked for. Same length and therefore the same indices as <c>Body</c>.
    /// </param>
    private static IEnumerable<(ProgressScan.PageScan Page, ProgressRegistry.Reported Entry, string Call, string Body, string Code)>
        CoveredCalls()
    {
        foreach (var page in ProgressScan.ScanAll())
        {
            foreach (var reported in EntryFor(page.Page).Reports)
            {
                var body = ProgressScan.BodyOf(page, reported.Method);
                var code = ProgressScan.StripStringLiterals(body);
                foreach (var call in reported.CoveredCalls)
                    yield return (page, reported, call, body, code);
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
    /// Matched against the comment-stripped body, so prose naming a service cannot satisfy it.
    /// </summary>
    [Fact]
    public void AReportedOperationContainsTheCallItClaimsToCover()
    {
        var offenders = new List<string>();

        foreach (var (page, reported, call, body, code) in CoveredCalls())
        {
            if (body.Length == 0)
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

        foreach (var (page, reported, call, body, code) in CoveredCalls())
        {
            var activities = ProgressScan.ActivitiesIn(body);

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

        foreach (var (page, reported, call, body, code) in CoveredCalls())
        {
            var activities = ProgressScan.ActivitiesIn(body);

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
                var body = ProgressScan.BodyOf(page, exempt.Method);
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
}
