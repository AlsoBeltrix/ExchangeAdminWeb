# utei-2: A malformed kill-switch value collects telemetry while the UI shows it off

**Severity**: MEDIUM - the privacy kill switch and the screen that displays it
disagree. A corrupted `UsageTelemetryEnabled` value renders as an UNCHECKED checkbox
on the module config page while the service treats it as ON and keeps writing rows.
**Status**: Admitted
**Branch**: -
**Commit**: the commit that completes this record; read it from `git log -1 -- .agents/review/findings/utei-2.md`

## Evidence

`Modules/ModuleCatalog.cs:872` declares `UsageTelemetryEnabled` as a `Boolean` config
field on `AdminEventLog`. `Components/Pages/ModuleConfig.razor:460` computes the
checkbox state as `bool.TryParse(...) && parsedBool`, so any unparseable non-blank
value renders UNCHECKED. `Services/UsageTelemetryService.cs:85-88` returns
`!bool.TryParse(configured, out var enabled) || enabled` - unparseable means ON.
`Services/UsageTelemetryService.cs:167-174` (`Record`) only suppresses the write when
`Enabled()` is false. The Home disclosure is driven by the same `Enabled()` read.

## Predicted observable failure

`module_config` holds `AdminEventLog` / `UsageTelemetryEnabled` = `flase` (or `0`,
`yes`, or anything a direct database edit or a future import could leave behind). The
config page shows the switch unchecked; the Home page shows the "anonymous usage data
is collected" disclosure; rows keep being written. An operator who looks at the switch
to confirm collection is off is told the opposite of the truth.

## What

The unparseable branch was modelled on `PreventSelfGrant`, whose default-on is
correct for a behaviour preference. This field is a privacy control whose state is
also rendered to the operator, so the two readings must agree, and where they cannot,
the safe reading is OFF.

## Approach

`Enabled()` now splits the two cases that were collapsed into one. Absent or blank
still means "never configured" and still defaults ON, exactly as documented. A
non-blank value that will not parse as a Boolean now reads OFF and is logged once,
through the same `LogSwitchUnreadableOnce` helper the store-fault path already used.
The helper gained an optional `malformed` argument so the warning can name the
offending value rather than claiming the read failed.

The reasoning is recorded in the code, not just here: where a privacy control's state
cannot be established, the safe reading and the reading the operator can see on the
config page must be the same one, and both are off. This keeps the switch, the module
config checkbox and the Home page disclosure telling the operator the same thing.

No caching or read-path change is made here; that is utei-3's scope and it must be
layered over these corrected semantics.

## Files changed

- `Services/UsageTelemetryService.cs` - `Enabled()` fail-closed branch for a non-blank
  unparseable value; `LogSwitchUnreadableOnce` takes an optional `malformed` value and
  emits a distinct warning naming it.
- `ExchangeAdminWeb.Tests/UsageTelemetryServiceTests.cs` - three tests added.

## Guard proof

Tests (`ExchangeAdminWeb.Tests/UsageTelemetryServiceTests.cs`):

- `MalformedSwitch_IsDisabled` (:269) - Theory over `flase`, `0`, `1`, `yes`, `on`.
  Asserts `Enabled()` is false and that all four Record methods write nothing.
- `BlankSwitch_IsStillDefaultOn` (:289) - Theory over `""` and `"   "`. Guards the
  other direction: the fix must not turn "never configured" into off.
- `MalformedSwitch_LogsOnce` (:300) - three `Enabled()` calls produce one warning.

Mutation probe (non-vacuity): with `return LogSwitchUnreadableOnce(null, malformed:
configured);` replaced by `return true;` - the pre-fix behaviour - 6 tests fail (all
five `MalformedSwitch_IsDisabled` rows plus `MalformedSwitch_LogsOnce`) and
`BlankSwitch_IsStillDefaultOn` still passes, confirming it guards the opposite case
rather than the same one. The file was restored from a copy outside the working tree,
not by `git checkout`.

Verification after restore: `dotnet build ExchangeAdminWeb.slnx -c Release` succeeded
(0 errors); `dotnet test ExchangeAdminWeb.slnx` 2381 passed / 0 failed / 3 skipped;
`dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore` clean.

## Coder dispute (if any)

None. The blank/absent default-on behaviour is correct and documented and is NOT
changed by this finding; only the non-blank unparseable case is.

## Known gaps

A value written directly into `module_config` by hand is still not validated at the
write seam; this fix makes the read safe, it does not stop the bad value being stored.
The config page itself cannot produce one - its checkbox only ever posts `true` or
`false`.

## Reviewer comments

Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard
  (same dispatch as utei-1)
Harness: codex-cli 0.153.2. Reviewed `e33b201~1..dea899b`, verdict `findings`,
capability_ok true. Envelope `.agents/review/ute-impl.result.json`.
