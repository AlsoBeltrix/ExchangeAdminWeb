using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Source scanner for <see cref="EventSubscriptionLifetimeTests"/>: finds every delegate
/// subscription in <c>Components/</c> and decides whether Dispose can lose a race with it.
/// </summary>
/// <remarks>
/// <para>
/// THE DEFECT THIS EXISTS FOR. A component subscribes to an event on a DI SINGLETON after an
/// await. If the circuit goes away during that await, Dispose runs first, its <c>-=</c> removes a
/// handler that is not there yet, and the continuation then adds a DEAD component to an event that
/// lives for the whole process. Through the component's injected NavigationManager and IJSRuntime
/// that roots the entire circuit and its scoped container, and Blazor's disconnected-circuit
/// eviction cannot reclaim it, because an external GC root beats eviction.
/// </para>
/// <para>
/// WHY A RULE AND NOT A LIST. This was found, understood and fixed once already: commit
/// <c>1ed797a</c> put the guard into Components/Shared/ and carries a comment in
/// GlobalProgress.razor describing the failure in plain words. Components/Pages/ was never swept,
/// and the two pages with the identical bug sat there until docs/CircuitLifetimeLeak-Plan.md.
/// A test naming those two pages would have been satisfied by the fix and would catch nothing.
/// This one is written against the shape, so it fires on the eighth site nobody has written yet.
/// </para>
/// <para>
/// THE RULE. On every control-flow path that reaches a delegate subscription from the last
/// preceding <c>await</c>, a returning disposed-check must DOMINATE the subscription - it must be
/// impossible to arrive at the <c>+=</c> without having run the check. Where the await is in a
/// CALLER rather than in the subscribing method, the rule applies at the call instead, recursively:
/// a helper entered after an unguarded await is in exactly the same race as the await's own method.
/// Where no await can precede the subscription at all, there is no window and nothing to prove. The
/// only sanctioned guard shape is the early return the four fixed components already use:
/// </para>
/// <code>
/// if (_disposed)
///     return;
/// </code>
/// <para>
/// and the flag it reads must be one the component's own Dispose sets to true, so a never-assigned
/// field cannot buy a pass.
/// </para>
/// <para>
/// WHY ROSLYN. The predecessor was a regex over blanked source and review finding leak-1 broke it
/// three ways, each executed against the compiled scanner: a subscription moved into a helper
/// (regex computes awaits only inside the method physically holding the <c>+=</c>), a guard nested
/// inside an unrelated <c>if</c> or sitting in a <c>try</c> whose <c>finally</c> does the
/// subscribing (regex compares offsets, and an offset between two others is not dominance), and the
/// <c>delegate (...) { }</c> form, which has no <c>=&gt;</c> in it and so was not seen as a
/// subscription AT ALL. The first two are control flow and the third is syntax; a syntax tree
/// answers all three, and <see cref="RazorSyntax"/> is what gets one out of a <c>.razor</c> file.
/// Dominance here is the structured kind - a guard is credited only when it is a preceding
/// statement in a statement list that encloses the subscription - and a method containing any
/// <c>goto</c> or label is refused outright rather than reasoned about.
/// </para>
/// <para>
/// AND WHY A TREE IS STILL NOT ENOUGH. A tree answers what is WRITTEN WHERE; dominance over a
/// statement list is such a question, and it is only the same thing as "the guard ran first" while
/// the enclosing body runs top to bottom from one entry point. A LOCAL FUNCTION and a LAMBDA both
/// break that: their bodies run where they are CALLED, not where they are written, so a guard
/// written above one of them can execute after it, or never. Review finding prog-10(a) is exactly
/// that - a subscription in a local function declared BELOW the guard it was credited with, which
/// in execution order runs before it. Nothing available here says when the invocation happens, so a
/// subscription inside either shape is REFUSED rather than reasoned about. The refusal costs two
/// correct shapes - guarded at the call, and guarded inside the local function - and that is the
/// price of not guessing. No component carries either today; the fix in both cases is to subscribe
/// from a method, where the call-site analysis above applies.
/// </para>
/// <para>
/// HONEST LIMITATIONS. It is syntactic, with no semantic model: a method is matched to a call by
/// NAME, so an overload or a same-named method on another type is treated as the page's own, in the
/// conservative direction. Classification is deliberately over-inclusive: a <c>+=</c> whose
/// right-hand side is a lambda, or an identifier that looks like a member name, is treated as a
/// subscription even if it is really an accumulation. Over-inclusion costs a clear failure message
/// on code that is fine; under-inclusion costs the whole guard, which is the mistake this replaces.
/// </para>
/// </remarks>
internal static class EventSubscriptionScan
{
    /// <summary>One <c>+=</c> the scanner classified as a delegate subscription.</summary>
    /// <param name="Problem">Empty when it obeys the rule; otherwise what is wrong with it.</param>
    internal sealed record Subscription(
        string File,
        string Method,
        int Line,
        string Target,
        string Handler,
        string Problem);

    /// <summary>One <c>-=</c>, for the anti-vacuity cross-check.</summary>
    internal sealed record Unsubscribe(string File, int Line, string Target);

    /// <summary>What one component file says about its own subscriptions.</summary>
    internal sealed record FileResult(
        string File,
        IReadOnlyList<Subscription> Subscriptions,
        IReadOnlyList<Unsubscribe> Unsubscribes);

    private static readonly string[] DisposeMethods = ["Dispose", "DisposeAsync"];

