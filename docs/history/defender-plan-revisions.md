# Defender for Endpoint Devices Plan - Revision History

Revisions rotated out of `docs/DefenderEndpointDevices-Plan.md` on 2026-09-25, kept
verbatim. They were 1,036 lines of a 2,911-line plan - 36 percent of a document whose
job is telling an implementer what to build.

**Nothing here is needed to implement the plan.** Every measured fact these revisions
established - the 10,000-row cap, the absent continuation cursor, the 113,102-device
tenant size, the partitioned fetch - is stated independently in the plan body, which was
checked before the rotation rather than assumed.

Read this file for WHY the plan says what it says: the codex findings, the live run that
falsified the first design, and the rebuild it forced.

**The numbering is confusing and is preserved as-is rather than tidied**, because the
revisions cite each other. There are two series. Revisions 1-8 are the first: draft,
three codex rounds, then slices S1, S2, S4, S3 and a review of the finished module. A
second series then restarted at "Revision 3 - the live run" and "Revision 4 - codex
review of the rebuild", continuing to 5. Where it matters, the plan body says "first
series" or "second series" explicitly.

Line citations inside these revisions point at the plan **as it stood when each was
written**, not at the current file. Treat them as historical references; re-read the
current plan before acting on any of them.

---

## Revision 1 - codex review, 2026-09-18

Reviewer: codex, `@azure-openai-eus2-global/gpt-5.5-dzs` at xhigh, standard depth, capability
proof passed. Verdict **sound_with_changes**, three findings. Every finding was re-verified
against the named files before being acted on, per the repo rule that disputes are settled by
reading the file. All three were confirmed and all three are closed. Nothing was rejected; one
was accepted with a recorded correction to part of its reasoning, and that correction is written
here rather than left in a chat log, because only repo files are durable memory.

**Finding 1 (HIGH) - S4 could not report Graph hunting failures with the planned call shape.
ACCEPTED, confirmed.** `GraphTokenClient.PostAsync` is `GraphTokenClient.cs:109-126`, and line 122
is `if (!response.IsSuccessStatusCode) return null;` - status code and body both discarded. The
plan's T7 demanded distinct reasons for 403, 429 and timeout, and S4 planned the call over that
method; those two statements could not both be satisfied. **Changed:** T1 rewritten - the
module-local client now takes base URL and token scope as constructor arguments and is
instantiated twice (Defender host, Graph host), exposing `PostWithStatusAsync` alongside
`GetWithStatusAsync`, both returning document plus status plus sanitized error, with
`TaskCanceledException` mapped to a distinct timed-out result because a timeout is an exception
and not a status. S4 rewritten to use it, with one test per distinct reason. **Route chosen, as
the reviewer asked to be told explicitly:** module-local, **not** a new `PostWithStatusAsync` on
the shared `GraphTokenClient`. The shared route is tidier and is recorded in T1 as the rejected
alternative with its reason - it is shared infrastructure used by three other modules and would
bump the base app version. The sanitizer is still reused: `ExtractGraphError` is
`internal static` (`GraphTokenClient.cs:162`) and is callable from the same assembly with a
zero-line diff to that file. **The versioning call is unchanged and now says so in its own
section: module `1.0.0`, no base app bump.**

**Finding 2 (HIGH) - the one-page fetch quietly narrowed "all devices" to "first N". ACCEPTED on
the substance; one part of the reasoning corrected.** The correctness gap was real and was the
most serious of the three: Scope promised the tenant's devices and an export of the result set,
while T3 fetched one bounded page and deferred paging, so a large result set would have produced
a CSV that looked complete and was not. **Changed:** paging is now in S1, not deferred -
`@odata.nextLink` followed as an absolute URL (which the module-local client can do and the
shared one cannot), `$skip` as the fallback, de-duplication on device `id` because the collection
documents no `$orderby` and `$skip` pages can therefore overlap, and a `MaxDevices` ceiling whose
behaviour is **refusal, not truncation**: no table, no export button, and a message naming the
ceiling. The config field `DeviceFetchLimit` (5000) became `MaxDevices` (20000) with that
semantics; the model has no `Truncated` flag at all, deliberately, so that a partial state does
not exist to be rendered later. Scope, the CSV section, the verification non-vacuity list and
manual checks 14 and 15 were updated to match. **The correction:** the reviewer read old open
question 5 as breaching `.agents/repo-guidance.md` invariant 7. Invariant 7 forbids naming this
environment in **source** as behaviour, and forbids a **safety argument** resting on this
environment's shape; it explicitly requires the opposite of silence about environment facts -
"Where an environment fact is unavoidable, verify it and fail closed rather than assume it." A
plan asking the owner how many devices they have is verification, not assumption, and was not a
breach. It was reworded anyway, because the better answer made the question redundant: Q5 now
asks about ceiling *policy* (refuse or mark partial) and names no deployment, and R1(f) verifies
the actual count on dev. The distinction is recorded so a future reader does not "fix" R1(f) back
out on invariant-7 grounds.

**Finding 3 (MEDIUM) - S1's live proof depended on config S1 does not register. ACCEPTED,
confirmed.** Module config is descriptor-driven: `Components/Pages/ModuleConfig.razor:73` and
`:332` gate the Configuration tab on `module.ConfigFields.Count > 0`, and the page is
`/module-config/{ModuleId}` for catalogued modules. With no descriptor until S2, there was no
supported way to enter the Secret ID, so S1's "live tasks, blocking S2" were unreachable.
**Changed:** the reviewer's second option, with the reason recorded. S1 is explicitly
call-free and says why; the reconnaissance moved into a new gate **R1**, sited between S2 and S3,
run on dev after S2 ships `EnabledByDefault = false` and fail-closed. The first option - a
config-only descriptor in S1 - was not taken because it would register a route with no page and
`tools/validate-module-package.ps1` requires a razor page carrying `<ModuleVersion />`
(`validate-module-package.ps1:296-297`). S1 was also given the sentence that makes the ordering
safe: nothing in S1's code depends on a reconnaissance answer, because each open question already
has a documented default and a conservative fallback.

**Unchanged by this revision:** `Status: Draft`, the API recommendation and its evidence, the
permission ask, the `CanBeOnboarded` literal, the field mapping and what is not obtainable, and
all six open questions except Q5's wording. No source file was touched; this plan file is still
the only artifact.

## Revision 2 - codex round 2, 2026-09-18

Same reviewer and configuration. Verdict on Revision 1: **unsound**, two findings. Both were
re-verified against this file before being acted on; both were confirmed exactly as described,
and both are closed here. Revision 1's record above is left intact - it is the history of what
round 1 changed, and round 2 upheld two of its conclusions.

**Carried forward from round 1, independently confirmed by round 2:** finding 1 is closed
(`PostAsync` collapses non-success to null; `ExtractGraphError` is `internal static` and reachable
without editing that file; the module-only version call is sound **provided
`Services/GraphTokenClient.cs` is not touched**). Finding 3's validator reasoning is confirmed -
`validate-module-package.ps1` accumulates `CAT002`, `PAGE001` and `PAGE009` through `Add-Issue`
and exits non-zero, so the config-only-descriptor alternative really was closed. **And the
invariant-7 rebuttal recorded in Revision 1 was upheld:** asking about or verifying an unavoidable
environment fact is permitted; resting a safety argument on this deployment's shape is not. That
reasoning stays in this file deliberately.

### Finding A (HIGH) - the `$skip` fallback could still export a silently partial list. ACCEPTED

Finding 2 was closed on the main path only. Revision 1's T3 step 3 read "If no `@odata.nextLink`
is returned, fall back to `$skip` paging until a short page arrives", conceded that pages "can in
principle overlap or gap", and then concluded that de-duplicating on `id` was "the only thing
making the `$skip` fallback trustworthy". **That conclusion does not follow, and the sentence was
wrong.** The corrected reasoning is now written into T3 under "Why `$skip` is not used" rather
than quietly deleted, because the next person to reach for `$skip` needs to meet the argument:

- de-duplication removes overlaps and **cannot detect a gap** - skipped devices leave no trace in
  the accumulated set, so the loop's own data cannot tell "all of them" from "the ones I saw";
- **a short page is exactly what a moved window produces**, so "page until a short page arrives"
  terminates identically on a complete collection and a truncated one;
- `$skip` is order-dependent and this collection documents no `$orderby`.

