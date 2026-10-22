# aud-a28b: SaveAsync has no happy-path behavioural coverage

**Severity**: MEDIUM. Not a defect in shipped behaviour - a gap in what the suite can detect.
**Status**: Merged to master (`5e55e6aa`); closed on guard proof; codex attr-batch-1 clean
**Branch**: `worktree-agent-acad2a289bd002e11`
**Commit**: `5b5bd6f6` (merged `5e55e6aa`)

## Evidence

Found while fixing `aud-a28`. A broken edit made
`ADAttributeEditorService.SaveAsync` return `(false, "Attribute allowlist configuration is
corrupt.")` on **every** call, unconditionally, before reaching validation, the
protected-principal check, credentials or the write.

The full suite passed: 4397 passed, 3 known AD skips, 0 failed.

Nothing in `ExchangeAdminWeb.Tests/` drives `SaveAsync` to a successful save, or to any
specific refusal, and asserts on the result. The method's coverage is entirely source scans -
`RefusalAuditTests`, `StaleTargetDisciplineTests`, the servicer guards - which read the file
as text and so cannot see reachability.

## Predicted observable failure

Any regression that makes `SaveAsync` refuse early ships green. The operator sees a refusal
message that names a cause unrelated to what they did; the attribute editor is inert and the
suite says the repo is healthy.

## Approach (proposed, not approved)

`SaveAsync` reaches AD and Delinea through `_moduleCredentials` and `Task.Run(PerformSave)`.
The refusals above those calls - allowlist, validation, denylist, protection check - are all
reachable with substitutes for `IModuleCredentialProvider` and `IProtectedPrincipalService`,
which are already faked elsewhere in the suite. That covers five of the seven refusals and,
with a credential provider that returns a value, proves the method gets as far as attempting
a write without needing a directory.

The happy path past `PerformSave` needs a seam that does not exist yet; that part is the
larger piece and may not be worth it.

## Known gaps

- Scope is one method. `PreviewAsync` in the same service has the same shape and the same
  absence of coverage. Other services were not surveyed.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"). One commit
`test(ad-attribute-editor): ... (aud-a28b)`, after aud-a26b. Tests only: no production change,
because aud-a26b already added the two seams (`CreateDirectoryRunspace`, `GetCredentialsAsync`)
and `AttributeEditorHarness`, which runs the real SaveAsync against local stand-ins for
Import-Module, Get-ADUser and Set-ADUser with module autoloading off. The "happy path past
PerformSave needs a seam" gap this record named is therefore closed by the seam, not by a new one.

New `ExchangeAdminWeb.Tests/ADAttributeEditorSaveTests.cs`, behavioural, asserting on the result,
on the stand-in directory's Set-ADUser calls, on how many directory sessions were opened, and on
the audit records read back from the real JSONL writer:

- happy path: one Set-ADUser to the bound DN with the replace and the clear; the applied list
  returned; an `ADAttributeEditor_Update` Success record with old/new values, the object GUID,
  ticket, actor and ip; no refusal record;
- refusals, each opening no directory session and writing an `ADAttributeEdit_Refused` record with
  `refusedAt` and `mutationAttempted: false` (RefusalAuditTests' fields, now read from the record
  instead of the source): a protected target; a FAILED protection read (fail closed); an
  unreadable servicer grant on a protected target (fail closed), with its readable twin as the
  control that the servicer path is otherwise open and records the grant; missing credentials;
  an attribute above the caller's level; a denylisted attribute; a corrupt allowlist;
- the bound-object check: a re-read GUID that differs from the lookup's refuses with no write and
  an audited failure.

Mutations at the call sites, each required to fail a named test: the record's own (SaveAsync
refusing every call with the corrupt-allowlist message), the protected refusal disabled, the
failed check let through, the protected refusal's LogRefusal deleted, the success LogAudit
deleted, the servicer note dropped from the success audit, the level filter disabled, the GUID
comparison disabled.

## Fix

