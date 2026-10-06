# Graph Secret Remap Scope - Plan

Status: **IMPLEMENTED 2026-10-06.** Owner approved ("go"). The code fix, its tests and the
base app version bump have landed. **The deleted value still has to be restored** - that was
explicitly NOT authorised by the approval and needs its own go.

---

## Exec summary

*This is the only part of this plan the owner reads or is bound by
(`.agents/decisions.md` 2026-09-30, 2026-10-01). Everything below it is agent-facing.*

**What it does.** Stops the app from deleting Emergency Disable's on-prem Delinea secret ID
every time it starts. A startup cleanup routine, written to tidy up after an old setting
rename, treats that secret as leftover junk and removes it. This narrows that routine so it
only touches settings that really are leftovers.

**What it gets you.** The on-prem secret ID stays where you put it. Today you re-enter it,
the next deploy restarts the app, and it is gone again - which is why this looks like dev and
prod disagreeing when they are actually sharing one value that keeps being erased.

**What it costs.** One condition in one shared file, plus tests. Because it is shared rather
than inside one module, the base app version moves (2.27.0 to 2.27.1). Separately, and not
fixed by code: the value that was already deleted has to be put back, either from a
pre-deploy backup or by re-entering it.

**Biggest risk.** The cleanup routine exists for a real reason - an old rename did strand
settings on some modules, and this plan deliberately makes it do less. If it is narrowed too
far, a genuinely stranded setting on some other module would stop being repaired. The plan
contains that by skipping only modules that declare BOTH names as live settings, which today
is Emergency Disable alone, and by pinning that in a test that reads the real catalog so the
answer cannot silently change.

**One thing that should change your answer.** You told me my diagnosis was wrong and have not
said which part. This plan is built on that diagnosis. If the cause is something else, the
fix is wrong and approving it spends a slice on nothing. Approving says you accept the cause
as written in the Root cause section, or that you want it settled in review instead.

**What approving authorises:** the single-slice fix below, in `ModuleConfigService` and its
tests, plus the base app version bump. It does NOT authorise restoring the lost value - that
is an operational step you or I take separately, and it needs its own go.

---

## Root cause

`Services/ModuleConfigService.cs:102` `MigrateGraphSecretKeys` selects modules by one test:

```csharp
if (!module.ConfigFields.Any(f => string.Equals(f.Key, newKey, StringComparison.OrdinalIgnoreCase)))
    continue;
```

"Declares `GraphDelineaSecretId`" is treated as "any `DelineaSecretId` here is pre-rename
residue". `EmergencyDisable` declares both keys as live fields - `DelineaSecretId` is its
on-prem AD credential (`Services/EmergencyDisableService.cs` `GetCredentialsAsync`) and
`GraphDelineaSecretId` its cloud one - so it matches a predicate meant to exclude it.

`Services/Storage/ModuleConfigRepository.cs:127` `RemapKey` then deletes the old row
unconditionally (lines 157-162), outside the `copyValue` branch:

- Graph key already set: `copyValue` is false, so the on-prem value is **deleted and copied
  nowhere**.
- Graph key empty: the on-prem AD secret is **copied into the Graph slot** - an AD credential
  in a cloud credential field, which is a Constitution credential-isolation concern in its
  own right.

`Program.cs:366` calls it on every startup with no run-once marker, and both instances share
one config DB (`.agents/repo-guidance.md` invariant 2), so any restart of either instance
erases a value the operator just set.

`docs/GraphSecretKeyMigration-Plan.md:38` classified `EmergencyDisable` as
"`GraphDelineaSecretId` only (no fallback)". That was true of what its Graph client *reads*
and false of what the module *declares*; the analysis never looked at the field list.

**Disputed:** the owner rejected this diagnosis on 2026-10-06 without naming the part. The
evidence above is file-and-line and re-checked; it is not settled by that, and the dispute is
recorded rather than closed.

## Scope

In scope:

- Narrow the selection predicate in `MigrateGraphSecretKeys`.
- Tests over the real catalog.
- Base app version bump.

Out of scope:

- Restoring the deleted value (operational; separate go).
- `RemapKey`'s unconditional delete. It is correct for a genuinely stranded key and the
  predicate is the defect. Changing both would make the blast radius of this fix larger than
  the bug. Named here so it is a decision, not an oversight.
- Any other module's config.

## Fix

`Services/ModuleConfigService.cs`, `MigrateGraphSecretKeys`: skip a module that declares
`DelineaSecretId` as a current ConfigField. A module declaring both keys owns both; neither
is residue.

```csharp
if (module.ConfigFields.Any(f => string.Equals(f.Key, oldKey, StringComparison.OrdinalIgnoreCase)))
    continue; // declares both: the old key is a live field here, not pre-rename residue
```

Comment must state the rule, not the module name - a second dual-key module must be covered
without an edit.

## Tests

`ExchangeAdminWeb.Tests/GraphSecretKeyMigrationTests.cs`:

1. A dual-key module's `DelineaSecretId` survives the migration, with the Graph key set.
2. The same with the Graph key EMPTY - proves the on-prem value is not promoted into the
   Graph slot.
3. A Graph-only module with a stranded old key still migrates (the routine still works).
4. Catalog-driven: every module declaring both keys is skipped, asserted by querying
   `ModuleCatalog` rather than naming `EmergencyDisable`, so a future dual-key module is
   covered by the existing test.
5. `IsModuleConfigured("EmergencyDisable")` is unaffected across a migration run.

Non-vacuous proof: revert the predicate, confirm 1, 2 and 4 fail, restore, confirm green.

## Verification

