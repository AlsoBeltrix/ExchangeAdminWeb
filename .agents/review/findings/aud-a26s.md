# aud-a26s: Exchange Online Config repeats the runtime fallback rule with no test keeping them in step; the Secret ID check accepts "+42" and "0042"; `ChangedEntries` still ignores case

**Severity**: LOW - three leftovers of aud-a26p. (1) The Exchange Online page's "the configured fallback
applies" text re-derives the fallback from `ExoConnectionPool.GetExoConfig`'s rule by hand, and no test
asserts the page and the pool agree. (2) The Secret ID validation uses the lookups' `int.TryParse`, so
"+42" and "0042" are accepted and stored as typed; the lookups parse them the same, so the outcome is
right, but the stored text differs from what the operator meant. (3) `ChangedEntries`, which names the
entries in an already-applied record, compares case-insensitively while the dirty count (aud-a26p) is
now case-sensitive, so a casing-only change counts as unsaved yet is named as unchanged.
**Status**: Fixed on the branch; closed on guard proof; awaiting merge and codex review (one follow-up in a file owned by another agent, below)
**Branch**: `worktree-agent-a9db60993a8970569`
**Commit**: `63f43eb5` (plan `40026345`; comments `082a2b51`, `c7b61943`)
**Reviewer**: implementer (surfaced by the aud-a28r/a26p agent, 2026-10-22, candidates 2, 3 and 4 of its hand-back)

## Evidence

At `25071281`: `Components/Pages/ExchangeOnlineConfig.razor` (the fallback text; NotYetConverted for
`ClickGateRegistry`); `Services/ExoConnectionPool.cs` `GetExoConfig`; `Components/Pages/AdminSettings.razor`
(the Secret ID check); the `ChangedEntries` helper and its callers.

## Approach (to be written by the implementing agent before code)

- One function owns the fallback rule (the pool exposes it; the page calls it); a test asserts the page's
  text for each fallback source comes from that function.
- The Secret ID is accepted only as digits with no sign and no leading zero (or normalised to its parsed
  value and the operator shown what will be stored - choose the former, the onus is on the user to
  provide the value).
- `ChangedEntries` uses the same comparison as the dirty count.
- Tests through the settings harness; mutations at the call site: the page's hand-written rule again;
  `int.TryParse` alone again; the case-insensitive comparer again.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted at `d3f242a3`. One commit
`fix(settings): one function owns the Exchange Online fallback rule and a test pins it to the pool, the Secret ID takes
digits only, and an already-applied record names entries as the count counts them (aud-a26s)`, after aud-a28v on this
branch. Behaviour: Exchange Online Config (the Exchange Online module's version) and Admin Settings with the shared
protected-principal service (base app version); the supervisor owns both bumps.

Evidence re-read at `e9886df7`:
- `ExchangeOnlineConfig.razor` `LoadExoConfig` 249-257 fills the fields by hand (`stored ?? Config[setting] ?? ""`, the
  certificate `?? BuiltInCertificateSubject`), and `FallbackCaptions`/`AddFallbackCaption` 600-628 caption them by the same
  rule restated, with the setting names and the default as page constants (594-598). Their remarks (603-608) name
  `ExoConnectionPool.GetExoConfig` 164-181 as the rule followed - 176-179: `appId ??= _config["ExchangeOnline:AppId"] ?? ""`,
  the same for the organization, and the certificate `?? "CN=EXO-Automation"`. No test asserts that the two agree.
  `Services/ExoConnectionPool.cs` is owned by another agent now and is not edited here.
- `ProtectedPrincipalService.IsSecretId` 1311 is `PositiveId` 1313-1314, `int.TryParse(value, out var id) && id > 0`, whose
  default `NumberStyles.Integer` takes a leading sign, leading zeros and surrounding white space, so "+42" and "0042" pass the
  page's check (`AdminSettings.razor` 1144) and are stored as typed.
- `AdminSettings.razor` `ChangedEntries` 1376-1379 takes `Except` under `OrdinalIgnoreCase`; its one caller is
  `ChangedRuleEntries` 1368-1374, the `alreadyApplied` list of the rules' success record (1247). The count, `EntryDelta`
  497-499, is ordinal since aud-a26p.

1. **One function owns the fallback rule.** New `Services/ExoConfigFallback.cs`, static.
   `Resolve(IConfiguration config, string key, string? stored)` -> `ExoConfigValue(Value, Source, Setting)`: a stored value,
   even an empty one, as it is (`Stored`); otherwise the appsettings value when there is one, even blank (`AppSettings`);
   otherwise, for the certificate only, `CN=EXO-Automation` (`BuiltInDefault`); otherwise "" (`None`) - `GetExoConfig`
   176-179 exactly. `Caption(value)`: "Not set in the database; the configured fallback applies: {value} ({setting} in
   appsettings.json)." for a non-blank appsettings value, "Not set in the database; the built-in default applies: {value}."
   for the default, none otherwise - aud-a26p's captions. The three setting names and the default are named there once.
   `LoadExoConfig` takes the fields and the captions from it; the page's constants, `FallbackCaptions` and
   `AddFallbackCaption` go. The corrupt branch is unchanged (its banner and blank fields are not the fallback rule).
   Left for a follow-up, recorded below: `GetExoConfig` 176-179 calls `ExoConfigFallback.Resolve`. Until then the pool keeps
   its own copy, and the test below is what holds the two in step.