**Changed:** `$skip` is removed from the design entirely and recorded as a named rejected
alternative; reintroducing it requires a cited source that `$skip` paging is stable for this
endpoint, not an argument from de-duplication. De-duplication is demoted to cheap insurance
against a duplicate row inside a cursor chain and is explicitly labelled **not a completeness
argument**. The request-count sentence now derives from the page size instead of restating a
stale "20 requests". A guard test was added to S1 - a full page carrying no `@odata.nextLink` must
refuse - together with the note proving it bites: against the committed design (`b12a7b1`) that
same stubbed response falls into the `$skip` fallback and renders a list, so the test fails before
this change and passes after. It is the executable equivalent of the gapped-`$skip` scenario the
reviewer asked for, which becomes unreachable once `$skip` is gone.

**The completion rule, in full, so it does not have to be reconstructed:** a run is complete only
on a positive proof of exhaustion, and absence of evidence is never proof.

- **P1 - single request.** Asked for `$top = N`, fewer than N rows returned, no
  `@odata.nextLink`. Complete.
- **P2 - cursor chain.** Every continuation followed an `@odata.nextLink` as an absolute URL, and
  the final response carried none and was short in the P1 sense. Complete.
- **Refuse** when a response returns exactly the `$top` it asked for and carries no
  `@odata.nextLink`; when the next page would exceed `MaxDevices`; or when any request in the
  chain fails. No table, no export button.

A full page with no cursor is byte-identical whether exactly N devices exist or the server capped
us, so the module refuses rather than guessing. That is why it cannot silently under-report: the
only route to a rendered table is a proof, and every ambiguous state exits through the same
refusal the ceiling already uses.

One dependency this exposed and did not paper over: **whether this endpoint emits
`@odata.nextLink` at all is undocumented** - the only mention on the Defender API pages names
Microsoft Graph. So **R1(g)** was added: probe `$top=1` against a filter matching more than one
device, with both branches pre-decided (cursor present, P2 applies and multi-page ships; cursor
absent, single-request at `$top = min(MaxDevices + 1, 10000)` with the full-page refusal, and this
file revised to say so). No multi-page behaviour ships that R1(g) has not observed. Assumption 8
was rewritten from the old `$orderby` note to state the paging contract and the one inference P1
rests on.

### Finding B (MEDIUM) - stale references to the pre-R1 slice order. ACCEPTED

Revision 1 created the R1 gate but left five sentences still assigning live proof to S1 - Known
Failure Class 4, and two rounds of edits is how it got in. All five are repointed: the
endpoint-coverage proof to R1(a), the JSON/filter casing to R1(b), server-side `startswith` to
R1(d), and `ipAddresses` to R1(e), in both the body and the Assumptions list. A sixth was found
on the sweep the reviewer asked for and fixed too - Assumption 6 said `startswith` was "tested
live" without naming the gate. S1 now states positively that **every one of its tests runs against
a stub and it makes no live call**, and a missing blank line that ran a paragraph into a bullet
list was repaired.

**Unchanged by this revision:** `Status: Draft`, the API recommendation, the permission ask and
the least-privilege analysis, the `CanBeOnboarded` literal, the field mapping, the descriptor, and
all six open questions. The versioning call is unaffected - nothing here touches a shared file -
so module `1.0.0` with no base app version bump still stands. No source file was touched; this
plan file is still the only artifact.
# Revision 3 - codex round 3, CONSENSUS REACHED

Reviewer: codex / `@azure-openai-eus2-global/gpt-5.5-dzs` / xhigh / standard, 2026-09-18.
Capability proof passed. Verdict **sound_with_changes**, one LOW finding, now fixed.

**Both round 2 findings confirmed closed.** `$skip` is no longer a live path; T3 and S1 refuse on
full-page-without-cursor, on `MaxDevices` exceedance and on any request failure; and the reviewer
independently confirmed the S1 guard test is **not vacuous** by comparing against the design at
`b12a7b1`. The stale S1 live-proof references are gone in substance - body text assigns live proof
to R1, and S1 says stub-only.

**R1(g) was explicitly upheld as an adequate way to handle the undocumented paging contract**,
and the reason is worth keeping because it is the answer to "shouldn't this be resolved before the
owner creates the app registration?": it cannot be. Observing the contract *requires* the
registration. What makes pre-deciding both branches sufficient is that no multi-page behaviour
ships that R1(g) has not observed, and every ambiguous no-cursor state refuses rather than
rendering or exporting a partial list. The uncertainty is bounded by refusal, not by hope.

**The one finding (LOW):** Assumption 7 still carried `PageSize 1000` and a fixed 20-request run
from before the revision 2 paging design removed the fixed page size - the stale-reference failure
class again, and the second time it has appeared in this plan across three rounds. Fixed: the
request count is now expressed in terms of the chosen `$top`, the service-observed page size and
the branch R1(g) records, with the obsolete number named as obsolete so it cannot be carried into
code.

**This plan has reached codex consensus.** It stays `Status: Draft` because consensus is not the
same as approval here: the owner still has to answer the six open questions - question 1 above all,
since it decides whether S4 exists and who must grant consent - and create the app registration,
which is theirs to do and which no amount of planning can substitute for.
# Revision 4 - S1 implemented, and a tension S1 surfaced, 2026-09-19

S1 landed call-free as designed. Four corrections and one open tension, all found by implementing
it rather than by reading it.

**The tension, and S2's author must read this before starting.** R1(g) states that **no multi-page
behaviour ships that R1(g) has not observed**, and R1 is gated between S2 and S3. But S1's own
bullet list mandates the cursor-chain code and its tests, and **S2 ships a reachable page** - so
cursor-following becomes reachable one slice before the gate meant to authorise it. S1's section is
the more specific instruction and the ambiguous-state refusal bounds the risk either way, so the
code shipped; but the ordering is genuinely inconsistent as written. The likely resolution is to
state in S2 that the module ships `EnabledByDefault = false`, so "reachable" means "reachable by
the owner on dev" - which is where R1 runs anyway. **That is a proposal, not a ruling.**

**T6.1 asks for escaping tests over "the device name box", which does not exist** anywhere in the
design - S2's control list has only two filters, onboarding status and Windows-only. The escaping
tests were written against the onboarding-status value, the only operator-supplied string that
reaches a filter.

**Two additions beyond the plan text, made by the implementer and flagged as theirs.** First,
`GetWithStatusAsync` refuses an absolute continuation URL on a different host or over plain HTTP,
and refuses it **before acquiring a token** - the absolute-URL path exists to follow a link out of
a response body, and following an arbitrary host would hand this registration's bearer token to
whatever that body named. Second, a run stops after 100 requests, derived from the documented
per-minute limit, and **refuses** rather than returning what it has. Both are tested.

**One pinned shape was deviated from, additively.** The plan pins `(JsonDocument?, HttpStatusCode,
string?)`. The implementation returns that plus a `TimedOut` flag, because a 3-tuple's only free
slot is the status, and mapping a client-side timeout onto 408 makes it indistinguishable from a
service-issued 408 - the collapse T7 forbids. `ServiceIssued408_IsNotReportedAsAClientSideTimeout`
pins the distinction.

**Confirmed against the plan:** `Program.cs` is in S1 and the plan pre-clears it as additive module
registration, so no base app bump - `ExchangeAdminWeb.csproj`, `Modules/ModuleCatalog.cs` and
`Services/GraphTokenClient.cs` are all unchanged. `DiscoveryEnrichment` state ships in S1 as
`NotAttempted`; S4's five per-device enrichment properties deliberately do **not**, because S4 is
droppable on the owner's question 1 and they would be dead code.

Everything downstream still needs the owner: the app registration, question 1, and R1(g) - which
cannot run before the registration exists, as Revision 3 upheld.

# Revision 5 - S2 implemented, and the R1(g) tension resolved, 2026-09-21

S2 landed: the descriptor, `Components/Pages/DefenderEndpointDevices.razor`, the catalog-count and
alias assertions, four house-style descriptor facts, and a click-gating registry entry written with
the page rather than bolted on afterwards. Two owner answers arrived with the slice and both change
what was written above.

**Owner answer to Q6: the display name is "Defender for Endpoint Devices".** Shipped verbatim, with
route `defender-endpoint-devices` and section-access alias `DefenderEndpointDevices`. Q6 is closed.

