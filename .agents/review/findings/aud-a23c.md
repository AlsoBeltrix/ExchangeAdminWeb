# aud-a23c: Migration rebuilds a permission result field by field and drops AppliedRights and Changed

**Severity**: LOW. Latent: no Migration result carries either field today, and nothing on the
Migration path reads them. It is the field-by-field-rebuild shape `WithServicedNote` had to be
fixed for in `aud-a23` - here five times over, in one module - and each copy drops the next
field `PermissionResult` gains the same way.
**Status**: Fixed on master `a3f3ec45` (patch applied by the supervisor); closed on guard proof; codex review pending
**Branch**: -- (delivered as a patch against `master` `1c6038d` from the worktree
`agent-af538bba9cd55306a`, whose own branch predates `aud-a23b`; the coordinator applies and
commits it)
**Commit**: `a3f3ec45`
**Reviewer**: implementer (surfaced by the `aud-a23b` agent)

## Evidence

`aud-a23b`'s known gaps: "`MigrationService`'s protected-exclusion re-wrap (`:557-566`) builds a
new `PermissionResult` without `AppliedRights` or `Changed`."

`Services/MigrationService.cs` at `1c6038d`, the end of `CreateMigrationBatchAsync`:

```csharp
555:        if (excludedTargets.Count > 0 || servicedSummary != null)
556:        {
557:            return new PermissionResult
558:            {
559:                Success = result.Success,
560:                Message = result.Success && excludedTargets.Count > 0
561:                    ? $"{result.Message} NOTE: {excludedTargets.Count} protected principal(s) were excluded and NOT migrated."
562:                    : result.Message,
563:                Detail = result.Detail,
564:                ServicedNote = servicedSummary,
565:                ExcludedTargets = excludedTargets
566:            };
567:        }
```

`Models/PermissionResult.cs` at `1c6038d`: `AppliedRights` (line 31, `aud-a23`) and
`Changed { get; init; } = true;` (line 37, `aud-a25`), and the model's own re-wrap, which carries
both:

```csharp
81:    public PermissionResult WithServicedNote(string? servicedNote)
...
86:                Success = Success,
87:                Message = Message,
88:                Detail = Detail,
89:                ExcludedTargets = ExcludedTargets,
90:                AppliedRights = AppliedRights,
91:                Changed = Changed,
92:                ServicedNote = servicedNote,
```

**Mechanism: no error at all.** An object initializer that leaves out an `init` property leaves
it at its declared default, so the re-wrap returns `AppliedRights` null and `Changed` TRUE
whatever the result it wraps said. To the brief's question - does `Changed` survive? - no: it is
not carried, it is reset to `true`, which is the trap `aud-a25`'s record names ("a future no-op
that forgets `NoChange()` is announced as a change").

**The survey.** Every field-by-field copy of a `PermissionResult` in the production trees
(`Authorization`, `Components`, `Middleware`, `Models`, `Modules`, `Services`, the root `.cs`
files), found by their signature, an initializer line `Success = <x>.Success`: the one above, and
four more in `Components/Pages/Migration.razor` - the catches that note a failed audit write:

```csharp
2068:            catch (Exception ex)
2069:            {
2070:                singleCreateResult = new PermissionResult
2071:                {
2072:                    Success = singleCreateResult.Success,
2073:                    Message = singleCreateResult.Message,
2074:                    Detail = $"Audit logging failed: {ex.Message}",
2075:                    ServicedNote = singleCreateResult.ServicedNote,
2076:                    ExcludedTargets = singleCreateResult.ExcludedTargets
2077:                };
2078:            }
```

`2223-2230` is the same for `createResult` (the bulk create). `3327-3332` (`ExecuteBatchAction`)
and `3396-3401` (`ExecuteUserAction`) copy only `Success` and `Message` from `result` and drop
`ServicedNote` and `ExcludedTargets` as well. All five drop `AppliedRights` and reset `Changed`.
`WithServicedNote` is the only other re-wrap and carries every field. So every re-wrap that drops
a field is in the Migration module.

## Predicted observable failure

Not reachable today. `RunAsync` builds the wrapped result from `Success`, `Message` and `Detail`
only (`Services/ExchangeServiceBase.cs:62`), no Migration method returns `NoChange` or sets
`AppliedRights`, and neither the page nor its notifications read either field after a re-wrap.