- `dotnet build ExchangeAdminWeb.slnx -c Release`
- `dotnet test ExchangeAdminWeb.slnx`
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`
- `git diff --check HEAD`
- Dispatch codex on the slice before closing it.

## Versioning

`ModuleConfigService` is shared, so the base app version moves: `<VersionPrefix>`,
`AssemblyVersion` and `FileVersion` 2.27.0 -> 2.27.1 in `ExchangeAdminWeb.csproj`. No module
version moves - no module's own behaviour changes.

## Manual acceptance - owner or operator

1. Restore or re-enter the on-prem `DelineaSecretId` on Emergency Disable's config page.
2. Restart the instance. Confirm the value is still there.
3. Restart the OTHER instance. Confirm it is still there - this is the dev/prod symptom.
4. Confirm `GraphDelineaSecretId` is unchanged and still the cloud secret, not the AD one.
5. Run an Emergency Disable lookup and confirm the AD credential resolves.

## Owner gate

Go or no-go on the fix as described in the exec summary, and separately: say which part of
the diagnosis you rejected, or that review should settle it.

---

## Approach review, 2026-10-06

`openreview`, pins `6fd1e5e..4a1dd66`.
Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / frontier (grade
`fallback`, and this machine's frontier pair is an alias of standard; the gateway rejects
effort `max`, so xhigh is its ceiling - `.agents/review/harnesses.local.json`). The owner
named the harness but did not explicitly accept the fallback grade for this dispatch, which
the playbook asks for. Recorded, not glossed.
Raw output: `.agents/review/remap-open.result.json`; prompt and stream log alongside.

**Verdict: best approach, no material changes needed.** It stated the goal in its own words,
said it would narrow `MigrateGraphSecretKeys` to skip dual-key modules while leaving
`RemapKey`'s delete intact for genuinely graph-only legacy rows, and named the same
catalog-driven tests, the version bump and the separate operational restore.

**This is NOT independent corroboration of the root cause, and must not be cited as such.**
The review question is answered by reading the pinned change, and the pinned change IS this
plan - so the reviewer read the diagnosis before judging it. What it did do on its own:
read `Services/ModuleConfigService.cs` at the pinned head and grep both key names across the
catalog, so the mechanism was checked against source rather than taken on the plan's word.
An unprimed second opinion on the CAUSE would need a review that is not handed this file.

## Unprimed cause review, 2026-10-06

The approach review above was primed - it read this plan. This one was not: codex was given
the operator's symptom verbatim, the repository, and an explicit prohibition on reading this
plan, anything under `.agents/review/` matching "remap", and any commit from `4a1dd66`
onward. It confirmed compliance unprompted.

Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / frontier (`fallback`
alias). Effort `max` RE-PROBED on codex-cli 0.159.0 and still rejected by the gateway
("Supported values are: 'none', 'low', 'medium', 'high', and 'xhigh'"), so xhigh is the
ceiling, not a shortcut.
Raw output: `.agents/review/remap-cause.result.json`.

**It reached the same mechanism from the symptom alone, rated HIGH**, with the same call
chain and the same evidence lines, plus two the plan had not cited:
`Services/ModuleCredentialService.cs:27-31` and `Services/EmergencyDisableService.cs:195-199`
are where the AD path still reads `DelineaSecretId`, which is what makes the deletion
operationally fatal rather than cosmetic.

It also refined the dev/prod point: the pattern is real but it is a TRIGGER pattern, not a
promotion artefact - any startup of either instance does it, and promotion copies nothing.

**Three further candidates it raised, none folded into this fix:**

1. **MEDIUM, genuinely separate and still open.** Whole-module config save replaces the
   module: `Services/Storage/ModuleConfigRepository.cs:78-92` deletes every row for the
   module then writes the page's snapshot, and `Components/Pages/ModuleConfig.razor` loads
   that snapshot once (`:927`, `:945`) and saves it at `:1112-1113`. A stale page, or a
   snapshot taken during a transient read failure, can therefore delete rows written
   elsewhere. It is dev/prod-shaped too. It fits "lose one specific value repeatedly" worse
   than the startup bug, because it would usually lose several at once - which is why it is
   recorded as its own question rather than merged here. **Needs its own plan.**
2. LOW, one-time: legacy JSON import during first shared-DB adoption can let one instance's
   incomplete config win while the other's file is archived unread
   (`Services/ModuleConfigService.cs:157-172`, `Services/Storage/LegacyConfigImport.cs:25-26`).
   Not a fit for repeated loss after re-entry.
3. Scope check: the startup path touches only those two keys and only the numeric ID, never
   the secret contents. Its explicit warnings for the repair step - do not bulk-copy
   `GraphDelineaSecretId` back across modules, do not resurrect `OnPremDelineaSecretId`, do
   not abandon the shared config DB - are adopted into the manual acceptance section's
   intent.

Its one approach difference: it would prefer an explicit allowlist of modules whose retired
key was truly a Graph key, over this plan's "skip modules declaring both". The plan's form is
kept - an allowlist is a second list to forget to update, and the dual-key test is exactly
the condition that makes the key non-residue - but the alternative is recorded as a real
option rather than dismissed.

## Fix review, 2026-10-06

`codereview`, pins `3a0aa23..d1ccd07`.
Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard.
Raw output: `.agents/review/remap-fix.result.json`.

**Clean - no findings.** It confirmed the dual-key guard stops the EmergencyDisable path
reaching `RemapKey` while Graph-only modules such as `MfaReset` still migrate, ran
`GraphSecretKeyMigrationTests` itself (8 passed) and `git diff --check` on the range, and
checked the version bump against the Constitution - shared service, so base app moves and no
module version does.

It was asked directly whether the four catalog-driven tests would pass vacuously if the
catalog contained no dual-key module, and whether it still preferred its own allowlist
recommendation over the shape built. It judged the tests not empty-passing and raised no
material objection to the chosen shape.
