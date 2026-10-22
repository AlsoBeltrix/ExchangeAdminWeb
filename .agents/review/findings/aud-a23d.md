# aud-a23d: Migration and recipient-lookup timestamps are read with `as DateTime?`, so another type silently drops the value

**Severity**: LOW - no wrong write. `Services/MigrationService.cs` reads `CreationDateTime`,
`StartDateTime`, `CompletionDateTime`, `CompleteAfter` and `LastSyncTime`, and
`Services/RecipientLookupService.cs` reads `WhenCreated` and `LastLogonTime`, each with
`as DateTime?` from the PowerShell object. If the Exchange Online or AD module hands back a
`DateTimeOffset`, a string or a wrapped PSObject, the cast yields null and the page shows no value,
as if none were set, with no sign anything was dropped - the aud-a7h shape in two other modules.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a38k/a7h agent, 2026-10-22, candidate 1 of its hand-back)

## Evidence

At `0187257e`: `git grep -n "as DateTime?" Services/MigrationService.cs Services/RecipientLookupService.cs`.
aud-a7h introduced `ReportedTime.Read(object?)` in `Models/LookupModels.cs` as the one reader for a
reported time (PSObject unwrapped; `DateTime` as is; `DateTimeOffset` as its instant; ISO 8601 text;
anything else named as unreadable).

## Approach (to be written by the implementing agent before code)

- Read each of the seven timestamps through `ReportedTime.Read` (or a shared sibling if it needs
  to live outside the Out of Office models), and render an unreadable value as the named marker, not
  as blank. Where a timestamp drives logic (a migration batch's completion status, a sort), an
  unreadable value must not be treated as "none" silently: refuse or flag it, per the no-guess rule.
  Label the live modules' actual types as an unverified assumption.
- Tests: for each read, a `DateTimeOffset` and a wrapped value render as the same instant; an
  unreadable type renders the marker and is not treated as absent in any logic that depends on it.
  `ClickGateRegistry`: the Migration page has line-keyed entries - do not add or remove lines in
  `Components/Pages/Migration.razor`; keep the change in the services and models. Mutation at the
  call site: `as DateTime?` again on one Migration read and one lookup read.

## Guard proof

Pending.