It becomes reachable the day a Migration path reports either - for example "batch already
started" returned as `NoChange`, the `aud-a25` idiom for an idempotent success. A batch with one
protected target excluded then comes back through the exclusion re-wrap as `Changed = true`, and
any gate written the `aud-a25` way (`Success && Changed`) announces a change that did not happen;
an audit write that fails does the same through the page's catch.

## Plan (approved by the owner's EVERYTHING go)

Owner, 2026-10-21: "fix all the bugs that you know about and any more you find while doing
so" and "... EVERYTHING." (`.agents/decisions.md`, 2026-10-21 "Every open review follow-up
is approved for implementation"). The supervisor admitted this from `aud-a23b`'s known gap with
"fix so every re-wrap carries AppliedRights and Changed"; the survey above found every such
re-wrap in this one module. Written before the code.

1. **Two copies, each naming every property, and no others in the module.**
   `Services/MigrationService.cs` gains two `internal static` helpers, written the way
   `WithServicedNote` is:
   - `WithProtectionOutcome(result, excludedTargets, servicedSummary)` - the exclusion re-wrap's
     body, moved unchanged for the three fields it owns (the `NOTE` on a success's `Message`,
     `ExcludedTargets`, `ServicedNote`), and now carrying `Success`, `Detail`, `AppliedRights` and
     `Changed` from the result it wraps.
   - `WithDetail(result, detail)` - replaces `Detail` and carries every other field.
2. **The call sites.** `CreateMigrationBatchAsync` returns
   `WithProtectionOutcome(result, excludedTargets, servicedSummary)` where the initializer was.
   The page's four audit-failure catches become
   `<target> = MigrationService.WithDetail(<source>, $"Audit logging failed: {ex.Message}");`,
   each with the source it copies today. At `3327` and `3396` that also carries `ServicedNote` and
   `ExcludedTargets`, which those two dropped; nothing displays or reads either there (the alert
   renders `Success`, `Message` and `Detail`), so nothing visible moves.
3. **`PermissionResult` is not changed.** The fix is the module's: Migration only, no shared
   change, so no base app version bump; the Migration module bump is the supervisor's.
4. **The page loses 24 lines** (each catch body becomes one statement). Every line-keyed
   `ClickGateRegistry` entry for `Migration.razor` is at line 1540 or earlier and every edit is at
   line 2070 or later, so no entry moves; `ExpectedLineCount` goes from 4401 to the new count. (The
   Migration entry's prose cites method lines - 2526, 2623, 2629, 3133 - that are already stale on
   `master` and checked by nothing; not touched here.)
5. **Guards**, `ExchangeAdminWeb.Tests/MigrationResultRewrapTests.cs`:
   - the exclusion re-wrap keeps `AppliedRights` and `Changed` (and `Success`, `Detail`) and sets
     the three fields it owns exactly as before; of a failure, its message is left alone;
   - the audit-failure re-wrap keeps every field but `Detail`;
   - reflection, per helper: every public property of `PermissionResult` set to a non-default
     value, re-wrapped, and every property the helper does not own compared - so the next field
     the model gains is held to the same rule, and a property type the test cannot populate fails
     it loudly rather than being skipped;
   - source (comments blanked): the exclusion path returns
     `WithProtectionOutcome(result, excludedTargets, servicedSummary)`; `MigrationService.cs` holds
     exactly the two helpers' copies (`Success = result.Success` twice); `Migration.razor` holds no
     field-by-field copy and makes exactly four `MigrationService.WithDetail(` calls.
6. **Mutations planned**, each at the call site: `AppliedRights` dropped from
   `WithProtectionOutcome`; `Changed` dropped from it; `AppliedRights` dropped from `WithDetail`;
   `Changed` dropped from it; the exclusion return restored to the pre-fix initializer; one page
   catch restored to its pre-fix initializer.

## Approach

As planned.

`Services/MigrationService.cs` after the fix:

- Line 557: `return WithProtectionOutcome(result, excludedTargets, servicedSummary);` where the
  initializer was; the comment above it (552-554) now ends "without losing anything else".
