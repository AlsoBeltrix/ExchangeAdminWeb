# aud-a40: Every other settings save still reports a busy store as "Failed to save: SQLite Error 5"

**Severity**: LOW - the aud-a28v class at the saves aud-a28v did not cover; nothing is written in any case.
A protected-principals save whose Secret ID did not change meets a busy store at `ReplaceConfig` and
reaches the generic catch; the other settings saves' catches do the same: Admin Settings (two saves),
Exchange Online Config (two), Module Config (three). The operator sees a provider error code instead of
"the store was busy; nothing was written; try again".
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a28v/a26s/a28w agent, 2026-10-22, candidate 1 of its hand-back)

## Evidence

At `a583777d`: `Components/Pages/AdminSettings.razor` around lines 709 and 1230/1306; `Components/Pages/ExchangeOnlineConfig.razor`
around lines 352 and 581; `Components/Pages/ModuleConfig.razor` around lines 1254, 1491 and 1609 (all
NotYetConverted for `ClickGateRegistry`); `Services/Storage/ConfigStoreBusy.IsBusy` (aud-a28v).

## Approach (to be written by the implementing agent before code)

- One shared helper turns a save's exception into its operator message and refusal record
  (`StoreBusy` -> the aud-a28v text and `refusedAt: StoreBusy`, `mutationAttempted: false`; anything else
  -> the existing failure path), used by every settings save catch, so the pages stop hand-writing it.
- A busy failure on a write that may have partially applied (the second half of a two-store save) says
  so: "the store was busy during <step>; <what was written>; check and retry".
- Tests through the settings harness with a held lock per page (keep the wait short: see aud-a41);
  mutation at the call site: one page's catch back to the generic message.

## Guard proof

Pending.
