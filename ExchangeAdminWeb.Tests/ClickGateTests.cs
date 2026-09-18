using System.Text.RegularExpressions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// The shared click-gating tripwires. One suite over <see cref="ClickGateRegistry"/>, replacing
/// per-page copies of the seven bespoke tripwires the precedent needed for Migration alone.
/// </summary>
/// <remarks>
/// <para>
/// These enforce the owner ruling of 2026-09-17: a control is clickable only when its click will
/// definitively execute. A Blazor circuit stays interactive across every await, so a control left
/// enabled during an async operation can be clicked, accepted, and then silently discarded or
/// applied to state that has since been replaced underneath it.
/// </para>
/// <para>
/// <b>What this suite cannot do, stated once so no reader over-reads a green run.</b> There is no
/// bUnit harness in this repo; nothing here renders a page. Every assertion is a source-level
/// scan, so it proves an identifier is present, never that it is load-bearing: a
/// <c>disabled="@(IsBusy &amp;&amp; false)"</c> passes every test below. These are tripwires
/// against drift, not proofs of correctness, and the manual acceptance checklist in
/// docs/ClickGatingAudit-Plan.md is what actually establishes the operator sees the fix.
/// </para>
/// <para>
/// A second limit worth naming: with no ErrorBoundary anywhere in the app (verified during the
/// Revision 1 reconnaissance), an exception escaping a handler tears the circuit down rather than
/// leaving a live page with a stuck flag. <see cref="EveryRegisteredFlagIsLoweredInAFinally"/>
/// therefore does NOT close the risk the original plan wrote it for. It is still worth having -
/// it catches the non-throw paths - but the throw paths need a catch that converts the failure
/// into something the operator can see, and no source scan can tell a good catch from a bad one.
/// </para>
/// </remarks>
public class ClickGateTests
{
    public static IEnumerable<object[]> ConvertedPages() =>
        ClickGateRegistry.Pages.Select(entry => new object[] { entry.Page });

    private static PageGateEntry Entry(string page) =>
        ClickGateRegistry.Pages.Single(candidate => candidate.Page == page);

    // ---- the self-enforcing half -----------------------------------------------------------

    [Fact]
    public void EveryPageIsRegisteredOrDeclaredUnconverted()
    {
        // This is what makes the rule survive the people who come after it. A new module's page
        // cannot arrive ungated and unnoticed: it is absent from both lists and this fails,
        // naming it. Declaring a page unconverted is allowed, but it is an edit someone has to
        // make on purpose and a reviewer can see.
        var onDisk = Directory.GetFiles(ClickGateSource.PagesDirectory(), "*.razor")
            .Select(Path.GetFileName)
            .Where(name => name != null)
            .Select(name => name!)
            .ToHashSet(StringComparer.Ordinal);

        var accounted = ClickGateRegistry.Pages.Select(entry => entry.Page)
            .Concat(ClickGateRegistry.NotYetConverted.Keys)
            .ToHashSet(StringComparer.Ordinal);

        var unaccounted = onDisk.Except(accounted).OrderBy(name => name, StringComparer.Ordinal).ToList();
        Assert.True(unaccounted.Count == 0,
            "these pages are in neither ClickGateRegistry.Pages nor NotYetConverted; a page must "
            + "be placed in one or the other so it cannot be gated by accident or forgotten:\n  "
            + string.Join("\n  ", unaccounted));

        var phantom = accounted.Except(onDisk).OrderBy(name => name, StringComparer.Ordinal).ToList();
        Assert.True(phantom.Count == 0,
            "these registry entries name pages that no longer exist:\n  " + string.Join("\n  ", phantom));
    }

    [Fact]
    public void NoPageIsBothConvertedAndDeclaredUnconverted()
    {
        var both = ClickGateRegistry.Pages
            .Select(entry => entry.Page)
            .Where(ClickGateRegistry.NotYetConverted.ContainsKey)
            .ToList();

        Assert.True(both.Count == 0,
            "converted pages still listed as NotYetConverted: " + string.Join(", ", both));
    }

