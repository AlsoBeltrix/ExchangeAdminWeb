# Emergency Disable - Lockdown OU Move - Plan

Status: **DRAFT - awaiting owner go.** Queue item 18. Nothing below is authorised yet.
Grounding and the owner's rulings of 2026-10-05 are recorded in `.agents/state.md` under
"Queue item 18"; this plan is the durable version of that work and supersedes the state
entry's design notes once approved.

**Revision 1, 2026-10-05:** stamp target is `info` (ADUC Notes), not `description`. Owner
ruling. Nothing else changed.

---

## Exec summary

*This is the only part of this plan the owner reads or is bound by
(`.agents/decisions.md` 2026-09-30, 2026-10-01). Everything below it is agent-facing.*

**What it does.** Emergency Disable gains one more step: after it disables the account, it
moves that account into a lockdown OU and writes where the account came from into its
Notes field, which anyone can read in ADUC. A checkbox on the form turns the move
on or off; it is ticked by default.

**What it gets you.** The reason for the move is the thing the disable alone does not buy: the
lockdown OU is where delegation does not let ordinary helpdesk re-enable an account. Today a
compromised account can be disabled and then switched back on by anyone with routine rights.
After this, it cannot - unless the operator deliberately unticks the box.

**What it costs.** Two slices in one module. The first adds the capability and its tests; the
second puts the checkbox on the page. One new setting to fill in on the module's config screen
(the lockdown OU). Until that setting is filled in, the checkbox shows as unavailable and
Emergency Disable behaves exactly as it does today.

**Biggest risk.** This is the first place in the app that moves an AD object between OUs.
A move changes which group policies and which delegated admins apply to the account - that is
the entire point of it, and it is also what makes it a bigger deal than a password reset. The
plan contains it by refusing to move at all unless the destination is configured and actually
exists, by never moving an account whose disable did not succeed, and by reporting
"disabled, but NOT moved" as its own loud, separate outcome rather than folding it into a
success.

**The checkbox is temporary and the plan says so in the code.** It exists because the
stakeholder has not said what they actually want. When they do, it either goes away or becomes
the settled behaviour.

**What approval authorises:** S1 and S2 below, in the Emergency Disable module only.

---

## Scope

In scope:

- One new module config field: the lockdown OU distinguished name.
- One new runtime option on the Emergency Disable form: a checkbox, default checked.
- One new service step: move the target into the lockdown OU after a successful AD disable.
- One further write: append the account's previous parent OU to its `info` attribute, which
  ADUC labels **Notes** (Telephones tab).
- Audit, security-team notification and the on-screen step table all report which of
  "disabled and locked down" / "disabled WITHOUT lockdown" happened.
- Tests for every decision that can be made without a live directory.

Out of scope, by owner ruling 2026-10-05:

- **Release.** No release action, no round trip, no reading the stamp back, no state file.
  Returning an account to its original OU is a human doing it in ADUC.
- **Multiple identities.** The module stays single-target.
- **Enabling or re-enabling anything.** Unchanged.
- **A separate module or a separate page.** This is a step inside Emergency Disable.

## Source material

