# aud-a26p: Settings pages - the Secret ID field is unvalidated, fallback-sourced fields are unmarked, a casing-only edit is not dirty, and parallel PowerShell-session test classes can race on start-up

**Severity**: LOW. (1) The directory-read Secret ID field accepts any text; nothing checks it is a
Secret Server id (an integer) before it is stored, so a typo is found only when the read fails
later. (2) The Exchange Online Config page does not mark which fields show the appsettings fallback
rather than a stored value (Admin Settings now does, aud-a26n). (3) The protected-principals dirty
count compares ignoring case, so an edit that changes only casing does not count as unsaved and the
Save bar does not appear. (4) Test infrastructure: two test classes opening PowerShell sessions at
the same moment can hit PowerShell's start-up race ("An item with the same key has already been
added. Key: Alias"), seen once in `ADAttributeEditorSaveTests` during a filtered run - a flaky
failure source for the full suite.
**Status**: Fixed on the branch; closed on guard proof; awaiting merge and codex review
**Branch**: `worktree-agent-a992f9d6471a07501`
**Commit**: `ec2e09c3` (plan `c8deba78`; comments `07ef4e8e`)
**Reviewer**: implementer (surfaced by the aud-a28p/a26n agent, 2026-10-22, candidates 1, 3, 4 and 6 of its hand-back; its candidate 5, a connection change on one instance not draining the other's pool, is the scdi-1 design - each borrow compares the config generation - and is not admitted)

## Evidence

At the aud-a28p/a26n branch tip `a904c288`: `Components/Pages/AdminSettings.razor` (secret id input;
dirty count), `Components/Pages/ExchangeOnlineConfig.razor` (field rendering), the test classes that
construct `PowerShell` sessions in parallel (xUnit v3 runs classes in parallel by default).

## Approach (to be written by the implementing agent before code)

- Secret ID: validate as a positive integer at save; refuse otherwise, audited (aud-a28h helper),
  naming the field. No guess about what the admin meant.
- Fallback marking: the Exchange Online Config page shows "not set in the database; the configured
  fallback applies" on each such field, as Admin Settings does.
- Dirty count: compare ordinal (casing counts as a change), consistent with what the save writes.
- Test race: either a shared xUnit collection for every class that opens a PowerShell session (so
  they run serially), or a one-time static initialisation guard in the harness; say which and why;
  pin with a test that opens two sessions concurrently through the harness.
- Mutations at the call site: accept any text again; drop the fallback marking; compare
  case-insensitively again; remove the collection attribute from one class.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted at `34c7ccd4`. One commit
`fix(settings): the Secret ID is checked before it is stored, Exchange Online Config marks the fields a fallback
supplies, a casing-only edit counts, and PowerShell starts once before any test (aud-a26p)`, after aud-a28r on this
branch. Behaviour: Admin Settings (base app version) and Exchange Online Config (the Exchange Online module's
version); part 4 is test infrastructure and ships nothing. The supervisor owns both bumps.

Evidence re-read at `55a111e9`, and the four parts:

1. **Secret ID.** `AdminSettings.razor` 273-296 is a text input; `SaveProtectedPrincipals` 1132-1133 takes the
   trimmed field and writes it when it changed, unchecked. The lookups read it through
   `ProtectedPrincipalService.PositiveId` 1306-1307 (`int.TryParse`, greater than zero) and otherwise fall back.
   Fix: a changed, non-blank value the lookups would not read as a Secret ID is refused before anything is written
   - refusal `Validation` under `ProtectedPrincipals` (`LogSettingsSaveRefused`, `mutationAttempted: false`), the
   message naming the field ("Directory Read Credential (Delinea Secret ID)") and what it takes. The rule is the
   lookups' own, shared as new `ProtectedPrincipalService.IsSecretId`: nothing accepted that they would not use,
   nothing guessed or normalised. Blank stays allowed - it clears the stored id (aud-a26n). An unchanged value is
   not checked: the save does not write it. Placed after the `!ppLoaded` refusal (1124), before the secret's store
   checks (1134).
2. **Fallback marking.** `ExchangeOnlineConfig.razor` `LoadExoConfig` 206-247 fills each field from its stored key,
   else `ExchangeOnline:AppId` / `:Organization` / `:CertificateSubject` from appsettings, else (the certificate
   only) `CN=EXO-Automation` - the runtime's rule (`ExoConnectionPool.GetExoConfig` 164-180: a key with no row falls
   back; a stored empty value does not). The fields (99-111) say nothing of where a value came from. Fix: the load
   records, per key with no row, the fallback that supplies it (`exoFallback`), and each such field carries Admin
   Settings' caption shape (`SecretFallbackText` 573-575): "Not set in the database; the configured fallback
   applies: {value} ({setting} in appsettings.json)." - or, for the certificate with no appsettings value, "Not set
   in the database; the built-in default applies: CN=EXO-Automation." A blank fallback marks nothing: nothing
   applies. A save that writes a key, or finds it already applied, leaves it stored, so its caption goes (507-511);
   a key not edited keeps it.
