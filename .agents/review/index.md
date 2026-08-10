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
| blr-1 | HIGH | A pasted 48-digit recovery key was written to the audit log in cleartext | `[x]` | | codex/gpt-5.5-dzs/xhigh/std (codex-cli 0.146.1, generation pass, base 81fd069..e39e18f, capability_ok) — fixed in `53f3ac5`; audit target now parsed and redacted at the page, key IDs kept verbatim; guard: revert -> 4 fail, restore -> 36 pass |
| blr-2 | MEDIUM | A capped search that found nothing told the operator it had searched successfully | `[x]` | | codex/gpt-5.5-dzs/xhigh/std (same pass) — fixed in `61552d9`; zero-results branch reads Truncated first; reviewer's paging suggestion declined as a search-strategy change, recorded in the finding; guard: revert -> 3 fail, restore -> 42 pass |
| blr-3 | HIGH | A second search showed a false "no keys found, searched successfully" while the live AD query was still running | `[x]` | | **Owner, on dev** — not review, not tests. `SearchAsync` emptied `results` but never cleared `searched`, so the zero-results branch rendered over emptied data for the whole in-flight window. First search after a page load was always fine, which is why it survived. Render gate now also requires `!isSearching`, and the previous answer is retracted before the new query runs; guard: revert -> 3 fail, restore -> 45 pass |
| blr-4 | MEDIUM | A running search showed nothing at all, so the page read as hung | `[x]` | | **Owner, on prod — caused by the blr-3 fix.** Suppressing the stale result left the results area blank for the seconds a live AD query takes. In-flight indicator added, plus a forced render + `Task.Yield` before the work: `Microsoft.Data.Sqlite`'s `*Async` methods complete synchronously, so the handler could finish the archive query without ever yielding to the renderer. **Two of the three guards were false coverage on the first cut** — they matched the Search button's own `isSearching` spinner and text inside the disabled block, so they passed against the broken page; both now anchor the condition to the markup it gates. Each guard proven against its own mutation; restore -> 48 pass |

| ppsvc-1 | HIGH | On an unconfigured store the protected-principal bypass defaulted to `Security:AllowedGroups` | `[x]` | | codex/gpt-5.5-dzs/xhigh/std (codex-cli 0.146.1, generation pass, base `a378785..025a5c6`, capability_ok) — the fail-closed set is built from catalog policy aliases, and a `ProtectedServicer:` key is deliberately none, so it fell through to the legacy app-wide fallback. **Worse than reported: `Evaluate` reads the same method, so the bypass was live with no admin page involvement and no stored row to find.** Fixed in the service by prefix-matching the key as fail-closed; guard: revert -> 2 fail, restore -> 10 pass, 197 authorization tests unmoved |
| pps-1 | HIGH | Mailbox/Calendar bulk CSV cannot service, and the on-prem branch writes without a fresh protection check | `[x]` | | codex/gpt-5.5-dzs/xhigh/std (base `b351005..HEAD`) — bulk called the back-compat `ValidateTargetMailboxAsync` overload that by construction never services, so a servicer was allowed one row at a time and refused in a CSV; separately `ExecuteOnPrem` re-checked authorization but not protection after the confirmation dialog, violating "immediately before the write" and auditing no override. Fixed: bulk threads the principal + its own module id and audits the note per row; `ExecuteOnPrem` re-validates before the write on BOTH pages. **The reviewer named only Mailbox for the on-prem half; Calendar had the identical defect and was found by checking rather than assuming the pair diverged.** Guard: revert -> 3 of 10 fail, anchored inside the `ExecuteOnPrem` body so the single-submit handler's own validation cannot satisfy them |
| pps-2 | HIGH | Page-level gates block the servicer before the serviced service gate can run | `[x]` | | codex (same pass) — `ADAttributeEditor.razor:313` set `protectedBlocked` and hid the edit UI with no servicer consultation, so the serviced save gate was unreachable from the page; undo preview got no principal while execute did. **This is the Emergency Disable two-gate shape I recorded and then failed to apply to the other module that has it.** Same class as the 2026-08-06 unreachable-capability defect. Fixed: page gate consults the servicer and shows an override banner (an override the operator cannot see is one they cannot decline); `PreviewUndoAsync` takes the acting principal so preview and execute agree. Guard: revert -> 3 of 5 page tripwires fail; the interface guard is honestly weaker (removing the parameter breaks the build first) and says so in the file |
| pps-3 | HIGH | Serviced notes computed and then discarded, losing the audit record of the override | `[x]` | | codex (same pass) — `ADAttributeEditorUndoService.cs:180` evaluated `NoteFor(...) is null` inside a boolean and dropped the note on the allow path; Emergency Disable kept its note in the operation trace and out of the `AuditService` event (different stores, different readers — the audit log is where "who permitted this?" gets asked). **The helper returns a nullable note precisely so permission and record cannot be separated; a bare null test defeats that by design.** Fixed: note bound and carried to the audit in both. The guard forbidding that call shape was BUILT, not deferred — it scans every file under `Services/` and `Components/Pages/`, with one bounded exemption for the no-audit preview path; probe: restoring the discarding shape fails it by name. Emergency Disable's audit threading is guarded source-level because `LogAudit` sits past a Delinea fetch and two live backends, and the test says so |