- Lines 563-589: `WithProtectionOutcome` - its doc comment, then an initializer naming all seven
  properties: `Success`, `Detail`, `AppliedRights` and `Changed` from the result; `Message` with
  the NOTE on a success with exclusions, exactly as before; `ExcludedTargets` and `ServicedNote`
  from the batch.
- Lines 591-610: `WithDetail` - replaces `Detail`, carries the other six.

`Components/Pages/Migration.razor` after the fix - each catch is one statement:

```csharp
2070:                singleCreateResult = MigrationService.WithDetail(singleCreateResult, $"Audit logging failed: {ex.Message}");
2216:                createResult = MigrationService.WithDetail(createResult, $"Audit logging failed: {ex.Message}");
3313:                batchActionResult = MigrationService.WithDetail(result, $"Audit logging failed: {ex.Message}");
3377:                batchActionResult = MigrationService.WithDetail(result, $"Audit logging failed: {ex.Message}");
```

The page is 4377 lines (4401 - 24). `ExchangeAdminWeb.Tests/ClickGateRegistry.cs`: the Migration
entry's `ExpectedLineCount` 4401 -> 4377, nothing else; the ClickGate suite passes unchanged
otherwise (no line-keyed entry was at or after line 2070).

**Version**: module-scoped - **Migration**. No shared code changed (`PermissionResult` is
untouched), so no base app version bump. The Migration module bump is the supervisor's.

## Files changed

- `Services/MigrationService.cs` - the two helpers and the exclusion call site.
- `Components/Pages/Migration.razor` - the four catches.
- `ExchangeAdminWeb.Tests/MigrationResultRewrapTests.cs` - new, 7 tests.
- `ExchangeAdminWeb.Tests/ClickGateRegistry.cs` - Migration `ExpectedLineCount`.
- This record.

## Guard proof

The helpers are driven for real; the call sites are source scans (comments blanked, through
`RazorSyntax.CommentsBlanked`), because the paths around them need an Exchange runspace
(`RunAsync`) or a rendered circuit.

| Test (`ExchangeAdminWeb.Tests/MigrationResultRewrapTests.cs`) | Pins |
| --- | --- |
| `TheExclusionRewrap_KeepsAppliedRightsAndChanged` | **the finding**: a result carrying `AppliedRights` and `Changed = false` keeps both (and `Success`, `Detail`) through `WithProtectionOutcome`; the three fields it owns come out exactly as the inline re-wrap set them, the NOTE text included |
| `TheExclusionRewrap_OfAFailure_LeavesItsMessageAlone` | a failure's message gets no NOTE; `AppliedRights` and `Changed` survive there too |
| `TheAuditFailureRewrap_KeepsEveryFieldButDetail` | `WithDetail` replaces `Detail` and keeps the other six, `AppliedRights` and `Changed` included |
| `EveryRewrap_CarriesEveryFieldItDoesNotOwn` (`WithProtectionOutcome`, `WithDetail`) | reflection over every settable public property of `PermissionResult`, each set to a non-default value (a type the test cannot fill throws, so a new property cannot be skipped silently): each property the helper does not own is carried; each it owns is not simply passed through; `AppliedRights` and `Changed` are among those checked |
| `TheExclusionPath_ReturnsThroughTheHelper_AndTheServiceHoldsNoOtherCopy` | in `CreateMigrationBatchAsync` (`AuditCategoryFilingTests.MethodBody`) the return is `WithProtectionOutcome(result, excludedTargets, servicedSummary)` and no `Success = <x>.Success` copy remains; the file holds exactly two such lines, the helpers' |
| `ThePage_CopiesNoResultFieldByField_AndNotesAFailedAuditThroughTheHelper` | `Migration.razor` holds no `Success = <x>.Success` copy and exactly four `MigrationService.WithDetail(<x>, $"Audit logging failed: {ex.Message}");` |

