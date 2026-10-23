# aud-a62: Configuration numbers and a Boolean are parsed in singleton constructors on the audit and mail paths, so one mistyped value stops the audit writer, the extended log, the mail service or the trace service from being built

**Severity**: MEDIUM - availability of the audit path. The aud-a58 class with other parsers: (1)
`OperationTraceService`'s constructor runs `config.GetValue<bool?>("OperationTrace:Enabled")`, which throws
on a non-Boolean. (2) `EmailService`'s constructor runs `int.Parse(config["Email:SmtpPort"] ?? "25")`,
which throws on a non-number. (3) `JsonlLogService` (three sites) and `ExtendedLogService` (three sites)
read `GetValue<int?>` / `GetValue<long?>` for their file limits in their constructors, which throw on
non-numbers, so the audit writer or the extended log cannot be built. Each is a singleton every page
injects, so a typo in one setting fails every resolution.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go within the 2026-10-23 stopping rule (MEDIUM); not started
**Branch**: -
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

## Guard proof

Pending.
