# aud-a26i: Multi-section failed settings records cannot be found by their affected section in the Admin Event Log or its CSV

**Severity**: LOW - the record exists; section-based review misses it. Since aud-a28h, a refused or
failed save that spans several sections (Admin Settings enablement; a module's section access) files
one record under the save's own scope (`ModuleEnablement`, the module's main alias) with the affected
sections in a `sections` list. The Admin Event Log filters on category/user/IP/result only, its target
summary shows `section` only, and the CSV exports `FullTarget` only, so an admin reviewing
`Module:MailboxPermissions` sees its success rows and not the refused or failed attempts that named
it in `sections`.
**Status**: Plan written; in progress
**Branch**: `worktree-agent-ad10b5ab24c8ce164`
**Commit**: -
**Reviewer**: codex / gpt-5.5-dzs / xhigh / standard (attr-batch-4, `prompt-attr-batch-4.md`, 2026-10-22)

## Evidence

At `f1b4692d`, `Components/Pages/AdminEventLog.razor` around line 877 (filters, target summary, CSV
export); `Services/SettingsSaveAudit.cs` (the `sections` list on multi-section refusals and failures).

## Approach (to be written by the implementing agent before code)

- Include `sections` in the searchable and displayed target text and in the CSV `FullTarget` (or a
  sibling column), so a text filter on a section name finds the multi-section rows; if the page has a
  section filter, match both `section` and `sections`.
- Tests: an aud-a28h multi-section refusal record is found by a filter on one of its sections and its
  CSV line carries the section; a single-section record is unchanged. `ClickGateRegistry`: check the
  page's status; re-anchor per the playbook if it has line-keyed entries. Mutation at the call site:
  drop `sections` from the target text.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted by codex attr-batch-4 and handed to
this branch, which owns `AdminEventLog.razor`; this record taken from master `83cb9992`. One commit
`fix(event-log): a settings record that lists its sections names them in its target, on the page and
in the CSV (aud-a26i)`, third of three on this branch. Behaviour: Admin Event Log (module version; the
supervisor owns the bump). Evidence re-read at `374718b3`, `Components/Pages/AdminEventLog.razor`:
GetAuditTarget 1055-1056 is `target ?? batchName ?? section`; ParseAuditLine builds the entry's Target
summary, its FullTarget and the expanded row's Full Target from it (710, 736-737, 746); DownloadCsv
writes FullTarget as the CSV's Target column (1223-1226); ApplyFilters 873-882 filters on category,
user, IP and result - there is no section or target filter. `Services/SettingsSaveAudit.cs` 70-74
writes `sections` beside `section` for the two multi-section saves (aud-a28h).

1. **The target names the sections.** GetAuditTarget appends a record's `sections`, when it lists any,
   as `{section} (sections: a, b)` - the text the Target cell (its 90-character summary), the cell's
   tooltip, the expanded row's Full Target and the CSV's Target column all show. So an admin looking
   for one section by name finds its refused and failed multi-section attempts beside its successes. A
   record with no `sections` - every single-section record and every non-settings record - reads as
   before. FullTarget, not a sibling CSV column: EventLogCsvFormatter's header set is pinned, and the
   Target column is where a section's rows are already read.
2. **No section filter to extend.** The record's conditional half (match `section` and `sections` in a
   section filter) has nothing to apply to. A target filter would be a new control, not this fix; it
   is named as a candidate in the hand-back.
3. **Registries.** NotYetConverted, nothing re-anchors (as aud-a28l); ProgressRegistry's LoadEvents
   and DownloadCsv pins unchanged.

Tests. New `ExchangeAdminWeb.Tests/EventLogSectionsTargetTests.cs`: records written by aud-a28h's own
writers through the real AuditService - a multi-section enablement refusal (`ModuleEnablement`,
sections `Module:MailboxPermissions` and `Module:CalendarPermissions`), a multi-section section-access
failure, a single-section refusal and a success record for `Module:MailboxPermissions` - read back,
parsed by the compiled page's own ParseAuditLine, then exported by its DownloadCsv over a recording JS
runtime. A filter for one of a multi-section record's sections, over the entries' target text and over
the CSV's Target column, finds it beside that section's own rows; its CSV line carries the section;
the single-section and success records' targets are exactly their `section`, on the page and in the
CSV. Conditions asserted, not wording.