    private const string AfterAwait =
        "sits after an await with no disposed-guard dominating it. Dispose can run during that "
        + "await, unsubscribe nothing, and leave this handler on the event for the life of the "
        + "process. Add the guard Components/Shared/GlobalProgress.razor uses, immediately before "
        + "the subscription: if (_disposed) return;";

    private const string NoEnclosingMethod =
        "the scanner cannot tell which method this subscription is in, so it cannot tell whether "
        + "an await precedes it. Extend EventSubscriptionScan rather than leaving the "
        + "subscription unchecked";

    /// <summary>
    /// Why a deferred body is refused outright. Shared by both shapes so the two messages cannot
    /// drift apart, and so a failure reads the same whichever one produced it.
    /// </summary>
    private const string DeferredBody =
        ", which does not run where it is written - it runs where it is CALLED. The rule credits "
        + "a guard by proving it is an earlier statement in a list enclosing the subscription, "
        + "and that proves nothing about a body invoked somewhere else: a guard written above "
        + "this one can execute after it, or not at all (review finding prog-10). Nothing here "
        + "says when the invocation happens, so the shape is refused instead of guessed at. "
        + "Subscribe from a method of the component, where the call-site analysis applies.";

    /// <summary>
    /// Every component source file. Taken from the filesystem, never from a list: a page that is
    /// absent from a hand-maintained list is a page the rule does not reach, which is the failure
    /// mode this whole file exists to close.
    /// </summary>
    internal static IReadOnlyList<string> ComponentFiles()
    {
        var root = Path.Combine(ProgressScan.RepoRoot(), "Components");

        return Directory.GetFiles(root, "*.razor", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
    }

    internal static IReadOnlyList<FileResult> ScanComponents() =>
        ComponentFiles()
            .Select(path => ScanOne(Path.GetFileName(path), path, File.ReadAllText(path)))
            .ToList();

    internal static IReadOnlyList<Subscription> ScanAll() =>
        ScanComponents().SelectMany(f => f.Subscriptions).ToList();

    /// <summary>
    /// The scan over text rather than a file on disk, so the mechanics can be exercised against
    /// fixtures that depend on no component.
    /// </summary>
    internal static IReadOnlyList<Subscription> ScanText(string file, string raw) =>
        ScanOne(file, file, raw).Subscriptions;

    private static FileResult ScanOne(string file, string path, string raw)
    {
        var component = new Component(file, raw, RazorSyntax.ParseFile(path, raw));
        var subscriptions = new List<Subscription>();

        foreach (var assignment in component.Assignments(SyntaxKind.AddAssignmentExpression))
        {
            var target = Collapse(assignment.Left);

            if (!IsDelegateSubscription(component, assignment, target))
                continue;

            var line = component.LineOf(assignment);
            var handler = Describe(assignment.Right);
            var method = assignment.FirstAncestorOrSelf<MethodDeclarationSyntax>();

            if (method is null)
            {
                subscriptions.Add(new Subscription(file, "<no enclosing method>", line, target, handler,
                    NoEnclosingMethod));
                continue;
            }

            subscriptions.Add(new Subscription(file, method.Identifier.ValueText, line, target, handler,
                ProblemAt(component, assignment, method, [])));
        }

        var unsubscribes = component.Assignments(SyntaxKind.SubtractAssignmentExpression)
            .Where(a => a.Right is AnonymousFunctionExpressionSyntax || IsNameChain(a.Right))
            .Select(a => new Unsubscribe(file, component.LineOf(a), Collapse(a.Left)))
            .ToList();

        return new FileResult(file, subscriptions, unsubscribes);
    }

    private static bool IsDelegateSubscription(
        Component component,
        AssignmentExpressionSyntax assignment,
        string target)
    {
        // A lambda or an anonymous delegate can only be added to a delegate; nothing else accepts
        // one. Both forms are one node kind to a syntax tree, which is what closes review finding
        // leak-1c: the predecessor keyed on the text "=>", so `delegate (string id) { }` was not a
        // subscription as far as it was concerned. That form is also the worse of the two, because
        // an anonymous handler cannot be unsubscribed at all.
        if (assignment.Right is AnonymousFunctionExpressionSyntax)
            return true;

        if (!IsNameChain(assignment.Right))
            return component.Unsubscribed.Contains(target);

        var name = LastName(assignment.Right);
        if (name.Length == 0)
            return component.Unsubscribed.Contains(target);

        // A method group names a method, and C# names methods in PascalCase. "_OnChanged" is
        // allowed for the same reason. A camelCase right-hand side ("total += step") is an
        // accumulation unless the file proves otherwise by unsubscribing the same target.
        return char.IsUpper(name[0])
            || name[0] == '_'
            || component.Declares(name)
            || component.Unsubscribed.Contains(target);
    }

    /// <summary>
    /// The nearest body that encloses <paramref name="node"/> and is entered by a call of its
    /// own: the method, local function or anonymous function it is written inside, or null when
    /// it is in none of them (a field initializer, a constructor, a property accessor).
    /// </summary>
    private static SyntaxNode? EnclosingCallable(SyntaxNode node) =>
        node.Ancestors().FirstOrDefault(a => a is
            MethodDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax);

    /// <summary>
    /// The refusal for a subscription written inside a deferred body. See the class remarks: this
    /// is a REFUSAL, not a finding of fault, and it fires on correct code as well as on the
    /// prog-10(a) shape, because separating the two needs the invocation order the scanner has no
    /// way to establish.
    /// </summary>
    private static string DeferredProblem(SyntaxNode callable) => callable switch
    {
        LocalFunctionStatementSyntax local =>
            $"sits inside the local function '{local.Identifier.ValueText}'" + DeferredBody,
        _ => "sits inside a lambda or anonymous delegate body" + DeferredBody,
    };

    /// <summary>
    /// What is wrong with the subscription or call at <paramref name="node"/>, or an empty string
    /// when nothing is.
    /// </summary>
    private static string ProblemAt(
        Component component,
        SyntaxNode node,
        MethodDeclarationSyntax method,
        HashSet<string> visiting)
    {
        // Everything below reasons about POSITION inside this method - which await precedes the
        // node, which guard encloses it - and position is execution order only for the method's
        // own statements. A node written inside a local function or a lambda belongs to a body
        // entered by a call, so refuse it here, at the one place the position reasoning starts.
        // This catches the subscription itself and, through CallerWindow, a CALL to a
        // subscribing helper written in one (review finding prog-10a).
        var callable = EnclosingCallable(node);
        if (!ReferenceEquals(callable, method))
            return callable is null ? NoEnclosingMethod : DeferredProblem(callable);

        // The LAST await, not the first. A guard that runs before an await is back in the same
        // race the guard exists to lose safely.
        var lastAwait = LastAwaitBefore(method, node);

        // Is there a window for Dispose to run in at all? Either an await earlier in this method,
        // or this method is reachable from one that awaited before calling it. The second is the
        // method boundary the predecessor could not cross (review finding leak-1a).
        var window = lastAwait is not null
            ? AfterAwait
            : CallerWindow(component, method, visiting);

        if (window.Length == 0)
            return "";

        var guard = DominatingGuard(method, node, lastAwait?.Span.End ?? method.SpanStart);
        if (guard is null)
            return window;

        var flag = GuardFlag(guard)!;

        return component.DisposeRaises(flag)
            ? ""
            : $"guards on '{flag}', which no Dispose on this component sets to true. A flag that is "
                + "never raised is not a guard; set it as the first statement of Dispose";
    }

    /// <summary>
    /// Why a window can already be open when <paramref name="method"/> is entered, or an empty
    /// string when every call into it is itself safe. A method nothing on the page calls is a
    /// framework entry point: nothing has awaited before it runs.
    /// </summary>
    private static string CallerWindow(
        Component component,
        MethodDeclarationSyntax method,
        HashSet<string> visiting)
    {
        var name = method.Identifier.ValueText;

        // A cycle in the call graph. Whichever call site in the cycle actually follows an
        // unguarded await is still reached on its own arm of this walk, so stopping here loses
        // nothing and keeps the recursion finite.
        if (!visiting.Add(name))
            return "";

        try
        {
            foreach (var (site, caller) in component.CallsTo(name))
            {
                if (ReferenceEquals(caller, method))
                    continue;

                var why = ProblemAt(component, site, caller, visiting);
                if (why.Length == 0)
                    continue;

                return $"is reached from {caller.Identifier.ValueText}() at line "
                    + $"{component.LineOf(site)}, where the call {why}";
            }

            return "";
        }
        finally
        {
            visiting.Remove(name);
        }
    }

    private static AwaitExpressionSyntax? LastAwaitBefore(MethodDeclarationSyntax method, SyntaxNode node) =>
        method.DescendantNodes()
            .OfType<AwaitExpressionSyntax>()
            .Where(a => a.Span.End <= node.SpanStart)
            .MaxBy(a => a.Span.End);

    /// <summary>
    /// The disposed-guard that dominates <paramref name="node"/> and runs at or after
    /// <paramref name="from"/>, or null when no path-proof is available.
    /// </summary>
    private static IfStatementSyntax? DominatingGuard(
        MethodDeclarationSyntax method,
        SyntaxNode node,
        int from)
    {
        // A jump can enter the region between a guard and a subscription without passing the
        // guard. Nothing here proves it does not, so refuse to credit any guard in such a method
        // rather than reason about it.
        if (method.DescendantNodes().Any(n => n is GotoStatementSyntax or LabeledStatementSyntax))
            return null;

        return method.DescendantNodes()
            .OfType<IfStatementSyntax>()
            .Where(candidate => candidate.SpanStart >= from && candidate.Span.End <= node.SpanStart)
            .Where(IsReturningDisposedGuard)
            .FirstOrDefault(candidate => Dominates(candidate, node));
    }

    /// <summary>
    /// True when arriving at <paramref name="node"/> implies <paramref name="guard"/> already ran.
    /// </summary>
    /// <remarks>
    /// The proof is structural and deliberately narrow: the guard must be a statement EARLIER IN
    /// THE SAME STATEMENT LIST as one of the node's own ancestors. Entering that list runs the
    /// guard first, and any construct that could skip it - the node being nested inside a branch
    /// the guard is not in, the guard sitting in a <c>try</c> whose <c>finally</c> holds the node -
    /// puts the guard in a list that is not on the node's ancestor chain and is not credited. Both
    /// shapes review finding leak-1b executed against the predecessor fail here for that reason.
    /// </remarks>
    private static bool Dominates(IfStatementSyntax guard, SyntaxNode node)
    {
        var siblings = StatementsOf(guard.Parent);
        if (siblings is null)
            return false;

        for (SyntaxNode? child = node; child is not null; child = child.Parent)
        {
            if (!ReferenceEquals(child.Parent, guard.Parent))
                continue;

            return child is StatementSyntax statement
                && siblings.Value.IndexOf(statement) > siblings.Value.IndexOf(guard);
        }

        return false;
    }

    private static SyntaxList<StatementSyntax>? StatementsOf(SyntaxNode? parent) => parent switch
    {
        BlockSyntax block => block.Statements,
        SwitchSectionSyntax section => section.Statements,
        _ => null,
    };

    private static bool IsReturningDisposedGuard(IfStatementSyntax node) =>
        node.Else is null && GuardFlag(node) is not null && AlwaysReturns(node.Statement);

    private static string? GuardFlag(IfStatementSyntax node) => node.Condition switch
    {
        IdentifierNameSyntax id when IsDisposedFlag(id.Identifier.ValueText) => id.Identifier.ValueText,
        MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name: IdentifierNameSyntax n }
            when IsDisposedFlag(n.Identifier.ValueText) => n.Identifier.ValueText,
        _ => null,
    };

