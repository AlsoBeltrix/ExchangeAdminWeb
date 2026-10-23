# aud-a62: Configuration numbers and a Boolean are parsed in singleton constructors on the audit and mail paths, so one mistyped value stops the audit writer, the extended log, the mail service or the trace service from being built

**Severity**: MEDIUM - availability of the audit path. The aud-a58 class with other parsers: (1)
`OperationTraceService`'s constructor runs `config.GetValue<bool?>("OperationTrace:Enabled")`, which throws
on a non-Boolean. (2) `EmailService`'s constructor runs `int.Parse(config["Email:SmtpPort"] ?? "25")`,
which throws on a non-number. (3) `JsonlLogService` (three sites) and `ExtendedLogService` (three sites)
read `GetValue<int?>` / `GetValue<long?>` for their file limits in their constructors, which throw on
non-numbers, so the audit writer or the extended log cannot be built. Each is a singleton every page
injects, so a typo in one setting fails every resolution.
**Status**: Complete; merged `a4fc0e29`; codex attr-batch-17 clean
**Branch**: `worktree-agent-a4c254a086c7d1d55` (from `4a7a202e`)
**Commit**: `0e6b8abc` (fix, tests, README, record); plan `46dacfdd`
**Reviewer**: implementer (surfaced by the aud-a56/a57/a58 agent, 2026-10-23, candidates 7, 8 and 9 of its hand-back)

## Evidence

At `3da510cb`: `Services/OperationTraceService.cs` 15; `Services/EmailService.cs` 47;
`Services/JsonlLogService.cs` 34, 222, 226; `Services/ExtendedLogService.cs` 57, 344, 348;
`Services/ConfigBoolean.cs` (aud-a58, the shape to extend).

## Approach (to be written by the implementing agent before code)

- `ConfigBoolean` grows into (or is joined by) a `ConfigValue` helper with `ReadInt`, `ReadLong` and the
  existing Boolean read: blank or absent is the documented default; a value that does not parse is the
  default plus one warning per process per (setting, value), guarded; never a throw. The Boolean read
  replaces `GetValue<bool?>` in the trace service; the number reads replace `int.Parse` and the
  `GetValue<int?>`/`<long?>` sites named above. A mistyped SMTP port reads as the default 25 with the
  warning (a wrong port fails visibly at connect time, as today for a wrong number).
- A source check extends aud-a58's: no `int.Parse(config[` / `GetValue<int?>` / `GetValue<long?>` /
  `GetValue<bool?>` on a configuration value in `Services/` or `Components/`.
- Tests: each constructor built with a non-number / non-Boolean, the default taken, one warning; the source
  check. Mutation at the call site: `int.Parse` again at one site.

*Amended 2026-10-23 by the implementing agent, from the grep (Plan, evidence 6):* the same parsers stand in one more
singleton constructor the record does not name. `Services/Jobs/BulkJobService.cs` 63-65 reads
`BulkJobs:StaleHeartbeatMinutes` and `BulkJobs:RetentionDays` with `GetValue<double?>` and `BulkJobs:RecentJobLimit` with
`GetValue<int?>`. The runner is resolved at start-up, so a typo there stops the app from starting. Line 64 is a site the
record's own source check names. All three are fixed the same way: the helper gains a `ReadDouble`, and the check names
every typed `GetValue<T>` but `string`.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"), within the 2026-10-23 stopping rule (MEDIUM). Admitted from the
aud-a56/a57/a58 hand-back, candidates 7, 8 and 9. One commit `fix(config): ... (aud-a62)`, the first fix on branch
`worktree-agent-a4c254a086c7d1d55` (from `4a7a202e`), before aud-a63 and aud-a65. Every service it changes is shared and
belongs to no module: the audit and trace writer, the trace service, the extended log, the mail service and the bulk job
runner. So the base app version is due; the supervisor owns the bump.

Evidence, re-read at `4a7a202e`. `git grep -n -E "GetValue<|int\.Parse\(|long\.Parse\(|double\.Parse\(|Convert\.To(Int32|Int64|Double|Boolean)\("`
over `Services/`, `Components/`, `Modules/`, `Models/` and `Program.cs` finds every site that parses a configuration value:
1. `Services/OperationTraceService.cs` 15: `_enabled = config.GetValue<bool?>("OperationTrace:Enabled") ?? true;`.
   - The binder converts a present value with the type's converter and wraps its failure in an
     `InvalidOperationException`, so anything but true or false throws.
   - The constructor (12) takes no logger.
   - A singleton (`Program.cs` 320) that the audit service and every traced service inject.
