# aud-a28b: SaveAsync has no happy-path behavioural coverage

**Severity**: MEDIUM. Not a defect in shipped behaviour - a gap in what the suite can detect.
**Status**: Recorded follow-up; not approved
**Branch**: --
**Commit**: --

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