3. **Dirty count.** `EntryDelta` 493-495 takes `Except` under `OrdinalIgnoreCase`, while the save writes the lists
   as they are (`ReplaceConfig`), so an entry removed and added back in other casing changes what is stored and
   counts zero: no Save bar, no leave warning. Fix: `StringComparer.Ordinal`. `ChangedEntries` (the names in an
   already-applied record) is not the count and stays.
4. **PowerShell start-up.** 41 test files open sessions directly (`CreateDefault`, `CreateDefault2`,
   `RunspaceFactory.CreateRunspace()`, `PowerShell.Create()`) and more through the services they drive, and xUnit
   v3 runs classes in parallel. The one failure seen (aud-a28p, `ADAttributeEditorSaveTests`) was two classes'
   first sessions opening together in a filtered run; no full run, whose many concurrent sessions follow the first,
   has shown it. **Chosen: a one-time start-up guard, not a collection.** A collection serialises only the classes
   that carry it, so it must be on every class that opens a session, indirectly included, which no list stays true
   to; it cannot help one test that opens two sessions at once; and it serialises tens of slow classes for the whole
   suite. What was seen points at the process's first sessions (an inference: the race has not been reproduced on
   demand), so PowerShell starts once, alone, before any test: new
   `ExchangeAdminWeb.Tests/PowerShellStartup.cs`, an xUnit v3 assembly fixture (`[assembly: AssemblyFixture]`) whose
   construction opens one `CreateDefault` and one `CreateDefault2` runspace and runs a command in each, retried, and
   never throws - a start that fails must not fail every test; the tests then report as they would.
   `AttributeEditorHarness.cs` is not touched.

Registries: ClickGateRegistry has both pages NotYetConverted ("tier 2, not approved", 77-78), no line keys; nothing
re-anchors. No handler added; ProgressRegistry's pins stand (`SaveExoConfig` keeps `ModuleConfigSvc.SaveChangedValues(`
and `ExoPool.DrainPool(`).