2. `Services/EmailService.cs` 47: `_smtpPort = int.Parse(config["Email:SmtpPort"] ?? "25");`.
   - `FormatException` on a value that is not a number, and on a blank one: `"" ?? "25"` is `""`.
   - A singleton (`Program.cs` 331) that every notifying page and service injects.
3. `Services/JsonlLogService.cs`, the audit and trace writer, a singleton (`Program.cs` 315):
   - 34: `Math.Clamp(config.GetValue<int?>("Audit:MaxFilesPerPeriod") ?? DefaultMaxFilesPerPeriod, 2, 50)`.
   - `GetMaxFileBytes` (220-228), from the constructor (33). 222: `GetValue<long?>("Audit:MaxFileBytes")`, which wins
     when present (`Math.Max(1024, ...)`). Else 226: `Math.Max(1, GetValue<int?>("Audit:MaxFileMB") ?? DefaultMaxFileMB)`.
   - Every audit and trace row goes through this one writer, so a mistyped limit leaves the app with no audit writer.
4. `Services/ExtendedLogService.cs`, a singleton (`Program.cs` 314):
   - 57: `GetValue<int?>("ExtendedLog:MaxFilesPerDay")`.
   - `GetMaxFileBytes` (342-350): 344 `GetValue<long?>("ExtendedLog:MaxFileBytes")`, 348
     `GetValue<int?>("ExtendedLog:MaxFileMB")`.
5. `Services/ConfigBoolean.cs` (aud-a58) is the shape to follow, and is left as it is. aud-a58's follow-up names its
   `Read` for two pages another agent owns, so it keeps its name and signature, and the new helper sits beside it.
6. Not in the record: `Services/Jobs/BulkJobService.cs` 63-65 (the amendment above). The runner is resolved at start-up
   (`Program.cs` 440-441), so a throw from its constructor stops the app.
7. Hits that are not configuration values, so not sites:
   - database values: `Models/ReportedCount.cs` 126, `Services/Jobs/BulkJobRepository.cs` 119, 587 and 619,
     `Services/Jobs/JobStoreMigrator.cs` 118, `Services/Storage/ConfigStoreMigrator.cs` 363,
     `Services/Storage/ModuleEnablementRepository.cs` 137;
   - a CSV row: `Services/CalendarPermissionService.cs` 211.
   - No file another agent owns holds a site, so nothing is left for a follow-up.

**Rule.** A configuration number or Boolean read at construction never throws. Absent or blank is its documented default.
A value that cannot be read is the default too, and is named in one warning, once per process for each setting and value.
A decimal value the binder read is read the same.

1. New `Services/ConfigValue.cs`, `internal static class ConfigValue`, beside `ConfigBoolean`:
   - `ReadInt(IConfiguration config, string key, int whenBlank, ILogger logger)`: `int.TryParse` with
     `NumberStyles.Integer` and the invariant culture, the style and culture the binder's converter reads a decimal
     value in.
   - `ReadLong(IConfiguration config, string key, long? whenBlank, ILogger logger)`: the same for `long`. `whenBlank` may
     be null, "not set", which `*:MaxFileBytes` needs: not set, the megabyte setting applies.
   - `ReadDouble(IConfiguration config, string key, double whenBlank, ILogger logger)`: `NumberStyles.Float` and the
     invariant culture, the binder's again, and the number must be finite. "NaN" and "Infinity" passed the binder and
     then threw out of `TimeSpan.FromMinutes` and `TimeSpan.FromDays` in the same constructor.
   - Blank or absent is `whenBlank`, with no warning. Any other value that does not parse is `whenBlank`, with the
     warning "The configuration value {Key} is '{Value}', which cannot be read as {Kind}; it is read as {Reading} until it
     is corrected". `{Kind}` is "a whole number" or "a finite number"; `{Reading}` is the default, or "not set".
   - Once per process for each key and value: a static `ConcurrentDictionary` and `TryAdd`, the aud-a43 and aud-a58
     shape. The warning sits in a try whose catch takes any exception (aud-a28u).