    private static bool AlwaysReturns(StatementSyntax body) => body switch
    {
        ReturnStatementSyntax => true,
        BlockSyntax block => block.Statements.Count > 0 && block.Statements[^1] is ReturnStatementSyntax,
        _ => false,
    };

    private static bool IsDisposedFlag(string name) =>
        name.Contains("dispos", StringComparison.OrdinalIgnoreCase);

    private static bool IsNameChain(ExpressionSyntax node) => node switch
    {
        IdentifierNameSyntax => true,
        ThisExpressionSyntax => true,
        MemberAccessExpressionSyntax m when m.IsKind(SyntaxKind.SimpleMemberAccessExpression) =>
            IsNameChain(m.Expression) && m.Name is IdentifierNameSyntax,
        _ => false,
    };

    private static string LastName(ExpressionSyntax node) => node switch
    {
        IdentifierNameSyntax id => id.Identifier.ValueText,
        MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
        _ => "",
    };

    private static string Collapse(SyntaxNode node) =>
        new(node.ToString().Where(c => !char.IsWhiteSpace(c)).ToArray());

    /// <summary>A short, readable right-hand side for the failure message.</summary>
    private static string Describe(ExpressionSyntax handler) => handler switch
    {
        AnonymousMethodExpressionSyntax => "delegate { ... }",
        LambdaExpressionSyntax => "lambda",
        _ => Collapse(handler),
    };

