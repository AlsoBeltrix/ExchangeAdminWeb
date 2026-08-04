# Review status

Workflow: see `.agents/playbooks/codereview.md`.
Per-finding detail: see `.agents/review/findings/<id>.md`.

## Legend
- `[ ]` Admitted, open (passed intake triage; not yet started)
- `[~]` In progress / pending review
- `[x]` Verified (awaiting owner-gated merge / implementation)
- `[!]` Contested — declined, disputed, or ruled invalid; awaiting owner adjudication
- `[-]` Declined at intake (kept for the record; no work)

## Findings

| ID                | Severity | Impact (one line)                                             | Status | Branch | Reviewer |
|-------------------|----------|---------------------------------------------------------------|--------|--------|----------|
| pp-finder-1       | HIGH     | Protected room editable via single-room Finder (no PP gate)   | `[x]`  |        | codex/gpt-5.5-dzs/xhigh/std (finding+plan r1-3); codex-commercial/gpt-5.6-sol/max/frontier (consolidation r4) |
| gm3-task2-slice1  | n/a      | Slice-landing review: list-time member-write eligibility      | `[x]`  |        | codex/gpt-5.5-dzs/xhigh/std — accepted, no material issue (1a0cf58) |
| gm3-task2-slice2  | HIGH     | SID gate accepted SDDL aliases (alternate-identity to AD)     | `[x]`  |        | codex/gpt-5.5-dzs/xhigh/std — reopened (d85c511) → accepted after fix (e748e32) |
| gm3-task3-slice1  | n/a      | Slice-landing review: module descriptor + page skeleton       | `[x]`  |        | codex/gpt-5.5-dzs/xhigh/std — accepted, no material issue (ba22cf5) |
| gm3-task4-slice1  | n/a      | Slice-landing review: in-list filter (AC9), pure client-side  | `[x]`  |        | codex-commercial (MCP, default) — accepted, static-only, no material issue (f17f3de) |
| gm3-task5-slice5a | n/a      | Slice-landing review: member add/remove decision core (6.5)   | `[x]`  |        | codex-commercial (MCP, default) — accepted, static-only, no material issue (08a2a53) |
| gm3-task5-slice5b | n/a      | Slice-landing review: live member add/remove write path (6.5) | `[x]`  |        | codex-commercial (MCP, default) — reopened (6fd722f: F1 check/write mismatch, F2 write not fail-closed) → accepted after fixes (5ef1b0d), static-only |
| gm3-task5-slice5c | n/a      | Slice-landing review: member add/remove UI + audit/notify (6.5)| `[x]`  |        | codex CLI (codex-cli 0.145.0, codex exec, default) — accepted, static-only, no material issue (b461fed). MCP route abandoned (idle-timeout, then no-local-reader invalid); switched to headless CLI per owner |
| mt-detail-slice1  | n/a      | Slice-landing review: MessageTraceDetail models (MT plan s1)   | `[x]`  |        | codex/gpt-5.5-dzs/xhigh/std (codex-cli 0.145.0, codex exec, default) — accepted, no material issue, build-verified (ade48c1) |
| mt-detail-slice2  | n/a      | Slice-landing review: per-message delivery-detail service (s2) | `[x]`  |        | codex CLI (codex exec, default) — reopened (1f0af9c: outer fail-soft gaps, cloud pre-delegate + on-prem pre-Task.Run throttle) → round-1 fix landed (RunDetailBackendAsync seam + 2 tests, 768/768) → round-2 accepted (b00c5b7, guard+capability confirmed, no comments) |
| mt-detail-slice3  | n/a      | Slice-landing review: pure detail-export CSV builder + threshold helper (s3) | `[x]`  |        | codex CLI (codex exec, gpt-5.5-dzs/xhigh/std) — accepted r1 (2df0f48, base 7181db5); threshold guard confirmed (mutate -> 4 fail, restore -> 25 pass) in isolated copy tree, capability build EXIT=0, no comments |
| mt-detail-slice4  | n/a      | Slice-landing review: detail-export email + zip attachment + resolver (s4) | `[x]`  |        | codex CLI (codex exec, gpt-5.5-dzs/xhigh/std) — accepted r1 (2575467, base 09a9605); exfiltration rule confirmed (recipients only user + admins), dedup guard confirmed (remove .Distinct -> FAIL, restore -> PASS) in isolated tree, capability build EXIT=0, no comments |
| mt-detail-slice5  | n/a      | Slice-landing review: detail-export bulk job processor + payload + detail seam + tests (s5) | `[x]`  |        | codex CLI (codex exec, gpt-5.5-dzs/xhigh/std) — accepted r1 (7a307ac, base e1751af); no-silent-drop + exfiltration + cloud-cost (fetch-once/retain) + fail-loud-save/fail-safe-completion + DI lifetimes confirmed, guard confirmed (drop detail.Error->Failed mapping -> ProcessRow_FetchError test FAIL 1/5, restore -> 6/6) in isolated tree, capability build EXIT=0, no comments |
| mt-detail-slice6  | n/a      | Slice-landing review: per-row Details drill-in + inline trail + audit, live path (s6) | `[x]`  |        | codex CLI (codex exec, gpt-5.5-dzs/xhigh/std) — reopened r1 (68ab114, base a5f3fa7): F1 stale-response race (2nd row openable mid-fetch -> earlier response overwrites newer row's detail + clears newer spinner) -> r1 fix (6960887: disable-all-while-in-flight + monotonic request-token guard) -> accepted r2 (base 68ab114); F1 closed, fail-soft/audit/cloud-cost preserved, capability build EXIT=0, no further comments |
| mt-detail-slice7  | n/a      | Slice-landing review: selection + threshold-driven download/email of detail export (s7) | `[x]`  |        | codex CLI (codex exec, gpt-5.5-dzs/xhigh/std) — accepted r1 (99cf4a1, base 1dd74e1); exfiltration gate (payload UserEmail-only, no address input) + threshold re-checked in-handler (not UI-only) + select-all/manual cap at EmailMax + audit both branches + no-drop/result-order + state reset all confirmed, capability build EXIT=0, no comments |
| mt-detail-slice8  | n/a      | Slice-landing review: MessageTrace module version bump 1.1.1 -> 1.2.0 (s8) | `[x]`  |        | codex CLI (codex exec, gpt-5.5-dzs/xhigh/std) — accepted r1 (e7ce73c, base a6ae3d0); correct-layer bump (module Version only, csproj base app version untouched per Constitution), no collateral change, count guard 23 holds (ModuleCatalogTests 24/24), capability build EXIT=0, no comments |
| mt-detail-slice9  | n/a      | Slice-landing review: verification + manual-validation note, plan marked Implemented (s9) | `[x]`  |        | codex CLI (codex exec, gpt-5.5-dzs/xhigh/std) — accepted r1 (b5e6688, base 6a3298c); docs-only (only docs/MessageTraceDetail-Plan.md changed), per-slice claims cross-checked against index rows s1..s8 (SHAs corroborated), deferred manual-validation items honestly recorded as not-executed (no dev tenant), capability build EXIT=0, no comments |
| mt-export-delivery-plan | HIGH | openreview of the MessageTrace export delivery plan v2 (docs only) | `[~]` |        | codex-commercial (MCP, gpt-5.6-sol/max/frontier, base 68bfd25..1e98eaf) — **findings** (4), capability_ok. F1 HIGH: with the attachment removed, `SaveToLogPath`'s swallowed catch turns a save failure into a "ready" email + an Expired row (accepted). F2 MED: plan allowed a blank ticket while claiming the download "requires" one (accepted; presence enforced, validation still never). F3 MED: relative-link fallback is unusable from an email (accepted, remedy modified — omit the link rather than fail the send, which would violate fail-safe completion). F4 MED: global `Export:RetentionDays` breaks Constitution:59 module-config rule + creates a second retention truth (accepted; key dropped, 30 pinned as a constant). F5 coder-raised: v2 dropped v1's Constitution-conflict section (restored). Plan revised; re-review pending |
| operator-email-resolution-plan | HIGH | openreview of the operator email resolution plan (docs only) | `[~]` |        | codex-commercial (MCP, gpt-5.6-sol/max/frontier, base 64b211a..ace6230) — **findings** (3), capability_ok, both SHAs echoed. F1 HIGH: resolving the operator by samAccountName through the autocomplete wildcard search fails four ways an exact-match post-filter cannot close (UPN input can never match; sub-3-char accounts unreachable; ResultSetSize can truncate the exact row; dropping `DOMAIN\` lets a cross-trust collision return one confidently-wrong user). Accepted — plan now resolves by `ClaimTypes.PrimarySid` via bound `Get-ADUser -Identity`, per the existing `SelfServiceGroupService.ResolveCallerDn` precedent; the reviewer's name-based fallback declined (D3 already rules the unresolvable case). F2 MED: a synchronous resolve in `OnInitializedAsync` can block the circuit 30s on the shared AD lock (accepted — `ResolveAsync` under `Task.Run`, late result fills the box only while untouched). F3 LOW: `state.md` says both "2.3.31 deployed nowhere / dev on 2.3.30" and "pre-fill missing on dev against 2.3.31" (accepted — flagged as an unresolved conflict + OQ-4, not guessed). Plan revised; re-review pending, plan still Draft |
| excl-users-retire | n/a      | Whole-diff review: retire Security:ExcludedUsers appsettings fallback (8 commits) | `[x]`  |        | codex CLI (codex exec review, gpt-5.5-dzs/xhigh, base 3a1c79f..cb42c10) — accepted, "no actionable correctness issues"; build + targeted PermissionValidator/ProtectedPrincipalService tests passed. One raised concern (GetLegacyExclusions lacks IsModuleCorrupt guard) declined as stale: post-SQLite IsModuleCorrupt means DB-integrity failure, not JSON parse (ModuleConfigService.cs:38-51); not introduced by this diff, out of plan scope. Codex's ptk MCP disabled to avoid nested-runspace deadlock (ptk issue #12) |
| ppv-1 | HIGH | Cloud-only principal reached by alias returns NotFound, so the allow-on-NotFound gates let it through | `[x]` | | codex/gpt-5.5-dzs/xhigh/std (codex-cli 0.146.0, generation pass, base 10d1593..521bb6e, capability_ok) — fixed `a6927b2`; branch on ExistsOnPrem not address equality; probe fails 5 |
| ppv-2 | MEDIUM | DOMAIN-prefix stripping mangles a DN with an escaped comma, refusing a valid group/OU and badging saved ones stale | `[x]` | | codex/gpt-5.5-dzs/xhigh/std (same pass) — fixed `0940964`; DN shape check before stripping; probe fails 5 |
| ppv-3 | MEDIUM | Save mid-validation persists the list without the pending entry and still reports success | `[x]` | | codex/gpt-5.5-dzs/xhigh/std (same pass) — Save disabled + server-side refusal; probe fails 1. Browser timing manual-check NOT run |
| ppv-4 | LOW | Live AD tests reported passed, not skipped, when the directory is unreachable | `[x]` | | codex/gpt-5.5-dzs/xhigh/std (same pass) — Assert.SkipWhen at 5 sites; simulating no-AD now yields "Skipped: 5" where it previously said "Passed: 5" |
| sid-1 | HIGH | An unmigrated name row still authorizes, so the same-name ambiguity survives the whole work stream | `[x]` | | codex/gpt-5.5-dzs/xhigh/std (codex-cli 0.146.0, generation pass, base b872861..0a50d01, capability_ok) — fixed `54e762d`; non-SID allowed values discarded in handler, checker and job snapshot; probe fails 7 |
| sid-2 | MEDIUM | Legacy sectionaccess.json imports AFTER the migration, leaving names in the table for the process lifetime | `[x]` | | codex/gpt-5.5-dzs/xhigh/std (same pass) — fixed `019b814`; SectionAccessService resolved before the migration; probe reproduces the bug, fails 1 |
| sidf-1 | HIGH | The sid-1 fix filtered STATIC AdminGroups too, locking every admin out of the admin page on deploy | `[x]` | | codex/gpt-5.5-dzs/xhigh/frontier (fallback grade) (codex-cli 0.146.0, second pass, base d2844e1..019b814, capability_ok) — filter scoped to the dynamic store; verified against LIVE PROD config (AdminGroups = ANALOG\ExchangeWebAdmins, a name); probe reinstating it fails 2 |
| tsr-1 | MEDIUM | Coverage ratchet set 0.7 points below the measured baseline, so tests could be deleted and CI still pass | `[x]` | | codex/default configured model (gpt-5.5-dzs @ xhigh)/std (codex-cli 0.146.0, generation pass, base 802ea74..2543fb9, capability_ok) — floor moved to a committed file at the measured value, comparison de-rounded, 11 Pester tests added for the gate itself; probe: 64.9% coverage passed the shipped gate (exit 0), fails after the fix (exit 1) |

Notes:
- **sidf-1 was the OWED FRONTIER PASS over the whole SID work stream** (`d2844e1..019b814`,
  including the sid-1/sid-2 fixes). It found a HIGH defect **introduced by the sid-1 fix itself**:
  that fix filtered non-SID values on every requirement, including the static
  `Security:AdminGroups` from appsettings, which no migration converts and which is deployed here
  as `ANALOG\ExchangeWebAdmins`. Deploying would have locked every admin out of `/admin-settings`
  — the page needed to repair section-access fallout, so the failure removed its own remedy.
  Verified against the live prod config, not the sample. **The argument for second passes over
  security-critical code, in one finding: the first pass's fix was the second pass's defect.**
- **Frontier tier resolved 2026-08-03/04 (owner ruling).** The recorded pin
  `@azure-openai-eus2-global/gpt-5.6-sol` 404s at the gateway. The owner ruled codex at its
  default configured model is the strongest reviewer available here, so frontier now resolves to
  the same pair as standard (`gpt-5.5-dzs` @ xhigh) with **`grade: fallback`**. Probing also
  established that effort `max` is REJECTED by this gateway (supported:
  none/minimal/low/medium/high/xhigh), so xhigh is the ceiling. Consequence per the playbook: a
  future escalation halts to the owner instead of redispatching, because it would buy nothing.
- **tsr-1 came from a generation-half dispatch over the test-remediation work**
  (`802ea74..2543fb9`), on the owner's instruction to use codex with its DEFAULT configured model
  (no `--model` flag). Verdict **findings** (1), `capability_ok`, both SHAs echoed. The prompt
  asked the reviewer to scrutinise two specific claims: that the seam extractions were
  behavior-neutral, and that the coverage gate could not pass vacuously. **It cleared the first
  and found the defect in the second** -- the gate added to catch regressions was itself the
  regression risk, shipped with 0.7 points of slack while the plan claimed it had none.
- **sid-1..2 came from one generation-half dispatch** over the four SID-storage slices
  (`b872861..0a50d01`). Verdict **findings** (2), `capability_ok`, both SHAs echoed. Both were
  verified against the code before any fix; neither was declined. **sid-1 contradicted a claim
  I made in the slice-3 commit message** -- that an unmigrated store "fails CLOSED" under exact
  comparison. It does not: measured on a domain-joined host, `IsInRole("Domain Users")` is
  **true**, so `WindowsPrincipal.IsInRole` resolves names as well as SIDs. Until the fix, a
  deferred or halted migration left name rows authorizing exactly as before -- during precisely
  the window the migration was designed to survive. The lesson is the same shape as ppv-1: the
  guards were sound, the reasoning about what they guaranteed was not.
- **Routing exception, both findings:** T1 (sensitive paths) matched this diff and should have
  routed to **frontier**. The recorded frontier pin `@azure-openai-eus2-global/gpt-5.6-sol`
  returns 404 at the gateway, so the pass ran at **standard** (`gpt-5.5-dzs` @ xhigh). Recorded
  in `.agents/review/harnesses.local.json` under `tiers.frontier.unavailable`. **A frontier
  re-review of this range is still owed** once the owner names a live frontier model.
- **ppv-1..4 came from one generation-half dispatch** (`codereview codex <model> xhigh
  10d1593..521bb6e`) over the two protected-principal work streams: Exchange-backed
  resolution and admin input validation, 10 commits, ~2950 lines. Verdict **findings** (4),
  all four verified against the code before any fix, all four fixed one-per-commit with a
  non-vacuity probe each. No finding was declined. The HIGH one (ppv-1) is notable: the
  Exchange-fallback work closed the alias bypass for on-prem principals but reinstated it
  for cloud-only ones, because it branched on address equality instead of the `ExistsOnPrem`
  flag it had itself introduced and never read.
- Finding **confirmed real** (round 1), fix **plan reviewed & accepted** (rounds 2-3
  page-seam; round 4 consolidation C2-G), and **implemented** 2026-07-21 (commit
  2a97d09; `ConferenceRoomProtectionGate`, 672 tests pass, non-vacuity verified).
  Only follow-up: live-tenant/UI validation (deferred, no dev tenant).
- Scratch dispatch artifacts (`*.prompt.txt`, `*.schema.json`, `*.result.json`) are
  left untracked pending the owner's commit-vs-clean decision.

