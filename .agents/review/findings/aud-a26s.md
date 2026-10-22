# aud-a26s: Exchange Online Config repeats the runtime fallback rule with no test keeping them in step; the Secret ID check accepts "+42" and "0042"; `ChangedEntries` still ignores case

**Severity**: LOW - three leftovers of aud-a26p. (1) The Exchange Online page's "the configured fallback
applies" text re-derives the fallback from `ExoConnectionPool.GetExoConfig`'s rule by hand, and no test
asserts the page and the pool agree. (2) The Secret ID validation uses the lookups' `int.TryParse`, so
"+42" and "0042" are accepted and stored as typed; the lookups parse them the same, so the outcome is
right, but the stored text differs from what the operator meant. (3) `ChangedEntries`, which names the
entries in an already-applied record, compares case-insensitively while the dirty count (aud-a26p) is
now case-sensitive, so a casing-only change counts as unsaved yet is named as unchanged.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
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

## Guard proof

Pending.