2. The call sites, each to its documented default:
   - `OperationTraceService`: the constructor gains an optional `ILogger<OperationTraceService>? logger = null`. The
     container injects it, and the 68 direct constructions in the tests, many in other agents' files, compile unchanged.
     `_enabled` is `ConfigBoolean.Read(config, "OperationTrace:Enabled", whenBlank: true, whenNotBoolean: true, ...)`.
     Both readings are the documented default, on: the trace is diagnostic, and a mistyped switch keeps the record
     rather than hiding it.
   - `EmailService` 47: `ConfigValue.ReadInt(config, "Email:SmtpPort", whenBlank: 25, logger)`. A mistyped port is 25,
     with the warning. A wrong port fails at connect time, as a wrong number does today.
   - `JsonlLogService` 34, and `GetMaxFileBytes`, which takes the logger: `ReadInt` for `Audit:MaxFilesPerPeriod`
     (`DefaultMaxFilesPerPeriod`), `ReadLong` for `Audit:MaxFileBytes` (`whenBlank: null`), `ReadInt` for
     `Audit:MaxFileMB` (`DefaultMaxFileMB`). The clamps, and bytes before megabytes, are unchanged.
   - `ExtendedLogService` 57 and `GetMaxFileBytes`: the same, for `ExtendedLog:`.
   - `BulkJobService` 63-65: `ReadDouble` (5), `ReadInt` (25), `ReadDouble` (30).
3. `README.md`, section 3 (Configure Application Settings): one paragraph naming these settings and how a value that
   cannot be read is read.
4. Comments after the mutation round, checked code-identical.

Tests, new `ExchangeAdminWeb.Tests/ConfigValueTests.cs`. Each case uses a key or value of its own, because the set of named
values lasts for the process:
- The reads:
  - `ReadInt_TakesAWholeNumber_AndBlankAsItsDefault` (theory: "7", " 7 ", "-3", "+12", absent, "", "   "), with no
    warning.
  - `ReadInt_TakesAValueItCannotRead_AsItsDefault_AndNamesIt` (theory: "five", "50MB", "2.5", "99999999999"). One
    warning naming the key, the value and the reading.
  - The same two for `ReadLong` (a value past `int`; the null default, "not set") and `ReadDouble` ("2.5" and "1e1"
    read; "NaN", "Infinity", "-Infinity" and "abc" not).
  - `AValueItCannotRead_IsNamedOncePerProcess` and `ALoggerThatThrows_DoesNotStopTheAnswer`.
- One per constructor, each built with values that cannot be read. Each limit is asserted by its field, and each warning
  by key and value:
  - `TheAuditWriter_GivenLimitsItCannotRead_IsBuilt_WithItsDefaults`. Also: `Audit:MaxFileBytes` that cannot be read falls
    to `Audit:MaxFileMB`.
  - `TheExtendedLog_GivenLimitsItCannotRead_IsBuilt_WithItsDefaults`.
  - `TheMailService_GivenAPortThatIsNotANumber_IsBuilt_OnPort25`. Also: blank, which threw before, is 25, and "587" is
    read as written.
  - `TheTraceService_GivenASwitchThatIsNotABoolean_IsBuilt_AndTraces`. Also: "false" still turns it off.
  - `TheTraceService_BuiltByTheContainer_NamesTheValueThroughItsLogger`: a `ServiceCollection` registering it as
    `Program.cs` does, so the warning is shown to reach the logger the container injects.
  - `TheBulkJobRunner_GivenSettingsItCannotRead_IsBuilt_WithItsDefaults`, "NaN" among them.
- `NoPageOrService_ReadsAConfigurationValueWithAParserThatThrows`:
  - A source check over `Components/` and `Services/` (`.razor`, `.cs`), with comments blanked by
    `RazorSyntax.CommentsBlanked`, with no allowance.
  - It names any typed `GetValue<T>` but `string`, and a numeric `Parse(` or `Convert.ToX(` of a configuration indexer
    (`config[`, `Config[`, `_config[`, `Configuration[`).
  - `bool.Parse` stays aud-a58's check, untouched, so that check's allowance can be edited by its own follow-up without a
    conflict.
  - The pattern is pinned against samples, so the check bites on a clean tree. The record's four forms match:
    `int.Parse(config["Email:SmtpPort"] ?? "25")`, `GetValue<int?>`, `GetValue<long?>` and `GetValue<bool?>`.
    `GetValue<string>` and `int.TryParse(config[...])` do not.