2. **The Secret ID is digits only.** `IsSecretId`: ASCII digits only, the first not zero, a value an `int` holds, greater
   than zero - so no sign, no leading zero and no white space. A changed value it rejects is refused as aud-a26p refuses one,
   naming the field and never repeating the value; the message says what the field takes ("in digits only, with no sign or
   leading zero"). Not normalised: the onus is on the admin to enter the ID. The lookups' read is unchanged (`PositiveId`,
   behind `GetDirectoryReadSecretId` and `DescribeDirectoryReadSecret`), so a "+42" or "0042" stored before this still reads as
   42 and is not rewritten; the page refuses only a changed value.
3. **`ChangedEntries` compares as the count does**: `StringComparer.Ordinal`. A casing-only edit, which the count counts as
   two changes, is named as those two entries in an already-applied record, where it was named as nothing.
4. **Registries.** ClickGateRegistry: both pages NotYetConverted, no line keys; nothing re-anchors. No handler added;
   ProgressRegistry's pins stand (`SaveExoConfig` keeps `ModuleConfigSvc.SaveChangedValues(` and `ExoPool.DrainPool(`).

Tests. New `ExchangeAdminWeb.Tests/ExoConfigFallbackTests.cs`, for each connection key and each source - a stored value; a
stored empty value with an appsettings value; no row with an appsettings value; no row with a blank appsettings value; no row
and no appsettings entry - the other two keys stored: (a) the pool, built over the harness's services with its connect seam
recording what it is asked to connect under (an unopened runspace and no connection id, so nothing is opened or invoked),
resolves what `Resolve` gives - for the app id and the organization a blank value is the pool's "not configured"
(`IsConfigured` false and `BorrowAsync` refusing) and any other the value it connects under, for the certificate the subject
it connects under; (b) the Exchange Online Config page shows `Resolve`'s value in the field and renders `Caption`'s text, or
no caption. `SettingsFieldTests`: a changed Secret ID with a sign or a leading zero ("+42", "0042", "042", "+0042") is
refused, naming the field, nothing written; "10" saves (the control: a zero that does not lead); `IsSecretId` over the edge
values; a casing-only edit already applied elsewhere names both entries in its record's `alreadyApplied`. Mutations at the
call site: (1) the page's hand-written rule again - written by hand in `LoadExoConfig` once more, drifted in one case (a
blank appsettings certificate taken as unset), since a verbatim copy of a rule that agrees changes nothing a test could
see; (2) `int.TryParse` alone again (`IsSecretId` as `PositiveId(value) is not null`); (3) the case-insensitive comparer
again in `ChangedEntries`. Extra: (E1) `Resolve` drifted from the pool (a stored empty value falling back).

## Fix

