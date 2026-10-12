# App-Wide Verbosity Sweep - Plan

Status: **APPROVED at FULL scope, owner 2026-10-12 ("All 39"). Not started.** The section 10
question is answered: all 39 files, S0 through S10, not the seven-worst subset this plan
recommended. Queue item 25. Drafted 2026-10-12 against
`9106c6c`.
Owner: Michael
Base app version: shared-layer change (see section 9) - **one base bump at the end**, no
per-module bumps except where a slice changes that module's own behaviour text.

## Exec summary

**What it does.** Cuts the writing in the app down to what a person can read at a glance.
Roughly 124 blocks of operator-facing text across 39 files are too long, explain themselves,
or talk to the reader the way a chat window does. They get replaced with short, flat
statements of fact.

**What it gets you.** The app stops sounding like a transcript and starts sounding like a
tool. Your worked example - the True Last Logon banner that tells the reader which sentence
to read - is the clearest case, and there are 123 more.

**What it costs, and this is the part worth knowing before you say yes.** The writing is the
easy half. The app's test harness is keyed to **line numbers** inside every page file, so
deleting three lines of prose from a page silently invalidates every click-gate and progress
entry below it. That is true of all 30 page files in scope. On top of that, 20-plus test
files assert on the **exact wording** of specific sentences, several of them pinned there
because you approved that wording. So each page costs: rewrite, re-anchor the registries,
update the pinned tests. Estimate **9 to 13 sessions**, not the 2 or 3 the writing alone
suggests.

**The biggest risk.** Cutting words can delete meaning. Three of the longest offenders are
long because they are carrying a safety warning, a coverage caveat, or a legal-ish audit
notice. The rule below is written to protect counts, coverage facts and safety statements
explicitly - but a sweep is exactly the kind of change where a reviewer has to check what
went missing, not just what reads better.

**The cheaper alternative was OFFERED AND DECLINED.** The seven worst pages alone would have
been 47% of the problem for about 40% of the cost; the owner chose all 39 files on 2026-10-12.

**What approving it authorises.** The house style in section 3, and the slices you name in
section 8 - not automatically all of them. Nothing here changes what the app *does*.

**No decision is outstanding.** Full sweep, all 39 files.

## 1. The ask, verbatim

> 25. Do a verbosity sweep. Too much chat context leaked into the app, too many words for a
> human to read instantly. Example: "No logon found on what was checked
> Checked: 0 domain controller(s) answered and none had seen this account, but 37 did not
> answer and any of them could be holding a logon; the cloud was not checked. Read that
> sentence and not the headline. How much this is worth depends entirely on how much was
> checked, which is spelled out below."
> No editorializing anywhere in the app.

The quoted example is `Components/Pages/TrueLastLogon.razor:117-118` plus its surrounding
coverage block. It is an example, not the scope: the item says "anywhere in the app".

## 2. Measured scope

Surveyed 2026-10-12 at `9106c6c`. **~124 distinct operator-facing strings across 39 files** -
30 `.razor` under `Components/`, 9 `.cs` under `Services/`. Counting rule: one contiguous
rendered block is one string; a `switch` or expression-bodied table of operator-facing
sentences counts per branch.

Heaviest concentration:

| File | Offending strings |
|---|---|
| `Components/Pages/IntuneDevices.razor` | 14 |
| `Components/Pages/ModuleConfig.razor` | 11 |
| `Components/Pages/Migration.razor` | 8 |
| `Components/Pages/SelfServiceGroups.razor` | 7 |
| `Components/Pages/TrueLastLogon.razor` | 6 |
| `Components/Pages/MessageTrace.razor` | 6 |
| `Components/Pages/RiskyUsers.razor` | 6 |
| (remaining 32 files) | 66 |

Those seven pages hold **58 of 124 (47%)**.

Out of scope and not counted: code comments, XML doc comments, `@* *@` and `<!-- -->` markup
comments, `docs/`, `.agents/`, tests and fixtures. Nothing in this plan edits a comment.

## 3. The house style - the rule a slice is checked against

