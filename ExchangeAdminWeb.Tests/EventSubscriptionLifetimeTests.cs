using System.Text.RegularExpressions;

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
/// THE RULE. For every delegate subscription in Components/, either no <c>await</c> precedes it in
/// the same method - there is then no window for Dispose to run in - or a disposed-guard sits
/// between the LAST preceding await and the subscription. A guard before the await is useless and
/// is not credited. The only sanctioned guard shape is the early return the four fixed components
/// already use:
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
/// Same family as <see cref="ProgressScan"/>, and it reuses that type's comment stripping for the
/// same reason: this file's own doc comment contains the words "await", "disposed" and the guard
/// itself, and three guards in this repo have already passed or failed on prose instead of code.
/// Everything is matched against the blanked source.
/// </para>
/// <para>
/// HONEST LIMITATIONS. It proves lexical order, not runtime reachability, exactly as
/// ProgressRegistry's scans do. Classification is deliberately over-inclusive: a <c>+=</c> whose
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

    // The lookbehind keeps the match from starting in the middle of a dotted path or an
    // identifier. The right-hand side runs to the first ';' or '{' so a handler written on the
    // next line is still captured, and a statement lambda is recognised by its "=>" before its
    // block begins.
    private static readonly Regex CompoundAssignment = new(
        @"(?<![\w.])(?<lhs>(?:this\s*\.\s*)?[A-Za-z_]\w*(?:\s*\.\s*[A-Za-z_]\w*)*)\s*\+=\s*(?<rhs>[^;{]*)",
        RegexOptions.Compiled);

    private static readonly Regex IdentifierChain = new(
        @"^(?:this\.)?[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*$", RegexOptions.Compiled);

    private static readonly Regex Await = new(
        @"(?<![\w])await(?![\w])", RegexOptions.Compiled);

    // The one sanctioned guard shape, with or without braces around the return.
    private static readonly Regex DisposedGuard = new(
        @"if\s*\(\s*(?:this\s*\.\s*)?(?<flag>[A-Za-z_]\w*)\s*\)\s*(?:\{\s*)?return\s*;",
        RegexOptions.Compiled);

    private static readonly string[] DisposeMethods = ["Dispose", "DisposeAsync"];

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

    internal static IReadOnlyList<Subscription> ScanAll() =>
        ComponentFiles().SelectMany(p => ScanText(Path.GetFileName(p), File.ReadAllText(p))).ToList();

    /// <summary>
    /// The scan over text rather than a file on disk, so the mechanics can be exercised against
    /// fixtures that depend on no component.
    /// </summary>
    internal static IReadOnlyList<Subscription> ScanText(string file, string raw)
    {
        var source = ProgressScan.StripComments(raw);
        var methods = ProgressScan.DeclaredMethods(source);
        var found = new List<Subscription>();

        foreach (Match match in CompoundAssignment.Matches(source))
        {
            var target = Collapse(match.Groups["lhs"].Value);
            var handler = Collapse(match.Groups["rhs"].Value);

            if (!IsDelegateSubscription(source, target, handler, methods))
                continue;

            var line = ProgressScan.LineOf(source, match.Index);
            var method = EnclosingMethod(methods, match.Index);

            if (method is null)
            {
                found.Add(new Subscription(file, "<no enclosing method>", line, target, handler,
                    "the scanner cannot tell which method this subscription is in, so it cannot "
                    + "tell whether an await precedes it. Extend EventSubscriptionScan rather than "
                    + "leaving the subscription unchecked"));
                continue;
            }

            found.Add(new Subscription(file, method.Name, line, target, handler,
                ProblemWith(source, methods, method, match.Index)));
        }

        return found;
    }

    /// <summary>
    /// True when the file ever writes <c>-=</c> against the same target. A real subscription is
    /// nearly always unsubscribed somewhere, so this catches handlers the name-shape rules below
    /// would miss, and it is the signal the two fixed pages and the two shared components all
    /// carry.
    /// </summary>
    internal static bool HasMatchingUnsubscribe(string source, string target)
    {
        var path = string.Join(@"\s*\.\s*", target.Split('.').Select(Regex.Escape));
        return Regex.IsMatch(source, @"(?<![\w.])" + path + @"\s*-=");
    }

    private static bool IsDelegateSubscription(
        string source,
        string target,
        string handler,
        IReadOnlyDictionary<string, ProgressScan.PageMethod> methods)
    {
        // A lambda can only be added to a delegate. Nothing else accepts one.
        if (handler.Contains("=>", StringComparison.Ordinal))
            return true;

        if (!IdentifierChain.IsMatch(handler))
            return HasMatchingUnsubscribe(source, target);

        var name = handler.Split('.')[^1];

        // A method group names a method, and C# names methods in PascalCase. "_OnChanged" is
        // allowed for the same reason. A camelCase right-hand side ("total += step") is an
        // accumulation unless the file proves otherwise by unsubscribing the same target.
        return char.IsUpper(name[0])
            || name[0] == '_'
            || methods.ContainsKey(name)
            || HasMatchingUnsubscribe(source, target);
    }

    /// <summary>
    /// What is wrong with the subscription at <paramref name="index"/>, or an empty string when
    /// nothing is.
    /// </summary>
    private static string ProblemWith(
        string source,
        IReadOnlyDictionary<string, ProgressScan.PageMethod> methods,
        ProgressScan.PageMethod method,
        int index)
    {
        var before = source[method.Start..index];
        var awaits = Await.Matches(before);

        // No await, no window: Dispose cannot interleave with a synchronous path.
        if (awaits.Count == 0)
            return "";

        // The LAST await, not the first. A guard that runs before an await is back in the same
        // race the guard exists to lose safely.
        var window = before[awaits[^1].Index..];

        var guard = DisposedGuard.Matches(window)
            .FirstOrDefault(g => IsDisposedFlag(g.Groups["flag"].Value));

        if (guard is null)
        {
            return "subscribes after an await with no disposed-guard between them. Dispose can run "
                + "during that await, unsubscribe nothing, and leave this handler on the event for "
                + "the life of the process. Add the guard Components/Shared/GlobalProgress.razor "
                + "uses, immediately before the subscription: if (_disposed) return;";
        }

        var flag = guard.Groups["flag"].Value;

        return SetsFlagInDispose(source, methods, flag)
            ? ""
            : $"guards on '{flag}', which no Dispose on this component sets to true. A flag that is "
                + "never raised is not a guard; set it as the first statement of Dispose";
    }

    private static bool IsDisposedFlag(string name) =>
        name.Contains("dispos", StringComparison.OrdinalIgnoreCase);

    private static bool SetsFlagInDispose(
        string source,
        IReadOnlyDictionary<string, ProgressScan.PageMethod> methods,
        string flag)
    {
        var assignment = new Regex(@"(?<![\w.])" + Regex.Escape(flag) + @"\s*=\s*true\s*;");

        return DisposeMethods
            .Where(methods.ContainsKey)
            .Select(name => methods[name])
            .Any(m => assignment.IsMatch(source[m.Start..m.End]));
    }

    /// <summary>The innermost declared method whose body contains <paramref name="index"/>.</summary>
    private static ProgressScan.PageMethod? EnclosingMethod(
        IReadOnlyDictionary<string, ProgressScan.PageMethod> methods,
        int index) =>
        methods.Values
            .Where(m => m.Start <= index && index < m.End)
            .OrderBy(m => m.End - m.Start)
            .FirstOrDefault();

    private static string Collapse(string text) => Regex.Replace(text, @"\s+", "");
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
    /// target must also have been seen subscribing to it. A regex that silently stops matching
    /// takes this test with it, so the rule above cannot quietly become an assertion about nothing.
    /// </summary>
    [Fact]
    public void EveryComponentThatUnsubscribesWasSeenSubscribing()
    {
        var unsubscribe = new Regex(
            @"(?<![\w.])(?<lhs>(?:this\s*\.\s*)?[A-Za-z_]\w*(?:\s*\.\s*[A-Za-z_]\w*)*)\s*-=\s*[A-Za-z_]");

        var found = EventSubscriptionScan.ScanAll()
            .Select(s => $"{s.File}|{s.Target}")
            .ToHashSet(StringComparer.Ordinal);

        var missing = new List<string>();

        foreach (var path in EventSubscriptionScan.ComponentFiles())
        {
            var file = Path.GetFileName(path);
            var source = ProgressScan.StripComments(File.ReadAllText(path));

            foreach (Match match in unsubscribe.Matches(source))
            {
                var target = Regex.Replace(match.Groups["lhs"].Value, @"\s+", "");
                if (!found.Contains($"{file}|{target}"))
                    missing.Add($"{file}:{ProgressScan.LineOf(source, match.Index)} {target} -=");
            }
        }

        Assert.True(missing.Count == 0,
            "These components unsubscribe from something the subscription scanner never saw them "
            + "subscribe to. Either the subscription is written in a shape "
            + "EventSubscriptionScan does not recognise - in which case it is also exempt from the "
            + "lifetime rule, which is the hole this test exists to find - or the unsubscribe is "
            + "orphaned:\n" + string.Join("\n", missing));
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

    private static string OneProblem(string fixtureSource)
    {
        var found = EventSubscriptionScan.ScanText("Fixture.razor", fixtureSource);
        return Assert.Single(found).Problem;
    }
}
