# Defect review request: settings-save audit on three pages, the attribute editor page's refusals, allowlist text and rows, the concurrent allowlist save (aud-a28g, aud-a28h, aud-a26f, aud-a28i)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `91e63d47` or later.
- The change is the branch merged at `f1b4692d`: `42b33629` (plans), `e993a83f` (aud-a28g),
  `87833faf` (aud-a28h), `887d8c22` (aud-a26f), `ff6823b8` (aud-a28i), `87660ef8` (comments only),
  `2deea79c` (record SHAs), forked from `22833c0d`. Review `git diff 22833c0d 2deea79c`, and read the
  COMBINED `Services/ADAttributeEditorService.cs`, `Services/SettingsSaveAudit.cs` (new),
  `Services/Storage/AttributeEditorRepository.cs` (`ReplaceAllowlist`, `ReadAllowlistSnapshot`),
  `Components/Pages/ADAttributeEditor.razor` (`ConfirmSave`), `Components/Pages/ModuleConfig.razor`,
  `Components/Pages/AdminSettings.razor`, `Components/Pages/ExchangeOnlineConfig.razor` (every save
  handler) on master.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`,
  `git grep`). One command per call. No writes, no builds, no network.
- Records: `.agents/review/findings/aud-a28g.md`, `aud-a28h.md` (nine-handler survey table), `aud-a26f.md`,
  `aud-a28i.md`; parents `aud-a28f.md`, `aud-a26e.md`, `aud-a28e.md`, `aud-a33b.md`. ALREADY admitted
  from the implementer's hand-back and out of scope: `aud-a28j` (AdminSettings protected-principals
  save has no corrupt/unreadable re-check and can wipe every rule; ExchangeOnlineConfig saves lack a
  corrupt-store refusal), `aud-a28k` (whole-map/whole-table overwrites from page snapshots on
  enablement and section access; empty before-state on two success records; SaveLogLevel records
  success when the level did not persist), `aud-a28l` (AdminEventLog undo's authorization refusal
  unaudited; the public `SaveAllowlist` now has no caller and no version check), `aud-a26h` (other
  corrupt-store texts name retired JSON files; a DrainPool failure after a successful save is shown as
  a failed save). Do not re-raise them. Repo rules: `AGENTS.md`; `.agents/repo-guidance.md` "Known
  Failure Classes" (1, 2, 3), Architectural Invariant 2; `docs/ProjectConstitution.md` audit rules.

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat f1b4692d`. Report whether
both succeeded.

## Mandate

Hunt for observable defects. a28g: the corrupt and level checks moved into `SaveAndAuditAsync` - any
caller (the undo service) whose behaviour changed in a way that is wrong (the record says an undo
above the operator's level now reads "Access denied" at `LevelCheck` - acceptable?); exactly one
record per attempt (page + service); the `(none)` target on the lookup refusal. a28h: the nine
handlers - any refusal return or catch still unrecorded (count them yourself against the survey);
the multi-section records (`sections` list) - can a filter on the Admin Event Log find them; the
ip read once; a success record that could now be followed by a catch record on the same attempt;
the extension-method helpers versus `UsageTelemetryServiceTests`' every-public-audit-method list.
a26f: blank/duplicate refusals ordering versus the pattern check; the banner surfacing the import
reason - safe content; the README and message text now naming the `editable_attribute` table and
page. a28i: `ReplaceAllowlist(decide)` - every decision inside the write, the private exception for
rollback (does `SqliteConfigStore.Write` really roll back the token bump on any throw; is the
exception type leaking), `SQLITE_BUSY` under `BEGIN IMMEDIATE` with two instances; the SHA-256
version over rows "in name order" (two rows with the same name - refused earlier by a26f, fine - but
case?); the page keeping the version across saves and reloads; the `ConcurrentChange` message
telling the admin what to do. Tests passing for the wrong reason: the reflection-driven page
handlers with a do-nothing renderer; the hook that runs B's save as A's write opens.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For
each: title; file and line; triggering input or state; predicted observable failure;
severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a better approach. No style
preferences, no out-of-scope work.
