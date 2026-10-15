# Agent State

Current work and blockers only. Rules live in `docs/ProjectConstitution.md` and
`.agents/repo-guidance.md`, decisions in `.agents/decisions.md`, and machine observations in
`.agents/machines.md`. Each plan owns its implementation and manual acceptance checklist.
Superseded descriptions are verbatim in `docs/history/state-archive.md` (newest section first;
the latest sweep is Archived 2026-10-14).

## Now

### Comms-10k / Defender remediation - C9 verified; C1 next

Owner 2026-10-15: "approved. go." All twenty findings in
`docs/Comms10kDefenderRemediation-Plan.md` are approved. That plan owns the slice order and
implementation table; `.agents/decisions.md` owns the Sol 6.1 / Astra dispatch exception.
One fresh Sol 6.1 agent at max implements each finding, sequentially; Astra at max independently
reviews the affected workflow. C9 landed in `f530c62` and independent Astra review endorsed it;
implementation, guard proof and review are recorded in `.agents/review/findings/cdr-c9.md`.

Implementation baseline: clean `master` at `8581484`; both `origin` and `github` remained at
`8853979` when verified on 2026-10-15. The intervening approval bookkeeping is local.
The old "unpushed since 96cdb89" assertions below are historical and superseded by this receipt.
New implementation commits will be local until separately authorized under push policy `ask`.
No work branch is in use. The previous handoff is preserved in `docs/history/state-archive.md`
under Archived 2026-10-15 - remediation approval.

C9's baseline Release build and full per-slice gate passed. Its actual-PowerShell-SDK guards
were mutation-proven against compiled pre-fix behavior, followed by byte-identical restore
and mtime update. Counts, hashes, the LF normalization receipt and the review packet live in
the finding record; `.agents/review/index.md` tracks the slice.

Next: C1, pinning the target and every group operation to the approved ObjectGUID.
The C9 trial established a passing baseline, regression proof and independent review. Continue
the approved slice order. C11 also owns the ordinary interrupted-write count-label case found
by C9's reviewer; the plan carries its reproduction and required coverage.

Standing boundaries: no live AD probe, tenant write or deployment. The approval does not widen
Comms-10k's protection exemption or authorize a push. No self-review. The module specification
still identifies version 2.0 / last verified June 25 while the app is 2.27.1; treat its stale
version as a documentation flag and verify relevant contracts against code.

### Defender review stream - NINE findings over four rounds, all fixed; round 5 is out

**Branch `master`, working tree clean.** The 2026-10-14 session took the two uncommitted
round-2 fixes through the gate, filed the five findings that had never been recorded, and ran
review rounds 3 and 4, each of which found two more.

| Commit | Finding | What |
|---|---|---|
| `c7e64b1` | `mde-4` | the observer failure count survives a mixed partial run |
| `ac71855` | `mde-5` | a file with no usable rows produces its errors CSV |
| `4e0c586` | `mde-6` | the seen-by filter stops judging a column nothing populated |
| `acf99e9` | `mde-7` | the errors report is reachable from every state that has one |
| `107cfe4` | `mde-8` | a blank device id says "(unavailable)" instead of nothing |
| `3f16528` | `mde-9` | two rows of the same non-matching address are reported as two |

Defender `1.6.3` -> `1.6.6`; Comms-10k `1.4.1` -> `1.4.4`; base app unmoved (module-scoped
changes only). **Every fix passed the full gate before its commit** - Release build 0 errors,
full suite green (3,826 rising to 3,830 passed / 0 failed / 3 skipped), `dotnet format`
exit 0, `git diff --check` clean, ASCII lint passed - **and every new test was mutation-proven
in that session**, several of them twice. `.agents/review/findings/mde-*.md` carries each
finding's evidence, guard proof and reviewer envelope; `.agents/review/index.md` is the index.

**THE PATTERN IS THE FINDING, and it has held four rounds running: every round has found the
previous round's fix incomplete at a NEIGHBOURING path.** `mde-4` re-opened `mde-2`; `mde-5`
re-opened `mde-3`; `mde-6` re-opened the Site conflation `111bfa2` fixed; `mde-7` re-opened
`mde-5`; `mde-8` re-opened `mde-6` per row instead of per run; `mde-9` re-opened the duplicate
decision `mde-3` recorded. Each original was verified against the state it was written for and
not against the one beside it. **Rounds 3 and 4 asked for that shape by name and it is how all
four of their findings were found.** Writing the records is what made it visible - the index's
"zero open rows" had been true only by omission until the drift sweep flagged it.

**Round 5 came back CLEAN over `96cdb89..f5febd5`, which CLOSES the stream** - codex /
`@azure-openai-eus2-global/gpt-5.5-dzs` / xhigh / standard. It was dispatched to attack round
4's two fixes specifically and cleared both, and it found no other block-bodied caller of the
repaired `Member()` helper that had been falsely green. No T1 sensitive path matched in any
round. Prompts are machine-local (`.gitignore:66`); outputs are
`.agents/review/out/session-review.txt`, `round2.txt`, `mde-r3.txt`, `mde-r4.txt`, `mde-r5.txt`.

**Next action: ASK THE OWNER TO PUSH.** The review stream is closed and there is no further
coding work owed on it. `.agents/push-policy.md` is `ask`, and nothing in this range has
reached either remote - `github` and `origin` were both still at `96cdb89` when last checked
live. On push, the nine rows in `.agents/review/index.md` move from `[v]` to `[x]`. After that
the stream needs a deploy and the three live checks below; neither is owed work in this repo.

**Two things the owner has not been asked about and should see.**

1. **`mde-9` changed behaviour that `mde-3` recorded as a deliberate decision** - a duplicate
   row is no longer unconditionally silent. The fix was shaped to preserve that decision
   exactly where its stated premise holds (the first occurrence RESOLVED) and to drop it where
   the premise is false. The ruling it serves is the owner's own: every non-matching address
   reported with its reason. **Flagged, not assumed settled.**
2. **`ExchangeAdminWeb.Tests/ClickGateRegistry.cs` still says tiers 2-4 are "not approved"** -
   seventeen entries plus a header comment reading "audited, NOT approved for conversion. Do
   not convert without an owner go." The owner approved tiers 2-4 on 2026-10-12. The drift
   sweep corrected that claim in `.agents/state.md` but its checklist does not reach test
   source. **Left alone deliberately: it is a code file, so changing it is a code change.** It
   is the file a future agent reads immediately before deciding whether to convert a page.

**Nothing in this session has run against the live tenant or the live forest**, and the whole
stream is unpushed and undeployed. The three environmental unknowns are owned by the entry
below. A post-deploy checker that answers all three from an exported CSV is at
`%TEMP%\Test-DefenderLocationExport.ps1`, validated against the owner's 2026-10-13 export.

**Owner instruction, standing: do NOT probe Active Directory without a direct go.**

### Defender for Endpoint is the live work; 2026-10-12/14 landed nine slices

**Queue 8 is the module people are waiting on and it now does what it exists for.** Landed and
verified, not deployed since `963b85d`:

| Commit | What |
|---|---|
| `43c0735` | Device table paged at 50. It rendered every device and killed the circuit |
| `5cea983` | Hunting enrichment batched at 1,000 ids, per-batch failure reported honestly |
| `156b890` | Recently seen by, column and filter |
| `e8688ed` | Search form rebuilt - three groups, nothing happens until Search |
| `c6b90b4` | Subnet-to-site matching and the forest read |
| `581a9ab` | **Site column, CSV field and filter** - the location answer |
| `963b85d` | Observer column resolved by name discovery, not a fixed guess |
| `042834b` | Every new lookup isolated so it cannot break what already works |
| `772112a` | A missing DI registration fails the suite instead of the app |

Also landed: `a01767c` Comms-10k rejected-address CSV (queue 26 point 3).

**What the owner's 26,812-row export settled, and it should not be re-derived:** LastIpAddress
is present on 26,811 devices. DiscoverySources 35%, Vendor 25%, MachineGroup 100% but only
EIGHT distinct values tenant-wide, MachineTags one row, LastExternalIpAddress zero,
RecentlySeenBy zero. **Site is the only location signal that exists for essentially every
device.** Anything proposed as a locator gets measured against that table first.

**Three things are unproven against the live environment and all three fail visibly:**

1. **The forest read has never run.** `AdSiteDirectory` discovers the configuration partition
   from RootDSE and queries `CN=Subnets,CN=Sites,<configNc>`. If it fails, every Site cell
   reads `(unavailable)` with a banner - it does NOT fail the report, which deviates from plan
   Q10 (`.agents/decisions.md` 2026-10-14 records why and the one-line revert).
2. **`SeenBy()` has never returned data here.** Its first shipped version named the wrong
   output column and was empty for all 26,812 devices silently. It now tries every documented
   spelling via `column_ifexists`, runs in its OWN request so it cannot take the posture
   columns down, and a succeeded run whose observer column is empty for EVERY row says so on
   the page.
3. **Whether AD site names are a physical locator at all.** The owner's position: *"subnet
   match will maybe get you AD site, but that's not a physical locator."* 167 sites exist and
   their `location` attribute was empty everywhere when last measured. **Do not probe AD to
   settle this** - owner instruction 2026-10-14, probing is explicitly forbidden without a
   direct go.

**Still not built, and the reason is the same each time:** subnet, default gateway, DHCP
server and MAC vendor need a `DeviceNetworkInfo` join whose column names are unverified, and
KQL fails a whole query on an unknown column. The `column_ifexists` technique now proven on
the observer column is the safe route in, in its own request.



### What comes after Defender, in the owner's stated order

