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
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
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

## Guard proof

Pending.