    // ---- the button sweep ------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(ConvertedPages))]
    public void EveryClickableButtonConsultsAPredicateOrIsRegisteredExempt(string page)
    {
        // Walks the markup rather than a list of known buttons, so a button added later cannot
        // quietly skip the gate. A per-control guard only knows about its own operation, which is
        // exactly the blind spot the ruling outlaws.
        var entry = Entry(page);
        var source = ClickGateSource.Load(page);
        var exemptLines = entry.ExemptControls.Select(exempt => exempt.Line).ToHashSet();

        var ungated = source.ClickableButtons()
            .Where(tag => !NamesAnyPredicate(entry, tag.Text))
            .Where(tag => !exemptLines.Contains(tag.Line))
            .Select(tag => $"{page}:{tag.Line} {ClickGateSource.HandlerOf(tag)}")
            .ToList();

        Assert.True(ungated.Count == 0,
            "these buttons consult no busy predicate and are not registered as exempt. Either gate "
            + "them or add an ExemptControl with the reason it is safe:\n  "
            + string.Join("\n  ", ungated));
    }

    [Theory]
    [MemberData(nameof(ConvertedPages))]
    public void EveryRegisteredExemptionStillPointsAtARealControl(string page)
    {
        // Exemptions are the part of a blanket rule that rots. This pins each one to the control it
        // was granted for, so a button that moves or disappears forces the entry to be revisited
        // rather than silently covering some other control that drifted onto that line.
        var entry = Entry(page);
        var source = ClickGateSource.Load(page);
        var buttons = source.ClickableButtons().ToDictionary(tag => tag.Line);

        foreach (var exempt in entry.ExemptControls)
        {
            Assert.True(buttons.TryGetValue(exempt.Line, out var tag),
                $"{page}: no clickable button at line {exempt.Line}, where an exemption is "
                + $"registered ({exempt.Reason})");

            Assert.True(tag!.Text.Contains(exempt.Snippet, StringComparison.Ordinal),
                $"{page}:{exempt.Line} no longer contains '{exempt.Snippet}'. The exemption may now "
                + $"be covering a different control. Registered reason: {exempt.Reason}");

            if (exempt.KeepsItsOwnGuard is { } guard)
            {
                Assert.True(tag.Text.Contains(guard, StringComparison.Ordinal),
                    $"{page}:{exempt.Line} lost its own narrower guard '{guard}'. It is exempt from "
                    + $"the page predicate only because it keeps that one.");
            }
        }
    }

    // ---- predicates and flags ---------------------------------------------------------------

    [Theory]
    [MemberData(nameof(ConvertedPages))]
    public void EveryPredicateNamesEveryFlagItRegisters(string page)
    {
        // The gate is only as wide as the predicate. A new long-running operation that adds its own
        // flag and forgets to name it here leaves every control on the page live for the whole of
        // it, while every button still looks correctly guarded.
        var entry = Entry(page);
        var source = ClickGateSource.Load(page);

        foreach (var predicate in entry.Predicates)
        {
            var body = source.MemberBody(predicate.Name);
            Assert.False(string.IsNullOrEmpty(body),
                $"{page}: predicate '{predicate.Name}' not found");

            foreach (var flag in predicate.Members)
            {
                Assert.True(Regex.IsMatch(body, ClickGateSource.WholeWord(flag)),
                    $"{page}: {predicate.Name} does not consult {flag}; the page stays clickable "
                    + "while it is set");
            }
        }
    }