The owner's rule is "no editorializing anywhere in the app". Operationally:

**Cut these.**

1. **Instruction to the reader.** "Read that sentence and not the headline", "note that",
   "keep in mind", "it is worth noting", "Check the name - note that...".
2. **Meta-commentary about the result's own worth.** "How much this is worth depends entirely
   on how much was checked, which is spelled out below."
3. **Second person coaching and rhetorical questions.** "What do these actions do?", "You can
   load, filter and search now...", "Ticking this contradicts the button's own label".
4. **Restatement.** Any sentence whose content is already in the label, the count, or the row
   beside it.
5. **Chat residue.** Emoticons (`Migration.razor:86` ends a result with ` :)`), hedging
   connectives, and paragraphs repeated verbatim at three sites
   (`Migration.razor:112`, `:205`, `:375`).

**Keep these. A slice that removes one is wrong, not concise.**

1. **Counts and coverage facts.** "none of 34 that answered" is a materially weaker claim than
   "none of 37" and the difference is the whole point. Numbers survive.
2. **Plain safety statements of fact.** "This is not evidence of dormancy." stays. So does
   "This is a security risk." The *instruction* that follows it may be cut to an imperative.
3. **Audit and consequence notices.** `Home.razor:38-45` and `IntuneDevices.razor:33-41` are
   long because they tell an operator their actions are recorded. Shorten the prose; do not
   drop the fact.
