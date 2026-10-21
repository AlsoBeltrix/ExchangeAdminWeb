# aud-a38g: the cutover stops the pool it is named, not the pool that serves the instance

**Severity**: LOW-MEDIUM. The `aud-a38c` class, in the cutover. It needs a wrong but existing
pool name, so an operator error starts it; from there the verified stop (`aud-a38e`)
truthfully confirms the WRONG pool stopped while dev keeps running, and the run's own restore
commands then overwrite dev's live database with the stale snapshot.
**Status**: Recorded; approved under the owner's EVERYTHING go, NOT started - its fix needs
`Assert-AppPoolServesPath` (`aud-a38c`), which is on `master` but not in the base of the branch
that found it
**Branch**: --
**Commit**: --
**Reviewer**: implementer (found while fixing `aud-a38e`)

## Evidence

`tools/Move-ConfigDbToShared.ps1` as of `aud-a38f` (`3dd3ca5`). `-DevAppPoolName` and
`-ProdAppPoolName` are free parameters (defaults `ExchangeAdminWebDev`, `ExchangeAdminWeb`),
and nothing in the script asks IIS which pool serves `-DevPublishPath` or `-ProdPublishPath`.
Every IIS action is keyed on the NAME:

- step 1b resolves each named pool's identity (`Get-AppPoolIdentity -Name $DevAppPoolName`);
- step 2 stops each named pool (`Stop-AppPoolChecked`, verified since `aud-a38e`);
- step 5 grants the named pools' identities Modify on the shared directory;
- step 7 starts the named pools (verified since `aud-a38f`).

The promotion had the same gap and `aud-a38c` closed it: `Assert-AppPoolServesPath` in
`tools/AppPoolControl.psm1` (on `master` since `0733f5d`) refuses unless IIS serves the folder
from the named pool and from no other. `deploy.ps1` makes its own binding check. The cutover
makes neither.

## Predicted observable failure

`-DevAppPoolName DefaultAppPool` is passed by mistake (or dev's pool has been renamed and the
default no longer matches). Step 1b resolves `IIS AppPool\DefaultAppPool`. Step 2 stops
DefaultAppPool and IIS confirms it Stopped. The real dev application keeps serving under its
own pool while steps 3 and 4 snapshot its database; step 5 grants the wrong identity; step 6
repoints dev's `appsettings.json`, which the running dev application does not reread for
`ConfigStore:Path`; step 7 starts DefaultAppPool. Step 8 then waits 90 s for a "Config store
schema ready" line dev never writes - it never restarted - and fails, printing the restore
commands. The first of those is
`Copy-SqliteDbFile -SourceDbPath '<shared>' -DestDbPath '<dev's database>'`: the snapshot copied
back over dev's live file, erasing every write dev made after it.

Reasoned from the code, not reproduced.

## Approach (approved, not implemented)

Before anything is stopped - in step 1b, beside the identity read, which is a pure read placed
there because "a bad lookup costs nothing" before the stop - ask IIS, for each instance:
`$null = Assert-AppPoolServesPath -Name $DevAppPoolName -PhysicalPath $DevPublishPath`, and the
same for prod; plan mode prints the check, and when elevated on the host attempts it as it
attempts the identity read. Guards in the `aud-a38e` block's shape: the lifted check driven
through the module's `-ApplicationsProbe`, and an AST pin that both checks precede the first
`Stop-AppPoolChecked`.

**Why not done on the branch that found it**: that branch's base (`ca6575a`) predates
`aud-a38c`, so the module function does not exist there. A local copy of the check in the
cutover would be a second implementation of one rule; a call to the module's would not run.
It belongs on a branch from current `master`.

## Known gaps

- Not reproduced; no IIS was touched.
- The prod side is the same gap with a smaller consequence - prod's database is backed up but
  not copied into the shared file - and would be closed by the same call.
