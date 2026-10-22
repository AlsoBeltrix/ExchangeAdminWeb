# aud-a28g: The attribute editor page's own refusals and its catch write no audit record

**Severity**: MEDIUM - same class as aud-a28 / a28c / a28d / a28e / a28f: an operator's save
attempt that ends with no durable record. `ADAttributeEditor.razor`'s save handler refuses before
calling the service when authorization is denied, when the allowlist is corrupt, and when an
attribute is above the caller's level; none of those writes an audit record. Its catch sends a
notice but writes no record either.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a28f/a26e agent, 2026-10-22, candidate 1 of its hand-back)

## Evidence

At `488e535e`, `Components/Pages/ADAttributeEditor.razor`, the save handler: three page-level
refusal returns ahead of `SaveAsync` (authorization re-check, corrupt allowlist, level-unauthorized
attribute) with no `LogRefusal`; the outer catch dispatches a failure notice and returns. The
service (`SaveAndAuditAsync`, aud-a26b/a28b/a28c/a28e) audits every outcome IT reaches; these paths
never reach it.

## Approach (to be written by the implementing agent before code)

- Follow aud-a28f's shape: move what can move into the service so every path is drivable, or add
  two small page-callable service helpers (`LogSaveRefused`, `LogSaveFailed`) that write the
  refusal/failure record (`refusedAt` naming the step, `mutationAttempted: false` for refusals) and
  never throw. The page's authorization re-check stays on the page (it holds the principal) and
  records through the helper before returning; the corrupt-allowlist and level refusals either move
  into `SaveAndAuditAsync` (preferred, if the service can see the same inputs) or record through the
  helper. The catch records a failure through the helper. Exactly one record per attempt; the
  aud-a26b single notice stays single.
- Tests through `AttributeEditorHarness` and the page tripwires used by a28b/a28f: each page-level
  refusal leaves exactly one refusal record and no directory session; the catch path leaves one
  failure record; no attempt leaves zero records. Mutations at the call site: drop each refusal
  record; drop the catch record.
- `ClickGateRegistry`: `ADAttributeEditor.razor` is `NotYetConverted`; confirm. `ProgressRegistry`
  pins unchanged.

## Guard proof

Pending.