    /// <summary>One component file's C#, as trees plus the few whole-file facts the rule needs.</summary>
    private sealed class Component
    {
        private readonly string raw;
        private readonly IReadOnlyList<RazorSyntax.CodeRegion> regions;
        private readonly Dictionary<SyntaxTree, int> offsets;
        private readonly List<MethodDeclarationSyntax> methods;
        private readonly HashSet<string> declared;
        private readonly List<(InvocationExpressionSyntax Site, MethodDeclarationSyntax Caller)> calls;

        internal Component(string file, string raw, RazorSyntax.ParsedSource parsed)
        {
            this.File = file;
            this.raw = raw;
            this.regions = parsed.Regions;
            this.offsets = parsed.Regions.ToDictionary(r => r.Tree, r => r.Offset);

            this.methods = parsed.Regions
                .SelectMany(r => r.Root.DescendantNodes().OfType<MethodDeclarationSyntax>())
                .ToList();

            this.declared = this.methods
                .Select(m => m.Identifier.ValueText)
                .ToHashSet(StringComparer.Ordinal);

            this.calls = this.methods
                .SelectMany(m => m.DescendantNodes().OfType<InvocationExpressionSyntax>().Select(c => (Site: c, Caller: m)))
                .ToList();

            this.Unsubscribed = this.Assignments(SyntaxKind.SubtractAssignmentExpression)
                .Select(a => Collapse(a.Left))
                .ToHashSet(StringComparer.Ordinal);
        }

        internal string File { get; }

        /// <summary>Every target the file writes a <c>-=</c> against.</summary>
        internal HashSet<string> Unsubscribed { get; }

        internal IEnumerable<AssignmentExpressionSyntax> Assignments(SyntaxKind kind) =>
            this.regions
                .SelectMany(r => r.Root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
                .Where(a => a.IsKind(kind));

        internal bool Declares(string name) => this.declared.Contains(name);

        internal int LineOf(SyntaxNode node) =>
            RazorSyntax.LineOf(this.raw, this.offsets[node.SyntaxTree] + node.SpanStart);

        /// <summary>
        /// Every call to <paramref name="name"/> that could be a call to the component's own
        /// method: a bare identifier or <c>this.Name</c>. Matched by name, with no semantic model
        /// behind it, which is over-inclusive and therefore the safe direction.
        /// </summary>
        internal IEnumerable<(InvocationExpressionSyntax Site, MethodDeclarationSyntax Caller)> CallsTo(string name) =>
            this.calls.Where(c => string.Equals(InvokedName(c.Site), name, StringComparison.Ordinal));

        /// <summary>True when a Dispose on this component sets <paramref name="flag"/> to true.</summary>
        internal bool DisposeRaises(string flag) =>
            this.methods
                .Where(m => DisposeMethods.Contains(m.Identifier.ValueText, StringComparer.Ordinal))
                .SelectMany(m => m.DescendantNodes().OfType<AssignmentExpressionSyntax>())
                .Any(a => a.IsKind(SyntaxKind.SimpleAssignmentExpression)
                    && string.Equals(LastName(a.Left), flag, StringComparison.Ordinal)
                    && a.Right.IsKind(SyntaxKind.TrueLiteralExpression));

        private static string? InvokedName(InvocationExpressionSyntax call) => call.Expression switch
        {
            IdentifierNameSyntax id => id.Identifier.ValueText,
            MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name: IdentifierNameSyntax n } =>
                n.Identifier.ValueText,
            _ => null,
        };
    }
}

