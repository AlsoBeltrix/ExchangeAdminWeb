# Protected-principal servicer: make the group configurable

Status: **Implemented 2026-08-07. NOT YET PROVEN - the manual checks below have not been run.**
Owner approved 2026-08-07; reviewed clean by grok (`grok-4.5-build`, 0 findings) before any code.

Landed as one commit rather than three slices: the editor, its opt-in rule and the save-path wiring
are one change, and the guards assert the result of all three together.

Two corrections found while implementing, both from checking rather than assuming:

- Adding the servicer alias to `policyAliases` (needed for the shared save path) also made the
  ORDINARY grant loop render it a second time - untitled, unwarned, and captioned as plain module
  access. That would have presented a protection bypass as a normal grant. The loop now excludes
  it, and a guard pins that.
- The plan assumed a `TestConfigStore.CreateSectionAccess` helper and a `repo.GetAll()`; neither
  exists. The real API is `TryGetAll(out ...)`, wrapped in a local helper.

## Why

`docs/ProtectedPrincipalBreakGlass-Plan.md` is marked "IMPLEMENTED 2026-08-06 for ONE module
(Blocked Senders)". The mechanism is real: `ProtectedPrincipalServicerService` is registered
(`Program.cs:183`), consumed by `BlockedSenderProtectionGate`, and covered by tests.

**It is unreachable.** No admin surface writes the `ProtectedServicer:<moduleId>` section-access
key, so no group can ever be granted the capability and the feature has never done anything for
anyone.

Verified, not inferred:

- `Components/Pages/ModuleConfig.razor:691-693` builds the section-access editor list from
  `module.MainPermission.PolicyAlias` plus `module.GranularPermissions` only. The servicer key is
  deliberately neither - it is separate precisely so that being able to USE a module never implies
  being able to service protected principals in it.
- No `.razor` in the repo mentions `ProtectedServicer`.
- Both live config stores (dev and prod, 2026-08-07) contain **zero** rows with a
  `ProtectedServicer%` policy alias.

The existing plan calls the inert state "the intended default", which is true and fail-closed. What
it never says is that there is no way OUT of that state short of a hand-written database row. That
omission is this plan's subject.

## Scope

Add the admin surface. Change nothing about how the capability is evaluated.

Out of scope, explicitly:

- `ProtectedPrincipalServicerService.Evaluate` and its fail-closed rules.
- `BlockedSenderProtectionGate` and the audit shape of a serviced action.
- Extending servicing to any further module (that is a per-module commit, per the existing plan).
- Which group and who is in it - runtime data the owner sets in the picker after deploy. The owner
  ruled 2026-08-07 that this is not a build input.

## The hazard this plan must not create

`SectionAccessService.SaveSectionAccess` calls `_repository.SaveAll(...)`, which **replaces the
entire section-access store**. `ModuleConfig.razor:878` avoids data loss only because it reads
`GetSectionAccess()` first and writes the full map back with its own aliases merged in.

So the servicer key is safe today purely as a side effect of that read-modify-write. Two
consequences, both binding on the implementation:

1. The new editor must participate in the **same** read-modify-write, not perform its own save. A
   second save path against a whole-store replace is how one page silently erases another's grants.
2. A test must pin it: saving a module's ordinary access must leave a configured servicer group
   intact, and vice versa. This is the highest-value test in the plan - the failure is silent,
   destroys authorization state, and would surface as an unexplained loss of access.

## Decisions

**D1 - Where does the editor live?** On the existing **Module Config** page for the module, in the
Section Access pane, rendered as a clearly separated block below the ordinary permission grants.
Recommended: it is already the page for "who may do what in this module", it is already
`isGlobalAdmin`-gated, and it already owns the read-modify-write the hazard above requires.

**D2 - Which modules show it?** Only modules that actually consult the servicer service. Today that
is Blocked Senders alone. A module that has not opted in must NOT show a servicer editor, because a
configured group there would grant nothing while appearing to grant something - worse than absent.