**Owner answer to Q1: discovery sources ARE wanted - a direct request from senior leadership.** So
`ThreatHunting.Read.All` will be granted, consent must be done by a Privileged Role Administrator or
a Global Administrator (see "Admin consent"), and **S4 is no longer droppable**. The consequence for
this slice: `IncludeDiscoverySources` ships in S2 as the descriptor's third config field, and the
`GraphDelineaSecretId` description names both `Machine.Read.All` on WindowsDefenderATP and
`ThreatHunting.Read.All` on Microsoft Graph. Deferring the switch was correct only while S4 might
never exist; that reasoning is superseded and the field is live now, so the operator can turn the
enrichment off on a deployment whose registration does not yet hold the grant. Q1 is closed.

**The Revision 4 tension is moot - but NOT for the reason Revision 4 proposed.** Revision 4 offered
`EnabledByDefault = false` as the likely resolution: cursor-following becomes reachable in S2, one
slice before the R1(g) gate meant to authorise it, and shipping the module disabled would mean
"reachable" only means "reachable by the owner on dev". **That cannot be what resolves it, because
`EnabledByDefault = false` is already required independently** - the Constitution's optional-module
rule mandates it and `tools/validate-module-package.ps1:202` raises CAT004 when a descriptor omits
it. A property the module was going to carry anyway resolves nothing; it would have been the same
with or without the tension.

**The real answer is that the shipped paging is reactive, not speculative.**
`DefenderEndpointDeviceService` reads `@odata.nextLink` out of the response body
(`TryReadPage`, `DefenderEndpointDeviceService.cs:384-385`), stops when it is absent
(`:233`) and follows it only when the service emitted one (`:267`). There is no code that
*assumes* a cursor exists and no request that would be issued on a guess. R1(g)'s two pre-decided
branches are therefore not two implementations of which one is unproven: they are one piece of code
dispatching on what actually came back. The no-cursor branch is the `:233` exit, the cursor branch
is the `:267` exit, and the ambiguous state between them - a full page with no cursor - exits
through the same refusal the ceiling uses. So "no multi-page behaviour ships that R1(g) has not
observed" is satisfied by construction: the multi-page path cannot execute unless the service has
already demonstrated the contract in the response that triggers it. R1(g) remains worth running -
it records the observed page size, which is what turns the request budget in T3 from an unknown
into a number - but it is no longer a gate on correctness.

Reachability is separately gated by three independent owner acts, none of which this slice
performs: enabling the module in Admin Settings, granting a group the `DefenderEndpointDevices`
section-access alias, and entering the Secret ID on the module's own config page. That is defence
in depth, not the argument; the argument is the paragraph above.

**Click gating: registered, not deferred.** The page is in `ClickGateRegistry.Pages` rather than
`NotYetConverted`, which was cheap because the page was designed for it: one operation, one
in-flight flag (`isLoading`), one page-wide predicate (`IsBusy`), no staged-confirmation state, no
banner to dismiss, every click target a `<button>`, and no keyboard handler at all - so
`ExcludedFields`, `ExemptControls`, `NonButtonTargets`, `KeyboardPaths` and `HarmlessKeyboardPaths`
are all legitimately empty and the two hardest assertion families are vacuous rather than waived.
Two DOM-synced controls (the onboarding-status `<select>` and the Windows-only checkbox) are
registered with their verbatim `disabled="@IsBusy"` attributes, and both filters are registered as
`CapturedAtEntry` snapshot obligations.

**One deviation from the S2 sketch, and the reason for it.** The sketch had `OnInitializedAsync`
end with `isLoading = DeviceService.IsAvailable;` so the first paint shows a spinner - the shape
`ServiceHealth.razor:340-341` uses. That shape cannot be registered: `isLoading` is the page's
in-flight flag, and `ClickGateTests.EveryRegisteredFlagIsLoweredInAFinally` and
`SingleFlightHandlersRaiseAFlagBeforeTheirFirstAwait` both require every raise of a registered flag
to sit inside a try whose finally lowers it, before that handler's first await.
`OnInitializedAsync` has neither. ServiceHealth gets away with it only because it is listed in
`NotYetConverted` and nothing checks it; here it would be a stuck-flag raise, and under a page-wide
predicate a stuck flag does not grey one button, it deadens the page. The first-render spinner is
therefore keyed on `result == null` instead, which costs nothing: the deferred load starts from
`OnAfterRenderAsync` on that same render, and the branch strictly contains the sketch's
`isLoading && result == null` condition.

**Confirmed against the plan and unchanged by this slice:** module `1.0.0`, and **no base app
version bump** - `ExchangeAdminWeb.csproj` stays at 2.21.1 / 2.21.1.0. `Program.cs` was already
complete from S1 (the named `HttpClient` and the singleton registration) and was not touched.
`Services/GraphTokenClient.cs` was not touched. `tools/validate-module-package.ps1` was NOT run
against this change and must not be reported as passing: it requires `-PackagePath` and validates a
contributed-package layout, which an in-repo module slice does not produce.

**Still outstanding, all of it the owner's:** the app registration itself, the two consents, the
Delinea secret and its Secret ID, then R1 on dev - and Q2, Q3, Q4 and Q5 remain unanswered.
# Revision 6 - S4 implemented, 2026-09-21

Discovery sources shipped. Three corrections and one flagged unknown, all found by implementing it.

**The enrichment warning belongs ABOVE the table and S2 shipped it below.** The Field mapping
section says "the page says why above the table"; S4 moved it. Recorded so it is not helpfully
moved back.

**A 200 with an unreadable body is a second door into the malformed state.** The plan anticipated
malformed only as "no results collection". Without the extra branch the operator reads "Microsoft
Graph rejected the advanced hunting query (200 OK)", which is a contradiction in one sentence.
Tested both ways.

**Eleven distinct reason strings, not the eight the plan enumerated** - the code has three more
doors: credentials unavailable, no devices to enrich, and a listing that refused. All eleven live
in one type so the model's refusal factory and the service cannot drift apart, and the distinctness
test runs seven of them through the real service rather than asserting over constants. It also
asserts **no reason names `Machine.Read.All` or `api.securitycenter`** - those belong to the device
list's own 403, and borrowing that text would send an operator to check a permission that is
demonstrably working.

**Unverified and R1-class: whether `DiscoverySources` serialises as a JSON string or a JSON array.**
No Learn page settles it and WebSearch is blocked in this environment. Both shapes are handled and
joined with the house separator, so the module is correct either way - but **reading only the string
shape would silently blank the one column senior leadership asked for**, which is why it is called
out rather than left to the merge code. Add it to R1's list if that gate is still open.

**One judgement call, stated plainly:** the hunting call reads the Delinea secret a second time
within a run rather than sharing the listing's read. The service is a singleton, so sharing would
mean holding a credential in a field - which is how a rotated client secret keeps failing until the
app pool recycles. The cost is one extra Secret Server round trip per run.

# Revision 7 - S3 implemented, and the R1(e) decision taken without R1, 2026-09-21

The CSV export shipped. **S3 ran BEFORE R1**, which the plan gates it behind, and the rest of this
section is the record of why that was safe and of the one decision it forced.

**Only R1(e) touches S3, and its answer was pre-empted deliberately.** R1's other questions are
about the listing - whether `CanBeOnboarded` devices come back at all, the casing of
`onboardingStatus`, the `osPlatform` values present, server-side `startswith`, how close this
tenant runs to the ceiling, and the paging contract. Not one of them changes a column. R1(e) does:
it asks whether `ipAddresses` is populated on list responses, and the plan says that if it is not,
`IpAddresses` and `MacAddresses` leave the CSV before S3 writes it.

**Decision taken with the slice, recorded here so it does not read as an oversight: both columns
are IN.** It was not the owner's call and is not presented as one - it is the implementing slice's,
and R1(e) can still overturn it. The reasoning is asymmetric cost. If R1(e) comes back empty,
removing two columns from the header list and two cells from the projection is a five-line revision
with a test to update. If they had been
left out and R1(e) comes back populated, the module would have shipped, been documented in S5, and
been used - without the MAC addresses that are the only way to identify a discovered device that
has no name yet, and which the machines API hands over for free alongside the IPs it was asked for.
The columns are written from `ipAddresses[].ipAddress` and distinct `ipAddresses[].macAddress`, and
an absent collection parses to empty rather than throwing (S1), so the empty answer costs two blank
columns until someone deletes them. **If R1(e) comes back empty, delete `IpAddresses` and
`MacAddresses` from `BuildCsv`'s header and projection and from `ExpectedHeader` and the indices in
`DefenderEndpointDevicesCsvTests`, and record it as Revision 8.**

