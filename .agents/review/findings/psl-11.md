# psl-11: the validate-exists object-kind theory now proves only the unavailable stand-in path

**Severity**: LOW - coverage false confidence. `ExchangeAdminWeb.Tests/ADDirectoryValidateExistsTests.cs` about 245: `Search_AllObjectKindsIncludingOu_DoNotThrow` uses `ADDirectorySearchServiceTests.CreateService()`, whose helper asserts AD is unavailable, so `Search` returns at the availability guard before `ExecuteSearch`; a live-only regression in the User/Group/OU command path (the `Get-ADOrganizationalUnit` branch especially) ships green.
**Status**: Merged to master `c3af5bd4`; codex defect review cr-batch-9 in flight
**Branch**: `fix/psl10-a2y` (worktree `D:\source\ExchangeAdminWeb-wt\psl10-a2y`, from `f7b794cb`, no merge of master)
**Commit**: the commit that adds this line (SHA in the implementer's hand-back)
**Reviewer**: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard; codex-cli 0.163.0; base 3e43b35b head f7b794cb (merged 07f2d371); 2026-10-23 evening; capability proof succeeded (AGENTS.md read, git show --stat f7b794cb)

## Evidence

Codex cr-batch-8 finding 2 at `07f2d371`.

## Approach

- An available non-live stand-in runspace with fake `Get-ADUser`, `Get-ADGroup`, `Get-ADOrganizationalUnit` (the psl-8 / `CreateDirectoryRunspace` seam) asserting each object kind reaches its command; real directory coverage stays opt-in (psl-10 (5)); mutation: one kind's command removed.

### As written before the code (branch `fix/psl10-a2y` from `f7b794cb`, after kd-32 on it)

**Verified.** Codex is right: since psl-10 (4) the theory's service is unavailable, and `Search` returns at its availability
guard before `ExecuteSearch`. And `ADDirectorySearchService` has no seam the search could run through: its only one,
`CreateProbeRunspace` (psl-7), opens the availability probe's session; the search's own session comes from
`GetOrCreateRunspace` (609-624), which builds an `InitialSessionState` that imports the real ActiveDirectory module at
`Open` - the live module, so no test may open it. (`CreateDirectoryRunspace` is the seam of other services, not this one.)

**The change.**
1. `Services/ADDirectorySearchService.cs`: `internal Func<Runspace> CreateSearchRunspace { get; init; } = SearchRunspace;`
   beside `CreateProbeRunspace`, its shape and reason (the class is sealed, so a property); `SearchRunspace()` builds the
   session `GetOrCreateRunspace` built inline, to the line; `GetOrCreateRunspace` opens what the seam hands it. Production
   is unchanged. Shared code (the directory autocomplete every module uses): a base version bump is due, outside this brief.
2. `ExchangeAdminWeb.Tests/ADDirectorySearchServiceTests.cs`: `AvailableStandInService(calls)` - the probe's stand-in import
   loading (so the directory reads as available, checked), and the search's stand-in session: `Get-ADUser`, `Get-ADGroup`,
   `Get-ADOrganizationalUnit` and `Get-ADForest` as advanced functions answering one object each and noting the call,
   autoloading off - nothing in either session can load a module or reach a directory.
3. `ExchangeAdminWeb.Tests/ADDirectoryValidateExistsTests.cs`: `Search_EachObjectKind_ReachesItsCommand` (User, Group, OU, Any)
   beside the fail-soft theory: each kind runs its command and only its own - Group through the forest's global catalog,
   `-Server` "<gc>:3268"; Any user and group, never OU (the remarks on `Search`) - and maps its answer: the OU's label from
   `Name`, as the live class guards, the group's domain from its DN.
4. Mutations: one kind's command removed (the OU branch never taken); a second, the OU's label read from `DisplayName`.

## Files changed

Snapshot before repair: `654ff843` (kd-32, on this branch).

- `Services/ADDirectorySearchService.cs` - `CreateSearchRunspace` and `SearchRunspace()`, new; `GetOrCreateRunspace` opens the seam's
  session (production builds the same session as before).
- `ExchangeAdminWeb.Tests/ADDirectorySearchServiceTests.cs` - `AvailableStandInService`, `StandInSession`, `StandInGlobalCatalog`.
- `ExchangeAdminWeb.Tests/ADDirectoryValidateExistsTests.cs` - `Search_EachObjectKind_ReachesItsCommand`.
- This record.

No operator-visible change: the seam's default is the session the service always opened.

## Guard proof

`ADDirectoryValidateExistsTests.Search_EachObjectKind_ReachesItsCommand` (User, Group, OU, Any), on the available stand-in
(the probe's import loads - checked, with its one import - and the search's session's four commands are local functions,
autoloading off): the calls exactly - User `Get-ADUser`; Group `Get-ADForest` then `Get-ADGroup` with `-Server`
"gc01.example.test:3268"; OU `Get-ADOrganizationalUnit`; Any `Get-ADUser`, `Get-ADForest`, `Get-ADGroup`, and no OU command -
and the results: each kind's object types; the OU's label "Test Rooms" from its `Name` and its DN; the group's domain
"example.test" from its DN. The fail-soft theory beside it is unchanged. Filter `ADDirectoryValidateExistsTests|ADDirectorySearchServiceTests`
less the two live classes (71); `Services/ADDirectorySearchService.cs` md5 fixed = restored `55bd095864bc79233ded44495d4d94f8`.

| # | Mutation (call site, `ExecuteSearch`) | md5 mutated | Failing (of 71) | Restored |
| --- | --- | --- | --- | --- |
| M1 | the OU kind's command removed - its branch never taken (the brief's mutation) | `46089d1299aa70dd80d2080ce070be4a` | 1: the OU case, no `Get-ADOrganizationalUnit` and no OU | PASS 71 |
| M2 | the User kind's command removed - its branch taken for Any only | `096fd52ba1d1d40cff9c1c9a38673f4d` | 1: the User case | PASS 71 |
| M3 | the OU's label read from `DisplayName` (the property-name slip `ADDirectoryLiveTests` exists for) | `e5ec2280d47294f97bf48ed33bee6b56` | 1: the OU case, a blank label | PASS 71 |
| M4 | OUs leaking into Any | `686224547f8d04fac3670a38bf755bbe` | 1: the Any case, an OU command it must not run | PASS 71 |

On the fix, before the round: `ADDirectoryValidateExistsTests`, `ADDirectorySearchServiceTests`, `DirectoryModuleImportTests`
less the live classes 105 of 105. Not run, by design: the theory on the production search session (it imports the real
module - the live call psl-10 (4) removed).

## Coder dispute

The approach names a `CreateDirectoryRunspace` seam; this service had none for its searches (that seam belongs to other
services), so `CreateSearchRunspace` was added in the probe seam's shape. No other dispute.

## Known gaps

- The stand-ins answer with the shapes the mapping reads; that the real cmdlets return those property names stays the live
  classes' job, opt-in by psl-10 (5) - this proves routing and mapping, not the module.
- `ValidateExists` (`ExecuteValidateExists`) opens the same session and can now be driven the same way; not this record's.
- Shared code: `ADDirectorySearchService` gains a seam (no behaviour change) - a base version bump is due, outside this
  brief.

## Closeout

Fixed on branch `fix/psl10-a2y`, not merged, not pushed: the commit that adds this section (its SHA in the implementer's
hand-back). No module version moves (no behaviour changed). Pending: the merge and codex review.