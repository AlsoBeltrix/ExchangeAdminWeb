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

Notes:
- Finding **confirmed real** (round 1), fix **plan reviewed & accepted** (rounds 2-3
  page-seam; round 4 consolidation C2-G), and **implemented** 2026-07-21 (commit
  2a97d09; `ConferenceRoomProtectionGate`, 672 tests pass, non-vacuity verified).
  Only follow-up: live-tenant/UI validation (deferred, no dev tenant).
- Scratch dispatch artifacts (`*.prompt.txt`, `*.schema.json`, `*.result.json`) are
  left untracked pending the owner's commit-vs-clean decision.