**Twenty-seven columns, in the plan's order, unchanged from the "CSV export" table.** The header is
asserted verbatim in `DefenderEndpointDevicesCsvTests.ExpectedHeader` rather than derived from the
code, so a column moved in the page has to be moved by a human who can check it against this file.

**`BuildCsv` takes a second argument the plan's bullet did not name, and it is load-bearing.** The
bullet says "a `static internal` method taking the row list". It takes the row list AND the run's
`DefenderDiscoveryEnrichmentState`, because the five enrichment columns cannot be rendered from a
device: an empty `DiscoverySources` means "this device has no value" on a succeeded run and "NO
device has one, the query never ran or failed" on any other, and only the RUN knows which. That is
the same reason Revision 6 put the state on the result rather than on the device. Passing it in is
also what lets the two readings be proven apart by a test with no page instance.

**How a non-succeeded enrichment reads in the file, and the one way it deviates from the screen.**
Any state but `Succeeded` writes `(unavailable)` into all five columns, from
`DefenderEndpointDeviceService.EnrichmentUnavailable` - the same constant the page uses, so the file
and the screen cannot describe the same run differently, and a test asserts the CSV cell equals
`DescribeEnrichmentCell`'s output for the same state. The deviation: on a **succeeded** run a
genuinely empty value is written blank, where the screen shows "-". `CsvExport` neutralises a
leading `-` into `'-` (`Services/CsvExport.cs:50`), so the screen's placeholder would reach a
spreadsheet as a literal apostrophe-dash, and it would be the only column in the file carrying a
placeholder at all. Nothing is lost: once `(unavailable)` owns the failed-run meaning, a blank can
only mean the run worked and that device had no value. The direction that matters - a blank
standing in for a run that never executed - cannot happen.

**Two formatting decisions the plan's table did not specify.** The timestamp columns are NAMED
`FirstSeenUtc` and `LastSeenUtc`, so they are written in UTC with the `u` format - sortable, and
always invariant-culture, so an exported file does not change shape with the host's locale. The
table above them deliberately still shows local time, because that is what the operator reading the
screen wants. `IsAadJoined` renders lowercase `true`/`false`, matching `NamedLocations.BuildCsv`.

**The export is offered from the complete branch and nowhere else, and that is now asserted.** A
refusal carries zero devices by construction, so an export offered from that state would write a
header-only file - indistinguishable from "no devices matched" to whoever opens it, and the refusal
banner that explained it does not travel with the file.
`DefenderEndpointDevices_OffersNoExportFromTheRefusalBranch` brackets the single button between the
first element of the complete branch and the device table's closing tag, which is a span entirely
inside that branch; an anchor below the branch alone would also be satisfied by a button placed
after the whole if/else chain. Honest limitation, stated as everywhere else in this file: that is
textual position in the markup, not a render.

**Click gating, updated with the slice rather than after it.** `isDownloadingCsv` is the second
member of `IsBusy`. The button carries two registry obligations a CSV control on a page-wide
predicate attracts: an `AnnotatedControl` at line 172, because `ExportableDevices.Count == 0` is a
precondition a mechanical rewrite to the bare predicate would delete, and a `RaiseAfterEarlyReturn`,
because the raise must stay below the empty-set return that no `finally` covers - on a page-wide
predicate that raise would not grey one button, it would deaden the page. `ExpectedLineCount` moved
430 -> 612 and the two `DomSyncedControls` moved 65/75 -> 66/76, because the page gained
`@inject IJSRuntime JS` above them.

**Two stale line references in this file, found by following them.** S3's bullet cites
`DhcpAuthorization.razor:174` as the `BuildCsv` pattern; it is at **187**. The "CSV export" section
cites `BlockedSenders.razor:236-256` for the download-and-audit shape; that method now runs
**230-256**. Both patterns are still correct - only the coordinates drifted. Recorded rather than
silently corrected, because this file is the thing being read by the next slice.

**Confirmed against the plan and unchanged by this slice:** module `1.0.0` in
`Modules/ModuleCatalog.cs:760`, and **no base app version bump** - `ExchangeAdminWeb.csproj` is
byte-identical (`git diff --stat` empty) and stays at 2.21.1 / 2.21.1.0. `Program.cs`,
`Services/GraphTokenClient.cs`, `Services/DefenderEndpointDeviceService.cs`,
`Services/DefenderApiClient.cs` and `Models/DefenderDeviceModels.cs` were all untouched: S3 is the
page, its tests, and the click-gate registry entry.

**Still outstanding, all of it the owner's:** the app registration, the two consents, the Delinea
secret and its Secret ID, then R1 on dev - including R1(e) above and the `DiscoverySources`
serialisation question Revision 6 added. S5 (documentation and the README section) is the only slice
left, and it is not this one's: S3 was the last code slice.

# Revision 8 - codex review of the finished module, 2026-09-21

Reviewer: codex, `@azure-openai-eus2-global/gpt-5.5-dzs`, effort xhigh, against `bb37bc9`, with the
suite at 2923 passed / 0 failed / 3 skipped. Capability proof passed. Verdict **unsound**, two
findings, both fixed here. Full result in `.agents/review/q8-module.result.json`.

**What it cleared, and that is the half that mattered.** No path renders or exports a short device
list as complete, and no page or CSV blank can mean a failed enrichment run - T3 and T7's central
promises hold, and neither was re-litigated here. The defect was narrower and lay entirely on the
enrichment side of a run whose first half had already succeeded.

## Finding 1 (HIGH), verified and fixed - a hunting-side exception took a COMPLETED list down

`GetDiscoveryEnrichmentAsync` handled a null client and a status-bearing `DefenderApiResult` and
nothing else, and two throw paths reached it. `BuildClientAsync` throws `InvalidOperationException`
three ways for a missing, unreadable or incomplete Delinea secret. `DefenderApiClient` throws when
the token request comes back non-2xx, and its send catches only `TaskCanceledException`, so a
transport failure or an unreadable token body escapes it too. Either one reached the page's own
catch, which sets `result = null` - so a device list that had already PROVED itself complete was
replaced by a page-wide failure and no table.

**The call site's own comment convicted the code.** It reads "its failure is independent of the
listing's success ... It must never take the page down, and it must never leave those columns
merely blank." That was the contract; the code did not honour it. The reviewer is right and the
finding is accepted without qualification.

**The fix: two narrow try blocks, one per call that leaves this process.** The factory call and the
POST are wrapped separately, so each converts into its own reason and neither can borrow the
other's sentence. A factory throw is `NotAttempted` with the existing `CredentialsUnavailable` -
nothing was sent, which is exactly what that constant already says. A POST throw is `Failed` with a
new constant. Both reasons are FIXED strings, never `ex.Message`: the credential messages name a
Secret ID and a Secret Server condition and the token message names a sign-in status, and
`DefenderApiClient` already refuses to echo an auth body for precisely that reason.

**What is caught, and what deliberately is not.** The catches are
`catch (Exception ex) when (IsHuntingSideFailure(ex))`, and that predicate names five types:
`InvalidOperationException`, `HttpRequestException`, `JsonException`, `KeyNotFoundException` and
`TaskCanceledException`. Each is a path that exists today, not a defensive guess - the doc comment
on the predicate records which code throws which. **A blanket `catch (Exception)` was rejected**: it
would also swallow a defect in this module's own parsing and merging and report it to the operator
as a Graph failure, and a bug that greys five columns and blames Microsoft is a bug nobody ever
finds. For the same reason the read of the response body - `TryReadHuntingResults` and the
`MalformedResponse` branch - sits OUTSIDE both try blocks. Anything not on the list still escapes
and still takes the page down, which is what an unexpected exception should do.

`TaskCanceledException` is on the list only because no `CancellationToken` is plumbed through this
call chain, so it cannot be a caller's cancellation being swallowed; the predicate's remarks say so,
and say to revisit that entry if one is ever added. On the POST side the client already converts a
cancellation into the `TimedOut` reason, so that entry bites only on the Secret Server read.