Mutation at the call site (as aud-a28l): M5 `sections` dropped from the target text (GetAuditTarget as
at `374718b3`).

Files: `Components/Pages/AdminEventLog.razor`, `ExchangeAdminWeb.Tests/EventLogSectionsTargetTests.cs`
(new), this record.

## Fix

`Components/Pages/AdminEventLog.razor`, GetAuditTarget: `target ?? batchName ?? section`, then - when the
record has a `sections` array that names any - ` (sections: a, b)` appended (the array formatted by the
page's own FormatJsonValue), or `sections: a, b` when there is no target at all. Everything that reads
the target text follows from it: the Target cell's summary and tooltip, the expanded row's Full Target,
and the CSV's Target column, which is FullTarget. +9 lines; the page is NotYetConverted in
ClickGateRegistry, so there is no line key to move.

Tests: new `ExchangeAdminWeb.Tests/EventLogSectionsTargetTests.cs` (3 cases). The records are written
by aud-a28h's writers (`LogSettingsSaveRefused`, `LogSettingsSaveFailed`, with and without `sections`)
and by `LogSettingsChange`, through the real AuditService; read back from its JSONL; parsed by the
compiled page's ParseAuditLine; exported by its DownloadCsv over a recording JS runtime.

Caller-visible change, by design: a refused or failed multi-section settings record reads, in the
event log and its CSV, as for example `ModuleEnablement (sections: Module:MailboxPermissions,
Module:CalendarPermissions)` where it read `ModuleEnablement`. Every other record reads as before. No
filter was added.

## Guard proof

No separate pre-fix reproduction: M5 below is GetAuditTarget exactly as at `374718b3`.

Same driver and conditions as aud-a28l (`a26i-mutations.log`), filter
`FullyQualifiedName~EventLogSectionsTargetTests|FullyQualifiedName~EventLogCsvWiringTests|FullyQualifiedName~AuditCategoryFilingTests|FullyQualifiedName~SettingsSaveAuditTests|FullyQualifiedName~EventLogUndoRefusalTests|FullyQualifiedName~ReversedOperationsTests|FullyQualifiedName~AuditFirstMergeTests`
(77 tests). The mutant built with 0 errors; the restore matched its original MD5. After the
round, a rebuild on the restored bytes and the filter: 77/77 green.

| Mutation (call site) | File, original MD5 | Mutant MD5 | Named test failing |
|---|---|---|---|
| M5 `sections` dropped from the target text - GetAuditTarget as at `374718b3` | `AdminEventLog.razor`, `A55F8DB9DE66EFF00FDC20707AAF22DF` | `5E39AE7D8BCEBA8FA6992C34141DD0E0` | `AMultiSectionRecord_IsFoundByAFilterOnAnyOfItsSections`, `AMultiSectionRecord_CarriesItsSectionsIntoTheCsvTarget`; the control `ARecordThatListsNoSections_KeepsItsSectionAsItsTarget` passes, as it must |

The mutant's MD5 is the page's at `748d358d`, before this fix: the revert is exact. Unchanged through
the round: `EventLogSectionsTargetTests.cs` `57B19F8F133BAB8890FE99323018526C`. The code comments are
written after the round, in their own commit, so the MD5s above are this commit's bytes.

## Gates - 2026-10-22 (this commit's tree)

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors, 61 warnings, all pre-existing, none in a
  touched file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug, as the gate is
  written): exit 0, 5,285 passed / 3 skipped (the three known AD skips) / 0 failed, 5,288 total,
  5m6s; nothing hung, no Sequence file. aud-a26g's tree: 5,282 / 3 / 0, 5,285 total; the
  three new cases are this record's.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check
  HEAD`: exit 0. `tools/Test-AsciiOnly.ps1` with the files staged: exit 0.

No PowerShell source, AD module, directory, Delinea, SMTP, push or deployment. No coder self-review
verdict is claimed.

## Known gaps

- The page still has no section or target filter: a section is found by reading or searching the
  Target text, on the page or in the CSV. A filter box would be a new control; it is a candidate in the
  hand-back, not part of this fix.
- The Target cell's summary is cut at 90 characters, as every target is; a long `sections` list is
  then complete only in the tooltip, the expanded row and the CSV.
- The expanded row shows the sections twice: in Full Target, and as the record's own `sections` field.
