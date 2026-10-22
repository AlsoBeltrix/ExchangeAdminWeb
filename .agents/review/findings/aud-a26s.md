# aud-a26s: Exchange Online Config repeats the runtime fallback rule with no test keeping them in step; the Secret ID check accepts "+42" and "0042"; `ChangedEntries` still ignores case

**Severity**: LOW - three leftovers of aud-a26p. (1) The Exchange Online page's "the configured fallback
applies" text re-derives the fallback from `ExoConnectionPool.GetExoConfig`'s rule by hand, and no test
asserts the page and the pool agree. (2) The Secret ID validation uses the lookups' `int.TryParse`, so
"+42" and "0042" are accepted and stored as typed; the lookups parse them the same, so the outcome is
right, but the stored text differs from what the operator meant. (3) `ChangedEntries`, which names the
entries in an already-applied record, compares case-insensitively while the dirty count (aud-a26p) is
now case-sensitive, so a casing-only change counts as unsaved yet is named as unchanged.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a28r/a26p agent, 2026-10-22, candidates 2, 3 and 4 of its hand-back)

## Evidence

At `25071281`: `Components/Pages/ExchangeOnlineConfig.razor` (the fallback text; NotYetConverted for
`ClickGateRegistry`); `Services/ExoConnectionPool.cs` `GetExoConfig`; `Components/Pages/AdminSettings.razor`
(the Secret ID check); the `ChangedEntries` helper and its callers.

## Approach (to be written by the implementing agent before code)

- One function owns the fallback rule (the pool exposes it; the page calls it); a test asserts the page's
  text for each fallback source comes from that function.
- The Secret ID is accepted only as digits with no sign and no leading zero (or normalised to its parsed
  value and the operator shown what will be stored - choose the former, the onus is on the user to
  provide the value).
- `ChangedEntries` uses the same comparison as the dirty count.
- Tests through the settings harness; mutations at the call site: the page's hand-written rule again;
  `int.TryParse` alone again; the case-insensitive comparer again.

## Guard proof

Pending.