4. **Irreversibility and scope warnings.** `InFlightWorkGuard.razor:101-108` ("Leaving this
   page will CANCEL it") is carrying a destructive consequence.

**Shape.** Two short sentences is the ceiling for a banner, tooltip or empty state. One is
better. Lead with the state, not the explanation. No paragraph where a clause works.

## 4. The two constraints that set the cost

### 4.1 Pages are line-keyed - every prose edit moves the keys

`ClickGateRegistry.cs` and `ProgressRegistry.cs` key their entries by **line number** within
each page file. `.agents/repo-guidance.md` Verification states it: *"A page edit that adds or
removes lines invalidates every `ClickGateRegistry` entry below it, because entries are keyed
by line number. Re-anchor with `.agents/playbooks/clickgate-reanchor.md`; do not hand-compute
a single offset."*

This applies to **all 30 page files in scope**, not a subset. `TrueLastLogon.razor` alone is
line-keyed at `ClickGateRegistry.cs:4813`, `ProgressRegistry.cs:2264` and
`GlobalProgressWiringTests.cs:378`.

**Operative rule for every slice:** after editing a page, run the clickgate-reanchor playbook
for that page. Never hand-compute an offset. Never batch two pages into one re-anchor without
re-running it per page.

### 4.2 Twenty-plus test files pin exact wording

Wording is **not** free app-wide. Hard locks, exact-equality on whole sentences:

- `RiskyUsersPageTests.cs:413-423` - `[InlineData]` exact-equality on all three
  `ActionConsequence` lines. The test is named
  `ActionConsequence_MatchesTheOwnerApprovedConfirmationLine`. **Owner-approved wording:
  changing it needs a ruling, not a rewrite.** Also `:149`, `:921`.
- `IntuneDevicesPageTests.cs:1145-1160` - exact-equality on `ActionHelp` first and first-two
  sentences. Plus `Contains` assertions at `:175-176`, `:523`, `:709`, `:1017`, `:1064`,
  `:1075`, `:1194`, `:1261`, `:1263`.
- `GroupMemberNestingProtectionTests.cs:179, 188, 199, 292, 865-866`.
- `GroupBulkActionsWiringTests.cs:332-333, 470` - pins the SelfServiceGroups paste-help
  sentence verbatim and asserts the IT-Support-Desk clause appears exactly once.
- `ProtectedPrincipalServicerAdminUiTests.cs:248, 261` - two sentences of the ModuleConfig
  servicing banner.
- `MigrationStatusPageTests.cs:1340-1353`, `MigrationMailboxLoadTests.cs:80`.
- `InFlightWorkGuardTests.cs:60-66` - includes **negative** assertions on removed phrasing.
- Further `Contains`-level pins: `MessageTraceExportUxTests.cs:36, 81, 93`;
  `ServiceHealthPageTests.cs:115-116`; `BitLockerRecoveryTests.cs:878, 1105`;
  `CloudPasswordResetEmailTests.cs:45-58`; `EmergencyDisableServiceTests.cs:493`;
  `IntuneDeviceServiceTests.cs:636, 1004, 1110, 1208`;
  `DefenderEndpointDeviceServiceTests.cs:208, 308`; `SelfServiceBulkResolveTests.cs:105`;
  `GroupMemberListingTests.cs:169`; `BulkIdentityListTests.cs:271`;
  `PermissionValidatorTests.cs:152, 390`; `SectionAccessGroupDirectoryTests.cs:226, 331`;
  `Comms10kMemberResolverTests.cs:147`; `TrueLastLogonIdentityFilterTests.cs:108`;
  `CloudSignInServiceTests.cs:459`; `MessageTraceWindowPlannerTests.cs:36, 46`;
  `LookupValidationTests.cs:44`.

**`TrueLastLogon.razor` prose is entirely unpinned** - no test asserts "Read that sentence",
"Checked:", "No logon found on what was checked", `CoverageSentence()` or
`VerificationMeaning()`. The owner's own example is free to rewrite. Its line count is not.

**This corrects a claim in `.agents/state.md`.** That file says *"Grep found NO test pinning
any of that copy, so the wording is free"*. That statement is true **only for True Last
Logon**, which is what it was measuring. It must not be read as app-wide; app-wide it is
false. Fix the state wording when slice 0 lands.

**Rule for a pinned string:** update the assertion in the same commit as the prose. For the
three `ActionConsequence` lines, stop and ask - the test name records them as owner-approved.

## 5. Non-goals

- **No behaviour change.** Nothing here alters what any module does, what it writes, who may
  click what, or any audit payload. A slice that changes behaviour is out of scope and goes
  back through planning.
- **No layout or component change.** Shortening text may let a banner shrink; it must not
  become a different control, move, or change colour semantics.
- **No comment edits.** The sweep is operator-facing strings only.
- **No translation or resource extraction.** Strings stay where they are.
- **Not a tone pass on `docs/` or `.agents/`.** Those are agent-facing by design.

## 6. Design

There is no new code and no new abstraction. Each slice is: edit strings on one page or one
service, update any test that pins them, re-anchor the registries for that page, commit.

**One new guard is proposed (slice 0) to stop the problem returning.** A source-level test
over `Components/Pages/*.razor` and `Components/Shared/*.razor` that fails on the banned
constructions of section 3 part 1 - the instruction phrases, the rhetorical-question opener,
and emoticons - checked against `ProgressScan.CodeView(body)` so comments and string literals
inside code do not trip it.

Two known limits, stated up front because this repo has been bitten by both:

1. **It cannot measure "too many words".** Length is not the defect; editorializing is, and a
   long correct sentence of counts must pass. The guard screens for named constructions only.
   Everything else is reviewer judgment.
2. **A source-text guard anchored on prose fails correct code** - finding `prog-6` in this
   repo, three guards broke on the word "finally" in a trailing comment. This guard must read
   `CodeView`, and its own allowlist must live in a registry file with a reason per entry, not
   as literals in the assertion.

## 7. Evidence a slice is done

Per `.agents/repo-guidance.md` Verification:

- `dotnet build ExchangeAdminWeb.slnx -c Release` - 0 errors.
- `dotnet test ExchangeAdminWeb.slnx` - the full suite, green.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`.
- `git diff --check HEAD`.
- `tools/Test-AsciiOnly.ps1` - **load-bearing here.** Rewriting prose is the single most
  likely way to introduce a smart quote, an em-dash or an arrow into a tracked file.
- The clickgate-reanchor playbook run for each page the slice touched, with its output in the
  commit message.
- **A diff-read for lost meaning:** for every string shortened, the commit message names what
  was dropped and why it was not a count, a safety statement or an audit notice.

New guard (slice 0) must be proven non-vacuous: reintroduce one banned phrase, watch it fail,
remove it, watch it pass.

## 8. Slices

One page or one coherent group per slice, one commit each, heaviest first. Each slice carries
its own re-anchor.

- **S0 - the guard and the records.** The section-6 guard, its registry and its non-vacuity
  proof. Correct the `.agents/state.md` "wording is free" claim per 4.2. No prose edits. No
  version bump.
- **S1 - `IntuneDevices.razor`** (14). Includes the `ActionHelp` exact-equality tests at
  `IntuneDevicesPageTests.cs:1145-1160` and nine `Contains` pins.
- **S2 - `ModuleConfig.razor`** (11). Includes `ProtectedPrincipalServicerAdminUiTests.cs:248,
  261`.
- **S3 - `Migration.razor`** (8). Includes the emoticon at `:86` and the paragraph repeated at
  `:112`, `:205`, `:375` - collapse to one shared string.
- **S4 - `SelfServiceGroups.razor`** (7). Includes `GroupBulkActionsWiringTests.cs:332-333,
  470`, which pins the paste-help sentence and asserts a clause appears exactly once.
- **S5 - `TrueLastLogon.razor`** (6). The owner's worked example. Prose unpinned; line count
  is not. Includes the four `VerificationMeaning` branches at `:532-542`.
- **S6 - `MessageTrace.razor` and `RiskyUsers.razor`** (6 + 6). **Stop at the three
  `ActionConsequence` lines** (`RiskyUsersPageTests.cs:413-423`) and ask - the test name
  records them as owner-approved.
- **S7 - the nine `Services/*.cs` strings.** `Comms10kReplaceWriter.cs:309-312`,
  `CloudPasswordResetService.cs:510-512`, `DefenderEndpointDeviceService.cs:627-630, 648-651,
  1277-1280`, `BitLockerRecoveryService.cs:229-230`, `CloudSignInService.cs:454-456`,
  `SelfServiceGroups/SelfServiceGroupService.cs:192-193`, `ConferenceRoomService.cs:252, 504`,
  `ProtectedPrincipalEntryValidator.cs:89-90`. No page re-anchor needed; service tests pin
  several.
- **S8 - `Components/Shared`** - `InFlightWorkGuard.razor:101-108, 127-130`. Note the
  **negative** assertions at `InFlightWorkGuardTests.cs:60-66`.
- **S9 - the long tail**, the remaining ~22 files at 1-3 strings each, batched by directory.
- **S10 - records and the base app version bump.** Status header, `.agents/state.md`,
  `.agents/token-log.md`.

## 9. Versioning

Operator-facing text is module-scoped behaviour where it lives on a module page: each of S1 to
S6 bumps that module's `Version` in `Modules/ModuleCatalog.cs` in its own commit. S8 touches a
shared component and S0 adds a shared guard, so the **base app version bumps once, at S10** -
not per slice. Authority: `docs/ProjectConstitution.md` Deployment And Versioning, and
`.agents/repo-guidance.md` Versioning. Both rules fire independently.

## 10. Open question for the owner - answer before any code

**Full sweep, or the seven worst pages?**

- **Full** - all 124 strings, S0 through S10, 9-13 sessions.
- **Seven worst** - S0 through S6 plus S10, 58 strings (47%), 4-5 sessions, and it covers
  every page heavy enough to have annoyed you.

Recommendation: **seven worst.** The long tail is 1-3 strings per file across 22 files, and
each one still costs a full re-anchor and verification cycle - the worst ratio of benefit to
cost in the sweep. S9 can be approved later on its own.

## 11. Assumptions

- The survey at `9106c6c` is a sample of judgment, not a closed enumeration. A slice may find
  strings the survey missed on a page it is already editing; fixing those is in scope.
- "Two short sentences" is the author's ceiling, not a measured threshold, and no test
  enforces it.
- The `ActionConsequence` lines are treated as owner-approved because the test name says so;
  no decision record was found for them. Confirm or correct at S6.