`Services/ExoConfigFallback.cs` (new, static): `Resolve(config, key, stored)` -> `ExoConfigValue(Value, Source, Setting)` - a
stored value, even empty, as it is; otherwise the appsettings value when there is one, even blank; otherwise
`CN=EXO-Automation` for the certificate and "" for the others (`GetExoConfig` 176-179's rule). `Caption(value)` - aud-a26p's two
captions, for a non-blank appsettings value and for the built-in default, and none otherwise. The three setting names and the
default are its constants; an unknown key throws.

`Components/Pages/ExchangeOnlineConfig.razor` `LoadExoConfig`: the fields, the stored baselines and the captions from `Resolve`
and `Caption` (helpers `Resolve`, `StoredOrEmpty`, `FallbackCaptions`); the page's setting constants and its
`FallbackCaptions`/`AddFallbackCaption` are gone.

`Services/ProtectedPrincipalService.cs` `IsSecretId`: ASCII digits only, the first not zero, and within `int` above zero. Its doc
comment now says it is stricter than the lookups' read, which is unchanged. `Components/Pages/AdminSettings.razor`: the
refusal's message says the field takes "in digits only, with no sign or leading zero", and the comment above it no longer
calls the rule the lookups' own; `ChangedEntries` compares `StringComparer.Ordinal`.

Tests: new `ExchangeAdminWeb.Tests/ExoConfigFallbackTests.cs` (30 cases: 15 key-and-source cases, each through the pool and
through the page). `SettingsFieldTests`: a Secret ID with a sign or a leading zero refused (4 cases); `IsSecretId` over 15 values;
"10" added to the saved controls; a casing-only edit already applied elsewhere named as its two entries (1).

Caller-visible change, by design: on Admin Settings a changed directory-read Secret ID written with a sign or a leading zero is
refused, naming the field, where it was stored as typed (white space around it is trimmed, as before); an already-applied rules
record names a casing-only edit as the two entries the count counted. Exchange Online Config shows the same values and captions as before -
the rule moved, it did not change.

## Guard proof

Session-local driver (`mutate.ps1` in the scratchpad), as for aud-a28v: the fix staged; MD5; mutate by exact text (one match
required); touch; `dotnet build` of the test project; `dotnet test --no-build` filtered to
`FullyQualifiedName~ExoConfigFallbackTests|FullyQualifiedName~SettingsFieldTests|FullyQualifiedName~SettingsConnectionAndSecretSaveTests|FullyQualifiedName~SettingsSaveStoreCheckTests|FullyQualifiedName~SettingsSaveAuditTests|FullyQualifiedName~SettingsWholeProtectedSaveTests|FullyQualifiedName~SettingsNoOpAndUnreadableSecretTests|FullyQualifiedName~SettingsPerRowSaveTests|FullyQualifiedName~AdminPageDirtyStateTests|FullyQualifiedName~ProgressRegistryTests|FullyQualifiedName~ExoConnectionPoolSharedConfigTests|FullyQualifiedName~ProtectedPrincipalServiceTests|FullyQualifiedName~SettingsConcurrentSaveTests|FullyQualifiedName~SettingsStoreTextTests`
(362 tests); `git checkout --` of the file from the index; touch; MD5 re-confirmed; rebuild; the same filter. Every mutant built
with 0 errors, every file came back to its MD5, and the restored tree ran 362/362 green after each row.

| # | Mutation (call site) | MD5 before | MD5 mutant | MD5 after | Named test failing |
|---|---|---|---|---|---|
| 1 | `ExchangeOnlineConfig.LoadExoConfig`: the page's hand-written rule again - the certificate worked out by hand in place of `Resolve`, drifted in one case (a blank appsettings value taken as unset, so the built-in default is shown and captioned) | `9F57A6EEA2FCBA700DA119001A3F6F48` | `27732B22D576701688D91833A08005FB` | `9F57A6EEA2FCBA700DA119001A3F6F48` | `ExoConfigFallbackTests.ThePage_ShowsTheRulesValue_WithTheRulesCaption(key: "CertificateSubject", source: "appsettings-blank")` (`"CN=EXO-Automation"` shown, `""` expected); `SettingsFieldTests.ExchangeOnlineConfig_AStoredFieldAndFieldsNothingSupplies_AreNotMarked` |
| 2 | `ProtectedPrincipalService.IsSecretId`: `int.TryParse` alone again (`PositiveId(value) is not null`) | `21BAECDC84C2D80FB517BD1F6226C15B` | `F031BD0483D8A9204C88FF2D23892116` | `21BAECDC84C2D80FB517BD1F6226C15B` | `SettingsFieldTests.AdminSettings_ASecretIdWithASignOrALeadingZero_IsRefused_NamingTheField_AndNothingIsWritten` for "+42", "0042", "042" and "+0042" (each stored); `IsSecretId_TakesDigitsOnly_WithNoSignOrLeadingZero` for "01", "+1", " 1" and "1 " |
| 3 | `AdminSettings.ChangedEntries`: the case-insensitive comparer again (`StringComparer.OrdinalIgnoreCase` on both sides) | `DFDE334AE08EEF4B74E025529796CBCB` | `C88A1F676931008E56A048C6A56AB6B3` | `DFDE334AE08EEF4B74E025529796CBCB` | `SettingsFieldTests.AdminSettings_ACasingOnlyEditAlreadyAppliedElsewhere_NamesBothEntriesInItsRecord` (`[]`, `["Patterns:SVC-*", "Patterns:svc-*"]` expected) |
| E1 | extra, `ExoConfigFallback.Resolve`: drifted from the pool - a stored empty value falls back (`if (!string.IsNullOrEmpty(stored))`) | `8F1495574FEF7B8856FBA955BC3B314D` | `5C61383513C81E1CB451CA4467B50B8B` | `8F1495574FEF7B8856FBA955BC3B314D` | `ExoConfigFallbackTests.TheRule_IsWhatThePoolConnectsUnder` for `stored-empty` on AppId, Organization and CertificateSubject (the pool keeps the empty value) |

Mutation 1 is drifted on purpose: the rule the page wrote by hand agreed with the pool, so a verbatim copy of it changes nothing a
test could see, and what this finding guards against is the two drifting apart. Under E1 the page's own cases pass, as they
must: the page follows `Resolve`, drifted or not, and the pool theory is what holds `Resolve` to the pool. The code comments are
written after the round, in their own commit, so the MD5s above are this commit's bytes.

## Left for a follow-up (file owned by another agent)

`Services/ExoConnectionPool.cs` was owned by another agent during this work and is not edited here. `GetExoConfig` should take
its fallback from `ExoConfigFallback.Resolve`, so the rule has one copy, not two held together by a test. At `e9886df7`, lines
176-179:

```csharp
        appId ??= _config["ExchangeOnline:AppId"] ?? "";
        organization ??= _config["ExchangeOnline:Organization"] ?? "";
        certSubject ??= _config["ExchangeOnline:CertificateSubject"]
            ?? "CN=EXO-Automation";
```

become

```csharp
        appId = ExoConfigFallback.Resolve(_config, ConfigAppIdKey, appId).Value;
        organization = ExoConfigFallback.Resolve(_config, ConfigOrganizationKey, organization).Value;
        certSubject = ExoConfigFallback.Resolve(_config, ConfigCertSubjectKey, certSubject).Value;
```

with the reads above them (166-168, `GetValue`: null for a key with no row) and the corrupt check (170-174) unchanged. No
behaviour moves; `ExoConfigFallbackTests.TheRule_IsWhatThePoolConnectsUnder` passes before and after, and after it the pool
cannot drift from the page at all.

## Gates - 2026-10-22 (this commit's tree)

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: 0 errors; 61 warnings, all pre-existing, none in a touched
  file. (An incremental build after a test-only edit re-emits only the test project's warnings - 42 - so the count is taken
  from a full build.)
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug, as the gate is written): exit 0,
  5,781 passed / 3 skipped (the three known AD skips) / 0 failed, 5,784 total, 5m18s. No hang. (After aud-a28v, `65e8644b`:
  5,730 / 3 / 0.)
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0 - on its first run it failed on one test value, a full-width digit
  written as an escape that reached the file as the character itself; the value tested nothing `int.TryParse` does not already
  refuse, so it was dropped (15 values, not 16), and the build, the format check and the full suite were run again on this
  tree.

No directory, Exchange Online, Delinea, SMTP, push or deployment. The pool cases connect through the seam only - an unopened
runspace over an empty session state, no connection id - so no PowerShell session is opened or invoked. The pages were not
rendered in a browser. No coder self-review verdict is claimed.

## Known gaps

- Until the follow-up above lands, the pool keeps its own copy of the rule; `TheRule_IsWhatThePoolConnectsUnder` is what holds
  the two together, for the five sources it covers per key.
- The value a pool connects under is read through its internal connect seam (its own test seam); `IsConfigured` and
  `BorrowAsync` are public. Nothing connected to Exchange Online.
- A "+42" or "0042" stored before this fix stays as stored, and the lookups still read it as 42: the page refuses only a changed
  value, so the stored text is corrected when an admin next enters the ID.

## Branch-tip gates - 2026-10-22 (`c7b61943`: the three fixes and the comments)

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: 0 errors, 61 warnings, all pre-existing.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug, as the gate is written): exit 0,
  5,788 passed / 3 skipped (the three known AD skips) / 0 failed, 5,791 total, 5m16s. No hang.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0.
- The comments commit `082a2b51` failed this gate once, on aud-a28w's comment (that record says how); `c7b61943` rewords it.