**One new reason constant, `DefenderDiscoveryReasons.SendFailed`, because none of the nine fitted.**
The existing reasons are either not-attempted states or answers off the wire; this is the case where
the module reached for Graph and got no status at all, so there is nothing for
`DescribeEnrichmentFailure` to read. Reusing `Unauthorized` would name a 401 that did not happen and
send the operator to check a client secret that may be fine; reusing `TimedOut` would name a timeout
that did not happen. It is called `SendFailed` and NOT `RequestFailed` on purpose:
`DefenderDeviceListOutcome.RequestFailed` already owns that word in this module and means the DEVICE
LIST failed, which is the one thing this reason promises did not happen.
`EveryEnrichmentReasonIsADifferentSentence` now collects twelve reasons rather than eleven and stays
green; collapsing the new one onto `TimedOut` fails it (probe M6).

## Finding 2 (MEDIUM), fixed - the query guard proved the safe text appeared, not that it ran

`ThreatHunting.Read.All` is scoped to the whole hunting schema, so the permission cannot narrow this
module to `DeviceInfo` and the request body is the only boundary there is. The guard asserted only
that the body CONTAINED `"Query"` and CONTAINED the constant, which a broken implementation could
satisfy while posting broader KQL as the real `Query`, or by appending to it.

The body is now parsed as JSON and the root `Query` property must EQUAL
`DefenderEndpointDeviceService.HuntingQuery` exactly. Two further assertions close the escape hatch:
no second property whose name contains "query" in any casing, and - strictly stronger, and not
redundant, because `runHuntingQuery` also accepts `Timespan` - no second property at all. Three
mutations prove the three halves independently (M3, M4, M5), and the first of them, appending
`| union DeviceNetworkEvents` to the posted query, PASSES the assertions this revision replaced.

## What was deliberately not changed

- **The page.** `Components/Pages/DefenderEndpointDevices.razor` is untouched. Its catch is correct
  for what it is - a listing failure should clear the result - and the defect was that enrichment
  exceptions were reaching it at all. Fixing it there would have been fixing the symptom one layer
  too late, and would have required the page to distinguish two failure sources it cannot see.
- **The version.** Module stays `1.0.0`, no base app bump; `ExchangeAdminWeb.csproj` is
  byte-identical (`git diff` empty) at 2.21.1 / 2.21.1.0.
- **`Services/DefenderApiClient.cs`.** Widening its catch was considered and rejected: it is the
  layer that must NOT decide what a failure means to an operator, and turning its throws into
  results would have changed behaviour for the inventory call as well - the one call that SHOULD
  take the page down when it fails.

## Guard proof - six probes, each attributable to one test

| Probe | Mutation | Test that failed |
| --- | --- | --- |
| M1 | remove the factory catch | `AHuntingClientFactoryThatThrows_LeavesTheCompletedListStandingAndSaysWhyInFixedWords` |
| M2 | remove the POST catch | `AHuntingTokenRequestThatFails_LeavesTheCompletedListStandingAndSaysWhyInFixedWords` |
| M3 | append `\| union DeviceNetworkEvents` to the posted query | `TheHuntingCallIsOnePostToRunHuntingQueryOnTheGraphHostCarryingTheConstantKql` |
| M4 | add a second `QueryOverride` property carrying the wider KQL | same |
| M5 | add a `Timespan` property beside a correct `Query` | same |
| M6 | collapse `SendFailed` onto `TimedOut` | `EveryEnrichmentReasonIsADifferentSentence` |

Every probe failed exactly one test with 91 of 92 still passing, so each is attributable. Every
restore was a file copy followed by `touch` and a SHA256 comparison against the pre-mutation hash -
no `git checkout --` at any point.

## Verification

`dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental` 0 errors / 23 warnings;
`dotnet test ExchangeAdminWeb.slnx` 2925 passed / 0 failed / 3 skipped, +2 on the 2923 baseline and
the two new tests account for it exactly; `dotnet format ExchangeAdminWeb.slnx --verify-no-changes
--no-restore` exit 0; `git diff --check HEAD` exit 0; `tools/Test-AsciiOnly.ps1` exit 0.

## Still outstanding, all of it the owner's, and unchanged by this revision

The app registration, the two consents, the Delinea secret and its Secret ID, then R1 on dev -
including R1(e) and the `DiscoverySources` serialisation question - and the manual acceptance
checklist. Q2, Q3, Q4 and Q5 remain unanswered.

## Revision 3 - the live run, and the rebuild it forced, 2026-09-21

The owner created the app registration, granted and consented both permissions, created the
Delinea secret, entered the Secret ID, deployed to dev and clicked Load. The module authenticated,
reached the real service, and refused with an empty page:

> The Defender for Endpoint API returned exactly the 10000 devices this request asked for and gave
> no continuation link... No devices are shown and no export is offered... Narrow the filters.

Owner, verbatim: *"we have well over 40000 devices. we need this to work in our environment. I
will not approve any code that limits what this can do."* And: *"anything that is obtainable via
Microsoft's portal needs to be obtainable here. No compromises."*

The refusal was correct - the run genuinely could not prove it had seen every device - and useless.
"Narrow the filters" named an action the operator could not take, because the only server-side
filter on the page was one dropdown.

### The two R1 questions this run answered

**R1(g) - the paging contract. ANSWERED: there is no cursor.** `GET /api/machines` returned exactly
the 10,000 rows the request asked for and carried no `@odata.nextLink`. The endpoint has no
continuation token. The branch R1(g) decided in advance was "the module stays single-request", and
that branch is now known to be **unusable on a real tenant**: a single request can return at most
10,000 rows against an inventory of more than 40,000, so the single-request design can never
produce a provable answer here. R1(g) recorded the fact; this revision replaces the design that
fact invalidates.

**R1(f) - does the result set approach `MaxDevices`? ANSWERED: it exceeds it.** The descriptor's
default of 20,000 was below the size of the tenant, so even with paging solved the first load would
have refused on the ceiling. The default is raised to 100,000, which is what the ceiling was always
for - a runaway stop on what one operator's circuit will hold in memory, not a statement about how
many devices may exist.

The remaining R1 questions - (a), (b), (c), (d), (e) - are **still unanswered**. Nothing in this
revision depends on any of them: (c) and (d) are why the "Any Windows" platform choice stays
client-side, and (b) is why the parser still reads properties case-insensitively.

### The design: partition the QUESTION, not the answer

Paging divides an answer the service has already computed; with no cursor there is nothing to
divide. So the module divides the question instead.

The inventory is partitioned on `lastSeen` into parts that are **disjoint** and together
**exhaustive**. Each part is asked for on its own. A part that comes back under the cap has proved
itself complete by the unchanged P1 rule. A part that comes back AT the cap has proved nothing, and
is split in two and asked again. The answer is the union of the parts that proved themselves.

Why this is a proof where `$skip` was not - the argument Revision 2's Finding A rejected `$skip`
on, and the reason this is not the same shape:

- Nothing depends on the service's ordering. Each part carries its own server-side predicate;
  two sibling parts are `lastSeen lt X` and `lastSeen ge X` for one literal X. No device satisfies
  both. No device with a `lastSeen` value satisfies neither.
- Nothing depends on a stable window. Each part is an independent question with a fixed answer set.
- Each part proves its own completeness positively, by P1: it asked for N and got fewer than N,
  with no continuation link. There is no "we probably saw everything".
- **The refusal machinery is unchanged.** A part that cannot be divided, a run that exhausts its
  request budget, a failed request, or a breached ceiling all refuse and carry zero devices. What
  changed is that the common case no longer reaches any of them.

**`lastSeen` is filterable with range operators, and that is documented rather than assumed.**
Learn's List machines page names `lastSeen` in the `$filter` list for this collection, and the
OData samples page carries a worked range example on this exact endpoint:
`GET /api/machines?$filter=lastSeen gt 2018-08-01Z`. The literal is emitted unquoted, in UTC, at
the tick precision the service's own samples use.

**The devices with no `lastSeen`.** A null satisfies neither `ge` nor `lt`, so a partition built
only from intervals silently omits them - the exact failure this design exists to prevent. Two
things stop it:

1. While the root part needs no dividing it carries **no `lastSeen` clause at all**, so those
   devices are in the one request like everyone else. A small tenant costs one request and the
   question never arises.
2. The moment the root has to be split, a separate part asking `lastSeen eq null` is queued -
   **unless the operator set a Last seen window**, in which case devices with no Last seen value
   are outside what they asked for and adding them would answer a different question.

`lastSeen eq null` is core OData v4 and this is an OData v4 surface, but unlike `lastSeen gt` it
has no worked example on this collection. If the service rejects it with a 400, the run **refuses**
and the refusal names the one action that makes the question answerable: set a Last seen window, at
which point the null case is no longer part of what was asked.