Mutations at the call site (md5 before and after, the named failing test, restored from the index, touched, rebuilt,
md5 matched, green):
- M1 `EmailService`'s `int.Parse(config["Email:SmtpPort"] ?? "25")` again (the record's).
- Extra E1 `JsonlLogService`'s `config.GetValue<int?>("Audit:MaxFilesPerPeriod")` again.
- Extra E2 `ExtendedLogService`'s `config.GetValue<long?>("ExtendedLog:MaxFileBytes")` again.
- Extra E3 `OperationTraceService`'s `config.GetValue<bool?>("OperationTrace:Enabled") ?? true` again.
- Extra E4 `BulkJobService`'s `config.GetValue<int?>("BulkJobs:RecentJobLimit") ?? 25` again.
- Extra E5 the once-per-value guard removed (`Named.TryAdd(...) || true`).
- Extra E6 `ReadDouble`'s finite check removed.

Filter:
`FullyQualifiedName~ConfigValueTests|FullyQualifiedName~ConfigBooleanTests|FullyQualifiedName~JsonlWriter|FullyQualifiedName~ExtendedLogServiceTests|FullyQualifiedName~EmailService|FullyQualifiedName~OperationTrace|FullyQualifiedName~AuditServiceTests|FullyQualifiedName~AuditWriterThrowFreeTests|FullyQualifiedName~BulkJobService`.
`ClickGate*` and `ProgressRegistryTests` are run alone as well.

Files:
- `Services/ConfigValue.cs` (new)
- `Services/OperationTraceService.cs`
- `Services/EmailService.cs`
- `Services/JsonlLogService.cs`
- `Services/ExtendedLogService.cs`
- `Services/Jobs/BulkJobService.cs`
- `ExchangeAdminWeb.Tests/ConfigValueTests.cs` (new)
- `README.md`
- this record

## Fix

- New `Services/ConfigValue.cs`, `internal static class ConfigValue`, beside `ConfigBoolean` (left as it is):
  - `ReadInt(IConfiguration config, string key, int whenBlank, ILogger logger)`: blank or absent is `whenBlank`;
    `int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, ...)` decides the rest; anything else is
    `whenBlank`, named.
  - `ReadLong(..., long? whenBlank, ...)`: the same for `long`, and a null `whenBlank` is named as "not set".
  - `ReadDouble(..., double whenBlank, ...)`: `NumberStyles.Float`, the invariant culture, and `double.IsFinite`.
  - The naming is new private `NameOnce`: `Named.TryAdd((key, value), 0)` on a static `ConcurrentDictionary`, then
    `LogWarning("The configuration value {Key} is '{Value}', which cannot be read as {Kind}; it is read as {Reading} until
    it is corrected")` in `try { ... } catch (Exception) { }`.
- `Services/OperationTraceService.cs`:
  - The constructor is `(IConfiguration config, JsonlLogService log, ILogger<OperationTraceService>? logger = null)`.
  - `_enabled` is `ConfigBoolean.Read(config, "OperationTrace:Enabled", whenBlank: true, whenNotBoolean: true, logger ??
    NullLogger<OperationTraceService>.Instance)`, under a three-line comment.
- `Services/EmailService.cs`: `_smtpPort` is `ConfigValue.ReadInt(config, "Email:SmtpPort", whenBlank: 25, logger)`.
- `Services/JsonlLogService.cs`:
  - `_maxFilesPerPeriod` is `Math.Clamp(ConfigValue.ReadInt(config, "Audit:MaxFilesPerPeriod", DefaultMaxFilesPerPeriod,
    logger), 2, 50)`.
  - `GetMaxFileBytes(config, logger)`: `ReadLong(config, "Audit:MaxFileBytes", whenBlank: null, logger)` first, then
    `Math.Max(1, ReadInt(config, "Audit:MaxFileMB", DefaultMaxFileMB, logger))`.
- `Services/ExtendedLogService.cs`: the same three reads for `ExtendedLog:MaxFilesPerDay`, `ExtendedLog:MaxFileBytes` and
  `ExtendedLog:MaxFileMB`.
