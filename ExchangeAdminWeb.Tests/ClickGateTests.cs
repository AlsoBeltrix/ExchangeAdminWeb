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

    // ---- what a gate cannot close ------------------------------------------------------------

    [Theory]
    [MemberData(nameof(ConvertedPages))]
    public void NoHandlerReadsARegisteredFieldLiveAfterItsFirstAwait(string page)
    {
        // docs/ClickGatingAudit-Plan.md Revision 1 falsification 6, made executable. On six of the
        // eleven reconnoitred pages the sharpest defect is not a gating defect at all: a handler
        // reads a page field back AFTER an await, and another control has changed it in the
        // meantime. Gating narrows that window - the browser's copy of a disabled attribute is one
        // round trip stale - and only a local closes it.
        //
        // This is the assertion the page-1 conversion was missing. A size-preserving revert of
        // DhcpAuthorization's operationResult snapshot passed all fourteen assertions above it and
        // was caught only incidentally, by the line-count fingerprint noticing the file had changed
        // size at all.
        //
        // What is the cheapest broken implementation that still passes this? Worked through rather
        // than assumed:
        //   - "change nothing" fails: the live read after the await is exactly what trips it.
        //   - "delete the feature" fails: the snapshot local must still be READ after the await, so
        //     a handler that quietly stops using the value fails too.
        //   - "never call the thing under test" fails: a handler that is gone, or that no longer
        //     awaits, fails rather than being skipped.
        //   - "rename the field" fails: the capture (CapturedAtEntry) or the publish
        //     (PublishedFromLocal) must still name the field by its registered name.
        // The one that does pass, stated honestly rather than papered over: this is still text
        // containment, so it cannot prove the local is the value actually handed to the service
        // call. It proves the live field is not read after the await and that the local is read
        // after it - not that the two are wired together.
        //
        // Not in this assertion's dependency path, deliberately: ClickGateSource.ExtractBlock,
        // which counts braces without regard for quoting and so mis-ends a block containing an
        // interpolated string. Every handler here is full of "$"{dns} ({ip})"". This works from
        // MethodBody, which is delimited by the next member signature, and from index arithmetic.
        var entry = Entry(page);
        var source = ClickGateSource.Load(page);

        foreach (var obligation in entry.PostAwaitLiveReads)
            AssertSnapshotObligationHolds(page, source, obligation);
    }

    [Theory]
    [MemberData(nameof(ConvertedPages))]
    public void EveryExemptionPrerequisiteIsEnforcedAndNotJustDescribed(string page)
    {
        // An exemption granted on a precondition is only as good as the precondition. The forcing
        // case is DhcpAuthorization line 40: the banner dismiss is safe to leave clickable during
        // an operation ONLY because both write handlers snapshot the result they publish. Restore
        // the field reads and that exemption is a live NullReferenceException path again - and the
        // registry would still read as though it had been reasoned about.
        //
        // The tie to machinery is the field name, read out of the exemption's own registered
        // snippet, so the two cannot be pointed at different things. It is asserted in both
        // directions: a prerequisite demands a matching obligation, and an obligation on a field an
        // exempt control writes demands the prerequisite be written down.
        //
        // What is the cheapest broken implementation that still passes this? Deleting BOTH the
        // prerequisite note and the PostAwaitLiveReads entries for that field - a deliberate,
        // reviewable registry edit that removes the record of why the exemption was granted, which
        // is the most any registry-driven assertion in this suite can do. Deleting either one alone
        // fails. Deleting neither and reverting the page fails, because this re-runs the obligation
        // rather than restating it.
        var entry = Entry(page);
        var source = ClickGateSource.Load(page);

        foreach (var exempt in entry.ExemptControls)
        {
            var field = FieldWrittenBy(exempt.Snippet);
            var covering = entry.PostAwaitLiveReads
                .Where(obligation =>
                    string.Equals(obligation.LiveField, field, StringComparison.Ordinal))
                .ToList();

            if (exempt.PrerequisiteBeforeExemptionHolds is { } prerequisite)
            {
                Assert.False(string.IsNullOrEmpty(field),
                    $"{page}:{exempt.Line} records a prerequisite but its snippet '{exempt.Snippet}' "
                    + "assigns no page field, so there is nothing to tie the prerequisite to. Either "
                    + "state it as a PostAwaitLiveRead on the field the control mutates, or move the "
                    + "note to ConditionThatKeepsItTrue, where it is honestly prose. Recorded "
                    + $"prerequisite: {prerequisite}");

                Assert.True(covering.Count > 0,
                    $"{page}:{exempt.Line} is exempt only because of a prerequisite elsewhere in the "
                    + $"page, but no PostAwaitLiveReads entry names {field}, so nothing checks that "
                    + $"the prerequisite still holds. Recorded prerequisite: {prerequisite}");

                foreach (var obligation in covering)
                    AssertSnapshotObligationHolds(page, source, obligation);
            }
            else
            {
                Assert.True(covering.Count == 0,
                    $"{page}:{exempt.Line} stays clickable while the page is busy and assigns "
                    + $"{field}, which {covering.Count} registered handler obligation(s) depend on "
                    + "not changing mid-flight - yet the exemption records no "
                    + "PrerequisiteBeforeExemptionHolds. Write down what has to stay true, or the "
                    + "next reader grants this exemption on the gating reason alone and never learns "
                    + "the snapshot is load-bearing.");
            }
        }
    }

    [Theory]
    [MemberData(nameof(ConvertedPages))]
    public void EveryRaiseThatMustFollowAnEarlyReturnStillDoes(string page)
    {
        // EveryRegisteredFlagIsLoweredInAFinally proves a finally exists; it cannot see that an
        // early return above the try skips that finally entirely. DhcpAuthorization.DownloadCsvAsync
        // is the forcing case: isDownloadingCsv is a member of a page-wide predicate, so a raise
        // moved above the empty-list guard does not grey one button, it deadens every control on the
        // page, permanently, the first time the operator exports an empty list.
        //
        // What is the cheapest broken implementation that still passes this? None of the usual four.
        // "Change nothing" after moving the raise fails on ordering; "delete the feature" fails
        // because the raise must exist; "hard-code one value" - rewriting the condition - fails
        // because the guard is matched verbatim; "never call the thing under test" fails because a
        // missing handler fails rather than skips. Removing the return but keeping the if also
        // fails.
        //
        // Honest limitation: this is textual ordering within one method body, not control flow. It
        // cannot see a raise reached by a call, and it cannot prove the early return is reachable.
        // It also does not use ClickGateSource.ExtractBlock, whose brace counting is not quote-aware
        // and would mis-end any block in these interpolated-string-heavy handlers.
        var entry = Entry(page);
        var source = ClickGateSource.Load(page);

        foreach (var rule in entry.RaiseMustFollowEarlyReturn)
        {
            var body = source.MethodBody(rule.Handler);
            Assert.False(string.IsNullOrEmpty(body),
                $"{page}: RaiseMustFollowEarlyReturn names {rule.Handler}, which does not exist");

            var offset = source.Text.IndexOf(body, StringComparison.Ordinal);

            var guard = body.IndexOf($"if ({rule.EarlyReturn})", StringComparison.Ordinal);
            Assert.True(guard >= 0,
                $"{page}: {rule.Handler} no longer carries the guard 'if ({rule.EarlyReturn})', "
                + $"which {rule.Flag} is raised below on purpose. {rule.Consequence}");

            var exit = Regex.Match(body[guard..], @"^if \([^\r\n]*\)\s*(?:\{\s*)?return\s*;");
            Assert.True(exit.Success,
                $"{page}:{source.LineAt(offset + guard)} 'if ({rule.EarlyReturn})' no longer returns "
                + $"immediately, so there is no early exit left for the raise of {rule.Flag} to sit "
                + "below. Re-check the ordering by hand before updating this entry.");

            var raises = ClickGateSource.NonClearingSetters(body, rule.Flag).ToList();
            Assert.True(raises.Count > 0,
                $"{page}: {rule.Handler} no longer raises {rule.Flag} at all, so nothing marks the "
                + "page busy for the whole of that operation.");

            var tooEarly = raises
                .Where(raise => raise.Index < guard + exit.Length)
                .Select(raise => source.LineAt(offset + raise.Index))
                .ToList();

            Assert.True(tooEarly.Count == 0,
                $"{page}: {rule.Handler} raises {rule.Flag} at line(s) {string.Join(", ", tooEarly)}, "
                + $"at or above the early return 'if ({rule.EarlyReturn})'. Nothing lowers it on that "
                + $"path. {rule.Consequence}");
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

    /// <summary>
    /// A regex matching a READ of <paramref name="identifier"/>: a whole-word occurrence that is not
    /// the left-hand side of an assignment. <c>x.Foo</c>, <c>f(x)</c> and <c>x == y</c> are reads;
    /// <c>x = y</c> is not. The distinction is the whole point here - a handler clearing a form
    /// field after a successful write is correct, while reading one back mid-flight is the defect.
    /// </summary>
    private static string ReadOf(string identifier) =>
        ClickGateSource.WholeWord(identifier) + @"(?!\s*=(?!=))";

    /// <summary>
    /// The page field an inline handler snippet assigns to: "operationResult" for
    /// <c>@onclick="() =&gt; operationResult = null"</c>. Empty when the snippet names a method
    /// instead, which most do.
    /// </summary>
    private static string FieldWrittenBy(string snippet) =>
        Regex.Match(snippet, @"=>\s*(?<field>[A-Za-z_][A-Za-z0-9_]*)\s*=(?!=)").Groups["field"].Value;

    /// <summary>
    /// The shared machinery behind <see cref="NoHandlerReadsARegisteredFieldLiveAfterItsFirstAwait"/>
    /// and <see cref="EveryExemptionPrerequisiteIsEnforcedAndNotJustDescribed"/>. One implementation
    /// so an exemption's prerequisite is checked by the same code as the obligation it names, and
    /// the two cannot drift into meaning different things.
    /// </summary>
    private static void AssertSnapshotObligationHolds(
        string page, ClickGateSource source, PostAwaitLiveRead obligation)
    {
        var body = source.MethodBody(obligation.Handler);
        Assert.False(string.IsNullOrEmpty(body),
            $"{page}: PostAwaitLiveReads names handler {obligation.Handler}, which does not exist. "
            + $"The hazard it records has not gone away: {obligation.Why}");

        var offset = source.Text.IndexOf(body, StringComparison.Ordinal);

        // Deliberately a failure, not a skip. A handler that no longer awaits is either a different
        // handler or a rewritten one, and either way the entry needs a human.
        var firstAwait = body.IndexOf("await ", StringComparison.Ordinal);
        Assert.True(firstAwait >= 0,
            $"{page}: {obligation.Handler} is registered as needing a snapshot of "
            + $"{obligation.LiveField} but no longer awaits anything. Re-check the handler and this "
            + "entry together rather than deleting either.");

        var afterAwait = body[firstAwait..];

        var liveReads = Regex.Matches(afterAwait, ReadOf(obligation.LiveField))
            .Select(match => source.LineAt(offset + firstAwait + match.Index))
            .ToList();
        Assert.True(liveReads.Count == 0,
            $"{page}: {obligation.Handler} reads {obligation.LiveField} after its first await, at "
            + $"line(s) {string.Join(", ", liveReads)}. Another control can change that field while "
            + $"this handler is suspended, and the gate cannot stop it: read {obligation.Snapshot} "
            + $"instead. {obligation.Why}");

        // Without this, "stop using the value at all" satisfies the negative above.
        Assert.True(Regex.IsMatch(afterAwait, ReadOf(obligation.Snapshot)),
            $"{page}: {obligation.Handler} never reads {obligation.Snapshot} after its first await, "
            + $"so either the snapshot of {obligation.LiveField} is decoration or the handler has "
            + "stopped using the value altogether.");

        switch (obligation.Shape)
        {
            case SnapshotShape.CapturedAtEntry:
                var capture = Regex.Match(body,
                    @"\bvar\s+" + Regex.Escape(obligation.Snapshot) + @"\s*=[^\r\n;]*"
                    + ClickGateSource.WholeWord(obligation.LiveField) + @"[^\r\n;]*;");
                Assert.True(capture.Success,
                    $"{page}: {obligation.Handler} has no 'var {obligation.Snapshot} = ...' capturing "
                    + $"{obligation.LiveField}, so the local it reads is no longer that field's "
                    + $"entry-time value. {obligation.Why}");
                Assert.True(capture.Index < firstAwait,
                    $"{page}:{source.LineAt(offset + capture.Index)} {obligation.Handler} captures "
                    + $"{obligation.Snapshot} BELOW its first await. A capture taken after the "
                    + "handler has already yielded is not a snapshot of what the operator clicked.");
                break;

            case SnapshotShape.PublishedFromLocal:
                Assert.True(
                    Regex.IsMatch(body,
                        @"\bvar\s+" + Regex.Escape(obligation.Snapshot) + @"\s*=\s*await\b"),
                    $"{page}: {obligation.Handler} no longer holds its awaited result in "
                    + $"'var {obligation.Snapshot} = await ...'. {obligation.Why}");
                Assert.True(
                    Regex.IsMatch(afterAwait,
                        ClickGateSource.WholeWord(obligation.LiveField) + @"\s*=\s*"
                        + ClickGateSource.WholeWord(obligation.Snapshot) + ";"),
                    $"{page}: {obligation.Handler} does not publish {obligation.Snapshot} to "
                    + $"{obligation.LiveField}, so the banner never reports the operation the "
                    + "operator just ran.");
                break;
        }
    }
}