| mbs-1 | MEDIUM | Per-user actions inside an expanded batch still prompt for the ticket at the top of the table | `[x]` | | codex/gpt-5.5-dzs/xhigh/std (codex-cli 0.147.0, generation pass, base `c6abcdf..e70dfb2`, capability_ok, both SHAs echoed) -- the slice-3 inline confirm matched on a BATCH NAME, and `StageUserAction` sets the target to an EMAIL, so every per-user action fell through to the top-of-table bar: the reported off-screen-prompt defect, still live one level down. **The plan's D1 said the inner table gets this fix; the slice-3 note listed only the bulk cases, and I built to the note.** Fixed `1ef7fae`: confirm row rendered inside the `batchUsers` loop, `PendingActionNamesALoadedBatch` -> `...ALoadedRow` covering both row kinds. Guards anchored inside the user loop; probe: revert the row -> 2 of 26 fail, revert the predicate half alone -> 1 of 26 |

Notes:
- **mbs-1 is the case for the reviewer reading the plan as a claim to check.** Every automated
  guard passed, the suite was green at 1645, and ten mutation probes had already run against that
  change. The defect was in the half of an owner ruling the implementation quietly skipped --
  invisible to a diff review that only asks whether the code does what the code says.
- **ppsvc-1 is the case for reviewing a diff you believe is safe.** The commit had already been
  reviewed clean as a PLAN by grok, was written against a plan that named the whole-store-replace
  hazard explicitly, and shipped with 8 passing guards. The defect was in none of that: it was a
  pre-existing fallback in a file the diff never touched, reachable because the new key was not the
  KIND of thing the existing fail-closed set knew how to cover. A plan review cannot see that, and
  neither can a test suite that never runs against an unconfigured store.
- **blr-1..2 came from one generation-half dispatch** (`codereview codex gpt-5.5-dzs xhigh
  81fd069..e39e18f`) over the BitLocker Recovery module integration. Verdict **findings** (2),
  both verified against the code before any fix, both fixed one-per-commit with a non-vacuity
  probe each, neither declined. T1 did not match (no sensitive path in the diff), so the pass
  ran at standard as routed.
  **blr-1 is the one worth remembering: the module took great care to keep recovery keys out of
  the audit log on the REVEAL path, and then wrote one there from the SEARCH path.** The
  recovery-screen box is documented to accept a pasted 48-digit key, so the leak was on the
  happy path, and it bypassed the very `RevealRecoveryKey` event that exists to record a key
  reaching a human. Three prior review rounds over the same code missed it.
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
  **gitignored** as of 2026-08-07, and the nine that had been committed are untracked (the files
  stay on disk). The durable record is the finding doc plus the index row, which quote whatever
  mattered; the scratch was a second copy free to drift. Settled by consequence rather than
  preference: leaving them untracked blocked a **prod deploy**, because `deploy-pipeline.ps1`
  refuses to promote a build made from a dirty tree - correctly, since "which source produced
  this binary" is exactly what an incident needs to answer.