- `Services/Jobs/BulkJobService.cs`: `ReadDouble(config, "BulkJobs:StaleHeartbeatMinutes", 5, logger)`,
  `ReadInt(config, "BulkJobs:RecentJobLimit", 25, logger)` and `ReadDouble(config, "BulkJobs:RetentionDays", 30, logger)`.
- `README.md`, section 3: a "Number settings and the trace switch" paragraph, after the `Audit:LogRoot` requirement.

Tests, new `ExchangeAdminWeb.Tests/ConfigValueTests.cs`, 53 cases:
- The reads: `ReadInt_TakesAWholeNumber_AndBlankAsItsDefault` (7), `ReadInt_TakesAValueItCannotRead_AsItsDefault_AndNamesIt`
  (6, "0x32" among them), `ReadLong_TakesAWholeNumber_AndBlankAsNotSet` (4),
  `ReadLong_TakesAValueItCannotRead_AsItsDefault_AndNamesIt`, `ReadDouble_TakesAFiniteNumber_AndBlankAsItsDefault` (5),
  `ReadDouble_TakesAValueThatIsNotAFiniteNumber_AsItsDefault_AndNamesIt` (6), `AValueItCannotRead_IsNamedOncePerProcess`,
  `ALoggerThatThrows_DoesNotStopTheAnswer`.
- The constructors: `TheAuditWriter_GivenLimitsItCannotRead_IsBuilt_WithItsDefaults`,
  `TheExtendedLog_GivenLimitsItCannotRead_IsBuilt_WithItsDefaults`, `TheMailService_GivenAPortThatIsNotANumber_IsBuilt_OnPort25`,
  `TheTraceService_GivenASwitchThatIsNotABoolean_IsBuilt_AndTraces`,
  `TheTraceService_BuiltByTheContainer_NamesTheValueThroughItsLogger`,
  `TheBulkJobRunner_GivenSettingsItCannotRead_IsBuilt_WithItsDefaults`.
- The source check: `TheSourceCheck_NamesTheParsersThatThrow_AndNotTheReadsThatDoNot` (15 samples) and
  `NoPageOrService_ReadsAConfigurationValueWithAParserThatThrows`.

Caller-visible changes, by design:
- A number setting, or the trace switch, that cannot be read no longer throws out of a constructor: the audit and trace
  writer, the trace service, the extended log, the mail service and the bulk job runner are built. The value is read as
  its default and named once per process for each setting and value.
- Blank is the default everywhere. Before, a blank `Email:SmtpPort` threw, as did a value of white space only at the
  `GetValue` sites; an empty value there was already the default.
- A decimal value the binder read is read the same. Its converter also took a hexadecimal form (`0x32`, `&h32`, `#32`)
  for the integer settings; that is now named and read as the default. No setting documents one.
- "NaN" and "Infinity" for `BulkJobs:StaleHeartbeatMinutes` and `BulkJobs:RetentionDays` passed the binder and then threw
  out of `TimeSpan.FromMinutes` and `TimeSpan.FromDays`. They are named and read as the default.
- `Audit:MaxFileBytes` or `ExtendedLog:MaxFileBytes` that cannot be read is not set, so its `MaxFileMB` decides, as when
  it is left out.
- `OperationTraceService` takes an optional logger, which the container injects.

## Guard proof

Reproduction:
- M1, E1, E2, E3 and E4 each put one call site back as it was at `4a7a202e`.
- Each threw out of its constructor on a value that cannot be read: `FormatException` from `int.Parse`, and the binder's
  `InvalidOperationException` ("Failed to convert configuration value ... to type ...") from `GetValue`. That is the
  defect as the record states it.
- The source check named the line each time.

Driver: session scratchpad `w-a62a63a65/mutate.ps1`, aud-a56's driver pointed at this worktree; spec
`w-a62a63a65/round-a62.ps1`, log `w-a62a63a65/a62-mutations.log`.
- For each mutant: the file must equal its staged version, and its MD5 is taken. One literal replacement must match
  exactly once.
- A Release build of the solution with 0 errors is required. Then the filter runs with a TRX logger, and the failing
  tests are read from the TRX.
- In a `finally`, the file is restored from the index (`git checkout --`), its mtime advanced so MSBuild rebuilds, and
  its MD5 re-checked.

