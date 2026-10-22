# aud-a26i: Multi-section failed settings records cannot be found by their affected section in the Admin Event Log or its CSV

**Severity**: LOW - the record exists; section-based review misses it. Since aud-a28h, a refused or
failed save that spans several sections (Admin Settings enablement; a module's section access) files
one record under the save's own scope (`ModuleEnablement`, the module's main alias) with the affected
sections in a `sections` list. The Admin Event Log filters on category/user/IP/result only, its target
summary shows `section` only, and the CSV exports `FullTarget` only, so an admin reviewing
`Module:MailboxPermissions` sees its success rows and not the refused or failed attempts that named
it in `sections`.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; handed to the aud-a28l/a26g agent (it owns `AdminEventLog.razor`)
**Branch**: -
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

## Guard proof

Pending.