    [Theory]
    [MemberData(nameof(ConvertedPages))]
    public void NoExcludedFieldAppearsInAnyPredicate(string page)
    {
        // The one mistake that would make a page unusable, and the reason the precedent's plan was
        // reviewed before any code was written. Staged-confirmation state means "waiting for the
        // operator", not "busy". The Confirm button is rendered ONLY while that state is set, so
        // folding it into the predicate disables Confirm at the only moment it is ever shown - and
        // no destructive action could be executed again - while every other guard still reads as
        // correct. Asserted as a negative per field, which is the only form that cannot be
        // satisfied by a broken page.
        var entry = Entry(page);
        var source = ClickGateSource.Load(page);

        foreach (var predicate in entry.Predicates)
        {
            var body = source.MemberBody(predicate.Name);

            foreach (var excluded in entry.ExcludedFields)
            {
                Assert.False(Regex.IsMatch(body, ClickGateSource.WholeWord(excluded.Field)),
                    $"{page}: {predicate.Name} names {excluded.Field}, which is staged state, not "
                    + $"in-flight state. It stages the control at {excluded.RendersAt}, which would "
                    + $"be disabled at the only moment it renders. Reason on record: {excluded.Reason}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(ConvertedPages))]
    public void EveryRegisteredFlagIsLoweredInAFinally(string page)
    {
        // A flag cleared only on the success path sticks the first time the call throws, and under
        // a page-wide gate that deadens far more than the one control it used to grey.
        //
        // Anchored per occurrence rather than per method, so a new setter cannot skip the finally.
        //
        // Honest limitation: brace balancing takes the first finally that lowers the flag, and
        // cannot attribute it to the right try when a method has several. It also cannot see a
        // lowering that is itself nested inside an if within the finally.
        var entry = Entry(page);
        var source = ClickGateSource.Load(page);

        foreach (var flag in AllFlags(entry))
        {
            var setters = source.NonClearingSetters(flag).ToList();
            Assert.True(setters.Count > 0,
                $"{page}: {flag} is registered as an in-flight flag but is never set");

            foreach (var setter in setters)
            {
                var method = source.EnclosingMethodName(setter.Index);
                var body = source.MethodBody(method);

                Assert.True(body.Contains("finally", StringComparison.Ordinal),
                    $"{page}:{source.LineAt(setter.Index)} {method} sets {flag} but has no finally "
                    + "to clear it in");

                var cleanup = ClickGateSource.ExtractBlock(body, "finally");
                Assert.True(
                    Regex.IsMatch(cleanup, ClickGateSource.WholeWord(flag) + @"\s*=\s*(null|false);"),
                    $"{page}:{source.LineAt(setter.Index)} {method} sets {flag} but does not clear "
                    + "it in its finally");
            }
        }
    }

    [Theory]
    [MemberData(nameof(ConvertedPages))]
    public void NoRealAwaitSitsBetweenARaiseAndItsProtectingTry(string page)
    {
        // The sharp form of the previous assertion, and the one that actually finds the defect
        // class. A raise outside the try it is supposed to be protected by means the awaits in
        // between have no finally behind them at all.
        //
        // `await Task.Yield()` is allowed because it is the house idiom for letting the spinner
        // paint before a long call, and it is the only await here whose continuation is lost solely
        // on circuit teardown. That is a judgement about the framework, not something this suite
        // can prove.
        var entry = Entry(page);
        var source = ClickGateSource.Load(page);

        foreach (var flag in AllFlags(entry))
        {
            foreach (var setter in source.NonClearingSetters(flag))
            {
                var method = source.EnclosingMethodName(setter.Index);
                var body = source.MethodBody(method);

                var raise = body.IndexOf(setter.Value, StringComparison.Ordinal);
                var guardedFrom = body.IndexOf("try", raise < 0 ? 0 : raise, StringComparison.Ordinal);
                if (raise < 0 || guardedFrom < 0)
                    continue;

                var between = body[raise..guardedFrom];
                var offending = Regex.Matches(between, @"await\s+(?<call>[^\r\n;]+)")
                    .Select(match => match.Groups["call"].Value.Trim())
                    .Where(call => !call.StartsWith("Task.Yield()", StringComparison.Ordinal))
                    .ToList();

                Assert.True(offending.Count == 0,
                    $"{page}:{source.LineAt(setter.Index)} {method} raises {flag} and then awaits "
                    + $"before entering its try, so these have no finally behind them: "
                    + string.Join(", ", offending));
            }
        }
    }

    [Theory]
    [MemberData(nameof(ConvertedPages))]
    public void SingleFlightHandlersRaiseAFlagBeforeTheirFirstAwait(string page)
    {
        // A flag set after the authorization round-trip is false for the whole of that round-trip.
        // The circuit stays interactive across it, so the gate never closed and the operator can
        // click the same button again, or any other.
        //
        // Asserted per handler rather than per setter: a handler may legitimately learn which row
        // to load only after an await, so what matters is that SOME flag is raised before the
        // first one. The stricter per-raise form needs a per-page allow-list (BlockedSenders
        // raises deliberately late so its spinner paints) and is deferred until a page needs it.
        var entry = Entry(page);
        var source = ClickGateSource.Load(page);
        var flags = AllFlags(entry).ToList();

        var handlers = flags
            .SelectMany(source.NonClearingSetters)
            .Select(setter => source.EnclosingMethodName(setter.Index))
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal);

        foreach (var handler in handlers)
        {
            var body = source.MethodBody(handler);
            var firstAwait = body.IndexOf("await ", StringComparison.Ordinal);
            if (firstAwait < 0)
                continue;

            var closedEarly = flags
                .SelectMany(flag => ClickGateSource.NonClearingSetters(body, flag))
                .Any(setter => setter.Index < firstAwait);

            Assert.True(closedEarly,
                $"{page}: {handler} raises no flag before its first await; the gate is open for the "
                + "whole of that await and the click can be repeated");
        }
    }

    // ---- controls the disabled attribute cannot reach ---------------------------------------

    [Theory]
    [MemberData(nameof(ConvertedPages))]
    public void EveryNonButtonClickTargetIsRegistered(string page)
    {
        // An <a> or <div> ignores the disabled attribute entirely, so the button sweep cannot see
        // these and their refusal has to be arranged deliberately.
        //
        // Honest limitation, and it is a large one: this only finds @onclick. Every @onchange
        // checkbox and radio, every InputFile, every @onkeydown Enter path, every @bind select and
        // every child-component Disabled= parameter is invisible to it. Those must be enumerated
        // into the registry by hand and nothing here detects one that was missed.
        var entry = Entry(page);
        var source = ClickGateSource.Load(page);
        var registered = entry.NonButtonTargets.Select(target => target.Line).ToHashSet();

        var unregistered = source.NonButtonClickTargets()
            .Where(tag => !registered.Contains(tag.Line))
            .Select(tag => $"{page}:{tag.Line} {ClickGateSource.HandlerOf(tag)}")
            .ToList();

        Assert.True(unregistered.Count == 0,
            "these non-button click targets are not in the registry; disabled= does not apply to "
            + "them, so each needs a declared refusal mechanism:\n  " + string.Join("\n  ", unregistered));
    }

    [Theory]
    [MemberData(nameof(ConvertedPages))]
    public void EveryNonButtonTargetIsRefusedByItsDeclaredMechanism(string page)
    {
        var entry = Entry(page);
        var source = ClickGateSource.Load(page);
        var tags = source.Tags("a").Concat(source.NonButtonClickTargets())
            .GroupBy(tag => tag.Line)
            .ToDictionary(group => group.Key, group => group.First());

        foreach (var target in entry.NonButtonTargets)
        {
            Assert.True(tags.TryGetValue(target.Line, out var tag),
                $"{page}: no non-button click target at line {target.Line}");
            Assert.True(tag!.Text.Contains(target.Snippet, StringComparison.Ordinal),
                $"{page}:{target.Line} no longer contains '{target.Snippet}'");

            switch (target.Mechanism)
            {
                case RefusalMechanism.DisabledAttribute:
                    Assert.True(NamesAnyPredicate(entry, tag.Text) &&
                                tag.Text.Contains("disabled", StringComparison.OrdinalIgnoreCase),
                        $"{page}:{target.Line} declares DisabledAttribute but carries no disabled= "
                        + "naming a predicate");
                    break;

                case RefusalMechanism.ChildComponentParameter:
                    Assert.True(NamesAnyPredicate(entry, tag.Text) &&
                                tag.Text.Contains("Disabled=", StringComparison.Ordinal),
                        $"{page}:{target.Line} declares ChildComponentParameter but passes no "
                        + "Disabled= naming a predicate");
                    break;

                case RefusalMechanism.HandlerGuard:
                    // The greying on these is styling only and must never be mistaken for the
                    // guard; the refusal has to be in the method the click reaches.
                    var method = ClickGateSource.CalledMethod(target.Handler);
                    var body = source.MethodBody(method);
                    Assert.True(
                        entry.Predicates.Any(predicate =>
                            body.Contains($"if ({predicate.Name}) return;", StringComparison.Ordinal)),
                        $"{page}:{target.Line} handler {method} does not refuse while the page is "
                        + "busy, and the element it hangs off cannot be disabled in markup");
                    break;

                case RefusalMechanism.SharedComponentContract:
                case RefusalMechanism.SnapshotNotGate:
                    Assert.False(string.IsNullOrWhiteSpace(target.WhyNotHandlerGuard),
                        $"{page}:{target.Line} declares {target.Mechanism} but records no reason");
                    break;
            }
        }
    }

    [Theory]
    [MemberData(nameof(ConvertedPages))]
    public void NoHandlerGuardOnADomSyncedControl(string page)
    {
        // A design tripwire against the remedy the original plan prescribed for every non-button
        // target. On a control that renders server state into the DOM, a refusing handler leaves
        // the backing field unchanged, so the render diff emits no correction and the browser keeps
        // the operator's action while the server never took it. That writes a value the operator
        // visibly changed away from - on one page, permanently and to Graph.
        var entry = Entry(page);

        foreach (var target in entry.NonButtonTargets.Where(IsDomSynced))
        {
            Assert.True(target.Mechanism != RefusalMechanism.HandlerGuard,
                $"{page}:{target.Line} is DOM-synced ({target.Snippet}) and declares a handler "
                + "guard. Refusing in the handler desyncs the browser from the server: use the "
                + "disabled attribute or a child Disabled= parameter instead.");

            Assert.False(string.IsNullOrWhiteSpace(target.WhyNotHandlerGuard),
                $"{page}:{target.Line} is DOM-synced but records no WhyNotHandlerGuard");
        }

        static bool IsDomSynced(NonButtonTarget target) =>
            target.Snippet.Contains("@bind", StringComparison.Ordinal)
            || target.Snippet.Contains("checked", StringComparison.OrdinalIgnoreCase)
            || target.Tag.Equals("InputFile", StringComparison.OrdinalIgnoreCase);
    }

    // ---- where the guard may not live --------------------------------------------------------

    [Theory]
    [MemberData(nameof(ConvertedPages))]
    public void ForbiddenGuardSitesCarryNoGuard(string page)
    {
        // The single most likely implementation error in this whole sweep: six of the eleven
        // reconnoitred pages call a refresh or shared helper from a handler that is already busy.
        // A guard in that callee makes the post-write refresh a silent no-op and the operator
        // concludes the write failed.
        //
        // Honest limitation: this proves the absence of the obvious guard shape, not the absence of
        // refusal. A callee that refuses via a helper or a flag read mid-body passes.
        var entry = Entry(page);
        var source = ClickGateSource.Load(page);

        foreach (var site in entry.ForbiddenGuardSites)
        {
            var body = source.MethodBody(site.Method);
            Assert.False(string.IsNullOrEmpty(body),
                $"{page}: ForbiddenGuardSite names {site.Method}, which does not exist");

            foreach (var predicate in entry.Predicates)
            {
                Assert.False(body.Contains($"if ({predicate.Name}) return;", StringComparison.Ordinal),
                    $"{page}: {site.Method} must NOT refuse while busy - it is called from "
                    + $"{string.Join(", ", site.CalledWhileBusyFrom)} while the page is already "
                    + $"busy. {site.Consequence}");
            }
        }
    }

    // ---- things the gate must not break ------------------------------------------------------

    [Theory]
    [MemberData(nameof(ConvertedPages))]
    public void AnnotatedControlsKeepTheirNonBusyClauses(string page)
    {
        // Prevents the worst single outcome available in this sweep. A disabled expression can
        // carry a clause that is not about busyness at all but is the only enforcement of
        // something - a typed confirmation, a ticket number, form validity. Rewriting the
        // expression to the predicate deletes it, and on the most destructive action in this app
        // there is no server-side backstop to catch that.
        var entry = Entry(page);
        var source = ClickGateSource.Load(page);
        var tags = source.ClickableButtons().ToDictionary(tag => tag.Line);

        foreach (var control in entry.AnnotatedControls)
        {
            Assert.True(tags.TryGetValue(control.Line, out var tag),
                $"{page}: no clickable button at annotated line {control.Line}");
            Assert.True(tag!.Text.Contains(control.Snippet, StringComparison.Ordinal),
                $"{page}:{control.Line} no longer contains '{control.Snippet}'");

            foreach (var clause in control.PreserveClauses)
            {
                Assert.True(tag.Text.Contains(clause, StringComparison.Ordinal),
                    $"{page}:{control.Line} lost the clause '{clause}'. It is not a busy condition "
                    + "and the predicate does not replace it; it must be OR-ed alongside.");
            }
        }
    }

    [Theory]
    [MemberData(nameof(ConvertedPages))]
    public void SpinnerExpressionsAreNotSimplifiedIntoThePredicate(string page)
    {
        // Spinner conditions read the flags in non-busy combinations. Collapsing them to the
        // predicate during a mechanical pass shows two spinners at once, or none.
        var entry = Entry(page);
        var source = ClickGateSource.Load(page);

        foreach (var expression in entry.SpinnerExpressions)
        {
            Assert.True(source.Text.Contains(expression, StringComparison.Ordinal),
                $"{page}: the spinner condition '{expression}' is gone. If it was folded into the "
                + "predicate, the page now shows the wrong number of spinners.");
        }
    }

    [Theory]
    [MemberData(nameof(ConvertedPages))]
    public void TheRegisteredLineCountStillMatchesTheFile(string page)
    {
        // Every other assertion keys on line numbers, and registered coordinates drift faster than
        // anyone expects. This fails loudly on any size change rather than letting an exemption
        // silently re-point at whatever moved onto its line.
        var entry = Entry(page);
        var actual = File.ReadAllLines(Path.Combine(ClickGateSource.PagesDirectory(), page)).Length;

        Assert.True(actual == entry.ExpectedLineCount,
            $"{page} is now {actual} lines, registered as {entry.ExpectedLineCount}. Re-check every "
            + "registered line number for this page, then update ExpectedLineCount.");
    }

    // ---- helpers -----------------------------------------------------------------------------

    private static bool NamesAnyPredicate(PageGateEntry entry, string text) =>
        entry.Predicates.Any(predicate =>
            Regex.IsMatch(text, ClickGateSource.WholeWord(predicate.Name)));

    private static IEnumerable<string> AllFlags(PageGateEntry entry) =>
        entry.Predicates.SelectMany(predicate => predicate.Members).Distinct(StringComparer.Ordinal);
}