The filter (272 tests) covers the new tests, aud-a58's, both writers' suites, the mail service's, the trace service's,
the audit service and writer, and the job runner's:
`FullyQualifiedName~ConfigValueTests|FullyQualifiedName~ConfigBooleanTests|FullyQualifiedName~JsonlWriter|FullyQualifiedName~ExtendedLogServiceTests|FullyQualifiedName~EmailService|FullyQualifiedName~OperationTrace|FullyQualifiedName~AuditServiceTests|FullyQualifiedName~AuditWriterThrowFreeTests|FullyQualifiedName~BulkJobService`

Every mutant built with 0 errors, and each restore matched its original MD5. After the round, a rebuild on the restored
bytes and the filter: 272/272 green.

| Mutation (call site) | File, original MD5 | Mutant MD5 | Named tests failing |
|---|---|---|---|
| M1 `EmailService`'s `int.Parse(config["Email:SmtpPort"] ?? "25")` again | `EmailService.cs`, `97676757BFBB82EF5592D54809233363` | `868083B3850E33C299412A91F0797CEC` | 2 of 272: `TheMailService_GivenAPortThatIsNotANumber_IsBuilt_OnPort25` - `FormatException` out of the constructor; `NoPageOrService_ReadsAConfigurationValueWithAParserThatThrows` - "Services/EmailService.cs:49" |
| extra E1 `JsonlLogService`'s `config.GetValue<int?>("Audit:MaxFilesPerPeriod")` again | `JsonlLogService.cs`, `7C9FC645F005FFADEFEE029477C74017` | `BFC651BAE7A79CE96A45165843B94636` | 2 of 272: `TheAuditWriter_GivenLimitsItCannotRead_IsBuilt_WithItsDefaults` - "Failed to convert configuration value ... at 'Audit:MaxFilesPerPeriod' to type 'System.Int32'"; the source check - "Services/JsonlLogService.cs:36" |
| extra E2 `ExtendedLogService`'s `config.GetValue<long?>("ExtendedLog:MaxFileBytes")` again | `ExtendedLogService.cs`, `A56111C315D655C59867F5681592CF42` | `9D96237A1FC90A98191B9D9FB1FD5448` | 2 of 272: `TheExtendedLog_GivenLimitsItCannotRead_IsBuilt_WithItsDefaults` - the same, to 'System.Int64'; the source check - "Services/ExtendedLogService.cs:347" |
| extra E3 `OperationTraceService`'s `config.GetValue<bool?>("OperationTrace:Enabled") ?? true` again (the logger expression kept as a discard, so the mutant builds) | `OperationTraceService.cs`, `0781AE6AB9B38B8DAED9561E113841DC` | `4932D5D440FC5A6F40819882B8AA3C8B` | 3 of 272: `TheTraceService_GivenASwitchThatIsNotABoolean_IsBuilt_AndTraces` and `TheTraceService_BuiltByTheContainer_NamesTheValueThroughItsLogger` - "... to type 'System.Boolean'", the second out of the container's resolution; the source check - "Services/OperationTraceService.cs:21" |
| extra E4 `BulkJobService`'s `config.GetValue<int?>("BulkJobs:RecentJobLimit") ?? 25` again | `BulkJobService.cs`, `969BDBEB7C33292A3F275994C3901AD1` | `DD82526EFA3EC23A0CA986C58E9037C1` | 2 of 272: `TheBulkJobRunner_GivenSettingsItCannotRead_IsBuilt_WithItsDefaults` - "... to type 'System.Int32'"; the source check - "Services/Jobs/BulkJobService.cs:67" |
| extra E5 the once-per-value guard removed (`if (!(Named.TryAdd((key, value), 0) \|\| true))`) | `ConfigValue.cs`, `5A70626528AAB7DC89CA2146B95017EF` | `B56A875295112F6C0CF2136BAD0EED32` | 1 of 272: `AValueItCannotRead_IsNamedOncePerProcess` - two warnings where one |
| extra E6 `ReadDouble`'s finite check removed | `ConfigValue.cs`, `5A70626528AAB7DC89CA2146B95017EF` | `5AD81E15F1C1CFDC1383C1F8EC085305` | 5 of 272: `ReadDouble_TakesAValueThatIsNotAFiniteNumber_...` for "NaN", "Infinity", "-Infinity" and "1e400" - the value returned instead of 5; `TheBulkJobRunner_...` - "TimeSpan does not accept floating point Not-a-Number values" out of the constructor |