**Scope is no longer a gate** (owner 2026-10-12, `.agents/decisions.md`: *"I want all of
everything done. stop asking me questions that just reinforce that. you do everything."*).
**Do not put go-ahead questions about queued work to him** - re-raising approved scope is the
failure mode that ruling exists to stop.

The order he stated, all three already approved:

1. **Click-gating tiers 2-4** - the remaining 19 pages. Approved 2026-10-12, not started;
   `docs/ClickGatingAudit-Plan.md` owns the design (Revision 1 as corrected by Revision 2) and
   its Status header carries the approval.
2. **The verbosity sweep at FULL scope** - all 39 files, S0 through S10, not the seven-worst
   subset the plan recommended. Approved 2026-10-12; `docs/VerbositySweep-Plan.md` owns it.
3. **A REWRITE of the Service Health public-status plan.** The old document is REJECTED and is
   explicitly not amendable; the real ask is consolidating Microsoft's status posts across all
   of its official channels with the tenant feed already in the app, for a user of this app who
   holds no privileged Azure/M365 access of their own. It is NOT an auth change. Rewrite from
   the ask. `.agents/decisions.md` 2026-10-12 owns the ruling and the owner's verbatim words.

### What the progress-coverage stream settled - the narration is archived

The per-slice record for S1 through S5 is verbatim in `docs/history/state-archive.md` (Archived
2026-10-09). These are the parts a later session needs, and they outlive the plan:

1. **Eleven findings in seven days, and every one was found by review or by an agent tripping
   something while doing adjacent work - never by the author of the guard reading their own
   code.** Each tool closed the previous tool's holes and brought its own: regex, then a hand
   lexer, then Roslyn. The last one disproved its own finding. **That is the argument for
   per-slice review, not for a better tool.**
2. **What gets named as a covered call** (reviewer ruling, 2026-10-08, settling two entries that
   had come to disagree): **name operation-scale work, including local disk or file-size work;
   do not name cheap unrelated local prechecks.** `f1257bd`'s unnamed calls were authorization
   prechecks; `0eba929`'s named local call IS the operation its label describes.
3. **What a non-success exit should do** (same ruling): **a lookup that finished reports success
   even when it finished in a read-only state; refusals stay non-success.** The distinction is
   "did the work complete", not "did the operator get what they wanted".
4. **Check the controls, not the flag.** "The busy flag is in `IsBusy`, raised above the yield,
   therefore the page is shut" is only as good as the gating. `ClickGateRegistry.Migration`
   itself records three deliberately UNGATED controls that write exactly the fields two handlers
   read after their yield, so the premise was false there and two defects had been cleared on
   it.
5. **"Is there a dismiss control" is the wrong question.** It asks for
   `@onclick="() => someField = null"`. Neither `IntuneDevices` nor `RiskyUsers` has one and
   both still need a captured local: `IntuneDevices` clears `actingDeviceId` early in its
   `finally`, which re-enables Search, whose handler calls `deviceOutcomes.Clear()`;
   `RiskyUsers`' Search button is gated on `isLoading` and `ExecuteActionAsync` never raises it,
   so the clear is reachable throughout. **The question is "can anything null the field the
   `Complete` reads".**
6. **A probe that does not compile is silent and is not evidence.** Two probe runs produced no
   assertion at all - once because a `perl` anchor matched the first of four occurrences on the
   page, once because of a syntax error in the registry - and both looked like clean runs until
   the output was read. Anchor every probe on text unique to the handler, and grep the probe
   output for `error CS` as well as for the failure. Related and already recorded below: a
   mutation that did not apply is not a passing test.
7. **A source-text guard anchored on prose fails correct code** (review finding `prog-6`). Three
   guards took `body.LastIndexOf("finally")` over the RAW method text, so a trailing comment
   containing the word moved the anchor. All three now anchor on `ProgressScan.CodeView(body)`,
   which blanks comments and string literals while preserving every offset.
8. **Nothing in this work proves a progress bar renders.** Repeated deliberately: it is the one
   thing a green suite in this repo cannot tell anyone.

### Circuit lifetime leak - landed, UNREVIEWED, and unproven

`docs/CircuitLifetimeLeak-Plan.md` reads `Implemented` and owns the design; all three slices
landed (`98b29d1`, `c6c73d2`, `9741f7c`, plus the S3 record commit). It is step 1 of
`docs/ProductionMemory-Plan.md`'s sequence; steps 2 to 4 are untouched.

**Two things are owed and neither is ours.** Per the owner's standing instruction codex reviews
each code change, and this work stream has had none. **Nothing here proves the leak is gone** -
a source guard cannot. **Prod took the fix on 2026-10-09 and the reading was attempted 2026-10-12**: not takeable
from an unelevated shell, and no worker on the host survives long enough for a since-the-deploy
clock to exist. What it needs is a worker with ~24 hours of uptime under traffic plus one
elevated shell for the pool-to-PID map. `.agents/machines.md` owns the attempt, the figures and
what each outcome means. Nothing on the host resembles the pre-fix leak, which is an inference
and not proof; continued growth would say this was not the dominant cause and point at step 2.

The recurrence guard is `ExchangeAdminWeb.Tests/EventSubscriptionLifetimeTests.cs`, written
against the shape rather than against the two pages - it was probed with a brand-new eighth page
named nowhere in the suite and fired on it, and it reaches `Components/Shared` as well as
`Components/Pages`.

## Previously

### Queue item 24, the global progress system

**ITEM 24 IS FUNCTIONALLY COMPLETE AND PARTIALLY ACCEPTED.** 86 page spinners to ZERO across 32
pages; every module reports to the bottom status bar instead. Re-measured 2026-10-12 as of
`96cdb89` and still true: `grep -rc "spinner-border" Components/Pages/*.razor` returns zero.
`docs/GlobalProgressSystem-Plan.md` owns the design; the landed record is in
`docs/history/state-archive.md` (Archived 2026-10-09). The owner deployed to dev 2026-10-02 and
reported *"browser check looks fine for the modules I checked. reset worked."* - **a sample, not
a sweep**; he did not say which modules, and 32 pages changed. Treat the status bar as working
in principle and each unchecked page as unverified.

**Two things remain open on item 24 and the owner has not ruled on either:**

1. **Four spinners survive in `Components/Shared`** - `ADGroupAutocomplete`,
   `ADIdentityAutocomplete`, `RecipientAutocomplete` (typeahead) and `TicketNumberInput`
   (ServiceNow validation). They fire per keystroke inside the field the operator is looking at
   and the bar cannot say WHICH field, which is the "information the bar cannot carry" case the
   owner's ruling says to raise rather than decide. **Raised 2026-10-02, unanswered.**
   `GlobalProgress.razor`'s own spinner is the bar itself and stays regardless.
2. **Cancellation tokens.** Only Message Trace's bulk detail download honours
   `handle.CancellationToken`. Everywhere else the navigation guard's OK path stops the UI
   waiting rather than the work. Pre-dates that session; the last real gap in the system.
   Extending it means threading tokens through service signatures, module by module.

**Four rules learned the expensive way. Breaking any of them re-creates a defect already paid
for:**

1. **Converted pages take the service as `[Inject] private IActivityProgress Progress` inside
   `@code`, never an `@inject` directive.** A directive sits above every registered control and
   shifts all of them; the property sits below and shifts nothing. Only `ExpectedLineCount`
   changes, and all 316 ClickGate assertions stay green without re-anchoring.
2. **Remove a module's spinner ONLY after its operation reports.** A spinner removed from
   unreported work leaves no feedback at all, which is worse than the duplication the sweep
   exists to remove.
3. **Per-row indicators and the pre-authorization latch STAY.** The frame says what is
   happening and has no concept of where in a table; the latch is refusal, not progress. This
   reading of "spinners don't live in modules" has not been ruled on - one owner word overrules
   it everywhere.
4. **Never report page-load state from the browser.** Four mechanisms were tried and all four
   stranded the readout on a page that had arrived. Page loads are server-side
   (`Services/PageLoadTracker.cs`, `Middleware/PageLoadMiddleware.cs`);
   `wwwroot/nav-progress.js` is deleted, must not come back, and `Components/App.razor` carries
   the reason where its script tag was.

**STANDING CONSTRAINT, owner 2026-10-01: no deployments until item 24 is done**
(`.agents/decisions.md`). **The "it is being read as prod-only in practice" reading once
recorded here is FALSIFIED: BOTH instances have been redeployed during the freeze, on
2026-10-02 and again on 2026-10-09.** Prod shipped both times, so the freeze is not being
observed on either instance. `.agents/machines.md` owns the receipt and the version numbers;
they are not copied here. Whether the ruling still stands is the owner's to say; nothing here
reinterprets it.

**NOTHING HERE CAN VERIFY THE REST** (`.agents/decisions.md` 2026-10-01, "Rendered is not
visible"). Three dev rejections in a row came from source scans passing while the feature was
visibly broken. Do not report an unchecked page as working.

**A process failure from 2026-10-01 worth not repeating:** `ed669a8` was committed with two
failing tests and a "0 failed" claim in its message, because the commit was chained onto the
test command with `&&` and the result was never read. `cc3e8c9` corrects it. Do not chain a
commit onto an unread verification run.

**Item 25, the verbosity sweep: APPROVED AT FULL SCOPE 2026-10-12 - all 39 files, S0
through S10, not the seven-worst subset the plan recommended - and NOT STARTED. It must not
be folded into 24.** `docs/VerbositySweep-Plan.md` owns the measured scope, the house style
and the slices; its one open owner question is ANSWERED. Nothing is copied here. Owner,
verbatim: *"Do a verbosity sweep. Too much chat context leaked into the app, too many words for
a human to read instantly."* His example is True Last Logon's no-logon banner, and his rule is
**"No editorializing anywhere in the app."** It is an app-wide prose sweep. One line to hold
while cutting: a COUNT or a coverage fact is not editorializing - in True Last Logon
specifically, `lastLogon` does not replicate, so "none of 34 that answered" is a materially
weaker claim than "none of 37", and "This is not evidence of dormancy." is a safety statement.
What goes is instruction to the reader. **The claim once recorded here that "grep found NO test
pinning any of that copy, so the wording is free" was TRUE ONLY FOR TRUE LAST LOGON, which is
what it measured, and is FALSE app-wide:** 20-plus test files assert exact operator-facing
wording, several with whole-sentence equality, and `RiskyUsersPageTests.cs:413-423` pins three
lines under a test named `ActionConsequence_MatchesTheOwnerApprovedConfirmationLine`. True Last
Logon prose is still free; its LINE COUNT is not, because every page is line-keyed by
`ClickGateRegistry` and `ProgressRegistry`. The plan owns both constraints. **Do not read the "0 answered / 37 did not
answer" in the owner's example as a live defect** - that output is from the pre-`fcc94a4` build,
where `Get-ADUser -Identity` could not resolve a UPN so every DC errored, and an errored DC
correctly counts as "did not answer".

**Item 23 is closed - the owner verified it on dev 2026-10-01** (queue table below). Its lesson
outlives it: a defect recorded as an open question is still a
defect in production.** `ClickGateRegistry` found this exact fault, called it "the wrong refusal
for a DOM-synced control", and parked it for the owner because D2(a) is an owner ruling. Nothing
in this repo re-raises a parked entry, so it shipped and the owner hit it. When a registry entry
or a plan note parks a live defect, it needs a line in this file too, not only in the artefact
that found it.

There is no agent-executable work left on the Comms-10k plan. What S4 changed that acceptance must actually
exercise: the write is no longer atomic. It clears the `member` attribute and refills it in
batches, so for roughly eight seconds at ten thousand members the list is empty and then
partial. The module reports five distinct outcomes for that - succeeded, succeeded with
unremovable primary-group members, partly applied, could not confirm, refused before any
change - and **the two worth checking by hand are "partly applied" and "could not confirm"**,
because they are the ones telling an operator the list may be broken.

**Two things in the plan were removed by owner ruling and MUST NOT be reintroduced**
(`.agents/decisions.md` 2026-09-30; both have tripwires that fail if they come back):

- **The distribution-group guard.** Slice 4's text still refers to it; it went with the rest of
  the protection path in S3. Plan test 19 is void and the "distribution-group guard" clause in
  tests 12-15 reads as deleted.
- **All replace locking.** No mutex, no semaphore. Plan test 17 is void. The accepted risk:
  two concurrent replaces can interleave and leave the list holding neither uploaded file; each
  run's read-back reports that as incomplete rather than claiming success.

**Comms-10k runs no protected-principal check of either kind, and two agent concerns about
that were raised and OVERRULED. Do not re-litigate either** - `.agents/decisions.md`
2026-09-30 records both so a reviewer finds them already answered: the group is
`GroupCategory: Security` not Distribution, and `TargetGroupName` is a generic field nothing
structurally pins to this group. Both true; the owner ruled neither warrants a check in a
single-purpose module. The Constitution, `.agents/repo-guidance.md` KFC3 and the developer
guide all carry the scoped exception, contrasted against Self-Service Groups.

**Owner-side work outstanding, none of it agent-executable:**

- **True Last Logon:** the UPN defect is CLOSED - `Get-ADUser -Identity` could not resolve a UPN
  (fixed in `fcc94a4`, module `1.0.1`), and the owner confirmed the UPN search works on dev
  2026-10-01. Still outstanding and still owner work: `AuditLog.Read.All` consent and the
  two-account live comparison against `Get-TrueLastLogon-Commercial.ps1`. The on-prem half now
  works against a real domain; the cloud half has still never run.
- **Migration `@key` fix:** proven at source level only; the owner's own screenshot case is
  still the acceptance check.
- **Comms-10k: FAILS THE STAKEHOLDER'S TEST (owner, 2026-10-01). The plan is fully implemented
  and it is NOT accepted.** The stakeholder's own three points, verbatim, are below. **Point 3 is
  DIAGNOSED as of 2026-10-12 - root cause and a separate reporting defect, both below. Points 1
  and 2 are not.** No fix is started; do not open one without an owner go.

  1. *"Longer synchronization time. In the previous version of the Self-Service App, audience
     updates were completed much faster, typically within 15 to 20 minutes. With the current
     interface/version, member loading can take 1.5 hours or more before the distribution list
     is fully updated."*
  2. *"Lack of Sync Progress Visibility. We currently have no visibility into whether the member
     synchronization is still in progress ... a clear notification indicating whether the member
     loading process has been successfully completed or has failed."*
  3. *"Ongoing issue with missing email addresses. We continue to encounter cases where not all
     intended members are loaded into the distribution list."*

  Three things the next session should know before touching any of it, none of them a
  diagnosis:
  - **Point 2 IS queue item 24**, which is functionally complete and awaiting the owner's
    acceptance pass. Fixing the P1 covers it; it is not separate work.
  - **Point 3 IS DIAGNOSED, 2026-10-12, and it is NOT a regression.** The lookup has never
    matched alias addresses. `Services/Comms10kAddressResolver.cs:117-118` builds
    `(&(objectCategory=person)(objectClass=user)(|(userPrincipalName=V)(mail=V)))` per address -
    UPN and the PRIMARY SMTP address only. `proxyAddresses` is matched nowhere in the module
    (`grep -rn -i proxyaddress Services/Comms10k*.cs Components/Pages/Comms10k.razor` returns
    nothing; the only hit in the whole app is `Services/ADDirectorySearchService.cs`). Any
    secondary or alias SMTP address - another owned domain, a pre-rename address, a vanity
    alias - resolves to nothing and the person is left off the list. **Checked for regression and
    there is none:** the pre-S1 path at `1c6eaba^` `Services/Comms10kService.cs:137` filtered
    `UserPrincipalName -eq ... -or EmailAddress -eq ...`, and the AD module's `EmailAddress` IS
    `mail`, so S1 changed the transport and preserved the keys exactly. That is why the
    stakeholder says "ongoing" and "we continue to" - it predates the plan by design.
    The resolver's own remarks at `:76-81` say widening the keys is deliberate and names the
    risk: matching `sAMAccountName` would let a bare login resolve and matching groups would put
    a group DN into an individual-subscriber list. **`proxyAddresses` is not in that risk class**
    - a proxy address identifies the same single person the primary does - but it is still a
    widening of who receives the mail, so it needs an owner go, not a coder-side fix.
  - **Point 3's second half is a reporting defect and is separate from the cause.**
    `Components/Pages/Comms10k.razor:110` renders `SkippedEmails.Take(10)` then a literal `"..."`,
    and `:120` gives only a count. There is no export and no full list anywhere in the UI, so on a
    ten-thousand-row upload an operator who loses 200 people can see ten of their addresses and
    has no way to obtain the rest. That is precisely the stakeholder's *"requiring us to manually
    identify the affected users"*. The resolver already carries every skipped address and its
    reason (`Comms10kAddressResolver.Outcome.SkippedAddresses`); nothing but the view truncates.
  - **Point 1 needs its baseline established first.** "The previous version of the Self-Service
    App" may not be this app, and "before the distribution list is fully updated" may be
    measuring AD-to-Exchange-Online directory sync rather than the module's write, which is
    measured at 7.9s for 10,001 members. Ask before timing anything.

**A recurring defect in this session's own tests, worth carrying:** three separate guards were
written to forbid a named antipattern and then read the comment that EXPLAINED the antipattern,
failing against the prose rather than the code. All of them now strip comments first, and so do
the S3 and S4 tripwires. **Two more of the same family surfaced in S4's own mutation probes and
are worth carrying:** a guard on a FACTORY is not a guard on its CALL SITE - the host-lock test
asserted the mutex was keyed on the GUID it was handed, which a caller handing it the wrong
value satisfies perfectly, and only a probe found it. And a probe whose sed silently no-opped
was reported as run; a mutation that did not apply is not a passing test, so the probe script
now verifies the mutation landed before trusting the result. Related:
one mutation probe PASSED and was nearly recorded as bitten - the "mutation" was an equivalent
implementation. A probe that passes is either a vacuous test or a bad probe, and assuming the
first without checking is how a vacuous test gets certified.

**2026-09-30 - Module development architecture plan revised for a complete cutover.**
[`docs/ModuleDevelopmentPlatform-Plan.md`](../docs/ModuleDevelopmentPlatform-Plan.md)
revision 2 owns the proposed all-module code/registration/test/record boundaries,
generated composition and concurrent-development proof. The owner rejected both a
two-pilot rollout and a registry-only reduction. **Draft; implementation not started.**
The prior [Claude review](review/module-development-platform-plan-r1.md) is retained;
its plan dispositions are in revision 2, section 10. No repeat review was run.
Next: approval of the full cutover, including its explicitly proposed structural-only
Migration exception. Until approved, the Migration prohibition below remains in force.
The existing feature queue and its behavior scopes remain unchanged.

**2026-09-29 (late). THE MIGRATION MODULE IS CLOSED BY OWNER ORDER. DO NOT TOUCH IT.**
Five days on one module was the limit. It was left broken on dev - the mailbox pane rendering
empty for batches that have mailboxes - and the diagnosis is verbatim in
`docs/history/state-archive.md` (Archived 2026-10-09, the queue 12/13/14 entry). **That record
is history, not a work item.** Several other modules are waiting to deploy and they outrank
finishing it.

**TWO owner exceptions have been made to that closure, both for prod blockers he raised himself
and both scoped to the one defect named:** the `@key` checkbox fix (2026-09-30, *"we cannot
deploy like this"*) and queue item 23 (2026-09-30, *"new item 23 is p1"*). Neither reopens the
module for anything else, and nothing else was touched in either. A closed module plus a
prod blocker the owner names is the only pattern that has authorised work here.

### The whole queue, swept 2026-10-01 against `C:\Users\mcoelho\Desktop\queue.txt`

**Owner instruction, 2026-09-30: "this entire queue needs to be done this week."** That date is
long past and the remaining work never fitted it; do not plan around it. The feasibility note
this paragraph used to point at no longer exists anywhere - the pointer is removed rather than
reconstructed.

**`queue.txt` is the owner's file. Never write to it, including status markers** - its own first
line says so. Status lives here.

**Rows 2, 4, 8, 9 and 25 were re-verified against repo evidence on 2026-10-14 as of
`6bfb8a6`; rows 14, 18, 20 and 24 carry their 2026-10-09 reading and the rest their
2026-10-01 sweep reading.**

| # | Item | State |
| --- | --- | --- |
| 1 | Licensing under Identity | **DONE** (owner-marked). |
| 2 | Add status.cloud.microsoft to the O365 status page | **THE PLAN IS REJECTED and must be REWRITTEN, owner 2026-10-12.** `docs/ServiceHealthPublicStatus-Plan.md` carries `Status: REJECTED BY THE OWNER, 2026-10-12. Do not implement any part of this document.` It is explicitly not amendable into the real ask; `## Now` ("What comes after Defender") and `.agents/decisions.md` 2026-10-12 own what the rewrite has to be. |
| 3 | Migration report survives batch recreate | **DONE** (owner-marked). |
| 4 | Split message trace vs header analysis permissions | **DONE** (owner-marked) **but the code disagrees and this needs the owner's word.** Re-measured 2026-10-14 as of `6bfb8a6`: outside the tests, `MessageTraceSearch` appears only as a declaration at `Modules/ModuleCatalog.cs:489` (with an explanatory comment at `:468`) and in a comment on `Modules/ModulePermission.cs:22`; no page, handler or policy consults it, so the alias is still inert exactly as slice 1 left it. `docs/MessageTracePermissionSplit-Plan.md` still reads `APPROVED ... and IN PROGRESS` and slices 2-4 are unstarted. Either the owner considers the declaration sufficient, or the DONE marker is premature. Flagged, not resolved. |
| 5 | Other tenants/domains in message trace | **FEASIBILITY ONLY, no code proposed.** `docs/MessageTraceMultiTenant-Plan.md`, `Status: Draft. Scoping and feasibility only.` Needs an owner decision on whether to proceed at all. |
| 6 | Containerize the app | **ON-HOLD** (owner-marked). `docs/Containerization-Feasibility.md`. |
| 7 | O365 status module loading affordance | **DONE** (owner-marked). |
| 8 | Defender for Endpoint devices | **S1-S7 IMPLEMENTED, never run against the live tenant; S8 (docs, version, and the `DeviceNetworkEvents` fallback) is not started.** `docs/DefenderEndpointDevices-Plan.md` carries `Status: S1-S7 IMPLEMENTED` and the three things now settled differently from its body; `Modules/ModuleCatalog.cs` owns the version. The live work and its three environmental unknowns are in `## Now`. Secret ID 657, both permissions consented. |
| 9 | App-wide click-gating audit | **TIER 1 COMPLETE. Tiers 2, 3 and 4 were APPROVED by the owner 2026-10-12 (the remaining 19 pages) and are NOT STARTED.** `docs/ClickGatingAudit-Plan.md`; `.agents/decisions.md` 2026-10-12 owns the approval, superseding the 2026-09-18 scope limit. Largest single block of remaining effort in the queue. |
| 10 | O365 password change matches on EmployeeID | **BUILT, REVIEWED, NEVER RUN AGAINST A REAL TENANT.** `docs/CloudPasswordReset-Plan.md`. Needs a deploy and owner validation, not code. |
| 11 | Force-change-at-next-login option at runtime | **Same build as 10.** Same pending deploy and validation. |
| 12 | CompleteAfter in migration | **DONE** (owner-marked). |
| 13 | Per-migration checkboxes and actions | **DONE** (owner-marked). |
| 14 | Migration interface redesign | **DONE** (owner-marked). The `@key` checkbox defect that was blocking the deploy is in this surface, is FIXED in code and is unverified in a browser; the owner's own screenshot case is the acceptance check. The implementation record is verbatim in `docs/history/state-archive.md` (Archived 2026-10-09). |
| 15 | Risky Users complete results | **DONE.** Browser check outstanding, owner's. |
| 16 | Sidebar scrollbar | **DONE.** `ExchangeAdminWeb.Tests/SidebarScrollCssTests.cs`. Browser check outstanding, owner's. |
| 17 | True Last Logon module | **BUILT, and its first live run found a defect that is now fixed. S1-S3 landed, with the UPN fix in `fcc94a4` (`Modules/ModuleCatalog.cs` owns the version) after `Get-ADUser -Identity` proved unable to resolve a UPN - every DC answered "cannot find". NOT done: the re-run and the mandatory live comparison are outstanding owner work.** Detail block below. |
| 18 | Security hold | **DONE.** Built, shipped and reviewed; manual acceptance is production use, not owed work (`.agents/decisions.md` 2026-10-12). The owner's rulings of 2026-10-05 dropped the CSV, dropped release entirely and made the lockdown-OU move one optional step inside Emergency Disable. `docs/EmergencyDisableLockdownOU-Plan.md` owns the design and the acceptance list; `.agents/decisions.md` owns the two owner rulings (2026-10-05 `info` stamp, 2026-10-06 default-unchecked). |
| 19 | Risky Users labels | **DONE.** `60c3ace`. |
| 20 | Implement `docs/Comms10kBulkResolveScale-Plan.md` | **PLAN FULLY IMPLEMENTED 2026-09-30, all four slices. NOT ACCEPTED - it fails the stakeholder's test (owner, 2026-10-01) and the plan's Acceptance section is outstanding in full.** `Modules/ModuleCatalog.cs` owns the module version; the plan sets it once on S1. The stakeholder's three points, and the three things to know before touching any of it, are in `## Previously` above. |
| 21 | Popup report has no scrollbar and ignores the mouse wheel | **DONE. Fixed 2026-09-30, browser-verified by the owner 2026-09-30.** `docs/AppLayoutAndScrolling-Plan.md`. Cause was structural: a Bootstrap `.card-body` between `.mig-modal` and the `<pre>` made the scroll rule inert. |
| 22 | Module bottom always cut off; audit the whole app for layout, alignment and scrolling | **DONE. Fixed 2026-09-30, browser-verified by the owner 2026-09-30.** The layout now has a real height chain, so all 9 chrome-arithmetic guesses are gone. Alignment had no open defect. App `2.24.0` -> `2.25.0`. |
| 23 | Migration batch tick boxes drop ticks when clicked in rapid succession | **DONE. Fixed 2026-09-30 (`af4fb4c`), owner-verified on dev 2026-10-01.** Was an owner-declared P1 prod blocker. A handler guard on a DOM-synced control: the browser applied the tick, `if (IsBusy) return;` discarded it, and Blazor sent no correction because its last-rendered value still matched the model. Guard removed from `ToggleBatchSelected` and `ToggleSelectAllBatches`; D2(a) rules out the disabled-attribute repair. Migration `1.22.4` -> `1.22.5`. Acceptance is the owner's rapid-click case. |
| 24 | Global progress-meter system; a click landing is not obvious in most modules | **FUNCTIONALLY COMPLETE.** S1-S4 landed, all 32 pages adopted, every page spinner gone, Migration included, and `docs/ProgressCoverage-Plan.md` has since closed the 52 operations that were adopted but silent. **Remaining: the four `Components/Shared` typeahead spinners he has not ruled on, and cancellation tokens** - both are in `## Previously` above. The acceptance pass is production use, not owed work (`.agents/decisions.md` 2026-10-12). `docs/GlobalProgressSystem-Plan.md` revision 2. Owner-declared P1. Not a harmonisation of the existing spinners - one system that modules report into and that owns all display, built on the rule that work which writes many items or runs long moves to the background runner so its result lands. S1 and S2 touch no pages; S3 moves Comms-10k; S4+ adopt one module per slice. |
| 25 | Verbosity sweep across the whole app; no editorializing anywhere | **APPROVED AT FULL SCOPE 2026-10-12 (all 39 files, S0-S10), NOT STARTED.** Owner: *"Too much chat context leaked into the app, too many words for a human to read instantly."* His example is True Last Logon's no-logon banner, but the item is an app-wide sweep, not that one module. Counts and coverage facts are not editorializing; instruction to the reader is. **The copy is NOT free app-wide** - 20-plus test files assert exact operator-facing wording, and every page is line-keyed by `ClickGateRegistry` and `ProgressRegistry`; see `## Previously`. |

### Queue item 17 - True Last Logon, built and awaiting a live check

**All three slices are landed and the module ships** (`Modules/ModuleCatalog.cs`
owns that number). **It is NOT done, but the reason has narrowed:** the on-prem half HAS now
run against a real domain - the owner exercised the UPN search on dev 2026-10-01 - while the
cloud half has still never touched a real tenant.

**Landed:**

- **S1, the on-prem sweep.** `Services/OnPremLogonAggregator.cs` (pure, 5 tests) and
  `Services/TrueLastLogonService.cs`. DCs enumerated at runtime from the host's own domain
  membership, TCP:389 preflight at 2s, parallel query via PS7 `ForEach-Object -Parallel`.
  `MapRow` is internal and tested (5 more).
- **S2 core, the cloud rules.** `Services/CloudSignInAggregator.cs` (pure, 9 tests). Later of
  the two sources per field; the four verification states.
- **S2 Graph I/O.** `Services/CloudSignInService.cs` (26 tests). Three concurrent queries for
  one user: `signInActivity` via the `/users` COLLECTION form with an `eq` filter, the
  interactive sign-in log on v1.0, and the non-interactive sign-in log on **beta**. Two
  decisions worth not re-deriving:
  - **Non-interactive sign-ins are beta-only.** Graph v1.0 documents its sign-in list as
    carrying interactive sign-ins only, and the v1.0 `signIn` resource has no
    `signInEventTypes` to filter on. The shared `GraphTokenClient` is confined to v1.0 by a
    deliberate guard, so the client here is `DefenderApiClient`, the one class in the assembly
    that takes its base URL as a constructor argument. Widening the shared client would be a
    shared-infrastructure change, and adding a module must not bump the base app version
    (`.agents/decisions.md` 2026-07-21). No credential is shared - this module reads its own
    `GraphDelineaSecretId`.
  - **The log counts as having ANSWERED only when BOTH its queries did.** The non-interactive
    query is the only source that catches what `signInActivity` under-reports, so a run that
    lost it has verified nothing. Relaxing that `&&` to `||` makes a live account read as
    confirmed dormant, and four tests fail when it is.

**Review round: CLOSED.** A codex defect hunt over `046e3d2..cb968da` returned two findings,
both admitted and both fixed one-per-commit with mutation proof: `tll-1` (HIGH, `4725109`) and
`tll-2` (MEDIUM, `a908598`). Detail in `.agents/review/findings/`; no open rows in
`.agents/review/index.md`. Both were the same shape - the file stated a rule in its own class
remarks and then did not apply it.

**S3 IS LANDED. The module is registered, reachable and shipping** (the version literal is
owned by `Modules/ModuleCatalog.cs`, not copied here) - descriptor,
page, permission, click gating and audit. The base app version was deliberately NOT bumped
(Constitution, Deployment And Versioning; `.agents/decisions.md` 2026-07-21). All four things
S3 owned are done:

1. `TrueLastLogonService` and `CloudSignInService` registered as singletons in `Program.cs`.
2. The named client `CloudSignInService.HttpClientName` registered at **2 minutes** - clear of
   the two roughly-ten-second log queries without letting a hung request sit for four, the way
   the Defender hunting client does.
3. `GraphDelineaSecretId` declared on the descriptor, naming BOTH `AuditLog.Read.All` and
   `User.Read.All` and saying a Privileged Role Administrator or Global Administrator must
   consent. `Catalog_TrueLastLogon_DeclaresItsOwnGraphSecretAndNamesBothPermissions` pins that
   wording, because the descriptor is the only place a deployer is told.
4. "Not checked", "no logon found on what was checked" and a date render three different ways,
   and the verification state is a first-class field with its meaning spelled out beside it.

**Three decisions in S3 worth not re-deriving:**

- **The unconfigured module does NOT return.** Every other Graph-backed page renders an alert
  and returns when its secret is unset, which kills every control below. This page must not:
  the on-prem sweep needs no credential and works fine while the cloud half is unconfigured.
  It warns, stays usable, and reports the cloud half as not checked - the module's own thesis
  applied to its own configuration. The `ClickGateRegistry` entry records this as the reason
  its button renders unconditionally.
- **A sAMAccountName never reaches Graph.** The filter is an equality match on
  `userPrincipalName`, so the page reports the cloud half as not checked rather than spending
  ten seconds to be told "no account matched" - a weaker sentence and an easier one to misread.
- **The combining logic is a pure service, not page code.** `Services/TrueLastLogonAnswer.cs`
  (`TrueLastLogonCombiner`, 9 tests). There is no bUnit harness here, so anything left in the
  .razor is reachable only by a source-level tripwire.

**One reachable case the tests caught before the page could show it.** `CloudSignInService`
marks the log as having answered only when BOTH its queries did, so an interactive query that
returns a real sign-in while the non-interactive one and `signInActivity` both fail arrives at
the combiner as `Unverified` CARRYING A DATE. Defining "the cloud was checked" as
`Verified != Unverified` alone would have printed a date and "Not checked" in the same breath.
`CloudChecked` therefore also accepts a date, and `NothingWasCheckedAndADateCanNeverBothBeTrue`
pins the invariant across every combination.

Gates at S3: build 0 errors, **3384 passed / 0 failed / 3 skipped**, format, ASCII,
`git diff --check`. Four mutations probed and all four bit: dropping the carries-a-date clause
(2 fail), requiring an answering DC for `OnPremChecked` (1), earliest-wins instead of
latest (3), and stripping `disabled="@IsBusy"` from the page input (2 ClickGate failures).

**NEXT ACTION on this item is NOT code. It is the live check, and it is the owner's:** one
recently-active account and one known-dormant account, looked up in the module and compared
against `Get-TrueLastLogon-Commercial.ps1`'s own output for the same two users. If they
disagree, the script is right until proven otherwise. The CLOUD half has never touched a real
tenant; the on-prem half has - the owner exercised the UPN search on dev 2026-10-01, after
`fcc94a4`.
It also needs `AuditLog.Read.All` consented on the app registration behind
whatever secret is pointed at it, and the module is `EnabledByDefault = false`, so it has to be
enabled in Module Config before anyone can see it.

**Two rules in this module are owner rulings and must not be quietly re-derived:**

1. **Coverage is reported, never a gate** (owner, 2026-09-30: *"you will NEVER get a response
   from ALL domain controllers. that cannot be a gate."*). An earlier version required a
   complete DC sweep before it would say no logon was found; that gate never opens in a global
   estate. A probe that re-introduces it fails a test on purpose.
2. **An absence is only as good as the source that reported it.** A source that answered "no
   sign-ins" and a source that FAILED are different facts. `signInActivity` under-reports - 9
   of 557 measured - so `ActivityOnly` is never sufficient evidence of dormancy.

**Do not open new queue items without a go.** The queue table above owns each item's standing.

**Run the suite with `-- xUnit.MaxParallelThreads=4`.** Why, and the host measurement behind it,
are in `.agents/machines.md` under Test tooling - it is a property of this box, not of the repo.

**TWO SESSIONS SHARED THIS WORKING TREE ON 2026-09-25 AND IT CAUSED REAL DAMAGE. READ THIS
BEFORE RUNNING TWO AGENTS IN ONE CHECKOUT AGAIN.** Three separate incidents, all on shared
files: (1) an uncommitted Migration `1.13.0 -> 1.14.0` bump was destroyed by the other session's
`git checkout -- Modules/ModuleCatalog.cs`, which it ran to undo its own unanchored `sed` after
explicitly identifying that line as another session's work - so `2a0dd6a` claims a bump it does
not contain, and `1.14.0` is a version that never existed as a committed state; (2) an earlier
commit of theirs, `7715780`, swallowed a different uncommitted Migration bump into a Risky Users
commit whose message says nothing about it; (3) `.agents/state.md` was repeatedly rewritten by
`head`/`sed` splices through "changed on disk" warnings, so uncommitted edits to it cannot be
reconstructed from git. **The version history in `Modules/ModuleCatalog.cs` carries the first
incident inline; none of it was rewritten, because history rewrites need explicit authority.**

### Carried forward from earlier rotations

Each of these sat inside a block that was archived whole. The records are verbatim in
`docs/history/state-archive.md` (Archived 2026-10-09 and 2026-10-14); what is still live is here.

- **Queue 8, Defender for Endpoint: both defects carried here are now FIXED; what remains is
  live verification.** Corrected 2026-10-14 as of `6bfb8a6`. (1) *The page was unusable at
  tenant scale* - closed by `43c0735`, which pages the device table at 50 rows while the CSV
  still carries the whole matching set. The fork recorded here as unanswered was answered by
  that shape: filter-first and paged, complete set on export. (2) *Discovery sources was the
  wrong data* - closed by `963b85d`, which resolves the observer column through `SeenBy()` by
  name discovery rather than a fixed guess. **What is still outstanding is not code:** the live
  reconnaissance pass R1 (a)-(e), and the fact that no device row has ever been rendered here.
  `## Now` owns the three environmental unknowns and how each fails visibly.
- **Queue 4: the aliases `MessageTrace` and `MessageTraceSearch` must NOT be renamed.** Kept
  live because no other file states it. Each is the section-access storage key and the store is
  fail-closed, so a rename orphans the groups granted against it and denies them rather than
  failing open. Operator-facing wording changes through `ModulePermission.DisplayName` instead;
  the alias stays on screen because it is what the denial log line names.
- **Cloud Password Reset (queue 10 and 11) is BLOCKED ON THE OWNER and nothing works until
  these are done.** (1) Create a dedicated Entra app registration with `User.Read.All`,
  `User-PasswordProfile.ReadWrite.All` and `RoleManagement.Read.Directory`, admin-consented -
  **do not reuse another module's registration.** (2) Create the Delinea record (`Tenant ID`,
  `Application ID`, `Client Secret`, no checkout workflow) and enter its id in the module's
  `GraphDelineaSecretId`. (3) Deploy, enable the module (it ships disabled) and grant the
  section-access groups. (4) Run the plan's manual acceptance checklist; **none of it has been
  run.** The two cases that matter most are a contrived two-people-share-an-employee-ID case and
  the CLEARED force-change case against an account whose sign-in path cannot service a change
  prompt.
- **Two Migration owner questions are still open and are his to settle.** (a)
  `ToggleBatchSelected` is registered in `ClickGateRegistry` as an ungated DOM-synced control on
  ruling D2(a), whose stated rationale was that it "makes no call and awaits nothing" - S3 made
  that false, because ticking down to one batch now fetches from Exchange. (b) R27 says loading
  must not be "a page-wide freeze"; its other half, that `IsBusy` should stop disabling
  everything, contradicts the later ruling that one page-level predicate gates every control.
  Both are flagged rather than taken.
- **Known and deliberately not acted on: the Migration mailbox pane re-sorts the whole set about
  five times per render**, and the filter box binds `oninput`, so it runs on every keystroke. At
  2000 mailboxes this is low single-digit milliseconds and there is no measurement. **If a large
  batch ever feels sluggish while typing in the mailbox filter, this is the first place to
  look**, and the fix is to compute the list once per render, not to cache between renders.
- **`CompleteAfter` today means "complete now"** - `MigrationService.cs` passes a PAST timestamp
  at two call sites and reads the property as an auto-complete boolean at a third. Queue item 12
  is owner-marked DONE; whether a real future schedule works is unverified here. **Flagged, not
  resolved.**
- **An unrelated finding the owner should chase: the DC intermittently refuses writes** with "A
  required audit event could not be generated for the operation" - measured at three of nine
  large writes during the Comms-10k work, twice consecutively at one size. Not caused by any
  module; it will surface as random failures in anything that writes AD.
- **A red gate early in a CI job hides every gate behind it.** The ASCII lint failure of
  2026-09-23 aborted the `powershell` job before PSScriptAnalyzer and Pester ran, so neither had
  executed in CI for two days and nobody noticed.
- **Emergency Disable's lockdown checkbox is an explicitly TEMPORARY escape hatch, and nothing
  else records that.** Carried out of the queue-18 block archived 2026-10-14. When the
  stakeholder says what they want, the checkbox either goes away or becomes the settled default;
  whoever closes that loop comes back to this bullet rather than leaving an unexplained option on
  a security screen forever. **The default has moved once already** (owner 2026-10-05 opt-OUT,
  superseded by 2026-10-06 default-UNCHECKED, both in `.agents/decisions.md`), which is why it
  lives in a single `LockdownDefaultArmed` constant on the page rather than as a literal at each
  of its three sites. `docs/EmergencyDisableLockdownOU-Plan.md` owns the design.

### Live defects found and deliberately not fixed

- **The Migration mailbox selection is never pruned and survives a batch change. Pre-existing,
  found by the codex review of the Remove completed slice (finding `rc-2`), and only the new
  control's half is fixed.** `Components/Pages/Migration.razor` -
  `AdoptSelectionAsOpenBatch` nulls `batchUsers`, blanks `mailboxFilter` and resets the page,
  and does NOT touch `selectedMailboxes`; `ReplaceBatchUsers` does not either.
  `MigrationUserActionPlanner.PruneSelection` exists for exactly this and its own remarks still
  say "Called after every reload" - **and it is called from no reload path**. Re-verified
  2026-10-14 as of `6bfb8a6`: its only mailbox-side caller is `TicksInThisBatch()`
  (`Migration.razor:2789`), the read the rc-2 fix added for the new control, so the remark is
  still false of every reload. Observable: tick a mailbox in batch A, open batch B, and B shows
  no ticked row while the mailbox Actions bar renders with `Actions (1)`; the five bulk actions
  then plan over an address the planner drops, so the banner reads "Nothing to do: none of 0
  selected mailboxes is eligible". Confusing, not destructive - the harmful direction is closed
  because the planner only ever acts on addresses it can find in `batchUsers`.
  **`RemoveCompletedCandidates()` prunes for itself** and is not affected. The general fix is
  one call, but it changes behaviour for five shipped actions and needs its own slice and its
  own guard; the batch side already has a `PruneSelection()` wrapper at `:2326` to copy.

- **A live defect was found while scoping queue 4 and is NOT fixed. It needs its own commit.**
  **Citations re-verified 2026-10-14 as of `6bfb8a6`; the defect itself is unchanged and still
  live.** `Components/Pages/MessageTraceReports.razor` carries only the main `MessageTrace`
  policy (`:4`, and the handler re-check at `:124`), and the listing has since moved into
  `Services/MessageTraceExportListing.cs` - `GetExports()` (`:119`) calls
  `_jobs.GetFinishedByType(ModuleName, JobType, ListLimit)` (`:121`) with **no submitter
  filter** - the table renders `@item.SubmittedBy` per row (`:79`) precisely because it is an
  all-operators listing - while `TryDownloadAsync` (`MessageTraceExportListing.cs:173`, reached
  from the page's `Download` at `:144`) checks ticket presence and job type and **no
  submitter**, so it serves any listed export to any policy holder. So every
  operator can download every other operator's full message-trace detail exports. This is
  independent of the permission split; the split makes it worse by admitting header-only
  operators to that page. Not caused by this run's work.

- **A defect in committed TEST infrastructure, found in plan review and not yet fixed.**
  `ExchangeAdminWeb.Tests/ClickGateSource.cs:364-384` (re-verified 2026-10-14 as of `6bfb8a6`
  - the citation is still exact and the method is still not quote-aware;
  the earlier `:213-228` citation was stale), `ExtractBlock`, counts `{` and `}`
  without being quote-aware, so a brace inside a string or interpolated string miscounts the
  depth and the extracted block ends in the wrong place - usually over-capturing into the code
  that follows. **The asymmetry is the tell:** `Tags()` in the same file IS quote-aware and its
  doc comment explains exactly why a naive scan breaks; `ExtractBlock` never got the same
  treatment. Consequence measured in the queue-4 review: an assertion looking for a `return`
  inside a denial block can find one that is actually in later code, so a handler that performs
  a protected operation after an authorization denial would pass. It is used by the live
  click-gating suite, so any assertion built on the block's END offset is suspect; assertions
  that only test containment within a generously-sized block are less affected. **Its own slice
  with its own guard proof** (an interpolated string containing a brace, placed inside an
  extracted block); deliberately not folded into another agent's work.

### Landed streams whose only remaining item is a manual acceptance pass

**Every stream below is DONE** (`.agents/decisions.md` 2026-10-12: *"these will come from
production use, do not keep asking for them. I'll report issues when they arise."*). A shipped
stream whose only remaining item is a manual acceptance pass is not "shipped, acceptance
outstanding" - it is finished. The checklists each plan carries are reference for whoever
investigates a reported issue, never a queue of owed work; the individual wordings below
predate that ruling and are kept for their technical detail, not their framing. What does NOT
change: there is no bUnit harness, so no source-level guard here proves anything renders. That
is a fact about the suite, not a task.

- **Mailbox Migrations no longer shows a stale open report; only the manual checks remain.**
  `docs/MigrationStaleReport-Plan.md` is Implemented and owns the acceptance checklist. Landed
  2026-09-17 in `3e4f13d`, with the review fix `2b93fdc` on top; Migration module version 1.8.2,
  no base app bump.
  The earlier batch-name-caching hypothesis was FALSIFIED by reading the page: the report was
  never keyed on batch name, and that fix would not have touched the reported repro, which
  reuses the name. Actual cause: `userReport` is a one-time snapshot, the modal's render guard
  (`Migration.razor:1211`, re-anchored 2026-10-14 as of `6bfb8a6`; the old `:771` citation was
  stale) keys on the report's own fields alone and not on the batch, and seven paths reloaded
  or discarded `batchUsers` without clearing it. One `CloseUserReport()` helper now owns the
  clear at all seven, and a `reportGeneration` counter drops the result of a fetch a reload
  superseded.
  Ten source-level tripwires guard it, anchored per branch; all three guard-proof mutations
  bit. Nothing here reaches the rendered page - no bUnit harness exists - so the plan's manual
  checks are the only evidence that the operator sees the fix. **Owner 2026-09-17: those
  checks are deferred indefinitely, not skipped** - migrations are production actions, and the
  owner will verify opportunistically the next time a real migration comes up. Do not re-queue
  them as blocking work and do not treat their absence as drift. **The implementation codereview is done** -
  codex, `@azure-openai-eus2-global/gpt-5.5-dzs` at xhigh, standard tier, over
  `1250220..3e4f13d`, verdict **findings (1)**, 2026-09-17, capability proof passed. Four of the five things it was
  asked about came back clean and stand as reviewed - no eighth reload path, the
  generation guard correct on all three continuation paths, no half-populated field
  combination, and the module-only bump right. The fifth produced **`msr-1`** (MEDIUM,
  `.agents/review/findings/msr-1.md`), **fixed 2026-09-17 in `2b93fdc`**: closing the report
  before the await was not enough, because the refresh-in-place paths leave the old rows and
  their **Report** button rendered for the whole Exchange call. One helper,
  `ReplaceBatchUsers`, now owns every assignment of `batchUsers` and closes the report
  itself, so the close lands after the await. **The lesson worth keeping is about the tests,
  not the code:** the original ten tripwires asserted that the close precedes the refetch,
  which the defective code did, so they passed the bug - the guard proof's first mutation is
  literally the pre-fix code and only the three new tripwires caught it. Assert the negative
  per occurrence (nothing assigns `batchUsers` outside the helper), not the ordering per
  method.

- **Module ordering is alphabetical and `SortOrder` is gone; the sidebar check is unrun.**
  `docs/AlphabeticalModuleOrdering-Plan.md` is Implemented and owns the manual acceptance
  checklist. Landed 2026-09-15 in `e5a8ccd`, `73be51c`, `7057f9c`, `12b699f`, `5090996`,
  `b657ac0`; app version 2.21.0. Every catalog-driven list now orders by `DisplayName` with
  `StringComparer.OrdinalIgnoreCase`, nav categories are named from `Modules/ModuleCategories.cs`,
  and the bottom sidebar block is selected by `ModuleCategories.Administration` rather than the
  old integer threshold. A new module declares no position: the author picks a category and the
  name decides the order. Blazor markup has no test harness here, so the rendered sidebar,
  Module Config tree and home tiles are unverified by automation - run the plan's manual checks
  after a dev deploy. The plan's implementation codereview has not been dispatched.

- **Service Health is implemented; design acceptance and manual checks remain.**
  `docs/ServiceHealth-Plan.md` owns the design and checklist. The original dashboard appearance
  is binding: no redesign, charts or gradients; incident HTML must pass through the sanitizer.
  Enablement, Graph secret ID and section access are now set; secret validity and live Graph
  authentication were not checked. Implementation codereview remains outstanding and needs a go.

- **Shared config cutover is reflected in both deployed appsettings files.**
  `docs/SharedConfigDb-Plan.md` is Implemented and its implementation codereview is closed.
  Do not repeat the one-time cutover based on the old state. Section 8 manual checks (startup
  logs and cross-instance refresh) remain unrecorded. Section 9's decision-wording and
  Constitution flags remain open. Jobs and usage stores stay per instance.

- **Usage telemetry and the save-then-prompt fix are landed.**
  `docs/UsageTelemetry-Plan.md` owns the telemetry implementation and acceptance checklist;
  `.agents/review/findings/utei-{1,2,3,4}.md` own the closed findings. The forced-reload fix is
  guarded by `AdminPageDirtyStateTests.ClearingDirtyStateIsRenderedBeforeAForcedReload`.
  Nothing is queued code-side. Run telemetry's manual checks and confirm saving module
  enablement no longer asks to discard already-saved changes.

- **Group bulk actions and cross-domain fixes are landed; live writes remain unverified.**
  `docs/GroupBulkActions-Plan.md` owns the checklist. Validate remove/re-add and bulk operations
  on a throwaway group: the original admin group is protected and cannot serve as the repro.
  Self-service's home-domain users-only add scope remains intentional. D1 used its drafted
  default (one batch administrator email, per-member audit and affected-user notices);
  the owner may overrule it. Implementation codereview remains outstanding and needs a go.

- **Intune Devices and Risky Users are implemented; remaining manual checks stay live.**
  `docs/IntuneDeviceManagement-Plan.md` and `docs/RiskyUsersModule-Plan.md` own scope and checks.
  Both registrations were proven live in owner validation recorded 2026-09-02; shared-store
  configuration remains present in the dated machine receipt. The owner confirmed Intune
  search on dev 2026-09-03 after `acfabe9`, superseding the earlier combined-filter uncertainty.
  Intune notification/Entra removal are act-time choices, not Module Config defaults.
  Risky Users reads audit without alert emails. Its direct ServiceNow validation, instead of
  the newer per-module seam, remains an unscheduled judgment call, not an admitted defect.

- **The remaining implemented queue awaits manual acceptance only. Do not restart it.**
  Checklists: `docs/BooleanConfigControls-Plan.md`, `docs/BitLockerMandatoryTicket-Plan.md`,
  `docs/ModuleCsvExport-Plan.md`, and `docs/EventLogCsvTicket-Plan.md`. The sidebar Home link
  removal (`2128610`) also needs a visual check; the brand link stays. BitLocker CSV includes
  keys under the owner's ruling, with ticketed disclosure audit and no keys in audit logs.

- **Nesting and protected group targets are implemented; remaining checks stay live.**
  `docs/GroupMemberNesting-Plan.md` and `docs/ProtectedGroupWriteTarget-Plan.md` own the lists.
  Cross-domain member listing was confirmed in both group modules on dev 2026-08-31;
  nested add/remove, cross-domain picker behavior and admin refusal still need applicable
  checks. Self-service is exempt from Protected Group Targets by the 2026-08-31 ruling;
  member protection stays. Review loops are closed; pgwt-3 remains declined at intake
  (`.agents/review/pgwt-3.contested.md`).

## Next

- **QUEUE 9 CLICK-GATING: TIER 1 IS COMPLETE. All nine approved pages converted, plus the
  harness, plus five defects fixed in already-shipped code. Tiers 2, 3 and 4 were APPROVED by
  the owner 2026-10-12 - the remaining 19 pages - and are NOT STARTED.**
  `docs/ClickGatingAudit-Plan.md` owns the findings and Revision 1 owns the design; read Revision 1
  before touching any page. **Manual acceptance is production use, not owed work**
  (`.agents/decisions.md` 2026-10-12); the plan keeps its checklist as reference for whoever
  investigates a reported issue. What stays true is the limit, not a task:
  nothing in this repo reaches the rendered page, there is no bUnit
  harness, and every assertion is a source-level scan that proves a shape, never a behaviour.
  Pages, in the order converted: DhcpAuthorization, NamedLocations, MailboxPermissions,
  CalendarPermissions, IntuneDevices, GroupManagement, M365GroupManagement, ConferenceRooms,
  SelfServiceGroups. Harness grew 14 -> 31 assertions + 3 fixtures, 266 instantiated cases;
  full suite 2510 -> 2791. Base app 2.21.0 -> 2.21.1 once, for the shared-component change only.
  **Defects found in code that had already shipped and been reviewed, all fixed:**
  1. `Migration` - two `@onkeydown` paths reached operations their buttons refused; one destructive,
     and it avoided double-execution only by accident. Seven bespoke tripwires and a codex review
     had missed them because nothing could see a keyboard path.
  2. `MailboxPermissions` - a tab click mid-write **revoked the permission the operator asked to
     grant**, and mislabelled the audit row and both emails on the way past.
  3. `CalendarPermissions` - the audit row and both emails said *Set* while the code performed a
     *Remove*, from one handler in one run.
  4. `ConferenceRooms` - `RemoveJob` re-looked its row up in a live windowed collection a
     background callback replaces, so a durable record could be hard-deleted with an audit row
     carrying no ticket and no old values.
  5. `SelfServiceGroups` - a null dereference **inside a catch block**, reachable today, which with
     no `ErrorBoundary` tears the circuit down.
  **Known limits of the harness, all measured rather than argued, all recorded on their entries:**
  - **Over-gating is undetectable.** Nothing distinguishes a correct exemption from a control gated
    into a trap. `SelfServiceGroups` M11 proves it.
  - **CLOSED in `ddb0f83`:** the button sweep matched the whole TAG, so a `title` naming the
    predicate satisfied it with the `disabled` attribute deleted - measured on page 9. It now
    reads the attribute value via `AttributeValue`. Two siblings shared the hole and were fixed
    with it, one worse than the original: the non-button refusal check tested for the bare WORD
    "disabled" anywhere in the tag, and **Migration's tab anchor carries a CSS class named
    "disabled"**, so a class name was satisfying a gating assertion.
  - A **token-guarded lowering** cannot be enforced (`GroupManagement`); reverting it to the shape
    that sticks the flag true and deadens the page fails nothing.
  - `ConferenceRooms`' **background-callback hazard** is structurally unreachable - the snapshot
    assertion is defined over a first await and the handler has none, which is the point.
  - `SpinnerExpressions` cannot see a duplicated condition; `ScannerFalsePositives` is read by no
    assertion at all; `ExemptControl.ConditionThatKeepsItTrue` and `BecauseOfControl` are prose.
  - `ExemptControl.PrerequisiteBeforeExemptionHolds` cannot express a **method-reference**
    exemption - it reads the tied field out of the snippet via a regex matching only an inline
    `() => field = null`.
  **Found and deliberately NOT fixed - each needs an owner go:**
  - **`IntuneDevices` typed device-name confirmation is markup-only.** `ExecuteActionAsync` never
    re-reads `wipeConfirmName`, so the only enforcement of the second key on a factory wipe is one
    clause in a disabled expression. A concrete server-side shape is proposed in the token log.
  - **`M365GroupManagement` validates no ticket anywhere** - no ServiceNow check, no handler
    re-check, so four markup clauses are the entire enforcement. Same shape on `NamedLocations`
    delete and twice on `ConferenceRooms`.
  - **`ConferenceRooms.CancelJob` audits nothing** while `RemoveJob` audits - cancelling a running
    bulk job mid-write to Exchange leaves no record. Constitution-shaped.
  - **`SelfServiceGroups` cross-group result bleed** - a previous group's operation can publish its
    banner into a different group's view. Misleading rather than wrong; closing it changes what
    those fields *are*.
  - **`RiskyUsers.razor` has `IntuneDevices`' old partial shape** (`ActionsDisabled` missing
    `historyLoading`, 4 of 7 buttons outside the gate). Tier 3, approved 2026-10-12, untouched.
  - **`BlockedSenderService.UnblockSenderAsync` still takes no `CancellationToken`** and the file
    has no timeout - the one confirmed live instance of the page-deadening hazard the whole sweep
    was about.

- **The owner's issue queue is the enumeration of outstanding items and this file does not copy
  it.** It lives at the path recorded in `.agents/machines.md` (machine-local, not in the repo,
  and not ours to write to - its own first line says so). Read it for the owner's wording and
  their own status markers. **The queue table in `## Previously` owns each item's current
  standing and this paragraph does not restate it** - the per-item summary that used to sit here was stale
  within days. The previous
  copy of the queue was rotated to `docs/history/state-archive.md` on 2026-09-23: its "not yet
  started" header was false for four of its six items, and the entries for 5 and 6 had been
  overtaken by owner rulings of 2026-09-21.

- **`docs/DevValidation-2.3.34.md` is reference, not a queue.** It owns older streams' checks -
  Admin Settings access, protected-user alias refusal and the reported MailboxPermissions
  friction; newer plans own their own. Per `.agents/decisions.md` 2026-10-12 these are not owed
  work and are not re-raised; read the file when investigating a reported issue. Both instances
  address live production AD/Exchange, so any manual write still needs authority for its named
  scope.
- **Coverage follow-up:** the 2026-08-14 ruling supersedes the standalone "one of three files
  done" task. `ProtectedPrincipalService` work was folded into the now-landed queued plans;
  re-measure on green CI and ratchet per `.agents/review/coverage-floor.txt`.
  `PermissionValidator` still needs its own approved plan for the credential-carrying I/O seam.
  Old percentages are dropped; the floor file owns the measured baseline.
- **Owner-deferred validation stays deferred:** Bulk Job Runner live UI and writes,
  ConferenceRooms protection, GM-3 self-service groups, and servicer override end-to-end proof,
  inverse denial and per-target batch notes. Sources: `docs/BulkJobRunner-Plan.md`,
  `docs/ConferenceRoomsFinderProtectedPrincipalGate-Plan.md`,
  `docs/SelfServiceGroupManagement-Plan.md`, and the archived 2026-08-11 servicer record.
  The owner accepted waiting for real production servicer use; do not re-queue it as urgent.
- **Module packaging/import stays deferred** (owner 2026-07-22, `.agents/decisions.md`).
  **AccountLockoutRemediation and its notification question stay parked** with the disabled
  module; disablement is re-verified in `.agents/machines.md`. No resumption authorized.

## Blockers

- **Falsified deployment/configuration blockers:**
  the old dev/prod versions, incomplete shared cutover and missing initial ServiceHealth,
  RiskyUsers and IntuneDevices configuration disagree with the host. Evidence is canonical in
  `.agents/machines.md`, whose receipt was re-measured 2026-10-12 as of `96cdb89` - **dev and
  prod are at the same base version and the same build, redeployed 2026-10-09.** This entry
  records a blocker that is GONE; it is kept only so the old claim is not re-derived. Manual
  acceptance is production use, not an open blocker (`.agents/decisions.md` 2026-10-12).
- **Queue 6, containerize: the SPN and gMSA work is not ours to do.** The owner's 2026-09-21
  ruling makes containerization a requirement, but a containerised app runs under Kestrel rather
  than IIS, so Windows Authentication needs a gMSA, a credential spec and SPNs registered in the
  forest - `deploy.ps1:381` removes the Negotiate provider today precisely to avoid that. That is
  the owner's AD team's work. Also unanswered and it changes the design: where "elsewhere" is. A
  container outside line of sight of on-prem AD and Exchange cannot do the AD, on-prem Exchange or
  DHCP work at all. Evidence: `.agents/decisions.md` 2026-09-21, items 3 and the closing paragraph.
- **SQLite upgrade is no longer blocked by package availability.** The 2026-06-26 decision's
  "no patched package exists" basis is falsified, re-verified 2026-10-14 as of `6bfb8a6`:
  `obj/project.assets.json` still resolves `SQLitePCLRaw.lib.e_sqlite3/2.1.11`, while
  [NuGet publishes newer builds](https://www.nuget.org/packages/SQLitePCLRaw.lib.e_sqlite3),
  including 3.53.3 (the package version identifies its SQLite engine).
  The [advisory](https://github.com/advisories/GHSA-2m69-gcr7-jv3q) still lists affected versions
  through 2.1.11 and its patched-version field says None. Compatibility is not established.
  Next action: plan and verify a supported update; do not suppress NU1903.
- **CloudPasswordReset residuals:** owner disposition of remaining survey export(s); current
  inventory differs from the old three-file claim (machine receipt). Survey registration
  `16866221-3e2b-4ea5-8aae-157b043b6d7c` was recorded as holding unnecessary
  `Directory.ReadWrite.All`, `User.ReadWrite.All`, `Group.ReadWrite.All` and
  `UserAuthenticationMethod.ReadWrite.All` grants. Consent was not re-queried under the hold;
  revocation needs separate authority.
- **Unsettled guidance conflicts:** the 2026-09-01 owner ruling in the archived queue permits
  one session for all slices with coding subagents; `.agents/repo-guidance.md` Token Budget
  and the 2026-08-27 decision still require a fresh Fable session per slice. Also, the 2026-07-31
  protected-principal input-validation rationale relies on this deployment's read visibility,
  while the 2026-09-11 rule rejects environmental safety assumptions. No contested rule changed;
  owner reconciliation is required.
- **SharedConfigDb record flags:** the decision says SQLite `data_version` / next read;
  implementation uses the store's change token with a throttle. The additive-only rule has
  operative homes in the decision, guidance and test but is absent from the Constitution.
  Evidence: `docs/SharedConfigDb-Plan.md` section 9. No ruling inferred.
- **Plan-status drift remains, re-verified 2026-10-14 as of `6bfb8a6`:**
  `docs/BlockedSendersLoadTiming-Plan.md` and
  `docs/Comms10kReplaceUx-Plan.md` still say Approved;
  `docs/ConferenceRooms-OnPremRoomListAdd-Plan.md` still says Approved / In progress.
  Implementation was recorded but full completion is unverified. Do not silently relabel them.
  `docs/AdminUIRedesign-Plan.md` remains In progress with manual checks.
  `docs/MessageTracePermissionSplit-Plan.md` still reads APPROVED and IN PROGRESS while queue
  item 4 is owner-marked DONE and slices 2-4 are unstarted - see the queue table row.
- **Unscheduled M365 protection gap:** group update/delete and owner adds were recorded as
  ungated, and protection configuration cannot identify a cloud-only group. Re-verified
  2026-10-14 as of `6bfb8a6` (gate called from `:255` and `:276` only, while `UpdateGroupAsync`
  is at `:125` and `DeleteGroupAsync` at `:143`): `Services/M365GroupManagementService.cs` calls its
  `CheckProtectedAsync` gate from the member/owner paths only - `UpdateGroupAsync` and
  `DeleteGroupAsync` still call neither.
  The owner excluded this module from the on-prem target work; no work approved.
- **Older questions:** `docs/MessageTraceNullRow-Plan.md` needs a live rerun; the upstream
  null-row cause remains undiagnosed (OQ-1). `docs/ProtectedPrincipalResolution-Plan.md`
  retains OQ-2 on whether the reported cloud-only mailbox targets should remain reachable.
  Alias-protection and MailboxPermissions fixes are not field-validated by deployment alone.
- **Ops gaps:** ConferenceRooms AD secret configuration on prod was previously outstanding
  and was not checked here. `deploy.ps1` still lacks native `-PlanOnly`; the pipeline dry-run
  is the recorded workaround. Reviewer sandbox capability was not re-probed; use dated
  machine notes, not superseded CLI-version assumptions.

## Verification

Commands and mandatory guards are owned by `.agents/repo-guidance.md` and `AGENTS.md`.

**Last full run, as of `3f16528` (the two commits after it are docs-only):** Release build
0 errors; full Release suite **3,830 passed / 0 failed / 3 skipped** with
`-- xUnit.MaxParallelThreads=4`; `dotnet format ExchangeAdminWeb.slnx --verify-no-changes
--no-restore` exit 0; `git diff --check` clean; `tools/Test-AsciiOnly.ps1` passed. **The full
gate ran before EACH of the six fix commits**, not once at the end.

**Mutation proof ran this session for every new test, nine probes across six fixes**, all bit.
Two are worth repeating. For `mde-6` the obvious probe - delete the escape disjunct - proves
almost nothing, because a guard satisfied by the disjunct's presence passes against a flag
defined as `true`; the second probe widened the state binding and failed, which is what pins
the flag to meaning something. For `mde-9`, one probe flipped the emit condition to `false`
and one deleted a reset site; both bit.

**Restores were done with `git apply -R` / `git apply` or `cp` followed by `touch`, and every
file was md5-checked against its original.** The trap this avoids is in `.agents/token-log.md`:
a restore that keeps the old mtime lets MSBuild skip the rebuild, so the test runs against the
mutated binary and reports a pass.

**A second trap was NOT avoided and cost a confusing round:** a Razor parse error produced a
build with 35 errors, the build output was tailed for warnings rather than grepped for
`error CS`, and the next `dotnet test --no-build` reported a confident FAIL from the previous
binary. **Grep builds for `error CS`; do not tail them.**

**PSScriptAnalyzer and Pester were last reported for `390bba6` and still stand:** no `.ps1` or
`.psm1` has changed since. Existing dependency and compiler warnings remain (39 at this head,
all pre-existing).

**What the 2026-10-14 drift sweep re-verified by reading the files** (`b357770`):
`ProgressRegistry` holds zero `KnownGap` entries; zero `spinner-border` matches under
`Components/Pages`; the citations in `## Previously` and `## Blockers` for queue 4, the M365
protection gap, `ClickGateSource.ExtractBlock`, `Migration.razor`, `MessageTraceExportListing`
and the SQLite package version all still land. **Its claim that `.agents/review/index.md` had
zero open rows was true only by omission** and is corrected - nine rows now stand there.

Browser acceptance, live writes and the live forest were not exercised. **Both instances were
redeployed 2026-10-09 at FileVersion `2.27.1.0`**, re-measured 2026-10-12 and recorded in
`.agents/machines.md`, which owns the receipt. **This head is well ahead of what is deployed**
- the nine Defender slices and all nine review fixes are on neither instance, and the whole
range is unpushed. Current CI status does not belong here. Per-finding status is owned by
`.agents/review/index.md`.

## Active sources

- `AGENTS.md`, `.agents/repo-guidance.md`, and `docs/ProjectConstitution.md` govern work.
- `.agents/decisions.md` owns decisions; `.agents/machines.md` owns host facts.
- Referenced plans own scope, status and acceptance checklists.
- `Modules/ModuleCatalog.cs` owns module versions and enumeration;
  `ExchangeAdminWeb.csproj` owns the current base app version.
