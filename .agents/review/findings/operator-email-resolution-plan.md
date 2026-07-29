# operator-email-resolution-plan: openreview of the operator email resolution plan

**Severity**: HIGH (worst of 3 findings)
**Status**: Findings accepted (3 of 3); plan + state revised, re-review not yet run
**Branch**: `master` (committed directly per repo policy; no per-finding branch)
**Range reviewed**: `64b211a..ace6230` (plan commits `b7b9c94` + `3962904` + `186d4e0`, state commit `ace6230`)

## Dispatch

`Reviewer: codex-commercial (MCP) / gpt-5.6-sol / max / frontier`

- Transport: MCP thread `019fafbe-43fa-7812-8d2e-640be1c76e39`, `sandbox=read-only`,
  `cwd=D:\source\ExchangeAdminWeb`, `config.model_reasoning_effort=max`.
- `openreview` contract honored: the neutral question only ("Is the change as implemented
  the best way to achieve the goal?"), no rubric, no area list, no suspected risks, no
  prior conclusions. Mechanical coordinates (base/head SHA, repo location), disposable-
  worktree isolation, side-effect boundaries, and the verdict schema only.
- Base SHA: `64b211a`, the commit preceding the first plan commit. Both plan and state
  commits landed directly on `master`, so `git merge-base` would have returned the head.
  `git diff --stat 64b211a..ace6230` = 2 files, +305/-3.
- **verdict: findings** (3), `capability_ok: true`, both SHAs echoed correctly. Envelope
  parsed clean against the embedded schema (the MCP `codex` tool has no `--output-schema`
  flag, so the schema rides the prompt per `.agents/machines.md`).

## What was reviewed

`docs/OperatorEmailResolution-Plan.md` (Draft) and its `.agents/state.md` entry. A plan
document, not shipped code: findings are defects in the *intended design*, and the repair
is a plan revision. No code exists for this work stream.

## Findings

### F1 (HIGH) -- resolve the operator by their authenticated SID, not by samAccountName through an autocomplete query

**Accepted as raised. This replaces the plan's central design choice.**

Reviewer evidence, each citation re-opened and confirmed in the working tree:

- `Components/Pages/SelfServiceGroups.razor:325` -- `callerSid = user.FindFirst(ClaimTypes.PrimarySid)?.Value;`
  with the comment at `:322-324` stating the Negotiate scheme populates it. **Negotiate
  does carry an identity claim this app already consumes.** The plan asserted the token
  carries "the account name and group SIDs" and reached for a name-based lookup; the
  primary SID was available the whole time.
- `Services/SelfServiceGroups/SelfServiceGroupService.cs:672-685` -- `ResolveCallerDn`
  does exactly the lookup this plan needs: `Get-ADUser -Identity <SID>`, bound parameter,
  no interpolation. Its own doc comment at `:64-68` rejects "an alternate identity form
  (DN, GUID, sAMAccountName)" **by name**, and `:73` hard-validates the input is a SID.
  The repo has already settled how to turn an authenticated principal into a directory
  object, and settled it against the exact approach this plan proposed.
- `Services/ADDirectorySearchService.cs:144` -- `(sAMAccountName=*{escaped}*)`, capped by
  `ResultSetSize` at `:149`.

Predicted failures, all of which the plan's exact-match filter fails to close:

1. **UPN-shaped input cannot ever match.** The plan says at `:206` that a UPN-shaped
   input must resolve, and at `:150` that the filter is an exact samAccountName match.
   `user@contoso.com` never equals `user`. Those two requirements contradict each other.
2. **Short accounts are unreachable by construction.** `:69-70` rejects terms under 3
   characters, and the plan at `:155-156` accepts that as "return null and log". A
   two-character account holder silently gets no pre-fill and a false AD-unreachable
   refusal on historical search.
3. **The cap can hide the exact match.** A common substring returns 25 rows before the
   exact match is reached; the exact-match filter then finds nothing in a truncated set
   and returns null. The plan's ambiguity rule cannot detect this -- it looks like a
   clean "no match".
4. **Dropping the `DOMAIN\` prefix discards identity.** The plan at `:127-129` strips it
   and queries the remainder. Across a trust, two domains can hold the same
   samAccountName; the search is not domain-scoped, so the exact-match filter can return
   one row that is confidently the *wrong person* -- and mail a mail-flow report to them.
   The plan's "2+ matches means stop" guard does not fire, because only one row matched.

Note what survives: the wildcard hazard the plan identified (asking for `jdoe` also
returns `jdoe2`) was real and correctly diagnosed. The plan's error was repairing it with
a post-filter over an unsuitable query instead of replacing the query.

Repair (plan Design, Tests, and the D1 note): resolve from `ClaimTypes.PrimarySid` via a
bound `Get-ADUser -Identity <sid>`, mirroring `ResolveCallerDn`. A SID is immutable,
unambiguous, domain-qualified, and needs no length minimum, no wildcard, no cap, and no
post-filter -- all four predicted failures disappear rather than being guarded against.
`ADDirectorySearchService.Search` is not used for identity resolution at all; the new
`FindUserBySid` method rides its pooled runspace. The name-based fallback the reviewer
allowed is **not** adopted: an absent SID claim returns null and takes the D3 path, which
is what D3 already ruled for an unresolvable address.

### F2 (MEDIUM) -- a synchronous resolve in `OnInitializedAsync` can block the circuit for 30 seconds

**Accepted as raised.**

Evidence, verified:

- Plan `:121` declares `public virtual string? Resolve(string? accountName)` -- synchronous
  -- and `:168` calls it directly from `OnInitializedAsync`.
- `Services/ADDirectorySearchService.cs:80` -- `_runspaceLock.Wait(TimeSpan.FromSeconds(30))`.
  The lock is process-wide on a singleton (`Program.cs:133`), so any concurrent AD search
  anywhere in the app, or a cold `ActiveDirectory` module import, is on the critical path.
- `Components/Shared/ADIdentityAutocomplete.razor:104` -- `await Task.Run(() => ADSearch.Search(...))`.
  The repo already treats this call as blocking and already moves it off the renderer
  thread. The plan reintroduced the blocking pattern the shared component exists to avoid.

Predicted failure: opening Message Analysis stalls server-side rendering and the Blazor
circuit for up to 30 seconds, for a value used only to pre-fill a text box that the D4(a)
ruling explicitly says is a default and not a floor. The page is unusable while the least
important thing on it loads.

Repair (plan Design + Verification): the resolver exposes `ResolveAsync`, doing the
blocking PowerShell work under `Task.Run`. The page renders immediately and fills the box
when the lookup returns. The reviewer's late-write hazard is adopted as a rule: the
resolved value is written into the box **only if the operator has not typed into it yet**,
so a slow lookup can never overwrite input already given. Historical search awaits the
resolution rather than reading a possibly-unset field.

### F3 (LOW) -- `.agents/state.md` gives two incompatible answers about what is running on dev

**Accepted as raised. This is a defect in the state edit under review, not in the plan.**

Evidence: `.agents/state.md:124-125` records the owner reporting the missing pre-fill on
dev "against `2.3.31`". `:181-182` says `2.3.31` is "**not yet deployed anywhere**" and
`:185` says "prod and dev are both on `2.3.30`".

Both cannot be true: the recipient box does not exist before `2.3.31` (it landed in
`2f0b99c`), so a report that the box fails to pre-fill cannot come from a `2.3.30` host.

Predicted failure: a later session picks a deploy baseline from whichever line it reads
first, and either redeploys over a newer build or validates against the wrong version.

Repair: the contradiction is recorded as an open question for the owner rather than
silently resolved. Which line is stale is a fact about the host that the repo cannot
settle by reading itself -- guessing a version into the canonical state file is exactly
the failure the flag-conflicts invariant exists to prevent. `.agents/state.md` now flags
both readings and names the one-line check that settles it (the version in the dev
sidebar, which `BuildInfo` renders).

## Guard proof

Docs-only. No automated test guards a plan document. Static read-through instead: every
line reference in every finding was re-opened in the working tree and confirmed before
the repair was written (Known Failure Class 4, "never trust remembered file contents").
The two citations that carried the review -- `SelfServiceGroups.razor:325` and
`SelfServiceGroupService.cs:672-685` -- were read in full rather than grepped, because F1
turns entirely on whether the SID precedent is real and whether it rejects the plan's
approach by name. Both hold.

## Coder dispute (if any)

One partial, on F1's remedy. The reviewer offered a name-based fallback ("if a name
fallback is required, use an exact escaped LDAP filter and preserve domain identity").
Not adopted. A fallback path exists to handle an absent `PrimarySid` claim, but D3 has
already ruled what happens when the address cannot be resolved -- historical search
refuses, and the pre-fill is simply empty. Adding a second, weaker resolution path to
avoid that ruled outcome would reintroduce the ambiguity F1 removes, for a case the owner
has already decided is acceptable. Recorded here rather than settled silently.

## Known gaps for a re-reviewer to grade explicitly

1. `ClaimTypes.PrimarySid` is load-bearing under the F1 repair but is verified only by
   precedent (`SelfServiceGroups.razor:325` and the module working in production), not by
   a direct observation of the claim set on this deployment. Manual check 7 was added to
   confirm it, and the resolver fail-softs to null if the claim is absent.
2. F2's "only fill an untouched box" rule is page-level behavior. The repo has no bUnit
   harness, so it is manual-check-only (check 8). A future bUnit harness would make it
   testable; adding one is out of this plan's scope.
3. The plan is still **Draft**. F1 changes its central mechanism, so the owner is ruling
   on a materially different design than the one first drafted. No owner ruling (D1-D3)
   was contradicted by any repair -- D1 asked for an AD lookup and still gets one; the
   SID is only the key it looks up by.
