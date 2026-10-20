# Independent review request: unreviewed remediations (backlog batch 3)

You are reviewing, not implementing. **Do not modify any file.** Report findings only.

## How to get at the code

Use `run_command` with git. **One command per call, and never add `--no-pager`** - command
matching looks at the first two tokens, so `git show` is allowed and `git --no-pager show`
is not. Do not chain with `;`, `&&` or a pipe.

- A commit and its diff: `git show 02ba918`
- Any file at HEAD: `git show HEAD:Services/ADAttributeEditorUndoService.cs`
- A finding record: `git show HEAD:.agents/review/findings/aud-a26.md`
- Repo rules: `git show HEAD:.agents/repo-guidance.md`
- The constitution: `git show HEAD:docs/ProjectConstitution.md`
- Commit list: `git log --oneline 30e893b..HEAD`

## Context

`D:\source\ExchangeAdminWeb` - ASP.NET Core 10 Blazor Server app for Exchange Online,
Microsoft Graph and on-prem Active Directory administration.

These commits are on `master`, unpushed, and **have never been independently reviewed**. The
agent that wrote them wrongly believed no review harness was available and landed them on
that false belief. You are the first reader.

| Commit | Finding | Record |
| --- | --- | --- |
| `02ba918` | aud-a26 - undo an AD attribute edit now notifies administrators | `aud-a26.md` |
| `8978732` | aud-a37 - the config DB recovery guide rewritten (docs only) | `aud-a37.md` |
| `ba0d418` | aud-a39 - refuse a shared database inside a publish folder | `aud-a39.md` |
| `eed6333` | aud-a40 - wake the app before reading its startup log | `aud-a40.md` |
| `6c1b827` | pipeline provenance - a check that cannot run has not passed | `aud-pipeline-provenance.md` |
| `dd2f10d` | the `-WhatIf` directory claim, disproved | `aud-whatif-directory.md` |
| `aeab7ec` | Graph app registrations - never buffer an unreissuable secret | `aud-graphapps-stranding.md` |

## Authority to check against

- `docs/ProjectConstitution.md` - highest engineering authority.
- `.agents/repo-guidance.md` - the **Architectural Invariants** and the four **Known Failure
  Classes**. Invariant 1 (the installer is standalone), 4 (every ops step supports
  `-PlanOnly`) and 6 (files read by Windows PowerShell 5.1 must be pure ASCII) all apply.

## What to attack

1. **aud-a26.** Undo now emails administrators on both outcomes. Check it cannot fail the
   reversal, that the action name distinguishes a reversal from a forward edit, and that no
   path double-notifies. The record admits the root cause is untouched (dispatch lives in
   the page) - is that honestly scoped or is something worse hiding behind it?
2. **aud-a39.** `Test-PathIsInside` is textual and case-insensitive. Attack it: junctions,
   `subst` drives, UNC, trailing separators, a path that differs only by case, `..`
   segments. Can a shared database still be placed where a deploy will delete it?
3. **aud-a40.** The cutover now sends an HTTP request to `http://localhost<PathBase>/` to
   force application initialisation before reading the startup log. **The whole fix rests on
   the assumption that this actually causes IIS to initialise an ASP.NET Core app.** Say
   whether that assumption holds, and what happens on an HTTPS-only or host-header-only
   binding. Check the certificate-validation callback is restored on every path.
4. **pipeline provenance.** The stale-build check now fails closed. Verify there is no
   remaining path that reports "Dev commit matches source HEAD" without a comparison, and
   that `-AllowUnverifiedProvenance` is the only way past.
5. **aud-whatif-directory.** This one claims the audit was WRONG - that `New-Item` honours
   `-WhatIf` by inheritance, so no directory was ever created during a dry run. **Verify or
   refute that claim.** If the finding was real on some PowerShell version, this record is
   the thing that is wrong.
6. **aud-graphapps-stranding.** Secrets are now printed per registration rather than
   buffered. Check the failure window the record admits to: between creating an application
   and recording its secret. Is anything else strandable?
7. **The tests.** Mostly Pester source scans and injected script blocks. Tell me which
   guards would pass against broken code. This stream has repeatedly shipped guards that
   assert a string while the branch around it is dead - look for exactly that shape.
8. **The records.** Say plainly if any record claims something the code does not do.

## Output

Findings as a list: severity, file and line, what is wrong, and the concrete failure it
produces. Name any false record claim. If a category is clean, say so explicitly - I need to
know what you checked and found nothing in, not only what you found.
