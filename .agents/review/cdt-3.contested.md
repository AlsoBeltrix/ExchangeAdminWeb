# cdt-3 (declined): "plan-only paths still execute the app db tool"

Codex S2 candidate 2 over `9e7f9f9..b1577f7`: `Assert-NoActiveBulkJobsBeforeRecycle -PlanOnly`
runs `db active-jobs` before checking `-PlanOnly`, and the cutover's `Assert-SharedDbUsable`
runs `db check` in plan mode.

Declined, one reason: both behaviours predate S2 and are deliberate. Before S2 the same two
paths ran `sqlite3.exe` read-only in plan mode (`JobStateWarning.psm1` line 113 vs 115 at
`9e7f9f9`; `Move-ConfigDbToShared.ps1` line 539 at `9e7f9f9`, review finding scdi-3, whose
record requires the broken-shared-file report "in plan mode too"). Plan mode's contract in
this repo is "creates and changes nothing", which both satisfy: they read a database so the
plan can say what a real run would find. S2 swapped the reader, not the behaviour. The
"no-execute" wording came from the review prompt, not from the plan or repo guidance.