Implementation: a static opt-in list in the page, keyed by module id, with a comment naming the
requirement (the module must call `ProtectedPrincipalServicerService.Evaluate`). Deliberately not
inferred from the descriptor: `AdminModuleDescriptor` has no such field, and adding one would
imply every module can opt in by declaration rather than by writing gate code.

**D3 - How is it labelled?** Not "bypass" and not "break-glass". The owner ruled 2026-08-06 that
this is routine authorised work for the executive support team. Proposed heading: **"Protected
principal servicing"**, with body text stating plainly that members may act on protected
principals in this module, that it is separate from module access, and that actions are audited
normally.

**D4 - Warn on grant?** Yes, inline and permanent - not a confirmation dialog. The owner's ruling
forbids per-operation ceremony for the servicers themselves; this is a different audience (a global
admin granting the capability) and a different moment (rare, deliberate). A standing sentence
naming what the grant permits, with no click to dismiss.

## Slices

One commit each.

**Slice 1 - the editor.** Section Access pane gains the servicer block for opted-in modules,
reusing the existing `adm-tbl` grant-row markup and `ADIdentityAutocomplete` (Group / SID), so it
looks and behaves like every other grant list. The alias used is
`ProtectedPrincipalServicerService.SectionKeyFor(module.Id)` - never a hand-built string, so the
page and the service cannot disagree about the key.

Wiring: append the servicer alias to the same `policyAliases`-driven load and save path, so it
inherits the read-modify-write, the dirty tracking, and the per-alias audit already there. This is
the smallest change that satisfies the hazard section.

**Slice 2 - guards.** Tests for:

- `SectionKeyFor` produces the alias the page uses (round-trip, so a rename breaks loudly).
- Saving a module's ordinary grants preserves a configured servicer grant (**the hazard test**).
- Saving a servicer grant preserves other modules' grants.
- A module that has not opted in renders no servicer editor (source assertion).

**Slice 3 - version and docs.** `AdminSettings`/`ModuleConfig` owning module version bumped;
`docs/ProtectedPrincipalBreakGlass-Plan.md` corrected - its "IMPLEMENTED" claim must record that
the capability was unreachable until this plan, since that is exactly the kind of overstatement
that let it sit inert. `README.md` gains a short subsection under Blocked Senders.

## Verification

```powershell
dotnet build ExchangeAdminWeb.slnx -c Release
dotnet test ExchangeAdminWeb.slnx
dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore
git diff --check HEAD
```

Non-vacuity per guard: revert, confirm FAIL, restore, confirm PASS - **and confirm the revert
actually applied to the file before trusting the result.** A probe whose edit silently matched
nothing has produced a false verdict twice in this repo (`blr-3`, `blr-4`).

## Manual validation

Requires a dev deploy and a real group.

1. Module Config for Blocked Senders shows a "Protected principal servicing" block, empty, stating
   that access is denied to everyone while empty.
2. Module Config for a module that has NOT opted in (e.g. MFA Reset) shows no such block.
3. Add a group, save, reload: the grant persists and displays domain / name / SID like other grants.
4. **Save the module's ordinary section access afterwards, reload, and confirm the servicer grant
   is still there.** The whole-store-replace hazard, checked directly.
5. A member of that group unblocks a protected sender in Blocked Senders: allowed, and the audit
   record carries `protectedPrincipalServiced` naming the group.
6. A non-member attempts the same: refused, as before.
7. Remove the group, save: the capability is inert again and the same operator is refused.

Checks 4 and 5 are load-bearing: 4 guards silent destruction of authorization state, 5 is the only
end-to-end proof the feature does anything at all.

## Rollback

Three presentation/config commits. Reverting removes the editor; any group already stored keeps
working, because evaluation is unchanged and reads the store directly.

## Risk

Two, both real:

1. **Whole-store replace** - addressed above; slice 2's hazard test exists for it.
2. **Granting more than intended.** The editor makes a genuine authorization bypass configurable
   from a web page. Mitigated by: `isGlobalAdmin` gating (unchanged), per-module scope, an explicit
   standing warning (D4), an audited change (`LogSettingsChange`, already wired per alias), and the
   evaluation staying fail-closed. Nothing here weakens a protection check; it only makes the
   existing, tested grant reachable.
