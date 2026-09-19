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
/// <b>The keyboard section is preventive, and should not be read as a bug hunt that found one.</b>
/// It was added 2026-09-18, after commit 2eb8c15 had already fixed two live Enter-key holes on
/// Migration by hand. The census behind it was then run against all three converted pages:
/// Migration's two @onkeydown paths are the only keyboard handlers on any converted page and both
/// are gated, the other two pages have none, and neither instantiates a component carrying one. So
/// every keyboard assertion below passes today by being true of correct code, not by finding
/// anything. The value is in the seven tier-1 pages still to come, four of which - IntuneDevices,
/// GroupManagement, M365GroupManagement and SelfServiceGroups - already carry an @onkeydown search
/// box. A future reader should expect this section to have caught nothing yet.
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
        // Honest limitation: this only finds @onclick. Every @onchange checkbox and radio, every
        // InputFile, every @onkeydown Enter path, every @bind select and every child-component
        // Disabled= parameter is invisible to it.
        //
        // The second half of that sentence used to read "and nothing here detects one that was
        // missed". That is no longer true and was measured false rather than argued away: the
        // NamedLocations conversion stripped Disabled="@IsBusy" from CountryCodePicker and the suite
        // stayed at 0 failed / 63 passed. EveryDomSyncedControlIsRegisteredOrRecordedUngated below
        // now enumerates the inputs, selects, textareas and Disabled-bearing child components that
        // this assertion cannot see, and EveryKeyboardPathIsRegisteredOrRecordedHarmless enumerates
        // the @onkeydown / @onkeyup / @onkeypress handlers neither of those two can see - the last
        // demonstrated blind spot, and the one commit 2eb8c15 measured rather than argued.
        //
        // What is still uncovered by all three, stated so nobody over-reads a green run: @onchange
        // and @onsubmit have no enumerator of their own, and neither does a keyboard handler that
        // lives inside a child component rather than on this page. The DOM-synced sweep reaches an
        // @onchange control only if it is an input, select or textarea, which is most of them but
        // not a rule.
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

    // ---- controls the disabled attribute is the ONLY thing that can reach -------------------

    [Theory]
    [MemberData(nameof(ConvertedPages))]
    public void EveryDomSyncedControlStillCarriesItsRegisteredGate(string page)
    {
        // The headline assertion of this section, and the one a measurement asked for. On the
        // NamedLocations conversion, mutation M6 deleted Disabled="@IsBusy" from the
        // CountryCodePicker at 172 and the suite stayed at 0 failed / 63 passed. That control
        // latches _initialized on its first parameter push (Components/Shared/CountryCodePicker.razor
        // 45, 49-56) and Apply is its sole ValueChanged path (79-85), so a click the server does not
        // take desyncs it permanently and the NEXT save writes the old country set to Graph. For a
        // control that renders server state into the DOM the disabled attribute is not the
        // convenient refusal, it is the only safe one: a handler guard leaves the backing field
        // unchanged, the render diff emits no correction, and the browser keeps an action the server
        // never took. See RefusalMechanism and docs/ClickGatingAudit-Plan.md Revision 1
        // falsification 2.
        //
        // What is the cheapest broken implementation that still passes this? Worked through:
        //   - "delete the feature" fails: that IS M6, and it is what this exists to catch.
        //   - "hard-code one value" fails twice over. DisabledExpression = "" is contained in every
        //     string, so the verbatim check alone would be decoration; the two registry self-checks
        //     below reject it, and reject a bare "IsBusy" that could match incidentally elsewhere in
        //     the tag, and reject an expression naming nothing this page calls a busy signal.
        //   - "never call the thing under test" fails: a control that has moved, been renamed, or
        //     changed element type fails rather than being skipped, because the tag is located from
        //     the live enumeration and its name is compared.
        //   - "change nothing" passes, and that is correct - this is a drift tripwire, not a proof.
        // The one that does pass, said plainly rather than overclaimed: this is text containment. It
        // proves the attribute is present and unchanged, never that it is load-bearing. A page whose
        // registered gate is Disabled="@(IsBusy && false)" passes, as the suite's own header warns.
        //
        // Why "names a busy signal" and not "names the page predicate": Migration's form controls
        // are gated on isLoading or isCreating, not IsBusy, because that page was converted before
        // the page-wide ruling. Both are members of its predicate. Requiring the predicate here
        // would fail a converted page, and that is a decision about Migration.razor rather than
        // something a test should force. Recorded on the Migration registry entry.
        var entry = Entry(page);
        var source = ClickGateSource.Load(page);
        var found = DomSyncedTags(source);

        foreach (var control in entry.DomSyncedControls)
        {
            Assert.True(
                control.DisabledExpression.StartsWith("disabled=", StringComparison.Ordinal)
                || control.DisabledExpression.StartsWith("Disabled=", StringComparison.Ordinal),
                $"{page}:{control.Line} registers '{control.DisabledExpression}' as its gate, which "
                + "is not a whole disabled=/Disabled= attribute. Register the attribute exactly as "
                + "the markup writes it: a bare identifier can be matched incidentally elsewhere in "
                + "the same tag, so containment of one proves nothing.");

            Assert.True(NamesAnyBusySignal(entry, control.DisabledExpression),
                $"{page}:{control.Line} registers the gate '{control.DisabledExpression}', which "
                + "names no predicate and no flag this page registers. A DOM-synced control greyed "
                + "by something that is not a busy signal is not gated at all.");

            var tag = LocateDomSyncedControl(page, found, control.Line, control.Snippet, control.Tag);

            Assert.True(tag.Text.Contains(control.DisabledExpression, StringComparison.Ordinal),
                $"{page}:{control.Line} no longer carries {control.DisabledExpression}. This control "
                + "renders server state into the DOM, so that attribute is the only safe refusal it "
                + "has: refuse in the handler instead and the field is unchanged, the render diff "
                + "emits no correction, and the browser keeps an action the server never took. If "
                + "the gate was deliberately narrowed, narrow the registered expression in the same "
                + "commit so the change is visible.");
        }
    }

    [Theory]
    [MemberData(nameof(ConvertedPages))]
    public void EveryDomSyncedControlIsRegisteredOrRecordedUngated(string page)
    {
        // The completeness half, and the half that makes the section worth having. An assertion that
        // only checked registered controls would leave the blind spot wide open: a new ungated
        // <input> added next month is simply never registered and nothing notices. This mirrors
        // EveryPageIsRegisteredOrDeclaredUnconverted - the page enumerates itself, so a new control
        // fails the suite and names itself.
        //
        // Asserted in BOTH directions, which is what keeps the enumerator honest as well as the
        // registry. Forward: a control on disk in neither list fails. Backward: a registered line
        // the enumeration no longer finds fails - so breaking DomSyncedTags, or stripping the
        // Disabled= that makes a child component visible to it, fails here too. M6 therefore trips
        // this assertion as well as the one above: with its parameter gone, CountryCodePicker stops
        // being a child component the scan can see at all.
        //
        // What is the cheapest broken implementation that still passes this?
        //   - "delete the feature" fails: if DomSyncedTags returns nothing, every registered line is
        //     unfound and the backward direction names them all.
        //   - "never call the thing under test" is the same failure - the enumeration is the thing
        //     under test in one direction and the registry in the other.
        //   - "hard-code one value" has nothing to hard-code; both sides are computed sets.
        //   - "change nothing" passes, correctly.
        // The escape hatch that does exist, stated rather than hidden: a genuinely ungated control
        // can be silenced by adding it to UngatedDomSyncedControls with a reason. That is a
        // deliberate, reviewable registry edit with prose attached - the same bargain
        // NotYetConverted strikes at page granularity - and it is the most a source-level scan can
        // ask for. NoControlRecordedUngatedHasQuietlyAcquiredAGate stops that list being used for a
        // control that IS gated.
        var entry = Entry(page);
        var source = ClickGateSource.Load(page);
        var found = DomSyncedTags(source);

        // The line is the registry key, here as everywhere else in this file, so two DOM-synced
        // controls starting on one line would hide one of them from both directions below.
        var collisions = found.GroupBy(tag => tag.Line)
            .Where(group => group.Count() > 1)
            .Select(group => $"{page}:{group.Key} ({group.Count()} controls)")
            .ToList();
        Assert.True(collisions.Count == 0,
            "two or more DOM-synced controls start on one line, and the line is what the registry "
            + "keys on, so one of them cannot be registered or checked. Put them on separate "
            + "lines:\n  " + string.Join("\n  ", collisions));

        var gated = entry.DomSyncedControls.Select(control => control.Line).ToList();
        var ungated = entry.UngatedDomSyncedControls.Select(control => control.Line).ToList();

        var both = gated.Intersect(ungated).OrderBy(line => line).ToList();
        Assert.True(both.Count == 0,
            $"{page}: line(s) {string.Join(", ", both)} are registered as gated AND as ungated. One "
            + "of the two entries is stale.");

        var accounted = gated.Concat(ungated).ToHashSet();

        var unregistered = found
            .Where(tag => !accounted.Contains(tag.Line))
            .Select(tag => $"{page}:{tag.Line} {FirstLineOf(tag.Text)}")
            .ToList();
        Assert.True(unregistered.Count == 0,
            "these controls render server state into the DOM and are in neither DomSyncedControls "
            + "nor UngatedDomSyncedControls. The disabled attribute is the only safe refusal for "
            + "such a control - a handler guard corrupts data - so each needs its gate registered, "
            + "or a written reason it has none:\n  " + string.Join("\n  ", unregistered));

        var onDisk = found.Select(tag => tag.Line).ToHashSet();
        var phantom = accounted.Except(onDisk).OrderBy(line => line).ToList();
        Assert.True(phantom.Count == 0,
            $"{page}: line(s) {string.Join(", ", phantom)} are registered as DOM-synced controls but "
            + "no input, select, textarea or Disabled-bearing child component starts there. Either "
            + "the control moved, or it lost the Disabled= parameter that is the only thing making a "
            + "child component visible to this scan - which for a component like CountryCodePicker "
            + "is the whole defect, not a bookkeeping error.");
    }

    [Theory]
    [MemberData(nameof(ConvertedPages))]
    public void NoControlRecordedUngatedHasQuietlyAcquiredAGate(string page)
    {
        // Without this, UngatedDomSyncedControls is a free pass out of the completeness assertion:
        // anything awkward goes in the list and stays there forever, including a control that was
        // later gated properly and now sits in the wrong half of the registry telling the next
        // reader it has no gate.
        //
        // What is the cheapest broken implementation that still passes this?
        //   - "delete the feature" fails: a recorded control that no longer exists, or that changed
        //     element type, fails at the locate step rather than being skipped.
        //   - "never call the thing under test" is that same failure.
        //   - "hard-code one value" - an empty reason - fails on the WhyUngated check.
        //   - "change nothing" passes, correctly.
        // Limits, both ways. The test for "has acquired a gate" is a conjunction: the tag carries a
        // disabled=/Disabled= attribute AND the tag mentions a registered busy signal. A control
        // gated on something this page does not register as a flag reads as ungated, which is
        // accurate - it is not busy-gated - and a control that mentions a flag in some unrelated
        // attribute while carrying an unrelated disabled= would fail spuriously. That direction of
        // error is the safe one: it says "re-check this by hand", which is what a reviewer should
        // do. What this cannot do is judge a reason; a plausible sentence attached to a real gap
        // passes. Migration 390 and 821 used to be the worked example here - their reasons said in
        // as many words that they were recorded rather than granted - and both have since been
        // gated, so the example is gone and this limitation now rests on nothing but its own
        // argument. That is worth saying rather than quietly deleting: a recorded-ungated entry is
        // a promise to come back, and the only thing enforcing it is a reader.
        var entry = Entry(page);
        var source = ClickGateSource.Load(page);
        var found = DomSyncedTags(source);

        foreach (var control in entry.UngatedDomSyncedControls)
        {
            Assert.False(string.IsNullOrWhiteSpace(control.WhyUngated),
                $"{page}:{control.Line} is recorded as an ungated DOM-synced control with no reason. "
                + "An inferred exemption is an oversight that looks like a decision.");

            var tag = LocateDomSyncedControl(page, found, control.Line, control.Snippet, control.Tag);

            Assert.False(
                DisabledAttribute.IsMatch(tag.Text) && NamesAnyBusySignal(entry, tag.Text),
                $"{page}:{control.Line} is recorded as ungated but now carries a disabled attribute "
                + "naming a busy signal. If it was gated on purpose, move it to DomSyncedControls "
                + $"with the expression registered verbatim. Recorded reason: {control.WhyUngated}");
        }
    }

    // ---- the paths that reach an operation without a click -----------------------------------

    [Theory]
    [MemberData(nameof(ConvertedPages))]
    public void EveryKeyboardPathIsRegisteredOrRecordedHarmless(string page)
    {
        // The completeness half of the keyboard section, and the reason the section exists.
        //
        // Commit 2eb8c15 found two live gating holes on the converted precedent page. Migration's
        // search box and staged-ticket box carried no disabled attribute and routed @onkeydown to
        // handlers calling SearchUser and ConfirmPendingAction with no busy guard, while the Search
        // and Confirm buttons beside them were gated on IsBusy. The button greyed out and Enter
        // still fired the operation; on the staged-ticket box that operation is destructive, and it
        // avoided double execution only by accident. Seven bespoke tripwires and a review missed it
        // because no assertion here matched a keyboard handler at all: the non-button sweep finds
        // @onclick only, and the DOM-synced assertions key on the disabled attribute, so a keyboard
        // path on an element with no gate was invisible to both.
        //
        // A key press that reaches an operation is a click-equivalent, and the owner ruling of
        // 2026-09-17 - a control is actionable only when its action will definitively execute - does
        // not care which key produced it.
        //
        // Three directions, which is what makes this worth having rather than decorative:
        //   forward  - a keyboard handler on the page in neither list fails and names itself;
        //   backward - a registered path the scan no longer finds fails, so deleting the markup or
        //              breaking the scanner fails here instead of going quiet;
        //   sentinel - an @onkey... attribute in a shape the scanner cannot read fails, rather than
        //              being counted as absent. Without it, "make the regex stop matching" empties
        //              the forward direction and every page reads as clean.
        //
        // What is the cheapest broken implementation that still passes this?
        //   - "delete the feature" (the scan returns nothing) fails: every registered path on
        //     Migration is then unfound and the backward direction names all of them.
        //   - "never call the thing under test" is that same failure.
        //   - "hard-code one value" has nothing to hard-code; both sides are computed sets.
        //   - "change nothing" passes, correctly - this is a drift tripwire, not a proof.
        // Stated rather than hidden: on a page with no keyboard handler and no registered path -
        // DhcpAuthorization and NamedLocations today - this is vacuous in both directions, and a
        // broken scanner would pass it. That is exactly why the scanner has a fixture test of its
        // own, KeyboardPathScanFindsTheShapesItClaimsTo, which depends on no page.
        var entry = Entry(page);
        var source = ClickGateSource.Load(page);

        var unreadable = UnparsedKeyboardAttributes(source.Text)
            .Select(index => $"{page}:{source.LineAt(index)}")
            .ToList();
        Assert.True(unreadable.Count == 0,
            "these @onkey... attributes are not in a shape this scan can read, so nothing below can "
            + "see what they are bound to. Write the handler as @onkeydown=\"Handler\", or extend "
            + "the scanner in the same commit:\n  " + string.Join("\n  ", unreadable));

        var found = KeyboardHandlerSites(source.Text);

        var untagged = found
            .Where(site => site.TagText.Length == 0)
            .Select(site => $"{page}:{source.LineAt(site.AttributeIndex)} @on{site.Event}")
            .ToList();
        Assert.True(untagged.Count == 0,
            "these keyboard handlers sit in no element this scan can delimit, so nothing can check "
            + "which element refuses them:\n  " + string.Join("\n  ", untagged));

        // Line plus event is the registry key, so two handlers for one event on one element's line
        // would hide one of them from both directions below.
        var collisions = found
            .GroupBy(site => KeyOf(source, site), StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => $"{page}:{group.Key} ({group.Count()} handlers)")
            .ToList();
        Assert.True(collisions.Count == 0,
            "two or more handlers for the same key event start on one line, and line plus event is "
            + "what the registry keys on, so one of them cannot be registered or checked. Put them "
            + "on separate lines:\n  " + string.Join("\n  ", collisions));

        var gated = entry.KeyboardPaths.Select(path => $"{path.Line}:{path.Event}").ToList();
        var harmless = entry.HarmlessKeyboardPaths
            .Select(path => $"{path.Line}:{path.Event}").ToList();

        var both = gated.Intersect(harmless, StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal).ToList();
        Assert.True(both.Count == 0,
            $"{page}: {string.Join(", ", both)} are registered as gated keyboard paths AND as "
            + "harmless. One of the two entries is stale.");

        var accounted = gated.Concat(harmless).ToHashSet(StringComparer.Ordinal);

        var unregistered = found
            .Where(site => !accounted.Contains(KeyOf(source, site)))
            .Select(site => $"{page}:{KeyOf(source, site)} -> {site.Handler}")
            .ToList();
        Assert.True(unregistered.Count == 0,
            "these keyboard handlers are in neither KeyboardPaths nor HarmlessKeyboardPaths. A key "
            + "press that reaches an operation is a click-equivalent and needs a declared refusal; "
            + "one that reaches nothing needs a written reason. Neither can be inferred, which is "
            + "the whole lesson of 2eb8c15:\n  " + string.Join("\n  ", unregistered));

        var onDisk = found.Select(site => KeyOf(source, site)).ToHashSet(StringComparer.Ordinal);
        var phantom = accounted.Except(onDisk).OrderBy(key => key, StringComparer.Ordinal).ToList();
        Assert.True(phantom.Count == 0,
            $"{page}: {string.Join(", ", phantom)} are registered keyboard paths that the scan no "
            + "longer finds. Either the element moved, or it lost the handler - and if the handler "
            + "really is gone the entry goes in the same commit, so the removal is visible rather "
            + "than leaving a registry that describes a page that no longer exists.");
    }

    [Theory]
    [MemberData(nameof(ConvertedPages))]
    public void EveryKeyboardPathIsRefusedByItsDeclaredMechanism(string page)
    {
        // The refusal half. Two legitimate answers, both checked against the file: the element
        // carries a gate consulting a registered busy signal, or the handler carries its own guard.
        //
        // The GatedTwinButtonLine check is 2eb8c15's defect stated as an invariant. The hole was not
        // that the key path was ungated in the abstract - it was that the button beside it WAS
        // gated, so every reading of the page said "this operation is refused while busy" and Enter
        // fired it anyway. Tying the two together means neither half can be changed alone.
        //
        // What is the cheapest broken implementation that still passes this? Worked through:
        //   - "delete the feature" fails: strip the disabled attribute from the element and the
        //     verbatim containment check fails naming the page, the line and the operation. That is
        //     the headline probe, and it is the 2eb8c15 defect restored.
        //   - "hard-code one value" fails twice over. Gate = "" is contained in every string, so the
        //     containment check alone would be decoration; the whole-attribute shape check and the
        //     names-a-busy-signal check reject it, and reject a bare "IsBusy" that could match
        //     incidentally elsewhere in the tag.
        //   - "never call the thing under test" fails: a path whose element moved, changed type, or
        //     lost its handler fails at the locate step rather than being skipped.
        //   - "change nothing" passes, correctly.
        // The one that does pass, said plainly rather than overclaimed: this is text containment. It
        // proves the attribute is present and unchanged, never that it is load-bearing - a page
        // whose gate is disabled="@(IsBusy && false)" passes, as this suite's header warns. What it
        // does add over the DOM-synced assertion covering the same element is the reason: that one
        // says the control must not accept typing, this one says the key press must not run
        // SearchUser, and the two can be narrowed apart by anyone who only reads one of them.
        var entry = Entry(page);
        var source = ClickGateSource.Load(page);
        var found = KeyboardHandlerSites(source.Text);

        foreach (var path in entry.KeyboardPaths)
        {
            var site = LocateKeyboardPath(
                page, source, found, path.Line, path.Event, path.Snippet, path.Tag);

            Assert.True(
                string.Equals(
                    ClickGateSource.CalledMethod(site.Handler), path.Handler, StringComparison.Ordinal),
                $"{page}:{path.Line} @on{path.Event} now binds '{site.Handler}', registered as "
                + $"'{path.Handler}'. The refusal on record was granted against the old handler.");

            var handlerBody = source.MethodBody(path.Handler);
            Assert.False(string.IsNullOrEmpty(handlerBody),
                $"{page}:{path.Line} binds @on{path.Event} to {path.Handler}, which this page does "
                + "not declare.");

            Assert.True(Regex.IsMatch(handlerBody, ClickGateSource.WholeWord(path.ReachesOperation)),
                $"{page}: {path.Handler} no longer reaches {path.ReachesOperation}, the operation "
                + "this entry's refusal was granted against. Re-check by hand what a key press runs "
                + $"now rather than updating the name to match. Recorded hazard: {path.Why}");

            switch (path.Refusal)
            {
                case KeyboardRefusal.DisabledAttribute:
                    Assert.True(
                        path.Gate.StartsWith("disabled=", StringComparison.Ordinal)
                        || path.Gate.StartsWith("Disabled=", StringComparison.Ordinal),
                        $"{page}:{path.Line} registers '{path.Gate}' as its gate, which is not a "
                        + "whole disabled=/Disabled= attribute. Register it exactly as the markup "
                        + "writes it: a bare identifier can be matched incidentally elsewhere in the "
                        + "same tag, so containment of one proves nothing.");

                    Assert.True(NamesAnyBusySignal(entry, path.Gate),
                        $"{page}:{path.Line} registers the gate '{path.Gate}', which names no "
                        + "predicate and no flag this page registers. A key path greyed by something "
                        + "that is not a busy signal is not gated at all.");

                    Assert.True(site.TagText.Contains(path.Gate, StringComparison.Ordinal),
                        $"{page}:{path.Line} no longer carries {path.Gate}, so @on{path.Event} fires "
                        + $"during an operation and {path.ReachesOperation} runs from the keyboard "
                        + "while the button beside it is refusing clicks. A disabled form control "
                        + $"fires no key events; nothing else on this element does. {path.Why}");

                    // Same element, same attribute, two registry halves. Without this they can drift
                    // into saying different things - or opposite things, if the element is sitting in
                    // UngatedDomSyncedControls while a keyboard path calls it gated.
                    if (FormElementTags.Contains(site.TagName, StringComparer.OrdinalIgnoreCase))
                    {
                        var domSynced = entry.DomSyncedControls
                            .FirstOrDefault(control => control.Line == path.Line);

                        Assert.True(domSynced is not null,
                            $"{page}:{path.Line} declares a disabled-attribute refusal on a control "
                            + "that renders server state into the DOM, but it is not in "
                            + "DomSyncedControls. Either it is unregistered there, or it is recorded "
                            + "as ungated - and the two halves of the registry cannot say opposite "
                            + "things about one element.");

                        Assert.True(
                            string.Equals(
                                domSynced!.DisabledExpression, path.Gate, StringComparison.Ordinal),
                            $"{page}:{path.Line} registers the gate as '{path.Gate}' here and as "
                            + $"'{domSynced.DisabledExpression}' in DomSyncedControls. The element "
                            + "has one attribute; one of the two entries is stale.");
                    }

                    break;

                case KeyboardRefusal.HandlerGuard:
                    Assert.True(NamesAnyBusySignal(entry, path.Gate),
                        $"{page}:{path.Line} registers the guard '{path.Gate}', which names no "
                        + "predicate and no flag this page registers, so it does not refuse a busy "
                        + "page.");

                    var guard = handlerBody.IndexOf(path.Gate, StringComparison.Ordinal);
                    Assert.True(guard >= 0,
                        $"{page}: {path.Handler} no longer carries '{path.Gate}'. The handler was "
                        + $"this path's only refusal, so {path.ReachesOperation} now runs on every "
                        + $"key press. {path.Why}");

                    var firstAwait = handlerBody.IndexOf("await ", StringComparison.Ordinal);
                    Assert.True(firstAwait < 0 || guard < firstAwait,
                        $"{page}: {path.Handler} carries '{path.Gate}' BELOW its first await, so the "
                        + "key press has already started work before anything refuses it.");
                    break;
            }

            if (path.GatedTwinButtonLine is { } twinLine)
            {
                var button = source.ClickableButtons().FirstOrDefault(tag => tag.Line == twinLine);
                Assert.True(button is not null,
                    $"{page}:{path.Line} records the button at {twinLine} as the gated twin of this "
                    + "key path, and there is no clickable button there any more.");

                var twinReaches = ClickGateSource.CalledMethod(ClickGateSource.HandlerOf(button!));
                Assert.True(
                    string.Equals(twinReaches, path.ReachesOperation, StringComparison.Ordinal),
                    $"{page}:{twinLine} reaches {twinReaches}, not {path.ReachesOperation}, so it is "
                    + $"no longer the twin of the key path at {path.Line} and the pairing on record "
                    + "is describing two different operations.");

                Assert.True(NamesAnyPredicate(entry, button!.Text),
                    $"{page}:{twinLine} no longer names a busy predicate, and it is on record as the "
                    + $"gated twin of the key path at {path.Line}. If that button is genuinely "
                    + "exempt now, the key path's own refusal is the only thing left and this entry "
                    + "needs re-checking by hand.");
            }
        }
    }

    [Theory]
    [MemberData(nameof(ConvertedPages))]
    public void NoKeyboardPathIsRefusedByADisabledAttributeAnElementIgnores(string page)
    {
        // The keyboard counterpart of NoHandlerGuardOnADomSyncedControl: a design tripwire against
        // the wrong remedy rather than against a missing one. The disabled attribute is inert on an
        // anchor, a div, a span or a td - the browser renders it and carries on dispatching keydown
        // - so a key path "refused" that way is not refused at all, while the markup reads exactly
        // like the two that are. Both of Migration's are on inputs, so this passes today by being
        // true, not by being unreachable.
        //
        // What is the cheapest broken implementation that still passes this?
        //   - "hard-code one value" is the live risk and is refused: the element name is read off
        //     the file by the locate step, not off the registry, so writing Tag = "input" on a div
        //     fails the locate step rather than satisfying this one.
        //   - "delete the feature" and "never call the thing under test" are the same failure - a
        //     path whose element is gone fails at the locate step.
        //   - "change nothing" passes, correctly.
        // Honest limitation: a child component could legitimately honour a Disabled parameter on its
        // own inner elements, and this refuses that shape outright rather than trying to judge it.
        // No converted page has one - a keyboard handler written on a component tag is a parameter,
        // not a DOM event - and forcing a human to look is the right failure direction if one
        // appears.
        var entry = Entry(page);
        var source = ClickGateSource.Load(page);
        var found = KeyboardHandlerSites(source.Text);

        foreach (var path in entry.KeyboardPaths
                     .Where(path => path.Refusal == KeyboardRefusal.DisabledAttribute))
        {
            var site = LocateKeyboardPath(
                page, source, found, path.Line, path.Event, path.Snippet, path.Tag);

            Assert.True(HonoursDisabled.Contains(site.TagName),
                $"{page}:{path.Line} is a <{site.TagName}> and declares a disabled-attribute "
                + "refusal. Only input, select, textarea and button honour disabled; every other "
                + $"element ignores it and keeps firing @on{path.Event}, so {path.ReachesOperation} "
                + "still runs and the greying is styling rather than refusal. Move the handler onto "
                + "a control the attribute reaches, or declare KeyboardRefusal.HandlerGuard and put "
                + "the guard in the handler.");
        }
    }

    [Theory]
    [MemberData(nameof(ConvertedPages))]
    public void EveryHarmlessKeyboardPathStillReachesNoOperation(string page)
    {
        // Without this, HarmlessKeyboardPaths is a free pass out of the completeness assertion:
        // anything awkward goes in the list with a plausible sentence and stays there, including a
        // handler that was later wired to an operation and now sits in the half of the registry that
        // tells the next reader a key press does nothing.
        //
        // "Reaches no operation" is made checkable rather than left as prose: the handler must not
        // name any method on this page that raises a flag the page registers as in-flight. A handler
        // that raises one itself is caught by the same test, because a method body includes its own
        // signature.
        //
        // What is the cheapest broken implementation that still passes this?
        //   - "hard-code one value" - an empty reason - fails on the WhyHarmless check.
        //   - "delete the feature" and "never call the thing under test" fail at the locate step: a
        //     recorded handler that is gone, moved, or on a different element fails rather than
        //     being skipped.
        //   - "change nothing" passes, correctly.
        // Two limits, both stated rather than papered over. This is one call deep: a handler calling
        // a helper that calls a busy operation passes, and only a reader catches that. And it cannot
        // judge a reason - a plausible sentence attached to a real gap passes, which is the most any
        // registry-driven assertion in this suite can do. The list is empty on all three converted
        // pages today, so this assertion is vacuous until one needs it; it was proved to bite by
        // moving Migration 390 into the list and watching it fail on SearchUser.
        var entry = Entry(page);
        var source = ClickGateSource.Load(page);
        var found = KeyboardHandlerSites(source.Text);

        var raisers = AllFlags(entry)
            .SelectMany(source.NonClearingSetters)
            .Select(setter => source.EnclosingMethodName(setter.Index))
            .Where(name => name.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var path in entry.HarmlessKeyboardPaths)
        {
            Assert.False(string.IsNullOrWhiteSpace(path.WhyHarmless),
                $"{page}:{path.Line} is recorded as a harmless keyboard path with no reason. An "
                + "inferred exemption is an oversight that looks like a decision.");

            var site = LocateKeyboardPath(
                page, source, found, path.Line, path.Event, path.Snippet, path.Tag);

            Assert.True(
                string.Equals(
                    ClickGateSource.CalledMethod(site.Handler), path.Handler, StringComparison.Ordinal),
                $"{page}:{path.Line} @on{path.Event} now binds '{site.Handler}', recorded as "
                + $"'{path.Handler}'. The harmlessness on record is about the old handler.");

            var body = source.MethodBody(path.Handler);
            Assert.False(string.IsNullOrEmpty(body),
                $"{page}:{path.Line} binds @on{path.Event} to {path.Handler}, which this page does "
                + "not declare.");

            var reaches = raisers
                .Where(raiser => Regex.IsMatch(body, ClickGateSource.WholeWord(raiser)))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

            Assert.True(reaches.Count == 0,
                $"{page}:{path.Line} is recorded as reaching no operation, but {path.Handler} names "
                + $"{string.Join(", ", reaches)}, which raise a busy flag this page registers. A key "
                + "press that reaches an operation is a click-equivalent: move it to KeyboardPaths "
                + $"with the refusal that stops it. Recorded reason: {path.WhyHarmless}");
        }
    }

    [Fact]
    public void KeyboardPathScanFindsTheShapesItClaimsTo()
    {
        // The completeness assertion above is vacuous on a page with no keyboard handler, and two of
        // the three converted pages are exactly that. A broken scanner would be green on both and
        // the third page would be the only thing holding the rule up. This exercises the scanner
        // against a fixture written here, so "delete the feature" fails whatever the pages contain.
        //
        // What is the cheapest broken implementation that still passes this? None of the four. A
        // scanner returning nothing fails the first assertion; one returning everything fails the
        // negatives; one hard-coding a single shape fails the others; and "never call the thing
        // under test" is not available, because the thing under test is all this test calls.
        // Every stray ">" below is inside a quoted attribute value and is load-bearing, in two
        // different positions, because a fixture that only put one AFTER the handler would not catch
        // a walker that stopped being quote-aware. On the textarea the ">" comes BEFORE the handler,
        // so losing quote-awareness ends the element early and the handler falls outside every
        // element - TagName goes empty and the tuple assertion below fails. On the first input it
        // comes AFTER the handler but BEFORE the gate, so the element is still matched but its text
        // is truncated - which the TagText assertion below catches and the tuple assertion does not.
        // Both directions matter: TagText is what every verbatim gate check is read out of.
        var fixture = string.Join("\n",
            "<div class=\"wrap\">",
            "    <input type=\"text\" @bind=\"q\" @onkeydown=\"OnSearchKey\" title=\"a > b\" "
            + "disabled=\"@IsBusy\" />",
            "    <textarea title=\"b > c\" @onkeyup=\"OnNotesKey\"></textarea>",
            "    <span @onkeypress=\"OnSpanKey\">x</span>",
            "    <a href=\"#\" @onkeydown=\"() => Pick(row)\" @onkeydown:preventDefault>go</a>",
            "    <input title=\"no handler\" @bind:event=\"oninput\" />",
            "</div>",
            "@* a comment carrying @onkeydown=\"Ghost\" *@");

        var text = ClickGateSource.BlankComments(fixture);

        // Proves four things at once that the pages cannot: keyup and keypress are found as well as
        // keydown; a ">" inside a quoted attribute value does not end the element; the element name
        // is read off the file rather than guessed; and a handler named only inside a razor comment
        // is not found, because the scan runs on comment-blanked text.
        Assert.Equal(
            new[]
            {
                "keydown/OnSearchKey/input",
                "keyup/OnNotesKey/textarea",
                "keypress/OnSpanKey/span",
                "keydown/() => Pick(row)/a",
            },
            KeyboardHandlerSites(text)
                .Select(site => $"{site.Event}/{site.Handler}/{site.TagName}")
                .ToArray());

        // The gate checks are all containment tests over TagText, so a walker that ends an element
        // early would report every gate as missing - or, worse, report a narrowed one as intact.
        Assert.Contains("disabled=\"@IsBusy\"",
            KeyboardHandlerSites(text)
                .Single(site => site.Handler == "OnSearchKey").TagText,
            StringComparison.Ordinal);

        // @onkeydown:preventDefault binds nothing, so it must not be reported as unreadable either.
        Assert.Empty(UnparsedKeyboardAttributes(text));

        // The shape the sentinel exists for: a handler bound without quotes, which the attribute
        // pattern cannot read. It must be REPORTED, not silently counted as absent - otherwise
        // narrowing the pattern is a way to make every page look clean.
        var unquoted = ClickGateSource.BlankComments("<input @onkeydown=@(() => Go()) />");
        Assert.Empty(KeyboardHandlerSites(unquoted));
        Assert.Single(UnparsedKeyboardAttributes(unquoted));
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
        // This used to match the literal "if (IsBusy) return;" and nothing else, and the hole that
        // left was measured rather than argued. The MailboxPermissions conversion planted a matched
        // pair in ExecuteOnPrem: the bare shape failed this assertion, and the COMPOUND shape that
        // page's own guard is written in - if (IsBusy || !onPremConfirmPending) return; - left the
        // suite fully green at 109/109. The registered consequence for that site is a destructive
        // on-prem write silently not happening, so the one shape that slipped past was the one a
        // reader would have copied from the page beside it. It now matches the guard SHAPE - an
        // early return whose condition names a busy signal this page registers - rather than one
        // spelling of it.
        //
        // Where the line is drawn, and what it may now wrongly catch. A busy guard is an if whose
        // body is nothing but a return, with or without braces and with or without a value, and
        // whose condition names a registered predicate or flag. Three consequences worth naming:
        //   - the inverse, if (!isLoading) return;, is also caught, and the message will say
        //     "refuses while busy" when it does not. That is the safe direction - an early return
        //     keyed on the busy state of a method that must never be keyed on it needs a human
        //     either way - but it is a false positive in the strict sense and is not a defect found.
        //   - a condition that merely mentions a flag alongside something else is caught too.
        //   - a refusal that reports before returning - if (isLoading) { result = Fail(...); return; }
        //     - is still NOT caught, nor is one behind a helper or a mid-body flag read. This proves
        //     the absence of the guard shapes, never the absence of refusal, exactly as before.
        //
        // What is the cheapest broken implementation that still passes this? A negative assertion
        // has an honest answer and it is a bad one: break the matcher so it finds nothing and every
        // page passes. That is not left hanging. BusyGuardScanFindsTheShapesItClaimsTo exercises the
        // matcher against a fixture written here, so a dead matcher fails whatever the pages
        // contain, and TheExactlyOneGuardSitsWhereItIsRegisteredAndNowhereElse uses the SAME matcher
        // in the positive direction - the registered guard must be found - so a matcher that stops
        // finding things fails there as well. "Change nothing" passes, correctly; this is a drift
        // tripwire.
        var entry = Entry(page);
        var source = ClickGateSource.Load(page);

        foreach (var site in entry.ForbiddenGuardSites)
        {
            var body = source.MethodBody(site.Method);
            Assert.False(string.IsNullOrEmpty(body),
                $"{page}: ForbiddenGuardSite names {site.Method}, which does not exist");

            var offset = source.Text.IndexOf(body, StringComparison.Ordinal);

            var planted = BusyGuards(entry, body)
                .Select(match => $"{page}:{source.LineAt(offset + match.Index)} {OneLine(match.Value)}")
                .ToList();

            Assert.True(planted.Count == 0,
                $"{page}: {site.Method} must NOT refuse while busy - it is called from "
                + $"{string.Join(", ", site.CalledWhileBusyFrom)} while the page is already busy. "
                + $"{site.Consequence}\n  " + string.Join("\n  ", planted));
        }
    }

    [Theory]
    [MemberData(nameof(ConvertedPages))]
    public void TheExactlyOneGuardSitsWhereItIsRegisteredAndNowhereElse(string page)
    {
        // ForbiddenGuardSites says where a guard may not live. It cannot say where the guard that
        // replaces it DOES live, so on a pair like MailboxPermissions' Continue path - ConfirmOnPrem
        // guards, ExecuteOnPrem must not - deleting the guard outright satisfies every assertion in
        // this suite: the forbidden site is still clean and no other rule requires a handler guard
        // anywhere. That leaves the destructive on-prem write open to a second dispatch in the round
        // trip before the Continue button's disabled attribute reaches the browser, which is the
        // hazard the page's own comment describes. This is the positive half.
        //
        // Three checks, and the first alone would be decoration:
        //   1. the registered guard is present verbatim in the method that owns it. On its own this
        //      is "the text exists somewhere", which proves nothing about where;
        //   2. it sits ABOVE that method's first await. A guard below the first await refuses after
        //      the handler has already yielded, which is not a refusal;
        //   3. no busy guard exists in any of the methods registered as the ones that must not carry
        //      one. This is what makes it "exactly one" rather than "at least one somewhere".
        // Checks 1 and 3 are what a size-preserving MOVE of the guard from the owner into the callee
        // flips at once, and both halves are collected into a single failure so that mutation names
        // both methods rather than stopping at the first.
        //
        // What is the cheapest broken implementation that still passes this?
        //   - "delete the feature" fails twice: the guard's absence trips check 1, and a matcher
        //     that finds nothing trips check 1 as well, because the same matcher must locate the
        //     registered guard as a real early return on a busy signal.
        //   - "hard-code one value" fails: an empty Guard is contained in every string, so the shape
        //     check rejects it, and an empty WhyThisOne is rejected outright.
        //   - "never call the thing under test" fails: a renamed or deleted method fails rather than
        //     being skipped, as does one that no longer awaits.
        //   - "change nothing" passes, correctly.
        // Said plainly rather than overclaimed, as everywhere else here: this is text containment
        // plus ordering within one method body. It proves the guard is present, is shaped like an
        // early return on a busy signal, and precedes the first await. It cannot prove the guard is
        // reachable, and it cannot prove the caller relationship the reasoning rests on - that
        // ExecuteOnPrem has exactly one caller is checked by a reader, not by this.
        //
        // Deliberate overlap, recorded rather than hidden: on MailboxPermissions check 3 covers the
        // same ground as that page's ForbiddenGuardSite for ExecuteOnPrem, so the move mutation
        // fails both assertions. They are kept separate because they are separate claims - one is
        // "a busy caller calls this, so a guard here no-ops the refresh", the other is "the refusal
        // for this pair lives there" - and either registry entry can be deleted without the other.
        var entry = Entry(page);
        var source = ClickGateSource.Load(page);

        foreach (var rule in entry.ExactlyOneGuard)
        {
            Assert.False(string.IsNullOrWhiteSpace(rule.WhyThisOne),
                $"{page}: the ExactlyOneGuardOf on {rule.GuardedMethod} records no reason. Which of "
                + "two methods owns a refusal is a decision, and an unrecorded decision is "
                + "indistinguishable from an accident to the next reader.");

            Assert.True(rule.Guard.StartsWith("if (", StringComparison.Ordinal),
                $"{page}: '{rule.Guard}' is registered as {rule.GuardedMethod}'s guard but is not a "
                + "whole if-condition. Register it exactly as the method writes it, up to the "
                + "closing bracket: a bare identifier can be matched incidentally elsewhere in the "
                + "body, so containment of one proves nothing.");

            var body = source.MethodBody(rule.GuardedMethod);
            Assert.False(string.IsNullOrEmpty(body),
                $"{page}: ExactlyOneGuardOf names {rule.GuardedMethod} as the method that owns this "
                + $"refusal, and the page does not declare it. {rule.WhyThisOne}");

            var offset = source.Text.IndexOf(body, StringComparison.Ordinal);
            var findings = new List<string>();

            var at = body.IndexOf(rule.Guard, StringComparison.Ordinal);
            if (at < 0)
            {
                findings.Add($"{rule.GuardedMethod} no longer carries '{rule.Guard}', so nothing "
                    + "refuses this operation in the method on record as owning the refusal");
            }
            else
            {
                if (!BusyGuards(entry, body).Any(match => match.Index == at))
                    findings.Add($"{rule.GuardedMethod} still contains the text '{rule.Guard}' at "
                        + $"line {source.LineAt(offset + at)}, but it is no longer an early return "
                        + "on a busy signal - it has stopped returning, or it now guards something "
                        + "else and the refusal has quietly gone");

                var firstAwait = body.IndexOf("await ", StringComparison.Ordinal);
                if (firstAwait < 0)
                    findings.Add($"{rule.GuardedMethod} no longer awaits anything, so it is either a "
                        + "different method or a rewritten one and this entry needs a human rather "
                        + "than an update");
                else if (at > firstAwait)
                    findings.Add($"{rule.GuardedMethod} carries '{rule.Guard}' at line "
                        + $"{source.LineAt(offset + at)}, BELOW its first await. A refusal that runs "
                        + "after the handler has already yielded is not a refusal");
            }

            foreach (var unguarded in rule.UnguardedMethods)
            {
                var calleeBody = source.MethodBody(unguarded);
                Assert.False(string.IsNullOrEmpty(calleeBody),
                    $"{page}: ExactlyOneGuardOf lists {unguarded} as a method that must carry no "
                    + $"guard, and the page does not declare it. {rule.WhyThisOne}");

                var calleeOffset = source.Text.IndexOf(calleeBody, StringComparison.Ordinal);

                findings.AddRange(BusyGuards(entry, calleeBody)
                    .Select(match => $"{unguarded} has grown a busy guard at line "
                        + $"{source.LineAt(calleeOffset + match.Index)}: {OneLine(match.Value)}"));
            }

            Assert.True(findings.Count == 0,
                $"{page}: the refusal for this pair must sit in {rule.GuardedMethod} and in none of "
                + $"{string.Join(", ", rule.UnguardedMethods)}. {rule.WhyThisOne}\n  "
                + string.Join("\n  ", findings));
        }
    }

    [Fact]
    public void BusyGuardScanFindsTheShapesItClaimsTo()
    {
        // ForbiddenGuardSitesCarryNoGuard is a negative assertion, so a matcher that finds nothing
        // makes every page pass. That cannot be closed from the pages - it is the same hole
        // KeyboardPathScanFindsTheShapesItClaimsTo exists for - so the matcher is exercised here
        // against a fixture that depends on no page at all.
        //
        // What is the cheapest broken implementation that still passes this? None of the four. A
        // matcher returning nothing fails the first assertion; one returning every if fails the
        // negatives below it; one hard-coding the bare literal fails on the compound, braced and
        // valued shapes; and "never call the thing under test" is not available, because the matcher
        // is all this test calls.
        var entry = new PageGateEntry
        {
            Page = "fixture",
            ExpectedLineCount = 0,
            Predicates = [new PredicateScope("IsBusy", ["isLoading"], AppliesWhen: "the fixture")],
        };

        // Four shapes that ARE busy guards, then four that are not. The compound shape on the second
        // line is the one commit 426f3a1 measured slipping past the old literal match, and the
        // nested call on the fifth is there because a condition one paren deep used to be all the
        // scan could read.
        const string body = """
            if (IsBusy) return;
            if (IsBusy || !onPremConfirmPending)
                return;
            if (isLoading)
            {
                return;
            }
            if (IsBusy) return false;
            if (IsBusy || string.IsNullOrWhiteSpace(Trim(ticket))) return;
            if (csvFile is null) return;
            if (string.IsNullOrWhiteSpace(ticket)) return;
            if (isLoading)
            {
                result = Fail("busy");
                return;
            }
            """;

        Assert.Equal(
            new[]
            {
                "IsBusy",
                "IsBusy || !onPremConfirmPending",
                "isLoading",
                "IsBusy",
                "IsBusy || string.IsNullOrWhiteSpace(Trim(ticket))",
            },
            BusyGuards(entry, body)
                .Select(match => OneLine(match.Groups["cond"].Value))
                .ToArray());

        // The two negatives that matter most: an early return on a form precondition is not a busy
        // guard however many of them a handler has, and neither is one whose condition names nothing
        // this page registers. Without these the widening would fail correct pages instead.
        Assert.Empty(BusyGuards(entry, "if (csvFile is null) return;"));
        Assert.Empty(BusyGuards(entry, "if (string.IsNullOrWhiteSpace(ticket)) return;"));

        // The documented limit, asserted so it stays a known limit rather than becoming a surprise:
        // a refusal that reports before returning is not matched.
        Assert.Empty(BusyGuards(entry, "if (isLoading)\n{\n    result = Fail(\"busy\");\n    return;\n}"));
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
    /// True if <paramref name="text"/> names a predicate OR one of the flags a predicate is built
    /// from. Wider than <see cref="NamesAnyPredicate"/> on purpose: Migration's form controls are
    /// gated on a single member flag rather than on IsBusy, which is that page's pre-ruling shape
    /// and not something a test can change without editing the page.
    /// </summary>
    private static bool NamesAnyBusySignal(PageGateEntry entry, string text) =>
        NamesAnyPredicate(entry, text)
        || AllFlags(entry).Any(flag => Regex.IsMatch(text, ClickGateSource.WholeWord(flag)));

    /// <summary>
    /// An early return keyed on a condition: <c>if (...) return;</c>, with or without braces around
    /// the return and with or without a returned value.
    /// </summary>
    /// <remarks>
    /// The condition pattern reads a bracketed expression two levels deep -
    /// <c>if (IsBusy || string.IsNullOrWhiteSpace(Trim(x)))</c> - which covers every guard shape on
    /// the converted pages. A third level is not read, and such an if is skipped rather than
    /// half-matched. Deliberately not built on ClickGateSource.ExtractBlock, whose brace counting is
    /// not quote-aware; this never counts a brace at all.
    /// </remarks>
    private static readonly Regex EarlyReturnGuard = new(
        @"\bif\s*\((?<cond>[^()]*(?:\((?:[^()]|\([^()]*\))*\)[^()]*)*)\)\s*(?:\{\s*)?return\b[^;{}]*;",
        RegexOptions.Compiled);

    /// <summary>
    /// Every early return in <paramref name="body"/> whose condition names a busy signal
    /// <paramref name="entry"/> registers: the shapes a busy guard is written in, rather than one
    /// spelling of it. See <see cref="ForbiddenGuardSitesCarryNoGuard"/> for where the line is drawn
    /// and what it may wrongly catch, and <see cref="BusyGuardScanFindsTheShapesItClaimsTo"/> for
    /// the fixture that keeps it from quietly matching nothing.
    /// </summary>
    private static IEnumerable<Match> BusyGuards(PageGateEntry entry, string body) =>
        EarlyReturnGuard.Matches(body)
            .Where(match => NamesAnyBusySignal(entry, match.Groups["cond"].Value));

    /// <summary>A matched statement collapsed onto one line, for a readable failure message.</summary>
    private static string OneLine(string text) => Regex.Replace(text, @"\s+", " ").Trim();

    /// <summary>
    /// A disabled attribute or Disabled parameter that has a VALUE. A bare <c>disabled</c> with no
    /// "=" is a static, inert control, which is a different thing and is registered as ungated.
    /// </summary>
    private static readonly Regex DisabledAttribute = new(@"\b[Dd]isabled\s*=", RegexOptions.Compiled);

    private static readonly string[] FormElementTags = { "input", "select", "textarea" };

    /// <summary>
    /// Every control on the page that renders server state into the DOM: the html form elements,
    /// plus any child component carrying a disabled attribute or a Disabled parameter.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Built on <see cref="ClickGateSource.Tags"/>, which is quote-aware, and deliberately NOT on
    /// ClickGateSource.ExtractBlock, whose brace counting is not - a brace inside an interpolated
    /// string ends its block in the wrong place, and Migration's placeholder at 324 is exactly such
    /// a string. Recorded in .agents/state.md; fixing it is its own slice.
    /// </para>
    /// <para>
    /// Tags() needs a name, so child components are found by harvesting PascalCase tag names first.
    /// That harvest also hits C# generics in the @code block - List&lt;NamedLocation&gt;,
    /// Task&lt;PermissionResult&gt; - and the disabled filter is what keeps them out: a type
    /// argument carries no disabled=.
    /// </para>
    /// <para>
    /// Honest limitation, stated rather than papered over: a child component carrying NO disabled
    /// attribute at all is invisible here, because there is nothing to tell it apart from a generic.
    /// Once a component IS registered the gap closes from the other side - losing the attribute
    /// fails <see cref="EveryDomSyncedControlIsRegisteredOrRecordedUngated"/> in its backward
    /// direction - so what remains uncovered is a child component that was never gated at all.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<ClickGateSource.Tag> DomSyncedTags(ClickGateSource source)
    {
        var tags = FormElementTags.SelectMany(source.Tags).ToList();

        var components = Regex.Matches(source.Text, @"<(?<name>[A-Z][A-Za-z0-9]*)\b")
            .Select(match => match.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal);

        tags.AddRange(components
            .SelectMany(source.Tags)
            .Where(tag => DisabledAttribute.IsMatch(tag.Text)));

        return tags.OrderBy(tag => tag.Index).ToList();
    }

    /// <summary>The element or component name a tag opens with, e.g. "input", "CountryCodePicker".</summary>
    private static string TagNameOf(ClickGateSource.Tag tag) =>
        Regex.Match(tag.Text, @"^<(?<name>[A-Za-z][A-Za-z0-9]*)").Groups["name"].Value;

    /// <summary>The first line of a tag's text, for a failure message that stays one line.</summary>
    private static string FirstLineOf(string text) =>
        text.Split('\n')[0].Trim();

    /// <summary>
    /// The live tag a registered DOM-synced entry points at. Located by line, disambiguated by
    /// snippet, and checked for element type - so an entry cannot silently start covering whatever
    /// drifted onto its line, which is the failure mode ExemptControl.Snippet was added for.
    /// </summary>
    private static ClickGateSource.Tag LocateDomSyncedControl(
        string page,
        IReadOnlyList<ClickGateSource.Tag> found,
        int line,
        string snippet,
        string tagName)
    {
        var onLine = found.Where(tag => tag.Line == line).ToList();
        Assert.True(onLine.Count > 0,
            $"{page}: no input, select, textarea or Disabled-bearing child component starts at line "
            + $"{line}, where a DOM-synced control is registered.");

        var match = onLine.FirstOrDefault(tag => tag.Text.Contains(snippet, StringComparison.Ordinal));
        Assert.True(match is not null,
            $"{page}:{line} no DOM-synced control there contains '{snippet}'. The entry may now be "
            + "covering a different control; re-check it against the file rather than updating the "
            + "snippet to whatever is there.");

        Assert.True(string.Equals(TagNameOf(match!), tagName, StringComparison.Ordinal),
            $"{page}:{line} is registered as <{tagName}> but is now <{TagNameOf(match!)}>. The "
            + "refusal mechanism depends on which element this is, so re-check the entry.");

        return match!;
    }

    /// <summary>
    /// A regex matching a READ of <paramref name="identifier"/>: a whole-word occurrence that is
    /// neither the left-hand side of an assignment nor a named-argument label. <c>x.Foo</c>,
    /// <c>f(x)</c> and <c>x == y</c> are reads; <c>x = y</c> is not, and neither is the <c>x:</c> of
    /// <c>f(a, x: b)</c>. The distinction is the whole point here - a handler clearing a form field
    /// after a successful write is correct, while reading one back mid-flight is the defect.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The label exclusion was forced by a real obligation that could not otherwise be written down,
    /// not by tidiness. AuditService.LogMailboxPermission takes a parameter called autoMapping and
    /// MailboxPermissions.SubmitSingle passes it by name at the success-path audit call, so the
    /// label <c>autoMapping:</c> read as a live field read and the page's real autoMapping
    /// obligation failed on it while the code it describes was already correct. The alternative -
    /// making that the ninth positional bool of an audit call - was rejected: it deforms readable
    /// code to satisfy a text matcher, and the matcher is what was wrong.
    /// </para>
    /// <para>
    /// Where the line is drawn. A colon is treated as a label only when BOTH halves hold: the
    /// identifier sits in argument-start position - immediately after a "(" or a "," with nothing
    /// but whitespace between - AND a ":" follows it immediately, with no space and not a "::". In
    /// C# that conjunction is a named argument and nothing else. Every other colon a @code block can
    /// produce still counts as a read, which is what the conjunction buys: a ternary's
    /// <c>cond ? live : other</c> (the middle operand follows "?", never "(" or ","), a
    /// <c>case live:</c> label (follows "case"), a goto label (follows ";" or "{"), and an
    /// interpolated string's format or alignment specifier, <c>{live:N2}</c> and
    /// <c>{live,8:N2}</c> (both follow "{"). The razor colons - <c>@bind:event</c>,
    /// <c>@onkeydown:preventDefault</c> - are markup, so they cannot reach a method body at all, and
    /// they follow "@" rather than "(" or "," in any case.
    /// </para>
    /// <para>
    /// What this might now wrongly miss, stated rather than left to be discovered: a genuine read
    /// written as <c>f(live:</c> or <c>f(a, live:</c>. No C# expression has that shape except the
    /// named argument, so what is given up is a future construct rather than a present one. The
    /// spaced spelling <c>f(live : b)</c> is deliberately NOT excluded, so a named argument written
    /// that way still fails as a live read - the safe direction, because it asks for a human instead
    /// of going quiet.
    /// </para>
    /// </remarks>
    private static string ReadOf(string identifier)
    {
        var read = ClickGateSource.WholeWord(identifier) + @"(?!\s*=(?!=))";

        // "Not (argument-start position AND followed by a label colon)", written as the equivalent
        // disjunction because a lookbehind placed after the identifier would look back at the
        // identifier itself rather than at what precedes it. First alternative: not in
        // argument-start position. Second: not followed by a label colon. Either makes it a read,
        // and both alternatives match the identifier alone, so a position cannot be counted twice.
        return $@"(?:(?<![(,]\s*){read}|{read}(?!:(?!:)))";
    }

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

    // ---- keyboard-path machinery ---------------------------------------------------------------
    //
    // Placed below FormElementTags on purpose: static field initialisers run in textual order, and
    // HonoursDisabled reads that array.

    /// <summary>One markup element, from its "&lt;" to the "&gt;" that closes its attribute list.</summary>
    /// <param name="End">The index of the closing "&gt;" itself, not one past it.</param>
    private sealed record MarkupTag(int Start, int End, string Name);

    /// <summary>
    /// A keyboard handler bound in markup, with the element it sits on. <see cref="TagText"/> is
    /// empty when no element could be delimited around it, which the completeness assertion treats
    /// as a failure rather than a skip.
    /// </summary>
    private sealed record KeyboardHandlerSite(
        string Event,
        string Handler,
        int AttributeIndex,
        string TagName,
        string TagText,
        int TagIndex);

    /// <summary>A keyboard handler bound to a quoted expression: <c>@onkeydown="Foo"</c>.</summary>
    private static readonly Regex KeyboardHandlerAttribute = new(
        @"@on(?<event>keydown|keyup|keypress)\s*=\s*""(?<handler>[^""]*)""", RegexOptions.Compiled);

    /// <summary>
    /// A Blazor event modifier, which binds no handler: <c>@onkeydown:preventDefault</c>. Matched
    /// only so the sentinel below does not report it as a shape it cannot read.
    /// </summary>
    private static readonly Regex KeyboardEventModifier = new(
        @"@on(?:keydown|keyup|keypress):[A-Za-z]+", RegexOptions.Compiled);

    /// <summary>Anything that looks like a keyboard attribute at all, including both shapes above.</summary>
    private static readonly Regex AnyKeyboardAttribute = new(@"@onkey[A-Za-z]*", RegexOptions.Compiled);

    /// <summary>
    /// The elements the disabled attribute actually reaches. Everything else renders it as an inert
    /// attribute and carries on dispatching key events.
    /// </summary>
    private static readonly HashSet<string> HonoursDisabled =
        new(FormElementTags.Append("button"), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Every element in <paramref name="text"/>, walked quote-aware.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same shape as <see cref="ClickGateSource.Tags"/>, and deliberately NOT
    /// ClickGateSource.ExtractBlock, whose brace counting is not quote-aware. Tags() itself cannot
    /// be used here because it needs a tag name up front and a keyboard handler can sit on any
    /// element; this walk is generic for exactly that reason. It skips past each element's closing
    /// "&gt;", so a "&lt;" inside a quoted attribute value cannot open a second element inside the
    /// first, and the elements it returns never overlap.
    /// </para>
    /// <para>
    /// Honest limitation: outside an element, a "&lt;" immediately followed by a letter opens a
    /// candidate, which C# generics in the @code block do - <c>Func&lt;string, Task&gt;</c>. Those
    /// are harmless because a keyboard attribute is never inside one, and if a stray candidate ever
    /// did swallow a real element the registered Tag name would no longer match, so the entry fails
    /// loudly rather than passing.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<MarkupTag> MarkupTags(string text)
    {
        var tags = new List<MarkupTag>();

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '<' || i + 1 >= text.Length || !char.IsLetter(text[i + 1]))
                continue;

            var nameEnd = i + 1;
            while (nameEnd < text.Length
                   && (char.IsLetterOrDigit(text[nameEnd]) || text[nameEnd] == '_'))
                nameEnd++;

            var quote = '\0';
            for (var j = nameEnd; j < text.Length; j++)
            {
                var c = text[j];
                if (quote != '\0')
                {
                    if (c == quote) quote = '\0';
                }
                else if (c is '"' or '\'')
                {
                    quote = c;
                }
                else if (c == '>')
                {
                    tags.Add(new MarkupTag(i, j, text[(i + 1)..nameEnd]));
                    i = j;
                    break;
                }
            }
        }

        return tags;
    }

    /// <summary>
    /// Every keyboard handler bound in markup, with the element it sits on. Pass comment-blanked
    /// text (<see cref="ClickGateSource.Text"/>), or a handler named only in a comment is found.
    /// </summary>
    private static IReadOnlyList<KeyboardHandlerSite> KeyboardHandlerSites(string text)
    {
        var tags = MarkupTags(text);

        return KeyboardHandlerAttribute.Matches(text)
            .Select(match =>
            {
                var tag = tags.FirstOrDefault(
                    candidate => candidate.Start < match.Index && match.Index < candidate.End);

                return new KeyboardHandlerSite(
                    match.Groups["event"].Value,
                    match.Groups["handler"].Value,
                    match.Index,
                    tag?.Name ?? "",
                    tag is null ? "" : text[tag.Start..(tag.End + 1)],
                    tag?.Start ?? match.Index);
            })
            .ToList();
    }

    /// <summary>
    /// The offset of every keyboard attribute that is neither a readable handler nor a modifier.
    /// </summary>
    /// <remarks>
    /// All three patterns anchor at the same "@onkey", so a set difference on the match offset is
    /// exact. This is what stops "narrow the pattern until it matches nothing" from emptying the
    /// completeness sweep: an unreadable shape is reported, never counted as absent.
    /// </remarks>
    private static IReadOnlyList<int> UnparsedKeyboardAttributes(string text)
    {
        var readable = KeyboardHandlerAttribute.Matches(text).Select(match => match.Index)
            .Concat(KeyboardEventModifier.Matches(text).Select(match => match.Index))
            .ToHashSet();

        return AnyKeyboardAttribute.Matches(text)
            .Select(match => match.Index)
            .Where(index => !readable.Contains(index))
            .ToList();
    }

    /// <summary>
    /// The registry key for a keyboard path: the line the ELEMENT starts on, plus the event. The
    /// element's line rather than the attribute's, which is often a continuation line - 390 and not
    /// 392 for Migration's search box - so a gated path can be checked against the
    /// <see cref="DomSyncedControl"/> entry for the same control.
    /// </summary>
    private static string KeyOf(ClickGateSource source, KeyboardHandlerSite site) =>
        $"{source.LineAt(site.TagIndex)}:{site.Event}";

    /// <summary>
    /// The live handler a registered keyboard path points at. Located by line and event,
    /// disambiguated by snippet, and checked for element type - so an entry cannot silently start
    /// covering whatever drifted onto its line, and cannot claim a refusal that depends on the
    /// element being something it is not.
    /// </summary>
    private static KeyboardHandlerSite LocateKeyboardPath(
        string page,
        ClickGateSource source,
        IReadOnlyList<KeyboardHandlerSite> found,
        int line,
        string keyEvent,
        string snippet,
        string tagName)
    {
        var onLine = found
            .Where(site => source.LineAt(site.TagIndex) == line
                           && string.Equals(site.Event, keyEvent, StringComparison.Ordinal))
            .ToList();

        Assert.True(onLine.Count > 0,
            $"{page}: no @on{keyEvent} on an element starting at line {line}, where a keyboard path "
            + "is registered.");

        var match = onLine.FirstOrDefault(
            site => site.TagText.Contains(snippet, StringComparison.Ordinal));
        Assert.True(match is not null,
            $"{page}:{line} no @on{keyEvent} element there contains '{snippet}'. The entry may now "
            + "be covering a different control; re-check it against the file rather than updating "
            + "the snippet to whatever is there.");

        Assert.True(string.Equals(match!.TagName, tagName, StringComparison.Ordinal),
            $"{page}:{line} is registered as <{tagName}> but is now <{match.TagName}>. Which element "
            + "this is decides whether the disabled attribute refuses anything at all, so re-check "
            + "the entry.");

        return match;
    }
}