**Parts are visited in ascending time order.** A device that reports in mid-run moves FORWARD in
`lastSeen`, so visiting low parts first means it can only move into a part not yet read. The stack
is pushed high-then-low so the low child pops first; the no-`lastSeen` part is pushed last so it
pops first of all, and a device that acquires its first `lastSeen` mid-run lands in the final,
unbounded-above part. This rests on `lastSeen` being non-decreasing for a device, which is what
"last seen" means - not on anything about this environment.

**Termination, and the budget.** The split point is the median of the timestamps the ambiguous
response actually returned, restricted to values STRICTLY INSIDE the part. Strictly-inside is the
whole termination argument: both children are strictly narrower than the parent, widths are whole
numbers of ticks, so the recursion cannot descend forever. A part with no eligible value cannot be
divided and refuses. The split point comes from the data rather than from arithmetic on the clock
because bisecting wall-clock time between an epoch and now would spend a dozen requests walking
down to the few days most devices were last seen in; the data lands the split in the middle of the
population. Balance is not guaranteed - the returned rows are an arbitrary subset - but correctness
never depended on balance, only the request count does.

`MaxRequestsPerRun` rises from 100 to **250**. 100 was chosen for a design that issued one request.
A balanced division of a 100,000-device tenant at 10,000 rows a part is 16 leaves, so 31 requests
plus the no-`lastSeen` part; 250 leaves roughly eight times that headroom for an unbalanced
division while staying a sixth of the endpoint's documented 1,500 calls an hour. It is a STOP for a
partition that terminates but does not converge, not a promise: the endpoint's other documented
limit, 100 calls a minute, is enforced by the service as a 429, which already refuses with its own
operator action.

**Rows from an ambiguous part are not in the answer.** They are counted against the ceiling - they
are real devices that matched - but only keys from parts that proved themselves are promoted into
the union. Nothing is lost, because a child part re-reads them; what is gained is that the answer
is by construction the union of parts that each proved themselves, and a partition whose two
children do not meet loses the boundary device visibly instead of having it already in hand from
the parent.

### Filtering - portal parity

The two hardcoded filters are replaced by the machine record's real fields. Which side each one
runs on is a fact about the API, taken from Learn's List machines page, which names the properties
`$filter` accepts on this collection: `computerDnsName`, `id`, `version`, `deviceValue`,
`aadDeviceId`, `machineTags`, `lastSeen`, `exposureLevel`, `onboardingStatus`, `lastIpAddress`,
`healthStatus`, `osPlatform`, `riskScore`, `rbacGroupId`.

| Filter | Side | Clause, or why not |
| --- | --- | --- |
| Device name starts with | server | `startswith(computerDnsName,'...')` - the one function with a worked example on this collection |
| Onboarding status | server | `onboardingStatus eq '...'` |
| Platform (one value) | server | `osPlatform eq '...'` |
| Platform - Any Windows | **client** | No `osPlatform` value means "any Windows". `startswith` is documented for `computerDnsName` and no other property, and a guessed clause the service accepts while matching nothing is indistinguishable from an empty inventory. R1(d) would settle it; it has not run. |
| Health status | server | `healthStatus eq '...'` |
| Risk score | server | `riskScore eq '...'` |
| Exposure level | server | `exposureLevel eq '...'` |
| Last seen from / before | server | `lastSeen ge ...` / `lastSeen lt ...`, and the partition's root bounds |
| Machine tag | **client** | `machineTags` is in the filterable list, but a tag filter is a collection query (`machineTags/any(...)`) with no worked example here |
| Machine group | **client** | The filterable property is `rbacGroupId`, a numeric id. The name the operator knows, and the portal shows, is `rbacGroupName`, which is not filterable |
| First seen from / before | **client** | `firstSeen` is absent from the filterable list - the one timestamp on the machine resource that is not |

A client-side filter narrows what is SHOWN and never what is FETCHED. The ceiling refusal says so
in as many words, because sending an operator to narrow a filter that cannot reduce the fetch is a
loop with no exit.

The **Windows-devices-only checkbox is gone**, at the owner's instruction. Platform is now one
dropdown among the others and defaults to Any.

### UI

The filter card is a three-per-row Bootstrap grid of labelled controls with the two buttons (Clear,
Load devices) in the last cell, and one help line under the whole card rather than a grey paragraph
beside one control. Client-side filters are marked `(after fetch)` inline in their own labels, so
nothing shoves the layout. No new CSS.

The results header now reports the partition: `N device(s)`, then `M fetched in R request(s).
Ceiling C.` The request count is the visible evidence that the partition ran: above one means the
inventory had to be divided. The range count was in this header until the codex round of
2026-09-21, which read it as implementation detail on screen; it is still carried on the result
type and still asserted by the partition tests.

### On-screen wording