/// <summary>
/// The recurrence guard for docs/CircuitLifetimeLeak-Plan.md S2. See
/// <see cref="EventSubscriptionScan"/> for the defect, the rule and the limitations.
/// </summary>
public class EventSubscriptionLifetimeTests
{
    /// <summary>
    /// The rule, over every component on disk. This is the whole point of the slice: it is written
    /// against the shape, so it fires on a page that does not exist yet.
    /// </summary>
    [Fact]
    public void NoComponentSubscribesToAnEventAfterAnAwaitWithoutADisposedGuard()
    {
        var offenders = EventSubscriptionScan.ScanAll()
            .Where(s => s.Problem.Length > 0)
            .Select(s => $"{s.File}:{s.Line} {s.Method} - {s.Target} += {s.Handler}: {s.Problem}")
            .ToList();

        Assert.True(offenders.Count == 0,
            "A component that subscribes after an await can have Dispose run during that await, "
            + "and then it adds a DEAD component to an event. Where the event is on a DI singleton "
            + "that pins the component, its circuit and its whole scoped container for the life of "
            + "the process, and disconnected-circuit eviction cannot reclaim it:\n"
            + string.Join("\n", offenders));
    }

    /// <summary>
    /// Anti-vacuity, with no page named and no count to maintain: a file that unsubscribes a
    /// target must also have been seen subscribing to it. A scanner that silently stops matching
    /// takes this test with it, so the rule above cannot quietly become an assertion about nothing.
    /// </summary>
    [Fact]
    public void EveryComponentThatUnsubscribesWasSeenSubscribing()
    {
        var missing = new List<string>();

        foreach (var file in EventSubscriptionScan.ScanComponents())
        {
            var subscribed = file.Subscriptions.Select(s => s.Target).ToHashSet(StringComparer.Ordinal);

            missing.AddRange(file.Unsubscribes
                .Where(u => !subscribed.Contains(u.Target))
                .Select(u => $"{u.File}:{u.Line} {u.Target} -="));
        }

        Assert.True(missing.Count == 0,
            "These components unsubscribe from something the subscription scanner never saw them "
            + "subscribe to. Either the subscription is written in a shape "
            + "EventSubscriptionScan does not recognise - in which case it is also exempt from the "
            + "lifetime rule, which is the hole this test exists to find - or the unsubscribe is "
            + "orphaned:\n" + string.Join("\n", missing));
    }

    /// <summary>
    /// Anti-vacuity for the scanner itself: the live components really do hand it subscriptions to
    /// judge. Every assertion above passes on an empty set, and an extraction bug that returned no
    /// C# at all would make the whole suite green while proving nothing.
    /// </summary>
    [Fact]
    public void TheLiveComponentsStillPresentSubscriptionsToJudge()
    {
        var found = EventSubscriptionScan.ScanAll();

        Assert.True(found.Count >= 5,
            "The scanner found "
            + $"{found.Count} subscriptions under Components/. The five it has always seen are the "
            + "two fixed pages, GlobalProgress, InFlightWorkGuard and UsageTracker; fewer than "
            + "that means the Razor extraction has stopped finding @code blocks and every rule in "
            + "this file is now asserting about nothing.");

        Assert.Contains(found, s => string.Equals(s.File, "GlobalProgress.razor", StringComparison.Ordinal));
    }

    /// <summary>The defect itself, against a fixture, so the scanner is known to bite.</summary>
    [Fact]
    public void AnUnguardedSubscriptionAfterAnAwaitIsReported()
    {
        var problem = OneProblem("""
            @code {
                protected override async Task OnInitializedAsync()
                {
                    var state = await AuthStateProvider.GetAuthenticationStateAsync();
                    BulkJobs.JobChanged += OnJobChanged;
                }

                private bool _disposed;

                private void OnJobChanged(string id) { }

                public void Dispose()
                {
                    _disposed = true;
                    BulkJobs.JobChanged -= OnJobChanged;
                }
            }
            """);

        Assert.Contains("no disposed-guard", problem, StringComparison.Ordinal);
    }

    /// <summary>The two shapes that are correct: guarded after the await, and no await at all.</summary>
    [Theory]
    [InlineData("""
        @code {
            protected override async Task OnInitializedAsync()
            {
                var state = await AuthStateProvider.GetAuthenticationStateAsync();

                if (_disposed)
                    return;

                BulkJobs.JobChanged += OnJobChanged;
            }

            private bool _disposed;

            private void OnJobChanged(string id) { }

            public void Dispose()
            {
                _disposed = true;
                BulkJobs.JobChanged -= OnJobChanged;
            }
        }
        """)]
    [InlineData("""
        @code {
            protected override void OnInitialized()
            {
                BulkJobs.JobChanged += OnJobChanged;
            }

            private void OnJobChanged(string id) { }

            public void Dispose()
            {
                BulkJobs.JobChanged -= OnJobChanged;
            }
        }
        """)]
    public void TheGuardedShapeAndTheSynchronousShapeBothPass(string fixtureSource)
    {
        var found = EventSubscriptionScan.ScanText("Fixture.razor", fixtureSource);

        Assert.Single(found);
        Assert.Equal("", found[0].Problem);
    }

