# aud-a62: Configuration numbers and a Boolean are parsed in singleton constructors on the audit and mail paths, so one mistyped value stops the audit writer, the extended log, the mail service or the trace service from being built

**Severity**: MEDIUM - availability of the audit path. The aud-a58 class with other parsers: (1)
`OperationTraceService`'s constructor runs `config.GetValue<bool?>("OperationTrace:Enabled")`, which throws
on a non-Boolean. (2) `EmailService`'s constructor runs `int.Parse(config["Email:SmtpPort"] ?? "25")`,
which throws on a non-number. (3) `JsonlLogService` (three sites) and `ExtendedLogService` (three sites)
read `GetValue<int?>` / `GetValue<long?>` for their file limits in their constructors, which throw on
non-numbers, so the audit writer or the extended log cannot be built. Each is a singleton every page
injects, so a typo in one setting fails every resolution.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go within the 2026-10-23 stopping rule (MEDIUM); in progress, plan committed
**Branch**: `worktree-agent-a4c254a086c7d1d55` (from `4a7a202e`)
**Commit**: -
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

## Guard proof

Pending.