Mutations, fixed md5s (pre-comment, LF - the content as committed):
`Services/MigrationService.cs` `C4EBBAC679EF8F4E115FB93647700AA5`,
`Components/Pages/Migration.razor` `9BE6F8EDBBCAA826B2C4773E55866BE1`. Each was applied by exact-text
edits asserted to match once, confirmed by md5, built in Release and run against
`MigrationResultRewrapTests`, the ClickGate suite and every `MigrationService*` class (339 tests),
then the fixed bytes restored, touched (so MSBuild rebuilds rather than testing the mutated
binary) and md5-confirmed.

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| C1: `AppliedRights` dropped from `WithProtectionOutcome` | `00416B2B76EAFFF5FFF789C0B418B97A` | 3: `TheExclusionRewrap_KeepsAppliedRightsAndChanged`, `TheExclusionRewrap_OfAFailure_LeavesItsMessageAlone`, `EveryRewrap_CarriesEveryFieldItDoesNotOwn(WithProtectionOutcome)` |
| C2: `Changed` dropped from `WithProtectionOutcome` | `76DA8E660CE3EE817B42545CF515489A` | 3: the same three |
| C3: `AppliedRights` dropped from `WithDetail` | `7E044A3DE7B07B75466D5CC2608F91DC` | 2: `TheAuditFailureRewrap_KeepsEveryFieldButDetail`, `EveryRewrap_CarriesEveryFieldItDoesNotOwn(WithDetail)` |
| C4: `Changed` dropped from `WithDetail` | `ED658095DC5F83CF25B3FC1C808DC720` | 2: the same two |
| C5: the exclusion return restored to the pre-fix initializer | `E583745AA09ABE19D5F783C77458C947` | 1: `TheExclusionPath_ReturnsThroughTheHelper_AndTheServiceHoldsNoOtherCopy` |
| C6: the single-create catch on the page restored to its pre-fix initializer (`Migration.razor`) | `01E6AC573860E618CE69EBCD72A0B4C9` | 2: `ThePage_CopiesNoResultFieldByField_...`, and `ClickGateTests.TheRegisteredLineCountStillMatchesTheFile(Migration.razor)` (the restored block is 7 lines longer) |

Post-comment md5s (LF): `Services/MigrationService.cs` `A33487CDE47E2B966678A2CFB2231924`;
`Components/Pages/Migration.razor` `9BE6F8EDBBCAA826B2C4773E55866BE1` (no comment added there);
`ExchangeAdminWeb.Tests/MigrationResultRewrapTests.cs` `991A3F9AD3CCD1D68C2DA5B992383DF5`;
`ExchangeAdminWeb.Tests/ClickGateRegistry.cs` `D0F44ECF614F543CF9B8E61524EBA76B`.

## Gates

On an export (`git archive`) of `master` `1c6038d` with this change applied, on the final
(commented) code:

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors. No warning in a changed file but the
  page's existing CS8604, moved up 24 lines with the page (4075 -> 4051). The NuGet audit warnings
  (NU1902/NU1903) are on `master`'s own build too.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m`: 4998 passed, 3
  skipped (the three known `ADDirectorySearchServiceTests` AD skips), 0 failed - 5001, of which 7
  are new; 5 m 18 s, no hang.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: clean (exit 0).
- ASCII: `tools/Test-AsciiOnly.ps1` needs git, so its rule was applied by a script to every `.cs`,
  `.ps1` and `.psm1` of the export outside `bin\`/`obj\` (487 files): passed.
- `git diff --check HEAD`: not runnable outside a repository; the patch's added lines checked
  against the default rules (blank-at-eol, space-before-tab, blank-at-eof): clean.

The supervisor re-runs the gates in a checkout after applying the patch.

## Known gaps

- **No Exchange and no circuit were reached.** The helpers are driven for real; that the
  exclusion path and the page call them is a source scan, as for `aud-a23` and `aud-a23b`.
- **The reflection guard covers settable properties.** A get-only, computed property added to
  `PermissionResult` is derived from the others and is not checked; a settable one of a type the
  test does not know fails the test until it is taught a value.
- **The exclusion re-wrap still replaces `ServicedNote` with the batch's own summary.** That is
  the field it owns: `RunAsync`'s result never carries one (`ExchangeServiceBase.cs:62`), and the
  batch's serviced overrides are the note this operation has. Unchanged behaviour.
- **On an audit failure the page still replaces the result's `Detail`** (on a create, the batch
  summary with its excluded-target list) with the audit error. Unchanged; on the two create paths
  the excluded targets are still shown, from `ExcludedTargets`, which those catches already
  carried.
