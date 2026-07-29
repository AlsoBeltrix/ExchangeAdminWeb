# mt-export-delivery-plan: openreview of the MessageTrace export delivery plan (v2)

**Severity**: HIGH (worst of 4 findings)
**Status**: Findings accepted (3 as raised, 1 modified); plan revised, re-review pending
**Branch**: `master` (committed directly per repo policy; no per-finding branch)
**Range reviewed**: `68bfd25..1e98eaf` (the two plan commits `13e3f49` + `1e98eaf`)

## Dispatch

`Reviewer: codex-commercial (MCP) / gpt-5.6-sol / max / frontier`

- Transport: MCP thread `019faeb0-c835-7e41-aef5-4cf2c3d5e17d`, `sandbox=read-only`,
  `cwd=D:\source\ExchangeAdminWeb`, `config.model_reasoning_effort=max`.
- `openreview` contract: neutral question only ("Is the change as implemented the best
  way to achieve the goal?"), no rubric, no area list, no claimed invariants supplied.
  Mechanical coordinates + isolation + side-effect boundaries only.
- Base SHA correction: `git merge-base master HEAD` returned the head commit, because
  both plan commits landed directly on `master`. The pinned base is `68bfd25`, the
  commit preceding `13e3f49`. `git diff --stat 68bfd25..1e98eaf` = 2 files, +429/-1.
- **verdict: findings** (4), `capability_ok: true`. No `--output-schema` on the MCP
  `codex` tool, so the JSON verdict schema was embedded in the prompt per
  `.agents/machines.md`; the reply parsed clean against it.

## What was reviewed

`docs/MessageTraceDownloadLink-Plan.md` (v2) and the `.agents/state.md` entry for it.
A plan document, not shipped code: findings are defects in the *intended design*, and
the repair is a plan revision, not a code change. No code exists for this work stream.

## Findings

### F1 (HIGH) -- a failed file save still produces a "your export is ready" email

**Accepted as raised.**

Evidence: plan `:221-222` directs the implementer to keep `SaveToLogPath`'s swallowed
try/catch "exactly as it is"; `:276` sends the ready-and-linked email; `:245` labels
every unresolvable file **Expired**. Today's processor emails unconditionally after the
save attempt (`Services/Jobs/MessageTraceDetailJobProcessor.cs:112,116-118`).

Why the reviewer is right, and why the fail-soft catch was correct *before* this plan:
today the zip travels **in** the mail, so a save failure costs only the archive copy and
the operator still receives the data. This plan removes the attachment, which makes the
saved file the **sole** delivery. The same swallowed catch then converts a disk-full or
permissions failure into an email pointing at nothing, rendered by the page as "Expired"
-- indistinguishable from ordinary retention, and the export is unrecoverable.

The existing audit path already knows the difference (`MessageTraceDetailJobProcessor.cs
:168-172` writes `success: savedPath is not null` and "log save failed"), so the signal
exists and is simply not reaching the operator.

Repair (plan `:221-222`, `:245`, `:276`, Tests): keep the catch (a save failure must
still not fault the job -- `OnJobCompletedAsync` is documented fail-safe), but branch the
notification on `savedPath`. On a null path send an explicit **failure** notice naming
the ticket and telling the operator to re-run; never a ready-with-link mail. The page
distinguishes **Failed** (job record says the save failed) from **Expired** (save
succeeded, file since removed), so retention is never blamed for a write error.

### F2 (MEDIUM) -- the ticket prompt does not enforce the stated ticket requirement

**Accepted, resolved by reading, not deferred.**

Evidence: plan `:260` allows a blank ticket through; plan `:110` states the owner
requirement as a download "requiring a ticket number as the audit check".

This is an internal inconsistency in the plan, not a conflict with D2. D2 settles the
*validation* question ("recorded, never validated" -- no ServiceNow lookup, no
authorization weight). It does not settle *presence*. The owner's word was "requiring",
and a blank ticket makes the audit check nominal, which is exactly the failure mode D3
cites as the reason to route through a page at all.

Repair (plan `:258-263`): require a non-blank trimmed ticket before the file is read;
show inline validation; still never validate its content. Recorded in the plan as an
assumption the owner can reverse with a one-line change, so it does not block slice 2.

### F3 (MEDIUM) -- the relative-link fallback cannot work from an email

**Accepted with modification.**

Evidence: plan `:283-286` makes an unset `Application:PublicBaseUrl` fall back to a
relative link. Confirmed the config writers emit only `PathBase` and `ContactEmail`:
`tools/Install-ExchangeAdminWeb.ps1:439-443`, `deploy.ps1:735-738`.

An email client has no application origin, so `/message-analysis/reports` in a mail body
resolves against nothing. The fallback ships a link that is knowingly dead, which is
worse than shipping no link -- a dead hyperlink reads as a broken app.

Modified rather than accepted verbatim: the reviewer's "require and validate an absolute
HTTPS PublicBaseUrl" would fail the send on a misconfigured host. The job has already
completed at that point and mail formatting must never change a job result. Repair
instead: when the key is unset, **omit the hyperlink** and state in prose where to find
the export ("the Downloadable Reports page in <app name>"), plus log a warning. The
operator can still act; nothing dead ships. Adding the key to both config writers and to
`tools/promote-dev-to-prod.ps1` is accepted as raised.

### F4 (MEDIUM) -- `Export:RetentionDays` violates the module-config rule

**Accepted as raised; repaired by deleting the key rather than relocating it.**

Evidence: plan `:54-56`, `:203`, `:321` introduce a global `Export:RetentionDays`.
`docs/ProjectConstitution.md:59`: "Module-specific settings belong to that module's
config, not global `appsettings.json`, unless explicitly retained as upgrade fallback."
`Export:` is a global key for MessageTrace-only behavior, and generic enough to invite
unrelated future use.

The drift half of the finding is the sharper point: under D1 the app never deletes, so
the value is descriptive only. A configurable descriptive value is a second source of
retention truth that can silently disagree with the external scheduled task (setting 60
while the task deletes at 30 promises the operator a file that is already gone).

Repair (plan `:54-56`, `:203`, `:321`): drop the key. Pin 30 days as a constant in
`MessageTraceExportStore`, commented as mirroring the host scheduled task's policy and
enforcing nothing. Relocating it to MessageTrace module config was considered and
rejected: it would satisfy the Constitution while preserving the drift the finding is
actually about.

## Coder-raised finding (not from the reviewer)

### F5 -- the Constitution-conflict section was dropped in the v2 rewrite

Plan v1 (`13e3f49:docs/MessageTraceDownloadLink-Plan.md:117`) carried "Constitution
Conflict To Record, Not To Silently Resolve", covering the Never-Do rule *"Do not write
durable state into locations that deployment or log pruning scripts delete."* The v2
rewrite (`1e98eaf`) dropped it.

The D3 rework does not resolve that tension -- the reports page still hands out links
backed by an externally pruned directory -- so the section's absence is a regression in
the plan. Its argument holds verbatim under the Razor-page design: the export file is a
convenience artifact, the audit event and job record are the authoritative durable
state, and every consumer must render absence as an ordinary outcome. Restored, with the
F1 correction folded in (absence is now Failed **or** Expired, not uniformly Expired).

## Guard proof

Docs-only. No automated test guards a plan document. Static read-through instead: every
line reference in each finding was re-opened in the working tree and confirmed before
the repair was written (Known Failure Class number 4). Line numbers cited by the
reviewer for `Install-ExchangeAdminWeb.ps1`, `deploy.ps1`,
`MessageTraceDetailJobProcessor.cs` and `ProjectConstitution.md` were each verified
independently rather than trusted.

## Coder dispute (if any)

One partial: F3's remedy. The finding is correct; the proposed remedy (hard-require and
validate, implicitly failing the send) conflicts with the fail-safe completion contract
at `Services/Jobs/MessageTraceDetailJobProcessor.cs:89-90`. Repaired in the direction of
the finding without adopting the remedy verbatim. Recorded here rather than settled
silently.

## Known gaps for the re-reviewer to grade explicitly

1. F2 was resolved by reading the owner's word "requiring" literally rather than by
   returning to the owner. Is treating presence-vs-validation as separable a defensible
   reading of D2, or does it need an owner ruling?
2. F1's repair adds a **Failed** state to the reports page that the owner never asked
   for. Is that in scope for a delivery-path plan, or scope creep?
3. D4 (default email recipient) remains open and unruled. Slices 1, 2 and the string
   removal in slice 4 are unaffected.
