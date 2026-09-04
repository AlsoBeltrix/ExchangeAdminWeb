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

TBD - fix commit will fill this in.

## Files changed

TBD

## Guard proof

TBD

## Coder dispute (if any)

None. The blank/absent default-on behaviour is correct and documented and is NOT
changed by this finding; only the non-blank unparseable case is.

## Known gaps

TBD

## Reviewer comments

Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard
  (same dispatch as utei-1)
Harness: codex-cli 0.153.2. Reviewed `e33b201~1..dea899b`, verdict `findings`,
capability_ok true. Envelope `.agents/review/ute-impl.result.json`.