Owner ruling, 2026-09-21, applied to every user-facing string this module owns: **state what
happened and what to do, nothing else.** No design rationale, no self-reference ("this module must
never", "the run could not prove"), no restating the situation twice, no reassurance. The rationale
lives here and in the code comments, where the people who need it look. The distinctness
requirement on the failure reasons is unchanged and still enforced by
`EveryEnrichmentReasonIsADifferentSentence`; the reasons are simply shorter.

The page banner is one line. The refusal banner is a heading and the reason. The detail panel's
paragraph about Active Directory domains is gone - the row is labelled "DNS domain (derived)",
which was the whole of the point.

### Versioning

Module `1.0.0` -> `1.1.0` in `Modules/ModuleCatalog.cs`. **No base app bump**:
`ExchangeAdminWeb.csproj` is byte-identical, verified by `git diff` rather than assumed. Nothing
outside `Models/DefenderDeviceModels.cs`, `Services/DefenderEndpointDeviceService.cs`,
`Components/Pages/DefenderEndpointDevices.razor`, this module's catalog entry and its tests was
touched, apart from this plan and `docs/DefenderEndpointDevices.md`.

### What a real run costs now

A "Windows, can be onboarded" load on a tenant of 40,000+ devices, with the default 10,000-row cap.

The cost is driven by the RATIO of matching devices to the per-request cap, not by the device count,
because each part is divided until it fits under the cap. **Measured, not estimated**: the
43-device fixture against a 10-row cap - a ratio of 4.3, the same ratio 43,000 devices have against
the real 10,000-row cap - costs **12 requests**: one root, one for the no-`lastSeen` part, and ten
across the tree that divides it. A tenant at twice that ratio adds roughly one level, not twice the
requests.

Against the endpoint's documented 100 calls a minute, a run of that size is one sixth of one
minute's allowance, and the hard stop at 250 is eight times the worst division the fixture
produced.

The "Any Windows" choice does not reduce the fetch, so it costs the same as no platform filter;
choosing an exact platform (`Windows11`) does reduce it, and proportionally.

### Guard proof

| Probe | Mutation | Test that failed |
| --- | --- | --- |
| P1 | `lastSeen ge` -> `lastSeen gt` in the high child's clause | `MoreDevicesThanOneRequestCanReturn_AreAllListedByDividingOnLastSeen`, `TwoSiblingPartsMeetExactly_WithNoGapAndNoDeviceInBoth` |
| P2 | never queue the `lastSeen eq null` part | `TheDevicesWithNoLastSeen_AreFetchedByATheirOwnRequestOnceTheRootIsDivided` |
| P3 | queue the `lastSeen eq null` part even when the operator set a window | `AnOperatorSuppliedLastSeenWindow_IsTheRootRangeAndSuppressesTheNoLastSeenRequest` |
| P4 | drop the strictly-inside test in `ChooseSplit` | `ASplitPointIsAlwaysStrictlyInsideItsRange`, `ARangeWithNothingToDivideOn_CannotBeSplit` |
| P5 | push the high child last, reversing the visit order | `ThePartitionVisitsItsPartsInAscendingTimeOrder` |
| P6 | treat the no-`lastSeen` part's 400 as an empty part | `AServiceThatRejectsTheNoLastSeenFilter_RefusesAndNamesTheWindowToSet` |
| P7 | send `machineTags/any(t: t eq '...')` server-side | `TheClientSideFiltersNeverReachTheQuery` |
| P8 | a device with no `firstSeen` passes a `firstSeen` bound | `ADeviceWithNoFirstSeenFailsAFirstSeenBoundRatherThanPassingIt` |

Every restore was a file copy followed by `touch` and a SHA256 comparison against the pre-mutation
hash - no `git checkout --` at any point.

P3's mutation is "push the no-`lastSeen` part unconditionally beside the root", because simply
flipping `HasLastSeenBound` changes nothing: that flag is only read inside the split branch, and
the window fixture completes in one request without splitting.

### Verification

`dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental` 0 errors / 23 warnings;
`dotnet test ExchangeAdminWeb.slnx` 2946 passed / 0 failed / 3 skipped, +21 on the 2925 baseline
and the twenty-one new tests account for it exactly; `dotnet format ExchangeAdminWeb.slnx
--verify-no-changes --no-restore` exit 0; `git diff --check HEAD` exit 0;
`tools/Test-AsciiOnly.ps1` exit 0.

### Still outstanding

R1 (a), (b), (c), (d) and (e), and the manual acceptance checklist. Q2, Q3, Q4 and Q5 remain
unanswered. The design above has been proved against a stub that evaluates the filter it is given;
it has not been run against the live service since the change.

## Revision 4 - codex review of the rebuild, 2026-09-21

Harness: codex-cli 0.154.0, `codex exec --json -s read-only`, model
`@azure-openai-eus2-global/gpt-5.5-dzs` at `model_reasoning_effort=xhigh`. Prompt
`.agents/review/q8-scale.prompt.txt`, verdict `.agents/review/q8-scale.result.json`. Capability
proof passed. Reviewed the uncommitted working tree against HEAD `1ddafab`.

**Verdict: unsound - 1 MEDIUM, 4 LOW. No CRITICAL and no HIGH.**

What it cleared explicitly, which is worth as much as the findings and does not get re-litigated:
sibling parts emit `lastSeen lt` and `lastSeen ge`, so they are disjoint and meet exactly; a null
`lastSeen` is covered on the unsplit root and by the one explicit null part after a split, and an
operator-supplied window suppresses that part correctly; rows from an ambiguous parent are counted
against the ceiling but not promoted into the answer; only a `Complete` result renders or exports
rows. Termination is fail-closed rather than unbounded - `ChooseSplit` returns only timestamps
strictly inside the range, and a capped part whose rows share one timestamp or carry none refuses.
The server/client filter split is applied in the right order, and the ceiling refusal matches it.
It also confirmed that `MoreDevicesThanOneRequestCanReturn_AreAllListedByDividingOnLastSeen` does
fail a partition that misses a dated or an undated device, so the miss-a-device case is covered.

### F1 (MEDIUM) - user-facing text still editorialised in six places

The owner's ruling of 2026-09-21 is that every string states what happened and what to do, and
nothing else. The rebuild applied it to the refusals and the banner and missed these.

| Was | Now |
|---|---|
| `This module's app credentials could not be built...` | `App credentials could not be built...` |
| `...the sign-in for this module's app registration succeeds...` | `...the app registration can sign in...` |
| `Microsoft Graph rejected this module's credentials... the module's Secret Server record` | `Microsoft Graph rejected the credentials... the Secret Server record` |
| `The Defender for Endpoint API rejected this module's credentials... the module's Secret Server record` | `...rejected the credentials... the Secret Server record` |
| Two-line grey helper paragraph under the filter card | One line |
| `M fetched in R request(s) across P Last seen range(s). Ceiling C.` | `M fetched in R request(s). Ceiling C.` |

The range count is gone from the header: it is the partition's implementation detail, and an
operator does not act on it. A request count above one already says the inventory had to be
divided. `RangesCompleted` stays on the result type and stays asserted by the partition tests,
which is where it is load-bearing.

### F2 (LOW) - the enrichment warning fired for rows that do not exist

A complete run matching zero devices rendered "...read '(unavailable)' for every device here" above
an empty table. The warning is now guarded on `result.Devices.Count > 0`; a run that matched nothing
says so in the header and says nothing else.

**Honest limitation.** The guard that bites here is the CSV export-position test's textual anchor
(M4 below), not a behavioural one. There is no render harness for this page, so no test asserts
that the warning is absent at zero devices - only that the branch condition is the string the test
expects. Removing the Count clause fails the suite; rewriting it to something equally wrong that
kept the same text would not, and nothing on this page can currently close that.

### F3 (LOW) - the ceiling-refusal test asserted four of seven server-side names

`TheCeilingRefusalNamesTheFiltersThatCannotReduceTheFetch` checked onboarding status, platform,
health status and risk score, while the production string also names exposure level, device name
and the Last seen window. A regression dropping one of those three would send an operator away from
a filter that would in fact have reduced the fetch - the exact loop this refusal was rewritten to
break - and the test would not have noticed. All seven are now asserted.

### F4 (LOW) - a budget test whose floor and ceiling were both 250

`MaxRequestsPerRun >= 250` and `MaxRequestsPerRun <= 1500 / 6` are the same number, so the test
pinned the constant to itself while its name claimed headroom. Both bounds are now derived and
neither evaluates to 250: the floor is ten times the cost of a balanced division of a ceiling-sized
tenant (`2 * leaves - 1`, plus the no-`lastSeen` part - 200 at today's constants), and the ceiling
is a fifth of the endpoint's documented 1,500 calls an hour (300), so one run cannot eat the
tenant's hour and four more operators can still run in it.

### F5 (LOW) - the accumulation-order guard covered one client-side filter of four

`TheClientSideFiltersAreAppliedAfterThePartitionAndNeverDuringIt` exercised only the Any Windows
prefix. An implementation that got the prefix right and applied the machine tag, the machine group
or a First seen bound inside the page reader has exactly the same defect - a full response of
non-matching rows reads as short, which is a false proof of exhaustion - and the test cleared it.
It is now `EveryClientSideFilterIsAppliedAfterThePartitionAndNeverDuringIt`, a Theory over all four.
The `Page` test helper gained an `extra` parameter so a fixture row can carry `machineTags`,
`rbacGroupName` or `firstSeen`. Only the platform case sets a non-matching `osPlatform`; the other
three leave the rows as Windows, so the exclusion is provably the filter under test and not the
prefix.

### Guard proof - 4 probes, each attributable

| Probe | Mutation | Test that failed |
|---|---|---|
| M1 | filter the page through `MatchesClientSideFilters` before `rows.AddRange(page)` | `EveryClientSideFilterIsAppliedAfterThePartitionAndNeverDuringIt`, all 4 cases |
| M2 | drop exposure level, device name and the Last seen window from `CeilingRefusal` | `TheCeilingRefusalNamesTheFiltersThatCannotReduceTheFetch` |
| M3 | `MaxRequestsPerRun` 250 -> 100 | `TheRequestBudgetLeavesRoomForAPartitionedRunAndStaysUnderTheHourlyLimit` |
| M4 | drop `result.Devices.Count > 0 &&` from the enrichment warning | `DefenderEndpointDevices_OffersNoExportFromTheRefusalBranch` |

M1 is the finding-5 probe and it fails all four Theory cases, which is the point: each of the four
client-side filters is guarded on its own, not by proxy through the platform prefix. Every restore
was `Copy-Item` plus an explicit `LastWriteTime` touch - a preserved mtime makes MSBuild skip the
rebuild and the next run tests the mutated binary - followed by a SHA256 compare against the
pre-mutation hash, which the probe script throws on. Both files match.

One in-flight break to record rather than hide: the F2 guard changed the exact line
`DefenderEndpointDevicesCsvTests.DefenderEndpointDevices_OffersNoExportFromTheRefusalBranch` uses to
locate the start of the complete branch, so that test failed on the first run after the fix. The
anchor was repointed to the new condition, not weakened - it still brackets the export button
between the first element of the complete branch and the device table's closing tag.

### What was not re-dispatched

Per `.agents/decisions.md` 2026-08-31, reviewer verification rounds are CRITICAL-only and each needs
an explicit owner go. None of these five is CRITICAL, so all five close on the guard proof above
rather than on a second codex round.

### Verification after the five fixes

`dotnet build ExchangeAdminWeb.slnx -c Release` 0 errors / 23 warnings; `dotnet test
ExchangeAdminWeb.slnx` **2949 passed / 0 failed / 3 skipped**, +3 on the 2946 of Revision 3 and the
three are accounted for exactly by the one-Fact ordering test becoming a four-case Theory;
`dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore` exit 0; `git diff --check
HEAD` exit 0; `tools/Test-AsciiOnly.ps1` exit 0.

### Still outstanding

Unchanged by this round. R1 (a), (b), (c), (d) and (e), and the manual acceptance checklist. Q2,
Q3, Q4 and Q5 remain unanswered. Nothing here has been run against the live service.

# Revision 5 - the owner revised queue item 8, 2026-09-24

Plan text only. No source file, test, script or descriptor was touched; `git diff --stat` over this
change names one path, `docs/DefenderEndpointDevices-Plan.md`.

**Numbering note, because this file has two revision series and a reader will trip on it.**
Revisions 1-8 record the first-series work (draft, three codex rounds, then S1, S2, S4, S3 and a
review of the finished module). A second series then restarted at "Revision 3 - the live run" and
"Revision 4 - codex review of the rebuild". This entry continues the second series as **5**. Where
the body above needs to disambiguate it says "first series" or "second series" explicitly.

## What triggered it

The owner revised queue item 8. `.agents/state.md` parked queue 8 on 2026-09-22 "waiting on an
updated requirements document from the stakeholder"; **that document has arrived**, and the revised
queue text is quoted verbatim at the top of this file, replacing the original. Whether the park
lifts is the owner's call and `.agents/state.md` owns it - this revision folds the new requirement
into the plan and does not resume the work.

Four things changed, and each one invalidated something written above it.

## 1. The purpose is now stated, and it re-decides the column set

"Purpose of this is to help locate machines physically in a global company." The module is still
list-and-export; what changed is that a column now has to earn its place by narrowing a location.
Recorded in a new section, "The purpose, and what it reframes".

**`DiscoverySources` is demoted, not deleted.** It names a product, never a machine and never a
place; it was a proxy for the real question and the research shows it is the wrong data. It stays
in the report because it is already shipped and costs nothing, and it stops being described as
answering the owner's need anywhere - field mapping, manual check 7 and the CSV section all say so
now.

## 2. The blocker is gone, so the registration section became a checklist

The registration exists, Revision 3 of the second series records both permissions granted and
consented, and the Delinea Secret ID is **657** - which goes in `GraphDelineaSecretId`
(`Modules/ModuleCatalog.cs:825`; descriptor at `:799`, `Version = "1.1.0"` at `:816`, `MaxDevices`
at `:827`, `IncludeDiscoverySources` at `:830`, all read from the file rather than remembered).

**The permission section was NOT deleted.** It was converted, because one permission changed status
and a registration consented under the old reading may not hold what the new one needs:
`ThreatHunting.Read.All` went from optional to load-bearing. The Privileged Role Administrator /
Global Administrator fact - an Application Administrator cannot consent a Microsoft Graph app role -
is unchanged, and is now the thing the checklist exists to make somebody check.

## 3. "Recently Seen By" is obtainable, and it makes hunting mandatory

The owner's screenshot **answers the question `.agents/state.md` records as open and unanswered**:
yes, the Defender portal shows an onboarded machine on a can-be-onboarded device's page, in a field
labelled "Recently seen by" with a "View all seen by" link.

The route is `SeenBy()` - **a documented advanced hunting function, not a column**, which is why
the earlier column enumeration was correct and still found nothing. Microsoft publishes the query
for exactly this scenario and states the purpose in the owner's own terms: the data "can help
determine the network location of each discovered device". A 1,000-device cap per invocation, a
documented disagreement about the returned column name, and the fact that the function returns an
**ID and not an FQDN** are all recorded in the new "Recently Seen By" section, with their URLs.

**One prior claim is falsified.** This file and `.agents/state.md` both treat
`DeviceInfo.HostDeviceId` as a dead end that disappointed - populated on 1 device of 113,102,
pointing at itself. Learn documents it as "Device ID of the device running Windows Subsystem for
Linux". It is a WSL host pointer; one row in a hundred thousand is exactly correct behaviour, not a
data gap, and it was never a candidate. The measurement was right and the conclusion drawn from it
was not.

**And one inference is scoped rather than repeated.** The 30-day hunting retention is real and is a
product limit, not a tenant setting - but it was measured across the whole 113,102-device inventory
and **does not transfer** to this module's target set, which is devices Defender is actively
discovering right now and which are therefore far more likely to be inside the window. The plan
now asks rather than asserts: **R1(i)** measures the coverage fraction.

**Consequence, and it is the important one:** hunting becomes load-bearing, the registration must
be verified to hold that consent, and the behaviour when hunting fails while the list succeeds is
**extended in T7 rather than duplicated** - the list still stands, the columns still read
`(unavailable)` with a named reason, and a failed enrichment still may not blank a column silently.
What T7 adds is that the banner must now say the report does not answer the request, that the
request-body guard must be widened exactly rather than relaxed, and that "once per refresh" may not
survive the 1,000-device cap (**Q9**).

## 4. A location-narrowing column set is proposed for approval

The owner asked for one. The new "Location-narrowing candidate fields" section presents L1-L16 as
a table with, per row, the exact source, the location strength, and **whether the field exists for
a non-onboarded device** - stated per row rather than in a footnote, because several of the
strongest signals are onboarded-only and that is exactly the trap. It names what is not documented
anywhere (no AD site field, no building, floor or geo-coordinate), recommends **against** the
`findbyip` REST route because its only Application permission is `Machine.ReadWrite.All` and T4
exists to keep a write scope off this registration, and treats "View in map" as a second recon
surface to enumerate (**R1(l)**) rather than as a design. The owner's own screenshot supplies the
sharpest point in the table: the example device is VMware-vendored, so adapter vendor is primarily
an **exclusion** signal - it is what keeps virtual machines out of a report about walking to a
machine.

## The `$skip` tension - flagged, not resolved

Learn documents `$skip` as supported on `GET /api/machines`, beside `$top` max 10,000 and "Maximum
page size is 10,000". Revision 3 of the second series recorded a live run returning 10,000 rows
with **no** `@odata.nextLink`. **Both are true**: supporting a client-computed offset and emitting
no server-side cursor are different mechanisms.

**The rejected-alternative section was NOT reversed.** It is a recorded decision with an argument,
and the argument it actually rests on - that `$skip` needs a stable order and this collection
documents no `$orderby` - is untouched by the docs listing `$skip`. A note now sits inside that
section where a reader meets the tension, and **R1(j)** probes `?$top=10000&$skip=10000` live. The
rejection stands until the probe and a cited stability source say otherwise, and the partition
design does not depend on the answer either way.

## Also corrected while in the file

- **Q1 and Q6 were still listed as open** in "Open questions for the owner" although Revision 5 of
  the first series closed both on 2026-09-21. Both are now marked closed with their answers. Stale
  reference, Known Failure Class 4.
- **Revision 6 of the first series left an instruction dangling** - the `DiscoverySources`
  string-or-array serialisation question, to be added "to R1's list if that gate is still open". It
  is still open, so it is now **R1(m)**.
- **Manual checklist steps 3 and 4 are stale** - they exercise a "Windows only" toggle Revision 3
  of the second series removed. Flagged in place rather than rewritten; see the note under that
  heading for why.
- **S5's `1.0.0` line is history.** The module is at `1.1.0`. A new "S6 - NOT YET SLICED" section
  says so and says why no slice is written yet.

## Versioning

**This revision bumps nothing** - it is plan text. The proposal for S6, computed from the field and
the Constitution rule rather than from memory, is module `1.1.0` -> **`1.2.0`** (minor: new
behaviour on a shipped module) with **no base app version bump** (nothing S6 needs is shared). The
condition that would flip the base call is named in the Versioning section. The implementing slice
recomputes from `Modules/ModuleCatalog.cs` rather than trusting this line.

## Verification

Docs-only, so the repo's docs rule applies: `git diff --check`. Build, test and format were not run
and are not claimed - no compiled file, test or script changed. No live query was run against the
tenant by this revision; every API fact added here came from Microsoft Learn on 2026-09-24 and the
URLs are in Sources. Every `file:line` cited was read from the file at the time of writing.

## Still outstanding

R1 (a), (b), (c), (d), (e) and the new (h), (i), (j), (k), (l), (m). The manual acceptance
checklist. Q2, Q3, Q4, Q5, Q8 and Q9. Q7 and Q10 are RULED (2026-09-24/25) and R1(o) is measured. **The pending step is the owner's: approve or
strike the L1-L16 candidate table (Q8) and answer Q9.** The proposed next action after that is to
draft S6 against the answers. Queue 8's park, and the unresolved fork about the page being unusable
at tenant scale, are `.agents/state.md`'s and are not re-asked here.