    /// <summary>
    /// A guard that runs before the await closes nothing: Dispose can still interleave after it.
    /// The rule looks at the LAST await, and this fixture is the reason.
    /// </summary>
    [Fact]
    public void AGuardBeforeTheAwaitIsNotCredited()
    {
        var problem = OneProblem("""
            @code {
                protected override async Task OnInitializedAsync()
                {
                    if (_disposed)
                        return;

                    var state = await AuthStateProvider.GetAuthenticationStateAsync();
                    BulkJobs.JobChanged += OnJobChanged;
                }

                private bool _disposed;

                private void OnJobChanged(string id) { }

                public void Dispose()
                {
                    _disposed = true;
                    BulkJobs.JobChanged -= OnJobChanged;
                }
            }
            """);

        Assert.Contains("no disposed-guard", problem, StringComparison.Ordinal);
    }

    /// <summary>A flag nothing raises is a guard in shape only.</summary>
    [Fact]
    public void AGuardFlagDisposeNeverSetsIsNotCredited()
    {
        var problem = OneProblem("""
            @code {
                protected override async Task OnInitializedAsync()
                {
                    var state = await AuthStateProvider.GetAuthenticationStateAsync();

                    if (_disposed)
                        return;

                    BulkJobs.JobChanged += OnJobChanged;
                }

                private bool _disposed;

                private void OnJobChanged(string id) { }

                public void Dispose()
                {
                    BulkJobs.JobChanged -= OnJobChanged;
                }
            }
            """);

        Assert.Contains("never raised is not a guard", problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// Prose cannot satisfy the rule. Three guards in this repo have already matched the comment
    /// that explained a rule instead of the code obeying it, and the comment on both fixed pages
    /// spells the guard out verbatim, so this is the exact shape that would have let them pass.
    /// </summary>
    [Fact]
    public void AGuardWrittenOnlyInACommentDoesNotCount()
    {
        var problem = OneProblem("""
            @code {
                protected override async Task OnInitializedAsync()
                {
                    var state = await AuthStateProvider.GetAuthenticationStateAsync();

                    // Dispose can run during the await above, so this is guarded:
                    // if (_disposed)
                    //     return;
                    BulkJobs.JobChanged += OnJobChanged;
                }

                private bool _disposed;

                private void OnJobChanged(string id) { }

                public void Dispose()
                {
                    _disposed = true;
                    BulkJobs.JobChanged -= OnJobChanged;
                }
            }
            """);

        Assert.Contains("no disposed-guard", problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// A string accumulation is not a subscription. Both fixed pages sit in files that also do
    /// this, so a scanner that confused the two would report noise on real code.
    /// </summary>
    [Fact]
    public void AStringOrNumericAccumulationIsNotTreatedAsASubscription()
    {
        var found = EventSubscriptionScan.ScanText("Fixture.razor", """
            @code {
                private async Task<string> BuildAsync()
                {
                    var text = await LoadAsync();
                    text += $" ({count:N0} rows)";
                    text += Suffix();
                    total += step;
                    return text;
                }
            }
            """);

        Assert.Empty(found);
    }

    // ---------------------------------------------------------------------------------------
    // Review finding leak-1. Three bypasses, each EXECUTED against the predecessor scanner by
    // the reviewer rather than argued, and each one a thing a regex over source cannot answer:
    // (a) crosses a method boundary, (b) crosses a branch, (c) is a syntax form with no "=>" in
    // it. All three are now fixtures that must be REJECTED.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// leak-1(a). The await is in the caller and the <c>+=</c> is in a helper, so the helper has
    /// no local await and the predecessor declared it safe with an empty problem. The window is
    /// open before the helper is entered; the rule now follows the call.
    /// </summary>
    [Fact]
    public void ASubscriptionMovedIntoAHelperCalledAfterAnAwaitIsReported()
    {
        var problem = OneProblem("""
            @code {
                protected override async Task OnInitializedAsync()
                {
                    var state = await AuthStateProvider.GetAuthenticationStateAsync();
                    SubscribeToJobs();
                }

                private void SubscribeToJobs()
                {
                    BulkJobs.JobChanged += OnJobChanged;
                }

                private bool _disposed;

                private void OnJobChanged(string id) { }

                public void Dispose()
                {
                    _disposed = true;
                    BulkJobs.JobChanged -= OnJobChanged;
                }
            }
            """);

        Assert.Contains("is reached from OnInitializedAsync()", problem, StringComparison.Ordinal);
        Assert.Contains("no disposed-guard", problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other side of leak-1(a): the same helper shape, guarded AT THE CALL, is correct and
    /// must pass. A rule that demanded the guard inside the helper would report every page that
    /// factors its subscription out, which is the false-positive direction this must not take.
    /// </summary>
    [Theory]
    [InlineData("""
        @code {
            protected override async Task OnInitializedAsync()
            {
                var state = await AuthStateProvider.GetAuthenticationStateAsync();

                if (_disposed)
                    return;

                SubscribeToJobs();
            }

            private void SubscribeToJobs()
            {
                BulkJobs.JobChanged += OnJobChanged;
            }

            private bool _disposed;

            private void OnJobChanged(string id) { }

            public void Dispose()
            {
                _disposed = true;
                BulkJobs.JobChanged -= OnJobChanged;
            }
        }
        """)]
    [InlineData("""
        @code {
            protected override async Task OnInitializedAsync()
            {
                var state = await AuthStateProvider.GetAuthenticationStateAsync();
                SubscribeToJobs();
            }

            private void SubscribeToJobs()
            {
                if (_disposed)
                    return;

                BulkJobs.JobChanged += OnJobChanged;
            }

            private bool _disposed;

            private void OnJobChanged(string id) { }

            public void Dispose()
            {
                _disposed = true;
                BulkJobs.JobChanged -= OnJobChanged;
            }
        }
        """)]
    public void AHelperGuardedAtEitherEndPasses(string fixtureSource)
    {
        var found = EventSubscriptionScan.ScanText("Fixture.razor", fixtureSource);

        Assert.Single(found);
        Assert.Equal("", found[0].Problem);
    }

    /// <summary>
    /// leak-1(b), first shape. The guard is real, is after the await and is lexically between the
    /// await and the subscription - and is skipped whenever the branch it sits in is not taken.
    /// Offsets cannot tell the difference; a tree can.
    /// </summary>
    [Fact]
    public void AGuardNestedInsideAnUnrelatedBranchIsNotCredited()
    {
        var problem = OneProblem("""
            @code {
                protected override async Task OnInitializedAsync()
                {
                    var state = await AuthStateProvider.GetAuthenticationStateAsync();

                    if (showingOptionalPanel)
                    {
                        if (_disposed)
                            return;
                    }

                    BulkJobs.JobChanged += OnJobChanged;
                }

                private bool showingOptionalPanel;

                private bool _disposed;

                private void OnJobChanged(string id) { }

                public void Dispose()
                {
                    _disposed = true;
                    BulkJobs.JobChanged -= OnJobChanged;
                }
            }
            """);

        Assert.Contains("no disposed-guard", problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// leak-1(b), second shape, and the nastier one: the <c>finally</c> runs ON the guarded
    /// return, so the guard does not merely fail to dominate the subscription - taking the guard
    /// is what reaches it.
    /// </summary>
    [Fact]
    public void AGuardInATryWhoseFinallySubscribesIsNotCredited()
    {
        var problem = OneProblem("""
            @code {
                protected override async Task OnInitializedAsync()
                {
                    var state = await AuthStateProvider.GetAuthenticationStateAsync();

                    try
                    {
                        if (_disposed)
                            return;
                    }
                    finally
                    {
                        BulkJobs.JobChanged += OnJobChanged;
                    }
                }

                private bool _disposed;

                private void OnJobChanged(string id) { }

                public void Dispose()
                {
                    _disposed = true;
                    BulkJobs.JobChanged -= OnJobChanged;
                }
            }
            """);

        Assert.Contains("no disposed-guard", problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// leak-1(c). <c>delegate (...) { }</c> carries no <c>=&gt;</c>, so the predecessor reported
    /// ZERO subscriptions for this file - not unguarded, invisible. It is also strictly worse than
    /// the bug being guarded against, because an anonymous handler cannot be unsubscribed at all.
    /// </summary>
    [Fact]
    public void AnAnonymousDelegateSubscriptionIsSeenAndJudged()
    {
        var found = EventSubscriptionScan.ScanText("Fixture.razor", """
            @code {
                protected override async Task OnInitializedAsync()
                {
                    var state = await AuthStateProvider.GetAuthenticationStateAsync();
                    BulkJobs.JobChanged += delegate (string jobId) { OnJobChanged(jobId); };
                }

                private bool _disposed;

                private void OnJobChanged(string id) { }

                public void Dispose()
                {
                    _disposed = true;
                }
            }
            """);

        var subscription = Assert.Single(found);
        Assert.Equal("BulkJobs.JobChanged", subscription.Target);
        Assert.Contains("no disposed-guard", subscription.Problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// The guard is about dominance, not about rejecting every nested subscription: a guard and a
    /// subscription inside the SAME branch is correct, and so is a guard in the block that
    /// encloses the branch.
    /// </summary>
    [Theory]
    [InlineData("""
        @code {
            protected override async Task OnInitializedAsync()
            {
                var state = await AuthStateProvider.GetAuthenticationStateAsync();

                if (wantsLiveUpdates)
                {
                    if (_disposed)
                        return;

                    BulkJobs.JobChanged += OnJobChanged;
                }
            }

            private bool wantsLiveUpdates;

            private bool _disposed;

            private void OnJobChanged(string id) { }

            public void Dispose()
            {
                _disposed = true;
                BulkJobs.JobChanged -= OnJobChanged;
            }
        }
        """)]
    [InlineData("""
        @code {
            protected override async Task OnInitializedAsync()
            {
                var state = await AuthStateProvider.GetAuthenticationStateAsync();

                if (_disposed)
                    return;

                if (wantsLiveUpdates)
                {
                    BulkJobs.JobChanged += OnJobChanged;
                }
            }

            private bool wantsLiveUpdates;

            private bool _disposed;

            private void OnJobChanged(string id) { }

            public void Dispose()
            {
                _disposed = true;
                BulkJobs.JobChanged -= OnJobChanged;
            }
        }
        """)]
    public void AGuardThatReallyDominatesIsCreditedWhereverItSits(string fixtureSource)
    {
        var found = EventSubscriptionScan.ScanText("Fixture.razor", fixtureSource);

        Assert.Single(found);
        Assert.Equal("", found[0].Problem);
    }

    // ---------------------------------------------------------------------------------------
    // Review finding prog-10(a). The rule above proves DOMINANCE over a statement list, which is
    // a fact about what is WRITTEN WHERE. A local function and a lambda both break the link
    // between the two: their bodies do not run where they are written, so a guard that is
    // lexically earlier can execute later, or never. The scanner cannot see when the invocation
    // happens, so it refuses the shape instead of guessing - the fixtures below are the shapes
    // it refuses, including the two that happen to be CORRECT code. That cost is deliberate.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// prog-10(a) itself, first row. The subscription runs on the call at line 4 and the guard
    /// is written at line 5, so the guard runs AFTER it - and the predecessor credited the guard
    /// anyway, because the local function is DECLARED below it. The second row is the same shape
    /// guarded at the call and the third is guarded inside the local function; both are correct
    /// code and both are refused, because proving either one needs the ordering fact the scanner
    /// does not have.
    /// </summary>
    [Theory]
    [InlineData("""
        @code {
            protected override async Task OnInitializedAsync()
            {
                var state = await AuthStateProvider.GetAuthenticationStateAsync();
                SubscribeToJobs();

                if (_disposed)
                    return;

                void SubscribeToJobs()
                {
                    BulkJobs.JobChanged += OnJobChanged;
                }
            }

            private bool _disposed;

            private void OnJobChanged(string id) { }

            public void Dispose()
            {
                _disposed = true;
                BulkJobs.JobChanged -= OnJobChanged;
            }
        }
        """)]
    [InlineData("""
        @code {
            protected override async Task OnInitializedAsync()
            {
                var state = await AuthStateProvider.GetAuthenticationStateAsync();

                if (_disposed)
                    return;

                SubscribeToJobs();

                void SubscribeToJobs()
                {
                    BulkJobs.JobChanged += OnJobChanged;
                }
            }

            private bool _disposed;

            private void OnJobChanged(string id) { }

            public void Dispose()
            {
                _disposed = true;
                BulkJobs.JobChanged -= OnJobChanged;
            }
        }
        """)]
    [InlineData("""
        @code {
            protected override async Task OnInitializedAsync()
            {
                var state = await AuthStateProvider.GetAuthenticationStateAsync();
                SubscribeToJobs();

                void SubscribeToJobs()
                {
                    if (_disposed)
                        return;

                    BulkJobs.JobChanged += OnJobChanged;
                }
            }

            private bool _disposed;

            private void OnJobChanged(string id) { }

            public void Dispose()
            {
                _disposed = true;
                BulkJobs.JobChanged -= OnJobChanged;
            }
        }
        """)]
    public void ASubscriptionInsideALocalFunctionIsRefused(string fixtureSource)
    {
        var problem = OneProblem(fixtureSource);

        Assert.Contains("local function 'SubscribeToJobs'", problem, StringComparison.Ordinal);
        Assert.Contains("does not run where it is written", problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same hole in its other form, and the worse one: a lambda body can be invoked at any
    /// time, including after Dispose has run and returned. The guard here really does dominate
    /// the STATEMENT that creates the delegate, which is exactly why dominance is the wrong
    /// question to be answering about it.
    /// </summary>
    [Fact]
    public void ASubscriptionInsideALambdaIsRefused()
    {
        var problem = OneProblem("""
            @code {
                protected override async Task OnInitializedAsync()
                {
                    var state = await AuthStateProvider.GetAuthenticationStateAsync();

                    if (_disposed)
                        return;

                    await InvokeAsync(() => { BulkJobs.JobChanged += OnJobChanged; });
                }

                private bool _disposed;

                private void OnJobChanged(string id) { }

                public void Dispose()
                {
                    _disposed = true;
                    BulkJobs.JobChanged -= OnJobChanged;
                }
            }
            """);

        Assert.Contains("lambda or anonymous delegate body", problem, StringComparison.Ordinal);
        Assert.Contains("does not run where it is written", problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same hole one hop deeper, and the reason the refusal sits where the position
    /// reasoning STARTS rather than at the subscription: here the <c>+=</c> is in an ordinary
    /// method and it is the CALL to it that was written inside a local function. The caller
    /// window then proved dominance over the declaration's position, which is prog-10(a) again
    /// with an extra method boundary in front of it.
    /// </summary>
    [Fact]
    public void ACallToASubscribingHelperFromInsideALocalFunctionIsRefused()
    {
        var problem = OneProblem("""
            @code {
                protected override async Task OnInitializedAsync()
                {
                    var state = await AuthStateProvider.GetAuthenticationStateAsync();
                    Go();

                    if (_disposed)
                        return;

                    void Go()
                    {
                        SubscribeToJobs();
                    }
                }

                private void SubscribeToJobs()
                {
                    BulkJobs.JobChanged += OnJobChanged;
                }

                private bool _disposed;

                private void OnJobChanged(string id) { }

                public void Dispose()
                {
                    _disposed = true;
                    BulkJobs.JobChanged -= OnJobChanged;
                }
            }
            """);

        Assert.Contains("is reached from OnInitializedAsync()", problem, StringComparison.Ordinal);
        Assert.Contains("local function 'Go'", problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// The refusal is about the body the subscription SITS IN, not about the file containing a
    /// local function at all. A page may factor anything it likes into one; only a <c>+=</c>
    /// written inside it is refused, and the ordinary rule still judges the rest.
    /// </summary>
    [Fact]
    public void ALocalFunctionElsewhereInTheMethodDoesNotDisturbTheOrdinaryRule()
    {
        var found = EventSubscriptionScan.ScanText("Fixture.razor", """
            @code {
                protected override async Task OnInitializedAsync()
                {
                    var state = await AuthStateProvider.GetAuthenticationStateAsync();

                    if (_disposed)
                        return;

                    BulkJobs.JobChanged += OnJobChanged;
                    Render(Label());

                    string Label() => "jobs";
                }

                private bool _disposed;

                private void OnJobChanged(string id) { }

                public void Dispose()
                {
                    _disposed = true;
                    BulkJobs.JobChanged -= OnJobChanged;
                }
            }
            """);

        Assert.Single(found);
        Assert.Equal("", found[0].Problem);
    }

    private static string OneProblem(string fixtureSource)
    {
        var found = EventSubscriptionScan.ScanText("Fixture.razor", fixtureSource);
        return Assert.Single(found).Problem;
    }
}