`C:\Users\mcoelho\Desktop\Set-AccountSecurityHold.ps1` (owner's Desktop, not in the repo).
Only four of its behaviours survive into this work: resolve the account, capture its current
parent OU, move it to the hold OU, stamp the old OU onto the object. Its release half, its CSV
state file, its GUID-matched release lookup and its dry-run mode are all out of scope.

Four of its decisions are adopted deliberately:

- Confirm the destination OU exists before doing anything, and refuse rather than guess.
- Already in the destination OU is a skip, not a failure.
- Bind the post-move attribute write by **ObjectGUID, not DN** - the DN changed when the
  object moved. This is the single most likely implementation bug in the whole change.
- Stamp `info` read-append-write, as `Set-AccountSecurityHold.ps1:193-201` does. Not adopted:
  the switch being off by default - here the stamp always accompanies a move.

Its `-StampInfo` discretion warning does **not** carry over: it argues a visible stamp leaks a
discreet hold, but the move itself relocates the account into an OU named for lockdown, so the
move is the disclosure and the stamp announces nothing new.

## Binding constraints

1. **Environment neutrality (`.agents/repo-guidance.md` invariant 7, owner ruling 2026-09-11).**
   No source file may name an OU, domain or host. The lockdown OU is a config field, never
   defaulted and never named in source. Test fixtures use obviously synthetic DNs
   (`OU=Lockdown,DC=example,DC=test`). A missing, unreadable or non-existent OU fails the step
   closed; it never guesses a destination.
2. **Protected-principal check (Known Failure Class 3).** Already performed at step 1 of
   `DisableAsync`, before any mutation, and the move happens after it. No second check is
   needed and none is skipped. Emergency Disable is not one of the two scoped exemptions.
3. **No blanket success (Known Failure Class 2).** A requested move that did not happen is its
   own reported outcome and fails the operation overall.
4. **Side-effect ordering (Known Failure Class 1).** The pre-action snapshot, which already
   records the target's full DN including its parent OU, is persisted before any mutation and
   is the authoritative record of where the account came from. The `info` stamp is a
   convenience breadcrumb for a human in ADUC, not the record of truth.
5. **No spinner.** The page already reports through `IActivityProgress`
   (`Progress.Begin(...)` in `ExecuteDisable`). Nothing new is drawn; the existing activity
   covers the longer run.
6. **ClickGate.** `ExchangeAdminWeb.Tests/ClickGateRegistry.cs:81` records
   `EmergencyDisable.razor` as "tier 3, not approved" - it has no line-keyed entries, so page
   edits need no re-anchoring. Verify this is still true before editing.
7. **Nothing in the app may ever parse the stamp** (owner ruling 2026-10-05). It is written and
   never read. That rules out de-duplicating a prior stamp by recognising its format.

## Design decisions

### Ordering: disable, then move, then stamp

The disable is the urgent safety act and must not wait on an OU move. The move is attempted
only after `DisableAD` reports `OK`.

The stamp is written after the move, and must bind by ObjectGUID because the DN has changed.
Writing it after rather than before means the stamp describes something that actually happened;
nothing is lost if it fails, because the original DN is already in the persisted snapshot and
the audit record.

### The move is not attempted when the AD disable failed

Previously open; decided here. Moving a still-enabled account into a lockdown OU produces a
misleading object - a live account sitting where a reader will assume everything is disabled -
and compounds an incident that already needs a human. The step reports
`SKIPPED: AD disable did not succeed; the account was not moved`. The operation is already a
failure on the disable, so this changes no overall verdict.

The move is gated on `DisableAD == "OK"` only, not on the password reset or the Entra steps:
those failing does not make the move wrong or more dangerous.

### The stamp appends and never rewrites

Previously open; decided here. The existing `info` value is read, appended to, and written back.
A prior stamp is **not** removed, because removing it would mean parsing the format, which
constraint 7 forbids. An account emergency-disabled twice therefore carries two notes, which is
an honest record of two events rather than noise.

Two concrete consequences:

- **CRLF separator**, matching `Set-AccountSecurityHold.ps1:200`. Empty existing value gets no
  leading newline.
- **1024-character ceiling** (`info` rangeUpper; verify against the live schema in S1). Over
  it, nothing is written and the step reports `FAILED`. Never truncate silently.

Stamp form (illustrative; no code may depend on it):
`[EMERGENCY DISABLE 2026-10-05] previous OU: OU=Staff,DC=example,DC=test`

### An unconfigured lockdown OU disables the checkbox

The operator must not be able to arm a step that cannot run. With no configured OU the checkbox
renders disabled and unchecked with the reason shown, and the page passes `false` to the
service. This keeps fail-closed behaviour without turning a missing setting into a surprise
mid-disable. The service still refuses independently - the UI gate is not the control.

### The checkbox state is audited either way

An unticked box is a decision, not an absence: it is the operator declining the protection the
step exists to provide. The audit event and the security-team notification both say which of
"disabled and locked down" and "disabled WITHOUT lockdown" happened.

### Overall success accounting

`IsOverallSuccess` gains the lockdown outcome. Success requires the four existing conditions
**and** a lockdown outcome in `{NotRequested, Moved, AlreadyInPlace}`. `Failed` and any
`Skipped` other than already-in-place fail the operation.

The **stamp** is deliberately excluded from overall success. It is a human breadcrumb whose
information is already held authoritatively elsewhere; its failure shows as a red row in the
step table, in the notification and in the audit, and is not silent.

## Implementation

### S1 - service-side capability, not yet reachable from the page

1. `Modules/ModuleCatalog.cs`, `EmergencyDisable` descriptor: add a fourth config field.
   - Key `LockdownOuDn`, label `Lockdown OU`, description naming it as the distinguished name
     of the OU an emergency-disabled account is moved into, and stating that leaving it blank
     makes the lockdown step unavailable.
   - `Required: false`. A required field would flip `ModuleConfigService.IsModuleConfigured`
     to false for every existing install (`Services/ModuleConfigService.cs:70-79`).
   - `FieldType: ConfigFieldType.OU`. Note: `ConfigFieldType.OU` is declared in
     `Modules/ModuleConfigField.cs` but **has no renderer** - `Components/Pages/ModuleConfig.razor`
     only special-cases `AdGroup` and `Boolean`, so it renders as a plain text box today. That
     is a pre-existing gap, not one this work introduces; the type is still the honest one.
   - Do **not** bump `Version` in this slice (see S2).
2. `Services/EmergencyDisableService.cs`:
   - New enum for the outcome: `NotRequested`, `Moved`, `AlreadyInPlace`, `Skipped`, `Failed`.
   - New **pure static** decision function, in the style of the existing
     `ShouldSkipEntraDisable` / `IsOverallSuccess`: given "requested", "disable succeeded",
     "configured OU DN", "target DN", return either the outcome to report without touching AD
     or an instruction to proceed. Everything decidable without a directory lives here so it is
     testable.
   - New **pure static** stamp builder: given the existing `info` value, the previous parent DN
     and a timestamp, return the new `info` value or a refusal when the result would
     exceed 1024 characters. Joins with CRLF when the existing value is non-empty.
   - New private `ExecuteMoveToLockdownOu`, following the shape of `ExecuteDisableAD`:
     own runspace, `Import-Module ActiveDirectory`, credential from the same Delinea secret,
     `VerifyBoundObject` before mutating, `Get-ADOrganizationalUnit` on the configured DN to
     confirm the destination exists, then `Move-ADObject -Identity <dn> -TargetPath <ou>`.
     Then re-read and `Set-ADUser -Identity <ObjectGUID> -Replace @{info=...}`.
     **Bind the attribute write by GUID, not DN.**
   - Take the AD throttle slot once and hold it across move and stamp, exactly as the existing
     code holds it across disable and reset.
   - `DisableAsync` gains a `bool moveToLockdownOu` parameter. Make it **required and not
     defaulted**, for the reason the file already gives for `actingUser`: a default quietly
     makes every caller that forgot it into a caller with different behaviour.
   - Extend `IsOverallSuccess` with the lockdown outcome.
   - `LogAudit`: carry the requested flag and the outcome in the `extra` dictionary (the
     parameter already exists; `ProtectedPrincipalServicing.Extra` shows the shape).
   - `SendSecurityNotificationAsync`: add the lockdown step to `stepDetails`, and make the
     requested-but-not-done case unmistakable in the body.
   - Existing callers in this slice pass `false`, so behaviour is unchanged until S2.
3. Tests in `ExchangeAdminWeb.Tests/EmergencyDisableServiceTests.cs` (see Tests below).

### S2 - the checkbox, and the version bump

1. `Components/Pages/EmergencyDisable.razor`:
   - Inject `ModuleConfigService`; read `LockdownOuDn` once in `OnInitializedAsync` and hold
     whether it is configured.
   - In the confirm panel, below the existing confirmation checkbox, add the lockdown checkbox
     bound to a field initialised `true`, following `Components/Pages/CloudPasswordReset.razor`
     lines 125-132 (the "Force password change at next sign-in" precedent the owner named).
   - When not configured: rendered disabled, bound field forced `false`, with the reason in
     muted text.
   - Add the conditional line to the "You are about to:" list so the confirm panel states what
     will happen.
   - Pass the value to `DisableService.DisableAsync`.
   - `ResetForm` and `PerformLookup` reset the checkbox to its default, not to `false` -
     check both, they both clear state today.
2. `Modules/ModuleCatalog.cs`: bump `EmergencyDisable` `Version` from `1.2.1` to `1.3.0`, with
   a comment line in the existing style saying what 1.3.0 is. The Constitution's rule fires on
   shipped module behaviour, which is this slice, not S1 - do not bump twice.
   Base app `<VersionPrefix>` does **not** move: this is module-scoped.
3. `ExchangeAdminWeb.Tests/EmergencyDisableServiceTests.cs:114` asserts the exact version
   string. Update it in the same commit.

## Tests

New, in `ExchangeAdminWeb.Tests/EmergencyDisableServiceTests.cs`, all without a live directory:

1. Lockdown decision, one case each: not requested; requested with no configured OU; requested
   with a configured OU and a failed AD disable; requested with the target already in the
   destination OU; requested and proceeding; requested with a null target DN.
2. `IsOverallSuccess` with each lockdown outcome, including the case that currently has no
   analogue: everything else OK, lockdown `Failed`, overall failure.
3. Stamp builder: empty existing `info` (no leading newline); existing content preserved and
   appended after a CRLF; a prior stamp left in place (two notes, by design); a result over
   1024 characters refused.
4. Catalog: the `LockdownOuDn` field exists, is `Required: false`, and the version is `1.3.0`
   (S2).
5. Audit: the lockdown request flag and outcome reach the audit event, extending the harness
   already used by `DisableAsync_ServicedOverride_RecordsTheAuthorisingGroupInTheAUDIT`.

**Non-vacuous proof is required** (`.agents/repo-guidance.md`): for at least the "requested but
disable failed" guard and the 1024 ceiling, revert the guard, watch the test fail, restore it,
confirm green. Beware the mutation-probe restore trap - `Copy-Item` preserves the old mtime and
MSBuild then skips the rebuild, so touch the file after restoring.

## Verification

- `dotnet build ExchangeAdminWeb.slnx -c Release`
- `dotnet test ExchangeAdminWeb.slnx`
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`
- `git diff --check HEAD`
- Dispatch codex per slice before moving on, per `.agents/state.md` and the 2026-10-02 lesson
  that four unreviewed commits produced two real findings no test here could reach.

## Manual acceptance - owner or operator, not automatable

Nothing here can be proven by the test suite; it needs a dev deploy and a disposable account.

1. With no lockdown OU configured: the checkbox shows unavailable with its reason, and a
   disable behaves exactly as before.
2. Configure the lockdown OU on the module config screen.
3. Disable a disposable test account with the box ticked: confirm in ADUC that the account is
   disabled, sits in the lockdown OU, and the Notes box on its Telephones tab carries the
   previous OU.
4. Confirm the security-team notification and the audit entry both say it was locked down.
5. Disable a second disposable account with the box unticked: confirm it is disabled, has NOT
   moved, and that the audit and the notification both say so explicitly.
6. Configure a lockdown OU DN that does not exist, tick the box, and run: confirm the account
   is disabled, the lockdown step is loudly reported as failed, and the overall result is not
   presented as a success.

## Risks

- **First OU move in the app.** Nothing else in this codebase calls `Move-ADObject`. The
  service account in the Emergency Disable Delinea secret needs write access to move objects
  into the lockdown OU; if it does not have it, every move fails at runtime with an access
  denial. This cannot be verified from here - manual acceptance step 3 is what proves it.
- **The hold OU's ACLs are an unverified environment fact.** The source script implies the OU
  is permission-restricted (it refers to a companion `Set-SecurityHoldOUPermissions.ps1` that
  is not available). Whether the move is self-disclosing to ordinary readers therefore cannot
  be confirmed, and no part of this design rests on either answer.
- Nothing here is undone by the app: re-enable, move back and clear the stamp are all manual.

## Owner gate

One question, and only one: **go or no-go on S1 and S2 as described in the exec summary.**
Nothing is implemented until that answer arrives.