Tests, new `ExchangeAdminWeb.Tests/SettingsFieldTests.cs` through `SettingsPageHarness`: Secret IDs that are not
whole numbers above zero are refused, one `Validation` refusal naming the field, nothing written - rules or secret; a
valid one saves (the control). The EXO page renders the caption on a field whose key has no row (the appsettings
fallback; the certificate's built-in default), none on a stored field or a blank fallback, and drops it once that
field is saved. A pattern removed and added back in other casing counts two and is saved as typed. New
`ExchangeAdminWeb.Tests/PowerShellStartupTests.cs`: the start completed before the test ran, and two sessions opened at
once through `AttributeEditorHarness`'s directory session both open and run. Mutations at the call site: (2) any text
accepted for the Secret ID again; (3) the fallback marking dropped; (4) the count case-insensitive again; (5) the
guard's assembly registration removed.

## Executed reproduction - 2026-10-22

Branch `worktree-agent-a992f9d6471a07501` on aud-a28r (`dbcac40f`), the fix's tests and `PowerShellStartup.cs` in place and the
three production files as they were at `dbcac40f` swapped in (`git show`, each `git hash-object` equal to its blob:
AdminSettings `A14C80B2B968A6501BAA61EE476C7A10`, ExchangeOnlineConfig `6F1EFF3C09CF05D149CAC53720A19A7D`,
ProtectedPrincipalService `07895376985514AF4E80FCEE4EF8614C`), build 0 errors: `SettingsFieldTests` and
`PowerShellStartupTests` 10 failed / 5 passed. Every Secret ID that is not a whole number above zero was stored (`"12a"`,
`"secret 4242"`, `"0"`, `"-5"`, `"42.5"`, `"99999999999"`, each where `"777"` was expected), and the pasted-password case
saved with two success records where one refusal was expected; neither Exchange Online case found a caption; the pattern
added back in other casing left the page clean (`IsDirty` false). The five that passed are the three save controls, the
Exchange Online control (no caption either way) and the race pin, whose guard was in place. Every file was restored to its
MD5 and touched.

The race itself was not reproduced: with the guard's registration removed, 12 cold test-host runs of twelve first sessions
opened at once (the harness's directory session, `CreateDefault`, `CreateDefault2`) were clean, as were about sixty cold
processes of 8 to 16 first sessions outside the suite (`CreateDefault`, `CreateDefault2`, `RunspaceFactory.CreateRunspace()`,
`PowerShell.Create()`, started together or staggered up to 400 ms) against Microsoft.PowerShell.SDK 7.6.1. PowerShell's
source at that tag (read for this record, not run) keys no shared static dictionary by provider name: its per-assembly
caches are keyed by assembly, and a dictionary keyed `Alias` is each session's own, unlocked provider table, filled while
that runspace binds - and a bind runs its steps in parallel. Which of these the race was is not established. So the guard
rests on what was observed - the one failure was two classes' first sessions, and no full run has shown it - not on a
reproduced cause (Known gaps).

## Fix

`Services/ProtectedPrincipalService.cs`: `IsSecretId(value)`, the lookups' own `PositiveId` rule as a public test.

`Components/Pages/AdminSettings.razor`: `SaveProtectedPrincipals`, after the `!ppLoaded` refusal and before the secret's store
checks: a changed, non-blank Secret ID that `IsSecretId` rejects refuses the save through `LogSettingsSaveRefused`
(`Validation`, under `ProtectedPrincipals`), the message naming the field and what it takes and not repeating the text - a
field that takes an ID is where a pasted password lands. `EntryDelta` takes `Except` under `StringComparer.Ordinal`.

`Components/Pages/ExchangeOnlineConfig.razor`: `exoFallback`, filled by `FallbackCaptions` from the one load read - per key
with no row, the configured appsettings value, or for the certificate with no appsettings entry the built-in
`CN=EXO-Automation`; a blank appsettings value supplies nothing, as the runtime reads it - and rendered under each field. A
key the save writes or finds already applied leaves `exoFallback`. The setting names and the built-in default are named once
(`AppIdSetting`, `OrganizationSetting`, `CertificateSubjectSetting`, `BuiltInCertificateSubject`) for the load and the
captions.

`ExchangeAdminWeb.Tests/PowerShellStartup.cs`: the xUnit v3 assembly fixture (`[assembly: AssemblyFixture(...)]`); its
construction opens a `CreateDefault` and a `CreateDefault2` runspace and runs `1` in each, up to three attempts, never
throwing, and sets `Started`.

Tests: new `ExchangeAdminWeb.Tests/SettingsFieldTests.cs` (14 cases) and `ExchangeAdminWeb.Tests/PowerShellStartupTests.cs`
(1). Departures from the plan, none in what is decided: the Exchange Online control covers an appsettings value set blank for
the certificate (the runtime takes it over the built-in default, so nothing is marked); a case beyond it pins that a refused
Secret ID is not repeated in the record or the message; and `AttributeEditorHarness.cs` is untouched, as planned - the pin
builds sessions from its directory session state.

Caller-visible change, by design: on Admin Settings, a changed Secret ID that is not a whole number above zero is refused,
naming the field, with nothing saved; an entry removed and added back in other casing shows as unsaved and saves as typed. On
Exchange Online Config, a field whose value the database does not hold says where it comes from. The suite starts PowerShell
once before any test.

## Guard proof

Same driver as aud-a28r, filter
`FullyQualifiedName~SettingsFieldTests|FullyQualifiedName~PowerShellStartupTests|FullyQualifiedName~SettingsWholeProtectedSaveTests|FullyQualifiedName~SettingsConnectionAndSecretSaveTests|FullyQualifiedName~SettingsSaveStoreCheckTests|FullyQualifiedName~SettingsSaveAuditTests|FullyQualifiedName~SettingsNoOpAndUnreadableSecretTests|FullyQualifiedName~SettingsPerRowSaveTests|FullyQualifiedName~ProtectedPrincipalServiceTests|FullyQualifiedName~ModuleConfigServiceTests|FullyQualifiedName~AdminPageDirtyStateTests|FullyQualifiedName~ProgressRegistryTests|FullyQualifiedName~WholeStoreWriterTripwireTests|FullyQualifiedName~ProtectedGroupWriteTargetTests|FullyQualifiedName~SettingsConcurrentSaveTests|FullyQualifiedName~SettingsStoreTextTests|FullyQualifiedName~ADAttributeEditorSaveTests|FullyQualifiedName~ClickGateTests|FullyQualifiedName~GlobalProgressWiringTests`
(729 tests). Fixed-file MD5s: `Components/Pages/AdminSettings.razor` `9D69AE6C73295B9CE471380D31ED92FE`,
`Components/Pages/ExchangeOnlineConfig.razor` `3022C478173090001893C3B4A1F09496`, `ExchangeAdminWeb.Tests/PowerShellStartup.cs`
`49A0046607D070A3E71B7634295ECC9E`; every mutant built with 0 errors and every file was restored to its MD5. After the round,
the restored tree rebuilt and the same filter: 729/729 green.

| # | Mutation (call site) | Mutant MD5 | Named test failing |
|---|---|---|---|
| 2 | `AdminSettings.SaveProtectedPrincipals`: any text accepted for the Secret ID again (`if (false && secretChanged && ...)`) | `1A2A0028D562B0DA9FAC421C40852C90` | `AdminSettings_ASecretIdThatIsNotAWholeNumberAboveZero_IsRefused_NamingTheField_AndNothingIsWritten`, all six (`"-5"`, `"99999999999"`, ... stored where `"777"` was expected), `AdminSettings_ARefusedSecretId_IsNotRepeatedInTheRecord` (the save went through) |
| 3 | `ExchangeOnlineConfig.LoadExoConfig`: the fallback marking dropped (`exoFallback = new(StringComparer.OrdinalIgnoreCase)` in place of `FallbackCaptions(...)`) | `2F62D530311AC75807A2FEC27EA8D4D7` | `ExchangeOnlineConfig_AFieldWithNoRow_IsMarkedWithTheFallbackThatSuppliesIt`, `ExchangeOnlineConfig_SavingAMarkedField_StoresIt_AndOnlyThatFieldLosesItsMark` (no caption rendered) |
| 4 | `AdminSettings.EntryDelta`: the count case-insensitive again (`StringComparer.OrdinalIgnoreCase` on both sides) | `893C8D4125159D6B53989B2F46D9B675` | `AdminSettings_AnEntryAddedBackInOtherCasing_CountsAsTwoChanges_AndIsSavedAsTyped` (`IsDirty` false) |
| 5 | `PowerShellStartup.cs`: the guard's assembly registration removed (the `[assembly: AssemblyFixture(...)]` line commented out) | `C0FE313C333291ED35285D96698B84CA` | `PowerShellStartupTests.PowerShellIsStartedBeforeAnyTest_AndTwoSessionsOpenedAtOnceThroughTheHarness_BothStart` ("PowerShell was not started before the tests ran") |
| E2 | extra, `ExchangeOnlineConfig.SaveExoConfig`: a saved key keeps its caption (`exoFallback.Remove(key)` dropped) | `27A0DA51A4D72D8CFD56050BE52C7E33` | `ExchangeOnlineConfig_SavingAMarkedField_StoresIt_AndOnlyThatFieldLosesItsMark` (the built-in caption still rendered) |
| E3 | extra, `ExchangeOnlineConfig.AddFallbackCaption`: the built-in default marked though appsettings blanks it (`else if (builtIn is not null)`) | `A4356FDE840A2A7DAB98BA8CA08D0F8E` | `ExchangeOnlineConfig_AStoredFieldAndFieldsNothingSupplies_AreNotMarked` |
| E4 | extra, `AdminSettings.SaveProtectedPrincipals`: a blank Secret ID refused too (`secretId.Length > 0 &&` dropped) | `194C0E1ECD39329C5E248F20418C25E0` | `AdminSettings_AWholeNumberSecretIdOrABlankOne_IsSaved(entered: "")`, `SettingsConnectionAndSecretSaveTests.AdminSettings_ClearingTheSecretWithAFallbackConfigured_IsThenShownAsNotSetInTheDatabase` (`"777"` kept, `""` expected) |

Mutation 5 fails the pin on its first assertion, deterministically; its second half - two sessions opened at once through
the harness, both started - passes with or without the guard, because the race could not be produced on demand (Executed
reproduction). The pin holds that the start runs before any test, not that the race is gone.

The code comments are written after the round, in their own commit, so the MD5s above are this commit's bytes.

## Gates - 2026-10-22 (this commit's tree)

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors; 61 warnings, all pre-existing, none in a touched file (the
  pin's first draft drew xUnit1031 for a blocking wait and was made async before this tree).
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug, as the gate is written): exit 0,
  5,639 passed / 3 skipped (the three known AD skips) / 0 failed, 5,642 total, 5m34s. No hang. (After aud-a28r, `dbcac40f`:
  5,624 / 3 / 0.)
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0.

No directory, Exchange Online, Delinea, SMTP, push or deployment. PowerShell sessions are opened, locally: the start-up
guard's two and the pin's two, over the harness's stand-in directory functions with module autoloading off; the connection
save drains a pool that holds no session. The pages were not rendered in a browser. No coder self-review verdict is claimed.

## Known gaps

- The start-up guard rests on the one failure seen - two classes' first sessions, in a filtered run - and on no full run
  showing it; the cause was not reproduced. If the race can come from one runspace's own parallel bind at any time, a start
  before the tests narrows it and does not end it, and a collection would not end it either.
- Production starts PowerShell the same way on its first sessions; nothing in the app opens one first (candidate, not in
  scope).
- The Secret ID rule is the lookups' `int.TryParse`, so `+42` and `0042` are accepted and stored as typed; they read as 42.
- An Exchange Online field shown from a fallback and edited is captioned with the fallback until it is saved; the caption is
  about where the stored state stands, not about the text being typed.
- `ChangedEntries`, which names the admin's entries in an already-applied record, still compares ignoring case (plan item 3).

## Branch-tip gates - 2026-10-22 (`07ef4e8e`: both fixes and the comments)

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors, 61 warnings, all pre-existing.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug, as the gate is written): exit 0,
  5,639 passed / 3 skipped (the three known AD skips) / 0 failed, 5,642 total, 5m26s. No hang.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0.