Unchanged through the round: `ConfigValueTests.cs` `009ADCA4627FDA2AB1C17AD8C58902CA`.

`ClickGate*` and `ProgressRegistryTests`, run alone on the restored tree (Release): 351/351.

Not run, by design:
- A real SMTP relay - the port is asserted on the built service, and nothing is sent.
- The app's start-up with a mistyped `BulkJobs:` value - the runner's constructor is the call `Program.cs` 440 makes,
  and it is built directly.

## Comments after the round

None were needed. The fix's comments were written before the round and are in the bytes measured above.

## Known gaps

- A value that parses but is out of range for its use still throws where a constructor converts it. A finite
  `BulkJobs:RetentionDays` past `TimeSpan`'s range (about 10,675,199 days, say "30000000") throws `OverflowException`
  from `TimeSpan.FromDays`, and `BulkJobs:StaleHeartbeatMinutes` likewise. That is a range, not a parse; listed in the
  hand-back as a candidate.
- The meaning of a number that parses is unchanged: the clamps and floors at the call sites stay, and a negative
  `BulkJobs:` value keeps the meaning it had. Two of those meanings are listed in the hand-back as candidates:
  - a negative `BulkJobs:RetentionDays` puts the start-up prune's cutoff in the future (`BulkJobService.InitializeAsync`,
    `BulkJobRepository.PruneFinishedBefore`), so every finished job record is deleted at start-up;
  - a negative `BulkJobs:RecentJobLimit` reaches SQLite as a negative `LIMIT`, which is no limit
    (`BulkJobRepository.GetRecentFinished`).
- The once-per-value set lasts for the process and is never trimmed, as aud-a58's is.
- The source check sees the shapes it names: a typed `GetValue<T>` but `string`, and a numeric `Parse(` or `Convert.ToX(`
  of a configuration indexer. A parse of a value first copied into a local is not seen.

## Branch-tip gates - 2026-10-23 (`bdf013cf`: the plans and the three fixes)

These gates are this branch's, shared by aud-a63's and aud-a65's records.
- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: exit 0, 0 errors, 19 warnings.
  - The same 19 as a Release build at `4a7a202e` before any change: CS8604, CS8602, CS0649 and CS0414 in
    `GroupManagement.razor`, `SelfServiceGroups.razor`, `HeaderAnalysisService.cs`, `AccountLockoutRemediationService.cs`,
    `MigrationReportExportProcessor.cs`, `RiskyUsers.razor`, `MessageTrace.razor` and `Comms10kApprovedSetTests.cs`.
  - None is in a file this branch touches, and NuGet audit raised nothing (no NU1902, NU1903 or NU1904).
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug): exit 0.
  - 6,444 passed / 0 skipped / 0 failed, 6,444 total, 3m54s. Nothing hung, and no Sequence file was written.
  - The sixty-four new cases are this branch's: aud-a62 fifty-three, aud-a63 five, aud-a65 six. The base was not run on
    its own; 6,380 is expected at `4a7a202e`.
  - The full suite ran at the tip only. Each fix's own bytes ran its round's filter green before it was committed: 272,
    181 and 185 tests.
- `ClickGate*` and `ProgressRegistryTests` (Release), run alone after each fix: 351/351 each time.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0.
  - All three also ran on each fix's staged bytes before it was committed.
  - The plan commits, records only, had `git diff --check`.
- Not run: `Invoke-ScriptAnalyzer` and `Invoke-Pester tests/ps` - no PowerShell changed.

No AD, EXO, Graph, DHCP, IIS, SMTP or ServiceNow call, no real module, no Delinea, push or deployment. The stand-ins were:
- real JSONL writers and a real extended log over temp folders, with files held open the way a tailer holds them;
- real config stores over temp databases;
- the Admin Event Log's own `ExecuteUndo`, `LoadEvents` and render tree, driven by reflection;
- a service container built in the test, registering the trace service as `Program.cs` does;
- clocks the tests move by hand.

No coder self-review verdict is claimed.

## Closeout

Fixed on branch `worktree-agent-a4c254a086c7d1d55`, not merged, not pushed. The services it changes - the audit and trace
writer, the trace service, the extended log, the mail service and the bulk job runner - are shared and belong to no
module, so the base app version is due. The bump is the supervisor's, at merge: `ExchangeAdminWeb.csproj` was outside
this brief.