Tests only; no production byte changed in this commit.

- `ExchangeAdminWeb.Tests/ADAttributeEditorSaveTests.cs` (new, 10 tests): the happy path, seven
  refusals before the directory (each asserting the refusal record's action, `refusedAt`,
  `mutationAttempted: false`, error, target, actor, ip and ticket, and that no directory session
  was opened), the servicer control, and the bound-object GUID check.
- `ExchangeAdminWeb.Tests/AttributeEditorHarness.cs`: `CorruptAllowlist()` (an unparseable
  `choices_json` row, the store's corrupt state).

## Guard proof

Same session-local driver as aud-a26b (`acad2a-mutate.ps1`): exact bytes saved, one literal
replacement matching exactly once, Release build with 0 `error CS` required, filter
`FullyQualifiedName~ADAttributeEditorSaveTests`, exact restore in `finally`, MD5 re-checked, mtime
advanced; after the round a rebuild and the filter again, 10/10 green. Every mutation is in
`Services/ADAttributeEditorService.cs`, original (committed, `277f2b9a`) MD5
`C30973909BB6E08176984458B5A2E0AD`, restored to it after each.

| Mutation (call site) | Mutant MD5 | Named test failing |
|---|---|---|
| this record's own: `if (allowlist == null \|\| allowlist.Count >= 0)`, so every call refuses as a corrupt allowlist | `8D678A8142DAF8DE90EC56243AF32435` | `AnOrdinaryEdit_IsWrittenOnceToTheBoundObject_AndAudited` (9 of 10 fail; only the corrupt-allowlist test passes) |
| protected refusal disabled (`false && protectionResult.IsProtected`) | `6771EAC33C3A63181BFB1D84C4DC7993` | `AProtectedTarget_IsRefusedBeforeTheDirectory_AndTheRefusalIsAudited` |
| failed check let through (`false && protectionResult.CheckFailed`) | `EB0F9E63C167CC80C9CA436DB23A7995` | `AFailedProtectionRead_Refuses_FailClosed` |
| the protected refusal's `LogRefusal` deleted | `65512AF4679612212AA5D71C601D1432` | `AProtectedTarget_IsRefusedBeforeTheDirectory_AndTheRefusalIsAudited` |
| the success `LogAudit` deleted | `29EA8B9C6BC35963B848DA6CF639F94C` | `AnOrdinaryEdit_IsWrittenOnceToTheBoundObject_AndAudited` |
| the servicer note dropped from the success `LogAudit` | `A14AD6DCE11B288887EC9C0AB354E131` | `AnAuthorisedServicer_IsWritten_AndTheAuditNamesTheGrant` |
| level filter disabled (`false && maxLevel.HasValue`) | `CC741133D180C08E61ED83EB74CA1CC0` | `AnAttributeAboveTheCallersLevel_IsRefused` |
| bound-object GUID comparison disabled | `3F80EC7E3A25AE5E6AE195AED3095500` | `AReReadThatIsNotTheLookedUpObject_IsRefused_WithNoWrite` |

Every mutant built with 0 errors.

## Gates - 2026-10-21

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors, 40 warnings (incremental), all
  pre-existing.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m`: exit 0, 4,508
  passed / 3 skipped / 0 failed, 4,511 total, 5m06s. No hang.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0.
  `git diff --check HEAD`: exit 0. `tools/Test-AsciiOnly.ps1` with the new file staged: exit 0.

No production, PowerShell, directory, Delinea, SMTP, push or deployment change. No coder
self-review verdict is claimed.

## Known gaps (after the fix)

- `PreviewAsync` is still untested behaviourally: its re-read opens its own session outside the
  seam (`ReReadCurrentValues`), which this record's scope did not touch.
- Throttle exhaustion ("AD service is busy.") is not driven: it needs two saves held for two
  minutes. Its refusal is still pinned by RefusalAuditTests' source scans.
- The stand-in cmdlets are the evidence; no real directory, Delinea or SMTP was exercised.
